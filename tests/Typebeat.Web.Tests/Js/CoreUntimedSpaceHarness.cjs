// Node harness for UNTIMED SPACES in the browser scoring core (backlog 148): a space typed on a
// SPACE CELL is judged as though it landed dead on that cell's target, so it takes the top tier
// whatever the clock said and can never fall into one of the two zero-point tiers that break combo.
//
// Written as SEQUENCES rather than as transcribed literals, in the style of
// CoreComboRestoreHarness: the point of a parity guard is to drive the shipped JS through the same
// keystrokes the game's own fixture uses (NonVisual/UntimedSpaceTest.cs and, for the space key's
// own fate on a lyric cell, NonVisual/SpaceDisciplineTest.cs) and let the C# side assert
// what came out, so a divergence shows up as a wrong number rather than as a test nobody updated.
//
// Half of this is the NEGATIVE half, and it matters as much: the exemption is keyed on the CELL
// being a space, not on the KEY being one, so a space that lands anywhere else must still cost the
// player. Since backlog 184 what it costs is the CELL (it is typed through as an ordinary typo)
// rather than a rejection, so the mash-fail streak it used to build is pinned on the arm that still
// reaches the rejection path, the engine's strict model.
//
// Usage: node CoreUntimedSpaceHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// The game fixture's workhorse line, transcribed into the .osu form /play consumes: "ab cd", words
// "ab" [1000, 2000] and "cd" [2000, 3000], so cells are a = 1000, b = 1500, ' ' = 2000 (the first
// word's end), c = 2000, d = 2500. Line granularity, matching the C# fixture, so the windows are
// the unscaled ones: Great [-250, 400], Ok [-600, 1000], Meh [-1200, 2000].
//
// Two boundaries of the same line. `end_ms` on the line is its SUNG end (3000 either way); the
// line's deadline is the next line's start, or song_end_ms for the last line, which is the knob
// here. LONG runs to 60000 so a press can be made absurdly late without the line sealing underneath
// it; SEALING keeps the real 4000 deadline, matching the C# fixture's two shapes.
function osu(songEndMs) {
    return '[General]\n' +
        'AudioFilename: a.mp3\n' +
        '[Metadata]\n' +
        'Title: t\n' +
        'Artist: a\n' +
        '[Lyrics]\n' +
        '{"granularity":"line","version":2,"song_end_ms":' + songEndMs + '}\n' +
        '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[' +
        '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
        '{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n';
}

const LONG = osu(60000);
const SEALING = osu(4000);

function started(source) {
    const engine = new TB.TypingEngine(TB.buildBeatmap(TB.parseLyricOsu(source)));
    // Every scenario in this file was authored on the skip-OFF arm (a mid-word space is a typo,
    // typed through), so the arm is declared rather than inherited: since backlog 198 the engine
    // DEFAULTS to skip-on, and these pins are about the untimed-space exemption, not the skip.
    engine.spaceSkipsWord = false;
    engine.breaks = 0;
    engine.onComboBroken = () => { engine.breaks++; };
    engine.update(1000);
    return engine;
}

/** Everything a press can move, on both accounts (the engine's own run and the submitted one). */
function snapshot(engine) {
    const cells = engine.lines[0].cells;
    return {
        caretIndex: engine.caretIndex,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        breaks: engine.breaks,
        score: engine.score,
        mistypes: engine.counts.WrongChar || 0,
        consecutiveWrongKeys: engine.consecutiveWrongKeys,
        liveAccuracy: engine.totalKeypresses === 0 ? 1 : engine.correctKeypresses / engine.totalKeypresses,
        failed: !!engine.failed,
        states: cells.map(c => c.state),
        judgeTypes: cells.map(c => c.judgeType),
        judgedDeltas: cells.map(c => c.judgedDelta),
        processorHighestCombo: engine.processor.highestCombo
    };
}

// The shape itself, so a drift in where the space cell sits or what it targets is caught here and
// not as a mystery in one of the runs below.
function shape() {
    const cells = TB.buildBeatmap(TB.parseLyricOsu(LONG)).lines[0].cells;
    return {
        expected: cells.map(c => c.expected),
        targets: cells.map(c => c.target),
        typeable: cells.map(c => c.typeable),
        tiers: cells.map(c => c.tier)
    };
}

// The headline: 'a' and 'b' dead on target, then the word gap pressed 5 SECONDS late, far outside
// even the Meh window. Paired with the identical run whose space lands on target, because
// "the spacebar is not part of the timing challenge" IS that equality.
function spacePressedAt(time) {
    const engine = started(LONG);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey(' ', time);
    return snapshot(engine);
}

// The same press one cell later is NOT exempt: it was the spacebar that left the timing challenge,
// not the player's sense of rhythm.
function lyricCharPressedJustAsLate() {
    const engine = started(LONG);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey(' ', 7000);   // exempt
    const comboAfterSpace = engine.combo;
    // Delta 4100 on a lyric char: Lagging, no points, combo breaks. 4100 and not the 5100 the game's
    // own fixture pins, because that fixture drives a BARE engine (classic era, judged on 'c''s own
    // 2000 target) while the browser only ever plays live and judges the span of the syllable "cd",
    // which is sung over [2000, 3000]. Live desktop play measures the same 4100. Nothing the case is
    // about moves with it: the press is just as late, still Lagging, still worth nothing.
    engine.processKey('c', 7100);
    return Object.assign(snapshot(engine), { comboAfterSpace: comboAfterSpace });
}

// Scoped to the CELL, hole 1: the exemption is keyed on the cell being a space, never on the KEY
// being one, so a space pressed on a LYRIC character earns nothing. Since backlog 184 it is TYPED
// THROUGH as an ordinary typo rather than rejected (on the skip-OFF arm this file declares, there
// is no word for the press to skip and it is simply a wrong character), so what it costs the
// player is the cell. Keying the exemption off the key instead would have made space-mashing
// free, which is what this pins either way.
function spaceOnALyricChar() {
    const engine = started(LONG);
    engine.rejected = null;
    engine.onWrongKey = (c) => { engine.rejected = c; };
    engine.processKey('a', 1000);
    engine.processKey(' ', 1500); // caret is on 'b', dead on ITS target
    return Object.assign(snapshot(engine), {
        spaceSkipsWord: !!engine.spaceSkipsWord,
        rejected: engine.rejected,
        typedChar: engine.lines[0].cells[1].typedChar
    });
}

// Scoped to the CELL, hole 2, in the two models. Backlog 184 took the mid-word space off the
// rejection path, so a mashed spacebar now spells the line wrong instead of building the
// consecutive-wrong-key streak: `live` is that, and it is what every /play run does. The streak and
// its 13-key fail are unchanged where the rejection path is still reachable, which the browser can
// only be put on by hand (Gatekeeper has no mods payload to arrive through), so `strict` drives it
// directly. The engine's mash guard is what this keeps covered: a rule with no reachable arm left
// would otherwise go untested and rot.
function mashedSpaces() {
    const live = started(LONG);
    const liveStreak = [];
    for (let i = 0; i < 13; i++) {
        live.processKey(' ', 1000 + i);
        liveStreak.push(live.consecutiveWrongKeys);
    }

    const strict = started(LONG);
    strict.allowWrongInput = false;
    const streak = [];
    for (let i = 0; i < 13; i++) {
        strict.processKey(' ', 1000 + i);
        streak.push(strict.consecutiveWrongKeys);
    }
    const failedAt13 = Object.assign(snapshot(strict), { streak: streak });

    // ...and any accepted char resets it. Cleared on a fresh engine, since the one above is failed.
    const fresh = started(LONG);
    fresh.allowWrongInput = false;
    for (let i = 0; i < 3; i++) fresh.processKey(' ', 1000 + i);
    const beforeReset = fresh.consecutiveWrongKeys;
    fresh.processKey('a', 1000);
    return Object.assign(failedAt13, {
        streakBeforeReset: beforeReset,
        streakAfterAccepted: fresh.consecutiveWrongKeys,
        live: Object.assign(snapshot(live), { streak: liveStreak })
    });
}

// An untimed space is not a FREE space: a space cell nobody pressed is a character of the map left
// untyped, and seals a miss with every other one.
function untypedSpaceSeals() {
    const engine = started(SEALING);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.update(5000); // past the line's 4000 deadline and its seal grace
    const s = snapshot(engine);
    return Object.assign(s, {
        missCount: s.states.filter(x => x === 'missed').length,
        gapState: s.states[2],
        gapJudgeType: s.judgeTypes[2]
    });
}

// The anti-farming rule is untouched: retyping a space after backspacing over it is scoring-inert,
// and it re-classifies the stored firstCorrectDelta, which for a space is the zeroed one, so the
// retype is a top-tier judgement too however late it lands.
function retypeIsInert() {
    const engine = started(LONG);
    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey(' ', 7000);
    const scoreAfterFirst = engine.score;
    engine.processBackspace();
    const reopenedState = engine.lines[0].cells[2].state;
    engine.processKey(' ', 9000);
    return Object.assign(snapshot(engine), {
        scoreAfterFirst: scoreAfterFirst,
        reopenedState: reopenedState
    });
}

process.stdout.write(JSON.stringify({
    shape: shape(),
    late: spacePressedAt(7000),
    onTime: spacePressedAt(2000),
    lyricLate: lyricCharPressedJustAsLate(),
    midWordSpace: spaceOnALyricChar(),
    mashed: mashedSpaces(),
    sealed: untypedSpaceSeals(),
    retyped: retypeIsInert()
}));
