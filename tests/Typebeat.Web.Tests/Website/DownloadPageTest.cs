using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The /download page (restored after the "remove fluff" deletion). The shared fixture host has
/// neither TYPEBEAT_GAME_DOWNLOAD nor TYPEBEAT_GAME_DOWNLOAD_LINUX configured ⇒ the "coming soon"
/// state. A second host with both keys + real files under {TYPEBEAT_FILE_ROOT}/downloads exercises
/// the per-platform Windows + Linux cards and their download hrefs.
///
/// The byte-stream itself (/download/game, /download/game-linux) is covered by DownloadEndpointTest;
/// here we pin the page's rendered states.
///
/// NonParallelizable: the second case boots its own WebApplicationFactory host, heavy enough to
/// disturb the shared-host fixture if run concurrently with the rest of the namespace.
/// </summary>
[NonParallelizable]
public class DownloadPageTest
{
    private const string windows_file = "typebeat-win-x64.zip";
    private const string linux_file = "typebeat-linux-x86_64.AppImage";

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
        });
    }

    [Test]
    public async Task Download_WithStoredBuilds_ShowsBothPlatformCards()
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-download-page-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        // Distinct sizes so each card's caption is meaningful (1.5 MB / 3.0 MB).
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", windows_file), new byte[1_572_864]);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", linux_file), new byte[3_145_728]);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD", windows_file);
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD_LINUX", linux_file);
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
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
