using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Beatmapset page: header + stats box + XSS-safe plain-text description, the best-per-user
/// leaderboard (engine judgement names, no unranked rows), the favourite toggle and report
/// POST handlers, visibility rules for hidden/removed sets, and the /beatmaps/{id} 301.
/// </summary>
public class SetPageTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task SetPage_RendersHeaderStatsAndDescription()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Header block.
            Assert.That(html, Does.Contain("Leaderboard Anthem"));
            Assert.That(html, Does.Contain("The Score Settlers"));
            Assert.That(html, Does.Contain("mapped by"));
            Assert.That(html, Does.Contain($"href=\"/users/{PublicSiteSeed.MapperId}\""));
            Assert.That(html, Does.Contain(">Ranked</span>"));
            Assert.That(html, Does.Contain($"href=\"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}/download\""));

            // Stats box: 95.5s renders as 1:35; words/chars/wpm/stars from the beatmap row.
            Assert.That(html, Does.Contain("1:35"));
            Assert.That(html, Does.Contain(">120<"));
            Assert.That(html, Does.Contain(">600<"));
            Assert.That(html, Does.Contain("80 WPM"));
            Assert.That(html, Does.Contain("3.2"));

            // Description is plain text: markup arrives encoded, never live.
            Assert.That(html, Does.Contain("Line one"));
            Assert.That(html, Does.Contain("&lt;script&gt;alert(1)&lt;/script&gt;"));
            Assert.That(html, Does.Not.Contain("<script>alert(1)</script>"));

            // Tags + source chips, and OpenGraph metadata.
            Assert.That(html, Does.Contain("anthem"));
            Assert.That(html, Does.Contain("Type Hero"));
            Assert.That(html, Does.Contain("property=\"og:title\""));
        });
    }

    [Test]
    public async Task Leaderboard_BestPerUser_RankedOnly_EngineJudgementNames()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Podium (#1) + table players.
            Assert.That(html, Does.Contain("podium"));
            Assert.That(html, Does.Contain("typist one"));
            Assert.That(html, Does.Contain("typist two"));
            Assert.That(html, Does.Contain("typist three"));
            Assert.That(html, Does.Contain("900,000"));

            // typist one's weaker second score must be folded away by best-per-user.
            Assert.That(html, Does.Not.Contain("600,000"));

            // The unranked score (and its owner) never surface.
            Assert.That(html, Does.Not.Contain("cheat suspect"));
            Assert.That(html, Does.Not.Contain("999,999,999"));

            // Wire keys great/ok/meh/miss surface under the engine's judgement names.
            Assert.That(html, Does.Contain(">Perfect<"));
            Assert.That(html, Does.Contain(">Good<"));
            Assert.That(html, Does.Contain(">Ok<"));
            Assert.That(html, Does.Contain(">Miss<"));

            // Accuracy formatting.
            Assert.That(html, Does.Contain("98.46%"));
        });
    }

    [Test]
    public async Task Favourite_Post_TogglesRowAndCounter()
    {
        long setId = PublicSiteSeed.LeaderboardSetId;

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Sign in as the fixture-seeded user.
        string loginToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
        using (var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Login"] = WebsiteFixture.SeededUsername,
            ["Password"] = WebsiteFixture.SeededPassword,
        })))
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int countBefore = await FavouriteCountAsync(setId);

        // Toggle ON.
        string html = await PostFavouriteAsync(client, setId);
        bool rowOn = await HasFavouriteRowAsync(setId, WebsiteFixture.SeededUserId);
        int countOn = await FavouriteCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Favourited"));
            Assert.That(rowOn, Is.True);
            Assert.That(countOn, Is.EqualTo(countBefore + 1));
        });

        // Toggle OFF.
        html = await PostFavouriteAsync(client, setId);
        bool rowOff = await HasFavouriteRowAsync(setId, WebsiteFixture.SeededUserId);
        int countOff = await FavouriteCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("Favourited"));
            Assert.That(rowOff, Is.False);
            Assert.That(countOff, Is.EqualTo(countBefore));
        });
    }

    [Test]
    public async Task Favourite_Anonymous_IsSentToLogin()
    {
        long setId = PublicSiteSeed.LeaderboardSetId;

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        using var response = await client.PostAsync($"/beatmapsets/{setId}?handler=Favourite",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["returnUrl"] = $"/beatmapsets/{setId}",
            }));

        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(html, Does.Contain("Sign in"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Report_Post_InsertsAnonymousReport_AndThanks()
    {
        long setId = PublicSiteSeed.LeaderboardSetId;
        const string reason = "stolen map - this is not the mapper's work";

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        using var response = await client.PostAsync($"/beatmapsets/{setId}?handler=Report",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["reason"] = reason,
            }));

        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.Query, Does.Contain("reported=true"));
            Assert.That(html, Does.Contain("your report is in"));
        });

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var report = await conn.QuerySingleAsync<(string Reason, long? ReporterId, string Kind, string Status)>(
            """
            SELECT reason AS Reason, reporter_id AS ReporterId, kind AS Kind, status AS Status
            FROM reports
            WHERE set_id = @setId
            ORDER BY id DESC
            LIMIT 1
            """,
            new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(report.Reason, Is.EqualTo(reason));
            Assert.That(report.ReporterId, Is.Null, "anonymous reports store a NULL reporter");
            Assert.That(report.Kind, Is.EqualTo("user_report"));
            Assert.That(report.Status, Is.EqualTo("open"));
        });
    }

    [Test]
    public async Task PackagelessSet_HidesDownload_ShowsInGameOnlyHint()
    {
        // The pre-M3 shape (live diffs, no set_versions): a Download link would 404.
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.PackagelessId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain($"/beatmapsets/{PublicSiteSeed.PackagelessId}/download"));
            Assert.That(html, Does.Contain("available in-game only"));

            // Its diff is live (the migration-004 state), so stats still render.
            Assert.That(html, Does.Not.Contain("No difficulty data yet"));
            Assert.That(html, Does.Contain("60 WPM"));
        });
    }

    [Test]
    public async Task PackagelessSet_CardOmitsTheDownloadLink()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/beatmapsets?q=Editor%20Era");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.PackagelessId}\""));
            Assert.That(html, Does.Not.Contain($"/beatmapsets/{PublicSiteSeed.PackagelessId}/download"));
            Assert.That(html, Does.Contain("available in-game only"));
        });
    }

    [Test]
    public async Task HiddenAndRemovedSets_Are404_ForAnonymous()
    {
        using var hidden = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.HiddenId}");
        using var removed = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.RemovedId}");
        using var missing = await WebsiteFixture.Client.GetAsync("/beatmapsets/987654321");

        Assert.Multiple(() =>
        {
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task RemovedSet_VisibleToOwnerAndAdmin_404ForEveryoneElse()
    {
        const string owner_name = "dmca owner";
        const string admin_name = "dmca admin";
        const string password = "hunter2hunter2";

        long setId;
        string hash = new Typebeat.Web.Auth.PasswordService().Hash(password);

        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();

            long ownerId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES (@name, 'dmca.owner@example.com', @hash, 'US')
                RETURNING id
                """,
                new { name = owner_name, hash });

            await conn.ExecuteAsync(
                """
                INSERT INTO users (username, email, password_hash, country_code, is_admin)
                VALUES (@name, 'dmca.admin@example.com', @hash, 'US', true)
                """,
                new { name = admin_name, hash });

            setId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmapsets (owner_id, title, artist, status)
                VALUES (@ownerId, 'Taken Down Tune', 'Struck Artist', 'removed')
                RETURNING id
                """,
                new { ownerId });
        }

        // Anonymous: gone.
        using (var anonymous = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{setId}"))
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // A signed-in user who is neither owner nor admin: still gone.
        using (var client = await SignedInBrowserAsync(WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword))
        using (var other = await client.GetAsync($"/beatmapsets/{setId}"))
            Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // The owner sees their own removed set, with the status pill making the state obvious.
        using (var client = await SignedInBrowserAsync(owner_name, password))
        using (var response = await client.GetAsync($"/beatmapsets/{setId}"))
        {
            string html = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(html, Does.Contain("Taken Down Tune"));
                Assert.That(html, Does.Contain(">Removed</span>"));
            });
        }

        // Admins see it too (takedown review).
        using (var client = await SignedInBrowserAsync(admin_name, password))
        using (var response = await client.GetAsync($"/beatmapsets/{setId}"))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>A browser client signed in via the real /login form.</summary>
    private static async Task<HttpClient> SignedInBrowserAsync(string username, string password)
    {
        var (client, _) = WebsiteFixture.CreateBrowser();

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Login"] = username,
            ["Password"] = password,
        }));

        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"login as {username}");
        return client;
    }

    [Test]
    public async Task BeatmapId_RedirectsPermanentlyToItsSet()
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();

        using var response = await client.GetAsync($"/beatmaps/{PublicSiteSeed.LeaderboardBeatmapId}");
        using var unknown = await client.GetAsync("/beatmaps/987654321");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently));
            Assert.That(response.Headers.Location!.OriginalString,
                Is.EqualTo($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}"));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- helpers ----

    private static async Task<string> PostFavouriteAsync(HttpClient client, long setId)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        using var response = await client.PostAsync($"/beatmapsets/{setId}?handler=Favourite",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["returnUrl"] = $"/beatmapsets/{setId}",
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<int> FavouriteCountAsync(long setId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT favourite_count FROM beatmapsets WHERE id = @setId", new { setId });
    }

    private static async Task<bool> HasFavouriteRowAsync(long setId, long userId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM favourites WHERE set_id = @setId AND user_id = @userId)",
            new { setId, userId });
    }
}
