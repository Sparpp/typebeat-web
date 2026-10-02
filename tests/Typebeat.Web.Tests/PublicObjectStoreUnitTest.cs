using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Typebeat.Web.Pages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 364's pure pieces: when the public bucket counts as configured (anything short of all
/// five keys, or a non-https base, is OFF, which is what keeps an unconfigured deployment
/// byte-identical), the URL and metadata shapes it is fed, and the installer catalog over it.
/// </summary>
public class PublicObjectStoreUnitTest
{
    private static readonly Dictionary<string, string?> full_config = new()
    {
        [PublicObjectStoreOptions.EndpointKey] = "https://account.r2.cloudflarestorage.com",
        [PublicObjectStoreOptions.BucketKey] = "typebeat",
        [PublicObjectStoreOptions.AccessKeyIdKey] = "key-id",
        [PublicObjectStoreOptions.SecretAccessKeyKey] = "secret-value",
        [PublicObjectStoreOptions.PublicBaseUrlKey] = "https://dl.typebeat.sh/",
    };

    private static IConfiguration config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void NothingConfigured_IsTheDisabledStore()
    {
        var store = PublicObjectStores.FromConfiguration(config(new Dictionary<string, string?>()));

        Assert.Multiple(() =>
        {
            Assert.That(store, Is.InstanceOf<DisabledPublicObjectStore>());
            Assert.That(store.Enabled, Is.False);
            Assert.That(store.Description, Does.Contain("not configured"));
            Assert.That(store.IsDirectHost("typebeat.sh"), Is.True, "off: every host streams from the box");
        });
    }

    /// <summary>Compose declares every key with an empty default, so empty must read as unset.</summary>
    [Test]
    public void EmptyStrings_AreUnset()
    {
        var values = full_config.ToDictionary(p => p.Key, _ => (string?)"");
        var store = PublicObjectStores.FromConfiguration(config(values));

        Assert.That(store.Description, Does.Contain("not configured"));
    }

    [TestCase(PublicObjectStoreOptions.EndpointKey)]
    [TestCase(PublicObjectStoreOptions.BucketKey)]
    [TestCase(PublicObjectStoreOptions.AccessKeyIdKey)]
    [TestCase(PublicObjectStoreOptions.SecretAccessKeyKey)]
    [TestCase(PublicObjectStoreOptions.PublicBaseUrlKey)]
    public void AnyOneKeyMissing_IsDisabled_AndNamesIt(string missingKey)
    {
        var values = new Dictionary<string, string?>(full_config) { [missingKey] = null };
        var store = PublicObjectStores.FromConfiguration(config(values));

        Assert.Multiple(() =>
        {
            Assert.That(store, Is.InstanceOf<DisabledPublicObjectStore>());
            Assert.That(store.Description, Does.Contain("incomplete, missing " + missingKey));
            Assert.That(store.Description, Does.Not.Contain("secret-value"));
        });
    }

    [TestCase("http://dl.typebeat.sh")]
    [TestCase("dl.typebeat.sh")]
    public void NonHttpsBase_IsDisabled(string publicBase)
    {
        var values = new Dictionary<string, string?>(full_config) { [PublicObjectStoreOptions.PublicBaseUrlKey] = publicBase };
        var store = PublicObjectStores.FromConfiguration(config(values));

        Assert.Multiple(() =>
        {
            Assert.That(store, Is.InstanceOf<DisabledPublicObjectStore>());
            Assert.That(store.Description, Does.Contain("not an absolute https URL"));
        });
    }

    [Test]
    public void FullyConfigured_IsR2_WithTheDefaultDirectHosts()
    {
        var store = PublicObjectStores.FromConfiguration(config(full_config));

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(store, Is.InstanceOf<R2PublicObjectStore>());
                Assert.That(store.Enabled, Is.True);
                Assert.That(store.IsDirectHost("bss.typebeat.sh"), Is.True);
                Assert.That(store.IsDirectHost("BSS.typebeat.mingda.sh"), Is.True);
                Assert.That(store.IsDirectHost("typebeat.sh"), Is.False);
                Assert.That(store.IsDirectHost("typebeat.mingda.sh"), Is.False);
                Assert.That(store.PublicUrl("downloads/releases/a.nupkg"), Is.EqualTo("https://dl.typebeat.sh/downloads/releases/a.nupkg"));
                Assert.That(store.Description, Does.Not.Contain("secret-value").And.Not.Contain("key-id"));
            });
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void DirectHosts_CanBeOverridden()
    {
        var values = new Dictionary<string, string?>(full_config) { [PublicObjectStoreOptions.DirectHostsKey] = " origin.example , other.example " };
        var options = PublicObjectStoreOptions.FromConfiguration(config(values), out _)!;

        Assert.That(options.DirectHosts, Is.EquivalentTo(new[] { "origin.example", "other.example" }));
    }

    [Test]
    public void PublicUrl_EscapesEachSegment()
        => Assert.That(PublicObjectStoreOptions.BuildPublicUrl("https://dl.example/", "downloads/type!beat Setup #1.exe"),
            Is.EqualTo("https://dl.example/downloads/type%21beat%20Setup%20%231.exe"));

    [Test]
    public void AttachmentDisposition_CarriesAnAsciiFallbackAndTheUtf8Name()
        => Assert.That(PublicObjectMetadata.AttachmentDisposition("Röyksopp - \"Eple\".typb"),
            Is.EqualTo("attachment; filename=\"R_yksopp - _Eple_.typb\"; filename*=UTF-8''R%C3%B6yksopp%20-%20%22Eple%22.typb"));

    [Test]
    public void PublicPackageKey_IsTheVersionPlusSixteenHexOfTheHash()
    {
        byte[] sha = Enumerable.Range(0, 32).Select(i => (byte)(0xa0 + i % 16)).ToArray();

        Assert.That(StoreKeys.PublicPackage(42, 7, sha), Is.EqualTo("packages/42/7-a0a1a2a3a4a5a6a7.typb"));
    }

    [Test]
    public void DisabledStore_RefusesWrites()
    {
        var store = new DisabledPublicObjectStore();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync("k", new MemoryStream(), "x", "y", null));
            Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync("k"));
            Assert.Throws<InvalidOperationException>(() => store.PublicUrl("k"));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // GameInstallers: the /download page's and the landing CTA's view of the installers.
    // ---------------------------------------------------------------------------------------------

    private const string installer = "typebeat-Setup.exe";

    private static (GameInstallers Installers, FakePublicObjectStore Bucket, string Root) catalog(bool localCopy)
    {
        string root = Path.Combine(Path.GetTempPath(), "typebeat-installers-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));

        if (localCopy)
            File.WriteAllBytes(Path.Combine(root, "downloads", installer), new byte[1000]);

        var bucket = new FakePublicObjectStore();
        return (new GameInstallers(new LocalFileStore(root), bucket, NullLogger<GameInstallers>.Instance), bucket, root);
    }

    [Test]
    public async Task Installers_BucketOff_AreTheLocalStore()
    {
        var (installers, bucket, root) = catalog(localCopy: true);

        try
        {
            bucket.Seed(StoreKeys.Download(installer), new byte[5000]);

            Assert.Multiple(async () =>
            {
                Assert.That(await installers.SizeAsync(installer), Is.EqualTo(1000), "off: the bucket is never asked");
                Assert.That(await installers.PublicSizeAsync(installer), Is.Null);
                Assert.That(await installers.ExistsAsync(installer), Is.True);
                Assert.That(await installers.ExistsAsync("absent.exe"), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Installers_BucketOn_PreferTheBucket_ThenFallBackToTheBox()
    {
        var (installers, bucket, root) = catalog(localCopy: true);
        bucket.Enabled = true;

        try
        {
            Assert.That(await installers.SizeAsync(installer), Is.EqualTo(1000), "not in the bucket: the box answers");

            bucket.Seed(StoreKeys.Download(installer), new byte[5000]);
            Assert.That(await installers.SizeAsync(installer), Is.EqualTo(1000), "the absent answer is cached for a minute");

            installers.ClearCache();
            Assert.That(await installers.SizeAsync(installer), Is.EqualTo(5000), "in the bucket: its size wins");

            File.Delete(Path.Combine(root, "downloads", installer));
            Assert.That(await installers.ExistsAsync(installer), Is.True, "the bucket alone is enough for the CTA");

            var settings = config(new Dictionary<string, string?> { ["TYPEBEAT_GAME_DOWNLOAD"] = installer });
            Assert.That(await IndexModel.AnyGameDownloadAvailableAsync(settings, installers.ExistsAsync), Is.True);

            bucket.Objects.Clear();
            installers.ClearCache();
            Assert.That(await IndexModel.AnyGameDownloadAvailableAsync(settings, installers.ExistsAsync), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Installers_AFailingStat_FallsBackToTheBox_AndIsNotCached()
    {
        var (_, _, root) = catalog(localCopy: true);
        var throwing = new ThrowingStatStore();
        var installers = new GameInstallers(new LocalFileStore(root), throwing, NullLogger<GameInstallers>.Instance);

        try
        {
            Assert.That(await installers.SizeAsync(installer), Is.EqualTo(1000));
            Assert.That(await installers.SizeAsync(installer), Is.EqualTo(1000));
            Assert.That(throwing.Stats, Is.EqualTo(2), "a failure is retried on the next request, not cached");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ThrowingStatStore : IPublicObjectStore
    {
        public int Stats;

        public bool Enabled => true;
        public string Description => "throwing";
        public Task PutAsync(string key, Stream content, string contentType, string cacheControl, string? contentDisposition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

        public Task<long?> StatAsync(string key, CancellationToken ct = default)
        {
            Stats++;
            throw new IOException("bucket unreachable");
        }

        public Task<IReadOnlyList<PublicObjectInfo>> ListAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublicObjectInfo>>([]);

        public string PublicUrl(string key) => "https://dl.example/" + key;
        public bool IsDirectHost(string host) => false;
    }
}
