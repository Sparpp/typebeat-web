using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 045 re-grade semantics against a database in the pre-deploy state: migrations 001-044
/// applied, with scores whose ranks were graded under the OLD completion rule. 045 must re-grade
/// PASSED scores on ACCURACY with the missed-cell condition, and leave failed scores' 'F' alone.
/// Same harness shape as <see cref="Migration008CompletionBackfillTest"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration045AccuracyRankRegradeTest
{
    private const string database_name = "typebeat_migr045tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;

    private long sloppyButFullId;   // typed every cell, bad timing: old rank X -> accuracy 0.85 -> A
    private long cleanFullId;       // all greats: X stays X
    private long oneMissId;         // 99 greats + 1 miss: accuracy 0.99, 1% missed -> S (not X)
    private long unfixedTypoId;     // 98 greats + 2 good: accuracy 0.98, 2% missed -> S
    private long failedId;          // rank F stays F

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

        // Simulate the pre-deploy database: 001-044 applied and recorded, so MigrateAsync applies
        // exactly 045, against scores that already exist with old-rule ranks.
        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");

            var assembly = typeof(Db).Assembly;
            var files = assembly.GetManifestResourceNames()
                                .Where(n => n.Contains("Data.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                                .Select(n => n[(n.IndexOf("Data.Migrations.", StringComparison.Ordinal) + "Data.Migrations.".Length)..])
                                .OrderBy(n => n, StringComparer.Ordinal)
                                .ToArray();

            foreach (string file in files)
            {
                if (string.CompareOrdinal(file, "045_accuracy_rank_regrade.sql") >= 0) continue;

                await conn.ExecuteAsync(readEmbeddedMigration(file));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)", new { migration = file });
            }

            long userId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('regrade player', 'rg@example.com', 'x', 'US')
                RETURNING id
                """);

            long setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@userId, 'S', 'A', 'ranked') RETURNING id",
                new { userId });

            long beatmapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
                VALUES (@setId, 'type!beat', @checksum, 60, 54, 'map.osu')
                RETURNING id
                """,
                new { setId, checksum = Guid.NewGuid().ToString("N") });

            // Completion 1.0 under the old rule: an SS, however sloppy the timing. Accuracy 0.85 -> A.
            sloppyButFullId = await insertScoreAsync(conn, userId, beatmapId, rank: "X", passed: true,
                accuracy: 0.85, statistics: """{"great":80,"ok":10,"meh":10}""", maximum: """{"great":100}""");

            cleanFullId = await insertScoreAsync(conn, userId, beatmapId, rank: "X", passed: true,
                accuracy: 1.0, statistics: """{"great":100}""", maximum: """{"great":100}""");

            // One missed cell: no longer an SS (the X condition requires no cell missed), and 1% is
            // under the S limit, so S.
            oneMissId = await insertScoreAsync(conn, userId, beatmapId, rank: "X", passed: true,
                accuracy: 0.99, statistics: """{"great":99,"miss":1}""", maximum: """{"great":100}""");

            // An uncorrected typo is a missed cell (backlog 213), so 2/100 missed is under the S
            // limit and accuracy 0.98 clears the S cutoff -> S.
            unfixedTypoId = await insertScoreAsync(conn, userId, beatmapId, rank: "X", passed: true,
                accuracy: 0.98, statistics: """{"great":98,"good":2}""", maximum: """{"great":100}""");

            failedId = await insertScoreAsync(conn, userId, beatmapId, rank: "F", passed: false,
                accuracy: 0.4, statistics: """{"great":38,"miss":2}""", maximum: """{"great":100}""");
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
    public async Task PassedScores_AreRegradedOnAccuracy()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string sloppy = await rankOf(conn, sloppyButFullId);
        string clean = await rankOf(conn, cleanFullId);
        string oneMiss = await rankOf(conn, oneMissId);
        string unfixedTypo = await rankOf(conn, unfixedTypoId);

        Assert.Multiple(() =>
        {
            Assert.That(sloppy, Is.EqualTo("A"), "accuracy 0.85, no cell missed: A, not the old completion SS");
            Assert.That(clean, Is.EqualTo("X"), "all greats stays an SS");
            Assert.That(oneMiss, Is.EqualTo("S"), "a missed cell denies the SS; 1% is under the S limit");
            Assert.That(unfixedTypo, Is.EqualTo("S"), "the uncorrected typo is a missed cell, 2% is under the S limit");
        });
    }

    [Test]
    public async Task FailedScores_KeepRankF()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        Assert.That(await rankOf(conn, failedId), Is.EqualTo("F"));
    }

    [Test]
    public async Task TheRegradeIsIdempotent()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string before = await rankOf(conn, sloppyButFullId);
        await conn.ExecuteAsync(readEmbeddedMigration("045_accuracy_rank_regrade.sql"));
        Assert.That(await rankOf(conn, sloppyButFullId), Is.EqualTo(before), "a pure function of stored values re-derives the same grade");
    }

    // ---- helpers ----

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<string> rankOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<string>("SELECT rank FROM scores WHERE id = @id", new { id });

    private static async Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        string rank, bool passed, double accuracy, string statistics, string maximum)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, 100000, @accuracy, 10, @rank, @passed, @passed,
                 '[]'::jsonb, CAST(@statistics AS jsonb), CAST(@maximum AS jsonb))
            RETURNING id
            """,
            new { userId, beatmapId, rank, passed, accuracy, statistics, maximum });
}
