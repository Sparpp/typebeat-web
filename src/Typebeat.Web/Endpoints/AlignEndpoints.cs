using Microsoft.AspNetCore.Http.Features;
using Typebeat.Web.Align;
using Typebeat.Web.Auth;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Server-side lyric alignment for game clients without a local lyriclab environment (the
/// installed build ships no Python/torch — only dev checkouts have the aligner beside the exe).
///
///  - POST   /api/v2/typebeat/align        multipart: audio file + lyrics text (+ artist/title)
///                                          → { id, state: "pending" }; 409 while a job is active
///  - GET    /api/v2/typebeat/align/{id}   → { id, state, progress?, timing_json?, error? }
///  - DELETE /api/v2/typebeat/align/{id}   cancel: the worker stops aligning and the owner's slot
///                                          frees immediately (sent when the client abandons the wait)
///
/// Jobs are files on the shared /data volume (see <see cref="AlignJobStore"/>); the aligner
/// worker container processes them one at a time (torch/demucs are memory-hungry — serialization
/// IS the capacity plan on this box). Bearer-authed; a job is only visible to its creator.
/// </summary>
public static class AlignEndpoints
{
    private const int status_unprocessable = StatusCodes.Status422UnprocessableEntity;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/typebeat/align", CreateJob).RequireBearer().DisableAntiforgery().WithAlignBodyLimit();
        app.MapGet("/api/v2/typebeat/align/{id}", GetJob).RequireBearer();
        app.MapDelete("/api/v2/typebeat/align/{id}", CancelJob).RequireBearer();
    }

    /// <summary>
    /// Raises Kestrel's per-request body cap on the create endpoint only (default ~30 MB is below
    /// the 64 MB audio allowance). Same filter shape as BssEndpoints.WithUploadBodyLimit: absent
    /// under TestServer and read-only mid-body — both skipped.
    /// </summary>
    private static TBuilder WithAlignBodyLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var feature = invocationContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = AlignJobStore.MAX_AUDIO_BYTES + AlignJobStore.MAX_LYRICS_BYTES + 1024 * 1024;

                return await next(invocationContext);
            });
        });

        return builder;
    }

    private static async Task<IResult> CreateJob(HttpContext ctx, AlignJobStore store)
    {
        var user = ctx.AuthedUser();

        if (!ctx.Request.HasFormContentType)
            return WireJson.Error(status_unprocessable, "multipart form expected");

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var audio = form.Files.GetFile("audio");
        string lyrics = form["lyrics"].ToString();
        string artist = form["artist"].ToString();
        string title = form["title"].ToString();

        if (audio == null || audio.Length == 0)
            return WireJson.Error(status_unprocessable, "missing audio file");

        if (audio.Length > AlignJobStore.MAX_AUDIO_BYTES)
            return WireJson.Error(status_unprocessable, "audio file too large (64 MB max)");

        // The game client's multipart writer doesn't carry a usable part filename, so it sends
        // the extension as an explicit field; browsers/tests supply a real FileName instead.
        string extension = form["extension"].ToString();

        if (string.IsNullOrWhiteSpace(extension))
            extension = Path.GetExtension(audio.FileName);
        else if (!extension.StartsWith('.'))
            extension = "." + extension;

        if (!AlignJobStore.IsAllowedAudioExtension(extension))
            return WireJson.Error(status_unprocessable, "unsupported audio format");

        if (string.IsNullOrWhiteSpace(lyrics))
            return WireJson.Error(status_unprocessable, "missing lyrics");

        if (System.Text.Encoding.UTF8.GetByteCount(lyrics) > AlignJobStore.MAX_LYRICS_BYTES)
            return WireJson.Error(status_unprocessable, "lyrics too large (64 KB max)");

        // One active job per player: alignment is minutes of CPU; a queue pile-up from one user
        // must not starve the single worker.
        if (store.FindActiveJob(user.Id) is { } activeId)
            return WireJson.Error(StatusCodes.Status409Conflict, $"an alignment is already in progress (job {activeId})");

        await using var audioStream = audio.OpenReadStream();
        string id = await store.CreateJobAsync(user.Id, artist, title, extension, audioStream, lyrics, ctx.RequestAborted);

        return WireJson.Ok(new { id, state = "pending" });
    }

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
            progress = status.Progress,
            timing_json = status.TimingJson,
            error = status.Error,
        });
    }

    private static async Task<IResult> CancelJob(string id, HttpContext ctx, AlignJobStore store)
    {
        var user = ctx.AuthedUser();

        // Idempotent for the owner (already-finished jobs report success); 404 for unknown or
        // not-the-caller's — same visibility rule as GET (a job is only its creator's).
        bool cancelled = await store.RequestCancelAsync(id, user.Id, ctx.RequestAborted);

        return cancelled
            ? WireJson.Ok(new { id, state = "cancelled" })
            : WireJson.Error(StatusCodes.Status404NotFound, "no such alignment job");
    }
}
