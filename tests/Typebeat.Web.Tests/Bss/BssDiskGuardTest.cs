using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Below the disk guard's upload floor (backlog 365) every BSS route that writes to the store
/// answers 507 Insufficient Storage before reading a body or spending a rate-limiter slot: the
/// upload-session create and the direct full upload are pinned here, on the EXISTING BSS host,
/// through the guard's <see cref="DiskGuard.ProbeOverride"/> seam. 507 rather than 503 because the
/// game retries 502/503/504 as a gateway blip; anything else fails the step with this message.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssDiskGuardTest
{
    private const long gib = 1024L * 1024 * 1024;

    private DiskGuard guard = null!;
    private string bearer = null!;
    private long setId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        guard = BssFixture.Services.GetRequiredService<DiskGuard>();
        (_, bearer) = await BssFixture.CreateUserAsync("disk guard mapper", verified: true);

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "WIP",
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "creating the set shell writes no file and is never refused");
        setId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["beatmapset_id"]!;
    }

    [TearDown]
    public void RestoreTheRealProbe() => guard.ProbeOverride = null;

    [OneTimeTearDown]
    public void OneTimeTearDown() => guard.ProbeOverride = null;

    [Test]
    public async Task SessionCreate_BelowTheFloor_Is507_AndOpensNoSession()
    {
        guard.ProbeOverride = _ => (75 * gib, 3 * gib);

        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Post, $"/bss/beatmapsets/{setId}/upload-sessions", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                kind = "full",
                content_type = "multipart/form-data; boundary=----typebeat-test-boundary",
                total_bytes = 20_000L,
                sha256 = new string('a', 64),
            }));

        string body = await response.Content.ReadAsStringAsync();
        string sessions = Path.Combine(BssFixture.FileRoot, "upload-sessions");
        bool anyForThisSet = Directory.Exists(sessions)
                             && Directory.EnumerateFiles(sessions, "meta.json", SearchOption.AllDirectories)
                                         .Any(f => File.ReadAllText(f).Contains($"\"set_id\":{setId}"));

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(507), body);
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("low on storage"));
            Assert.That(anyForThisSet, Is.False, "no session directory was created");
        });
    }

    [Test]
    public async Task FullUpload_BelowTheFloor_Is507_AndCutsNoVersion()
    {
        guard.ProbeOverride = _ => (75 * gib, 3 * gib);

        using var package = BssSubmissionFlowTest.PackageBody([1, 2, 3, 4]);
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, package);
        string body = await response.Content.ReadAsStringAsync();

        await using var conn = await BssFixture.OpenDbAsync();
        long versions = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(507), body);
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("low on storage"));
            Assert.That(versions, Is.Zero);
        });
    }

    [Test]
    public async Task AboveTheFloor_TheSameRequestReachesTheNormalValidation()
    {
        // The control: with room on the disk the same junk body is judged on its merits (a 422 for
        // a package that is not a zip), proving the 507 above came from the guard alone.
        guard.ProbeOverride = _ => (75 * gib, 50 * gib);

        using var package = BssSubmissionFlowTest.PackageBody([1, 2, 3, 4]);
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, package);

        Assert.That((int)response.StatusCode, Is.Not.EqualTo(507));
    }
}
