using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Landing page (Phase B): live stats line, newest-maps strip rendered with the shared card
/// partial (cover + preview markup included), and the signed-out CTA.
/// </summary>
public class LandingPageTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task Landing_ShowsStatsLine_AndNewestMapsStrip()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Live stats line (numbers vary with concurrent tests; labels are the contract).
            Assert.That(html, Does.Contain("registered players"));
            Assert.That(html, Does.Contain("scores</span>"));
            Assert.That(html, Does.Contain("maps available"));

            // Signed-out CTA: no game build configured in the fixture, so the hero invites sign-up.
            Assert.That(html, Does.Contain("href=\"/register\""));

            // Newest strip: the newest public set leads, rendered via the card partial.
            Assert.That(html, Does.Contain("Fresh Drop"));
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.FreshId}\""));
            Assert.That(html, Does.Contain("browse all maps"));

            // The covered set is inside the newest 8: cover img + preview button markup.
            Assert.That(html, Does.Contain($"/covers/{PublicSiteSeed.CoveredSetId}/1/list.jpg"));
            Assert.That(html, Does.Contain($"data-preview=\"/previews/{PublicSiteSeed.CoveredSetId}.mp3\""));

            // Hidden sets must not leak onto the landing strip.
            Assert.That(html, Does.Not.Contain("Hidden Gem Nobody"));
        });
    }

    [Test]
    public async Task Landing_SignedIn_HidesSignUpCta()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Sign in via the real two-step flow (password → emailed code → session).
        using (await WebsiteFixture.LoginAndVerifyAsync(client, WebsiteFixture.SeededUsername, WebsiteFixture.SeededPassword)) { }

        using var response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        // The hero's Sign up button is gone; the nav shows the user chip instead.
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("href=\"/register\""));
            Assert.That(html, Does.Contain("user-chip"));
        });
    }
}
