using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The disk guard's refusals on the EXISTING website host (backlog 365), driven by its
/// <see cref="DiskGuard.ProbeOverride"/> test seam: no disk is filled, no new host is built.
///
/// What is pinned: below the upload floor the replay PUT answers 507 before writing anything, the
/// avatar upload fails as a form error, and /health turns "degraded" (200, no "ok" in it); score
/// SUBMISSION is never refused and a score TOKEN is refused only at the critical level, on both the
/// bearer route and the web player's /play/token, while a submission against a token issued before
/// the disk went critical is still accepted.
///
/// Every test restores the real probe in a finally: the host is shared with the whole namespace.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DiskGuardSiteTest
{
    private const long gib = 1024L * 1024 * 1024;

    /// <summary>Under the 5 GiB upload floor, above the 2 GiB critical line.</summary>
    private static (long, long) uploadsRefused(string _) => (75 * gib, 3 * gib);

    /// <summary>Under the critical line (and so under the floor too).</summary>
    private static (long, long) critical(string _) => (75 * gib, 1 * gib);

    private const string password = "diskguard-pass-1234";

    private static NpgsqlDataSource dataSource = null!;
    private static DiskGuard guard = null!;

    private static long playerId;
    private static string bearer = null!;
    private static long beatmapId;
    private static string checksum = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        guard = WebsiteFixture.Services.GetRequiredService<DiskGuard>();

        await using var conn = await dataSource.OpenConnectionAsync();

        playerId = await WebsiteFixture.SeedUserAsync("disk guard player", "disk.guard.player@example.com", password, verified: true);
        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(playerId)).AccessToken;

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Full Disk Blues', 'The Quota', 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

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
        guard.ProbeOverride = null;

        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [TearDown]
    public void RestoreTheRealProbe() => guard.ProbeOverride = null;

    // ---- /health ----

    [Test]
    public async Task Health_IsOkWhenHealthy_AndDegradedWithoutOkBelowTheFloor()
    {
        guard.ProbeOverride = _ => (75 * gib, 50 * gib);
        var (healthyStatus, healthyBody) = await healthAsync();

        guard.ProbeOverride = uploadsRefused;
        var (degradedStatus, degradedBody) = await healthAsync();

        guard.ProbeOverride = critical;
        var (criticalStatus, criticalBody) = await healthAsync();

        Assert.Multiple(() =>
        {
            Assert.That(healthyStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(healthyBody, Is.EqualTo("ok"));

            Assert.That(degradedStatus, Is.EqualTo(HttpStatusCode.OK), "degraded is still a 200: the site is up");
            Assert.That(degradedBody, Does.StartWith("degraded"));
            Assert.That(degradedBody, Does.Not.Contain("ok").IgnoreCase, "UptimeRobot's keyword must miss");

            Assert.That(criticalStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(criticalBody, Does.StartWith("degraded"));
            Assert.That(criticalBody, Does.Not.Contain("ok").IgnoreCase);
        });
    }

    // ---- replay upload ----

    [Test]
    public async Task ReplayUpload_BelowTheFloor_Is507_WritesNothing_AndLeavesTheRowAlone()
    {
        long scoreId = await insertScoreAsync(800_000);
        var store = WebsiteFixture.Services.GetRequiredService<IFileStore>();

        // The file root outlives the database between runs, so a score id handed out again can find
        // a replay object left by an earlier run's retry below. Clear it, or "nothing was written"
        // would be measuring the previous run.
        await store.DeleteObjectAsync(StoreKeys.Replay(scoreId));

        guard.ProbeOverride = uploadsRefused;

        using var response = await uploadReplayAsync(scoreId, fakeReplay(7, 512));
        string body = await response.Content.ReadAsStringAsync();

        await using var conn = await dataSource.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string? Key, int? Bytes, DateTime? UploadedAt)>(
            "SELECT replay_key AS Key, replay_bytes AS Bytes, replay_uploaded_at AS UploadedAt FROM scores WHERE id = @scoreId",
            new { scoreId });

        bool objectWritten = await store.ObjectExistsAsync(StoreKeys.Replay(scoreId));

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(507));
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("low on storage"), "a WireJson.Error envelope");
            Assert.That(objectWritten, Is.False, "no object was written");
            Assert.That(row.Key, Is.Null);
            Assert.That(row.Bytes, Is.Null);
            Assert.That(row.UploadedAt, Is.Null);
        });

        // Space back, the same upload goes through (nothing about the row was poisoned).
        guard.ProbeOverride = null;
        using var retry = await uploadReplayAsync(scoreId, fakeReplay(7, 512));
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await store.DeleteObjectAsync(StoreKeys.Replay(scoreId));
    }

    // ---- score tokens and submission ----

    [Test]
    public async Task AtTheUploadFloor_TokensAndSubmissionStillWork()
    {
        guard.ProbeOverride = uploadsRefused;

        using var token = await requestTokenAsync();
        Assert.That(token.StatusCode, Is.EqualTo(HttpStatusCode.OK), "tokens are only refused at the critical level");

        long tokenId = (long)JObject.Parse(await token.Content.ReadAsStringAsync())["id"]!;

        using var submit = await submitAsync(tokenId);
        Assert.That(submit.StatusCode, Is.EqualTo(HttpStatusCode.OK), await submit.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task AtCritical_TokenIsRefused_ButAnAlreadyIssuedTokenStillSubmits()
    {
        // Issued while healthy.
        using var issued = await requestTokenAsync();
        Assert.That(issued.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        long tokenId = (long)JObject.Parse(await issued.Content.ReadAsStringAsync())["id"]!;

        guard.ProbeOverride = critical;

        long tokensBefore = await countTokensAsync();

        using var refused = await requestTokenAsync();
        string refusedBody = await refused.Content.ReadAsStringAsync();

        long tokensAfter = await countTokensAsync();

        using var submit = await submitAsync(tokenId);
        string submitBody = await submit.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That((string?)JObject.Parse(refusedBody)["error"], Is.EqualTo(DiskGuard.TokenRefusal),
                "the game shows this message in its 'Score will not be submitted' notification");
            Assert.That(tokensAfter, Is.EqualTo(tokensBefore), "a refused token writes no row");
            Assert.That(submit.StatusCode, Is.EqualTo(HttpStatusCode.OK), submitBody);
        });
    }

    [Test]
    public async Task WebPlayerToken_IsRefusedOnlyAtCritical()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string username = "diskguard_web_" + Guid.NewGuid().ToString("N")[..8];
        await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);

        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        string csrf = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"").Groups[1].Value;
        Assert.That(csrf, Is.Not.Empty, "the /play page publishes an antiforgery token");

        async Task<HttpResponseMessage> playTokenAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/play/token");
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Content = new StringContent(JsonConvert.SerializeObject(new { setId = PublicSiteSeed.CoveredSetId }), Encoding.UTF8, "application/json");
            return await client.SendAsync(request);
        }

        guard.ProbeOverride = uploadsRefused;
        using var atFloor = await playTokenAsync();

        guard.ProbeOverride = critical;
        using var atCritical = await playTokenAsync();
        string criticalBody = await atCritical.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(atFloor.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atCritical.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That((string?)JObject.Parse(criticalBody)["error"], Is.EqualTo(DiskGuard.TokenRefusal));
        });
    }

    // ---- avatar ----

    [Test]
    public async Task AvatarUpload_BelowTheFloor_IsAFormError_AndStoresNothing()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string username = "diskguard_av_" + Guid.NewGuid().ToString("N")[..8];
        long id = await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");

        guard.ProbeOverride = uploadsRefused;

        string html;
        using (var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } })
        {
            var file = new ByteArrayContent(Convert.FromBase64String(png_base64));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "avatar", "avatar.png");

            using var upload = await client.PostAsync("/settings?handler=Avatar", form);
            html = await upload.Content.ReadAsStringAsync();
        }

        await using var conn = await dataSource.OpenConnectionAsync();
        string? avatarKey = await conn.ExecuteScalarAsync<string?>("SELECT avatar_key FROM users WHERE id = @id", new { id });

        Assert.Multiple(() =>
        {
            Assert.That(avatarKey, Is.Null);
            Assert.That(html, Does.Contain("low on storage"), "the refusal is shown on the page");
        });
    }

    // ---- helpers ----

    private const string png_base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAe0lEQVR4nO3PUQkAIBTAQJu9XsY2gSH8OITBAtzWnvN1" +
        "iwsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa" +
        "0IIGtKABLWhACxrQgga0oIHxiJeBC2uMsYdARYnQAAAAAElFTkSuQmCC";

    private static async Task<(HttpStatusCode, string)> healthAsync()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/health");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> countTokensAsync()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM score_tokens WHERE user_id = @playerId", new { playerId });
    }

    private static async Task<long> insertScoreAsync(long totalScore)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@playerId, @beatmapId, @totalScore, 0.97, 1.0, 100, 'S', true, true,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { playerId, beatmapId, totalScore });
    }

    private static async Task<HttpResponseMessage> requestTokenAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{beatmapId}/solo/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["version_hash"] = "disk-guard-test-build",
            ["beatmap_hash"] = checksum,
            ["ruleset_id"] = "0",
        });

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> submitAsync(long tokenId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId}");
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
        }), Encoding.UTF8, "application/json");

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> uploadReplayAsync(long scoreId, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{scoreId}/replay");
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await WebsiteFixture.Client.SendAsync(request);
    }

    /// <summary>The smallest body ReplayEndpoints.LooksLikeLegacyReplay accepts (ReplayStorageTest's shape).</summary>
    internal static byte[] fakeReplay(byte seed, int length)
    {
        byte[] bytes = new byte[Math.Max(length, 6)];

        bytes[0] = 0;
        BitConverter.TryWriteBytes(bytes.AsSpan(1, 4), 30000001);
        bytes[5] = 0x0b;
        if (bytes.Length > 6)
            bytes[6] = 32;

        var rng = new Random(seed);
        for (int i = 7; i < bytes.Length; i++)
            bytes[i] = (byte)rng.Next(256);

        return bytes;
    }
}
