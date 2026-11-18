// Node harness for the HOLD DISPLAY parity pin: typebeat-player.js's port of the desktop's hold
// presentation (LyricLineDisplay's hold-track statics, its SungLocalX playhead geometry, and
// TypingLine's sung polyline across a held cell) held against the GAME's own public statics by
// WireCompat's HoldDisplayParityTest, with no tolerance.
//
// The fixtures and the probes come from the C# side, as in CoreHoldsHarness.cjs: the test writes a
// JSON file of { maps: { name: osuText }, track, paced, pace, heldSung, sungX } and passes its path
// as the second argument. Each map is a timing.json written through the game's own
// LyricOsuFormat.GenerateOsu, and this harness reads that very text through parseLyricOsu and
// buildBeatmap; the test decodes it through the game's LyricBeatmapDecoder. For each line it reports
// the sung position at a set of probe times of its own choosing (every anchor of the polyline, either
// side of each, and a grid across the line) and the polyline's closing time, and the test asks
// TypingLine.SungPositionAt and SweepEndTime the same.
//
// Usage: node PlayerHoldParityHarness.cjs <absolute path to typebeat-core.js> <fixtures json path>

'use strict';

const fs = require('fs');
const nodePath = require('path');

const corePath = process.argv[2];
const fixturesPath = process.argv[3];

if (!corePath || !fixturesPath) {
    process.stderr.write('usage: PlayerHoldParityHarness.cjs <typebeat-core.js> <fixtures.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;
const input = JSON.parse(fs.readFileSync(fixturesPath, 'utf8'));

// The probe times for one line: each anchor, a third of a millisecond and half a millisecond
// either side of it, and a grid from before the line's start to past its last anchor.
function probesFor(points) {
    const times = new Set();
    for (const p of points) {
        for (const d of [0, -0.5, 0.5, -1 / 3, 1 / 3]) times.add(p.t + d);
    }
    const from = points[0].t - 50;
    const to = points[points.length - 1].t + 50;
    for (let t = from; t <= to; t += 37) times.add(t);
    return Array.from(times).sort((a, b) => a - b);
}

const maps = {};
for (const name of Object.keys(input.maps)) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(input.maps[name]));
    maps[name] = {
        lines: beatmap.lines.map(line => {
            const points = D.buildSungPoints(line);
            const times = probesFor(points);
            return {
                held: line.cells.filter(c => D.holdDurationOf(c) > 0).length,
                times: times,
                positions: times.map(t => D.sungPositionAt(points, t)),
                sweepEnd: points[points.length - 1].t
            };
        })
    };
}

const out = {
    constants: D.holdConstants,
    track: input.track.map(p => D.holdTrackWidth(p[0], p[1])),
    paced: input.paced.map(p => D.holdTrackWidth(p[0], p[1], p[2], p[3])),
    pace: input.pace.map(p => D.holdPaceLettersPerSecond(p[0], p[1], p[2], p[3])),
    heldSung: input.heldSung.map(p => D.heldSungOffset(p[0], p[1], p[2])),
    // SungLocalX: each case is one line's edges, advances and tracks, probed at its fractions.
    sungX: input.sungX.map(c => c.at.map(f => D.sungLocalX(f, c.lefts, c.advances, c.tracks))),
    tones: ['Great', 'Ok', 'Meh', 'Miss'].map(D.holdResultTone),
    maps: maps
};

process.stdout.write(JSON.stringify(out));
