using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The submit-path integrity checks both routes share (Scoring/ScoreSubmission.cs): an acronym the
/// server does not know stores UNRANKED instead of boarding at the 2.0x unknown-mod allowance, and a
/// statistics key that is not a HitResult name is dropped before it is stored, merged into
/// <c>user_stats.hit_counts</c> or echoed to a board reader. The game route (bearer, the two-phase
/// solo score) and the browser route (session, /play/submit) are each driven once, on LOVED sets so
/// a ranked play reaches the ranked board without touching the shared database's pp and ranked
/// score rankings, which other fixtures pin by rank.
///
/// <para>The browser player sends no mods at all (its row stores <c>'[]'</c>), so the acronym check
/// has only the game route to be pinned on.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class ScoreSubmitIntegrityTest
{
    private const string typist_name = "integrity typist";
    private const string browser_name = "integrity browser typist";
    private const string browser_password = "integritybrowser-123456";

    private static long typistId;
    private static string typistBearer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@name, 'integrity.typist@example.com', 'x', 'US')
            RETURNING id
            """,
            new { name = typist_name });

        typistBearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        await WebsiteFixture.SeedUserAsync(browser_name, "integrity.browser@example.com", browser_password, verified: true);
    }

    /// <summary>
    /// A perfect honest run plus one invented acronym used to board at 2,000,000, above every
    /// stack the client can assemble. It is still stored (and still priced at the allowance, so its
    /// total is in bounds), but unranked, so it never reaches the board. The control, a ranked
    /// acronym the server knows, still ranks at its own price.
    /// </summary>
    [Test]
    public async Task GameRoute_UnknownAcronym_IsStoredUnranked_AndAKnownOneStillRanks()
    {
        var (beatmapId, checksum) = await seedLovedMapAsync("Integrity Acronym Song");

        var unknown = await submitScoreAsync(beatmapId, checksum, totalScore: 2_000_000, mods: [new { acronym = "ZZ" }]);
        var known = await submitScoreAsync(beatmapId, checksum, totalScore: 1_020_000, mods: [new { acronym = "FC" }]);

        var board = (JArray)(await getLeaderboardAsync(beatmapId))["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((bool)unknown["ranked"]!, Is.False, "an unknown acronym must not rank");
            Assert.That(unknown["position"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
            Assert.That((bool)known["ranked"]!, Is.True, "a known ranked acronym still ranks");
            Assert.That((long)known["total_score"]!, Is.EqualTo(1_020_000));
            Assert.That(board, Has.Count.EqualTo(1));
            Assert.That((long)board[0]["id"]!, Is.EqualTo((long)known["id"]!));
        });

        Assert.That(await rankedAsync((long)unknown["id"]!), Is.False);
    }

    /// <summary>
    /// An unknown statistics key is dropped on the game route before anything reads it: the play
    /// still ranks on its honest counts, and neither the stored row, the submit response, the board
    /// nor the player's lifetime hit counts carry the key.
    /// </summary>
    [Test]
    public async Task GameRoute_UnknownStatisticsKey_IsDropped_AndThePlayStillRanks()
    {
        var (beatmapId, checksum) = await seedLovedMapAsync("Integrity Statistics Song");

        var submitted = await submitScoreAsync(beatmapId, checksum, totalScore: 1_000_000,
            statistics: new Dictionary<string, int> { ["great"] = 5, ["zz"] = 2_000_000_000, ["Great"] = 3 },
            maximumStatistics: new Dictionary<string, int> { ["great"] = 5, ["zz"] = 1 });
        long scoreId = (long)submitted["id"]!;

        var board = (JArray)(await getLeaderboardAsync(beatmapId))["scores"]!;
        var (statistics, maximumStatistics) = await storedStatisticsAsync(scoreId);
        string hitCounts = await hitCountsAsync(typistId);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True, "the honest counts still rank");
            Assert.That(((JObject)submitted["statistics"]!).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(statistics.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(maximumStatistics.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(board, Has.Count.EqualTo(1));
            Assert.That(((JObject)board[0]["statistics"]!).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(JObject.Parse(hitCounts)["zz"], Is.Null, "an unknown key never reaches the lifetime hit counts");
        });
    }

    /// <summary>
    /// A row stored before submissions were filtered can still hold a junk key. The board filters
    /// on the way out too, so such a row cannot fail the game's <c>Dictionary&lt;HitResult, int&gt;</c>
    /// read for everyone viewing the map.
    /// </summary>
    [Test]
    public async Task Board_FiltersAnAlreadyStoredUnknownKey()
    {
        var (beatmapId, _) = await seedLovedMapAsync("Integrity Stored Row Song");

        await using (var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString))
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO scores
                    (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                     mods, statistics, maximum_statistics, pp, pp_version)
                VALUES
                    (@typistId, @beatmapId, 900000, 1.0, 1.0, 5, 'X', true, true,
                     '[]'::jsonb, '{"great":5,"zz":1}'::jsonb, '{"great":5,"zz":1}'::jsonb, 0, 0)
                """,
                new { typistId, beatmapId });
        }

        var board = (JArray)(await getLeaderboardAsync(beatmapId))["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That(board, Has.Count.EqualTo(1));
            Assert.That(((JObject)board[0]["statistics"]!).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(((JObject)board[0]["maximum_statistics"]!).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
        });
    }

    /// <summary>The browser route drops an unknown statistics key exactly as the game route does.</summary>
    [Test]
    public async Task BrowserRoute_UnknownStatisticsKey_IsDropped_AndThePlayStillRanks()
    {
        var (beatmapId, _) = await seedLovedMapAsync("Integrity Browser Song");
        long setId;

        await using (var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString))
        await using (var conn = await dataSource.OpenConnectionAsync())
            setId = await conn.ExecuteScalarAsync<long>("SELECT set_id FROM beatmaps WHERE id = @beatmapId", new { beatmapId });

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, browser_name, browser_password)) { }

        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        var csrfMatch = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(csrfMatch.Success, Is.True, "the /play page must publish an antiforgery token");
        string csrf = csrfMatch.Groups[1].Value;

        long tokenId;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/play/token"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(new { setId, beatmapId }), Encoding.UTF8, "application/json");

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
                statistics = new Dictionary<string, int> { ["great"] = 10, ["zz"] = 2_000_000_000 },
                maximumStatistics = new Dictionary<string, int> { ["great"] = 10, ["zz"] = 1 },
            }), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play submit");
            submitted = JObject.Parse(await response.Content.ReadAsStringAsync());
        }

        long scoreId;
        await using (var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString))
        await using (var conn = await dataSource.OpenConnectionAsync())
            scoreId = await conn.ExecuteScalarAsync<long>("SELECT score_id FROM score_tokens WHERE id = @tokenId", new { tokenId });

        var (statistics, maximumStatistics) = await storedStatisticsAsync(scoreId);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True, "the honest counts still rank");
            Assert.That(statistics.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
            Assert.That(maximumStatistics.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "great" }));
        });
    }

    // ---- helpers ----

    private static async Task<(long BeatmapId, string Checksum)> seedLovedMapAsync(string title)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Integrity', 'loved', now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

        string checksum = Guid.NewGuid().ToString("N");

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum });

        return (beatmapId, checksum);
    }

    /// <summary>
    /// The game's two-phase submission (as LovedStatusTest). 1,000,000 is the exact no-mod ceiling
    /// for 5/5 greats, which is what the default counts below describe.
    /// </summary>
    private static async Task<JObject> submitScoreAsync(long beatmapId, string checksum, long totalScore,
        object[]? mods = null, Dictionary<string, int>? statistics = null, Dictionary<string, int>? maximumStatistics = null)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", typistBearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "7e570000000000000000000000000008",
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
                total_score_without_mods = 1_000_000,
                accuracy = 1.0,
                max_combo = 5,
                ruleset_id = 0,
                rank = "X",
                statistics = statistics ?? new Dictionary<string, int> { ["great"] = 5 },
                maximum_statistics = maximumStatistics ?? new Dictionary<string, int> { ["great"] = 5 },
                mods = mods ?? [],
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

    private static async Task<bool> rankedAsync(long scoreId)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("SELECT ranked FROM scores WHERE id = @scoreId", new { scoreId });
    }

    private static async Task<(JObject Statistics, JObject MaximumStatistics)> storedStatisticsAsync(long scoreId)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        var (statistics, maximum) = await conn.QuerySingleAsync<(string, string)>(
            "SELECT statistics::text, maximum_statistics::text FROM scores WHERE id = @scoreId", new { scoreId });
        return (JObject.Parse(statistics), JObject.Parse(maximum));
    }

    private static async Task<string> hitCountsAsync(long userId)
    {
        await using var dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>("SELECT hit_counts::text FROM user_stats WHERE user_id = @userId", new { userId }) ?? "{}";
    }
}
