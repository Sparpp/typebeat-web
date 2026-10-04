using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The score reweight backfill (owner 2026-10-04, migration 046): stored total_score moves from the
/// osu 500000/500000 split onto 300000/700000. Exercises <see cref="ScoreReweightBackfill"/> against
/// a real database and pins the arithmetic, the audit guard and the no-op second run.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ScoreReweightBackfillTest
{
    private const string database_name = "typebeat_rewtest";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;

    private ScoreReweightBackfill.Outcome firstRun = null!;
    private long cleanId;      // all greats: a perfect play, must stay 1_000_000
    private long sloppyId;     // bad timing: accuracy 0.85, changes
    private long moddedId;     // a 1.05x mod: the multiplier must round-trip
    private long missId;       // misses move accuracyProgress

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

        dataSource = NpgsqlDataSource.Create(connection_string);
        await new Db(dataSource).MigrateAsync(NullLogger.Instance);

        await using var c = await dataSource.OpenConnectionAsync();

        long userId = await c.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('reweight player', 'rw@example.com', 'x', 'US')
            RETURNING id
            """);

        long setId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@userId, 'S', 'A', 'ranked') RETURNING id",
            new { userId });

        long beatmapId = await c.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 54, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        // A perfect play: acc 1, comboProgress 1, accuracyProgress 1 -> 1_000_000 on BOTH splits.
        cleanId = await insertAsync(c, userId, beatmapId, total: 1_000_000, accuracy: 1.0,
            statistics: """{"great":100}""", maximum: """{"great":100}""");

        // Sloppy but complete: accuracy 0.85. Its OLD total is what the real engine would submit.
        sloppyId = await insertAsync(c, userId, beatmapId,
            total: OldTotal(0.85, comboProgress: 1.0, accuracyProgress: 1.0, multiplier: 1.0),
            accuracy: 0.85, statistics: """{"great":80,"ok":10,"meh":10}""", maximum: """{"great":100}""");

        // A 1.05x mod stack, so the multiplier must cancel through the re-base.
        moddedId = await insertAsync(c, userId, beatmapId,
            total: OldTotal(0.9, comboProgress: 0.8, accuracyProgress: 1.0, multiplier: 1.05),
            accuracy: 0.9, statistics: """{"great":90,"miss":10}""", maximum: """{"great":100}""",
            mods: """[{"acronym":"FL"}]""");

        // Misses pull accuracyProgress down (judged cells over the map's cells).
        missId = await insertAsync(c, userId, beatmapId,
            total: OldTotal(0.7, comboProgress: 0.6, accuracyProgress: 0.5, multiplier: 1.0),
            accuracy: 0.7, statistics: """{"great":50}""", maximum: """{"great":100}""");

        // The sweep runs ONCE here, so the tests assert on one shared run rather than each racing to
        // be the first to consume it (NUnit does not order tests within a fixture).
        firstRun = await ScoreReweightBackfill.RunAsync(new Db(dataSource), NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    private static long OldTotal(double accuracy, double comboProgress, double accuracyProgress, double multiplier)
        => (long)Math.Round((500_000 * accuracy * comboProgress + 500_000 * Math.Pow(accuracy, 5) * accuracyProgress) * multiplier,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// What the sweep produces: it re-bases from the STORED (already rounded) total, not from the
    /// exact base, so the original rounding residue survives the move - the same one-point slack
    /// 015_ht_nerf_rescore.sql documents, and why this mirrors the sweep's own arithmetic rather
    /// than recomputing a fresh total.
    /// </summary>
    private static long ReweightedFrom(long storedTotal, double accuracy, double accuracyProgress, double multiplier)
        => (long)Math.Round(
            (0.6 * (storedTotal / multiplier) + 0.8 * 500_000 * Math.Pow(accuracy, 5) * accuracyProgress) * multiplier,
            MidpointRounding.AwayFromZero);

    [Test]
    public async Task TheReweightMovesStoredTotalsOntoTheNewSplit()
    {
        Assert.That(firstRun.Reweighted, Is.EqualTo(3), "the perfect play is unchanged; the other three move");

        await using var conn = await dataSource.OpenConnectionAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await totalOf(conn, cleanId), Is.EqualTo(1_000_000), "a perfect play is 1000000 on both splits");
            Assert.That(await totalOf(conn, sloppyId), Is.EqualTo(ReweightedFrom(OldTotal(0.85, 1.0, 1.0, 1.0), 0.85, 1.0, 1.0)));
            Assert.That(await totalOf(conn, moddedId), Is.EqualTo(ReweightedFrom(OldTotal(0.9, 0.8, 1.0, 1.05), 0.9, 1.0, 1.05)), "the mod multiplier cancels through");
            Assert.That(await totalOf(conn, missId), Is.EqualTo(ReweightedFrom(OldTotal(0.7, 0.6, 0.5, 1.0), 0.7, 0.5, 1.0)));
        });
    }

    [Test]
    public async Task ASecondRunMovesNothing()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        long before = await totalOf(conn, sloppyId);
        var second = await ScoreReweightBackfill.RunAsync(new Db(dataSource), NullLogger.Instance);

        Assert.Multiple(async () =>
        {
            Assert.That(second.Reweighted, Is.Zero, "the audit table is the idempotence guard");
            Assert.That(await totalOf(conn, sloppyId), Is.EqualTo(before), "and the total is not shrunk twice");
        });
    }

    [Test]
    public async Task TheAuditRecordsEveryMove()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var row = await conn.QuerySingleAsync<(long Old, long New)>(
            "SELECT old_total AS Old, new_total AS New FROM score_reweights WHERE score_id = @id", new { id = sloppyId });

        Assert.That(row.Old, Is.EqualTo(OldTotal(0.85, 1.0, 1.0, 1.0)));
        Assert.That(row.New, Is.EqualTo(ReweightedFrom(OldTotal(0.85, 1.0, 1.0, 1.0), 0.85, 1.0, 1.0)));
    }

    private static async Task<long> totalOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<long>("SELECT total_score FROM scores WHERE id = @id", new { id });

    private static async Task<long> insertAsync(NpgsqlConnection conn, long userId, long beatmapId, long total,
        double accuracy, string statistics, string maximum, string mods = "[]")
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @total, @accuracy, 10, 'A', true, true,
                 CAST(@mods AS jsonb), CAST(@statistics AS jsonb), CAST(@maximum AS jsonb))
            RETURNING id
            """,
            new { userId, beatmapId, total, accuracy, statistics, maximum, mods });
}
