// Node harness for the /play PRESENTATION layer (typebeat-player.js). It loads the actual
// served scripts, drives the pure display math exported on TypeBeatCore.display, and emits its
// observations as JSON on stdout so the C# guard (WebplayDisplayTest) can assert them against
// values mirrored from the desktop client (LyricStage / Caret / LyricLineDisplay / TypingLine /
// TypeBeatHudOverlay).
//
// This is the DISPLAY twin of the scoring harnesses: nothing here can move a judgement, but the
// sung playhead, the cue-in bars and the HUD readouts are ports of desktop formulas, and a
// silent drift in any of them is exactly what makes browser play stop feeling like the client.
//
// Both scripts are plain browser scripts that hang off a bare `window`; provide a global one
// before loading, then read the exports back off it.
//
// Usage: node PlayerDisplayHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

// play.js is the PAGE glue, and it owns the one thing the player deliberately does not: the
// localStorage flag behind the first-clear Discord nudge (backlog 289). It is an IIFE with no module
// exports, and it publishes window.TypeBeatPlayPage BEFORE its own stage guard precisely so this
// harness can drive that decision. A document whose getElementById answers null takes that guard's
// early return, so nothing else in the file runs and no DOM is needed.
global.document = { getElementById: function () { return null; } };
require(nodePath.join(nodePath.dirname(corePath), 'play.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;
const PAGE = global.window.TypeBeatPlayPage;

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

// The desktop test suite's workhorse line, transcribed into the .osu form /play consumes:
// "ab cd", boundary [1000, 4000), sung end 3000, words "ab" [1000,2000] and "cd" [2000,3000].
// Cell targets: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
const abcdOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":4000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n';

// backlog 245: the same shape as abcdOsu, but the mapper dragged the blue sung-end flag PAST the
// last word's own end (words unchanged: "ab" [1000,2000], "cd" [2000,3200]; end_ms 6000 instead of
// 3200). Cell targets: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2600. Before the fix the sung
// polyline's final anchor was max(singEndTime, lastTime) = max(6000, 2600) = 6000, so the caret
// crawled across the single last character 'd' for 3400ms. The fix closes on the last word's own
// end instead: max(lastUnitEnd = 3200, lastTime = 2600) = 3200.
const draggedOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":6000,"words":[' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"cd","start_ms":2000,"end_ms":3200,"score":1}]}\n';

// Two lines with a long instrumental stretch between them, for the cue-target and gap logic:
// line 0 sings [1000, 2000], line 1 does not start until 12000.
const gapOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
    '{"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}\n';

function build(osu) { return TB.buildBeatmap(TB.parseLyricOsu(osu)); }

// Every scenario in this file was authored on the skip-OFF arm (a mid-word space is a typo, typed
// through, never a word skip). Since backlog 198 the engine DEFAULTS to skip-on, so the arm is
// declared at each construction rather than inherited: these pins are about the display, and none
// of them means to exercise the skip.
function engineFor(map) {
    const engine = new TB.TypingEngine(map);
    engine.spaceSkipsWord = false;
    return engine;
}

const abcd = build(abcdOsu);
const dragged = build(draggedOsu);
const gapMap = build(gapOsu);
const line0 = abcd.lines[0];
const points = D.buildSungPoints(line0);
const draggedLine0 = dragged.lines[0];
const draggedPoints = D.buildSungPoints(draggedLine0);

// --- a perfect run and a one-key run, for the live HUD readouts ---
function playPerfect() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    const keys = [['a', 1000], ['b', 1500], [' ', 2000], ['c', 2000], ['d', 2500]];
    for (const [c, t] of keys) { engine.update(t); engine.processKey(c, t); }
    engine.update(5000);
    return engine;
}

function playOneKeyThenSeal() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    // The line's deadline is 4000 with no seal grace, and since backlog 208 the flexible caret adds
    // FLETCHER_DRAG_GRACE_MS on top of it whenever the player is still ON the line with cells owed,
    // which is the whole of this scenario (one key of five). So the force-seal, and the missed
    // paint and completion this fixture is here to show, land at 5500. playPerfect above needs no
    // such move: a fully typed line has no drag to protect and seals on its own 4000 deadline.
    engine.update(5500);
    return engine;
}

// A late-but-correct press. Before backlog 251 removed the browser's sync readout this was the
// scenario that proved sync a real timing measure and not just a hit count; completion does not
// distinguish it from a dead-on-target press (both count as typed), which is the assertion that
// survives it in LiveHudReadoutsTrackTheRun.
//
// Every press time here is measured from the SYLLABLE SPAN the cell belongs to, not from the cell's
// own target (backlog 179): 'a' and 'b' are the two characters of the word "ab", which is sung over
// [1000, 2000], so a press anywhere in that window is delta 0 and a press at 2600 is +600 past the
// span's late edge.
function playOneLatePress() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // inside the "ab" span, q = 1
    engine.update(2600);
    engine.processKey('b', 2600);   // +600 past the span's end, q = 0.5
    return engine;
}

// --- cell classes on a mixed run ---
// A run that lands one cell of each kind the class rules have to tell apart: dead on the anchor,
// off but still typeable through the caret, the untimed word gap, and a press so late it is
// Lagging, which is still a CORRECT cell but keeps .tb-c-off's flat warn tint rather than
// .tb-c-hit's.
function playMixedTiming() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // 'a' OPENS the "ab" span [1000, 2000], and lands on its start
    engine.update(2100);
    engine.processKey('b', 2100);   // a non-opening cell, +100 past that span's end, still Ok
    engine.update(2600);
    engine.processKey(' ', 2600);   // the word gap: untimed, judged on a zeroed delta whenever it lands
    engine.processKey('c', 2600);   // 'c' OPENS the "cd" span [2000, 3000], +600 from its start
    engine.update(3800);
    engine.processKey('d', 4300);   // +1300 past that span's end -> past the Ok edge, Lagging
    return engine;
}

// Backspace over a correct cell: the engine clears its judged delta and the class must go back to
// untyped rather than staying on whatever state it had earned.
function playThenBackspace() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processBackspace();
    return engine;
}

// A freestyle cell ('&' authors a slot any key fills), typed dead on target so its class comes
// only from the freestyle exclusion, not from how it was timed.
const freestyleOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"a&b","start_ms":1000,"end_ms":4000,"freestyle":true,' +
    '"words":[{"text":"a&b","start_ms":1000,"end_ms":4000,"score":1}]}\n';

function playFreestyle() {
    const map = build(freestyleOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // ordinary cell, dead on target
    engine.update(2000);
    engine.processKey('z', 2000);   // the free slot, also dead on target
    return engine;
}

// A wrong LETTER on the word gap (backlog 181): it lands IN the gap, which is the one cell whose
// glyph is not fixed for the whole play. Typed and then left there, so the seal below resolves it
// as an unfixed typo and the run is what a player would actually see on a finished line.
function playGapTypo() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey('x', 2000);   // the gap takes the typo, the caret moves past it
    engine.processKey('c', 2000);
    engine.processKey('d', 2500);
    engine.update(5000);            // seal: the gap stays WRONG, showing the character that went in
    return engine;
}

// The same typo taken back. Backspace clears the cell, so the gap is a space again: the typed
// glyph belongs to the WRONG state and to nothing else.
function playGapTypoErased() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey('x', 2000);
    engine.processBackspace();
    return engine;
}

// A space typed INSIDE a word (backlog 185's near miss). With space-skip off there is no word for
// the press to skip, so it is typed through as an ordinary wrong character: cell 1 of "ab cd" is a
// wrong LYRIC cell showing its own 'b' in red. It is the cell that proves the gap dimming is keyed
// on what the cell EXPECTS, not on what was pressed.
function playMidWordSpaceTypo() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey(' ', 1500);   // lands on 'b', wrong, and the caret moves past it
    return engine;                  // left unsealed, so the untouched gap after it is still todo
}

// --- the push warning (backlog 263) ---
// One frame of the red bar that counts down the drag cutoff (updatePushWarning): which line it hangs
// off, and the bar it draws there. It is the SAME cueBar() the blue cue-in bars are drawn with, at
// full opacity, so the only new statements are the line it belongs to (the ACTIVE one, the one about
// to be taken, rather than the upcoming one a cue belongs to) and the window it covers, which is the
// final CUE_LEAD_MS before TypingEngine.dragCutoffAt.
function pushWarningAt(engine, time) {
    const cutoff = engine.dragCutoffAt;
    return {
        cutoff: cutoff,
        line: cutoff === null ? -1 : engine.activeLineIndex,
        bar: cutoff === null ? null : D.cueBar(cutoff - time, 1)
    };
}

// A player one character into "ab cd" and going nowhere. The line's deadline is 4000 with no seal
// grace, so the drag holds it to 5500 and that is what the bar counts down to.
function playDragging() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    return engine;
}

// The same line TYPED OUT, left unsealed. Nothing is owed, so no push is coming and the bar has
// nothing to draw, however close the line's deadline is.
function playTypedOut() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    for (const [c, t] of [['a', 1000], ['b', 1500], [' ', 2000], ['c', 2000], ['d', 2500]]) {
        engine.update(t);
        engine.processKey(c, t);
    }
    return engine;
}

// --- the sung row under a parked caret (backlog 217) ---
// Two lines typed FAST: both characters of line 0 go in by 1500, which finishes the line, so the
// flexible caret (the default since backlog 208) rolls forward and parks at the head of line 1 while
// the vocal is still singing line 0. That is the case the sweep got wrong: it rode the caret's row,
// so it sat at position 0 of a line the song had not reached instead of tracking the vocal on the
// row behind.
//
// Its own fixture rather than gapOsu since backlog 218, which is a re-timing and not a re-aiming.
// The rush bound opens entry into a line FLETCHER_DRAG_GRACE_MS before its cue, so a caret can only
// be ahead of the song for that 1500 ms, and on gapOsu's twelve-second instrumental the vocal of
// line 0 is long finished by then: the reading would be the clamped end of the line rather than a
// live position, which is a much weaker thing to pin. Line 1 here comes due at 1500, while line 0
// is still being sung, so the observation is exactly the one it always was, at exactly the numbers
// it always had (1.2 characters in, against a caret row reading 0).
//   L0 "ab" [1000, 3000), sung [1000, 2000]: a = 1000, b = 1500, so it is finished at 1500 and
//           still UNSEALED (its window runs to line 1's start) with the vocal mid-line.
//   L1 "cd" [3000, 7000), sung [3000, 4000]: activation 3000, so entry opens at 1500.
const rollOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
    '{"text":"cd","start_ms":3000,"end_ms":4000,"words":[{"text":"cd","start_ms":3000,"end_ms":4000,"score":1}]}\n';

const rollMap = build(rollOsu);

function playRolledForward() {
    const map = build(rollOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);   // line 0 finished, and 1500 is the instant entry into line 1
                                    // opens, so the caret rolls on and line 0 stays UNSEALED
    engine.update(1600);
    return engine;
}

// The same run once line 0 has actually sealed and the song has caught up with the caret: the
// coincident case, where the sung line is the caret's line and nothing about the old behaviour
// moves. A decoder-built line's window runs to the NEXT line's start, so line 0 does not seal until
// 3000 however early it was typed, and 3500 is half a character into line 1's own vocal.
function playRolledForwardThenSealed() {
    const engine = playRolledForward();
    engine.update(3500);
    return engine;
}

// A DRAGGING player, which is the case backlog 223 is about: only 'a' of line 0 goes in, so drag
// protection holds line 0 unsealed (and the caret on it) up to FLETCHER_DRAG_GRACE_MS past its own
// deadline, i.e. to 4500. At 3200 the vocal has been on line 1 for 200 ms while the seal cursor is
// still pinned at 0, which is the row the cursor alone could never report.
function playDraggingBehind() {
    const map = build(rollOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // one character of two, so the line stays owed and drag-deferred
    engine.update(3200);
    return engine;
}

// A caret PARKED COMPLETE, the case backlog 218 turned from a blink into seconds: both characters
// of line 0 are in by 1100, but entry into line 1 does not open until its activation (3000) less
// FLETCHER_DRAG_GRACE_MS, i.e. 1500, so the roll is refused and the caret sits at the end of a
// finished line with the vocal still singing it.
function playParkedComplete() {
    const map = build(rollOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1100);   // line 0 complete, but 1100 is 400 ms before entry opens
    engine.update(1200);
    return engine;
}

// The sung row's own index and the playhead position READ OFF IT, which is what the renderer feeds
// xAt() for the sweep fill, the sweep head and the sung caret, plus the two visibility flags and the
// per-row sweep fills the same frame would write.
function sungPlacement(engine, map, time) {
    const active = engine.activeLineIndex;
    const line = D.sungLineFor(engine.fletcherEnabled, active, engine.nextUnsealedLineIndex, map.lines, time);
    // The renderer's own gate on the sung head: rowFor() returns a row only for the focused line and
    // its two neighbours, so a song further off than that is not on the visible stack at all.
    const onStack = line >= 0 && Math.abs(line - active) <= 1;
    const lineComplete = active >= 0 && engine.isLineComplete(active);

    return {
        active: active,
        nextUnsealed: engine.nextUnsealedLineIndex,
        sungLine: line,
        sungPos: line >= 0 ? D.sungPositionAt(D.buildSungPoints(map.lines[line]), time) : -1,
        // What the caret's row would have given instead, for the C# side to hold the two apart.
        caretRowPos: D.sungPositionAt(D.buildSungPoints(map.lines[active]), time),
        lineComplete: lineComplete,
        shown: D.caretsVisible(active >= 0, lineComplete, engine.finished, onStack),
        // One fill per line, as updateSweeps would write them: only the sung row carries one.
        sweepFills: map.lines.map((l, i) => D.sweepFillFor(i, line, D.buildSungPoints(l), time))
    };
}

// Class and GLYPH exactly as paintRow would write them, for every cell of a line. Backlog 251
// removed the per-cell sync tint (cellFill / --tb-sync-fill) this used to also report; a correct
// cell now takes .tb-c-hit's flat colour straight from CSS, so there is nothing left to sample.
function paint(engine, lineIndex) {
    return engine.lines[lineIndex].cells.map(c => ({
        cls: D.cellClass(c, false, false),
        glyph: D.cellGlyph(c)
    }));
}

// --- instrumental gaps: the SAME lyric fixtures InstrumentalGapsTest feeds the C# mirror ---
//
// These strings are transcribed verbatim from tests/Typebeat.Web.Tests/InstrumentalGapsTest.cs, so
// the C# side can run Packages/Lyrics/InstrumentalGaps.Compute over them and hold the numbers below
// against its own, rather than against a second set of hand-copied literals.
const GAP_FIXTURES = {
    // Perceived instrumental of exactly MIN_GAP_MS: line 0 sings 1000-2000, line 1's first vocal
    // is at 12000. gapStart 3000, activation 12000, skipTarget 9000.
    exactlyTen:
        '{"version":2,"song_end_ms":40000,"granularity":"Word"}\n' +
        '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
        '{"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}\n',

    // The same map with the second line pulled one millisecond earlier: does not qualify.
    oneMsShort:
        '{"version":2,"song_end_ms":40000,"granularity":"Word"}\n' +
        '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
        '{"text":"cd","start_ms":11999,"end_ms":13000,"words":[{"text":"cd","start_ms":11999,"end_ms":13000,"score":1}]}\n',

    // Qualifies on the perceived stretch, but line 0's word overruns to the boundary so its last
    // typeable cell targets 9250 and the usable window is negative: dropped, not shown.
    noUsableWindow:
        '{"version":2,"song_end_ms":40000,"granularity":"Word"}\n' +
        '{"text":"abcd","start_ms":1000,"end_ms":2000,"words":[{"text":"abcd","start_ms":1000,"end_ms":15000,"score":1}]}\n' +
        '{"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}\n',

    // 30 s of silence before anything is sung: NOT an InstrumentalGaps gap (that is the intro skip).
    longIntro:
        '{"version":2,"song_end_ms":60000,"granularity":"Word"}\n' +
        '{"text":"ab","start_ms":30000,"end_ms":31000,"words":[{"text":"ab","start_ms":30000,"end_ms":31000,"score":1}]}\n' +
        '{"text":"cd","start_ms":32000,"end_ms":33000,"words":[{"text":"cd","start_ms":32000,"end_ms":33000,"score":1}]}\n',

    // Four lines, two long instrumentals: 52 s + 30 s = 82 s of allowance, the shape the server
    // prices into beatmaps.skippable_s. The 95000 line is 1 s behind its predecessor and opens none.
    twoOfFour:
        '{"version":2,"song_end_ms":140000,"granularity":"Word"}\n' +
        '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
        '{"text":"cd","start_ms":58000,"end_ms":59000,"words":[{"text":"cd","start_ms":58000,"end_ms":59000,"score":1}]}\n' +
        '{"text":"ef","start_ms":93000,"end_ms":94000,"words":[{"text":"ef","start_ms":93000,"end_ms":94000,"score":1}]}\n' +
        '{"text":"gh","start_ms":95000,"end_ms":96000,"words":[{"text":"gh","start_ms":95000,"end_ms":96000,"score":1}]}\n',

    // One line: no pair, so no gap and no allowance.
    single:
        '{"version":2,"song_end_ms":40000,"granularity":"Word"}\n' +
        '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n'
};

function gapReport(lyrics) {
    const map = build(OSU_HEADER + lyrics);
    return {
        gaps: D.computeGaps(map.lines).map(g => ({
            line: g.line,
            gapStartTime: g.gapStartTime,
            activationTime: g.activationTime,
            skipTarget: g.skipTarget,
            skippableMs: g.skipTarget - g.gapStartTime
        })),
        skippableMs: D.skippableMs(map.lines),
        introSkipTarget: D.introSkipTarget(map.lines),
        lastTypeableTarget: D.lastTypeableTarget(map.lines[0]),
        firstVocalTime: D.firstVocalTime(map.lines[0])
    };
}

// --- the skip trigger, and the WPM clock it must not move ---
//
// gapOsu is the exactlyTen fixture: line 0 "ab" sung 1000-2000, line 1 at 12000, so the qualifying
// gap runs [3000, 9000] and the skip lands at 9000 (line 1's activation less SKIP_LEAD_MS).

// Both characters of line 0 typed, so the line is COMPLETE and the caret parks on it (entry into
// line 1 does not open until 10500). This is the state the desktop reaches its skip overlay from.
function playGapMapComplete() {
    const map = build(gapOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    return { engine, map };
}

// One character of two: the line is still owed, so the song is still asking for characters and a
// typeable key (Space included) must be consumed for typing.
function playGapMapIncomplete() {
    const map = build(gapOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    return { engine, map };
}

// The WPM-clock trap, both arms. Advance from the gap's start to its skip target, which is exactly
// what a skip makes the engine do on its next tick, and report what activeTimeMs did.
function activeTimeAcrossSkip(run) {
    const { engine } = run;
    engine.update(3000);
    const before = engine.activeTimeMs;
    engine.update(9000);
    return { before: before, after: engine.activeTimeMs, moved: engine.activeTimeMs - before };
}

// What a Space press does in a given state, expressed as the two facts the key handler branches on
// and the cell it would land in.
function spaceInState(run, time) {
    const { engine, map } = run;
    const active = engine.activeLineIndex >= 0;
    const complete = active && engine.isLineComplete(engine.activeLineIndex);
    const upcoming = D.upcomingLineIndex(engine.activeLineIndex, complete, engine.nextSealIndex);
    const allowed = D.skipAllowed(active, complete, false);
    return {
        active: active,
        lineComplete: complete,
        upcomingLine: upcoming,
        skipAllowed: allowed,
        skipTarget: allowed ? D.skipTargetAt(D.computeGaps(map.lines), D.introSkipTarget(map.lines), upcoming, time) : null
    };
}

// A Space pressed on the word gap of the "ab cd" line, with the line still incomplete: the cell it
// lands in must be the gap, judged, and no skip may be offered.
function spaceIsAWordGapCharacter() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    const active = engine.activeLineIndex >= 0;
    const complete = active && engine.isLineComplete(engine.activeLineIndex);
    const before = engine.caretIndex;
    engine.processKey(' ', 2000);
    return {
        skipAllowed: D.skipAllowed(active, complete, false),
        caretBefore: before,
        caretAfter: engine.caretIndex,
        gapCellState: engine.lines[0].cells[2].state,
        gapCellExpected: engine.lines[0].cells[2].expected
    };
}

// --- the first-clear Discord nudge (backlog 289) ---
//
// Two halves, driven together because neither is the feature on its own: play.js decides (and spends
// the per-browser flag), typebeat-player.js renders what it is handed. A DISTINCTIVE invite goes in
// here, so the C# side can prove the href is the one that came from the page's data-discord-url and
// not a second hardcoded copy of the real invite.
const NUDGE_URL = 'https://discord.test/invite-from-the-data-attribute';

function fakeStorage() {
    const values = new Map();
    return {
        getItem: (k) => (values.has(k) ? values.get(k) : null),
        setItem: (k, v) => { values.set(k, String(v)); },
        size: () => values.size
    };
}

// A private window / blocked storage: every call throws, including the read.
const blockedStorage = {
    getItem: () => { throw new Error('storage is blocked'); },
    setItem: () => { throw new Error('storage is blocked'); }
};

// The whole chain exactly as showResults runs it: play.js's decision behind the player's own hook
// wrapper, then the markup the card renders for the answer (null html = no block on the card).
function nudgeOn(storage, passed) {
    const url = D.nudgeUrlFor((results) => PAGE.takeDiscordNudge(storage, results.passed, NUDGE_URL), { passed: passed });
    return { url: url, html: url === null ? null : D.nudgeHtml(url) };
}

// --- rolling WPM ring ---
function ring(pushes) {
    const r = D.makeRollingWpm(D.constants.ROLLING_WPM_WINDOW);
    for (let i = 0; i < pushes; i++) r.push(i * 100);
    return r.value(-1);
}

// --- the key handler (backlog 305), driven through the shipped router ---
//
// routeKeyDown is the whole of mountPlayer's keydown listener, over a host. This one stands in for
// the page: a settable clock, the map's skip windows, a selection slot, and a seek that is only
// recorded (nothing here plays audio, so a skip is a fact to report rather than a clock jump).

function fakeKey(key, mods) {
    const m = mods || {};
    const e = {
        key: key,
        code: key === ' ' ? 'Space' : (key.length === 1 && /[a-z]/i.test(key) ? 'Key' + key.toUpperCase() : key),
        ctrlKey: !!m.ctrl,
        altKey: !!m.alt,
        metaKey: !!m.meta,
        shiftKey: !!m.shift,
        repeat: !!m.repeat,
        prevented: false,
        preventDefault: function () { this.prevented = true; }
    };
    return e;
}

function keyHostFor(engine, map) {
    const host = {
        engine: engine,
        clock: 0,
        now: function () { return host.clock; },
        gaps: D.computeGaps(map.lines),
        introTarget: D.introSkipTarget(map.lines),
        selection: null,
        getSelection: function () { return host.selection; },
        setSelection: function (s) { host.selection = s; },
        skips: [],
        performSkip: function (target) { host.skips.push(target); }
    };
    return host;
}

// Every engine op the router makes, recorded with the arguments it was handed. The spies forward
// EVERY argument (the CLAUDE.md rule for harness spies: a dropped one silently changes the engine).
function spyOnOps(engine) {
    const calls = [];
    const key = engine.processKey.bind(engine);
    const enter = engine.processEnter.bind(engine);
    const backspace = engine.processBackspace.bind(engine);
    engine.processKey = function (c, t) { const r = key.apply(null, arguments); calls.push({ fn: 'key', c: c, t: t, result: r }); return r; };
    engine.processEnter = function (t) { const r = enter.apply(null, arguments); calls.push({ fn: 'enter', t: t, result: r }); return r; };
    engine.processBackspace = function () { const r = backspace.apply(null, arguments); calls.push({ fn: 'backspace', result: r }); return r; };
    return calls;
}

// The skip truth table's missing row: the caret PARKED at the head of a line it has not touched,
// with the song still on the line behind it. gapOsu with line 0 typed out: entry into line 1 opens
// at 10500 (its 12000 cue less FLETCHER_DRAG_GRACE_MS), so at 10600 the rush bound has handed the
// caret on and the song is nowhere near it. skipAllowed says "typing" here (a line is active and
// incomplete), which is exactly why the old handler typed the space into it as a word skip.
function spaceOnAParkedUntouchedHead() {
    const map = build(gapOsu);
    const engine = new TB.TypingEngine(map); // the browser's own defaults: word skip ON
    const host = keyHostFor(engine, map);
    const calls = spyOnOps(engine);

    host.clock = 1000; D.routeKeyDown(fakeKey('a'), host);
    host.clock = 2000; D.routeKeyDown(fakeKey('b'), host);
    engine.update(10600);
    calls.length = 0;

    const before = { line: engine.activeLineIndex, cell: engine.caretIndex };
    const untouched = engine.activeLineUntouched;
    const songOnIt = engine.songIsOnTheCaretsLine;
    const allowed = D.skipAllowedFor(engine, 10600, false);

    const e = fakeKey(' ');
    host.clock = 10600.25;
    D.routeKeyDown(e, host);

    // And the first real letter afterwards types normally: the drop is not a lockout.
    const letter = fakeKey('c');
    host.clock = 10700;
    D.routeKeyDown(letter, host);

    return {
        at: before,
        activeLineUntouched: untouched,
        songIsOnTheCaretsLine: songOnIt,
        skipAllowed: allowed,
        dropped: D.spaceIsDropped({ fletcherEnabled: engine.fletcherEnabled, songIsOnTheCaretsLine: songOnIt, activeLineUntouched: untouched }),
        spacePrevented: e.prevented,
        spaceEngineCalls: calls.filter(c => c.fn === 'key' && c.c === ' ').length,
        skipsTaken: host.skips.length,
        cellsAfterSpace: engine.lines[1].cells.map(c => c.state),
        letterLanded: engine.lines[1].cells[0].state,
        untouchedAfterLetter: engine.activeLineUntouched,
        combo: engine.combo
    };
}

// The modifier matrix: which chords reach which gesture. Ctrl+Backspace and Ctrl+A match with
// extra modifiers held (the desktop's KeyCombinationMatchingMode.Any), AltGr arriving as Ctrl plus
// Alt; Meta never. Enter reaches the line skip under Ctrl or Alt, never under Meta.
function gestureMatrix() {
    const rows = [
        ['Backspace', {}], ['Backspace', { ctrl: true }], ['Backspace', { ctrl: true, alt: true }],
        ['Backspace', { alt: true }], ['Backspace', { ctrl: true, meta: true }], ['Backspace', { ctrl: true, shift: true }],
        ['a', { ctrl: true }], ['a', { ctrl: true, alt: true }], ['A', { ctrl: true, shift: true }],
        ['a', { alt: true }], ['a', { meta: true }], ['a', { ctrl: true, meta: true }]
    ];
    return rows.map(([key, mods]) => ({ key: key, mods: Object.keys(mods).sort().join('+'), gesture: D.isWordGesture(fakeKey(key, mods)) }));
}

// Enter under each modifier, on a line with something left to give up: whether the router handed
// it to processEnter at all.
function enterUnder(mods) {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    const host = keyHostFor(engine, map);
    host.clock = 1000; D.routeKeyDown(fakeKey('a'), host);
    const calls = spyOnOps(engine);
    const e = fakeKey('Enter', mods);
    host.clock = 1200;
    D.routeKeyDown(e, host);
    return { reached: calls.some(c => c.fn === 'enter'), prevented: e.prevented, caret: engine.caretIndex };
}

// AltGr+Backspace erases a whole word, as Ctrl+Backspace does: "ab" typed, then the chord.
function altGrBackspace() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    const host = keyHostFor(engine, map);
    host.clock = 1000; D.routeKeyDown(fakeKey('a'), host);
    host.clock = 1500; D.routeKeyDown(fakeKey('b'), host);
    const before = engine.caretIndex;
    const e = fakeKey('Backspace', { ctrl: true, alt: true });
    host.clock = 1600;
    D.routeKeyDown(e, host);
    return { before: before, after: engine.caretIndex, prevented: e.prevented };
}

// The Gatekeeper erase gate (TypeBeatPlayfield: !AllowWrongInput && no selection): a plain erase is
// inert, while an erase over a live selection still collapses it. Latent in the browser (it is
// never Gatekeeper), which is why it is driven with the flag set by hand.
function gatekeeperErase() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.allowWrongInput = false;
    const host = keyHostFor(engine, map);
    host.clock = 1000; D.routeKeyDown(fakeKey('a'), host);
    host.clock = 1500; D.routeKeyDown(fakeKey('b'), host);
    const calls = spyOnOps(engine);

    host.clock = 1600; D.routeKeyDown(fakeKey('Backspace'), host);
    const plainErases = calls.filter(c => c.fn === 'backspace').length;
    const caretAfterPlain = engine.caretIndex;

    host.setSelection({ lineIndex: 0, startCell: 1, endCell: engine.caretIndex });
    host.clock = 1700; D.routeKeyDown(fakeKey('Backspace'), host);

    return { plainErases: plainErases, caretAfterPlain: caretAfterPlain, caretAfterSelection: engine.caretIndex, selectionLeft: host.selection };
}

// THE KEYSTROKE PROTOCOL, live (KeyHandlerOrderLiveParityTest's browser arm). A five-line map
// played on a 60 Hz tick whose phase (3.3 ms) keeps every tick off a whole millisecond, with every
// press landing BETWEEN two ticks and just after an instant the engine owes a transition, so the
// press is only judged where the desktop judges it if the router advances the engine to the press
// before anything else:
//
//   L0 "ab cd" [1000, 4000)  a 1000, b 1500, ' ' 2000, c 2000, d 2500
//   L1 "ef"    [4000, 8000)  e 4000, f 4500      (entry opens 2500)
//   L2 "gh"    [8000, 12000) g 8000, h 8500      (entry opens 6500)
//   L3 "ij"    [12000, 16000) i 12000, j 12500   (entry opens 10500)
//   L4 "kl"    [16000, ...)  k 20000, l 20500, activation 18500 (entry opens 17000)
//
// 1000.2 'a': L0's ACTIVATION falls between the tick and the press (the pre-roll dead zone; the
//        first line's head start covers the press either way, so this is the benign arm).
// 2500.3 Enter: gives L0 up with three cells owed; entry into L1 is open, so the caret rolls on.
// 3000.5 Space: the parked-untouched-head DROP (the song is still on L0).
// 5500.4 Backspace: L0's held deadline (4000 + the 1500 drag grace) fell between the tick and the
//        press. The desktop has SEALED it, so the head-of-line backspace is inert; judged against
//        the stale tick it steps back up into L0.
// 5510.5 'e': a MIDPOINT, which banker's rounding sends to 5510 where Math.round sends it to
//        5511 (a late press, so the judged delta moves with it).
// 9500.4 'g': L1's DRAG CUTOFF (8000 + 1500) fell between the tick and the press. The desktop has
//        pushed the caret to L2 cell 0, so 'g' is L2's first letter; against the stale tick it is a
//        wrong key on L1's 'f'.
// 9600.5 'h': another midpoint (9600 on the desktop), which finishes L2 ahead of L3's entry.
// 10500.5 'i': the RUSH SNAP. L2 finished early, so the caret sat parked past its end until entry
//        into L3 opened at 10500; against the stale tick the press hits the "line fully typed"
//        guard and is lost.
// 10600.2 'j': an ordinary press, finishing L3.
// 18500.4 'x': a WRONG key into L4 (typed through, as allow-wrong-input does), so a 'wrong' cell
//        sits behind the caret and ActiveLineUntouched has its second clause to answer.
// 20500.7 'l': L4's last letter, typed with the Wrong cell behind it.
const KEY_ORDER_OSU = OSU_HEADER +
    '{"granularity":"line","version":2,"song_end_ms":30000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n' +
    '{"text":"ef","start_ms":4000,"end_ms":5000,"words":[{"text":"ef","start_ms":4000,"end_ms":5000,"score":1}]}\n' +
    '{"text":"gh","start_ms":8000,"end_ms":9000,"words":[{"text":"gh","start_ms":8000,"end_ms":9000,"score":1}]}\n' +
    '{"text":"ij","start_ms":12000,"end_ms":13000,"words":[{"text":"ij","start_ms":12000,"end_ms":13000,"score":1}]}\n' +
    '{"text":"kl","start_ms":16000,"end_ms":21000,"words":[{"text":"kl","start_ms":20000,"end_ms":21000,"score":1}]}\n';

const KEY_ORDER_PRESSES = [
    { t: 1000.2, key: 'a' }, { t: 1500.7, key: 'b' },
    { t: 2500.3, key: 'Enter' },
    { t: 3000.5, key: ' ' },
    { t: 5500.4, key: 'Backspace' },
    { t: 5510.5, key: 'e' },
    { t: 9500.4, key: 'g' }, { t: 9600.5, key: 'h' },
    { t: 10500.5, key: 'i' }, { t: 10600.2, key: 'j' },
    { t: 18500.4, key: 'x' }, { t: 20500.7, key: 'l' }
];

const KEY_ORDER_TICK_PHASE = 3.3;
const KEY_ORDER_TICK_MS = 1000 / 60;
const KEY_ORDER_END = 26000;

function keyOrderRun() {
    const map = build(KEY_ORDER_OSU);
    const engine = new TB.TypingEngine(map); // the browser's own defaults, exactly as begin() builds it
    const host = keyHostFor(engine, map);
    const calls = spyOnOps(engine);

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    const where = () => ({ line: engine.activeLineIndex, cell: engine.caretIndex, seal: engine.nextSealIndex });

    function reading() {
        return {
            line: engine.activeLineIndex,
            cell: engine.caretIndex,
            nextSealIndex: engine.nextSealIndex,
            finished: engine.finished,
            combo: engine.combo,
            maxCombo: engine.maxCombo,
            comboBreaks: breaks,
            mistypes: engine.mistypes,
            score: engine.score,
            liveWpm: engine.liveWpm,
            counts: Object.assign({}, engine.counts),
            activeLineUntouched: engine.activeLineUntouched,
            songIsOnTheCaretsLine: engine.songIsOnTheCaretsLine,
            states: engine.lines.map(l => l.cells.map(c => c.state)),
            deltas: engine.lines.map(l => l.cells.map(c => (c.judgedDelta === null || c.judgedDelta === undefined) ? null : c.judgedDelta))
        };
    }

    // The state the router judged the press against, read at the moment its own update returns.
    // Observed through a spy rather than reproduced here: an update made by the harness would
    // advance the engine for the router and hide exactly the defect this section exists to catch.
    // A router that never advances the engine leaves it null.
    let pressing = false;
    let judgedAgainst = null;
    const update = engine.update.bind(engine);
    engine.update = function (t) {
        const r = update.apply(null, arguments);
        if (pressing && judgedAgainst === null) {
            judgedAgainst = {
                t: t, line: engine.activeLineIndex, cell: engine.caretIndex, seal: engine.nextSealIndex,
                untouched: engine.activeLineUntouched, songOnIt: engine.songIsOnTheCaretsLine
            };
        }
        return r;
    };

    const steps = [];
    let next = 0;

    for (let k = 0; ; k++) {
        const tick = KEY_ORDER_TICK_PHASE + k * KEY_ORDER_TICK_MS;

        while (next < KEY_ORDER_PRESSES.length && KEY_ORDER_PRESSES[next].t < tick) {
            const press = KEY_ORDER_PRESSES[next++];
            const before = where();

            calls.length = 0;
            judgedAgainst = null;
            pressing = true;
            const e = fakeKey(press.key);
            host.clock = press.t;
            D.routeKeyDown(e, host);
            pressing = false;

            steps.push(Object.assign({
                op: 'press',
                key: press.key,
                t: press.t,
                prevented: e.prevented,
                calls: calls.slice(),
                before: before,
                judgedAgainst: judgedAgainst
            }, reading()));
        }

        if (tick > KEY_ORDER_END) break;

        engine.update(tick);
        steps.push(Object.assign({ op: 'update', t: tick }, reading()));
    }

    return {
        lines: map.lines.map(l => ({
            activationTime: l.activationTime,
            endTime: l.endTime,
            sealGraceMs: l.sealGraceMs,
            cells: l.cells.map(c => ({ expected: c.expected, target: c.target }))
        })),
        entryOpensAt: map.lines.slice(1).map((_, i) => engine.entryOpensAt(i + 1)),
        steps: steps,
        skips: host.skips
    };
}

// --- keystroke to character (backlog 309) ---
//
// The pinned table, one row per [e.key, e.code, shiftKey, prevWasDead]: keyToChar's answer for
// each (null = dropped). WebplayDisplayTest holds the answers; WireCompat's KeyToCharParityTest
// generates a far larger table from the game's KeyCharMap and runs it through KeyToCharHarness.cjs.
const KEY_TO_CHAR_ROWS = [
    // rule 1: e.key is already typeable, whatever the position (Dvorak 'e' sits on KeyD)
    ['a', 'KeyA', false, false], ['A', 'KeyA', true, false], ['e', 'KeyD', false, false],
    ['7', 'Digit7', false, false], ['5', 'Numpad5', false, false], ['a', 'KeyQ', false, true],
    // rule 2: the vowel after a dead key, folded; only on a letter position, only after a dead key
    ['ê', 'KeyE', false, true], ['Ê', 'KeyE', true, true], ['â', 'KeyQ', false, true],
    ['ë', 'KeyE', false, true], ['ý', 'KeyY', false, true],
    ['ê', 'KeyE', false, false], ['ö', 'Semicolon', false, true], ['ù', 'Quote', false, true],
    ['ç', 'Digit9', false, true], ['ß', 'Minus', false, true], ['Dead', 'BracketLeft', false, true],
    // rule 3: the digit row and keypad by position, whatever Shift or the layout says
    ['!', 'Digit1', true, false], ['@', 'Digit2', true, false], ['é', 'Digit2', false, false],
    ['à', 'Digit0', false, false], ['&', 'Digit1', false, false], ['§', 'Digit3', true, false],
    ['End', 'Numpad1', false, false], ['Insert', 'Numpad0', true, false],
    // rule 4: a non-Latin letter types its position, cased as e.key is
    ['ф', 'KeyA', false, false], ['Ф', 'KeyA', true, false], ['я', 'KeyZ', false, false],
    ['ς', 'KeyW', false, false], ['Σ', 'KeyS', true, false], ['ب', 'KeyF', false, false],
    // rule 5: dropped
    [',', 'KeyM', false, false], ['?', 'KeyM', true, false], [';', 'KeyQ', false, false],
    ['Dead', 'BracketLeft', false, false], ['Dead', 'Equal', true, false],
    ['ö', 'Semicolon', false, false], ['ü', 'BracketLeft', false, false], ['ß', 'Minus', false, false],
    ['ж', 'Semicolon', false, false], ['б', 'Comma', false, false], ['ù', 'Quote', false, false],
    ['é', 'KeyE', false, false], ['Unidentified', 'KeyA', false, false], ['Process', 'KeyA', false, false],
    ['Shift', 'ShiftLeft', true, false], ['.', 'Period', false, false], ['ñ', 'Semicolon', false, false]
];

function keyToCharTable() {
    return KEY_TO_CHAR_ROWS.map(([key, code, shift, dead]) => ({
        key: key, code: code, shift: shift, dead: dead,
        ch: D.keyToChar({ key: key, code: code, shiftKey: shift }, dead)
    }));
}

function keyEv(key, code, mods) {
    const e = fakeKey(key, mods);
    e.code = code;
    return e;
}

// The dead-key state through the SHIPPED router, on "ab cd": an Azerty circumflex, then Shift (a
// modifier, which must not end the sequence), then the composed capital on the KeyQ position, is
// the lyric's 'a'; 'b' follows on its own cell rather than one cell early.
function routedDeadKeyComposes() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    const host = keyHostFor(engine, map);
    const calls = spyOnOps(engine);
    const trail = [];

    host.clock = 1000;
    D.routeKeyDown(keyEv('Dead', 'BracketLeft'), host); trail.push(host.prevWasDead);
    D.routeKeyDown(keyEv('Shift', 'ShiftLeft', { shift: true }), host); trail.push(host.prevWasDead);
    D.routeKeyDown(keyEv('Â', 'KeyQ', { shift: true }), host); trail.push(host.prevWasDead);
    host.clock = 1500;
    D.routeKeyDown(keyEv('b', 'KeyB'), host); trail.push(host.prevWasDead);

    return {
        chars: calls.filter(c => c.fn === 'key').map(c => c.c),
        states: engine.lines[0].cells.slice(0, 2).map(c => c.state),
        deadTrail: trail
    };
}

// A dead key followed by any OTHER key ends the sequence: a composed vowel after that is dropped.
function routedDeadKeyEnds() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    const host = keyHostFor(engine, map);
    const calls = spyOnOps(engine);

    host.clock = 1000;
    D.routeKeyDown(keyEv('Dead', 'BracketLeft'), host);
    D.routeKeyDown(keyEv('a', 'KeyQ'), host);
    host.clock = 1500;
    D.routeKeyDown(keyEv('ê', 'KeyE'), host);

    return { chars: calls.filter(c => c.fn === 'key').map(c => c.c), prevWasDead: host.prevWasDead };
}

// The digit row by position through the router: Shift+1 reaches processKey as '1'.
function routedShiftDigit() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    const host = keyHostFor(engine, map);
    const calls = spyOnOps(engine);
    const e = keyEv('!', 'Digit1', { shift: true });
    host.clock = 1000;
    D.routeKeyDown(e, host);
    return { chars: calls.filter(c => c.fn === 'key').map(c => c.c), prevented: e.prevented };
}

const perfect = playPerfect();
const partial = playOneKeyThenSeal();
const late = playOneLatePress();

const out = {
    // The line the sung playhead sweeps across, straight off the decoder.
    cellTargets: line0.cells.map(c => c.target),
    lineStart: line0.startTime,
    lineEnd: line0.endTime,
    lineSingEnd: line0.singEndTime,
    lineActivation: line0.activationTime,

    // Sung playhead: the polyline anchor points, then the golden samples from the desktop
    // suite (clamped before start, mid-segment, a zero-length segment that must JUMP, the
    // final segment into sing end, clamped after).
    sungPointTimes: points.map(p => p.t),
    sungPointIndices: points.map(p => p.i),
    sungAt: [500, 1250, 2000, 2750, 9999].map(t => D.sungPositionAt(points, t)),

    // backlog 245: the sung-end flag dragged past the last word's own end must not move the sweep's
    // pace through the last character. lastUnitEnd is read straight off the line (not recomputed),
    // and the final anchor must land there, not on the dragged singEndTime.
    draggedLineSingEnd: draggedLine0.singEndTime,
    draggedLastUnitEnd: D.lastUnitEndOf(draggedLine0),
    draggedSungPointTimes: draggedPoints.map(p => p.t),
    draggedSungPointIndices: draggedPoints.map(p => p.i),
    draggedSungAt: [2600, 2900, 3200, 9999].map(t => D.sungPositionAt(draggedPoints, t)),

    // Cue-in bars: depleting over the final CUE_LEAD_MS, brightening as they land.
    cueLeadMs: D.constants.CUE_LEAD_MS,
    cueFull: D.cueBar(D.constants.CUE_LEAD_MS, 1),
    cueHalf: D.cueBar(D.constants.CUE_LEAD_MS / 2, 1),
    cueWordHalf: D.cueBar(D.constants.CUE_LEAD_MS / 2, 0.5),
    cueLanded: D.cueBar(0, 1),
    cueTooEarly: D.cueBar(D.constants.CUE_LEAD_MS + 1, 1),

    // Which line the cue belongs to. On the two-line gap map: nothing active -> the next
    // unsealed line; line 1 active but still inside its own lead-in -> itself; line 0 active
    // and past its own first word -> the line after it.
    cueTargetIdle: D.cueTargetLine(gapMap.lines, -1, 0, 0),
    cueTargetOwnLeadIn: D.cueTargetLine(gapMap.lines, 1, 1, 11000),
    cueTargetNext: D.cueTargetLine(gapMap.lines, 0, 0, 1500),

    // THE PUSH WARNING (backlog 263): the same bar, mirrored. It hangs off the END of the line the
    // player is ON and counts down the drag cutoff instead of a line's arrival. The line's own
    // deadline is 4000 with no seal grace and the cutoff is therefore 5500, so with CUE_LEAD_MS and
    // FLETCHER_DRAG_GRACE_MS both 1500 the window opens at exactly 4000: the instant the song leaves
    // the line's own grace, which makes the whole of the borrowed time what the player watches drain.
    pushWarning: (function () {
        const dragging = playDragging();
        const samples = [3999, 4000, 4750, 5499].map(t => { dragging.update(t); return pushWarningAt(dragging, t); });

        dragging.update(5500);

        return {
            lineEnd: dragging.lines[0].endTime,
            lineSealGraceMs: dragging.lines[0].sealGraceMs,
            samples: samples,
            // The push has landed: on this one-line map that ends the run, and a finished run is
            // warned about nothing.
            afterTheCutoff: pushWarningAt(dragging, 5500),
            // Nothing owed, nothing coming: typing the last cell out calls the warning off where the
            // player stands, at a time the bar would otherwise be mid-window.
            typedOut: pushWarningAt(playTypedOut(), 4500)
        };
    })(),

    // Which line carries the sung sweep, sung head and sung caret (LyricStage.sungLineFor). The
    // rule alone, on the two-line roll map with made-up cursor coordinates. At 1600 the playhead is
    // inside line 0's window (which runs to 3000), so the walk cannot run and these are the four
    // answers that shipped before backlog 223: pinned caret -> always the active line; flexible
    // caret parked one line ahead -> the first unsealed line; everything sealed -> the active one.
    sungLinePinned: D.sungLineFor(false, 1, 0, rollMap.lines, 1600),
    sungLineParked: D.sungLineFor(true, 1, 0, rollMap.lines, 1600),
    sungLineCoincident: D.sungLineFor(true, 1, 1, rollMap.lines, 1600),
    sungLineAllSealed: D.sungLineFor(true, 1, -1, rollMap.lines, 1600),

    // The walk itself (backlog 223), on the same coordinates. Line 0 closes at endTime + sealGraceMs
    // = 3000 + 0, and the step is >=: one millisecond earlier it is still the sung line, and at the
    // instant itself the song has left it, however far behind the drag-deferred seal cursor is. The
    // walk never runs off the end: the last line has no successor to step on to.
    sungLineJustInside: D.sungLineFor(true, 0, 0, rollMap.lines, 2999),
    sungLineAtWindowClose: D.sungLineFor(true, 0, 0, rollMap.lines, 3000),
    sungLineWalked: D.sungLineFor(true, 0, 0, rollMap.lines, 3200),
    sungLineWalkStopsAtLast: D.sungLineFor(true, 1, 1, rollMap.lines, 99999),

    // Both heads' visibility, the rule alone (LyricStage's setCaretsVisible call). Exactly one flag
    // moved in 223, caretsLineComplete's sung half: a complete line still hides the TYPING caret and
    // no longer hides the playhead.
    caretsTyping: D.caretsVisible(true, false, false, true),
    caretsLineComplete: D.caretsVisible(true, true, false, true),
    caretsFinished: D.caretsVisible(true, true, true, true),
    caretsOffStack: D.caretsVisible(true, false, false, false),
    caretsIdle: D.caretsVisible(false, false, false, true),

    // And the rule on a real run: the caret rolled forward onto line 1 before line 0 sealed, then
    // the same engine once line 0 has sealed, then the two cases 223 is about (dragging behind the
    // song, and parked complete on the line still being sung).
    sungParked: sungPlacement(playRolledForward(), rollMap, 1600),
    sungSealed: sungPlacement(playRolledForwardThenSealed(), rollMap, 3500),
    sungDragging: sungPlacement(playDraggingBehind(), rollMap, 3200),
    sungParkedComplete: sungPlacement(playParkedComplete(), rollMap, 1200),

    // Caret damping + blink.
    dampHalf: D.dampContinuously(0, 100, 35, 35),
    dampNone: D.dampContinuously(10, 100, 35, 0),
    caretAlphaTyping: D.caretAlpha(0, false, true),
    caretAlphaJustBefore: D.caretAlpha(529, false, true),
    caretAlphaMoving: D.caretAlpha(99999, true, true),
    caretAlphaTrough: D.caretAlpha(530 + 265, false, true),
    caretAlphaCrest: D.caretAlpha(530 + 530, false, true),
    caretAlphaNoBlink: D.caretAlpha(99999, false, false),

    // Rolling WPM: 30 presses 100ms apart span 2900ms of active time, and the ring must report
    // the same once it has wrapped (the window slides, it does not reset).
    rollingEmpty: ring(0),
    rollingOne: ring(1),
    rollingFull: ring(30),
    rollingWrapped: ring(40),
    rollingZeroSpan: (() => { const r = D.makeRollingWpm(30); r.push(500); r.push(500); return r.value(-1); })(),

    // Backlog 251 removed the browser's sync quality readout and its tint ramp (syncQuality,
    // syncTintFill, SYNC_TINT_FLOOR are gone from typebeat-player.js), so there is nothing left to
    // sample here; the classes/glyphs paintRow would still write on real runs are below.
    mixedPaint: paint(playMixedTiming(), 0),
    sealedPaint: paint(playOneKeyThenSeal(), 0),
    backspacedPaint: paint(playThenBackspace(), 0),
    freestylePaint: paint(playFreestyle(), 0),
    gapTypoPaint: paint(playGapTypo(), 0),
    gapTypoErasedPaint: paint(playGapTypoErased(), 0),
    midWordSpacePaint: paint(playMidWordSpaceTypo(), 0),

    // Live HUD readouts off real engine runs.
    perfectStats: D.liveStats(perfect),
    partialStats: D.liveStats(partial),
    lateStats: D.liveStats(late),

    // The display layer must not have moved the score: the same run through the untouched
    // scorer still reads a clean X.
    perfectScore: (() => { const s = TB.computeScore(perfect); return { rank: s.rank, completion: s.completion, totalScore: s.totalScore }; })(),

    // Easing used by the line-change scroll.
    outQuint: [0, 0.5, 1].map(D.outQuint),

    // ---- the instrumental-skip rule (backlog 230), a THIRD copy of a cross-repo-pinned one ----
    gapConstants: {
        MIN_GAP_MS: D.constants.MIN_GAP_MS,
        GAP_START_SETTLE_MS: D.constants.GAP_START_SETTLE_MS,
        MIN_SKIP_WINDOW_MS: D.constants.MIN_SKIP_WINDOW_MS,
        SKIP_LEAD_MS: D.constants.SKIP_LEAD_MS
    },
    gapExactlyTen: gapReport(GAP_FIXTURES.exactlyTen),
    gapOneMsShort: gapReport(GAP_FIXTURES.oneMsShort),
    gapNoUsableWindow: gapReport(GAP_FIXTURES.noUsableWindow),
    gapLongIntro: gapReport(GAP_FIXTURES.longIntro),
    gapTwoOfFour: gapReport(GAP_FIXTURES.twoOfFour),
    gapSingleLine: gapReport(GAP_FIXTURES.single),

    // Which line the chip and the skip look up. Not the seal cursor: a caret parked COMPLETE on a
    // line is waiting for the NEXT one, which is the whole of a real map's instrumental.
    upcomingIdle: D.upcomingLineIndex(-1, false, 0),
    upcomingParkedComplete: D.upcomingLineIndex(0, true, 0),
    upcomingOnItsOwnLine: D.upcomingLineIndex(1, false, 0),

    // TypeBeatPlayfield's fall-through, the predicate that decides skip-or-character.
    skipAllowedIdle: D.skipAllowed(false, false, false),
    skipAllowedComplete: D.skipAllowed(true, true, false),
    skipAllowedCompleteWithSelection: D.skipAllowed(true, true, true),
    skipAllowedTyping: D.skipAllowed(true, false, false),
    skipAllowedTypingWithSelection: D.skipAllowed(true, false, true),

    // The window itself, sampled around its two edges on the exactlyTen map (gap [3000, 9000)).
    // -1 stands for "no skip offered" so the array stays all-numeric for the C# reader.
    skipWindow: (() => {
        const map = build(gapOsu);
        const gaps = D.computeGaps(map.lines);
        const intro = D.introSkipTarget(map.lines);
        return [2999, 3000, 8999, 9000].map(t => { const v = D.skipTargetAt(gaps, intro, 1, t); return v === null ? -1 : v; });
    })(),

    // The intro, which is the desktop's separate intro SkipOverlay: first vocal less SKIP_LEAD_MS.
    introWindow: (() => {
        const map = build(OSU_HEADER + GAP_FIXTURES.longIntro);
        const gaps = D.computeGaps(map.lines);
        const intro = D.introSkipTarget(map.lines);
        return [0, 26999, 27000].map(t => { const v = D.skipTargetAt(gaps, intro, 0, t); return v === null ? -1 : v; });
    })(),

    // Real runs: the state a skip may fire from, the state it may not, and the word-gap press.
    spaceParked: spaceInState(playGapMapComplete(), 3000),
    spaceTyping: spaceInState(playGapMapIncomplete(), 3000),
    spaceAsWordGap: spaceIsAWordGapCharacter(),

    // ---- the first-clear Discord nudge (backlog 289) ----
    discordNudgeKey: PAGE.DISCORD_NUDGE_KEY,
    nudgeUrl: NUDGE_URL,

    // A fresh browser clearing its first map, and then clearing another one.
    nudgeFirstClear: (() => {
        const s = fakeStorage();
        const first = nudgeOn(s, true);
        return { first: first, flag: s.getItem(PAGE.DISCORD_NUDGE_KEY), second: nudgeOn(s, true) };
    })(),

    // A FAIL shows nothing and spends nothing, so the clear that comes after it still gets the one
    // invitation this browser is owed.
    nudgeAfterFail: (() => {
        const s = fakeStorage();
        const failed = nudgeOn(s, false);
        return {
            failed: failed,
            flagAfterFail: s.getItem(PAGE.DISCORD_NUDGE_KEY),
            storedKeys: s.size(),
            thenCleared: nudgeOn(s, true)
        };
    })(),

    // Storage that throws, and no storage at all: no nudge, no exception out of the decision.
    nudgeBlockedStorage: nudgeOn(blockedStorage, true),
    nudgeNoStorage: nudgeOn(null, true),

    // A stage root with no data-discord-url: nothing to link, so nothing shown and the flag is left
    // unspent rather than burnt on a nudge the player never saw.
    nudgeNoUrl: (() => {
        const s = fakeStorage();
        const url = PAGE.takeDiscordNudge(s, true, '');
        return { url: url, storedKeys: s.size() };
    })(),

    // The player's own wrapper around the host hook: a host that throws, and a host that passes no
    // hook at all, both render nothing rather than taking the results card down with them.
    nudgeHookThrew: (() => {
        // The wrapper reports the host's error to the console, which is right in a browser and pure
        // noise on this harness's stderr (the C# side prints stderr when a run fails), so silence it
        // for the one call that is supposed to throw.
        const report = console.error;
        console.error = () => {};
        try { return D.nudgeUrlFor(() => { throw new Error('the host blew up'); }, { passed: true }); }
        finally { console.error = report; }
    })(),
    nudgeNoHook: D.nudgeUrlFor(undefined, { passed: true }),
    nudgeHtml: D.nudgeHtml(NUDGE_URL),

    // THE WPM-CLOCK PIN. Crossing the gap with the line COMPLETE (the only state a skip is offered
    // in) must not move activeTimeMs at all; crossing it with the line still owed does, which is
    // exactly the divergence the gating exists to prevent.
    activeTimeParked: activeTimeAcrossSkip(playGapMapComplete()),
    activeTimeTyping: activeTimeAcrossSkip(playGapMapIncomplete()),

    // ---- the key handler (backlog 305) ----
    spaceParkedHead: spaceOnAParkedUntouchedHead(),
    gestureMatrix: gestureMatrix(),
    enterPlain: enterUnder({}),
    enterCtrl: enterUnder({ ctrl: true }),
    enterAlt: enterUnder({ alt: true }),
    enterMeta: enterUnder({ meta: true }),
    altGrBackspace: altGrBackspace(),
    gatekeeperErase: gatekeeperErase(),
    // C# Math.Round's MidpointRounding.ToEven, on the halves either side of an even and an odd
    // integer, one off a half each way, and a negative half.
    roundHalfEven: [2000.5, 2001.5, 2000.49, 2000.51, 5510.5, 9600.5, -0.5, -1.5, 3.3].map(D.roundHalfEven),
    keyOrder: keyOrderRun(),

    // ---- the clock start and the intro skip anchor (backlog 308) ----
    // A line 0 stamped at 10000 whose first word is not sung until 14000: the two anchors the intro
    // skip could use are 4 s apart here, where the longIntro fixture has them equal. The desktop
    // lands at line0.startTime - 3000 = 7000; the old browser rule (first vocal - 3000) said 11000.
    clockLateFirstWord: (() => {
        const map = build(OSU_HEADER +
            '{"version":2,"song_end_ms":60000,"granularity":"Word"}\n' +
            '{"text":"ab","start_ms":10000,"end_ms":15000,"words":[{"text":"ab","start_ms":14000,"end_ms":15000,"score":1}]}\n' +
            '{"text":"cd","start_ms":30000,"end_ms":31000,"words":[{"text":"cd","start_ms":30000,"end_ms":31000,"score":1}]}\n');
        const start = D.gameplayStartTime(map);
        const intro = D.introSkipTarget(map.lines, start);
        return {
            lineStart: map.lines[0].startTime,
            firstVocalTime: D.firstVocalTime(map.lines[0]),
            gameplayStartTime: start,
            introSkipTarget: intro,
            introSkipTargetDefaulted: D.introSkipTarget(map.lines),
            window: [0, 6999, 7000, 10999].map(t => { const v = D.skipTargetAt([], intro, 0, t); return v === null ? -1 : v; })
        };
    })(),

    // The early-vocal pre-roll: a first line at 500, with no AudioLeadIn, with the 2000 the importer
    // writes for exactly this shape, and with a longer one that outreaches the 2000 term.
    clockEarlyVocal: [null, 2000, 5000].map(leadIn => {
        const header = leadIn === null ? OSU_HEADER : OSU_HEADER.replace('[General]\n', '[General]\nAudioLeadIn: ' + leadIn + '\n');
        const map = build(header +
            '{"version":2,"song_end_ms":20000,"granularity":"Word"}\n' +
            '{"text":"ab","start_ms":500,"end_ms":1500,"words":[{"text":"ab","start_ms":500,"end_ms":1500,"score":1}]}\n');
        const start = D.gameplayStartTime(map);
        const intro = D.introSkipTarget(map.lines, start);
        return { audioLeadIn: map.audioLeadIn, gameplayStartTime: start, introSkipTarget: intro === null ? -1 : intro };
    }),

    // A first line well past 2 s: the clock starts at 0 and the 30 s intro still skips to 27000.
    clockLongIntro: (() => {
        const map = build(OSU_HEADER + GAP_FIXTURES.longIntro);
        return { gameplayStartTime: D.gameplayStartTime(map), introSkipTarget: D.introSkipTarget(map.lines, D.gameplayStartTime(map)) };
    })(),

    // ---- keystroke to character (backlog 309) ----
    keyToChar: keyToCharTable(),
    routedDeadKeyComposes: routedDeadKeyComposes(),
    routedDeadKeyEnds: routedDeadKeyEnds(),
    routedShiftDigit: routedShiftDigit(),

    // ---- the HP bar and the start gate (backlog 306) ----
    healthBarSamples: [1, 0.5, 0.2, 0.19999, 0.05, 0, -0.1, 1.2].map(h => Object.assign({ health: h }, D.healthBar(h))),
    lowHealthThreshold: D.LOW_HEALTH_THRESHOLD,
    healthBarRuns: healthBarRuns(),
    startGateHint: D.START_GATE_HINT,

    // ---- stack layout and animation timings (backlog 322) ----
    perfectPopMs: D.constants.PERFECT_POP_MS,
    caretFadeMs: D.constants.CARET_FADE_MS,
    caretFade: (() => {
        // One fade in, one fade out, then a reversal half way through a fade in.
        const f = D.makeCaretFade(D.constants.CARET_FADE_MS);
        return [
            f(false, 0), f(true, 0), f(true, 60), f(true, 120), f(true, 500),
            f(false, 1000), f(false, 1060), f(false, 1120),
            f(true, 2000), f(false, 2060), f(false, 2120), f(false, 2180)
        ];
    })()
};

// THE HP BAR READS THE ACCOUNT (backlog 306). Two plays that never reject a key, so the rejection
// streak the bar used to read stays at 0 throughout: abcdOsu with a wrong letter on every lyric
// character, and a 43-cell line nobody types, whose seal takes the bar to 1 - 43 * 0.0225 = 0.0325,
// alive and under the danger threshold. The bar has to follow the health account in both.
function healthBarRuns() {
    const idleOsu = OSU_HEADER +
        '{"granularity":"word","version":2,"song_end_ms":12000}\n' +
        JSON.stringify({
            text: 'the quick brown fox jumps over the lazy dog', start_ms: 1000, end_ms: 10000,
            words: 'the quick brown fox jumps over the lazy dog'.split(' ').map((w, i) => ({ text: w, start_ms: 1000 + i * 1000, end_ms: 2000 + i * 1000, score: 1 }))
        }) + '\n';

    function run(osuText, keys, until) {
        const engine = new TB.TypingEngine(TB.buildBeatmap(TB.parseLyricOsu(osuText)));
        engine.update(1000);

        for (const [c, t] of keys) {
            engine.update(t);
            engine.processKey(c, t);
        }

        engine.update(until);

        const bar = D.healthBar(engine.health);
        return { health: engine.health, widthPct: bar.widthPct, danger: bar.danger, streak: engine.consecutiveWrongKeys, failed: engine.failed };
    }

    return {
        typos: run(abcdOsu, [['x', 1000], ['x', 1500], [' ', 2000], ['x', 2000], ['x', 2500]], 9000),
        idle: run(idleOsu, [], 20000)
    };
}

process.stdout.write(JSON.stringify(out));
