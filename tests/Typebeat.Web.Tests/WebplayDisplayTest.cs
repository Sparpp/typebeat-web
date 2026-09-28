using System.Text.Json;
using Typebeat.Web;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the /play PRESENTATION layer (wwwroot/js/typebeat-player.js). The browser
/// player reproduces the desktop client's gameplay stage in HTML/CSS/JS: the damped, idle-blinking
/// player caret, the sung playhead that sweeps with the vocals independently of the player, the two
/// depleting cue-in bars, and the rolling WPM HUD readout. Backlog 251 removed the browser's sync
/// HUD readout and its per-cell tint (the desktop metric they mirrored is off by default too, and
/// the results grade never read either), so the sync-specific goldens that used to live here are
/// gone with it.
///
/// <para>None of it can move a score, so this is not a scoring guard; it is the guard that browser
/// play keeps FEELING like the client. Every formula below is a port of a specific desktop source
/// (TypingLine.SungPositionAt, LyricStage.updateCueBar / updateApproachCue, Caret.Update,
/// TypingEngine.LiveRollingWpm), and the golden values are mirrored from typebeat-osu's own
/// fixtures, so a silent drift trips here rather than in play.</para>
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
    /// backlog 245: a mapper dragging the blue sung-end flag past the last word's own end must not
    /// stretch the caret's pace through the last character. Fixture: "ab cd", words "ab" [1000,2000]
    /// and "cd" [2000,3200] (own end 3200), but the line's declared sung end dragged to 6000. Before
    /// the fix the polyline's final anchor was max(singEndTime, lastTime) = max(6000, 2600) = 6000,
    /// a 3400ms crawl across 'd' alone. The fix closes on the last word's own end instead:
    /// max(lastUnitEnd = 3200, lastTime = 2600) = 3200, matching every other word's own-bound close.
    /// </summary>
    [Test]
    public void SungPlayheadClosesOnTheLastWordsOwnEndNotADraggedSungEnd()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // The dragged flag is still the line's own singEndTime (unaffected: this is presentation
            // only, no wire or scoring surface moves), but the polyline no longer reads it directly.
            Assert.That(Num(root, "draggedLineSingEnd"), Is.EqualTo(6000));
            Assert.That(Num(root, "draggedLastUnitEnd"), Is.EqualTo(3200));

            Assert.That(JsHarness.Doubles(root, "draggedSungPointTimes"),
                Is.EqualTo(new[] { 1000d, 1000d, 1500d, 2000d, 2000d, 2600d, 3200d }));
            Assert.That(JsHarness.Doubles(root, "draggedSungPointIndices"),
                Is.EqualTo(new[] { 0d, 0d, 1d, 2d, 3d, 4d, 5d }));

            // Sampled at d's own target (4), halfway across d -> the last word's own end (4.5), at
            // that end (5, clamped), and long after (5, still clamped): never at 6000.
            Assert.That(JsHarness.Doubles(root, "draggedSungAt"), Is.EqualTo(new[] { 4d, 4.5d, 5d, 5d }));
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
    /// THE PUSH WARNING (backlog 263), the mirror of the cue-in bars above and a port of
    /// <c>LyricStage.updatePushWarning</c>. A player lagging behind on a line the song has already
    /// left keeps it only until the drag cutoff, where the engine force-seals it and lands the caret
    /// on the next line, and that used to arrive with no notice at all. It is now counted down by the
    /// very same depleting bar, at full opacity, hung off the END of the line the player is ON rather
    /// than the start of the line they are about to gain, and painted in the error red rather than
    /// the sung blue, because it is the opposite message: a line about to be taken.
    ///
    /// <para>The bar's own shape is already pinned above, so what is pinned here is the WINDOW, which
    /// is the thing a drift would silently move. The bar covers the final <c>CUE_LEAD_MS</c> before
    /// <c>TypingEngine.dragCutoffAt</c>, and the cutoff is the line's deadline plus its seal grace
    /// plus <c>FLETCHER_DRAG_GRACE_MS</c>; the two constants are both 1500, so the first frame drawn
    /// is the line's own deadline exactly, the instant the seal becomes permitted but for drag
    /// protection. The whole of the borrowed time is what the player watches drain.</para>
    ///
    /// <para>And it is silent wherever the engine says no push is coming: once the push has landed
    /// (here the run's end), and on a line typed out with time to spare, where the readout goes null
    /// mid-window and the bar with it.</para>
    /// </summary>
    [Test]
    public void PushWarningCountsDownTheDragCutoffFromTheEndOfTheLine()
    {
        var push = Harness().GetProperty("pushWarning");
        var samples = push.GetProperty("samples");

        double cutoff = Num(push, "lineEnd") + Num(push, "lineSealGraceMs") + 1500;

        Assert.Multiple(() =>
        {
            Assert.That(cutoff, Is.EqualTo(5500), "4000 + 0 + FLETCHER_DRAG_GRACE_MS");

            foreach (var sample in samples.EnumerateArray())
            {
                Assert.That(Num(sample, "cutoff"), Is.EqualTo(cutoff), "the bar counts down the engine's own deadline, never one of its own");
                Assert.That(sample.GetProperty("line").GetInt32(), Is.Zero, "and hangs off the line the player is on, not the upcoming one");
            }

            // One millisecond before the window opens: the cutoff is known, but nothing is drawn.
            Assert.That(Flag(samples[0].GetProperty("bar"), "shown"), Is.False);

            // The line's own deadline, which is where the window opens because CUE_LEAD_MS and
            // FLETCHER_DRAG_GRACE_MS are the same 1500.
            var opening = samples[1].GetProperty("bar");
            Assert.That(Flag(opening, "shown"), Is.True);
            Assert.That(Num(opening, "width"), Is.EqualTo(140), "full width, the same CUE_BAR_MAX_PX a cue starts at");
            Assert.That(Num(opening, "alpha"), Is.EqualTo(0.5).Within(1e-12));

            // Halfway through the borrowed time, and one frame from the end of it: the width depletes
            // while the alpha ramps 0.50 -> 0.85, which is the cue's own solid ramp.
            var half = samples[2].GetProperty("bar");
            Assert.That(Num(half, "width"), Is.EqualTo(70));
            Assert.That(Num(half, "alpha"), Is.EqualTo(0.675).Within(1e-12));

            var last = samples[3].GetProperty("bar");
            Assert.That(Num(last, "width"), Is.LessThan(0.1));
            Assert.That(Num(last, "alpha"), Is.EqualTo(0.85).Within(0.001));

            // Silent wherever the engine says no push is coming.
            Assert.That(push.GetProperty("afterTheCutoff").GetProperty("cutoff").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(push.GetProperty("afterTheCutoff").GetProperty("line").GetInt32(), Is.EqualTo(-1));
            Assert.That(push.GetProperty("typedOut").GetProperty("cutoff").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "typing the last cell out calls the push off where the player stands");
            Assert.That(push.GetProperty("typedOut").GetProperty("bar").ValueKind, Is.EqualTo(JsonValueKind.Null));
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
    /// Which line carries the sung sweep, the sweep head and the sung caret (mirrors
    /// LyricStage.sungLineFor). With a pinned caret it is always the active line and this is the
    /// behaviour that shipped before backlog 208. Under the flexible caret, which is the DEFAULT
    /// since it, the caret and the vocal come apart: finishing a line early parks the caret at the
    /// head of the next one while the song is still singing the line behind, so the playhead has to
    /// follow the first UNSEALED line or it sits at position 0 of a line the vocal has not reached.
    /// Once everything has sealed there is no unsealed line left and it falls back to the active one.
    ///
    /// <para>Every arm here reads at 1600, inside the first unsealed line's own window, which is
    /// where backlog 223's walk provably does not run: these are the four answers exactly as they
    /// were before it.</para>
    /// </summary>
    [Test]
    public void SungPlayheadRidesTheSongsLineNotTheCarets()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "sungLinePinned"), Is.EqualTo(1));      // pinned: the active line, always
            Assert.That(Num(root, "sungLineParked"), Is.EqualTo(0));      // parked ahead: the line behind
            Assert.That(Num(root, "sungLineCoincident"), Is.EqualTo(1));  // the normal case: one line, unchanged
            Assert.That(Num(root, "sungLineAllSealed"), Is.EqualTo(1));   // nothing unsealed: back to the active line
        });
    }

    /// <summary>
    /// Backlog 223: the seal cursor alone cannot say where the vocals are. Drag protection
    /// (TypingEngine.sealPermitted) deliberately holds the caret's own line unsealed while the player
    /// is still typing it, and the seal loop hands the caret on whenever it seals the caret's line,
    /// so the cursor is structurally never AHEAD of the caret and the row the song had moved to was
    /// unreachable. So the sung line is read from the CLOCK: start at the cursor and walk off every
    /// line the playhead has already left, stepping on endTime + sealGraceMs.
    ///
    /// <para>That instant is the upper bound of TypingEngine.songWindowOpen and the deadline canSeal
    /// uses, which is what makes the walk conservative: inside the first unsealed line's window it
    /// does not run at all. The fixture's line 0 closes at 3000 (its window runs to line 1's start,
    /// with no seal grace), so 2999 is still line 0 and 3000 has already left it, and the walk stops
    /// at the last line rather than running off the end of the map.</para>
    /// </summary>
    [Test]
    public void SungPlayheadWalksOffEveryLineTheSongHasLeft()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "sungLineJustInside"), Is.EqualTo(0));       // 2999: still inside line 0's window
            Assert.That(Num(root, "sungLineAtWindowClose"), Is.EqualTo(1));    // 3000: the step is >=, so it fires here
            Assert.That(Num(root, "sungLineWalked"), Is.EqualTo(1));           // 3200: past it, cursor still pinned at 0
            Assert.That(Num(root, "sungLineWalkStopsAtLast"), Is.EqualTo(1));  // the last line has nowhere to step
        });
    }

    /// <summary>
    /// The two heads answer to different facts, which is the other half of backlog 223 (mirrors the
    /// setCaretsVisible call in LyricStage.Update).
    ///
    /// <para>The TYPING caret hides the moment its line is complete: there is nothing left to type
    /// on it, and that absence IS the "you are done, wait for the song" signal. The MAP PLAYHEAD is
    /// not the player's and must not take that term, because the vocals go on being sung under a
    /// finished caret: since backlog 218 a refused roll parks a complete caret until entry into the
    /// next line opens, which blanked the playhead for seconds at a time while the sweep beneath it
    /// kept moving. It hides only for its own reasons, the run being over and its row being off the
    /// visible stack.</para>
    /// </summary>
    [Test]
    public void TheTypingCaretAndTheMapPlayheadHideForDifferentReasons()
    {
        var root = Harness();

        void Heads(string key, bool player, bool sung)
        {
            var e = root.GetProperty(key);
            Assert.That(Flag(e, "player"), Is.EqualTo(player), $"{key}.player");
            Assert.That(Flag(e, "sung"), Is.EqualTo(sung), $"{key}.sung");
        }

        Assert.Multiple(() =>
        {
            Heads("caretsTyping", true, true);          // mid-line: both
            Heads("caretsLineComplete", false, true);   // nothing left to type, but the song plays on
            Heads("caretsFinished", false, false);      // the run is over
            Heads("caretsOffStack", true, false);       // the song is not on a visible row
            Heads("caretsIdle", false, false);          // no active line at all
        });
    }

    /// <summary>
    /// The same rule on a real run, which is where it earns its keep. The fixture is two lines, both
    /// characters of line 0 typed by 1500: the caret rolls forward onto line 1, but a decoder-built
    /// line's window runs to the NEXT line's start, so line 0 stays unsealed and being sung until
    /// 3000.
    ///
    /// <para>At 1600 the playhead is 1.2 characters into line 0 while the caret sits on line 1, and
    /// the coordinates the sweep is drawn from must be line 0's. Line 1's own playhead reads 0 there
    /// (clamped before its start), which is precisely the stuck sweep this pins against. Once line 0
    /// seals the two are one line again and the placement is the ordinary one.</para>
    ///
    /// <para>Backlog 218 gave this its own fixture rather than the twelve-second instrumental one,
    /// and that is a re-timing rather than a re-aiming: the rush bound lets a caret run at most
    /// <c>FLETCHER_DRAG_GRACE_MS</c> ahead of a line's cue, so on a long instrumental the vocal of
    /// the line behind is finished by the time the caret leaves it and the reading would be that
    /// line's clamped end, which is a far weaker thing to pin. Here line 1 comes due at 1500, while
    /// line 0 is still being sung, so every number below is the one it always was.</para>
    /// </summary>
    [Test]
    public void SungPlayheadTracksTheVocalWhileTheCaretIsParkedAhead()
    {
        var root = Harness();
        var parked = root.GetProperty("sungParked");
        var sealedUp = root.GetProperty("sungSealed");

        Assert.Multiple(() =>
        {
            Assert.That(Num(parked, "active"), Is.EqualTo(1));        // the caret rolled forward
            Assert.That(Num(parked, "nextUnsealed"), Is.EqualTo(0));  // the song is still on line 0
            Assert.That(Num(parked, "sungLine"), Is.EqualTo(0));
            Assert.That(Num(parked, "sungPos"), Is.EqualTo(1.2).Within(1e-9));
            // What riding the caret's row would have drawn: nothing, parked at the head of a line
            // whose first character is ten seconds away.
            Assert.That(Num(parked, "caretRowPos"), Is.EqualTo(0));

            Assert.That(Num(sealedUp, "active"), Is.EqualTo(1));
            Assert.That(Num(sealedUp, "nextUnsealed"), Is.EqualTo(1));
            Assert.That(Num(sealedUp, "sungLine"), Is.EqualTo(1));
            Assert.That(Num(sealedUp, "sungPos"), Is.EqualTo(Num(sealedUp, "caretRowPos")));
            Assert.That(Num(sealedUp, "sungPos"), Is.EqualTo(1));

            // Exactly one row carries a fill, and it is the sung one (LyricStage.setSungSweep).
            Assert.That(JsHarness.Doubles(parked, "sweepFills"), Is.EqualTo(new[] { 1.2, 0d }).Within(1e-9));
            Assert.That(JsHarness.Doubles(sealedUp, "sweepFills"), Is.EqualTo(new[] { 0d, 1d }).Within(1e-9));
        });
    }

    /// <summary>
    /// Backlog 223, the DRAG case: the mirror of the parked one above, and the one the seal cursor
    /// could not express. One character of line 0's two goes in, so drag protection holds line 0
    /// unsealed (and the caret on it) to FLETCHER_DRAG_GRACE_MS past its 3000 deadline, i.e. 4500.
    /// At 3200 the seal cursor is still pinned at 0 while the vocal has been on line 1 for 200 ms.
    ///
    /// <para>The playhead has to be on line 1: reading the cursor stranded it at line 0's tail, its
    /// position clamped to the end of a line the song had finished (caretRowPos 2, the frozen 100%
    /// sweep this pins against), while the row actually being sung got no head and no sweep. And the
    /// row the player is still reading must be zeroed rather than left claiming the vocals are on
    /// it, which is the one-row rule doing work that used to be invisible.</para>
    /// </summary>
    [Test]
    public void SungPlayheadMovesOnToTheSungRowWhileTheCaretDragsBehind()
    {
        var root = Harness();
        var dragging = root.GetProperty("sungDragging");

        Assert.Multiple(() =>
        {
            Assert.That(Num(dragging, "active"), Is.EqualTo(0));        // the caret is still on line 0
            Assert.That(Num(dragging, "nextUnsealed"), Is.EqualTo(0));  // and so is the drag-deferred seal cursor
            Assert.That(Num(dragging, "sungLine"), Is.EqualTo(1));      // but the song has moved on
            Assert.That(Num(dragging, "sungPos"), Is.EqualTo(0.4).Within(1e-9));
            // What riding the seal cursor gave instead: line 0's clamped end, a dead full sweep.
            Assert.That(Num(dragging, "caretRowPos"), Is.EqualTo(2));
            Assert.That(JsHarness.Doubles(dragging, "sweepFills"), Is.EqualTo(new[] { 0d, 0.4 }).Within(1e-9));
            // Both heads are drawn: the player still owes characters on line 0, and the song is one
            // row away, which is on the stack.
            Assert.That(Flag(dragging.GetProperty("shown"), "player"), Is.True);
            Assert.That(Flag(dragging.GetProperty("shown"), "sung"), Is.True);
        });
    }

    /// <summary>
    /// Backlog 223, the PARK case: both characters of line 0 are in by 1100, but backlog 218's rush
    /// bound refuses the roll until entry into line 1 opens (its 3000 activation less
    /// FLETCHER_DRAG_GRACE_MS, so 1500). The caret sits complete at the end of a line the vocal is
    /// still singing, and the playhead used to go dark for the whole park because both heads shared
    /// one boolean: the typing caret hides, which is right, and the map playhead stays lit over a
    /// sweep that is visibly still moving (0.4 of the way through the line at 1200).
    /// </summary>
    [Test]
    public void MapPlayheadStaysLitWhileTheRushBoundParksACompleteCaret()
    {
        var root = Harness();
        var park = root.GetProperty("sungParkedComplete");

        Assert.Multiple(() =>
        {
            Assert.That(Num(park, "active"), Is.EqualTo(0));
            Assert.That(Flag(park, "lineComplete"), Is.True);        // nothing left to type
            Assert.That(Num(park, "sungLine"), Is.EqualTo(0));       // and the song is still on that line
            Assert.That(Num(park, "sungPos"), Is.EqualTo(0.4).Within(1e-9));

            Assert.That(Flag(park.GetProperty("shown"), "player"), Is.False);
            Assert.That(Flag(park.GetProperty("shown"), "sung"), Is.True);
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

    // SyncQualityDecaysToTheOkWindowEdges (Judgement.SyncQuality) removed by backlog 251: the
    // browser's sync readout it fed is gone, and syncQuality/syncTintFill/SYNC_TINT_FLOOR no longer
    // exist in typebeat-player.js to test.

    /// <summary>
    /// The live HUD readouts over real engine runs. COMPLETION is every character of the map the
    /// player owes (word gaps included), and what the results rank keys off. Backlog 251 removed
    /// the browser's SYNC readout (a timing mean over resolved TIMED cells) along with its per-cell
    /// tint; this test used to pin both, and now pins completion alone.
    /// </summary>
    [Test]
    public void LiveHudReadoutsTrackTheRun()
    {
        // Backlog 319: the score cell is the standardised total (ScoreProcessor.TotalScore, what the
        // card shows and /play submits), not engine.score's internal points, and the accuracy cell
        // is JUDGED-only accuracy (ScoreProcessor.Accuracy), not the old 'typed' completion.
        var root = Harness();
        var perfect = root.GetProperty("perfectHud");
        var partial = root.GetProperty("partialHud");
        var late = root.GetProperty("lateHud");

        Assert.Multiple(() =>
        {
            foreach (var hud in new[] { perfect, partial, late })
            {
                Assert.That(Num(hud, "score"), Is.EqualTo(Num(hud, "computeTotalScore")));
                Assert.That(Num(hud, "accuracy"), Is.EqualTo(Num(hud, "baseScore") / Num(hud, "maximumBaseScore")).Within(1e-12));
            }

            Assert.That(Num(perfect, "score"), Is.EqualTo(1_000_000));
            Assert.That(Num(perfect, "engineScore"), Is.Not.EqualTo(1_000_000), "the internal points are on another scale");
            Assert.That(perfect.GetProperty("accuracyText").GetString(), Is.EqualTo("100.00%"));

            // One Great and four seal misses, all five judged: 300 / 1500.
            Assert.That(Num(partial, "accuracy"), Is.EqualTo(0.2).Within(1e-12));

            // Two judged: a Great and a Meh (+600 past its span), 350 / 600, while the card's
            // whole-map figure is 350 / 1500 and the keypress ratio reads a flat 1 (tier-blind).
            Assert.That(Num(late, "judged"), Is.EqualTo(2));
            Assert.That(Num(late, "accuracy"), Is.EqualTo(350.0 / 600).Within(1e-12));
            Assert.That(late.GetProperty("accuracyText").GetString(), Is.EqualTo("58.33%"));
            Assert.That(Num(late, "cardAccuracy"), Is.EqualTo(350.0 / 1500).Within(1e-12));
            Assert.That(Num(late, "liveAccuracy"), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Backlog 319: every lyric cell of the workhorse line pressed 400 ms off its span (Meh), the
    /// word gap untimed (Great). The accuracy cell reads the judged-only ratio after every press,
    /// floored the way FormatUtils.FormatAccuracy floors, and the score cell climbs on the card's
    /// scale and ends on exactly the total the card shows and /play submits.
    /// </summary>
    [Test]
    public void AMehHeavyRunReadsItsJudgedAccuracyLive()
    {
        var run = Harness().GetProperty("hudMehRun");
        var samples = run.GetProperty("samples");
        var expected = new[] { 50.0 / 300, 100.0 / 600, 400.0 / 900, 450.0 / 1200, 500.0 / 1500 };
        var texts = new[] { "16.66%", "16.66%", "44.44%", "37.50%", "33.33%" };

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("judgeTypes").EnumerateArray().Select(j => j.GetString()),
                Is.EqualTo(new[] { "Meh", "Meh", "Great", "Meh", "Meh" }));
            Assert.That(samples.GetArrayLength(), Is.EqualTo(5));

            double lastScore = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                var s = samples[i];
                Assert.That(Num(s, "accuracy"), Is.EqualTo(expected[i]).Within(1e-12), $"press {i}");
                Assert.That(s.GetProperty("accuracyText").GetString(), Is.EqualTo(texts[i]), $"press {i}");
                Assert.That(Num(s, "score"), Is.EqualTo(Num(s, "computeTotalScore")), $"press {i}");
                Assert.That(Num(s, "score"), Is.GreaterThan(lastScore), $"press {i}");
                lastScore = Num(s, "score");
            }

            var final = run.GetProperty("final");
            Assert.That(Flag(run, "finished"), Is.True);
            Assert.That(Num(final, "score"), Is.EqualTo(Num(final, "computeTotalScore")));
            Assert.That(Num(final, "score"), Is.EqualTo(168724), "cross-check: 500000 * (1/3) * comboProgress + 500000 * (1/3)^5");
            // A completed run judged every cell, so the judged ratio and the card's whole-map one agree.
            Assert.That(Num(final, "accuracy"), Is.EqualTo(Num(final, "cardAccuracy")).Within(1e-12));
        });
    }

    /// <summary>
    /// Backlog 319: the perfect run sampled before any press, after each, and after the seal. The
    /// score cell climbs strictly and lands on the card's 1,000,000.
    /// </summary>
    [Test]
    public void TheHudScoreClimbsToTheCardsTotal()
    {
        var samples = Harness().GetProperty("hudClimb").EnumerateArray().ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.Length.EqualTo(7));
            Assert.That(Num(samples[0], "score"), Is.EqualTo(0));
            Assert.That(samples[0].GetProperty("accuracyText").GetString(), Is.EqualTo("100.00%"), "nothing judged reads 1");
            for (int i = 1; i < 6; i++)
                Assert.That(Num(samples[i], "score"), Is.GreaterThan(Num(samples[i - 1], "score")), $"press {i}");
            foreach (var s in samples)
                Assert.That(Num(s, "score"), Is.EqualTo(Num(s, "computeTotalScore")));
            Assert.That(Num(samples[6], "score"), Is.EqualTo(1_000_000));
        });
    }

    /// <summary>
    /// Backlog 319: SongProgress over the PLAYABLE bounds (line 0's start to the latest line end
    /// plus seal grace), with an intro phase from the clock's own start. The fixture is backlog
    /// 308's pre-roll shape: a first line at 500, so the clock starts at -1500 and the intro runs
    /// through negative time. The Argon bar holds at 0 through the intro; the time text counts from
    /// the first line (negative in the intro) and freezes once the span has elapsed.
    /// </summary>
    [Test]
    public void SongProgressIsBoundedByTheLinesWithAnIntroPhase()
    {
        var root = Harness();
        var intro = root.GetProperty("progressIntro");
        var bounds = intro.GetProperty("bounds");
        var s = intro.GetProperty("samples").EnumerateArray().ToDictionary(e => Num(e, "time"));

        Assert.Multiple(() =>
        {
            Assert.That(Num(bounds, "clockStart"), Is.EqualTo(-1500));
            Assert.That(Num(bounds, "first"), Is.EqualTo(500));
            var lineEnds = intro.GetProperty("lineEnds").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Assert.That(Num(bounds, "last"), Is.EqualTo(lineEnds.Max()));

            // The intro: -1500..500, a quarter of the way at -1000, three quarters at 0.
            Assert.That(Flag(s[-1500], "isIntro"), Is.True);
            Assert.That(Num(s[-1500], "introProgress"), Is.EqualTo(0));
            Assert.That(Num(s[-1000], "introProgress"), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(Num(s[0], "introProgress"), Is.EqualTo(0.75).Within(1e-12));
            Assert.That(Flag(s[499], "isIntro"), Is.True);
            foreach (var t in new[] { -1500.0, -1000, -1, 0, 499 })
                Assert.That(Num(s[t], "barProgress"), Is.EqualTo(0), $"intro at {t}");
            Assert.That(s[-1500].GetProperty("elapsedText").GetString(), Is.EqualTo("-0:02"));
            Assert.That(s[0].GetProperty("elapsedText").GetString(), Is.EqualTo("-0:01"));

            // The playable span: 500..last.
            double last = Num(bounds, "last");
            Assert.That(Flag(s[500], "isIntro"), Is.False);
            Assert.That(Num(s[500], "barProgress"), Is.EqualTo(0));
            Assert.That(s[500].GetProperty("elapsedText").GetString(), Is.EqualTo("0:00"));
            Assert.That(Num(s[5000], "barProgress"), Is.EqualTo((5000 - 500) / (last - 500)).Within(1e-12));
            Assert.That(s[5000].GetProperty("elapsedText").GetString(), Is.EqualTo("0:04"));
            Assert.That(s[5000].GetProperty("remainingText").GetString(), Is.EqualTo("0:04"), "cross-check: last 9000 - 5000");
            Assert.That(Flag(s[6500], "textLive"), Is.True);

            // Past the last line: the bar is full and the text stops being rewritten.
            Assert.That(Num(s[99999], "barProgress"), Is.EqualTo(1));
            Assert.That(Flag(s[99999], "textLive"), Is.False);

            // A 30 s intro from a clock at 0.
            var longIntro = root.GetProperty("progressLongIntro");
            var ls = longIntro.GetProperty("samples").EnumerateArray().ToDictionary(e => Num(e, "time"));
            Assert.That(Num(longIntro.GetProperty("bounds"), "clockStart"), Is.EqualTo(0));
            Assert.That(Num(ls[15000], "introProgress"), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(ls[15000].GetProperty("elapsedText").GetString(), Is.EqualTo("-0:15"));
            Assert.That(Flag(ls[30000], "isIntro"), Is.False);

            // SongProgressInfo.formatTime and FormatUtils.FormatAccuracy.
            Assert.That(root.GetProperty("songTimeFormat").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(new[] { "0:00", "0:00", "0:01", "0:59", "1:00", "1:01", "60:00", "-0:00", "-0:01", "-0:01", "-1:01" }));
            Assert.That(root.GetProperty("accuracyFormat").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(new[] { "100.00%", "99.99%", "89.99%", "33.33%", "50.00%", "0.00%" }));
        });
    }

    /// <summary>
    /// Backlog 319: computeScore's all-Great combo portion is memoised per engine because the HUD
    /// asks every frame. The memo must not change what computeScore answers: over a real run, at
    /// every frame, the cached portion equals the loop it replaced and the total equals the total
    /// restated on the uncached portion; a fresh engine gets the same value; and an engine whose
    /// lines are swapped recomputes instead of answering for cells it never counted.
    /// </summary>
    [Test]
    public void TheCachedComputeScoreEqualsTheUncachedOne()
    {
        var c = Harness().GetProperty("maxComboCache");

        Assert.Multiple(() =>
        {
            Assert.That(Num(c, "frames"), Is.GreaterThan(500));
            Assert.That(Num(c, "judged"), Is.GreaterThan(0));
            Assert.That(Num(c, "maxTotal"), Is.GreaterThan(Num(c, "minTotal")), "the run must move the total");
            Assert.That(Num(c, "portionMismatches"), Is.EqualTo(0));
            Assert.That(Num(c, "totalMismatches"), Is.EqualTo(0));
            Assert.That(Num(c, "freshEnginePortion"), Is.EqualTo(Num(c, "portion")));
            Assert.That(Num(c, "swappedPortion"), Is.EqualTo(Num(c, "swappedExpected")));
            Assert.That(Num(c, "swappedPortion"), Is.Not.EqualTo(Num(c, "portion")));
            Assert.That(Num(c, "restoredPortion"), Is.EqualTo(Num(c, "portion")));
        });
    }

    // TheUntimedSpaceIsNeutralInTheLiveSyncReadout removed by backlog 251: it existed entirely to
    // pin backlog 148's sync-mean exemption for the word gap, and that mean (and the readout it fed)
    // is gone from the browser now. judgedDelta is still zeroed for a space in typebeat-core.js (see
    // its own comments there), but nothing here reads it back for a display readout any more.

    private static string Cls(JsonElement paint, int i) => paint[i].GetProperty("cls").GetString()!;

    private static string Glyph(JsonElement paint, int i) => paint[i].GetProperty("glyph").GetString()!;

    // SyncTintRampIsFlooredMonotonicAndExactAtItsEnds removed by backlog 251: the ramp it pinned
    // (LyricLineDisplay.CorrectCharColour, re-expressed here) no longer exists, and neither do
    // syncTintFill or SYNC_TINT_FLOOR in typebeat-player.js.

    // CorrectCharsAreFilledByHowInSyncTheKeypressWas removed by backlog 251 for the same reason: it
    // pinned exact ramp percentages (95.83%, 75.00%, ...) that cellFill() no longer produces because
    // cellFill() no longer exists. The class assertions it also carried ('a' and 'b' both tb-c-hit)
    // are not lost: OffTimeMissedAndBackspacedCellsKeepTheirOwnClass below still exercises this
    // same mixedPaint fixture for its own (non-sync) class checks.

    /// <summary>
    /// Everything OFF-TIME, MISSED and BACKSPACED cells have in common: none of them carry the
    /// correct-cell class, each for its own reason.
    ///
    /// <para>An OFF-TIME press (Premature/Lagging) still lands the cell Correct, but the browser
    /// gives it .tb-c-off's flat warn tint: a browser-only affordance the desktop does not have.
    /// MISSED and UNTYPED cells have no keypress at all, and a BACKSPACE clears the judged delta,
    /// so the class has to come off with it rather than leaving the glyph in whatever state it had
    /// earned. (Before backlog 251 removed the sync tint, this test also pinned that each of these
    /// took no ramp fill; there is no ramp left to take one from.)</para>
    /// </summary>
    [Test]
    public void OffTimeMissedAndBackspacedCellsKeepTheirOwnClass()
    {
        var root = Harness();
        var mixed = root.GetProperty("mixedPaint");
        var sealedPaint = root.GetProperty("sealedPaint");
        var backspaced = root.GetProperty("backspacedPaint");

        Assert.Multiple(() =>
        {
            // 'd' at +1300ms past its syllable's span, beyond the 1200ms Ok edge: correct, but off-time.
            Assert.That(Cls(mixed, 4), Is.EqualTo("tb-c tb-c-off"));

            Assert.That(Cls(sealedPaint, 0), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Cls(sealedPaint, 1), Is.EqualTo("tb-c tb-c-miss"));

            // Typed, then backspaced: back to untyped.
            Assert.That(Cls(backspaced, 0), Is.EqualTo("tb-c tb-c-todo"));
        });
    }

    /// <summary>
    /// A wrong letter typed into the WORD GAP (backlog 181) shows the character that went in, in
    /// the same error red a wrong lyric cell wears. It is the one cell whose glyph is not fixed for
    /// the whole play, and the exception is forced rather than chosen: a lyric cell keeps showing
    /// its own character because a mistyped line still has to read as the line it was meant to be,
    /// but the gap's own character is a space, and a space painted red is nothing at all.
    ///
    /// <para>Mirrors <c>LyricLineDisplay.CellGlyph</c>, whose rule is exactly this one: the typed
    /// char for a Wrong SPACE cell, the expected char otherwise. The gap in every other state is a
    /// space again, the typo once backspaced included, which is the second half asserted here.</para>
    /// </summary>
    [Test]
    public void AWrongWordGapShowsTheTypedCharacterInErrorRed()
    {
        var root = Harness();
        var typo = root.GetProperty("gapTypoPaint");
        var erased = root.GetProperty("gapTypoErasedPaint");

        Assert.Multiple(() =>
        {
            // Cell 2 is the word gap of "ab cd". 'x' went in, the seal left it wrong, and it is
            // drawn as the typo rather than as an invisible red space. The second class is backlog
            // 185's dimming lane, carried only by a gap (see TheDimmingIsKeyedOnTheGapNotTheKey).
            Assert.That(Cls(typo, 2), Is.EqualTo("tb-c tb-c-wrong tb-c-wrong-gap"));
            Assert.That(Glyph(typo, 2), Is.EqualTo("x"));

            // Nothing else moved: the letters either side are ordinary hits still showing their own
            // characters, which is what says the typo did not shift the line.
            Assert.That(Glyph(typo, 1), Is.EqualTo("b"));
            Assert.That(Cls(typo, 3), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Glyph(typo, 3), Is.EqualTo("c"));

            // Backspaced: the cell is untyped again, so the glyph goes back to the space with it.
            Assert.That(Cls(erased, 2), Is.EqualTo("tb-c tb-c-todo"));
            Assert.That(Glyph(erased, 2), Is.EqualTo(" "));

            // And a gap nobody has touched shows a space in every other run here, which is what
            // makes the line above an exception rather than the rule.
            Assert.That(Glyph(root.GetProperty("mixedPaint"), 2), Is.EqualTo(" "));
            Assert.That(Glyph(root.GetProperty("sealedPaint"), 2), Is.EqualTo(" "));
        });
    }

    /// <summary>
    /// Backlog 185: a wrong WORD GAP is dimmed, because the letter it shows is standing where a
    /// space was, and a burst of them welds the words either side into one run. A wrong LYRIC cell
    /// is not, because it shows its own character and takes no boundary away.
    ///
    /// <para>The predicate is what the cell EXPECTS, not what was pressed, and the cell below is
    /// the one that separates the two: a space typed inside a word. Space-skip is hardcoded off in
    /// the browser, so that press is typed through as an ordinary wrong character on a lyric cell,
    /// which still shows its own 'b' in red. Keying on the typed char instead would dim it and
    /// start dimming half the wrong cells on the line.</para>
    ///
    /// <para>The dimming itself is CSS, ported by OUTCOME rather than by literal: the desktop dims
    /// its own cell-alpha lane, /play dims with this site's own idiom, the same one .tb-c-miss
    /// already uses.</para>
    /// </summary>
    [Test]
    public void TheDimmingIsKeyedOnTheGapNotTheKey()
    {
        var root = Harness();
        var midWord = root.GetProperty("midWordSpacePaint");

        string css = File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css"));

        Assert.Multiple(() =>
        {
            // Cell 1 of "ab cd" took a SPACE where 'b' was expected: wrong, but a lyric cell, so it
            // keeps its own glyph, the undimmed error red, and no gap class.
            Assert.That(Cls(midWord, 1), Is.EqualTo("tb-c tb-c-wrong"));
            Assert.That(Glyph(midWord, 1), Is.EqualTo("b"));

            // The gap on that same run is untouched, so the class above is not simply missing.
            Assert.That(Cls(midWord, 2), Is.EqualTo("tb-c tb-c-todo"));

            // And the class the gap typo does carry has to mean something: the dim lives here.
            Assert.That(css, Does.Contain(".tb-c-wrong-gap { opacity: .55; }"));
        });
    }

    /// <summary>
    /// FREESTYLE cells keep their own identity colour rather than the ordinary hit class's,
    /// matching the desktop exclusion and for the same reason: their colour is an IDENTITY signal
    /// ("this slot was free") that has to keep saying so for the rest of the play, not a state
    /// signal. (Before backlog 251 removed the sync tint, this test also pinned that the freestyle
    /// cell took no ramp fill while the ordinary cell beside it did; there is no ramp left.)
    /// </summary>
    [Test]
    public void FreestyleCellsKeepTheirOwnIdentityColour()
    {
        var paint = Harness().GetProperty("freestylePaint");

        Assert.Multiple(() =>
        {
            Assert.That(Cls(paint, 0), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Cls(paint, 1), Is.EqualTo("tb-c tb-c-hit tb-c-free"));
        });
    }

    /// <summary>
    /// The site's own tokens, not the desktop's colour literals: /play re-skins gameplay onto the
    /// site palette, so the desktop's grey-to-off-white UntypedChar/TypedChar literals must never
    /// appear here. Before backlog 251 removed the sync tint, this test also pinned the color-mix()
    /// rule that read the ramp position and the cascade order that let .tb-c-free beat it; both are
    /// gone along with the ramp, leaving the flat colours it mixed between.
    /// </summary>
    [Test]
    public void HitAndOffColoursAreTheSiteTokensNotTheDesktopLiterals()
    {
        string css = File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css"));

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain(".tb-c-hit  { color: var(--violet); }"));
            Assert.That(css, Does.Contain(".tb-c-off  { color: var(--warn); }"));

            // The desktop's UntypedChar / TypedChar. Copying them across would collide with
            // meanings this palette has already assigned.
            Assert.That(css, Does.Not.Contain("#646669"));
            Assert.That(css, Does.Not.Contain("#d1d0c5"));
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

    // =============================================================================================
    // INSTRUMENTAL SKIP (backlog 230). Unlike everything above, this is NOT display-only: the
    // browser's skip spends the same allowance the anti-cheat play-time gate refunds, so the JS port
    // of the rule is a THIRD copy of something CLAUDE.md's mirror table already pins twice. These
    // tests hold it against the SECOND copy directly, by running the server's own
    // InstrumentalGaps.Compute over the same lyric fixtures the harness feeds the browser.
    // =============================================================================================

    /// <summary>The four constants, off the server mirror rather than off a memory of them.</summary>
    [Test]
    public void GapConstantsMatchTheServerMirror()
    {
        var c = Harness().GetProperty("gapConstants");

        Assert.Multiple(() =>
        {
            Assert.That(Num(c, "MIN_GAP_MS"), Is.EqualTo(InstrumentalGaps.MIN_GAP_MS));
            Assert.That(Num(c, "GAP_START_SETTLE_MS"), Is.EqualTo(InstrumentalGaps.GAP_START_SETTLE_MS));
            Assert.That(Num(c, "MIN_SKIP_WINDOW_MS"), Is.EqualTo(InstrumentalGaps.MIN_SKIP_WINDOW_MS));
            Assert.That(Num(c, "SKIP_LEAD_MS"), Is.EqualTo(InstrumentalGaps.SKIP_LEAD_MS));
        });
    }

    /// <summary>
    /// THE PARITY PIN. Every gap fixture in InstrumentalGapsTest, computed by BOTH implementations
    /// and compared gap for gap: the qualification threshold, the settle, the skip lead, the
    /// usability filter, and the total allowance the server stores as beatmaps.skippable_s.
    ///
    /// <para>The expected side is computed, not transcribed, so this cannot go stale against the
    /// C# and it fails the moment either side moves. The one literal here is the headline case's
    /// own numbers (gapStart 3000 / skipTarget 9000 / 6000 removable), which are the game's, from
    /// its own InstrumentalGapsTest, and are pinned so that a change agreed on BOTH sides of the
    /// mirror still has to be a deliberate one.</para>
    /// </summary>
    [Test]
    public void GapComputationMatchesTheServerMirrorOnEveryFixture()
    {
        var root = Harness();

        AssertGapParity(root, "gapExactlyTen", GapFixtures.ExactlyTen);
        AssertGapParity(root, "gapOneMsShort", GapFixtures.OneMsShort);
        AssertGapParity(root, "gapNoUsableWindow", GapFixtures.NoUsableWindow);
        AssertGapParity(root, "gapLongIntro", GapFixtures.LongIntro);
        AssertGapParity(root, "gapTwoOfFour", GapFixtures.TwoOfFour);
        AssertGapParity(root, "gapSingleLine", GapFixtures.Single);

        var exactly = root.GetProperty("gapExactlyTen");
        var gaps = exactly.GetProperty("gaps");
        var four = root.GetProperty("gapTwoOfFour");

        Assert.Multiple(() =>
        {
            // The game's own golden window, reached by the browser.
            Assert.That(Num(gaps[0], "gapStartTime"), Is.EqualTo(3000));
            Assert.That(Num(gaps[0], "activationTime"), Is.EqualTo(12000));
            Assert.That(Num(gaps[0], "skipTarget"), Is.EqualTo(9000));
            Assert.That(Num(gaps[0], "skippableMs"), Is.EqualTo(6000));

            // And the four-line map's 82 s allowance, which is what PlayTimeGate refunds.
            Assert.That(Num(four, "skippableMs") / 1000, Is.EqualTo(82).Within(1e-9));

            // The weird-data path both implementations carry: a word overrunning its line's sing
            // end pushes the skip period past the target, and the gap is dropped rather than shown.
            Assert.That(Num(root.GetProperty("gapNoUsableWindow"), "lastTypeableTarget"), Is.EqualTo(9250).Within(1e-9));
        });
    }

    /// <summary>
    /// The INTRO skip, which is not an InstrumentalGaps gap on either side, on a 30 s intro whose
    /// first word is sung AT line 0's stamp: the desktop's separate intro SkipOverlay lands at
    /// GameplayStartTime - MINIMUM_SKIP_TIME, i.e. line 0's start less 3000, and removes only run-up
    /// drain_length_s already excludes (so the gate never sees it). This fixture skips to 27000
    /// while contributing nothing to skippable_s. Because its line start and first vocal coincide it
    /// cannot tell which of the two the skip is anchored on; that is
    /// <see cref="IntroSkipIsAnchoredOnLineZerosStart_NotItsFirstVocal"/>'s job.
    /// </summary>
    [Test]
    public void AThirtySecondIntroWhoseFirstWordOpensLineZeroSkipsTo27000_AndCostsNoAllowance()
    {
        var root = Harness();
        var intro = root.GetProperty("gapLongIntro");
        var window = JsHarness.Doubles(root, "introWindow").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(Num(intro, "firstVocalTime"), Is.EqualTo(30000));
            Assert.That(Num(intro, "introSkipTarget"), Is.EqualTo(27000));
            Assert.That(Num(intro, "skippableMs"), Is.Zero, "the intro is not part of the allowance");
            Assert.That(InstrumentalGaps.SkippableSeconds(Lines(GapFixtures.LongIntro)), Is.Zero,
                "and the server agrees, which is why the intro skip needs no refund");

            // Live from the start of the map right up to (not including) its own target; -1 is the
            // harness's stand-in for "no skip offered".
            Assert.That(window[0], Is.EqualTo(27000));
            Assert.That(window[1], Is.EqualTo(27000));
            Assert.That(window[2], Is.EqualTo(-1), "at the target itself there is nothing left to skip");
        });
    }

    /// <summary>
    /// The intro skip's ANCHOR (backlog 308), on a line 0 stamped at 10000 whose first word is not
    /// sung until 14000. The desktop lands at GameplayStartTime - MINIMUM_SKIP_TIME =
    /// (line0.StartTime - 2000) - 1000 = 7000, before drain_length_s starts, so the play-time gate
    /// never counts what it removes. The old browser rule (first vocal - 3000 = 11000) landed a
    /// second INSIDE the drain, removing time the gate counts, which could store an honest browser
    /// play unranked. Its clock starts at 0 (line 0 is well past 2 s).
    /// </summary>
    [Test]
    public void IntroSkipIsAnchoredOnLineZerosStart_NotItsFirstVocal()
    {
        var root = Harness();
        var late = root.GetProperty("clockLateFirstWord");
        var window = late.GetProperty("window").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var serverLines = Lines(LateFirstWordLyrics);

        Assert.Multiple(() =>
        {
            Assert.That(Num(late, "lineStart"), Is.EqualTo(10000));
            Assert.That(Num(late, "firstVocalTime"), Is.EqualTo(14000), "the fixture has to separate the two anchors");
            Assert.That(Num(late, "gameplayStartTime"), Is.EqualTo(0));
            Assert.That(Num(late, "introSkipTarget"), Is.EqualTo(7000), "line 0's start less SKIP_LEAD_MS, not 11000");
            Assert.That(Num(late, "introSkipTargetDefaulted"), Is.EqualTo(7000), "the lines-only call means no AudioLeadIn");

            // The server's own drain starts at line 0's start, and the skip lands in front of it.
            Assert.That(serverLines[0].StartTime, Is.EqualTo(10000));
            Assert.That(Num(late, "introSkipTarget"), Is.LessThanOrEqualTo(serverLines[0].StartTime),
                "the intro skip must not remove drain time the play-time gate counts");

            Assert.That(window[0], Is.EqualTo(7000));
            Assert.That(window[1], Is.EqualTo(7000));
            Assert.That(window[2], Is.EqualTo(-1), "at the target itself there is nothing left to skip");
            Assert.That(window[3], Is.EqualTo(-1), "and nothing past it, where the old rule still offered 11000");
        });
    }

    /// <summary>
    /// The desktop's CLOCK START (backlog 308): MasterGameplayClockContainer.findEarliestStartTime
    /// over DrawableRuleset.GameplayStartTime, min(0, line0.StartTime - 2000, line0.StartTime -
    /// AudioLeadIn when positive), with no storyboard term on /play. A first vocal at 500 pre-rolls
    /// from -1500 whether the map carries no AudioLeadIn or the 2000 the importer writes for exactly
    /// this shape; a longer lead-in outreaches the 2000 term. The intro skip is offered only when it
    /// lands after the clock start, which is where the desktop's intro SkipOverlay expires at once:
    /// none for the first two, and a skip through negative time to -2500 for the third.
    /// </summary>
    [Test]
    public void TheClockStartsWhereTheDesktopsPreRollDoes()
    {
        var root = Harness();
        var early = root.GetProperty("clockEarlyVocal").EnumerateArray().ToArray();
        var longIntro = root.GetProperty("clockLongIntro");

        Assert.Multiple(() =>
        {
            Assert.That(Num(early[0], "audioLeadIn"), Is.Zero);
            Assert.That(Num(early[0], "gameplayStartTime"), Is.EqualTo(-1500));
            Assert.That(Num(early[0], "introSkipTarget"), Is.EqualTo(-1));

            Assert.That(Num(early[1], "audioLeadIn"), Is.EqualTo(2000), "buildBeatmap carries AudioLeadIn through");
            Assert.That(Num(early[1], "gameplayStartTime"), Is.EqualTo(-1500));
            Assert.That(Num(early[1], "introSkipTarget"), Is.EqualTo(-1));

            Assert.That(Num(early[2], "audioLeadIn"), Is.EqualTo(5000));
            Assert.That(Num(early[2], "gameplayStartTime"), Is.EqualTo(-4500));
            Assert.That(Num(early[2], "introSkipTarget"), Is.EqualTo(-2500));

            Assert.That(Num(longIntro, "gameplayStartTime"), Is.Zero);
            Assert.That(Num(longIntro, "introSkipTarget"), Is.EqualTo(27000));
        });
    }

    // The clockLateFirstWord fixture's lyrics, as the harness builds them.
    private const string LateFirstWordLyrics =
        """
        {"version":2,"song_end_ms":60000,"granularity":"Word"}
        {"text":"ab","start_ms":10000,"end_ms":15000,"words":[{"text":"ab","start_ms":14000,"end_ms":15000,"score":1}]}
        {"text":"cd","start_ms":30000,"end_ms":31000,"words":[{"text":"cd","start_ms":30000,"end_ms":31000,"score":1}]}
        """;

    /// <summary>
    /// The skip window's edges on the headline map: live from gapStart, dead from skipTarget. Half
    /// open at both ends the way the desktop's SkipOverlay lifetime is (visible over
    /// [skipStartTime, fadeOutBeginTime]).
    /// </summary>
    [Test]
    public void TheSkipIsOfferedOnlyInsideItsOwnWindow()
    {
        var window = JsHarness.Doubles(Harness(), "skipWindow").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(window[0], Is.EqualTo(-1), "one ms before the settle, nothing is offered");
            Assert.That(window[1], Is.EqualTo(9000), "at gapStart the skip opens");
            Assert.That(window[2], Is.EqualTo(9000), "one ms before the target it is still live");
            Assert.That(window[3], Is.EqualTo(-1), "at the target it closes");
        });
    }

    /// <summary>
    /// THE TRIGGER GATE, mirrored from TypeBeatPlayfield's key handler: a typeable key is swallowed
    /// for TYPING whenever a line is active and incomplete, and falls through to the skip only when
    /// no line is active, or the active line is complete with no live retype selection. That is what
    /// keeps Space a word-gap character, and (see the next test) what keeps the WPM clock still.
    /// </summary>
    [Test]
    public void SpaceReachesTheSkipOnlyWhereTheDesktopWouldFallThrough()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Flag(root, "skipAllowedIdle"), Is.True, "no line active");
            Assert.That(Flag(root, "skipAllowedComplete"), Is.True, "the active line is complete");
            Assert.That(Flag(root, "skipAllowedCompleteWithSelection"), Is.False, "a live retype selection suspends it");
            Assert.That(Flag(root, "skipAllowedTyping"), Is.False, "a line still owed consumes the key");
            Assert.That(Flag(root, "skipAllowedTypingWithSelection"), Is.False);

            // The line the chip and the skip look up. The seal cursor alone answers 0 for a caret
            // parked complete on line 0, which is the line the player has just FINISHED.
            Assert.That(Num(root, "upcomingIdle"), Is.EqualTo(0));
            Assert.That(Num(root, "upcomingParkedComplete"), Is.EqualTo(1));
            Assert.That(Num(root, "upcomingOnItsOwnLine"), Is.EqualTo(1));
        });

        var parked = root.GetProperty("spaceParked");
        var typing = root.GetProperty("spaceTyping");
        var wordGap = root.GetProperty("spaceAsWordGap");

        Assert.Multiple(() =>
        {
            // Line 0 fully typed, sitting in the twelve-second instrumental: the skip is live.
            Assert.That(Flag(parked, "lineComplete"), Is.True);
            Assert.That(Flag(parked, "skipAllowed"), Is.True);
            Assert.That(Num(parked, "skipTarget"), Is.EqualTo(9000));

            // One character of two typed: same clock, same map, no skip.
            Assert.That(Flag(typing, "lineComplete"), Is.False);
            Assert.That(Flag(typing, "skipAllowed"), Is.False);
            Assert.That(typing.GetProperty("skipTarget").ValueKind, Is.EqualTo(JsonValueKind.Null));

            // And on a line with a word gap still to type, the press IS that gap's character.
            Assert.That(Flag(wordGap, "skipAllowed"), Is.False);
            Assert.That(wordGap.GetProperty("gapCellExpected").GetString(), Is.EqualTo(" "));
            Assert.That(wordGap.GetProperty("gapCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(Num(wordGap, "caretAfter"), Is.EqualTo(Num(wordGap, "caretBefore") + 1));
        });

        // THE PARKED UNTOUCHED HEAD (backlog 305), the row the table above cannot express: line 0
        // typed out, the rush bound has handed the caret to the head of line 1 at 10500, and the
        // song is still far behind it. skipAllowed reads "typing" (a line is active and
        // incomplete), which is why the old handler typed the space into line 1 as a word skip.
        // TypeBeatPlayfield drops it instead (FletcherEnabled, !SongIsOnTheCaretsLine,
        // ActiveLineUntouched), and so does the router now: swallowed, no engine call, no skip
        // (none is live this close to the line), line 1 untouched, and the next letter types.
        var head = root.GetProperty("spaceParkedHead");

        Assert.Multiple(() =>
        {
            Assert.That(head.GetProperty("at").GetProperty("line").GetInt32(), Is.EqualTo(1));
            Assert.That(head.GetProperty("at").GetProperty("cell").GetInt32(), Is.EqualTo(0));
            Assert.That(Flag(head, "activeLineUntouched"), Is.True);
            Assert.That(Flag(head, "songIsOnTheCaretsLine"), Is.False);
            Assert.That(Flag(head, "skipAllowed"), Is.False, "the fall-through predicate alone would have typed it");
            Assert.That(Flag(head, "dropped"), Is.True);
            Assert.That(Flag(head, "spacePrevented"), Is.True, "the key is swallowed, not left to scroll the page");
            Assert.That(Num(head, "spaceEngineCalls"), Is.Zero, "no processKey for the dropped space");
            Assert.That(Num(head, "skipsTaken"), Is.Zero);
            Assert.That(JsHarness.Strings(head, "cellsAfterSpace"), Is.EqualTo(new[] { "correct", "untyped" }),
                "line 1's first word is intact: no abandoned cells, no word skip");
            Assert.That(head.GetProperty("letterLanded").GetString(), Is.EqualTo("correct"));
            Assert.That(Flag(head, "untouchedAfterLetter"), Is.False);
            Assert.That(Num(head, "combo"), Is.EqualTo(3), "a, b and c: the dropped space broke nothing");
        });
    }

    /// <summary>
    /// THE MANUAL NEWLINE'S SPACE REACHES THE ENGINE BEFORE THE SKIP (backlog 307), in the desktop's
    /// order: line 0 typed out and parked inside the instrumental's live skip window, the first Space
    /// is the newline (the engine takes it, no skip), the caret lands on line 1 AWAITING its window,
    /// and only the SECOND Space, meeting the parked-head drop, falls through to the skip. The skip's
    /// WPM-clock guard is untouched: it still fires only from a state the clock does not run in.
    /// </summary>
    [Test]
    public void TheManualNewlineTakesTheSpaceBeforeTheSkipDoes()
    {
        var run = Harness().GetProperty("spaceNewlineBeforeTheSkip");
        var parkedOn = run.GetProperty("parkedOn");
        var first = run.GetProperty("afterFirst");
        var second = run.GetProperty("afterSecond");

        Assert.Multiple(() =>
        {
            Assert.That(Num(parkedOn, "line"), Is.EqualTo(0));
            Assert.That(Flag(parkedOn, "complete"), Is.True, "the finished caret parks: no roll, no snap");
            Assert.That(Num(run, "skipLiveBefore"), Is.EqualTo(9000), "and the skip is live, so the order is what decides");

            Assert.That(first.GetProperty("spaceCalls").EnumerateArray().Select(v => v.GetBoolean()), Is.EqualTo(new[] { true }),
                "the first Space went to the engine, once, and was the newline");
            Assert.That(Num(first, "skips"), Is.Zero, "so it was not the skip");
            Assert.That(Flag(first, "prevented"), Is.True);
            Assert.That(Num(first, "line"), Is.EqualTo(1));
            Assert.That(Num(first, "cell"), Is.EqualTo(0));
            Assert.That(Flag(first, "awaiting"), Is.True, "entry into line 1 opens at 10500, so the line waits");

            Assert.That(Num(second, "engineCalls"), Is.Zero, "the second Space is the parked-head drop");
            Assert.That(JsHarness.Doubles(second, "skips"), Is.EqualTo(new[] { 9000d }), "and it is that one that takes the skip");
            Assert.That(Num(second, "line"), Is.EqualTo(1));
            Assert.That(JsHarness.Strings(run, "cellsOfLineOne"), Is.EqualTo(new[] { "untyped", "untyped" }), "nothing was typed into the waiting line");
        });
    }

    /// <summary>
    /// The modifier rules of the router (backlog 305), against the desktop's default bindings under
    /// KeyCombinationMatchingMode.Any: Ctrl+Backspace and Ctrl+A fire with Alt or AltGr (Ctrl plus
    /// Alt in a browser) or Shift also held, never with Meta; Enter reaches the line skip under Ctrl
    /// or Alt, never under Meta (backlog 283).
    /// </summary>
    [Test]
    public void TheGesturesMatchTheDesktopsAnyModifierBindings()
    {
        var root = Harness();
        var matrix = root.GetProperty("gestureMatrix").EnumerateArray()
                         .ToDictionary(r => r.GetProperty("key").GetString() + ":" + r.GetProperty("mods").GetString(), r => r.GetProperty("gesture").GetBoolean());

        Assert.Multiple(() =>
        {
            Assert.That(matrix["Backspace:"], Is.False, "plain Backspace is the single erase, not the gesture");
            Assert.That(matrix["Backspace:ctrl"], Is.True);
            Assert.That(matrix["Backspace:alt+ctrl"], Is.True, "AltGr+Backspace");
            Assert.That(matrix["Backspace:alt"], Is.False, "Alt alone does not satisfy a Ctrl binding");
            Assert.That(matrix["Backspace:ctrl+meta"], Is.False);
            Assert.That(matrix["Backspace:ctrl+shift"], Is.True);
            Assert.That(matrix["a:ctrl"], Is.True);
            Assert.That(matrix["a:alt+ctrl"], Is.True, "Ctrl+Alt+A");
            Assert.That(matrix["A:ctrl+shift"], Is.True);
            Assert.That(matrix["a:alt"], Is.False);
            Assert.That(matrix["a:meta"], Is.False);
            Assert.That(matrix["a:ctrl+meta"], Is.False);

            foreach (var name in new[] { "enterPlain", "enterCtrl", "enterAlt" })
            {
                var enter = root.GetProperty(name);
                Assert.That(Flag(enter, "reached"), Is.True, $"{name}: reaches processEnter");
                Assert.That(Flag(enter, "prevented"), Is.True, $"{name}: an effective skip is swallowed");
                Assert.That(Num(enter, "caret"), Is.EqualTo(5), $"{name}: the caret parks past the line");
            }

            var meta = root.GetProperty("enterMeta");
            Assert.That(Flag(meta, "reached"), Is.False, "Meta+Enter is the browser's");
            Assert.That(Flag(meta, "prevented"), Is.False);
            Assert.That(Num(meta, "caret"), Is.EqualTo(1));

            var altGr = root.GetProperty("altGrBackspace");
            Assert.That(Num(altGr, "before"), Is.EqualTo(2));
            Assert.That(Num(altGr, "after"), Is.EqualTo(0), "AltGr+Backspace takes the whole word");
            Assert.That(Flag(altGr, "prevented"), Is.True);
        });
    }

    /// <summary>
    /// The router's two small mirrors of the desktop key handler (backlog 305): C# Math.Round's
    /// banker's rounding (JS Math.round sends every half up), and the Gatekeeper erase gate
    /// (!AllowWrongInput and no live selection), under which a plain erase is inert while an erase
    /// over a selection still collapses it.
    /// </summary>
    [Test]
    public void TheRouterRoundsAndGatesErasesAsTheDesktopDoes()
    {
        var root = Harness();
        var gate = root.GetProperty("gatekeeperErase");

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(root, "roundHalfEven"),
                Is.EqualTo(new[] { 2000.5, 2001.5, 2000.49, 2000.51, 5510.5, 9600.5, -0.5, -1.5, 3.3 }.Select(x => Math.Round(x))));

            Assert.That(Num(gate, "plainErases"), Is.Zero, "no engine erase under Gatekeeper without a selection");
            Assert.That(Num(gate, "caretAfterPlain"), Is.EqualTo(2));
            Assert.That(Num(gate, "caretAfterSelection"), Is.EqualTo(1), "the selection is still collapsed");
            Assert.That(gate.GetProperty("selectionLeft").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    /// <summary>
    /// THE WPM-CLOCK TRAP, pinned. TypingEngine accrues activeTimeMs only while the active line is
    /// incomplete, so a skip taken from an allowed state adds nothing to the clock the submitted WPM
    /// is computed from, and the browser stays byte-comparable with the desktop. The second arm is
    /// the non-vacuity: the very same six seconds crossed with the line still owed DO land on the
    /// clock, which is exactly what a skip fired from the wrong state would have injected.
    /// </summary>
    [Test]
    public void ASkipFromAnAllowedStateLeavesTheWpmClockAlone()
    {
        var root = Harness();
        var parked = root.GetProperty("activeTimeParked");
        var typing = root.GetProperty("activeTimeTyping");

        Assert.Multiple(() =>
        {
            Assert.That(Num(parked, "moved"), Is.Zero, "a complete line's caret is not typing");
            Assert.That(Num(parked, "after"), Is.EqualTo(Num(parked, "before")));

            Assert.That(Num(typing, "moved"), Is.EqualTo(6000).Within(1e-9),
                "the same span with the line owed is real typing time, which is why the gate matters");
        });
    }

    // ---- the first-clear Discord nudge (backlog 289) ----
    //
    // Two files, one feature: play.js decides (it owns the localStorage flag and the invite URL the
    // stage root carries) and typebeat-player.js renders the answer inside showResults. The harness
    // drives both halves in the order a real results card does, with a fake storage, so these are
    // pins on the actual shipped scripts rather than on a description of them.

    /// <summary>
    /// A browser's FIRST cleared map gets the invitation, and only that once: showing it spends the
    /// flag (<c>tb_discord_nudged</c>), so the next clear, including a "play again" on the same
    /// mounted player, renders no block at all. The flag is spent on SHOW rather than on click,
    /// which is why the second clear is silent even though nobody followed the link.
    /// </summary>
    [Test]
    public void TheFirstClearedMapShowsTheDiscordNudgeExactlyOnce()
    {
        var root = Harness();
        var observed = root.GetProperty("nudgeFirstClear");
        string invite = root.GetProperty("nudgeUrl").GetString()!;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("discordNudgeKey").GetString(), Is.EqualTo("tb_discord_nudged"));

            Assert.That(observed.GetProperty("first").GetProperty("url").GetString(), Is.EqualTo(invite),
                "the first clear is invited");
            Assert.That(observed.GetProperty("first").GetProperty("html").GetString(), Is.Not.Null,
                "and the card gets a block to render");
            Assert.That(observed.GetProperty("flag").GetString(), Is.EqualTo("1"),
                "showing it is what spends the flag");

            Assert.That(observed.GetProperty("second").GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "a second clear is not asked again");
            Assert.That(observed.GetProperty("second").GetProperty("html").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    /// <summary>
    /// A FAILED run neither shows the nudge nor consumes it: nothing is written to storage at all, so
    /// the clear that follows is still the browser's first clear and still gets its one invitation.
    /// </summary>
    [Test]
    public void AFailedRunNeitherShowsTheNudgeNorSpendsIt()
    {
        var root = Harness();
        var observed = root.GetProperty("nudgeAfterFail");
        string invite = root.GetProperty("nudgeUrl").GetString()!;

        Assert.Multiple(() =>
        {
            Assert.That(observed.GetProperty("failed").GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "a fail is not the moment to ask");
            Assert.That(observed.GetProperty("flagAfterFail").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(Num(observed, "storedKeys"), Is.Zero, "a fail writes nothing at all");

            Assert.That(observed.GetProperty("thenCleared").GetProperty("url").GetString(), Is.EqualTo(invite),
                "so the clear after it is still the first clear");
        });
    }

    /// <summary>
    /// The storage arm that must never cost the player their results: a private window or a browser
    /// with storage blocked throws on the read as well as the write, and no storage object at all is
    /// the same case. Both read as "already asked", so the card renders without a nudge instead of
    /// dying halfway through. The last two arms are the player's own wrapper: a host hook that throws,
    /// and no hook at all.
    /// </summary>
    [Test]
    public void BlockedStorageShowsNothingAndBreaksNothing()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("nudgeBlockedStorage").GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("nudgeBlockedStorage").GetProperty("html").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("nudgeNoStorage").GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null));

            Assert.That(root.GetProperty("nudgeHookThrew").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "a host hook that throws costs the nudge, not the card");
            Assert.That(root.GetProperty("nudgeNoHook").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "and a player mounted without the hook shows nothing");
        });
    }

    /// <summary>
    /// The invite is the one the page handed over (<c>#tb-stage</c>'s <c>data-discord-url</c>, rendered
    /// from <see cref="SiteLinks.DISCORD_INVITE"/>), never a second copy hardcoded in JavaScript: the
    /// harness feeds a distinctive URL through, and it is that URL the button links. It opens in a new
    /// tab, with rel=noopener, and carries the Discord-branded button styling (not the card's own
    /// primary button, since it is meant to read as a Discord button).
    /// </summary>
    [Test]
    public void TheNudgeButtonLinksTheInviteItWasHandedInANewTab()
    {
        var root = Harness();
        string invite = root.GetProperty("nudgeUrl").GetString()!;
        string html = root.GetProperty("nudgeHtml").GetString()!;

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"href=\"{invite}\""));
            Assert.That(html, Does.Contain("target=\"_blank\""));
            Assert.That(html, Does.Contain("rel=\"noopener\""));
            Assert.That(html, Does.Contain("class=\"tb-btn tb-btn-discord\""));
            Assert.That(html, Does.Contain("Did you enjoy playing? Then join the official Discord server"));

            // No hardcoded second copy: the real invite only ever reaches the scripts through the
            // data attribute, so neither shipped file may contain it.
            string js = File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "js", "play.js"))
                + File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "js", "typebeat-player.js"));
            Assert.That(js, Does.Not.Contain(SiteLinks.DISCORD_INVITE));
        });
    }

    /// <summary>
    /// A stage root with no <c>data-discord-url</c> (an older cached page) has nothing to link, so the
    /// nudge is not shown AND the flag is left unspent: the browser is still owed its one invitation
    /// once the attribute is there.
    /// </summary>
    [Test]
    public void NoInviteOnThePageMeansNoNudgeAndAnUnspentFlag()
    {
        var root = Harness();
        var observed = root.GetProperty("nudgeNoUrl");

        Assert.Multiple(() =>
        {
            Assert.That(observed.GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(Num(observed, "storedKeys"), Is.Zero);
        });
    }

    /// <summary>
    /// Placement, which no pure function can report: the nudge block is appended to the results card
    /// AFTER the stat grid and BEFORE the submit status line, so it reads as part of the result and
    /// leaves "play again" / "back to maps" (.tb-result-actions, appended last) where they were.
    /// </summary>
    [Test]
    public void TheNudgeSitsBetweenTheResultGridAndTheSubmitStatus()
    {
        string player = File.ReadAllText(
            Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "js", "typebeat-player.js"));

        int grid = player.IndexOf("card.appendChild(grid)", StringComparison.Ordinal);
        int nudge = player.IndexOf("'tb-result-nudge'", StringComparison.Ordinal);
        int status = player.IndexOf("card.appendChild(status)", StringComparison.Ordinal);
        int actions = player.IndexOf("'tb-result-actions'", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(grid, Is.GreaterThan(-1), "the stat grid is still appended to the card");
            Assert.That(nudge, Is.GreaterThan(grid), "the nudge comes after the grid");
            Assert.That(status, Is.GreaterThan(nudge), "and before the submit status");
            Assert.That(actions, Is.GreaterThan(status), "with the action buttons still last");
        });
    }

    // ---- gap fixture plumbing ----

    /// <summary>
    /// The lyric payloads, verbatim from <see cref="InstrumentalGapsTest"/> and from the harness's
    /// own GAP_FIXTURES. Both sides must read the SAME text, which is what makes the comparison a
    /// parity check rather than two independent transcriptions.
    /// </summary>
    private static class GapFixtures
    {
        public const string ExactlyTen =
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            {"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}
            """;

        public const string OneMsShort =
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            {"text":"cd","start_ms":11999,"end_ms":13000,"words":[{"text":"cd","start_ms":11999,"end_ms":13000,"score":1}]}
            """;

        public const string NoUsableWindow =
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"abcd","start_ms":1000,"end_ms":2000,"words":[{"text":"abcd","start_ms":1000,"end_ms":15000,"score":1}]}
            {"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}
            """;

        public const string LongIntro =
            """
            {"version":2,"song_end_ms":60000,"granularity":"Word"}
            {"text":"ab","start_ms":30000,"end_ms":31000,"words":[{"text":"ab","start_ms":30000,"end_ms":31000,"score":1}]}
            {"text":"cd","start_ms":32000,"end_ms":33000,"words":[{"text":"cd","start_ms":32000,"end_ms":33000,"score":1}]}
            """;

        public const string TwoOfFour =
            """
            {"version":2,"song_end_ms":140000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            {"text":"cd","start_ms":58000,"end_ms":59000,"words":[{"text":"cd","start_ms":58000,"end_ms":59000,"score":1}]}
            {"text":"ef","start_ms":93000,"end_ms":94000,"words":[{"text":"ef","start_ms":93000,"end_ms":94000,"score":1}]}
            {"text":"gh","start_ms":95000,"end_ms":96000,"words":[{"text":"gh","start_ms":95000,"end_ms":96000,"score":1}]}
            """;

        public const string Single =
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            """;
    }

    private static void AssertGapParity(JsonElement root, string key, string lyrics)
    {
        var observed = root.GetProperty(key);
        var browser = observed.GetProperty("gaps");
        var server = InstrumentalGaps.Compute(Lines(lyrics));

        Assert.That(browser.GetArrayLength(), Is.EqualTo(server.Count), $"{key}: gap count");

        for (int i = 0; i < server.Count; i++)
        {
            var b = browser[i];

            Assert.Multiple(() =>
            {
                Assert.That(Num(b, "gapStartTime"), Is.EqualTo(server[i].GapStartTime).Within(1e-9), $"{key}[{i}]: gapStart");
                Assert.That(Num(b, "activationTime"), Is.EqualTo(server[i].ActivationTime).Within(1e-9), $"{key}[{i}]: activation");
                Assert.That(Num(b, "skipTarget"), Is.EqualTo(server[i].SkipTarget).Within(1e-9), $"{key}[{i}]: skipTarget");
                Assert.That(Num(b, "skippableMs"), Is.EqualTo(server[i].SkippableMs).Within(1e-9), $"{key}[{i}]: skippable");
            });
        }

        Assert.That(Num(observed, "skippableMs") / 1000,
            Is.EqualTo(InstrumentalGaps.SkippableSeconds(Lines(lyrics))).Within(1e-9), $"{key}: total allowance");
    }

    private static IReadOnlyList<LyricLine> Lines(string lyrics)
        => LyricTiming.ParseSection(lyrics.ReplaceLineEndings("\n").Split('\n')).Lines;

    /// <summary>
    /// Keystroke to character (backlog 309): keyToChar's answer per (e.key, e.code, Shift, previous
    /// keydown was dead), null meaning dropped. The desktop reasoning behind each row lives on
    /// keyToChar itself; WireCompat's KeyToCharParityTest holds a generated table against the
    /// game's KeyCharMap.
    /// </summary>
    private static readonly (string Key, string Code, bool Shift, bool Dead, string? Expected)[] keyToCharPins =
    [
        // rule 1: e.key already typeable
        ("a", "KeyA", false, false, "a"), ("A", "KeyA", true, false, "A"), ("e", "KeyD", false, false, "e"),
        ("7", "Digit7", false, false, "7"), ("5", "Numpad5", false, false, "5"), ("a", "KeyQ", false, true, "a"),
        // rule 2: the composed vowel after a dead key, on a letter position only
        ("ê", "KeyE", false, true, "e"), ("Ê", "KeyE", true, true, "E"), ("â", "KeyQ", false, true, "a"),
        ("ë", "KeyE", false, true, "e"), ("ý", "KeyY", false, true, "y"),
        ("ê", "KeyE", false, false, null), ("ö", "Semicolon", false, true, null), ("ù", "Quote", false, true, null),
        ("ç", "Digit9", false, true, "9"), ("ß", "Minus", false, true, null), ("Dead", "BracketLeft", false, true, null),
        // rule 3: digit row and keypad by position
        ("!", "Digit1", true, false, "1"), ("@", "Digit2", true, false, "2"), ("é", "Digit2", false, false, "2"),
        ("à", "Digit0", false, false, "0"), ("&", "Digit1", false, false, "1"), ("§", "Digit3", true, false, "3"),
        ("End", "Numpad1", false, false, "1"), ("Insert", "Numpad0", true, false, "0"),
        // rule 4: a non-Latin letter types its position, in e.key's case
        ("ф", "KeyA", false, false, "a"), ("Ф", "KeyA", true, false, "A"), ("я", "KeyZ", false, false, "z"),
        ("ς", "KeyW", false, false, "w"), ("Σ", "KeyS", true, false, "S"), ("ب", "KeyF", false, false, "f"),
        // rule 5: dropped (the Azerty comma on KeyM and the Greek ';' on KeyQ are Latin punctuation)
        (",", "KeyM", false, false, null), ("?", "KeyM", true, false, null), (";", "KeyQ", false, false, null),
        ("Dead", "BracketLeft", false, false, null), ("Dead", "Equal", true, false, null),
        ("ö", "Semicolon", false, false, null), ("ü", "BracketLeft", false, false, null), ("ß", "Minus", false, false, null),
        ("ж", "Semicolon", false, false, null), ("б", "Comma", false, false, null), ("ù", "Quote", false, false, null),
        ("é", "KeyE", false, false, null), ("Unidentified", "KeyA", false, false, null), ("Process", "KeyA", false, false, null),
        ("Shift", "ShiftLeft", true, false, null), (".", "Period", false, false, null), ("ñ", "Semicolon", false, false, null),
    ];

    [Test]
    public void KeyToChar_TranslatesEachRowOfThePinnedTable()
    {
        var rows = Harness().GetProperty("keyToChar");
        var observed = new Dictionary<(string, string, bool, bool), string?>();

        foreach (var row in rows.EnumerateArray())
        {
            var ch = row.GetProperty("ch");
            observed[(row.GetProperty("key").GetString()!, row.GetProperty("code").GetString()!,
                      row.GetProperty("shift").GetBoolean(), row.GetProperty("dead").GetBoolean())]
                = ch.ValueKind == JsonValueKind.Null ? null : ch.GetString();
        }

        Assert.That(observed, Has.Count.EqualTo(keyToCharPins.Length), "the harness rows and the pins are the same table");

        Assert.Multiple(() =>
        {
            foreach (var (key, code, shift, dead, expected) in keyToCharPins)
            {
                Assert.That(observed.TryGetValue((key, code, shift, dead), out string? got), Is.True, $"row {key}/{code} is in the harness");
                Assert.That(got, Is.EqualTo(expected), $"keyToChar({key}, {code}, shift={shift}, prevDead={dead})");
            }
        });
    }

    /// <summary>
    /// THE HP BAR (backlog 306): the fill is the health value in percent, clamped to the bar, and the
    /// danger tint is on strictly below the desktop FailingLayer's low-health fraction, 0.2.
    /// </summary>
    [Test]
    public void HealthBarFillsToTheHealthAndTintsBelowTheLowHealthThreshold()
    {
        var root = Harness();

        Assert.That(Num(root, "lowHealthThreshold"), Is.EqualTo(0.2), "FailingLayer.low_health_threshold");

        Assert.Multiple(() =>
        {
            foreach (var sample in root.GetProperty("healthBarSamples").EnumerateArray())
            {
                double health = Num(sample, "health");
                double clamped = Math.Clamp(health, 0, 1);

                Assert.That(Num(sample, "widthPct"), Is.EqualTo(clamped * 100), $"health {health}: width");
                Assert.That(Flag(sample, "danger"), Is.EqualTo(clamped < 0.2), $"health {health}: danger tint");
            }
        });
    }

    [Test]
    public void KeyToChar_TheRouterCarriesTheDeadKeyStateAcrossShift()
    {
        var root = Harness();
        var composes = root.GetProperty("routedDeadKeyComposes");
        var ends = root.GetProperty("routedDeadKeyEnds");
        var digit = root.GetProperty("routedShiftDigit");

        Assert.Multiple(() =>
        {
            // Dead, Shift, 'Â' on KeyQ: the Shift keeps the sequence open, the capital lands on 'a',
            // and 'b' lands on its own cell.
            Assert.That(JsHarness.Strings(composes, "chars"), Is.EqualTo(new[] { "A", "b" }));
            Assert.That(JsHarness.Strings(composes, "states"), Is.EqualTo(new[] { "correct", "correct" }));
            Assert.That(composes.GetProperty("deadTrail").EnumerateArray().Select(e => e.GetBoolean()),
                Is.EqualTo(new[] { true, true, false, false }));

            // Any other key ends it: 'ê' after an intervening 'a' is dropped.
            Assert.That(JsHarness.Strings(ends, "chars"), Is.EqualTo(new[] { "a" }));
            Assert.That(ends.GetProperty("prevWasDead").GetBoolean(), Is.False);

            // Shift+1 reaches the engine as the digit, and is swallowed.
            Assert.That(JsHarness.Strings(digit, "chars"), Is.EqualTo(new[] { "1" }));
            Assert.That(digit.GetProperty("prevented").GetBoolean(), Is.True);
        });
    }

    /// <summary>
    /// The bar reads the ACCOUNT and not the rejection streak it used to (backlog 306). Neither play
    /// rejects a key, so the old read would have drawn both at 100% and untinted, which is what a
    /// /play bar did through runs the desktop fails. A run of typos drains it, and a line nobody
    /// typed takes it into the danger tint while the play is still alive.
    /// </summary>
    [Test]
    public void HealthBarReadsTheAccountNotTheRejectionStreak()
    {
        var runs = Harness().GetProperty("healthBarRuns");
        var typos = runs.GetProperty("typos");
        var idle = runs.GetProperty("idle");

        Assert.Multiple(() =>
        {
            Assert.That(Num(typos, "streak"), Is.Zero, "typos: nothing was rejected");
            Assert.That(Num(typos, "health"), Is.LessThan(1), "typos: the drain never reached the account");
            Assert.That(Num(typos, "widthPct"), Is.EqualTo(Num(typos, "health") * 100), "typos: the bar is not the account");

            Assert.That(Num(idle, "streak"), Is.Zero, "idle: nothing was rejected");
            Assert.That(Flag(idle, "failed"), Is.False, "idle: the run should still be alive");
            Assert.That(Num(idle, "widthPct"), Is.EqualTo(Num(idle, "health") * 100), "idle: the bar is not the account");
            Assert.That(Flag(idle, "danger"), Is.True, "idle: the bar should be tinted below 20%");
        });
    }

    /// <summary>
    /// The start gate describes the HP bar the play really runs on (backlog 306), not the
    /// 13-wrong-keys fail, which belongs to the rejection model a /play run cannot reach.
    /// </summary>
    [Test]
    public void StartGateCopyDescribesTheHealthBar()
    {
        string hint = Harness().GetProperty("startGateHint").GetString()!;

        Assert.Multiple(() =>
        {
            Assert.That(hint, Does.Not.Contain("13 wrong keys"), "the retired mash-streak promise");
            Assert.That(hint, Does.Contain("your health"), "the bar is named");
            Assert.That(hint, Does.Contain("refill"), "what fills it");
            Assert.That(hint, Does.Contain("typos"), "what drains it: typos");
            Assert.That(hint, Does.Contain("missed characters"), "what drains it: misses");
            Assert.That(hint, Does.Contain("skipped words"), "what drains it: word skips");
            Assert.That(hint, Does.Contain("fails if it empties"), "what an empty bar does");
            Assert.That(hint, Does.StartWith("type the lyrics as they are sung"), "the rest of the brief is unchanged");
        });
    }

    // =============================================================================================
    // STACK LAYOUT AND ANIMATION TIMINGS (backlog 322). The desktop draws all three lyric rows at
    // one size with a 96 px pitch at LYRIC_FONT_SIZE 42 (LyricStage), pops a Great for 120 ms
    // OutQuint (LyricLineDisplay.PlayJudgementFeedback), fades the carets over 120 ms
    // (LyricStage.setCaretsVisible) and centres the rejected letter at (+/-34, -4) px in Mono(30)
    // (LyricStage.onWrongKeyRejected). These hold the browser to those values.
    // =============================================================================================

    private static string SiteCss() =>
        File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css")).Replace("\r\n", "\n");

    /// <summary>The body of the FIRST rule whose selector is exactly <paramref name="selector"/>.</summary>
    private static string Rule(string css, string selector)
    {
        int at = css.IndexOf("\n" + selector + " {", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), $"no rule for {selector}");
        int open = css.IndexOf('{', at);
        int close = css.IndexOf('}', open);
        return css.Substring(open + 1, close - open - 1);
    }

    private static double Invariant(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Every row is drawn at the current row's size and weight, the desktop's one LYRIC_FONT_SIZE:
    /// the neighbours used to be about 54% of it, which read the next line ahead at half size and
    /// grew it on activation. The size lives on the stack and the rows inherit it.
    /// </summary>
    [Test]
    public void EveryLyricRowDrawsAtTheCurrentRowsSizeAndWeight()
    {
        string css = SiteCss();

        Assert.Multiple(() =>
        {
            Assert.That(Rule(css, ".tb-stack"), Does.Contain("font-size: clamp(1.6rem, min(4.4vw, 5.5vh), 2.6rem);"));
            Assert.That(css, Does.Contain(".tb-line-prev, .tb-line-cur, .tb-line-next { font-size: 1em; font-weight: 600; }"));

            // The half-size neighbour rule is gone, and no row rule sets a size of its own.
            Assert.That(css, Does.Not.Contain("clamp(1rem, 2.4vw, 1.4rem)"));
            Assert.That(css, Does.Not.Match(@"\.tb-line-(prev|cur|next)[^{]*\{[^}]*font-size: clamp"));
        });
    }

    /// <summary>
    /// The row pitch is the desktop's 96 px over its 42 px font, in em. A row's box is its
    /// line-height, so the gap has to be the pitch minus THAT line-height: this reads both off the
    /// stylesheet and checks they add up, so a line-height change that forgets the gap fails here.
    /// </summary>
    [Test]
    public void RowPitchIsTheDesktops96Over42()
    {
        string css = SiteCss();
        var lh = System.Text.RegularExpressions.Regex.Match(Rule(css, ".tb-line"), @"line-height: ([0-9.]+);");
        var minH = System.Text.RegularExpressions.Regex.Match(Rule(css, ".tb-line"), @"min-height: ([0-9.]+)em;");
        var gap = System.Text.RegularExpressions.Regex.Match(Rule(css, ".tb-stack"), @"gap: calc\(96em / 42 - ([0-9.]+)em\);");

        Assert.That(lh.Success && minH.Success && gap.Success, Is.True, "the pitch rules moved");

        double lineHeight = Invariant(lh.Groups[1].Value);
        double gapMinus = Invariant(gap.Groups[1].Value);

        Assert.Multiple(() =>
        {
            Assert.That(Invariant(minH.Groups[1].Value), Is.EqualTo(lineHeight), "an empty row must hold the same slot as a full one");
            Assert.That(gapMinus, Is.EqualTo(lineHeight), "the gap must subtract the row's own box");
            Assert.That(lineHeight + (96.0 / 42 - gapMinus), Is.EqualTo(2.2857).Within(1e-4), "centre to centre, in em");
        });
    }

    /// <summary>
    /// The Great pop is the desktop's 120 ms OutQuint (it was 140 ms ease-out, a porting slip). The
    /// JS constant decides how long the class stays on and the CSS decides how long the animation
    /// runs, so both are held to the one number.
    /// </summary>
    [Test]
    public void PerfectPopIs120MsOutQuintOnBothSides()
    {
        double pop = Num(Harness(), "perfectPopMs");

        Assert.Multiple(() =>
        {
            Assert.That(pop, Is.EqualTo(120));
            Assert.That(SiteCss(), Does.Contain($".tb-c-pop {{ animation: tbPop {pop}ms cubic-bezier(0.22, 1, 0.36, 1); }}"));
        });
    }

    /// <summary>
    /// The carets FADE over 120 ms OutQuint on a visibility change rather than snapping, starting
    /// from wherever the previous fade stood (FadeTo from the current alpha). Starts hidden, like
    /// the desktop's Alpha = 0 carets. Values: outQuint(0.5) = 0.96875.
    /// </summary>
    [Test]
    public void CaretsFadeOver120MsOutQuint()
    {
        var root = Harness();
        var f = JsHarness.Doubles(root, "caretFade");

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "caretFadeMs"), Is.EqualTo(120));
            Assert.That(f[0], Is.EqualTo(0), "hidden at birth");
            Assert.That(f[1], Is.EqualTo(0), "a fade in starts from 0");
            Assert.That(f[2], Is.EqualTo(0.96875).Within(1e-12), "half way, OutQuint");
            Assert.That(f[3], Is.EqualTo(1));
            Assert.That(f[4], Is.EqualTo(1));
            Assert.That(f[5], Is.EqualTo(1), "a fade out starts from 1");
            Assert.That(f[6], Is.EqualTo(0.03125).Within(1e-12));
            Assert.That(f[7], Is.EqualTo(0));
            Assert.That(f[8], Is.EqualTo(0));
            Assert.That(f[9], Is.EqualTo(0.96875).Within(1e-12), "reversed half way in");
            Assert.That(f[10], Is.EqualTo(0.96875 * 0.03125).Within(1e-12), "turns round from where it stood");
            Assert.That(f[11], Is.EqualTo(0));
        });
    }

    /// <summary>
    /// The rejected letter is Mono(30) centred at (+/-34, -4) px off the caret top, moves by
    /// (18, -30) over 140 ms OutQuint then (12, 110) over 460 ms InQuad, spins to 18 deg over 600 ms
    /// OutQuint, and fades over the last 460 ms InQuad. Desktop px at 42 become em: the letter's
    /// own em is 30/42 of the row's, so each desktop px is 1/30 of it.
    /// </summary>
    [Test]
    public void WrongKeyLetterIsCentredWithTheDesktopOffsetsAndSplitEasing()
    {
        string css = SiteCss();
        string letter = Rule(css, ".tb-wrongkey");

        Assert.Multiple(() =>
        {
            Assert.That(Rule(css, ".tb-wrongkeys"), Does.Contain("top: .16em;"), "on the caret's top edge");
            Assert.That(letter, Does.Contain("font-size: calc(30em / 42);"));
            Assert.That(letter, Does.Contain("font-family: var(--font-display);"));
            Assert.That(letter, Does.Contain("translate: calc(-50% + var(--dir) * 34em / 30) calc(-50% - 4em / 30);"));
            Assert.That(letter, Does.Contain("tbWrongKeySpin 600ms cubic-bezier(0.22, 1, 0.36, 1) forwards"));

            // 34 + 18 = 52 and -4 - 30 = -34 at 140/600 of the way, then + (12, 110) = (64, 76).
            Assert.That(css, Does.Contain("23.333% { translate: calc(-50% + var(--dir) * 52em / 30) calc(-50% - 34em / 30); animation-timing-function: cubic-bezier(0.11, 0, 0.5, 0); }"));
            Assert.That(css, Does.Contain("100%    { translate: calc(-50% + var(--dir) * 64em / 30) calc(-50% + 76em / 30); }"));
            Assert.That(css, Does.Contain("to   { rotate: calc(var(--dir) * 18deg); }"));
            Assert.That(css, Does.Contain("23.333% { opacity: 1; animation-timing-function: cubic-bezier(0.11, 0, 0.5, 0); }"));
        });
    }
}
