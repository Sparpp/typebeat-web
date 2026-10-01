using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Fixture for the BSS (beatmap submission) end-to-end tests: drop+recreate a DEDICATED
/// "typebeat_bsstests" database (kept distinct from typebeat_webtests / typebeat_pkgtests /
/// typebeat_wirecompat; every DB-backed fixture owns its own database), point the app at it
/// AND at a throwaway TYPEBEAT_FILE_ROOT via environment, then boot the real app in-process.
/// The app applies migrations 001–003 itself at startup.
///
/// The host carries a <see cref="FakePublicObjectStore"/> in place of the real public bucket
/// (backlog 364). It starts disabled, which is the unconfigured deployment every other test here
/// models; only the R2 tests switch it on, and they switch it back off. The Windows installer
/// name is configured (<see cref="InstallerFileName"/>) for the same tests; nothing else here asks
/// for /download/game.
///
/// Both environment variables are restored on teardown so later-running namespace fixtures
/// (Website) build their hosts against their own configuration.
/// </summary>
[SetUpFixture]
public class BssFixture
{
    public const string DatabaseName = "typebeat_bsstests";

    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=" + DatabaseName + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>The app's IFileStore root for this run; assert stored objects via paths under it.</summary>
    public static string FileRoot { get; private set; } = null!;

    public static HttpClient Client { get; private set; } = null!;

    /// <summary>The host's public object store (disabled unless a test switches it on).</summary>
    public static FakePublicObjectStore PublicStore { get; } = new();

    /// <summary>TYPEBEAT_GAME_DOWNLOAD on this host; TYPEBEAT_GAME_DOWNLOAD_LINUX/_MACOS stay unset.</summary>
    public const string InstallerFileName = "typebeat-test-Setup.exe";

    /// <summary>The running host's service provider (the R2 tests reach its singletons through it).</summary>
    public static IServiceProvider Services => factory!.Services;

    private static WebApplicationFactory<Program>? factory;
    private static NpgsqlDataSource? dataSource;
    private static string? previousDb;
    private static string? previousFileRoot;

    /// <summary>A pooled connection to the test database for direct seeding/asserting.</summary>
    public static async Task<NpgsqlConnection> OpenDbAsync()
    {
        var conn = dataSource!.CreateConnection();
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Creates a user (optionally verified) and issues a real bearer token for it.</summary>
    public static async Task<(long Id, string Bearer)> CreateUserAsync(string username, bool verified)
    {
        await using var conn = await OpenDbAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, verified_at)
            VALUES (@username, @email, 'x', 'US', CASE WHEN @verified THEN now() END)
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '_') + "@example.com", verified });

        var pair = await new TokenService(new Db(dataSource!)).IssueAsync(id);
        return (id, pair.AccessToken);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var conn = new NpgsqlConnection(admin_connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {DatabaseName}");
        }

        // citext must exist before the app's pooled data source first connects (type catalog
        // snapshot pitfall, same as the WireCompat/Website fixtures).
        await Db.EnsureExtensionsAsync(ConnectionString);

        FileRoot = Path.Combine(Path.GetTempPath(), "typebeat-bsstests-" + Guid.NewGuid().ToString("N"));

        // Must be set BEFORE the host is built (read at startup).
        previousDb = Environment.GetEnvironmentVariable("TYPEBEAT_DB");
        previousFileRoot = Environment.GetEnvironmentVariable("TYPEBEAT_FILE_ROOT");
        Environment.SetEnvironmentVariable("TYPEBEAT_DB", ConnectionString);
        Environment.SetEnvironmentVariable("TYPEBEAT_FILE_ROOT", FileRoot);

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("TYPEBEAT_GAME_DOWNLOAD", InstallerFileName);

            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPublicObjectStore>();
                services.AddSingleton<IPublicObjectStore>(PublicStore);
            });
        });
        Client = factory.CreateDefaultClient();

        // Forces host startup (and surfaces migration errors) before any test runs.
        using (var probe = await Client.GetAsync("/health"))
            probe.EnsureSuccessStatusCode();

        dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Client?.Dispose();
        factory?.Dispose();

        if (dataSource != null)
            await dataSource.DisposeAsync();

        Environment.SetEnvironmentVariable("TYPEBEAT_DB", previousDb);
        Environment.SetEnvironmentVariable("TYPEBEAT_FILE_ROOT", previousFileRoot);

        if (FileRoot != null && Directory.Exists(FileRoot))
            Directory.Delete(FileRoot, recursive: true);
    }
}
