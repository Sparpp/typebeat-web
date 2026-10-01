using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Typebeat.Web;

/// <summary>
/// Search and share metadata the layout and the robots/sitemap routes render (backlog 369):
/// the canonical URL of a request, which pages ask not to be indexed, the default share image,
/// the robots.txt body and the JSON-LD blocks. Pure statics, so they are testable without a host.
/// </summary>
public static class Seo
{
    /// <summary>
    /// The one host every canonical URL names. Hard-coded rather than read off the request, so a
    /// page reached through an old or alias host (typebeat.mingda.sh, typebeat.gg, www) still
    /// points search engines at typebeat.sh.
    /// </summary>
    public const string CANONICAL_ORIGIN = "https://typebeat.sh";

    /// <summary>The share image for pages that provide none of their own (1200x630).</summary>
    public const string DEFAULT_OG_IMAGE = CANONICAL_ORIGIN + "/media/og-default.png";

    /// <summary>The landing page's full tab title (the layout adds no suffix to it).</summary>
    public const string LANDING_TITLE = "type!beat: the lyric typing rhythm game";

    /// <summary>
    /// Path prefixes (whole segments) whose pages carry <c>noindex</c>: sign-in and account
    /// flows, per-user pages, error pages, and /play (a per-request antiforgery token and no
    /// indexable content). /auth covers the Google sign-in steps.
    /// </summary>
    private static readonly string[] noindex_prefixes =
    [
        "/login", "/register", "/forgot-password", "/reset-password", "/verify", "/settings",
        "/logout", "/watching", "/error", "/play", "/auth",
    ];

    /// <summary>
    /// The query keys that make a genuinely different page, per path; every other key (and every
    /// key on any other path) is dropped from the canonical. The listing keeps its search, sort
    /// and status, plus its paging cursor so each "show more" page is its own canonical rather
    /// than a duplicate of page one; the per-user <c>unplayed</c> filter is dropped. The rankings
    /// keep their board and page.
    /// </summary>
    private static readonly Dictionary<string, string[]> kept_query_keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/beatmapsets"] = ["q", "s", "status", "after", "after_id", "after_tier"],
        ["/rankings"] = ["board", "page"],
    };

    /// <summary>Default values that name the same page as their absence.</summary>
    private static readonly HashSet<(string Key, string Value)> default_values =
    [
        ("s", "newest"),
        ("status", "any"),
        ("page", "1"),
        ("board", "performance"),
    ];

    /// <summary>Paging keys, kept only when they hold an integer (junk never reaches a canonical).</summary>
    private static readonly HashSet<string> numeric_keys = ["after", "after_id", "after_tier", "page"];

    public static bool IsNoIndex(PathString path)
        => noindex_prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The absolute canonical URL for <paramref name="path"/>: the canonical origin, the path in
    /// lower case without a trailing slash (the landing is <c>/</c>), and only the query keys
    /// <see cref="kept_query_keys"/> allows for that path, in a fixed order.
    /// </summary>
    public static string CanonicalUrl(PathString path, IQueryCollection? query = null)
    {
        string value = path.HasValue ? path.Value!.ToLowerInvariant() : "/";
        if (value.Length > 1)
            value = value.TrimEnd('/');
        if (value.Length == 0)
            value = "/";

        var url = new StringBuilder(CANONICAL_ORIGIN).Append(new PathString(value).ToUriComponent());

        if (query is not null && kept_query_keys.TryGetValue(value, out var keys))
        {
            char separator = '?';
            foreach (string key in keys)
            {
                string v = query[key].ToString().Trim();
                if (v.Length == 0 || default_values.Contains((key, v.ToLowerInvariant())))
                    continue;
                if (numeric_keys.Contains(key) && !long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    continue;

                url.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(v));
                separator = '&';
            }
        }

        return url.ToString();
    }

    /// <summary>
    /// The app-served robots.txt. Cloudflare prepends its managed content-signals block on the
    /// proxied host; these lines must survive below it (an owner check after each deploy).
    /// </summary>
    public const string ROBOTS_TXT =
        "User-agent: *\n"
        + "Disallow: /api/\n"
        + "Disallow: /bss/\n"
        + "Disallow: /oauth/\n"
        + "Disallow: /play/token\n"
        + "Disallow: /play/submit\n"
        + "Disallow: /settings\n"
        + "Disallow: /watching\n"
        + "Disallow: /logout\n"
        + "Disallow: /verify\n"
        + "Disallow: /error/\n"
        + "Allow: /\n"
        + "\n"
        + "Sitemap: " + CANONICAL_ORIGIN + "/sitemap.xml\n";

    /// <summary>
    /// The schema.org VideoGame block for the landing and download pages. Serialised with the
    /// default encoder, which escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>, so the result is
    /// safe to emit raw inside a script element.
    /// </summary>
    public static string VideoGameJsonLd(string url, string description)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "VideoGame",
            ["name"] = "type!beat",
            ["alternateName"] = "typebeat",
            ["url"] = url,
            ["description"] = description,
            ["image"] = DEFAULT_OG_IMAGE,
            ["genre"] = new[] { "Rhythm", "Typing" },
            ["gamePlatform"] = new[] { "Windows", "macOS", "Linux", "Web browser" },
            ["applicationCategory"] = "Game",
            ["operatingSystem"] = "Windows, macOS, Linux",
            ["offers"] = new Dictionary<string, object>
            {
                ["@type"] = "Offer",
                ["price"] = "0",
                ["priceCurrency"] = "USD",
            },
            ["sameAs"] = new[] { SiteLinks.SOURCE_REPOSITORY, SiteLinks.DISCORD_INVITE },
        });

    /// <summary>The schema.org MusicRecording block for a beatmap set page.</summary>
    public static string MusicRecordingJsonLd(string url, string title, string artist, string? image)
    {
        var block = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "MusicRecording",
            ["name"] = title,
            ["url"] = url,
            ["byArtist"] = new Dictionary<string, object>
            {
                ["@type"] = "MusicGroup",
                ["name"] = artist,
            },
        };

        if (image is not null)
            block["image"] = image;

        return JsonSerializer.Serialize(block);
    }
}
