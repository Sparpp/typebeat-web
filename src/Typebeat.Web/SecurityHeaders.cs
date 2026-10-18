namespace Typebeat.Web;

/// <summary>
/// The baseline security headers on every response the app writes, on every host (typebeat.sh,
/// the old typebeat.mingda.sh, and the grey-cloud bss.* names that no Cloudflare edge covers):
/// <c>nosniff</c>, a Referrer-Policy, HSTS when the request came in over HTTPS, and framing
/// protection everywhere except the /play page, which the itch.io embed loads in an iframe.
/// deploy/Caddyfile sets the same nosniff, Referrer-Policy and HSTS values as defaults for the
/// responses Caddy writes itself (maintenance and restarting pages, redirects).
///
/// <para>Set in <c>OnStarting</c> so they survive the website error pages' re-execution (which
/// clears the response headers), and decided on the ORIGINAL path, so a /play error stays
/// frameable and every other error page does not. No CSP beyond <c>frame-ancestors</c> yet: the
/// inline scripts on /play, Settings and the layout would need nonces first.</para>
/// </summary>
public static class SecurityHeaders
{
    /// <summary>One year, the value deploy/Caddyfile has always sent. No includeSubDomains (nor
    /// preload): that would lock browsers out of any plain-HTTP subdomain.</summary>
    public const string Hsts = "max-age=31536000";

    public const string ReferrerPolicy = "strict-origin-when-cross-origin";

    public const string FrameAncestorsNone = "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        bool development = app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

        return app.Use((ctx, next) =>
        {
            bool frameable = IsFrameable(ctx.Request.Path);

            ctx.Response.OnStarting(() =>
            {
                Apply(ctx, frameable, development);
                return Task.CompletedTask;
            });

            return next(ctx);
        });
    }

    /// <summary>
    /// Only the /play page may be framed: the itch.io embed loads it, and it frames nothing
    /// itself. Its fetches (/play/map/*, /play/token, /play/submit) and scripts are not documents,
    /// so they need no exemption, and every page it links to stays unframeable. Antiforgery's own
    /// SAMEORIGIN, which /play would otherwise carry for its token, is suppressed in Program.cs.
    /// </summary>
    public static bool IsFrameable(PathString path)
        => path.Value is { } value && string.Equals(value.TrimEnd('/'), "/play", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the headers. HSTS only over HTTPS (behind Caddy that is the forwarded
    /// scheme) and never in Development, where it would pin localhost to HTTPS in the browser.</summary>
    public static void Apply(HttpContext ctx, bool frameable, bool development)
    {
        var headers = ctx.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = ReferrerPolicy;

        if (!frameable)
        {
            headers.ContentSecurityPolicy = FrameAncestorsNone;
            headers.XFrameOptions = "DENY";
        }

        if (ctx.Request.IsHttps && !development)
            headers.StrictTransportSecurity = Hsts;
    }
}
