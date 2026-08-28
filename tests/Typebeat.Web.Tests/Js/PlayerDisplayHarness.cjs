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

const TB = global.window.TypeBeatCore;
const D = TB.display;

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
const gapMap = build(gapOsu);
const line0 = abcd.lines[0];
const points = D.buildSungPoints(line0);

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

// A late press, to prove sync is a real timing measure and not just a hit count.
//
// Every press time here is measured from the SYLLABLE SPAN the cell belongs to, not from the cell's
// own target (backlog 179): 'a' and 'b' are the two characters of the word "ab", which is sung over
// [1000, 2000], so a press anywhere in that window is delta 0 and a press at 2600 is +600 past the
// span's late edge. Tier Word puts the Meh-late window at 2000 * 0.6 = 1200, so that is exactly
// half quality, which is the number this scenario exists to produce.
function playOneLatePress() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // inside the "ab" span, q = 1
    engine.update(2600);
    engine.processKey('b', 2600);   // +600 past the span's end, q = 0.5
    return engine;
}

// Backlog 148: the word gap is UNTIMED, so it is out of BOTH halves of the sync mean and its own
// timing cannot move the readout. Every LYRIC character is pressed 200 ms late here (tier Word, so
// the Meh-late window is 2000 * 0.6 = 1200 and q = 1 - 200/1200 = 5/6 apiece); only the space moves
// between the two runs, and the two syncs must come out identical. Counted IN at its zeroed delta
// the loose run would read 100 * (4*5/6 + 1) / 5, i.e. a free lift toward the grade thresholds.
//
// "200 ms late" is 200 ms past the SYLLABLE SPAN each character belongs to (backlog 179), which is
// why both letters of a word share a press time: "ab" is sung over [1000, 2000] and "cd" over
// [2000, 3000], so 2200 is +200 for BOTH of a and b, and 3200 is +200 for both of c and d.
function playWithSpaceAt(spaceTime) {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 2200);
    engine.processKey('b', 2200);
    engine.processKey(' ', spaceTime);
    engine.processKey('c', 3200);
    engine.processKey('d', 3200);
    return engine;
}

// --- the sync tint (LyricLineDisplay.CorrectCharColour, re-expressed in the site's tokens) ---
// A run that lands one cell of each kind the ramp has to tell apart: dead on target (full hit
// colour), half quality (mid ramp), and a press so late it is Lagging, which is still a CORRECT
// cell but must keep .tb-c-off's flat warn tint instead of joining the ramp.
function playMixedTiming() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // inside the "ab" span [1000, 2000], delta 0     -> q 1
    engine.update(2600);
    engine.processKey('b', 2600);   // +600 past that span's end, delta +600  -> q 0.5 (Ok-late window 1200)
    engine.processKey(' ', 3100);   // the word gap: untimed, judged on a zeroed delta whenever it lands
    engine.processKey('c', 3100);   // +100 past the "cd" span [2000, 3000], delta +100
    engine.update(3800);
    engine.processKey('d', 4300);   // +1300 past that span's end -> past the Ok edge, Lagging
    return engine;
}

// Backspace over a correct cell: the engine clears its judged delta, so the tint must come off
// with it and the glyph go back to untyped rather than keeping the brightness it had earned.
function playThenBackspace() {
    const map = build(abcdOsu);
    const engine = engineFor(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.processBackspace();
    return engine;
}

// A freestyle cell ('&' authors a slot any key fills), typed dead on target so the ONLY reason it
// could miss the ramp is the deliberate exclusion.
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

// Class, tint and GLYPH exactly as paintRow would write them, for every cell of a line.
function paint(engine, lineIndex) {
    return engine.lines[lineIndex].cells.map(c => ({
        cls: D.cellClass(c, false, false),
        fill: D.cellFill(c),
        glyph: D.cellGlyph(c)
    }));
}

// --- rolling WPM ring ---
function ring(pushes) {
    const r = D.makeRollingWpm(D.constants.ROLLING_WPM_WINDOW);
    for (let i = 0; i < pushes; i++) r.push(i * 100);
    return r.value(-1);
}

const perfect = playPerfect();
const partial = playOneKeyThenSeal();
const late = playOneLatePress();

const wordWindows = TB.windowsFor('Word');

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

    // Sync quality at the window edges.
    syncOnTarget: D.syncQuality(0, wordWindows),
    syncHalfLate: D.syncQuality(wordWindows.ml / 2, wordWindows),
    syncAtLateEdge: D.syncQuality(wordWindows.ml, wordWindows),
    syncPastLateEdge: D.syncQuality(wordWindows.ml * 2, wordWindows),
    syncAtEarlyEdge: D.syncQuality(-wordWindows.me, wordWindows),

    // Sync tint: the ramp itself, then the classes/fills paintRow would write on real runs.
    syncTintFloor: D.constants.SYNC_TINT_FLOOR,
    rampAt: [0, 0.25, 0.5, 0.75, 1].map(D.syncTintFill),
    // Same, as bare numbers, so the C# side can assert the ramp never goes backwards.
    rampCurve: Array.from({ length: 21 }, (_, i) => parseFloat(D.syncTintFill(i / 20))),
    rampClamped: [-1, 2, NaN].map(D.syncTintFill),

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
    looseSpaceStats: D.liveStats(playWithSpaceAt(7000)),
    tightSpaceStats: D.liveStats(playWithSpaceAt(2000)),

    // The display layer must not have moved the score: the same run through the untouched
    // scorer still reads a clean X.
    perfectScore: (() => { const s = TB.computeScore(perfect); return { rank: s.rank, completion: s.completion, totalScore: s.totalScore }; })(),

    // Easing used by the line-change scroll.
    outQuint: [0, 0.5, 1].map(D.outQuint)
};

process.stdout.write(JSON.stringify(out));
