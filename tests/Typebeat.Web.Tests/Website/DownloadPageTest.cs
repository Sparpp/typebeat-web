using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The /download page (restored after the "remove fluff" deletion). The shared fixture host has
/// none of TYPEBEAT_GAME_DOWNLOAD / _LINUX / _MACOS configured ⇒ the "coming soon" state. A second
/// host with all three keys + real files under {TYPEBEAT_FILE_ROOT}/downloads exercises the
/// per-platform Windows + Linux + macOS cards and their download hrefs.
///
/// The byte-stream itself (/download/game, /download/game-linux, /download/game-macos) is covered
/// by DownloadEndpointTest; here we pin the page's rendered states and the User-Agent sniff.
///
/// NonParallelizable: the second case boots its own WebApplicationFactory host, heavy enough to
/// disturb the shared-host fixture if run concurrently with the rest of the namespace.
/// </summary>
[NonParallelizable]
public class DownloadPageTest
{
    private const string windows_file = "typebeat-win-x64.zip";
    private const string linux_file = "typebeat-linux-x86_64.AppImage";
    private const string macos_file = "typebeat-macos.pkg";

    [Test]
    public async Task Download_WithoutConfiguredBuilds_ShowsComingSoon()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/download");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("coming soon"));
            // No live download links when nothing is configured/stored.
            Assert.That(html, Does.Not.Contain("href=\"/download/game\""));
            Assert.That(html, Does.Not.Contain("href=\"/download/game-linux\""));
            Assert.That(html, Does.Not.Contain("href=\"/download/game-macos\""));
        });
    }

    [Test]
    public async Task Download_WithStoredBuilds_ShowsAllPlatformCards()
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-download-page-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        // Distinct sizes so each card's caption is meaningful (1.5 MB / 3.0 MB / 6.0 MB).
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", windows_file), new byte[1_572_864]);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", linux_file), new byte[3_145_728]);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", macos_file), new byte[6_291_456]);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD", windows_file);
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD_LINUX", linux_file);
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD_MACOS", macos_file);
            builder.UseSetting("TYPEBEAT_FILE_ROOT", root);
        });

        try
        {
            using var client = factory.CreateDefaultClient(WebsiteFixture.BaseAddress, new RedirectHandler());

            using var page = await client.GetAsync("/download");
            string html = await page.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(html, Does.Not.Contain("coming soon"));

                // Windows card: download link, file name, size, requirement.
                Assert.That(html, Does.Contain("href=\"/download/game\""));
                Assert.That(html, Does.Contain(windows_file));
                Assert.That(html, Does.Contain("1.5 MB"));
                // The "+" in "Windows 10+" is HTML-encoded (&#x2B;), so match the stable prefix.
                Assert.That(html, Does.Contain("Windows 10"));

                // Linux card: its own route, file name, size.
                Assert.That(html, Does.Contain("href=\"/download/game-linux\""));
                Assert.That(html, Does.Contain(linux_file));
                Assert.That(html, Does.Contain("3 MB"));

                // macOS card: its own route, file name, size, requirement.
                Assert.That(html, Does.Contain("href=\"/download/game-macos\""));
                Assert.That(html, Does.Contain(macos_file));
                Assert.That(html, Does.Contain("6 MB"));
                Assert.That(html, Does.Contain("Apple Silicon"));

                // Card order IS the L-shaped layout (two-column grid, macOS last so it lands under
                // Windows). Pin it: a reorder silently reshapes the page.
                Assert.That(html.IndexOf("/download/game-linux", StringComparison.Ordinal),
                    Is.LessThan(html.IndexOf("/download/game-macos", StringComparison.Ordinal)));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// DownloadModel.DetectOs only picks which card wears the "Your system" badge, but getting it
    /// wrong is user-visible on every visit, and the mobile exclusions are the easy thing to break:
    /// Android UAs contain "Linux", and iPhone/iPad UAs contain "Mac OS X". Neither has a build.
    /// </summary>
    [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36", "windows")]
    [TestCase("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15", "macos")]
    [TestCase("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36", "macos")]
    [TestCase("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:127.0) Gecko/20100101 Firefox/127.0", "macos")]
    [TestCase("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36", "linux")]
    [TestCase("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0", "linux")]
    // iOS: "like Mac OS X" must not be read as a Mac, there is no iOS build.
    [TestCase("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", null)]
    [TestCase("Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", null)]
    [TestCase("Mozilla/5.0 (iPod touch; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148", null)]
    // Android: "Linux" must not be read as desktop Linux, same reason.
    [TestCase("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36", null)]
    [TestCase("", null)]
    public void DetectOs_ClassifiesRealUserAgents(string userAgent, string? expected)
    {
        Assert.That(DownloadModel.DetectOs(userAgent), Is.EqualTo(expected));
    }

    /// <summary>
    /// Known, unfixable gap, pinned so it is a deliberate choice rather than a surprise: an iPad in
    /// "Request Desktop Website" mode sends a UA byte-identical to a Mac's (no "iPad" token at all),
    /// so it is reported as macOS. Nothing server-side can distinguish the two.
    /// </summary>
    [Test]
    public void DetectOs_IpadInDesktopMode_IsIndistinguishableFromAMac()
    {
        const string ipad_desktop_ua =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15";

        Assert.That(DownloadModel.DetectOs(ipad_desktop_ua), Is.EqualTo("macos"));
    }
}
