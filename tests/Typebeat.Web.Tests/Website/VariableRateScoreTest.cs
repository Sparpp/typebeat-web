using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Variable-rate mods end to end (task 27, server side): DT/NC/HT are ranked at EVERY speed, the
/// submitted <c>settings.speed_change</c> survives into the stored mods jsonb (snapped, clamped, and
/// stripped of everything else), the multiplied total is bounded by the EXACT multiplier that stack
/// earns rather than a flat 2x allowance, and the site renders the rate on the mod badge.
/// </summary>
[TestFixture]
[NonParallelizable]
public class VariableRateScoreTest
{
    // 10 greats out of 10: accuracy 1, fully judged, so the no-mod ceiling is exactly 1,000,000 and
    // any base at or under it is in bounds. The mod multiplier is then the only thing under test.
    private const long clean_base = 400_000;

    private static long setId;
    private static long beatmapId;
    private static string checksum = null!;
    private static string bearer = null!;

    private static long displaySetId;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        long typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('rate typist', 'rate.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        setId = await InsertRankedSetAsync(conn, "Rate Candidate");

        // Zero drain length: the 90%-of-drain minimum-play-time gate clears instantly.
        checksum = Guid.NewGuid().ToString("N");
        beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum });

        // A second set whose leaderboard exists only to be rendered, so the display assertions do
        // not depend on which submission test ran last.
        displaySetId = await InsertRankedSetAsync(conn, "Rate Display");

        long displayBeatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 55, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId = displaySetId, checksum = Guid.NewGuid().ToString("N") });

        // Top row: a modern submission carrying its rate. Second row: a HISTORIC row with no
        // settings at all, which must still render a rate (the client default it could only have
        // been played at).
        await InsertScoreAsync(conn, PublicSiteSeed.TypistOneId, displayBeatmapId, 700_000,
            """[{"acronym": "DT", "settings": {"speed_change": 1.75}}, {"acronym": "FL"}]""");
        await InsertScoreAsync(conn, PublicSiteSeed.TypistTwoId, displayBeatmapId, 200_000,
            """[{"acronym": "HT"}]""");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- ranked at any rate ----

    [Test]
    public async Task DoubleTimeAtAnyRate_IsRanked_AndKeepsItsRate()
    {
        // 1.75x pays 1 + 0.46 × 0.75 = 1.345, a rate the old server would have refused to rank.
        var submitted = await SubmitAsync(
            total: (long)Math.Round(clean_base * 1.345),
            mods: [Mod("DT", ("speed_change", 1.75))]);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True, "a non-default rate must now rank");
            Assert.That(submitted["position"]!.Type, Is.Not.EqualTo(JTokenType.Null), "a ranked score gets a position");
        });

        Assert.That(await StoredModsAsync(submitted), Is.EqualTo("""[{"acronym":"DT","settings":{"speed_change":1.75}}]"""));
    }

    [Test]
    public async Task HalfTimeAtAnyRate_IsRanked_AndKeepsItsRate()
    {
        // 0.62x is below the post-nerf floor crossing (0.70x), so it pays the 0.10 minimum.
        var submitted = await SubmitAsync(
            total: (long)Math.Round(clean_base * 0.10),
            mods: [Mod("HT", ("speed_change", 0.62))]);

        Assert.That((bool)submitted["ranked"]!, Is.True);
        Assert.That(await StoredModsAsync(submitted), Is.EqualTo("""[{"acronym":"HT","settings":{"speed_change":0.62}}]"""));
    }

    [Test]
    public async Task AlwaysUnrankedMods_AreStillUnranked()
    {
        // Deleting the rate-mod unranking branch must not have loosened RX / WU / WD.
        var submitted = await SubmitAsync(total: (long)Math.Round(clean_base * 0.1), mods: [Mod("RX")]);

        Assert.That((bool)submitted["ranked"]!, Is.False, "Mashing is unranked at every configuration");
    }

    // ---- what gets stored ----

    [Test]
    public async Task StoredRate_IsSnappedAndClamped()
    {
        // Between two slider steps: snapped to 0.01 (1.756 → 1.76, priced at 1.3496).
        var snapped = await SubmitAsync(
            total: (long)Math.Round(clean_base * 1.3496),
            mods: [Mod("DT", ("speed_change", 1.756))]);

        // Far past the slider's top: clamped to 2.00, so it buys the 1.46 price and no more.
        var clamped = await SubmitAsync(
            total: (long)Math.Round(clean_base * 1.46),
            mods: [Mod("DT", ("speed_change", 40))]);

        // Below the floor of Half Time's slider: clamped to 0.50 (the 0.10 multiplier floor).
        var floored = await SubmitAsync(
            total: (long)Math.Round(clean_base * 0.10),
            mods: [Mod("HT", ("speed_change", -3))]);

        string snappedMods = await StoredModsAsync(snapped);
        string clampedMods = await StoredModsAsync(clamped);
        string flooredMods = await StoredModsAsync(floored);

        Assert.Multiple(() =>
        {
            Assert.That(snappedMods, Is.EqualTo("""[{"acronym":"DT","settings":{"speed_change":1.76}}]"""));
            Assert.That(clampedMods, Is.EqualTo("""[{"acronym":"DT","settings":{"speed_change":2.0}}]"""));
            Assert.That(flooredMods, Is.EqualTo("""[{"acronym":"HT","settings":{"speed_change":0.5}}]"""));

            Assert.That((bool)clamped["ranked"]!, Is.True, "a clamped rate is priced, not rejected");
        });
    }

    [Test]
    public async Task EveryOtherSetting_IsStillStripped()
    {
        var submitted = await SubmitAsync(
            total: (long)Math.Round(clean_base * 1.23 * 1.05),
            mods:
            [
                // adjust_pitch is a real client setting; the padding is what a tampered payload
                // would use to bloat the row. Only the score-affecting rate survives.
                Mod("dt", ("speed_change", 1.5), ("adjust_pitch", true), ("padding", new string('x', 4096))),
                Mod("FL", ("junk", 1)),
            ]);

        Assert.That(await StoredModsAsync(submitted),
            Is.EqualTo("""[{"acronym":"DT","settings":{"speed_change":1.5}},{"acronym":"FL"}]"""));
    }

    [Test]
    public async Task UnparseableRate_StoresNoSetting_AndPricesTheDefault()
    {
        var submitted = await SubmitAsync(
            total: (long)Math.Round(clean_base * 1.23),
            mods: [Mod("DT", ("speed_change", "very fast"))]);

        string stored = await StoredModsAsync(submitted);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.EqualTo("""[{"acronym":"DT"}]"""),
                "a rate that is not a number is dropped, and reads back as the default");
            Assert.That((bool)submitted["ranked"]!, Is.True);
        });
    }

    // ---- the exact ceiling ----

    [Test]
    public async Task TotalScore_IsBoundedByTheExactModMultiplier()
    {
        double fattest = ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null)]);
        object[] fattestStack = [Mod("DT", ("speed_change", 2.0)), Mod("FL"), Mod("LT")];

        // 1,000,000 × 1.60965: the dearest total any ranked play on this map could ever submit.
        var atCeiling = await SubmitAsync(total: 1_609_650, baseScore: 1_000_000, mods: fattestStack);

        // Two points above it is not reachable by any honest client.
        var overCeiling = await SubmitAsync(total: 1_609_652, baseScore: 1_000_000, mods: fattestStack);

        // A no-mod play cannot beat its own base score, which the old flat 2x allowance let it do.
        var inflatedNoMod = await SubmitAsync(total: clean_base + 2, mods: []);

        // Half Time's ceiling drops with its multiplier: after the task-44 nerf that is
        // 400,000 × 0.25 = 100,000, so an un-updated client's 0.55-scaled 220,000 is out of
        // bounds and stores unranked. This is the whole deploy-window mechanism for the nerf.
        var inflatedHalfTime = await SubmitAsync(total: 220_000, mods: [Mod("HT", ("speed_change", 0.75))]);

        // The value that same play submits once the client ships the new curve.
        var nerfedHalfTime = await SubmitAsync(total: 100_000, mods: [Mod("HT", ("speed_change", 0.75))]);

        Assert.Multiple(() =>
        {
            Assert.That(fattest, Is.EqualTo(1.60965).Within(1e-9));
            Assert.That((bool)atCeiling["ranked"]!, Is.True, "the fattest honest stack must submit at its exact total");
            Assert.That((long)atCeiling["total_score"]!, Is.EqualTo(1_609_650));

            Assert.That((bool)overCeiling["ranked"]!, Is.False, "two points over the exact ceiling is out of bounds");
            Assert.That((bool)inflatedNoMod["ranked"]!, Is.False, "no-mod cannot beat its own base score");
            Assert.That((bool)inflatedHalfTime["ranked"]!, Is.False, "an old client's 0.55x Half Time total is over the 0.25x ceiling");

            Assert.That((bool)nerfedHalfTime["ranked"]!, Is.True, "the new curve's own total is exactly in bounds");
            Assert.That((long)nerfedHalfTime["total_score"]!, Is.EqualTo(100_000));
        });
    }

    // ---- display ----

    [Test]
    public async Task SetPage_ShowsTheRateOnTheModBadge()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{displaySetId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("mod-icon--rated"), "a rate mod's badge carries the rate pill");
            Assert.That(html, Does.Contain("1.75x"), "the submitted rate must be shown, not just \"DT\"");
            Assert.That(html, Does.Contain("Double Time 1.75x"), "and it rides on the tooltip too");

            // A historic row with no stored settings still shows a rate: the client default, which
            // under the old rules was the only rate a ranked bare HT could have been played at.
            Assert.That(html, Does.Contain("0.75x"));
            Assert.That(html, Does.Contain("Half Time 0.75x"));

            // Non-rate mods are untouched: acronym only, no pill.
            Assert.That(html, Does.Contain("""title="Flashlight">FL<"""));

            // The pill's depth tracks how far the rate is pushed (1.75 of a 1.01..2.00 slider).
            Assert.That(html, Does.Contain("--rate-intensity: 0.75"));
        });
    }

    // ---- helpers ----

    /// <summary>One submitted mod in the client's APIMod shape.</summary>
    private static object Mod(string acronym, params (string key, object value)[] settings)
        => settings.Length == 0
            ? new { acronym }
            : new { acronym, settings = settings.ToDictionary(s => s.key, s => s.value) };

    /// <summary>
    /// Runs the client's two-phase submission for a clean 10/10 play and returns the
    /// MultiplayerScore response body. <paramref name="baseScore"/> is the pre-multiplier total,
    /// which the no-mod ceiling (1,000,000 for this statistics blob) already bounds.
    /// </summary>
    private static async Task<JObject> SubmitAsync(long total, object[] mods, long baseScore = clean_base)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "variable-rate-test-build",
                ["beatmap_hash"] = checksum,
                ["ruleset_id"] = "0",
            });

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score token");

            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                passed = true,
                total_score = total,
                total_score_without_mods = baseScore,
                accuracy = 1.0,
                max_combo = 10,
                ruleset_id = 0,
                rank = "X",
                statistics = new Dictionary<string, int> { ["great"] = 10 },
                maximum_statistics = new Dictionary<string, int> { ["great"] = 10 },
                mods,
            }), System.Text.Encoding.UTF8, "application/json");

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score submit");

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>The mods jsonb as stored for a submitted score, compacted for exact comparison.</summary>
    private static async Task<string> StoredModsAsync(JObject submitted)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string stored = await conn.ExecuteScalarAsync<string>(
            "SELECT mods::text FROM scores WHERE id = @id", new { id = (long)submitted["id"]! }) ?? "[]";

        return JArray.Parse(stored).ToString(Formatting.None);
    }

    private static async Task<long> InsertRankedSetAsync(NpgsqlConnection conn, string title)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Rated', 'ranked', now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

    private static async Task InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmap, long totalScore, string modsJson)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmap, @totalScore, 0.97, 1.0, 100, 'X', true, true,
                 CAST(@modsJson AS jsonb), '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            """,
            new { userId, beatmap, totalScore, modsJson });
}
