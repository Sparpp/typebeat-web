using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Backlog 476: the submission wizard's WIP target is its own server state. A set submitted as WIP
/// publishes to 'wip' (not 'pending'), the game API reports it as "wip" (the client's
/// BeatmapOnlineStatus.WIP), and re-submitting moves a published set between WIP, Pending and
/// Unranked as the wizard asks, while a ranked set keeps its rank and only records the new intent.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssWipStatusTest
{
    private const string username = "wip submitter";

    private string bearer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_, bearer) = await BssFixture.CreateUserAsync(username, verified: true);
    }

    [TestCase("WIP", "wip")]
    [TestCase("wip", "wip")]
    [TestCase("Pending", "pending")]
    [TestCase("Unranked", "unranked")]
    [TestCase(null, "wip")]
    [TestCase("", "wip")]
    [TestCase("Qualified", "pending")]
    public void IntendedStatusFor_MapsEveryTarget(string? target, string expected)
        => Assert.That(BssEndpoints.IntendedStatusFor(target), Is.EqualTo(expected));

    [Test]
    public async Task WipTarget_PublishesAsWip_AndReTargetsBetweenWipPendingAndUnranked()
    {
        var (setId, diffId) = await createAsync("WIP");

        await using var conn = await BssFixture.OpenDbAsync();

        string intended = await intendedAsync(conn, setId);
        string shell = await statusAsync(conn, setId);

        Assert.Multiple(() =>
        {
            Assert.That(intended, Is.EqualTo("wip"), "the intent is recorded on the create PUT");
            Assert.That(shell, Is.EqualTo("hidden"), "still a shell until the first upload");
        });

        await uploadAsync(setId, diffId);

        Assert.That(await statusAsync(conn, setId), Is.EqualTo("wip"), "a WIP submission publishes as WIP, not Pending");

        // The game API: public (WIP is published) and reported as the client's WIP status.
        using (var anonymous = await BssFixture.Client.GetAsync($"/api/v2/beatmapsets/{setId}"))
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a WIP set is world-readable like a pending one");
            var body = JObject.Parse(await anonymous.Content.ReadAsStringAsync());

            Assert.Multiple(() =>
            {
                Assert.That((string)body["status"]!, Is.EqualTo("wip"));
                Assert.That((string)body["beatmaps"]![0]!["status"]!, Is.EqualTo("wip"));
            });
        }

        // The mapper marks it ready: Pending.
        await retargetAsync(setId, diffId, "Pending");
        Assert.That(await statusAsync(conn, setId), Is.EqualTo("pending"), "WIP -> Pending on re-submission");

        // ...and back to WIP.
        await retargetAsync(setId, diffId, "WIP");
        Assert.That(await statusAsync(conn, setId), Is.EqualTo("wip"), "Pending -> WIP on re-submission");

        // WIP -> Unranked -> WIP: the three published, mapper-chosen states all move freely.
        await retargetAsync(setId, diffId, "Unranked");
        Assert.That(await statusAsync(conn, setId), Is.EqualTo("unranked"));

        await retargetAsync(setId, diffId, "WIP");
        string finalStatus = await statusAsync(conn, setId);
        string finalIntent = await intendedAsync(conn, setId);

        Assert.Multiple(() =>
        {
            Assert.That(finalStatus, Is.EqualTo("wip"));
            Assert.That(finalIntent, Is.EqualTo("wip"));
        });
    }

    [Test]
    public async Task PendingTarget_StillPublishesAsPending()
    {
        var (setId, diffId) = await createAsync("Pending");
        await uploadAsync(setId, diffId);

        await using var conn = await BssFixture.OpenDbAsync();
        Assert.That(await statusAsync(conn, setId), Is.EqualTo("pending"));
    }

    [Test]
    public async Task RankedSet_ResubmittedAsWip_KeepsItsRank_AndRecordsTheIntent()
    {
        var (setId, diffId) = await createAsync("Pending");
        await uploadAsync(setId, diffId);

        await using var conn = await BssFixture.OpenDbAsync();

        // A reviewer ranked it (the lever itself is BeatmapsetReview's, tested on the website side).
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @setId", new { setId });

        await retargetAsync(setId, diffId, "WIP");
        string status = await statusAsync(conn, setId);
        string intended = await intendedAsync(conn, setId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo("ranked"), "a mapper never self-demotes a ranked set");
            Assert.That(intended, Is.EqualTo("wip"));
        });
    }

    [Test]
    public async Task Constraint_AcceptsWip_InBothColumns()
    {
        var (setId, _) = await createAsync("Pending");

        await using var conn = await BssFixture.OpenDbAsync();

        // Migration 059: 'wip' is a status and an intended_status; an unknown value is still refused.
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'wip', intended_status = 'wip' WHERE id = @setId", new { setId });

        Assert.That(await statusAsync(conn, setId), Is.EqualTo("wip"));

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                conn.ExecuteAsync("UPDATE beatmapsets SET status = 'qualified' WHERE id = @setId", new { setId }));
            Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                conn.ExecuteAsync("UPDATE beatmapsets SET intended_status = 'ranked' WHERE id = @setId", new { setId }));
        });
    }

    private async Task<(long SetId, long DiffId)> createAsync(string target)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target,
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).First());
    }

    private async Task retargetAsync(long setId, long diffId, string target)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(new
        {
            beatmapset_id = (long?)setId,
            beatmaps_to_create = 0,
            beatmaps_to_keep = new[] { diffId },
            target,
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"re-target to {target}");
    }

    private async Task uploadAsync(long setId, long diffId)
    {
        var osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            creator: username, version: "solo", beatmapId: diffId, beatmapSetId: setId));

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(("solo.osu", osu), ("audio.mp3", BssSubmissionFlowTest.MakeWav(seconds: 1)), ("bg.jpg", SyntheticPackage.TinyPng())))
            zipBytes = zip.ToArray();

        using var upload = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipBytes));
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    private static Task<string> statusAsync(Npgsql.NpgsqlConnection conn, long setId)
        => conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @setId", new { setId })!;

    private static Task<string> intendedAsync(Npgsql.NpgsqlConnection conn, long setId)
        => conn.ExecuteScalarAsync<string>("SELECT intended_status FROM beatmapsets WHERE id = @setId", new { setId })!;
}
