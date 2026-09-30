using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Beatmapset page: header + stats box + XSS-safe plain-text description, the best-per-user
/// leaderboard (engine judgement names, no unranked rows), the favourite toggle and report
/// POST handlers, visibility rules for hidden/removed sets, and the /beatmaps/{id} 301. Plus the
/// reviewer's Rank carrying the set's honest pending-era plays onto its board in the same request
/// (backlog 352).
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

            // The pace ladder, peak then target then average, each spelled out: a bare "WPM"
            // next to one number does not say which of the three figures it is.
            Assert.That(html, Does.Contain("Peak WPM"));
            Assert.That(html, Does.Contain(">143<"));
            Assert.That(html, Does.Contain("Target WPM"));
            Assert.That(html, Does.Contain(">118<"));
            Assert.That(html, Does.Contain("Average WPM"));
            Assert.That(html, Does.Contain(">80<"));

            // And the CPM readout is gone from the page entirely (backlog 272): it was the WPM
            // times five exactly, so it said nothing the row above it did not.
            Assert.That(html, Does.Not.Contain("Peak CPM"));
            Assert.That(html, Does.Not.Contain("Average CPM"));

            // Chars/word sits under Average WPM and needs no column: it is char_count / word_count
            // off the same row, 600/120 = 5.0 here. ONE DECIMAL, matching the game's wedge, because
            // real maps land between 4.1 and 4.6 and would all print "4" rounded to a whole number.
            Assert.That(html, Does.Contain("Chars/word"));
            Assert.That(html, Does.Contain(">5.0<"));

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

    /// <summary>
    /// The stats box names the mapper-chosen lyric font (backlog 291, 037_lyric_font.sql) only
    /// when the selected difficulty carries one. Per difficulty, like every other stat in the box:
    /// Twin Peaks' hard diff stores a family and its easy diff does not, so one set covers both
    /// arms through the same ?diff= re-render the rest of the box already uses.
    /// </summary>
    [Test]
    public async Task StatsBox_NamesTheLyricFont_OnlyWhenTheDifficultyHasOne()
    {
        using var hard = await WebsiteFixture.Client.GetAsync(
            $"/beatmapsets/{PublicSiteSeed.MultiDiffSetId}?diff={PublicSiteSeed.MultiDiffHardId}");
        string hardHtml = await hard.Content.ReadAsStringAsync();

        using var easy = await WebsiteFixture.Client.GetAsync(
            $"/beatmapsets/{PublicSiteSeed.MultiDiffSetId}?diff={PublicSiteSeed.MultiDiffEasyId}");
        string easyHtml = await easy.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(hard.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(hardHtml, Does.Contain("Lyric font"));
            Assert.That(hardHtml, Does.Contain(PublicSiteSeed.MultiDiffFontFamily));

            Assert.That(easy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(easyHtml, Does.Not.Contain("Lyric font"),
                "a difficulty with no font renders no row at all, not an empty one");
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
            // The pace panel's own readout ladder: target replaced the peak CPM that used to sit
            // here, and the seeded 702 must not surface anywhere on the page any more.
            Assert.That(html, Does.Contain("Target WPM"));
            Assert.That(html, Does.Contain(">118<"));
            Assert.That(html, Does.Not.Contain("Peak CPM"));
            Assert.That(html, Does.Not.Contain(">702<"));

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

            // A missing peak drops its row rather than printing a blank or a fabricated 0, and a
            // missing target (this fixture has neither, being the shape of a row no backfill has
            // reached) does the same: only the stored average survives.
            Assert.That(html, Does.Not.Contain("Peak WPM"));
            Assert.That(html, Does.Not.Contain("Target WPM"));
            Assert.That(html, Does.Contain("Average WPM"));
        });
    }

    [Test]
    public async Task SectionOrder_DescriptionAboveLeaderboard_CommentsBelowLyrics()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        int description = html.IndexOf("set-description", StringComparison.Ordinal);
        int leaderboard = html.IndexOf("set-leaderboard", StringComparison.Ordinal);
        int lyrics = html.IndexOf("set-lyrics", StringComparison.Ordinal);
        int comments = html.IndexOf("set-comments", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            // The description kept its pre-comments position above the leaderboard, and the new
            // comments section is the page's last, below the lyrics.
            Assert.That(description, Is.GreaterThanOrEqualTo(0));
            Assert.That(comments, Is.GreaterThanOrEqualTo(0));
            Assert.That(description, Is.LessThan(leaderboard));
            Assert.That(comments, Is.GreaterThan(lyrics));

            // Anonymous chrome: no edit box, and a sign-in link instead of the compose form.
            Assert.That(html, Does.Not.Contain("description-box"));
            Assert.That(html, Does.Contain("Sign in to comment"));
            Assert.That(html, Does.Not.Contain("id=\"comment-body\""));
        });
    }

    [Test]
    public async Task DescriptionEditBox_HiddenFromASignedInNonOwner()
    {
        // A dedicated user, NOT the shared "web player": every LoginAndVerifyAsync issues an
        // email code, and the shared account's hourly code budget (EmailCodeService.MaxPerHour)
        // is already spoken for by the older tests.
        const string name = "set page bystander";
        await WebsiteFixture.SeedUserAsync(name, "set.page.bystander@example.com", "hunter2hunter2");

        using var client = await SignedInBrowserAsync(name, "hunter2hunter2");
        using var response = await client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Not their set (and they are no reviewer): no edit box, but the compose form shows.
            Assert.That(html, Does.Not.Contain("description-box"));
            Assert.That(html, Does.Contain("id=\"comment-body\""));
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

    // ---- the rank button carries the set's pending-era plays (backlog 352) ----
    //
    // Each case seeds its OWN pending set, map and typists (dedicated users, never the shared seed
    // ones), and one dedicated reviewer signs in once for all of them: every sign-in spends the
    // per-account login budget.

    private const string carry_reviewer_name = "carry reviewer";
    private const string carry_reviewer_password = "carryreview-123456";

    private static HttpClient? carryReviewer;
    private static long carryBuildId;

    /// <summary>
    /// Ranking a pending set flips its honest unranked plays AND prices them inside the POST, so
    /// the page the reviewer is redirected to already has them on its ranked board, before any
    /// server boot. A Puppeteer play on the same set stays off it.
    /// </summary>
    [Test]
    public async Task Rank_CarriesAndPricesTheSetsHonestPendingPlays_InTheSameRequest()
    {
        var (setId, mapId) = await seedPendingCarrySetAsync("Carried Anthem");

        long one = await seedPendingPlayAsync(mapId, "carry typist one", 900_000);
        long two = await seedPendingPlayAsync(mapId, "carry typist two", 800_000);
        long three = await seedPendingPlayAsync(mapId, "carry typist three", 700_000);
        long puppeteer = await seedPendingPlayAsync(mapId, "carry puppeteer", 950_000, modsJson: """[{"acronym": "PT"}]""");

        var reviewer = await carryReviewerAsync();

        using var response = await postReviewAsync(reviewer, setId, "Rank");
        string html = await response.Content.ReadAsStringAsync();

        // Read BEFORE anything else could reprice: this is what the rank request itself left.
        var rows = await scoreRowsAsync(one, two, three, puppeteer);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo($"/beatmapsets/{setId}"));
            Assert.That(html, Does.Contain(">Ranked</span>"));

            // The redirected page's ranked board lists all three typists, in score order.
            Assert.That(html, Does.Contain("podium"));
            Assert.That(html, Does.Contain("carry typist one"));
            Assert.That(html, Does.Contain("carry typist two"));
            Assert.That(html, Does.Contain("carry typist three"));
            Assert.That(html.IndexOf("carry typist two", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("carry typist three", StringComparison.Ordinal)));
            Assert.That(html, Does.Not.Contain("carry puppeteer"), "PT is unranked at every configuration");

            foreach (long id in new[] { one, two, three })
            {
                Assert.That(rows[id].Ranked, Is.True, $"score {id} carried");
                Assert.That(rows[id].PpVersion, Is.EqualTo(PerformancePoints.VERSION), $"score {id} priced in the rank request");
                Assert.That(rows[id].Pp, Is.GreaterThan(0), $"score {id} earned real pp");
            }

            Assert.That(rows[puppeteer].Ranked, Is.False);
            Assert.That(rows[puppeteer].Pp, Is.Zero);
        });

        // Nothing is left for the safety net: the boot sweep's own plan, over this set, carries
        // nothing more (the unscoped sweep would also walk the other fixtures' rows in this shared
        // database, so it is pinned on its own database in SetRankRefundVersionTest instead).
        int leftover = await runSweepForSetAsync(setId);
        int oneAudits = await refundAuditCountAsync(one);
        int puppeteerAudits = await refundAuditCountAsync(puppeteer);

        Assert.Multiple(() =>
        {
            Assert.That(leftover, Is.Zero);
            Assert.That(oneAudits, Is.EqualTo(1));
            Assert.That(puppeteerAudits, Is.Zero);
        });
    }

    /// <summary>
    /// The carry is best-effort, like the rank's audit row: a failure inside it logs and the rank
    /// still lands. FAULT INJECTION, not a seam: a trigger makes the refund's own
    /// <c>score_refunds</c> insert raise for the seeded play, so the real code path throws from
    /// the real place. The boot sweep then heals it once the fault is gone.
    /// </summary>
    [Test]
    public async Task Rank_WhenTheCarryFails_StillRanksTheSet()
    {
        var (setId, mapId) = await seedPendingCarrySetAsync("Faulted Anthem");
        long play = await seedPendingPlayAsync(mapId, "carry faulted typist", 850_000);

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(
            $"""
            CREATE OR REPLACE FUNCTION carry_fault_{play}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.score_id = {play} THEN RAISE EXCEPTION 'injected carry fault'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER carry_fault_{play} BEFORE INSERT ON score_refunds
                FOR EACH ROW EXECUTE FUNCTION carry_fault_{play}();
            """);

        HttpStatusCode status;
        string path;

        try
        {
            using var response = await postReviewAsync(await carryReviewerAsync(), setId, "Rank");
            status = response.StatusCode;
            path = response.RequestMessage!.RequestUri!.AbsolutePath;
        }
        finally
        {
            await conn.ExecuteAsync($"DROP TRIGGER carry_fault_{play} ON score_refunds; DROP FUNCTION carry_fault_{play}();");
        }

        string? setStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @setId", new { setId });
        int rankAudits = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM moderation_actions WHERE set_id = @setId AND action = 'rank'", new { setId });
        bool carried = await conn.ExecuteScalarAsync<bool>("SELECT ranked FROM scores WHERE id = @play", new { play });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "a failed carry must not 500 the rank");
            Assert.That(path, Is.EqualTo($"/beatmapsets/{setId}"));
            Assert.That(setStatus, Is.EqualTo("ranked"));
            Assert.That(rankAudits, Is.EqualTo(1));
            Assert.That(carried, Is.False, "the faulted row rolled back");
        });

        // With the fault gone, the safety net (the boot sweep's plan) picks it up.
        int healed = await runSweepForSetAsync(setId);

        Assert.That(healed, Is.EqualTo(1));
    }

    /// <summary>
    /// Unrank leaves carried plays as they are (backlog 352 changes nothing about unranking), and
    /// a re-rank carries only what is new since: never the same score twice, one audit row each.
    /// </summary>
    [Test]
    public async Task UnrankThenRerank_CarriesNothingTwice()
    {
        var (setId, mapId) = await seedPendingCarrySetAsync("Twice Ranked Anthem");

        long first = await seedPendingPlayAsync(mapId, "carry twice one", 900_000);
        long second = await seedPendingPlayAsync(mapId, "carry twice two", 800_000);

        var reviewer = await carryReviewerAsync();

        using (var rank = await postReviewAsync(reviewer, setId, "Rank"))
            Assert.That(rank.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using (var unrank = await postReviewAsync(reviewer, setId, "Unrank"))
            Assert.That(unrank.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // A play made while the set is pending AGAIN: the only thing the re-rank should carry.
        long third = await seedPendingPlayAsync(mapId, "carry twice three", 700_000);

        var afterUnrank = await scoreRowsAsync(first, second);

        using (var rerank = await postReviewAsync(reviewer, setId, "Rank"))
            Assert.That(rerank.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var afterRerank = await scoreRowsAsync(first, second, third);

        var audits = new Dictionary<long, int>();

        foreach (long id in new[] { first, second, third })
            audits[id] = await refundAuditCountAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(afterUnrank[first].Ranked, Is.True, "unranking re-flags nothing");
            Assert.That(afterUnrank[second].Ranked, Is.True);
            Assert.That(afterRerank[third].Ranked, Is.True, "the re-rank carries the new pending-era play");

            foreach (var (id, count) in audits)
                Assert.That(count, Is.EqualTo(1), $"score {id}: one audit row, carried once");
        });
    }

    /// <summary>The boot sweep's own plan, narrowed to one set, against the host's database and store.</summary>
    private static async Task<int> runSweepForSetAsync(long setId)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        return await SetRankRefund.RunForSetAsync(
            new Db(dataSource), WebsiteFixture.Services.GetRequiredService<IFileStore>(), NullLogger.Instance, setId);
    }

    private static async Task<HttpClient> carryReviewerAsync()
    {
        if (carryReviewer != null)
            return carryReviewer;

        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                """
                INSERT INTO users (username, email, password_hash, country_code, map_reviewer)
                VALUES (@name, 'carry.reviewer@example.com', @hash, 'US', true)
                """,
                new { name = carry_reviewer_name, hash = new Typebeat.Web.Auth.PasswordService().Hash(carry_reviewer_password) });

            carryBuildId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO builds (version_hash, blocked) VALUES ('carry-test-build', false) RETURNING id");
        }

        carryReviewer = await SignedInBrowserAsync(carry_reviewer_name, carry_reviewer_password);
        return carryReviewer;
    }

    /// <summary>A pending set owned by the seeded mapper, with one priceable difficulty.</summary>
    private static async Task<(long SetId, long MapId)> seedPendingCarrySetAsync(string title)
    {
        await carryReviewerAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Carriers', 'pending', now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

        // Zero drain, so the play-time gate clears; a rating matrix, so a carried play can be priced.
        long mapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, ratings)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu', @ratings::jsonb)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), ratings = TestRatings.Json(2.0) });

        return (setId, mapId);
    }

    /// <summary>
    /// A play exactly as the submit path stores one on a pending set: passed, clean, unranked,
    /// already settled at pp 0 at the CURRENT pp version (so only a real reprice moves it), with
    /// the completed token naming the map's current .osu.
    /// </summary>
    private static async Task<long> seedPendingPlayAsync(long mapId, string username, long totalScore, string modsJson = "[]")
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long userId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, build_id, started_at, ended_at, pp, pp_version)
            VALUES
                (@userId, @mapId, @totalScore, 1.0, 1.0, 5, 'X', true, false,
                 CAST(@modsJson AS jsonb), '{"great": 5}'::jsonb, '{"great": 5}'::jsonb, @buildId,
                 now() - interval '60 seconds', now(), 0, @ppVersion)
            RETURNING id
            """,
            new { userId, mapId, totalScore, modsJson, buildId = carryBuildId, ppVersion = PerformancePoints.VERSION });

        await SetRankRefundTest.insertTokenAsync(conn, userId, mapId, carryBuildId, scoreId);

        return scoreId;
    }

    private static async Task<HttpResponseMessage> postReviewAsync(HttpClient client, long setId, string handler)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        return await client.PostAsync($"/beatmapsets/{setId}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
    }

    private static async Task<Dictionary<long, (bool Ranked, double Pp, int PpVersion)>> scoreRowsAsync(params long[] ids)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return (await conn.QueryAsync<(long Id, bool Ranked, double Pp, int PpVersion)>(
                "SELECT id, ranked, pp, pp_version FROM scores WHERE id = ANY(@ids)", new { ids }))
            .ToDictionary(r => r.Id, r => (r.Ranked, r.Pp, r.PpVersion));
    }

    private static async Task<int> refundAuditCountAsync(long scoreId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM score_refunds WHERE migration = @migration AND score_id = @scoreId",
            new { migration = SetRankRefund.MIGRATION_KEY, scoreId });
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
