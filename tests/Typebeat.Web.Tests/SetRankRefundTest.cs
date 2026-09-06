using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="SetRankRefund"/> against a fully migrated database carrying the shape it exists for:
/// honest plays stored unranked because their SET was pending at submit time, on a set that is
/// ranked now, mixed in with rows unranked for every other reason the submit path has.
///
/// <para>
/// Three claims, all of which the five real production rows depend on. It must flip exactly the
/// pending-set victims; it must stamp <c>pp_version = 0</c> on each so
/// <see cref="Typebeat.Web.Packages.PpBackfill"/> (which runs immediately after it) actually
/// prices them rather than skipping rows already at the current version; and it must be a no-op on
/// a second run, which for a STANDING sweep is a property of the candidate query rather than of a
/// guard row.
/// </para>
///
/// <para>
/// Same harness shape as <see cref="Migration016SkipGateRefundTest"/>, minus its
/// migration-by-migration replay: this pass is not tied to a migration, so the schema is simply
/// brought fully up to date and the rows are seeded into it.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class SetRankRefundTest
{
    private const string database_name = "typebeat_setrankrefundtests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private const double drain_s = 100;

    /// <summary>0.9 x drain with no skip allowance: what an unmodded play here has to clear.</summary>
    private static double Required => PlayTimeGate.RequiredSeconds(drain_s, 0);

    private NpgsqlDataSource dataSource = null!;

    private long typistId;
    private int refunded;

    // The shape the pass exists for: honest in every way, unranked only because the set was
    // pending, on a set that is ranked now.
    private long pendingAtSubmitId;
    private long onTheBoundId;

    // Unranked for a reason ranking the set does not change. None may move.
    private long puppeteerId;     // Now Is Gold +PT: unranked at every configuration
    private long conductorId;     // CT: the acronym the stale refund list used to let through
    private long dyslexiaId;      // DX: likewise
    private long relaxId;         // RX: the one the stale list did catch
    private long stillPendingId;  // its set is STILL pending
    private long tooFastId;       // below the play-time bound
    private long failedId;        // a failed play
    private long overCeilingId;   // a total its own statistics cannot justify
    private long blockedBuildId;  // a blocked build

    // Already ranked and already priced: never a candidate, and its pp stamp must not be reset.
    private long alreadyRankedId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var admin = new NpgsqlConnection(admin_connection_string))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database_name} WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE {database_name}");
        }

        await Db.EnsureExtensionsAsync(connection_string);

        dataSource = NpgsqlDataSource.Create(connection_string);

        // The whole schema, not a replay up to some migration: this pass is not a migration.
        await new Db(dataSource).MigrateAsync(NullLogger.Instance);

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            typistId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('latecomer', 'latecomer@example.com', 'x', 'US')
                RETURNING id
                """);

            long rankedSetId = await insertSetAsync(conn, "Now Is Gold", "ranked");
            long pendingSetIdValue = await insertSetAsync(conn, "Still In Review", "pending");

            long rankedMapId = await insertBeatmapAsync(conn, rankedSetId);
            long pendingMapId = await insertBeatmapAsync(conn, pendingSetIdValue);

            long cleanBuildId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO builds (version_hash, blocked) VALUES ('clean-build', false) RETURNING id");
            long blockedBuild = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO builds (version_hash, blocked) VALUES ('blocked-build', true) RETURNING id");

            // ---- the victims ----
            // ppVersion 19 on purpose: these rows really are stamped BELOW the current version in
            // production, which is what makes "the backfill will fix them" so tempting and so wrong.
            pendingAtSubmitId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId, ppVersion: 19);
            onTheBoundId = await insertScoreAsync(conn, rankedMapId, elapsed: Required, buildId: cleanBuildId, ppVersion: 19);

            // ---- everything else ----
            puppeteerId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId,
                modsJson: """[{"acronym": "PT"}]""");
            conductorId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId,
                modsJson: """[{"acronym": "CT"}]""");
            dyslexiaId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId,
                modsJson: """[{"acronym": "DX"}]""");
            relaxId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId,
                modsJson: """[{"acronym": "RX"}]""");
            stillPendingId = await insertScoreAsync(conn, pendingMapId, elapsed: 95, buildId: cleanBuildId);
            tooFastId = await insertScoreAsync(conn, rankedMapId, elapsed: Required - 0.1, buildId: cleanBuildId);
            failedId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId, passed: false, rank: "F");
            overCeilingId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId, totalScore: 1_500_000);
            blockedBuildId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: blockedBuild);

            alreadyRankedId = await insertScoreAsync(conn, rankedMapId, elapsed: 95, buildId: cleanBuildId,
                ranked: true, ppVersion: PerformancePoints.VERSION);
        }

        refunded = await SetRankRefund.RunAsync(new Db(dataSource), NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task APlaySubmittedWhileItsSetWasPending_IsRankedOnceTheSetIs()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        bool victim = await rankedOf(conn, pendingAtSubmitId);
        bool onTheBound = await rankedOf(conn, onTheBoundId);

        Assert.Multiple(() =>
        {
            Assert.That(victim, Is.True, "the shape the pass exists for");
            Assert.That(onTheBound, Is.True, "the play-time bound is inclusive, exactly as the gate is");
            Assert.That(refunded, Is.EqualTo(2), "and nothing else was touched");
        });
    }

    [Test]
    public async Task ARefundedRow_IsStampedBackToPpVersionZero()
    {
        // THE LATENT BUG THIS PINS. PpBackfill's sweep predicate is pp_version < VERSION, so a
        // re-ranked row left at its stored version is SKIPPED and stays at pp 0 while reading as
        // priced. Both existing refund passes only ever worked because they shipped beside a
        // VERSION bump, which dragged every row in regardless.
        await using var conn = await dataSource.OpenConnectionAsync();

        int refundedVersion = await ppVersionOf(conn, pendingAtSubmitId);
        int untouchedVersion = await ppVersionOf(conn, alreadyRankedId);
        int declinedVersion = await ppVersionOf(conn, puppeteerId);

        Assert.Multiple(() =>
        {
            Assert.That(refundedVersion, Is.Zero, "a refunded row must be repriced, not restamped at 19");
            Assert.That(untouchedVersion, Is.EqualTo(PerformancePoints.VERSION),
                "an already-ranked row is not a candidate and keeps its stamp");
            Assert.That(declinedVersion, Is.EqualTo(20), "a declined row is left exactly as it was");
        });
    }

    [Test]
    public async Task EveryOtherUnrankedScore_StaysUnranked()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var reasons = new (long Id, string Why)[]
        {
            (puppeteerId, "Puppeteer is unranked at every configuration, whatever the set's status"),
            (conductorId, "and so is Conductor, which the refund's stale private list used to let through"),
            (dyslexiaId, "and Dyslexia, likewise"),
            (relaxId, "and Mashing, which the stale list did catch"),
            (stillPendingId, "a set that is still pending"),
            (tooFastId, "0.1 s under the play-time bound is still impossible"),
            (failedId, "a failed play"),
            (overCeilingId, "a total its own statistics cannot justify"),
            (blockedBuildId, "a blocked build"),
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
    public void TheAlwaysUnrankedListIsTheSubmitPathsOwn()
    {
        // The other half of the Puppeteer case, stated where it cannot rot: the refund reads the
        // ONE canonical set rather than a private copy claiming to mirror it. That copy held
        // { RX, WU, WD } while the submit path had grown to six, so three of the rows above would
        // have been re-ranked here after the submit path refused them.
        Assert.That(UnrankedMods.ACRONYMS, Is.EquivalentTo(new[] { "RX", "WU", "WD", "CT", "DX", "PT" }));

        Assert.Multiple(() =>
        {
            foreach (string acronym in UnrankedMods.ACRONYMS)
                Assert.That(UnrankedMods.IsAlwaysUnranked(acronym.ToLowerInvariant()), Is.True, acronym);

            // A deny list: an acronym nobody has heard of, and the two difficulty increases that
            // deliberately are not on it, all read as ranked.
            Assert.That(UnrankedMods.IsAlwaysUnranked("ZZ"), Is.False);
            Assert.That(UnrankedMods.IsAlwaysUnranked("RE"), Is.False, "Recite ranks");
            Assert.That(UnrankedMods.IsAlwaysUnranked("FC"), Is.False, "and so does Fletcher");
            Assert.That(UnrankedMods.IsAlwaysUnranked(null), Is.False);
            Assert.That(UnrankedMods.IsAlwaysUnranked("  "), Is.False);
        });
    }

    [Test]
    public async Task RunningItTwice_ChangesNothing()
    {
        // A STANDING sweep has no guard row, so idempotence is a property of the candidate query:
        // a row it re-ranked is no longer unranked and therefore no longer a candidate.
        await using var conn = await dataSource.OpenConnectionAsync();

        var before = (await conn.QueryAsync<(long Id, bool Ranked, int PpVersion)>(
            "SELECT id, ranked, pp_version FROM scores ORDER BY id")).ToList();

        int again = await SetRankRefund.RunAsync(new Db(dataSource), NullLogger.Instance);

        var after = (await conn.QueryAsync<(long Id, bool Ranked, int PpVersion)>(
            "SELECT id, ranked, pp_version FROM scores ORDER BY id")).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.Zero, "the re-ranked rows have stopped being candidates");
            Assert.That(after, Is.EqualTo(before));
        });
    }

    [Test]
    public async Task RankingASetLater_HealsItsPlaysAtTheNextBoot()
    {
        // THE PROPERTY THE STANDING SHAPE BUYS, and the reason this is not a one-shot migration: a
        // set ranked at any point in the future heals its plays on the next boot, with no further
        // code, no new migration key and nothing to remember. A guarded pass would have declined
        // this row once and never looked at it again.
        await using var conn = await dataSource.OpenConnectionAsync();

        Assert.That(await rankedOf(conn, stillPendingId), Is.False, "the set is pending to begin with");

        await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = 'ranked' WHERE title = 'Still In Review'");

        int healed = await SetRankRefund.RunAsync(new Db(dataSource), NullLogger.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(healed, Is.EqualTo(1));
            Assert.That(rankedOf(conn, stillPendingId).Result, Is.True);
        });

        // Put the world back, so the ordering of these tests cannot matter.
        await conn.ExecuteAsync("UPDATE scores SET ranked = false WHERE id = @id", new { id = stillPendingId });
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'pending' WHERE title = 'Still In Review'");
    }

    // ---- helpers ----

    private static async Task<bool> rankedOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<bool>("SELECT ranked FROM scores WHERE id = @id", new { id });

    private static async Task<int> ppVersionOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<int>("SELECT pp_version FROM scores WHERE id = @id", new { id });

    private async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@typistId, @title, 'The Latecomers', @status) RETURNING id",
            new { typistId, title, status });

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
            VALUES (@setId, 'type!beat', @checksum, @drain, @drain, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), drain = drain_s });

    private async Task<long> insertScoreAsync(
        NpgsqlConnection conn,
        long beatmapId,
        double elapsed,
        long buildId,
        bool ranked = false,
        bool passed = true,
        string rank = "X",
        long totalScore = 400_000,
        string modsJson = "[]",
        int ppVersion = 20)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, build_id, started_at, ended_at, pp, pp_version)
            VALUES
                (@typistId, @beatmapId, @totalScore, 0.97, 1.0, 10, @rank, @passed, @ranked,
                 CAST(@modsJson AS jsonb), '{"great": 10}'::jsonb, '{"great": 10}'::jsonb, @buildId,
                 now() - make_interval(secs => @elapsed), now(), 0, @ppVersion)
            RETURNING id
            """,
            new { typistId, beatmapId, totalScore, rank, passed, ranked, modsJson, buildId, elapsed, ppVersion });
}
