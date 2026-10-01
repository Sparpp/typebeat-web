using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// GET /releases/{file}, the Velopack feed, on the shared host, which has no TYPEBEAT_R2_* keys
/// and so carries the REAL disabled public store (backlog 364). This is the feature-off pin for the
/// feed: packages stream with a day of caching and manifests with no-cache, exactly as before the
/// bucket existed, and nothing redirects. The enabled behaviour is PublicObjectStoreTest's.
/// </summary>
public class ReleaseFeedTest
{
    private const string nupkg = "typebeat-feedtest-9.9.9-full.nupkg";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var store = WebsiteFixture.Services.GetRequiredService<IFileStore>();

        await store.WriteObjectAsync(StoreKeys.Release(nupkg), new MemoryStream(Encoding.UTF8.GetBytes("package bytes")));
        await store.WriteObjectAsync(StoreKeys.Release("releases.feedtest.json"), new MemoryStream(Encoding.UTF8.GetBytes("{}")));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        var store = WebsiteFixture.Services.GetRequiredService<IFileStore>();

        await store.DeleteObjectAsync(StoreKeys.Release(nupkg));
        await store.DeleteObjectAsync(StoreKeys.Release("releases.feedtest.json"));
    }

    [Test]
    public void UnconfiguredHost_RegistersTheDisabledStore()
    {
        var publicStore = WebsiteFixture.Services.GetRequiredService<IPublicObjectStore>();

        Assert.Multiple(() =>
        {
            Assert.That(publicStore, Is.InstanceOf<DisabledPublicObjectStore>());
            Assert.That(publicStore.Enabled, Is.False);
            Assert.That(publicStore.Description, Does.StartWith("disabled (not configured)"));
        });
    }

    [Test]
    public async Task Nupkg_StreamsFromTheBox_WithADayOfCaching()
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync($"/releases/{nupkg}");

        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=86400"));
            Assert.That(response.Headers.AcceptRanges, Does.Contain("bytes"));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("package bytes"));
        });
    }

    [Test]
    public async Task Manifest_IsNoCache()
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync("/releases/releases.feedtest.json?arch=x64&localVersion=1.0.0");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("no-cache"));
        });
    }

    [TestCase("/releases/missing-1.0.0-full.nupkg")]
    [TestCase("/releases/a..b.nupkg")]
    public async Task MissingOrEscaping_Is404(string path)
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
