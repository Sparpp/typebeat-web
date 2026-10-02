// Node harness for the map's FREESTYLE colour in the browser (backlog 384): what parseLyricOsu +
// buildBeatmap read off a whole stored .osu, and the colour the player then paints .tb-c-free with.
//
//   node FreestyleColourHarness.cjs <typebeat-core.js> <cases.json>
//
// cases.json is an array of whole .osu texts written by the C# side (FreestyleColourParityTest in
// Typebeat.WireCompat, which holds the game's own writer and decoder against this file).

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath || !casesPath) {
    process.stderr.write('usage: FreestyleColourHarness.cjs <typebeat-core.js> <cases.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));
const TB = global.window.TypeBeatCore;

const cases = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));

process.stdout.write(JSON.stringify({
    defaultColour: TB.DEFAULT_FREESTYLE_COLOUR,
    cases: cases.map(osu => {
        const beatmap = TB.buildBeatmap(TB.parseLyricOsu(osu));
        return {
            decoded: beatmap.freestyleColour,
            painted: TB.display.freestyleColourOf(beatmap),
            lines: beatmap.lines.length
        };
    })
}));
