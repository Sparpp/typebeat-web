using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// The chunked upload-session transport: the same two payloads the direct PUT/PATCH routes take,
/// delivered 8 KB at a time because some clients' networks black-hole any single request past
/// roughly 20 KB. What matters here is that the session path lands EXACTLY what the direct path
/// lands (version cut, stored package, blob reuse), that a half-finished session is resumable,
/// and that every way of getting it wrong is rejected without destroying recoverable state.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssUploadSessionTest
{
    private const string flow_username = "session uploader";

    private string flowBearer = null!;
    private long flowSetId;
    private long[] flowBeatmapIds = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_, flowBearer) = await BssFixture.CreateUserAsync(flow_username, verified: true);
        (flowSetId, flowBeatmapIds) = await CreateSetAsync(flowBearer, diffs: 2);
    }

    // ---------------------------------------------------------------------------------------------
    // The two real flows, mirroring the direct-upload tests' post-conditions.
    // ---------------------------------------------------------------------------------------------

    [Test]
    [Order(1)]
    public async Task FullSession_SentInChunks_CutsVersionOne_AndPublishes()
    {
        byte[] package = BuildPackage(flow_username, flowSetId, flowBeatmapIds);

        byte[] payload;
        string contentType;

        using (var multipart = BssSubmissionFlowTest.PackageBody(package))
            (payload, contentType) = await MaterializeAsync(multipart);

        int chunks = ChunkCount(payload.Length);
        Assert.That(chunks, Is.GreaterThan(1), "the package must be big enough to actually exercise chunking");

        var created = await CreateSessionAsync(flowBearer, flowSetId, "full", payload, contentType);
        string sessionId = (string)created["session_id"]!;

        Assert.Multiple(() =>
        {
            Assert.That(sessionId, Has.Length.EqualTo(32));
            Assert.That(sessionId, Does.Match("^[0-9a-f]{32}$"));
            Assert.That((int)created["chunk_bytes"]!, Is.EqualTo(UploadSessionStore.ChunkBytes));
            Assert.That((int)created["total_chunks"]!, Is.EqualTo(chunks));
            Assert.That(created["received"], Is.Empty);
            Assert.That((string)created["expires_at"]!, Does.EndWith("Z"), "the wire instant is explicitly UTC");
            Assert.That(
                DateTimeOffset.Parse((string)created["expires_at"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime,
                Is.EqualTo(DateTime.UtcNow + UploadSessionStore.Lifetime).Within(TimeSpan.FromMinutes(5)));
            Assert.That(SessionDirectory(sessionId), Does.Exist);
        });

        // First chunk, then both resume views (status GET and a re-declaration) must agree on it.
        using (var first = await PutChunkAsync(flowBearer, sessionId, 0, ChunkOf(payload, 0)))
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(first.Headers.Connection, Does.Contain("close"),
                "the ~20 KB ceiling is per connection, so the server has to end each one itself");
        }

        using (var status = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{sessionId}", flowBearer))
        {
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = ParseJson(await status.Content.ReadAsStringAsync());
            Assert.That(body["received"]!.Select(t => (int)t), Is.EqualTo(new[] { 0 }));
        }

        var resumed = await CreateSessionAsync(flowBearer, flowSetId, "full", payload, contentType);

        Assert.Multiple(() =>
        {
            Assert.That((string)resumed["session_id"]!, Is.EqualTo(sessionId), "an identical declaration resumes, never forks");
            Assert.That(resumed["received"]!.Select(t => (int)t), Is.EqualTo(new[] { 0 }));
        });

        for (int index = 1; index < chunks; index++)
        {
            using var response = await PutChunkAsync(flowBearer, sessionId, index, ChunkOf(payload, index));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"chunk {index}");
        }

        using (var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", flowBearer))
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await using var conn = await BssFixture.OpenDbAsync();

        var set = await conn.QuerySingleAsync<(string Status, string Title, int CurrentVersion)>(
            "SELECT status AS Status, title AS Title, current_version AS CurrentVersion FROM beatmapsets WHERE id = @flowSetId",
            new { flowSetId });

        Assert.Multiple(() =>
        {
            Assert.That(set.Status, Is.EqualTo("pending"), "a session upload publishes exactly like a direct one");
            Assert.That(set.Title, Is.EqualTo("Neon Nights"));
            Assert.That(set.CurrentVersion, Is.EqualTo(1));
            Assert.That(StoredFile($"packages/{flowSetId}/1.typb"), Does.Exist);
            Assert.That(SessionDirectory(sessionId), Does.Not.Exist, "a completed session keeps nothing");
        });

        var diffs = (await conn.QueryAsync<string?>(
            "SELECT filename FROM beatmaps WHERE set_id = @flowSetId AND filename IS NOT NULL", new { flowSetId })).ToList();

        Assert.That(diffs, Is.EquivalentTo(new[] { "easy.osu", "hard.osu" }));
    }

    [Test]
    [Order(2)]
    public async Task PatchSession_CutsVersionTwo_ReusingUnchangedBlobs()
    {
        long filesBefore;

        await using (var conn = await BssFixture.OpenDbAsync())
            filesBefore = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");

        byte[] payload;
        string contentType;

        using (var multipart = new MultipartFormDataContent())
        {
            // Distinct content from the direct patch test's readme on purpose: blobs are
            // content-addressed and shared across the whole fixture database, so identical bytes
            // would make the "one new file row" assertion below count zero.
            var changed = new ByteArrayContent(SyntheticPackage.Utf8("patched in over an upload session"));
            changed.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(changed, "filesChanged", "extra/readme.txt");
            multipart.Add(new StringContent("bg.jpg"), "filesDeleted");

            (payload, contentType) = await MaterializeAsync(multipart);
        }

        var created = await CreateSessionAsync(flowBearer, flowSetId, "patch", payload, contentType);
        string sessionId = (string)created["session_id"]!;

        // A small delta is a single short chunk, which is also the last-chunk remainder case.
        Assert.That((int)created["total_chunks"]!, Is.EqualTo(1));

        using (var response = await PutChunkAsync(flowBearer, sessionId, 0, ChunkOf(payload, 0)))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using (var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", flowBearer))
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await complete.Content.ReadAsStringAsync());

        await using (var conn = await BssFixture.OpenDbAsync())
        {
            var latest = await conn.QuerySingleAsync<(int VersionNo, long VersionId)>(
                """
                SELECT version_no AS VersionNo, id AS VersionId FROM set_versions
                WHERE set_id = @flowSetId ORDER BY version_no DESC LIMIT 1
                """,
                new { flowSetId });

            Assert.That(latest.VersionNo, Is.EqualTo(2));

            var manifest = (await conn.QueryAsync<string>(
                "SELECT filename FROM version_files WHERE version_id = @versionId", new { latest.VersionId })).ToList();

            Assert.That(manifest, Is.EquivalentTo(new[] { "easy.osu", "hard.osu", "audio.mp3", "extra/noise.bin", "extra/readme.txt" }));

            long filesAfter = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");
            Assert.That(filesAfter - filesBefore, Is.EqualTo(1), "unchanged blobs are reused, only the new file inserts");
        }

        Assert.Multiple(() =>
        {
            Assert.That(StoredFile($"packages/{flowSetId}/2.typb"), Does.Exist);
            Assert.That(SessionDirectory(sessionId), Does.Not.Exist);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Resume, validation and ownership. Each of these leaks a live session on purpose, so each
    // runs as its own user: the per-user live-session cap is 3.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task IdempotentCreate_KeepsTheSessionAndItsDisjointChunks()
    {
        var owner = await NewOwnerAsync("idempotent");
        byte[] payload = Noise(3 * UploadSessionStore.ChunkBytes);

        var created = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);
        string sessionId = (string)created["session_id"]!;

        // Out of order on purpose: resume must report exactly what is stored, not a prefix count.
        using (var response = await PutChunkAsync(owner.Bearer, sessionId, 2, ChunkOf(payload, 2)))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using (var response = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0)))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var again = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That((string)again["session_id"]!, Is.EqualTo(sessionId));
            Assert.That(again["received"]!.Select(t => (int)t), Is.EqualTo(new[] { 0, 2 }), "ascending, and only what was stored");
            Assert.That((int)again["total_chunks"]!, Is.EqualTo(3));
        });

        // A different payload for the same set and kind is a NEW declaration: it supersedes the
        // old session outright, chunks included. The client rebuilds its multipart payload per
        // attempt (fresh boundary, fresh hash), so once a new declaration exists the old payload
        // can never complete, and keeping its husk alive only burns a session slot.
        var other = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(UploadSessionStore.ChunkBytes), multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That((string)other["session_id"]!, Is.Not.EqualTo(sessionId));
            Assert.That(SessionDirectory(sessionId), Does.Not.Exist, "the superseded session keeps nothing");
        });
    }

    [Test]
    public async Task NewDeclaration_SupersedesOnlyItsOwnSetAndKind()
    {
        var owner = await NewOwnerAsync("supersede scope");
        var (otherSetId, _) = await CreateSetAsync(owner.Bearer, diffs: 1);

        string fullHere = (string)(await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(100), multipart_content_type))["session_id"]!;
        string patchHere = (string)(await CreateSessionAsync(owner.Bearer, owner.SetId, "patch", Noise(200), multipart_content_type))["session_id"]!;
        string fullThere = (string)(await CreateSessionAsync(owner.Bearer, otherSetId, "full", Noise(300), multipart_content_type))["session_id"]!;

        // At the disk bound with three live sessions, yet this create needs no eviction: it
        // supersedes the same-set same-kind session first, freeing the slot it then takes.
        var replacement = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(400), multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That(SessionDirectory(fullHere), Does.Not.Exist, "same set, same kind: superseded");
            Assert.That(SessionDirectory(patchHere), Does.Exist, "same set, other kind: untouched");
            Assert.That(SessionDirectory(fullThere), Does.Exist, "other set: untouched");
            Assert.That(SessionDirectory((string)replacement["session_id"]!), Does.Exist);
        });
    }

    [Test]
    public async Task CreateSession_AtTheDiskBound_EvictsTheOldestSession_InsteadOfRefusing()
    {
        // The lockout this prevents was real: a client bug leaked one session per failed upload of
        // one set, and after three of them the old hard 429 blocked every OTHER set the user owned
        // for the full session lifetime. Different sets, so supersession cannot apply.
        var owner = await NewOwnerAsync("evict", diffs: 1);
        var (setB, _) = await CreateSetAsync(owner.Bearer, diffs: 1);
        var (setC, _) = await CreateSetAsync(owner.Bearer, diffs: 1);
        var (setD, _) = await CreateSetAsync(owner.Bearer, diffs: 1);

        string oldest = (string)(await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(100), multipart_content_type))["session_id"]!;
        string middle = (string)(await CreateSessionAsync(owner.Bearer, setB, "full", Noise(200), multipart_content_type))["session_id"]!;
        string newest = (string)(await CreateSessionAsync(owner.Bearer, setC, "full", Noise(300), multipart_content_type))["session_id"]!;

        // Age them apart: three sessions created in one test tick can share a timestamp, and the
        // eviction order must be provable, not incidental.
        await BackdateAsync(oldest, TimeSpan.FromHours(3));
        await BackdateAsync(middle, TimeSpan.FromHours(2));
        await BackdateAsync(newest, TimeSpan.FromHours(1));

        var fourth = await CreateSessionAsync(owner.Bearer, setD, "full", Noise(400), multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That(SessionDirectory(oldest), Does.Not.Exist, "the oldest live session is the one evicted");
            Assert.That(SessionDirectory(middle), Does.Exist);
            Assert.That(SessionDirectory(newest), Does.Exist);
            Assert.That(SessionDirectory((string)fourth["session_id"]!), Does.Exist);
        });
    }

    [Test]
    public async Task ChunkUpload_Rejects_BadIndex_WrongSize_BadHash_AndMissingHeader()
    {
        var owner = await NewOwnerAsync("chunk errors");
        byte[] payload = Noise(2 * UploadSessionStore.ChunkBytes + 17);

        var created = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);
        string sessionId = (string)created["session_id"]!;

        Assert.That((int)created["total_chunks"]!, Is.EqualTo(3));

        using (var beyondEnd = await PutChunkAsync(owner.Bearer, sessionId, 3, ChunkOf(payload, 0)))
            Assert.That(beyondEnd.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "index past the last chunk");

        using (var shortChunk = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0)[..100]))
            Assert.That(shortChunk.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "a non-final chunk must be exactly chunk_bytes");

        using (var longLastChunk = await PutChunkAsync(owner.Bearer, sessionId, 2, ChunkOf(payload, 0)))
            Assert.That(longLastChunk.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "the last chunk must be exactly the remainder");

        using (var wrongHash = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0), sha: new string('a', 64)))
            Assert.That(wrongHash.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "hash mismatch");

        using (var noHeader = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0), omitHash: true))
            Assert.That(noHeader.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "missing X-Chunk-Sha256");

        using (var shortHeader = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0), sha: "abc"))
            Assert.That(shortHeader.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "malformed X-Chunk-Sha256");

        // Nothing above stored anything, and an uppercase hash of the right bytes still does.
        using (var status = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{sessionId}", owner.Bearer))
            Assert.That(ParseJson(await status.Content.ReadAsStringAsync())["received"], Is.Empty);

        using (var upper = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0), sha: Sha256Hex(ChunkOf(payload, 0)).ToUpperInvariant()))
            Assert.That(upper.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "the header hash compares case-insensitively");

        // Every response on this route ends its connection, errors included.
        using (var rejected = await PutChunkAsync(owner.Bearer, sessionId, 99, ChunkOf(payload, 0)))
            Assert.That(rejected.Headers.Connection, Does.Contain("close"));
    }

    [Test]
    public async Task ForeignBearer_Gets404_OnEverySessionRoute()
    {
        var owner = await NewOwnerAsync("owned");
        var (_, strangerBearer) = await BssFixture.CreateUserAsync("session stranger", verified: true);

        byte[] payload = Noise(UploadSessionStore.ChunkBytes);
        var created = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);
        string sessionId = (string)created["session_id"]!;

        using (var status = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{sessionId}", strangerBearer))
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var chunk = await PutChunkAsync(strangerBearer, sessionId, 0, ChunkOf(payload, 0)))
            Assert.That(chunk.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", strangerBearer))
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Unknown and malformed ids are the same answer: no probing for other people's sessions.
        using (var unknown = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{new string('b', 32)}", owner.Bearer))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var malformed = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, "/bss/upload-sessions/not-a-session-id", owner.Bearer))
            Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        Assert.That(SessionDirectory(sessionId), Does.Exist, "a stranger's poking must not destroy the owner's session");
    }

    [Test]
    public async Task Complete_WithAMissingChunk_Is422_AndKeepsTheSession()
    {
        var owner = await NewOwnerAsync("incomplete");
        byte[] payload = Noise(2 * UploadSessionStore.ChunkBytes);

        var created = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);
        string sessionId = (string)created["session_id"]!;

        using (var response = await PutChunkAsync(owner.Bearer, sessionId, 0, ChunkOf(payload, 0)))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using (var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", owner.Bearer))
        {
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That((string)ParseJson(await complete.Content.ReadAsStringAsync())["error"]!, Does.Contain("missing"));
        }

        // The whole point: the chunk already delivered is still there to build on.
        Assert.That(SessionDirectory(sessionId), Does.Exist);

        var resumed = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That((string)resumed["session_id"]!, Is.EqualTo(sessionId));
            Assert.That(resumed["received"]!.Select(t => (int)t), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public async Task Complete_WithAMismatchedPayloadHash_Is422_AndDropsTheSession()
    {
        var owner = await NewOwnerAsync("corrupt");
        byte[] payload = Noise(UploadSessionStore.ChunkBytes + 5);

        // Chunks that are individually honest, under a declaration that is not.
        string lie = Sha256Hex(Noise(32));
        var created = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type, sha: lie);
        string sessionId = (string)created["session_id"]!;

        for (int index = 0; index < 2; index++)
        {
            using var response = await PutChunkAsync(owner.Bearer, sessionId, index, ChunkOf(payload, index));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        using (var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", owner.Bearer))
        {
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That((string)ParseJson(await complete.Content.ReadAsStringAsync())["error"]!, Does.Contain("sha256"));
        }

        Assert.That(SessionDirectory(sessionId), Does.Not.Exist, "a payload that assembled wrong is not resumable");

        // ...so the same declaration now opens a NEW session with nothing in it.
        var fresh = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", payload, multipart_content_type, sha: lie);

        Assert.Multiple(() =>
        {
            Assert.That((string)fresh["session_id"]!, Is.Not.EqualTo(sessionId));
            Assert.That(fresh["received"], Is.Empty);
        });
    }

    [Test]
    public async Task CreateSession_RejectsAMalformedDeclaration()
    {
        var owner = await NewOwnerAsync("declaration");

        await AssertCreate422Async(owner, new { kind = "sideways", content_type = multipart_content_type, total_bytes = 100, sha256 = Sha256Hex(Noise(8)) }, "kind");
        await AssertCreate422Async(owner, new { kind = "full", content_type = "application/json", total_bytes = 100, sha256 = Sha256Hex(Noise(8)) }, "content_type");
        await AssertCreate422Async(owner, new { kind = "full", total_bytes = 100, sha256 = Sha256Hex(Noise(8)) }, "content_type");
        await AssertCreate422Async(owner, new { kind = "full", content_type = multipart_content_type, total_bytes = 0, sha256 = Sha256Hex(Noise(8)) }, "total_bytes");
        await AssertCreate422Async(owner, new { kind = "full", content_type = multipart_content_type, total_bytes = BssEndpoints.MaxUploadBodyBytes + 1, sha256 = Sha256Hex(Noise(8)) }, "total_bytes");
        await AssertCreate422Async(owner, new { kind = "full", content_type = multipart_content_type, total_bytes = 100, sha256 = "nope" }, "sha256");

        // The set gate still applies to a session, exactly as it does to a direct upload.
        using (var missingSet = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Post, "/bss/beatmapsets/99999999/upload-sessions", owner.Bearer,
                   BssSubmissionFlowTest.JsonBody(new { kind = "full", content_type = multipart_content_type, total_bytes = 100, sha256 = Sha256Hex(Noise(8)) })))
            Assert.That(missingSet.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var (_, unverifiedBearer) = await BssFixture.CreateUserAsync("session unverified", verified: false);

        using (var unverified = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Post, $"/bss/beatmapsets/{owner.SetId}/upload-sessions", unverifiedBearer,
                   BssSubmissionFlowTest.JsonBody(new { kind = "full", content_type = multipart_content_type, total_bytes = 100, sha256 = Sha256Hex(Noise(8)) })))
            Assert.That(unverified.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    [Test]
    public async Task ExpiredSessions_AreSweptWhenTheNextOneIsCreated()
    {
        var owner = await NewOwnerAsync("expiry");

        var stale = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(UploadSessionStore.ChunkBytes), multipart_content_type);
        string staleId = (string)stale["session_id"]!;

        // Backdate its manifest past the lifetime, the only way to age a session inside a test run.
        string metaPath = Path.Combine(SessionDirectory(staleId), "meta.json");
        var meta = JObject.Parse(await File.ReadAllTextAsync(metaPath));
        meta["created_at_utc"] = DateTimeOffset.UtcNow - UploadSessionStore.Lifetime - TimeSpan.FromHours(1);
        await File.WriteAllTextAsync(metaPath, meta.ToString(Formatting.None));

        // Expired means gone from the client's point of view before anything sweeps it.
        using (var status = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{staleId}", owner.Bearer))
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var fresh = await CreateSessionAsync(owner.Bearer, owner.SetId, "full", Noise(UploadSessionStore.ChunkBytes * 2), multipart_content_type);

        Assert.Multiple(() =>
        {
            Assert.That(SessionDirectory(staleId), Does.Not.Exist, "creating a session sweeps the expired ones first");
            Assert.That(SessionDirectory((string)fresh["session_id"]!), Does.Exist);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A content type that passes the create gate; the payload behind it is never parsed
    /// in the tests that use it (they never reach a successful complete).</summary>
    private const string multipart_content_type = "multipart/form-data; boundary=----typebeat-test-boundary";

    private sealed record Owner(long UserId, string Bearer, long SetId, long[] BeatmapIds);

    /// <summary>A fresh verified user with a fresh empty set, so a test can leak live sessions
    /// without spending another test's per-user session budget.</summary>
    private static async Task<Owner> NewOwnerAsync(string name, int diffs = 1)
    {
        var (userId, bearer) = await BssFixture.CreateUserAsync("session " + name, verified: true);
        var (setId, beatmapIds) = await CreateSetAsync(bearer, diffs);
        return new Owner(userId, bearer, setId, beatmapIds);
    }

    private static async Task<(long SetId, long[] BeatmapIds)> CreateSetAsync(string bearer, int diffs)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = diffs,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "WIP",
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = ParseJson(await response.Content.ReadAsStringAsync());
        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).ToArray());
    }

    private static byte[] BuildPackage(string creator, long setId, long[] beatmapIds)
    {
        byte[] easy = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: creator, version: "easy", beatmapId: beatmapIds[0], beatmapSetId: setId));
        byte[] hard = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: creator, version: "hard", beatmapId: beatmapIds[1], beatmapSetId: setId, previewTime: 500));

        // The noise file is deliberate: a synthetic package of .osu text plus a sine-wave WAV
        // deflates to well under one chunk, and a one-chunk "chunked" upload proves nothing.
        using var zip = SyntheticPackage.Zip(
            ("easy.osu", easy),
            ("hard.osu", hard),
            ("audio.mp3", BssSubmissionFlowTest.MakeWav(seconds: 2)),
            ("bg.jpg", SyntheticPackage.TinyPng()),
            ("extra/noise.bin", Noise(40 * 1024)));

        return zip.ToArray();
    }

    /// <summary>
    /// JObject.Parse with date parsing OFF. On by default, Newtonsoft rewrites any ISO-looking
    /// string into a DateTime token, so reading expires_at back as a string would hand the test a
    /// locally-formatted round trip instead of the bytes the server actually sent.
    /// </summary>
    private static JObject ParseJson(string json)
        => JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;

    private static async Task<(byte[] Payload, string ContentType)> MaterializeAsync(MultipartFormDataContent multipart)
        => (await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString());

    private static async Task<JObject> CreateSessionAsync(
        string bearer, long setId, string kind, byte[] payload, string contentType, string? sha = null)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Post, $"/bss/beatmapsets/{setId}/upload-sessions", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                kind,
                content_type = contentType,
                total_bytes = payload.LongLength,
                sha256 = sha ?? Sha256Hex(payload),
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

        return ParseJson(await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertCreate422Async(Owner owner, object declaration, string because)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Post, $"/bss/beatmapsets/{owner.SetId}/upload-sessions", owner.Bearer,
            BssSubmissionFlowTest.JsonBody(declaration));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), because);
        Assert.That((string)ParseJson(await response.Content.ReadAsStringAsync())["error"]!, Does.Contain(because));
    }

    private static async Task<HttpResponseMessage> PutChunkAsync(
        string bearer, string sessionId, int index, byte[] chunk, string? sha = null, bool omitHash = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/bss/upload-sessions/{sessionId}/chunks/{index}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (!omitHash)
            request.Headers.Add("X-Chunk-Sha256", sha ?? Sha256Hex(chunk));

        var content = new ByteArrayContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;

        return await BssFixture.Client.SendAsync(request);
    }

    private static int ChunkCount(int length) => (length + UploadSessionStore.ChunkBytes - 1) / UploadSessionStore.ChunkBytes;

    private static byte[] ChunkOf(byte[] payload, int index)
    {
        int start = index * UploadSessionStore.ChunkBytes;
        return payload[start..Math.Min(payload.Length, start + UploadSessionStore.ChunkBytes)];
    }

    private static byte[] Noise(int length)
    {
        byte[] bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Rewrites a session's manifest to look created this much earlier: the only way a
    /// test can order or expire sessions, since their timestamps are stamped server-side.</summary>
    private static async Task BackdateAsync(string sessionId, TimeSpan age)
    {
        string metaPath = Path.Combine(SessionDirectory(sessionId), "meta.json");
        var meta = JObject.Parse(await File.ReadAllTextAsync(metaPath));
        meta["created_at_utc"] = DateTimeOffset.UtcNow - age;
        await File.WriteAllTextAsync(metaPath, meta.ToString(Formatting.None));
    }

    private static string SessionDirectory(string sessionId)
        => Path.Combine(BssFixture.FileRoot, "upload-sessions", sessionId);

    private static string StoredFile(string key)
        => Path.Combine(BssFixture.FileRoot, key.Replace('/', Path.DirectorySeparatorChar));
}
