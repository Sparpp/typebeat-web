using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// /play's pause, focus-loss pause, retry and quit (backlog 311). Js/PlayerPauseHarness.cjs mounts
/// the real player over a fake DOM, a fake AudioContext (its clock only moves while 'running') and
/// fake timers, and reports what the lifecycle did. The leaderboard stakes: a paused clock must
/// never judge a key (a free look at the lyric), the cooldown bounds how often it can be taken, a
/// discarded run must never reach onFinish (the only road to /play/submit), and a pause must not
/// cost the playback-validity veto a discrepancy.
/// </summary>
[TestFixture]
public class PlayerPauseTest
{
    private static JsonElement root;

    [OneTimeSetUp]
    public void RunHarness() => root = JsHarness.Run("PlayerPauseHarness.cjs");

    private static JsonElement at(params string[] path)
    {
        var e = root;
        foreach (string p in path)
            e = e.GetProperty(p);
        return e;
    }

    private static bool flag(params string[] path) => at(path).GetBoolean();
    private static int num(params string[] path) => at(path).GetInt32();
    private static double real(params string[] path) => at(path).GetDouble();

    private static void assertNothingLive(JsonElement listeners, int keydown = 0)
    {
        Assert.That(listeners.GetProperty("keydown").GetInt32(), Is.EqualTo(keydown), "keydown");
        foreach (string k in new[] { "keyup", "visibility", "blur", "beforeunload", "raf", "intervals" })
            Assert.That(listeners.GetProperty(k).GetInt32(), Is.Zero, k);
    }

    /// <summary>The desktop's PauseCooldownDuration and UIHoldActivationDelay default.</summary>
    [Test]
    public void TheConstants_AreTheDesktops()
    {
        Assert.Multiple(() =>
        {
            Assert.That(num("constants", "cooldownMs"), Is.EqualTo(1000));
            Assert.That(num("constants", "holdMs"), Is.EqualTo(200));
        });
    }

    [Test]
    public void WhilePaused_TheClockIsFrozen_AndGameKeysAreIgnored()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("frozen", "escPrevented"), Is.True);
            Assert.That(num("frozen", "suspendCalls"), Is.EqualTo(1));
            Assert.That(flag("frozen", "cardShown"), Is.True);
            // Five seconds of wall time later the clock still reads the pause instant.
            Assert.That(real("frozen", "nowAfterWait"), Is.EqualTo(real("frozen", "pausedAt")));
            // 'a' at 1000 is the line's first cell and would have landed; paused, it does nothing.
            Assert.That(num("frozen", "caretAfterPausedPress"), Is.EqualTo(num("frozen", "caretBefore")));
            // No loop runs under the card.
            Assert.That(num("frozen", "loopsWhilePaused", "raf"), Is.Zero);
            Assert.That(num("frozen", "loopsWhilePaused", "intervals"), Is.Zero);
            // Escape again resumes at once (no countdown), and the same key then types.
            Assert.That(flag("frozen", "resumed", "paused"), Is.False);
            Assert.That(at("frozen", "resumed", "ctx").GetString(), Is.EqualTo("running"));
            Assert.That(flag("frozen", "resumed", "card"), Is.False);
            Assert.That(num("frozen", "caretAfterResumePress"), Is.EqualTo(num("frozen", "caretBefore") + 1));
            Assert.That(flag("frozen", "clockMovedAfterResume"), Is.True);
        });
    }

    /// <summary>
    /// The pre-roll (backlog 308) is negative time with no source playing yet; pausing there freezes
    /// the clock at its negative reading and resuming carries on from it.
    /// </summary>
    [Test]
    public void APauseInsideTheNegativePreRoll_FreezesAndResumes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("preRoll", "negativeWhenPaused"), Is.True);
            Assert.That(real("preRoll", "held"), Is.EqualTo(real("preRoll", "pausedAt")));
            Assert.That(real("preRoll", "after"), Is.GreaterThan(real("preRoll", "held") + 100));
        });
    }

    [Test]
    public void TheCooldown_RefusesASecondPauseInside1000msOfRealTime_AndAFocusLossRetriesIt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("cooldown", "first"), Is.True);
            Assert.That(flag("cooldown", "resumed"), Is.True, "resume is never gated");
            Assert.That(flag("cooldown", "secondRefused"), Is.True);
            Assert.That(flag("cooldown", "blurRefused"), Is.True);
            // The refused auto-pause is retried each tick, and lands on the first frame past 1000.
            Assert.That(real("cooldown", "retriedPauseAfterMs"), Is.InRange(1000, 1016));
            Assert.That(flag("cooldown", "thirdAfterCooldown"), Is.True);
            Assert.That(flag("cooldown", "pureInside"), Is.True);
            Assert.That(flag("cooldown", "pureAtEdge"), Is.False);
            Assert.That(flag("cooldown", "pureNever"), Is.False);
        });
    }

    /// <summary>
    /// The desktop's break exception: focus loss does not pause inside a skippable instrumental (a
    /// qualifying gap up to its skip target, or the intro run-up), and a hide that began there
    /// pauses once the clock leaves it. A context the browser suspended is never waived.
    /// </summary>
    [Test]
    public void AutoPause_IsSkippedInsideASkipWindow_AndTakenOutsideOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("breakException", "blurInGap"), Is.False);
            Assert.That(flag("breakException", "hideInGap"), Is.False);
            // Hidden, so only the 250 ms backstop ticks: the pause lands within one of its periods.
            double target = real("breakException", "skipTarget");
            Assert.That(real("breakException", "pausedAtClock"), Is.InRange(target, target + 260));
            Assert.That(flag("breakException", "blurOnLine"), Is.True);
            Assert.That(flag("breakException", "blurInIntro"), Is.False);
            Assert.That(flag("breakException", "interruptedInGap"), Is.True);
            Assert.That(flag("breakException", "pure", "inGap"), Is.True);
            Assert.That(flag("breakException", "pure", "atTarget"), Is.False);
            Assert.That(flag("breakException", "pure", "beforeGap"), Is.False);
            Assert.That(flag("breakException", "pure", "inIntro"), Is.True);
            Assert.That(flag("breakException", "pure", "atIntroTarget"), Is.False);
        });
    }

    /// <summary>
    /// The owner's decision (2026-09-28): an abandoned browser run is never submitted. Quit (card or
    /// held Ctrl+`) and mid-play retry (held ` or card) never reach onFinish, and leave nothing live
    /// behind them; the retried run is the only one that concludes.
    /// </summary>
    [Test]
    public void QuitAndMidPlayRetry_SendNoSubmission()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("discard", "cardQuit", "clicked"), Is.True);
            Assert.That(num("discard", "cardQuit", "finishes"), Is.Zero);
            Assert.That(num("discard", "cardQuit", "exits"), Is.EqualTo(1));
            Assert.That(at("discard", "cardQuit", "ctx").GetString(), Is.EqualTo("closed"));
            assertNothingLive(at("discard", "cardQuit", "listeners"));

            Assert.That(flag("discard", "hotkeyQuit", "abortedStillRunning"), Is.True, "released early aborts");
            Assert.That(flag("discard", "hotkeyQuit", "runningAt150"), Is.True, "not before the hold delay");
            Assert.That(flag("discard", "hotkeyQuit", "running"), Is.False);
            Assert.That(num("discard", "hotkeyQuit", "exits"), Is.EqualTo(1));
            Assert.That(num("discard", "hotkeyQuit", "finishes"), Is.Zero);
            assertNothingLive(at("discard", "hotkeyQuit", "listeners"));

            Assert.That(num("discard", "retry", "typedBefore"), Is.EqualTo(1));
            Assert.That(flag("discard", "retry", "retried"), Is.True);
            Assert.That(num("discard", "retry", "afterRetry", "playStarts"), Is.EqualTo(2), "a fresh token");
            Assert.That(num("discard", "retry", "afterRetry", "finishes"), Is.Zero);
            Assert.That(flag("discard", "retry", "afterRetry", "running"), Is.True);
            Assert.That(real("discard", "retry", "afterRetry", "now"), Is.LessThan(0), "back at the pre-roll");
            // Exactly one loop and one of each listener: the old run's were torn down, not doubled.
            foreach (string k in new[] { "keydown", "keyup", "visibility", "blur", "beforeunload", "raf", "intervals" })
            {
                Assert.That(num("discard", "retry", "afterRetry", "listeners", k), Is.EqualTo(1), k);
                Assert.That(num("discard", "retry", "afterCardRetry", "listeners", k), Is.EqualTo(1), k);
            }
            Assert.That(flag("discard", "retry", "cardRetryClicked"), Is.True);
            Assert.That(num("discard", "retry", "afterCardRetry", "playStarts"), Is.EqualTo(3));
            Assert.That(num("discard", "retry", "afterCardRetry", "finishes"), Is.Zero);
            Assert.That(flag("discard", "retry", "afterCardRetry", "paused"), Is.False);
            Assert.That(at("discard", "retry", "afterCardRetry", "ctx").GetString(), Is.EqualTo("running"));
            Assert.That(num("discard", "retry", "finishesAtEnd"), Is.EqualTo(1));

            // With no host to return to, quit falls back to the start gate, still unsubmitted.
            Assert.That(num("discard", "noExitHost", "finishes"), Is.Zero);
            Assert.That(flag("discard", "noExitHost", "running"), Is.False);
            Assert.That(flag("discard", "noExitHost", "startGate"), Is.True);
            assertNothingLive(at("discard", "noExitHost", "listeners"), keydown: 1);
        });
    }

    /// <summary>
    /// Eight pauses of five seconds each: more than the ALLOWED_PLAYBACK_DISCREPANCIES + 1 the veto
    /// tolerates, so even one discrepancy per pause would trip it. The run still concludes valid.
    /// </summary>
    [Test]
    public void ThePlaybackValidityVeto_DoesNotTripAcrossPauses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(num("validity", "pausesTaken"), Is.EqualTo(8));
            Assert.That(num("validity", "mid", "discrepancies"), Is.Zero);
            Assert.That(flag("validity", "final", "valid"), Is.True);
            Assert.That(num("validity", "final", "discrepancies"), Is.Zero);
            Assert.That(at("validity", "finishes").GetArrayLength(), Is.EqualTo(1));
            Assert.That(at("validity", "finishes")[0].GetProperty("playbackValid").GetBoolean(), Is.True);
        });
    }

    [Test]
    public void TheSkip_IsRefusedWhilePaused_AndBeforeUnloadGuardsARunningPlay()
    {
        Assert.Multiple(() =>
        {
            Assert.That(num("skipAndUnload", "skipWhilePaused", "sourcesCreated"), Is.Zero);
            Assert.That(flag("skipAndUnload", "skipWhilePaused", "clockMoved"), Is.False);
            // The control: resumed, the same gap skips (to the target less the 60 ms start lead).
            Assert.That(num("skipAndUnload", "skipAfterResume", "sourcesCreated"), Is.EqualTo(1));
            Assert.That(real("skipAndUnload", "skipAfterResume", "clock"),
                Is.EqualTo(real("skipAndUnload", "skipTarget") - 60).Within(1));
            Assert.That(num("skipAndUnload", "guard", "handlers"), Is.EqualTo(1));
            Assert.That(flag("skipAndUnload", "guard", "prevented"), Is.True);
            Assert.That(at("skipAndUnload", "guard", "returnValue").GetString(), Is.EqualTo(""));
            Assert.That(num("skipAndUnload", "afterDestroy"), Is.Zero);
        });
    }
}
