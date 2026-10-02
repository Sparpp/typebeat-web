// Node harness for the underline PACE HUE (backlog 317), the /play port of the desktop's
// UI/UnderlinePace.cs. Like CoreSplitCharsHarness it carries NO fixtures of its own: the .osu texts
// come from the C# side (UnderlinePaceParityTest writes them to a temp file whose path is argv[3]),
// so the bytes the browser reads here are the bytes the game's decoder read to build its bands.
//
// For each map it builds the browser's beatmap exactly as /play does (Core.buildBeatmap over
// Core.parseLyricOsu, the default stream), runs the player's own precompute (display.buildPaceBands,
// the RELATIVE mode /play draws since PR 3, which closes each line on buildSungPoints' last anchor
// as mountPlayer does), the whole-map RANKED mode beside it (display.buildRankedPaceBands) and the
// MAP-RELATIVE mode /play draws since PR 5 when the map's average WPM is served
// (display.buildMapRelativePaceBands, against the average the C# side hands over as `avgWpm`), and
// reports every band's cell range and colour under all three. The previous-speed and map-average
// ramps are probed on their own too (display.paceColourForPreviousSpeed,
// display.paceColourForMapAverage).
//
// Usage: node PlayerPaceBandsHarness.cjs <path to typebeat-core.js> <path to the maps JSON>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
const mapsPath = process.argv[3];

if (!corePath || !mapsPath) {
    process.stderr.write('usage: PlayerPaceBandsHarness.cjs <typebeat-core.js> <maps.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;

const input = JSON.parse(require('fs').readFileSync(mapsPath, 'utf8'));

const maps = input.maps.map(function (one) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(one.osu));
    const points = beatmap.lines.map(D.buildSungPoints);
    const bands = D.buildPaceBands(beatmap.lines, points);
    const ranked = D.buildRankedPaceBands(beatmap.lines, points);
    const mapRelative = D.buildMapRelativePaceBands(beatmap.lines, points, one.avgWpm);
    const sungEnds = points.map(p => p[p.length - 1].t);
    const report = lineBands => lineBands.map(b => ({
        startCell: b.startCell,
        endCellExclusive: b.endCellExclusive,
        r: b.colour.r,
        g: b.colour.g,
        b: b.colour.b,
        a: b.colour.a
    }));

    return {
        name: one.name,
        cellCounts: beatmap.lines.map(line => line.cells.length),
        // Where each line's last band closes (UnderlinePace.SungEndOf), and every segment's speed
        // before ranking: the two inputs a rank drift would come from, reported so a failure names
        // its cause rather than only its colour.
        sungEnds: sungEnds,
        speeds: beatmap.lines.map((line, k) => D.paceSegmentLine(line.cells, sungEnds[k], line.syllableMarkerCells).map(s => s.speed)),
        markers: beatmap.lines.map(line => (line.syllableMarkerCells || []).length),
        lines: bands.map(report),
        ranked: ranked.map(report),
        mapRelative: mapRelative.map(report)
    };
});

// ColourForRank on its own, over a sweep of ranks the maps may never land on (both endpoints, both
// buffer edges, NaN and out-of-range values), so the ramp is pinned end to end and not only where
// the fixtures happen to rank.
const rankProbes = input.ranks.map(function (r) {
    const c = D.paceColourForRank(r === null ? NaN : r);
    return { r: c.r, g: c.g, b: c.b, a: c.a };
});

// ColourForPreviousSpeed over [speed, previous, maxChange] triples, null previous meaning "no band
// before", and null maxChange the default.
const previousProbes = input.previous.map(function (p) {
    const c = D.paceColourForPreviousSpeed(p[0], p[1], p[2] === null ? undefined : p[2]);
    return { r: c.r, g: c.g, b: c.b, a: c.a };
});

// ColourForMapAverage over [speed, average, maxChange] triples, null maxChange the default.
const mapAverageProbes = (input.mapAverage || []).map(function (p) {
    const c = D.paceColourForMapAverage(p[0], p[1], p[2] === null ? undefined : p[2]);
    return { r: c.r, g: c.g, b: c.b, a: c.a };
});

process.stdout.write(JSON.stringify({ maps: maps, rankProbes: rankProbes, previousProbes: previousProbes, mapAverageProbes: mapAverageProbes }));
