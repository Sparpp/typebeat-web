using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Replay upload/download, the server half of the replay pipeline (backlog 37).
///
///  - PUT /api/v2/scores/{scoreId}/replay: bearer, owner only, raw .osr bytes as the request
///    body (application/octet-stream). 204 on success, 404 unknown score, 403 non-owner,
///    413 over the size cap. Idempotent: the owner may re-upload and the newer bytes win.
///  - GET /api/v2/scores/{scoreId}/replay: public (leaderboards are public), streams the stored
///    bytes back verbatim as application/octet-stream, 404 when nothing is stored. This is the
///    ONLY path that serves replay bytes anywhere (the game client's watch-replay action and the
///    website's "replay" / "Download" links all come here), so it is also where a view is counted
///    for the profile's "Replays watched by others": see <see cref="countViewAsync"/>. The wire
///    contract is untouched by that, a counted view changes nothing about the response.
///
/// Storage: the bytes are a NAMED object at <c>replays/{scoreId}.osr</c>
/// (<see cref="StoreKeys.Replay"/>) and <c>scores.replay_key</c> is the index over it (migration
/// 014). Named rather than content-addressed because a replay belongs to exactly one score (no
/// dedup to win) and the contract is overwrite-on-re-upload, which write-once blobs cannot do
/// without orphaning bytes forever.
///
/// Fidelity is NOT re-verified here: the game's engine is deterministic, so a replay either
/// reproduces its score on playback or it does not, and re-simulating server-side would mean
/// porting the whole engine again. Validation is deliberately cheap: non-empty, under the cap,
/// and structurally shaped like a legacy replay (<see cref="LooksLikeLegacyReplay"/>).
/// </summary>
public static class ReplayEndpoints
{
    /// <summary>
    /// Upload cap. A type!beat replay is a list of (integral-ms, char) pairs for one song, so a
    /// long map is still tens of KB compressed; 5 MB is orders of magnitude of headroom while
    /// bounding what one account can push into the /data volume.
    /// </summary>
    public const long MaxReplayBytes = 5L * 1024 * 1024;

    /// <summary>
    /// In-memory speed bump, keyed by user id, the same pattern as the BSS upload limiter. Owner-
    /// only already bounds this hard (you can only overwrite replays of scores you submitted, and
    /// submitting a score costs a whole play), so the budget is generous; it exists to stop a
    /// runaway client from rewriting the same object in a loop. Cloudflare WAF is the real layer.
    /// </summary>
    private const int uploads_per_window = 120;

    private static readonly FixedWindowLimiter upload_limiter = new(uploads_per_window, TimeSpan.FromHours(1));

    public static void Map(IEndpointRouteBuilder app)
    {
        // The hourly per-user cap below stays; the per-minute policy (backlog 366) stops a burst
        // before it reaches the database at all. The game backfills a few replays per board fetch.
        app.MapPut("/api/v2/scores/{scoreId:long}/replay", UploadAsync).RequireBearer().WithReplayBodyLimit()
            .RequireRateLimiting(RateLimits.ReplayUpload);

        // Public: leaderboards are public, and the client fetches a replay for any row it shows.
        app.MapGet("/api/v2/scores/{scoreId:long}/replay", DownloadAsync);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT: store (or overwrite) the caller's own replay for one score.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> UploadAsync(long scoreId, HttpContext ctx, Db db, IFileStore store, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();

        if (!upload_limiter.Allow(user.Id.ToString(CultureInfo.InvariantCulture)))
            return WireJson.Error(StatusCodes.Status429TooManyRequests, "too many replay uploads");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // Ownership is settled BEFORE the body is read, so a stranger's oversized upload is
        // rejected without ever being buffered.
        long? ownerId = await conn.ExecuteScalarAsync<long?>(
            "SELECT user_id FROM scores WHERE id = @scoreId", new { scoreId });

        if (ownerId is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        if (ownerId != user.Id)
            return WireJson.Error(StatusCodes.Status403Forbidden, "not your score");

        // Cheap reject when the client declares an over-cap length up front.
        if (ctx.Request.ContentLength is long declared && declared > MaxReplayBytes)
            return tooLarge();

        byte[] replay;

        try
        {
            replay = await readBoundedAsync(ctx.Request.Body, MaxReplayBytes, ctx.RequestAborted);
        }
        catch (BodyTooLargeException)
        {
            return tooLarge();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's own cap fired first (WithReplayBodyLimit) and aborted the body.
            return tooLarge();
        }
        catch (Exception e) when (e is BadHttpRequestException or IOException)
        {
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "the replay body could not be read");
        }

        if (!LooksLikeLegacyReplay(replay))
        {
            loggerFactory.CreateLogger("ReplayUpload").LogInformation(
                "Score {ScoreId}: rejected a {Bytes}-byte upload that is not shaped like a legacy replay.", scoreId, replay.Length);

            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "not a replay file");
        }

        string key = StoreKeys.Replay(scoreId);

        // WriteObjectAsync is temp-file + atomic move, so a re-upload either fully replaces the
        // old object or leaves it intact; a reader never sees a half-written replay.
        using (var buffer = new MemoryStream(replay, writable: false))
            await store.WriteObjectAsync(key, buffer, ctx.RequestAborted);

        long? replayBeatmapId = await conn.ExecuteScalarAsync<long?>(
            """
            UPDATE scores
            SET replay_key = @key, replay_bytes = @bytes, replay_uploaded_at = now()
            WHERE id = @scoreId
            RETURNING beatmap_id
            """,
            new { key, bytes = replay.Length, scoreId });

        // The board row's has_replay just flipped (backlog 366: the board memo is dropped).
        await ctx.RequestServices.GetRequiredService<CacheEviction>().AfterReplayAsync(replayBeatmapId);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------------------------------------
    // GET: stream a stored replay back. Public, no auth.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> DownloadAsync(long scoreId, HttpContext ctx, Db db, IFileStore store, ILoggerFactory loggerFactory)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // The owner comes back with the key because the view counter needs it: "watched by others"
        // is decided here, where the requester is known, and nowhere else.
        var row = await conn.QuerySingleOrDefaultAsync<(string? Key, long OwnerId)?>(
            "SELECT replay_key AS Key, user_id AS OwnerId FROM scores WHERE id = @scoreId", new { scoreId });

        if (row is not { Key: string key })
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        var stream = await store.OpenObjectReadAsync(key, ctx.RequestAborted);

        // The column says stored but the object is gone (a restored DB without its volume, a
        // manual cleanup). Same answer as "never uploaded": nothing to watch.
        if (stream == null)
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        await countViewAsync(ctx, conn, scoreId, row.Value.OwnerId, loggerFactory);

        // fileDownloadName is for the website's download link; the game client reads the body and
        // ignores Content-Disposition entirely.
        return Results.Stream(stream, "application/octet-stream", fileDownloadName: $"typebeat-{scoreId}.osr");
    }

    /// <summary>
    /// Credits one "replay watched by others" view for a serve that is about to happen, when the
    /// requester qualifies (see <see cref="ReplayViews"/> and 025_replay_views.sql for the full
    /// rule). Identity is read the same way the beatmapset download endpoint reads it: the website
    /// session cookie first, then a bearer token, which is what the game client carries on every
    /// API request. An anonymous requester counts nothing and is not an error.
    ///
    /// <para>
    /// Counted at the point the bytes are handed over, not on completion: the response streams
    /// after this handler returns, so "did they finish it" is not knowable here, and a replay is a
    /// few tens of KB anyway. Range requests cannot inflate this either, the stream is served
    /// without range processing, and the per-day dedup would absorb them regardless.
    /// </para>
    ///
    /// <para>
    /// FAILURES ARE SWALLOWED, deliberately. A counter is a nice-to-have on top of the thing the
    /// caller actually asked for; losing a view is invisible, while turning a watchable replay into
    /// a 500 because (say) the score row was deleted between the two statements is not.
    /// </para>
    /// </summary>
    private static async Task countViewAsync(
        HttpContext ctx, NpgsqlConnection conn, long scoreId, long ownerId, ILoggerFactory loggerFactory)
    {
        var viewer = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();

        if (viewer is null || viewer.Id == ownerId)
            return;

        try
        {
            await ReplayViews.RecordAsync(conn, scoreId, ownerId, viewer.Id, ctx.RequestAborted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("ReplayViews").LogWarning(
                e, "Score {ScoreId}: the replay was served but its view could not be counted.", scoreId);
        }
    }

    // ---- helpers ----

    private static IResult tooLarge()
        => WireJson.Error(StatusCodes.Status413PayloadTooLarge,
            $"a replay may not exceed {MaxReplayBytes / (1024 * 1024)} MB");

    /// <summary>
    /// Structural sanity check on an uploaded replay, NOT a parse. The legacy .osr layout the
    /// game's encoder writes starts with: 1 byte ruleset id, a 4-byte little-endian version int,
    /// then the beatmap-hash string in osu's serialization (0x0b = present + ULEB128 length,
    /// 0x00 = null). So byte 0 is a small ruleset id and byte 5 is one of those two markers.
    /// That is enough to reject an empty body, a stray screenshot or a zip, while staying blind
    /// to anything inside the replay (which the deterministic engine judges on playback anyway).
    /// </summary>
    public static bool LooksLikeLegacyReplay(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 6 && bytes[0] <= 3 && (bytes[5] == 0x0b || bytes[5] == 0x00);

    /// <summary>
    /// Reads the whole request body, refusing to buffer more than <paramref name="limit"/> bytes.
    /// Kestrel's per-endpoint cap does the same job in production but the feature is absent under
    /// TestServer, so the bound is enforced here too and is the one the tests exercise.
    /// </summary>
    private static async Task<byte[]> readBoundedAsync(Stream body, long limit, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];

        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new BodyTooLargeException();

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Lowers Kestrel's per-request body cap to the replay cap on this endpoint only, so an
    /// over-cap upload is aborted at the socket instead of streaming 28 MB into the process
    /// first. Absent under TestServer and read-only once the body started flowing; both are
    /// skipped and the in-handler bound still applies.
    /// </summary>
    private static TBuilder WithReplayBodyLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var feature = invocationContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = MaxReplayBytes;

                return await next(invocationContext);
            });
        });

        return builder;
    }

    private sealed class BodyTooLargeException : Exception;
}
