using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Caching;

/// <summary>
/// The only output-cache policy the app uses (backlog 366): a response is stored and served from
/// the cache ONLY for an anonymous GET/HEAD, and only when it is a plain 200 that sets no cookie.
///
/// <para>"Anonymous" has to be decided here, by hand. The app never sets <c>HttpContext.User</c>
/// (sessions live in <c>HttpContext.Items</c>, see <see cref="SessionCookieAuth"/>), so the
/// framework's default IsAuthenticated check is blind and would happily serve a cached signed-out
/// page to a signed-in visitor, or the reverse. A request is refused outright when it has a
/// resolved session user, an Authorization header, or ANY of the four auth cookies, even one that
/// no longer resolves (a stale cookie costs that one visitor a cache miss, nothing else).</para>
///
/// <para><see cref="CacheVaryByRules.VaryByHost"/> is always on: every wire DTO and most pages
/// build absolute URLs from <c>Request.Host</c>, and the app answers on several names
/// (typebeat.sh, typebeat.mingda.sh, the bss.* pair), so one host's body must never be served on
/// another. Query keys are explicit per route: a key not listed does not reach the cache key, so
/// a route listing too few would serve one query's page for another, and every listed key here
/// is checked against the page's own bindings.</para>
///
/// <para>Every entry carries the catch-all tag <see cref="CacheTags.Site"/>, which is what
/// <see cref="CacheEviction.EvictAllAsync"/> (tests, and anything that must flush everything)
/// evicts.</para>
/// </summary>
public sealed class AnonymousOnlyPolicy : IOutputCachePolicy
{
    /// <summary>Cookies whose mere presence makes a request personal (session, login challenge, Google flow and pending sign-up).</summary>
    public static readonly string[] PersonalCookies =
    [
        SessionCookieAuth.CookieName,
        ChallengeCookie.CookieName,
        GoogleCookies.FlowCookieName,
        GoogleCookies.PendingCookieName,
    ];

    private readonly TimeSpan ttl;
    private readonly StringValues queryKeys;
    private readonly string[] tags;
    private readonly string? routeTagKey;
    private readonly string? routeTagPrefix;
    private readonly string? cacheControl;

    /// <param name="ttl">How long the server keeps the entry.</param>
    /// <param name="queryKeys">The query keys the response varies by; anything else is ignored.</param>
    /// <param name="tags">Fixed eviction tags (beside <see cref="CacheTags.Site"/>).</param>
    /// <param name="cacheControl">The Cache-Control a STORED anonymous 200 is sent with (for browsers and the Cloudflare edge), or null to leave the header alone.</param>
    /// <param name="routeTag">An optional (route value, tag prefix) pair: the entry is also tagged <c>{prefix}{route value}</c>, e.g. ("setId", "set:").</param>
    public AnonymousOnlyPolicy(TimeSpan ttl, string[] queryKeys, string[] tags, string? cacheControl, (string Key, string Prefix)? routeTag = null)
    {
        this.ttl = ttl;
        this.queryKeys = new StringValues(queryKeys);
        this.tags = tags;
        this.cacheControl = cacheControl;
        routeTagKey = routeTag?.Key;
        routeTagPrefix = routeTag?.Prefix;
    }

    /// <summary>True when nothing about the request could make its response personal.</summary>
    public static bool IsAnonymous(HttpContext ctx)
    {
        if (ctx.SessionUser() is not null)
            return false;

        if (ctx.Request.Headers.ContainsKey("Authorization"))
            return false;

        foreach (string cookie in PersonalCookies)
        {
            if (ctx.Request.Cookies.ContainsKey(cookie))
                return false;
        }

        return true;
    }

    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        bool readMethod = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        bool eligible = readMethod && IsAnonymous(http);

        context.EnableOutputCaching = eligible;
        context.AllowCacheLookup = eligible;
        context.AllowCacheStorage = eligible;
        context.AllowLocking = true;

        if (!eligible)
            return ValueTask.CompletedTask;

        context.ResponseExpirationTimeSpan = ttl;
        context.CacheVaryByRules.VaryByHost = true;
        context.CacheVaryByRules.QueryKeys = queryKeys;

        context.Tags.Add(CacheTags.Site);
        foreach (string tag in tags)
            context.Tags.Add(tag);

        if (routeTagKey is not null && http.Request.RouteValues.TryGetValue(routeTagKey, out object? value) && value is not null)
            context.Tags.Add(routeTagPrefix + value);

        // The edge and browser header. Set as the response STARTS: the middleware only asks
        // ServeResponseAsync once the body has been written, when headers are read-only. The same
        // storability test as ServeResponseAsync, minus what only the body can decide, plus a
        // handler's own refusal (CachePolicies.DoNotStore).
        if (cacheControl is not null)
        {
            http.Response.OnStarting(() =>
            {
                if (context.AllowCacheStorage && storable(http.Response) && string.IsNullOrEmpty(http.Response.Headers.CacheControl))
                    http.Response.Headers.CacheControl = cacheControl;
                return Task.CompletedTask;
            });
        }

        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        // Runs after the body is written, as the store-or-not decision (headers are read-only by now).
        if (!storable(context.HttpContext.Response))
            context.AllowCacheStorage = false;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Only a plain success is shared: a 404 for one set, a redirect or an error page must not
    /// outlive the moment it described, and a response that sets a cookie belongs to whoever
    /// received that cookie.
    /// </summary>
    private static bool storable(HttpResponse response)
        => response.StatusCode == StatusCodes.Status200OK && response.Headers.SetCookie.Count == 0;
}

/// <summary>Output-cache tag names. One place, so a writer and its evictor cannot spell a tag two ways.</summary>
public static class CacheTags
{
    /// <summary>On every entry; evicting it flushes the whole output cache.</summary>
    public const string Site = "site";

    public const string Listing = "listing";
    public const string Landing = "landing";
    public const string Rankings = "rankings";

    public static string Set(long setId) => $"set:{setId}";
    public static string User(long userId) => $"user:{userId}";
}
