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
/// manifest → identical re-upload (no version) → PATCH delta (new version, blobs reused),
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

    // The second set, published straight to 'unranked' by Order(12) and served by Order(13).
    private long unrankedSetId;

    // The video-bearing set uploaded by Order(14), which the audio-only cases read.
    private const string video_file = "clip.mp4";
    private long videoSetId;
    private byte[] videoOsu = null!;

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
            Assert.That(set.Status, Is.EqualTo("pending"), "first successful upload publishes the set as pending (awaiting review)");
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
            Assert.That((string)set["status"]!, Is.EqualTo("pending"), "a fresh upload is pending until a reviewer ranks it");
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
        // is real; without it the field stays an empty string (never null; client contract).
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
            Assert.That((string)easy["status"]!, Is.EqualTo("pending"));
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

        // The segment covering the start of the file is the one that counts, exactly once,
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
            Assert.Ignore("ffmpeg not on PATH, no preview was generated for this set.");

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

    [Test]
    [Order(12)]
    public async Task UnrankedTarget_PublishesAsUnranked_AndReTargetsBothWays()
    {
        // A brand-new set whose creator picks "Unranked" in the submission wizard.
        using var create = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Unranked",
            notify_on_discussion_replies = false,
        }));
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await create.Content.ReadAsStringAsync());
        unrankedSetId = (long)body["beatmapset_id"]!;
        long diffId = body["beatmap_ids"]!.Select(t => (long)t).First();

        await using var conn = await BssFixture.OpenDbAsync();

        // Intent is recorded immediately; the set is still an unpublished shell.
        Assert.That(await conn.ExecuteScalarAsync<string>("SELECT intended_status FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId }),
            Is.EqualTo("unranked"));
        Assert.That(await conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId }),
            Is.EqualTo("hidden"), "still hidden until the first upload");

        // First upload publishes straight to 'unranked', not 'pending'.
        var osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: username, version: "solo", beatmapId: diffId, beatmapSetId: unrankedSetId));
        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(("solo.osu", osu), ("audio.mp3", MakeWav(seconds: 1)), ("bg.jpg", SyntheticPackage.TinyPng())))
            zipBytes = zip.ToArray();

        using (var upload = await SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{unrankedSetId}", bearer, PackageBody(zipBytes)))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(conn.ExecuteScalar<string>("SELECT status FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId }),
            Is.EqualTo("unranked"), "the creator opted out of ranking, so publication lands as unranked");

        // Re-submitting with target Pending moves an already-published, non-ranked set to pending.
        using (var toPending = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = unrankedSetId,
            beatmaps_to_create = 0,
            beatmaps_to_keep = new[] { diffId },
            target = "Pending",
            notify_on_discussion_replies = false,
        })))
            Assert.That(toPending.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(conn.ExecuteScalar<string>("SELECT status FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId }),
            Is.EqualTo("pending"), "changing the target to Pending re-targets a published set");

        // ...and back to Unranked.
        using (var toUnranked = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = unrankedSetId,
            beatmaps_to_create = 0,
            beatmaps_to_keep = new[] { diffId },
            target = "Unranked",
            notify_on_discussion_replies = false,
        })))
            Assert.That(toUnranked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(conn.ExecuteScalar<string>("SELECT status FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId }),
            Is.EqualTo("unranked"));
    }

    /// <summary>
    /// 'unranked' is a PUBLISHED status, so its package, covers and preview are world-readable
    /// exactly like 'pending' and 'ranked'. Anonymous is the load-bearing part of every case here:
    /// the gate used to fall through to an owner-only branch, which passed for the uploader and
    /// 404'd the website's download button and the client's /api/v2 fetch for everyone else.
    /// The second half pins the security boundary the same predicate carries.
    /// </summary>
    [Test]
    [Order(13)]
    public async Task UnrankedSet_ServesPackageAndMediaAnonymously_ButStaysOwnerOnlyWhenUnpublished()
    {
        Assert.That(unrankedSetId, Is.GreaterThan(0), "Order(12) publishes the unranked set this case reads.");

        using (var download = await BssFixture.Client.GetAsync($"/beatmapsets/{unrankedSetId}/download"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the website download button on an unranked set");
                Assert.That(download.Content.Headers.ContentDisposition?.ToString(), Does.Contain(".typb"));
            });
        }

        // The game client (DownloadBeatmapSetRequest) builds the /api/v2 alias, same handler, same gate.
        using (var viaClientRoute = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{unrankedSetId}/download"))
            Assert.That(viaClientRoute.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the in-game download of an unranked set");

        // ...and the same alias with the flag the client appends for a player who prefers no video.
        using (var noVideo = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{unrankedSetId}/download?noVideo=1"))
            Assert.That(noVideo.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the in-game audio-only download of an unranked set");

        // Covers and previews run through the same status check, so the listing card art and the
        // preview button on an unranked set have to work for a signed-out visitor too.
        using (var cover = await BssFixture.Client.GetAsync($"/covers/{unrankedSetId}/1/card.jpg"))
            Assert.That(cover.StatusCode, Is.EqualTo(HttpStatusCode.OK), "an unranked set's cover");

        // The browser player's media gate is a third copy of the same status check. The listing
        // only offers webplay on ranked sets today, but the route is reachable by URL, so the
        // copy is testable rather than merely latent.
        using (var webplayOsu = await BssFixture.Client.GetAsync($"/play/map/{unrankedSetId}/osu"))
            Assert.That(webplayOsu.StatusCode, Is.EqualTo(HttpStatusCode.OK), "an unranked set's /play .osu");

        await using var conn = await BssFixture.OpenDbAsync();

        // preview_key is written only when a clip really generated, so a null here means ffmpeg
        // was absent, not that the gate refused. Asserting past it would be flaky, not stricter.
        string? previewKey = await conn.ExecuteScalarAsync<string?>(
            "SELECT preview_key FROM beatmapsets WHERE id = @unrankedSetId", new { unrankedSetId });

        if (previewKey is not null)
        {
            using var preview = await BssFixture.Client.GetAsync($"/previews/{unrankedSetId}.mp3");
            Assert.That(preview.StatusCode, Is.EqualTo(HttpStatusCode.OK), "an unranked set's preview clip");
        }

        // The boundary, on the SAME set and the SAME stored package, with only the status column
        // moved: 'hidden' is an unpublished shell and 'removed' is a takedown, and both must keep
        // 404ing for everyone but the owner. This is what stops a later widening of the published
        // list (say to "not hidden") from quietly re-serving a DMCA'd set's bytes and artwork.
        foreach (string unpublished in new[] { "hidden", "removed" })
        {
            await conn.ExecuteAsync(
                "UPDATE beatmapsets SET status = @unpublished WHERE id = @unrankedSetId",
                new { unpublished, unrankedSetId });

            using (var download = await BssFixture.Client.GetAsync($"/beatmapsets/{unrankedSetId}/download"))
                Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"anonymous download of a '{unpublished}' set");

            // The audio-only flag is a variant of this route, not a fourth path around its gate:
            // both spellings of it 404 exactly like the full download does.
            using (var noVideo = await BssFixture.Client.GetAsync($"/beatmapsets/{unrankedSetId}/download?noVideo=1"))
                Assert.That(noVideo.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"anonymous audio-only download of a '{unpublished}' set");

            using (var apiNoVideo = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{unrankedSetId}/download?noVideo=1"))
                Assert.That(apiNoVideo.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"in-game audio-only download of a '{unpublished}' set");

            using (var sizes = await BssFixture.Client.GetAsync($"/beatmapsets/{unrankedSetId}/download-sizes"))
                Assert.That(sizes.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"anonymous sizes of a '{unpublished}' set");

            using (var cover = await BssFixture.Client.GetAsync($"/covers/{unrankedSetId}/1/card.jpg"))
                Assert.That(cover.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"anonymous cover of a '{unpublished}' set");

            using (var webplayOsu = await BssFixture.Client.GetAsync($"/play/map/{unrankedSetId}/osu"))
                Assert.That(webplayOsu.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"anonymous /play .osu of a '{unpublished}' set");

            // Owner-only, not gone: the uploader still reaches their own withheld set.
            using (var owned = await SendAsync(HttpMethod.Get, $"/beatmapsets/{unrankedSetId}/download", bearer))
                Assert.That(owned.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"the owner still downloads their own '{unpublished}' set");
        }

        await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = 'unranked' WHERE id = @unrankedSetId", new { unrankedSetId });
    }

    /// <summary>
    /// The audio-only download variant (?noVideo=1), and THE invariant that makes it safe: the
    /// video FILE is left out of the archive and nothing else changes, so every .osu entry is
    /// byte-identical to the full package's. beatmaps.checksum_md5 is the MD5 of those bytes and is
    /// the beatmap's leaderboard identity, so a variant that rewrote the .osu (stripping the
    /// [Events] Video line, say) would mint a different beatmap and orphan every score on it. The
    /// dangling Video line is asserted deliberately: it is what the identity costs, and the game
    /// tolerates it.
    /// </summary>
    [Test]
    [Order(14)]
    public async Task AudioOnlyDownload_OmitsTheVideoFile_AndKeepsEveryOsuByteIdentical()
    {
        long diffId;
        (videoSetId, diffId) = await CreateOneDiffSetAsync();

        videoOsu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            title: "Cinema Nights", artist: "Synth Rider", creator: username, version: "cinematic",
            beatmapId: diffId, beatmapSetId: videoSetId, video: video_file));

        // Much the biggest entry, as a real video is: an audio-only package that failed to drop it
        // would be indistinguishable from the full one by size alone.
        byte[] clip = new byte[96 * 1024];
        for (int i = 0; i < clip.Length; i++)
            clip[i] = (byte)(i % 251);

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(
                   ("cinematic.osu", videoOsu), ("audio.mp3", MakeWav(seconds: 1)),
                   ("bg.jpg", SyntheticPackage.TinyPng()), (video_file, clip),
                   // A second .mp4 that NO difficulty calls its video. It is why the filter reads
                   // the .osu's [Events] Video line rather than the file extension: a mapper's own
                   // extra files are not the map's video and must survive the variant.
                   ("bonus.mp4", SyntheticPackage.Utf8("liner notes reel"))))
            zipBytes = zip.ToArray();

        using (var upload = await SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{videoSetId}", bearer, PackageBody(zipBytes)))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using var conn = await BssFixture.OpenDbAsync();

        Assert.That(await conn.ExecuteScalarAsync<bool>(
                "SELECT has_video FROM beatmapsets WHERE id = @videoSetId", new { videoSetId }),
            Is.True, "the [Events] Video line is what makes this a video set");

        var full = await FetchPackageAsync($"/beatmapsets/{videoSetId}/download");
        var audioOnly = await FetchPackageAsync($"/beatmapsets/{videoSetId}/download?noVideo=1");

        Assert.Multiple(() =>
        {
            Assert.That(full.Parsed.Files.Select(f => f.Filename),
                Is.EquivalentTo(new[] { "cinematic.osu", "audio.mp3", "bg.jpg", video_file, "bonus.mp4" }));

            // Exactly one entry fewer, and it is the one the .osu's [Events] Video line names.
            // "bonus.mp4" stays: it is an .mp4, but it is not this map's video, and an extension
            // filter (which would also be a silent copy of the game's SupportedExtensions list)
            // would have thrown it away.
            Assert.That(audioOnly.Parsed.Files.Select(f => f.Filename),
                Is.EquivalentTo(new[] { "cinematic.osu", "audio.mp3", "bg.jpg", "bonus.mp4" }));

            // THE identity invariant, stated three ways: same MD5s as the full package, the same
            // MD5 the uploader's own bytes have, and the same MD5 the leaderboard is keyed on.
            Assert.That(audioOnly.Parsed.Difficulties.Select(d => d.ChecksumMd5),
                Is.EqualTo(full.Parsed.Difficulties.Select(d => d.ChecksumMd5)).AsCollection);
            Assert.That(audioOnly.Parsed.Difficulties.Single().ChecksumMd5,
                Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(videoOsu))));

            // The map still REFERENCES the video it no longer carries. Rewriting that line is
            // exactly what would break the line above.
            Assert.That(audioOnly.Parsed.Difficulties.Single().VideoFilename, Is.EqualTo(video_file));

            Assert.That(audioOnly.Disposition, Does.Contain("[no video].typb"));
            Assert.That(full.Disposition, Does.Contain("Synth Rider - Cinema Nights.typb"));
        });

        Assert.That(await conn.ExecuteScalarAsync<int>(
                "SELECT download_count FROM beatmapsets WHERE id = @videoSetId", new { videoSetId }),
            Is.EqualTo(2), "both options are downloads of this set and both count");
    }

    /// <summary>
    /// The pre-234 guard. A map imported from an mp4 alone names the same file as its audio AND its
    /// video, so an "audio-only" package of it would be a SILENT map. The variant is withdrawn for
    /// such a set: the request falls back to the full package rather than 404ing, because the game
    /// client sends ?noVideo=1 from a saved preference and a preference must never break a download.
    /// </summary>
    [Test]
    [Order(15)]
    public async Task NoVideoRequest_OnAnMp4AsAudioMap_FallsBackToTheFullPackage()
    {
        var (setId, diffId) = await CreateOneDiffSetAsync();

        const string only_file = "song.mp4";

        var osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            title: "One File Only", artist: "Synth Rider", creator: username, version: "single",
            beatmapId: diffId, beatmapSetId: setId,
            audioFilename: only_file, background: null, video: only_file));

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(("single.osu", osu), (only_file, SyntheticPackage.Utf8(new string('m', 4096)))))
            zipBytes = zip.ToArray();

        using (var upload = await SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, PackageBody(zipBytes)))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var served = await FetchPackageAsync($"/beatmapsets/{setId}/download?noVideo=1");

        Assert.Multiple(() =>
        {
            Assert.That(served.Parsed.Files.Select(f => f.Filename),
                Is.EquivalentTo(new[] { "single.osu", only_file }),
                "dropping the only media file would have shipped a silent map");
            Assert.That(served.Disposition, Does.Not.Contain("[no video]"),
                "the full package it really is must not be labelled as the variant");
        });

        // ...and the card is told not to offer the choice in the first place.
        using var sizes = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download-sizes");
        var body = JObject.Parse(await sizes.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(sizes.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((bool)body["audioOnlyAvailable"]!, Is.False);
            Assert.That((long)body["audioOnly"]!, Is.EqualTo((long)body["full"]!));
        });
    }

    /// <summary>
    /// The audio-only arm is filtered live, so it has no Content-Length and cannot seek: range
    /// processing is off there. A Range header on it is therefore ignored (200, the whole variant),
    /// which is also why every request on this arm counts, where
    /// <see cref="RangedContinuations_DoNotInflateTheDownloadCount"/> counts only the one that
    /// covers the start of the file. Two arms, two reasons, one counter.
    /// </summary>
    [Test]
    [Order(16)]
    public async Task AudioOnlyDownload_IgnoresRanges_AndCountsEveryRequest()
    {
        Assert.That(videoSetId, Is.GreaterThan(0), "Order(14) uploads the video set this case reads.");

        int before = await VideoSetDownloadCountAsync();

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/beatmapsets/{videoSetId}/download?noVideo=1"))
        {
            request.Headers.Range = new RangeHeaderValue(100, null);

            using var response = await BssFixture.Client.SendAsync(request);

            using var payload = new MemoryStream(await response.Content.ReadAsByteArrayAsync());

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "not 206: this arm has no ranges to satisfy");
                Assert.That(BeatmapPackageParser.Parse(payload).Files, Has.Count.EqualTo(4),
                    "a mid-file range must not truncate the variant into an unreadable zip");
            });
        }

        Assert.That(await VideoSetDownloadCountAsync(), Is.EqualTo(before + 1),
            "one request = one logical download on the arm that cannot be resumed");

        async Task<int> VideoSetDownloadCountAsync()
        {
            await using var conn = await BssFixture.OpenDbAsync();
            return await conn.ExecuteScalarAsync<int>(
                "SELECT download_count FROM beatmapsets WHERE id = @videoSetId", new { videoSetId });
        }
    }

    /// <summary>
    /// The sizes the card's two options are labelled with: approximate (files.size is the
    /// UNCOMPRESSED entry size; nothing stores a package's real byte count), and split by exactly
    /// the same .osu-derived rule the download uses, so the label can never promise a saving the
    /// download does not make.
    /// </summary>
    [Test]
    [Order(17)]
    public async Task DownloadSizes_SplitTheManifestByTheSameVideoRule()
    {
        Assert.That(videoSetId, Is.GreaterThan(0), "Order(14) uploads the video set this case reads.");

        using var response = await BssFixture.Client.GetAsync($"/beatmapsets/{videoSetId}/download-sizes");
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        await using var conn = await BssFixture.OpenDbAsync();

        long manifestTotal = await conn.ExecuteScalarAsync<long>(
            """
            SELECT sum(f.size)
            FROM set_versions sv
            JOIN version_files vf ON vf.version_id = sv.id
            JOIN files f ON f.sha256 = vf.sha256
            WHERE sv.set_id = @videoSetId
              AND sv.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = @videoSetId)
            """,
            new { videoSetId });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((bool)body["audioOnlyAvailable"]!, Is.True);
            Assert.That((long)body["full"]!, Is.EqualTo(manifestTotal));
            Assert.That((long)body["full"]! - (long)body["audioOnly"]!, Is.EqualTo(96 * 1024),
                "the saving is exactly the video entry");
        });

        // The same gate as the download: an unpublished set does not leak its package size either.
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'hidden' WHERE id = @videoSetId", new { videoSetId });

        using (var hidden = await BssFixture.Client.GetAsync($"/beatmapsets/{videoSetId}/download-sizes"))
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "anonymous sizes of a hidden set");

        using (var owned = await SendAsync(HttpMethod.Get, $"/beatmapsets/{videoSetId}/download-sizes", bearer))
            Assert.That(owned.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the owner still sees their own");

        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'pending' WHERE id = @videoSetId", new { videoSetId });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Allocates a fresh one-difficulty set through the real BSS create call.</summary>
    private async Task<(long SetId, long DiffId)> CreateOneDiffSetAsync()
    {
        using var create = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            notify_on_discussion_replies = false,
        }));

        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await create.Content.ReadAsStringAsync());

        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).First());
    }

    /// <summary>Downloads a package and reparses it, the only way to assert on what really shipped.</summary>
    private static async Task<(ParsedPackage Parsed, string Disposition)> FetchPackageAsync(string url)
    {
        using var response = await BssFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);

        using var payload = new MemoryStream(await response.Content.ReadAsByteArrayAsync());

        return (BeatmapPackageParser.Parse(payload), response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);
    }

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
