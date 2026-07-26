using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// The submission wizard's explicit-content toggle on PUT /bss/beatmapsets: the optional
/// <c>"explicit"</c> boolean is stored on beatmapsets.explicit, defaults to false when the key
/// is ABSENT (every client that predates the toggle), is re-applied on each re-submission so the
/// creator can flip it either way, and is echoed back additively by
/// GET /api/v2/beatmapsets/{id} so the wizard can preselect it.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssExplicitFlagTest
{
    private string bearer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_, bearer) = await BssFixture.CreateUserAsync("explicit submitter", verified: true);
    }

    [Test]
    public async Task CreateSet_WithExplicitTrue_StoresTheFlag()
    {
        long setId = await CreateSetAsync(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            @explicit = true,
            notify_on_discussion_replies = false,
        });

        Assert.That(await ExplicitAsync(setId), Is.True);
    }

    [Test]
    public async Task CreateSet_WithFieldAbsent_DefaultsToFalse()
    {
        // The exact body an old client sends: no "explicit" key at all.
        long setId = await CreateSetAsync(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "WIP",
            notify_on_discussion_replies = false,
        });

        Assert.That(await ExplicitAsync(setId), Is.False);
    }

    [Test]
    public async Task Resubmission_FlipsTheFlagBothWays()
    {
        long setId = await CreateSetAsync(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            @explicit = false,
            notify_on_discussion_replies = false,
        });

        Assert.That(await ExplicitAsync(setId), Is.False);

        // Re-target the same set with the toggle ON.
        await CreateSetAsync(new
        {
            beatmapset_id = (long?)setId,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            @explicit = true,
            notify_on_discussion_replies = false,
        });

        Assert.That(await ExplicitAsync(setId), Is.True, "an update must be able to turn the flag on");

        // ...and OFF again: the flag is not one-way, and an old client's omitted key clears it.
        await CreateSetAsync(new
        {
            beatmapset_id = (long?)setId,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            notify_on_discussion_replies = false,
        });

        Assert.That(await ExplicitAsync(setId), Is.False, "an update must be able to turn the flag off");
    }

    [Test]
    public async Task BeatmapsetGet_EchoesTheFlag_Additively()
    {
        long setId = await CreateSetAsync(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "Pending",
            @explicit = true,
            notify_on_discussion_replies = false,
        });

        // The set is still 'hidden' (no package uploaded yet), so only its owner may read it.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmapsets/{setId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await BssFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That((bool)body["explicit"]!, Is.True);
            // Nothing the client already binds moved or vanished.
            Assert.That((long)body["id"]!, Is.EqualTo(setId));
            Assert.That(body["status"], Is.Not.Null);
            Assert.That(body["covers"], Is.Not.Null);
        });
    }

    /// <summary>PUT /bss/beatmapsets with the given body; asserts 200 and returns the set id.</summary>
    private async Task<long> CreateSetAsync(object payload)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(payload));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        return (long)JObject.Parse(await response.Content.ReadAsStringAsync())["beatmapset_id"]!;
    }

    private static async Task<bool> ExplicitAsync(long setId)
    {
        await using var conn = await BssFixture.OpenDbAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT explicit FROM beatmapsets WHERE id = @setId", new { setId });
    }
}
