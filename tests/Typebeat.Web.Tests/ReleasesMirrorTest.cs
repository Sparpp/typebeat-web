using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 380: the box mirrors /data/downloads to the public bucket itself. Each test drives
/// <see cref="ReleasesMirror.SweepAsync"/> directly over a temp file root and a
/// <see cref="FakePublicObjectStore"/>, with no host (the service itself returns at once on every
/// test host, whose store is disabled at boot).
/// </summary>
public class ReleasesMirrorTest
{
    private const string installer = "typebeat-win-Setup.exe";
    private const string nupkg = "typebeat-1.2.3-full.nupkg";

    private string root = null!;
    private FakePublicObjectStore bucket = null!;
    private ReleasesMirrorStatus status = null!;
    private ReleasesMirror mirror = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "typebeat-mirror-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "downloads", "releases"));

        bucket = new FakePublicObjectStore { Enabled = true };
        status = new ReleasesMirrorStatus();
        mirror = build(attempts: 3);
    }

    [TearDown]
    public void TearDown()
    {
        mirror.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private ReleasesMirror build(int attempts)
        => new(bucket, new ReleasesMirrorOptions(root, [installer]) { Attempts = attempts, RetryDelay = TimeSpan.Zero },
            status, NullLogger<ReleasesMirror>.Instance);

    private string release(string name) => Path.Combine(root, "downloads", "releases", name);

    private string download(string name) => Path.Combine(root, "downloads", name);

    private static void write(string path, string content) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

    [Test]
    public async Task NewFiles_AreUploaded_WithTheirKeyTypeAndCacheControl_AndManifestsAndTempsAreNot()
    {
        write(release(nupkg), "full package");
        write(release("typebeat-1.2.3-delta.nupkg"), "delta package");
        write(release("RELEASES"), "manifest");
        write(release("releases.win.json"), "{}");
        write(release("assets.win.json"), "{}");
        write(release(".typebeat-1.2.4-full.nupkg.tmp"), "half a ship");
        write(release(".incoming-typebeat-1.2.4-delta.nupkg"), "half a CI ship");
        write(download(installer), "installer");
        write(download("unconfigured.exe"), "not an installer this box advertises");

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Objects.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[]
            {
                "downloads/releases/typebeat-1.2.3-delta.nupkg",
                "downloads/releases/" + nupkg,
                "downloads/" + installer,
            }.Order(StringComparer.Ordinal)));

            var package = bucket.Objects["downloads/releases/" + nupkg];
            Assert.That(Encoding.UTF8.GetString(package.Bytes), Is.EqualTo("full package"));
            Assert.That(package.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(package.CacheControl, Is.EqualTo("public, max-age=86400"));
            Assert.That(package.ContentDisposition, Is.Null);

            var setup = bucket.Objects["downloads/" + installer];
            Assert.That(setup.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(setup.CacheControl, Is.EqualTo("no-cache"));
            Assert.That(setup.ContentDisposition, Is.EqualTo(PublicObjectMetadata.AttachmentDisposition(installer)));

            Assert.That(sweep.Uploaded, Is.EqualTo(3));
            Assert.That(sweep.Failed, Is.Zero);
            Assert.That(status.LastSweep, Is.SameAs(sweep));
        });

        // Nothing changed: the second sweep puts nothing.
        bucket.Log.Clear();
        var again = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log, Is.Empty);
            Assert.That(again.Uploaded, Is.Zero);
            Assert.That(again.Unchanged, Is.EqualTo(3));
            Assert.That(again.Unverified, Is.Zero, "every object the mirror wrote has a content ETag");
        });
    }

    [Test]
    public async Task ASameSizeRewrite_IsReuploaded()
    {
        write(download(installer), "installer AAAA");
        await mirror.SweepAsync();

        // Same length, different bytes: only the content comparison can see it.
        write(download(installer), "installer BBBB");
        File.SetLastWriteTimeUtc(download(installer), DateTime.UtcNow.AddMinutes(1));
        bucket.Log.Clear();

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log, Is.EqualTo(new[] { "PUT downloads/" + installer }));
            Assert.That(Encoding.UTF8.GetString(bucket.Objects["downloads/" + installer].Bytes), Is.EqualTo("installer BBBB"));
            Assert.That(sweep.Uploaded, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AMultipartObject_OfTheSameSize_IsAcceptedUnverified_AndADifferentSizeIsReplaced()
    {
        // What 364's CLI and rclone legs left behind: an ETag that is not the content's MD5.
        write(release(nupkg), "box bytes");
        write(release("typebeat-1.2.3-delta.nupkg"), "delta");
        bucket.Seed("downloads/releases/" + nupkg, Encoding.UTF8.GetBytes("old bytes"), "public, max-age=86400", "0123456789abcdef0123456789abcdef-3");
        bucket.Seed("downloads/releases/typebeat-1.2.3-delta.nupkg", Encoding.UTF8.GetBytes("longer delta"), "public, max-age=86400", "0123456789abcdef0123456789abcdef-2");

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log, Is.EqualTo(new[] { "PUT downloads/releases/typebeat-1.2.3-delta.nupkg" }));
            Assert.That(sweep.Unverified, Is.EqualTo(1));
            Assert.That(sweep.Uploaded, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ADeletedBoxFile_DeletesItsObject()
    {
        write(release(nupkg), "full package");
        write(release("typebeat-1.2.2-full.nupkg"), "older package");
        write(download(installer), "installer");
        await mirror.SweepAsync();

        // The box prune removes an old package; the installer goes too.
        File.Delete(release("typebeat-1.2.2-full.nupkg"));
        File.Delete(download(installer));
        // An object the box never had under downloads/ (a renamed installer, a stale manifest) goes as well.
        bucket.Seed("downloads/releases/typebeat-1.0.0-full.nupkg", [1, 2, 3]);
        bucket.Log.Clear();

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log.Order(StringComparer.Ordinal), Is.EqualTo(new[]
            {
                "DELETE downloads/releases/typebeat-1.0.0-full.nupkg",
                "DELETE downloads/releases/typebeat-1.2.2-full.nupkg",
                "DELETE downloads/" + installer,
            }.Order(StringComparer.Ordinal)));
            Assert.That(bucket.Objects.Keys, Is.EqualTo(new[] { "downloads/releases/" + nupkg }));
            Assert.That(sweep.Deleted, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task PackagesAndEverythingOutsideDownloads_AreNeverTouched()
    {
        write(release(nupkg), "full package");
        bucket.Seed("packages/12/3-0123456789abcdef.typb", [1, 2, 3]);
        bucket.Seed("downloadsx/look-alike.bin", [4]);

        await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Objects.ContainsKey("packages/12/3-0123456789abcdef.typb"), Is.True);
            Assert.That(bucket.Objects.ContainsKey("downloadsx/look-alike.bin"), Is.True);
            Assert.That(bucket.Log.Where(l => !l.Contains(" downloads/", StringComparison.Ordinal)), Is.Empty);
        });
    }

    [Test]
    public async Task AnEmptyBox_NeverEmptiesTheBucket()
    {
        // A file root that is unmounted or misconfigured looks exactly like this.
        Directory.Delete(Path.Combine(root, "downloads"), recursive: true);
        bucket.Seed("downloads/releases/" + nupkg, [1, 2, 3]);
        bucket.Seed("downloads/" + installer, [4]);

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log, Is.Empty);
            Assert.That(bucket.Objects, Has.Count.EqualTo(2));
            Assert.That(sweep.DeletionsSkipped, Is.True);
        });
    }

    [Test]
    public async Task AFailingPut_IsRetried_ThenReported_UntilItSucceeds()
    {
        write(release(nupkg), "full package");
        int attempts = 0;
        bucket.FailPuts = true;
        bucket.OnPut = _ =>
        {
            attempts++;
            return Task.CompletedTask;
        };

        var failed = await mirror.SweepAsync();
        var firstSince = status.Failing.Single().Since;

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(3), "three tries within the sweep");
            Assert.That(failed.Failed, Is.EqualTo(1));
            Assert.That(failed.Uploaded, Is.Zero);
            Assert.That(status.Failing.Single().Key, Is.EqualTo("downloads/releases/" + nupkg));
            Assert.That(status.Failing.Single().Operation, Is.EqualTo("upload"));
            Assert.That(status.Failing.Single().Attempts, Is.EqualTo(3));
            Assert.That(status.Failing.Single().Error, Does.Contain("fake bucket unreachable"));
        });

        // Still failing next sweep: it keeps its first-failure time, so the readout shows how long.
        await Task.Delay(20);
        await mirror.SweepAsync();
        Assert.That(status.Failing.Single().Since, Is.EqualTo(firstSince));

        var json = JObject.FromObject(OpsMirrorEndpoints.Project(true, status));

        Assert.Multiple(() =>
        {
            Assert.That((bool)json["enabled"]!, Is.True);
            Assert.That((int)json["lastSweep"]!["failed"]!, Is.EqualTo(1));
            Assert.That((string)json["failing"]![0]!["key"]!, Is.EqualTo("downloads/releases/" + nupkg));
            Assert.That((string)json["failing"]![0]!["operation"]!, Is.EqualTo("upload"));
        });

        bucket.FailPuts = false;
        var recovered = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(recovered.Uploaded, Is.EqualTo(1));
            Assert.That(status.Failing, Is.Empty, "a file that succeeded drops out of the report");
            Assert.That(bucket.Objects.ContainsKey("downloads/releases/" + nupkg), Is.True);
        });
    }

    [Test]
    public async Task ATransientFailure_IsRetriedWithinTheSweep_AndNotReported()
    {
        write(release(nupkg), "full package");
        int attempts = 0;
        bucket.FailPuts = true;
        bucket.OnPut = _ =>
        {
            if (++attempts == 2)
                bucket.FailPuts = false;
            return Task.CompletedTask;
        };

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(sweep.Uploaded, Is.EqualTo(1));
            Assert.That(sweep.Failed, Is.Zero);
            Assert.That(status.Failing, Is.Empty);
        });
    }

    [Test]
    public async Task ABucketThatCannotBeListed_FailsTheSweepVisibly_AndTouchesNothing()
    {
        write(release(nupkg), "full package");
        bucket.FailLists = true;

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sweep.Error, Does.StartWith("could not list the bucket"));
            Assert.That(bucket.Log, Is.Empty);
            Assert.That((string?)JObject.FromObject(OpsMirrorEndpoints.Project(true, status))["lastSweep"]!["error"], Does.StartWith("could not list"));
        });
    }

    [Test]
    public async Task ADisabledStore_DoesNothing()
    {
        write(release(nupkg), "full package");
        bucket.Enabled = false;

        var sweep = await mirror.SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bucket.Log, Is.Empty);
            Assert.That(sweep.Uploaded, Is.Zero);
        });
    }

    [Test]
    public void Options_TakeTheConfiguredInstallerNames_AndRefuseAnyPath()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalFileStore.RootConfigKey] = "/data",
            [GameDownloadKeys.Windows] = "typebeat-win-Setup.exe",
            [GameDownloadKeys.Linux] = "../escape.AppImage",
            [GameDownloadKeys.Macos] = "",
        }).Build();

        var options = ReleasesMirrorOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.FileRoot, Is.EqualTo("/data"));
            Assert.That(options.InstallerFileNames, Is.EqualTo(new[] { "typebeat-win-Setup.exe" }));
        });
    }

    [TestCase("RELEASES", true)]
    [TestCase("releases.win.json", true)]
    [TestCase("assets.osx.json", true)]
    [TestCase("typebeat-1.2.3-full.nupkg", false)]
    [TestCase("bundled-maps.typb", false)]
    public void IsManifest_IsTheBoxCachingRule(string file, bool manifest)
        => Assert.That(ReleasesMirror.IsManifest(file), Is.EqualTo(manifest));
}
