using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Tombstone for the retired server-side lyric aligner (backlog 287).
///
/// The route used to accept a multipart audio + lyrics upload, hand it to a torch/demucs worker
/// container over a shared /data job directory, and serve the resulting timing.json back through
/// GET/DELETE by job id. All of that is gone: alignment is local now, and the game ships an
/// installer for the local auto-aligner instead.
///
/// Why a 410 and not simply nothing: the only consumer was the desktop client's import fallback
/// (RemoteAlignClient), so every build already installed in the wild will keep POSTing here for
/// months. One honest tombstone tells those clients WHY and where to go, which a bare 404 (the
/// catch-all a removed route would give them) cannot. Keeping the route also means a future
/// re-add is a deliberate decision made here rather than an accident of routing.
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
    /// What an installed client is told. Names the local aligner (Settings, Experimental) and the
    /// zero-install alternative (timestamped lyrics), because the import flow that calls this has
    /// no other way to learn either.
    /// </summary>
    public const string RETIRED_MESSAGE =
        "server-side alignment has been retired: install the local auto-aligner from the game's Settings (Experimental section), or add [mm:ss.xx] line timestamps to your lyrics";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/typebeat/align", () => WireJson.Error(StatusCodes.Status410Gone, RETIRED_MESSAGE))
           .DisableAntiforgery();
    }
}
