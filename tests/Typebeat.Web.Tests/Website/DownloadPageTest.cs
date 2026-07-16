using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// /download page states. The main fixture host has no TYPEBEAT_GAME_DOWNLOAD configured →
/// "coming soon"; a second host with the key + a real file in {TYPEBEAT_FILE_ROOT}/downloads
/// exercises the button + the /download/game streaming handler.
/// </summary>
public class DownloadPageTest
{
    private const string file_name = "typebeat-win-x64.zip";

    [Test]
    public async Task Download_WithoutConfiguredFile_ShowsComingSoon()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/download");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("coming soon"));
            Assert.That(html, Does.Contain("href=\"/register\""));
            Assert.That(html, Does.Not.Contain("/download/game"));
        });
    }

    [Test]
    public async Task DownloadGame_WithoutConfiguredFile_Is404()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/download/game");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Download_WithStoredFile_ShowsButton_AndStreamsTheZip()
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        // 1.5 MB of deterministic bytes — enough to make the size caption meaningful.
        byte[] payload = new byte[1_572_864];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", file_name), payload);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD", file_name);
            builder.UseSetting("TYPEBEAT_FILE_ROOT", root);
        });

        try
        {
            using var client = factory.CreateDefaultClient(WebsiteFixture.BaseAddress, new RedirectHandler());

            using (var page = await client.GetAsync("/download"))
            {
                string html = await page.Content.ReadAsStringAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(html, Does.Contain("href=\"/download/game\""));
                    Assert.That(html, Does.Contain(file_name));
                    Assert.That(html, Does.Contain("1.5 MB"));
                    Assert.That(html, Does.Contain("Windows 10+"));
                    Assert.That(html, Does.Not.Contain("coming soon"));
                });
            }

            using (var download = await client.GetAsync("/download/game"))
            {
                byte[] body = await download.Content.ReadAsByteArrayAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(download.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
                    Assert.That(download.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
                    Assert.That(download.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo(file_name));
                    Assert.That(download.Content.Headers.ContentLength, Is.EqualTo(payload.Length));
                    Assert.That(body, Is.EqualTo(payload));
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
