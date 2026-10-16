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

    // The status as the player reads it: setSubmitStatus sets text, then the link's text, then
    // textAfter, all as text nodes.
    private static (string Text, string Cls) status(string name)
    {
        var e = root.GetProperty(name);
        return (e.GetProperty("text").GetString() + optional(e, "linkText") + optional(e, "textAfter"), e.GetProperty("cls").GetString()!);
    }

    private static string optional(JsonElement e, string property) =>
        e.TryGetProperty(property, out var value) ? value.GetString()! : "";

    [Test]
    public void ARankedPlay_PrintsItsPp_AndSaysWhetherItIsTheNewBest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("rankedNewBest"), Is.EqualTo(("submitted ✓ ranked · 1,235pp · new best, #3", "tb-status-good")));
            // The position is the BEST row's, so a run that did not beat it must not claim it.
            Assert.That(status("rankedNotBest").Text, Is.EqualTo("submitted ✓ ranked · 88pp · your best is #3"));
            // A priced 0 is a real price and prints as one.
            Assert.That(status("rankedPricedZero").Text, Is.EqualTo("submitted ✓ ranked · 0pp · new best, #9"));
        });
    }

    [Test]
    public void ANullPp_IsNeverPrintedAsZero_PendingOnARankedPlay_ADashOtherwise()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("rankedPending").Text, Is.EqualTo("submitted ✓ ranked · pp pending · new best, #1"));
            Assert.That(status("failedRun"), Is.EqualTo(("recorded, not ranked (you failed this run) · - pp", "tb-status-muted")));
            Assert.That(status("checksFailed").Text, Is.EqualTo("recorded, not ranked (browser plays do not rank, only the desktop game does) · - pp"));

            foreach (string name in new[] { "rankedPending", "failedRun", "checksFailed", "pendingMap", "unrankedMapNotBest" })
                Assert.That(status(name).Text, Does.Not.Contain("0pp"), name);
        });
    }

    [Test]
    public void APlayOnALovedMap_SaysBrowserPlaysDoNotRank_NotThatTheMapDoesNotCount()
    {
        Assert.That(status("lovedChecksFailed").Text, Is.EqualTo("recorded, not ranked (browser plays do not rank, only the desktop game does) · - pp"));
    }

    [Test]
    public void APendingOrUnrankedMap_PlacesThePlayOnTheUnrankedBoard_AsNotCounting()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("pendingMap").Text,
                Is.EqualTo("recorded, not ranked (this map is pending, so it does not count) · - pp · new best, #2 on the unranked board, not counted"));
            Assert.That(status("unrankedMapNotBest").Text,
                Is.EqualTo("recorded, not ranked (this map is unranked, so it does not count) · - pp · your best is #4 on the unranked board, not counted"));
            Assert.That(status("pendingMap").Text, Does.Not.Contain("no leaderboard"));
        });
    }

    /// <summary>
    /// Backlog 312's wire additions from the player's side: the token carries the page's revision
    /// and the served checksum when it has them, and leaves each off when it does not (the server
    /// then answers as it did before: web-player, its own checksum).
    /// </summary>
    [Test]
    public void TheTokenBody_CarriesTheRevisionAndChecksum_OnlyWhenKnown()
    {
        var full = root.GetProperty("tokenBodyFull");
        var bare = root.GetProperty("tokenBodyBare");
        Assert.Multiple(() =>
        {
            Assert.That(full.GetProperty("setId").GetInt64(), Is.EqualTo(7));
            Assert.That(full.GetProperty("beatmapId").GetInt64(), Is.EqualTo(12));
            Assert.That(full.GetProperty("revision").GetString(), Is.EqualTo("0123456789abcdef"));
            Assert.That(full.GetProperty("beatmapHash").GetString(), Is.EqualTo("d41d8cd98f00b204e9800998ecf8427e"));
            Assert.That(bare.TryGetProperty("revision", out _), Is.False);
            Assert.That(bare.TryGetProperty("beatmapHash", out _), Is.False);
        });
    }

    /// <summary>The server's two refusals (the desktop's wording) each become a reload prompt;
    /// anything else keeps the generic line. A vetoed playback says why it was not sent.</summary>
    [Test]
    public void ARefusedToken_IsAReloadPrompt_AndAVetoedPlayback_SaysWhy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(status("refusedOutdated").Text, Does.Contain("out of date").And.Contain("reload"));
            Assert.That(status("refusedStaleMap").Text, Does.Contain("map was updated").And.Contain("reload"));
            // The reload is a link back to this same page.
            foreach (string name in new[] { "refusedOutdated", "refusedStaleMap" })
            {
                Assert.That(root.GetProperty(name).GetProperty("linkText").GetString(), Is.EqualTo("reload"), name);
                Assert.That(root.GetProperty(name).GetProperty("linkHref").GetString(), Is.EqualTo(""), name);
            }
            Assert.That(status("refusedOther"), Is.EqualTo(("score not submitted (could not open a play session).", "tb-status-bad")));
            Assert.That(status("playbackInvalid").Text, Does.StartWith("score not submitted: audio playback"));
        });
    }
}
