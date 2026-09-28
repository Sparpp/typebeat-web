using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// /play's playback-validity veto (backlog 312): typebeat-player.js's makePlaybackValidity, the
/// port of the desktop's MasterGameplayClockContainer.checkPlaybackValidity, driven with fake
/// audio and wall clocks by Js/PlaybackValidityHarness.cjs. A run it trips is flagged
/// (<c>results.playbackValid = false</c>) and play.js declines to submit it, as the desktop's
/// SubmittingPlayer does.
/// </summary>
[TestFixture]
public class PlaybackValidityTest
{
    private static JsonElement root;

    [OneTimeSetUp]
    public void RunHarness() => root = JsHarness.Run("PlaybackValidityHarness.cjs");

    private static (bool Valid, int Discrepancies) state(JsonElement e)
        => (e.GetProperty("valid").GetBoolean(), e.GetProperty("discrepancies").GetInt32());

    private static JsonElement at(params string[] path)
    {
        var e = root;
        foreach (string p in path)
            e = e.GetProperty(p);
        return e;
    }

    [Test]
    public void TheConstants_AreTheDesktops()
    {
        Assert.Multiple(() =>
        {
            // MasterGameplayClockContainer: `> 300` and allowed_playback_discrepancies = 5.
            Assert.That(at("constants", "discrepancyMs").GetInt32(), Is.EqualTo(300));
            Assert.That(at("constants", "allowed").GetInt32(), Is.EqualTo(5));
        });
    }

    [Test]
    public void AnHonestDriftingDevice_NeverCountsADiscrepancy()
        => Assert.That(state(at("normalDrift")), Is.EqualTo((true, 0)));

    /// <summary>
    /// The desktop's <c>playbackDiscrepancyCount++ &gt; allowed</c> reads the count BEFORE the
    /// increment, so six discrepancies are tolerated and the SEVENTH trips it (the spec's "more than
    /// 5" is the comparison, not the count of discrepancies it takes).
    /// </summary>
    [Test]
    public void AStalledAudioClock_TripsOnTheSeventhDiscrepancy_AndStaysTripped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(at("stalled", "lastValidDiscrepancies").GetInt32(), Is.EqualTo(6));
            Assert.That(at("stalled", "trippedAtDiscrepancy").GetInt32(), Is.EqualTo(7));
            Assert.That(state(at("stalled", "final")).Valid, Is.False);
            // Seven windows of just over 300 ms each (the re-seed frame is not measured).
            Assert.That(at("stalled", "stalledMs").GetInt32(), Is.InRange(2100, 2400));
            Assert.That(state(at("stalledWithinAllowance")), Is.EqualTo((true, 6)));
        });
    }

    /// <summary>
    /// The backstop tick after a hidden tab (1 s throttling, then Chrome's 1 min intensive mode)
    /// sees both clocks jump together, so it costs nothing. The frozen control is the same gap with
    /// the audio clock NOT moving, and it is caught on the first tick back.
    /// </summary>
    [Test]
    public void HiddenTabThrottling_DoesNotCount_ButAFrozenClockAcrossTheSameGapDoes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(state(at("hiddenTab", "clean")), Is.EqualTo((true, 0)));
            Assert.That(state(at("hiddenTab", "frozenControl")).Discrepancies, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// startSourceAt announces every seek, which drops the baseline so the jump is never measured
    /// (the unannounced control proves the jump alone would count). It deliberately does NOT clear
    /// the count: six discrepancies, a skip, and one more still trip the veto.
    /// </summary>
    [Test]
    public void ASeek_ResetsTheBaseline_ButNotTheCount()
    {
        Assert.Multiple(() =>
        {
            Assert.That(state(at("seek", "announced")), Is.EqualTo((true, 0)));
            Assert.That(state(at("seek", "unannouncedControl")).Discrepancies, Is.EqualTo(1));
            Assert.That(state(at("seek", "countSurvivesSeek", "beforeRestall")), Is.EqualTo((true, 6)));
            Assert.That(state(at("seek", "countSurvivesSeek", "after")), Is.EqualTo((false, 7)));
        });
    }

    /// <summary>
    /// A frame whose audio context is not 'running' is the desktop's !GameplayClock.IsRunning: not
    /// measured, and the first running frame only re-seeds. The control reports the same frozen
    /// stretch as running and trips.
    /// </summary>
    [Test]
    public void ANotRunningAudioContext_IsAPause_NotAStall()
    {
        Assert.Multiple(() =>
        {
            Assert.That(state(at("notRunning", "clean")), Is.EqualTo((true, 0)));
            Assert.That(state(at("notRunning", "runningControl")).Valid, Is.False);
        });
    }
}
