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

// A late press, to prove sync is a real timing measure and not just a hit count. 'b' targets
// 1500 with tier Word (Ok-late window 2000 * 0.6 = 1200), so +600ms is exactly half quality.
function playOneLatePress() {
    const map = build(abcdOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    engine.processKey('a', 1000);   // on target, q = 1
    engine.update(2100);
    engine.processKey('b', 2100);   // delta +600, q = 0.5
    return engine;
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
    syncHalfLate: D.syncQuality(wordWindows.ol / 2, wordWindows),
    syncAtLateEdge: D.syncQuality(wordWindows.ol, wordWindows),
    syncPastLateEdge: D.syncQuality(wordWindows.ol * 2, wordWindows),
    syncAtEarlyEdge: D.syncQuality(-wordWindows.oe, wordWindows),

    // Live HUD readouts off real engine runs.
    perfectStats: D.liveStats(perfect),
    partialStats: D.liveStats(partial),
    lateStats: D.liveStats(late),

    // The display layer must not have moved the score: the same run through the untouched
    // scorer still reads a clean X.
    perfectScore: (() => { const s = TB.computeScore(perfect); return { rank: s.rank, completion: s.completion, totalScore: s.totalScore }; })(),

    // Easing used by the line-change scroll.
    outQuint: [0, 0.5, 1].map(D.outQuint)
};

process.stdout.write(JSON.stringify(out));
