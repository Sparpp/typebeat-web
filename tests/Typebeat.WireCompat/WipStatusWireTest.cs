using Newtonsoft.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Screens.Edit.Submission;
using Typebeat.Web.Wire;
using BeatmapsetEndpoints = Typebeat.Web.Endpoints.BeatmapsetEndpoints;
using BssEndpoints = Typebeat.Web.Endpoints.BssEndpoints;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the WIP status (backlog 476), both directions over the real wire code:
///
///  1. IN: the game's submission wizard target, serialised by the client's own
///     <see cref="PutBeatmapSetRequest"/>, read by the server's <see cref="BssPutRequest"/> and
///     mapped by <see cref="BssEndpoints.IntendedStatusFor"/>. WIP must land on 'wip' and Pending
///     on 'pending', or the website goes back to calling every WIP map Pending.
///  2. OUT: a stored 'wip' set, sent through <see cref="BeatmapsetEndpoints.StatusString"/>, must
///     bind to the client's <see cref="BeatmapOnlineStatus.WIP"/> on both the set and its
///     beatmaps. WIP has been a member of that enum since before the fork, so every shipped client
///     binds it; the case below that sends an unknown name records why a NEW wire string would not
///     have been safe (Newtonsoft refuses it, failing the whole response).
/// </summary>
[TestFixture]
public class WipStatusWireTest
{
    [TestCase(BeatmapSubmissionTarget.WIP, "wip")]
    [TestCase(BeatmapSubmissionTarget.Pending, "pending")]
    [TestCase(BeatmapSubmissionTarget.Unranked, "unranked")]
    public void EveryWizardTarget_StoresItsOwnIntendedStatus(BeatmapSubmissionTarget target, string expected)
    {
        var settings = new BeatmapSubmissionSettings();
        settings.Target.Value = target;

        string json = JsonConvert.SerializeObject(PutBeatmapSetRequest.CreateNew(1, settings));
        var request = JsonConvert.DeserializeObject<BssPutRequest>(json)!;

        Assert.That(BssEndpoints.IntendedStatusFor(request.Target), Is.EqualTo(expected), json);
    }

    [Test]
    public void EveryWizardTarget_IsCovered()
        => Assert.That(Enum.GetValues<BeatmapSubmissionTarget>(), Has.Length.EqualTo(3),
            "a new BeatmapSubmissionTarget needs a server state in BssEndpoints.IntendedStatusFor and a case above");

    [Test]
    public void AStoredWipSet_BindsToTheClientsWipStatus()
    {
        string wire = BeatmapsetEndpoints.StatusString("wip");

        var set = JsonConvert.DeserializeObject<APIBeatmapSet>($$"""{"id":1,"status":"{{wire}}","beatmaps":[{"id":2,"status":"{{wire}}"}]}""")!;

        Assert.Multiple(() =>
        {
            Assert.That(wire, Is.EqualTo("wip"));
            Assert.That(set.Status, Is.EqualTo(BeatmapOnlineStatus.WIP));
            Assert.That(set.Beatmaps.Single().Status, Is.EqualTo(BeatmapOnlineStatus.WIP));
        });
    }

    [Test]
    public void AnUnknownStatusName_WouldFailTheClientsBinding()
        => Assert.Throws<JsonSerializationException>(() =>
            JsonConvert.DeserializeObject<APIBeatmap>("""{"id":1,"status":"work_in_progress"}"""));
}
