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
            Assert.That(html, Does.Contain("3.2"));

            // The pace pair, peak above average, with "Average" spelled out: a bare "WPM" next to
            // one number does not say which of the two figures it is.
            Assert.That(html, Does.Contain("Peak WPM"));
            Assert.That(html, Does.Contain(">143<"));
            Assert.That(html, Does.Contain("Average WPM"));
            Assert.That(html, Does.Contain(">80<"));

            // BPM is gone from the box; the seeded set's 128 must not surface anywhere.
            Assert.That(html, Does.Not.Contain(">BPM<"));
            Assert.That(html, Does.Not.Contain(">128<"));

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

            // Wire keys great/ok/meh/miss surface under the engine's judgement names, which are
            // the same words since backlog 133 aligned the two vocabularies.
            Assert.That(html, Does.Contain(">Great<"));
            Assert.That(html, Does.Contain(">Ok<"));
            Assert.That(html, Does.Contain(">Meh<"));
            Assert.That(html, Does.Contain(">Miss<"));
            Assert.That(html, Does.Not.Contain(">Perfect<"), "the fourth tier went with backlog 147");

            // Accuracy formatting.
            Assert.That(html, Does.Contain("98.46%"));
        });
    }

    [Test]
    public async Task LyricsSection_BelowLeaderboard_RendersLinesEncodedWithCasing()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("set-lyrics"));

            // The stored '\n' between lines reaches the markup as the &#xA; character reference
            // (Razor's HtmlEncoder escapes control chars); the browser decodes it back to a
            // newline in the text node, which pre-line renders as a line break. Casing survives.
            Assert.That(html, Does.Contain("Neon LIGHTS are calling&#xA;We TYPE through the storm"));

            // Plain text like the description: markup arrives encoded, never live.
            Assert.That(html, Does.Contain("&lt;i&gt;stage whisper&lt;/i&gt;"));
            Assert.That(html, Does.Not.Contain("<i>stage whisper</i>"));

            // The section sits below the leaderboard.
            Assert.That(html.IndexOf("set-lyrics", StringComparison.Ordinal),
                Is.GreaterThan(html.IndexOf("set-leaderboard", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task LyricsSection_Hidden_WhenTheDifficultyHasNoLyrics()
    {
        // The packageless fixture's diff carries the '' default (backfill not there yet / blank
        // map): no lyrics section at all, not an empty box.
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.PackagelessId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("set-lyrics"));
        });
    }

    [Test]
    public async Task WpmTab_RendersBothPanelsServerSide_StatsIsTheDefault()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Both tabs and both panels are in the markup: the swap is a CSS :checked rule, so a
            // visitor with no JavaScript still gets everything and lands on the stats panel.
            Assert.That(html, Does.Contain("id=\"set-stats-tab-stats\""));
            Assert.That(html, Does.Contain("id=\"set-stats-tab-pace\""));
            Assert.That(html, Does.Contain("stat-panel--stats"));
            Assert.That(html, Does.Contain("stat-panel--pace"));

            // Stats carries the checked attribute, and it is the only one that does.
            Assert.That(html, Does.Contain("id=\"set-stats-tab-stats\" checked"));
            Assert.That(html, Does.Not.Contain("id=\"set-stats-tab-pace\" checked"));

            // The graph: one bar per stored curve point, the seeded peak flagged, the seeded
            // zero bucket rendered as the baseline stub rather than dropped.
            Assert.That(html, Does.Contain("wpm-graph"));
            Assert.That(html, Does.Contain("title=\"143 WPM\""));
            Assert.That(html, Does.Contain("wpm-graph__bar is-empty"));
            Assert.That(html, Does.Contain("Peak CPM"));
            Assert.That(html, Does.Contain(">702<"));

            // Tallest bar is full height, the 60 next to a 143 peak is 41.96% of it.
            Assert.That(html, Does.Contain("height:100%"));
            Assert.That(html, Does.Contain("height:41.96%"));
        });
    }

    [Test]
    public async Task WpmTab_DegradesToANote_WhenTheDifficultyHasNoCurve()
    {
        // The packageless fixture's diff carries NULL peak/curve: the state of every row the v11
        // pace backfill has not reached, and of every map too short to measure.
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.PackagelessId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // The tab still exists, and clicking it says so instead of showing an empty plot.
            Assert.That(html, Does.Contain("stat-panel--pace"));
            Assert.That(html, Does.Contain("No pace graph for this difficulty yet."));
            Assert.That(html, Does.Not.Contain("wpm-graph__bar"));

            // A missing peak drops its row rather than printing a blank or a fabricated 0.
            Assert.That(html, Does.Not.Contain("Peak WPM"));
            Assert.That(html, Does.Contain("Average WPM"));
        });
    }

    [Test]
    public async Task Favourite_Post_TogglesRowAndCounter()
    {
        long setId = PublicSiteSeed.LeaderboardSetId;

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Sign in as the fixture-seeded user (two-step: password → emailed code → session).
        using (await WebsiteFixture.LoginAndVerifyAsync(client, WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword)) { }

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
            Assert.That(html, Does.Contain("Average WPM"));
            Assert.That(html, Does.Contain(">60<"));
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

    /// <summary>A browser client signed in via the real two-step /login → /verify flow.</summary>
    private static async Task<HttpClient> SignedInBrowserAsync(string username, string password)
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, username, password)) { }
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

    [Test]
    public async Task ScoreId_RedirectsPermanentlyToItsSet()
    {
        // The game's leaderboard "Copy Link" copies {WebsiteUrl}/scores/{id}; it must land.
        long scoreId;
        long hiddenScoreId;

        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();

            scoreId = await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM scores WHERE beatmap_id = @beatmapId AND ranked ORDER BY total_score DESC LIMIT 1",
                new { beatmapId = PublicSiteSeed.LeaderboardBeatmapId });

            // A score on a hidden set: the redirect must not leak the set id to the public.
            long hiddenBeatmapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
                VALUES (@setId, 'type!beat', @checksum, 60, 55, 1.5, 'hidden.osu')
                RETURNING id
                """,
                new { setId = PublicSiteSeed.HiddenId, checksum = Guid.NewGuid().ToString("N") });

            hiddenScoreId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO scores (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                                    mods, statistics, maximum_statistics)
                VALUES (@userId, @beatmapId, 100000, 0.9, 10, 'B', true, true, '[]'::jsonb, '{}'::jsonb, '{}'::jsonb)
                RETURNING id
                """,
                new { userId = PublicSiteSeed.TypistOneId, beatmapId = hiddenBeatmapId });
        }

        using var client = WebsiteFixture.CreateNoRedirectClient();

        using var response = await client.GetAsync($"/scores/{scoreId}");
        using var unknown = await client.GetAsync("/scores/987654321");
        using var hidden = await client.GetAsync($"/scores/{hiddenScoreId}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently));
            Assert.That(response.Headers.Location!.OriginalString,
                Is.EqualTo($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}"));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
