using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// The full BSS happy path against the real pipeline, in the client wizard's exact order
/// (recon result.bss.endpoint_sequence): create set → full package PUT → re-PUT returning the
/// manifest → identical re-upload (no version) → PATCH delta (new version, blobs reused) —
/// then the surrounding surface: the APIv2 beatmapset GET, the website download endpoint, the
/// real favourites read, and the media/cover routes.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssSubmissionFlowTest
{
    private const string username = "bss uploader";

    private long userId;
    private string bearer = null!;

    private long setId;
    private long[] beatmapIds = null!;

    private byte[] easyOsu = null!;
    private byte[] hardOsu = null!;
    private byte[] audio = null!;
    private byte[] background = null!;
    private byte[] packageZip = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (userId, bearer) = await BssFixture.CreateUserAsync(username, verified: true);
    }

    [Test]
    [Order(1)]
    public async Task CreateSet_AllocatesIds_WithEmptyFiles()
    {
        using var response = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 2,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "WIP",
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        setId = (long)body["beatmapset_id"]!;
        beatmapIds = body["beatmap_ids"]!.Select(t => (long)t).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(setId, Is.GreaterThan(0));
            Assert.That(beatmapIds, Has.Length.EqualTo(2));
            // Empty files[] = the client's "fresh set → full replace upload" branch.
            Assert.That(body["files"], Is.Empty);
        });

        await using var conn = await BssFixture.OpenDbAsync();

        string? status = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(status, Is.EqualTo("hidden"), "a fresh set must not appear in listings before its first upload");

        int blankRows = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmaps WHERE set_id = @setId AND filename IS NULL", new { setId });
        Assert.That(blankRows, Is.EqualTo(2), "allocated rows exist but are not live yet");
    }

    [Test]
    [Order(2)]
    public async Task FullPackageUpload_CutsVersionOne_AndPublishes()
    {
        easyOsu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: username, version: "easy", beatmapId: beatmapIds[0], beatmapSetId: setId));
        hardOsu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: username, version: "hard", beatmapId: beatmapIds[1], beatmapSetId: setId, previewTime: 500));
        audio = MakeWav(seconds: 2);
        background = SyntheticPackage.TinyPng();

        using (var zip = SyntheticPackage.Zip(
                   ("easy.osu", easyOsu), ("hard.osu", hardOsu), ("audio.mp3", audio), ("bg.jpg", background)))
            packageZip = zip.ToArray();

        using var response = await SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, PackageBody(packageZip));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using var conn = await BssFixture.OpenDbAsync();

        var set = await conn.QuerySingleAsync<(string Status, string Title, string Artist, string? CoverKey, int CurrentVersion)>(
            """
            SELECT status AS Status, title AS Title, artist AS Artist, cover_key AS CoverKey,
                   current_version AS CurrentVersion
            FROM beatmapsets WHERE id = @setId
            """,
            new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(set.Status, Is.EqualTo("public"), "first successful upload publishes the set");
            Assert.That(set.Title, Is.EqualTo("Neon Nights"));
            Assert.That(set.Artist, Is.EqualTo("Synth Rider"));
            Assert.That(set.CoverKey, Is.EqualTo($"covers/{setId}/1"));
            Assert.That(set.CurrentVersion, Is.EqualTo(1));
        });

        int versions = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });
        Assert.That(versions, Is.EqualTo(1));

        // Both diffs are live, upserted onto the pre-allocated rows with the real checksums.
        var diffs = (await conn.QueryAsync<(long Id, string? Filename, string Checksum)>(
            "SELECT id AS Id, filename AS Filename, checksum_md5 AS Checksum FROM beatmaps WHERE set_id = @setId ORDER BY id",
            new { setId })).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(diffs.Select(d => d.Id), Is.EquivalentTo(beatmapIds));
            Assert.That(diffs.Select(d => d.Filename), Is.EquivalentTo(new[] { "easy.osu", "hard.osu" }));
            Assert.That(diffs.Single(d => d.Filename == "easy.osu").Checksum,
                Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(easyOsu))));
        });

        // Blobs, assembled package and all eight cover buckets landed in the file store.
        Assert.Multiple(() =>
        {
            Assert.That(StoredFile($"files/{Sha256Hex(easyOsu)}"), Does.Exist);
            Assert.That(StoredFile($"files/{Sha256Hex(audio)}"), Does.Exist);
            Assert.That(StoredFile($"packages/{setId}/1.typb"), Does.Exist);

            foreach (string name in new[] { "card", "card@2x", "cover", "cover@2x", "list", "list@2x", "slimcover", "slimcover@2x" })
                Assert.That(StoredFile($"covers/{setId}/1/{name}.jpg"), Does.Exist, name);
        });
    }

    [Test]
    [Order(3)]
    public async Task SecondPut_ReturnsLatestVersionManifest()
    {
        using var response = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = setId,
            beatmaps_to_create = 0,
            beatmaps_to_keep = beatmapIds,
            target = "Pending",
            notify_on_discussion_replies = true,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That((long)body["beatmapset_id"]!, Is.EqualTo(setId));
            // Upstream contract: beatmap_ids = created + KEPT ids. The client exporter resolves
            // every kept diff's online id by membership in this list, so a keep-only PUT must
            // echo the kept ids back (an empty list hard-fails every re-submission at export).
            Assert.That(body["beatmap_ids"]!.Select(t => (long)t), Is.EquivalentTo(beatmapIds));
        });

        // files[] = the latest version manifest the client hash-diffs for its PATCH.
        var files = body["files"]!
            .ToDictionary(f => (string)f["filename"]!, f => (string)f["sha2_hash"]!);

        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Count.EqualTo(4));
            Assert.That(files["easy.osu"], Is.EqualTo(Sha256Hex(easyOsu)));
            Assert.That(files["hard.osu"], Is.EqualTo(Sha256Hex(hardOsu)));
            Assert.That(files["audio.mp3"], Is.EqualTo(Sha256Hex(audio)));
            Assert.That(files["bg.jpg"], Is.EqualTo(Sha256Hex(background)));
        });
    }

    [Test]
    [Order(4)]
    public async Task IdenticalReupload_DoesNotCutANewVersion()
    {
        using var response = await SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, PackageBody(packageZip));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using var conn = await BssFixture.OpenDbAsync();

        int versions = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });
        Assert.That(versions, Is.EqualTo(1), "identical content must only touch updated_at");
    }

    [Test]
    [Order(5)]
    public async Task Patch_CutsVersionTwo_ReusingUnchangedBlobs()
    {
        long filesBefore;

        await using (var conn = await BssFixture.OpenDbAsync())
            filesBefore = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");

        byte[] readme = SyntheticPackage.Utf8("patched-in file");

        using var body = new MultipartFormDataContent();
        var changed = new ByteArrayContent(readme);
        changed.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(changed, "filesChanged", "extra/readme.txt");
        body.Add(new StringContent("bg.jpg"), "filesDeleted");

        using var response = await SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using (var conn = await BssFixture.OpenDbAsync())
        {
            var latest = await conn.QuerySingleAsync<(int VersionNo, long VersionId)>(
                """
                SELECT version_no AS VersionNo, id AS VersionId FROM set_versions
                WHERE set_id = @setId ORDER BY version_no DESC LIMIT 1
                """,
                new { setId });

            Assert.That(latest.VersionNo, Is.EqualTo(2));

            var manifest = (await conn.QueryAsync<string>(
                "SELECT filename FROM version_files WHERE version_id = @versionId", new { latest.VersionId })).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(manifest, Is.EquivalentTo(new[] { "easy.osu", "hard.osu", "audio.mp3", "extra/readme.txt" }));
            });

            long filesAfter = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");
            Assert.That(filesAfter - filesBefore, Is.EqualTo(1), "unchanged blobs are reused, only the new file inserts");
        }

        Assert.That(StoredFile($"packages/{setId}/2.typb"), Does.Exist);
    }

    [Test]
    [Order(6)]
    public async Task BeatmapsetGet_HasTheAPIBeatmapSetShape()
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v2/beatmapsets/{setId}", bearer);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));

        var set = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That((long)set["id"]!, Is.EqualTo(setId));
            Assert.That((string)set["title"]!, Is.EqualTo("Neon Nights"));
            Assert.That((string)set["title_unicode"]!, Is.EqualTo("Neon Nights"));
            Assert.That((string)set["artist"]!, Is.EqualTo("Synth Rider"));
            Assert.That((string)set["status"]!, Is.EqualTo("ranked"));
            Assert.That((string)set["creator"]!, Is.EqualTo(username));
            Assert.That((long)set["user_id"]!, Is.EqualTo(userId));
            Assert.That((int)set["play_count"]!, Is.EqualTo(0));
            Assert.That((int)set["favourite_count"]!, Is.EqualTo(0));
            Assert.That((bool)set["has_favourited"]!, Is.False);
            Assert.That((double)set["bpm"]!, Is.EqualTo(120).Within(1e-6));
            Assert.That(set["submitted_date"]!.Type, Is.Not.EqualTo(JTokenType.Null));
            Assert.That(set["last_updated"]!.Type, Is.Not.EqualTo(JTokenType.Null));
        });

        // Real covers: the version-keyed URLs served by MediaEndpoints, not the placeholder.
        Assert.That((string)set["covers"]!["card@2x"]!, Does.EndWith($"/covers/{setId}/1/card@2x.jpg"));

        // Previews degrade gracefully: with ffmpeg available the WAV clip generates and the URL
        // is real; without it the field stays an empty string (never null — client contract).
        string previewUrl = (string)set["preview_url"]!;
        if (PreviewGenerator.IsFfmpegAvailable())
            Assert.That(previewUrl, Does.EndWith($"/previews/{setId}.mp3"));
        else
            Assert.That(previewUrl, Is.EqualTo(""));

        var beatmaps = (JArray)set["beatmaps"]!;
        Assert.That(beatmaps, Has.Count.EqualTo(2));

        var easy = beatmaps.Single(b => (string)b["version"]! == "easy");

        Assert.Multiple(() =>
        {
            Assert.That((long)easy["id"]!, Is.EqualTo(beatmapIds[0]));
            Assert.That((long)easy["beatmapset_id"]!, Is.EqualTo(setId));
            Assert.That((string)easy["status"]!, Is.EqualTo("ranked"));
            Assert.That((string)easy["checksum"]!, Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(easyOsu))));
            Assert.That((double)easy["difficulty_rating"]!, Is.GreaterThan(0));
            Assert.That(easy["beatmapset"], Is.Null, "nested beatmaps omit the back-reference key entirely");
        });
    }

    [Test]
    [Order(7)]
    public async Task AnonymousDownload_StreamsLatestPackage_AndCounts()
    {
        using var response = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string? disposition = response.Content.Headers.ContentDisposition?.ToString();
        Assert.That(disposition, Does.Contain("Synth Rider - Neon Nights.typb"));

        // The streamed zip is the reassembled latest version, bit-exact per content identity.
        using var payload = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        var reparsed = BeatmapPackageParser.Parse(payload);

        Assert.That(
            reparsed.Files.Select(f => f.Filename),
            Is.EquivalentTo(new[] { "easy.osu", "hard.osu", "audio.mp3", "extra/readme.txt" }));

        await using var conn = await BssFixture.OpenDbAsync();

        int downloadCount = await conn.ExecuteScalarAsync<int>(
            "SELECT download_count FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(downloadCount, Is.EqualTo(1));

        var logged = await conn.QuerySingleAsync<(long? UserId, long SetId)>(
            "SELECT user_id AS UserId, set_id AS SetId FROM beatmapset_downloads WHERE set_id = @setId",
            new { setId });
        Assert.Multiple(() =>
        {
            Assert.That(logged.UserId, Is.Null, "anonymous download logs a NULL user (migration 003)");
            Assert.That(logged.SetId, Is.EqualTo(setId));
        });
    }

    [Test]
    [Order(8)]
    public async Task RangedContinuations_DoNotInflateTheDownloadCount()
    {
        int countBefore = await DownloadCountAsync();

        // A resume / download-manager segment: mid-file range → 206, NOT counted.
        using (var resume = new HttpRequestMessage(HttpMethod.Get, $"/beatmapsets/{setId}/download"))
        {
            resume.Headers.Range = new RangeHeaderValue(100, null);

            using var response = await BssFixture.Client.SendAsync(resume);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        }

        Assert.That(await DownloadCountAsync(), Is.EqualTo(countBefore),
            "a non-zero-start range is a continuation of an already-counted download");

        // The segment covering the start of the file is the one that counts — exactly once,
        // so an 8-way segmented download totals one, not eight.
        using (var first = new HttpRequestMessage(HttpMethod.Get, $"/beatmapsets/{setId}/download"))
        {
            first.Headers.Range = new RangeHeaderValue(0, 99);

            using var response = await BssFixture.Client.SendAsync(first);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        }

        Assert.That(await DownloadCountAsync(), Is.EqualTo(countBefore + 1));

        async Task<int> DownloadCountAsync()
        {
            await using var conn = await BssFixture.OpenDbAsync();
            return await conn.ExecuteScalarAsync<int>(
                "SELECT download_count FROM beatmapsets WHERE id = @setId", new { setId });
        }
    }

    [Test]
    [Order(9)]
    public async Task Favourites_ReadsRealRows()
    {
        await using (var conn = await BssFixture.OpenDbAsync())
        {
            await conn.ExecuteAsync(
                "INSERT INTO favourites (user_id, set_id) VALUES (@userId, @setId)",
                new { userId, setId });
        }

        using var response = await SendAsync(HttpMethod.Get, "/api/v2/me/beatmapset-favourites", bearer);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo($"{{\"beatmapset_ids\":[{setId}]}}"));
        });
    }

    [Test]
    [Order(10)]
    public async Task CoverRoutes_ServeJpegs_WithWhitelistAndFallback()
    {
        using (var cover = await BssFixture.Client.GetAsync($"/covers/{setId}/1/card.jpg"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(cover.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(cover.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));

                // Bounded one-day TTL, never immutable: the cover key is not status-keyed, so a
                // DMCA status flip must become visible to edges/browsers within a day.
                Assert.That(cover.Headers.CacheControl?.ToString(), Does.Contain("max-age=86400"));
                Assert.That(cover.Headers.CacheControl?.ToString(), Does.Not.Contain("immutable"));
            });
        }

        using (var offList = await BssFixture.Client.GetAsync($"/covers/{setId}/1/original.jpg"))
            Assert.That(offList.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "only the eight generated bucket names are servable");

        // The self-hosted fallback referenced by the lookup/beatmapset DTOs (was a 404 in M1).
        using (var fallback = await BssFixture.Client.GetAsync("/img/default-cover.jpg"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(fallback.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(fallback.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
            });
        }
    }

    [Test]
    [Order(11)]
    public async Task PreviewRoute_ServesTheClip_WithRangeSupport()
    {
        if (!PreviewGenerator.IsFfmpegAvailable())
            Assert.Ignore("ffmpeg not on PATH — no preview was generated for this set.");

        using (var response = await BssFixture.Client.GetAsync($"/previews/{setId}.mp3"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("audio/mpeg"));
                Assert.That(response.Headers.AcceptRanges, Does.Contain("bytes"));
            });
        }

        using (var missing = await BssFixture.Client.GetAsync("/previews/99999999.mp3"))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    internal static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string bearer, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = content;

        return await BssFixture.Client.SendAsync(request);
    }

    internal static StringContent JsonBody(object payload)
        => new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8, "application/json");

    /// <summary>The full-upload multipart body: one "beatmapArchive" file part.</summary>
    internal static MultipartFormDataContent PackageBody(byte[] zipBytes)
    {
        var content = new MultipartFormDataContent();
        var archive = new ByteArrayContent(zipBytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(archive, "beatmapArchive", "package.osz");
        return content;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string StoredFile(string key)
        => Path.Combine(BssFixture.FileRoot, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A minimal PCM16 mono WAV (ffmpeg-decodable, so previews really generate).</summary>
    internal static byte[] MakeWav(int seconds)
    {
        const int sample_rate = 8000;
        int samples = sample_rate * seconds;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);              // fmt chunk size
        writer.Write((short)1);        // PCM
        writer.Write((short)1);        // mono
        writer.Write(sample_rate);
        writer.Write(sample_rate * 2); // byte rate
        writer.Write((short)2);        // block align
        writer.Write((short)16);       // bits per sample
        writer.Write("data"u8);
        writer.Write(samples * 2);

        for (int i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sample_rate) * 8000));

        writer.Flush();
        return buffer.ToArray();
    }
}
