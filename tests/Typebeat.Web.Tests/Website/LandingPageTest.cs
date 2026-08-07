using System.Net;
using Microsoft.Extensions.Configuration;
using Typebeat.Web.Pages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Landing page (Phase B): live stats line, newest-maps strip rendered with the shared card
/// partial (cover + preview markup included), and the signed-out CTA. Plus the hero download
/// gate, which is per-platform and so cannot be exercised through the shared fixture host (it has
/// no TYPEBEAT_GAME_DOWNLOAD* keys and they cannot be varied per test); those cases drive
/// IndexModel's static gate directly instead of booting a second host.
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

            // Signed-out CTA: no game build configured in the fixture, so the hero invites sign-up
            // and neither the download button nor the unsigned-build note is rendered. (The nav's
            // own /download link is always there, so match the hero's label, not the href.)
            Assert.That(html, Does.Contain("href=\"/register\""));
            Assert.That(html, Does.Not.Contain("download installer"));
            Assert.That(html, Does.Not.Contain("Builds aren't signed yet"));

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

    /// <summary>
    /// The hero CTA points at /download, which picks the platform, so the gate must ask "is ANY
    /// build available". It used to read the Windows key alone: a deployment publishing only a
    /// macOS or Linux build hid the front-page download entry point while /download happily served
    /// the builds that did exist. The non-Windows-only cases are that bug.
    /// </summary>
    [TestCase(GameDownloadKeys.Macos)]
    [TestCase(GameDownloadKeys.Linux)]
    [TestCase(GameDownloadKeys.Windows)]
    public async Task HeroDownloadGate_ShowsWhenAnySinglePlatformIsStored(string configKey)
    {
        Assert.That(await gateAsync(configured: [configKey], stored: [configKey]), Is.True);
    }

    [Test]
    public async Task HeroDownloadGate_HiddenWhenNoPlatformIsConfigured()
    {
        Assert.That(await gateAsync(configured: [], stored: []), Is.False);
    }

    /// <summary>Configured is not enough: the gate proves presence, so a key naming a file that was
    /// never uploaded must not light the CTA up (this is why it is not a plain config read).</summary>
    [Test]
    public async Task HeroDownloadGate_HiddenWhenEveryConfiguredBuildIsMissing()
    {
        Assert.That(await gateAsync(configured: GameDownloadKeys.All, stored: []), Is.False);
    }

    /// <summary>A build stored under a later key must still be found when the earlier keys are
    /// unset: the probe short-circuits on success, not on the first unset key.</summary>
    [Test]
    public async Task HeroDownloadGate_ShowsWhenOnlyTheLastPlatformResolves()
    {
        Assert.That(
            await gateAsync(configured: GameDownloadKeys.All, stored: [GameDownloadKeys.Macos]),
            Is.True);
    }

    /// <summary>
    /// Drives <see cref="IndexModel.AnyGameDownloadAvailableAsync"/> against a temp-rooted
    /// LocalFileStore: <paramref name="configured"/> keys get a file name, and the subset in
    /// <paramref name="stored"/> also gets bytes on disk under downloads/. No host, deliberately:
    /// the shared fixture cannot vary these keys and an extra WebApplicationFactory is heavy
    /// enough to disturb the rest of the namespace.
    /// </summary>
    private static async Task<bool> gateAsync(string[] configured, string[] stored)
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-landing-cta-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        try
        {
            var settings = new Dictionary<string, string?>();

            foreach (string key in configured)
            {
                string fileName = key.ToLowerInvariant() + ".bin";
                settings[key] = fileName;

                if (stored.Contains(key))
                    await File.WriteAllBytesAsync(Path.Combine(root, "downloads", fileName), [1, 2, 3]);
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            return await IndexModel.AnyGameDownloadAvailableAsync(config, new LocalFileStore(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
