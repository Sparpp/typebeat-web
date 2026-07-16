using System.Net;

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

            // Layout nav: wordmark + the two section links.
            Assert.That(html, Does.Contain("type!beat"));
            Assert.That(html, Does.Contain("href=\"/beatmapsets\""));
            Assert.That(html, Does.Contain("href=\"/download\""));

            // Anonymous state shows a sign-in entry point.
            Assert.That(html, Does.Contain("href=\"/login\""));

            // Footer: the status-page link must stay reachable (was on the old hardcoded page).
            Assert.That(html, Does.Contain("https://stats.uptimerobot.com/E7XRJ7vfer"));
            Assert.That(html, Does.Contain("href=\"/legal/dmca\""));
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
}
