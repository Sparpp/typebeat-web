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

const abcd = build(abcdOsu);
const gapMap = build(gapOsu);
const line0 = abcd.lines[0];
const points = D.buildSungPoints(line0);

// --- a perfect run and a one-key run, for the live HUD readouts ---
function playPerfect() {
    const map = build(abcdOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    const keys = [['a', 1000], ['b', 1500], [' ', 2000], ['c', 2000], ['d', 2500]];
    for (const [c, t] of keys) { engine.update(t); engine.processKey(c, t); }
    engine.update(5000);
    return engine;
}

function playOneKeyThenSeal() {
    const map = build(abcdOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(5000);
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
    const engine = new TB.TypingEngine(map);
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
    const engine = new TB.TypingEngine(map);
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
    const engine = new TB.TypingEngine(map);
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
    const engine = new TB.TypingEngine(map);
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
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // ordinary cell, dead on target
    engine.update(2000);
    engine.processKey('z', 2000);   // the free slot, also dead on target
    return engine;
}

// Class + tint exactly as paintRow would write them, for every cell of a line.
function paint(engine, lineIndex) {
    return engine.lines[lineIndex].cells.map(c => ({
        cls: D.cellClass(c, false, false),
        fill: D.cellFill(c)
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
