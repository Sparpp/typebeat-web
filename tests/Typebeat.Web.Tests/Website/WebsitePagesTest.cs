using System.Net;
using System.Text.RegularExpressions;
using Typebeat.Web.Caching;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Layout-level checks on the interim home page: the Razor pipeline renders, the shared nav and
/// footer are present (Phase B pages inherit both), and static assets are served from wwwroot.
/// </summary>
public class WebsitePagesTest
{
    [Test]
    public async Task Home_Returns200_WithNavAndFooter()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));

            // Layout nav: wordmark + the beatmaps and (restored) download section links.
            Assert.That(html, Does.Contain("type!beat"));
            Assert.That(html, Does.Contain("href=\"/beatmapsets\""));
            Assert.That(html, Does.Contain("href=\"/download\""));

            // Anonymous state shows a sign-in entry point.
            Assert.That(html, Does.Contain("href=\"/login\""));

            // Footer: the status-page link must stay reachable (was on the old hardcoded page).
            Assert.That(html, Does.Contain("https://stats.uptimerobot.com/E7XRJ7vfer"));
            // And the Discord link, which now renders from the shared SiteLinks constant.
            Assert.That(html, Does.Contain($"href=\"{SiteLinks.DISCORD_INVITE}\""));
            Assert.That(html, Does.Contain("href=\"/legal/dmca\""));
            Assert.That(html, Does.Contain("href=\"/legal/privacy\""));
        });
    }

    [Test]
    public async Task Stylesheet_IsServedFromWwwroot()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/css/site.css");
        string css = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/css"));
            Assert.That(css, Does.Contain("--grad-primary"));
        });
    }

    // Backlog 417: a versioned asset is kept for a year, fonts and flags for a day, and every
    // other wwwroot file keeps today's header (none), so replacing one in place needs no purge.
    [TestCase("/css/site.css?v=x", StaticAssetCaching.VERSIONED)]
    [TestCase("/fonts/jetbrainsmono-latin-wght.woff2", StaticAssetCaching.STABLE)]
    [TestCase("/flags/US.png", StaticAssetCaching.STABLE)]
    [TestCase("/media/og-default.png", null)]
    [TestCase("/css/site.css", null)]
    [TestCase("/favicon.svg", null)]
    public async Task StaticAssets_CarryTheirCacheControl(string url, string? expected)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
            Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo(expected), url);
        });
    }

    [Test]
    public async Task Layout_AppendVersionedAssets_GetTheYearLongHeader()
    {
        string html = await WebsiteFixture.Client.GetStringAsync("/");
        var link = Regex.Match(html, "href=\"(/css/site\\.css\\?v=[^\"]+)\"");
        Assert.That(link.Success, Is.True, "the stylesheet link carries asp-append-version's v");

        using var response = await WebsiteFixture.Client.GetAsync(WebUtility.HtmlDecode(link.Groups[1].Value));
        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo(StaticAssetCaching.VERSIONED));
    }

    [Test]
    public async Task DmcaPage_Renders()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/legal/dmca");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("takedown"));
        });
    }

    [Test]
    public async Task PrivacyPage_Renders()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/legal/privacy");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("Google user data"));
        });
    }
}
