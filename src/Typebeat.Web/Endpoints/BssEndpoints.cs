using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
// Alias rather than a namespace using: Kestrel.Core also exports an obsolete
// BadHttpRequestException that would make the references below ambiguous.
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Ops;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The lazer-compatible beatmap submission service (BSS) at /bss: the three endpoints the
/// editor's submission wizard calls (recon result.bss.endpoint_sequence / minimal_subset):
///
///  - <c>PUT /bss/beatmapsets</c> (JSON): create a fresh set or re-target an existing one;
///    allocates beatmap ids and returns the latest version's file manifest (EMPTY for a fresh
///    set, this drives the client's replace-vs-patch branch).
///  - <c>PUT /bss/beatmapsets/{id}</c> (multipart, one <c>beatmapArchive</c> part): full
///    package upload → Parse → Validate → Ingest → 204.
///  - <c>PATCH /bss/beatmapsets/{id}</c> (multipart, repeated <c>filesChanged</c> file parts +
///    repeated <c>filesDeleted</c> form fields): rebuild the latest version's package with the
///    overlay applied, then the same Parse → Validate → Ingest → 204.
///
/// Errors follow the upstream contract: 422 <c>{"error": "..."}</c> for invariants (WireJson),
/// 403 for ownership, 404 for a missing set. Auth is the existing bearer token; submission
/// additionally requires <c>users.verified_at</c>, set self-service by completing email
/// verification (any website sign-in / signup code), NOT by a manual admin lever. Ranking (not
/// submission) is the role-gated action.
///
/// Set/diff lifecycle conventions owned here:
///  - a fresh set is created with status 'hidden' and flipped to its intended published status by
///    its first successful upload (PackageIngest): 'pending' (awaits a reviewer's rank flip) or
///    'unranked' (creator opted out of ranking in the wizard; never leaderboard-eligible). The
///    choice rides in on the PUT's <c>target</c> and is stored in <c>intended_status</c>;
///  - the same PUT carries the optional <c>explicit</c> boolean (the wizard's explicit-content
///    toggle, absent = false). It is a display flag only, applied immediately on create and on
///    every re-submission, and surfaces as the site's EXPLICIT badge;
///  - beatmap rows are NEVER deleted (scores FK); a diff dropped from beatmaps_to_keep, or
///    absent from an uploaded package, gets <c>filename = NULL</c>, which is the repo-wide
///    "not part of the current version" marker (live diffs have <c>filename IS NOT NULL</c>);
///  - because nothing is ever deleted, allocation on the create PUT REUSES the set's existing
///    NEVER-LIVE placeholder rows before inserting new ones. Without that, every failed upload
///    attempt leaks a row per difficulty: the client re-runs the same create on each retry, so
///    one real set accreted five attempts' worth of ids in forty minutes. "Never live" is
///    <c>filename IS NULL AND word_count IS NULL</c>: filename alone also matches a ONCE-live
///    diff that was just dropped, and handing that id out again would attach a new difficulty
///    to an id that already carries scores and stats;
///  - newly allocated beatmap rows carry a random placeholder checksum until the first upload
///    overwrites it (checksum_md5 is NOT NULL UNIQUE); every beatmap id comes from nextval at
///    allocation time, so PackageIngest's explicit-id upserts can never outrun the sequence;
///  - the upload/patch routes run their whole tail (rebuild-base read → parse → validate →
///    version cut → artifact publication) inside PackageIngest's per-set ingest scope
///    (pg_advisory_xact_lock), so concurrent submissions to one set fully serialize.
/// </summary>
public static class BssEndpoints
{
    /// <summary>
    /// Per-endpoint Kestrel body cap for the two package-upload routes only (~100 MB: above
    /// PackageValidator's 95 MiB package cap + multipart overhead, below Cloudflare's 100 MB
    /// proxied-body limit). Everything else keeps Kestrel's ~28.6 MB default. Since backlog 189,
    /// uploads arrive via the direct-origin host (bss.typebeat.sh, not proxied through
    /// Cloudflare), so the Cloudflare figure motivated this cap but no longer bounds the live
    /// request path.
    /// </summary>
    public const long MaxUploadBodyBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Kestrel minimum body data rate for the two package-upload routes only. Kestrel's default
    /// floor (240 bytes/s after a 5 s grace period) aborts the exact connections the direct-origin
    /// host exists for: a throttled user's upload reaches the app but the body trickles under the
    /// floor, Kestrel hangs up mid-read ("Reading the request body timed out due to data arriving
    /// too slowly"), the 4xx lands before the body is drained so the client sees a generic
    /// transport error, and the transport retry burns its attempts against the same floor. A tiny
    /// floor is kept rather than null: slowloris exposure on these routes is already bounded
    /// (bearer-authed, 12/h per user, 100 MB cap, and legit clients release the connection at
    /// their own 600 s request timeout), but a free open-socket sink is still not worth handing
    /// out. Every other route keeps Kestrel's default, which is right for them.
    /// </summary>
    public static readonly MinDataRate UploadMinBodyDataRate = new MinDataRate(bytesPerSecond: 30, gracePeriod: TimeSpan.FromSeconds(30));

    // In-memory speed bump on the two upload routes, keyed by user id. Since backlog 189, uploads
    // arrive via the direct-origin host (not proxied through Cloudflare), so on that path this
    // limiter and the body cap above are the only layers, same doctrine as the login/register
    // limiters otherwise.
    private const int uploads_per_window = 12;
    private static readonly FixedWindowLimiter upload_limiter = new(uploads_per_window, TimeSpan.FromHours(1));

    private const string verification_required_message =
        "Beatmap submission requires a verified account. Sign in on the type!beat website to verify your email, then submit again.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPut("/bss/beatmapsets", PutBeatmapSetAsync).RequireBearer();
        app.MapPut("/bss/beatmapsets/{setId:long}", UploadFullPackageAsync).RequireBearer().WithUploadBodyLimit();
        app.MapPatch("/bss/beatmapsets/{setId:long}", PatchPackageAsync).RequireBearer().WithUploadBodyLimit();

        // Chunked alternative to the two routes above, for clients whose network cannot carry a
        // single large request body (see UploadSessionStore for the ~20 KB middlebox ceiling this
        // exists for). Same payloads, same gate, same parse path; only the transport differs.
        app.MapPost("/bss/beatmapsets/{setId:long}/upload-sessions", CreateUploadSessionAsync).RequireBearer();
        app.MapGet("/bss/upload-sessions/{sessionId}", GetUploadSessionAsync).RequireBearer();
        app.MapPut("/bss/upload-sessions/{sessionId}/chunks/{index:int}", PutUploadSessionChunkAsync).RequireBearer();
        app.MapPost("/bss/upload-sessions/{sessionId}/complete", CompleteUploadSessionAsync).RequireBearer();
    }

    // ---------------------------------------------------------------------------------------------
    // PUT /bss/beatmapsets: create or re-target a set, allocate ids, return the latest manifest.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> PutBeatmapSetAsync(HttpContext ctx, Db db, PackageIngest ingest)
    {
        var user = ctx.AuthedUser();

        BssPutRequest? request;

        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            request = JsonConvert.DeserializeObject<BssPutRequest>(await reader.ReadToEndAsync(ctx.RequestAborted));
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request == null)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The request body is not a valid beatmap submission request.");

        if (request.BeatmapsToCreate < 0)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "beatmaps_to_create cannot be negative.");

        long[] keep = request.BeatmapsToKeep.Distinct().ToArray();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await isVerifiedAsync(conn, user.Id))
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, verification_required_message);

        long setId;

        if (request.BeatmapsetId is long existingId)
        {
            if (await loadOwnedSetForSubmissionAsync(conn, existingId, user) is { } error)
                return error;

            var existingIds = (await conn.QueryAsync<long>(
                "SELECT id FROM beatmaps WHERE set_id = @existingId", new { existingId })).ToHashSet();

            var foreign = keep.Where(id => !existingIds.Contains(id)).ToList();

            if (foreign.Count > 0)
            {
                return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                    $"beatmaps_to_keep contains beatmap ids that do not belong to this set: {string.Join(", ", foreign)}.");
            }

            setId = existingId;
        }
        else
        {
            if (keep.Length > 0)
                return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "beatmaps_to_keep must be empty when creating a new beatmap set.");

            setId = 0; // allocated inside the transaction below.
        }

        int totalDifficulties = keep.Length + request.BeatmapsToCreate;

        if (totalDifficulties is < 1 or > PackageValidator.MaxDifficulties)
        {
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                $"A beatmap set must contain between 1 and {PackageValidator.MaxDifficulties} difficulties; this request would leave {totalDifficulties}.");
        }

        // The submission wizard's target: only the "not for ranking" choice carries a distinct
        // server state. WIP and Pending both publish to 'pending' (WIP has no separate state);
        // Unranked publishes to 'unranked'. Recorded now, applied at the publish flip below (and
        // immediately for an already-published re-submission).
        string intendedStatus = string.Equals(request.Target, "Unranked", StringComparison.OrdinalIgnoreCase)
            ? "unranked"
            : "pending";

        // The wizard's explicit-content toggle rides on the same call. Unlike intended_status it
        // needs no deferred flip: it is a pure display flag, so it applies immediately on create
        // AND on every re-submission (that is how a creator turns it back off). Absent on the
        // wire (any pre-toggle client) = false.
        bool isExplicit = request.Explicit;

        await using (var tx = await conn.BeginTransactionAsync(ctx.RequestAborted))
        {
            if (request.BeatmapsetId == null)
            {
                // Hidden until the first successful package upload publishes it (see class doc);
                // intended_status carries the creator's ranking-intent to that publish flip.
                setId = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO beatmapsets (owner_id, status, intended_status, explicit)
                    VALUES (@ownerId, 'hidden', @intendedStatus, @isExplicit)
                    RETURNING id
                    """,
                    new { ownerId = user.Id, intendedStatus, isExplicit });
            }
            else
            {
                // Drop diffs the client no longer keeps. Rows stay (scores FK); filename = NULL
                // marks them as not-part-of-the-current-version.
                await conn.ExecuteAsync(
                    "UPDATE beatmaps SET filename = NULL WHERE set_id = @setId AND id <> ALL(@keep)",
                    new { setId, keep });

                // Apply the ranking-intent choice: always record it (so a still-hidden set publishes
                // to the right status), and for an already-published, non-ranked set switch it now.
                // A 'loved' set keeps its recorded intent too: the wizard prefills Pending for it, and
                // overwriting would put a creator-opted-out ('unranked') map in the review queue on Unlove.
                // A 'ranked' set is never self-demoted here (reviewer-only); 'removed' is unreachable.
                await conn.ExecuteAsync(
                    """
                    UPDATE beatmapsets
                    SET intended_status = CASE WHEN status = 'loved' THEN intended_status ELSE @intendedStatus END,
                        status = CASE WHEN status IN ('pending', 'unranked') THEN @intendedStatus ELSE status END,
                        explicit = @isExplicit,
                        updated_at = now()
                    WHERE id = @setId
                    """,
                    new { setId, intendedStatus, isExplicit });
            }

            var newIds = new List<long>(request.BeatmapsToCreate);

            // Slot reuse. The client re-runs this exact create after a failed package upload, so
            // allocating unconditionally leaked beatmaps_to_create rows per failed attempt, and
            // rows are never deleted (scores FK). Hand back the set's own never-live placeholders
            // first and insert only the shortfall, which makes the retry idempotent.
            //
            // The predicate is load-bearing on BOTH columns. filename IS NULL also matches a
            // ONCE-live diff (dropped by the keep UPDATE just above, or by a package that no
            // longer contains it); those keep their ingest stats and may carry scores, so reusing
            // one would silently graft a new difficulty onto an old id. word_count is written only
            // by PackageIngest's upsert and is never nulled again, so word_count IS NULL is the
            // "never ingested" marker. The scores NOT EXISTS is belt and braces: it should be
            // vacuous, since a never-live row was never playable.
            //
            // FOR UPDATE keeps two creates racing in-flight off the same candidates. Once the
            // first commits, a second create for the SAME set can still pick the same ids, because
            // reuse leaves the rows never-live; that overlap is the idempotency this exists for.
            // Different sets can never collide: the predicate is set-scoped.
            if (request.BeatmapsetId != null && request.BeatmapsToCreate > 0)
            {
                newIds.AddRange(await conn.QueryAsync<long>(
                    """
                    SELECT b.id
                    FROM beatmaps b
                    WHERE b.set_id = @setId
                      AND b.filename IS NULL
                      AND b.word_count IS NULL
                      AND b.id <> ALL(@keep)
                      AND NOT EXISTS (SELECT 1 FROM scores s WHERE s.beatmap_id = b.id)
                    ORDER BY b.id
                    LIMIT @wanted
                    FOR UPDATE
                    """,
                    new { setId, keep, wanted = request.BeatmapsToCreate }));
            }

            for (int i = newIds.Count; i < request.BeatmapsToCreate; i++)
            {
                newIds.Add(await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@setId, @placeholder) RETURNING id",
                    new { setId, placeholder = placeholderChecksum() }));
            }

            await tx.CommitAsync(ctx.RequestAborted);

            // Metadata the set's cached reads show may just have changed (backlog 366).
            await ctx.RequestServices.GetRequiredService<CacheEviction>().AfterSetChangedAsync(setId, user.Id);

            // The latest version's manifest ([] for a fresh set): the client's replace-vs-patch pivot.
            var files = await ingest.GetLatestVersionFilesAsync(setId, ctx.RequestAborted);

            return WireJson.Ok(new BssPutBeatmapSetResponse
            {
                BeatmapsetId = setId,
                // Upstream contract: created ids followed by the KEPT ids. The client's exporter
                // resolves every kept diff's online id by membership in this list (and hands the
                // leftovers to new diffs), so omitting the kept ids hard-fails every re-submission
                // at the export stage.
                BeatmapIds = [.. newIds, .. keep],
                Files = files.Select(f => new BssFileWire { Filename = f.Filename, Sha2Hash = f.Sha256Hex }).ToList(),
            });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PUT /bss/beatmapsets/{id}: full package upload (single "beatmapArchive" file part).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> UploadFullPackageAsync(long setId, HttpContext ctx, Db db, PackageIngest ingest, DiskGuard disk, StartupSweepGate sweeps, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("BssUpload");

        if (sweepRefusal(sweeps) is { } sweepError)
            return sweepError;

        if (diskRefusal(disk) is { } diskError)
            return diskError;

        if (await gateUploadAsync(ctx, db, setId, user) is { } gateError)
            return gateError;

        return await uploadFullPackageCoreAsync(setId, ctx, user, ingest, logger);
    }

    /// <summary>
    /// Everything the full-upload route does AFTER its gate, reading the multipart body straight
    /// off <c>ctx.Request</c>. Split out so the upload-session complete route can run the gate
    /// exactly once (calling the route handler would consume a second slot of the rate limiter)
    /// and then replay its assembled body through this identical path.
    /// </summary>
    private static async Task<IResult> uploadFullPackageCoreAsync(long setId, HttpContext ctx, AuthedUser user, PackageIngest ingest, ILogger logger)
    {
        // Guarded (rather than letting ReadFormAsync throw InvalidOperationException → raw 500):
        // a missing or non-form Content-Type is a client-shaped request error.
        if (!ctx.Request.HasFormContentType)
        {
            logger.LogWarning("Set {SetId}: full upload rejected for user {UserId}, body is not multipart/form-data.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The upload body could not be read as multipart/form-data.");
        }

        IFormCollection form;

        try
        {
            form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (isClientBodyError(e))
        {
            logger.LogWarning(e, "Set {SetId}: full upload body from user {UserId} could not be read.", setId, user.Id);
            return uploadBodyError(e);
        }

        var archive = form.Files.GetFile("beatmapArchive");

        if (archive == null)
        {
            logger.LogWarning("Set {SetId}: full upload rejected for user {UserId}, missing beatmapArchive part.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The request is missing the beatmapArchive file part.");
        }

        // Parse/ingest need one seekable stream over the whole package; multipart sections are
        // forward-only, so buffer to a self-deleting temp file first.
        await using var buffer = createTempBuffer();

        await using (var source = archive.OpenReadStream())
            await source.CopyToAsync(buffer, ctx.RequestAborted);

        // Enter the per-set critical section only now that the body is fully buffered.
        await using var scope = await ingest.BeginSetScopeAsync(setId, ctx.RequestAborted);

        return await parseValidateIngestAsync(buffer, setId, user, ingest, scope, "full", logger, ctx.RequestServices.GetRequiredService<CacheEviction>(), ctx.RequestAborted);
    }

    // ---------------------------------------------------------------------------------------------
    // PATCH /bss/beatmapsets/{id}: delta upload, latest version overlaid with filesChanged
    // minus filesDeleted, rebuilt into a full package, then the same ingest path.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> PatchPackageAsync(long setId, HttpContext ctx, Db db, PackageIngest ingest, IFileStore fileStore, DiskGuard disk, StartupSweepGate sweeps, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("BssUpload");

        if (sweepRefusal(sweeps) is { } sweepError)
            return sweepError;

        if (diskRefusal(disk) is { } diskError)
            return diskError;

        if (await gateUploadAsync(ctx, db, setId, user) is { } gateError)
            return gateError;

        return await patchPackageCoreAsync(setId, ctx, user, ingest, fileStore, logger);
    }

    /// <summary>
    /// Everything the patch route does AFTER its gate, reading the multipart body straight off
    /// <c>ctx.Request</c>. Split out for the same reason as
    /// <see cref="uploadFullPackageCoreAsync"/>: the session complete route gates once, then
    /// replays its assembled body through this exact code.
    /// </summary>
    private static async Task<IResult> patchPackageCoreAsync(long setId, HttpContext ctx, AuthedUser user, PackageIngest ingest, IFileStore fileStore, ILogger logger)
    {
        // A no-change resubmission arrives with NO body at all: the client's diff is empty and
        // osu-framework's WebRequest only builds multipart content when it has parts, so there
        // is no Content-Type to read a form from. Treat any absent/non-form body as an empty
        // delta; the rebuild below then reproduces the latest version verbatim and the ingest
        // collapses it into a no-version-cut 204 (never a raw 500 out of ReadFormAsync).
        IFormCollection? form = null;

        if (ctx.Request.HasFormContentType)
        {
            try
            {
                form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            }
            catch (Exception e) when (isClientBodyError(e))
            {
                logger.LogWarning(e, "Set {SetId}: patch upload body from user {UserId} could not be read.", setId, user.Id);
                return uploadBodyError(e);
            }
        }

        // The rebuild base MUST be read inside the per-set critical section: were it read on an
        // unlocked connection, another submission committing between this read and our version
        // cut would be silently reverted by a rebuild based on the older manifest. The form is
        // already buffered above, so the lock is held only for rebuild + parse + ingest.
        await using var scope = await ingest.BeginSetScopeAsync(setId, ctx.RequestAborted);

        var manifest = await ingest.GetLatestVersionFilesAsync(scope, setId);

        if (manifest.Count == 0)
        {
            logger.LogWarning("Set {SetId}: patch rejected for user {UserId}, no uploaded version to patch.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "This beatmap set has no uploaded version to patch; upload the full package instead.");
        }

        // EXACT (case-SENSITIVE, slash-normalized) filename semantics, matching the client's
        // diff: a case-only rename arrives as filesChanged=[bg.JPG] + filesDeleted=[bg.jpg] and
        // must resolve to the NEW name surviving. Case-insensitive matching here silently
        // dropped the file (the delete swallowed the replacement). A malformed delta that
        // leaves case-colliding names in the rebuild is rejected by PackageValidator (422)
        // rather than resolved by guesswork.
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var changed = new Dictionary<string, IFormFile>(StringComparer.Ordinal);

        if (form != null)
        {
            foreach (string? value in form["filesDeleted"])
            {
                if (!string.IsNullOrEmpty(value))
                    deleted.Add(BeatmapPackageParser.NormalizeFilename(value));
            }

            foreach (var file in form.Files.Where(f => f.Name == "filesChanged"))
            {
                if (string.IsNullOrEmpty(file.FileName))
                {
                    logger.LogWarning("Set {SetId}: patch rejected for user {UserId}, a filesChanged part is missing its filename.", setId, user.Id);
                    return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "A filesChanged part is missing its archive path (multipart filename).");
                }

                changed[BeatmapPackageParser.NormalizeFilename(file.FileName)] = file;
            }
        }

        await using var buffer = createTempBuffer();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in manifest)
            {
                if (deleted.Contains(file.Filename) || changed.ContainsKey(file.Filename))
                    continue;

                var entry = zip.CreateEntry(file.Filename);

                await using var entryStream = entry.Open();
                await using var blob = await fileStore.OpenBlobReadAsync(file.Sha256, ctx.RequestAborted);
                await blob.CopyToAsync(entryStream, ctx.RequestAborted);
            }

            // Deletes apply to the BASE manifest only; an uploaded replacement always lands
            // (changed-wins, as upstream); never let a filesDeleted entry swallow new content.
            foreach (var (filename, file) in changed)
            {
                var entry = zip.CreateEntry(filename);

                await using var entryStream = entry.Open();
                await using var source = file.OpenReadStream();
                await source.CopyToAsync(entryStream, ctx.RequestAborted);
            }
        }

        return await parseValidateIngestAsync(buffer, setId, user, ingest, scope, "patch", logger, ctx.RequestServices.GetRequiredService<CacheEviction>(), ctx.RequestAborted);
    }

    // ---------------------------------------------------------------------------------------------
    // Chunked upload sessions. Same two payloads as the routes above, delivered as a series of
    // small requests instead of one large body, for clients whose path to this host black-holes
    // any single request past roughly 20 KB (see UploadSessionStore). The assembled payload is
    // fed back through the SAME handlers, so the two transports cannot diverge in what they
    // accept: only the delivery differs.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// POST /bss/beatmapsets/{id}/upload-sessions: declare a payload and get a session to send it
    /// in. Gated on a verified account owning a submittable set, but deliberately NOT on the
    /// upload rate limiter: a session is not an upload, and a client that has to resume one after
    /// a network failure must not burn a slot doing it. The limiter is consumed at complete.
    /// </summary>
    private static async Task<IResult> CreateUploadSessionAsync(
        long setId, HttpContext ctx, Db db, UploadSessionStore sessions, DiskGuard disk, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("BssUpload");

        if (diskRefusal(disk) is { } diskError)
            return diskError;

        BssUploadSessionRequest? request;

        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            request = JsonConvert.DeserializeObject<BssUploadSessionRequest>(await reader.ReadToEndAsync(ctx.RequestAborted));
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request == null)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The request body is not a valid upload session request.");

        await using (var conn = await db.OpenAsync(ctx.RequestAborted))
        {
            if (!await isVerifiedAsync(conn, user.Id))
                return WireJson.Error(StatusCodes.Status422UnprocessableEntity, verification_required_message);

            if (await loadOwnedSetForSubmissionAsync(conn, setId, user) is { } error)
                return error;
        }

        string kind = request.Kind ?? "";

        if (kind is not ("full" or "patch"))
        {
            logger.LogWarning("Set {SetId}: upload session rejected for user {UserId}, unknown kind.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "kind must be \"full\" or \"patch\".");
        }

        string contentType = request.ContentType ?? "";

        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Set {SetId}: upload session rejected for user {UserId}, content_type is not multipart/form-data.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "content_type must be the multipart/form-data content type of the upload body.");
        }

        if (request.TotalBytes <= 0 || request.TotalBytes > MaxUploadBodyBytes)
        {
            logger.LogWarning("Set {SetId}: upload session rejected for user {UserId}, total_bytes {TotalBytes} out of range.", setId, user.Id, request.TotalBytes);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                $"total_bytes must be between 1 and {MaxUploadBodyBytes}.");
        }

        if (!isSha256Hex(request.Sha256))
        {
            logger.LogWarning("Set {SetId}: upload session rejected for user {UserId}, malformed sha256.", setId, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "sha256 must be 64 hexadecimal characters.");
        }

        var session = await sessions.CreateOrResumeAsync(
            user.Id, setId, kind, contentType, request.TotalBytes, request.Sha256!.ToLowerInvariant(), ctx.RequestAborted);

        logger.LogInformation(
            "Set {SetId}: upload session {SessionId} ({Kind}) open for user {UserId}, {TotalBytes} bytes in {TotalChunks} chunks.",
            setId, session.SessionId, session.Kind, user.Id, session.TotalBytes, session.TotalChunks);

        return WireJson.Ok(sessionPayload(session, sessions));
    }

    /// <summary>GET /bss/upload-sessions/{id}: the resume view, same shape as create.</summary>
    private static async Task<IResult> GetUploadSessionAsync(string sessionId, HttpContext ctx, UploadSessionStore sessions)
    {
        var user = ctx.AuthedUser();
        var session = await sessions.TryGetAsync(sessionId, ctx.RequestAborted);

        if (session == null || session.UserId != user.Id)
            return unknownUploadSession();

        return WireJson.Ok(sessionPayload(session, sessions));
    }

    /// <summary>
    /// PUT /bss/upload-sessions/{id}/chunks/{index}: one raw slice of the payload, hash-checked
    /// against the caller's X-Chunk-Sha256 header. Re-PUTting an index overwrites it, so a client
    /// that never saw a 204 can simply send the chunk again.
    /// </summary>
    private static async Task<IResult> PutUploadSessionChunkAsync(
        string sessionId, int index, HttpContext ctx, UploadSessionStore sessions, DiskGuard disk, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("BssUpload");

        // Load-bearing, on EVERY response from this route including the errors: the ceiling this
        // protocol exists for is per TCP CONNECTION, not per request, so a pooled keep-alive
        // connection would accumulate chunk after chunk and die partway through the third one.
        // Closing server-side bounds every connection at a single chunk whatever the client does
        // with its pool.
        ctx.Response.Headers.Connection = "close";

        if (diskRefusal(disk) is { } diskError)
            return diskError;

        var session = await sessions.TryGetAsync(sessionId, ctx.RequestAborted);

        if (session == null || session.UserId != user.Id)
            return unknownUploadSession();

        if (index < 0 || index >= session.TotalChunks)
        {
            logger.LogWarning("Set {SetId}: chunk {Index} rejected for user {UserId}, outside session {SessionId}.",
                session.SetId, index, user.Id, session.SessionId);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                $"chunk index must be between 0 and {session.TotalChunks - 1}.");
        }

        string declared = ctx.Request.Headers["X-Chunk-Sha256"].ToString();

        if (!isSha256Hex(declared))
        {
            logger.LogWarning("Set {SetId}: chunk {Index} rejected for user {UserId}, missing or malformed X-Chunk-Sha256.",
                session.SetId, index, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "X-Chunk-Sha256 must be 64 hexadecimal characters.");
        }

        int expected = session.ChunkLength(index);

        // One byte of headroom: reading MORE than the chunk length is as wrong as reading less,
        // and this is how the difference gets noticed without buffering the overflow.
        byte[] buffer = new byte[expected + 1];
        int read = await ctx.Request.Body.ReadAtLeastAsync(buffer, expected + 1, throwOnEndOfStream: false, ctx.RequestAborted);

        if (read != expected)
        {
            logger.LogWarning("Set {SetId}: chunk {Index} rejected for user {UserId}, {Read} bytes where {Expected} were declared.",
                session.SetId, index, user.Id, read, expected);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, $"chunk {index} must carry exactly {expected} bytes.");
        }

        byte[] content = buffer[..expected];
        string actual = Convert.ToHexStringLower(SHA256.HashData(content));

        if (!string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Set {SetId}: chunk {Index} rejected for user {UserId}, hash mismatch.", session.SetId, index, user.Id);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, $"chunk {index} does not match its X-Chunk-Sha256.");
        }

        await sessions.WriteChunkAsync(session, index, content, ctx.RequestAborted);

        return Results.NoContent();
    }

    /// <summary>
    /// POST /bss/upload-sessions/{id}/complete: assemble, verify, then run the real upload. The
    /// session survives everything that a retry could still fix (missing chunks, a rate limit, a
    /// verification or ownership problem) and is dropped once the payload is either proven corrupt
    /// or actually handed to the ingest, success or failure.
    /// </summary>
    private static async Task<IResult> CompleteUploadSessionAsync(
        string sessionId, HttpContext ctx, Db db, PackageIngest ingest, IFileStore fileStore,
        UploadSessionStore sessions, DiskGuard disk, StartupSweepGate sweeps, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("BssUpload");

        // Before anything reads the session: a refused complete leaves it (and its chunks) intact,
        // so the client's retry completes the very same upload once the gate opens.
        if (sweepRefusal(sweeps) is { } sweepError)
            return sweepError;

        if (diskRefusal(disk) is { } diskError)
            return diskError;

        var session = await sessions.TryGetAsync(sessionId, ctx.RequestAborted);

        if (session == null || session.UserId != user.Id)
            return unknownUploadSession();

        int received = sessions.ReceivedIndexes(session).Count;

        if (received != session.TotalChunks)
        {
            logger.LogWarning("Set {SetId}: session {SessionId} completion rejected for user {UserId}, {Received} of {TotalChunks} chunks present.",
                session.SetId, session.SessionId, user.Id, received, session.TotalChunks);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                $"The upload session is missing {session.TotalChunks - received} of its {session.TotalChunks} chunks.");
        }

        await using var buffer = createTempBuffer();

        await sessions.AssembleAsync(session, buffer, ctx.RequestAborted);

        buffer.Position = 0;
        string assembledSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, ctx.RequestAborted));

        if (!string.Equals(assembledSha, session.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Every chunk matched its own hash yet the whole does not: the client's declaration
            // was wrong (or the payload changed under it), so nothing about this session is
            // salvageable and a retry has to start over.
            sessions.Delete(session);
            logger.LogWarning("Set {SetId}: session {SessionId} for user {UserId} assembled to {Actual}, not the declared {Declared}.",
                session.SetId, session.SessionId, user.Id, assembledSha, session.Sha256);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity,
                "The assembled upload does not match the declared sha256; start a new upload session.");
        }

        buffer.Position = 0;

        // The one and only rate-limiter consumption of the whole session: the transport was free,
        // the actual submission costs a slot exactly like a direct upload does.
        if (await gateUploadAsync(ctx, db, session.SetId, user) is { } gateError)
        {
            logger.LogWarning("Set {SetId}: session {SessionId} completion gated for user {UserId}.", session.SetId, session.SessionId, user.Id);
            return gateError;
        }

        // Replay the assembled payload through the ORIGINAL read path: swapping the request body
        // (plus its content type, length and a fresh form feature over them) means the core
        // handlers below parse a session upload with exactly the code that parses a direct one,
        // so the two transports cannot drift in what they accept.
        ctx.Request.Body = buffer;
        ctx.Request.ContentType = session.ContentType;
        ctx.Request.ContentLength = buffer.Length;
        ctx.Features.Set<IFormFeature>(new FormFeature(ctx.Request, ctx.RequestServices.GetRequiredService<IOptions<FormOptions>>().Value));

        try
        {
            logger.LogInformation("Set {SetId}: session {SessionId} ({Kind}) completing for user {UserId}, {TotalBytes} bytes assembled.",
                session.SetId, session.SessionId, session.Kind, user.Id, session.TotalBytes);

            return session.Kind == "full"
                ? await uploadFullPackageCoreAsync(session.SetId, ctx, user, ingest, logger)
                : await patchPackageCoreAsync(session.SetId, ctx, user, ingest, fileStore, logger);
        }
        finally
        {
            // The core ran, so the payload has been consumed: whatever it answered, re-completing
            // the same session would re-submit the same package. Dropped either way.
            sessions.Delete(session);
        }
    }

    /// <summary>
    /// The single answer for an upload session that is absent, expired, malformed or somebody
    /// else's. One shape for all four so a caller cannot probe other users' session ids.
    /// </summary>
    private static IResult unknownUploadSession()
        => WireJson.Error(StatusCodes.Status404NotFound, "upload session not found");

    private static BssUploadSessionResponse sessionPayload(UploadSessionStore.Session session, UploadSessionStore sessions)
        => new BssUploadSessionResponse
        {
            SessionId = session.SessionId,
            ChunkBytes = UploadSessionStore.ChunkBytes,
            TotalChunks = session.TotalChunks,
            Received = sessions.ReceivedIndexes(session),
            ExpiresAt = session.ExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };

    /// <summary>64 hex characters, either case (both the declared payload hash and the per-chunk header).</summary>
    private static bool isSha256Hex(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    // ---------------------------------------------------------------------------------------------
    // Shared plumbing.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Disk guard (backlog 365): every BSS route that writes to the store answers 507 Insufficient
    /// Storage below the upload floor, FIRST, so a refused upload consumes no rate-limiter slot and
    /// no body is buffered. 507 rather than 503 on purpose: the game's submission flow treats 502,
    /// 503 and 504 as a gateway blip and retries for minutes, while any other status fails the step
    /// at once and shows this message (an APIException, never a failover retry).
    /// </summary>
    private static IResult? diskRefusal(DiskGuard disk)
        => disk.Current is { UploadsRefused: true } status
            ? WireJson.Error(StatusCodes.Status507InsufficientStorage, DiskGuard.UploadRefusal(status))
            : null;

    /// <summary>
    /// Startup sweep gate (backlog 368): the three routes that hand a package to the ingest answer
    /// 503 with Retry-After until <see cref="GameplayFingerprintBackfill"/> has run in this process
    /// (see <see cref="StartupSweepGate"/>), FIRST, before the disk guard, the rate limiter or any body
    /// read, so a refused attempt costs the mapper nothing. 503 on purpose, the opposite of the disk
    /// guard's 507: this one clears by itself in a minute or two, and the game retries a 503.
    /// </summary>
    private static IResult? sweepRefusal(StartupSweepGate sweeps)
        => sweeps.IngestOpen ? null : sweeps.IngestRefusal();

    /// <summary>Common gate for both upload routes: rate limit, verified account, owned live set.</summary>
    private static async Task<IResult?> gateUploadAsync(HttpContext ctx, Db db, long setId, AuthedUser user)
    {
        if (!upload_limiter.Allow(user.Id.ToString()))
            return WireJson.Error(StatusCodes.Status429TooManyRequests, "Too many uploads. Please wait a while before submitting again.");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await isVerifiedAsync(conn, user.Id))
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, verification_required_message);

        return await loadOwnedSetForSubmissionAsync(conn, setId, user);
    }

    /// <summary>404 unknown set, 403 not the owner, 422 taken down; null when submittable.</summary>
    private static async Task<IResult?> loadOwnedSetForSubmissionAsync(NpgsqlConnection conn, long setId, AuthedUser user)
    {
        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return WireJson.Error(StatusCodes.Status404NotFound, "beatmapset not found");

        if (row.OwnerId != user.Id)
            return WireJson.Error(StatusCodes.Status403Forbidden, "You do not own this beatmap set.");

        // A takedown ('removed', e.g. DMCA) is final from the submission side: re-uploading must
        // not resurrect the set (only the publish flip from 'hidden' ever changes status here).
        if (row.Status == "removed")
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "This beatmap set has been removed and can no longer be updated.");

        return null;
    }

    /// <summary>
    /// The tail both upload routes share, running entirely inside the caller's per-set ingest
    /// scope: Parse → Validate (422 on invariant violations) → IngestAsync with the SAME
    /// seekable stream. Diff-liveness refresh, the publish flip and all artifact publication
    /// happen inside <see cref="PackageIngest.IngestAsync"/>'s transaction; nothing runs after
    /// the commit, so a crash at any point leaves either the whole new version or none of it.
    /// </summary>
    private static async Task<IResult> parseValidateIngestAsync(
        Stream package, long setId, AuthedUser user, PackageIngest ingest, PackageIngest.SetScope scope,
        string route, ILogger logger, CacheEviction eviction, CancellationToken ct)
    {
        // Read under the scope's lock: the snapshot validation pins embedded ids against.
        long[] allocatedIds = (await scope.Connection.QueryAsync<long>(
            "SELECT id FROM beatmaps WHERE set_id = @setId", new { setId })).ToArray();

        ParsedPackage parsed;

        try
        {
            parsed = BeatmapPackageParser.Parse(package);
            PackageValidator.Validate(parsed, setId, allocatedIds, user.Username);
        }
        catch (PackageValidationException e)
        {
            logger.LogWarning(e, "Set {SetId}: {Route} upload rejected for user {UserId} ({Username}): {Message}",
                setId, route, user.Id, user.Username, e.Message);
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, e.Message);
        }

        await ingest.IngestAsync(scope, package, parsed, setId, user.Id, ct);

        // The version is committed (IngestAsync commits the scope, publish flip included): every
        // cached read of the set, the listing, the landing strip, the owner's profile and the
        // lookup memo goes (backlog 366).
        await eviction.AfterSetChangedAsync(setId, user.Id);

        return Results.NoContent();
    }

    private static async Task<bool> isVerifiedAsync(NpgsqlConnection conn, long userId)
        => await conn.ExecuteScalarAsync<bool>(
            "SELECT verified_at IS NOT NULL FROM users WHERE id = @userId", new { userId });

    /// <summary>
    /// Raises Kestrel's per-request body cap and lowers its minimum body data rate to
    /// <see cref="UploadMinBodyDataRate"/> on this endpoint only (both features are absent under
    /// TestServer, and the size feature is read-only once the body started flowing; those cases
    /// are skipped). Runs as an endpoint filter, i.e. before the handler ever touches
    /// Request.Body/Form.
    /// </summary>
    private static TBuilder WithUploadBodyLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var sizeFeature = invocationContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (sizeFeature is { IsReadOnly: false })
                    sizeFeature.MaxRequestBodySize = MaxUploadBodyBytes;

                var rateFeature = invocationContext.HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();

                if (rateFeature != null)
                    rateFeature.MinDataRate = UploadMinBodyDataRate;

                return await next(invocationContext);
            });
        });

        return builder;
    }

    /// <summary>A self-deleting temp file for buffering an upload into a seekable stream.</summary>
    private static FileStream createTempBuffer()
        => new FileStream(
            Path.Combine(Path.GetTempPath(), "typebeat-bss-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    /// <summary>
    /// Body-read failures the CLIENT caused: Kestrel's over-the-cap abort surfaces as
    /// BadHttpRequestException (413), a malformed multipart body/Content-Type as
    /// InvalidDataException, and a truncated body (garbage where a boundary should be, or a
    /// mid-body disconnect) as IOException. All of these must produce the 422 error envelope,
    /// never a raw 500; the wizard surfaces the message verbatim.
    /// </summary>
    private static bool isClientBodyError(Exception e)
        => e is BadHttpRequestException or InvalidDataException or IOException;

    private static IResult uploadBodyError(Exception e)
        => e is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
            ? WireJson.Error(StatusCodes.Status413PayloadTooLarge, $"The upload exceeds the {MaxUploadBodyBytes / (1024 * 1024)} MB request limit.")
            : WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The upload body could not be read as multipart/form-data.");

    /// <summary>
    /// checksum_md5 for a just-allocated (never-uploaded) beatmap row: random 32-hex so the
    /// global UNIQUE constraint holds until the first real upload overwrites it.
    /// </summary>
    private static string placeholderChecksum()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
