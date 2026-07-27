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
/// The minimum-play-time gate end to end (task 47): a play that finished fast because the player
/// used the game's instrumental-skip button must rank, an impossibly fast one must not, and a map
/// with no skip allowance must be gated exactly as strictly as it was before.
///
/// <para>
/// The submission path measures elapsed from the score token's <c>created_at</c>, so each case
/// backdates that column between the two phases: it is the same wall-clock anchor a real play
/// would have produced, without the test having to wait a minute for it.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class SkipGateTest
{
    // 100 s drain. With a 40 s allowance the bound is 0.9 x 60 = 54 s; with none it is 90 s.
    private const double drain_s = 100;
    private const double skippable_s = 40;

    private static long skippableBeatmapId;
    private static long plainBeatmapId;
    private static string skippableChecksum = null!;
    private static string plainChecksum = null!;
    private static string bearer = null!;

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
            VALUES ('skip typist', 'skip.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Gap Heavy', 'The Instrumentalists', 'ranked', now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        skippableChecksum = Guid.NewGuid().ToString("N");
        skippableBeatmapId = await InsertBeatmapAsync(conn, setId, skippableChecksum, skippable_s);

        plainChecksum = Guid.NewGuid().ToString("N");
        plainBeatmapId = await InsertBeatmapAsync(conn, setId, plainChecksum, 0);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task ASkipFastPlay_IsRanked()
    {
        // 60 s of a 100 s map: under the old 90 s bound (which unranked honest players outright on
        // maps like "Immortal Flame"), over the corrected 54 s one.
        var submitted = await SubmitAsync(skippableBeatmapId, skippableChecksum, elapsedSeconds: 60);

        Assert.Multiple(() =>
        {
            Assert.That((bool)submitted["ranked"]!, Is.True, "a legal skip-using play must rank");
            Assert.That(submitted["position"]!.Type, Is.Not.EqualTo(JTokenType.Null));
        });
    }

    [Test]
    public async Task AnImpossiblyFastPlay_IsStillUnranked()
    {
        // 30 s is less than the map's own content even with every skip taken, so the gate still
        // catches it. The bound stays a sanity bound, it does not evaporate.
        var submitted = await SubmitAsync(skippableBeatmapId, skippableChecksum, elapsedSeconds: 30);

        Assert.That((bool)submitted["ranked"]!, Is.False);
    }

    [Test]
    public async Task WithNoAllowance_TheOldBoundIsUnchanged()
    {
        // Same 60 s play on a map with no qualifying gaps (skippable_s = 0, which is also what an
        // unparseable or not-yet-backfilled blob leaves behind): still unranked.
        var unranked = await SubmitAsync(plainBeatmapId, plainChecksum, elapsedSeconds: 60);
        var ranked = await SubmitAsync(plainBeatmapId, plainChecksum, elapsedSeconds: 95);

        Assert.Multiple(() =>
        {
            Assert.That((bool)unranked["ranked"]!, Is.False, "no allowance means the pre-task-47 bound");
            Assert.That((bool)ranked["ranked"]!, Is.True);
        });
    }

    // ---- helpers ----

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId, string checksum, double skippable)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, skippable_s)
            VALUES (@setId, 'type!beat', @checksum, @drain, @drain, 2.0, 'map.osu', @skippable)
            RETURNING id
            """,
            new { setId, checksum, drain = drain_s, skippable });

    /// <summary>
    /// The client's two-phase submission for a clean 10/10 play, with the token's creation
    /// backdated by <paramref name="elapsedSeconds"/> so the gate sees a play of that length.
    /// </summary>
    private static async Task<JObject> SubmitAsync(long beatmapId, string checksum, double elapsedSeconds)
    {
        long tokenId;

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version_hash"] = "skip-gate-test-build",
                ["beatmap_hash"] = checksum,
                ["ruleset_id"] = "0",
            });

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score token");

            tokenId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
        }

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE score_tokens SET created_at = now() - make_interval(secs => @elapsedSeconds) WHERE id = @tokenId",
                new { tokenId, elapsedSeconds });
        }

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                passed = true,
                total_score = 400_000,
                total_score_without_mods = 400_000,
                accuracy = 1.0,
                max_combo = 10,
                ruleset_id = 0,
                rank = "X",
                statistics = new Dictionary<string, int> { ["great"] = 10 },
                maximum_statistics = new Dictionary<string, int> { ["great"] = 10 },
                mods = Array.Empty<object>(),
            }), System.Text.Encoding.UTF8, "application/json");

            using var response = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score submit");

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }
    }
}
