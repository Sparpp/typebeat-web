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

    // ---- helpers ----

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
        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();

        var csrfMatch = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(csrfMatch.Success, Is.True, "the /play page must publish an antiforgery token");
        string csrf = csrfMatch.Groups[1].Value;

        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/play/token"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(new { setId = PublicSiteSeed.CoveredSetId }), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play token");
            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/play/submit"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                token = tokenId,
                passed = true,
                totalScore = 100_000,
                maxCombo = 10,
                statistics = new Dictionary<string, int> { ["great"] = 10 },
                maximumStatistics = new Dictionary<string, int> { ["great"] = 10 },
            }), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play submit");
        }
    }
}
