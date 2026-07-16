using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Fixture for the BSS (beatmap submission) end-to-end tests: drop+recreate a DEDICATED
/// "typebeat_bsstests" database (kept distinct from typebeat_webtests / typebeat_pkgtests /
/// typebeat_wirecompat — every DB-backed fixture owns its own database), point the app at it
/// AND at a throwaway TYPEBEAT_FILE_ROOT via environment, then boot the real app in-process.
/// The app applies migrations 001–003 itself at startup.
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

    /// <summary>The app's IFileStore root for this run — assert stored objects via paths under it.</summary>
    public static string FileRoot { get; private set; } = null!;

    public static HttpClient Client { get; private set; } = null!;

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
        // snapshot pitfall — same as the WireCompat/Website fixtures).
        await Db.EnsureExtensionsAsync(ConnectionString);

        FileRoot = Path.Combine(Path.GetTempPath(), "typebeat-bsstests-" + Guid.NewGuid().ToString("N"));

        // Must be set BEFORE the host is built (read at startup).
        previousDb = Environment.GetEnvironmentVariable("TYPEBEAT_DB");
        previousFileRoot = Environment.GetEnvironmentVariable("TYPEBEAT_FILE_ROOT");
        Environment.SetEnvironmentVariable("TYPEBEAT_DB", ConnectionString);
        Environment.SetEnvironmentVariable("TYPEBEAT_FILE_ROOT", FileRoot);

        factory = new WebApplicationFactory<Program>();
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
