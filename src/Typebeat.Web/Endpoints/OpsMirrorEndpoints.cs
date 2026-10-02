using System.Globalization;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/ops/mirror: the releases mirror's last sweep and every file it is failing on
/// (backlog 380), so a stuck mirror is visible rather than discovered as updaters streaming from
/// the box. Same footing as <see cref="OpsEndpoints"/>: private, gated by <c>TYPEBEAT_BUDDY_KEY</c>
/// through <see cref="BuddyEndpoints.Authorised"/>, 404 when the key is unset.
///
/// <para>A dumb readout like the others: no threshold, no edge. <c>enabled</c> false means the
/// public store is not configured and the mirror never runs; <c>lastSweep</c> null with
/// <c>enabled</c> true means it has not finished a pass since the app started.
/// <c>lastSweep.error</c> is set when the sweep could not run at all (the bucket could not be
/// listed); <c>failing</c> lists each key that still failed after every retry, with when it first
/// failed.</para>
/// </summary>
public static class OpsMirrorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/ops/mirror", Mirror);
    }

    private static IResult Mirror(HttpContext ctx, IConfiguration config, IPublicObjectStore publicStore, ReleasesMirrorStatus status)
    {
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        return Results.Json(Project(publicStore.Enabled, status));
    }

    /// <summary>The response body, pure, so it is testable without a host.</summary>
    public static object Project(bool enabled, ReleasesMirrorStatus status)
    {
        var last = status.LastSweep;

        return new
        {
            enabled,
            lastSweep = last is not { } s
                ? null
                : new
                {
                    at = stamp(s.StartedAt),
                    durationMs = s.DurationMs,
                    files = s.Files,
                    uploaded = s.Uploaded,
                    unchanged = s.Unchanged,
                    unverified = s.Unverified,
                    deleted = s.Deleted,
                    failed = s.Failed,
                    deletionsSkipped = s.DeletionsSkipped,
                    error = s.Error,
                },
            failing = status.Failing.Select(f => new
            {
                key = f.Key,
                operation = f.Operation,
                error = f.Error,
                attempts = f.Attempts,
                since = stamp(f.Since),
            }).ToArray(),
        };
    }

    private static string stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
