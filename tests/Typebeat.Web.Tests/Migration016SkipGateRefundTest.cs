using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 016 plus <see cref="SkipGateRefund"/> against a database in the pre-deploy state:
/// migrations 001-015 applied, carrying scores that the pre-task-47 play-time gate unranked, mixed
/// in with scores unranked for every OTHER reason the submit path has. The refund must flip exactly
/// the gate's victims, leave the rest byte-identical, touch no aggregate, and be safe to run twice.
/// Same harness shape as <see cref="Migration015HalfTimeRescoreTest"/>.
///
/// <para>
/// The seeded map is 100 s of drain with a 40 s skip allowance, so the OLD bound was 90 s and the
/// corrected one is 0.9 x (100 - 40) = 54 s. Every seeded elapsed time is expressed against those
/// two numbers rather than hand-typed, and the boundary case sits exactly on 54 s.
/// </para>
///
/// <para>
/// The column the refund reads (<c>beatmaps.skippable_s</c>) is filled in after the migration and
/// before the refund runs, which is the real startup order: 016 adds the column, PaceBackfill
/// computes it from the stored .osu blobs, SkipGateRefund then re-evaluates the bound (Program.cs).
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration016SkipGateRefundTest
{
    private const string database_name = "typebeat_migr016tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private const double drain_s = 100;
    private const double skippable_s = 40;

    /// <summary>0.9 x drain: what the gate demanded before task 47.</summary>
    private static double OldRequired => PlayTimeGate.RequiredSeconds(drain_s, 0);

    /// <summary>0.9 x (drain - skippable): what it demands now.</summary>
    private static double NewRequired => PlayTimeGate.RequiredSeconds(drain_s, skippable_s);

    private static readonly string[] migrations_before =
    [
        "001_init.sql", "002_website_uploads.sql", "003_anonymous_downloads.sql",
        "004_prem3_filename_backfill.sql", "005_ranked_approval.sql", "006_email_verification.sql",
        "007_account_settings.sql", "008_completion_rank.sql", "009_boundary_pace.sql",
        "010_backfill_play_counts.sql", "011_user_preferences.sql", "012_unranked_status.sql",
        "013_explicit_flag.sql", "014_replay_storage.sql", "015_ht_nerf_rescore.sql",
    ];

    private NpgsqlDataSource dataSource = null!;

    private long typistId;
    private long statsBefore;
    private int refunded;

    // Wrongly unranked: the gate fired, the corrected bound clears, nothing else was wrong.
    private long skipFastId;      // 60 s, comfortably over the new bound
    private long exactlyOnTheBoundId; // 54.0 s
    private long halfTimeId;      // a down-rate stack: ranked at every speed since task 27

    // Rightly unranked, one per reason the submit path has. None may move.
    private long justTooFastId;   // 53.9 s: below the corrected bound too
    private long clearedOldGateId; // 95 s: the gate never fired, so something else unranked it
    private long failedId;         // passed = false
    private long overCeilingId;    // total above what its statistics justify (tasks 36/44 shape)
    private long unrankedModId;    // Mashing
    private long partlyJudgedId;   // "passed" but half the map unjudged
    private long invalidStatsId;   // more judgements than the map holds
    private long blockedBuildId;   // build blocked after the fact
    private long pendingSetId;     // set is not ranked
    private long noAllowanceId;    // a map with no qualifying gaps at all
    private long noAnchorId;       // started_at never recorded, so elapsed is unknowable

    // Untouched because it was never a candidate.
    private long alreadyRankedId;

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

        long gapMapId;
        long plainMapId;
        long pendingMapId;

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
                VALUES ('skipper', 'skipper@example.com', 'x', 'US')
                RETURNING id
                """);

            long rankedSetId = await insertSetAsync(conn, "Immortal Flame", "ranked");
            long pendingSetIdValue = await insertSetAsync(conn, "Work In Progress", "pending");

            gapMapId = await insertBeatmapAsync(conn, rankedSetId);
            plainMapId = await insertBeatmapAsync(conn, rankedSetId);
            pendingMapId = await insertBeatmapAsync(conn, pendingSetIdValue);

            long cleanBuildId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO builds (version_hash, blocked) VALUES ('clean-build', false) RETURNING id");
            long blockedBuild = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO builds (version_hash, blocked) VALUES ('blocked-build', true) RETURNING id");

            // ---- the victims ----
            skipFastId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId);
            exactlyOnTheBoundId = await insertScoreAsync(conn, gapMapId, elapsed: NewRequired, buildId: cleanBuildId);
            halfTimeId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId,
                modsJson: """[{"acronym": "HT", "settings": {"speed_change": 0.75}}]""", totalScore: 100_000);

            // ---- everything else ----
            justTooFastId = await insertScoreAsync(conn, gapMapId, elapsed: NewRequired - 0.1, buildId: cleanBuildId);
            clearedOldGateId = await insertScoreAsync(conn, gapMapId, elapsed: OldRequired + 5, buildId: cleanBuildId);
            failedId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId, passed: false, rank: "F");
            overCeilingId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId, totalScore: 1_500_000);
            unrankedModId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId,
                modsJson: """[{"acronym": "RX"}]""");
            partlyJudgedId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId,
                statisticsJson: """{"great": 5}""", completion: 0.5);
            invalidStatsId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId,
                statisticsJson: """{"great": 20}""");
            blockedBuildId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: blockedBuild);
            pendingSetId = await insertScoreAsync(conn, pendingMapId, elapsed: 60, buildId: cleanBuildId);
            noAllowanceId = await insertScoreAsync(conn, plainMapId, elapsed: 60, buildId: cleanBuildId);
            noAnchorId = await insertScoreAsync(conn, gapMapId, elapsed: 60, buildId: cleanBuildId, anchored: false);

            alreadyRankedId = await insertScoreAsync(conn, gapMapId, elapsed: 95, buildId: cleanBuildId, ranked: true);

            // The player's aggregates as the submit path left them. Crucially the play-time gate was
            // never part of the accrual condition, so every score above ALREADY contributed its
            // total; the refund must therefore add nothing.
            statsBefore = await conn.ExecuteScalarAsync<long>("SELECT sum(total_score) FROM scores");

            await conn.ExecuteAsync(
                """
                INSERT INTO user_stats (user_id, play_count, total_score, play_time_s, hit_counts)
                VALUES (@typistId, 16, @statsBefore, 1600, '{"great": 160}'::jsonb)
                """,
                new { typistId, statsBefore });
        }

        dataSource = NpgsqlDataSource.Create(connection_string);

        // 016 adds skippable_s (defaulting to 0, the old bound) and the audit table.
        await new Db(dataSource).MigrateAsync(NullLogger.Instance);

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            // What PaceBackfill writes from the stored .osu blob at startup. The "plain" map keeps
            // the default 0: no qualifying gap, or a blob that could not be read.
            await conn.ExecuteAsync(
                "UPDATE beatmaps SET skippable_s = @skippable WHERE id IN (@gapMapId, @pendingMapId)",
                new { skippable = skippable_s, gapMapId, pendingMapId });
        }

        refunded = await SkipGateRefund.RunAsync(new Db(dataSource), NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- the refund ----

    [Test]
    public async Task GateVictims_AreRanked()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        bool skipFast = await rankedOf(conn, skipFastId);
        bool onTheBound = await rankedOf(conn, exactlyOnTheBoundId);
        bool halfTime = await rankedOf(conn, halfTimeId);
        bool alreadyRanked = await rankedOf(conn, alreadyRankedId);

        Assert.Multiple(() =>
        {
            Assert.That(alreadyRanked, Is.True, "a score that was already ranked is never a candidate");
            Assert.That(skipFast, Is.True, "a play that finished in 60 s of a 100 s map by skipping");
            Assert.That(onTheBound, Is.True, "the bound is inclusive, exactly as the gate is");
            Assert.That(halfTime, Is.True, "a down-rate stack is ranked at every speed since task 27");
            Assert.That(refunded, Is.EqualTo(3), "and nothing else was touched");
        });
    }

    [Test]
    public async Task EveryOtherUnrankedScore_StaysUnranked()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var reasons = new (long Id, string Why)[]
        {
            (justTooFastId, "0.1 s under the corrected bound is still impossible"),
            (clearedOldGateId, "the gate never fired here, so the gate is not why it is unranked"),
            (failedId, "a failed play"),
            (overCeilingId, "a total its own statistics cannot justify"),
            (unrankedModId, "Mashing is unranked at every configuration"),
            (partlyJudgedId, "\"passed\" with half the map unjudged"),
            (invalidStatsId, "more judgements than the map holds"),
            (blockedBuildId, "a blocked build"),
            (pendingSetId, "a set that is not ranked"),
            (noAllowanceId, "a map with no skip allowance keeps the old bound"),
            (noAnchorId, "no started_at means no provable elapsed time"),
        };

        var stillUnranked = new List<(string Why, bool Ranked)>();

        foreach (var (id, why) in reasons)
            stillUnranked.Add((why, await rankedOf(conn, id)));

        Assert.Multiple(() =>
        {
            foreach (var (why, ranked) in stillUnranked)
                Assert.That(ranked, Is.False, why);
        });
    }

    [Test]
    public async Task NothingButTheRankedFlag_Moves()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var row = await conn.QuerySingleAsync<(long TotalScore, double Accuracy, double Completion, int MaxCombo, string Rank, bool Passed)>(
            "SELECT total_score, accuracy, completion, max_combo, rank, passed FROM scores WHERE id = @id",
            new { id = skipFastId });

        Assert.Multiple(() =>
        {
            Assert.That(row.TotalScore, Is.EqualTo(400_000));
            Assert.That(row.Accuracy, Is.EqualTo(0.97).Within(1e-12));
            Assert.That(row.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(row.MaxCombo, Is.EqualTo(10));
            Assert.That(row.Rank, Is.EqualTo("X"));
            Assert.That(row.Passed, Is.True);
        });
    }

    // ---- aggregates ----

    [Test]
    public async Task UserStats_AreUntouched_BecauseTheyAlreadyAccrued()
    {
        // Both submission paths accrue user_stats on (StatisticsValid && withinBounds) alone; the
        // play-time gate was never part of that condition. So a gate-unranked score already added
        // its total, play count and hit counts at submit time, and the refund must add nothing on
        // top or the player would be paid twice for one play.
        await using var conn = await dataSource.OpenConnectionAsync();

        var stats = await conn.QuerySingleAsync<(int PlayCount, long TotalScore, long PlayTimeS, string HitCounts)>(
            "SELECT play_count, total_score, play_time_s, hit_counts::text FROM user_stats WHERE user_id = @typistId",
            new { typistId });

        Assert.Multiple(() =>
        {
            Assert.That(stats.PlayCount, Is.EqualTo(16));
            Assert.That(stats.TotalScore, Is.EqualTo(statsBefore));
            Assert.That(stats.PlayTimeS, Is.EqualTo(1600));
            Assert.That(stats.HitCounts, Is.EqualTo("""{"great": 160}"""));
        });
    }

    [Test]
    public async Task TheLeaderboardPicksTheRefundsUp()
    {
        // Every scoring surface is a live query over scores.ranked, so flipping the flag IS the
        // refund; there is no board or ranking cache to invalidate. This asserts it through the
        // shared global-ranking definition rather than through the flag again.
        await using var conn = await dataSource.OpenConnectionAsync();

        var ranking = await GlobalRanking.ForUserAsync(conn, typistId);

        Assert.That(ranking.RankedMapCount, Is.EqualTo(1), "the refunded scores are on the one ranked map");
        Assert.That(ranking.RankedScore, Is.EqualTo(400_000), "and the best of them is the player's ranked score there");
    }

    // ---- audit + idempotency ----

    [Test]
    public async Task EveryRefundedRow_IsAudited_WithTheNumbersItWasJudgedOn()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var logged = (await conn.QueryAsync<long>(
            "SELECT score_id FROM score_refunds WHERE migration = @migration ORDER BY score_id",
            new { migration = SkipGateRefund.MIGRATION_KEY })).ToList();

        var entry = await conn.QuerySingleAsync<(double ElapsedS, double OldRequiredS, double NewRequiredS)>(
            "SELECT elapsed_s, old_required_s, new_required_s FROM score_refunds WHERE score_id = @id",
            new { id = skipFastId });

        Assert.Multiple(() =>
        {
            Assert.That(logged, Is.EquivalentTo(new[] { skipFastId, exactlyOnTheBoundId, halfTimeId }));
            Assert.That(entry.ElapsedS, Is.EqualTo(60).Within(0.01));
            Assert.That(entry.OldRequiredS, Is.EqualTo(90).Within(1e-9));
            Assert.That(entry.NewRequiredS, Is.EqualTo(54).Within(1e-9));
        });
    }

    [Test]
    public async Task RunningItTwice_ChangesNothing()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var before = (await conn.QueryAsync<(long Id, bool Ranked)>(
            "SELECT id, ranked FROM scores ORDER BY id")).ToList();
        int loggedBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM score_refunds");

        // Re-apply both halves the way a lost schema_migrations row or a second boot would.
        await conn.ExecuteAsync(readEmbeddedMigration("016_refund_skip_gate.sql"));
        int again = await SkipGateRefund.RunAsync(new Db(dataSource), NullLogger.Instance);

        var after = (await conn.QueryAsync<(long Id, bool Ranked)>(
            "SELECT id, ranked FROM scores ORDER BY id")).ToList();
        int loggedAfter = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM score_refunds");

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(0), "the guard table is what makes a rerun a no-op");
            Assert.That(after, Is.EqualTo(before));
            Assert.That(loggedAfter, Is.EqualTo(loggedBefore));
        });
    }

    [Test]
    public async Task AManuallyUnrankedRefund_IsNotUndone()
    {
        // A reviewer unranking a refunded score by hand must stick: the guard row is permanent, so
        // the next boot does not reconsider it.
        await using var conn = await dataSource.OpenConnectionAsync();

        await conn.ExecuteAsync("UPDATE scores SET ranked = false WHERE id = @id", new { id = halfTimeId });

        await SkipGateRefund.RunAsync(new Db(dataSource), NullLogger.Instance);

        Assert.That(await rankedOf(conn, halfTimeId), Is.False);

        await conn.ExecuteAsync("UPDATE scores SET ranked = true WHERE id = @id", new { id = halfTimeId });
    }

    // ---- helpers ----

    private static async Task<bool> rankedOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<bool>("SELECT ranked FROM scores WHERE id = @id", new { id });

    private async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@typistId, @title, 'The Gapped', @status) RETURNING id",
            new { typistId, title, status });

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
            VALUES (@setId, 'type!beat', @checksum, @drain, @drain, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), drain = drain_s });

    /// <summary>
    /// One stored score. <paramref name="elapsed"/> becomes the started_at/ended_at span the gate
    /// measured; <paramref name="anchored"/> false leaves started_at null, as the oldest rows have it.
    /// </summary>
    private async Task<long> insertScoreAsync(
        NpgsqlConnection conn,
        long beatmapId,
        double elapsed,
        long buildId,
        bool ranked = false,
        bool passed = true,
        string rank = "X",
        long totalScore = 400_000,
        double completion = 1.0,
        string modsJson = "[]",
        string statisticsJson = """{"great": 10}""",
        bool anchored = true)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, build_id, started_at, ended_at)
            VALUES
                (@typistId, @beatmapId, @totalScore, 0.97, @completion, 10, @rank, @passed, @ranked,
                 CAST(@modsJson AS jsonb), CAST(@statisticsJson AS jsonb), '{"great": 10}'::jsonb, @buildId,
                 CASE WHEN @anchored THEN now() - make_interval(secs => @elapsed) END, now())
            RETURNING id
            """,
            new
            {
                typistId, beatmapId, totalScore, completion, rank, passed, ranked, modsJson, statisticsJson,
                buildId, anchored, elapsed,
            });

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
