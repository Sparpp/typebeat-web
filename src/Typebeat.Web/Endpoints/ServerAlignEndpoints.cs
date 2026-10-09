using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Typebeat.Web.Align;
using Typebeat.Web.Auth;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The server-hosted auto-aligner (backlog 413), an OPT-IN on the game's import screen for players
/// who cannot run the local aligner.
///
///  - POST   /api/v2/typebeat/server-align        multipart: audio, extension, lyrics, artist, title,
///                                                 language, vocal_mode
///                                                 200 { id, state: "pending", queue_position, aligner_version }
///  - GET    /api/v2/typebeat/server-align/{id}   { id, state, queue_position, progress, timing_json,
///                                                 error, aligner_version }
///  - DELETE /api/v2/typebeat/server-align/{id}   { id, state: "cancelled" }, idempotent for the owner
///
/// Jobs are files on the shared /data volume (<see cref="AlignJobStore"/>); the aligner worker
/// container (deploy/aligner/) runs them one at a time with the aligner script the game pin ships.
/// Bearer-authed; a job is only visible to its creator (404 to anyone else).
///
/// A submission is refused BEFORE its body is read whenever that is decidable from the headers and
/// the job store: 507 while the disk guard refuses uploads, 503 when the worker is not running or
/// the queue is full, 409 while the player already has a job, 429 at the daily cap. The same
/// checks run again, under a lock, when the job is created.
///
/// The old route, <c>/api/v2/typebeat/align</c>, stays a 410 for pre-287 clients (<see cref="AlignEndpoints"/>).
/// </summary>
public static class ServerAlignEndpoints
{
    public const string Path = "/api/v2/typebeat/server-align";

    private const int status_unprocessable = StatusCodes.Status422UnprocessableEntity;

    /// <summary>The whole multipart body: the audio, the lyrics, and room for the small fields.</summary>
    public const long MaxBodyBytes = AlignJobStore.MAX_AUDIO_BYTES + AlignJobStore.MAX_LYRICS_BYTES + 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(Path, CreateJob)
           .RequireBearer()
           .RequireRateLimiting(RateLimits.ServerAlignSubmit)
           .DisableAntiforgery()
           .WithAlignBodyLimit();
        app.MapGet(Path + "/{id}", GetJob).RequireBearer();
        app.MapDelete(Path + "/{id}", CancelJob).RequireBearer();
    }

    /// <summary>
    /// Raises Kestrel's per-request body cap on the create endpoint only (the default ~30 MB is
    /// below the 64 MB audio allowance) and lowers its minimum data rate as the BSS uploads do, so a
    /// slow uplink is not cut off. Same filter shape as BssEndpoints.WithUploadBodyLimit: the
    /// features are absent under TestServer, and the size one is read-only mid-body; both skipped.
    /// </summary>
    private static TBuilder WithAlignBodyLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var sizeFeature = invocationContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (sizeFeature is { IsReadOnly: false })
                    sizeFeature.MaxRequestBodySize = MaxBodyBytes;

                var rateFeature = invocationContext.HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();

                if (rateFeature != null)
                    rateFeature.MinDataRate = BssEndpoints.UploadMinBodyDataRate;

                return await next(invocationContext);
            });
        });

        return builder;
    }

    /// <summary>The message a refused admission carries (null when it was admitted).</summary>
    public static IResult? Refusal(AlignJobStore.Admission admission, DateTimeOffset now)
    {
        switch (admission.Refusal)
        {
            case AlignJobStore.AdmissionRefusal.WorkerDown:
                return WireJson.Unavailable(AlignJobStore.RefusalWorkerDown, 300);

            case AlignJobStore.AdmissionRefusal.QueueFull:
                return WireJson.Unavailable(AlignJobStore.RefusalQueueFull, 300);

            case AlignJobStore.AdmissionRefusal.ActiveJob:
                return WireJson.Error(StatusCodes.Status409Conflict,
                    $"you already have a server alignment in progress (job {admission.ActiveJobId}); wait for it to finish or cancel it first");

            case AlignJobStore.AdmissionRefusal.DailyCap:
                return WireJson.Error(StatusCodes.Status429TooManyRequests, DailyCapMessage(now));

            default:
                return null;
        }
    }

    /// <summary>The 429 at the daily cap: says how many, and when it resets (00:00 UTC) from now.</summary>
    public static string DailyCapMessage(DateTimeOffset now)
    {
        var reset = AlignJobStore.NextDailyReset(now);
        int total = Math.Max(1, (int)Math.Ceiling((reset - now).TotalMinutes));
        int hours = total / 60, minutes = total % 60;

        string inWords = hours > 0 ? $"{hours} h {minutes} min" : $"{minutes} min";
        return $"you have used today's {AlignJobStore.JobsPerDay} server alignments; the limit resets at 00:00 UTC (in {inWords}), or use the local aligner";
    }

    private static async Task<IResult> CreateJob(HttpContext ctx, AlignJobStore store, DiskGuard disk)
    {
        var user = ctx.AuthedUser();

        // Disk guard (backlog 365): below the upload floor nothing is written, refused before the body.
        if (disk.Current is { UploadsRefused: true } refusedAt)
            return WireJson.Error(StatusCodes.Status507InsufficientStorage, DiskGuard.UploadRefusal(refusedAt));

        // Everything the job store can decide without the body, before a 64 MB upload is drained.
        if (Refusal(store.Check(user.Id), DateTimeOffset.UtcNow) is { } early)
            return early;

        if (ctx.Request.ContentLength is long declared && declared > MaxBodyBytes)
            return WireJson.Error(status_unprocessable, "audio file too large (64 MB max)");

        if (!ctx.Request.HasFormContentType)
            return WireJson.Error(status_unprocessable, "multipart form expected");

        IFormCollection form;

        try
        {
            form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
        {
            // A form value past FormOptions' limits, or a body past the raised Kestrel cap.
            return WireJson.Error(status_unprocessable, "upload too large (64 MB audio, 64 KB lyrics max)");
        }

        var audio = form.Files.GetFile("audio");
        string lyrics = form["lyrics"].ToString();
        string artist = form["artist"].ToString().Trim();
        string title = form["title"].ToString().Trim();

        if (audio == null || audio.Length == 0)
            return WireJson.Error(status_unprocessable, "missing audio file");

        if (audio.Length > AlignJobStore.MAX_AUDIO_BYTES)
            return WireJson.Error(status_unprocessable, "audio file too large (64 MB max)");

        // The game client's multipart writer doesn't carry a usable part filename, so it sends
        // the extension as an explicit field; browsers/tests supply a real FileName instead.
        string extension = form["extension"].ToString().Trim();

        if (string.IsNullOrEmpty(extension))
            extension = System.IO.Path.GetExtension(audio.FileName);
        else if (!extension.StartsWith('.'))
            extension = "." + extension;

        if (!AlignJobStore.IsAllowedAudioExtension(extension))
            return WireJson.Error(status_unprocessable, "unsupported audio format");

        if (string.IsNullOrWhiteSpace(lyrics))
            return WireJson.Error(status_unprocessable, "missing lyrics");

        if (System.Text.Encoding.UTF8.GetByteCount(lyrics) > AlignJobStore.MAX_LYRICS_BYTES)
            return WireJson.Error(status_unprocessable, "lyrics too large (64 KB max)");

        string vocalMode = form["vocal_mode"].ToString().Trim().ToLowerInvariant();

        if (vocalMode.Length == 0)
            vocalMode = "aligned";
        else if (vocalMode is not ("aligned" or "estimated"))
            return WireJson.Error(status_unprocessable, "vocal_mode must be \"aligned\" or \"estimated\"");

        // Canonical name or "" (the aligner's own detector decides); never anything else on the
        // worker's command line.
        string language = BeatmapLanguages.Normalize(form["language"].ToString());

        var request = new AlignJobStore.JobRequest(clip(artist), clip(title), extension, lyrics, language, vocalMode);

        await using var audioStream = audio.OpenReadStream();
        var admitted = await store.TryCreateJobAsync(user.Id, request, audioStream, ctx.RequestAborted);

        if (Refusal(admitted, DateTimeOffset.UtcNow) is { } late)
            return late;

        return WireJson.Ok(new
        {
            id = admitted.Id,
            state = "pending",
            queue_position = admitted.QueuePosition,
            aligner_version = admitted.AlignerVersion,
        });
    }

    private static string clip(string value) => value.Length > 256 ? value[..256] : value;

    private static async Task<IResult> GetJob(string id, HttpContext ctx, AlignJobStore store)
    {
        var user = ctx.AuthedUser();
        var status = await store.ReadStatusAsync(id, user.Id, ctx.RequestAborted);

        if (status == null)
            return WireJson.Error(StatusCodes.Status404NotFound, "no such alignment job");

        return WireJson.Ok(new
        {
            id = status.Id,
            state = status.State,
            queue_position = status.QueuePosition,
            progress = status.Progress,
            timing_json = status.TimingJson,
            error = status.Error,
            aligner_version = status.AlignerVersion,
        });
    }

    private static async Task<IResult> CancelJob(string id, HttpContext ctx, AlignJobStore store)
    {
        var user = ctx.AuthedUser();

        // Idempotent for the owner (already-finished jobs report success); 404 for unknown or
        // not-the-caller's, same visibility rule as GET (a job is only its creator's).
        bool cancelled = await store.RequestCancelAsync(id, user.Id, ctx.RequestAborted);

        return cancelled
            ? WireJson.Ok(new { id, state = "cancelled" })
            : WireJson.Error(StatusCodes.Status404NotFound, "no such alignment job");
    }
}
