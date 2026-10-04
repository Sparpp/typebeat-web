using Typebeat.Web.Auth;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Quiet-client stubs: the endpoints the client polls on a timer or fetches once at startup and
/// whose empty/steady responses keep it from logging errors or retrying. Every shape here is
/// mirrored from the matching client response DTO (file references inline). Also serves the
/// static default avatar referenced by UserWire.AvatarUrl.
/// </summary>
public static class StubEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /api/v2/notifications -> APINotificationsBundle (Responses/APINotificationsBundle.cs).
        // notification_endpoint is the websocket URL the client then connects to (ws over http,
        // wss over https), served back from this same host.
        app.MapGet("/api/v2/notifications", (HttpContext ctx) =>
        {
            string wsScheme = ctx.Request.Scheme == "https" ? "wss" : "ws";
            string host = ctx.Request.Host.Value ?? string.Empty;
            return WireJson.Ok(new
            {
                has_more = false,
                notifications = Array.Empty<object>(),
                notification_endpoint = $"{wsScheme}://{host}/ws/notifications",
            });
        }).RequireBearer();

        // GET /api/v2/seasonal-backgrounds -> APISeasonalBackgrounds (ends_at + backgrounds).
        // A far-future ends_at means the client never re-fetches expecting a new set.
        app.MapGet("/api/v2/seasonal-backgrounds", () => WireJson.Ok(new
        {
            ends_at = "2099-01-01T00:00:00Z",
            backgrounds = Array.Empty<object>(),
        })).RequireBearer();

        // POST /api/v2/chat/ack -> ChatAckResponse (Responses/ChatAckResponse.cs): { "silences": [] }.
        app.MapPost("/api/v2/chat/ack", () => WireJson.Ok(new
        {
            silences = Array.Empty<object>(),
        })).RequireBearer();

        // GET /api/v2/chat/updates -> GetUpdatesResponse (Requests/GetUpdatesResponse.cs). The DTO
        // consumes "presence" and "messages" (NOT "silences"); both empty keeps chat idle.
        app.MapGet("/api/v2/chat/updates", () => WireJson.Ok(new
        {
            presence = Array.Empty<object>(),
            messages = Array.Empty<object>(),
        })).RequireBearer();

        // GET /api/v2/chat/channels -> ListChannelsRequest expects a bare List<Channel>: [].
        app.MapGet("/api/v2/chat/channels", () => WireJson.Ok(Array.Empty<object>())).RequireBearer();

        // GET /api/v2/friends moved to FriendEndpoints: it now reads the player follows for real.

        // GET /api/v2/blocks -> GetBlocksRequest expects a bare List<APIRelation>: []. Fetched
        // unconditionally at login (LocalUserState); a 404 logs a failing request every session.
        app.MapGet("/api/v2/blocks", () => WireJson.Ok(Array.Empty<object>())).RequireBearer();

        // GET /api/v2/me/beatmapset-favourites moved to BeatmapsetEndpoints (M3): it now reads
        // the favourites table for real instead of returning the empty stub.

        // Static default avatar referenced by UserWire.AvatarUrl. Unauthenticated: the client's
        // texture loader fetches images without a bearer token. A 64x64 solid PNG generated at
        // build-out time and embedded as base64 (avoids shipping a binary asset through text tools).
        app.MapGet("/img/default-avatar.png", () =>
            Results.Bytes(Convert.FromBase64String(default_avatar_png_base64), "image/png"));
    }

    // 64x64 solid PNG in the design system's violet #7a3ff2 (valid IHDR/IDAT/IEND,
    // zlib-compressed). Deterministically generated. The original build-out used osu's
    // signature pink, which is off-limits trade dress anywhere user-visible.
    private const string default_avatar_png_base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAe0lEQVR4nO3PUQkAIBTAQJu9XsY2gSH8OITBAtzWnvN1" +
        "iwsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa" +
        "0IIGtKABLWhACxrQgga0oIHxiJeBC2uMsYdARYnQAAAAAElFTkSuQmCC";
}
