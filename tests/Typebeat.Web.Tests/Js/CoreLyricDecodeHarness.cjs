// Node harness for the STORED MAP'S DECODE in the browser: what parseLyricOsu + buildBeatmap make of
// a whole .osu, line by line and cell by cell, on both branches of the cell flattening.
//
//   node CoreLyricDecodeHarness.cjs <typebeat-core.js> <cases.json>
//
// cases.json is an array of whole .osu texts written by the C# side (LyricParserParityTest in
// Typebeat.WireCompat, which holds the game's production decoder against this file for file). It
// exists for the lyric TEXT normalization (backlog 329's special letters): an import stores the
// aligner's raw line and every client normalizes it on decode, so the cells a /play run types have
// to be the cells desktop types.

'use strict';

const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath || !casesPath) {
    process.stderr.write('usage: CoreLyricDecodeHarness.cjs <typebeat-core.js> <cases.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

const cases = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));

function decode(osu, literate) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(osu), literate);
    return beatmap.lines.map(l => ({
        text: l.text,
        stream: l.cells.map(c => c.expected).join(''),
        targets: l.cells.map(c => c.target)
    }));
}

process.stdout.write(JSON.stringify(cases.map(osu => ({
    plain: decode(osu, false),
    literate: decode(osu, true)
}))));
