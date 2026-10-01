using Typebeat.Web.Wire;

namespace Typebeat.Web.Auth;

/// <summary>
/// Bearer authentication for wire endpoints. Usage:
/// <c>group.MapGet(...).RequireBearer()</c> then <c>ctx.AuthedUser()</c> inside the handler.
/// </summary>
public static class AuthExtensions
{
    private const string item_key = "typebeat.authed_user";

    public static TBuilder RequireBearer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async invocationContext =>
            {
                var ctx = invocationContext.HttpContext;
                var user = await ctx.ResolveBearerAsync();

                if (user == null)
                    return (object?)WireJson.Error(StatusCodes.Status401Unauthorized, "authentication failed");

                ctx.Items[item_key] = user;
                return await next(invocationContext);
            });
        });

        return builder;
    }

    /// <summary>The user resolved by <see cref="RequireBearer{TBuilder}"/>. Throws if used on an unauthenticated route.</summary>
    public static AuthedUser AuthedUser(this HttpContext ctx)
        => (AuthedUser?)ctx.Items[item_key] ?? throw new InvalidOperationException("Endpoint is missing RequireBearer().");

    /// <summary>
    /// The client IP every rate limit keys on. <c>Connection.RemoteIpAddress</c> wins whenever it
    /// is set: behind the proxy (TYPEBEAT_BEHIND_PROXY) UseForwardedHeaders has already rewritten
    /// it from the one X-Forwarded-For value Caddy sends, and Caddy only takes that from
    /// CF-Connecting-IP when the peer is a Cloudflare edge (deploy/Caddyfile, trusted_proxies),
    /// so a direct-to-origin caller cannot pick its own bucket. A raw CF-Connecting-IP is read
    /// only when there is no connection address at all (an in-process test host), and "unknown"
    /// when neither exists; the anonymous read cap exempts that last case (see RateLimits).
    /// </summary>
    public static string GetClientIp(this HttpContext ctx)
    {
        if (ctx.Connection.RemoteIpAddress is { } remote)
            return remote.ToString();

        string cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        return string.IsNullOrEmpty(cf) ? UnknownClientIp : cf;
    }

    /// <summary>What <see cref="GetClientIp"/> answers when the request carries no address at all.</summary>
    public const string UnknownClientIp = "unknown";

    /// <summary>Resolves the Authorization header to a user without enforcing it (for optional-auth routes).</summary>
    public static async Task<AuthedUser?> ResolveBearerAsync(this HttpContext ctx)
    {
        string? header = ctx.Request.Headers.Authorization.FirstOrDefault();

        if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return null;

        string token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
            return null;

        var tokens = ctx.RequestServices.GetRequiredService<TokenService>();
        var user = await tokens.ResolveAsync(token);

        // Restricted users can authenticate (so the client behaves) but never interact.
        if (user is null || user.Restricted)
            return null;

        // The game client's requests come through the same proxy, so a player who only ever signs
        // in from the game gets their country too (once; see CountryBackfill).
        return await CountryBackfill.ApplyAsync(ctx, user);
    }
}
