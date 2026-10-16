using Microsoft.AspNetCore.Http;

namespace Typebeat.Web.Caching;

/// <summary>
/// Browser caching for the wwwroot files UseStaticFiles serves (backlog 417), applied from its
/// <c>OnPrepareResponse</c>. A static, so it is testable without a host.
///
/// <para>Two rules, and everything else is left exactly as it was (no Cache-Control at all), so
/// replacing a demo video, a share image or the favicon in place needs no edge purge:</para>
/// <list type="bullet">
/// <item>a request carrying a <c>v</c> query is the <c>asp-append-version</c> form, whose URL
/// changes with the file's content, so it may be kept for a year and never revalidated;</item>
/// <item><c>/fonts/</c> and <c>/flags/</c> hold files that are never changed in place, only added,
/// so a day.</item>
/// </list>
/// </summary>
public static class StaticAssetCaching
{
    public const string VERSIONED = "public, max-age=31536000, immutable";

    public const string STABLE = "public, max-age=86400";

    private static readonly string[] stable_prefixes = ["/fonts", "/flags"];

    public static void Apply(HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.Request.Query["v"]))
            ctx.Response.Headers.CacheControl = VERSIONED;
        else if (stable_prefixes.Any(prefix => ctx.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            ctx.Response.Headers.CacheControl = STABLE;
    }
}
