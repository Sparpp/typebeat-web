using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Tombstone for the FIRST server-side lyric aligner route (backlog 287), kept for the clients
/// built before it.
///
/// The route used to accept a multipart audio + lyrics upload, hand it to a torch/demucs worker
/// container over a shared /data job directory, and serve the resulting timing.json back through
/// GET/DELETE by job id. Backlog 413 brought the server aligner back, but on a NEW route
/// (<see cref="ServerAlignEndpoints"/>, <c>/api/v2/typebeat/server-align</c>) with a new
/// contract (queue position, daily cap, vocal mode, language). This path stays a 410 on purpose:
/// every build shipped before backlog 287 still POSTs here as its import fallback, with the old
/// protocol, and must not start feeding the new queue.
///
/// Deliberately minimal: no bearer requirement, no form read, and no raised body cap. There is
/// nothing to authorise and nothing to parse, and draining a 64 MB audio upload just to answer
/// 410 would be the one expensive thing this endpoint could still do. The cost is that a client
/// sending a body above Kestrel's default per-request cap may see a 413 or a connection reset
/// before the 410 reaches it. That is acceptable: either way the upload fails, and the client
/// falls back to its own error path.
///
/// GET and DELETE are not mapped. Without a job id from a successful POST no client can ever
/// reach them, so they would be a tombstone nobody visits.
/// </summary>
public static class AlignEndpoints
{
    /// <summary>
    /// What an old installed client is told: newer builds of the game offer the server aligner as
    /// an opt-in on the import screen, and the two ways that need no server (the local aligner from
    /// Settings, timestamped lyrics) still work in any build.
    /// </summary>
    public const string RETIRED_MESSAGE =
        "this version of the game uses a retired server aligner: update the game, whose import screen offers the server aligner as an option, or install the local auto-aligner from Settings, or add [mm:ss.xx] line timestamps to your lyrics";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/typebeat/align", () => WireJson.Error(StatusCodes.Status410Gone, RETIRED_MESSAGE))
           .DisableAntiforgery();
    }
}
