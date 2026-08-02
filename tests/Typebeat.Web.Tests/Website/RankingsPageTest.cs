using System.Globalization;
using System.Net;
using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Global rankings page, both boards:
///
/// <list type="bullet">
/// <item>the MAIN board, total pp: best-pp play per ranked map, decay-weighted over all of them
/// (<see cref="PpRanking"/>, docs/pp.md);</item>
/// <item>the score board (<c>?board=score</c>), unchanged: cumulative score = sum of best-per-map
/// scores on RANKED maps only.</item>
/// </list>
///
/// The pp rows are seeded with EXPLICIT pp values rather than by playing maps, so the aggregation
/// is tested independently of the per-play formula (which <c>PerformancePointsTest</c> owns).
/// Seeds its own users/sets (unique names) on top of <see cref="PublicSiteSeed"/> and asserts only
/// on those rows, so it never depends on (or disturbs) the shared fixtures' counts.
/// </summary>
[NonParallelizable]
public class RankingsPageTest
{
    private long aliceId;
    private long bobId;
    private long dedupId;
    private long decayId;

    /// <summary>How many distinct ranked maps the decay player set a play on (deliberately > 10).</summary>
    private const int decay_plays = 12;

    /// <summary>Every one of those plays is worth the same, so the total is purely the decay series.</summary>
    private const double decay_play_pp = 100;

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

        long rankedSet = await insertSetAsync(conn, "Rankings Ranked Set", "ranked");
        long mapA = await insertBeatmapAsync(conn, rankedSet);
        long mapB = await insertBeatmapAsync(conn, rankedSet);

        long pendingSet = await insertSetAsync(conn, "Rankings Pending Set", "pending");
        long mapC = await insertBeatmapAsync(conn, pendingSet);

        // alice: best-per-map folds 300k (not 300k+100k) on A, plus 200k on B = 500k over 2 maps.
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
        // total_score would pick the wrong row and total 50 + 30 x 0.85 instead.
        long dedupSet = await insertSetAsync(conn, "Rankings Dedup Set", "ranked");
        long dedupMapX = await insertBeatmapAsync(conn, dedupSet);
        long dedupMapY = await insertBeatmapAsync(conn, dedupSet);
        await insertScoreAsync(conn, dedupId, dedupMapX, 900_000, pp: 50);
        await insertScoreAsync(conn, dedupId, dedupMapX, 100_000, pp: 120);
        await insertScoreAsync(conn, dedupId, dedupMapY, 500_000, pp: 30);

        // Decay: 12 equal plays on 12 distinct ranked maps. More than ten, so this also proves
        // there is no hard top-10 truncation.
        long decaySet = await insertSetAsync(conn, "Rankings Decay Set", "ranked");

        for (int i = 0; i < decay_plays; i++)
        {
            long map = await insertBeatmapAsync(conn, decaySet);
            await insertScoreAsync(conn, decayId, map, 250_000, pp: decay_play_pp);
        }
    }

    /// <summary>Σ pp·decay^i over the seeded equal-value plays: 100 · (1 − 0.85^12) / 0.15.</summary>
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
            // 120 (map X's best pp, though not its best score) + 30 x 0.85 (map Y).
            Assert.That(pp.TotalPp, Is.EqualTo(120 + 30 * PerformancePoints.DECAY).Within(1e-9));
            Assert.That(pp.PpPlayCount, Is.EqualTo(2), "three plays across two maps fold to two");
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
    public async Task Rankings_BothTabsAreLinkedFromEitherBoard()
    {
        using var performance = await WebsiteFixture.Client.GetAsync("/rankings");
        using var score = await WebsiteFixture.Client.GetAsync("/rankings?board=score");

        string performanceHtml = await performance.Content.ReadAsStringAsync();
        string scoreHtml = await score.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            foreach (string html in new[] { performanceHtml, scoreHtml })
            {
                Assert.That(html, Does.Contain("href=\"/rankings\""));
                Assert.That(html, Does.Contain("href=\"/rankings?board=score\""));
            }

            // Exactly one tab is active per board, and it is the right one.
            Assert.That(performanceHtml, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings\">Performance</a>"));
            Assert.That(scoreHtml, Does.Contain("<a class=\"lb-tab is-active\" href=\"/rankings?board=score\">Score</a>"));
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

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES (@setId, 'type!beat', @checksum, 90, 80, 3.0, 'map.osu', 100, 500, 75)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

    private static async Task insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double pp = 0, bool ranked = true, bool passed = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp, pp_version)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.97, 50, 'A', @passed, @ranked,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":103}'::jsonb, @pp, @ppVersion)
            """,
            new { userId, beatmapId, totalScore, passed, ranked, pp, ppVersion = PerformancePoints.VERSION });
}
