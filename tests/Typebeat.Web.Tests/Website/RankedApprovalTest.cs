using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The ranked-by-approval loop end to end, against a dedicated pending set (the shared
/// <see cref="PublicSiteSeed.PendingId"/> must STAY pending for the listing/set-page tests):
/// non-reviewers 404 on the Rank/Unrank POSTs; a score submitted while the set is pending
/// stores unranked and leaves the game-facing leaderboard empty; a reviewer's Rank flip makes
/// a NEW submission rank and appear (with a self-hosted absolute avatar_url); Unrank returns
/// the set to pending. Plus the website leaderboard section's ranked-only gate.
/// </summary>
[TestFixture]
[NonParallelizable]
public class RankedApprovalTest
{
    private const string reviewer_name = "map reviewer";
    private const string reviewer_password = "reviewpass-123456";

    private static long setId;
    private static long beatmapId;
    private static string checksum = null!;

    private static long typistId;
    private static string typistBearer = null!;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        string hash = new PasswordService().Hash(reviewer_password);

        await conn.ExecuteAsync(
            """
            INSERT INTO users (username, email, password_hash, country_code, map_reviewer)
            VALUES (@name, 'map.reviewer@example.com', @hash, 'US', true)
            """,
            new { name = reviewer_name, hash });

        typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('approval typist', 'approval.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        // A real bearer for the game-facing score endpoints (same monolith, same DB).
        typistBearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        // Past submitted_at keeps the seed's newest-sort assertions intact.
        setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Approval Candidate', 'The Reviewed', 'pending',
                    now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        // Zero drain length: the 90%-of-drain minimum-play-time gate clears instantly.
        checksum = Guid.NewGuid().ToString("N");

        beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    [Order(1)]
    public async Task RankPost_NonReviewer_Is404_AndChangesNothing()
    {
        // Signed in, but neither admin nor map_reviewer.
        using var client = await SignedInBrowserAsync(WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword);
        using var response = await PostHandlerAsync(client, "Rank");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Anonymous fares no better.
        var (anonymous, _) = WebsiteFixture.CreateBrowser();
        using var ___ = anonymous;
        using var anonymousResponse = await PostHandlerAsync(anonymous, "Rank");

        string? status = await StatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(status, Is.EqualTo("pending"));
        });
    }

    [Test]
    [Order(2)]
    public async Task ScoreOnPendingSet_StoresUnranked_AndShowsOnTheUnrankedBoard()
    {
        var submitted = await SubmitScoreAsync(totalScore: 1_000_000);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.False, "a pending set's play must store unranked");
            // No RANKED position: the submit response's position is the ranked board's, and the
            // caller has no ranked score here. The unranked board carries its own positions.
            Assert.That(submitted["position"]!.Type, Is.EqualTo(JTokenType.Null));
        });

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            bool ranked = await conn.ExecuteScalarAsync<bool>(
                "SELECT ranked FROM scores WHERE id = @id", new { id = (long)submitted["id"]! });
            Assert.That(ranked, Is.False);
        }

        // A pending set serves the website's UNRANKED board through the game-facing endpoint, so
        // the play that just stored unranked is visible (flagged ranked=false) rather than the map
        // reading as boardless. Nothing about it counts; the ranked board stays empty until review.
        var leaderboard = await GetLeaderboardAsync();
        var scores = (JArray)leaderboard["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)leaderboard["score_count"]!, Is.EqualTo(1), "a pending set serves its unranked board");
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(1_000_000));
            Assert.That((bool)scores[0]["ranked"]!, Is.False, "unranked-board rows are flagged as such on the wire");
            Assert.That((long)leaderboard["user_score"]!["score"]!["id"]!, Is.EqualTo((long)submitted["id"]!));
            Assert.That((int)leaderboard["user_score"]!["position"]!, Is.EqualTo(1));
        });
    }

    [Test]
    [Order(3)]
    public async Task ReviewerRank_FlipsToRanked_AndNewSubmissionAppears()
    {
        using var client = await SignedInBrowserAsync(reviewer_name, reviewer_password);

        // The reviewer sees the Rank control on the pending page (players don't).
        using (var page = await client.GetAsync($"/beatmapsets/{setId}"))
        {
            string html = await page.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("map review"));
                Assert.That(html, Does.Contain(">Rank this map</button>"));
            });
        }

        using (var response = await PostHandlerAsync(client, "Rank"))
        {
            string html = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo($"/beatmapsets/{setId}"),
                    "the POST redirects back to the set page");
                Assert.That(html, Does.Contain(">Ranked</span>"));
                Assert.That(html, Does.Contain(">Unrank</button>"), "a ranked set offers Unrank instead");
            });
        }

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            var (status, ageSeconds) = await conn.QuerySingleAsync<(string, double)>(
                "SELECT status, extract(epoch FROM (now() - updated_at)) FROM beatmapsets WHERE id = @setId",
                new { setId });

            Assert.Multiple(() =>
            {
                Assert.That(status, Is.EqualTo("ranked"));
                Assert.That(ageSeconds, Is.LessThan(60), "the rank flip must touch updated_at");
            });
        }

        // A NEW submission on the now-ranked set ranks and tops the (one-entry) leaderboard.
        var submitted = await SubmitScoreAsync(totalScore: 999_990);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True);
            Assert.That((int)submitted["position"]!, Is.EqualTo(1));
        });

        var leaderboard = await GetLeaderboardAsync();
        var scores = (JArray)leaderboard["scores"]!;

        Assert.Multiple(() =>
        {
            // Exactly the post-rank score: the pending-era submission stays buried.
            Assert.That((int)leaderboard["score_count"]!, Is.EqualTo(1));
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(999_990));
            Assert.That((long)scores[0]["user"]!["id"]!, Is.EqualTo(typistId));

            // avatar_url is never null (the client would fall back to a ppy CDN): the
            // self-hosted default, absolute on this host.
            Assert.That((string)scores[0]["user"]!["avatar_url"]!,
                Is.EqualTo("https://localhost/img/default-avatar.png"));
        });
    }

    [Test]
    [Order(4)]
    public async Task ReviewerUnrank_ReturnsToPending_AndBuriesTheRankedBoardAgain()
    {
        using var client = await SignedInBrowserAsync(reviewer_name, reviewer_password);
        using var response = await PostHandlerAsync(client, "Unrank");

        string html = await response.Content.ReadAsStringAsync();
        string? status = await StatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(status, Is.EqualTo("pending"));
            Assert.That(html, Does.Contain("Leaderboard unlocks when this map is ranked."));
            Assert.That(html, Does.Contain(">Rank this map</button>"));

            // The ranked-era score keeps its flag, but the set page must not render a podium
            // for a pending set; the note replaces the whole leaderboard body.
            Assert.That(html, Does.Not.Contain("podium"));
        });

        // The map is pending again, so the API is back to serving its UNRANKED board: the
        // pending-era 1,000,000 returns and the ranked-era 999,990 stays buried, exactly as the
        // website's own Unranked tab (ranked = false) would list it. The Rank/Unrank lever is
        // authoritative in both directions and neither board's rows ever cross onto the other.
        var leaderboard = await GetLeaderboardAsync();
        var scores = (JArray)leaderboard["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)leaderboard["score_count"]!, Is.EqualTo(1));
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(1_000_000), "the unranked board, not the buried ranked one");
            Assert.That((bool)scores[0]["ranked"]!, Is.False);
        });
    }

    [Test]
    public async Task SetPage_LeaderboardSection_RankedOnly()
    {
        // The seeded pending set: note instead of a board.
        using var pendingResponse = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.PendingId}");
        string pending = await pendingResponse.Content.ReadAsStringAsync();

        // A ranked set with scores: the board, no note.
        using var rankedResponse = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");
        string ranked = await rankedResponse.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pendingResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), "pending sets are publicly viewable");
            Assert.That(pending, Does.Contain(">Pending</span>"));
            Assert.That(pending, Does.Contain("Leaderboard unlocks when this map is ranked."));
            Assert.That(pending, Does.Not.Contain("podium"));

            Assert.That(ranked, Does.Contain("podium"));
            Assert.That(ranked, Does.Not.Contain("Leaderboard unlocks when this map is ranked."));
        });
    }

    // ---- helpers ----

    private static async Task<HttpClient> SignedInBrowserAsync(string username, string password)
    {
        // CreateBrowser already stamps each client with its own unique CF-Connecting-IP, so the
        // per-IP login limiter never collides across tests. Login is now a two-step flow
        // (password → emailed code → session).
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, username, password)) { }
        return client;
    }

    /// <summary>POSTs the Rank/Unrank page handler with a fresh antiforgery token.</summary>
    private static async Task<HttpResponseMessage> PostHandlerAsync(HttpClient client, string handler)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        return await client.PostAsync($"/beatmapsets/{setId}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));
    }

    private static async Task<string?> StatusAsync()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM beatmapsets WHERE id = @setId", new { setId });
    }

    /// <summary>
    /// Runs the client's two-phase submission (token POST, then the SoloScoreInfo PUT) for a
    /// clean full-accuracy play and returns the MultiplayerScore response body. 1,000,000 is
    /// the exact no-mod ceiling for 5/5 greats, so anything at or below it passes the bounds.
    /// </summary>
    private static async Task<JObject> SubmitScoreAsync(long totalScore)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "approval-test-build",
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
                total_score_without_mods = totalScore, // nomod: base == total (bounded vs the nomod ceiling)
                accuracy = 1.0,
                max_combo = 5,
                ruleset_id = 0,
                rank = "X",
                statistics = new Dictionary<string, int> { ["great"] = 5 },
                maximum_statistics = new Dictionary<string, int> { ["great"] = 5 },
            }), System.Text.Encoding.UTF8, "application/json");

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score submit");

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    private static async Task<JObject> GetLeaderboardAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "leaderboard");

        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }
}
