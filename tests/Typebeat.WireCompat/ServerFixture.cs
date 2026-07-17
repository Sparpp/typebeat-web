using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Typebeat.WireCompat;

/// <summary>
/// Assembly-wide fixture for the wire-compat harness. It:
///  1. drop+recreates a DEDICATED database "typebeat_wirecompat" (superuser postgres, so the
///     migration's <c>CREATE EXTENSION citext</c> works) — the dev db is never touched;
///  2. points the server at it via the <c>TYPEBEAT_DB</c> env var, read at startup by
///     <c>Db.ResolveConnectionString</c>, BEFORE the <see cref="WebApplicationFactory{Program}"/>
///     builds the host (which runs the SQL migrations);
///  3. seeds users / a beatmapset / beatmaps and a bearer token directly via SQL (no seed tool);
///  4. exposes a shared <see cref="Client"/> plus the seeded identities the tests assert against.
///
/// Placed in the <c>Typebeat.WireCompat</c> namespace so its OneTimeSetUp runs before every test
/// in the harness.
/// </summary>
[SetUpFixture]
public class ServerFixture
{
    public const string DatabaseName = "typebeat_wirecompat";

    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=" + DatabaseName + ";Username=postgres;Password=postgres";

    // Connect to the maintenance db to CREATE/DROP the test db.
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>The fixed checksum stored on the primary seeded beatmap (tests d/e/f).</summary>
    public const string SeedChecksum = "11111111111111111111111111111111";

    /// <summary>Username of the seeded player whose bearer token <see cref="BearerToken"/> resolves to.</summary>
    public const string PlayerUsername = "wc_player";

    /// <summary>Raw opaque access token for the seeded player; use as <c>Bearer {BearerToken}</c>.</summary>
    public const string BearerToken = "wc-primary-access-token-000000000000000000000";

    // Captured serial ids from seeding.
    public static long OwnerUserId { get; private set; }
    public static long PlayerUserId { get; private set; }
    public static long SeededBeatmapSetId { get; private set; }
    public static long SeededBeatmapId { get; private set; }
    public static long Md5SeededBeatmapId { get; private set; }

    /// <summary>Raw bytes of a tiny "type!beat file format v1" file (test g).</summary>
    public static byte[] OsuFileBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>The MD5 (lowercase hex over the final bytes) we STORED for the MD5-parity beatmap.</summary>
    public static string OsuFileChecksum { get; private set; } = string.Empty;

    private static WebApplicationFactory<Program>? factory;

    public static HttpClient Client { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await recreateDatabaseAsync();

        // Must be set BEFORE the host is built (Db.ResolveConnectionString reads it at startup).
        Environment.SetEnvironmentVariable("TYPEBEAT_DB", ConnectionString);

        factory = new WebApplicationFactory<Program>();

        // Building the client starts the host, which runs MigrateAsync (schema is created here).
        Client = factory.CreateClient();

        // A no-op request forces host startup to complete (and surfaces migration errors early).
        using (var probe = await Client.GetAsync("/health"))
            probe.EnsureSuccessStatusCode();

        await seedAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Client?.Dispose();
        factory?.Dispose();
    }

    private static async Task recreateDatabaseAsync()
    {
        await using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            await conn.OpenAsync();

            // FORCE terminates any lingering connections (Postgres 13+); neither statement may run
            // inside a transaction, so they are executed as standalone commands.
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {DatabaseName}");
        }

        // Create citext BEFORE the server's NpgsqlDataSource opens its first connection. That
        // connection (made during MigrateAsync) snapshots the database's type catalog; the
        // migration's own CREATE EXTENSION runs AFTER the snapshot, so on a brand-new database the
        // data source would never learn the citext OID and every read of a citext column (e.g.
        // users.username during bearer resolution) throws "Reading as 'System.Object' is not
        // supported ... DataTypeName '-.-'". In dev the extension already exists, so this only bites
        // a freshly-created db. Creating it here (idempotent with the migration) fixes the harness
        // without touching the server.
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS citext");
        }
    }

    private static async Task seedAsync()
    {
        // The tiny fixture file. Its content need not be a decodable beatmap — test (g) only
        // computes MD5 over these exact bytes and compares to the checksum stored below.
        const string osuFile =
            "type!beat file format v1\n\n[General]\nAudioFilename: audio.mp3\n\n[Metadata]\nTitle:Wire Compat\nArtist:Harness\n";
        OsuFileBytes = Encoding.UTF8.GetBytes(osuFile);
        OsuFileChecksum = Convert.ToHexString(MD5.HashData(OsuFileBytes)).ToLowerInvariant();

        // Seeded-token hashes (server stores SHA-256 of the opaque token; see TokenService).
        byte[] accessHash = SHA256.HashData(Encoding.UTF8.GetBytes(BearerToken));
        byte[] refreshHash = SHA256.HashData(Encoding.UTF8.GetBytes(BearerToken + "-refresh"));

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        OwnerUserId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('wc_owner', 'wc_owner@example.com', 'x', 'US')
            RETURNING id
            """);

        PlayerUserId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, 'wc_player@example.com', 'x', 'US')
            RETURNING id
            """,
            new { username = PlayerUsername });

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id = PlayerUserId });

        // Bearer token for the authed tests (24h validity), issued directly rather than through
        // the oauth flow so those tests don't consume registration/oauth rate-limit budget.
        await conn.ExecuteAsync(
            """
            INSERT INTO oauth_tokens (user_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at)
            VALUES (@userId, @accessHash, @refreshHash, now() + interval '1 day', now() + interval '30 days')
            """,
            new { userId = PlayerUserId, accessHash, refreshHash });

        SeededBeatmapSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, 'Wire Compat', 'Harness', 'ranked')
            RETURNING id
            """,
            new { ownerId = OwnerUserId });

        // Primary map: the fixed known checksum; short drain so the 400s-backdated play-time gate
        // clears comfortably in the score-loop test.
        SeededBeatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
            VALUES (@setId, 'type!beat', 0, @checksum, 60, 30, 1.5)
            RETURNING id
            """,
            new { setId = SeededBeatmapSetId, checksum = SeedChecksum });

        // MD5-parity map: checksum = MD5 of the fixture file bytes (test g asserts the client's
        // ComputeMD5Hash reproduces exactly this stored value).
        Md5SeededBeatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
            VALUES (@setId, 'type!beat', 0, @checksum, 60, 30, 1.5)
            RETURNING id
            """,
            new { setId = SeededBeatmapSetId, checksum = OsuFileChecksum });
    }

    /// <summary>A client-authenticated request builder for the seeded player.</summary>
    public static HttpRequestMessage Authed(HttpMethod method, string uri)
    {
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Add("Authorization", $"Bearer {BearerToken}");
        return req;
    }
}
