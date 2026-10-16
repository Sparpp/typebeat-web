using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Search and share metadata (backlog 369): canonical and og:url on every page, noindex on the
/// account and per-request pages, twitter card tags, JSON-LD, the app-served robots.txt and
/// sitemap.xml, and crawlable listing pages. Runs on the shared WebsiteFixture host.
/// </summary>
public class SeoTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    private static async Task<string> getHtmlAsync(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(expected), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static string? canonicalOf(string html)
    {
        var match = Regex.Match(html, "<link rel=\"canonical\" href=\"([^\"]+)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string? metaOf(string html, string attribute, string name)
    {
        var match = Regex.Match(html, $"<meta {attribute}=\"{Regex.Escape(name)}\" content=\"([^\"]*)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string? titleOf(string html)
    {
        var match = Regex.Match(html, "<title>([^<]*)</title>");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static JsonElement jsonLdOf(string html)
    {
        var match = Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.That(match.Success, Is.True, "no JSON-LD block");
        return JsonDocument.Parse(match.Groups[1].Value).RootElement.Clone();
    }

    private static IEnumerable<string?> typesOf(JsonElement node)
    {
        var type = node.GetProperty("@type");
        return type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(e => e.GetString()) : [type.GetString()];
    }

    /// <summary>The one node of an <c>@graph</c> root whose <c>@type</c> names <paramref name="type"/>.</summary>
    private static JsonElement node(JsonElement root, string type)
    {
        var matches = root.GetProperty("@graph").EnumerateArray().Where(n => typesOf(n).Contains(type)).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"one {type} node in the graph");
        return matches[0];
    }

    private static string[] stringsOf(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Test]
    public async Task Landing_CarriesTitleCanonicalShareTagsAndVideoGameJsonLd()
    {
        string html = await getHtmlAsync("/");
        var root = jsonLdOf(html);
        var game = node(root, "VideoGame");
        var org = node(root, "Organization");
        var source = node(root, "SoftwareSourceCode");
        var site = node(root, "WebSite");

        Assert.Multiple(() =>
        {
            Assert.That(titleOf(html), Is.EqualTo("type!beat: the lyric typing rhythm game"));
            Assert.That(html, Does.Contain("<p class=\"hero-sub\">the free lyric typing rhythm game</p>"));

            // The canonical names typebeat.sh even though the fixture serves https://localhost.
            Assert.That(canonicalOf(html), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(metaOf(html, "property", "og:url"), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(metaOf(html, "property", "og:site_name"), Is.EqualTo("type!beat"));
            Assert.That(metaOf(html, "property", "og:image"), Is.EqualTo(Seo.DEFAULT_OG_IMAGE));
            Assert.That(metaOf(html, "name", "twitter:card"), Is.EqualTo("summary_large_image"));
            Assert.That(metaOf(html, "name", "twitter:title"), Is.EqualTo(Seo.LANDING_TITLE));
            Assert.That(metaOf(html, "name", "twitter:image"), Is.EqualTo(Seo.DEFAULT_OG_IMAGE));
            Assert.That(metaOf(html, "name", "twitter:description"), Is.EqualTo(metaOf(html, "name", "description")));
            Assert.That(metaOf(html, "name", "robots"), Is.Null);

            string description = metaOf(html, "name", "description")!;
            Assert.That(description, Does.Contain("lyric typing rhythm game"));
            Assert.That(description, Does.Contain("type to the beat"));
            Assert.That(description, Does.Contain("typing game with music"));

            // Backlog 417: the default share image declares its size; the description is the
            // constant the JSON-LD shares, unchanged.
            Assert.That(metaOf(html, "property", "og:image:width"), Is.EqualTo("1200"));
            Assert.That(metaOf(html, "property", "og:image:height"), Is.EqualTo("630"));
            Assert.That(description, Is.EqualTo(Seo.GAME_DESCRIPTION));

            // One @graph under one @context (backlog 417), plus the WebSite node (backlog 418).
            Assert.That(root.GetProperty("@context").GetString(), Is.EqualTo("https://schema.org"));
            Assert.That(root.GetProperty("@graph").GetArrayLength(), Is.EqualTo(4));
            Assert.That(typesOf(game), Is.EqualTo(new[] { "VideoGame", "WebApplication" }));

            // Absolute ids, so /download names the same three entities rather than its own.
            Assert.That(game.GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#game"));
            Assert.That(org.GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#org"));
            Assert.That(source.GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#source"));
            Assert.That(game.GetProperty("publisher").GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#org"));
            Assert.That(game.GetProperty("isBasedOn").GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#source"));

            Assert.That(game.GetProperty("name").GetString(), Is.EqualTo("type!beat"));
            Assert.That(game.GetProperty("alternateName").GetString(), Is.EqualTo("typebeat"));
            Assert.That(game.GetProperty("url").GetString(), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(game.GetProperty("description").GetString(), Is.EqualTo(Seo.GAME_DESCRIPTION));
            Assert.That(stringsOf(game.GetProperty("genre")), Is.EqualTo(new[] { "Rhythm game", "Typing game", "Music game" }));
            Assert.That(stringsOf(game.GetProperty("gamePlatform")), Is.EqualTo(new[] { "Windows", "macOS", "Linux", "Web browser" }));
            Assert.That(game.GetProperty("applicationCategory").GetString(), Is.EqualTo("GameApplication"));
            Assert.That(game.GetProperty("operatingSystem").GetString(), Is.EqualTo("Windows, macOS, Linux, Web browser"));
            Assert.That(game.GetProperty("playMode").GetString(), Is.EqualTo("SinglePlayer"));
            Assert.That(game.GetProperty("isAccessibleForFree").GetBoolean(), Is.True);
            Assert.That(game.GetProperty("downloadUrl").GetString(), Is.EqualTo("https://typebeat.sh/download"));
            Assert.That(game.GetProperty("screenshot").GetString(), Is.EqualTo("https://typebeat.sh/media/widedemo-poster.jpg"));
            Assert.That(game.GetProperty("offers").GetProperty("price").GetString(), Is.EqualTo("0"));
            Assert.That(stringsOf(game.GetProperty("sameAs")),
                Is.EqualTo(new[] { SiteLinks.SOURCE_REPOSITORY, SiteLinks.DISCORD_INVITE, SiteLinks.YOUTUBE_CHANNEL }));

            // The game ships CC BY-NC resources, so the MIT licence is the source code's alone, and
            // no rating or review is ever claimed.
            Assert.That(game.TryGetProperty("license", out _), Is.False, "no licence on the game");
            Assert.That(source.GetProperty("license").GetString(), Is.EqualTo("https://opensource.org/licenses/MIT"));
            Assert.That(source.GetProperty("codeRepository").GetString(), Is.EqualTo(SiteLinks.SOURCE_REPOSITORY));
            Assert.That(source.GetProperty("programmingLanguage").GetString(), Is.EqualTo("C#"));
            foreach (var n in root.GetProperty("@graph").EnumerateArray())
            {
                Assert.That(n.TryGetProperty("aggregateRating", out _), Is.False, "no aggregateRating");
                Assert.That(n.TryGetProperty("review", out _), Is.False, "no review");
            }

            Assert.That(org.GetProperty("name").GetString(), Is.EqualTo("type!beat"));
            Assert.That(org.GetProperty("url").GetString(), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(stringsOf(org.GetProperty("sameAs")),
                Is.EqualTo(new[] { SiteLinks.SOURCE_REPOSITORY, SiteLinks.DISCORD_INVITE, SiteLinks.YOUTUBE_CHANNEL }));

            // Out of scope until signed off: the Organization logo.
            Assert.That(org.TryGetProperty("logo", out _), Is.False, "no logo");

            // Backlog 418: the site name search results show, with its fallbacks, and no SearchAction.
            Assert.That(site.GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#website"));
            Assert.That(site.GetProperty("name").GetString(), Is.EqualTo("type!beat"));
            Assert.That(stringsOf(site.GetProperty("alternateName")), Is.EqualTo(new[] { "typebeat.sh", "typebeat" }));
            Assert.That(site.GetProperty("url").GetString(), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(site.TryGetProperty("potentialAction", out _), Is.False, "no SearchAction");
        });
    }

    [Test]
    public async Task Download_HasSuffixedTitleAndVideoGameJsonLd()
    {
        string html = await getHtmlAsync("/download");
        var root = jsonLdOf(html);
        var game = node(root, "VideoGame");

        Assert.Multiple(() =>
        {
            Assert.That(titleOf(html), Is.EqualTo("Download · type!beat"));
            Assert.That(canonicalOf(html), Is.EqualTo("https://typebeat.sh/download"));
            Assert.That(root.GetProperty("@context").GetString(), Is.EqualTo("https://schema.org"));
            Assert.That(typesOf(game), Does.Contain("VideoGame"));

            // The landing's ids (backlog 417), not fragments of /download.
            Assert.That(game.GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#game"));
            Assert.That(node(root, "Organization").GetProperty("@id").GetString(), Is.EqualTo("https://typebeat.sh/#org"));

            // The WebSite node is the landing's alone (backlog 418).
            Assert.That(root.GetProperty("@graph").EnumerateArray().Any(n => typesOf(n).Contains("WebSite")), Is.False, "no WebSite node");

            // The JSON-LD carries the game's description; the page's own meta description stays.
            Assert.That(game.GetProperty("description").GetString(), Is.EqualTo(Seo.GAME_DESCRIPTION));
            Assert.That(metaOf(html, "name", "description"), Does.StartWith("Get type!beat for Windows, macOS or Linux"));
        });
    }

    [Test]
    public async Task SetPage_CanonicalIgnoresQuery_AndCarriesMusicRecording()
    {
        long id = PublicSiteSeed.LeaderboardSetId;
        string html = await getHtmlAsync($"/beatmapsets/{id}?comments_after=5&after=123&utm_source=x");
        var recording = jsonLdOf(html);
        var chip = Regex.Match(html, "<a class=\"chip\" href=\"/beatmapsets\\?q=language%3A([a-z]+)\"");

        Assert.Multiple(() =>
        {
            Assert.That(canonicalOf(html), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(metaOf(html, "property", "og:url"), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(titleOf(html), Does.EndWith(" · type!beat"));

            // Backlog 418: the selected difficulty's stars, length and target, and a browser play.
            Assert.That(titleOf(html), Is.EqualTo("Leaderboard Anthem typing game: The Score Settlers · type!beat"));
            Assert.That(metaOf(html, "name", "description"), Is.EqualTo(
                "Type the lyrics of Leaderboard Anthem by The Score Settlers in time with the song. "
                + "3.2★ map by neon mapper, 1:35 long, about 118 WPM target. Play free in your browser or in the desktop client."));

            Assert.That(recording.GetProperty("@type").GetString(), Is.EqualTo("MusicRecording"));
            Assert.That(recording.GetProperty("url").GetString(), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(recording.GetProperty("byArtist").GetProperty("@type").GetString(), Is.EqualTo("MusicGroup"));
            Assert.That(recording.GetProperty("name").GetString(), Is.Not.Empty);

            // Backlog 417: the selected (only) difficulty's 95.5 s, truncated as the Length row
            // shows it (1:35), and the mapper linked by id as the "mapped by" line links them.
            Assert.That(recording.GetProperty("duration").GetString(), Is.EqualTo("PT1M35S"));
            Assert.That(html, Does.Contain(">1:35<"));
            Assert.That(recording.GetProperty("creator").GetProperty("@type").GetString(), Is.EqualTo("Person"));
            Assert.That(recording.GetProperty("creator").GetProperty("name").GetString(), Is.EqualTo("neon mapper"));
            Assert.That(recording.GetProperty("creator").GetProperty("url").GetString(),
                Is.EqualTo($"https://typebeat.sh/users/{PublicSiteSeed.MapperId}"));
            // The shared set's language is not fixed across the run, so the expectation is read off
            // the page's own language chip: its tag when there is one, otherwise no inLanguage.
            string? expectedLang = chip.Success ? BeatmapLanguages.LangTag(chip.Groups[1].Value) : null;
            if (expectedLang is null)
                Assert.That(recording.TryGetProperty("inLanguage", out _), Is.False, "no language tag, no inLanguage");
            else
                Assert.That(recording.GetProperty("inLanguage").GetString(), Is.EqualTo(expectedLang));

            // No cover of its own: the default share image, which declares its size.
            Assert.That(metaOf(html, "property", "og:image"), Is.EqualTo(Seo.DEFAULT_OG_IMAGE));
            Assert.That(metaOf(html, "property", "og:image:width"), Is.EqualTo("1200"));
        });
    }

    [Test]
    public async Task SetPage_CoverShareImage_NamesTheCanonicalHost_AndDeclaresNoSize()
    {
        long id = PublicSiteSeed.CoveredSetId;
        string html = await getHtmlAsync($"/beatmapsets/{id}");
        string? image = metaOf(html, "property", "og:image");

        Assert.Multiple(() =>
        {
            // The fixture serves https://localhost; the share image is on typebeat.sh regardless (backlog 417).
            Assert.That(image, Is.EqualTo($"https://typebeat.sh/covers/{id}/1/cover.jpg"));
            Assert.That(new Uri(image!).Host, Is.EqualTo("typebeat.sh"));
            Assert.That(metaOf(html, "name", "twitter:image"), Is.EqualTo(image));
            Assert.That(jsonLdOf(html).GetProperty("image").GetString(), Is.EqualTo(image));

            // A cover's size is not known, so none is claimed.
            Assert.That(metaOf(html, "property", "og:image:width"), Is.Null);
            Assert.That(metaOf(html, "property", "og:image:height"), Is.Null);
        });
    }

    [Test]
    public async Task SetPage_NotPlayableInTheBrowser_PointsAtTheDesktopClient()
    {
        // No package, so no browser play; no target WPM either, so that clause drops (backlog 418).
        string html = await getHtmlAsync($"/beatmapsets/{PublicSiteSeed.PackagelessId}");

        Assert.Multiple(() =>
        {
            Assert.That(titleOf(html), Is.EqualTo("Editor Era Classic typing game: The Backfilled · type!beat"));
            Assert.That(metaOf(html, "name", "description"), Is.EqualTo(
                "Type the lyrics of Editor Era Classic by The Backfilled in time with the song. "
                + "2.5★ map by neon mapper, 1:40 long. Play free in the desktop client."));
        });
    }

    [TestCase("Friday Pilots Club", "Friday Pilots Club")]
    [TestCase("Tyler, The Creator", "Tyler, The Creator")]
    [TestCase("Earth, Wind & Fire", "Earth, Wind & Fire")]
    [TestCase("NicoisNXXT, churgney gurgney, KennyTheLyr1c1st, ChaseD_Ih", "NicoisNXXT")]
    [TestCase("Some Fairly Long Artist Name & Another One", "Some Fairly Long Artist Name")]
    [TestCase("A Rather Long Headliner Name feat. Guest Vocalist", "A Rather Long Headliner Name")]
    [TestCase("The Producer Collective Prime x Vocal Guest", "The Producer Collective Prime")]
    [TestCase("The Extraordinarily Long Named Orchestra of Somewhere", "The Extraordinarily Long Named…")]
    [TestCase("A Very Long Primary Artist Name Here ft. Someone", "A Very Long Primary Artist…")]
    [TestCase("Supercalifragilisticexpialidocious Orchestra", "Supercalifragilisticexpialidoc…")]
    public void ShortArtist_KeepsShortCreditsWhole_AndCutsLongOnes(string artist, string expected)
        => Assert.That(Seo.ShortArtist(artist), Is.EqualTo(expected));

    [Test]
    public void SetDescription_DropsEveryMissingClause()
        => Assert.That(Seo.SetDescription("Song", "", "mapper", stars: null, lengthSeconds: null, targetWpm: null, playableInBrowser: false),
            Is.EqualTo("Type the lyrics of Song in time with the song. Map by mapper. Play free in the desktop client."));

    [Test]
    public void MusicRecording_CarriesLanguageAndLength_AndKeepsACommaArtistWhole()
    {
        var recording = JsonDocument.Parse(Seo.MusicRecordingJsonLd(
            "https://typebeat.sh/beatmapsets/7", "See You Again", "Tyler, The Creator", null,
            lengthSeconds: 3725.9, langTag: "ja", creator: "neon mapper", creatorId: 42)).RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(recording.GetProperty("inLanguage").GetString(), Is.EqualTo("ja"));
            Assert.That(recording.GetProperty("duration").GetString(), Is.EqualTo("PT62M5S"));
            Assert.That(recording.GetProperty("byArtist").GetProperty("@type").GetString(), Is.EqualTo("MusicGroup"));
            Assert.That(recording.GetProperty("byArtist").GetProperty("name").GetString(), Is.EqualTo("Tyler, The Creator"));
            Assert.That(recording.GetProperty("creator").GetProperty("url").GetString(), Is.EqualTo("https://typebeat.sh/users/42"));
            Assert.That(recording.TryGetProperty("image", out _), Is.False);
        });
    }

    [Test]
    public void MusicRecording_OmitsAnUnknownLength()
    {
        var recording = JsonDocument.Parse(Seo.MusicRecordingJsonLd(
            "https://typebeat.sh/beatmapsets/7", "t", "a", null, lengthSeconds: null, langTag: null, creator: "m", creatorId: 1)).RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(recording.TryGetProperty("duration", out _), Is.False);
            Assert.That(recording.TryGetProperty("inLanguage", out _), Is.False);
        });
    }

    [Test]
    public async Task Profile_ReachedByName_CanonicalisesToId()
    {
        // The mapper, whose published sets keep the profile indexed and so canonical (backlog 418).
        string html = await getHtmlAsync("/users/" + Uri.EscapeDataString("neon mapper"));
        Assert.That(canonicalOf(html), Is.EqualTo($"https://typebeat.sh/users/{PublicSiteSeed.MapperId}"));
    }

    [Test]
    public async Task Profile_BelowTheThreshold_IsNoIndex_AndAMappersIsNot()
    {
        // Typist one has a handful of ranked plays and no sets (backlog 418).
        string thin = await getHtmlAsync($"/users/{PublicSiteSeed.TypistOneId}");
        string mapper = await getHtmlAsync($"/users/{PublicSiteSeed.MapperId}");

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(thin, "name", "robots"), Is.EqualTo("noindex"));
            Assert.That(canonicalOf(thin), Is.Null, "a noindex page names no canonical");

            Assert.That(metaOf(mapper, "name", "robots"), Is.Null);
            Assert.That(canonicalOf(mapper), Is.EqualTo($"https://typebeat.sh/users/{PublicSiteSeed.MapperId}"));
        });
    }

    [TestCase(0, 0, false)]
    [TestCase(4, 0, false)]
    [TestCase(5, 0, true)]
    [TestCase(0, 1, true)]
    [TestCase(120, 3, true)]
    public void ProfileIndexable_FiveRankedPlaysOrOnePublishedSet(long rankedPlays, long publishedSets, bool expected)
        => Assert.That(Seo.ProfileIndexable(rankedPlays, publishedSets), Is.EqualTo(expected));

    [TestCase("/login")]
    [TestCase("/register")]
    [TestCase("/forgot-password")]
    public async Task AccountPages_AreNoIndex(string url)
    {
        string html = await getHtmlAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(html, "name", "robots"), Is.EqualTo("noindex"));
            Assert.That(canonicalOf(html), Is.Null, "a noindex page names no canonical");
        });
    }

    [Test]
    public async Task Play_IsIndexed_WithItsOwnTitleDescriptionAndIntro()
    {
        string html = await getHtmlAsync("/play");

        Assert.Multiple(() =>
        {
            // Backlog 418.
            Assert.That(metaOf(html, "name", "robots"), Is.Null);
            Assert.That(canonicalOf(html), Is.EqualTo("https://typebeat.sh/play"));
            Assert.That(titleOf(html), Is.EqualTo("Play online free, no download: typing to music · type!beat"));
            Assert.That(metaOf(html, "name", "description"), Is.EqualTo(
                "Play type!beat free in your browser: pick a song and type the lyrics in time with the music. "
                + "No download, no account needed. Sign in to save your scores and earn pp."));
            Assert.That(html, Does.Contain(
                "<a href=\"/login\">sign in</a> to save your scores to the leaderboard and earn pp on ranked maps.</p>"));
        });

        // The intro sits inside the picker, above the card grid, so it hides with the picker when
        // a run starts (play.js showStage).
        int picker = html.IndexOf("<div id=\"tb-picker\"", StringComparison.Ordinal);
        int stage = html.IndexOf("<div id=\"tb-stage\"", StringComparison.Ordinal);
        Assert.That(picker, Is.GreaterThan(0));
        Assert.That(stage, Is.GreaterThan(picker));
        string pickerHtml = html[picker..stage];

        Assert.Multiple(() =>
        {
            foreach (string line in new[]
                     {
                         "<p>Pick a map below and type the lyrics as they're sung. No download, no account.</p>",
                         "<p>Only letters, numbers and spaces count, so don't worry about capitals or punctuation. A hyphen counts as a space. "
                         + "You can pause (Esc), retry or quit a run, and skip long instrumental breaks. Every run ends with your score, "
                         + "accuracy, WPM, best combo, typos and your count of Greats, Oks, Mehs and misses.</p>",
                         "<p>Mods, rate changes and the map editor live in the desktop client.</p>",
                         "<p>type!beat is played on a physical keyboard.</p>",
                     })
            {
                Assert.That(pickerHtml, Does.Contain(line));
                Assert.That(pickerHtml.IndexOf(line, StringComparison.Ordinal),
                    Is.LessThan(pickerHtml.IndexOf("class=\"card-grid\"", StringComparison.Ordinal)), "above the card grid");
            }
        });
    }

    [Test]
    public async Task PlayDeepLink_CanonicalisesToPlay()
    {
        string html = await getHtmlAsync($"/play?set={PublicSiteSeed.MultiDiffSetId}&diff={PublicSiteSeed.MultiDiffEasyId}");
        Assert.That(canonicalOf(html), Is.EqualTo("https://typebeat.sh/play"));
    }

    [Test]
    public async Task ErrorPage_IsNoIndex()
    {
        string html = await getHtmlAsync("/no-such-page-for-seo", HttpStatusCode.NotFound);
        Assert.That(metaOf(html, "name", "robots"), Is.EqualTo("noindex"));
    }

    [Test]
    public async Task Robots_IsPlainTextWithDisallowsAndSitemap()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/robots.txt");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
            Assert.That(body, Does.Contain("User-agent: *"));
            Assert.That(body, Does.Contain("Sitemap: https://typebeat.sh/sitemap.xml"));

            foreach (string path in new[]
                     {
                         "/api/", "/bss/", "/oauth/", "/play/token", "/play/submit", "/settings", "/watching", "/logout", "/verify", "/error/",
                         "/beatmapsets/*/download", "/download/game", "/play/map/",
                     })
                Assert.That(body, Does.Contain($"Disallow: {path}\n"), path);

            Assert.That(body, Does.Not.Contain("Disallow: /\n"), "the site as a whole stays crawlable");
            Assert.That(body, Does.Not.Contain("Disallow: /download\n"), "the /download page itself stays crawlable");

            // Noindexed pages stay crawlable, or a crawler could never read their noindex.
            foreach (string path in new[] { "/beatmapsets\n", "/beatmapsets/\n", "/rankings", "/users" })
                Assert.That(body, Does.Not.Contain($"Disallow: {path}"), path);
        });
    }

    [Test]
    public async Task ListingSearch_IsNoIndex_WithNoCanonical()
    {
        string html = await getHtmlAsync("/beatmapsets?q=love");

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(html, "name", "robots"), Is.EqualTo("noindex"));
            Assert.That(canonicalOf(html), Is.Null, "a noindex page names no canonical");
        });
    }

    [TestCase("/beatmapsets?q=language:japanese", "https://typebeat.sh/beatmapsets?q=language%3Ajapanese")]
    [TestCase("/beatmapsets?s=plays&status=ranked", "https://typebeat.sh/beatmapsets?s=plays&status=ranked")]
    [TestCase("/beatmapsets", "https://typebeat.sh/beatmapsets")]
    public async Task ListingWithoutAFreeSearch_StaysIndexed(string url, string canonical)
    {
        string html = await getHtmlAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(html, "name", "robots"), Is.Null);
            Assert.That(canonicalOf(html), Is.EqualTo(canonical));
        });
    }

    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("language:japanese", true)]
    [TestCase("language:english", true)]
    [TestCase("love", false)]
    [TestCase("language:japanese love", false)]
    [TestCase("language:japanese star:>4", false)]
    [TestCase("lang:japanese", false)]
    [TestCase("language:jp", false)]
    [TestCase("language:Japanese", false)]
    [TestCase("language:klingon", false)]
    [TestCase("language:", false)]
    [TestCase("star:>4", false)]
    public void ListingSearchIndexable_OnlyTheUnsearchedListingAndOneCanonicalLanguageFilter(string? query, bool expected)
        => Assert.That(Seo.ListingSearchIndexable(query), Is.EqualTo(expected));

    [Test]
    public async Task FollowLists_AreNoIndex()
    {
        foreach (string list in new[] { "followers", "following" })
        {
            string html = await getHtmlAsync($"/users/{PublicSiteSeed.MapperId}/{list}");

            Assert.Multiple(() =>
            {
                Assert.That(metaOf(html, "name", "robots"), Is.EqualTo("noindex"), list);
                Assert.That(canonicalOf(html), Is.Null, list);
            });
        }

        // The profile they hang off is still indexed.
        string profile = await getHtmlAsync($"/users/{PublicSiteSeed.MapperId}");
        Assert.That(metaOf(profile, "name", "robots"), Is.Null);
    }

    [Test]
    public async Task RankingsPlayerSearch_IsNoIndex_AndTheBoardsAreNot()
    {
        string search = await getHtmlAsync("/rankings?q=x");
        string board = await getHtmlAsync("/rankings?board=score");

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(search, "name", "robots"), Is.EqualTo("noindex"));
            Assert.That(canonicalOf(search), Is.Null);

            Assert.That(metaOf(board, "name", "robots"), Is.Null);
            Assert.That(canonicalOf(board), Is.EqualTo("https://typebeat.sh/rankings?board=score"));
        });
    }

    [Test]
    public async Task Sitemap_ListsPublishedSetsWithLastmod_AndNotHiddenOnes()
    {
        SeoEndpoints.ResetCache();

        using var response = await WebsiteFixture.Client.GetAsync("/sitemap.xml");
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/xml"));

        var doc = XDocument.Parse(body);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        Assert.That(doc.Root!.Name, Is.EqualTo(ns + "urlset"));

        var urls = doc.Root.Elements(ns + "url")
                      .ToDictionary(u => u.Element(ns + "loc")!.Value, u => u.Element(ns + "lastmod")?.Value);

        string published = $"https://typebeat.sh/beatmapsets/{PublicSiteSeed.LeaderboardSetId}";
        string typist = $"https://typebeat.sh/users/{PublicSiteSeed.TypistOneId}";
        string mapper = $"https://typebeat.sh/users/{PublicSiteSeed.MapperId}";

        Assert.Multiple(() =>
        {
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/download"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/beatmapsets"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/rankings"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/legal/privacy"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/play"));

            Assert.That(urls.Keys, Does.Contain(published));
            Assert.That(urls.GetValueOrDefault(published), Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
            Assert.That(urls.Keys, Does.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.PendingId}"));

            Assert.That(urls.Keys, Does.Not.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.HiddenId}"));
            Assert.That(urls.Keys, Does.Not.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.RemovedId}"));

            // Backlog 418: only profiles Seo.ProfileIndexable keeps. The mapper owns every seeded
            // set, so is listed with a lastmod; typist one has fewer than five ranked plays and no set.
            Assert.That(urls.Keys, Does.Contain(mapper));
            Assert.That(urls.GetValueOrDefault(mapper), Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
            Assert.That(urls.Keys, Does.Not.Contain(typist));
        });
    }

    [Test]
    public void SitemapIndex_NamesEveryPage()
    {
        var doc = XDocument.Parse(SeoEndpoints.WriteIndex(3));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        Assert.That(doc.Root!.Elements(ns + "sitemap").Select(s => s.Element(ns + "loc")!.Value), Is.EqualTo(new[]
        {
            "https://typebeat.sh/sitemap.xml?page=1",
            "https://typebeat.sh/sitemap.xml?page=2",
            "https://typebeat.sh/sitemap.xml?page=3",
        }));
    }

    [Test]
    public async Task Listing_NextPageIsAPlainAnchor_WithItsOwnCanonical()
    {
        string first = await getHtmlAsync("/beatmapsets?unplayed=true");
        Assert.That(canonicalOf(first), Is.EqualTo("https://typebeat.sh/beatmapsets"), "unplayed is per-user and dropped");

        var next = Regex.Match(first, "<a class=\"btn btn-ghost\" href=\"(/beatmapsets\\?[^\"]*after=[^\"]+)\">show more</a>");
        Assert.That(next.Success, Is.True, "the listing's next page is a real anchor");

        string second = await getHtmlAsync(WebUtility.HtmlDecode(next.Groups[1].Value));
        string? canonical = canonicalOf(second);

        Assert.Multiple(() =>
        {
            Assert.That(canonical, Does.StartWith("https://typebeat.sh/beatmapsets?"));
            Assert.That(canonical, Does.Contain("after="));
            Assert.That(canonical, Does.Contain("after_id="));
        });
    }

    [TestCase("/", "", "https://typebeat.sh/")]
    [TestCase("/Download/", "", "https://typebeat.sh/download")]
    [TestCase("/beatmapsets", "?q=night%20drive&s=plays&status=ranked&unplayed=true&junk=1", "https://typebeat.sh/beatmapsets?q=night%20drive&s=plays&status=ranked")]
    [TestCase("/beatmapsets", "?s=newest&status=any", "https://typebeat.sh/beatmapsets")]
    [TestCase("/beatmapsets", "?after=99&after_id=7&after_tier=1", "https://typebeat.sh/beatmapsets?after=99&after_id=7&after_tier=1")]
    [TestCase("/rankings", "?board=score&page=2", "https://typebeat.sh/rankings?board=score&page=2")]
    [TestCase("/rankings", "?page=1", "https://typebeat.sh/rankings")]
    [TestCase("/rankings", "?board=performance&page=abc", "https://typebeat.sh/rankings")]
    [TestCase("/beatmapsets/12", "?q=x&after=3", "https://typebeat.sh/beatmapsets/12")]
    [TestCase("/play", "?set=12&diff=34", "https://typebeat.sh/play")]
    public void CanonicalUrl_KeepsOnlyThePathsMeaningfulQuery(string path, string query, string expected)
        => Assert.That(Seo.CanonicalUrl(path, new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected));

    [TestCase("/login", true)]
    [TestCase("/settings/avatar", true)]
    [TestCase("/error/404", true)]
    [TestCase("/play", false)]
    [TestCase("/auth/google/username", true)]
    [TestCase("/playlist", false)]
    [TestCase("/beatmapsets", false)]
    [TestCase("/", false)]
    public void NoIndex_MatchesWholeSegments(string path, bool expected)
        => Assert.That(Seo.IsNoIndex(path), Is.EqualTo(expected));
}
