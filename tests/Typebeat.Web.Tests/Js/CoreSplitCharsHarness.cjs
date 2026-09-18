// Node harness for the AUTHORED syllable split (backlog 181), the word-level `split_chars` array
// that lets a mapper cut "beauti|ful" where the syllabifier would have cut "beau|tiful". The split
// drives BOTH the per-char target times and the judgement groups, so a browser that read it
// differently from the desktop client would judge the same performance differently on the shared
// leaderboards.
//
// UNLIKE every other harness here, this one carries NO fixtures of its own. The .osu texts come
// from the C# side (SyllableSplitParityTest writes them to a temp file whose path is argv[3]), most
// of them produced by the GAME'S OWN TypeBeatBeatmapEncoder, so the bytes the browser parses are
// the bytes the editor writes and neither side can drift onto a hand-copied fixture. This harness
// only reads them back and reports what the browser made of them.
//
// Each case carries TWO variants of the same map, `a` (as authored) and `b` (the comparison, in
// most cases the identical map with split_chars taken away). The harness reports both readings and
// counts how many cells MOVED between them, which is the coverage counter: an authored case whose
// count fell to zero would be passing for free, and the C# side asserts the two arms agree on the
// count as well as on the readings.
//
// Usage: node CoreSplitCharsHarness.cjs <path to typebeat-core.js> <path to the cases JSON>

'use strict';

const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath || !casesPath) {
    process.stderr.write('usage: CoreSplitCharsHarness.cjs <typebeat-core.js> <cases.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

const input = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));

/** Everything the two loaders have to agree on for one map: the cells, the groups, the membership. */
function read(osuText, literate) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(osuText), literate === true);

    return {
        granularity: beatmap.granularity,
        lines: beatmap.lines.map(line => ({
            endTime: line.endTime,
            activationTime: line.activationTime,
            sealGraceMs: line.sealGraceMs,
            cells: line.cells.map(c => ({ expected: c.expected, target: c.target })),
            syllables: line.syllables.map(g => ({
                startCell: g.startCell, endCellExclusive: g.endCellExclusive, startTime: g.startTime, endTime: g.endTime
            })),
            cellSyllable: line.cellSyllable.slice()
        }))
    };
}

/**
 * Cells whose target time, and cells whose syllable membership, differ between the two variants.
 * A shape mismatch (different line or cell counts) is reported as -1 rather than counted, because
 * the two variants of a case are the same map and differing there is a bug, not coverage.
 */
function movement(a, b) {
    if (a.lines.length !== b.lines.length) return { targets: -1, membership: -1 };

    let targets = 0;
    let membership = 0;

    for (let i = 0; i < a.lines.length; i++) {
        const la = a.lines[i];
        const lb = b.lines[i];

        if (la.cells.length !== lb.cells.length) return { targets: -1, membership: -1 };

        for (let c = 0; c < la.cells.length; c++) {
            if (la.cells[c].target !== lb.cells[c].target) targets++;
            if (la.cellSyllable[c] !== lb.cellSyllable[c]) membership++;
        }
    }

    return { targets: targets, membership: membership };
}

const cases = input.cases.map(one => {
    const a = read(one.a, one.literate);
    const b = read(one.b, one.literate);
    const moved = movement(a, b);

    return {
        name: one.name,
        a: a,
        b: b,
        movedTargets: moved.targets,
        movedMembership: moved.membership
    };
});

// The SyllableSegments derivation itself, held against the game's own for the words the cases use.
// The readings above prove the whole pipeline agrees; this says WHICH of its parts moved when it
// does not, which is the difference between a five-minute diagnosis and an afternoon.
const segments = (input.words || []).map(probe => ({
    token: probe.token,
    segments: probe.segments,
    authored: probe.authored,
    valid: TB.isAuthoredValid(probe.token, probe.segments, probe.authored),
    derived: TB.derivedSplits(probe.token, probe.segments),
    effective: TB.splitsFor(probe.token, probe.segments, probe.authored),
    cuts: TB.cellCuts(probe.token, TB.splitsFor(probe.token, probe.segments, probe.authored))
}));

process.stdout.write(JSON.stringify({ cases: cases, segments: segments }));
