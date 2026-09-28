// Node harness for the map's TRACK GAIN (PR 1's [Metadata] AudioGain, backlog 315): the value
// parseLyricOsu reads off the stored .osu, and the in-place scale and clamp the player runs on the
// decoded audio before any volume stage.
//
// Two modes:
//   node CoreAudioGainHarness.cjs <typebeat-core.js>
//       the self-contained pins (AudioGainTest in Typebeat.Web.Tests): the parse over a battery of
//       raw values, and applyTrackGain over a small buffer.
//   node CoreAudioGainHarness.cjs <typebeat-core.js> <cases.json>
//       the cross-parser pin (LyricParserParityTest in Typebeat.WireCompat): cases.json is an array
//       of whole .osu texts written by the C# side, and the harness reports the gain the browser
//       reads from each, so the game's production decoder can be held against it file for file.

'use strict';

const corePath = process.argv[2];
const casesPath = process.argv[3];

if (!corePath) {
    process.stderr.write('usage: CoreAudioGainHarness.cjs <typebeat-core.js> [cases.json]\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

if (casesPath) {
    const cases = JSON.parse(require('fs').readFileSync(casesPath, 'utf8'));
    const gains = cases.map(osu => TB.parseLyricOsu(osu).audioGain);
    // Carried through buildBeatmap as well, since that is the object the player reads.
    const built = cases.map(osu => TB.buildBeatmap(TB.parseLyricOsu(osu), false).audioGain);
    process.stdout.write(JSON.stringify({ gains, built }));
    process.exit(0);
}

const LINE = '{"text":"hello world","start_ms":1000,"end_ms":2000,"words":[{"text":"hello","start_ms":1000,"end_ms":1500},{"text":"world","start_ms":1500,"end_ms":2000}]}';

function osu(metadataLines) {
    return [
        'type!beat file format v2',
        '',
        '[General]',
        'AudioFilename: audio.mp3',
        '',
        '[Metadata]',
        'Title:t',
        'Artist:a',
        ...metadataLines,
        '',
        '[Lyrics]',
        '{"version":2,"granularity":"word"}',
        LINE,
        ''
    ].join('\n');
}

const RAW = ['2', '0.5', '4', '4.5', '1e1', '-1', '-0.25', '0', '.5', '1.', '+2', '2.5e-1',
    '2x', '1.5.2', 'abc', '', '1,5', '0x2', 'NaN', 'Infinity', '-Infinity', '1e400', ' 3 '];

const parsed = {};
for (const raw of RAW) parsed[raw] = TB.parseLyricOsu(osu(['AudioGain:' + raw])).audioGain;

// A buffer with a sample that clips each way at gain 2, and one that does not.
function scaled(gain) {
    const left = new Float32Array([0.25, -0.25, 0.75, -0.75]);
    const right = new Float32Array([0.5, -0.5, 0, 1]);
    const clipped = TB.applyTrackGain([left, right], gain);
    return { left: Array.from(left), right: Array.from(right), clipped };
}

process.stdout.write(JSON.stringify({
    defaultGain: TB.constants.DEFAULT_AUDIO_GAIN,
    maxGain: TB.constants.MAX_AUDIO_GAIN,
    absent: TB.parseLyricOsu(osu([])).audioGain,
    builtAbsent: TB.buildBeatmap(TB.parseLyricOsu(osu([])), false).audioGain,
    builtBoosted: TB.buildBeatmap(TB.parseLyricOsu(osu(['AudioGain:2.5'])), false).audioGain,
    // Only a SUCCESSFUL read assigns, as in the decoder: a later junk line leaves the earlier value.
    goodThenJunk: TB.parseLyricOsu(osu(['AudioGain:2', 'AudioGain:junk'])).audioGain,
    junkThenGood: TB.parseLyricOsu(osu(['AudioGain:junk', 'AudioGain:3'])).audioGain,
    // The key is a [Metadata] key: the same line under [General] is not the map's gain.
    inGeneral: TB.parseLyricOsu(osu([]).replace('AudioFilename: audio.mp3', 'AudioFilename: audio.mp3\nAudioGain:2')).audioGain,
    raw: RAW,
    parsed: RAW.map(r => parsed[r]),
    gain2: scaled(2),
    gainHalf: scaled(0.5),
    gain1: scaled(1),
    gain0: scaled(0)
}));
