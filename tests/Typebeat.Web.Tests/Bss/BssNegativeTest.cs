using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// BSS error contract: 422 {"error"} for invariant violations (unverified account, bad archive,
/// foreign keep-ids, oversized package), 403 for non-owners, 404 for missing sets — the exact
/// codes and envelope the client's per-stage failure handlers surface.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssNegativeTest
{
    private long ownerId;
    private string ownerBearer = null!;
    private string strangerBearer = null!;
    private string unverifiedBearer = null!;

    private long setId;
    private long beatmapId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (ownerId, ownerBearer) = await BssFixture.CreateUserAsync("negative owner", verified: true);
        (_, strangerBearer) = await BssFixture.CreateUserAsync("negative stranger", verified: true);
        (_, unverifiedBearer) = await BssFixture.CreateUserAsync("negative unverified", verified: false);

        // A set owned by "negative owner" with one allocated (blank) diff, seeded directly.
        await using var conn = await BssFixture.OpenDbAsync();

        setId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status) VALUES (@ownerId, 'hidden') RETURNING id",
            new { ownerId });

        beatmapId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@setId, 'feedfacefeedfacefeedfacefeedface') RETURNING id",
            new { setId });
    }

    [Test]
    public async Task UnverifiedUser_Gets422_WithAClearMessage()
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, "/bss/beatmapsets", unverifiedBearer,
            BssSubmissionFlowTest.JsonBody(new { beatmapset_id = (long?)null, beatmaps_to_create = 1, beatmaps_to_keep = Array.Empty<long>(), target = "WIP", notify_on_discussion_replies = false }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        string error = (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!;
        Assert.That(error, Does.Contain("verif"), "the message must tell the user verification is required");
    }

    [Test]
    public async Task NonOwner_Gets403_OnEveryRoute()
    {
        using (var put = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Put, "/bss/beatmapsets", strangerBearer,
                   BssSubmissionFlowTest.JsonBody(new { beatmapset_id = setId, beatmaps_to_create = 1, beatmaps_to_keep = Array.Empty<long>(), target = "WIP", notify_on_discussion_replies = false })))
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using (var upload = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Put, $"/bss/beatmapsets/{setId}", strangerBearer,
                   BssSubmissionFlowTest.PackageBody([1, 2, 3])))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using (var patch = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Patch, $"/bss/beatmapsets/{setId}", strangerBearer,
                   new MultipartFormDataContent { { new StringContent("x.txt"), "filesDeleted" } }))
            Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task MissingSet_Gets404()
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, "/bss/beatmapsets", ownerBearer,
            BssSubmissionFlowTest.JsonBody(new { beatmapset_id = 99_999_999L, beatmaps_to_create = 1, beatmaps_to_keep = Array.Empty<long>(), target = "WIP", notify_on_discussion_replies = false }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ForeignKeepIds_Get422()
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, "/bss/beatmapsets", ownerBearer,
            BssSubmissionFlowTest.JsonBody(new { beatmapset_id = setId, beatmaps_to_create = 0, beatmaps_to_keep = new[] { beatmapId, 88_888_888L }, target = "WIP", notify_on_discussion_replies = false }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        string error = (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!;
        Assert.That(error, Does.Contain("88888888"));
    }

    [Test]
    public async Task BadArchive_Gets422()
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", ownerBearer,
            BssSubmissionFlowTest.PackageBody(SyntheticPackage.Utf8("this is not a zip")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        string error = (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!;
        Assert.That(error, Does.Contain("zip"));
    }

    [Test]
    public async Task OversizedPackage_Gets422()
    {
        // Incompressible content pushes the package stream over PackageValidator's 95 MiB cap
        // (the ~100 MB Kestrel per-endpoint cap is transport-level and absent under TestServer).
        byte[] noise = new byte[PackageValidator.MaxPackageBytes + 1024 * 1024];
        Random.Shared.NextBytes(noise);

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(("noise.bin", noise)))
            zipBytes = zip.ToArray();

        Assert.That(zipBytes, Has.Length.GreaterThan(PackageValidator.MaxPackageBytes));

        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", ownerBearer,
            BssSubmissionFlowTest.PackageBody(zipBytes));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        string error = (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!;
        Assert.That(error, Does.Contain("size limit"));
    }

    [Test]
    public async Task HiddenSet_Is404_ForOthers_AndWipForTheOwner()
    {
        using (var stranger = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/api/v2/beatmapsets/{setId}", strangerBearer))
            Assert.That(stranger.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var anonymous = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{setId}"))
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var owner = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/api/v2/beatmapsets/{setId}", ownerBearer))
        {
            Assert.That(owner.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var body = JObject.Parse(await owner.Content.ReadAsStringAsync());
            Assert.That((string)body["status"]!, Is.EqualTo("wip"), "hidden = pre-publish; the wizard preselects WIP from this");
        }
    }
}
