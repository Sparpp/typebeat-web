using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dapper;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Regression coverage for the adversarially-verified M3 BSS findings:
///
///  - PUT /bss/beatmapsets must return created + KEPT beatmap ids (the client exporter resolves
///    kept diffs by membership in that list);
///  - a no-change resubmission PATCH (no body / non-form body / empty delta) is a graceful
///    no-op 204, never a raw 500, while malformed form bodies still get the 422 envelope;
///  - a case-only rename (bg.jpg → bg.JPG as filesChanged+filesDeleted) must keep the file
///    under its NEW name — exact case-sensitive delta semantics matching the client diff;
///  - two concurrent full uploads to one set serialize: no interleaved state, versions cut
///    sequentially, the final DB state is wholly one upload's content;
///  - a version row implies its download package object exists (assembled before commit), and
///    a historical dangling package_key heals on an identical resubmission.
///
/// Each test creates its own user + set so the per-user upload rate limit and the shared-DB
/// fixture state never couple tests together.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssDeltaAndConcurrencyTest
{
    // ---------------------------------------------------------------------------------------------
    // beatmap_ids response shape (finding: kept ids omitted broke every re-submission).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task KeepAndCreatePut_ReturnsCreatedPlusKeptIds()
    {
        var (bearer, setId, ids) = await CreateUploadedSetAsync("kept ids user", diffCount: 2);

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                beatmapset_id = setId,
                beatmaps_to_create = 1,
                beatmaps_to_keep = ids,
                target = "Pending",
                notify_on_discussion_replies = false,
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        long[] returned = body["beatmap_ids"]!.Select(t => (long)t).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(returned, Has.Length.EqualTo(3), "one created + two kept");
            Assert.That(returned, Is.SupersetOf(ids), "kept ids MUST be echoed back — the client exporter resolves kept diffs by membership");
            Assert.That(returned.Except(ids).Count(), Is.EqualTo(1), "exactly one freshly allocated id");
            Assert.That(returned.Except(ids).Single(), Is.Not.AnyOf(ids[0], ids[1]));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Empty / body-less / non-form PATCH (finding: raw 500 out of ReadFormAsync).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task NoChangePatch_IsAGracefulNoOp_NeverA500()
    {
        var (bearer, setId, _) = await CreateUploadedSetAsync("empty patch user", diffCount: 1);

        // 1. The real client's no-change resubmission: literally no body, no Content-Type.
        using (var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "a body-less PATCH is an empty delta = identical rebuild = no-op");

        // 2. A well-formed multipart body whose delta is empty (a part unrelated to
        //    filesChanged/filesDeleted; RFC 2046 requires at least one part, so THIS is the
        //    smallest valid multipart with an empty delta).
        using (var body = new MultipartFormDataContent { { new StringContent("Pending"), "target" } })
        using (var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, body))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "an empty form delta is a no-op");

        // 3. A urlencoded form (parses to zero files, zero deletes).
        using (var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer,
                   new FormUrlEncodedContent(new Dictionary<string, string>())))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "an empty urlencoded form is an empty delta");

        // 4. A non-form body (wrong Content-Type entirely) — still not a 500.
        using (var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer,
                   new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "non-form content is treated as an empty delta");

        // None of the no-ops may have cut a version.
        await using (var conn = await BssFixture.OpenDbAsync())
        {
            int versions = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });
            Assert.That(versions, Is.EqualTo(1), "no-op PATCHes must not cut versions");
        }

        // 5. MALFORMED form bodies are client errors: 422 with the {"error"} envelope — never a
        //    raw 500. Both a multipart Content-Type without a boundary and a zero-part
        //    multipart body (invalid per RFC 2046 — a multipart needs at least one part).
        var missingBoundary = new ByteArrayContent([1, 2, 3]);
        missingBoundary.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");

        foreach (HttpContent malformed in new HttpContent[] { missingBoundary, new MultipartFormDataContent() })
        {
            using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, malformed);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.That((string?)body["error"], Is.Not.Null.And.Not.Empty, "errors must use the wire envelope");
        }
    }

    [Test]
    public async Task BodylessPatch_OnASetWithNoVersion_Still422s()
    {
        var (_, bearer) = await BssFixture.CreateUserAsync("empty patch noversion", verified: true);
        var (setId, _) = await CreateSetShellAsync(bearer, diffCount: 1);

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity),
            "there is nothing to rebuild — the no-version guard must still fire");
    }

    // ---------------------------------------------------------------------------------------------
    // Case-only rename via PATCH (finding: delete swallowed the replacement; file vanished).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task CaseOnlyRenamePatch_KeepsTheFileUnderItsNewName()
    {
        var (bearer, setId, _) = await CreateUploadedSetAsync("case rename user", diffCount: 1);

        // The client's diff for bg.jpg → bg.JPG: case-sensitive matching yields BOTH a change
        // (new name) and a delete (old name). The new name must survive the rebuild.
        using var body = new MultipartFormDataContent();
        var renamed = new ByteArrayContent(SyntheticPackage.TinyPng());
        renamed.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(renamed, "filesChanged", "bg.JPG");
        body.Add(new StringContent("bg.jpg"), "filesDeleted");

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using var conn = await BssFixture.OpenDbAsync();

        var latest = await conn.QuerySingleAsync<(int VersionNo, long VersionId)>(
            """
            SELECT version_no AS VersionNo, id AS VersionId FROM set_versions
            WHERE set_id = @setId ORDER BY version_no DESC LIMIT 1
            """,
            new { setId });

        Assert.That(latest.VersionNo, Is.EqualTo(2), "a rename is a manifest change — a new version is cut");

        var manifest = (await conn.QueryAsync<string>(
            "SELECT filename FROM version_files WHERE version_id = @versionId", new { versionId = latest.VersionId })).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("bg.JPG"), "the renamed file must survive under its NEW casing");
            Assert.That(manifest, Does.Not.Contain("bg.jpg"), "the old casing was deleted");
            Assert.That(manifest, Is.EquivalentTo(new[] { "map1.osu", "audio.mp3", "bg.JPG" }));
        });

        Assert.That(StoredFile($"packages/{setId}/2.osz"), Does.Exist, "the new version's package was assembled before commit");
    }

    // ---------------------------------------------------------------------------------------------
    // Concurrent full uploads to one set (finding: post-commit refresh/covers raced; lost diffs).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task ParallelFullUploads_Serialize_WithNoInterleavedState()
    {
        var (_, bearer) = await BssFixture.CreateUserAsync("parallel uploader", verified: true);
        var (setId, ids) = await CreateSetShellAsync(bearer, diffCount: 2);

        var packageA = BuildPackageEntries("parallel uploader", setId, ids, previewTime: -1);
        var packageB = BuildPackageEntries("parallel uploader", setId, ids, previewTime: 1234);

        byte[] zipA, zipB;
        using (var zip = SyntheticPackage.Zip(packageA)) zipA = zip.ToArray();
        using (var zip = SyntheticPackage.Zip(packageB)) zipB = zip.ToArray();

        // Fire both uploads at once. The per-set ingest lock must serialize them fully.
        var taskA = BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipA));
        var taskB = BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipB));

        using var responseA = await taskA;
        using var responseB = await taskB;

        Assert.Multiple(() =>
        {
            Assert.That(responseA.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(responseB.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        });

        await using var conn = await BssFixture.OpenDbAsync();

        var versions = (await conn.QueryAsync<(long Id, int VersionNo)>(
            "SELECT id AS Id, version_no AS VersionNo FROM set_versions WHERE set_id = @setId ORDER BY version_no",
            new { setId })).ToList();

        Assert.That(versions.Select(v => v.VersionNo), Is.EqualTo(new[] { 1, 2 }),
            "different contents serialized under the set lock cut exactly versions 1 and 2");

        int currentVersion = await conn.ExecuteScalarAsync<int>(
            "SELECT current_version FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(currentVersion, Is.EqualTo(2));

        // Both versions' packages must exist (a committed version implies a durable package).
        Assert.Multiple(() =>
        {
            Assert.That(StoredFile($"packages/{setId}/1.osz"), Does.Exist);
            Assert.That(StoredFile($"packages/{setId}/2.osz"), Does.Exist);
        });

        // Determine which upload owns the CURRENT version by its manifest, then require the
        // whole visible state — beatmap checksums, liveness, cover key — to match that upload
        // wholly. Any mix of A and B is the interleaving this test exists to catch.
        var currentManifest = (await conn.QueryAsync<byte[]>(
            "SELECT vf.sha256 FROM version_files vf WHERE vf.version_id = @versionId",
            new { versionId = versions[1].Id }))
            .Select(Convert.ToHexStringLower)
            .ToHashSet();

        var winner = currentManifest.SetEquals(packageA.Select(e => Sha256Hex(e.Content))) ? packageA
            : currentManifest.SetEquals(packageB.Select(e => Sha256Hex(e.Content))) ? packageB
            : throw new InvalidOperationException("current version matches neither upload — interleaved manifest");

        var diffs = (await conn.QueryAsync<(long Id, string? Filename, string Checksum)>(
            "SELECT id AS Id, filename AS Filename, checksum_md5 AS Checksum FROM beatmaps WHERE set_id = @setId ORDER BY id",
            new { setId })).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(diffs.Select(d => d.Id), Is.EquivalentTo(ids));
            Assert.That(diffs.Select(d => d.Filename), Is.EquivalentTo(new[] { "map1.osu", "map2.osu" }),
                "both diffs stay live — the loser's liveness refresh must not NULL the winner's diffs");
            Assert.That(diffs.Select(d => d.Checksum),
                Is.EquivalentTo(winner.Where(e => e.Name.EndsWith(".osu")).Select(e => Md5Hex(e.Content))),
                "beatmap rows must wholly match the upload that owns the current version");
        });

        string? coverKey = await conn.ExecuteScalarAsync<string?>(
            "SELECT cover_key FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(coverKey, Is.EqualTo($"covers/{setId}/2"), "covers must track the CURRENT version, never an older upload's");
    }

    // ---------------------------------------------------------------------------------------------
    // Dangling package repair (finding: missing package object could never heal).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task IdenticalResubmission_ReassemblesAMissingPackageObject()
    {
        var (bearer, setId, ids) = await CreateUploadedSetAsync("heal uploader", diffCount: 1);

        string packagePath = StoredFile($"packages/{setId}/1.osz");
        Assert.That(packagePath, Does.Exist, "sanity: the first upload assembled its package");

        // Simulate a version row whose package object is gone (pre-invariant data / operator
        // damage). The mapper's natural reaction is to re-submit the same content.
        File.Delete(packagePath);

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(BuildPackageEntries("heal uploader", setId, ids, previewTime: -1)))
            zipBytes = zip.ToArray();

        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipBytes));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using (var conn = await BssFixture.OpenDbAsync())
        {
            int versions = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });
            Assert.That(versions, Is.EqualTo(1), "identical content still must not cut a version");
        }

        Assert.That(packagePath, Does.Exist, "the identical-content path must reassemble a missing package from version_files");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>PUT-creates a set shell with <paramref name="diffCount"/> allocated ids (no upload).</summary>
    private static async Task<(long SetId, long[] Ids)> CreateSetShellAsync(string bearer, int diffCount)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                beatmapset_id = (long?)null,
                beatmaps_to_create = diffCount,
                beatmaps_to_keep = Array.Empty<long>(),
                target = "WIP",
                notify_on_discussion_replies = false,
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).ToArray());
    }

    /// <summary>Fresh verified user + set + successful full v1 upload (map1.osu.., audio.mp3, bg.jpg).</summary>
    private static async Task<(string Bearer, long SetId, long[] Ids)> CreateUploadedSetAsync(string username, int diffCount)
    {
        var (_, bearer) = await BssFixture.CreateUserAsync(username, verified: true);
        var (setId, ids) = await CreateSetShellAsync(bearer, diffCount);

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(BuildPackageEntries(username, setId, ids, previewTime: -1)))
            zipBytes = zip.ToArray();

        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipBytes));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "v1 upload must succeed");

        return (bearer, setId, ids);
    }

    /// <summary>
    /// One package: map{N}.osu per allocated id (ids embedded, so bytes/checksums are unique
    /// per set — checksum_md5 is globally unique), one fake audio, one decodable bg.
    /// <paramref name="previewTime"/> varies the .osu bytes to make distinct package contents.
    /// </summary>
    private static (string Name, byte[] Content)[] BuildPackageEntries(string creator, long setId, long[] ids, double previewTime)
    {
        var entries = new List<(string Name, byte[] Content)>();

        for (int i = 0; i < ids.Length; i++)
        {
            entries.Add(($"map{i + 1}.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                creator: creator, version: $"diff {i + 1}", beatmapId: ids[i], beatmapSetId: setId, previewTime: previewTime))));
        }

        entries.Add(("audio.mp3", SyntheticPackage.Utf8($"fake audio for set {setId}")));
        entries.Add(("bg.jpg", SyntheticPackage.TinyPng()));

        return entries.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    private static string StoredFile(string key)
        => Path.Combine(BssFixture.FileRoot, key.Replace('/', Path.DirectorySeparatorChar));
}
