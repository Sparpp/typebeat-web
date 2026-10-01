using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Typebeat.Web.Endpoints;

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

    [Test]
    public async Task Landing_CarriesTitleCanonicalShareTagsAndVideoGameJsonLd()
    {
        string html = await getHtmlAsync("/");
        var game = jsonLdOf(html);

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

            Assert.That(game.GetProperty("@context").GetString(), Is.EqualTo("https://schema.org"));
            Assert.That(game.GetProperty("@type").GetString(), Is.EqualTo("VideoGame"));
            Assert.That(game.GetProperty("name").GetString(), Is.EqualTo("type!beat"));
            Assert.That(game.GetProperty("alternateName").GetString(), Is.EqualTo("typebeat"));
            Assert.That(game.GetProperty("url").GetString(), Is.EqualTo("https://typebeat.sh/"));
            Assert.That(game.GetProperty("genre").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "Rhythm", "Typing" }));
            Assert.That(game.GetProperty("gamePlatform").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(new[] { "Windows", "macOS", "Linux", "Web browser" }));
            Assert.That(game.GetProperty("offers").GetProperty("price").GetString(), Is.EqualTo("0"));
            Assert.That(game.GetProperty("sameAs").EnumerateArray().Select(e => e.GetString()),
                Does.Contain(SiteLinks.DISCORD_INVITE).And.Contain(SiteLinks.SOURCE_REPOSITORY));
        });
    }

    [Test]
    public async Task Download_HasSuffixedTitleAndVideoGameJsonLd()
    {
        string html = await getHtmlAsync("/download");

        Assert.Multiple(() =>
        {
            Assert.That(titleOf(html), Is.EqualTo("Download · type!beat"));
            Assert.That(canonicalOf(html), Is.EqualTo("https://typebeat.sh/download"));
            Assert.That(jsonLdOf(html).GetProperty("@type").GetString(), Is.EqualTo("VideoGame"));
        });
    }

    [Test]
    public async Task SetPage_CanonicalIgnoresQuery_AndCarriesMusicRecording()
    {
        long id = PublicSiteSeed.LeaderboardSetId;
        string html = await getHtmlAsync($"/beatmapsets/{id}?comments_after=5&after=123&utm_source=x");
        var recording = jsonLdOf(html);

        Assert.Multiple(() =>
        {
            Assert.That(canonicalOf(html), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(metaOf(html, "property", "og:url"), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(titleOf(html), Does.EndWith(" · type!beat"));
            Assert.That(recording.GetProperty("@type").GetString(), Is.EqualTo("MusicRecording"));
            Assert.That(recording.GetProperty("url").GetString(), Is.EqualTo($"https://typebeat.sh/beatmapsets/{id}"));
            Assert.That(recording.GetProperty("byArtist").GetProperty("@type").GetString(), Is.EqualTo("MusicGroup"));
            Assert.That(recording.GetProperty("name").GetString(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Profile_ReachedByName_CanonicalisesToId()
    {
        string html = await getHtmlAsync("/users/" + Uri.EscapeDataString(WebsiteFixture.SeededUsername));
        Assert.That(canonicalOf(html), Is.EqualTo($"https://typebeat.sh/users/{WebsiteFixture.SeededUserId}"));
    }

    [TestCase("/login")]
    [TestCase("/register")]
    [TestCase("/forgot-password")]
    [TestCase("/play")]
    public async Task AccountAndPlayPages_AreNoIndex(string url)
    {
        string html = await getHtmlAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(metaOf(html, "name", "robots"), Is.EqualTo("noindex"));
            Assert.That(canonicalOf(html), Is.Null, "a noindex page names no canonical");
        });
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

            foreach (string path in new[] { "/api/", "/bss/", "/oauth/", "/play/token", "/play/submit", "/settings", "/watching", "/logout", "/verify", "/error/" })
                Assert.That(body, Does.Contain($"Disallow: {path}\n"), path);

            Assert.That(body, Does.Not.Contain("Disallow: /\n"), "the site as a whole stays crawlable");
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

        Assert.Multiple(() =>
        {
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/download"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/beatmapsets"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/rankings"));
            Assert.That(urls.Keys, Does.Contain("https://typebeat.sh/legal/privacy"));

            Assert.That(urls.Keys, Does.Contain(published));
            Assert.That(urls.GetValueOrDefault(published), Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
            Assert.That(urls.Keys, Does.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.PendingId}"));

            Assert.That(urls.Keys, Does.Not.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.HiddenId}"));
            Assert.That(urls.Keys, Does.Not.Contain($"https://typebeat.sh/beatmapsets/{PublicSiteSeed.RemovedId}"));

            // A profile with a score is listed, its latest score as lastmod.
            Assert.That(urls.Keys, Does.Contain(typist));
            Assert.That(urls.GetValueOrDefault(typist), Is.Not.Null);
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
    public void CanonicalUrl_KeepsOnlyThePathsMeaningfulQuery(string path, string query, string expected)
        => Assert.That(Seo.CanonicalUrl(path, new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected));

    [TestCase("/login", true)]
    [TestCase("/settings/avatar", true)]
    [TestCase("/error/404", true)]
    [TestCase("/play", true)]
    [TestCase("/auth/google/username", true)]
    [TestCase("/playlist", false)]
    [TestCase("/beatmapsets", false)]
    [TestCase("/", false)]
    public void NoIndex_MatchesWholeSegments(string path, bool expected)
        => Assert.That(Seo.IsNoIndex(path), Is.EqualTo(expected));
}
