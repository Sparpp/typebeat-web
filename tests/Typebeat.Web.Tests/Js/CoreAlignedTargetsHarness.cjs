// Node harness for PR 5's ALIGNED subdivision targets (the C#'s TypingEngine.AlignSubdivisionTargets,
// extended CONFIG bit 3): a subdivided word with no valid split_chars is timed on its DERIVED letter
// cut rather than an index-even spread, and a paused stretch with no authored cell cut is timed on
// its own char cuts. The browser plays live only, so it takes the rule unconditionally.
//
// Like CoreSplitCharsHarness, it carries NO fixtures: AlignedSubdivisionTargetsParityTest writes the
// .osu texts to a temp file (argv[3]) and this only reports the per-line cell targets the browser
// builds from them through parseLyricOsu and buildBeatmap.
//
// Usage: node CoreAlignedTargetsHarness.cjs <path to typebeat-core.js> <path to the cases JSON>

'use strict';

const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath || !casesPath) {
    process.stderr.write('usage: CoreAlignedTargetsHarness.cjs <typebeat-core.js> <cases.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

const input = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));

const cases = input.cases.map(one => {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(one.osu), false);

    return {
        name: one.name,
        lines: beatmap.lines.map(line => line.cells.map(c => ({ expected: c.expected, target: c.target })))
    };
});

process.stdout.write(JSON.stringify({ cases: cases }));
