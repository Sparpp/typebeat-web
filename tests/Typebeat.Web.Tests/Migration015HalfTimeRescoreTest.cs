using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 015 (the Half Time nerf re-base) against a database in the pre-deploy state:
/// migrations 001-014 applied, with scores whose totals were computed by a client running the OLD
/// rate curve (decrease slope 1.80, so a default Half Time paid 0.55). 015 must rescale exactly
/// the rows carrying a rate below 1.00 onto the new curve (slope 3.00, default Half Time 0.25),
/// leave everything else byte-identical, correct the one denormalized aggregate that embeds score
/// totals (user_stats.total_score), and be safe to run twice. Same harness shape as
/// <see cref="Migration008CompletionBackfillTest"/>.
///
/// <para>
/// The expected values are not hand-typed constants: <see cref="ExpectedRescale"/> is an
/// independent C# implementation of the same integer arithmetic the SQL performs, so the migration
/// is reviewed against a green pin rather than against arithmetic done once in a comment. The
/// multipliers it feeds on come from <see cref="RateMultiplier"/> itself for the new curve, and
/// from the pre-nerf slope for the old one.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration015HalfTimeRescoreTest
{
    private const string database_name = "typebeat_migr015tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>The decrease slope the stored totals were priced under, before task 44.</summary>
    private const double old_decrease_slope = 1.8;

    private static readonly string[] migrations_before =
    [
        "001_init.sql", "002_website_uploads.sql", "003_anonymous_downloads.sql",
        "004_prem3_filename_backfill.sql", "005_ranked_approval.sql", "006_email_verification.sql",
        "007_account_settings.sql", "008_completion_rank.sql", "009_boundary_pace.sql",
        "010_backfill_play_counts.sql", "011_user_preferences.sql", "012_unranked_status.sql",
        "013_explicit_flag.sql", "014_replay_storage.sql",
    ];

    private NpgsqlDataSource dataSource = null!;

    private long typistId;

    // Rows that must move, and the (rate, stored total) they were seeded with.
    private long htDefaultId;      // bare "HT", no settings: the pre-task-27 shape, reads as 0.75
    private long htNinetyId;       // 0.90x: 0.82 -> 0.70, the gentlest down-rate that still moves
    private long htEightyId;       // 0.80x: 0.64 -> 0.40, chosen so the rescale lands on an exact .5
    private long htSixtyTwoId;     // 0.62x: 0.316 -> the 0.10 floor
    private long htStackedId;      // HT 0.75 + FL + LT: the other factors must cancel out
    private long htUnrankedId;     // an unranked HT row: history displays it, so it moves too

    // Rows that must come out byte-identical.
    private long htFloorId;        // 0.50x: 0.10 under both curves
    private long dtId;             // 1.75x: the increase side is untouched
    private long noModId;
    private long windDownId;       // WD: ramp endpoints are not persisted, so it is left alone

    private long statsBefore;

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

        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");

            foreach (string migration in migrations_before)
            {
                await conn.ExecuteAsync(readEmbeddedMigration(migration));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)", new { migration });
            }

            typistId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('slow typist', 'slow@example.com', 'x', 'US')
                RETURNING id
                """);

            long setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@typistId, 'Slow Song', 'The Draggers', 'ranked') RETURNING id",
                new { typistId });

            long beatmapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
                VALUES (@setId, 'type!beat', @checksum, 60, 54, 'map.osu')
                RETURNING id
                """,
                new { setId, checksum = Guid.NewGuid().ToString("N") });

            htDefaultId = await insertScoreAsync(conn, beatmapId, 200_000, """[{"acronym": "HT"}]""");
            htNinetyId = await insertScoreAsync(conn, beatmapId, 123_457, """[{"acronym": "HT", "settings": {"speed_change": 0.9}}]""");
            htEightyId = await insertScoreAsync(conn, beatmapId, 100_004, """[{"acronym": "HT", "settings": {"speed_change": 0.8}}]""");
            htSixtyTwoId = await insertScoreAsync(conn, beatmapId, 100_000, """[{"acronym": "HT", "settings": {"speed_change": 0.62}}]""");
            htStackedId = await insertScoreAsync(conn, beatmapId, 220_500,
                """[{"acronym": "HT", "settings": {"speed_change": 0.75}}, {"acronym": "FL"}, {"acronym": "LT"}]""");
            htUnrankedId = await insertScoreAsync(conn, beatmapId, 90_000,
                """[{"acronym": "HT", "settings": {"speed_change": 0.75}}]""", ranked: false);

            htFloorId = await insertScoreAsync(conn, beatmapId, 40_000, """[{"acronym": "HT", "settings": {"speed_change": 0.5}}]""");
            dtId = await insertScoreAsync(conn, beatmapId, 538_000, """[{"acronym": "DT", "settings": {"speed_change": 1.75}}]""");
            noModId = await insertScoreAsync(conn, beatmapId, 750_000, "[]");
            windDownId = await insertScoreAsync(conn, beatmapId, 310_000, """[{"acronym": "WD"}]""", ranked: false);

            // The player's cumulative total, as the submit path accumulated it: the sum of every
            // stored total above.
            statsBefore = await conn.ExecuteScalarAsync<long>("SELECT sum(total_score) FROM scores");

            await conn.ExecuteAsync(
                "INSERT INTO user_stats (user_id, play_count, total_score, play_time_s) VALUES (@typistId, 10, @statsBefore, 500)",
                new { typistId, statsBefore });
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

    // ---- the re-base itself ----

    [Test]
    public async Task DownRateScores_AreRescaledOntoTheNewCurve()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        long htDefault = await totalOf(conn, htDefaultId);
        long htNinety = await totalOf(conn, htNinetyId);
        long htSixtyTwo = await totalOf(conn, htSixtyTwoId);
        long htStacked = await totalOf(conn, htStackedId);
        long htUnranked = await totalOf(conn, htUnrankedId);

        Assert.Multiple(() =>
        {
            Assert.That(htDefault, Is.EqualTo(ExpectedRescale(200_000, 0.75)),
                "a bare HT reads as the 0.75x client default");
            Assert.That(htNinety, Is.EqualTo(ExpectedRescale(123_457, 0.90)));
            Assert.That(htSixtyTwo, Is.EqualTo(ExpectedRescale(100_000, 0.62)));

            // The stack's other factors (FL 1.05, LT 1.05) are on both sides of the ratio and
            // cancel, so the row moves by exactly the Half Time factor.
            Assert.That(htStacked, Is.EqualTo(ExpectedRescale(220_500, 0.75)));

            // An unranked row still shows in the player's own history, so it is re-based too.
            Assert.That(htUnranked, Is.EqualTo(ExpectedRescale(90_000, 0.75)));
        });
    }

    [Test]
    public async Task DefaultHalfTime_LandsOnTheKnownFraction()
    {
        // Spelled out once, independently of the shared helper: 0.55 -> 0.25 is a factor of 5/11,
        // so 200,000 becomes round(200000 x 5 / 11) = round(90909.09) = 90,909.
        await using var conn = await dataSource.OpenConnectionAsync();

        Assert.That(await totalOf(conn, htDefaultId), Is.EqualTo(90_909));
    }

    [Test]
    public async Task AnExactMidpoint_RoundsAwayFromZero()
    {
        // 0.80x is 0.64 -> 0.40, a factor of exactly 5/8, and 100,004 x 5 / 8 = 62,502.5. The
        // migration's integer form is floor(x + 1/2), so the half goes up rather than to even.
        await using var conn = await dataSource.OpenConnectionAsync();

        long htEighty = await totalOf(conn, htEightyId);

        Assert.Multiple(() =>
        {
            Assert.That(htEighty, Is.EqualTo(62_503));
            Assert.That(htEighty, Is.EqualTo(ExpectedRescale(100_004, 0.80)));
        });
    }

    [Test]
    public async Task EverythingElse_IsByteIdentical()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        long htFloor = await totalOf(conn, htFloorId);
        long dt = await totalOf(conn, dtId);
        long noMod = await totalOf(conn, noModId);
        long windDown = await totalOf(conn, windDownId);

        Assert.Multiple(() =>
        {
            // 0.50x sits on the 0.10 floor under BOTH curves, so it is a no-op even though it is a
            // down-rate row (it is still logged, see the idempotency test).
            Assert.That(htFloor, Is.EqualTo(40_000));

            Assert.That(dt, Is.EqualTo(538_000), "the increase side was not touched");
            Assert.That(noMod, Is.EqualTo(750_000));
            Assert.That(windDown, Is.EqualTo(310_000), "a ramp's endpoints are not persisted");
        });
    }

    [Test]
    public async Task RanksAndCompletion_AreNotTouched()
    {
        // Rank is graded on completion percent, which the multiplier has nothing to do with.
        await using var conn = await dataSource.OpenConnectionAsync();

        var row = await conn.QuerySingleAsync<(string Rank, double Completion, double Accuracy, int MaxCombo, bool Ranked)>(
            "SELECT rank, completion, accuracy, max_combo, ranked FROM scores WHERE id = @id", new { id = htDefaultId });

        Assert.Multiple(() =>
        {
            Assert.That(row.Rank, Is.EqualTo("X"));
            Assert.That(row.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(row.Accuracy, Is.EqualTo(0.97).Within(1e-12));
            Assert.That(row.MaxCombo, Is.EqualTo(100));
            Assert.That(row.Ranked, Is.True);
        });
    }

    // ---- the denormalized aggregate ----

    [Test]
    public async Task UserStatsTotal_MovesByExactlyTheSameDelta()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        long scoresNow = await conn.ExecuteScalarAsync<long>("SELECT sum(total_score) FROM scores");
        long statsNow = await conn.ExecuteScalarAsync<long>(
            "SELECT total_score FROM user_stats WHERE user_id = @typistId", new { typistId });

        Assert.Multiple(() =>
        {
            Assert.That(scoresNow, Is.LessThan(statsBefore), "the seeded rows must actually have shrunk");
            Assert.That(statsNow, Is.EqualTo(scoresNow),
                "user_stats.total_score seeded as the sum of the rows, so it must track them exactly");
        });
    }

    // ---- idempotency ----

    [Test]
    public async Task RunningItTwice_ChangesNothing()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var totalsBefore = (await conn.QueryAsync<(long Id, long Total)>(
            "SELECT id, total_score FROM scores ORDER BY id")).ToList();
        long statsBeforeRerun = await conn.ExecuteScalarAsync<long>(
            "SELECT total_score FROM user_stats WHERE user_id = @typistId", new { typistId });
        int loggedBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM score_rescales");

        // Re-apply the file by hand, the way a lost schema_migrations row or a manual replay would.
        await conn.ExecuteAsync(readEmbeddedMigration("015_ht_nerf_rescore.sql"));

        var totalsAfter = (await conn.QueryAsync<(long Id, long Total)>(
            "SELECT id, total_score FROM scores ORDER BY id")).ToList();
        long statsAfter = await conn.ExecuteScalarAsync<long>(
            "SELECT total_score FROM user_stats WHERE user_id = @typistId", new { typistId });
        int loggedAfter = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM score_rescales");

        Assert.Multiple(() =>
        {
            Assert.That(totalsAfter, Is.EqualTo(totalsBefore), "no score may shrink twice");
            Assert.That(statsAfter, Is.EqualTo(statsBeforeRerun), "no aggregate delta may be applied twice");
            Assert.That(loggedAfter, Is.EqualTo(loggedBefore), "the guard table is the thing that makes it a no-op");
        });
    }

    [Test]
    public async Task EveryTouchedRow_IsAudited()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var logged = (await conn.QueryAsync<long>(
            "SELECT score_id FROM score_rescales WHERE migration = '015_ht_nerf_rescore' ORDER BY score_id")).ToList();

        // Every down-rate row is recorded, INCLUDING the 0.50x one whose total did not change:
        // logging it is what stops a rerun from reconsidering it.
        Assert.That(logged, Is.EquivalentTo(new[]
        {
            htDefaultId, htNinetyId, htEightyId, htSixtyTwoId, htStackedId, htUnrankedId, htFloorId,
        }));
    }

    // ---- the reference arithmetic the SQL is checked against ----

    /// <summary>
    /// The migration's rescale, reimplemented in C#: multipliers as integer basis points off the
    /// snapped rate, then floor(old x new_bp / old_bp + 1/2) in exact integer arithmetic. The NEW
    /// multiplier is taken from the shipped <see cref="RateMultiplier"/> so this cannot drift away
    /// from the curve; the OLD one is the same function with the pre-nerf decrease slope.
    /// </summary>
    private static long ExpectedRescale(long oldTotal, double rate)
    {
        long oldBp = basisPoints(oldMultiplier(rate));
        long newBp = basisPoints(RateMultiplier.For(rate));

        return (2 * oldTotal * newBp + oldBp) / (2 * oldBp);
    }

    private static double oldMultiplier(double rate)
    {
        double snapped = Math.Round(rate, RateMultiplier.RATE_DECIMALS, MidpointRounding.AwayFromZero);

        double raw = snapped >= 1
            ? 1 + RateMultiplier.INCREASE_SLOPE * (snapped - 1)
            : 1 - old_decrease_slope * (1 - snapped);

        return Math.Round(Math.Max(RateMultiplier.MINIMUM, raw), RateMultiplier.MULTIPLIER_DECIMALS, MidpointRounding.AwayFromZero);
    }

    private static long basisPoints(double multiplier)
        => (long)Math.Round(multiplier * 10000, MidpointRounding.AwayFromZero);

    // ---- helpers ----

    private static async Task<long> totalOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<long>("SELECT total_score FROM scores WHERE id = @id", new { id });

    private async Task<long> insertScoreAsync(NpgsqlConnection conn, long beatmapId, long totalScore, string modsJson, bool ranked = true)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@typistId, @beatmapId, @totalScore, 0.97, 1.0, 100, 'X', true, @ranked,
                 CAST(@modsJson AS jsonb), '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { typistId, beatmapId, totalScore, modsJson, ranked });

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
