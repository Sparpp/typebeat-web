using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Play history (task 66) end to end: the monthly rollup both submission paths write, the windowed
/// read that fills the gaps, and the Play History section the profile renders from it.
///
/// <para>
/// The invariant every test here defends is that the chart and the stats card agree: a play that
/// moves <c>user_stats.play_count</c> moves its month, and a play that does not move neither.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class PlayHistoryTest
{
    private const string player_username = "history player";
    private const string player_password = "historypass-123456";

    private static long chartedId;   // rollup rows seeded directly: the rendering cases
    private static long silentId;    // never played: the section must not render at all
    private static long windowedId;  // 30 months of history: the 24-month window
    private static long playerId;    // the one that actually submits scores

    private static string bearer = null!;
    private static long beatmapId;
    private static string checksum = null!;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        chartedId = await insertUserAsync(conn, "history charted");
        silentId = await insertUserAsync(conn, "history silent");
        windowedId = await insertUserAsync(conn, "history windowed");

        playerId = await WebsiteFixture.SeedUserAsync(player_username, "history.player@example.com", player_password, verified: true);
        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(playerId)).AccessToken;

        // Submissions land on the seeded playable set (ranked, packaged, one map.osu difficulty),
        // so no new beatmapset is introduced into the listing/paging fixtures.
        (beatmapId, checksum) = await conn.QuerySingleAsync<(long, string)>(
            """
            SELECT id, checksum_md5 FROM beatmaps
            WHERE set_id = @setId AND filename LIKE '%.osu'
            ORDER BY id LIMIT 1
            """,
            new { setId = PublicSiteSeed.CoveredSetId });

        // The rendering fixture: three months of plays with a deliberate hole in the middle, and a
        // clear peak. Written straight into the rollup because the BACKFILL is 024's job (tested
        // in Migration024PlayHistoryTest) and the read path is what this asserts.
        await seedMonthsAsync(conn, chartedId, ("2026-03-01", 4), ("2026-05-01", 12), ("2026-06-01", 7));

        // Thirty consecutive months, so the window has something to cut.
        for (int i = 0; i < 30; i++)
        {
            var month = new DateTime(2024, 1, 1).AddMonths(i);
            await seedMonthsAsync(conn, windowedId, (month.ToString("yyyy-MM-dd"), i + 1));
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- rendering ----

    [Test]
    public async Task Profile_RendersTheChart_WithABarPerMonthIncludingTheEmptyOnes()
    {
        string html = await getHtmlAsync($"/users/{chartedId}");

        string section = sectionOf(html);

        // March through the current month, inclusive: every month in between is a slot, whether or
        // not it has a row, which is what makes the x axis real month boundaries.
        int expectedMonths = Math.Min(monthsBetween(new DateTime(2026, 3, 1), firstOfThisMonth()), PlayHistory.WindowMonths);

        int columns = Regex.Matches(section, "class=\"bar-chart__bar").Count;
        int gaps = Regex.Matches(section, "class=\"bar-chart__zero\"").Count;

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(">Play history<"));
            Assert.That(section, Does.Contain("role=\"img\" aria-label=\"Plays per month\""));
            Assert.That(columns + gaps, Is.EqualTo(expectedMonths), "one slot per month in the window");
            Assert.That(columns, Is.EqualTo(3), "only the three months with plays draw a column");
            Assert.That(gaps, Is.EqualTo(expectedMonths - 3), "every other month is a zero-height gap");

            // The peak is the one directly-labelled value, and it wears the peak class.
            Assert.That(section, Does.Contain("bar-chart__peak"));
            Assert.That(section, Does.Contain("class=\"bar-chart__bar is-peak\""));
            Assert.That(section, Does.Contain(">12</text>"));

            // Hover text and the table view carry the exact numbers for every month.
            Assert.That(section, Does.Contain("<title>May 2026: 12 plays</title>"));
            Assert.That(section, Does.Contain("<title>April 2026: 0 plays</title>"));
            Assert.That(section, Does.Contain("<title>March 2026: 4 plays</title>"));
            Assert.That(section, Does.Contain("<th scope=\"row\">June 2026</th>"));

            // Caption: the window, and a total that matches the seeded rows.
            Assert.That(section, Does.Contain("Mar 2026 to"));
            Assert.That(section, Does.Contain("23 plays"));

            // Server-rendered only: no script and no external asset rides along with the chart.
            Assert.That(section, Does.Not.Contain("<script"));
            Assert.That(section, Does.Not.Contain("http"));
        });
    }

    [Test]
    public async Task Profile_WithNoRecordedMonths_RendersNoSectionAtAll()
    {
        string html = await getHtmlAsync($"/users/{silentId}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Most played"), "the page itself rendered");
            Assert.That(html, Does.Not.Contain(">Play history<"));
            Assert.That(html, Does.Not.Contain("bar-chart"));
        });
    }

    [Test]
    public async Task TheWindow_IsTheLast24Months_RunningUpToTheCurrentMonth()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var pinned = new DateOnly(2026, 8, 15);
        var months = await PlayHistory.ForUserAsync(conn, windowedId, today: pinned);

        Assert.Multiple(() =>
        {
            Assert.That(months, Has.Count.EqualTo(PlayHistory.WindowMonths));
            Assert.That(months[0].Month, Is.EqualTo(new DateOnly(2024, 9, 1)), "24 months back from August 2026");
            Assert.That(months[^1].Month, Is.EqualTo(new DateOnly(2026, 8, 1)), "always up to the current month");

            // The run is contiguous, and it runs past the last month with a row (June 2026 is the
            // 30th seeded month) into the silent months that follow.
            for (int i = 1; i < months.Count; i++)
                Assert.That(months[i].Month, Is.EqualTo(months[i - 1].Month.AddMonths(1)));

            Assert.That(months[^1].Plays, Is.Zero, "an idle month reads as idle, not as the last play");
        });
    }

    [Test]
    public async Task TheWindow_StopsAtTheFirstPlay_ForAShorterHistory()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var months = await PlayHistory.ForUserAsync(conn, chartedId, today: new DateOnly(2026, 6, 20));

        Assert.Multiple(() =>
        {
            Assert.That(months.Select(m => m.Month.ToString("yyyy-MM")), Is.EqualTo(new[]
            {
                "2026-03", "2026-04", "2026-05", "2026-06",
            }));
            Assert.That(months.Select(m => m.Plays), Is.EqualTo(new long[] { 4, 0, 12, 7 }));
        });

        Assert.That(await PlayHistory.ForUserAsync(conn, silentId), Is.Empty);
    }

    // ---- recording ----

    [Test]
    public async Task GameClientSubmission_CreditsTheCurrentMonth_AndTracksThePlayCountStat()
    {
        var before = await snapshotAsync(playerId);

        await submitViaGameClientAsync(statistics: 10, maximum: 10);

        var after = await snapshotAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(after.ThisMonth, Is.EqualTo(before.ThisMonth + 1));
            Assert.That(after.PlayCount, Is.EqualTo(before.PlayCount + 1));
            Assert.That(after.Total, Is.EqualTo(after.PlayCount), "the chart's total must track the stat");
        });
    }

    [Test]
    public async Task BrowserSubmission_CreditsTheSameMonthlyRollup()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, player_username, player_password);

        var before = await snapshotAsync(playerId);

        await submitViaBrowserAsync(client);

        var after = await snapshotAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(after.ThisMonth, Is.EqualTo(before.ThisMonth + 1));
            Assert.That(after.PlayCount, Is.EqualTo(before.PlayCount + 1));
        });
    }

    [Test]
    public async Task ATamperedSubmission_CountsInNeitherTheStatNorTheMonth()
    {
        var before = await snapshotAsync(playerId);

        // More judged cells than the map has: the hard invariant that withholds every aggregate.
        // The score row is still stored (unranked), which is exactly the case 024's header calls
        // out as the one direction the backfill can disagree with play_count.
        await submitViaGameClientAsync(statistics: 20, maximum: 10);

        var after = await snapshotAsync(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(after.PlayCount, Is.EqualTo(before.PlayCount), "the stat withholds it");
            Assert.That(after.ThisMonth, Is.EqualTo(before.ThisMonth), "so the month must withhold it too");
        });
    }

    // ---- the difficulty the token keys off, and the status it is stored under (backlog 230) ----

    /// <summary>
    /// The token, and therefore the score row, keys off the difficulty the player CHOSE, not the
    /// set's primary one. "Twin Peaks Typing" exists for this: its lowest-id difficulty is the easy
    /// one, so a set-addressed token would have boarded a run of "twin hard" on "twin easy" (and
    /// gated it against the wrong drain/skippable pair, which is per beatmap).
    /// </summary>
    [Test]
    public async Task BrowserToken_BoardsTheChosenDifficulty_NotTheSetsPrimaryOne()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, player_username, player_password);

        string csrf = await browserCsrfAsync(client);
        long tokenId = await browserTokenAsync(client, csrf, PublicSiteSeed.MultiDiffSetId, PublicSiteSeed.MultiDiffHardId);
        await backdateTokenAsync(tokenId, 300);
        var submitted = await browserSubmitAsync(client, csrf, tokenId);

        await using var conn = await dataSource.OpenConnectionAsync();

        long storedBeatmapId = await conn.ExecuteScalarAsync<long>(
            "SELECT s.beatmap_id FROM scores s JOIN score_tokens t ON t.score_id = s.id WHERE t.id = @tokenId",
            new { tokenId });

        Assert.Multiple(() =>
        {
            Assert.That(storedBeatmapId, Is.EqualTo(PublicSiteSeed.MultiDiffHardId));
            Assert.That(storedBeatmapId, Is.Not.EqualTo(PublicSiteSeed.MultiDiffEasyId), "the primary (lowest-id) difficulty");
            // The ranked control for the unranked case below: same play, same path, a ranked set.
            Assert.That((bool)submitted["ranked"]!, Is.True);
        });
    }

    /// <summary>
    /// (b)'s server half, asserted rather than assumed: a browser play of an UNRANKED (published,
    /// never rankable) set is recorded and stored <c>ranked = false</c>. Paired with the ranked
    /// control above, which is the same submission over the same wall-clock with only the set's
    /// status different, so this cannot pass for the trivial reason that a fast play never ranks.
    /// </summary>
    [Test]
    public async Task BrowserSubmission_OnAnUnrankedSet_IsRecordedButStoredUnranked()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, player_username, player_password);

        var before = await snapshotAsync(playerId);

        string csrf = await browserCsrfAsync(client);
        long tokenId = await browserTokenAsync(client, csrf, PublicSiteSeed.UnrankedSetId, beatmapId: 0);
        await backdateTokenAsync(tokenId, 300);
        var submitted = await browserSubmitAsync(client, csrf, tokenId);

        var after = await snapshotAsync(playerId);

        await using var conn = await dataSource.OpenConnectionAsync();

        var stored = await conn.QuerySingleAsync<(bool Ranked, bool Passed, double Pp)>(
            """
            SELECT s.ranked, s.passed, s.pp::double precision
            FROM scores s JOIN score_tokens t ON t.score_id = s.id
            WHERE t.id = @tokenId
            """,
            new { tokenId });

        Assert.Multiple(() =>
        {
            Assert.That(stored.Ranked, Is.False, "an unranked set's play has no leaderboard to reach");
            Assert.That(stored.Passed, Is.True, "it is still a real, recorded play");
            Assert.That(stored.Pp, Is.Zero, "and it is worth no pp");
            Assert.That((bool)submitted["ranked"]!, Is.False, "which is what the player is told");
            Assert.That(after.PlayCount, Is.EqualTo(before.PlayCount + 1), "it still counts as a play");
            Assert.That(after.ThisMonth, Is.EqualTo(before.ThisMonth + 1));
        });
    }

    /// <summary>
    /// THE NEGATIVE PIN on the token's tamper bound. Once the client names a beatmap, the server
    /// must refuse one that does not belong to the set it is claiming to play, and one that is not a
    /// live .osu difficulty at all. Drop the <c>b.set_id = @setId</c> clause from CreateTokenAsync
    /// and the first of these mints a token on another set's map.
    /// </summary>
    [Test]
    public async Task BrowserToken_RefusesABeatmapFromAnotherSet_OrADroppedDifficulty()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, player_username, player_password);

        string csrf = await browserCsrfAsync(client);

        using var foreign = await browserTokenResponseAsync(
            client, csrf, PublicSiteSeed.MultiDiffSetId, PublicSiteSeed.LeaderboardBeatmapId);
        using var dropped = await browserTokenResponseAsync(
            client, csrf, PublicSiteSeed.MultiDiffSetId, PublicSiteSeed.MultiDiffDroppedId);
        using var unknown = await browserTokenResponseAsync(
            client, csrf, PublicSiteSeed.MultiDiffSetId, 987_654_321);
        using var legitimate = await browserTokenResponseAsync(
            client, csrf, PublicSiteSeed.MultiDiffSetId, PublicSiteSeed.MultiDiffEasyId);

        Assert.Multiple(() =>
        {
            Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "another set's difficulty");
            Assert.That(dropped.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "a dropped (filename NULL) difficulty");
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "not a difficulty at all");
            Assert.That(legitimate.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the control: a live difficulty of that set");
        });
    }

    // ---- what /play/submit answers (backlog 321) ----
    //
    // Each case signs in a DEDICATED user: EmailCodeService caps login codes per (user, purpose) per
    // hour, and the shared history player is already at that budget. A fresh user also means "is
    // this the player's best" starts from nothing.

    /// <summary>
    /// A ranked, PRICED browser play reports its pp exactly as the formula returned it, and its
    /// board position is the player's BEST row's, with personal_best saying whether that row is this
    /// play. A rival sits between the best and the worse run, so a regression that reported the
    /// worse run's own placement would read one place (or more) lower and fail.
    /// </summary>
    [Test]
    public async Task BrowserSubmitResponse_RankedPricedMap_CarriesPp_AndTheBestRowsPosition()
    {
        long me = await WebsiteFixture.SeedUserAsync("submit priced", "submit.priced@example.com", "submitpriced-123456", verified: true);
        long hardId = PublicSiteSeed.MultiDiffHardId;

        await using var conn = await dataSource.OpenConnectionAsync();

        long rival = await insertUserAsync(conn, "submit priced rival");
        await insertBoardScoreAsync(conn, rival, hardId, 80_000, ranked: true);

        await conn.ExecuteAsync("UPDATE beatmaps SET ratings = @ratings::jsonb WHERE id = @hardId",
            new { hardId, ratings = TestRatings.Json(2.0) });

        try
        {
            var (client, _) = WebsiteFixture.CreateBrowser();
            await WebsiteFixture.LoginAndVerifyAsync(client, "submit priced", "submitpriced-123456");
            string csrf = await browserCsrfAsync(client);

            var (best, bestId) = await browserPlayAsync(client, csrf, PublicSiteSeed.MultiDiffSetId, hardId, totalScore: 90_000);
            var (worse, worseId) = await browserPlayAsync(client, csrf, PublicSiteSeed.MultiDiffSetId, hardId, totalScore: 50_000);
            var (better, betterId) = await browserPlayAsync(client, csrf, PublicSiteSeed.MultiDiffSetId, hardId, totalScore: 95_000);

            double expectedPp = PerformancePoints.Compute(2.0, 10, TestRatings.DEFAULT_DIFFICULT_CHARACTERS, 0, 1.0, 10, []);

            int bestPosition = await boardPositionAsync(conn, hardId, me, 90_000, bestId, ranked: true);
            int worseOwnPlacement = await boardPositionAsync(conn, hardId, me, 50_000, worseId, ranked: true);
            int betterPosition = await boardPositionAsync(conn, hardId, me, 95_000, betterId, ranked: true);

            Assert.Multiple(() =>
            {
                Assert.That((bool)best["ranked"]!, Is.True);
                Assert.That((double)best["pp"]!, Is.EqualTo(expectedPp).Within(1e-9), "the price the formula returned");
                Assert.That(expectedPp, Is.GreaterThan(0), "a priced play, not a placeholder");
                Assert.That((bool)best["pp_pending"]!, Is.False);
                Assert.That((string?)best["board"], Is.EqualTo("ranked"));
                Assert.That((bool)best["personal_best"]!, Is.True);
                Assert.That((int)best["position"]!, Is.EqualTo(bestPosition));

                Assert.That((bool)worse["personal_best"]!, Is.False, "50k did not beat the 90k best");
                Assert.That((int)worse["position"]!, Is.EqualTo(bestPosition), "the BEST row's position, still");
                Assert.That(worseOwnPlacement, Is.GreaterThan(bestPosition), "non-vacuity: the rival sits between the two runs");

                Assert.That((bool)better["personal_best"]!, Is.True, "95k is the new best");
                Assert.That((int)better["position"]!, Is.EqualTo(betterPosition));
            });
        }
        finally
        {
            await conn.ExecuteAsync("UPDATE beatmaps SET ratings = NULL WHERE id = @hardId", new { hardId });
        }
    }

    /// <summary>
    /// A ranked play on a map whose rating cell is not stored yet is PENDING: pp null with
    /// pp_pending true, never a confident 0 (the stored column's 0 is a placeholder PpBackfill
    /// overwrites).
    /// </summary>
    [Test]
    public async Task BrowserSubmitResponse_RankedUnpricedMap_IsPpPending()
    {
        await WebsiteFixture.SeedUserAsync("submit pending", "submit.pending@example.com", "submitpending-123456", verified: true);

        await using var conn = await dataSource.OpenConnectionAsync();
        Assert.That(await conn.ExecuteScalarAsync<bool>("SELECT ratings IS NULL FROM beatmaps WHERE id = @beatmapId", new { beatmapId }),
            Is.True, "precondition: the covered set's map carries no rating matrix");

        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, "submit pending", "submitpending-123456");
        string csrf = await browserCsrfAsync(client);

        var (played, _) = await browserPlayAsync(client, csrf, PublicSiteSeed.CoveredSetId, beatmapId, totalScore: 100_000);

        Assert.Multiple(() =>
        {
            Assert.That((bool)played["ranked"]!, Is.True);
            Assert.That(played["pp"]!.Type, Is.EqualTo(JTokenType.Null), "no price yet, and not a 0");
            Assert.That((bool)played["pp_pending"]!, Is.True);
            Assert.That((string?)played["board"], Is.EqualTo("ranked"));
            Assert.That((bool)played["personal_best"]!, Is.True);
        });
    }

    /// <summary>
    /// A play of an UNRANKED set lands on that map's unranked board (the one the game API serves
    /// for pending and unranked sets) and is positioned there, against the unranked rows only. Its
    /// pp is a refused null (a dash), not pending. A failed run is on no board and gets no position.
    /// </summary>
    [Test]
    public async Task BrowserSubmitResponse_UnrankedSet_IsPlacedOnTheUnrankedBoard()
    {
        long me = await WebsiteFixture.SeedUserAsync("submit unranked", "submit.unranked@example.com", "submitunranked-123456", verified: true);

        await using var conn = await dataSource.OpenConnectionAsync();

        long unrankedMap = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM beatmaps WHERE set_id = @setId AND filename LIKE '%.osu' ORDER BY id LIMIT 1",
            new { setId = PublicSiteSeed.UnrankedSetId });

        long rival = await insertUserAsync(conn, "submit unranked rival");
        await insertBoardScoreAsync(conn, rival, unrankedMap, 50_000, ranked: false);
        // RANKED-flag rows on the same map, outscoring everything: they belong to the other board
        // and must not move this play (the boards are counted separately). Three of them, so a
        // position counted against the wrong board cannot coincide with the right one however many
        // unranked rows earlier tests left above this play.
        for (int i = 0; i < 3; i++)
            await insertBoardScoreAsync(conn, await insertUserAsync(conn, $"submit unranked ghost {i}"), unrankedMap, 999_999, ranked: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, "submit unranked", "submitunranked-123456");
        string csrf = await browserCsrfAsync(client);

        var (first, firstId) = await browserPlayAsync(client, csrf, PublicSiteSeed.UnrankedSetId, unrankedMap, totalScore: 60_000);
        var (lower, _) = await browserPlayAsync(client, csrf, PublicSiteSeed.UnrankedSetId, unrankedMap, totalScore: 40_000);
        var (failed, _) = await browserPlayAsync(client, csrf, PublicSiteSeed.UnrankedSetId, unrankedMap, totalScore: 70_000, passed: false);

        int firstPosition = await boardPositionAsync(conn, unrankedMap, me, 60_000, firstId, ranked: false);
        int wrongBoardPosition = await boardPositionAsync(conn, unrankedMap, me, 60_000, firstId, ranked: true);

        Assert.Multiple(() =>
        {
            Assert.That((bool)first["ranked"]!, Is.False);
            Assert.That(first["pp"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That((bool)first["pp_pending"]!, Is.False, "refused, not pending: the player sees a dash");
            Assert.That((string?)first["board"], Is.EqualTo("unranked"));
            Assert.That((bool)first["personal_best"]!, Is.True);
            Assert.That((int)first["position"]!, Is.EqualTo(firstPosition));
            Assert.That(wrongBoardPosition, Is.Not.EqualTo(firstPosition), "non-vacuity: the ranked-flag rows would move it");

            Assert.That((string?)lower["board"], Is.EqualTo("unranked"));
            Assert.That((bool)lower["personal_best"]!, Is.False);
            Assert.That((int)lower["position"]!, Is.EqualTo(firstPosition), "the best row's position");

            Assert.That(failed["board"]!.Type, Is.EqualTo(JTokenType.Null), "a fail is on no board");
            Assert.That(failed["position"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That((bool)failed["personal_best"]!, Is.False);
        });
    }

    // ---- helpers ----

    /// <summary>A token for the named difficulty, backdated past the play-time gate, then submitted.
    /// Returns the response and the stored score id.</summary>
    private static async Task<(JObject Response, long ScoreId)> browserPlayAsync(
        HttpClient client, string csrf, long setId, long diffId, long totalScore, bool passed = true)
    {
        long tokenId = await browserTokenAsync(client, csrf, setId, diffId);
        await backdateTokenAsync(tokenId, 300);
        var response = await browserSubmitAsync(client, csrf, tokenId, totalScore, passed);

        await using var conn = await dataSource.OpenConnectionAsync();
        long scoreId = await conn.ExecuteScalarAsync<long>("SELECT score_id FROM score_tokens WHERE id = @tokenId", new { tokenId });
        return (response, scoreId);
    }

    /// <summary>A passed score row written straight in, for a rival on a board.</summary>
    private static Task insertBoardScoreAsync(NpgsqlConnection conn, long userId, long mapId, long totalScore, bool ranked)
        => conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @mapId, @totalScore, 1.0, 1.0, 10, 'X', true, @ranked,
                 '[]'::jsonb, '{"great":10}'::jsonb, '{"great":10}'::jsonb)
            """,
            new { userId, mapId, totalScore, ranked });

    /// <summary>
    /// The ORACLE for a board position, written out by hand rather than through BeatmapLeaderboard
    /// so it cannot share a bug with the endpoint: 1 + the other players whose best passed row on
    /// that board (flag = <paramref name="ranked"/>) beats (total, id), higher total first and the
    /// earlier id winning a tie.
    /// </summary>
    private static Task<int> boardPositionAsync(NpgsqlConnection conn, long mapId, long me, long total, long scoreId, bool ranked)
        => conn.ExecuteScalarAsync<int>(
            """
            SELECT 1 + COUNT(*)
            FROM (
                SELECT DISTINCT ON (user_id) user_id, total_score, id
                FROM scores
                WHERE beatmap_id = @mapId AND passed AND ranked = @ranked
                ORDER BY user_id, total_score DESC, id ASC
            ) b
            WHERE b.user_id <> @me
              AND (b.total_score > @total OR (b.total_score = @total AND b.id < @scoreId))
            """,
            new { mapId, me, total, scoreId, ranked });

    private static DateTime firstOfThisMonth()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1);
    }

    private static int monthsBetween(DateTime from, DateTime to)
        => (((to.Year - from.Year) * 12) + to.Month - from.Month) + 1;

    /// <summary>The Play history section's markup, sliced out so assertions cannot match the rest of the page.</summary>
    private static string sectionOf(string html)
    {
        int start = html.IndexOf("id=\"play-history\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "the profile must render a play-history section");

        int end = html.IndexOf(">Maps<", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static async Task<string> getHtmlAsync(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, replace(@username, ' ', '.') || '@example.com', 'x', 'US')
            RETURNING id
            """,
            new { username });

    private static async Task seedMonthsAsync(NpgsqlConnection conn, long userId, params (string Month, int Plays)[] months)
    {
        foreach (var (month, plays) in months)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO user_month_playcounts (user_id, month, plays)
                VALUES (@userId, @month::date, @plays)
                ON CONFLICT (user_id, month) DO UPDATE SET plays = excluded.plays
                """,
                new { userId, month, plays });
        }
    }

    /// <summary>play_count, the user's total across every month, and the current UTC month's count.</summary>
    private static async Task<(int PlayCount, long Total, long ThisMonth)> snapshotAsync(long userId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        return await conn.QuerySingleAsync<(int, long, long)>(
            """
            SELECT COALESCE((SELECT play_count FROM user_stats WHERE user_id = @userId), 0),
                   COALESCE((SELECT sum(plays)::bigint FROM user_month_playcounts WHERE user_id = @userId), 0::bigint),
                   COALESCE((SELECT plays::bigint FROM user_month_playcounts
                             WHERE user_id = @userId
                               AND month = date_trunc('month', now() AT TIME ZONE 'UTC')::date), 0::bigint)
            """,
            new { userId });
    }

    /// <summary>The bearer two-phase submission (Endpoints/ScoreEndpoints.cs).</summary>
    private static async Task submitViaGameClientAsync(int statistics, int maximum)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "play-history-test-build",
                ["beatmap_hash"] = checksum,
                ["ruleset_id"] = "0",
            });

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score token");
            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                passed = true,
                total_score = 100_000,
                total_score_without_mods = 100_000,
                accuracy = 1.0,
                max_combo = statistics,
                ruleset_id = 0,
                rank = "X",
                statistics = new Dictionary<string, int> { ["great"] = statistics },
                maximum_statistics = new Dictionary<string, int> { ["great"] = maximum },
                mods = Array.Empty<object>(),
            }), Encoding.UTF8, "application/json");

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score submit");
        }
    }

    /// <summary>The in-browser player's session + antiforgery submission (Endpoints/PlayEndpoints.cs).</summary>
    private static async Task submitViaBrowserAsync(HttpClient client)
    {
        string csrf = await browserCsrfAsync(client);
        long tokenId = await browserTokenAsync(client, csrf, PublicSiteSeed.CoveredSetId, beatmapId: 0);
        await browserSubmitAsync(client, csrf, tokenId);
    }

    private static async Task<string> browserCsrfAsync(HttpClient client)
    {
        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();

        var csrfMatch = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(csrfMatch.Success, Is.True, "the /play page must publish an antiforgery token");
        return csrfMatch.Groups[1].Value;
    }

    /// <summary>POST /play/token with the body the player sends: both ids, so the server can bind
    /// the named difficulty to the set. Returns the response, un-asserted, so negative cases can
    /// read the status too.</summary>
    private static async Task<HttpResponseMessage> browserTokenResponseAsync(HttpClient client, string csrf, long setId, long beatmapId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/play/token");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { setId, beatmapId }), Encoding.UTF8, "application/json");

        return await client.SendAsync(request);
    }

    private static async Task<long> browserTokenAsync(HttpClient client, string csrf, long setId, long beatmapId)
    {
        using var response = await browserTokenResponseAsync(client, csrf, setId, beatmapId);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play token");
        return (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
    }

    private static async Task<JObject> browserSubmitAsync(HttpClient client, string csrf, long tokenId, long totalScore = 100_000, bool passed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/play/submit");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = new StringContent(JsonConvert.SerializeObject(new
        {
            token = tokenId,
            passed,
            totalScore,
            maxCombo = 10,
            statistics = new Dictionary<string, int> { ["great"] = 10 },
            maximumStatistics = new Dictionary<string, int> { ["great"] = 10 },
        }), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play submit");
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Backdate a token's wall-clock anchor so the play-time gate sees a play of that length, the
    /// same manoeuvre SkipGateTest makes for the bearer path. Without it every browser submission
    /// here is unranked for the trivial reason that it took no time, which would make the
    /// status-driven pin below vacuous.
    /// </summary>
    private static async Task backdateTokenAsync(long tokenId, double seconds)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        await conn.ExecuteAsync(
            "UPDATE score_tokens SET created_at = now() - make_interval(secs => @seconds) WHERE id = @tokenId",
            new { tokenId, seconds });
    }
}
