using Typebeat.Web.Wire;

namespace Typebeat.Web.Auth;

/// <summary>
/// Bearer authentication for wire endpoints. Usage:
/// <c>group.MapGet(...).RequireBearer()</c> then <c>ctx.AuthedUser()</c> inside the handler.
/// </summary>
public static class AuthExtensions
{
    private const string item_key = "typebeat.authed_user";

    /// <summary>
    /// Where <see cref="ResolveBearerAsync"/> keeps its answer for the rest of the request: the
    /// user, or null when the header resolved to nobody. Absent until the first resolution.
    /// </summary>
    public const string ResolvedBearerKey = "typebeat.bearer_user";

    public static TBuilder RequireBearer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.Metadata.Add(BearerRequiredMetadata.Instance);
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

    /// <summary>
    /// Marks an endpoint as carrying <see cref="RequireBearer{TBuilder}"/>, so the route table can be
    /// audited (backlog 406: every write stays signed-in, and a test enumerates them off this).
    /// </summary>
    public sealed class BearerRequiredMetadata
    {
        public static readonly BearerRequiredMetadata Instance = new();

        private BearerRequiredMetadata()
        {
        }
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

    /// <summary>
    /// Resolves the bearer once, BEFORE the rate limiter and the output cache (backlog 366), so
    /// both can tell a real bearer from any other Authorization header: only a resolved user is
    /// exempt from the anonymous read cap, keyed by token and refused by the cache. A request with
    /// a session user skips it (the session already decides all three), and a request with no
    /// Authorization header pays nothing. The answer is kept in <see cref="ResolvedBearerKey"/>,
    /// so the handler's own <see cref="ResolveBearerAsync"/> or <see cref="RequireBearer{TBuilder}"/>
    /// reuses it: still one token query per request.
    /// </summary>
    public static IApplicationBuilder UseBearerResolution(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.SessionUser() is null && ctx.Request.Headers.ContainsKey("Authorization"))
            {
                try
                {
                    await ctx.ResolveBearerAsync();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Resolution failing (the database unreachable) must not take down routes that never
                    // needed the bearer. Nothing is cached, so the request is limited and cached as a
                    // guest, and a route that does need the bearer resolves it again and fails as before.
                }
            }

            await next(ctx);
        });

    /// <summary>
    /// The bearer user this request has already resolved to, or null (no bearer, one that resolved
    /// to nobody, or not resolved yet). Never queries; see <see cref="UseBearerResolution"/>.
    /// </summary>
    public static AuthedUser? ResolvedBearer(this HttpContext ctx)
        => ctx.Items[ResolvedBearerKey] as AuthedUser;

    /// <summary>
    /// Resolves the Authorization header to a user without enforcing it (for optional-auth routes).
    /// The first call per request does the work and the rest reuse its answer.
    /// </summary>
    public static async Task<AuthedUser?> ResolveBearerAsync(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(ResolvedBearerKey, out object? resolved))
            return resolved as AuthedUser;

        var user = await resolveBearerUncachedAsync(ctx);
        ctx.Items[ResolvedBearerKey] = user;
        return user;
    }

    private static async Task<AuthedUser?> resolveBearerUncachedAsync(HttpContext ctx)
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
