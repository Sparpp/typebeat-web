// Node harness that loads the browser scoring core (typebeat-core.js) and exercises the
// FREESTYLE character feature ('&' authors a cell any key satisfies, whose pressed char is
// kept), emitting the observations as JSON on stdout so the C# fidelity test
// (FreestyleParityTest) can assert them against the game's golden values. Those golden values
// come from typebeat-osu's NonVisual/FreestyleCharTest.cs.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE
// invoked with the bare `window` identifier; provide a global `window` before loading so it
// resolves, then read the export back off it.
//
// Usage: node CoreFreestyleHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

const MARKER = '&';

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

// The game fixture's map, as the .osu the browser /play path actually consumes: one line "a&b",
// one word spanning [1000, 4000], so the three cells target 1000 / 2000 / 3000.
const FREESTYLE_OSU = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"a&b","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"a&b","start_ms":1000,"end_ms":4000,"score":1}]}\n';

// Same shape, letters only: the control the freestyle play must score identically to.
const PLAIN_OSU = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"axb","start_ms":1000,"end_ms":4000,"words":[{"text":"axb","start_ms":1000,"end_ms":4000,"score":1}]}\n';

// Storage: an aligner-authored line whose lyrics genuinely contain "&" (no opt-in flag) versus
// the same text flagged by the editor's encoder.
const LEGACY_AMPERSAND_OSU = OSU_HEADER +
    '{"version":2,"song_end_ms":20000}\n' +
    '{"text":"me & you","start_ms":1000,"end_ms":4000}\n';

const FLAGGED_AMPERSAND_OSU = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"me & you","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[' +
    '{"text":"me","start_ms":1000,"end_ms":2000},{"text":"&","start_ms":2000,"end_ms":3000},{"text":"you","start_ms":3000,"end_ms":4000}]}\n';

function build(osuText) {
    return TB.buildBeatmap(TB.parseLyricOsu(osuText));
}

// A fresh engine on a fresh beatmap (cells carry play state), already active on line 0.
function activeEngine(osuText) {
    const engine = new TB.TypingEngine(build(osuText || FREESTYLE_OSU));
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;
    engine.update(1000);
    return engine;
}

function cellShape(beatmap) {
    const cells = beatmap.lines[0].cells;
    return {
        text: beatmap.lines[0].text,
        count: cells.length,
        expected: cells.map(c => c.expected).join(''),
        targets: cells.map(c => c.target),
        freestyle: cells.map(c => c.freestyle),
        freestyleCount: cells.filter(c => c.freestyle).length,
        typeable: cells.every(c => c.typeable)
    };
}

// "Any key really is any key on the typeable surface EXCEPT space": press 'a', then `pressed` on
// the slot.
function anyKey(pressed) {
    const engine = activeEngine();
    const first = engine.processKey('a', 1000);
    engine.update(2000);
    const accepted = engine.processKey(pressed, 2000);
    const cell = engine.lines[0].cells[1];
    return {
        activeLineIndex: engine.activeLineIndex,
        first: first,
        accepted: accepted,
        state: cell.state,
        typedChar: cell.typedChar,
        judgeType: cell.judgeType,
        judgedDelta: cell.judgedDelta,
        caretIndex: engine.caretIndex,
        combo: engine.combo,
        consecutiveWrongKeys: engine.consecutiveWrongKeys,
        liveAccuracy: engine.liveAccuracy
    };
}

// A full three-cell play, scored through computeScore exactly as /play submits it.
function playThrough(osuText, middle) {
    const beatmap = build(osuText);
    const engine = new TB.TypingEngine(beatmap);
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;
    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey(middle, 2000);
    engine.update(3000);
    engine.processKey('b', 3000);
    engine.update(9000);
    const results = TB.computeScore(engine);
    return {
        totalCells: beatmap.totalCells,
        engineScore: engine.score,
        totalScore: results.totalScore,
        maxCombo: results.maxCombo,
        accuracy: results.accuracy,
        completion: results.completion,
        rank: results.rank,
        passed: results.passed,
        counts: results.counts,
        wpm: results.wpm,
        liveAccuracy: engine.liveAccuracy
    };
}

// Space is the ONE key a freestyle slot refuses (backlog 50), and since backlog 184 it is the one
// cell that refuses it: on an ordinary cell a mid-word space is typed through like any other wrong
// character. So the slot's rejection is held against the STRICT control (the same run on the plain
// "axb" map with allowWrongInput off, which is the branch the slot still takes) and against the LIVE
// one (the same run with the flag on, which now types the space through). All three sets of
// observations are emitted, and the test asserts the equality with one and the contrast with the
// other.
function spaceRejected(osuText, refill, strict) {
    const engine = activeEngine(osuText);
    const rejected = [];
    let judged = 0;
    engine.allowWrongInput = strict !== true;
    engine.onWrongKey = ch => rejected.push(ch);

    engine.processKey('a', 1000);
    engine.onCharJudged = () => judged++; // count only what the space does
    engine.update(2000);
    const handled = engine.processKey(' ', 2000);
    const cell = engine.lines[0].cells[1];

    const after = {
        handled: handled,
        state: cell.state,
        typedChar: cell.typedChar,
        judgedDelta: cell.judgedDelta,
        caretIndex: engine.caretIndex,
        combo: engine.combo,
        consecutiveWrongKeys: engine.consecutiveWrongKeys,
        liveAccuracy: engine.liveAccuracy,
        judged: judged,
        rejected: rejected.join('')
    };

    // The slot is still fillable afterwards: the space cost combo, not the cell. The refill lands on
    // the cell's OWN target, which is where the freestyle arm and the ordinary control are judged
    // alike: since backlog 209 a freestyle slot is a STRETCH cell and so is graded on its character
    // target, while an ordinary cell keeps its syllable's sung span, and an off-target refill would
    // put the two arms on different rules over a difference this equality is not about.
    engine.update(2000);
    after.refillAccepted = engine.processKey(refill, 2000);
    after.refillState = engine.lines[0].cells[1].state;
    after.refillWrongKeys = engine.consecutiveWrongKeys;
    after.score = engine.score;
    after.maxCombo = engine.maxCombo;
    after.finalAccuracy = engine.liveAccuracy;
    return after;
}

// Mashing accepts any key on any cell. A freestyle slot is exempt from the char rewrite, so space
// (the one key it would otherwise reject) is substituted with the auto char instead.
function mashingSpace() {
    const engine = activeEngine();
    engine.mashingEnabled = true;
    engine.processKey('a', 1000);
    engine.update(2000);
    const accepted = engine.processKey(' ', 2000);
    const cell = engine.lines[0].cells[1];

    const control = activeEngine(PLAIN_OSU);
    control.mashingEnabled = true;
    const controlAccepted = control.processKey(' ', 1000); // space on an ORDINARY cell, still taken

    return {
        accepted: accepted,
        state: cell.state,
        typedChar: cell.typedChar,
        combo: engine.combo,
        liveAccuracy: engine.liveAccuracy,
        controlAccepted: controlAccepted,
        controlState: control.lines[0].cells[0].state,
        controlTypedChar: control.lines[0].cells[0].typedChar
    };
}

// Literate (case-sensitive) is bypassed for the slot; the control proves the flag is live.
function literate() {
    const engine = activeEngine();
    engine.caseSensitive = true;
    engine.processKey('a', 1000);
    engine.update(2000);
    const accepted = engine.processKey('Q', 2000);
    const cell = engine.lines[0].cells[1];

    // Wrong case on an ORDINARY cell still does not MATCH; since backlog 107 the default model
    // types the offending char through as a wrong cell instead of rejecting it. Both facts are
    // asserted, and the strict control below keeps the rejection branch covered too.
    const control = activeEngine();
    control.caseSensitive = true;
    control.processKey('A', 1000);

    const strictControl = activeEngine();
    strictControl.caseSensitive = true;
    strictControl.allowWrongInput = false;
    strictControl.processKey('A', 1000);

    return {
        accepted: accepted,
        state: cell.state,
        typedChar: cell.typedChar,
        combo: engine.combo,
        liveAccuracy: engine.liveAccuracy,
        controlState: control.lines[0].cells[0].state,
        controlTypedChar: control.lines[0].cells[0].typedChar,
        controlMistypes: control.mistypes,
        controlWrongKeys: control.consecutiveWrongKeys,
        strictControlState: strictControl.lines[0].cells[0].state,
        strictControlWrongKeys: strictControl.consecutiveWrongKeys
    };
}

// Mashing must not stamp the marker over the player's char; the control proves it still rewrites
// on an ordinary cell.
function mashing() {
    const engine = activeEngine();
    engine.mashingEnabled = true;
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('q', 2000);

    const control = activeEngine();
    control.mashingEnabled = true;
    control.processKey('z', 1000); // wrong key on an ORDINARY cell: judged as its expected char

    return {
        typedChar: engine.lines[0].cells[1].typedChar,
        state: engine.lines[0].cells[1].state,
        combo: engine.combo,
        controlTypedChar: control.lines[0].cells[0].typedChar,
        controlState: control.lines[0].cells[0].state
    };
}

// Backspace reopens the slot (shimmer resumes) and a different char lands, scoring-inert.
function backspace() {
    const engine = activeEngine();
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('q', 2000);
    const scoreAfterFirst = engine.score;

    engine.processBackspace();
    const cell = engine.lines[0].cells[1];
    const reopenedState = cell.state;
    const reopenedChar = cell.typedChar;
    const reopenedCaret = engine.caretIndex;

    engine.update(2400);
    const accepted = engine.processKey('7', 2400);

    return {
        scoreAfterFirst: scoreAfterFirst,
        reopenedState: reopenedState,
        reopenedChar: reopenedChar,
        reopenedCaret: reopenedCaret,
        accepted: accepted,
        retypedChar: cell.typedChar,
        scoreAfterRetype: engine.score,
        judgedDelta: cell.judgedDelta
    };
}

// An untyped slot seals as a Miss like any other cell.
function sealed_() {
    const engine = activeEngine();
    engine.processKey('a', 1000);
    engine.update(9000);
    const results = TB.computeScore(engine);
    return {
        state: engine.lines[0].cells[1].state,
        missCount: results.counts.miss,
        completion: results.completion,
        rank: results.rank
    };
}

// Shimmer: an open slot never shows the raw marker, is deterministic, and actually moves.
function shimmer() {
    const seen = {};
    let markerSeen = false;
    for (let tick = 0; tick < 40; tick++) {
        const g = TB.freestyleGlyph(tick, 0);
        if (g === MARKER) markerSeen = true;
        seen[g] = true;
    }
    return {
        deterministic: TB.freestyleGlyph(17, 3) === TB.freestyleGlyph(17, 3),
        distinctOver40Ticks: Object.keys(seen).length,
        markerSeen: markerSeen,
        neighboursDiffer: TB.freestyleGlyph(5, 0) !== TB.freestyleGlyph(5, 1),
        tickHoldsWithinInterval: TB.freestyleTick(0) === TB.freestyleTick(TB.constants.SHIMMER_INTERVAL_MS - 1),
        tickAdvances: TB.freestyleTick(0) !== TB.freestyleTick(TB.constants.SHIMMER_INTERVAL_MS + 1)
    };
}

const out = {
    // Typeability: the marker stays outside the typeable surface and outside default normalize.
    isTypeableMarker: TB.isTypeable(MARKER),
    isFreestyleMarker: TB.isFreestyle(MARKER),
    isCellMarker: TB.isCell(MARKER),
    normalizeDefault: TB.normalize('R&B rock & roll'),
    normalizeKept: TB.normalize('R&B rock & roll', true),
    normalizeKeptPunctuation: TB.normalize('  hey,   &you!  ', true),
    defaultStreamKeptPunctuation: TB.toDefaultStream(TB.normalize('  hey,   &you!  ', true)),

    // Flattening: the slot is a cell, timed like a letter.
    freestyleShape: cellShape(build(FREESTYLE_OSU)),
    plainShape: cellShape(build(PLAIN_OSU)),

    // Judgement.
    anyKeyQ: anyKey('q'),
    anyKeyZ: anyKey('Z'),
    anyKey7: anyKey('7'),
    spaceOnFreestyle: spaceRejected(FREESTYLE_OSU, '7'),
    spaceOnOrdinaryStrict: spaceRejected(PLAIN_OSU, 'x', true),
    spaceOnOrdinaryLive: spaceRejected(PLAIN_OSU, 'x'),
    freeRun: playThrough(FREESTYLE_OSU, 'q'),
    plainRun: playThrough(PLAIN_OSU, 'x'),
    literate: literate(),
    mashing: mashing(),
    mashingSpace: mashingSpace(),
    backspace: backspace(),
    sealed: sealed_(),

    // Storage: the opt-in flag, and back-compat for a bare ampersand without it.
    legacyShape: cellShape(build(LEGACY_AMPERSAND_OSU)),
    flaggedShape: cellShape(build(FLAGGED_AMPERSAND_OSU)),

    shimmer: shimmer(),

    // The freestyle judgement ladder (PR 16): a slot is graded on a WIDER ladder than an ordinary
    // cell. Emitted so the C# side can pin the scale against SyncWindows.FREESTYLE_WINDOW_SCALE.
    freestyleWindowScale: TB.FREESTYLE_WINDOW_SCALE,
    freestyleWindows: TB.FREESTYLE_WINDOWS
};

process.stdout.write(JSON.stringify(out));
