using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The lazer-compatible beatmap submission service (BSS) at /bss — the three endpoints the
/// editor's submission wizard calls (recon result.bss.endpoint_sequence / minimal_subset):
///
///  - <c>PUT /bss/beatmapsets</c> (JSON): create a fresh set or re-target an existing one;
///    allocates beatmap ids and returns the latest version's file manifest (EMPTY for a fresh
///    set — this drives the client's replace-vs-patch branch).
///  - <c>PUT /bss/beatmapsets/{id}</c> (multipart, one <c>beatmapArchive</c> part): full
///    package upload → Parse → Validate → Ingest → 204.
///  - <c>PATCH /bss/beatmapsets/{id}</c> (multipart, repeated <c>filesChanged</c> file parts +
///    repeated <c>filesDeleted</c> form fields): rebuild the latest version's package with the
///    overlay applied, then the same Parse → Validate → Ingest → 204.
///
/// Errors follow the upstream contract: 422 <c>{"error": "..."}</c> for invariants (WireJson),
/// 403 for ownership, 404 for a missing set. Auth is the existing bearer token; submission
/// additionally requires <c>users.verified_at</c> (manual lever: deploy/verify-user.sh).
///
/// Set/diff lifecycle conventions owned here:
///  - a fresh set is created with status 'hidden' and flipped to 'public' by its first
///    successful upload, so empty shells never appear in listings;
///  - beatmap rows are NEVER deleted (scores FK); a diff dropped from beatmaps_to_keep — or
///    absent from an uploaded package — gets <c>filename = NULL</c>, which is the repo-wide
///    "not part of the current version" marker (live diffs have <c>filename IS NOT NULL</c>);
///  - newly allocated beatmap rows carry a random placeholder checksum until the first upload
///    overwrites it (checksum_md5 is NOT NULL UNIQUE, and real rows at allocation time keep
///    PackageIngest's sequence realignment race-free).
/// </summary>
public static class BssEndpoints
{
    /// <summary>
    /// Per-endpoint Kestrel body cap for the two package-upload routes only (~100 MB: above
    /// PackageValidator's 95 MiB package cap + multipart overhead, below Cloudflare's 100 MB
    /// proxied-body limit). Everything else keeps Kestrel's ~28.6 MB default.
    /// </summary>
    public const long MaxUploadBodyBytes = 100L * 1024 * 1024;

    // In-memory speed bump on the two upload routes, keyed by user id (Cloudflare WAF is the
    // real production layer, same doctrine as the login/register limiters).
    private const int uploads_per_window = 12;
    private static readonly FixedWindowLimiter upload_limiter = new(uploads_per_window, TimeSpan.FromHours(1));

    private const string verification_required_message =
        "Beatmap submission requires a verified account. Verification is currently manual — ask an admin to verify your account.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPut("/bss/beatmapsets", PutBeatmapSetAsync).RequireBearer();
        app.MapPut("/bss/beatmapsets/{setId:long}", UploadFullPackageAsync).RequireBearer().WithUploadBodyLimit();
        app.MapPatch("/bss/beatmapsets/{setId:long}", PatchPackageAsync).RequireBearer().WithUploadBodyLimit();
    }

    // ---------------------------------------------------------------------------------------------
    // PUT /bss/beatmapsets — create or re-target a set, allocate ids, return the latest manifest.
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

        await using (var tx = await conn.BeginTransactionAsync(ctx.RequestAborted))
        {
            if (request.BeatmapsetId == null)
            {
                // Hidden until the first successful package upload publishes it (see class doc).
                setId = await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO beatmapsets (owner_id, status) VALUES (@ownerId, 'hidden') RETURNING id",
                    new { ownerId = user.Id });
            }
            else
            {
                // Drop diffs the client no longer keeps. Rows stay (scores FK); filename = NULL
                // marks them as not-part-of-the-current-version.
                await conn.ExecuteAsync(
                    "UPDATE beatmaps SET filename = NULL WHERE set_id = @setId AND id <> ALL(@keep)",
                    new { setId, keep });
            }

            var newIds = new List<long>(request.BeatmapsToCreate);

            for (int i = 0; i < request.BeatmapsToCreate; i++)
            {
                newIds.Add(await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@setId, @placeholder) RETURNING id",
                    new { setId, placeholder = placeholderChecksum() }));
            }

            await tx.CommitAsync(ctx.RequestAborted);

            // The latest version's manifest ([] for a fresh set) — the client's replace-vs-patch pivot.
            var files = await ingest.GetLatestVersionFilesAsync(setId, ctx.RequestAborted);

            return WireJson.Ok(new BssPutBeatmapSetResponse
            {
                BeatmapsetId = setId,
                BeatmapIds = newIds,
                Files = files.Select(f => new BssFileWire { Filename = f.Filename, Sha2Hash = f.Sha256Hex }).ToList(),
            });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PUT /bss/beatmapsets/{id} — full package upload (single "beatmapArchive" file part).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> UploadFullPackageAsync(long setId, HttpContext ctx, Db db, PackageIngest ingest)
    {
        var user = ctx.AuthedUser();

        if (await gateUploadAsync(ctx, db, setId, user) is { } gateError)
            return gateError;

        IFormCollection form;

        try
        {
            form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (isClientBodyError(e))
        {
            return uploadBodyError(e);
        }

        var archive = form.Files.GetFile("beatmapArchive");

        if (archive == null)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "The request is missing the beatmapArchive file part.");

        // Parse/ingest need one seekable stream over the whole package; multipart sections are
        // forward-only, so buffer to a self-deleting temp file first.
        await using var buffer = createTempBuffer();

        await using (var source = archive.OpenReadStream())
            await source.CopyToAsync(buffer, ctx.RequestAborted);

        return await parseValidateIngestAsync(buffer, setId, user, db, ingest, ctx.RequestAborted);
    }

    // ---------------------------------------------------------------------------------------------
    // PATCH /bss/beatmapsets/{id} — delta upload: latest version overlaid with filesChanged
    // minus filesDeleted, rebuilt into a full package, then the same ingest path.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> PatchPackageAsync(long setId, HttpContext ctx, Db db, PackageIngest ingest, IFileStore fileStore)
    {
        var user = ctx.AuthedUser();

        if (await gateUploadAsync(ctx, db, setId, user) is { } gateError)
            return gateError;

        IFormCollection form;

        try
        {
            form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (isClientBodyError(e))
        {
            return uploadBodyError(e);
        }

        var manifest = await ingest.GetLatestVersionFilesAsync(setId, ctx.RequestAborted);

        if (manifest.Count == 0)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "This beatmap set has no uploaded version to patch; upload the full package instead.");

        // Same filename comparison the version manifest itself uses: normalized slashes,
        // case-insensitive (PackageValidator rejects case-colliding duplicates).
        var deleted = new HashSet<string>(
            form["filesDeleted"].Where(v => !string.IsNullOrEmpty(v)).Select(v => BeatmapPackageParser.NormalizeFilename(v!)),
            StringComparer.OrdinalIgnoreCase);

        var changed = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in form.Files.Where(f => f.Name == "filesChanged"))
        {
            if (string.IsNullOrEmpty(file.FileName))
                return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "A filesChanged part is missing its archive path (multipart filename).");

            changed[BeatmapPackageParser.NormalizeFilename(file.FileName)] = file;
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

            foreach (var (filename, file) in changed)
            {
                if (deleted.Contains(filename))
                    continue;

                var entry = zip.CreateEntry(filename);

                await using var entryStream = entry.Open();
                await using var source = file.OpenReadStream();
                await source.CopyToAsync(entryStream, ctx.RequestAborted);
            }
        }

        return await parseValidateIngestAsync(buffer, setId, user, db, ingest, ctx.RequestAborted);
    }

    // ---------------------------------------------------------------------------------------------
    // Shared plumbing.
    // ---------------------------------------------------------------------------------------------

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
    /// The tail both upload routes share: Parse → Validate (422 on invariant violations) →
    /// IngestAsync with the SAME seekable stream, then refresh diff liveness and publish a
    /// hidden set on its first successful version.
    /// </summary>
    private static async Task<IResult> parseValidateIngestAsync(
        Stream package, long setId, AuthedUser user, Db db, PackageIngest ingest, CancellationToken ct)
    {
        long[] allocatedIds;

        await using (var conn = await db.OpenAsync(ct))
            allocatedIds = (await conn.QueryAsync<long>("SELECT id FROM beatmaps WHERE set_id = @setId", new { setId })).ToArray();

        ParsedPackage parsed;

        try
        {
            parsed = BeatmapPackageParser.Parse(package);
            PackageValidator.Validate(parsed, setId, allocatedIds, user.Username);
        }
        catch (PackageValidationException e)
        {
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, e.Message);
        }

        await ingest.IngestAsync(package, parsed, setId, user.Id, ct);

        await using (var conn = await db.OpenAsync(ct))
        {
            // Liveness refresh: the uploaded package IS the current version — any diff row not
            // in it stops being referenced (validated ids ⊆ allocated, so this only ever clears).
            long[] liveIds = parsed.Difficulties.Select(d => d.BeatmapId!.Value).ToArray();

            await conn.ExecuteAsync(
                "UPDATE beatmaps SET filename = NULL WHERE set_id = @setId AND id <> ALL(@liveIds)",
                new { setId, liveIds });

            // First successful upload publishes the set. 'removed' is deliberately not touched.
            await conn.ExecuteAsync(
                "UPDATE beatmapsets SET status = 'public' WHERE id = @setId AND status = 'hidden'",
                new { setId });
        }

        return Results.NoContent();
    }

    private static async Task<bool> isVerifiedAsync(NpgsqlConnection conn, long userId)
        => await conn.ExecuteScalarAsync<bool>(
            "SELECT verified_at IS NOT NULL FROM users WHERE id = @userId", new { userId });

    /// <summary>
    /// Raises Kestrel's per-request body cap on this endpoint only (the feature is absent under
    /// TestServer and read-only once the body started flowing — both cases are skipped). Runs
    /// as an endpoint filter, i.e. before the handler ever touches Request.Body/Form.
    /// </summary>
    private static TBuilder WithUploadBodyLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var feature = invocationContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = MaxUploadBodyBytes;

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
    /// BadHttpRequestException (413), malformed multipart as InvalidDataException.
    /// </summary>
    private static bool isClientBodyError(Exception e)
        => e is BadHttpRequestException or InvalidDataException;

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
