using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// What the browser player PRINTS once /play/submit answers (play.js's submitStatus, backlog 321),
/// driven through the shipped script by Js/PlaySubmitStatusHarness.cjs. The server half of the same
/// contract (which fields the response carries, and when) is pinned in
/// <c>Website/PlayHistoryTest</c>; this is the half that turns them into words.
/// </summary>
[TestFixture]
public class PlaySubmitStatusTest
{
    private static JsonElement root;

    [OneTimeSetUp]
    public void RunHarness() => root = JsHarness.Run("PlaySubmitStatusHarness.cjs");

    private static (string Html, string Cls) status(string name)
    {
        var e = root.GetProperty(name);
        return (e.GetProperty("html").GetString()!, e.GetProperty("cls").GetString()!);
    }

    [Test]
    public void ARankedPlay_PrintsItsPp_AndSaysWhetherItIsTheNewBest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("rankedNewBest"), Is.EqualTo(("submitted ✓ ranked · 1,235pp · new best, #3", "tb-status-good")));
            // The position is the BEST row's, so a run that did not beat it must not claim it.
            Assert.That(status("rankedNotBest").Html, Is.EqualTo("submitted ✓ ranked · 88pp · your best is #3"));
            // A priced 0 is a real price and prints as one.
            Assert.That(status("rankedPricedZero").Html, Is.EqualTo("submitted ✓ ranked · 0pp · new best, #9"));
        });
    }

    [Test]
    public void ANullPp_IsNeverPrintedAsZero_PendingOnARankedPlay_ADashOtherwise()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("rankedPending").Html, Is.EqualTo("submitted ✓ ranked · pp pending · new best, #1"));
            Assert.That(status("failedRun"), Is.EqualTo(("recorded, not ranked (you failed this run) · - pp", "tb-status-muted")));
            Assert.That(status("checksFailed").Html, Is.EqualTo("recorded, not ranked (checks failed) · - pp"));

            foreach (string name in new[] { "rankedPending", "failedRun", "checksFailed", "pendingMap", "unrankedMapNotBest" })
                Assert.That(status(name).Html, Does.Not.Contain("0pp"), name);
        });
    }

    [Test]
    public void APendingOrUnrankedMap_PlacesThePlayOnTheUnrankedBoard_AsNotCounting()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("pendingMap").Html,
                Is.EqualTo("recorded, not ranked (this map is pending, so it does not count) · - pp · new best, #2 on the unranked board, not counted"));
            Assert.That(status("unrankedMapNotBest").Html,
                Is.EqualTo("recorded, not ranked (this map is unranked, so it does not count) · - pp · your best is #4 on the unranked board, not counted"));
            Assert.That(status("pendingMap").Html, Does.Not.Contain("no leaderboard"));
        });
    }
}
