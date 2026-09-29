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
    /// Best-effort client IP for rate limiting. Behind Cloudflare the connection/XFF chain ends
    /// at a CF edge IP (shared by many players), so prefer CF-Connecting-IP when present. A
    /// direct-to-origin caller can spoof that header, but the in-memory limiters are documented
    /// speed bumps; Cloudflare WAF rules are the real production layer.
    /// </summary>
    public static string GetClientIp(this HttpContext ctx)
    {
        string cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrEmpty(cf))
            return cf;

        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

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
