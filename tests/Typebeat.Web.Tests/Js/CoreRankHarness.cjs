// Node harness that loads the browser scoring core (typebeat-core.js) and emits its GRADE for a
// table of (accuracy, missedFraction) pairs, plus a couple of (accuracy, counts) pairs through
// rankFromStatistics.
//
// This is a scoring-fidelity seam. PR 17 (grades from accuracy + missed-cell limits) changed the
// grade rule in three places that must agree: the game's TypeBeatScoreProcessor, the server's
// ScoringContract, and the hand-written JS engine. The C# side (RankParityTest here, and the
// WireCompat rank test) holds this output against the server's own ScoringContract and the game's
// own RankFromAccuracy, so a one-sided edit fails there rather than in production.
//
// typebeat-core.js attaches window.TypeBeatCore via an IIFE invoked with the bare `window`
// identifier; provide a global `window` before loading so it resolves.
//
// Usage: node CoreRankHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// Boundary and interior points for (accuracy, missedFraction). The missedFraction values straddle
// S_MISS_LIMIT (0.03) and zero, which is where the SS/S conditions bite.
const PAIRS = [
    [1.00, 0.00], [0.98, 0.00], [0.979999, 0.00],
    [1.00, 0.001], [0.92, 0.029999], [0.92, 0.03], [0.99, 0.03],
    [0.919999, 0.00], [0.85, 0.15], [0.849999, 0.00],
    [0.75, 0.25], [0.749999, 0.00], [0.60, 0.40], [0.599999, 0.00],
    [0.00, 1.00], [1.00, 0.50], [0.95, 0.10],
];

// (accuracy, counts) for rankFromStatistics: notes = great+ok+meh, missedFraction = miss/notes.
const STATS = [
    { accuracy: 1.00, counts: { great: 100, ok: 0, meh: 0, miss: 0 } },       // X
    { accuracy: 0.99, counts: { great: 99, ok: 0, meh: 0, miss: 1 } },        // 1/100 missed -> S
    { accuracy: 0.99, counts: { great: 96, ok: 0, meh: 0, miss: 4 } },        // 4/100 missed -> A
    { accuracy: 0.80, counts: { great: 80, ok: 0, meh: 0, miss: 0 } },        // B
    { accuracy: 0.50, counts: { great: 50, ok: 0, meh: 0, miss: 0 } },        // D
    { accuracy: 1.00, counts: { great: 99, ok: 0, meh: 0, good: 1, miss: 0 } }, // unfixed typo counts as a missed cell -> not X
    { accuracy: 0.99, counts: { great: 98, ok: 0, meh: 0, good: 2, miss: 0 } }, // 2/100 missed -> S
];

const pairs = PAIRS.map(([accuracy, missed]) => ({ accuracy, missed, rank: TB.rankFromAccuracy(accuracy, missed) }));
const stats = STATS.map(s => ({ accuracy: s.accuracy, counts: s.counts, rank: TB.rankFromStatistics(s.accuracy, s.counts) }));

process.stdout.write(JSON.stringify({ pairs, stats }));
