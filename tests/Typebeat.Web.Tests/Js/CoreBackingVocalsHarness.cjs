// Node harness for the BACKING VOCALS in the browser (backlog 445): what buildBeatmap reads and
// fits from a stored .osu's "backing" arrays, and the player's pure display math for them (where a
// word rests across its line, the tilt it settles at, and its pop-up pose over time).
//
//   node CoreBackingVocalsHarness.cjs <typebeat-core.js> <cases.json>
//
// cases.json is written by the C# side (BackingVocalParityTest in Typebeat.WireCompat, which holds
// every answer here against the game's own BackingVocals / LyricLineDisplay):
//   { osu: [whole .osu texts], tilts: [{ text, start, index }], elapsed: [ms], anchors: [{ anchors: [number|null], index }] }

'use strict';

const nodePath = require('path');
const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath || !casesPath) {
    process.stderr.write('usage: CoreBackingVocalsHarness.cjs <typebeat-core.js> <cases.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));
const TB = global.window.TypeBeatCore;
const D = TB.display;

const cases = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));

process.stdout.write(JSON.stringify({
    lines: cases.osu.map(osu => TB.buildBeatmap(TB.parseLyricOsu(osu), false).lines.map(l => l.backing.map(w => ({
        text: w.text, start: w.start, end: w.end, anchor: w.anchor
    })))),
    tilts: cases.tilts.map(t => D.backingTiltOf({ text: t.text, start: t.start }, t.index)),
    poses: cases.elapsed.map(e => D.backingPoseAt(e)),
    anchors: cases.anchors.map(a => D.backingAnchorOf(a.anchors.map(x => ({ anchor: x })), a.index))
}));
