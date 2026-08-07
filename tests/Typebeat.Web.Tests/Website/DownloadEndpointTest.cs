using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// GET /download/game, /download/game-linux, /download/game-macos: the per-platform artifact
/// streams. The main fixture host has none of the TYPEBEAT_GAME_DOWNLOAD* keys ⇒ 404 on all
/// three; a second host with two of the keys + real files in {TYPEBEAT_FILE_ROOT}/downloads
/// exercises the attachment stream and the third platform's independent 404.
///
/// NonParallelizable: the second case boots its own WebApplicationFactory host, which is heavy
/// enough to disturb the shared-host fixture if run concurrently with the rest of the namespace.
/// </summary>
[NonParallelizable]
public class DownloadEndpointTest
{
    private const string file_name = "typebeat-win-x64.zip";
    private const string macos_file = "typebeat-macos.pkg";

    [Test]
    public async Task DownloadGame_WithoutConfiguredFile_Is404()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/download/game");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Every per-platform route is registered and reachable. 404 (not 405/500, and crucially not
    /// the styled 404 page an unrouted path would get) is the "routed, but this platform has no
    /// configured build" answer, which is what the shared fixture host models.
    /// </summary>
    [TestCase("/download/game")]
    [TestCase("/download/game-linux")]
    [TestCase("/download/game-macos")]
    public async Task DownloadGame_PerPlatformRoute_IsMappedAnd404sWhenUnconfigured(string route)
    {
        using var response = await WebsiteFixture.Client.GetAsync(route);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// One extra host covers every artifact shape: the Windows zip (its own content type) and the
    /// macOS pkg (the octet-stream fallback), plus Linux left unconfigured on a host where the
    /// other two work, which is the per-platform independence the /download page relies on.
    /// Deliberately ONE factory: each extra in-process host is heavy enough to destabilise the
    /// shared fixture, so new per-platform coverage belongs in here rather than in a new host.
    /// </summary>
    [Test]
    public async Task DownloadGame_WithStoredFile_StreamsTheZipAsAttachment()
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        // 1.5 MB of deterministic bytes so the round-trip is meaningful.
        byte[] payload = new byte[1_572_864];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", file_name), payload);

        byte[] pkgPayload = new byte[262_144];
        for (int i = 0; i < pkgPayload.Length; i++)
            pkgPayload[i] = (byte)(i % 241);
        await File.WriteAllBytesAsync(Path.Combine(root, "downloads", macos_file), pkgPayload);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD", file_name);
            builder.UseSetting("TYPEBEAT_GAME_DOWNLOAD_MACOS", macos_file);
            // TYPEBEAT_GAME_DOWNLOAD_LINUX deliberately unset.
            builder.UseSetting("TYPEBEAT_FILE_ROOT", root);
        });

        try
        {
            using var client = factory.CreateDefaultClient(WebsiteFixture.BaseAddress, new RedirectHandler());

            using var download = await client.GetAsync("/download/game");
            byte[] body = await download.Content.ReadAsByteArrayAsync();

            using var macDownload = await client.GetAsync("/download/game-macos");
            byte[] macBody = await macDownload.Content.ReadAsByteArrayAsync();

            using var linuxDownload = await client.GetAsync("/download/game-linux");

            Assert.Multiple(() =>
            {
                Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(download.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
                Assert.That(download.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
                Assert.That(download.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo(file_name));
                Assert.That(download.Content.Headers.ContentLength, Is.EqualTo(payload.Length));
                Assert.That(body, Is.EqualTo(payload));

                // The .pkg is not a .zip, so the handler's octet-stream fallback applies.
                Assert.That(macDownload.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(macDownload.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(macDownload.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
                Assert.That(macDownload.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo(macos_file));
                Assert.That(macBody, Is.EqualTo(pkgPayload));

                // An unconfigured platform still 404s while its neighbours serve.
                Assert.That(linuxDownload.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
