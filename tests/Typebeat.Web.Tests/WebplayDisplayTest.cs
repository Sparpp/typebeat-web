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
    /// Which line carries the sung sweep, the sweep head and the sung caret (mirrors
    /// LyricStage.sungLineFor). With a pinned caret it is always the active line and this is the
    /// behaviour that shipped before backlog 208. Under the flexible caret, which is the DEFAULT
    /// since it, the caret and the vocal come apart: finishing a line early parks the caret at the
    /// head of the next one while the song is still singing the line behind, so the playhead has to
    /// follow the first UNSEALED line or it sits at position 0 of a line the vocal has not reached.
    /// Once everything has sealed there is no unsealed line left and it falls back to the active one.
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
    /// The live HUD readouts over real engine runs. Sync counts every TIMED cell a SEALED line
    /// resolved, including the ones that were never typed (q = 0), which is what makes it a timing
    /// measure rather than a hit count.
    ///
    /// <para>The two denominators are different on purpose, and backlog 148 moved only one of them.
    /// COMPLETION is every character of the map the player owes, word gaps included. SYNC is a
    /// timing mean, and a space is no longer timed (the engine judges it on a zeroed delta), so it
    /// is out of both halves of that mean: see
    /// <see cref="TheUntimedSpaceIsNeutralInTheLiveSyncReadout"/>.</para>
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

            // One of FIVE cells typed (completion counts the word gap), and the sync mean is over
            // the FOUR timed ones: 'a' at q = 1 and three sealed misses at q = 0.
            Assert.That(Num(partial, "completion"), Is.EqualTo(0.2).Within(1e-12));
            Assert.That(Num(partial, "sync"), Is.EqualTo(25).Within(1e-12));

            // Two presses, one on target and one at half quality: both count as typed, sync 75%.
            Assert.That(Num(late, "completion"), Is.EqualTo(1));
            Assert.That(Num(late, "sync"), Is.EqualTo(75).Within(1e-12));
        });
    }

    /// <summary>
    /// Backlog 148 in the browser's HUD, mirroring TypingEngine.LiveSyncPercent: a space is out of
    /// the sync mean entirely, so how well it was timed cannot move the readout. Asserted as an
    /// equality between two runs that differ only in when the space was pressed, because that
    /// equality IS the claim, plus the absolute value so a change to both sides still trips it.
    /// </summary>
    [Test]
    public void TheUntimedSpaceIsNeutralInTheLiveSyncReadout()
    {
        var root = Harness();
        var loose = root.GetProperty("looseSpaceStats");
        var tight = root.GetProperty("tightSpaceStats");

        Assert.Multiple(() =>
        {
            Assert.That(Num(loose, "sync"), Is.EqualTo(Num(tight, "sync")).Within(1e-12));

            // Four lyric chars 200 ms past their syllable's span at tier Word (Meh-late 1200), so
            // q = 5/6 each, over the four TIMED cells. Counted in at its zeroed delta the space
            // would lift this to 86.67.
            Assert.That(Num(loose, "sync"), Is.EqualTo(500.0 / 6).Within(1e-12));

            // ...and the space is still a character the player owes, so completion is untouched.
            Assert.That(Num(loose, "completion"), Is.EqualTo(1));
            Assert.That(Num(tight, "completion"), Is.EqualTo(1));
        });
    }

    private static string Cls(JsonElement paint, int i) => paint[i].GetProperty("cls").GetString()!;

    private static string Glyph(JsonElement paint, int i) => paint[i].GetProperty("glyph").GetString()!;

    private static string? Fill(JsonElement paint, int i)
    {
        var e = paint[i].GetProperty("fill");
        return e.ValueKind == JsonValueKind.Null ? null : e.GetString();
    }

    /// <summary>
    /// The sync tint ramp (LyricLineDisplay.CorrectCharColour, re-expressed in the site's own
    /// tokens): a correct char is filled by how in sync the keypress was, so the trail behind the
    /// caret reads as brightness.
    ///
    /// <para>The FLOOR is the load-bearing part. SyncQuality returns exactly 0 at the Ok-window
    /// edges and stays there beyond them while the cell is still Correct, so an unfloored ramp
    /// would paint a character the player DID type in precisely the untyped colour (0%), which is
    /// a legibility regression rather than feedback. The top of the ramp is a contract in the
    /// other direction: quality 1 must give 100%, which mixes to var(--violet) itself, so nothing
    /// about a perfectly timed line changed when the ramp landed.</para>
    /// </summary>
    [Test]
    public void SyncTintRampIsFlooredMonotonicAndExactAtItsEnds()
    {
        var root = Harness();
        var curve = JsHarness.Doubles(root, "rampCurve");

        Assert.Multiple(() =>
        {
            // The browser floor is NOT the desktop's 0.35: that ramp is walked in linear light,
            // this one in oklab, so the number was ported by outcome (see SYNC_TINT_FLOOR).
            Assert.That(Num(root, "syncTintFloor"), Is.EqualTo(0.5));

            Assert.That(JsHarness.Strings(root, "rampAt"),
                Is.EqualTo(new[] { "50.00%", "62.50%", "75.00%", "87.50%", "100%" }));

            // Quality 0 lands on the floor, and the floor is emphatically not the untyped end.
            Assert.That(curve[0], Is.EqualTo(50));
            Assert.That(curve[0], Is.GreaterThan(0));
            // Quality 1 lands exactly on the full hit colour.
            Assert.That(curve[^1], Is.EqualTo(100));

            // Brightness rises with quality, everywhere, never flat and never backwards.
            for (int i = 1; i < curve.Length; i++)
                Assert.That(curve[i], Is.GreaterThan(curve[i - 1]), $"ramp went backwards at {i}");

            // Out-of-range and NaN clamp to the ends rather than escaping the ramp.
            Assert.That(JsHarness.Strings(root, "rampClamped"),
                Is.EqualTo(new[] { "50.00%", "100%", "50.00%" }));
        });
    }

    /// <summary>
    /// The ramp on a real run: dead on target is the full hit colour, a press at half quality is
    /// half way up from the floor, and every cell in between is somewhere on the ramp.
    /// </summary>
    [Test]
    public void CorrectCharsAreFilledByHowInSyncTheKeypressWas()
    {
        var paint = Harness().GetProperty("mixedPaint");

        Assert.Multiple(() =>
        {
            // 'a' on target: quality 1, so the colour .tb-c-hit shipped before the ramp existed.
            Assert.That(Cls(paint, 0), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Fill(paint, 0), Is.EqualTo("100%"));

            // 'b' at +600ms against a 1200ms Ok-late window: quality 0.5, so half of the ramp
            // above the 50% floor, i.e. 75%. This is the assertion that makes the tint continuous
            // rather than the two-bucket approximation /play shipped before. Since backlog 179 the
            // +600 is measured from the late edge of the SYLLABLE "ab" is sung over, not from 'b''s
            // own target, because that is the delta the engine judges and stores.
            Assert.That(Cls(paint, 1), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Fill(paint, 1), Is.EqualTo("75.00%"));

            // Cell 2 is the word GAP, pressed at the same instant as 'c' beside it, which is +100
            // past the span of the syllable 'c' belongs to. Since backlog 148 a space is judged on a
            // zeroed delta, so it stores quality 1 and paints at the full hit colour rather than at
            // 'c''s 95.83%. Invisible in practice (a space renders as a gap), but the desktop's
            // LyricLineDisplay reads back the same zeroed delta, so the mirror has to agree here too.
            // A space is also in NO syllable group, so the span rule leaves it on this arm.
            Assert.That(Fill(paint, 2), Is.EqualTo("100%"));

            // +100ms past a LYRIC character's syllable span: high quality, but distinctly not the
            // full colour, which is what separates it from the space beside it.
            Assert.That(Fill(paint, 3), Is.EqualTo("95.83%"));
        });
    }

    /// <summary>
    /// Everything the ramp deliberately does NOT touch.
    ///
    /// <para>An OFF-TIME press (Premature/Lagging) still lands the cell Correct, but the browser
    /// gives it .tb-c-off's flat warn tint: a browser-only affordance the desktop does not have,
    /// and folding it into the ramp would throw it away. MISSED and UNTYPED cells have no
    /// keypress to be in sync with, and a BACKSPACE clears the judged delta, so the tint has to
    /// come off with it rather than leaving the glyph holding brightness it no longer owns.</para>
    /// </summary>
    [Test]
    public void OffTimeMissedAndBackspacedCellsTakeNoSyncTint()
    {
        var root = Harness();
        var mixed = root.GetProperty("mixedPaint");
        var sealedPaint = root.GetProperty("sealedPaint");
        var backspaced = root.GetProperty("backspacedPaint");

        Assert.Multiple(() =>
        {
            // 'd' at +1300ms past its syllable's span, beyond the 1200ms Ok edge: correct, but off-time.
            Assert.That(Cls(mixed, 4), Is.EqualTo("tb-c tb-c-off"));
            Assert.That(Fill(mixed, 4), Is.Null);

            Assert.That(Cls(sealedPaint, 0), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Fill(sealedPaint, 0), Is.EqualTo("100%"));
            Assert.That(Cls(sealedPaint, 1), Is.EqualTo("tb-c tb-c-miss"));
            Assert.That(Fill(sealedPaint, 1), Is.Null);

            // Typed, then backspaced: back to untyped, and the fill goes with it.
            Assert.That(Cls(backspaced, 0), Is.EqualTo("tb-c tb-c-todo"));
            Assert.That(Fill(backspaced, 0), Is.Null);
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
            Assert.That(Fill(typo, 2), Is.Null, "a wrong cell is not on the sync ramp");

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
    /// FREESTYLE cells are excluded from the ramp, matching the desktop exclusion and for the same
    /// reason: their colour is an IDENTITY signal ("this slot was free") that has to keep saying so
    /// for the rest of the play, not a state signal. The cell below is typed DEAD ON TARGET, so the
    /// only thing that can keep it off the ramp is the exclusion itself.
    /// </summary>
    [Test]
    public void FreestyleCellsAreExcludedFromTheSyncTint()
    {
        var paint = Harness().GetProperty("freestylePaint");

        Assert.Multiple(() =>
        {
            // The ordinary cell next to it, typed identically, does take the ramp.
            Assert.That(Cls(paint, 0), Is.EqualTo("tb-c tb-c-hit"));
            Assert.That(Fill(paint, 0), Is.EqualTo("100%"));

            Assert.That(Cls(paint, 1), Is.EqualTo("tb-c tb-c-hit tb-c-free"));
            Assert.That(Fill(paint, 1), Is.Null);
        });
    }

    /// <summary>
    /// The other half of the ramp lives in CSS, and the split is deliberate: JS writes only the
    /// position (--tb-sync-fill), the mix is done against the site's own design TOKENS so neither
    /// endpoint is duplicated as a literal that could drift from the theme.
    ///
    /// <para>This is not a port of the desktop's colours. /play re-skins gameplay onto the site
    /// palette, so the desktop's grey-to-off-white literals must never appear here; and the
    /// cascade order is load-bearing, because .tb-c-free has to keep beating the ramp at equal
    /// specificity.</para>
    /// </summary>
    [Test]
    public void SyncTintMixesTheSiteTokensAndLosesToFreestyle()
    {
        string css = File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css"));

        int mix = css.IndexOf("color-mix(in oklab, var(--violet) var(--tb-sync-fill, 100%), var(--text-muted))", StringComparison.Ordinal);
        int free = css.IndexOf(".tb-c-free, .tb-line-cur .tb-c-free", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(mix, Is.GreaterThan(-1), "the .tb-c-hit ramp must mix the todo and hit TOKENS, not literals");
            // A browser without color-mix() must still get the flat accent, never the inherited grey.
            Assert.That(css, Does.Contain(".tb-c-hit  { color: var(--violet); }"));
            // The off-time bucket stays a flat, distinct warn tint: it is outside the ramp.
            Assert.That(css, Does.Contain(".tb-c-off  { color: var(--warn); }"));

            Assert.That(free, Is.GreaterThan(mix), "the freestyle colour must still win over the ramp by cascade order");

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
}
