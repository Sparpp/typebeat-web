// Node harness that loads the browser scoring core (typebeat-core.js) and exercises the CHAR-TIMED
// STRETCH narrowing of the syllable-span rule (backlog 209): a FREESTYLE slot, or a cell inside a
// run of three or more identical characters of one syllable, is judged on its own character target
// while the rest of the line keeps its syllable's sung span. The observations are emitted as JSON on
// stdout so the C# fidelity test (CharTimedStretchParityTest) can assert them against the game's
// golden values, which come from typebeat-osu's NonVisual/CharTimedStretchTest.cs.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE invoked
// with the bare `window` identifier; provide a global `window` before loading so it resolves, then
// read the export back off it.
//
// Usage: node CoreCharStretchHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"line","version":2,"song_end_ms":100000}\n';

// The game fixtures, as the .osu the browser /play path actually consumes. Every one of them sings
// its word over [1000, 13000], so the syllable spans are wide enough that the two rules disagree by
// seconds rather than by window edges.

// The field report's shape: six markers sung as one word, cells at 1000 + 2000 i, and the whole
// token ONE group over [1000, 13000] (the syllabifier only refuses a run of three identical
// LETTERS, and '&' is not a letter).
const FREESTYLE_SPAM = OSU_HEADER +
    '{"text":"&&&&&&","start_ms":1000,"end_ms":13000,"freestyle":true,' +
    '"words":[{"text":"&&&&&&","start_ms":1000,"end_ms":13000,"score":1}]}\n';

// "1000": cells at 1000 / 4000 / 7000 / 10000, one group, so the lone '1' and the three-long '0' run
// sit INSIDE a single syllable and the fixture can tell the two rules apart there.
const DIGIT_RUN = OSU_HEADER +
    '{"text":"1000","start_ms":1000,"end_ms":13000,"words":[{"text":"1000","start_ms":1000,"end_ms":13000,"score":1}]}\n';

// "goo": a run of exactly TWO identical characters, which keeps span timing. The threshold is 3 for
// the same reason isSyllabifiable's is.
const DOUBLED_CHAR = OSU_HEADER +
    '{"text":"goo","start_ms":1000,"end_ms":13000,"words":[{"text":"goo","start_ms":1000,"end_ms":13000,"score":1}]}\n';

// The subtimed stretch: "heyyyyy" subtimed at 5000 with the authored split "hey|yyyy", so the gate
// that would have left a stylised spelling ungrouped does not apply. Group 0 owns cells 0..2 over
// [1000, 5000] and group 1 owns cells 3..6 over [5000, 13000].
const SUBTIMED_STRETCH = OSU_HEADER +
    '{"text":"heyyyyy","start_ms":1000,"end_ms":13000,"words":[{"text":"heyyyyy","start_ms":1000,"end_ms":13000,"score":1,' +
    '"syllables":[{"start_ms":5000}],"split_chars":[3]}]}\n';

// A LONE freestyle slot between two letters: it qualifies on the "any key" rule alone, not on the
// length of the run it sits in.
const LONE_FREESTYLE = OSU_HEADER +
    '{"text":"a&b","start_ms":1000,"end_ms":13000,"freestyle":true,' +
    '"words":[{"text":"a&b","start_ms":1000,"end_ms":13000,"score":1}]}\n';

// "a a a": a SPACE breaks a run outright (it is in no group at all), which is what keeps three
// separate characters three separate characters.
const SPACED = OSU_HEADER +
    '{"text":"a a a","start_ms":1000,"end_ms":13000,"words":[' +
    '{"text":"a","start_ms":1000,"end_ms":5000,"score":1},' +
    '{"text":"a","start_ms":5000,"end_ms":9000,"score":1},' +
    '{"text":"a","start_ms":9000,"end_ms":13000,"score":1}]}\n';

// The same subtimed shape capitalised: "heYYyY" is one run of three however the mapper wrote it,
// because runs fold case exactly as the matcher does. Built under LITERATE, where the cells keep the
// authored capitals, so the fold is doing real work rather than reading an already folded stream.
const FOLDED = OSU_HEADER +
    '{"text":"heYYyY","start_ms":1000,"end_ms":13000,"words":[{"text":"heYYyY","start_ms":1000,"end_ms":13000,"score":1,' +
    '"syllables":[{"start_ms":5000}],"split_chars":[3]}]}\n';

function build(osuText, literate) {
    return TB.buildBeatmap(TB.parseLyricOsu(osuText), literate === true);
}

// A fresh engine on a fresh beatmap (cells carry play state), already active on line 0. Word
// skipping is declared OFF, which is the arm the browser plays on.
function activeEngine(osuText, literate) {
    const engine = new TB.TypingEngine(build(osuText, literate));
    engine.spaceSkipsWord = false;
    engine.update(1000);
    return engine;
}

// The line's whole derivation: the cells, the groups they belong to, and the stretch flags derived
// from the two. The flags are ADDITIVE, so the groups and targets emitted here are also the pin that
// nothing about them moved.
function shape(osuText, literate) {
    const line = build(osuText, literate).lines[0];

    return {
        text: line.text,
        expected: line.cells.map(c => c.expected).join(''),
        targets: line.cells.map(c => c.target),
        freestyle: line.cells.map(c => c.freestyle),
        syllables: line.syllables.map(g => ({
            startCell: g.startCell, endCellExclusive: g.endCellExclusive, startTime: g.startTime, endTime: g.endTime
        })),
        cellSyllable: line.cellSyllable.slice(),
        stretch: line.cells.map((c, i) => TB.isCharTimedStretch(line, i)),
        // Every stretch cell must still be a MEMBER of its group and still sit inside its cell range:
        // the fix lives in the judgement predicate and never in the grouping, so the sung-syllable
        // highlight keeps lighting it.
        stretchKeepsItsGroup: line.cells.every((c, i) => {
            if (!TB.isCharTimedStretch(line, i)) return true;

            const g = TB.syllableIndexOf(line, i);
            return g >= 0 && i >= line.syllables[g].startCell && i < line.syllables[g].endCellExclusive;
        })
    };
}

// Play a keystroke script and read back what each cell was judged on. `keys` is [time, char] pairs;
// the deltas and tiers come off the cells themselves, which is where the engine records them.
function play(osuText, keys, literate) {
    const engine = activeEngine(osuText, literate);

    for (const [time, ch] of keys) {
        engine.update(time);
        engine.processKey(ch, time);
    }

    const cells = engine.lines[0].cells;

    return {
        deltas: cells.map(c => c.judgedDelta),
        types: cells.map(c => c.judgeType),
        states: cells.map(c => c.state),
        maxCombo: engine.maxCombo
    };
}

// Six keys mashed the instant the section opens. Under the pure span rule (the era every replay
// stored before backlog 209 re-derives under, and what this file used to do) all six were delta 0;
// the browser judges them on the characters' own targets, so five of them are seconds early.
const spamMash = play(FREESTYLE_SPAM, [[1000, 'q'], [1000, 'q'], [1000, 'q'], [1000, 'q'], [1000, 'q'], [1000, 'q']]);

// The same free ride on a stretched run, inside ONE syllable: the '1' keeps the span, the three '0's
// lose it, so the same press time is worth two different things four cells apart.
const digitMash = play(DIGIT_RUN, [[1000, '1'], [1000, '0'], [1000, '0'], [1000, '0']]);

const out = {
    stretchRunLength: TB.constants.STRETCH_RUN_LENGTH,

    // Structure: the groups the two rules disagree inside, and which cells lose the span.
    freestyleSpam: shape(FREESTYLE_SPAM),
    digitRun: shape(DIGIT_RUN),
    doubledChar: shape(DOUBLED_CHAR),
    subtimedStretch: shape(SUBTIMED_STRETCH),
    loneFreestyle: shape(LONE_FREESTYLE),
    spaced: shape(SPACED),
    foldedLiterate: shape(FOLDED, true),

    // Judgement.
    spamMash: spamMash,
    digitMash: digitMash,
    // A freestyle slot played ON its own target is still a Great: the fix prices the mash, it does
    // not make the section unplayable.
    spamOnTarget: play(FREESTYLE_SPAM, [1000, 3000, 5000, 7000, 9000, 11000].map(t => [t, 'q'])),
    // A LONE character is never char-timed. The '1' pressed 11 seconds past its own target is still
    // inside its syllable, but it also OPENS that syllable, so since backlog 247 it is judged on the
    // distance from the span's start (11000) rather than paid the whole span's 0.
    loneCharLate: play(DIGIT_RUN, [[12000, '1']]),
    // A doubled letter is an ordinary spelling: the two 'o's are deep inside the span and paid 0,
    // while the 'g' that opens the syllable is judged from the span's start (backlog 247).
    doubledLate: play(DOUBLED_CHAR, [[12000, 'g'], [12000, 'o'], [12000, 'o']]),
    // The subtimed "hey|yyyy": the first syllable is span-judged behind its opening 'h' (including
    // the 'y' the split left out of the run), the four-long run in the second is char-timed, and its
    // first cell is both an opener and a stretch, where the stretch arm wins.
    subtimedRun: play(SUBTIMED_STRETCH, [[4900, 'h'], [4900, 'e'], [4900, 'y'],
                                         [5100, 'y'], [5100, 'y'], [5100, 'y'], [5100, 'y']])
};

process.stdout.write(JSON.stringify(out));
