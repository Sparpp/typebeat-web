using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The header bell and its surfaces (027_notifications.sql, task 70): the badge on every page,
/// the dropdown's fetched contents, the two read-marking handlers and their auth rules, and the
/// nav change that replaced the old "watching" tab with the bell.
///
/// Rows are seeded with SQL here, deliberately: WHERE they come from (the publish latch inside
/// PackageIngest) is <see cref="NotificationFanOutTest"/>'s subject, and driving a real upload
/// through this fixture would only make these assertions slower and less specific.
///
/// Every test owns its OWN user, because the badge is a per-user count and a shared user would
/// make one test's "mark all read" another test's precondition. NUnit promises no order.
/// </summary>
public class NotificationsTest
{
    private const string password = "notifpass-123456";

    private static long mapperId;
    private static long setId;
    private static long secondSetId;

    private const string set_title = "Bellringer Suite";
    private const string second_set_title = "Bellringer Encore";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        mapperId = await WebsiteFixture.SeedUserAsync("bell mapper", "bell.mapper@example.com", password, verified: true);

        setId = await insertSetAsync(conn, mapperId, set_title);
        secondSetId = await insertSetAsync(conn, mapperId, second_set_title);
    }

    // ---- the badge ----

    [Test]
    public async Task Bell_ShowsUnreadCount_OnEveryPage()
    {
        var (client, userId) = await signedInUserAsync("bell counter");
        using var __ = client;

        await seedNotificationAsync(userId, setId);
        await seedNotificationAsync(userId, secondSetId);

        using var response = await client.GetAsync("/beatmapsets");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // The bell lives in the layout, so it is on a page that has nothing to do with
            // notifications.
            Assert.That(html, Does.Contain("notif-bell"));
            Assert.That(html, Does.Contain("data-notif-badge>2<"));
            Assert.That(html, Does.Contain("bell-btn notif-bell__btn is-on"), "unread lights the bell");
        });
    }

    [Test]
    public async Task Bell_HasNoBadge_WhenNothingIsUnread()
    {
        var (client, _) = await signedInUserAsync("bell quiet");
        using var __ = client;

        using var response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("notif-bell"), "the bell is still there");
            // Hidden at zero is the ABSENCE of the element, not a CSS rule that could be defeated.
            Assert.That(html, Does.Not.Contain("data-notif-badge"));
            Assert.That(html, Does.Not.Contain("notif-bell__btn is-on"));
        });
    }

    [Test]
    public async Task Bell_ReadNotifications_DoNotCount()
    {
        var (client, userId) = await signedInUserAsync("bell read");
        using var __ = client;

        long id = await seedNotificationAsync(userId, setId);
        await markReadInDbAsync(id);

        using var response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(html, Does.Not.Contain("data-notif-badge"));
    }

    [Test]
    public async Task Bell_IsNotRenderedForAnonymousVisitors()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("notif-bell"));
        });
    }

    // ---- the nav ----

    [Test]
    public async Task Nav_NoLongerCarriesAWatchingTab()
    {
        var (client, _) = await signedInUserAsync("bell navver");
        using var __ = client;

        using var response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        string navLinks = sliceNavLinks(html);

        Assert.Multiple(() =>
        {
            Assert.That(navLinks, Does.Not.Contain("/watching"), "the tab is gone from the main nav");
            Assert.That(navLinks, Does.Contain("/beatmapsets"), "the slice really is the nav");
            // The page it pointed at is still reachable, now from the bell.
            Assert.That(html, Does.Contain("href=\"/watching\""));
        });
    }

    // ---- the dropdown ----

    [Test]
    public async Task Panel_RendersRecentNotifications_AndLinksToTheFullPage()
    {
        var (client, userId) = await signedInUserAsync("bell panelist");
        using var __ = client;

        await seedNotificationAsync(userId, setId);

        using var response = await client.GetAsync("/watching?handler=Panel");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain(set_title));
            Assert.That(html, Does.Contain("bell mapper"));
            Assert.That(html, Does.Contain("notif-row is-unread"));
            // The row is a self-posting form, not a link, so it can mark itself read.
            Assert.That(html, Does.Contain("handler=Read"));
            Assert.That(html, Does.Contain("mark all read"));
            // "See all" is the surviving /watching page.
            Assert.That(html, Does.Contain("href=\"/watching\""));
            // A partial, not a page: no layout came back with it.
            Assert.That(html, Does.Not.Contain("<html"));
        });
    }

    [Test]
    public async Task Panel_ShowsOnlyYourOwnNotifications()
    {
        var (client, _) = await signedInUserAsync("bell mine");
        using var __ = client;

        var (otherClient, otherId) = await signedInUserAsync("bell theirs");
        otherClient.Dispose();

        await seedNotificationAsync(otherId, setId);

        using var response = await client.GetAsync("/watching?handler=Panel");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain(set_title));
            Assert.That(html, Does.Contain("Nothing yet"));
        });
    }

    [Test]
    public async Task WatchingPage_IsTheNotificationsHome_AndKeepsItsWatchlist()
    {
        var (client, userId) = await signedInUserAsync("bell homer");
        using var __ = client;

        await seedNotificationAsync(userId, setId);
        await seedWatchAsync(userId, mapperId);

        using var response = await client.GetAsync("/watching");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("<h1>Notifications</h1>"));
            // The notification list, from the same row partial the dropdown uses.
            Assert.That(html, Does.Contain("notif-row is-unread"));
            Assert.That(html, Does.Contain(set_title));
            // The watched-mapper management surface task 64 built has to stay reachable.
            Assert.That(html, Does.Contain($"data-user-id=\"{mapperId}\""));
            Assert.That(html, Does.Contain("Watching 1"));
        });
    }

    // ---- read semantics ----

    [Test]
    public async Task Read_MarksThatRowOnly_AndForwardsToTheSet()
    {
        var (client, userId) = await signedInUserAsync("bell clicker");
        using var __ = client;

        long clicked = await seedNotificationAsync(userId, setId);
        long untouched = await seedNotificationAsync(userId, secondSetId);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/watching");

        using var response = await client.PostAsync("/watching?handler=Read",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = clicked.ToString(),
            }));

        bool clickedRead = await isReadAsync(clicked);
        bool untouchedRead = await isReadAsync(untouched);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo($"/beatmapsets/{setId}"),
                "a click lands on the map it is about");
            Assert.That(clickedRead, Is.True);
            Assert.That(untouchedRead, Is.False, "only the clicked row");
        });
    }

    [Test]
    public async Task Read_OfSomebodyElsesNotification_Is404_AndReadsNothing()
    {
        var (client, _) = await signedInUserAsync("bell forger");
        using var __ = client;

        var (victimClient, victimId) = await signedInUserAsync("bell victim");
        victimClient.Dispose();

        long victimNotification = await seedNotificationAsync(victimId, setId);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/watching");

        using var response = await client.PostAsync("/watching?handler=Read",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = victimNotification.ToString(),
            }));

        bool victimRead = await isReadAsync(victimNotification);

        Assert.Multiple(() =>
        {
            // Indistinguishable from "no such notification": ownership is a WHERE clause, so the
            // forged id simply matches no row.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(victimRead, Is.False);
        });
    }

    [Test]
    public async Task ReadAll_ClearsTheBadge_AndAnswersFetchWithJson()
    {
        var (client, userId) = await signedInUserAsync("bell clearer");
        using var __ = client;

        await seedNotificationAsync(userId, setId);
        await seedNotificationAsync(userId, secondSetId);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/watching");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/watching?handler=ReadAll")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }),
        };
        request.Headers.Add("X-Requested-With", "fetch");

        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        int unread = await unreadCountAsync(userId);

        using var page = await client.GetAsync("/");
        string html = await page.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(json.RootElement.GetProperty("unread").GetInt32(), Is.Zero);
            Assert.That(unread, Is.Zero);
            Assert.That(html, Does.Not.Contain("data-notif-badge"), "the badge is gone on the next render");
        });
    }

    [Test]
    public async Task ReadAll_TouchesNobodyElsesRows()
    {
        var (client, userId) = await signedInUserAsync("bell selfish");
        using var __ = client;

        var (bystanderClient, bystanderId) = await signedInUserAsync("bell bystander");
        bystanderClient.Dispose();

        long bystanderNotification = await seedNotificationAsync(bystanderId, setId);
        await seedNotificationAsync(userId, setId);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/watching");

        using var response = await client.PostAsync("/watching?handler=ReadAll",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        bool bystanderRead = await isReadAsync(bystanderNotification);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bystanderRead, Is.False);
        });
    }

    // ---- anonymous ----

    [Test]
    public async Task Handlers_Anonymous_AreSentToLogin()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Signed out there is no bell and no form, so the token comes from /login (antiforgery
        // tokens are per-session, not per-page).
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var panel = await client.GetAsync("/watching?handler=Panel");

        using var read = await client.PostAsync("/watching?handler=Read",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = "1",
            }));

        using var readAll = await client.PostAsync("/watching?handler=ReadAll",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        Assert.Multiple(() =>
        {
            Assert.That(panel.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(read.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(readAll.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    // ---- visibility ----

    [Test]
    public async Task RemovedSets_DropOutOfBothTheListAndTheBadge()
    {
        var (client, userId) = await signedInUserAsync("bell takedown");
        using var __ = client;

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long doomedSetId = await insertSetAsync(conn, mapperId, "Bellringer Withdrawn");
        await seedNotificationAsync(userId, doomedSetId);

        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'removed' WHERE id = @doomedSetId", new { doomedSetId });

        using var response = await client.GetAsync("/watching");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("Bellringer Withdrawn"), "a taken-down map is not a notification");
            // The badge shares the list's visibility rule BY CONSTRUCTION, which is what stops a
            // hidden-but-counted row making the badge permanently unclearable.
            Assert.That(html, Does.Not.Contain("data-notif-badge"));
        });
    }

    // ---- helpers ----

    /// <summary>Seeds a fresh account and returns a browser client signed in as it, plus its id.</summary>
    private static async Task<(HttpClient Client, long UserId)> signedInUserAsync(string username)
    {
        long id = await WebsiteFixture.SeedUserAsync(
            username, username.Replace(' ', '.') + "@example.com", password, verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);

        return (client, id);
    }

    private static async Task<long> insertSetAsync(NpgsqlConnection conn, long ownerId, string title)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, @title, 'The Bell Band', 'ranked')
            RETURNING id
            """,
            new { ownerId, title });

    private static async Task<long> seedNotificationAsync(long userId, long setId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO user_notifications (user_id, kind, set_id, actor_id)
            VALUES (@userId, 'mapper_upload', @setId, @mapperId)
            RETURNING id
            """,
            new { userId, setId, mapperId });
    }

    private static async Task seedWatchAsync(long followerId, long followeeId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(
            """
            INSERT INTO user_follows (follower_id, followee_id, kind)
            VALUES (@followerId, @followeeId, 'mapper')
            ON CONFLICT DO NOTHING
            """,
            new { followerId, followeeId });
    }

    private static async Task markReadInDbAsync(long id)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync("UPDATE user_notifications SET read_at = now() WHERE id = @id", new { id });
    }

    private static async Task<bool> isReadAsync(long id)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<bool>(
            "SELECT read_at IS NOT NULL FROM user_notifications WHERE id = @id", new { id });
    }

    private static async Task<int> unreadCountAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_notifications WHERE user_id = @userId AND read_at IS NULL",
            new { userId });
    }

    /// <summary>
    /// The contents of the main nav's link list, so "the watching tab is gone" is an assertion
    /// about the NAV rather than about the whole page (the bell links to /watching too, from
    /// .nav-auth, and it must keep doing so).
    /// </summary>
    private static string sliceNavLinks(string html)
    {
        int start = html.IndexOf("class=\"nav-links\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "no nav-links block in the page");

        int end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), "unterminated nav-links block");

        return html[start..end];
    }
}
