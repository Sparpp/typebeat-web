using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 008 backfill semantics against a database in the pre-deploy state: migrations
/// 001–007 applied, with scores whose ranks were graded under the OLD accuracy rule. 008 must
/// add scores.completion, backfill it from the stored statistics jsonb, and re-grade PASSED
/// scores on completion — while leaving failed scores' 'F' untouched. Same harness shape as
/// <see cref="Migration004BackfillTest"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration008CompletionBackfillTest
{
    private const string database_name = "typebeat_migr008tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;

    private long sloppyButFullId;   // typed everything with bad timing: old rank B → X
    private long missedSomeId;      // 90/100 typed: → A
    private long failedId;          // failed 40 cells in: rank F stays F, completion 0.38
    private long degenerateId;      // empty maximum_statistics: completion stays at the 0 default

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var conn = new NpgsqlConnection(admin_connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {database_name} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {database_name}");
        }

        await Db.EnsureExtensionsAsync(connection_string);

        // Simulate the pre-deploy database: 001–007 applied and recorded, so MigrateAsync below
        // applies exactly 008 — against scores that already exist with old-rule ranks.
        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");

            foreach (string migration in new[]
                     {
                         "001_init.sql", "002_website_uploads.sql", "003_anonymous_downloads.sql",
                         "004_prem3_filename_backfill.sql", "005_ranked_approval.sql",
                         "006_email_verification.sql", "007_account_settings.sql",
                     })
            {
                await conn.ExecuteAsync(readEmbeddedMigration(migration));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)", new { migration });
            }

            long userId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('grandfathered player', 'gp@example.com', 'x', 'US')
                RETURNING id
                """);

            long setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@userId, 'Old Song', 'Old Artist', 'ranked') RETURNING id",
                new { userId });

            long beatmapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
                VALUES (@setId, 'type!beat', @checksum, 60, 54, 'map.osu')
                RETURNING id
                """,
                new { setId, checksum = Guid.NewGuid().ToString("N") });

            // Old rule graded this accuracy-0.85 play a B; it typed 100% of the map.
            sloppyButFullId = await insertScoreAsync(conn, userId, beatmapId, rank: "B", passed: true,
                statistics: """{"great":80,"ok":10,"meh":10}""", maximum: """{"great":100}""");

            // 90/100 typed (all-great timing): old rule said A via accuracy 0.9 — new rule agrees
            // via completion 0.9, but for the completion REASON.
            missedSomeId = await insertScoreAsync(conn, userId, beatmapId, rank: "A", passed: true,
                statistics: """{"great":90,"miss":10}""", maximum: """{"great":100}""");

            // A fail 40 cells into the map: rank F must survive the re-grade untouched.
            failedId = await insertScoreAsync(conn, userId, beatmapId, rank: "F", passed: false,
                statistics: """{"great":38,"miss":2}""", maximum: """{"great":100}""");

            // Degenerate row (empty maximums): the backfill must skip it, not divide by zero.
            degenerateId = await insertScoreAsync(conn, userId, beatmapId, rank: "D", passed: true,
                statistics: """{"great":5}""", maximum: "{}");
        }

        dataSource = NpgsqlDataSource.Create(connection_string);
        await new Db(dataSource).MigrateAsync(NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task PassedScores_AreRegradedOnCompletion()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var sloppy = await rowOf(conn, sloppyButFullId);
        var missed = await rowOf(conn, missedSomeId);

        Assert.Multiple(() =>
        {
            // Typed every cell with bad timing: B under the old rule, SS now.
            Assert.That(sloppy.Completion, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(sloppy.Rank, Is.EqualTo("X"));

            Assert.That(missed.Completion, Is.EqualTo(0.9).Within(1e-9));
            Assert.That(missed.Rank, Is.EqualTo("A"));
        });
    }

    [Test]
    public async Task FailedScores_KeepRankF_ButGetTheirCompletion()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        var failed = await rowOf(conn, failedId);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Rank, Is.EqualTo("F"));
            Assert.That(failed.Completion, Is.EqualTo(0.38).Within(1e-9));
        });
    }

    [Test]
    public async Task DegenerateStatistics_DoNotBreakTheBackfill()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        var degenerate = await rowOf(conn, degenerateId);

        Assert.Multiple(() =>
        {
            // No cells to divide by → completion stays at the column default; the passed re-grade
            // then reads it as 0 → 'D'.
            Assert.That(degenerate.Completion, Is.EqualTo(0.0));
            Assert.That(degenerate.Rank, Is.EqualTo("D"));
        });
    }

    // ---- helpers ----

    private static async Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        string rank, bool passed, string statistics, string maximum)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, 100000, 0.5, 10, @rank, @passed, @passed,
                 '[]'::jsonb, CAST(@statistics AS jsonb), CAST(@maximum AS jsonb))
            RETURNING id
            """,
            new { userId, beatmapId, rank, passed, statistics, maximum });

    private static async Task<(string Rank, double Completion)> rowOf(NpgsqlConnection conn, long id)
        => await conn.QuerySingleAsync<(string, double)>(
            "SELECT rank, completion FROM scores WHERE id = @id", new { id });

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
