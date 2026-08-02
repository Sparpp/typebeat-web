using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Replay views (task 65) end to end: the counting rule the replay download endpoint applies, and
/// the three profile surfaces built on it (the stats-card total, the "Most viewed replays" list and
/// the views-per-month chart).
///
/// <para>
/// The counted-view definition is the thing under test, clause by clause (025_replay_views.sql):
/// only a real serve counts, never the owner's own, never an anonymous one, and at most once per
/// (score, viewer) per UTC day. The recording tests run in <see cref="OrderAttribute"/> order
/// because each one leaves the counter where the next one expects it.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReplayViewsTest
{
    private const string session_username = "views session watcher";
    private const string session_password = "sessionwatch-123456";

    private static long ownerId;
    private static long watcherId;        // bearer watcher, the main subject
    private static long otherWatcherId;   // a second bearer watcher (dedup is per viewer)
    private static long sessionWatcherId; // signs in on the website, cookie identity
    private static long chartedId;        // rollup rows seeded directly: the chart rendering
    private static long unwatchedId;      // has a replay nobody watched: the hidden-section case

    private static string ownerBearer = null!;
    private static string watcherBearer = null!;
    private static string otherWatcherBearer = null!;

    private static long topScoreId;      // the owner's most-watched score
    private static long secondScoreId;   // a second watched score, for the list's ordering
    private static long noReplayScoreId; // stored nothing: a 404 serve
    private static long unwatchedScoreId;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        ownerId = await insertUserAsync(conn, "views owner");
        watcherId = await insertUserAsync(conn, "views watcher");
        otherWatcherId = await insertUserAsync(conn, "views other watcher");
        chartedId = await insertUserAsync(conn, "views charted");
        unwatchedId = await insertUserAsync(conn, "views unwatched");

        sessionWatcherId = await WebsiteFixture.SeedUserAsync(
            session_username, "views.session@example.com", session_password, verified: true);

        var tokens = new TokenService(new Db(dataSource));
        ownerBearer = (await tokens.IssueAsync(ownerId)).AccessToken;
        watcherBearer = (await tokens.IssueAsync(watcherId)).AccessToken;
        otherWatcherBearer = (await tokens.IssueAsync(otherWatcherId)).AccessToken;

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@mapperId, 'Watched Anthem', 'The Observed', 'ranked', now() - interval '4 days', now() - interval '4 days')
            RETURNING id
            """,
            new { mapperId = PublicSiteSeed.MapperId });

        long beatmapId = await insertBeatmapAsync(conn, setId, "type!beat");
        long secondBeatmapId = await insertBeatmapAsync(conn, setId, "encore");

        topScoreId = await insertScoreAsync(conn, ownerId, beatmapId, 900_000);
        secondScoreId = await insertScoreAsync(conn, ownerId, secondBeatmapId, 700_000);
        noReplayScoreId = await insertScoreAsync(conn, ownerId, beatmapId, 400_000);
        unwatchedScoreId = await insertScoreAsync(conn, unwatchedId, beatmapId, 500_000);

        await uploadReplayAsync(topScoreId, ownerBearer);
        await uploadReplayAsync(secondScoreId, ownerBearer);

        // The unwatched user's replay exists and is downloadable; nobody ever fetches it, so their
        // profile must show the zero state everywhere.
        await uploadReplayAsync(unwatchedScoreId, (await tokens.IssueAsync(unwatchedId)).AccessToken);

        // The chart fixture: three months with a deliberate hole, written straight into the rollup
        // (the recording path is asserted above; this is about rendering a window).
        await seedMonthsAsync(conn, chartedId, ("2026-03-01", 4), ("2026-05-01", 12), ("2026-06-01", 7));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- what does NOT count ----

    [Test]
    [Order(1)]
    public async Task TheOwnerWatchingTheirOwnReplay_CountsNothing()
    {
        using var response = await downloadAsync(topScoreId, ownerBearer);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the owner still gets their replay");

        int views = await scoreViewsAsync(topScoreId);
        int ledgerRows = await ledgerRowsAsync(topScoreId);
        long total = await totalAsync(ownerId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.Zero, "'watched by OTHERS' excludes the owner");
            Assert.That(ledgerRows, Is.Zero, "and leaves no ledger row to dedup against");
            Assert.That(total, Is.Zero);
        });
    }

    [Test]
    [Order(2)]
    public async Task AnAnonymousDownload_CountsNothing()
    {
        // The shared client carries neither a bearer nor a session cookie, exactly like a
        // signed-out visitor clicking "replay" on a set page.
        using var response = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{topScoreId}/replay");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "signed-out watching keeps working");

        int views = await scoreViewsAsync(topScoreId);
        long total = await totalAsync(ownerId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.Zero, "there is no honest per-person key for it");
            Assert.That(total, Is.Zero);
        });
    }

    [Test]
    [Order(3)]
    public async Task ARequestThatServesNothing_CountsNothing()
    {
        using var response = await downloadAsync(noReplayScoreId, watcherBearer);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no replay was ever uploaded for it");

        int views = await scoreViewsAsync(noReplayScoreId);
        int ledgerRows = await ledgerRowsAsync(noReplayScoreId);
        long total = await totalAsync(ownerId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.Zero, "a 404 is not a watch");
            Assert.That(ledgerRows, Is.Zero);
            Assert.That(total, Is.Zero);
        });
    }

    // ---- what does count ----

    [Test]
    [Order(4)]
    public async Task ASignedInStranger_CountsOnce_AndLandsInTheOwnersMonth()
    {
        using (var response = await downloadAsync(topScoreId, watcherBearer))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var ledger = await ledgerAsync(topScoreId, watcherId);
        int views = await scoreViewsAsync(topScoreId);
        long ownerTotal = await totalAsync(ownerId);
        long watcherTotal = await totalAsync(watcherId);
        long month = await monthViewsAsync(ownerId, currentMonth());

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.EqualTo(1));
            Assert.That(ownerTotal, Is.EqualTo(1), "credited to the score's owner, not the viewer");
            Assert.That(watcherTotal, Is.Zero, "watching somebody else's replay is not being watched");
            Assert.That(month, Is.EqualTo(1));
            Assert.That(ledger, Has.Count.EqualTo(1));
            Assert.That(ledger[0], Is.EqualTo(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                "the ledger bucket is the UTC day");
        });
    }

    [Test]
    [Order(5)]
    public async Task TheSameWatcherAgainTheSameDay_CountsNothing()
    {
        // Two more serves: a refreshed browser tab, and a client retrying its import.
        using (var again = await downloadAsync(topScoreId, watcherBearer))
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK), "they still get the replay");

        using (var third = await downloadAsync(topScoreId, watcherBearer))
            Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int views = await scoreViewsAsync(topScoreId);
        long total = await totalAsync(ownerId);
        int ledgerRows = await ledgerRowsAsync(topScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.EqualTo(1), "refresh-spam cannot inflate a view count");
            Assert.That(total, Is.EqualTo(1));
            Assert.That(ledgerRows, Is.EqualTo(1), "one row per viewer per day");
        });
    }

    [Test]
    [Order(6)]
    public async Task TheSameWatcherOnTheNextDay_CountsAgain()
    {
        // Age the existing claim by a day: to the endpoint this is indistinguishable from the
        // watcher coming back tomorrow, which is exactly the case the day bucket exists to allow.
        await backdateLedgerAsync(topScoreId, watcherId, days: 1);

        using (var response = await downloadAsync(topScoreId, watcherBearer))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int views = await scoreViewsAsync(topScoreId);
        long total = await totalAsync(ownerId);
        int ledgerRows = await ledgerRowsAsync(topScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.EqualTo(2));
            Assert.That(total, Is.EqualTo(2));
            Assert.That(ledgerRows, Is.EqualTo(2), "yesterday's claim and today's");
        });
    }

    [Test]
    [Order(7)]
    public async Task ADifferentWatcher_CountsSeparately_OnTheSameDay()
    {
        using (var response = await downloadAsync(topScoreId, otherWatcherBearer))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int views = await scoreViewsAsync(topScoreId);
        long total = await totalAsync(ownerId);
        long month = await monthViewsAsync(ownerId, currentMonth());

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.EqualTo(3), "the dedup is per viewer, not per score");
            Assert.That(total, Is.EqualTo(3));
            Assert.That(month, Is.EqualTo(3));
        });
    }

    [Test]
    [Order(8)]
    public async Task AWebsiteSessionWatcher_IsIdentifiedByTheirCookie()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, session_username, session_password);

        using (var response = await client.GetAsync($"/api/v2/scores/{topScoreId}/replay"))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int views = await scoreViewsAsync(topScoreId);
        var ledger = await ledgerAsync(topScoreId, sessionWatcherId);

        Assert.Multiple(() =>
        {
            Assert.That(views, Is.EqualTo(4), "the site's own download link counts too");
            Assert.That(ledger, Has.Count.EqualTo(1));
        });
    }

    // ---- the profile surfaces ----

    [Test]
    [Order(9)]
    public async Task TheStatsCard_ShowsTheTotal_AndTheListRanksTheScores()
    {
        // A second watched score, one view behind, so the list has an order to get right.
        using (var response = await downloadAsync(secondScoreId, watcherBearer))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string html = await getHtmlAsync($"/users/{ownerId}");
        string section = sectionOf(html, "id=\"most-viewed-replays\"");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Replays watched by others"));
            Assert.That(statValue(html, "Replays watched by others"), Is.EqualTo("5"), "4 on one score, 1 on the other");

            Assert.That(section, Does.Contain(">Most viewed replays<"));
            Assert.That(section, Does.Contain("4 views"));
            Assert.That(section, Does.Contain("1 view<"), "one view is a view, not '1 views'");

            // Most watched first, and every row links its own replay (they all have one).
            Assert.That(section.IndexOf("4 views", StringComparison.Ordinal),
                Is.LessThan(section.IndexOf("1 view<", StringComparison.Ordinal)),
                "the list is ordered by views, descending");
            Assert.That(section, Does.Contain($"/api/v2/scores/{topScoreId}/replay"));
            Assert.That(section, Does.Contain($"/api/v2/scores/{secondScoreId}/replay"));
        });
    }

    [Test]
    [Order(10)]
    public async Task TheChart_BucketsTheViewsByMonth()
    {
        string html = await getHtmlAsync($"/users/{ownerId}");
        string section = sectionOf(html, "id=\"replay-views\"");

        // Every view above landed today, so this month carries all five.
        string thisMonth = DateTime.UtcNow.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain(">Replay views<"));
            Assert.That(section, Does.Contain("role=\"img\" aria-label=\"Replay views per month\""));
            Assert.That(section, Does.Contain($"<title>{thisMonth}: 5 views</title>"));
            Assert.That(section, Does.Contain("5 views"), "the caption carries the window total");
        });
    }

    [Test]
    [Order(11)]
    public async Task ASeededHistory_RendersOneSlotPerMonthIncludingTheEmptyOnes()
    {
        string html = await getHtmlAsync($"/users/{chartedId}");
        string section = sectionOf(html, "id=\"replay-views\"");

        int expectedMonths = Math.Min(monthsBetween(new DateTime(2026, 3, 1), firstOfThisMonth()), ReplayViews.WindowMonths);

        int columns = Regex.Matches(section, "class=\"bar-chart__bar").Count;
        int gaps = Regex.Matches(section, "class=\"bar-chart__zero\"").Count;

        Assert.Multiple(() =>
        {
            Assert.That(columns + gaps, Is.EqualTo(expectedMonths), "one slot per month in the window");
            Assert.That(columns, Is.EqualTo(3), "only the three months with views draw a column");

            Assert.That(section, Does.Contain("<title>May 2026: 12 views</title>"));
            Assert.That(section, Does.Contain("<title>April 2026: 0 views</title>"));
            Assert.That(section, Does.Contain("Mar 2026 to"));
            Assert.That(section, Does.Contain("23 views"));

            // The rollup alone drives the chart and the stat; no score of theirs has been watched,
            // so the LIST stays hidden even though the chart is there.
            Assert.That(statValue(html, "Replays watched by others"), Is.EqualTo("23"));
            Assert.That(html, Does.Not.Contain(">Most viewed replays<"));
        });
    }

    [Test]
    [Order(12)]
    public async Task AProfileWithNoWatchedReplays_RendersNeitherSection()
    {
        string html = await getHtmlAsync($"/users/{unwatchedId}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Most played"), "the page itself rendered");
            Assert.That(html, Does.Contain($"/api/v2/scores/{unwatchedScoreId}/replay"), "they do have a replay");
            Assert.That(html, Does.Not.Contain(">Most viewed replays<"));
            Assert.That(html, Does.Not.Contain(">Replay views<"));
            Assert.That(html, Does.Not.Contain("bar-chart"));
            Assert.That(statValue(html, "Replays watched by others"), Is.EqualTo("0"));
        });
    }

    // ---- the window, directly ----

    [Test]
    [Order(13)]
    public async Task TheWindow_IsTheLast24Months_RunningUpToTheCurrentMonth()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // Thirty consecutive months, so there is something for the window to cut.
        long windowedId = await insertUserAsync(conn, "views windowed");

        for (int i = 0; i < 30; i++)
        {
            var month = new DateTime(2024, 1, 1).AddMonths(i);
            await seedMonthsAsync(conn, windowedId, (month.ToString("yyyy-MM-dd"), i + 1));
        }

        var months = await ReplayViews.ForUserAsync(conn, windowedId, today: new DateOnly(2026, 8, 15));
        var unwatched = await ReplayViews.ForUserAsync(conn, unwatchedId);

        Assert.Multiple(() =>
        {
            Assert.That(months, Has.Count.EqualTo(ReplayViews.WindowMonths));
            Assert.That(months[0].Month, Is.EqualTo(new DateOnly(2024, 9, 1)), "24 months back from August 2026");
            Assert.That(months[^1].Month, Is.EqualTo(new DateOnly(2026, 8, 1)), "always up to the current month");
            Assert.That(months[^1].Views, Is.Zero, "an unwatched month reads as unwatched, not as the last view");

            for (int i = 1; i < months.Count; i++)
                Assert.That(months[i].Month, Is.EqualTo(months[i - 1].Month.AddMonths(1)), "the run is contiguous");

            Assert.That(unwatched, Is.Empty, "never watched, no window at all");
        });
    }

    // ---- helpers ----

    private static DateTime firstOfThisMonth()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1);
    }

    private static DateOnly currentMonth()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    private static int monthsBetween(DateTime from, DateTime to)
        => (((to.Year - from.Year) * 12) + to.Month - from.Month) + 1;

    /// <summary>
    /// One profile section's markup, sliced out so assertions cannot match the rest of the page.
    /// The slice runs from the section's anchor to whichever later section heading comes first
    /// (the ones in between are hidden on profiles with nothing to put in them), searching from
    /// past this section's OWN heading so a section cannot end at itself.
    /// </summary>
    private static string sectionOf(string html, string marker)
    {
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), $"the profile must render {marker}");

        int afterHeading = html.IndexOf("</h2>", start, StringComparison.Ordinal);
        Assert.That(afterHeading, Is.GreaterThan(-1), $"{marker} must have a heading");

        int end = new[] { ">Most viewed replays<", ">Replay views<", ">Play history<", ">Maps<" }
            .Select(m => html.IndexOf(m, afterHeading, StringComparison.Ordinal))
            .Where(i => i > 0)
            .Min();

        return html[start..end];
    }

    /// <summary>The value printed in the stats card next to <paramref name="label"/>.</summary>
    private static string statValue(string html, string label)
    {
        var match = Regex.Match(html, Regex.Escape(label) + "</span><span class=\"stat-row__value\">([^<]+)</span>");
        Assert.That(match.Success, Is.True, $"no stats row labelled '{label}'");
        return match.Groups[1].Value;
    }

    private static async Task<string> getHtmlAsync(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> downloadAsync(long scoreId, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/scores/{scoreId}/replay");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task uploadReplayAsync(long scoreId, string bearer)
    {
        // Minimal legacy-replay shape (ruleset id, version int, string marker), the same structural
        // check ReplayStorageTest documents.
        byte[] replay = new byte[64];
        replay[5] = 0x0b;
        replay[6] = 32;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{scoreId}/replay");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new ByteArrayContent(replay);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"replay upload for score {scoreId}");
    }

    private static async Task<int> scoreViewsAsync(long scoreId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT replay_views FROM scores WHERE id = @scoreId", new { scoreId });
    }

    private static async Task<long> totalAsync(long userId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await ReplayViews.TotalForUserAsync(conn, userId);
    }

    /// <summary>Views credited to one user in one month; the month is passed as text because
    /// Dapper cannot bind a DateOnly parameter.</summary>
    private static async Task<long> monthViewsAsync(long userId, DateOnly month)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            """
            SELECT COALESCE((SELECT views FROM user_month_replay_views
                             WHERE user_id = @userId AND month = @month::date), 0)::bigint
            """,
            new { userId, month = month.ToString("yyyy-MM-dd") });
    }

    private static async Task<int> ledgerRowsAsync(long scoreId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM replay_views WHERE score_id = @scoreId", new { scoreId });
    }

    /// <summary>The days this viewer has claimed for this score, as yyyy-MM-dd (Dapper has no
    /// scalar DateOnly reader, and the string is what the assertions compare anyway).</summary>
    private static async Task<List<string>> ledgerAsync(long scoreId, long viewerId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return (await conn.QueryAsync<string>(
            """
            SELECT to_char(viewed_on, 'YYYY-MM-DD') FROM replay_views
            WHERE score_id = @scoreId AND viewer_id = @viewerId
            ORDER BY viewed_on
            """,
            new { scoreId, viewerId })).ToList();
    }

    private static async Task backdateLedgerAsync(long scoreId, long viewerId, int days)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        int moved = await conn.ExecuteAsync(
            """
            UPDATE replay_views SET viewed_on = viewed_on - @days
            WHERE score_id = @scoreId AND viewer_id = @viewerId
            """,
            new { scoreId, viewerId, days });

        Assert.That(moved, Is.EqualTo(1), "there should be exactly one claim to age");
    }

    private static async Task seedMonthsAsync(NpgsqlConnection conn, long userId, params (string Month, int Views)[] months)
    {
        foreach (var (month, views) in months)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO user_month_replay_views (user_id, month, views)
                VALUES (@userId, @month::date, @views)
                ON CONFLICT (user_id, month) DO UPDATE SET views = excluded.views
                """,
                new { userId, month, views });
        }
    }

    private static Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, replace(@username, ' ', '.') || '@example.com', 'x', 'US')
            RETURNING id
            """,
            new { username });

    private static Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId, string versionName)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, @versionName, @checksum, 60, 55, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, versionName, checksum = Guid.NewGuid().ToString("N") });

    private static Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, long totalScore)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.97, 1.0, 100, 'S', true, true,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmapId, totalScore });
}
