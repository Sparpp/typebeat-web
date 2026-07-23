using System.Net.WebSockets;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The /ws/notifications websocket the client connects to after reading notification_endpoint
/// from GET /api/v2/notifications (WebSocketNotificationsClient). In M1 there are no server-pushed
/// events: we accept the socket, authenticate it, and idle, reading and discarding anything the
/// client sends (e.g. a chat.start frame), so it stays quietly connected without reconnect churn.
///
/// Crucially we never initiate a close: the client's read loop treats a server Close frame as an
/// error and reconnects with backoff. We only complete the handshake once the client closes.
/// </summary>
public static class NotificationsSocket
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/notifications", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // The client authenticates the upgrade with an "Authorization: Bearer {token}" header
            // (WebSocketNotificationsClientConnector). Reject the handshake if it doesn't resolve.
            var user = await ctx.ResolveBearerAsync();
            if (user == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await idleUntilClosed(socket, ctx.RequestAborted);
        });
    }

    /// <summary>Reads and discards frames until the client closes or the request is aborted.</summary>
    private static async Task idleUntilClosed(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }

                // Ignore all inbound frames. No server-pushed notifications exist in M1.
                // TODO: this socket is the future force-logout channel: pushing a text frame
                // {"event":"logout"} here signs the user out client-side (APIAccess handles the
                // "logout" SocketMessage event by calling Logout()).
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted / server shutting down, nothing to do.
        }
        catch (WebSocketException)
        {
            // Abrupt client disconnect (no clean close frame). The socket is disposed by `using`.
        }
    }
}
