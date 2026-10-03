using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Newtonsoft.Json.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;
using osu.Framework.Extensions;

// The server's two ranking metrics, aliased so the client's own Online/Scoring namespaces above
// can't collide with them.
using GlobalRanking = Typebeat.Web.Scoring.GlobalRanking;
using PpRanking = Typebeat.Web.Scoring.PpRanking;

namespace Typebeat.WireCompat;

/// <summary>
/// The wire-compat suite. Every test makes a REAL HTTP call through the in-process server and
/// deserializes the raw response body with the CLIENT'S OWN DTO types via
/// <c>JsonConvert.DeserializeObject&lt;T&gt;</c> (Newtonsoft default settings, the same primitive
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
            _ = stats.GlobalRank;                      // nullable: null while the player is unranked
            _ = stats.PP;
            _ = stats.Level.Current;
        }, "reading the login statistics must not throw");

        // The login payload carries the REAL statistics, the same ones the profile fetch serves
        // (UserStatisticsWire), not a zeroed placeholder: this response is what lands in the
        // client's api.LocalUser for the whole session, so a stand-in there is a wrong number that
        // nothing ever corrects. Asserted against the DB rather than a literal so the test does not
        // depend on how many scores other tests have submitted by now.
        await using var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString);
        await db.OpenAsync();

        var performance = await PpRanking.ForUserAsync(db, ServerFixture.PlayerUserId);

        Assert.That(me.Statistics.PP, Is.Not.Null);
        Assert.That((double)me.Statistics.PP!.Value,
            Is.EqualTo(Math.Round(performance.TotalPp, 2, MidpointRounding.AwayFromZero)).Within(1e-9),
            "the login payload's pp must be the same total the profile fetch serves");
        Assert.That(me.Statistics.GlobalRank, Is.EqualTo(performance.GlobalRank is { } r ? (int?)(int)r : null),
            "and its global_rank the same pp rank");
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

    // (d2) The set's song language rides the lookup as `song_language` (backlog 373): the server writes
    // its canonical BeatmapLanguages name, the client reads it through APIBeatmapSet.SongLanguage and
    // decodes it with BeatmapLanguageExtensions.FromCanonicalName, which is what fills an Unspecified
    // realm row for a map whose .osu carries no Language: line. Both directions are pinned here, and
    // the empty case, which the client must read as Unspecified rather than fail on. One set per
    // language rather than one set rewritten in place: a FOUND lookup row is memoised for
    // CacheEviction.LookupMemoTtl (backlog 366), so a rewrite would read back the first answer.
    [Test]
    public async Task BeatmapLookup_CarriesTheSetsSongLanguage_InTheClientsCanonicalForm()
    {
        await using var conn = new NpgsqlConnection(ServerFixture.ConnectionString);

        var cases = Enum.GetValues<BeatmapLanguage>().Where(l => l != BeatmapLanguage.Unspecified)
                        .Select(l => (language: l, canonical: l.ToCanonicalName()))
                        .Append((language: BeatmapLanguage.Unspecified, canonical: string.Empty))
                        .ToList();

        for (int i = 0; i < cases.Count; i++)
        {
            var (language, canonical) = cases[i];
            string checksum = $"5a{i:x2}".PadRight(32, 'f');

            long setId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmapsets (owner_id, title, artist, status, language)
                VALUES (@ownerId, 'Wire Compat Language', 'Harness', 'ranked', @language)
                RETURNING id
                """,
                new { ownerId = ServerFixture.OwnerUserId, language = canonical });

            await conn.ExecuteAsync(
                """
                INSERT INTO beatmaps (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
                VALUES (@setId, 'type!beat', 0, @checksum, 60, 30, 1.5)
                """,
                new { setId, checksum });

            using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/lookup?checksum={checksum}");
            using var resp = await client.SendAsync(req);

            Assert.That(resp.IsSuccessStatusCode, Is.True, $"lookup status {(int)resp.StatusCode}");

            var beatmap = JsonConvert.DeserializeObject<APIBeatmap>(await resp.Content.ReadAsStringAsync());
            Assert.That(beatmap?.BeatmapSet, Is.Not.Null);

            Assert.That(beatmap!.BeatmapSet!.SongLanguage, Is.EqualTo(canonical),
                language == BeatmapLanguage.Unspecified ? "a set with no language sends the empty string, never null" : $"{language} must travel as its canonical name");
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName(beatmap.BeatmapSet.SongLanguage), Is.EqualTo(language), $"{language} must decode back on the client");
        }
    }

    // (d3) The set-level vocals-stem flag rides the lookup as `has_vocals_stem` (backlog 396): the server
    // says whether the set's CURRENT version carries vocals.ogg/vocals.wav, and the client binds it to
    // APIBeatmapSet.HasVocalsStem, which the update-availability predicate reads against the local copy's
    // stem presence. Both directions pinned here, and the false case (an ordinary set).
    [Test]
    public async Task BeatmapLookup_CarriesTheSetsVocalsStemFlag_InTheClientsCanonicalForm()
    {
        await using var conn = new NpgsqlConnection(ServerFixture.ConnectionString);

        // A set whose current version carries the stem: a stem-less version 1 and a version 2 that adds
        // vocals.ogg (the backfill's exact shape). A brand-new checksum, so the lookup memo cannot answer.
        long stemSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, 'Wire Compat Stem', 'Harness', 'ranked')
            RETURNING id
            """,
            new { ownerId = ServerFixture.OwnerUserId });

        byte[] stemSha = System.Security.Cryptography.SHA256.HashData("wire-compat-stem"u8.ToArray());
        await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, 4) ON CONFLICT DO NOTHING", new { sha = stemSha });

        long stemVersion = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO set_versions (set_id, version_no) VALUES (@setId, 1) RETURNING id", new { setId = stemSetId });
        await conn.ExecuteAsync(
            "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'vocals.ogg')",
            new { versionId = stemVersion, sha = stemSha });
        await conn.ExecuteAsync("UPDATE beatmapsets SET current_version = 1 WHERE id = @setId", new { setId = stemSetId });

        string stemChecksum = "5b0e" + Guid.NewGuid().ToString("N")[..28];
        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
            VALUES (@setId, 'type!beat', 0, @checksum, 60, 30, 1.5)
            """,
            new { setId = stemSetId, checksum = stemChecksum });

        // An ordinary set: its current version's manifest carries no stem, so the flag is false.
        string plainChecksum = "5b0f" + Guid.NewGuid().ToString("N")[..28];
        long plainSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, 'Wire Compat No Stem', 'Harness', 'ranked')
            RETURNING id
            """,
            new { ownerId = ServerFixture.OwnerUserId });
        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
            VALUES (@setId, 'type!beat', 0, @checksum, 60, 30, 1.5)
            """,
            new { setId = plainSetId, checksum = plainChecksum });

        using (var stemReq = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/lookup?checksum={stemChecksum}"))
        using (var stemResp = await client.SendAsync(stemReq))
        {
            var stem = JsonConvert.DeserializeObject<APIBeatmap>(await stemResp.Content.ReadAsStringAsync());

            Assert.That(stem?.BeatmapSet, Is.Not.Null);
            Assert.That(stem!.BeatmapSet!.HasVocalsStem, Is.True, "a backfilled set reports its stem through the client DTO");
        }

        using (var plainReq = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/lookup?checksum={plainChecksum}"))
        using (var plainResp = await client.SendAsync(plainReq))
        {
            var plain = JsonConvert.DeserializeObject<APIBeatmap>(await plainResp.Content.ReadAsStringAsync());

            Assert.That(plain?.BeatmapSet, Is.Not.Null);
            Assert.That(plain!.BeatmapSet!.HasVocalsStem, Is.False, "an ordinary set reports false");
        }
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

        // The enum-keyed dictionary round-trip; catches snake_case key drift.
        Assert.That(result.Statistics[HitResult.Great], Is.EqualTo(7));
        Assert.That(result.Statistics[HitResult.Ok], Is.EqualTo(1));
        Assert.That(result.Statistics[HitResult.Meh], Is.EqualTo(1));
        Assert.That(result.Statistics[HitResult.Miss], Is.EqualTo(1));
        Assert.That(result.MaximumStatistics[HitResult.Great], Is.EqualTo(10));

        // The submission must bump the denormalized play counters the website reads; the map and
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

        // The play's pp reaches the client through MultiplayerScore.PP, which SubmittingPlayer
        // copies onto ScoreInfo.PP and the results screen shows in preference to its own local
        // calculation (backlog 75). Pinned against the value the server actually STORED, since the
        // two must be the same number: the results screen would otherwise print something the
        // leaderboards disagree with.
        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            double stored = await Dapper.SqlMapper.ExecuteScalarAsync<double>(db,
                "SELECT pp FROM scores WHERE id = @id", new { id = result.ID });

            Assert.That(result.PP, Is.Not.Null, "a priced play must not read as unpriced on the wire");
            Assert.That(result.PP!.Value, Is.EqualTo(stored).Within(1e-9));
            Assert.That(stored, Is.GreaterThan(0), "the fixture play is ranked on a ranked map, so it earns pp");
        }
    }

    // A completed play with a NON-DEFAULT rate mod (DT at 1.01x) must submit, RANK, and keep its
    // rate. type!beat ranks the rate mods at every speed and pays them on a continuous curve
    // (TypeBeatRateMultiplier), so the client's per-mod Ranked is true regardless of the slider and
    // the wire always carries settings.speed_change. The server must therefore:
    //   - rank the play (this pin was the exact inverse before task 27),
    //   - price it at that rate: 400,000 × For(1.01) = 400,000 × 1.0046 = 401,840,
    //   - persist the rate, or a "DT" on a board is ambiguous between 1.01x and 2.00x.
    [Test]
    [Order(50)]
    public async Task RateModAtAnyRate_IsRanked_AndKeepsItsRate()
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
            TotalScore = 401_840,          // base × the exact 1.01x rate multiplier (1.0046)
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

        Assert.That(result.Position, Is.Not.Null, "a rate-mod play is ranked at every speed, so it takes a leaderboard position");
        Assert.That(result.TotalScore, Is.EqualTo(401_840), "the rate-priced total must survive the bounds check unclamped");

        await using (var verify = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await verify.OpenAsync();
            bool ranked = await Dapper.SqlMapper.ExecuteScalarAsync<bool>(verify,
                "SELECT ranked FROM scores WHERE id = @id", new { id = result.ID });
            Assert.That(ranked, Is.True, "scores.ranked must be true for a rate mod at any speed");

            // The rate is score-affecting data, so it is persisted in the osu APIMod shape the
            // client's own ModIcon strip reads back off the leaderboard.
            string storedMods = await Dapper.SqlMapper.ExecuteScalarAsync<string>(verify,
                "SELECT mods::text FROM scores WHERE id = @id", new { id = result.ID }) ?? "[]";

            Assert.That(JArray.Parse(storedMods).ToString(Formatting.None),
                Is.EqualTo("""[{"acronym":"DT","settings":{"speed_change":1.01}}]"""),
                "the submitted rate must be stored, clamped to the slider range and stripped of every other setting");
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

        // has_replay (additive, backlog 37) must be EMITTED, not merely absent-and-defaulted: the
        // client binds it to SoloScoreInfo.HasReplay and from there to ScoreInfo.HasOnlineReplay,
        // which is the only thing that offers a replay on a leaderboard row. This loop never
        // uploads one, so the value is false; the presence of the key is what is pinned here.
        var raw = JObject.Parse(body);
        Assert.That(raw["scores"]![0]!["has_replay"], Is.Not.Null, "leaderboard rows must carry has_replay");
        Assert.That(collection.Scores[0].HasReplay, Is.False);
        Assert.That(collection.UserScore.Score.HasReplay, Is.False);
    }

    // ---------------------------------------------------------------------------------------------
    // (f3) The unranked-board path end to end (backlog 53): lookup reports "pending", the client's
    // own board decision turns that into GlobalLeaderboardKind.Unranked (so it fetches rather than
    // showing "leaderboards are not available"), and the board that comes back is the website's
    // unranked board, deserialised through APIScoresCollection with ranked = false on the row.
    //
    // This is the cross-repo pin: the server's status STRING, the client's status ENUM and the
    // client's board decision all have to agree, or the tab silently goes blank again.
    // ---------------------------------------------------------------------------------------------
    [Test]
    public async Task PendingMap_LookupSaysPending_AndItsBoardDeserialisesAsUnranked()
    {
        using (var lookup = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/lookup?id={ServerFixture.PendingBeatmapId}"))
        using (var lookupResp = await client.SendAsync(lookup))
        {
            Assert.That(lookupResp.IsSuccessStatusCode, Is.True, $"lookup status {(int)lookupResp.StatusCode}");

            var beatmap = JsonConvert.DeserializeObject<APIBeatmap>(await lookupResp.Content.ReadAsStringAsync());

            Assert.That(beatmap, Is.Not.Null);
            Assert.That(beatmap!.Status, Is.EqualTo(BeatmapOnlineStatus.Pending), "\"pending\" must bind to the pending enum member");
            Assert.That(GlobalLeaderboardAvailability.Resolve(beatmap.OnlineID, beatmap.Status),
                Is.EqualTo(GlobalLeaderboardKind.Unranked),
                "a pending map must resolve to the unranked board, not to no board at all");
        }

        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/beatmaps/{ServerFixture.PendingBeatmapId}/scores");
        using var resp = await client.SendAsync(req);

        Assert.That(resp.IsSuccessStatusCode, Is.True, $"leaderboard status {(int)resp.StatusCode}");

        string body = await resp.Content.ReadAsStringAsync();
        var collection = JsonConvert.DeserializeObject<APIScoresCollection>(body);

        Assert.That(collection, Is.Not.Null);
        Assert.That(collection!.ScoresCount, Is.EqualTo(1));
        Assert.That(collection.Scores, Has.Count.EqualTo(1), "a pending map now serves its unranked board");
        Assert.That(collection.Scores[0].TotalScore, Is.EqualTo(640000));
        Assert.That(collection.Scores[0].Ranked, Is.False, "unranked-board rows must bind to SoloScoreInfo.Ranked = false");
        Assert.That(collection.UserScore, Is.Not.Null, "the caller's own unranked play still gets a user_score");
        Assert.That(collection.UserScore!.Position, Is.EqualTo(1), "positioned within the unranked board");
    }

    // ---------------------------------------------------------------------------------------------
    // (f2) GET /api/v2/users/{id} → APIUser. The profile overlay's fetch, and the one
    // LocalUserStatisticsProvider polls for the local player; without it the client spins forever
    // (no Failure handler on GetUserRequest). Runs after the score loop so the player has a ranked
    // score to rank.
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(3)]
    public async Task GetUser_DeserializesAsAPIUser_WithRankedStatisticsAndPlaymode()
    {
        // A rival that outranks the player on CUMULATIVE SCORE while losing to them on pp: a huge
        // total_score carrying a token 0.01pp. Without it the two boards would agree on #1 and the
        // global_rank assertion below would prove nothing. Its map lives on its own ranked set so no
        // other test's leaderboard, position or grade count moves.
        await seedScoreRivalAsync();

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

        // The score loop submitted a single 400k play on the ranked seed map.
        Assert.That(user.Statistics.RankedScore, Is.EqualTo(400_000));

        await using var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString);
        await db.OpenAsync();

        var performance = await PpRanking.ForUserAsync(db, ServerFixture.PlayerUserId);
        var cumulative = await GlobalRanking.ForUserAsync(db, ServerFixture.PlayerUserId);

        Assert.That(cumulative.GlobalRank, Is.EqualTo(2), "the rival must outrank the player on cumulative score");
        Assert.That(performance.GlobalRank, Is.EqualTo(1), "and lose to them on pp");

        // THE call this task made: the client's single rank slot carries the pp rank, not the
        // cumulative-score rank. Every client surface reading global_rank pairs it with pp (profile
        // header, results-screen Overall Ranking, the toolbar delta), and the website has led with
        // pp since task 61. The score metric is still on the wire as ranked_score, above.
        Assert.That(user.Statistics.GlobalRank, Is.EqualTo(1),
            "global_rank must be the pp rank; #2 would mean the cumulative-score rank leaked into it");
        Assert.That(user.Statistics.IsRanked, Is.True, "is_ranked must track the rank it is sent with");

        // Total pp: the real weighted aggregate, at the 2dp the wire serves it.
        Assert.That(user.Statistics.PP, Is.Not.Null, "pp must never be null; a player with none is sent 0");
        Assert.That((double)user.Statistics.PP!.Value,
            Is.EqualTo(Math.Round(performance.TotalPp, 2, MidpointRounding.AwayFromZero)).Within(1e-9));
        Assert.That(performance.TotalPp, Is.GreaterThan(0), "the fixture play is ranked on a ranked map, so it earns pp");
    }

    // ---------------------------------------------------------------------------------------------
    // (f3) A user who has never earned pp: the brand-new-account case. pp is 0 (a number, not null:
    // the toolbar's delta display falls back to `Before.PP ?? After.PP`, so a null "before" would
    // render a player's very first pp as a gain of nothing), while global_rank is null, which is the
    // client's existing convention for unranked (a dash in GlobalRankDisplay, a hidden counter in
    // the toolbar). Uses the seeded set owner, who has no scores at all.
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(4)]
    public async Task GetUser_WithNoPerformancePoints_SendsZeroPpAndNullRank()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.OwnerUserId}?key=id");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"users status {(int)resp.StatusCode}: {body}");

        var user = JsonConvert.DeserializeObject<APIUser>(body);

        Assert.That(user, Is.Not.Null);
        Assert.That(user!.Statistics.PP, Is.EqualTo(0m), "no pp-earning play is worth exactly 0, not unknown");
        Assert.That(user.Statistics.GlobalRank, Is.Null, "unranked stays null rather than becoming rank #0");
        Assert.That(user.Statistics.IsRanked, Is.False);
    }

    /// <summary>
    /// Inserts a second ranked set + map + user carrying one enormous-score, near-zero-pp play, so
    /// the cumulative-score board and the pp board disagree about who is first.
    /// </summary>
    private static async Task seedScoreRivalAsync()
    {
        await using var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString);
        await db.OpenAsync();

        long setId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, 'Wire Compat Rival', 'Harness', 'ranked')
            RETURNING id
            """,
            new { ownerId = ServerFixture.OwnerUserId });

        long beatmapId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
            """
            INSERT INTO beatmaps
                (set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s, difficulty_rating)
            VALUES (@setId, 'type!beat', 0, '33333333333333333333333333333333', 60, 30, 1.5)
            RETURNING id
            """,
            new { setId });

        long rivalId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('wc_rival', 'wc_rival@example.com', 'x', 'US')
            RETURNING id
            """);

        await Dapper.SqlMapper.ExecuteAsync(db,
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp)
            VALUES
                (@rivalId, @beatmapId, 5000000, 0.99, 1.0, 400, 'X', true, true,
                 '[]'::jsonb, '{"great":400}'::jsonb, '{"great":400}'::jsonb, 0.01)
            """,
            new { rivalId, beatmapId });
    }

    // ---------------------------------------------------------------------------------------------
    // (f4) The profile overlay's SECTION fetches (task 83). Every one of these routes was missing
    // before, which is why the five sections were switched off; the two that are back on drive
    // GET users/{id}/scores/{pinned,best,firsts,recent} and users/{id}/beatmapsets/most_played.
    // Each must come back as a JSON ARRAY that the client's own list element type can parse.
    // ---------------------------------------------------------------------------------------------
    [Test]
    [Order(5)]
    public async Task ProfileScoreSections_DeserializeAsSoloScoreInfo_WithBeatmapMetadataAndPp()
    {
        // Pin the player's ranked play so the Pinned subsection has a row to serve. Written
        // directly rather than through the website's POST handler: this harness is the CLIENT's
        // contract, and the pin is fixture data, not the thing under test.
        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(db,
                """
                INSERT INTO score_pins (score_id, user_id)
                SELECT s.id, s.user_id FROM scores s
                WHERE s.user_id = @userId AND s.ranked
                ORDER BY s.id
                LIMIT 1
                ON CONFLICT DO NOTHING
                """,
                new { userId = ServerFixture.PlayerUserId });
        }

        foreach (string type in new[] { "best", "firsts", "recent", "pinned" })
        {
            var scores = await getScoresAsync(type);

            Assert.That(scores, Is.Not.Null, $"{type} must deserialize as a score list");
            Assert.That(scores!, Is.Not.Empty, $"the seeded player has a ranked play, so {type} must not be empty");

            var score = scores[0];

            // DrawableProfileScore does Score.Beatmap.AsNonNull() and then reads Metadata off it,
            // which resolves through the NESTED beatmapset. A payload without it is not an empty
            // row, it is a crash on every profile that opens.
            Assert.That(score.Beatmap, Is.Not.Null, $"{type} rows must carry their beatmap");
            Assert.That(score.Beatmap!.BeatmapSet, Is.Not.Null, $"{type} rows must carry the nested beatmapset");
            Assert.That(score.Beatmap.Metadata.Title, Is.EqualTo("Wire Compat"));
            Assert.That(score.Beatmap.Metadata.Artist, Is.EqualTo("Harness"));
            Assert.That(score.Beatmap.DifficultyName, Is.EqualTo("type!beat"));

            // The pp column only renders at all when the map's status grants pp AND the score is
            // ranked AND preserve is set AND it is not "processed with no pp". All four are the
            // server's to state, and three of them default to the value that HIDES pp.
            Assert.That(score.Beatmap.Status, Is.EqualTo(BeatmapOnlineStatus.Ranked));
            Assert.That(score.Ranked, Is.True);
            Assert.That(score.Preserve, Is.True, "preserve defaults to false, which would suppress pp on every row");
            Assert.That(score.Processed, Is.True);
            Assert.That(score.PP, Is.Not.Null.And.GreaterThan(0), "the seeded play is ranked on a ranked map");

            Assert.That(score.TotalScore, Is.EqualTo(400_000));
            Assert.That(score.UserID, Is.EqualTo((int)ServerFixture.PlayerUserId));
            Assert.That(score.EndedAt, Is.Not.EqualTo(default(DateTimeOffset)), "the date line renders from ended_at");
        }
    }

    [Test]
    [Order(6)]
    public async Task ProfileMostPlayed_DeserializesAsAPIUserMostPlayedBeatmap_WithSiblingSet()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get,
            $"/api/v2/users/{ServerFixture.PlayerUserId}/beatmapsets/most_played?offset=0&limit=51");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"most_played status {(int)resp.StatusCode}: {body}");

        var rows = JsonConvert.DeserializeObject<List<APIUserMostPlayedBeatmap>>(body);

        Assert.That(rows, Is.Not.Null);
        // The ranked seed map and the pending one: most played counts EVERY play, ranked or not.
        Assert.That(rows!, Is.Not.Empty);

        var top = rows[0];

        Assert.That(top.PlayCount, Is.GreaterThan(0));
        Assert.That(top.BeatmapSet, Is.Not.Null, "the set must be a SIBLING key, not only nested");

        // BeatmapInfo's getter reassigns beatmap.BeatmapSet from the sibling every read, so this
        // is where a nested-only payload would silently null the set back out.
        Assert.That(top.BeatmapInfo, Is.Not.Null);
        Assert.That(top.BeatmapInfo.BeatmapSet, Is.Not.Null);
        Assert.That(top.BeatmapInfo.Metadata.Title, Is.Not.Empty);
        Assert.That(top.BeatmapInfo.DifficultyName, Is.EqualTo("type!beat"));
    }

    // The OTHER half of task 83, and the half that makes a revived section honest rather than
    // merely populated: PaginatedProfileSubsection.GetCount reads these counters straight off the
    // user payload to print the number beside each subsection heading. A count that disagrees with
    // the list under it is the failure the endpoints and the counters had to land together to avoid.
    [Test]
    [Order(7)]
    public async Task GetUser_SectionCounts_MatchTheListsTheSectionsFetch()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.PlayerUserId}?key=id");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"users status {(int)resp.StatusCode}: {body}");

        var user = JsonConvert.DeserializeObject<APIUser>(body);
        Assert.That(user, Is.Not.Null);

        Assert.That(user!.ScoresBestCount, Is.EqualTo((await getScoresAsync("best")).Count));
        Assert.That(user.ScoresFirstCount, Is.EqualTo((await getScoresAsync("firsts")).Count));
        Assert.That(user.ScoresRecentCount, Is.EqualTo((await getScoresAsync("recent")).Count));
        Assert.That(user.ScoresPinnedCount, Is.EqualTo((await getScoresAsync("pinned")).Count));

        Assert.That(user.ScoresBestCount, Is.GreaterThan(0), "a zero here would prove nothing about the pairing");

        using var mostPlayedReq = ServerFixture.Authed(HttpMethod.Get,
            $"/api/v2/users/{ServerFixture.PlayerUserId}/beatmapsets/most_played?offset=0&limit=51");
        using var mostPlayedResp = await client.SendAsync(mostPlayedReq);

        var mostPlayed = JsonConvert.DeserializeObject<List<APIUserMostPlayedBeatmap>>(
            await mostPlayedResp.Content.ReadAsStringAsync());

        Assert.That(user.BeatmapPlayCountsCount, Is.EqualTo(mostPlayed!.Count));
        Assert.That(user.BeatmapPlayCountsCount, Is.GreaterThan(0));

        // The two graph subsections read their series off the user object and fetch nothing. They
        // hide below two points, so the assertion is only that the key is present and parseable.
        Assert.That(user.MonthlyPlayCounts, Is.Not.Null, "monthly_playcounts must be an array, not absent");
        Assert.That(user.MonthlyPlayCounts, Is.Not.Empty, "the player has submitted, so a month is recorded");
        Assert.That(user.MonthlyPlayCounts[0].Date, Is.Not.EqualTo(default(DateTime)));
        Assert.That(user.MonthlyPlayCounts.Sum(m => m.Count), Is.GreaterThan(0));

        // Nobody has watched a replay in this harness, so the other series is legitimately empty.
        Assert.That(user.ReplaysWatchedCounts, Is.Not.Null);
    }

    // Route hygiene, the "worse than a 404" finding of task 81: users/{id}/{ruleset} is two
    // segments wide and used to swallow every other two-segment users path, answering a USER
    // OBJECT where the caller wanted a list. The sections that are staying off must fail as
    // missing routes, not as unparseable ones.
    [Test]
    [Order(8)]
    public async Task UnservedUserSubroutes_404_RatherThanReturningAUserObject()
    {
        foreach (string path in new[] { "kudosu", "recent_activity", "not-a-ruleset" })
        {
            using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.PlayerUserId}/{path}");
            using var resp = await client.SendAsync(req);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"users/{{id}}/{path} must 404");
        }

        // ...while BOTH shapes GetUserRequest can actually build still resolve to the profile
        // payload. Its target is interpolated as `users/{lookup}/{Ruleset?.ShortName}`, so a null
        // ruleset produces a TRAILING SLASH and an empty final segment, which is the form
        // LocalUserStatisticsProvider, ChannelManager and ScoreImporter send. If validating the
        // ruleset segment had caught that one, every one of those would have started 404ing.
        foreach (string suffix in new[] { "/typebeat", "/" })
        {
            using var okReq = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.PlayerUserId}{suffix}?key=id");
            using var okResp = await client.SendAsync(okReq);

            Assert.That(okResp.IsSuccessStatusCode, Is.True, $"users/{{id}}{suffix} must still be the profile fetch");
            Assert.That(JsonConvert.DeserializeObject<APIUser>(await okResp.Content.ReadAsStringAsync())?.Id,
                Is.EqualTo((int)ServerFixture.PlayerUserId));
        }

        // An unknown score type is a missing route too, not an empty list: an empty list is a real
        // answer about a real section, and a typo must not be able to impersonate one.
        using var badTypeReq = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{ServerFixture.PlayerUserId}/scores/nonsense");
        using var badTypeResp = await client.SendAsync(badTypeReq);

        Assert.That(badTypeResp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // A restricted account is invisible site-wide, and its SECTIONS have to be too. Answering
        // an empty array would confirm the account exists, which is the one thing 404 is hiding.
        long restrictedId;

        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            restrictedId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
                """
                INSERT INTO users (username, email, password_hash, country_code, restricted)
                VALUES ('wc_restricted', 'wc_restricted@example.com', 'x', 'US', true)
                RETURNING id
                """);
        }

        foreach (string path in new[] { "scores/best", "beatmapsets/most_played" })
        {
            using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{restrictedId}/{path}");
            using var resp = await client.SendAsync(req);

            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                $"a restricted user's {path} must 404, not answer []");
        }
    }

    /// <summary>One profile score section, parsed with the client's own list element type.</summary>
    private static async Task<List<SoloScoreInfo>> getScoresAsync(string type)
    {
        using var req = ServerFixture.Authed(HttpMethod.Get,
            $"/api/v2/users/{ServerFixture.PlayerUserId}/scores/{type}?offset=0&limit=51&mode=typebeat");
        using var resp = await client.SendAsync(req);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"scores/{type} status {(int)resp.StatusCode}: {body}");

        return JsonConvert.DeserializeObject<List<SoloScoreInfo>>(body) ?? [];
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
