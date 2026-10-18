using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Ops surface for the isolated-vocals-stem backfill (backlog 393), on the same private footing as
/// <see cref="OpsEndpoints"/>: guarded by the single static <c>TYPEBEAT_BUDDY_KEY</c> through
/// <see cref="BuddyEndpoints.Authorised"/>, 404 (not 401) when the key is unset, so an un-opted-in
/// deploy exposes nothing. It is NOT the osu-compatible wire surface the game speaks.
///
/// <para>
/// Three routes, all for the LOCAL Demucs driver (see <c>tools/stem-backfill</c>):
/// <list type="bullet">
///   <item><c>GET /api/v2/ops/stems/missing</c>: the sets whose current version carries no
///   <c>vocals.ogg</c>, with the audio filename and blob identity so the driver knows what to
///   separate. Backed by <see cref="StemBackfill.ListMissingStemAsync"/>.</item>
///   <item><c>GET /api/v2/ops/stems/audio/{setId}</c>: streams a set's current-version audio, so the
///   driver separates the exact bytes the store holds rather than whatever a local copy drifted to.</item>
///   <item><c>PUT /api/v2/ops/stems/{setId}</c>: the raw <c>vocals.ogg</c> body; attaches it as a new
///   version (rank-safe, see <see cref="StemBackfill"/>) and returns the new version number. The
///   client's ordinary UPDATE then pulls it.</item>
/// </list>
/// </para>
///
/// <para>
/// The key gates the whole thing, but the endpoints are still keyed by set id and accept a raw body,
/// so this is an OPERATOR tool, not a user one: it belongs behind the ops key and is driven by the
/// local script, never by a browser.
/// </para>
/// </summary>
public static class OpsStemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/ops/stems/missing", Missing);
        app.MapGet("/api/v2/ops/stems/audio/{setId:long}", Audio);
        app.MapPut("/api/v2/ops/stems/{setId:long}", Attach);
    }

    private static async Task<IResult> Missing(HttpContext ctx, Db db, IFileStore store, IConfiguration config, ILoggerFactory loggerFactory)
    {
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        var logger = loggerFactory.CreateLogger("StemBackfill");
        var rows = await StemBackfill.ListMissingStemAsync(db, store, logger, ctx.RequestAborted);

        return WireJson.Ok(new
        {
            count = rows.Count,
            sets = rows.Select(r => new
            {
                set_id = r.SetId,
                artist = r.Artist,
                title = r.Title,
                status = r.Status,
                current_version = r.CurrentVersion,
                live_diffs = r.LiveDiffs,
                audio_filename = r.AudioFilename,
                audio_sha256 = r.AudioSha256Hex,
                audio_size = r.AudioSize,
            }),
        });
    }

    private static async Task<IResult> Audio(long setId, HttpContext ctx, Db db, IFileStore store, IConfiguration config)
    {
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        string? sha256 = ctx.Request.Query["sha256"];

        if (string.IsNullOrWhiteSpace(sha256))
            return WireJson.Error(StatusCodes.Status400BadRequest, "sha256 query parameter is required.");

        Stream? stream;

        try
        {
            stream = await StemBackfill.OpenAudioBlobAsync(db, store, setId, sha256, ctx.RequestAborted);
        }
        catch (FormatException)
        {
            return WireJson.Error(StatusCodes.Status400BadRequest, "sha256 is not valid hex.");
        }

        if (stream == null)
            return Results.NotFound();

        string filename = ctx.Request.Query["filename"].FirstOrDefault() ?? "audio";

        return Results.Stream(stream, "application/octet-stream", fileDownloadName: MediaEndpoints.SanitizeFilename(filename));
    }

    private static async Task<IResult> Attach(long setId, HttpContext ctx, Db db, IFileStore store, IConfiguration config, DiskGuard disk, ILoggerFactory loggerFactory)
    {
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        // Writing a blob to the store, so the same disk floor every other write route holds applies
        // (507 rather than 503: this is an operator tool, not a gateway blip).
        if (disk.Current is { UploadsRefused: true } status)
            return WireJson.Error(StatusCodes.Status507InsufficientStorage, DiskGuard.UploadRefusal(status));

        if (ctx.Request.ContentLength is { } length && length > StemBackfill.MaxStemBytes)
            return WireJson.Error(StatusCodes.Status413PayloadTooLarge,
                $"The vocals stem exceeds the {StemBackfill.MaxStemBytes} byte limit.");

        byte[] content;

        using (var buffer = new MemoryStream())
        {
            // Capped read: a chunked body with no Content-Length cannot slip past the check above.
            byte[] chunk = new byte[81920];
            long total = 0;
            int read;

            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
            {
                total += read;

                if (total > StemBackfill.MaxStemBytes)
                    return WireJson.Error(StatusCodes.Status413PayloadTooLarge,
                        $"The vocals stem exceeds the {StemBackfill.MaxStemBytes} byte limit.");

                buffer.Write(chunk, 0, read);
            }

            content = buffer.ToArray();
        }

        if (content.Length == 0)
            return WireJson.Error(StatusCodes.Status400BadRequest, "The vocals stem body is empty.");

        try
        {
            var result = await StemBackfill.AttachVocalsStemAsync(db, store, setId, content, ctx.RequestAborted);

            loggerFactory.CreateLogger("StemBackfill").LogInformation(
                "Attached {Filename} ({Sha256}, {Bytes} bytes) to set {SetId} as v{VersionNo} (new version: {Cut}).",
                result.Filename, result.Sha256Hex, content.Length, setId, result.VersionNo, result.CutNewVersion);

            return WireJson.Ok(new
            {
                set_id = result.SetId,
                version_no = result.VersionNo,
                cut_new_version = result.CutNewVersion,
                sha256 = result.Sha256Hex,
                filename = result.Filename,
            });
        }
        catch (StemBackfill.StemBackfillException e)
        {
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, e.Message);
        }
    }
}
