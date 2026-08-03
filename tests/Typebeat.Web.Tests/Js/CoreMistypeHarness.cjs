// Node harness that loads the browser scoring core (typebeat-core.js) and exercises WRONG
// KEYPRESSES: the mistype stat added in backlog 72.
//
// This is the scoring-fidelity seam. /play scores land on the SAME leaderboards as desktop ones, so
// the hand-written JS engine has to account a wrong key exactly as the C# TypingEngine +
// TypeBeatScoreProcessor do: reject it, break combo, and report it to the server as the
// combo_break statistics key WITHOUT moving accuracy, completion or rank. The C# side
// (MistypeParityTest) runs the emitted dictionaries through the server's own ScoringContract and
// PerformancePoints, so nothing is hardcoded twice: the JS produces a submission and the real
// server code judges it.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE invoked
// with the bare `window` identifier; provide a global `window` before loading so it resolves.
//
// Usage: node CoreMistypeHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// One line, "the bad cat sat" over four words: 15 cells, no 'z' anywhere, so 'z' is reliably the
// wrong key on every one of them.
const OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"the bad cat sat","start_ms":1000,"end_ms":9000,"words":[' +
    '{"text":"the","start_ms":1000,"end_ms":3000,"score":1},' +
    '{"text":"bad","start_ms":3000,"end_ms":5000,"score":1},' +
    '{"text":"cat","start_ms":5000,"end_ms":7000,"score":1},' +
    '{"text":"sat","start_ms":7000,"end_ms":9000,"score":1}]}\n';

const WRONG_KEY = 'z';

/**
 * Plays the map on target, pressing `wrongBefore` wrong keys immediately before each of the first
 * `mistypedCells` cells, and stopping `skipTrailing` cells short of the end (those seal as misses).
 *
 * The wrong keys are spread one cell apart on purpose: 13 CONSECUTIVE wrong keys fail the run, and
 * this harness is about a play that survives to submit.
 */
function play(options) {
    const wrongBefore = options.wrongBefore || 0;
    const mistypedCells = options.mistypedCells || 0;
    const skipTrailing = options.skipTrailing || 0;

    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(OSU), false);
    const engine = new TB.TypingEngine(beatmap);

    const cells = [];
    for (const line of beatmap.lines) for (const cell of line.cells) cells.push({ line, cell });

    const last = cells.length - skipTrailing;
    let typedCells = 0;

    for (const line of beatmap.lines) {
        engine.update(line.activationTime);

        for (const cell of line.cells) {
            if (typedCells >= last) break;

            engine.update(cell.target);

            if (typedCells < mistypedCells) {
                for (let i = 0; i < wrongBefore; i++)
                    engine.processKey(WRONG_KEY, cell.target);
            }

            engine.processKey(cell.expected, cell.target);
            typedCells++;
        }
    }

    engine.update(1000000);
    const score = TB.computeScore(engine);

    return {
        totalCells: cells.length,
        engineMistypes: engine.mistypes,
        consecutiveWrongKeys: engine.consecutiveWrongKeys,
        failed: engine.failed,
        passed: score.passed,
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

const out = {
    // The reference: no wrong keys at all. Its statistics must carry NO combo_break key, exactly
    // like a score submitted by a client from before the stat existed.
    clean: play({}),

    // Every cell typed, but seven wrong keys along the way. Rank must still be X and the accuracy
    // must be identical to the clean run: mistyping costs combo and pp, never the grade.
    mistyped: play({ wrongBefore: 1, mistypedCells: 7 }),

    // Two wrong keys before each of the first seven cells, to pin that the count is per KEYPRESS
    // and not per cell.
    doubleMistyped: play({ wrongBefore: 2, mistypedCells: 7 }),

    // Mistypes AND real misses together: the two are independent stats and neither may absorb the
    // other.
    mistypedAndMissed: play({ wrongBefore: 1, mistypedCells: 7, skipTrailing: 3 })
};

process.stdout.write(JSON.stringify(out));
