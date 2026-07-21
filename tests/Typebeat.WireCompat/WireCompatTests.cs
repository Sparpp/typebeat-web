using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;
using osu.Framework.Extensions;

namespace Typebeat.WireCompat;

/// <summary>
/// The wire-compat suite. Every test makes a REAL HTTP call through the in-process server and
/// deserializes the raw response body with the CLIENT'S OWN DTO types via
/// <c>JsonConvert.DeserializeObject&lt;T&gt;</c> (Newtonsoft default settings — the same primitive
/// the client's <c>OsuJsonWebRequest</c> uses). A server response the client types cannot parse
/// fails a test here, not in production.
/// </summary>
[TestFixture]
public class WireCompatTests
{
    private static HttpClient client => ServerFixture.Client;

    // ---------------------------------------------------------------------------------------------
    // (a) Register + oauth password grant → OAuthToken.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public async Task Register_Then_OAuthPasswordGrant_YieldsValidToken()
    {
        const string username = "wc_reg_user";
        const string email = "wc_reg_user@example.com";
        const string password = "sup3r-secret-pw";

        using (var reg = new HttpRequestMessage(HttpMethod.Post, "/users"))
        {
            reg.Headers.TryAddWithoutValidation("User-Agent", "type!beat");
            reg.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user[username]"] = username,
                ["user[user_email]"] = email,
                ["user[password]"] = password,
            });

            using var regResp = await client.SendAsync(reg);
            Assert.That(regResp.IsSuccessStatusCode, Is.True,
                $"registration should succeed (got {(int)regResp.StatusCode}: {await regResp.Content.ReadAsStringAsync()})");
        }

        using var tokenResp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "1",
            ["client_secret"] = "typebeat-official-client",
            ["scope"] = "*",
        }));

        Assert.That(tokenResp.IsSuccessStatusCode, Is.True, $"token grant status {(int)tokenResp.StatusCode}");

        string body = await tokenResp.Content.ReadAsStringAsync();
        var token = JsonConvert.DeserializeObject<OAuthToken>(body);

        Assert.That(token, Is.Not.Null);
        Assert.That(token!.IsValid, Is.True, "OAuthToken.IsValid must hold (non-empty access token, expires_in > 30)");
        Assert.That(token.RefreshToken, Is.Not.Null.And.Not.Empty, "refresh token must round-trip");
    }

    // ---------------------------------------------------------------------------------------------
    // (b) Duplicate-username registration → 422, parsed exactly as APIAccess.CreateAccount does.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public async Task DuplicateRegistration_Returns422_WithClientParseableFormError()
    {
        using var reg = new HttpRequestMessage(HttpMethod.Post, "/users");
        reg.Headers.TryAddWithoutValidation("User-Agent", "type!beat");
        reg.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            // Already seeded → uniqueness failure on the username field.
            ["user[username]"] = ServerFixture.PlayerUsername,
            ["user[user_email]"] = "wc_dup_unique@example.com",
            ["user[password]"] = "another-valid-pw",
        });

        using var resp = await client.SendAsync(reg);

        Assert.That((int)resp.StatusCode, Is.EqualTo(422), "duplicate registration must be HTTP 422");

        string body = await resp.Content.ReadAsStringAsync();

        // Mirror APIAccess.CreateAccount exactly.
        var errors = JObject.Parse(body)
                            .SelectToken("form_error", true)!
                            .ToObject<RegistrationRequest.RegistrationRequestErrors>();

        Assert.That(errors, Is.Not.Null);
        Assert.That(errors!.User, Is.Not.Null, "form_error.user must be present");
        Assert.That(errors.User!.Username, Is.Not.Empty, "username errors must be present");
        Assert.That(string.Join(" ", errors.User.Username).ToLowerInvariant(), Does.Contain("taken"));
    }

    // ---------------------------------------------------------------------------------------------
    // (c) GET /api/v2/me/ → APIMe. Statistics access (as at login) must not throw.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public async Task Me_DeserializesAsAPIMe_WithAccessibleStatistics()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, "/api/v2/me/");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"me status {(int)resp.StatusCode}: {body}");

        var me = JsonConvert.DeserializeObject<APIMe>(body);

        Assert.That(me, Is.Not.Null);
        Assert.That(me!.Id, Is.GreaterThan(0));
        Assert.That(me.Id, Is.EqualTo((int)ServerFixture.PlayerUserId));
        Assert.That(me.Username, Is.EqualTo(ServerFixture.PlayerUsername));

        // The exact dereferences the client performs on the login response.
        Assert.That(me.SessionVerificationMethod, Is.Null, "absent → keeps APIAccess on the Online path");
        Assert.DoesNotThrow(() =>
        {
            var stats = me.Statistics;                 // getter never returns null
            _ = stats.DisplayAccuracy;                 // computed from hit_accuracy
            _ = stats.GlobalRank;                      // nullable, server sends null
            _ = stats.PP;
            _ = stats.Level.Current;
        }, "reading the login statistics must not throw");
    }

    // ---------------------------------------------------------------------------------------------
    // (d) GET /api/v2/beatmaps/lookup?checksum=... → APIBeatmap.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public async Task BeatmapLookup_DeserializesAsAPIBeatmap_WithRankedStatusAndNestedSet()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/lookup?checksum={ServerFixture.SeedChecksum}");
        using var resp = await client.SendAsync(req);

        Assert.That(resp.IsSuccessStatusCode, Is.True, $"lookup status {(int)resp.StatusCode}");

        string body = await resp.Content.ReadAsStringAsync();
        var beatmap = JsonConvert.DeserializeObject<APIBeatmap>(body);

        Assert.That(beatmap, Is.Not.Null);
        Assert.That(beatmap!.OnlineID, Is.EqualTo((int)ServerFixture.SeededBeatmapId));
        Assert.That(beatmap.Checksum, Is.EqualTo(ServerFixture.SeedChecksum));
        Assert.That(beatmap.MD5Hash, Is.EqualTo(ServerFixture.SeedChecksum), "MD5Hash aliases Checksum");
        Assert.That(beatmap.Status, Is.EqualTo(BeatmapOnlineStatus.Ranked), "\"ranked\" must bind to the ranked enum member");
        Assert.That(beatmap.OnlineBeatmapSetID, Is.EqualTo((int)ServerFixture.SeededBeatmapSetId));
        Assert.That(beatmap.BeatmapSet, Is.Not.Null, "nested beatmapset must be populated");
    }

    // ---------------------------------------------------------------------------------------------
    // (e) Score loop: create token → backdate → submit SoloScoreInfo → MultiplayerScore.
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(1)]
    public async Task ScoreLoop_SubmitsSoloScore_AndParsesMultiplayerScore()
    {
        long beatmapId = ServerFixture.SeededBeatmapId;

        // POST the token.
        using var createReq = ServerFixture.Authed(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores");
        createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["version_hash"] = "0123456789abcdef0123456789abcdef",
            ["beatmap_hash"] = ServerFixture.SeedChecksum,
            ["ruleset_id"] = "0",
        });

        using var createResp = await client.SendAsync(createReq);
        Assert.That(createResp.IsSuccessStatusCode, Is.True, $"token create status {(int)createResp.StatusCode}");

        var token = JsonConvert.DeserializeObject<APIScoreToken>(await createResp.Content.ReadAsStringAsync());
        Assert.That(token, Is.Not.Null);
        Assert.That(token!.ID, Is.GreaterThan(0));

        // Backdate so the 90%-of-drain minimum-play-time gate clears (drain is 30s).
        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(db,
                "UPDATE score_tokens SET created_at = now() - interval '400 seconds' WHERE id = @id",
                new { id = token.ID });
        }

        // Build the SoloScoreInfo body the exact way SubmitScoreRequest serializes it.
        var score = new SoloScoreInfo
        {
            Passed = true,
            TotalScore = 400_000,
            TotalScoreWithoutMods = 400_000, // nomod: base == total (bounded against the provable ceiling)
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
            MaximumStatistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 10,
            },
        };

        string payload = JsonConvert.SerializeObject(score, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        });

        using var submitReq = ServerFixture.Authed(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{token.ID}");
        submitReq.Content = new StringContent(payload);
        submitReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var submitResp = await client.SendAsync(submitReq);
        Assert.That(submitResp.IsSuccessStatusCode, Is.True,
            $"submit status {(int)submitResp.StatusCode}: {await submitResp.Content.ReadAsStringAsync()}");

        var result = JsonConvert.DeserializeObject<MultiplayerScore>(await submitResp.Content.ReadAsStringAsync());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Position, Is.Not.Null, "a ranked submission must return a leaderboard position");
        Assert.That(result.TotalScore, Is.EqualTo(400_000), "submitted total score must echo back");

        // The enum-keyed dictionary round-trip — catches snake_case key drift.
        Assert.That(result.Statistics[HitResult.Great], Is.EqualTo(7));
        Assert.That(result.Statistics[HitResult.Ok], Is.EqualTo(1));
        Assert.That(result.Statistics[HitResult.Meh], Is.EqualTo(1));
        Assert.That(result.Statistics[HitResult.Miss], Is.EqualTo(1));
        Assert.That(result.MaximumStatistics[HitResult.Great], Is.EqualTo(10));

        // The submission must bump the denormalized play counters the website reads — the map and
        // its parent set should now show at least this play (the bug: they stayed 0 while scores
        // piled up on the leaderboard). They track the scores row count for the beatmap.
        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            var counts = await Dapper.SqlMapper.QuerySingleAsync<(int beatmapPlays, int setPlays, long scoreRows)>(db,
                """
                SELECT b.play_count AS beatmapPlays,
                       s.play_count AS setPlays,
                       (SELECT count(*) FROM scores WHERE beatmap_id = b.id) AS scoreRows
                FROM beatmaps b JOIN beatmapsets s ON s.id = b.set_id
                WHERE b.id = @id
                """,
                new { id = beatmapId });

            Assert.That(counts.beatmapPlays, Is.GreaterThan(0), "beatmaps.play_count must increment on submission");
            Assert.That(counts.beatmapPlays, Is.EqualTo(counts.scoreRows), "beatmaps.play_count must track submitted plays");
            Assert.That(counts.setPlays, Is.GreaterThanOrEqualTo(counts.beatmapPlays), "beatmapsets.play_count must include its beatmaps' plays");
        }
    }

    // A completed play with a NON-DEFAULT rate mod (DT at 1.01x) must submit successfully but store
    // unranked — mirroring the client's per-mod Ranked = SpeedChange.IsDefault. The multiplied total
    // is still accepted (bounded against the base score), it just never reaches the ranked board.
    [Test]
    [Order(50)]
    public async Task NonDefaultRateMod_SubmitsButIsUnranked()
    {
        long beatmapId = ServerFixture.SeededBeatmapId;

        using var createReq = ServerFixture.Authed(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores");
        createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["version_hash"] = "0123456789abcdef0123456789abcdef",
            ["beatmap_hash"] = ServerFixture.SeedChecksum,
            ["ruleset_id"] = "0",
        });
        using var createResp = await client.SendAsync(createReq);
        Assert.That(createResp.IsSuccessStatusCode, Is.True);
        var token = JsonConvert.DeserializeObject<APIScoreToken>(await createResp.Content.ReadAsStringAsync())!;

        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(db,
                "UPDATE score_tokens SET created_at = now() - interval '400 seconds' WHERE id = @id",
                new { id = token.ID });
        }

        var score = new SoloScoreInfo
        {
            Passed = true,
            TotalScore = 480_000,          // base × DoubleTime multiplier
            TotalScoreWithoutMods = 400_000, // base bounded against the provable ceiling
            Accuracy = 0.75,
            MaxCombo = 8,
            RulesetID = 0,
            Rank = ScoreRank.C,
            Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 7, [HitResult.Ok] = 1, [HitResult.Meh] = 1, [HitResult.Miss] = 1 },
            MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 10 },
            Mods = new[] { new APIMod { Acronym = "DT", Settings = new Dictionary<string, object> { ["speed_change"] = 1.01 } } },
        };

        string payload = JsonConvert.SerializeObject(score, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        using var submitReq = ServerFixture.Authed(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{token.ID}");
        submitReq.Content = new StringContent(payload);
        submitReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var submitResp = await client.SendAsync(submitReq);
        Assert.That(submitResp.IsSuccessStatusCode, Is.True, $"submit status {(int)submitResp.StatusCode}: {await submitResp.Content.ReadAsStringAsync()}");

        var result = JsonConvert.DeserializeObject<MultiplayerScore>(await submitResp.Content.ReadAsStringAsync())!;

        Assert.That(result.Position, Is.Null, "a non-default-rate play must be stored unranked (no leaderboard position)");

        await using (var verify = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await verify.OpenAsync();
            bool ranked = await Dapper.SqlMapper.ExecuteScalarAsync<bool>(verify,
                "SELECT ranked FROM scores WHERE id = @id", new { id = result.ID });
            Assert.That(ranked, Is.False, "scores.ranked must be false for a non-default-rate mod");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (f) GET /api/v2/beatmaps/{id}/scores → APIScoresCollection (runs after the score loop).
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(2)]
    public async Task Leaderboard_DeserializesAsScoresCollection_WithUserScorePosition()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/{ServerFixture.SeededBeatmapId}/scores");
        using var resp = await client.SendAsync(req);

        Assert.That(resp.IsSuccessStatusCode, Is.True, $"leaderboard status {(int)resp.StatusCode}");

        string body = await resp.Content.ReadAsStringAsync();
        var collection = JsonConvert.DeserializeObject<APIScoresCollection>(body);

        Assert.That(collection, Is.Not.Null);
        Assert.That(collection!.Scores, Is.Not.Null.And.Not.Empty, "leaderboard must contain the submitted score");
        Assert.That(collection.Scores[0].User, Is.Not.Null);
        Assert.That(collection.Scores[0].User!.Username, Is.EqualTo(ServerFixture.PlayerUsername));
        Assert.That(collection.UserScore, Is.Not.Null, "user_score must be present for the caller");
        Assert.That(collection.UserScore!.Position, Is.EqualTo(1));
    }

    // ---------------------------------------------------------------------------------------------
    // (f2) GET /api/v2/users/{id} → APIUser. The profile overlay's fetch; without it the client
    // spins forever (no Failure handler on GetUserRequest). Runs after the score loop so the
    // player has a ranked score to rank.
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(3)]
    public async Task GetUser_DeserializesAsAPIUser_WithRankedStatisticsAndPlaymode()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.PlayerUserId}?key=id");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"users status {(int)resp.StatusCode}: {body}");

        var user = JsonConvert.DeserializeObject<APIUser>(body);

        Assert.That(user, Is.Not.Null);
        Assert.That(user!.Id, Is.EqualTo((int)ServerFixture.PlayerUserId));
        Assert.That(user.Username, Is.EqualTo(ServerFixture.PlayerUsername));

        // playmode must be present + resolvable: UserProfileOverlay.userLoadComplete feeds it to
        // RulesetStore.GetRuleset(...).AsNonNull(), which NREs on a null/unknown ruleset.
        Assert.That(user.PlayMode, Is.EqualTo("typebeat"));

        Assert.DoesNotThrow(() =>
        {
            var stats = user.Statistics;
            _ = stats.DisplayAccuracy;
            _ = stats.Level.Current;
            _ = stats.GradesCount[ScoreRank.S];
        }, "reading the profile statistics must not throw");

        // The score loop submitted a single 400k play on the ranked seed map → global rank #1.
        Assert.That(user.Statistics.GlobalRank, Is.EqualTo(1));
        Assert.That(user.Statistics.RankedScore, Is.EqualTo(400_000));
    }

    // ---------------------------------------------------------------------------------------------
    // (g) MD5 identity: the client's ComputeMD5Hash over the fixture bytes == the stored checksum.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public void Md5Parity_ClientComputeMatchesStoredChecksum()
    {
        using var stream = new MemoryStream(ServerFixture.OsuFileBytes);

        // The exact primitive BeatmapImporter uses at import time (osu.Framework extension).
        string clientHash = stream.ComputeMD5Hash();

        Assert.That(clientHash, Is.EqualTo(ServerFixture.OsuFileChecksum),
            "client MD5-over-final-bytes must equal the server's stored checksum");
    }
}
