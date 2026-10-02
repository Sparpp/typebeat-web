using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;
using static Typebeat.Web.Tests.Bss.BssSubmissionFlowTest;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Backlog 364, DUAL-HOME: the three big-byte routes 302 to the public bucket on a Cloudflare host
/// (here "localhost", which is not a direct host) and keep streaming from the box on the direct
/// bss.* hosts; the ingest copies each committed package to the bucket under a content-unique key;
/// the prune deletes there too; the backfill fills in what predates the bucket.
///
/// <para>Runs on the BSS host, whose <see cref="FakePublicObjectStore"/> is disabled for every other
/// test: each test here switches it on in <see cref="SetUp"/> and <see cref="TearDown"/> puts it
/// back, so the rest of the namespace keeps seeing an unconfigured deployment.</para>
/// </summary>
[NonParallelizable]
public class PublicObjectStoreTest
{
    private const string username = "r2 uploader";
    private const string direct_host = "http://bss.typebeat.sh";

    private static FakePublicObjectStore bucket => BssFixture.PublicStore;

    private long userId;
    private string bearer = null!;

    /// <summary>A published set whose v1 was uploaded with the bucket on (so it has a public key).</summary>
    private long setId;
    private string publicKey = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (userId, bearer) = await BssFixture.CreateUserAsync(username, verified: true);

        bucket.Reset();
        bucket.Enabled = true;

        try
        {
            long diffId;
            (setId, diffId) = await createSetAsync();
            await uploadAsync(setId, diffId, "Bucket Anthem", seconds: 1);
            publicKey = (await publicKeyAsync(setId, 1))!;
        }
        finally
        {
            bucket.Reset();
        }
    }

    [SetUp]
    public void SetUp()
    {
        bucket.Reset();
        bucket.Enabled = true;
        installers.ClearCache();
    }

    [TearDown]
    public void TearDown()
    {
        bucket.Reset();
        installers.ClearCache();
    }

    private static GameInstallers installers => BssFixture.Services.GetRequiredService<GameInstallers>();

    // ---------------------------------------------------------------------------------------------
    // Ingest: the PUT after the commit.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task NewVersion_IsCopiedAfterTheCommit_UnderAContentUniqueKey()
    {
        var (newSetId, diffId) = await createSetAsync();

        int committedRowsAtPut = -1;
        bucket.OnPut = async _ =>
        {
            // A second connection only sees the version once the ingest transaction committed.
            await using var conn = await BssFixture.OpenDbAsync();
            committedRowsAtPut = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM set_versions WHERE set_id = @newSetId AND version_no = 1", new { newSetId });
        };

        await uploadAsync(newSetId, diffId, "Fresh Bucket", seconds: 1);

        byte[] local = await File.ReadAllBytesAsync(localPackagePath(newSetId, 1));
        string expectedKey = $"packages/{newSetId}/1-{Convert.ToHexStringLower(SHA256.HashData(local))[..16]}.typb";

        Assert.That(bucket.Objects.TryGetValue(expectedKey, out var stored), Is.True, string.Join(", ", bucket.Objects.Keys));

        Assert.Multiple(async () =>
        {
            Assert.That(committedRowsAtPut, Is.EqualTo(1), "the PUT ran after the version row was committed");
            Assert.That(await publicKeyAsync(newSetId, 1), Is.EqualTo(expectedKey));
            Assert.That(stored!.Bytes, Is.EqualTo(local), "the bucket holds exactly the local package");
            Assert.That(stored.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(stored.CacheControl, Is.EqualTo("public, max-age=86400"));
            Assert.That(stored.ContentDisposition, Is.EqualTo(
                "attachment; filename=\"Synth Rider - Fresh Bucket.typb\"; filename*=UTF-8''Synth%20Rider%20-%20Fresh%20Bucket.typb"));
        });
    }

    [Test]
    public async Task FailedUpload_LeavesTheKeyNull_AndTheIngestSucceeds()
    {
        var (newSetId, diffId) = await createSetAsync();
        bucket.FailPuts = true;

        byte[] zip = await uploadAsync(newSetId, diffId, "Unreachable Bucket", seconds: 1);

        Assert.Multiple(async () =>
        {
            Assert.That(await publicKeyAsync(newSetId, 1), Is.Null);
            Assert.That(bucket.Objects, Is.Empty);
        });

        // The download keeps streaming from the box for a NULL key, even on a Cloudflare host.
        using (var download = await BssFixture.Client.GetAsync($"/beatmapsets/{newSetId}/download"))
            Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // The mapper's identical resubmission repairs the missing copy without cutting a version.
        bucket.FailPuts = false;
        await putPackageAsync(newSetId, zip);

        await using var conn = await BssFixture.OpenDbAsync();
        int versions = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM set_versions WHERE set_id = @newSetId", new { newSetId });

        string? repaired = await publicKeyAsync(newSetId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(versions, Is.EqualTo(1), "identical content is not a new version");
            Assert.That(repaired, Does.StartWith($"packages/{newSetId}/1-"));
            Assert.That(bucket.Objects.Keys, Is.EquivalentTo(new[] { repaired }));
        });
    }

    [Test]
    public async Task RolledBackIngest_NeverReachesTheBucket()
    {
        var (newSetId, diffId) = await createSetAsync();

        // A files row whose size disagrees with the incoming entry of the same hash makes the
        // ingest throw INSIDE its transaction, after the blobs were written: a real rollback.
        byte[] poison = Encoding.UTF8.GetBytes("poison " + Guid.NewGuid().ToString("N"));

        await using (var conn = await BssFixture.OpenDbAsync())
        {
            await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, @size)",
                new { sha = SHA256.HashData(poison), size = poison.Length + 1 });
        }

        using var zip = SyntheticPackage.Zip(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                title: "Rolled Back", creator: username, beatmapId: diffId, beatmapSetId: newSetId))),
            ("audio.mp3", MakeWav(seconds: 1)),
            ("bg.jpg", SyntheticPackage.TinyPng()),
            ("extra/poison.txt", poison));

        var parsed = BeatmapPackageParser.Parse(zip);
        PackageValidator.Validate(parsed, newSetId, [diffId], username);

        var ingest = BssFixture.Services.GetRequiredService<PackageIngest>();

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var scope = await ingest.BeginSetScopeAsync(newSetId);
            await ingest.IngestAsync(scope, zip, parsed, newSetId, userId);
        });

        await using var check = await BssFixture.OpenDbAsync();
        int versions = await check.ExecuteScalarAsync<int>("SELECT count(*) FROM set_versions WHERE set_id = @newSetId", new { newSetId });

        Assert.Multiple(() =>
        {
            Assert.That(versions, Is.Zero, "the attempt rolled back");
            Assert.That(bucket.Log, Is.Empty, "and nothing of it reached the bucket");
        });
    }

    [Test]
    public async Task Prune_DeletesTheBucketCopies_BeyondTheLatestTwo()
    {
        var (newSetId, diffId) = await createSetAsync();

        await uploadAsync(newSetId, diffId, "Prune Me", seconds: 1);
        string? v1 = await publicKeyAsync(newSetId, 1);
        await uploadAsync(newSetId, diffId, "Prune Me", seconds: 2);
        string? v2 = await publicKeyAsync(newSetId, 2);
        await uploadAsync(newSetId, diffId, "Prune Me", seconds: 3);
        string? v3 = await publicKeyAsync(newSetId, 3);

        Assert.Multiple(async () =>
        {
            Assert.That(new[] { v1, v2, v3 }, Has.All.Not.Null);
            Assert.That(new[] { v1, v2, v3 }, Is.Unique);

            Assert.That(await publicKeyAsync(newSetId, 1), Is.Null, "the pruned version's column is cleared");
            Assert.That(bucket.Log, Does.Contain("DELETE " + v1));
            Assert.That(bucket.Objects.Keys, Is.EquivalentTo(new[] { v2, v3 }));
            Assert.That(await publicKeyAsync(newSetId, 2), Is.EqualTo(v2));
            Assert.That(await publicKeyAsync(newSetId, 3), Is.EqualTo(v3));
        });
    }

    [Test]
    public async Task Backfill_CopiesPublishedSetsOnly_AndIsIdempotent()
    {
        // Uploaded while the bucket was off: no public copy.
        bucket.Enabled = false;
        var (published, diffA) = await createSetAsync();
        await uploadAsync(published, diffA, "Backfill Published", seconds: 1);
        var (hidden, diffB) = await createSetAsync();
        await uploadAsync(hidden, diffB, "Backfill Hidden", seconds: 1);
        bucket.Enabled = true;

        await using (var conn = await BssFixture.OpenDbAsync())
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'hidden' WHERE id = @hidden", new { hidden });

        Assert.That(await publicKeyAsync(published, 1), Is.Null);

        var backfill = BssFixture.Services.GetServices<IHostedService>().OfType<PublicPackageBackfill>().Single();

        int first = await backfill.RunAsync();
        int putsAfterFirst = bucket.Log.Count;
        int second = await backfill.RunAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(first, Is.GreaterThanOrEqualTo(1));
            Assert.That(await publicKeyAsync(published, 1), Does.StartWith($"packages/{published}/1-"));
            Assert.That(await publicKeyAsync(hidden, 1), Is.Null, "a hidden set never goes to the CDN");
            Assert.That(second, Is.Zero, "a second pass has nothing left to do");
            Assert.That(bucket.Log.Count, Is.EqualTo(putsAfterFirst));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The package download.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task PublishedDownload_OnACloudflareHost_Redirects_AndCountsOnce()
    {
        int before = await downloadCountAsync(setId);

        using (var full = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download"))
            assertRedirect(full, publicKey);

        Assert.That(await downloadCountAsync(setId), Is.EqualTo(before + 1), "the 302 is issued after the counter");

        using (var api = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{setId}/download"))
            assertRedirect(api, publicKey);

        Assert.That(await downloadCountAsync(setId), Is.EqualTo(before + 2), "the game's alias counts too");

        using (var fromZero = new HttpRequestMessage(HttpMethod.Get, $"/beatmapsets/{setId}/download"))
        {
            fromZero.Headers.Range = new RangeHeaderValue(0, 99);
            using var response = await BssFixture.Client.SendAsync(fromZero);
            assertRedirect(response, publicKey);
        }

        Assert.That(await downloadCountAsync(setId), Is.EqualTo(before + 3), "a range from 0 is a download");

        using (var resume = new HttpRequestMessage(HttpMethod.Get, $"/beatmapsets/{setId}/download"))
        {
            resume.Headers.Range = new RangeHeaderValue(100, null);
            using var response = await BssFixture.Client.SendAsync(resume);
            assertRedirect(response, publicKey);
        }

        Assert.That(await downloadCountAsync(setId), Is.EqualTo(before + 3), "a mid-file range is a continuation");
    }

    [Test]
    public async Task PublishedDownload_OnADirectHost_StreamsFromTheBox()
    {
        byte[] local = await File.ReadAllBytesAsync(localPackagePath(setId, 1));

        foreach (string path in new[] { $"/beatmapsets/{setId}/download", $"/api/v2/beatmapsets/{setId}/download" })
        {
            using var response = await BssFixture.Client.GetAsync(direct_host + path);

            Assert.Multiple(async () =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(local), path);
            });
        }
    }

    [Test]
    public async Task AudioOnlyDownload_NeverRedirects()
    {
        foreach (string path in new[] { $"/beatmapsets/{setId}/download?noVideo=1", $"/api/v2/beatmapsets/{setId}/download?noVideo=1" })
        {
            using var response = await BssFixture.Client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        }
    }

    [Test]
    public async Task HiddenSet_OwnerStreamsLocally_AndAnyoneElseGets404()
    {
        await using var conn = await BssFixture.OpenDbAsync();
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'hidden' WHERE id = @setId", new { setId });

        try
        {
            using (var owned = await SendAsync(HttpMethod.Get, $"/beatmapsets/{setId}/download", bearer))
                Assert.That(owned.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the owner of a hidden set keeps the local stream");

            using (var anonymous = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download"))
                Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "the gate runs before any redirect");
        }
        finally
        {
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'pending' WHERE id = @setId", new { setId });
        }
    }

    [Test]
    public async Task NullPublicKey_StreamsLocally()
    {
        await using var conn = await BssFixture.OpenDbAsync();
        await conn.ExecuteAsync("UPDATE set_versions SET public_key = NULL WHERE set_id = @setId", new { setId });

        try
        {
            using var response = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            await conn.ExecuteAsync("UPDATE set_versions SET public_key = @publicKey WHERE set_id = @setId AND version_no = 1",
                new { publicKey, setId });
        }
    }

    [Test]
    public async Task BucketOff_AKeyInTheDatabase_NeverRedirects()
    {
        bucket.Enabled = false;

        using var response = await BssFixture.Client.GetAsync($"/beatmapsets/{setId}/download");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl, Is.Null, "the local stream's headers are unchanged");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The update feed.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task ReleaseFeed_NupkgRedirects_OnlyOnACloudflareHost_AndManifestsStayOnTheBox()
    {
        const string nupkg = "typebeat-r2test-1.2.3-full.nupkg";
        byte[] nupkgBytes = Encoding.UTF8.GetBytes("nupkg bytes");
        writeLocal(StoreKeys.Release(nupkg), nupkgBytes);
        writeLocal(StoreKeys.Release("RELEASES"), Encoding.UTF8.GetBytes("manifest"));
        writeLocal(StoreKeys.Release("releases.r2test.json"), Encoding.UTF8.GetBytes("{}"));
        writeLocal(StoreKeys.Release("bundled-r2test.typb"), Encoding.UTF8.GetBytes("bundled"));
        bucket.Seed(StoreKeys.Release(nupkg), nupkgBytes);

        using (var cf = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
            assertRedirect(cf, $"downloads/releases/{nupkg}");

        using (var direct = await BssFixture.Client.GetAsync($"{direct_host}/releases/{nupkg}"))
        {
            Assert.Multiple(async () =>
            {
                Assert.That(direct.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(direct.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=86400"));
                Assert.That(await direct.Content.ReadAsByteArrayAsync(), Is.EqualTo(nupkgBytes));
            });
        }

        // Velopack appends per-client query parameters to the manifest; it must still be the origin's.
        foreach (string manifest in new[] { "RELEASES", "releases.r2test.json?arch=x64&os=win&id=typebeat&localVersion=1.0.0" })
        {
            using var response = await BssFixture.Client.GetAsync($"/releases/{manifest}");

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), manifest);
                Assert.That(response.Headers.CacheControl?.NoCache, Is.True, manifest);
            });
        }

        using (var bundled = await BssFixture.Client.GetAsync("/releases/bundled-r2test.typb"))
            Assert.That(bundled.StatusCode, Is.EqualTo(HttpStatusCode.OK), "CI's bundled maps stay on the box");

        using (var escape = await BssFixture.Client.GetAsync("/releases/a..b.nupkg"))
            Assert.That(escape.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "the '..' guard runs before the redirect");

        bucket.Enabled = false;

        using (var off = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
        {
            Assert.Multiple(() =>
            {
                Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(off.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=86400"));
            });
        }
    }

    [Test]
    public async Task ReleaseFeed_ANupkgTheBucketLacks_StreamsFromTheBox_AndRedirectsOnceTheBucketHoldsIt()
    {
        // Backlog 380: the window between a ship landing on the box and the mirror uploading it.
        const string nupkg = "typebeat-r2test-1.2.4-delta.nupkg";
        byte[] nupkgBytes = Encoding.UTF8.GetBytes("delta bytes");
        writeLocal(StoreKeys.Release(nupkg), nupkgBytes);

        try
        {
            using (var notYet = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
            {
                Assert.Multiple(async () =>
                {
                    Assert.That(notYet.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a Cloudflare host streams what the bucket lacks");
                    Assert.That(notYet.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=86400"));
                    Assert.That(await notYet.Content.ReadAsByteArrayAsync(), Is.EqualTo(nupkgBytes));
                });
            }

            // The mirror's upload, and the stat cache entry it forgets after every PUT.
            bucket.Seed(StoreKeys.Release(nupkg), nupkgBytes);
            installers.Forget(StoreKeys.Release(nupkg));

            using (var mirrored = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
                assertRedirect(mirrored, $"downloads/releases/{nupkg}");

            using (var direct = await BssFixture.Client.GetAsync($"{direct_host}/releases/{nupkg}"))
                Assert.That(direct.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the direct hosts still stream");

            // A bucket object the box no longer has still redirects (the bucket holds it); a
            // package in neither is a plain 404 on either host.
            File.Delete(localPath(StoreKeys.Release(nupkg)));

            using (var bucketOnly = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
                assertRedirect(bucketOnly, $"downloads/releases/{nupkg}");

            bucket.Objects.Clear();
            installers.ClearCache();

            using (var neither = await BssFixture.Client.GetAsync($"/releases/{nupkg}"))
                Assert.That(neither.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            File.Delete(localPath(StoreKeys.Release(nupkg)));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The installers.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Installer_RedirectsWhenTheBucketHasIt_AndOtherwiseStreamsFromTheBox()
    {
        string key = StoreKeys.Download(BssFixture.InstallerFileName);
        byte[] localBytes = new byte[1_572_864];
        writeLocal(key, localBytes);

        try
        {
            // Bucket on but without the installer: the box's copy still serves.
            using (var notInBucket = await BssFixture.Client.GetAsync("/download/game"))
                Assert.That(notInBucket.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            bucket.Seed(key, new byte[3_145_728]);
            installers.ClearCache();

            using (var cf = await BssFixture.Client.GetAsync("/download/game"))
                assertRedirect(cf, key);

            using (var direct = await BssFixture.Client.GetAsync($"{direct_host}/download/game"))
            {
                Assert.Multiple(async () =>
                {
                    Assert.That(direct.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That((await direct.Content.ReadAsByteArrayAsync()).Length, Is.EqualTo(localBytes.Length));
                });
            }

            using (var unset = await BssFixture.Client.GetAsync("/download/game-linux"))
                Assert.That(unset.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "an unconfigured platform still 404s");

            // The page sizes the card from the bucket's copy (3 MB), not the box's (1.5 MB).
            using (var page = await BssFixture.Client.GetAsync("/download"))
            {
                string html = await page.Content.ReadAsStringAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(html, Does.Contain("href=\"/download/game\""));
                    Assert.That(html, Does.Contain("3 MB"));
                    Assert.That(html, Does.Not.Contain("1.5 MB"));
                });
            }
        }
        finally
        {
            File.Delete(localPath(key));
        }

        // Only the bucket holds it now: still a redirect.
        using (var bucketOnly = await BssFixture.Client.GetAsync("/download/game"))
            assertRedirect(bucketOnly, key);

        bucket.Objects.Clear();
        installers.ClearCache();

        using (var neither = await BssFixture.Client.GetAsync("/download/game"))
            Assert.That(neither.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static void assertRedirect(HttpResponseMessage response, string key)
    {
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(response.Headers.Location?.ToString(), Is.EqualTo($"{FakePublicObjectStore.PublicBaseUrl}/{key}"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True, "a cached 302 would skip the gate and the counter");
        });
    }

    private async Task<(long SetId, long DiffId)> createSetAsync()
    {
        using var create = await SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            notify_on_discussion_replies = false,
        }));

        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await create.Content.ReadAsStringAsync());
        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).First());
    }

    /// <summary>A full upload; a different <paramref name="seconds"/> makes different content (a new version).</summary>
    private async Task<byte[]> uploadAsync(long targetSetId, long diffId, string title, int seconds)
    {
        byte[] zip;

        using (var stream = SyntheticPackage.Zip(
                   ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                       title: title, titleUnicode: title, creator: username, beatmapId: diffId, beatmapSetId: targetSetId))),
                   ("audio.mp3", MakeWav(seconds)),
                   ("bg.jpg", SyntheticPackage.TinyPng())))
            zip = stream.ToArray();

        await putPackageAsync(targetSetId, zip);
        return zip;
    }

    private async Task putPackageAsync(long targetSetId, byte[] zip)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{targetSetId}", bearer, PackageBody(zip));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> publicKeyAsync(long targetSetId, int versionNo)
    {
        await using var conn = await BssFixture.OpenDbAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT public_key FROM set_versions WHERE set_id = @targetSetId AND version_no = @versionNo",
            new { targetSetId, versionNo });
    }

    private static async Task<int> downloadCountAsync(long targetSetId)
    {
        await using var conn = await BssFixture.OpenDbAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT download_count FROM beatmapsets WHERE id = @targetSetId", new { targetSetId });
    }

    private static string localPath(string key) => Path.Combine(BssFixture.FileRoot, key.Replace('/', Path.DirectorySeparatorChar));

    private static string localPackagePath(long targetSetId, int versionNo) => localPath(StoreKeys.Package(targetSetId, versionNo));

    private static void writeLocal(string key, byte[] bytes)
    {
        string path = localPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
