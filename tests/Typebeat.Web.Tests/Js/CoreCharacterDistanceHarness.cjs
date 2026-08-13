// Node harness that loads the browser scoring core (typebeat-core.js) and exercises backlog 133's
// CHARACTER-DISTANCE judgement: a keypress is graded on how many CHARACTERS it is from the
// character the playhead is on, in four quality tiers, rather than on milliseconds off its cell's
// target.
//
// This is the scoring-fidelity seam, and the fixtures below are transcribed from the game's own
// suite (TypingEngineTest.CharacterDistanceInterpolatesBetweenTargetsAndExtrapolatesPastTheEnds,
// .WindowsAreCharacterDistancesAndTheMillisecondLadderStillExists and
// .MashingAWholeLineAheadWalksDownEveryTierAndThenEarnsNothing) so the two implementations are held
// against the SAME numbers rather than against each other's opinion. The C# side
// (CharacterDistanceParityTest) asserts them and then runs the emitted dictionaries through the
// server's own ScoringContract, which is the code that will judge them in production.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE invoked
// with the bare `window` identifier; provide a global `window` before loading so it resolves.
//
// Usage: node CoreCharacterDistanceHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

const HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

function build(osu) { return TB.buildBeatmap(TB.parseLyricOsu(osu)); }

// The desktop suite's workhorse line, in the .osu form /play consumes: "ab cd", sung [1000, 3000],
// words "ab" [1000, 2000] and "cd" [2000, 3000]. Cell targets a = 1000, b = 1500, ' ' = 2000,
// c = 2000, d = 2500, so the character axis is [1000, 1500, 2000, 2000, 2500] and the line's mean
// typeable spacing is (2500 - 1000) / 4 = 375 ms.
const abcdOsu = HEADER +
    '{"granularity":"line","version":2,"song_end_ms":4000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n';

// A line whose data carries NO spacing at all: one typeable cell sung over [1000, 1600], so the
// fallback spacing is that 600 ms span over its 1 cell.
const singleOsu = HEADER +
    '{"granularity":"line","version":2,"song_end_ms":3000}\n' +
    '{"text":"a","start_ms":1000,"end_ms":1600,"words":[{"text":"a","start_ms":1000,"end_ms":1600,"score":1}]}\n';

// Word granularity, "ab" over [1000, 2000]: targets 1000 and 1500, mean spacing 500 ms.
const wordOsu = HEADER +
    '{"granularity":"word","version":2,"song_end_ms":3000}\n' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n';

// 24 x one letter over one unit [1000, 49000], so k = 24 and the targets step 2000 ms apart. The
// game's fixture was 14 characters until backlog 146 doubled the ladder's early reach from 10 to 20;
// it is 24 for the same reason it was 14, namely one more than the ladder can reach, so the run off
// the end of it is still covered.
const mashText = 'a'.repeat(24);
const mashOsu = HEADER +
    '{"granularity":"line","version":2,"song_end_ms":60000}\n' +
    '{"text":"' + mashText + '","start_ms":1000,"end_ms":49000,"words":[' +
    '{"text":"' + mashText + '","start_ms":1000,"end_ms":49000,"score":1}]}\n';

const abcd = build(abcdOsu);
const abcdLine = abcd.lines[0];
const singleLine = build(singleOsu).lines[0];

const dist = (line, time, cell) => TB.characterDistanceAt(line, time, cell);

// --- window ladders -----------------------------------------------------------------------
const characterLine = TB.windowsFor('Line');
const characterWord = TB.windowsFor('Word');
const characterSyllable = TB.windowsFor('Syllable');
const millisecondLine = TB.windowsFor('Line', TB.constants.MEASURE_MILLISECONDS);

function ladder(w) { return [w.pe, w.pl, w.ge, w.gl, w.oe, w.ol, w.me, w.ml]; }

// --- plays --------------------------------------------------------------------------------

// Every cell typed dead on its own target. Under a character measure that is 0 characters out for
// every one of them INCLUDING the two that share the 2000 ms word boundary, which is the whole
// point of the playhead being a span: without it a rhythm-perfect play would be charged a whole
// character on the boundary and score 98.75% sync instead of 100%.
function playOnTarget() {
    const map = build(abcdOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    for (const cell of map.lines[0].cells) {
        engine.update(cell.target);
        engine.processKey(cell.expected, cell.target);
    }
    engine.update(100000);
    return engine;
}

// The whole-line mash: 24 presses at t = 1000 on a line paced 2000 ms per character. Under the OLD
// millisecond measure every press but the first was thousands of ms early, i.e. Premature. Under a
// character measure the k'th press is exactly k characters ahead of the playhead whatever the
// tempo, so the ladder is walked down one rung at a time, which is the point: how far ahead a
// player may be is capped in CHARACTERS, so a slow line is not a harder line.
function playWholeLineMash() {
    const map = build(mashOsu);
    const engine = new TB.TypingEngine(map);
    let breaks = 0;
    engine.onComboBroken = () => breaks++;
    engine.update(1000);
    for (let i = 0; i < 24; i++) engine.processKey('a', 1000);
    return { engine: engine, breaks: breaks, types: map.lines[0].cells.map(c => c.judgeType) };
}

// A Word-granularity press one notch outside the scaled Perfect window: 'a' at 2201 is
// 1 + (2201 - 1500) / 500 = 2.402 characters behind the playhead, against a scaled PerfectLate of
// 4.00 * 0.6 = 2.40. The judgement event still carries the press in MILLISECONDS (1201), which is
// the split the measure introduced.
function playWordJustOutsidePerfect() {
    const map = build(wordOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    engine.processKey('a', 2201);
    const cell = map.lines[0].cells[0];
    return { judgeType: cell.judgeType, judgedDelta: cell.judgedDelta, judgedOffset: cell.judgedOffset };
}

// Backspace and retype a cell that was already judged correct: the replayed judgement must come
// from the banked OFFSET, not from a fresh distance at the retype's time, or the inert retype
// would silently re-grade the cell.
function playInertRetype() {
    const map = build(abcdOsu);
    const engine = new TB.TypingEngine(map);
    engine.update(1000);
    engine.processKey('a', 1000);
    const first = map.lines[0].cells[0].judgeType;
    for (let i = 0; i < 20; i++) {
        engine.processBackspace();
        engine.processKey('a', 6000 + i);
    }
    const cell = map.lines[0].cells[0];
    return {
        firstJudgeType: first,
        judgeType: cell.judgeType,
        judgedOffset: cell.judgedOffset,
        judgedDelta: cell.judgedDelta,
        score: engine.score,
        maxCombo: engine.maxCombo
    };
}

function submission(engine) {
    const score = TB.computeScore(engine);
    return {
        statistics: score.statistics,
        maximumStatistics: score.maximumStatistics,
        counts: score.counts,
        maxCombo: score.maxCombo,
        totalScore: score.totalScore,
        accuracy: score.accuracy,
        completion: score.completion,
        rank: score.rank
    };
}

const onTarget = playOnTarget();
const mash = playWholeLineMash();

const out = {
    // --- the axis itself ---------------------------------------------------------------------
    cellTargets: abcdLine.cells.map(c => c.target),
    cellPositions: abcdLine.axis.cellPositions,
    extrapolationSpacing: abcdLine.axis.extrapolationSpacingMs,
    singleExtrapolationSpacing: singleLine.axis.extrapolationSpacingMs,
    fallbackSpacing: TB.constants.FALLBACK_CHAR_SPACING_MS,

    // Dead on your own target is 0 characters out, always. That is the contract the whole measure
    // rests on.
    distanceOnOwnTarget: abcdLine.cells.map((c, i) => dist(abcdLine, c.target, i)),

    // INSIDE: exact linear interpolation between the bracketing targets, so where spacing is
    // locally uniform the distance is just the millisecond delta over that spacing.
    distanceHalfwayAToB: dist(abcdLine, 1250, 0),
    distanceHalfwayAToBFromB: dist(abcdLine, 1250, 1),
    distanceThreeFifthsBToSpace: dist(abcdLine, 1800, 1),

    // TIED targets: ' ' (cell 2) and 'c' (cell 3) both sit at 2000, which is what a word boundary
    // between contiguous words always looks like. A press at 2000 is 0 characters out for BOTH.
    distanceTiedSpace: dist(abcdLine, 2000, 2),
    distanceTiedC: dist(abcdLine, 2000, 3),
    distanceTiedFromAfter: dist(abcdLine, 2000, 4),
    distanceTiedFromBefore: dist(abcdLine, 2000, 0),

    // OUTSIDE: extrapolated at the line's mean spacing, NOT clamped. Clamping would make any press
    // before the line's first target a distance of exactly 0, i.e. a Perfect however early.
    distanceTwoSpacingsEarly: dist(abcdLine, 1000 - 2 * 375, 0),
    distanceTwoSpacingsLate: dist(abcdLine, 2500 + 2 * 375, 4),

    // Pressing a cell at a completely different cell's target: the plain index difference.
    distanceAcrossCells: dist(abcdLine, 1000, 3),

    // The no-spacing fallback chain: 600 ms of sung span over 1 cell, so 600 ms either side is
    // exactly one character out.
    distanceSingleEarly: dist(singleLine, 400, 0),
    distanceSingleLate: dist(singleLine, 1600, 0),

    // --- the two window ladders ---------------------------------------------------------------
    characterLine: ladder(characterLine),
    characterWord: ladder(characterWord),
    characterSyllable: ladder(characterSyllable),
    millisecondLine: ladder(millisecondLine),

    // Classification at and just past every edge of the live (character) ladder.
    characterClassify: [0, -2.5, -2.51, 4, 4.01, -5, -5.01, 8, 8.01, -10, -10.01, 16, 16.01, -20, -20.01, 32, 32.01]
        .map(o => TB.classify(o, characterLine)),

    // ...and of the millisecond ladder, whose Great/Ok/Meh rows are EXACTLY the windows this game
    // judged in before backlog 133 (then called Perfect/Good/Ok). Backlog 146 widened the CHARACTER
    // probes above and deliberately left these alone: the millisecond ladder is frozen.
    millisecondClassify: [200, 201, -250, 400, 401, 1000, 1001, 2000, 2001, -1201]
        .map(o => TB.classify(o, millisecondLine)),

    basePoints: ['Perfect', 'Great', 'Ok', 'Meh', 'Premature', 'Lagging', 'Miss', 'WrongChar'].map(TB.basePoints),
    hitResults: ['Perfect', 'Great', 'Ok', 'Meh', 'Premature', 'Lagging', 'Miss'].map(TB.toHitResult),

    // Sync quality is measured over the WIDEST scoring window, so it is 1 dead on the playhead and
    // exactly 0 at the Meh edges.
    syncOnPlayhead: TB.syncQuality(0, characterLine),
    syncHalfLate: TB.syncQuality(characterLine.ml / 2, characterLine),
    syncAtMehLate: TB.syncQuality(characterLine.ml, characterLine),
    syncAtMehEarly: TB.syncQuality(-characterLine.me, characterLine),
    syncPastMeh: TB.syncQuality(characterLine.ml * 3, characterLine),

    // --- plays --------------------------------------------------------------------------------
    onTarget: submission(onTarget),
    onTargetTypes: onTarget.lines[0].cells.map(c => c.judgeType),
    onTargetSync: onTarget.lines[0].cells.map(c => c.judgedSyncQuality),

    // The mash is also the only fixture that carries ALL FOUR quality keys in one dictionary, so it
    // is what the ScoringContract half of the guard recomputes.
    mash: submission(mash.engine),
    mashTypes: mash.types,
    mashScore: mash.engine.score,
    mashMaxCombo: mash.engine.maxCombo,
    mashComboBreaks: mash.breaks,

    wordJustOutsidePerfect: playWordJustOutsidePerfect(),
    inertRetype: playInertRetype()
};

process.stdout.write(JSON.stringify(out));
