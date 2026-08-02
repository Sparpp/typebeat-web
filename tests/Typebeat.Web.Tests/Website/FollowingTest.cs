using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Following (the profile Follow button) and mapper watching (the bell), both stored in
/// user_follows (023_follows.sql): toggle round trips, the server-side rules (auth, no
/// self-follow, restricted users are unfollowable), the two relations staying separate, the
/// follower/following counts and list pages, and the /watching upload feed.
///
/// Every test acts on its OWN follower/followee pair so the class has no internal ordering
/// dependency: NUnit does not promise an execution order, and a shared pair would make one
/// test's toggle another's precondition.
/// </summary>
public class FollowingTest
{
    private const string fan_username = "follow fan";
    private const string fan_password = "followpass-123456";

    private static long fanId;
    private static long followTargetId;
    private static long watchTargetId;
    private static long jsonTargetId;
    private static long restrictedTargetId;

    // Dedicated to the list-page assertions, wired up with SQL in setup.
    private static long idolId;
    private static long listedFanId;
    private static long restrictedFanId;

    // Feed fixtures: two watched mappers, one unwatched, all seeded with SQL.
    private static long mapperAlphaId;
    private static long mapperBravoId;
    private static long mapperCharlieId;

    private const string alpha_title = "Watched Alpha Anthem";
    private const string bravo_title = "Watched Bravo Ballad";
    private const string charlie_title = "Unwatched Charlie Chant";
    private const string alpha_hidden_title = "Watched Alpha Secret";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        fanId = await InsertUserAsync(conn, fan_username, new PasswordService().Hash(fan_password));
        followTargetId = await InsertUserAsync(conn, "follow target");
        watchTargetId = await InsertUserAsync(conn, "watch target");
        jsonTargetId = await InsertUserAsync(conn, "json target");
        restrictedTargetId = await InsertUserAsync(conn, "banned target", restricted: true);

        idolId = await InsertUserAsync(conn, "list idol");
        listedFanId = await InsertUserAsync(conn, "list fan");
        restrictedFanId = await InsertUserAsync(conn, "banned fan", restricted: true);

        mapperAlphaId = await InsertUserAsync(conn, "feed mapper alpha");
        mapperBravoId = await InsertUserAsync(conn, "feed mapper bravo");
        mapperCharlieId = await InsertUserAsync(conn, "feed mapper charlie");

        // List page fixture: two followers of the idol, one of them restricted (delisted
        // site-wide, so it must count for nothing and render nowhere). The idol follows the fan
        // back, which gives the "following" direction something to show.
        await InsertFollowAsync(conn, listedFanId, idolId, "user");
        await InsertFollowAsync(conn, restrictedFanId, idolId, "user");
        await InsertFollowAsync(conn, idolId, listedFanId, "user");

        // Feed fixture: the fan watches alpha and bravo, never charlie.
        await InsertFollowAsync(conn, fanId, mapperAlphaId, "mapper");
        await InsertFollowAsync(conn, fanId, mapperBravoId, "mapper");

        // Alpha's set is OLDER than bravo's, so "newest first" is falsifiable. Charlie's is the
        // newest of the three: if the feed ever stopped filtering by watch it would lead the page.
        await InsertSetAsync(conn, mapperAlphaId, alpha_title, days: -6);
        await InsertSetAsync(conn, mapperBravoId, bravo_title, days: -3);
        await InsertSetAsync(conn, mapperCharlieId, charlie_title, days: -1);

        // A watched mapper's unpublished draft is not "an upload you can go play".
        await InsertSetAsync(conn, mapperAlphaId, alpha_hidden_title, days: -2, status: "hidden");
    }

    // ---- toggles ----

    [Test]
    public async Task Follow_RoundTrip_TogglesTheRowCountAndButton()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        string html = await PostToggleAsync(client, followTargetId, "Follow");
        bool followedOn = await HasFollowAsync(fanId, followTargetId, "user");
        bool watchedOn = await HasFollowAsync(fanId, followTargetId, "mapper");

        Assert.Multiple(() =>
        {
            Assert.That(followedOn, Is.True, "follow row written");
            // Kind separation: the Follow button must not also arm the mapper bell.
            Assert.That(watchedOn, Is.False, "follow must not watch");
            // The redirect lands back on the profile, now showing the ON state and the count.
            Assert.That(html, Does.Contain("btn-ghost is-on"));
            Assert.That(html, Does.Contain("data-follower-count>1<"));
        });

        // A second post is the UNfollow half of the toggle (same shape as the favourite button).
        html = await PostToggleAsync(client, followTargetId, "Follow");
        bool followedOff = await HasFollowAsync(fanId, followTargetId, "user");

        Assert.Multiple(() =>
        {
            Assert.That(followedOff, Is.False, "follow row removed");
            Assert.That(html, Does.Contain("data-follower-count>0<"));
        });
    }

    [Test]
    public async Task Watch_RoundTrip_TogglesTheMapperKindOnly()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        await PostToggleAsync(client, watchTargetId, "Watch");
        bool watchedOn = await HasFollowAsync(fanId, watchTargetId, "mapper");
        bool followedOn = await HasFollowAsync(fanId, watchTargetId, "user");

        Assert.Multiple(() =>
        {
            Assert.That(watchedOn, Is.True, "watch row written");
            // Kind separation, the other direction: the bell is not a follow.
            Assert.That(followedOn, Is.False, "watch must not follow");
        });

        await PostToggleAsync(client, watchTargetId, "Watch");
        Assert.That(await HasFollowAsync(fanId, watchTargetId, "mapper"), Is.False, "watch row removed");
    }

    [Test]
    public async Task Follow_FetchRequest_ReturnsJsonState()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/users/{jsonTargetId}?handler=Follow")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }),
        };
        request.Headers.Add("X-Requested-With", "fetch");

        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(json.RootElement.GetProperty("on").GetBoolean(), Is.True);
            Assert.That(json.RootElement.GetProperty("kind").GetString(), Is.EqualTo("user"));
            Assert.That(json.RootElement.GetProperty("followers").GetInt64(), Is.EqualTo(1));
        });
    }

    // ---- server-side rules ----

    [Test]
    public async Task Follow_Self_IsRejected()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/");

        using var response = await client.PostAsync($"/users/{fanId}?handler=Follow",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        bool selfFollowed = await HasFollowAsync(fanId, fanId, "user");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(selfFollowed, Is.False);
        });
    }

    [Test]
    public async Task Follow_SelfRow_IsRejectedByTheTableToo()
    {
        // The handler's 400 is the friendly half; the CHECK is what makes the row unrepresentable
        // for every OTHER write path (admin tooling, a backfill, a psql session).
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        Assert.That(
            async () => await InsertFollowAsync(conn, fanId, fanId, "user"),
            Throws.InstanceOf<PostgresException>());
    }

    [Test]
    public async Task Follow_DuplicateInsert_IsANoOp()
    {
        // Idempotent at the storage layer: the pair is the primary key and the toggle inserts
        // ON CONFLICT DO NOTHING, so a double submit cannot double-count a follower.
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long dupFan = await InsertUserAsync(conn, "dupe fan");
        long dupIdol = await InsertUserAsync(conn, "dupe idol");

        await InsertFollowAsync(conn, dupFan, dupIdol, "user");
        await InsertFollowAsync(conn, dupFan, dupIdol, "user");

        int rows = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_follows WHERE follower_id = @dupFan AND followee_id = @dupIdol",
            new { dupFan, dupIdol });

        Assert.That(rows, Is.EqualTo(1));
    }

    [Test]
    public async Task Follow_Anonymous_IsSentToLogin()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Signed out, the profile renders links instead of forms, so the token comes from /login
        // (antiforgery tokens are per-session, not per-page).
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var response = await client.PostAsync($"/users/{followTargetId}?handler=Follow",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["returnUrl"] = $"/users/{followTargetId}",
            }));

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Follow_RestrictedUser_Is404()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/");

        using var response = await client.PostAsync($"/users/{restrictedTargetId}?handler=Follow",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        bool followed = await HasFollowAsync(fanId, restrictedTargetId, "user");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(followed, Is.False);
        });
    }

    // ---- counts + list pages ----

    [Test]
    public async Task Profile_ShowsCounts_ExcludingRestrictedFollowers()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{idolId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // Two follow rows point at the idol, but one of them is a restricted account.
            Assert.That(html, Does.Contain("data-follower-count>1<"));
            Assert.That(html, Does.Contain("data-following-count>1<"));
            Assert.That(html, Does.Contain($"href=\"/users/{idolId}/followers\""));
            Assert.That(html, Does.Contain($"href=\"/users/{idolId}/following\""));
        });
    }

    [Test]
    public async Task FollowersPage_ListsFollowers_WithoutRestrictedOnes()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{idolId}/followers");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("user-row"));
            Assert.That(html, Does.Contain("list fan"));
            Assert.That(html, Does.Not.Contain("banned fan"));
        });
    }

    [Test]
    public async Task FollowingPage_ListsWhoTheUserFollows()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{idolId}/following");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain($"data-user-id=\"{listedFanId}\""));
        });
    }

    [Test]
    public async Task FollowLists_EmptyAndUnknownUsers()
    {
        using var empty = await WebsiteFixture.Client.GetAsync($"/users/{watchTargetId}/followers");
        using var unknown = await WebsiteFixture.Client.GetAsync("/users/987654321/following");
        using var restricted = await WebsiteFixture.Client.GetAsync($"/users/{restrictedTargetId}/followers");

        string emptyHtml = await empty.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(emptyHtml, Does.Contain("Nobody yet"));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(restricted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- watched mappers feed ----

    [Test]
    public async Task WatchingFeed_ShowsOnlyWatchedMappersUploads_NewestFirst()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(client, fan_username, fan_password);

        using var response = await client.GetAsync("/watching");
        string html = await response.Content.ReadAsStringAsync();

        int bravoAt = html.IndexOf(bravo_title, StringComparison.Ordinal);
        int alphaAt = html.IndexOf(alpha_title, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Both watched mappers' published sets, and nothing from the unwatched one, even
            // though its set is the newest of the three.
            Assert.That(bravoAt, Is.GreaterThan(-1), "bravo's set is in the feed");
            Assert.That(alphaAt, Is.GreaterThan(-1), "alpha's set is in the feed");
            Assert.That(html, Does.Not.Contain(charlie_title));

            // Newest submission first.
            Assert.That(bravoAt, Is.LessThan(alphaAt), "newer set must render before the older one");

            // A watched mapper's hidden draft is not an upload.
            Assert.That(html, Does.Not.Contain(alpha_hidden_title));

            // The strip of who is being watched, from the same user-row partial the lists use.
            Assert.That(html, Does.Contain($"data-user-id=\"{mapperAlphaId}\""));
            Assert.That(html, Does.Contain($"data-user-id=\"{mapperBravoId}\""));
            Assert.That(html, Does.Not.Contain($"data-user-id=\"{mapperCharlieId}\""));

            // The page is reachable from the chrome: task 70 moved that entry point out of the
            // main nav and into the header bell, which links here.
            Assert.That(html, Does.Contain("href=\"/watching\""));
        });
    }

    [Test]
    public async Task WatchingFeed_EmptyForAUserWatchingNobody()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await WebsiteFixture.LoginAndVerifyAsync(
            client, WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword);

        using var response = await client.GetAsync("/watching");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("No bells rung yet"));
        });
    }

    [Test]
    public async Task WatchingFeed_Anonymous_IsSentToLogin()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/watching");
        Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
    }

    [Test]
    public async Task Chrome_HidesWatchingFromSignedOutVisitors()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("href=\"/watching\""));
        });
    }

    // ---- helpers ----

    private static async Task<string> PostToggleAsync(HttpClient client, long targetId, string handler)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/users/{targetId}");

        using var response = await client.PostAsync($"/users/{targetId}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["returnUrl"] = $"/users/{targetId}",
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{handler} toggle on /users/{targetId}");
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<bool> HasFollowAsync(long followerId, long followeeId, string kind)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM user_follows
                           WHERE follower_id = @followerId AND followee_id = @followeeId AND kind = @kind)
            """,
            new { followerId, followeeId, kind });
    }

    private static async Task InsertFollowAsync(NpgsqlConnection conn, long followerId, long followeeId, string kind)
        => await conn.ExecuteAsync(
            """
            INSERT INTO user_follows (follower_id, followee_id, kind)
            VALUES (@followerId, @followeeId, @kind)
            ON CONFLICT DO NOTHING
            """,
            new { followerId, followeeId, kind });

    private static async Task<long> InsertUserAsync(
        NpgsqlConnection conn, string username, string? hash = null, bool restricted = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted)
            VALUES (@username, @email, @hash, 'US', @restricted)
            RETURNING id
            """,
            new
            {
                username,
                email = username.Replace(' ', '.') + "@example.com",
                hash = hash ?? "not-a-real-hash",
                restricted,
            });

    private static async Task<long> InsertSetAsync(
        NpgsqlConnection conn, long ownerId, string title, int days, string status = "ranked")
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Feed Band', @status, now() + @offset, now() + @offset)
            RETURNING id
            """,
            new { ownerId, title, status, offset = TimeSpan.FromDays(days) });
}
