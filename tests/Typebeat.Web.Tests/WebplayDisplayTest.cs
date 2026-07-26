using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the /play PRESENTATION layer (wwwroot/js/typebeat-player.js). The browser
/// player reproduces the desktop client's gameplay stage in HTML/CSS/JS: the damped, idle-blinking
/// player caret, the sung playhead that sweeps with the vocals independently of the player, the two
/// depleting cue-in bars, and the rolling WPM / sync HUD readouts.
///
/// <para>None of it can move a score, so this is not a scoring guard; it is the guard that browser
/// play keeps FEELING like the client. Every formula below is a port of a specific desktop source
/// (TypingLine.SungPositionAt, LyricStage.updateCueBar / updateApproachCue, Caret.Update,
/// TypingEngine.LiveRollingWpm / LiveSyncPercent, Judgement.SyncQuality), and the golden values are
/// mirrored from typebeat-osu's own fixtures, so a silent drift trips here rather than in play.</para>
///
/// <para>A Node harness (Js/PlayerDisplayHarness.cjs) drives the actual shipped scripts and emits
/// its observations; this asserts them. Assert.Ignore when node is absent.</para>
/// </summary>
public class WebplayDisplayTest
{
    private static JsonElement Harness() => JsHarness.Run("PlayerDisplayHarness.cjs");

    private static double Num(JsonElement e, string key) => e.GetProperty(key).GetDouble();

    private static bool Flag(JsonElement e, string key) => e.GetProperty(key).GetBoolean();

    /// <summary>
    /// The fixture is typebeat-osu's workhorse line transcribed into the .osu form /play consumes:
    /// "ab cd", boundary [1000, 4000), sung end 3000, words "ab" [1000,2000] and "cd" [2000,3000].
    /// Pinned here because every sung-playhead golden below is read off it.
    /// </summary>
    [Test]
    public void FixtureLineDecodesToTheDesktopTargets()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(root, "cellTargets"), Is.EqualTo(new[] { 1000d, 1500d, 2000d, 2000d, 2500d }));
            Assert.That(Num(root, "lineStart"), Is.EqualTo(1000));
            Assert.That(Num(root, "lineEnd"), Is.EqualTo(4000));
            Assert.That(Num(root, "lineSingEnd"), Is.EqualTo(3000));
            // Activation is the cue-open instant: max(StartTime, firstTarget - CUE_LEAD_MS).
            Assert.That(Num(root, "lineActivation"), Is.EqualTo(1000));
        });
    }

    /// <summary>
    /// The sung playhead's polyline (mirrors TypingLine's sungPoints constructor):
    /// (StartTime, 0), every cell's (TargetTime, cellIndex), (SingEndTime, cellCount).
    /// </summary>
    [Test]
    public void SungPlayheadPolylineMatchesTheDesktopAnchors()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(root, "sungPointTimes"), Is.EqualTo(new[] { 1000d, 1000d, 1500d, 2000d, 2000d, 2500d, 3000d }));
            Assert.That(JsHarness.Doubles(root, "sungPointIndices"), Is.EqualTo(new[] { 0d, 0d, 1d, 2d, 3d, 4d, 5d }));
        });
    }

    /// <summary>
    /// Golden samples lifted verbatim from typebeat-osu's TypingEngineTest: clamped before the
    /// line, halfway through a segment, a ZERO-LENGTH segment the position must jump across, the
    /// final segment into sing end, and clamped after it.
    /// </summary>
    [Test]
    public void SungPlayheadInterpolatesLikeTheDesktopClient()
    {
        var sung = JsHarness.Doubles(Harness(), "sungAt");

        Assert.Multiple(() =>
        {
            Assert.That(sung[0], Is.EqualTo(0));    // t = 500, clamped before start
            Assert.That(sung[1], Is.EqualTo(0.5));  // t = 1250, halfway a -> b
            Assert.That(sung[2], Is.EqualTo(3));    // t = 2000, zero-length ' ' -> c segment skipped
            Assert.That(sung[3], Is.EqualTo(4.5));  // t = 2750, halfway d -> sing end
            Assert.That(sung[4], Is.EqualTo(5));    // t = 9999, clamped after sing end
        });
    }

    /// <summary>
    /// A cue-in bar depletes 1 -> 0 over the final CUE_LEAD_MS and brightens as it lands
    /// (alpha = (0.85 - 0.35 * progress) * opacityScale, max width 140px). Outside its own window
    /// it is hidden, so a stale cue can never appear on a line the clock has already passed.
    /// </summary>
    [Test]
    public void CueInBarDepletesAndBrightensLikeTheDesktopApproachCue()
    {
        var root = Harness();
        var full = root.GetProperty("cueFull");
        var half = root.GetProperty("cueHalf");
        var word = root.GetProperty("cueWordHalf");

        Assert.Multiple(() =>
        {
            // The window is the engine's own cue lead, not a display constant of its own.
            Assert.That(Num(root, "cueLeadMs"), Is.EqualTo(1500));

            Assert.That(Flag(full, "shown"), Is.True);
            Assert.That(Num(full, "width"), Is.EqualTo(140));
            Assert.That(Num(full, "alpha"), Is.EqualTo(0.5).Within(1e-12));

            Assert.That(Num(half, "width"), Is.EqualTo(70));
            Assert.That(Num(half, "alpha"), Is.EqualTo(0.675).Within(1e-12));

            // The 50%-opaque first-word bar is the solid boundary bar at half alpha, same width.
            Assert.That(Num(word, "width"), Is.EqualTo(Num(half, "width")));
            Assert.That(Num(word, "alpha"), Is.EqualTo(Num(half, "alpha") / 2).Within(1e-12));

            Assert.That(Flag(root.GetProperty("cueLanded"), "shown"), Is.False);
            Assert.That(Flag(root.GetProperty("cueTooEarly"), "shown"), Is.False);
        });
    }

    /// <summary>
    /// Which line the cue belongs to (mirrors LyricStage.updateApproachCue). A line activates at
    /// the very moment its cue window opens, so in a continuous map the still-active PREVIOUS line
    /// carries the cue for the next one; but after an instrumental gap a line self-activates with
    /// nobody before it, and while its own first word is still ahead the cue is its own. Getting
    /// this wrong drops the count-in on every line after a gap.
    /// </summary>
    [Test]
    public void CueTargetsTheLineThatIsActuallyBeingCountedIn()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "cueTargetIdle"), Is.EqualTo(0));        // nothing active: the next unsealed line
            Assert.That(Num(root, "cueTargetOwnLeadIn"), Is.EqualTo(1));   // active, own first word still ahead
            Assert.That(Num(root, "cueTargetNext"), Is.EqualTo(1));        // active and singing: cue the line after
        });
    }

    /// <summary>
    /// Caret motion and blink (Caret.Update): a damped approach whose half-time is exactly that,
    /// and a cosine blink that only starts one blink period after the last keystroke, never while
    /// the caret is still travelling.
    /// </summary>
    [Test]
    public void CaretDampsAndBlinksLikeTheDesktopCaret()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // One half-time of elapsed time covers exactly half the remaining distance.
            Assert.That(Num(root, "dampHalf"), Is.EqualTo(50).Within(1e-9));
            Assert.That(Num(root, "dampNone"), Is.EqualTo(10)); // no elapsed time, no movement

            Assert.That(Num(root, "caretAlphaTyping"), Is.EqualTo(1));
            Assert.That(Num(root, "caretAlphaJustBefore"), Is.EqualTo(1)); // still inside the period
            Assert.That(Num(root, "caretAlphaMoving"), Is.EqualTo(1));     // travelling: never blinks
            Assert.That(Num(root, "caretAlphaTrough"), Is.EqualTo(0).Within(1e-12));
            Assert.That(Num(root, "caretAlphaCrest"), Is.EqualTo(1).Within(1e-12));
            Assert.That(Num(root, "caretAlphaNoBlink"), Is.EqualTo(1));    // the sung caret never blinks
        });
    }

    /// <summary>
    /// Rolling WPM over the last 30 keypresses (mirrors TypingEngine.LiveRollingWpm), so the HUD
    /// tracks current pace instead of flattening the whole run. n presses bound n-1 gaps, and the
    /// ring must keep sliding once it wraps rather than resetting.
    /// </summary>
    [Test]
    public void RollingWpmAveragesTheLastThirtyPresses()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // Under two presses, or a window with no span, defers to the caller's whole-run figure
            // (-1 is the harness's sentinel for "fell back").
            Assert.That(Num(root, "rollingEmpty"), Is.EqualTo(-1));
            Assert.That(Num(root, "rollingOne"), Is.EqualTo(-1));
            Assert.That(Num(root, "rollingZeroSpan"), Is.EqualTo(-1));

            // 30 presses 100ms apart span 2900ms of active time: (29 / 5) / (2900 / 60000) = 120.
            Assert.That(Num(root, "rollingFull"), Is.EqualTo(120).Within(1e-9));
            Assert.That(Num(root, "rollingWrapped"), Is.EqualTo(120).Within(1e-9));
        });
    }

    /// <summary>Judgement.SyncQuality: 1 on target, linear to 0 at the Ok window edges, clamped.</summary>
    [Test]
    public void SyncQualityDecaysToTheOkWindowEdges()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "syncOnTarget"), Is.EqualTo(1));
            Assert.That(Num(root, "syncHalfLate"), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(Num(root, "syncAtLateEdge"), Is.EqualTo(0));
            Assert.That(Num(root, "syncPastLateEdge"), Is.EqualTo(0)); // clamped, never negative
            Assert.That(Num(root, "syncAtEarlyEdge"), Is.EqualTo(0));
        });
    }

    /// <summary>
    /// The live HUD readouts over real engine runs. Sync counts every cell a SEALED line resolved,
    /// including the ones that were never typed (q = 0), which is what makes it a timing measure
    /// rather than a hit count.
    /// </summary>
    [Test]
    public void LiveHudReadoutsTrackTheRun()
    {
        var root = Harness();
        var perfect = root.GetProperty("perfectStats");
        var partial = root.GetProperty("partialStats");
        var late = root.GetProperty("lateStats");

        Assert.Multiple(() =>
        {
            Assert.That(Num(perfect, "completion"), Is.EqualTo(1));
            Assert.That(Num(perfect, "sync"), Is.EqualTo(100));

            // One of five cells typed, the rest sealed as misses: 1/5 typed, and 4 cells at q = 0.
            Assert.That(Num(partial, "completion"), Is.EqualTo(0.2).Within(1e-12));
            Assert.That(Num(partial, "sync"), Is.EqualTo(20).Within(1e-12));

            // Two presses, one on target and one at half quality: both count as typed, sync 75%.
            Assert.That(Num(late, "completion"), Is.EqualTo(1));
            Assert.That(Num(late, "sync"), Is.EqualTo(75).Within(1e-12));
        });
    }

    /// <summary>
    /// The load-bearing invariant: the presentation layer is display only. The same perfect run,
    /// driven through the untouched scorer after the display helpers have read it, still submits a
    /// clean X. Nothing in typebeat-player.js may ever reach back into judgement.
    /// </summary>
    [Test]
    public void DisplayLayerDoesNotDisturbScoring()
    {
        var score = Harness().GetProperty("perfectScore");

        Assert.Multiple(() =>
        {
            Assert.That(score.GetProperty("rank").GetString(), Is.EqualTo("X"));
            Assert.That(Num(score, "completion"), Is.EqualTo(1));
            Assert.That(Num(score, "totalScore"), Is.EqualTo(1000000));
        });
    }

    /// <summary>OutQuint, the easing behind the 220ms line-change scroll.</summary>
    [Test]
    public void LineScrollUsesOutQuint()
    {
        var e = JsHarness.Doubles(Harness(), "outQuint");

        Assert.Multiple(() =>
        {
            Assert.That(e[0], Is.EqualTo(0));
            Assert.That(e[1], Is.EqualTo(0.96875).Within(1e-12)); // 1 - 0.5^5
            Assert.That(e[2], Is.EqualTo(1));
        });
    }
}
