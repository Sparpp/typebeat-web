// Node harness that loads the browser scoring core (typebeat-core.js) and exercises the
// PUNCTUATION derivation: a map stores the author's punctuated, case-sensitive line, and what the
// player types (and sees) is derived from it. The observations are emitted as JSON on stdout so the
// C# fidelity test (PunctuationParityTest) can assert them against the SERVER's own Typeability
// (which is the port of the game's) and against golden values shared with typebeat-osu's
// NonVisual/LiteratePunctuationTest.cs.
//
// This is the scoring-fidelity seam: /play scores land on the same leaderboards as desktop ones, so
// the JS derivation must agree with the C# char for char or browser scores on any punctuated map
// diverge.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE invoked
// with the bare `window` identifier; provide a global `window` before loading so it resolves, then
// read the export back off it.
//
// Usage: node CorePunctuationHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// The strings the C# side runs through its own Typeability.Normalize / ToDefaultStream. Kept here
// rather than in the test so both sides genuinely evaluate the SAME inputs; the C# asserts pairwise
// equality rather than comparing two hardcoded lists.
const SAMPLES = [
    'The bad-cat sat.',
    "It's his half-cut voice, through my lips!",
    'Hello, world!',
    'don’t stop',              // curly apostrophe
    'a‘b’c‚d′e', // every single-quote variant
    'a“b”c„d″e', // every double-quote variant
    'a–b—c―d−e', // every dash variant
    'Héllo,  wörld!',     // diacritics
    'a,b.c\'d-e?f!g;h:i(j)k[l]m"n', // every supported mark, once each
    'a*b/c#d&e',                    // unsupported chars
    'a - b',
    'a-b',
    'a--b',
    '-ab-',
    'a  b',
    '  padded  ',
    'MiXeD CaSe HeRe',
    '42 Apples',
    '',
    '-',
    '...',
];

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

// The normative example as the .osu the browser /play path actually consumes: one line
// "The bad-cat sat." over three words, matching the fixture in the game's LiteratePunctuationTest.
const NORMATIVE_OSU = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"The bad-cat sat.","start_ms":1000,"end_ms":6000,"words":[' +
    '{"text":"The","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"bad-cat","start_ms":2000,"end_ms":4000,"score":1},' +
    '{"text":"sat.","start_ms":4000,"end_ms":6000,"score":1}]}\n';

function shape(beatmap) {
    const line = beatmap.lines[0];
    return {
        text: line.text,                                   // the AUTHORED line the blob stored
        stream: line.cells.map(c => c.expected).join(''),   // what the player types and sees
        count: line.cells.length,
        targets: line.cells.map(c => c.target),
        typeable: line.cells.map(c => c.typeable)
    };
}

// Type the whole line perfectly, exactly on each cell's target, and report the score shape. This is
// the number that has to match desktop: it is what /play submits.
function playThrough(osuText, literate) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(osuText), literate);
    const engine = new TB.TypingEngine(beatmap);
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;
    engine.caseSensitive = !!literate;

    for (const line of beatmap.lines) {
        engine.update(line.activationTime);
        for (const cell of line.cells) {
            engine.update(cell.target);
            engine.processKey(cell.expected, cell.target);
        }
    }

    engine.update(1000000);
    const score = TB.computeScore(engine);

    return {
        finished: engine.finished,
        accuracy: engine.liveAccuracy,
        maxCombo: engine.maxCombo,
        totalScore: score.totalScore,
        rank: score.rank,
        completion: score.completion,
        allCorrect: beatmap.lines.every(l => l.cells.every(c => c.state === 'correct'))
    };
}

const out = {
    punctuationConstant: TB.constants.PUNCTUATION,
    wordBreakConstant: TB.constants.WORD_BREAK,
    // A mark must stay outside the typeable surface: it is never a plain typeable char, only a cell
    // under Literate.
    marksAreNotTypeable: TB.constants.PUNCTUATION.split('').every(c => !TB.isTypeable(c) && !TB.isCell(c)),
    marksAreRecognised: TB.constants.PUNCTUATION.split('').every(c => TB.isPunctuation(c)),
    unsupportedAreNot: '&/*_`#@$%^+=<>|~\\'.split('').every(c => !TB.isPunctuation(c)),

    samples: SAMPLES,
    normalized: SAMPLES.map(s => TB.normalize(s)),
    defaultStream: SAMPLES.map(s => TB.toDefaultStream(s)),
    // The derivation applied to what normalize produced: the real pipeline order.
    normalizedThenDefault: SAMPLES.map(s => TB.toDefaultStream(TB.normalize(s))),

    // Flattening, both branches, on the normative fixture.
    plainShape: shape(TB.buildBeatmap(TB.parseLyricOsu(NORMATIVE_OSU), false)),
    literateShape: shape(TB.buildBeatmap(TB.parseLyricOsu(NORMATIVE_OSU), true)),

    plainRun: playThrough(NORMATIVE_OSU, false),
    literateRun: playThrough(NORMATIVE_OSU, true)
};

process.stdout.write(JSON.stringify(out));
