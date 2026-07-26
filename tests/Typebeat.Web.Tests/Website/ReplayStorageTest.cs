using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Replay storage end to end (backlog 37, server half): the owner-only upload, the public
/// download, and the additive <c>has_replay</c> flag the game reads to decide whether a
/// leaderboard row can be watched.
///
/// The motivating bug: nothing on the server ever accepted a replay, so every online row said
/// "no replay available" no matter what the client had locally. These tests pin the whole
/// contract the client is built against: 204/404/403/413, byte-identical roundtrip, overwrite on
/// re-upload, and has_replay flipping false → true for exactly the score that got one.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReplayStorageTest
{
    private static long ownerId;
    private static string ownerBearer = null!;
    private static string strangerBearer = null!;

    private static long setId;
    private static long beatmapId;

    /// <summary>The owner's 900k top score; the one that gains a replay.</summary>
    private static long ownerScoreId;

    /// <summary>A second player's 500k score that never gets one (the has_replay=false control).</summary>
    private static long bareScoreId;

    private const long unknown_score_id = 987_654_321;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        ownerId = await insertUserAsync(conn, "replay owner");
        long strangerId = await insertUserAsync(conn, "replay stranger");

        var tokens = new TokenService(new Db(dataSource));
        ownerBearer = (await tokens.IssueAsync(ownerId)).AccessToken;
        strangerBearer = (await tokens.IssueAsync(strangerId)).AccessToken;

        setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Replay Anthem', 'The Recorded', 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 55, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        ownerScoreId = await insertScoreAsync(conn, ownerId, beatmapId, 900_000);
        bareScoreId = await insertScoreAsync(conn, strangerId, beatmapId, 500_000);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- before anything is uploaded ----

    [Test]
    [Order(1)]
    public async Task Leaderboard_CarriesHasReplay_FalseBeforeAnyUpload()
    {
        string body = await LeaderboardBodyAsync();
        var scores = JObject.Parse(body)["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That(scores.Count(), Is.EqualTo(2), "both players' best scores are on the board");
            // The field must EXIST, not merely read as false: the client binds it to
            // ScoreInfo.HasOnlineReplay, and a missing key silently disables the replay button.
            Assert.That(scores[0]!["has_replay"], Is.Not.Null, "leaderboard rows must carry has_replay");
            Assert.That((bool)scores[0]!["has_replay"]!, Is.False);
            Assert.That((bool)scores[1]!["has_replay"]!, Is.False);

            // Nothing pre-existing was renamed or dropped by the addition.
            Assert.That(scores[0]!["total_score"], Is.Not.Null);
            Assert.That(scores[0]!["maximum_statistics"], Is.Not.Null);
            Assert.That(scores[0]!["ranked"], Is.Not.Null);
            Assert.That(scores[0]!["user"], Is.Not.Null);
        });
    }

    [Test]
    [Order(2)]
    public async Task Download_WithNothingStored_Is404()
    {
        using var stored = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{ownerScoreId}/replay");
        using var unknown = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{unknown_score_id}/replay");

        Assert.Multiple(() =>
        {
            Assert.That(stored.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a real score with no replay has nothing to serve");
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- upload rejections ----

    [Test]
    [Order(3)]
    public async Task Upload_ForUnknownScore_Is404()
    {
        using var response = await UploadAsync(unknown_score_id, ownerBearer, FakeReplay(1, 512));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    [Order(4)]
    public async Task Upload_ByNonOwner_Is403_AndStoresNothing()
    {
        using var response = await UploadAsync(ownerScoreId, strangerBearer, FakeReplay(2, 512));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await ReplayKeyAsync(ownerScoreId), Is.Null, "a refused upload must not touch the row");
    }

    [Test]
    [Order(5)]
    public async Task Upload_WithoutBearer_Is401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{ownerScoreId}/replay")
        {
            Content = OctetStream(FakeReplay(3, 512)),
        };

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    [Order(6)]
    public async Task Upload_OverTheSizeCap_Is413()
    {
        byte[] oversize = FakeReplay(4, (int)Typebeat.Web.Endpoints.ReplayEndpoints.MaxReplayBytes + 1);

        using var response = await UploadAsync(ownerScoreId, ownerBearer, oversize);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await ReplayKeyAsync(ownerScoreId), Is.Null);
    }

    [Test]
    [Order(7)]
    public async Task Upload_OfSomethingThatIsNotAReplay_Is422()
    {
        // Empty body, and a PNG header: both fail the structural check without any parsing.
        using var empty = await UploadAsync(ownerScoreId, ownerBearer, Array.Empty<byte>());
        using var png = await UploadAsync(ownerScoreId, ownerBearer, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);

        Assert.Multiple(() =>
        {
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(png.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        });

        Assert.That(await ReplayKeyAsync(ownerScoreId), Is.Null);
    }

    // ---- the happy path ----

    [Test]
    [Order(8)]
    public async Task Upload_ThenDownload_RoundTripsTheExactBytes()
    {
        byte[] replay = FakeReplay(seed: 7, length: 4096);

        using (var upload = await UploadAsync(ownerScoreId, ownerBearer, replay))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await upload.Content.ReadAsStringAsync());

        using var download = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{ownerScoreId}/replay");
        byte[] served = await download.Content.ReadAsByteArrayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(download.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
            Assert.That(served, Is.EqualTo(replay), "the stored replay must come back byte-identical");
        });

        await using var conn = await dataSource.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string? Key, int? Bytes, DateTime? UploadedAt)>(
            "SELECT replay_key AS Key, replay_bytes AS Bytes, replay_uploaded_at AS UploadedAt FROM scores WHERE id = @id",
            new { id = ownerScoreId });

        Assert.Multiple(() =>
        {
            Assert.That(row.Key, Is.EqualTo($"replays/{ownerScoreId}.osr"));
            Assert.That(row.Bytes, Is.EqualTo(replay.Length));
            Assert.That(row.UploadedAt, Is.Not.Null);
        });
    }

    [Test]
    [Order(9)]
    public async Task Download_IsPublic_NoBearerNeeded()
    {
        // The shared client carries no Authorization header at all; leaderboards are public, so
        // a replay linked from one must be too.
        using var response = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{ownerScoreId}/replay");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await response.Content.ReadAsByteArrayAsync()), Has.Length.EqualTo(4096));
    }

    [Test]
    [Order(10)]
    public async Task Leaderboard_ReportsHasReplay_OnlyForTheScoreThatHasOne()
    {
        string body = await LeaderboardBodyAsync();
        var root = JObject.Parse(body);
        var scores = root["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((long)scores[0]!["id"]!, Is.EqualTo(ownerScoreId));
            Assert.That((bool)scores[0]!["has_replay"]!, Is.True, "the uploaded score must now advertise its replay");
            Assert.That((long)scores[1]!["id"]!, Is.EqualTo(bareScoreId));
            Assert.That((bool)scores[1]!["has_replay"]!, Is.False, "the untouched score must stay false");

            // user_score (the caller's own row) is built by the same projection.
            Assert.That((bool)root["user_score"]!["score"]!["has_replay"]!, Is.True);
        });
    }

    [Test]
    [Order(11)]
    public async Task Reupload_ByTheOwner_OverwritesTheStoredReplay()
    {
        byte[] replacement = FakeReplay(seed: 11, length: 1024);

        using (var upload = await UploadAsync(ownerScoreId, ownerBearer, replacement))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "re-upload is idempotent, not a conflict");

        using var download = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{ownerScoreId}/replay");

        Assert.That(await download.Content.ReadAsByteArrayAsync(), Is.EqualTo(replacement), "the newer bytes win");
        Assert.That(await ReplaySizeAsync(ownerScoreId), Is.EqualTo(replacement.Length), "the recorded size follows the object");
    }

    [Test]
    [Order(12)]
    public async Task SetPage_LinksTheReplay_OnlyOnRowsThatHaveOne()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{setId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"/api/v2/scores/{ownerScoreId}/replay"), "the score with a replay offers a download");
            Assert.That(html, Does.Not.Contain($"/api/v2/scores/{bareScoreId}/replay"), "the score without one offers nothing");
        });
    }

    [Test]
    [Order(13)]
    public async Task ProfilePage_LinksTheOwnersReplay_OnTheirScoreRow()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{ownerId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain($"/api/v2/scores/{ownerScoreId}/replay"),
            "the player's own Best/Recent rows must offer the replay they uploaded");
    }

    // ---- helpers ----

    /// <summary>
    /// Bytes shaped like the legacy .osr the game encodes: ruleset id 0, a version int, then the
    /// beatmap-hash string marker (0x0b) and a 32-char hash, followed by deterministic filler
    /// standing in for the compressed frame block.
    /// </summary>
    private static byte[] FakeReplay(byte seed, int length)
    {
        byte[] bytes = new byte[Math.Max(length, 6)];

        bytes[0] = 0;                                    // ruleset id (typebeat is legacy id 0)
        BitConverter.TryWriteBytes(bytes.AsSpan(1, 4), 30000001); // version int, little endian
        bytes[5] = 0x0b;                                 // "string present" marker
        if (bytes.Length > 6)
            bytes[6] = 32;                               // ULEB128 length of the md5 hash

        var rng = new Random(seed);
        for (int i = 7; i < bytes.Length; i++)
            bytes[i] = (byte)rng.Next(256);

        return bytes;
    }

    private static ByteArrayContent OctetStream(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task<HttpResponseMessage> UploadAsync(long scoreId, string bearer, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{scoreId}/replay")
        {
            Content = OctetStream(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<string> LeaderboardBodyAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerBearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "leaderboard");

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string?> ReplayKeyAsync(long scoreId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT replay_key FROM scores WHERE id = @scoreId", new { scoreId });
    }

    private static async Task<int?> ReplaySizeAsync(long scoreId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int?>(
            "SELECT replay_bytes FROM scores WHERE id = @scoreId", new { scoreId });
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

    private static async Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmap, long totalScore)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmap, @totalScore, 0.97, 1.0, 100, 'S', true, true,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmap, totalScore });
}
