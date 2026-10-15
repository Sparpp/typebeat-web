using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Typebeat.Web.Packages;

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

    /// <summary>
    /// <see cref="DEFAULT_OG_IMAGE"/>'s pixel size, declared beside it by the layout (backlog 417)
    /// so a share card can be laid out before the image is fetched. Re-check both if the file is
    /// replaced.
    /// </summary>
    public const int DEFAULT_OG_IMAGE_WIDTH = 1200;

    public const int DEFAULT_OG_IMAGE_HEIGHT = 630;

    /// <summary>The landing page's full tab title (the layout adds no suffix to it).</summary>
    public const string LANDING_TITLE = "type!beat: the lyric typing rhythm game";

    /// <summary>
    /// Path prefixes (whole segments) whose pages carry <c>noindex</c>: sign-in and account
    /// flows, per-user pages and error pages. /auth covers the Google sign-in steps. /play is
    /// indexed since backlog 418, which gave the picker its own copy; its query never reaches
    /// the canonical.
    /// </summary>
    private static readonly string[] noindex_prefixes =
    [
        "/login", "/register", "/forgot-password", "/reset-password", "/verify", "/settings",
        "/logout", "/watching", "/error", "/auth",
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
    /// The app-served robots.txt. Served as-is: no edge block is prepended (checked 2026-10-15).
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
        // Backlog 417: binary and JSON routes with nothing to index. /download/game is a prefix,
        // so it covers game-linux and game-macos as well, but not the /download page itself.
        + "Disallow: /beatmapsets/*/download\n"
        + "Disallow: /download/game\n"
        + "Disallow: /play/map/\n"
        + "Allow: /\n"
        + "\n"
        + "Sitemap: " + CANONICAL_ORIGIN + "/sitemap.xml\n";

    /// <summary>
    /// The landing page's meta description, which is also the game's JSON-LD description on both
    /// pages that carry it (backlog 417); /download keeps its own meta description. Generic search
    /// phrases, once each (backlog 369): "lyric typing rhythm game", "type to the beat", "typing
    /// game with music". The visible line under the landing hero carries the first.
    /// </summary>
    public const string GAME_DESCRIPTION = "type!beat is a free lyric typing rhythm game: type to the beat as the words "
        + "drop with the song. A typing game with music, in your browser or on Windows, macOS and Linux, "
        + "with community maps and leaderboards.";

    /// <summary>
    /// The JSON-LD node ids (backlog 417). Absolute, built from the canonical origin: a bare
    /// fragment would resolve against the page it sits on, so /download would declare a second
    /// game and a second organisation rather than naming the landing's.
    /// </summary>
    public const string GAME_NODE_ID = CANONICAL_ORIGIN + "/#game";

    public const string ORGANIZATION_NODE_ID = CANONICAL_ORIGIN + "/#org";

    public const string SOURCE_NODE_ID = CANONICAL_ORIGIN + "/#source";

    public const string WEBSITE_NODE_ID = CANONICAL_ORIGIN + "/#website";

    /// <summary>
    /// A JSON-LD block ready to sit raw inside a script element. The only way to make one is
    /// <see cref="Of"/>, which serialises an object with the default encoder (<c>&lt;</c>,
    /// <c>&gt;</c> and <c>&amp;</c> escaped), so the layout, which renders only this type, never
    /// writes a caller's hand-built string as markup.
    /// </summary>
    public sealed class JsonLd
    {
        private JsonLd(string json) => Json = json;

        /// <summary>The serialised block, without the script element around it.</summary>
        public string Json { get; }

        /// <summary>Serialises <paramref name="node"/> with the default encoder.</summary>
        public static JsonLd Of(object node) => new(JsonSerializer.Serialize(node));
    }

    /// <summary>
    /// The schema.org graph for the landing and download pages (backlog 417): the game, the
    /// organisation that publishes it and the source code it is built from, as one <c>@graph</c>
    /// under one <c>@context</c>. The MIT licence sits on the source node only, never on the game,
    /// whose bundled resources are CC BY-NC. Serialised through <see cref="JsonLd.Of"/>, so the
    /// result is safe to emit raw inside a script element. <paramref name="withWebSite"/> adds the WebSite node that names the site
    /// in search results (backlog 418), on the landing only.
    /// </summary>
    public static JsonLd GameJsonLd(bool withWebSite = false)
        => JsonLd.Of(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["@type"] = new[] { "VideoGame", "WebApplication" },
                    ["@id"] = GAME_NODE_ID,
                    ["name"] = "type!beat",
                    ["alternateName"] = "typebeat",
                    ["url"] = CanonicalUrl("/"),
                    ["description"] = GAME_DESCRIPTION,
                    ["image"] = DEFAULT_OG_IMAGE,
                    ["screenshot"] = CANONICAL_ORIGIN + "/media/widedemo-poster.jpg",
                    ["genre"] = new[] { "Rhythm game", "Typing game", "Music game" },
                    ["gamePlatform"] = new[] { "Windows", "macOS", "Linux", "Web browser" },
                    ["applicationCategory"] = "GameApplication",
                    ["operatingSystem"] = "Windows, macOS, Linux, Web browser",
                    ["playMode"] = "SinglePlayer",
                    ["isAccessibleForFree"] = true,
                    ["downloadUrl"] = CanonicalUrl("/download"),
                    ["offers"] = new Dictionary<string, object>
                    {
                        ["@type"] = "Offer",
                        ["price"] = "0",
                        ["priceCurrency"] = "USD",
                    },
                    ["publisher"] = new Dictionary<string, object> { ["@id"] = ORGANIZATION_NODE_ID },
                    ["isBasedOn"] = new Dictionary<string, object> { ["@id"] = SOURCE_NODE_ID },
                    ["sameAs"] = new[] { SiteLinks.SOURCE_REPOSITORY, SiteLinks.DISCORD_INVITE, SiteLinks.YOUTUBE_CHANNEL },
                },
                new Dictionary<string, object>
                {
                    ["@type"] = "Organization",
                    ["@id"] = ORGANIZATION_NODE_ID,
                    ["name"] = "type!beat",
                    ["url"] = CanonicalUrl("/"),
                    ["sameAs"] = new[] { SiteLinks.SOURCE_REPOSITORY, SiteLinks.DISCORD_INVITE, SiteLinks.YOUTUBE_CHANNEL },
                },
                new Dictionary<string, object>
                {
                    ["@type"] = "SoftwareSourceCode",
                    ["@id"] = SOURCE_NODE_ID,
                    ["codeRepository"] = SiteLinks.SOURCE_REPOSITORY,
                    ["license"] = "https://opensource.org/licenses/MIT",
                    ["programmingLanguage"] = "C#",
                },
            }.Concat(withWebSite ? new object[] { webSiteNode() } : []).ToArray(),
        });

    /// <summary>
    /// The WebSite node (backlog 418): the name search results show for the site, with its
    /// fallbacks. No SearchAction, since Google retired the sitelinks search box.
    /// </summary>
    private static Dictionary<string, object> webSiteNode()
        => new()
        {
            ["@type"] = "WebSite",
            ["@id"] = WEBSITE_NODE_ID,
            ["name"] = "type!beat",
            ["alternateName"] = new[] { "typebeat.sh", "typebeat" },
            ["url"] = CanonicalUrl("/"),
        };

    /// <summary>
    /// The schema.org MusicRecording block for a beatmap set page. Since backlog 417 it also
    /// carries the selected difficulty's length (<paramref name="lengthSeconds"/>, omitted when
    /// unknown), the song's BCP 47 language when the set has one, and the mapper as a Person
    /// linked to their profile by id, as the page's "mapped by" line links it. The artist stays ONE MusicGroup, unsplit: a comma is part of
    /// names like "Tyler, The Creator".
    /// </summary>
    public static JsonLd MusicRecordingJsonLd(
        string url, string title, string artist, string? image,
        double? lengthSeconds, string? langTag, string creator, long creatorId)
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

        if (lengthSeconds is > 0)
            block["duration"] = IsoDuration(lengthSeconds.Value);

        if (!string.IsNullOrEmpty(langTag))
            block["inLanguage"] = langTag;

        block["creator"] = new Dictionary<string, object>
        {
            ["@type"] = "Person",
            ["name"] = creator,
            ["url"] = CANONICAL_ORIGIN + "/users/" + creatorId.ToString(CultureInfo.InvariantCulture),
        };

        return JsonLd.Of(block);
    }

    /// <summary>The longest artist credit a set page's title carries whole (backlog 418).</summary>
    public const int SHORT_ARTIST_MAX_LENGTH = 30;

    /// <summary>Where a long credit is cut, at whichever comes first (backlog 418).</summary>
    private static readonly string[] artist_separators = [", ", " & ", " feat", " ft.", " x "];

    /// <summary>
    /// The artist as a set page's title shows it (backlog 418). A credit of up to
    /// <see cref="SHORT_ARTIST_MAX_LENGTH"/> characters stays whole, which keeps names like
    /// "Tyler, The Creator" and "Earth, Wind &amp; Fire" intact. A longer one is cut at its first
    /// separator, and if that is still too long, at the last word boundary within the limit, with
    /// an ellipsis. The full credit stays in the page body and the MusicGroup.
    /// </summary>
    public static string ShortArtist(string artist)
    {
        if (artist.Length <= SHORT_ARTIST_MAX_LENGTH)
            return artist;

        int cut = artist_separators.Select(separator => artist.IndexOf(separator, StringComparison.Ordinal))
                                   .Where(index => index > 0)
                                   .DefaultIfEmpty(artist.Length)
                                   .Min();

        string first = artist[..cut];
        if (first.Length <= SHORT_ARTIST_MAX_LENGTH)
            return first;

        int space = first.LastIndexOf(' ', SHORT_ARTIST_MAX_LENGTH);
        return first[..(space > 0 ? space : SHORT_ARTIST_MAX_LENGTH)].TrimEnd() + "…";
    }

    /// <summary>
    /// A set page's meta description (backlog 418), from the selected difficulty's stars, length
    /// and target WPM. A clause whose value is missing is dropped, and a set the browser cannot
    /// play points at the desktop client alone.
    /// </summary>
    public static string SetDescription(
        string title, string artist, string creator,
        double? stars, double? lengthSeconds, double? targetWpm, bool playableInBrowser)
    {
        var text = new StringBuilder("Type the lyrics");
        if (title.Length > 0)
            text.Append(" of ").Append(title);
        if (artist.Length > 0)
            text.Append(" by ").Append(artist);
        text.Append(" in time with the song. ");

        if (stars is > 0)
            text.Append(stars.Value.ToString("0.0#", CultureInfo.InvariantCulture)).Append("★ map by ");
        else
            text.Append("Map by ");
        text.Append(creator);
        if (lengthSeconds is > 0)
            text.Append(", ").Append(Pages.Beatmapsets.SetModel.FormatLength(lengthSeconds.Value)).Append(" long");
        if (targetWpm is double wpm)
            text.Append(", about ").Append(wpm.ToString("0", CultureInfo.InvariantCulture)).Append(" WPM target");
        text.Append(". ");

        text.Append(playableInBrowser
            ? "Play free in your browser or in the desktop client."
            : "Play free in the desktop client.");

        return text.ToString();
    }

    /// <summary>
    /// An ISO 8601 duration in whole minutes and seconds (<c>PT3M25S</c>), truncated the way the
    /// set page's Length row (<c>SetModel.FormatLength</c>) truncates, so the two agree.
    /// </summary>
    public static string IsoDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"PT{(int)t.TotalMinutes}M{t.Seconds}S");
    }

    /// <summary>The fewest ranked plays that keep a profile indexed (backlog 418).</summary>
    public const int PROFILE_MIN_RANKED_PLAYS = 5;

    /// <summary>The fewest published sets that keep a profile indexed (backlog 418).</summary>
    public const int PROFILE_MIN_PUBLISHED_SETS = 1;

    /// <summary>
    /// Whether a profile is worth indexing (backlog 418): enough ranked plays, or at least one
    /// published set. Below both it gets noindex and leaves the sitemap, whose profile query
    /// applies the same two constants in SQL.
    /// </summary>
    public static bool ProfileIndexable(long rankedPlays, long publishedSets)
        => rankedPlays >= PROFILE_MIN_RANKED_PLAYS || publishedSets >= PROFILE_MIN_PUBLISHED_SETS;

    /// <summary>
    /// Whether a /beatmapsets search (<paramref name="query"/>, already trimmed) may be indexed
    /// (backlog 417). The unsearched listing may; any search is internal results and may not,
    /// with one temporary exception: exactly one <c>language:</c> filter naming a canonical
    /// language, the form the set page's language chip links. Nothing else, so aliases such as
    /// <c>lang:jp</c> stay out rather than duplicating it.
    /// </summary>
    public static bool ListingSearchIndexable(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return true;

        const string language_filter = "language:";
        return query.StartsWith(language_filter, StringComparison.Ordinal)
               && BeatmapLanguages.IsCanonical(query[language_filter.Length..]);
    }
}
