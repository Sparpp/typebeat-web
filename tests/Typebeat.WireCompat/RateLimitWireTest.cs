using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 366 on the game's wire: a rate-limited game route answers 429 with a body the
/// client's own error path turns into the player's message (never 401/403, which log the game
/// out), the refresh grant is never limited (a 429 there also logs the game out), and the
/// leaderboard's memoised shared slice never leaks one caller's user_score to another and is
/// dropped by the next submit.
/// </summary>
[TestFixture]
public class RateLimitWireTest
{
    private static HttpClient client => ServerFixture.Client;

    [Test]
    public async Task ScoreToken_OverBudget_Is429_WithAnErrorTheGameShowsThePlayer()
    {
        // A token of its own: bearer budgets are keyed by token, and the shared one is the suite's.
        var (access, _) = await ServerFixture.IssueTokenAsync(ServerFixture.PlayerUserId);

        // ruleset_id=1 is refused with a cheap 422 after the limiter has counted the request.
        for (int i = 0; i < 30; i++)
        {
            using var ok = await createTokenAsync(access, ruleset: "1");
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), $"request {i + 1} is inside the budget");
        }

        using var limited = await createTokenAsync(access, ruleset: "1");
        string body = await limited.Content.ReadAsStringAsync();

        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(limited.Headers.RetryAfter, Is.Not.Null, "a 429 says when to come back");
        Assert.That(limited.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));

        // The exact decode APIRequest.Fail runs on an error body (its private DisplayableError).
        Assert.That(body, Does.Contain("\"error\""), "APIRequest only decodes bodies that contain \"error\"");
        var displayable = typeof(APIRequest).GetNestedType("DisplayableError", BindingFlags.NonPublic)!;
        object? decoded = JsonConvert.DeserializeObject(body, displayable);
        string? message = (string?)displayable.GetProperty("ErrorMessage")!.GetValue(decoded);

        Assert.That(message, Is.EqualTo(Typebeat.Web.Auth.RateLimits.RejectionMessage));
    }

    [Test]
    public async Task RefreshGrant_IsNeverLimited()
    {
        var (_, refresh) = await ServerFixture.IssueTokenAsync(ServerFixture.PlayerUserId);

        // Above the password grant's 10 per 5 minutes: the refresh grant shares no budget with it.
        for (int i = 0; i < 15; i++)
        {
            using var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = "1",
                ["client_secret"] = "typebeat-official-client",
                ["scope"] = "*",
            }));

            string body = await resp.Content.ReadAsStringAsync();
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"refresh {i + 1}: {body}");

            var token = JsonConvert.DeserializeObject<OAuthToken>(body)!;
            Assert.That(token.IsValid, Is.True);
            refresh = token.RefreshToken;
        }
    }

    [Test]
    public async Task Leaderboard_SharedSliceIsMemoised_ButUserScoreStaysPersonal_AndASubmitShowsAtOnce()
    {
        long beatmapId = ServerFixture.SeededBeatmapId;

        var (alice, _) = await ServerFixture.IssueTokenAsync(await insertUserAsync("wc_rl_alice"));
        var (bob, _) = await ServerFixture.IssueTokenAsync(await insertUserAsync("wc_rl_bob"));

        // Below the fixture player's 400,000 so the suite's own position-1 pins are untouched.
        long aliceScore = await submitAsync(alice, beatmapId, 300_000);

        var aliceFirst = await boardAsync(alice, beatmapId);
        var bobFirst = await boardAsync(bob, beatmapId); // inside the 5 s memo of alice's read

        long bobScore = await submitAsync(bob, beatmapId, 290_000);

        var aliceAfter = await boardAsync(alice, beatmapId); // still inside 5 s: only eviction explains a change
        var bobAfter = await boardAsync(bob, beatmapId);

        Assert.Multiple(() =>
        {
            Assert.That(aliceFirst.UserScore?.Score.ID, Is.EqualTo((ulong)aliceScore), "alice sees her own row");
            Assert.That(bobFirst.UserScore, Is.Null, "a memo hit must not hand bob alice's user_score");
            Assert.That(bobFirst.Scores.Select(s => s.ID), Does.Contain((ulong)aliceScore));

            Assert.That(aliceAfter.Scores.Select(s => s.ID), Does.Contain((ulong)bobScore), "bob's submit evicted the memo");
            Assert.That(aliceAfter.ScoresCount, Is.EqualTo(aliceFirst.ScoresCount + 1));
            Assert.That(aliceAfter.UserScore?.Score.ID, Is.EqualTo((ulong)aliceScore));
            Assert.That(bobAfter.UserScore?.Score.ID, Is.EqualTo((ulong)bobScore), "bob now sees his own row");
        });
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> createTokenAsync(string access, string ruleset)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{ServerFixture.SeededBeatmapId}/solo/scores");
        req.Headers.Add("Authorization", $"Bearer {access}");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["version_hash"] = "0123456789abcdef0123456789abcdef",
            ["beatmap_hash"] = ServerFixture.SeedChecksum,
            ["ruleset_id"] = ruleset,
        });
        return client.SendAsync(req);
    }

    private static async Task<long> insertUserAsync(string username)
    {
        await using var conn = new NpgsqlConnection(ServerFixture.ConnectionString);
        await conn.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username + "@example.com" });

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id });
        return id;
    }

    /// <summary>The game's token-then-submit loop (as WireCompatTests.ScoreLoop), returning the stored score id.</summary>
    private static async Task<long> submitAsync(string access, long beatmapId, long totalScore)
    {
        using var createResp = await createTokenAsync(access, ruleset: "0");
        Assert.That(createResp.IsSuccessStatusCode, Is.True, $"token create status {(int)createResp.StatusCode}");
        var token = JsonConvert.DeserializeObject<APIScoreToken>(await createResp.Content.ReadAsStringAsync())!;

        await using (var conn = new NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE score_tokens SET created_at = now() - interval '400 seconds' WHERE id = @id", new { id = token.ID });
        }

        var score = new SoloScoreInfo
        {
            Passed = true,
            TotalScore = totalScore,
            TotalScoreWithoutMods = totalScore,
            Accuracy = 0.75,
            MaxCombo = 8,
            RulesetID = 0,
            Rank = ScoreRank.C,
            Statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 7,
                [HitResult.Ok] = 1,
                [HitResult.Meh] = 1,
                [HitResult.Miss] = 1,
            },
            MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 10 },
        };

        using var submit = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{token.ID}");
        submit.Headers.Add("Authorization", $"Bearer {access}");
        submit.Content = new StringContent(JsonConvert.SerializeObject(score, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }));
        submit.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var submitResp = await client.SendAsync(submit);
        string body = await submitResp.Content.ReadAsStringAsync();
        Assert.That(submitResp.IsSuccessStatusCode, Is.True, $"submit status {(int)submitResp.StatusCode}: {body}");

        return (long)JsonConvert.DeserializeObject<MultiplayerScore>(body)!.ID;
    }

    private static async Task<APIScoresCollection> boardAsync(string access, long beatmapId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        req.Headers.Add("Authorization", $"Bearer {access}");
        using var resp = await client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
        Assert.That(resp.Headers.CacheControl?.NoStore, Is.True, "a bearer response is private, no-store");
        return JsonConvert.DeserializeObject<APIScoresCollection>(body)!;
    }
}
