using Microsoft.AspNetCore.OutputCaching;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Caching;

/// <summary>
/// The output-cache registrations (backlog 366, docs/drafts/0366-plan.md R1 to R3). Named
/// policies only: no base policy is added, so a route is cached only when it opts in with
/// <c>[OutputCache(PolicyName = ...)]</c> or <c>.CacheOutput(...)</c>, and every opted-in route
/// goes through <see cref="AnonymousOnlyPolicy"/>.
///
/// <para>Deliberately NOT cached: the set page (/beatmapsets/{id}, its anonymous report form
/// carries an antiforgery token, owner decision 2026-10-01), /play (always embeds a token),
/// /api/v2/me and the bearer leaderboard (personal; the board memoises only its shared slice, see
/// <see cref="CacheEviction"/>), every write, replay and download routes (they count views or
/// downloads), /ws, /health, /bss, /oauth, /api/v2/ops and the buddy feed.</para>
/// </summary>
public static class CachePolicies
{
    public const string Static1h = "static-1h";
    public const string Landing = "landing";
    public const string Listing = "listing";
    public const string Rankings = "rankings";
    public const string Profile = "profile";
    public const string SetApi = "set-api";
    public const string PlayMap = "play-map";

    /// <summary>Registers the output cache (64 MB) and every named policy above.</summary>
    public static IServiceCollection AddTypebeatOutputCache(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<CacheEviction>();

        services.AddOutputCache(o =>
        {
            o.SizeLimit = 64L * 1024 * 1024;

            o.AddPolicy(Static1h, new AnonymousOnlyPolicy(TimeSpan.FromHours(1), [], [], "public, max-age=3600"));

            o.AddPolicy(Landing, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(60), ["deleted"], [CacheTags.Landing],
                "public, max-age=0, s-maxage=60"));

            // ListingModel.OnGetAsync binds q, s, status, after, after_id, after_tier (and
            // unplayed, which only a signed-in viewer can use, and they never reach the cache).
            o.AddPolicy(Listing, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(30),
                ["q", "s", "status", "after", "after_id", "after_tier"], [CacheTags.Listing],
                "public, max-age=0, s-maxage=30"));

            // Rankings/IndexModel.OnGetAsync binds board, page and q (the player search, backlog
            // 407). Leaving q out would serve every anonymous search the cached unsearched board.
            o.AddPolicy(Rankings, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(60), ["board", "page", "q"], [CacheTags.Rankings],
                "public, max-age=0, s-maxage=60"));

            // The user:{id} tag is added by ProfileModel itself once it has resolved the id.
            o.AddPolicy(Profile, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(30), ["pin"], [],
                "public, max-age=0, s-maxage=30"));

            o.AddPolicy(SetApi, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(30), [], [],
                "public, max-age=0, s-maxage=30", ("setId", "set:")));

            o.AddPolicy(PlayMap, new AnonymousOnlyPolicy(TimeSpan.FromSeconds(60), ["diff"], [],
                "public, max-age=60", ("setId", "set:")));
        });

        return services;
    }

    /// <summary>
    /// Adds <c>Cache-Control: private, no-store</c> to every response for a signed-in visitor or
    /// a bearer caller that has not chosen a Cache-Control of its own, so neither a browser cache
    /// nor the Cloudflare edge ever keeps a personal page. Responses that already set one (the
    /// media routes' public covers and avatars) keep it: those bodies are the same for everyone.
    /// </summary>
    public static IApplicationBuilder UsePrivateNoStore(this IApplicationBuilder app)
        => app.Use((ctx, next) =>
        {
            if (ctx.SessionUser() is not null || ctx.Request.Headers.ContainsKey("Authorization"))
            {
                ctx.Response.OnStarting(() =>
                {
                    if (string.IsNullOrEmpty(ctx.Response.Headers.CacheControl))
                        ctx.Response.Headers.CacheControl = "private, no-store";
                    return Task.CompletedTask;
                });
            }

            return next(ctx);
        });

    /// <summary>
    /// Marks this response as never storable, whatever policy the route carries, and tells every
    /// downstream cache the same. For a body that is not world-readable (an unpublished set's
    /// media, served to its owner only).
    /// </summary>
    public static void DoNotStore(HttpContext ctx)
    {
        if (ctx.Features.Get<IOutputCacheFeature>() is { } feature)
            feature.Context.AllowCacheStorage = false;

        ctx.Response.Headers.CacheControl = "private, no-store";
    }

    /// <summary>Adds an eviction tag to the entry this request may store (no-op when the route is not output-cached).</summary>
    public static void AddTag(HttpContext ctx, string tag)
    {
        if (ctx.Features.Get<IOutputCacheFeature>() is { } feature)
            feature.Context.Tags.Add(tag);
    }
}
