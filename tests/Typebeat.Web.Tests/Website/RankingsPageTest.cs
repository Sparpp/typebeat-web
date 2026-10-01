using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Typebeat.Web.Pages.Rankings;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Global rankings page, all three boards:
///
/// <list type="bullet">
/// <item>the MAIN board, total pp: best-pp play per ranked SONG (the set, not the difficulty),
/// decay-weighted over all of them (<see cref="PpRanking"/>, docs/pp.md);</item>
/// <item>the score board (<c>?board=score</c>), unchanged: cumulative score = sum of best-per-map
/// scores on RANKED maps only. That metric is still per BEATMAP, which is why the seeds below can
/// move between sets without moving any figure this board asserts.</item>
/// <item>the top-plays board (<c>?board=plays</c>): INDIVIDUAL scores by pp descending, one row per
/// (player, song), so one player can hold several rows.</item>
/// </list>
///
/// The pp rows are seeded with EXPLICIT pp values rather than by playing maps, so the aggregation
/// is tested independently of the per-play formula (which <c>PerformancePointsTest</c> owns).
/// Seeds its own users/sets (unique names) on top of <see cref="PublicSiteSeed"/> and asserts only
/// on those rows, so it never depends on (or disturbs) the shared fixtures' counts. The top-plays
/// seeds use deliberately HUGE pp values (thousands) so they cannot be pushed off the board's
/// LIMIT by whatever the rest of the suite happens to have submitted into the shared database.
///
/// <para>
/// ONE SET PER SEEDED SONG, and it matters (backlog 162). pp dedups on the SET, so two beatmaps
/// sharing a set are two difficulties of one song and bank ONE weighted entry between them. Every
/// seed below that means "a different song" therefore gets its own <c>insertSetAsync</c>, and the
/// only place two beatmaps deliberately share a set is
/// <see cref="TotalPp_KeepsOnlyTheBestPpPlayPerSongAcrossDifficulties"/>, which is the test of that
/// rule. Do not re-merge them to save a row: every other assertion here would then be measuring the
/// set fold instead of the thing it names.
/// </para>
/// </summary>
[NonParallelizable]
public class RankingsPageTest
{
    private long aliceId;
    private long bobId;
    private long dedupId;
    private long decayId;
    private long topPlayId;
    private long runnerUpId;
    private long tieFirstId;
    private long tieSecondId;
    private long rateModId;
    private long oneSongId;

    /// <summary>The rate-mod probe map's three ratings, all distinct and unique to this test so an
    /// assertion on the rendered number cannot match any other row.</summary>
    private const double base_stars = 2.22;
    private const double dt_stars = 7.77;
    private const double ht_stars = 1.11;

    /// <summary>How many distinct ranked SONGS the decay player set a play on (deliberately > 10).
    /// One set each, because the fold is per set: twelve difficulties of one song would collapse to
    /// a single weighted entry and this fixture would stop testing decay at all.</summary>
    private const int decay_plays = 12;

    /// <summary>Every one of those plays is worth the same, so the total is purely the decay series.</summary>
    private const double decay_play_pp = 100;

    /// <summary>The one-song player's better difficulty: worth the most pp, scored the least.</summary>
    private const double one_song_best_pp = 260;

    /// <summary>Their other difficulty of the SAME song: bigger score, less pp, must bank nothing.</summary>
    private const double one_song_weak_pp = 45;

    /// <summary>
    /// How many filler players the pagination seeds add (backlog 299): enough that every board's
    /// population is comfortably past two 50-row pages, cheap enough (two inserts each) that the
    /// suite does not feel it.
    /// </summary>
    private const int paging_users = 120;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        aliceId = await insertUserAsync(conn, "rk alice");
        bobId = await insertUserAsync(conn, "rk bob");
        dedupId = await insertUserAsync(conn, "rk dedup");
        decayId = await insertUserAsync(conn, "rk decay");
        long restrictedId = await insertUserAsync(conn, "rk restricted", restricted: true);
        long deletedId = await insertUserAsync(conn, "rk deleted", deleted: true);

        // Two SONGS, so alice's two plays are two weighted entries. One set holding both maps would
        // make them difficulties of one song and fold to a single entry (backlog 162).
        long rankedSetA = await insertSetAsync(conn, "Rankings Ranked Set A", "ranked");
        long mapA = await insertBeatmapAsync(conn, rankedSetA);

        long rankedSetB = await insertSetAsync(conn, "Rankings Ranked Set B", "ranked");
        long mapB = await insertBeatmapAsync(conn, rankedSetB);

        long pendingSet = await insertSetAsync(conn, "Rankings Pending Set", "pending");
        long mapC = await insertBeatmapAsync(conn, pendingSet);

        // alice: best-per-map folds 300k (not 300k+100k) on A, plus 200k on B = 500k over 2 maps
        // for the SCORE board, and 40 + 20 x decay over 2 SONGS for the pp board.
        await insertScoreAsync(conn, aliceId, mapA, 300_000, pp: 40);
        await insertScoreAsync(conn, aliceId, mapA, 100_000, pp: 10);
        await insertScoreAsync(conn, aliceId, mapB, 200_000, pp: 20);

        // bob: 400k on A. His unranked-flag and failed scores must not count, on EITHER board, so
        // both carry a pp large enough that leaking one would be unmissable.
        await insertScoreAsync(conn, bobId, mapA, 400_000, pp: 30);
        await insertScoreAsync(conn, bobId, mapA, 888_888, pp: 8888, ranked: false);
        await insertScoreAsync(conn, bobId, mapB, 777_777, pp: 7777, passed: false);

        // Pending-map scores contribute nothing (alice stays at 500k and at her two-map pp).
        await insertScoreAsync(conn, aliceId, mapC, 555_555, pp: 5555);

        // Delisted accounts never surface, however large their totals.
        await insertScoreAsync(conn, restrictedId, mapA, 911_111, pp: 9111);
        await insertScoreAsync(conn, deletedId, mapA, 922_222, pp: 9222);

        // Per-map dedup: only the BEST-pp play on a map counts, and "best" is by pp, not by score.
        // The 120 pp play is deliberately the LOWER-scoring one, so a fold that ordered by
        // total_score would pick the wrong row and total 50 + 30 x DECAY instead.
        //
        // X and Y are two SONGS, one set each: this fixture is about the retry fold, so the set
        // fold must not also be firing here or the two rules become indistinguishable.
        long dedupSetX = await insertSetAsync(conn, "Rankings Dedup Set X", "ranked");
        long dedupMapX = await insertBeatmapAsync(conn, dedupSetX);

        long dedupSetY = await insertSetAsync(conn, "Rankings Dedup Set Y", "ranked");
        long dedupMapY = await insertBeatmapAsync(conn, dedupSetY);

        await insertScoreAsync(conn, dedupId, dedupMapX, 900_000, pp: 50);
        await insertScoreAsync(conn, dedupId, dedupMapX, 100_000, pp: 120);
        await insertScoreAsync(conn, dedupId, dedupMapY, 500_000, pp: 30);

        // Decay: 12 equal plays on 12 distinct ranked SONGS, a set each. More than ten, so this
        // also proves there is no hard top-10 truncation. Twelve difficulties of ONE set would fold
        // to one entry and this would silently stop being a decay test.
        for (int i = 0; i < decay_plays; i++)
        {
            long decaySet = await insertSetAsync(conn, $"Rankings Decay Set {i:00}", "ranked");
            long map = await insertBeatmapAsync(conn, decaySet);
            await insertScoreAsync(conn, decayId, map, 250_000, pp: decay_play_pp);
        }

        // ---- top-plays board ----
        //
        // Two players sharing one song plus a second song for the leader, so the board has to order
        // SCORES (not players) and let one player hold more than one row. The leader's second play
        // on map P is the dedup probe: same map, same player, lower pp, must never be listed.
        // P and Q are separate SETS, or the leader's two entries would fold into one.
        topPlayId = await insertUserAsync(conn, "rk topplay");
        runnerUpId = await insertUserAsync(conn, "rk runnerup");
        tieFirstId = await insertUserAsync(conn, "rk tiefirst");
        tieSecondId = await insertUserAsync(conn, "rk tiesecond");

        long topSetP = await insertSetAsync(conn, "Rankings Top Plays Set", "ranked");
        long topMapP = await insertBeatmapAsync(conn, topSetP);

        long topSetQ = await insertSetAsync(conn, "Rankings Top Plays Set Q", "ranked");
        long topMapQ = await insertBeatmapAsync(conn, topSetQ);

        await insertScoreAsync(conn, topPlayId, topMapP, 800_000, pp: 6000,
            statistics: """{"great":100,"miss":3,"combo_break":7}""");
        await insertScoreAsync(conn, topPlayId, topMapP, 700_000, pp: 5900);
        await insertScoreAsync(conn, topPlayId, topMapQ, 600_000, pp: 5000);
        await insertScoreAsync(conn, runnerUpId, topMapP, 750_000, pp: 5500);

        // A Double Time play, on a map whose three star ratings are all distinct and unique to this
        // test. pp prices rate exclusively through the rating recomputed at the play's clock rate
        // (docs/pp.md), so the board must show sr_dt here, never the base rating.
        rateModId = await insertUserAsync(conn, "rk ratemod");

        long rateSet = await insertSetAsync(conn, "Rankings Rate Mod Set", "ranked");
        long rateMap = await insertBeatmapAsync(conn, rateSet, stars: base_stars, srDt: dt_stars, srHt: ht_stars);
        await insertScoreAsync(conn, rateModId, rateMap, 450_000, pp: 4500, mods: """[{"acronym":"DT"}]""");

        // Equal pp on two different SONGS: the tie must break on the EARLIER submission, so the
        // board is a total order and never reshuffles between renders.
        long tieSetA = await insertSetAsync(conn, "Rankings Tie Set A", "ranked");
        long tieMapA = await insertBeatmapAsync(conn, tieSetA);

        long tieSetB = await insertSetAsync(conn, "Rankings Tie Set B", "ranked");
        long tieMapB = await insertBeatmapAsync(conn, tieSetB);

        await insertScoreAsync(conn, tieFirstId, tieMapA, 400_000, pp: 4000);
        await insertScoreAsync(conn, tieSecondId, tieMapB, 400_000, pp: 4000);

        // ---- the set fold itself (backlog 162) ----
        //
        // The ONLY seed here where two beatmaps share a set on purpose: one song, two difficulties,
        // one player. Only the better-pp difficulty may bank anything, and the set counts once.
        //
        // The higher-pp difficulty is deliberately the LOWER-scoring one, the same trap the retry
        // fold above sets, so a fold that ordered by total_score picks the wrong row and totals
        // one_song_weak_pp instead.
        oneSongId = await insertUserAsync(conn, "rk onesong");

        long oneSongSet = await insertSetAsync(conn, "Rankings One Song Set", "ranked");
        long oneSongHard = await insertBeatmapAsync(conn, oneSongSet);
        long oneSongEasy = await insertBeatmapAsync(conn, oneSongSet);

        await insertScoreAsync(conn, oneSongId, oneSongHard, 950_000, pp: one_song_weak_pp);
        await insertScoreAsync(conn, oneSongId, oneSongEasy, 150_000, pp: one_song_best_pp);

        // ---- pagination (backlog 299) ----
        //
        // 120 filler players, one small play each on one shared ranked song, so every board's
        // population is past two full pages. The figures are tiny ON PURPOSE: they sort below
        // every seed above, so no page-1 ordering assertion in this fixture moves, and no
        // pagination test ever names one of these players, only the rank numbers and pager links
        // their volume forces into existence. (For the shapes volume cannot force on a shared
        // database, a one-page board and a deep ellipsis window, the tests below move
        // IndexModel.PageSize instead of seeding hundreds more.)
        long pagingSet = await insertSetAsync(conn, "Rankings Paging Set", "ranked");
        long pagingMap = await insertBeatmapAsync(conn, pagingSet);

        for (int i = 0; i < paging_users; i++)
        {
            long fillerId = await insertUserAsync(conn, $"rk page {i:000}");
            await insertScoreAsync(conn, fillerId, pagingMap, 1_000 + i, pp: 0.5 + i * 0.001);
        }
    }

    /// <summary>Σ pp·decay^i over the seeded equal-value plays: 100 · (1 − DECAY^12) / (1 − DECAY).</summary>
    private static double ExpectedDecayTotal
    {
        get
        {
            double total = 0;

            for (int i = 0; i < decay_plays; i++)
                total += decay_play_pp * Math.Pow(PerformancePoints.DECAY, i);

            return total;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The aggregation SQL itself (PpRanking.PerUserTotalSql), read straight off the database.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task TotalPp_KeepsOnlyTheBestPpPlayPerMap()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var pp = await PpRanking.ForUserAsync(conn, dedupId);

        Assert.Multiple(() =>
        {
            // 120 (map X's best pp, though not its best score) + 30 x DECAY (map Y).
            Assert.That(pp.TotalPp, Is.EqualTo(120 + 30 * PerformancePoints.DECAY).Within(1e-9));
            Assert.That(pp.PpPlayCount, Is.EqualTo(2), "three plays across two songs fold to two");
        });
    }

    /// <summary>
    /// THE SET FOLD (backlog 162): pp is earned per SONG, not per difficulty. A player who clears
    /// two difficulties of one set banks only the better-pp one, and the set counts once toward
    /// <c>pp_play_count</c>.
    ///
    /// <para>
    /// The better-pp difficulty is the lower-SCORING one, so a fold that resolved the song by total
    /// score would bank <c>one_song_weak_pp</c> and be caught here rather than silently ranking the
    /// wrong play. The weaker difficulty contributing nothing is asserted as an exact total, not as
    /// an inequality: <c>one_song_best_pp + one_song_weak_pp * DECAY</c> is what the per-BEATMAP
    /// fold produced, and this is the one test that separates the two.
    /// </para>
    /// </summary>
    [Test]
    public async Task TotalPp_KeepsOnlyTheBestPpPlayPerSongAcrossDifficulties()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var pp = await PpRanking.ForUserAsync(conn, oneSongId);
        var mine = (await topPlaysAsync(conn)).Where(r => r.UserId == oneSongId).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(pp.TotalPp, Is.EqualTo(one_song_best_pp).Within(1e-9),
                "the song's weaker difficulty must contribute nothing at all");
            Assert.That(pp.TotalPp, Is.Not.EqualTo(one_song_best_pp + one_song_weak_pp * PerformancePoints.DECAY)
                .Within(1e-9), "that total is the old per-beatmap fold");
            Assert.That(pp.PpPlayCount, Is.EqualTo(1), "two difficulties of one song count once");

            // The top-plays board is built from the same fragment, so the one banked play is also
            // the one and only row this player holds there.
            Assert.That(mine.Select(r => r.Pp), Is.EqualTo(new[] { one_song_best_pp }));
        });
    }

    [Test]
    public async Task TotalPp_WeightsEveryPlayWithNoTopTenCutoff()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var pp = await PpRanking.ForUserAsync(conn, decayId);

        // What a hard top-10 truncation would have produced, i.e. what this must NOT equal.
        double topTenOnly = 0;
        for (int i = 0; i < 10; i++)
            topTenOnly += decay_play_pp * Math.Pow(PerformancePoints.DECAY, i);

        Assert.Multiple(() =>
        {
            Assert.That(pp.PpPlayCount, Is.EqualTo(decay_plays));
            Assert.That(pp.TotalPp, Is.EqualTo(ExpectedDecayTotal).Within(1e-9));
            Assert.That(pp.TotalPp, Is.GreaterThan(topTenOnly + 1),
                "the 11th and 12th plays must still contribute something");
        });
    }

    [Test]
    public async Task TotalPp_ExcludesUnrankedFailedAndNonRankedSetPlays()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var bob = await PpRanking.ForUserAsync(conn, bobId);
        var alice = await PpRanking.ForUserAsync(conn, aliceId);

        Assert.Multiple(() =>
        {
            // Bob's only qualifying play is the 30 pp one; the 8888 (unranked flag) and 7777
            // (failed) rows must never reach the fold.
            Assert.That(bob.TotalPp, Is.EqualTo(30).Within(1e-9));
            Assert.That(bob.PpPlayCount, Is.EqualTo(1));

            // Alice: 40 on A (her 10 pp replay folds away), 20 on B; the 5555 on a PENDING set is
            // excluded by the set-status join.
            Assert.That(alice.TotalPp, Is.EqualTo(40 + 20 * PerformancePoints.DECAY).Within(1e-9));
            Assert.That(alice.PpPlayCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task TotalPp_DelistsRestrictedAndDeletedAccounts()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long delisted = await conn.ExecuteScalarAsync<long>(
            $"""
             SELECT count(*) FROM ({PpRanking.PerUserTotalSql}) t
             JOIN users u ON u.id = t.user_id
             WHERE u.username IN ('rk restricted', 'rk deleted')
             """);

        Assert.That(delisted, Is.Zero);
    }

    // ---------------------------------------------------------------------------------------------
    // The page.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Rankings_DefaultsToThePerformanceBoard()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // The pp column, and the decay player's exact weighted total, rendered as pp.
            Assert.That(html, Does.Contain(">pp<"));
            Assert.That(html, Does.Contain(ExpectedDecayTotal.ToString("N0", CultureInfo.InvariantCulture) + "pp"));
            Assert.That(html, Does.Contain($"href=\"/users/{decayId}\""));
            Assert.That(html, Does.Contain($"href=\"/users/{dedupId}\""));

            // The decay player (572 pp) outranks the dedup player (146 pp), who outranks alice.
            Assert.That(html.IndexOf("rk decay", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("rk dedup", StringComparison.Ordinal)));
            Assert.That(html.IndexOf("rk dedup", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("rk alice", StringComparison.Ordinal)));

            // Nothing from the unranked flag, the fail, or the pending map leaked in.
            Assert.That(html, Does.Not.Contain("8,888pp"));
            Assert.That(html, Does.Not.Contain("7,777pp"));
            Assert.That(html, Does.Not.Contain("5,555pp"));

            Assert.That(html, Does.Not.Contain("rk restricted"));
            Assert.That(html, Does.Not.Contain("rk deleted"));
        });
    }

    [Test]
    public async Task Rankings_AllThreeTabsAreLinkedFromEveryBoard()
    {
        using var performance = await WebsiteFixture.Client.GetAsync("/rankings");
        using var score = await WebsiteFixture.Client.GetAsync("/rankings?board=score");
        using var plays = await WebsiteFixture.Client.GetAsync("/rankings?board=plays");

        string performanceHtml = await performance.Content.ReadAsStringAsync();
        string scoreHtml = await score.Content.ReadAsStringAsync();
        string playsHtml = await plays.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            foreach (string html in new[] { performanceHtml, scoreHtml, playsHtml })
            {
                Assert.That(html, Does.Contain("href=\"/rankings\""));
                Assert.That(html, Does.Contain("href=\"/rankings?board=score\""));
                Assert.That(html, Does.Contain("href=\"/rankings?board=plays\""));
            }

            // Exactly one tab is active per board, and it is the right one.
            Assert.That(performanceHtml, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings\">Performance</a>"));
            Assert.That(scoreHtml, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings?board=score\">Score</a>"));
            Assert.That(playsHtml, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings?board=plays\">Top plays</a>"));

            // The tab strip's order: Top plays sits to the RIGHT of Score.
            Assert.That(playsHtml.IndexOf(">Score</a>", StringComparison.Ordinal),
                Is.LessThan(playsHtml.IndexOf(">Top plays</a>", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task Rankings_UnknownBoardFallsBackToPerformance()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings?board=nonsense");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings\">Performance</a>"));
        });
    }

    [Test]
    public async Task Rankings_ScoreTab_SumsBestPerRankedMap_AndDelistsExcludedUsers()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings?board=score");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Both players list, linked to their profiles, with folded cumulative totals.
            Assert.That(html, Does.Contain($"href=\"/users/{aliceId}\""));
            Assert.That(html, Does.Contain($"href=\"/users/{bobId}\""));
            Assert.That(html, Does.Contain("500,000"));
            Assert.That(html, Does.Contain("400,000"));

            // alice (500k) ranks above bob (400k).
            Assert.That(html.IndexOf("rk alice", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("rk bob", StringComparison.Ordinal)));

            // Nothing from the unranked flag, the fail, or the pending map leaked into totals.
            Assert.That(html, Does.Not.Contain("888,888"));
            Assert.That(html, Does.Not.Contain("777,777"));
            Assert.That(html, Does.Not.Contain("555,555"));

            // Restricted + deleted accounts are delisted entirely.
            Assert.That(html, Does.Not.Contain("rk restricted"));
            Assert.That(html, Does.Not.Contain("rk deleted"));
            Assert.That(html, Does.Not.Contain("911,111"));
            Assert.That(html, Does.Not.Contain("922,222"));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Top plays (?board=plays): individual SCORES by pp, one row per (player, song).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The board's row set, read straight off the database through the very fragments the page
    /// embeds, so the eligibility and dedup assertions below test the shared definition rather than
    /// a copy of it.
    /// </summary>
    private static async Task<List<(long ScoreId, long UserId, double Pp)>> topPlaysAsync(NpgsqlConnection conn)
        => (await conn.QueryAsync<(long ScoreId, long UserId, double Pp)>(
            $"""
             SELECT best.id AS ScoreId, best.user_id AS UserId, best.pp AS Pp
             FROM ({PpRanking.BestPerSetSql}) best
             ORDER BY {PpRanking.TopPlaysOrder("best")}
             """)).ToList();

    [Test]
    public async Task TopPlays_OrdersByPpDescending_WithATotalTieBreak()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var rows = await topPlaysAsync(conn);

        Assert.Multiple(() =>
        {
            // Monotonically non-increasing pp, and strictly increasing score id inside a tie: the
            // order is TOTAL, so a LIMIT always cuts in the same place.
            for (int i = 1; i < rows.Count; i++)
            {
                Assert.That(rows[i].Pp, Is.LessThanOrEqualTo(rows[i - 1].Pp), $"row {i} out of pp order");

                if (rows[i].Pp == rows[i - 1].Pp)
                    Assert.That(rows[i].ScoreId, Is.GreaterThan(rows[i - 1].ScoreId), $"row {i} broke the tie backwards");
            }

            // The seeded equal-pp pair specifically: the earlier submission wins.
            int first = rows.FindIndex(r => r.UserId == tieFirstId);
            int second = rows.FindIndex(r => r.UserId == tieSecondId);

            Assert.That(first, Is.GreaterThanOrEqualTo(0));
            Assert.That(second, Is.GreaterThan(first), "equal pp must break on the earlier score id");
        });
    }

    [Test]
    public async Task TopPlays_KeepsOneRowPerSongButLetsAPlayerHoldSeveral()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var mine = (await topPlaysAsync(conn)).Where(r => r.UserId == topPlayId).ToList();

        Assert.Multiple(() =>
        {
            // Two songs, three plays: the 5900 retry on the same map as the 6000 folds away, and
            // the player still holds BOTH of their per-song bests (no per-user dedup).
            Assert.That(mine.Select(r => r.Pp), Is.EqualTo(new[] { 6000d, 5000d }));
        });
    }

    [Test]
    public async Task TopPlays_ExcludesUnrankedFailedNonRankedSetAndDelistedAccounts()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var rows = await topPlaysAsync(conn);
        var pps = rows.Select(r => r.Pp).ToList();
        var users = rows.Select(r => r.UserId).ToHashSet();

        long restrictedId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username = 'rk restricted'");
        long deletedId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username = 'rk deleted'");

        Assert.Multiple(() =>
        {
            Assert.That(pps, Does.Not.Contain(8888d), "a score with ranked = false must not be a top play");
            Assert.That(pps, Does.Not.Contain(7777d), "a failed score must not be a top play");
            Assert.That(pps, Does.Not.Contain(5555d), "a play on a PENDING set must not be a top play");

            Assert.That(users, Does.Not.Contain(restrictedId));
            Assert.That(users, Does.Not.Contain(deletedId));
            Assert.That(pps, Does.Not.Contain(9111d));
            Assert.That(pps, Does.Not.Contain(9222d));

            // The board is not empty for the wrong reason: the eligible seeds are all there.
            Assert.That(pps, Does.Contain(6000d));
            Assert.That(pps, Does.Contain(5500d));
        });
    }

    [Test]
    public async Task TopPlays_RendersScoreRowsOrderedByPp()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings?board=plays");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Players and their maps, on the same link targets the other boards and the score rows
            // elsewhere on the site use.
            Assert.That(html, Does.Contain($"href=\"/users/{topPlayId}\""));
            Assert.That(html, Does.Contain($"href=\"/users/{runnerUpId}\""));
            Assert.That(html, Does.Contain("Rankings Top Plays Set"));

            // pp, invariant-culture N0 like every other pp on the site.
            Assert.That(html, Does.Contain("6,000pp"));
            Assert.That(html, Does.Contain("5,500pp"));
            Assert.That(html, Does.Contain("5,000pp"));

            // 6000 (topplay) > 5500 (runnerup) > 5000 (topplay again).
            Assert.That(html.IndexOf("6,000pp", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("5,500pp", StringComparison.Ordinal)));
            Assert.That(html.IndexOf("5,500pp", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("5,000pp", StringComparison.Ordinal)));

            // The retry on an already-listed map, and everything ineligible, stay off the page.
            Assert.That(html, Does.Not.Contain("5,900pp"));
            Assert.That(html, Does.Not.Contain("8,888pp"));
            Assert.That(html, Does.Not.Contain("7,777pp"));
            Assert.That(html, Does.Not.Contain("5,555pp"));
            Assert.That(html, Does.Not.Contain("rk restricted"));
            Assert.That(html, Does.Not.Contain("rk deleted"));

            // Score-shaped columns, not the per-user aggregate ones.
            Assert.That(html, Does.Contain("<th>Map</th>"));
            Assert.That(html, Does.Contain("<th>Grade</th>"));
            Assert.That(html, Does.Not.Contain("<th>Cumulative score</th>"));

            // The Typos column appears because one seeded play carries the stat (docs/pp.md:
            // absence is not zero, so it is a conditional column exactly like the set page's). ONE
            // typo column since backlog 140: the seal-state count that used to sit beside it is no
            // longer surfaced, so no board shows two typo numbers.
            Assert.That(html, Does.Contain("<th>Typos</th>"));
            Assert.That(html, Does.Not.Contain("<th>Typo</th>"));
            Assert.That(html, Does.Not.Contain("<th>Mistype</th>"));
        });
    }

    [Test]
    public async Task TopPlays_ShowsTheStarRatingThePlayWasPricedAt_NotTheBaseOne()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings?board=plays");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // The row is on the board, and it is the DT one (the mod badge carries its rate).
            Assert.That(html, Does.Contain($"href=\"/users/{rateModId}\""));
            Assert.That(html, Does.Contain("1.50x"));

            // pp prices DT/HT through the rating recomputed at the play's clock rate and NEVER as a
            // flat multiplier (docs/pp.md), so the column must read sr_dt. Showing the base rating
            // beside a pp it did not produce is exactly what a future refactor would regress to.
            Assert.That(html, Does.Contain("&#9733; " + dt_stars.ToString("0.0#", CultureInfo.InvariantCulture)));
            Assert.That(html, Does.Not.Contain("&#9733; " + base_stars.ToString("0.0#", CultureInfo.InvariantCulture)));
            Assert.That(html, Does.Not.Contain("&#9733; " + ht_stars.ToString("0.0#", CultureInfo.InvariantCulture)));
        });
    }

    [Test]
    public async Task TopPlays_ListsOnePlayerOnSeveralRows()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings?board=plays");
        string html = await response.Content.ReadAsStringAsync();

        int rows = Regex.Matches(html, Regex.Escape($"href=\"/users/{topPlayId}\"")).Count;

        Assert.That(rows, Is.EqualTo(2),
            "a player with two eligible maps holds two rows: this board ranks plays, not players");
    }

    // ---------------------------------------------------------------------------------------------
    // Pagination (backlog 299): ?page=N, offset paging, ranks continuing across pages.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void PagerWindow_RendersTheConventionalEllipsisShapes()
    {
        Assert.Multiple(() =>
        {
            // One page is just itself (the page hides the pager entirely then, asserted below).
            Assert.That(IndexModel.PagerWindow(1, 1), Is.EqualTo(new int?[] { 1 }));
            Assert.That(IndexModel.PagerWindow(1, 2), Is.EqualTo(new int?[] { 1, 2 }));

            // Small counts elide nothing.
            Assert.That(IndexModel.PagerWindow(3, 6), Is.EqualTo(new int?[] { 1, 2, 3, 4, 5, 6 }));

            // A gap of exactly one page shows that page: never an ellipsis wider than it hides.
            Assert.That(IndexModel.PagerWindow(5, 9), Is.EqualTo(new int?[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

            // The head shape, the middle shape (a gap each side) and the tail shape.
            Assert.That(IndexModel.PagerWindow(1, 50), Is.EqualTo(new int?[] { 1, 2, 3, null, 50 }));
            Assert.That(IndexModel.PagerWindow(25, 50), Is.EqualTo(new int?[] { 1, null, 23, 24, 25, 26, 27, null, 50 }));
            Assert.That(IndexModel.PagerWindow(50, 50), Is.EqualTo(new int?[] { 1, null, 48, 49, 50 }));

            // Near an edge the ellipsis appears on the far side only.
            Assert.That(IndexModel.PagerWindow(4, 50), Is.EqualTo(new int?[] { 1, 2, 3, 4, 5, 6, null, 50 }));
            Assert.That(IndexModel.PagerWindow(47, 50), Is.EqualTo(new int?[] { 1, null, 45, 46, 47, 48, 49, 50 }));
        });
    }

    /// <summary>
    /// Rank numbers CONTINUE across pages on all three boards: page 2's first row is #51, not a
    /// second #1. The paging seeds guarantee page 2 is full everywhere, so #100 is there too.
    /// </summary>
    [Test]
    public async Task Rankings_PageTwo_ContinuesTheRankNumbers_OnEveryBoard()
    {
        foreach (string url in new[] { "/rankings", "/rankings?board=score", "/rankings?board=plays" })
        {
            string separator = url.Contains('?') ? "&" : "?";

            using var first = await WebsiteFixture.Client.GetAsync(url);
            using var second = await WebsiteFixture.Client.GetAsync(url + separator + "page=2");

            string firstHtml = await first.Content.ReadAsStringAsync();
            string secondHtml = await second.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);

                Assert.That(firstHtml, Does.Contain(">#1<"), url);
                Assert.That(firstHtml, Does.Contain(">#50<"), url);
                Assert.That(firstHtml, Does.Not.Contain(">#51<"), url);

                Assert.That(secondHtml, Does.Contain(">#51<"), url);
                Assert.That(secondHtml, Does.Contain(">#100<"), url);
                Assert.That(secondHtml, Does.Not.Contain(">#1<"), url);
            });
        }
    }

    [Test]
    public async Task Rankings_Pager_PreservesTheBoardAndUnlinksTheCurrentPage()
    {
        using var score = await WebsiteFixture.Client.GetAsync("/rankings?board=score&page=2");
        using var performance = await WebsiteFixture.Client.GetAsync("/rankings?page=2");

        string scoreHtml = await score.Content.ReadAsStringAsync();
        string performanceHtml = await performance.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Numbered neighbours keep the board selector (& renders HTML-encoded in the href).
            Assert.That(scoreHtml, Does.Contain("href=\"/rankings?board=score&amp;page=3\""));

            // Page 1 is the board's canonical URL with no redundant page=1, so the prev arrow's
            // target is byte-identical to the Score tab's own href.
            Assert.That(scoreHtml, Does.Contain("rel=\"prev\" aria-label=\"Previous page\" href=\"/rankings?board=score\""));

            // The current page is a highlighted span, not a link: nothing links to page 2. (The
            // head's canonical names page 2 by design, backlog 369, so only hrefs are checked.)
            Assert.That(scoreHtml, Does.Contain("<span class=\"pager-item is-current\" aria-current=\"page\">2</span>"));
            Assert.That(scoreHtml, Does.Not.Contain("href=\"/rankings?board=score&amp;page=2\""));

            // The main board's pager carries no board param at all.
            Assert.That(performanceHtml, Does.Contain("href=\"/rankings?page=3\""));
            Assert.That(performanceHtml, Does.Contain("rel=\"prev\" aria-label=\"Previous page\" href=\"/rankings\""));
            Assert.That(performanceHtml, Does.Not.Contain("board=performance"));
        });
    }

    /// <summary>
    /// A bad ?page never 404s, the same stance the board fallback takes on a bad ?board: zero,
    /// negative and unparseable values land on page 1, and a page past the end lands on the LAST
    /// page, whose number is computed from the same count the page itself uses.
    /// </summary>
    [Test]
    public async Task Rankings_Pager_ClampsOutOfRangeAndGarbagePages()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long total = await conn.ExecuteScalarAsync<long>(IndexModel.PerformanceCountSql);
        int last = (int)((total + IndexModel.PageSize - 1) / IndexModel.PageSize);
        int lastPageFirstRank = (last - 1) * IndexModel.PageSize + 1;

        using var over = await WebsiteFixture.Client.GetAsync("/rankings?page=999999");
        using var zero = await WebsiteFixture.Client.GetAsync("/rankings?page=0");
        using var negative = await WebsiteFixture.Client.GetAsync("/rankings?page=-3");
        using var garbage = await WebsiteFixture.Client.GetAsync("/rankings?page=pearl");

        string overHtml = await over.Content.ReadAsStringAsync();
        string zeroHtml = await zero.Content.ReadAsStringAsync();
        string negativeHtml = await negative.Content.ReadAsStringAsync();
        string garbageHtml = await garbage.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(overHtml, Does.Contain($">#{lastPageFirstRank}<"), "past the end clamps to the last page");
            Assert.That(overHtml, Does.Contain($"aria-current=\"page\">{last}</span>"));

            foreach ((HttpResponseMessage response, string html) in new[]
                     { (zero, zeroHtml), (negative, negativeHtml), (garbage, garbageHtml) })
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(html, Does.Contain(">#1<"), "everything unusable clamps to page 1");
            }
        });
    }

    /// <summary>
    /// Each board's page count comes from a count(*) over the very fragment its rows are read
    /// from, so the two cannot disagree: the count equals the row population, and a delisted
    /// account is missing from both (its exclusion from the rows is pinned above, in
    /// <see cref="TotalPp_DelistsRestrictedAndDeletedAccounts"/> and friends).
    /// </summary>
    [Test]
    public async Task BoardCounts_MatchTheRowPopulations_AndExcludeTheDelisted()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long performanceCount = await conn.ExecuteScalarAsync<long>(IndexModel.PerformanceCountSql);
        long scoreCount = await conn.ExecuteScalarAsync<long>(IndexModel.ScoreCountSql);
        long playsCount = await conn.ExecuteScalarAsync<long>(IndexModel.PlaysCountSql);

        var performanceUsers = (await conn.QueryAsync<long>(
            $"SELECT user_id FROM ({PpRanking.PerUserTotalSql}) totals")).ToList();
        var scoreUsers = (await conn.QueryAsync<long>(
            $"SELECT user_id FROM ({GlobalRanking.PerUserCumulativeSql}) totals")).ToList();
        var plays = await topPlaysAsync(conn);

        long restrictedId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username = 'rk restricted'");
        long deletedId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username = 'rk deleted'");

        Assert.Multiple(() =>
        {
            Assert.That(performanceCount, Is.EqualTo(performanceUsers.Count));
            Assert.That(scoreCount, Is.EqualTo(scoreUsers.Count));
            Assert.That(playsCount, Is.EqualTo(plays.Count));

            foreach (var users in new[] { performanceUsers, scoreUsers, plays.Select(p => p.UserId).ToList() })
            {
                Assert.That(users, Does.Not.Contain(restrictedId));
                Assert.That(users, Does.Not.Contain(deletedId));
            }

            // The paging seeds did their job: every board really has more than two pages, so the
            // page-2 assertions above cannot be passing vacuously.
            Assert.That(performanceCount, Is.GreaterThan(100));
            Assert.That(scoreCount, Is.GreaterThan(100));
            Assert.That(playsCount, Is.GreaterThan(100));
        });
    }

    /// <summary>
    /// A one-page board shows no pager at all. A shared database's population cannot be steered
    /// under 50 rows, so the one-page shape is produced by GROWING the page instead: PageSize is
    /// settable for exactly this (and restored whatever happens; the fixture is NonParallelizable
    /// and this class is the only one that requests /rankings, so nothing else sees the window).
    /// </summary>
    [Test]
    public async Task Rankings_Pager_HiddenWhenEverythingFitsOnOnePage()
    {
        int normal = IndexModel.PageSize;

        try
        {
            IndexModel.PageSize = 1_000_000;

            using var response = await WebsiteFixture.Client.GetAsync("/rankings");
            string html = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(html, Does.Contain(">#1<"));
                Assert.That(html, Does.Not.Contain("class=\"pager\""));
                Assert.That(html, Does.Not.Contain("pager-item"));
            });
        }
        finally
        {
            IndexModel.PageSize = normal;
        }
    }

    /// <summary>
    /// The full pager end to end, at ten rows a page so the deep-middle shape exists on the real
    /// population: &lt; 1 ... 4 5 [6] 7 8 ... last &gt;, current unlinked, one ellipsis per side,
    /// and the rank numbers still continuing (page 6 of ten-row pages opens at #51).
    /// </summary>
    [Test]
    public async Task Rankings_Pager_RendersTheEllipsisWindowEndToEnd()
    {
        int normal = IndexModel.PageSize;

        try
        {
            IndexModel.PageSize = 10;

            await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
            await conn.OpenAsync();

            long total = await conn.ExecuteScalarAsync<long>(IndexModel.PerformanceCountSql);
            int last = (int)((total + 9) / 10);

            using var response = await WebsiteFixture.Client.GetAsync("/rankings?page=6");
            string html = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(last, Is.GreaterThanOrEqualTo(13), "the paging seeds alone force thirteen ten-row pages");

                Assert.That(html, Does.Contain("rel=\"prev\" aria-label=\"Previous page\" href=\"/rankings?page=5\""));
                Assert.That(html, Does.Contain("href=\"/rankings?page=4\">4</a>"));
                Assert.That(html, Does.Contain("href=\"/rankings?page=5\">5</a>"));
                Assert.That(html, Does.Contain("aria-current=\"page\">6</span>"));
                Assert.That(html, Does.Contain("href=\"/rankings?page=7\">7</a>"));
                Assert.That(html, Does.Contain("href=\"/rankings?page=8\">8</a>"));
                Assert.That(html, Does.Contain($"href=\"/rankings?page={last}\">{last}</a>"));
                Assert.That(html, Does.Contain("rel=\"next\" aria-label=\"Next page\" href=\"/rankings?page=7\""));

                Assert.That(Regex.Matches(html, Regex.Escape("class=\"pager-gap\"")).Count, Is.EqualTo(2),
                    "exactly one ellipsis on each side of the window");

                Assert.That(html, Does.Contain(">#51<"), "ranks continue: page 6 of ten-row pages opens at #51");
            });
        }
        finally
        {
            IndexModel.PageSize = normal;
        }
    }

    [Test]
    public async Task Rankings_LinkedFromSiteNav()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("href=\"/rankings\""));
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username, bool restricted = false, bool deleted = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted, deleted_at)
            VALUES (@username, @email, 'not-a-real-hash', 'US', @restricted, @deletedAt)
            RETURNING id
            """,
            new
            {
                username,
                email = username.Replace(' ', '.') + "@example.com",
                restricted,
                deletedAt = deleted ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            });

    private static async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'Rankings Artist', @status, now(), now())
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title, status });

    /// <summary>
    /// A beatmap row with its ratings. The RATING MATRIX (034_ratings_matrix.sql) is written
    /// alongside the three legacy columns and carries the same figures in its arm-none cells,
    /// because since PerformancePoints v22 that matrix is what the board resolves a play's
    /// EffectiveStars through: a row without one reads as a map the sweep has not reached and shows
    /// no rating at all.
    /// </summary>
    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId,
        double stars = 3.0, double? srDt = null, double? srHt = null)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, sr_dt, sr_ht, filename, word_count, char_count, wpm, ratings)
            VALUES (@setId, 'type!beat', @checksum, 90, 80, @stars, @srDt, @srHt, 'map.osu', 100, 500, 75,
                    @ratings::jsonb)
            RETURNING id
            """,
            new
            {
                setId, checksum = Guid.NewGuid().ToString("N"), stars, srDt, srHt,
                ratings = TestRatings.Json(stars, srDt, srHt),
            });

    private static async Task insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double pp = 0, bool ranked = true, bool passed = true,
        string statistics = """{"great":100}""", string mods = "[]")
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp, pp_version)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.97, 50, 'A', @passed, @ranked,
                 @mods::jsonb, @statistics::jsonb, '{"great":103}'::jsonb, @pp, @ppVersion)
            """,
            new { userId, beatmapId, totalScore, passed, ranked, pp, statistics, mods, ppVersion = PerformancePoints.VERSION });
}
