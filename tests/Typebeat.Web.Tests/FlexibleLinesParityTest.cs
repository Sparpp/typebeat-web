using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// The browser core's FLEXIBLE LINES default (backlog 208), held against the game's own semantics.
/// The desktop unpinned the player's caret from the song's playhead for EVERY play and reversed the
/// mod that used to grant that (which now PINS the caret and takes acronym "FC"), so the browser had
/// to follow or a /play run and a desktop run of the same map would judge line transitions
/// differently on the SAME leaderboards.
///
/// <para>Four behaviours, and this fixture takes them one at a time in the order the C# engine
/// documents them:</para>
/// <list type="bullet">
/// <item>RUSH FREEDOM: finishing a line opens the next one at once, instead of waiting for its
/// cue.</item>
/// <item>DRAG FREEDOM: a line the player is still typing is held past its deadline for
/// <c>FLETCHER_DRAG_GRACE_MS</c>, then force-sealed with the caret handed on.</item>
/// <item>THE RUSH CAP: a press that leaves the caret more than <c>FLETCHER_MAX_CHARS_AHEAD</c>
/// countable characters past the playhead lands and scores as normal but earns no combo.</item>
/// <item>THE LINE-START SNAP: a caret parked past the end of a FINISHED line is handed to the next
/// line the moment that line is due.</item>
/// <item>THE RUSH BOUND (backlog 218): that rush freedom reaches no further than
/// <c>FLETCHER_DRAG_GRACE_MS</c> before the next line's cue, which is the same margin the drag
/// borrows at the other end of a line, so one constant bounds both directions.</item>
/// </list>
///
/// <para>The golden values are transcribed from typebeat-osu's
/// <c>NonVisual/FletcherEngineTest.cs</c>, which is why this guard runs without a game checkout.
/// The half it cannot do is prove the two engines agree on a whole submitted account, and that is
/// <c>Typebeat.WireCompat.EngineFuzzLiveParityTest</c>, which plays generated and scripted streams
/// through the game's real replay scorer with the same CONFIG bit set.</para>
/// </summary>
public class FlexibleLinesParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => JsHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section(string name) => harness.Value.GetProperty(name);

    /// <summary>The caret's position, as (line, cell), which is what every observation below is written in.</summary>
    private static (int Line, int Cell) At(JsonElement element)
        => (element.GetProperty("line").GetInt32(), element.GetProperty("cell").GetInt32());

    /// <summary>
    /// The browser's settings are the LIVE ones, unconditionally, and that is a statement about
    /// /play rather than a simplification. The C# engine defaults ALL THREE flags to false, because
    /// it must also re-derive a stored replay under the PINNED era every pre-208 row was played in
    /// (CONFIG frame bit 5) and under the UNBOUNDED rush every pre-218 row was played with (bit 7).
    /// The browser has no era axis at all: no mods payload (so the pinning mod "FC" is unreachable),
    /// no replay input, and nothing anywhere re-scores a stored row through that file. If /play ever
    /// grows a mods payload, these are the flags FC would clear.
    ///
    /// <para>The two constants are pinned here as well as being used below, because they are the
    /// mod's whole tuning surface and a drift in either is a silent scoring divergence rather than a
    /// visible one. Since backlog 218 <c>FLETCHER_DRAG_GRACE_MS</c> is doubly load-bearing: it is
    /// the margin in BOTH directions, so a drift moves the drag cutoff and the rush bound
    /// together.</para>
    /// </summary>
    [Test]
    public void TheBrowserRunsTheFlexibleCaretUnconditionally()
    {
        var defaults = Section("defaults");

        Assert.Multiple(() =>
        {
            Assert.That(defaults.GetProperty("fletcherEnabled").GetBoolean(), Is.True, "the browser plays live, and live means unpinned");
            Assert.That(defaults.GetProperty("flexibleLineSnap").GetBoolean(), Is.True, "and live means the line-start snap too");
            Assert.That(defaults.GetProperty("boundedRush").GetBoolean(), Is.True, "and live means the bounded rush, the pre-218 era being unreachable here");
            Assert.That(defaults.GetProperty("manualNewlines").GetBoolean(), Is.True, "and the manual newline, the desktop's shipped default since PR 2 (backlog 307)");
            Assert.That(defaults.GetProperty("newlineOnTypedLetter").GetBoolean(), Is.True, "and the typed-through newline, which rides the same setting");
            Assert.That(defaults.GetProperty("maxCharsAhead").GetInt32(), Is.EqualTo(5), "TypingEngine.FLETCHER_MAX_CHARS_AHEAD");
            Assert.That(defaults.GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(1500), "TypingEngine.FLETCHER_DRAG_GRACE_MS");
        });
    }

    /// <summary>
    /// RUSH FREEDOM, INSIDE THE BOUND, which is what ordinary back-to-back play is. "ab cd" is typed
    /// out by 2500, a second and a half before "ef" would have opened on its own 4000 cue, and the
    /// caret is on "ef" at once: the next press lands on its first cell rather than falling into the
    /// dead zone a pinned caret would have left it in.
    ///
    /// <para>Backlog 218 RE-POINTED this rather than weakening it, and the fixture needed no
    /// re-timing at all: entry into "ef" opens at 4000 - <c>FLETCHER_DRAG_GRACE_MS</c> = 2500, and
    /// the press that finishes "ab cd" lands at exactly 2500, the earliest instant the bound permits.
    /// So the roll still happens ON THE KEYPRESS, with no waiting and no parked state, which is the
    /// game's own <c>FinishingALineInsideTheBoundStillRollsOnAtOnce</c>. The two numbers are asserted
    /// here rather than left implicit, because "immediately" is now a claim about a boundary: a press
    /// one millisecond earlier would be refused, and that is
    /// <see cref="TheRushBoundParksAFinishedCaretUntilTheNextLineIsNearlyDue"/>.</para>
    ///
    /// <para>The line left behind is UNSEALED, which is the other half of the rule and the half that
    /// keeps the song's timeline still: rush freedom moves the PLAYER forward, never the map. That
    /// line seals on its own normal deadline, with nothing missed because it is fully typed.</para>
    /// </summary>
    [Test]
    public void FinishingALineInsideTheBoundOpensTheNextOneImmediately()
    {
        var rush = Section("rushFreedom");

        Assert.Multiple(() =>
        {
            Assert.That(rush.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(4000), "'ef' is cue-clamped to its own 4000 start");
            Assert.That(rush.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(2500), "4000 - FLETCHER_DRAG_GRACE_MS");
            Assert.That(rush.GetProperty("finishedAt").GetDouble(), Is.EqualTo(2500), "and the press that finishes 'ab cd' lands exactly there");

            Assert.That(At(rush.GetProperty("afterFinishing")), Is.EqualTo((1, 0)), "the last press of line 0 put the caret at the head of line 1");
            Assert.That(rush.GetProperty("pressHandled").GetBoolean(), Is.True, "a press at 2600 is a real keystroke, not a dead-zone no-op");
            Assert.That(At(rush.GetProperty("afterPress")), Is.EqualTo((1, 1)));
            Assert.That(rush.GetProperty("firstCellOfNextLine").GetString(), Is.EqualTo("correct"));
            Assert.That(rush.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "the finished line is left unsealed and seals on its own deadline");
        });
    }

    /// <summary>
    /// THE RUSH BOUND (backlog 218), on the shape a decoder actually builds: line windows are
    /// contiguous, so the twelve-second instrumental lives inside "ab"'s own window rather than in a
    /// hole between the lines, and "cdefghij" is cue-clamped to its own 14000 start. Entry into it
    /// therefore opens at 12500, and "ab" is finished at 1500, eleven seconds too early.
    ///
    /// <para>The roll is REFUSED, and the caret PARKS past the last cell of the line it finished. A
    /// press there is INERT in the strongest sense: it is not merely unscored, it enters nothing at
    /// all. No cell is written, no combo is broken, no typo is counted and it never reaches the
    /// accuracy denominator, which is exactly the dead-zone nothing the pinned game answers with
    /// (<c>processKey</c> returns false on a complete line before it touches a single counter). One
    /// frame short of the bound nothing has moved and nothing has sealed either, so the bound is the
    /// only thing deciding this caret's position.</para>
    ///
    /// <para>At 12500 the deferred roll fires from the line-start snap, and the head start is real
    /// typing time: the player is on "cdefghij" a second and a half before its cue and the press is
    /// judged early against its own 14000 target, which is what rushing always read as.</para>
    /// </summary>
    [Test]
    public void TheRushBoundParksAFinishedCaretUntilTheNextLineIsNearlyDue()
    {
        var bound = Section("rushBound");

        Assert.Multiple(() =>
        {
            Assert.That(bound.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(14000), "clamped to the line's own start");
            Assert.That(bound.GetProperty("thisLineEnd").GetDouble(), Is.EqualTo(14000),
                "line 0 could not seal before 14000 either way, so nothing but the bound decides this caret");
            Assert.That(bound.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(12500), "14000 - FLETCHER_DRAG_GRACE_MS");

            Assert.That(At(bound.GetProperty("parkedAt")), Is.EqualTo((0, 2)), "the roll was refused, so the caret sits past line 0's last cell");

            var inert = bound.GetProperty("inertPress");
            Assert.That(inert.GetProperty("handled").GetBoolean(), Is.False, "a parked-finished caret takes no input");
            Assert.That(inert.GetProperty("nextLineFirstCellState").GetString(), Is.EqualTo("untyped"), "and writes nothing into the line it cannot reach");
            Assert.That(inert.GetProperty("combo").GetInt32(), Is.EqualTo(2), "an inert press is not a typo and breaks nothing");
            Assert.That(inert.GetProperty("comboBreaks").GetInt32(), Is.Zero);
            Assert.That(inert.GetProperty("mistypes").GetInt32(), Is.Zero);
            Assert.That(inert.GetProperty("liveAccuracy").GetDouble(), Is.EqualTo(1), "and it never enters the accuracy denominator");

            var short_ = bound.GetProperty("oneFrameShort");
            Assert.That(At(short_.GetProperty("at")), Is.EqualTo((0, 2)), "one millisecond short of the bound the caret has still not moved");
            Assert.That(short_.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "and no seal has moved it either");

            var opened = bound.GetProperty("opened");
            Assert.That(At(opened.GetProperty("at")), Is.EqualTo((1, 0)), "at 12500 the deferred roll fires");
            Assert.That(opened.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "the song is still on line 0: only the player moved");

            Assert.That(bound.GetProperty("pressHandled").GetBoolean(), Is.True);
            Assert.That(bound.GetProperty("firstCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(bound.GetProperty("firstCellDelta").GetDouble(), Is.EqualTo(-1500),
                "12500 against a 14000 target: the head start reads as an early delta, judged as such");
        });
    }

    /// <summary>
    /// THE SYMMETRY, asserted against the one constant, which is the whole of backlog 218. On this
    /// back-to-back fixture line 0's natural END (<c>endTime + sealGraceMs</c>) and line 1's natural
    /// START (<c>activationTime</c>) are the same instant, 4000, so the two freedoms are measured
    /// from one edge: the drag cutoff sits at 5500 and entry opens at 2500, each exactly
    /// <c>FLETCHER_DRAG_GRACE_MS</c> away. A player may run ahead of the song by precisely the margin
    /// they may fall behind it.
    ///
    /// <para>Two scripts on one fixture, because a player cannot both finish early and drag. The
    /// rushing one is here (finish at 2000, 500 ms too early, park until 2500); the dragging one is
    /// <see cref="AnUnfinishedLineSurvivesItsDeadlineAndForceSealsAfterTheGrace"/>, which holds line
    /// 0 to 5499 and force-seals it at 5500.</para>
    /// </summary>
    [Test]
    public void TheRushBoundIsTheDragGraceMirrored()
    {
        var symmetry = Section("boundSymmetry");

        Assert.Multiple(() =>
        {
            double naturalEnd = symmetry.GetProperty("naturalEnd").GetDouble();
            double activation = symmetry.GetProperty("nextLineActivation").GetDouble();
            double entryOpens = symmetry.GetProperty("entryOpensAt").GetDouble();
            double dragCutoff = symmetry.GetProperty("dragCutoff").GetDouble();

            Assert.That(naturalEnd, Is.EqualTo(4000), "line 0's endTime plus its seal grace");
            Assert.That(activation, Is.EqualTo(4000), "line 1 starts where line 0 ends: this map is back to back");
            Assert.That(entryOpens, Is.EqualTo(2500));
            Assert.That(dragCutoff, Is.EqualTo(5500));

            // The two distances are the SAME constant, read off the engine's own numbers.
            Assert.That(dragCutoff - naturalEnd, Is.EqualTo(1500), "how far past a line's end a dragging player may still be on it");
            Assert.That(activation - entryOpens, Is.EqualTo(1500), "how far before a line's start a rushing player may already be on it");

            Assert.That(At(symmetry.GetProperty("finishedEarly")), Is.EqualTo((0, 5)), "finished at 2000, 500 ms before entry opens: the caret parks");
            Assert.That(At(symmetry.GetProperty("oneFrameShort")), Is.EqualTo((0, 5)));
            Assert.That(At(symmetry.GetProperty("atTheBound")), Is.EqualTo((1, 0)), "the earliest instant rush may hold line 1");
        });
    }

    /// <summary>
    /// THE RUSH CAP is untouched by the bound and still bites on the far side of a permitted roll:
    /// entry buys the player a line, never a licence to run away down it. At 12500 the playhead has
    /// reached two countable characters ('a' and 'b') and so has the caret, so the lead is zero on
    /// arrival; five characters of the new line keep it inside <c>FLETCHER_MAX_CHARS_AHEAD</c> and
    /// the combo climbs from 2 to 7, and the sixth costs the run exactly as it does inside one line.
    /// </summary>
    [Test]
    public void TheRushCapStillAppliesAfterAPermittedRoll()
    {
        var cap = Section("rushCapAfterARoll");

        Assert.Multiple(() =>
        {
            Assert.That(At(cap.GetProperty("rolledOnto")), Is.EqualTo((1, 0)), "the bound opened and the deferred roll landed the caret on line 1");
            Assert.That(cap.GetProperty("playhead").GetInt32(), Is.EqualTo(2), "the song has reached 'a' and 'b'");
            Assert.That(cap.GetProperty("leadOnArrival").GetInt32(), Is.Zero, "and so has the caret: a permitted roll is not itself an excursion");

            var inside = cap.GetProperty("insideTheCap");
            Assert.That(inside.GetProperty("lead").GetInt32(), Is.EqualTo(5), "five characters of the new line, the last one still inside the cap");
            Assert.That(inside.GetProperty("combo").GetInt32(), Is.EqualTo(7));
            Assert.That(inside.GetProperty("comboBreaks").GetInt32(), Is.Zero);

            var past = cap.GetProperty("pastTheCap");
            Assert.That(past.GetProperty("lead").GetInt32(), Is.EqualTo(6), "the sixth is over it");
            Assert.That(past.GetProperty("combo").GetInt32(), Is.Zero);
            Assert.That(past.GetProperty("comboBreaks").GetInt32(), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The bound is asked only of a caret moving ITSELF. The SEAL's hand-overs are the song moving on
    /// instead, so they are never refused, even on a map shaped so that they arrive first: a hole at
    /// the HEAD of line 1's window (its vocals are seven seconds into it) puts the bound at 9000
    /// while line 0 seals at 3000 and its drag cutoff falls at 4500. Entry there is LATE, not early,
    /// and refusing it would park the player in a dead zone the unpinned caret does not otherwise
    /// have.
    ///
    /// <para>A loader-built map cannot take this shape at the boundary that matters: a line's
    /// activation is clamped to its own start, which IS the previous line's end, so the bound opens
    /// at worst <c>FLETCHER_DRAG_GRACE_MS</c> before the previous line could seal at all (see
    /// <see cref="TheRushBoundParksAFinishedCaretUntilTheNextLineIsNearlyDue"/>, where it does). The
    /// fixture is hand-spliced for the same reason the parked-caret one is.</para>
    /// </summary>
    [Test]
    public void TheSealsHandOversAreNeverRefusedByTheBound()
    {
        var seal = Section("sealHandOver");

        Assert.Multiple(() =>
        {
            Assert.That(seal.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(10500));
            Assert.That(seal.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(9000), "both hand-overs below land inside the refusal window");
            Assert.That(seal.GetProperty("thisLineEnd").GetDouble(), Is.EqualTo(3000));
            Assert.That(seal.GetProperty("sealGraceMs").GetDouble(), Is.Zero, "so line 0 seals at 3000 flat and its drag cutoff is 4500");

            Assert.That(At(seal.GetProperty("parkedByTheBound")), Is.EqualTo((0, 2)), "the bound parked the caret at the end of line 0");

            var ordinary = seal.GetProperty("ordinary");
            Assert.That(At(ordinary.GetProperty("at")), Is.EqualTo((1, 0)), "the song left line 0 at 3000, so the caret goes with it, six seconds early");
            Assert.That(ordinary.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1));
            foreach (var state in ordinary.GetProperty("cellStates").EnumerateArray())
                Assert.That(state.GetString(), Is.EqualTo("correct"), "the fully typed line seals with nothing missed");

            var cutoff = seal.GetProperty("dragCutoff");
            Assert.That(At(cutoff.GetProperty("at")), Is.EqualTo((1, 0)), "and the drag cutoff's hand-over is not refused either");
            Assert.That(cutoff.GetProperty("untypedCellState").GetString(), Is.EqualTo("missed"));
            Assert.That(cutoff.GetProperty("pressHandled").GetBoolean(), Is.True, "the line it handed over is typeable at once");
            Assert.That(cutoff.GetProperty("firstCellState").GetString(), Is.EqualTo("correct"));
        });
    }

    /// <summary>
    /// DRAG FREEDOM, and its bound. "ab cd" runs to 4000 with the 'd' still owed, so the line is NOT
    /// force-sealed out from under the player at that deadline: the caret is still on the 'd' one
    /// frame short of 5500, which is the deadline plus <c>FLETCHER_DRAG_GRACE_MS</c>. At 5500 the
    /// borrowed time is spent: the line seals, its one untyped cell becomes a miss taking exactly one
    /// combo break however many cells were left, and the caret is handed to the next line rather
    /// than parked in a dead zone.
    ///
    /// <para>The grace is BOUNDED for the reason the C# states outright: a run always has to
    /// terminate. What it buys is a line finished late rather than a line taken away.</para>
    /// </summary>
    [Test]
    public void AnUnfinishedLineSurvivesItsDeadlineAndForceSealsAfterTheGrace()
    {
        var drag = Section("dragFreedom");

        Assert.Multiple(() =>
        {
            Assert.That(At(drag.GetProperty("atDeadline")), Is.EqualTo((0, 4)), "the boundary does not snatch a line the player is on");
            Assert.That(drag.GetProperty("sealedAtDeadline").GetInt32(), Is.Zero, "and nothing has sealed at the deadline either");
            Assert.That(At(drag.GetProperty("oneFrameShort")), Is.EqualTo((0, 4)), "one millisecond short of the grace expiring, the line is still the player's");

            Assert.That(At(drag.GetProperty("afterTheGrace")), Is.EqualTo((1, 0)), "the force-seal hands the caret to the next line, not to a dead zone");
            Assert.That(drag.GetProperty("sealedAfterTheGrace").GetInt32(), Is.EqualTo(1));
            Assert.That(drag.GetProperty("untypedCellState").GetString(), Is.EqualTo("missed"));
            Assert.That(drag.GetProperty("comboBreaks").GetInt32(), Is.EqualTo(1), "at most one break per sealed line");
        });
    }

    /// <summary>
    /// The same drag finished INSIDE the grace, which is what the whole freedom is for: the press
    /// lands on its own cell and the line seals with nothing missed at all. The lateness is not
    /// forgiven, it is JUDGED: the press is 5000 against a syllable sung over [2000, 3000], so it
    /// carries a delta of 2000 and is graded on exactly that. Per-char windows are untouched by the
    /// flexible caret, so dragging reads as late deltas and reports the drift honestly.
    /// </summary>
    [Test]
    public void FinishingInsideTheGraceKeepsTheCellAndJudgesTheLateness()
    {
        var drag = Section("dragFinishedInTime");

        Assert.Multiple(() =>
        {
            Assert.That(drag.GetProperty("pressHandled").GetBoolean(), Is.True);
            Assert.That(drag.GetProperty("lastCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(drag.GetProperty("lastCellDelta").GetDouble(), Is.EqualTo(2000),
                "5000 against the 'cd' syllable's [2000, 3000] span: late by 2000, and graded as such");
            Assert.That(At(drag.GetProperty("afterPress")), Is.EqualTo((1, 0)), "finishing late still rolls the caret forward");
        });
    }

    /// <summary>
    /// THE PUSH WARNING READOUT (backlog 263), the browser half of the game's
    /// <c>TypingEngine.DragCutoffAt</c>. The drag cutoff above force-seals a line out from under the
    /// player and lands their caret on the next one, and it used to arrive with no notice at all;
    /// both clients now count it down with a red bar right-anchored at the end of the line, the
    /// mirror of the blue cue-in bars that grow out of the start of the line a player is about to
    /// gain. This is the readout that bar is drawn from, and it decides nothing whatever.
    ///
    /// <para>So the only two things it can get wrong are the two pinned here. It must EQUAL the
    /// deadline <c>sealPermitted</c> already compares against, or the warning disagrees with the
    /// punishment it warns about: L0's 4000 deadline plus its zero seal grace plus
    /// <c>FLETCHER_DRAG_GRACE_MS</c> is the same 5500 the force-seal lands at above. And it must be
    /// SILENT everywhere no push is coming, which is the other three arms: a pinned caret (snatched
    /// at the boundary, so there is no borrowed time to count down), a line typed out (nothing owed,
    /// so it seals on its ordinary deadline with nobody pushed), and a line walked out of with a line
    /// skip (still held open for its misses, but nobody is standing on it).</para>
    ///
    /// <para>Golden values from typebeat-osu's <c>NonVisual/FletcherEngineTest.cs</c>, region "The
    /// push warning readout (backlog 263)". The game's fixture lines are shorter than this harness's,
    /// so the arithmetic is asserted against the harness's own emitted line coordinates rather than
    /// against the game's literals; the SHAPE is the same one, arm for arm.</para>
    /// </summary>
    [Test]
    public void ThePushWarningReadsOutTheCutoffAndIsSilentWhereNoPushIsComing()
    {
        var push = Section("pushWarning");
        var lines = push.GetProperty("lines");

        double CutoffOf(JsonElement line)
            => line.GetProperty("endTime").GetDouble() + line.GetProperty("sealGraceMs").GetDouble()
               + push.GetProperty("dragGraceMs").GetDouble();

        double firstCutoff = CutoffOf(lines[0]);
        double secondCutoff = CutoffOf(lines[1]);

        Assert.Multiple(() =>
        {
            Assert.That(firstCutoff, Is.EqualTo(5500), "4000 + 0 + FLETCHER_DRAG_GRACE_MS, the very instant the force-seal above lands");
            Assert.That(secondCutoff, Is.EqualTo(9500));

            // SILENT WHERE NO PUSH CAN HAPPEN.
            Assert.That(IsNull(push, "pinnedBeforeAnything"), Is.True, "nothing is active yet");
            Assert.That(At(push.GetProperty("pinnedAt")), Is.EqualTo((0, 1)), "a character is still owed, so this is the dragging shape");
            Assert.That(IsNull(push, "pinnedDragging"), Is.True, "but a pinned caret is snatched, not pushed");
            Assert.That(IsNull(push, "flexibleBeforeAnyLine"), Is.True, "and the unpinned engine is equally silent before its first line activates");

            // THE CUTOFF ITSELF. It is a property of the LINE, so it does not move as the song leaves
            // it: only the player draws a window, the final CUE_LEAD_MS of it, and the two constants
            // being equal is what makes the bar's first frame the instant the line's own grace ends.
            Assert.That(push.GetProperty("afterFirstPress").GetDouble(), Is.EqualTo(firstCutoff));
            Assert.That(push.GetProperty("atTheDeadline").GetDouble(), Is.EqualTo(firstCutoff), "unchanged as the song leaves the line");
            Assert.That(push.GetProperty("oneFrameShort").GetDouble(), Is.EqualTo(firstCutoff));
            Assert.That(push.GetProperty("cueLeadMs").GetDouble(), Is.EqualTo(push.GetProperty("dragGraceMs").GetDouble()),
                "the bar covers the final CUE_LEAD_MS before the cutoff, so with the two constants equal it covers the whole of the borrowed time");

            // The push lands: the caret is handed to L1, which is now the next unsealed line and owes
            // both its characters, so the readout is L1's own cutoff. Then the run ends, and a
            // finished run is warned about nothing.
            var afterThePush = push.GetProperty("afterThePush");
            Assert.That(At(afterThePush.GetProperty("at")), Is.EqualTo((1, 0)));
            Assert.That(afterThePush.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1));
            Assert.That(afterThePush.GetProperty("cutoff").GetDouble(), Is.EqualTo(secondCutoff), "the warning follows the caret through the push");
            Assert.That(push.GetProperty("afterTheRun").GetProperty("finished").GetBoolean(), Is.True);
            Assert.That(IsNull(push.GetProperty("afterTheRun"), "cutoff"), Is.True);

            // FINISHING CANCELS THE PUNISHMENT, isolated: same line, same seal cursor, nothing owed.
            // The gapped map's second line is eighteen seconds off (entry opens at 18500), so the
            // rush bound parks the finished caret on L0 rather than rolling it off it.
            Assert.That(push.GetProperty("gappedEntryOpensAt").GetDouble(), Is.EqualTo(18500));
            Assert.That(push.GetProperty("owedOne").GetDouble(), Is.EqualTo(CutoffOf(push.GetProperty("gappedLines")[0])));
            Assert.That(At(push.GetProperty("typedOutAt")), Is.EqualTo((0, 2)));
            Assert.That(push.GetProperty("typedOutNextSealIndex").GetInt32(), Is.Zero);
            Assert.That(push.GetProperty("typedOutLineComplete").GetBoolean(), Is.True);
            Assert.That(IsNull(push, "typedOutCutoff"), Is.True, "a line with nothing owed seals on its own deadline, with nobody pushed");

            // AN ABANDONED LINE WARNS NOBODY. Entry into L1 opens at 2500, so the line skip rolls the
            // caret straight on; L0 keeps its grace and reaches its misses at 5500 without touching
            // the caret, and only then is the player's own line the next one due to seal.
            Assert.That(push.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(2500));
            Assert.That(push.GetProperty("beforeTheSkip").GetDouble(), Is.EqualTo(firstCutoff));
            Assert.That(push.GetProperty("skipHandled").GetBoolean(), Is.True);

            var afterTheSkip = push.GetProperty("afterTheSkip");
            Assert.That(At(afterTheSkip.GetProperty("at")), Is.EqualTo((1, 0)), "the caret has left L0");
            Assert.That(afterTheSkip.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "while L0 is still held open for its misses");
            Assert.That(IsNull(afterTheSkip, "cutoff"), Is.True, "nobody is standing on the line being held");

            var sealed_ = push.GetProperty("staleLineSealed");
            Assert.That(At(sealed_.GetProperty("at")), Is.EqualTo((1, 0)), "the stale line's seal does not move the caret");
            Assert.That(sealed_.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1));
            Assert.That(sealed_.GetProperty("cutoff").GetDouble(), Is.EqualTo(secondCutoff), "and only now is a push coming for the player");
        });
    }

    /// <summary>Whether a harness property came back as JSON null, which is how the readouts say "nothing".</summary>
    private static bool IsNull(JsonElement element, string property)
        => element.GetProperty(property).ValueKind == JsonValueKind.Null;

    /// <summary>
    /// THE LINE-START SNAP, the one behaviour the new default has that the retired mod never did.
    /// The state it covers is the one the keypress roll-forward cannot: a caret that arrived on a
    /// line ALREADY complete, so no press of the player's can ever finish it and nothing would ever
    /// move it on. The fixture's middle line has no cells at all (its authored text is pure
    /// punctuation) and its window outlives the next line's cue by nine and a half seconds.
    ///
    /// <para>One frame short of that instant nothing has moved, which is what says the snap is the
    /// next line coming DUE rather than the caret being idle; and the middle line is still UNSEALED
    /// when the caret leaves it, which is what says the snap moved it rather than a seal's
    /// hand-over.</para>
    ///
    /// <para>"Due" is the instant backlog 218 moved: the snap takes a finished caret
    /// <c>FLETCHER_DRAG_GRACE_MS</c> BEFORE the line's own activation, because that is the head start
    /// the rush bound grants any finished caret and this is the arm that performs it (a second arm at
    /// the later instant could only re-move a caret this one had already moved). So 10500 - 1500 =
    /// 9000, and the middle line the caret parks on is reached at 1500, its own 3000 activation minus
    /// the same grace, which is exactly when the 'b' lands: the bound is deliberately not what puts
    /// the caret in the parked state this fixture exists for.</para>
    /// </summary>
    [Test]
    public void AParkedFinishedCaretIsSnappedWhenTheNextLineIsDue()
    {
        var snap = Section("lineStartSnap");

        Assert.Multiple(() =>
        {
            Assert.That(snap.GetProperty("middleLineCellCount").GetInt32(), Is.Zero, "the fixture's middle line must have no cells at all");
            Assert.That(snap.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(10500), "12000 - CUE_LEAD_MS");
            Assert.That(snap.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(9000), "10500 - FLETCHER_DRAG_GRACE_MS, which is when the snap fires");
            Assert.That(snap.GetProperty("middleLineEntryOpensAt").GetDouble(), Is.EqualTo(1500),
                "and the middle line is reached exactly as the 'b' lands, so the bound is not what parks this caret");
            Assert.That(snap.GetProperty("middleLineEnd").GetDouble(), Is.EqualTo(20000),
                "the next line has to come due before a seal could hand the caret over, or this proves nothing");

            Assert.That(At(snap.GetProperty("parkedOn")), Is.EqualTo((1, 0)), "rush freedom put the caret on a line it can never finish");

            var short_ = snap.GetProperty("oneFrameShort");
            Assert.That(At(short_.GetProperty("at")), Is.EqualTo((1, 0)), "one millisecond before the bound opens, nothing has moved");
            Assert.That(short_.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1));

            var snapped = snap.GetProperty("snapped");
            Assert.That(At(snapped.GetProperty("at")), Is.EqualTo((2, 0)), "the line came due, so it takes the finished caret");
            Assert.That(snapped.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1),
                "and the line the caret left is still unsealed, so no seal could have moved it");

            // ...and the player then types the new line from its head, on time.
            Assert.That(snap.GetProperty("pressHandled").GetBoolean(), Is.True);
            Assert.That(snap.GetProperty("firstCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(snap.GetProperty("firstCellDelta").GetDouble(), Is.Zero);
        });
    }

    /// <summary>
    /// THE LIMIT on the snap, and the reason it is gated on FINISHED rather than on time alone: a
    /// line the player is still typing is never taken from them, because lagging behind is precisely
    /// the freedom the flexible caret exists to grant. The same predicate <c>sealPermitted</c> uses
    /// ("nothing left untyped means there is no drag to protect") draws the line here.
    ///
    /// <para>Non-vacuous by construction: the next line activates at 10500 and this one cannot even
    /// seal until 20000, so between them the snap is the ONLY thing that could have moved the caret,
    /// and it does not. Only finishing the line does.</para>
    /// </summary>
    [Test]
    public void AnUnfinishedLineIsNeverSnappedAwayFromThePlayer()
    {
        var limit = Section("unfinishedIsNeverSnapped");

        Assert.Multiple(() =>
        {
            Assert.That(limit.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(10500));
            Assert.That(limit.GetProperty("thisLineEnd").GetDouble(), Is.EqualTo(20000),
                "the line the player is on must outlive the next line's cue, or nothing is being tested");

            var owed = limit.GetProperty("stillOwed");
            Assert.That(At(owed.GetProperty("at")), Is.EqualTo((0, 1)), "'b' is still owed, so the line is still the player's");
            Assert.That(owed.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "and no seal could have moved it either");
            Assert.That(limit.GetProperty("untouchedCellState").GetString(), Is.EqualTo("untyped"),
                "nothing has been put into the line that started underneath them");

            Assert.That(At(limit.GetProperty("afterFinishing")), Is.EqualTo((1, 0)),
                "finish it late and the ordinary roll-forward takes over, as it always did");
        });
    }

    /// <summary>
    /// THE RUSH CAP, which is what the flexible caret trades the timing lock for: a keypress may
    /// leave the caret at most <c>FLETCHER_MAX_CHARS_AHEAD</c> COUNTABLE characters past the
    /// playhead and still earn combo. Every press in this run is at 1000, where the playhead has
    /// reached exactly one countable character, so the caret's lead is simply the press count and
    /// the ladder can be read straight off it.
    ///
    /// <para>THE CAP IS A COMBO PENALTY, NOT A BLOCK. Every cell of the line still lands and still
    /// scores; what the excursion costs is the run. The break fires ONCE, on the press that crosses
    /// the line, and the presses that follow it out take no further break because there is no streak
    /// left to take. It measures where the CARET is and not how well the press was timed, which is
    /// why it can refuse combo to a press the ladder graded Great.</para>
    ///
    /// <para>THE SPACE IS EXEMPT, and that is the seventh press here rather than an aside: a word gap
    /// is not COUNTABLE, so it spends no budget. The caret is 5 ahead before it and 5 ahead after it,
    /// and the press earns combo. Had the gap cost a character it would have been the sixth and would
    /// have broken the run one press early.</para>
    /// </summary>
    [Test]
    public void PressingPastTheRushCapCostsTheComboOnceAndThenReArms()
    {
        var cap = Section("rushCap");

        Assert.Multiple(() =>
        {
            foreach (var state in cap.GetProperty("states").EnumerateArray())
                Assert.That(state.GetString(), Is.EqualTo("correct"), "the cap refuses combo, never the character");

            // a b c d e f  [gap]  g h, at a playhead that has reached one countable character.
            Assert.That(JsHarness.Doubles(cap, "leadAfterEachPress"), Is.EqualTo(new double[] { 0, 1, 2, 3, 4, 5, 5, 6, 7 }),
                "the word gap is the 7th press and leaves the lead where it found it: a space spends no rush budget");

            // The fifth character ahead is still fine and the sixth is not, so combo climbs to 7
            // (six letters plus the free gap) and the press that puts the caret 6 ahead zeroes it.
            Assert.That(JsHarness.Doubles(cap, "comboAfterEachPress"), Is.EqualTo(new double[] { 1, 2, 3, 4, 5, 6, 7, 0, 0 }));
            Assert.That(cap.GetProperty("comboBreaks").GetInt32(), Is.EqualTo(1),
                "one break for the excursion: the press after it is further out still and has no streak left to take");
            Assert.That(cap.GetProperty("maxComboBeforeTheCatchUp").GetInt32(), Is.EqualTo(7));

            // The clock catches up, the caret is back inside the cap, and the run rebuilds. That is
            // the re-arm: the cap does not latch, so the next excursion breaks the new streak too.
            Assert.That(cap.GetProperty("leadAfterCatchUp").GetInt32(), Is.EqualTo(1));
            Assert.That(cap.GetProperty("comboAfterCatchUp").GetInt32(), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The predicate the unpinned caret makes necessary: "is the SONG asking for characters on the
    /// line my caret is on", which is not the same question as "is a line window open" and not the
    /// same question as "is a line active".
    ///
    /// <para>The difference IS a real map's instrumental gap. A decoder-built line's window runs to
    /// the NEXT line's start, so windows are contiguous with no holes: through the eighteen seconds
    /// between these two lines the playhead is still inside line 0's window and the plain reading
    /// stays true, while the player who finished line 0 has nothing being asked of them. It goes true
    /// again at 20000, when the song reaches the line the caret has been waiting on.</para>
    ///
    /// <para>RE-TIMED by backlog 218 rather than re-aimed. Line 1's cue is 20000, so entry into it
    /// opens at 18500, and until then the rush bound holds the finished caret on line 0. The
    /// predicate reads TRUE for the whole of that park, correctly: the song IS on the line the caret
    /// is on, and it is the bound's own arm rather than this predicate that keeps the player out of
    /// the gap. What the two consumers need it for begins at 18500, where the caret is finally ahead
    /// of the song and the contiguous window still says nothing about it.</para>
    ///
    /// <para>On the desktop this is what lets Space reach the mid-song skip overlay from a parked
    /// caret. The BROWSER HAS NO SKIP OVERLAY: it cannot seek its scheduled audio source without
    /// moving the gameplay clock, so a dead stretch gets a labelled countdown chip instead and there
    /// is no fall-through for Space to reach. The predicate is still load-bearing here, because that
    /// chip is what it now gates: under a pinned caret "a line is active" meant "the song is asking
    /// for characters", and it does not any more.</para>
    /// </summary>
    [Test]
    public void ThroughAnInstrumentalTheSongIsNotOnTheParkedCaretsLine()
    {
        var gap = Section("songOnTheCaretsLine");

        Assert.Multiple(() =>
        {
            Assert.That(gap.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(18500), "20000 - FLETCHER_DRAG_GRACE_MS");

            foreach (var reading in gap.GetProperty("readings").EnumerateArray())
            {
                double time = reading.GetProperty("time").GetDouble();
                bool onTheCaretsLine = reading.GetProperty("songIsOnTheCaretsLine").GetBoolean();

                Assert.That(reading.GetProperty("songWindowOpen").GetBoolean(), Is.True,
                    $"{time}: a line window is open the whole way, which is exactly why it is the wrong question");

                bool untouched = reading.GetProperty("activeLineUntouched").GetBoolean();

                if (time < 18500)
                {
                    Assert.That(At(reading.GetProperty("at")), Is.EqualTo((0, 2)), $"{time}: the rush bound still has the caret parked past line 0's end");
                    Assert.That(onTheCaretsLine, Is.True, $"{time}: and the song IS on that line, which is the honest answer while the bound holds");
                    Assert.That(untouched, Is.False, $"{time}: both cells behind the parked caret are Correct");
                }
                else if (time < 20000)
                {
                    Assert.That(At(reading.GetProperty("at")), Is.EqualTo((1, 0)), $"{time}: the bound opened and the deferred roll moved the caret");
                    Assert.That(onTheCaretsLine, Is.False, $"{time}: the song is still on line 0, and asking nothing of the caret ahead of it");
                    Assert.That(untouched, Is.True, $"{time}: nothing typed into line 1 yet, so this is the state the desktop drops Space in");
                }
                else
                {
                    Assert.That(At(reading.GetProperty("at")), Is.EqualTo((1, 0)));
                    Assert.That(onTheCaretsLine, Is.True, $"{time}: the song has reached the line the caret was waiting on");
                    Assert.That(untouched, Is.True, $"{time}: still untouched, but the song being on the line makes Space a typing key again");
                }
            }

            // TypingEngine.ActiveLineUntouched after one press into line 1: touched.
            var afterAPress = gap.GetProperty("afterAPress");
            Assert.That(At(afterAPress.GetProperty("at")), Is.EqualTo((1, 1)));
            Assert.That(afterAPress.GetProperty("activeLineUntouched").GetBoolean(), Is.False, "one Correct cell behind the caret");
        });
    }

    /// <summary>
    /// The WPM clock is SUSPENDED through BOTH parked states, which since backlog 218 is what this
    /// run walks through in turn: the caret is held past the END of line 0 by the rush bound from
    /// 2000 to 18500, then sits at the head of line 1 ahead of its 20000 cue. A player who finishes a
    /// line and then waits out an eighteen-second instrumental has not been typing for eighteen
    /// seconds under either park, and a clock that ran through it would read the wait as typing time
    /// and halve the readout.
    ///
    /// <para>Two clauses stop it, and they must agree or the readout depends on which park the
    /// player happens to be in. The bound's park is stopped by the caller's own "the active line is
    /// INCOMPLETE" condition, since a parked-finished caret is complete by definition and nothing new
    /// implements that; the ahead-of-cue park is stopped by <c>clockRunsFrom</c>, which runs the clock
    /// only from the point the playhead reaches the parked line's own activation, exactly when that
    /// line would have gone active under a pinned caret.</para>
    ///
    /// <para>WAITING is the whole of what this pins, and the state next to it, sitting ahead of the
    /// cue and TYPING, is <see cref="TheWpmClockArmsOnTheFirstPressMadeAheadOfTheCue"/>.</para>
    ///
    /// <para>Measured on the frame boundaries, because that is where the rule lives: the frame that
    /// ENDS at the cue was still a parked frame and accrues nothing, and the one after it is real
    /// typing time again. Unsuspended, the clock here would read 20000 rather than 2000.</para>
    /// </summary>
    [Test]
    public void TheWpmClockDoesNotRunWhileTheCaretWaitsAheadOfTheCue()
    {
        var clock = Section("wpmClock");

        Assert.Multiple(() =>
        {
            Assert.That(clock.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(20000));
            Assert.That(clock.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(18500), "so the run is parked past line 0's end until 18500, then ahead of the cue");
            Assert.That(clock.GetProperty("activeTimeAfterTyping").GetDouble(), Is.EqualTo(1000), "typing the first line took a second");

            var pastTheEnd = clock.GetProperty("parkedPastTheEnd");
            Assert.That(At(pastTheEnd.GetProperty("at")), Is.EqualTo((0, 2)), "at 12000 the bound still has the caret past line 0's last cell");
            Assert.That(pastTheEnd.GetProperty("activeTimeMs").GetDouble(), Is.EqualTo(1000), "and ten seconds of that park added nothing");

            Assert.That(clock.GetProperty("activeTimeWhileParked").GetDouble(), Is.EqualTo(1000), "nor did the rest of the instrumental, ahead of the cue");
            Assert.That(clock.GetProperty("activeTimeAtTheCue").GetDouble(), Is.EqualTo(1000), "the frame that ends AT the cue was still a parked frame");
            Assert.That(clock.GetProperty("activeTimeAfterTheCue").GetDouble(), Is.EqualTo(2000), "and the frame after it is typing time again");
        });
    }

    #region The lazy clock arm (backlog 222)

    /// <summary>
    /// One reading out of a <c>wpmClockArm</c> run, addressed by the step that produced it rather
    /// than by its index, so inserting a step into the harness's script fails loudly here instead of
    /// silently re-aiming an assertion at its neighbour. <paramref name="c"/> separates the two
    /// presses that share a timestamp.
    /// </summary>
    private static JsonElement Reading(string run, string op, double t, string? c = null)
    {
        var readings = Section("wpmClockArm").GetProperty("runs").GetProperty(run).GetProperty("readings");

        foreach (var reading in readings.EnumerateArray())
        {
            if (reading.GetProperty("op").GetString() != op || reading.GetProperty("t").GetDouble() != t)
                continue;

            if (c != null && reading.GetProperty("c").GetString() != c)
                continue;

            return reading;
        }

        throw new AssertionException($"wpmClockArm.{run} has no {op} at {t}{(c == null ? "" : $" ('{c}')")}");
    }

    private static double ActiveTime(string run, string op, double t, string? c = null)
        => Reading(run, op, t, c).GetProperty("activeTimeMs").GetDouble();

    private static double Wpm(string run, string op, double t, string? c = null)
        => Reading(run, op, t, c).GetProperty("wpm").GetDouble();

    /// <summary>
    /// THE FIXTURES the runs below are played on, pinned before their readings are so a map that
    /// drifted cannot be read as an engine divergence. <c>twoLine</c> is the game's own
    /// <c>twoLineMap</c>: "ab cd" then "ef", with line 1 activating at its own 4000 start, so entry
    /// into it opens at 4000 - <c>FLETCHER_DRAG_GRACE_MS</c> = 2500, which is exactly where the 'd'
    /// that finishes line 0 lands. The caret is therefore on line 1 a full 1500 ms before the song
    /// reaches it, which is the state the whole of this region is about.
    ///
    /// <para><c>longTail</c> is that map with line 1 widened to four cells and nothing else changed,
    /// so its entry still opens at 2500. It exists only so that line stays INCOMPLETE after two
    /// presses (see <see cref="OnlyTheFirstPressAheadOfTheCueArmsTheClock"/>).</para>
    /// </summary>
    [Test]
    public void TheClockArmFixturesAreTheGamesTwoLineMap()
    {
        var section = Section("wpmClockArm");
        var twoLine = section.GetProperty("fixtures").GetProperty("twoLine");
        var longTail = section.GetProperty("fixtures").GetProperty("longTail");
        var lines = twoLine.GetProperty("lines");

        Assert.Multiple(() =>
        {
            Assert.That(section.GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(1500));
            Assert.That(twoLine.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(2500), "line 1's cue less the grace");
            Assert.That(lines.GetArrayLength(), Is.EqualTo(2));

            Assert.That(lines[0].GetProperty("activationTime").GetDouble(), Is.EqualTo(1000));
            Assert.That(lines[0].GetProperty("endTime").GetDouble(), Is.EqualTo(4000), "contiguous windows: line 0 runs to line 1's start");
            Assert.That(lines[0].GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(0), "so line 0's hard deadline is 4000 flat");
            Assert.That(Targets(lines[0]), Is.EqualTo(new[] { 1000.0, 1500, 2000, 2000, 2500 }), "a b ' ' c d");

            Assert.That(lines[1].GetProperty("activationTime").GetDouble(), Is.EqualTo(4000), "clamped to the line's own start");
            Assert.That(Targets(lines[1]), Is.EqualTo(new[] { 4000.0, 4500 }), "e f");

            // The wide-tailed twin: the same head start, four cells to spend it on.
            Assert.That(longTail.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(2500), "widening line 1 must not move the bound");
            Assert.That(Targets(longTail.GetProperty("lines")[0]), Is.EqualTo(Targets(lines[0])), "line 0 is untouched");
            Assert.That(Targets(longTail.GetProperty("lines")[1]), Is.EqualTo(new[] { 4000.0, 4250, 4500, 4750 }), "e f g h, the same second in four steps");
        });

        static double[] Targets(JsonElement line)
            => line.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("target").GetDouble()).ToArray();
    }

    /// <summary>
    /// THE HOLE <see cref="TheWpmClockDoesNotRunWhileTheCaretWaitsAheadOfTheCue"/> LEAVES (backlog
    /// 222): it parks and WAITS, and the state that was broken is parking and TYPING. A caret rolled
    /// on to the next line sits there from <c>FLETCHER_DRAG_GRACE_MS</c> before its cue and
    /// <c>processKey</c> has no time gate of its own, so the player really can type there; every
    /// character they land counts in the WPM numerator (<c>countCorrectCells</c>) for the rest of the
    /// run. Counting them while the clock stayed stopped walked the browser's readout upward for
    /// free, once per line, exactly as it did on the desktop.
    ///
    /// <para>So the clock ARMS LAZILY on the first press made on such a line and runs from that
    /// press's own time. Two things it deliberately is not: it does not back-date (the arming press
    /// is credited nothing, exactly like a press at the cue + 0), and it does not arm at
    /// <c>entryOpensAt</c>, which would hand the whole head start back as typing time to a player who
    /// never used it (see <see cref="AHeadStartThePlayerDoesNotTypeIsStillCreditedNothing"/>).</para>
    ///
    /// <para>The golden values are the game's own
    /// <c>FletcherEngineTest.ActiveTimeRunsFromTheFirstPressMadeAheadOfTheCue</c>, on the same
    /// fixture and the same script, so the two suites pin one trace. The cross-repo half, which
    /// replays this harness's emitted script through the game's real engine instead of trusting the
    /// transcription, is <c>Typebeat.WireCompat.WpmClockArmLiveParityTest</c>.</para>
    /// </summary>
    [Test]
    public void TheWpmClockArmsOnTheFirstPressMadeAheadOfTheCue()
    {
        var run = Section("wpmClockArm").GetProperty("runs").GetProperty("typed");

        Assert.Multiple(() =>
        {
            // Line 0 was clocked in full: 500 + 500 + 500 = 1500 ms for 5 correct cells (the space
            // counts), so (5/5)/(1500/60000) = 40. The caret is now on line 1, 1500 ms early.
            Assert.That(At(Reading("typed", "key", 2500, "d").GetProperty("at")), Is.EqualTo((1, 0)));
            Assert.That(ActiveTime("typed", "key", 2500, "d"), Is.EqualTo(1500));
            Assert.That(Wpm("typed", "key", 2500, "d"), Is.EqualTo(40).Within(1e-9));

            // The frame across the head start credits nothing yet: the player has not typed on line 1.
            Assert.That(ActiveTime("typed", "update", 2600), Is.EqualTo(1500), "no press, no arm, no accrual");
            Assert.That(Wpm("typed", "update", 2600), Is.EqualTo(40).Within(1e-9));

            // THE ARMING PRESS, judged early against its 4000 target (rushing frees the position,
            // never the clock) but landed correct, so it enters the numerator at once. It credits
            // itself NO elapsed time, so this reads (6/5)/(1500/60000) = 48, up from 40 on the
            // strength of the character alone. That step is honest for one press; what follows is
            // the part that used to be free.
            Assert.That(Reading("typed", "key", 2600, "e").GetProperty("handled").GetBoolean(), Is.True);
            Assert.That(ActiveTime("typed", "key", 2600, "e"), Is.EqualTo(1500), "the arm does not back-date");
            Assert.That(Wpm("typed", "key", 2600, "e"), Is.EqualTo(48).Within(1e-9));

            // 100 ms of real typing time, and the clock now counts it: 1500 + 100 = 1600 ms, so the
            // six cells read (6/5)/(1600/60000) = 45. The arm is at the PRESS (2600), not at entry
            // (2500): arming at entry would have made this 1700 ms.
            Assert.That(ActiveTime("typed", "update", 2700), Is.EqualTo(1600), "the frame spanning the arm credits only 2700 - 2600");
            Assert.That(Wpm("typed", "update", 2700), Is.EqualTo(45).Within(1e-9));

            // 7 correct cells over 1600 ms => 52.5. With the clock frozen this frame credited nothing
            // and the readout climbed to (7/5)/(1500/60000) = 56 instead.
            double frozenClockWpm = (7 / 5.0) / (1500 / 60000.0);

            Assert.That(ActiveTime("typed", "key", 2700, "f"), Is.EqualTo(1600));
            Assert.That(Wpm("typed", "key", 2700, "f"), Is.EqualTo(52.5).Within(1e-9));
            Assert.That(frozenClockWpm, Is.EqualTo(56).Within(1e-9), "what a stopped clock reported for the same seven characters");

            // Nothing in the run was refused or capped, so the readout is the only thing under test.
            Assert.That(run.GetProperty("maxCombo").GetInt32(), Is.EqualTo(7));
            Assert.That(run.GetProperty("comboBreaks").GetInt32(), Is.Zero);
            Assert.That(run.GetProperty("mistypes").GetInt32(), Is.Zero);
        });
    }

    /// <summary>
    /// The NON-VACUITY companion, on the same fixture and the same script minus the presses: a player
    /// handed the head start who does NOT use it is credited nothing for it, which is the rule the
    /// activation gate exists for and the reason the arm is lazy rather than automatic at
    /// <c>entryOpensAt</c>. The lazy arm buys the typing player their time without paying the idle
    /// one for waiting.
    ///
    /// <para>Green both before and after backlog 222, deliberately: it is the half of the statement
    /// the fix must not have broken, and it is what a revert of <c>clockRunsFrom</c> leaves standing
    /// while the pin above goes red.</para>
    /// </summary>
    [Test]
    public void AHeadStartThePlayerDoesNotTypeIsStillCreditedNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(At(Reading("idle", "key", 2500, "d").GetProperty("at")), Is.EqualTo((1, 0)));
            Assert.That(ActiveTime("idle", "key", 2500, "d"), Is.EqualTo(1500), "1500 ms, 5 cells");

            // The whole 1500 ms head start, on line 1, with no press made on it.
            foreach (double t in new[] { 2600.0, 3000, 3500, 4000 })
            {
                Assert.That(ActiveTime("idle", "update", t), Is.EqualTo(1500), $"{t}: waiting is not typing");
                Assert.That(Wpm("idle", "update", t), Is.EqualTo(40).Within(1e-9), $"{t}: so the readout does not move");
            }

            // From the cue the clock runs on the ordinary rule, unarmed: 1500 + 500 = 2000 ms, so
            // the same 5 cells now read (5/5)/(2000/60000) = 30.
            Assert.That(ActiveTime("idle", "update", 4500), Is.EqualTo(2000));
            Assert.That(Wpm("idle", "update", 4500), Is.EqualTo(30).Within(1e-9));
        });
    }

    /// <summary>
    /// ONLY THE FIRST PRESS ARMS, and the arm never moves forward with a later one: the time between
    /// two presses is the player TYPING, and swallowing it is the same defect the arm was added to
    /// close, one press smaller. Two presses land ahead of the cue before the next frame, so the
    /// frame after them credits 2700 - 2600 = 100 rather than 2700 - 2650 = 50.
    ///
    /// <para>On the <c>longTail</c> fixture, because the property is INVISIBLE on the two-cell line
    /// the other runs use: the second press completes that line and the caller stops accruing
    /// (<c>isLineComplete</c>), so a second arm would leave no trace to assert on. The game's own
    /// fixtures have the same shape, so this is a browser-side pin with no transcribed twin.</para>
    /// </summary>
    [Test]
    public void OnlyTheFirstPressAheadOfTheCueArmsTheClock()
    {
        const string run = "secondPressKeepsTheFirstArm";

        Assert.Multiple(() =>
        {
            Assert.That(At(Reading(run, "key", 2500, "d").GetProperty("at")), Is.EqualTo((1, 0)), "the roll put the caret on line 1 at 2500");
            Assert.That(ActiveTime(run, "update", 2600), Is.EqualTo(1500), "and the head start has credited nothing yet");

            // Both presses land before the next frame, so neither is credited any elapsed time and
            // the readout steps on the characters alone: 6 cells => 48, then 7 => 56.
            Assert.That(ActiveTime(run, "key", 2600, "e"), Is.EqualTo(1500));
            Assert.That(ActiveTime(run, "key", 2650, "f"), Is.EqualTo(1500));
            Assert.That(Wpm(run, "key", 2650, "f"), Is.EqualTo(56).Within(1e-9));

            // THE PIN: measured from the FIRST press. An arm that moved with the second would make
            // this 1550 and read (7/5)/(1550/60000) = 54.19 instead of 52.5.
            Assert.That(ActiveTime(run, "update", 2700), Is.EqualTo(1600), "the 50 ms between the two presses is typing time and must be kept");
            Assert.That(Wpm(run, "update", 2700), Is.EqualTo(52.5).Within(1e-9));

            // And the run stays clean, so no cap or refusal is doing the work.
            var summary = Section("wpmClockArm").GetProperty("runs").GetProperty(run);
            Assert.That(summary.GetProperty("maxCombo").GetInt32(), Is.EqualTo(8));
            Assert.That(summary.GetProperty("comboBreaks").GetInt32(), Is.Zero);
        });
    }

    /// <summary>
    /// A press stamped AHEAD of the frame that follows it, which the browser can produce on its own:
    /// a keypress reads the audio clock at the event while the render loop is still carrying the
    /// previous frame's stamp, so <c>update</c> can arrive with a time BEFORE the arm. The clock must
    /// credit zero for that frame rather than negative time, and must still run from the press
    /// onward: that is the whole of the <c>Math.max(0, time - from)</c> at the accrual site, which
    /// replaced a <c>Math.max(0, time - lastUpdateTime)</c> that could not see the arm at all.
    ///
    /// <para>Not reachable in the C# suite's own scripts, where every press is stamped on a frame,
    /// which is why it is pinned here rather than transcribed from the game.</para>
    /// </summary>
    [Test]
    public void APressStampedAheadOfTheNextFrameCreditsZeroRatherThanNegativeTime()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ActiveTime("aheadOfTheFrame", "update", 2600), Is.EqualTo(1500));

            // The press arms at 2900, three hundred milliseconds ahead of the frame loop.
            Assert.That(Reading("aheadOfTheFrame", "key", 2900, "e").GetProperty("handled").GetBoolean(), Is.True);
            Assert.That(ActiveTime("aheadOfTheFrame", "key", 2900, "e"), Is.EqualTo(1500));

            // The frame BEHIND the arm: 2700 - 2900 is negative, and the clock takes zero from it.
            Assert.That(ActiveTime("aheadOfTheFrame", "update", 2700), Is.EqualTo(1500), "a frame before the arm can never run the clock backwards");

            // And the next one runs from the arm, not from the frame before it: 3000 - 2900 = 100.
            Assert.That(ActiveTime("aheadOfTheFrame", "update", 3000), Is.EqualTo(1600), "100 ms, measured from the press");
            Assert.That(Wpm("aheadOfTheFrame", "update", 3000), Is.EqualTo(45).Within(1e-9));
        });
    }

    #endregion
}
