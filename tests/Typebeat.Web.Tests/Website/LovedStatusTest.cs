using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Packages;
using Typebeat.Web.Pages;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// 'loved' as a published beatmapset status: the DB constraint accepts it, the status helpers know
/// it, the game's set API serves it (publicly, with the wire string "loved"), and the website
/// listing shows it under Any and under its own filter pill. And what it MEANS for plays: a loved
/// set serves the ranked board (its plays store ranked and are positioned there) but earns no pp,
/// not at submit, not on a pp version bump, and not on the board; only ranking the set prices them.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LovedStatusTest
{
    private const string loved_title = "Loved Candidate Song";

    private const string reviewer_name = "love reviewer";
    private const string reviewer_password = "lovepass-123456";

    private static long lovedSetId;
    private static long pendingSetId;
    private static long unrankedSetId;
    private static long rankedSetId;
    private static long lovedForPageSetId;

    // The priced-loved flow: a loved set whose map carries a ratings matrix, so a play on it WOULD
    // be priced if the set were ranked, and a typist with a bearer for the game-facing submit.
    private const string priced_title = "Loved Priced Song";
    private const string typist_name = "loved typist";
    private static long pricedLovedSetId;
    private static long pricedLovedBeatmapId;
    private static string pricedChecksum = null!;
    private static long typistId;
    private static string typistBearer = null!;

    // A loved set holding a ranked-era row that still stores pp (ranked, then unranked, then loved).
    private static long rankedEraLovedBeatmapId;

    // The browser player's loved set.
    private static long browserLovedSetId;
    private static long browserLovedBeatmapId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        lovedSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Loved', 'loved', now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title = loved_title });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu')
            """,
            new { setId = lovedSetId, checksum = Guid.NewGuid().ToString("N") });

        await conn.ExecuteAsync(
            """
            INSERT INTO users (username, email, password_hash, country_code, map_reviewer)
            VALUES (@name, 'love.reviewer@example.com', @hash, 'US', true)
            """,
            new { name = reviewer_name, hash = new PasswordService().Hash(reviewer_password) });

        pendingSetId = await seedSetAsync(conn, "Love Pending", "pending", null);
        unrankedSetId = await seedSetAsync(conn, "Love Unranked", "unranked", "unranked");
        rankedSetId = await seedSetAsync(conn, "Love Ranked", "ranked", null);
        lovedForPageSetId = await seedSetAsync(conn, "Love Page Loved", "loved", null);

        typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@name, 'loved.typist@example.com', 'x', 'US')
            RETURNING id
            """,
            new { name = typist_name });

        typistBearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        pricedLovedSetId = await seedSetAsync(conn, priced_title, "loved", null, withRatings: true);
        (pricedLovedBeatmapId, pricedChecksum) = await conn.QuerySingleAsync<(long, string)>(
            "SELECT id, checksum_md5 FROM beatmaps WHERE set_id = @pricedLovedSetId", new { pricedLovedSetId });

        long rankedEraSetId = await seedSetAsync(conn, "Love Ranked Era", "loved", null, withRatings: true);
        rankedEraLovedBeatmapId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM beatmaps WHERE set_id = @rankedEraSetId", new { rankedEraSetId });

        // Priced while the set was ranked; the set has since been unranked and loved. The row keeps
        // its flag and its stored pp (neither transition rewrites scores), so it is on the loved board.
        await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp, pp_version)
            VALUES
                (@typistId, @rankedEraLovedBeatmapId, 900000, 1.0, 1.0, 5, 'X', true, true,
                 '[]'::jsonb, '{"great":5}'::jsonb, '{"great":5}'::jsonb, 123.4, @version)
            """,
            new { typistId, rankedEraLovedBeatmapId, version = PerformancePoints.VERSION });

        browserLovedSetId = await seedSetAsync(conn, "Love Browser Song", "loved", null, withRatings: true);
        browserLovedBeatmapId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM beatmaps WHERE set_id = @browserLovedSetId", new { browserLovedSetId });
    }

    private static async Task<long> seedSetAsync(NpgsqlConnection conn, string title, string status, string? intended, bool withRatings = false)
    {
        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Loved', @status, now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title, status });

        if (intended is not null)
            await conn.ExecuteAsync("UPDATE beatmapsets SET intended_status = @intended WHERE id = @id", new { id, intended });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, ratings)
            VALUES (@id, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu', @ratings::jsonb)
            """,
            // The ratings matrix is what prices a play (PerformancePoints v22), so a set seeded with
            // one WOULD price its plays if it were ranked: "no pp" there is the status, not the map.
            new { id, checksum = Guid.NewGuid().ToString("N"), ratings = withRatings ? TestRatings.Json(2.0) : null });

        return id;
    }

    private static async Task<string?> statusAsync(long id)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @id", new { id });
    }

    private static async Task<string?> lastAuditNoteAsync(long id, string action)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>(
            "SELECT note FROM moderation_actions WHERE set_id = @id AND action = @action ORDER BY id DESC LIMIT 1",
            new { id, action });
    }

    private static async Task<HttpClient> signedInReviewerAsync()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, reviewer_name, reviewer_password)) { }
        return client;
    }

    private static async Task<HttpResponseMessage> postHandlerAsync(HttpClient client, string handler, long id)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{id}");

        return await client.PostAsync($"/beatmapsets/{id}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
    }

    [Test]
    public async Task Love_NonReviewer_Is404_AndChangesNothing()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword)) { }

        using var response = await postHandlerAsync(client, "Love", pendingSetId);
        string? status = await statusAsync(pendingSetId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(status, Is.EqualTo("pending"));
        });
    }

    [Test]
    public async Task LoveThenUnlove_Pending_RoundTrips_WithAuditRows()
    {
        using var client = await signedInReviewerAsync();

        using (await postHandlerAsync(client, "Love", pendingSetId)) { }
        string? loved = await statusAsync(pendingSetId);
        string? loveNote = await lastAuditNoteAsync(pendingSetId, "love");

        using (await postHandlerAsync(client, "Unlove", pendingSetId)) { }
        string? back = await statusAsync(pendingSetId);
        string? unloveNote = await lastAuditNoteAsync(pendingSetId, "unlove");

        Assert.Multiple(() =>
        {
            Assert.That(loved, Is.EqualTo("loved"));
            Assert.That(loveNote, Is.EqualTo("pending -> loved"));
            Assert.That(back, Is.EqualTo("pending"));
            Assert.That(unloveNote, Is.EqualTo("loved -> pending"));
        });
    }

    [Test]
    public async Task LoveThenUnlove_Unranked_ReturnsToItsIntendedStatus()
    {
        using var client = await signedInReviewerAsync();

        using (await postHandlerAsync(client, "Love", unrankedSetId)) { }
        string? loved = await statusAsync(unrankedSetId);

        using (await postHandlerAsync(client, "Unlove", unrankedSetId)) { }
        string? back = await statusAsync(unrankedSetId);

        Assert.Multiple(() =>
        {
            Assert.That(loved, Is.EqualTo("loved"));
            Assert.That(back, Is.EqualTo("unranked"));
        });
    }

    [Test]
    public async Task Love_OnARankedSet_IsABenignNoOp()
    {
        using var client = await signedInReviewerAsync();
        using var response = await postHandlerAsync(client, "Love", rankedSetId);
        string? status = await statusAsync(rankedSetId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(status, Is.EqualTo("ranked"));
        });
    }

    [Test]
    public async Task ReviewerSetPage_OffersLoveOnPending_AndUnloveOnLoved()
    {
        using var client = await signedInReviewerAsync();

        string pending = await client.GetStringAsync($"/beatmapsets/{PublicSiteSeed.PendingId}");
        string loved = await client.GetStringAsync($"/beatmapsets/{lovedForPageSetId}");

        Assert.Multiple(() =>
        {
            Assert.That(pending, Does.Contain("Mark as Respected"));
            Assert.That(loved, Does.Contain("Remove Respected"));
        });
    }

    [Test]
    public void StatusHelpers_KnowLoved()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapsetEndpoints.IsPublished("loved"), Is.True);
            Assert.That(BeatmapsetEndpoints.StatusString("loved"), Is.EqualTo("loved"));
            Assert.That(BeatmapsetDisplay.StatusLabel("loved"), Is.EqualTo("Respected"));
        });
    }

    [Test]
    public async Task LovedSet_IsPublic_OnTheGameApi()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;
        using var response = await client.GetAsync($"/api/v2/beatmapsets/{lovedSetId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.That((string)body["status"]!, Is.EqualTo("loved"));
    }

    [Test]
    public async Task LovedSet_ListsOnTheWebsite_UnderAnyAndLoved()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;
        string any = await client.GetStringAsync("/beatmapsets?q=Loved+Candidate");
        string loved = await client.GetStringAsync("/beatmapsets?status=loved");
        Assert.Multiple(() =>
        {
            Assert.That(any, Does.Contain(loved_title));
            Assert.That(loved, Does.Contain(loved_title));
            Assert.That(loved, Does.Contain("pill--loved"));
        });
    }

    // ---- loved plays: the ranked board, no pp ----

    /// <summary>
    /// The whole life of a loved play, in order: it submits ranked with no pp, sits alone at #1 on
    /// the game's board and on the set page, keeps no pp across a pp version bump, renders no pp on
    /// its player's profile, and is priced the moment a reviewer ranks the set (Unlove, then Rank).
    /// </summary>
    [Test]
    public async Task LovedPlay_SitsOnTheRankedBoard_WithoutPp_UntilTheSetIsRanked()
    {
        var submitted = await submitScoreAsync(pricedLovedBeatmapId, pricedChecksum, totalScore: 1_000_000);
        long scoreId = (long)submitted["id"]!;

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True, "a loved set's play stores on the ranked board");
            Assert.That(submitted["pp"]!.Type, Is.EqualTo(JTokenType.Null), "a loved play is never priced");
            // The game's submit response carries no pp_pending field; absent reads as not pending.
            Assert.That((bool?)submitted["pp_pending"] ?? false, Is.False, "and is not waiting to be");
            Assert.That((int)submitted["position"]!, Is.EqualTo(1));
        });

        var stored = await scoreAsync(scoreId);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Ranked, Is.True);
            Assert.That(stored.Pp, Is.Zero);
            Assert.That(stored.PpVersion, Is.EqualTo(PerformancePoints.VERSION), "settled, not left for the backfill");
        });

        var leaderboard = await getLeaderboardAsync(pricedLovedBeatmapId);
        var scores = (JArray)leaderboard["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["id"]!, Is.EqualTo(scoreId));
            Assert.That((bool)scores[0]["ranked"]!, Is.True);
            Assert.That(scores[0]["pp"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        });

        var (anonymous, _) = WebsiteFixture.CreateBrowser();
        using (anonymous)
        {
            string page = await anonymous.GetStringAsync($"/beatmapsets/{pricedLovedSetId}");
            string profile = await anonymous.GetStringAsync($"/users/{typistId}");
            string best = profileSection(profile, "best-scores");
            string recent = profileSection(profile, "recent-scores");

            Assert.Multiple(() =>
            {
                Assert.That(page, Does.Contain(typist_name), "a loved set renders its board");
                Assert.That(page, Does.Not.Contain("Not ranked yet"));

                // As on osu!, a loved play is not a "best performance": Best scores is the pp board's
                // view of the player, and loved earns none. It is still a real play, so Recent
                // lists it (which is what makes the Best assertion more than an empty profile).
                Assert.That(best, Does.Contain("Best scores"), "the section itself renders");
                Assert.That(best, Does.Not.Contain(priced_title), "a loved play is not a best score");
                Assert.That(recent, Does.Contain(priced_title));
            });
        }

        // A pp version bump (the row stamped stale) never prices a loved play.
        await executeAsync("UPDATE scores SET pp_version = 0 WHERE id = @scoreId", new { scoreId });

        await using (var backfillSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString))
            await PpBackfill.RunAsync(new Db(backfillSource), NullLogger.Instance, setId: pricedLovedSetId);

        var afterBackfill = await scoreAsync(scoreId);

        Assert.Multiple(() =>
        {
            Assert.That(afterBackfill.Pp, Is.Zero, "a loved row settles at 0 like an unranked one");
            Assert.That(afterBackfill.PpVersion, Is.EqualTo(PerformancePoints.VERSION));
        });

        // Unlove, then Rank: the rank carry reprices the set's loved-era rows.
        string? finalStatus;
        (bool Ranked, double Pp, int PpVersion) afterRank;

        try
        {
            using (var client = await signedInReviewerAsync())
            {
                using (await postHandlerAsync(client, "Unlove", pricedLovedSetId)) { }
                using (await postHandlerAsync(client, "Rank", pricedLovedSetId)) { }
            }

            finalStatus = await statusAsync(pricedLovedSetId);
            afterRank = await scoreAsync(scoreId);
        }
        finally
        {
            // Back to loved, so this typist's 1,000,000 never counts toward the shared database's
            // ranked-score and pp rankings (both 'ranked'-only), which other fixtures pin by rank.
            await executeAsync("UPDATE beatmapsets SET status = 'loved' WHERE id = @pricedLovedSetId", new { pricedLovedSetId });
        }

        Assert.Multiple(() =>
        {
            Assert.That(finalStatus, Is.EqualTo("ranked"));
            Assert.That(afterRank.Ranked, Is.True);
            Assert.That(afterRank.Pp, Is.GreaterThan(0), "ranking the set prices its loved-era plays");
        });
    }

    /// <summary>
    /// A row priced while its set was ranked keeps its stored pp through an Unrank and a Love
    /// (neither rewrites scores). It still sits on the loved board, but the game is told no pp:
    /// whether a board carries pp is the set's status, not the row's history.
    /// </summary>
    [Test]
    public async Task LovedBoard_SendsNoPp_EvenForARankedEraRowThatStillStoresSome()
    {
        var leaderboard = await getLeaderboardAsync(rankedEraLovedBeatmapId);
        var scores = (JArray)leaderboard["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That(scores[0]["pp"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
            Assert.That(leaderboard["user_score"]!["score"]!["pp"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        });
    }

    /// <summary>The in-browser player (PlayEndpoints) follows the same rule as the game client.</summary>
    [Test]
    public async Task BrowserPlay_OnALovedSet_IsOnTheRankedBoard_WithNoPpAndNothingPending()
    {
        const string name = "loved browser typist";
        const string password = "lovedbrowser-123456";
        await WebsiteFixture.SeedUserAsync(name, "loved.browser@example.com", password, verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, name, password)) { }

        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        var csrfMatch = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(csrfMatch.Success, Is.True, "the /play page must publish an antiforgery token");
        string csrf = csrfMatch.Groups[1].Value;

        long tokenId;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/play/token"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(new { setId = browserLovedSetId, beatmapId = browserLovedBeatmapId }), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play token");
            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        JObject submitted;
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
            submitted = JObject.Parse(await response.Content.ReadAsStringAsync());
        }

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True);
            Assert.That((string?)submitted["board"], Is.EqualTo("ranked"));
            Assert.That((int?)submitted["position"], Is.EqualTo(1));
            Assert.That(submitted["pp"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That((bool)submitted["pp_pending"]!, Is.False);
        });
    }

    /// <summary>
    /// One profile section's markup, from its <c>id</c> to its closing tag (the score sections nest
    /// no other section), so an assertion about Best cannot be satisfied by Recent or vice versa.
    /// </summary>
    private static string profileSection(string html, string id)
    {
        int start = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"the profile renders its {id} section");
        int end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    [Test]
    public async Task Reviewer_ReLoveAfterChangedGameplayMarksTheOldScoreClassic()
    {
        long setId, beatmapId;
        string checksum;
        await using (var source = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString))
        await using (var conn = await source.OpenConnectionAsync())
        {
            setId = await seedSetAsync(conn, "Loved Version Change", "loved", null, withRatings: true);
            (beatmapId, checksum) = await conn.QuerySingleAsync<(long, string)>(
                "SELECT id, checksum_md5 FROM beatmaps WHERE set_id = @setId", new { setId });
        }

        var submitted = await submitScoreAsync(beatmapId, checksum, totalScore: 1_000_000);
        long scoreId = (long)submitted["id"]!;

        // The persisted outcome of a gameplay-changing upload: the same difficulty gets new
        // bytes and the reviewed set returns to pending.
        await executeAsync("UPDATE beatmaps SET checksum_md5 = @checksum WHERE id = @beatmapId",
            new { beatmapId, checksum = Guid.NewGuid().ToString("N") });
        await executeAsync("UPDATE beatmapsets SET status = 'pending', current_version = 2 WHERE id = @setId",
            new { setId });
        using (var client = await signedInReviewerAsync())
        using (await postHandlerAsync(client, "Love", setId)) { }

        var leaderboard = await getLeaderboardAsync(beatmapId);
        var score = leaderboard["scores"]!.Single(s => (long)s["id"]! == scoreId);
        Assert.Multiple(() =>
        {
            Assert.That(score["mods"]!.Select(m => (string?)m["acronym"]), Does.Contain("CL"),
                "an old-version play must be identified before the board reopens");
            Assert.That((long)score["total_score"]!, Is.EqualTo(950_000),
                "the existing Classic policy applies the 0.95 score penalty");
        });
    }

    private static async Task executeAsync(string sql, object param)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync(sql, param);
    }

    private static async Task<(bool Ranked, double Pp, int PpVersion)> scoreAsync(long id)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.QuerySingleAsync<(bool, double, int)>(
            "SELECT ranked, pp, pp_version FROM scores WHERE id = @id", new { id });
    }

    /// <summary>
    /// The game's two-phase submission for a clean full-accuracy play (as RankedApprovalTest).
    /// 1,000,000 is the exact no-mod ceiling for 5/5 greats.
    /// </summary>
    private static async Task<JObject> submitScoreAsync(long beatmapId, string checksum, long totalScore)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "loved-test-build",
                ["beatmap_hash"] = checksum,
                ["ruleset_id"] = "0",
            });

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score token");

            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                passed = true,
                total_score = totalScore,
                total_score_without_mods = totalScore,
                accuracy = 1.0,
                max_combo = 5,
                ruleset_id = 0,
                rank = "X",
                statistics = new Dictionary<string, int> { ["great"] = 5 },
                maximum_statistics = new Dictionary<string, int> { ["great"] = 5 },
            }), Encoding.UTF8, "application/json");

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score submit");

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    private static async Task<JObject> getLeaderboardAsync(long beatmapId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "leaderboard");

        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }
}
