// Node harness that loads the browser scoring core (typebeat-core.js) and emits the
// syllable-aware per-char target times as JSON on stdout, so the C# fidelity test
// (SyllableTimingParityTest) can assert them against the game's golden values.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an
// IIFE invoked with the bare `window` identifier; provide a global `window` before loading
// so it resolves, then read the export back off it.
//
// Usage: node CoreSyllableHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

const f = TB.syllableCharTarget;

// buildBeatmap end-to-end: prove the words[].syllables[] JSON field decodes into boundaries
// and warps the per-char targets exactly like the direct helper. `osu` is the type!beat file
// format the browser /play path actually consumes (parseLyricOsu -> buildBeatmap).
function cellTargets(osuText) {
    const parsed = TB.parseLyricOsu(osuText);
    const beatmap = TB.buildBeatmap(parsed);
    return beatmap.lines[0].cells.map(c => c.target);
}

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

// "abcd" over [1000,2000], one syllable boundary at 1200 (first syllable starts at the word
// start and contributes no boundary).
const dividedOsu = OSU_HEADER +
    '{"granularity":"syllable","version":2,"song_end_ms":100000}\n' +
    '{"text":"abcd","start_ms":1000,"end_ms":2000,"words":[{"text":"abcd","start_ms":1000,"end_ms":2000,"score":1,"syllables":[{"text":"ab","start_ms":1000,"end_ms":1200},{"text":"cd","start_ms":1200,"end_ms":2000}]}]}\n';

// Same word, no syllables[] at all -> flat ramp (undivided maps must stay byte-identical).
const flatOsu = OSU_HEADER +
    '{"granularity":"syllable","version":2,"song_end_ms":100000}\n' +
    '{"text":"abcd","start_ms":1000,"end_ms":2000,"words":[{"text":"abcd","start_ms":1000,"end_ms":2000,"score":1}]}\n';

const out = {
    // Direct helper (mirrors TypingLine.syllableCharTarget unit tests).
    divided: [0, 1, 2, 3].map(j => f(1000, 2000, [1200], 4, j)),
    flat: [0, 1, 2, 3].map(j => f(1000, 2000, [], 4, j)),
    multi: [0, 1, 2, 3, 4, 5].map(j => f(0, 1200, [300, 900], 6, j)),
    // End-to-end through parseLyricOsu -> buildBeatmap (proves the JSON field flows through).
    dividedCells: cellTargets(dividedOsu),
    flatCells: cellTargets(flatOsu)
};

process.stdout.write(JSON.stringify(out));
