using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Auth;

/// <summary>
/// Who a rate-limit budget belongs to. A signed-in website visitor is their user id; a bearer
/// caller is a hash of the token (bearer auth is an endpoint filter that runs AFTER the limiter,
/// so the user is not known yet, and a token is one game install); anyone else is their client
/// IP (<see cref="AuthExtensions.GetClientIp"/>, which the proxy makes unforgeable).
/// </summary>
public static class RateLimitKeys
{
    public static string For(HttpContext ctx)
    {
        if (ctx.SessionUser() is { } user)
            return "u:" + user.Id.ToString(CultureInfo.InvariantCulture);

        string? header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
            return "t:" + Convert.ToHexString(digest)[..16].ToLowerInvariant();
        }

        return "ip:" + ctx.GetClientIp();
    }
}

/// <summary>
/// The ASP.NET rate limiter (backlog 366, docs/drafts/0366-plan.md R5). Sliding windows with no
/// queue, so an over-budget request is answered at once rather than parked.
///
/// <para>Three rules from the game client shape every number and every answer here:</para>
/// <list type="bullet">
/// <item>A rejection is ALWAYS 429, never 401 or 403: the game logs out on those two
/// (APIAccess) and leaves any other status alone.</item>
/// <item>A wire 429 carries <c>{"error": "..."}</c> (<see cref="WireJson.Error"/>): the game's
/// APIRequest turns exactly that into the message the player sees. A website GET gets an empty
/// body instead, so the styled /error/429 page renders.</item>
/// <item>A 429 on score submit loses the score in both clients (neither retries), so the token
/// and submit budgets are speed bumps no honest player reaches (a play takes minutes), never
/// lower than 30 a minute. /oauth/token has NO policy: its grant type is in the body, and a 429
/// on a refresh grant logs the game out, so only the password grant is limited, in its handler.
/// The website auth forms keep their own in-handler limiters, which their tests pin.</item>
/// </list>
/// </summary>
public static class RateLimits
{
    public const string ScoreToken = "score-token";
    public const string ScoreSubmit = "score-submit";
    public const string ReplayUpload = "replay-upload";
    public const string Leaderboard = "leaderboard";
    public const string Lookup = "lookup";
    public const string Report = "report";

    public const int ScoreTokenPerMinute = 30;
    public const int ScoreSubmitPerMinute = 30;
    public const int ReplayUploadPerMinute = 30;
    public const int LeaderboardPerMinute = 120;
    public const int LookupBurst = 600;
    public const int LookupRefillPerSecond = 10;
    public const int ReportsPerWindow = 5;
    public static readonly TimeSpan ReportWindow = TimeSpan.FromMinutes(10);
    public const int AnonymousReadsPerMinute = 300;

    /// <summary>The message a limited wire caller (and so the player) reads.</summary>
    public const string RejectionMessage = "Too many requests. Please wait a minute and try again.";

    /// <summary>
    /// Paths the anonymous read cap never counts: the immutable media the CDN serves anyway, the
    /// uptime probe, and the error page a website 429 re-executes into (counting that would turn
    /// the styled page into a second, empty 429).
    /// </summary>
    private static readonly string[] anonymous_read_exempt =
        ["/covers", "/previews", "/avatars", "/user-covers", "/img", "/releases", "/health", "/error"];

    public static IServiceCollection AddTypebeatRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = onRejectedAsync;

            o.AddPolicy(ScoreToken, ctx => perMinute(ctx, ScoreTokenPerMinute));
            o.AddPolicy(ScoreSubmit, ctx => perMinute(ctx, ScoreSubmitPerMinute));
            o.AddPolicy(ReplayUpload, ctx => perMinute(ctx, ReplayUploadPerMinute));
            o.AddPolicy(Leaderboard, ctx => perMinute(ctx, LeaderboardPerMinute));

            // Import of a beatmap pack fires one lookup per difficulty, hundreds at once: a deep
            // bucket that refills fast, rather than a window that would cut the burst off.
            o.AddPolicy(Lookup, ctx => RateLimitPartition.GetTokenBucketLimiter(RateLimitKeys.For(ctx), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = LookupBurst,
                TokensPerPeriod = LookupRefillPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

            // On the set PAGE, whose handlers share one endpoint: only the Report POST is counted.
            // Anonymous reports stay allowed (owner decision), so this is their only brake.
            o.AddPolicy(Report, ctx => isReportPost(ctx)
                ? RateLimitPartition.GetSlidingWindowLimiter(RateLimitKeys.For(ctx), _ => sliding(ReportsPerWindow, ReportWindow))
                : RateLimitPartition.GetNoLimiter(string.Empty));

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(anonymousReadPartition);
        });

        return services;
    }

    /// <summary>
    /// The global cap on unauthenticated reads: 300 a minute per client IP for a GET/HEAD with no
    /// session user and no Authorization header. Requests a cached response answers still count,
    /// which is why it is generous. A request with no address at all ("unknown", which only an
    /// in-process test host produces) is not capped, so a whole test suite never shares one bucket.
    /// </summary>
    private static RateLimitPartition<string> anonymousReadPartition(HttpContext ctx)
    {
        var request = ctx.Request;

        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || ctx.SessionUser() is not null
            || request.Headers.ContainsKey("Authorization"))
            return RateLimitPartition.GetNoLimiter(string.Empty);

        foreach (string prefix in anonymous_read_exempt)
        {
            if (request.Path.StartsWithSegments(prefix))
                return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        string ip = ctx.GetClientIp();
        if (ip == AuthExtensions.UnknownClientIp)
            return RateLimitPartition.GetNoLimiter(string.Empty);

        return RateLimitPartition.GetSlidingWindowLimiter("ip:" + ip, _ => sliding(AnonymousReadsPerMinute, TimeSpan.FromMinutes(1)));
    }

    private static RateLimitPartition<string> perMinute(HttpContext ctx, int permits)
        => RateLimitPartition.GetSlidingWindowLimiter(RateLimitKeys.For(ctx), _ => sliding(permits, TimeSpan.FromMinutes(1)));

    private static SlidingWindowRateLimiterOptions sliding(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        SegmentsPerWindow = 6,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static bool isReportPost(HttpContext ctx)
        => HttpMethods.IsPost(ctx.Request.Method)
           && string.Equals(ctx.Request.Query["handler"].ToString(), "Report", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask onRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;

        int retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait)
            ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : 60;

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        // The OAuth error shape under /oauth (the game reads its "hint"), in case a policy is ever
        // put there; none is today (see the class remarks on the refresh grant).
        if (http.Request.Path.StartsWithSegments("/oauth"))
        {
            await WireJson.Ok(new { error = "invalid_request", hint = RejectionMessage, message = RejectionMessage },
                StatusCodes.Status429TooManyRequests).ExecuteAsync(http);
            return;
        }

        // A website GET (the styled status-code pages are active on exactly those requests) keeps
        // an empty body, so the visitor gets /error/429 rather than raw JSON.
        if (http.Features.Get<IStatusCodePagesFeature>() is { Enabled: true })
            return;

        await WireJson.Error(StatusCodes.Status429TooManyRequests, RejectionMessage).ExecuteAsync(http);
    }
}
