// Node harness for the browser's HP POOL (backlog 306): typebeat-core.js's HealthAccount, the port of
// the desktop's TypeBeatHealthProcessor, and the fail it now decides.
//
// Before it the browser had no account at all. Health was a read of the rejection streak, and the
// only fail was the 13-rejection branch, which /play can no longer reach, so a near-AFK or
// typo-drowned run played out and submitted passed=true where the desktop fails the identical input.
//
// EMITTED WITH THEIR SCRIPTS, the shape the flexible-lines harness established: the cross-repo arm
// (Typebeat.WireCompat.HealthLiveParityTest) replays these very steps through the game's own
// TypingEngine, with a real TypeBeatHealthProcessor behind it fed through TypeBeatHealthFeed, and
// compares the bar step for step, the fail instant, and the failed run's submitted account, with no
// tolerance. The fixture LINES are emitted too (their words and the windows this loader derived), so
// the C# side builds the same map from the same numbers, and the cells both loaders make are pinned
// before any health reading is trusted.
//
// A step that FAILS the run is the last one played, on both sides: that is the Player's scheduled
// ConcludeFailedScore, which lets the failing step run to its end and nothing after it. The SCORE
// stops earlier, at the result that emptied the bar: every result after it is stamped
// FailedAtJudgement and the score processor drops it.
//
// Usage: node CoreHealthHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

// A line whose words are laid end to end, one per `wordMs`, starting at `start`.
function line(text, start, wordMs, extra) {
    const words = text.split(' ');
    const out = { text: text, start_ms: start, end_ms: start + words.length * wordMs, words: [] };

    for (let i = 0; i < words.length; i++) {
        out.words.push({ text: words[i], start_ms: start + i * wordMs, end_ms: start + (i + 1) * wordMs, score: 1 });
    }

    return Object.assign(out, extra || {});
}

// The same line with its words starting later than the line does (a lead-in inside the window).
function lateLine(text, start, wordsFrom, wordMs) {
    const l = line(text, wordsFrom, wordMs);
    l.start_ms = start;
    return l;
}

function osu(lines, songEndMs) {
    return '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n' +
        JSON.stringify({ granularity: 'word', version: 2, song_end_ms: songEndMs }) + '\n' +
        lines.map(l => JSON.stringify(l)).join('\n') + '\n';
}

function build(lines, songEndMs) { return TB.buildBeatmap(TB.parseLyricOsu(osu(lines, songEndMs))); }

// ---------------------------------------------------------------------------------------------
// The maps.
// ---------------------------------------------------------------------------------------------

// THE AFK MAP, the desktop balance target's own shape (TypeBeatHealthProcessor's note on Spectator:
// seal 1 banks 30 missed cells and seal 2 banks 27 more). 30 misses leave 1 - 30 * 0.0225 = 0.325,
// and the 45th miss is the one that empties the bar, so an idle player dies on the SECOND seal, part
// way through its results, and the third line is never reached.
const AFK = [
    line('the quick brown fox jumps over', 1000, 1000),
    line('the lazy dog sleeps all day', 9000, 1000),
    line('then wakes up', 17000, 1000)
];

// The same idle first line, then two LONG words, so a run of typos lands on lyric cells of their own
// (a wrong letter on a word gap parks the caret instead, and a space paying a gap recovers).
const TYPO = [
    line('the quick brown fox jumps over', 1000, 1000),
    line('extraordinarily uncharacteristically', 9000, 2000),
    line('then wakes up', 17000, 1000)
];

// A short idle line to take the bar off full, so a refund or a recovery after it is not hidden by
// the clamp at 1: 13 cells, 1 - 13 * 0.0225 = 0.7075. The typed line starts its vocals two seconds
// into its own window, so a caret the drag cutoff lands on it at 6500 still presses on time.
const LOWERED = [
    line('one two three', 1000, 1000),
    lateLine('abc def ghi', 5000, 7000, 1000),
    line('jk', 12000, 1000)
];

// Three short words for the parked gap and the full bar. Words 1000 ms apart.
const SHORT = [
    line('ab cd ef', 1000, 1000),
    line('gh', 6000, 1000)
];

// The step-back map: a word skip at the end of line 0 abandons its last cell and parks the caret,
// the line-start snap carries it on, and a backspace at the head of line 1 walks back into line 0.
const STEP_BACK = [
    line('ab cd', 1000, 1000),
    line('ef gh', 5000, 1000)
];

// THE REJECTION MAP (spaceSkipsWord OFF, the one arm where a /play-shaped engine can still reject a
// key: the space on a FREESTYLE slot). Line 0 is idle (0.325 left), line 1 is typed out and then
// the caret rushes on to line 2, whose freestyle slot takes a run of rejected spaces. Those drain the
// bar to 0 WITHOUT failing (a rejection runs no empty test, only the streak test), and it is line
// 1's seal, which has nothing left to miss, that fails the play: the line container's IgnoreHit is
// applied like any other result, and HealthProcessor runs its fail test on every applied result.
//
// Line 0's window runs to 8000, so the idle caret is dragged off it at 9500; "go" is typed there
// (late, two Mehs: 0.365), which finishes line 1 two seconds before entry into line 2 opens at
// 11500. The snap carries the caret on to the slot, five rejected spaces take the bar to 0 with a
// streak of 5, and line 1 seals at its 13000 deadline with nothing left untyped.
const REJECT = [
    line('the quick brown fox jumps over', 1000, 1000),
    line('go', 8000, 500),
    line('& now', 13000, 1000, { freestyle: true })
];

// THE MASH: a freestyle slot at full health and thirteen rejected spaces in a row.
const MASH = [
    line('& ab', 1000, 1000, { freestyle: true }),
    line('cd', 5000, 1000)
];

// ---------------------------------------------------------------------------------------------
// The scripts. Every key is timed; `update` steps move the song clock between them.
// ---------------------------------------------------------------------------------------------
const U = t => ({ op: 'update', t: t });
const K = (c, t) => ({ op: 'key', c: c, t: t });
const B = t => ({ op: 'backspace', t: t });

function keysAt(text, from, step) {
    const out = [];
    for (let i = 0; i < text.length; i++) out.push(U(from + i * step), K(text[i], from + i * step));
    return out;
}

const scenarios = {
    // AFK death on the SECOND seal. No key at all: line 0 seals (30 misses, alive at 0.325), the drag
    // cutoff lands the caret on line 1, and line 1's seal fails the play on its 15th miss. The seal
    // still judges its other 12 cells, but they reach no score: 45 misses are submitted, not 57.
    afkDeathOnTheSecondSeal: {
        lines: AFK, songEnd: 30000, spaceSkipsWord: true,
        steps: [U(0), U(1000), U(5000), U(9000), U(10500), U(12000), U(18000), U(18500), U(25000), U(40000)]
    },

    // A TYPO EMPTYING THE BAR MID-LINE. Line 0 is left to seal (0.325), then line 1 takes a wrong
    // letter on every character of its long first word: the typo drain is taken at the keypress
    // (backlog 166), so the bar empties on a KEY (the 15th typo, 0.325 - 15 * 0.0225 < 0), on a
    // lyric cell, with line 1 still unsealed. The two presses after it land on a frozen engine.
    typoEmptiesTheBarMidLine: {
        lines: TYPO, songEnd: 30000, spaceSkipsWord: true,
        steps: [U(0), U(5000), U(10500)]
            .concat(keysAt('zzzzzzzzzzzzzzzzz', 10600, 50))
            .concat([U(20000), U(40000)])
    },

    // ERASE REFUND ON A PARKED GAP. "ab", then two wrong letters on the gap: the first PARKS the caret
    // on it, the second overwrites the same cell, and the desktop drains for BOTH (CharJudged is
    // raised for every typed-through wrong key). The backspace clears the park in place and refunds
    // ONE drain. The space then pays the gap (a capped correction, so an Ok) and the rest is typed.
    parkedGapErase: {
        lines: SHORT, songEnd: 12000, spaceSkipsWord: true,
        steps: [U(1000), K('a', 1000), U(1500), K('b', 1500), U(1900), K('x', 1900), K('y', 1950), B(2000), K(' ', 2000)]
            .concat(keysAt('cd ef', 2100, 450))
            .concat([U(9000), U(20000)])
    },

    // ERASE REFUND ON THE STEP BACK. "ab c", then a space inside "cd" skips the word's last cell
    // (one drain) and, the word running to the end of the line, parks the caret: entry into line 1
    // opens at 3500. The snap carries the caret on, and a backspace at the head of line 1 walks back
    // into line 0 (stepBackIntoLine), reclaiming the abandoned 'd' and refunding its drain. A typo on
    // line 1 is then erased the ordinary way.
    stepBackReclaim: {
        lines: STEP_BACK, songEnd: 12000, spaceSkipsWord: true,
        steps: [U(1000), K('a', 1000), U(1500), K('b', 1500), U(2000), K(' ', 2000), K('c', 2000), U(2100), K(' ', 2100),
            U(3500), B(3600), K('d', 3700), U(5000), K('e', 5000), U(5500), K('x', 5500), B(5600), K('f', 5600),
            U(6000), K(' ', 6000), K('g', 6000), U(6500), K('h', 6500), U(20000)]
    },

    // ABANDON RECLAIM versus THE SEAL, one map, two scripts. Both lower the bar first (line 0 idle),
    // type 'a' and skip the rest of "abc" (two cells drained). The RECLAIM walks back into the word and
    // types it: the refund lands at the backspace and the cells earn their own recovery.
    abandonReclaimed: {
        lines: LOWERED, songEnd: 20000, spaceSkipsWord: true,
        steps: [U(0), U(6500), U(7000), K('a', 7000), U(7100), K(' ', 7100), B(7200), B(7250), K('a', 7300), K('b', 7400), K('c', 7500),
            K(' ', 8000), U(8000), K('d', 8000), K('e', 8300), K('f', 8600), U(9000), K(' ', 9000), K('g', 9000), K('h', 9300), K('i', 9600),
            U(14000), U(30000)]
    },

    // ...and the SEAL: never coming back, the skip's drain is refunded at the seal (AbandonSealed)
    // into the two Miss results that follow, so the pair nets to exactly one charge per cell.
    abandonSealed: {
        lines: LOWERED, songEnd: 20000, spaceSkipsWord: true,
        steps: [U(0), U(6500), U(7000), K('a', 7000), U(7100), K(' ', 7100),
            U(8000), K('d', 8000), K('e', 8300), K('f', 8600), U(9000), K(' ', 9000), K('g', 9000), K('h', 9300), K('i', 9600),
            U(14000), U(30000)]
    },

    // CAPPED-FIX OK RECOVERY (backlog 210). A typo on 'b', erased, then 'b' retyped dead on its own
    // timing: the tier is capped to Ok, and the bar recovers OK_HEALTH_INCREASE, not GREAT's.
    cappedFixRecoversAtOk: {
        lines: LOWERED, songEnd: 20000, spaceSkipsWord: true,
        steps: [U(0), U(6500), U(7000), K('a', 7000), U(7300), K('x', 7300), B(7350), K('b', 7400), K('c', 7700),
            U(8000), K(' ', 8000), K('d', 8000), U(14000), U(30000)]
    },

    // A REFUND INTO A FULL BAR CLAMPS AT 1. A typo on 'a' (0.9775), 'b' right (+0.03, clamped to 1),
    // then two backspaces: the correct 'b' refunds nothing, the typo refunds its drain into a bar
    // that is already full, and it stays at exactly 1 rather than banking credit.
    refundIntoAFullBar: {
        lines: SHORT, songEnd: 12000, spaceSkipsWord: true,
        steps: [U(1000), K('x', 1000), U(1500), K('b', 1500), B(1600), B(1700), K('a', 1800), K('b', 1850), U(2000), K(' ', 2000), U(20000)]
    },

    // REJECTIONS TO AN EMPTY BAR, and the inert result that then fails it (see REJECT).
    rejectionsEmptyTheBarAndTheSealFailsIt: {
        lines: REJECT, songEnd: 20000, spaceSkipsWord: false,
        steps: [U(0), U(5000), U(9500), U(9600), K('g', 9600), U(9700), K('o', 9700), U(11500)]
            .concat([11600, 11650, 11700, 11750, 11800].map(t => K(' ', t)))
            .concat([U(12000), U(13000), U(13500), U(30000)])
    },

    // THE MASH: thirteen rejected spaces on a freestyle slot from a full bar fail on the thirteenth,
    // through the streak test (the bar itself is only ever tested by a result or a deferred drain).
    mashFailsOnTheThirteenth: {
        lines: MASH, songEnd: 12000, spaceSkipsWord: false,
        steps: [U(1000)].concat(Array.from({ length: 14 }, (_, i) => K(' ', 1000 + 10 * i))).concat([U(20000)])
    }
};

// ---------------------------------------------------------------------------------------------
// Play.
// ---------------------------------------------------------------------------------------------
function play(scenario) {
    const engine = new TB.TypingEngine(build(scenario.lines, scenario.songEnd));
    engine.spaceSkipsWord = scenario.spaceSkipsWord;

    const readings = [];
    let failStep = -1;

    for (let i = 0; i < scenario.steps.length; i++) {
        const step = scenario.steps[i];
        let handled = null;

        if (step.op === 'update') engine.update(step.t);
        else if (step.op === 'backspace') handled = engine.processBackspace();
        else handled = engine.processKey(step.c, step.t);

        readings.push({
            op: step.op,
            t: step.t,
            handled: handled,
            health: engine.health,
            failed: engine.failed,
            activeLineIndex: engine.activeLineIndex,
            caretIndex: engine.caretIndex,
            nextUnsealedLineIndex: engine.nextUnsealedLineIndex,
            consecutiveWrongKeys: engine.consecutiveWrongKeys
        });

        if (engine.failed) {
            failStep = i;
            break;
        }
    }

    const submitted = account(engine);

    // THE FREEZE. The desktop concludes a failed run and stops; the browser's engine instead keeps
    // being ticked until typebeat-player.js notices, and a key can arrive in between. So the steps
    // the comparison above never plays are fed to the frozen engine here anyway, and the account and
    // the bar must not move: that is what update()'s and processKey's failed guards are for.
    let afterFail = null;

    if (failStep >= 0) {
        for (let i = failStep + 1; i < scenario.steps.length; i++) {
            const step = scenario.steps[i];

            if (step.op === 'update') engine.update(step.t);
            else if (step.op === 'backspace') engine.processBackspace();
            else engine.processKey(step.c, step.t);
        }

        afterFail = {
            stepsFed: scenario.steps.length - failStep - 1,
            health: engine.health,
            submitted: account(engine),
            // The engine itself does not advance either: no further line seals, the caret stays put.
            // Invisible to the account (a result after the fail is dropped from the score anyway),
            // which is why it is read here rather than left to the comparison above.
            activeLineIndex: engine.activeLineIndex,
            caretIndex: engine.caretIndex,
            nextUnsealedLineIndex: engine.nextUnsealedLineIndex,
            finished: engine.finished
        };
    }

    return {
        readings: readings,
        failStep: failStep,
        finished: engine.finished,
        submitted: submitted,
        afterFail: afterFail
    };
}

function account(engine) {
    const score = TB.computeScore(engine);

    return {
        passed: score.passed,
        statistics: score.statistics,
        maximumStatistics: score.maximumStatistics,
        maxCombo: score.maxCombo,
        totalScore: score.totalScore,
        accuracy: score.accuracy,
        completion: score.completion,
        rank: score.rank
    };
}

// The fixture as both sides build it: the raw words (what the C# LyricLine is made of) beside the
// windows this loader derived from them, and the cells it made.
function fixture(scenario) {
    const beatmap = build(scenario.lines, scenario.songEnd);

    return beatmap.lines.map((l, i) => ({
        text: scenario.lines[i].text,
        startTime: l.startTime,
        endTime: l.endTime,
        singEndTime: l.singEndTime,
        activationTime: l.activationTime,
        sealGraceMs: l.sealGraceMs,
        words: scenario.lines[i].words.map(w => ({ text: w.text, start: w.start_ms, end: w.end_ms })),
        cells: l.cells.map(c => ({ expected: c.expected, target: c.target, typeable: c.typeable }))
    }));
}

const out = { scenarios: {} };

for (const name of Object.keys(scenarios)) {
    const scenario = scenarios[name];

    out.scenarios[name] = {
        spaceSkipsWord: scenario.spaceSkipsWord,
        script: scenario.steps,
        fixture: fixture(scenario),
        run: play(scenario)
    };
}

out.constants = {
    WRONG_KEY_FAIL_STREAK: TB.constants.WRONG_KEY_FAIL_STREAK,
    GREAT_HEALTH_INCREASE: TB.constants.GREAT_HEALTH_INCREASE,
    OK_HEALTH_INCREASE: TB.constants.OK_HEALTH_INCREASE,
    MEH_HEALTH_INCREASE: TB.constants.MEH_HEALTH_INCREASE,
    MISS_HEALTH_DRAIN: TB.constants.MISS_HEALTH_DRAIN,
    WRONG_KEY_HP_DRAIN: TB.constants.WRONG_KEY_HP_DRAIN,
    HEALTH_EPSILON: TB.constants.HEALTH_EPSILON
};

// The empty test, over the values either side of its edge.
const probes = [0, 1e-9, 5e-8, 9.99e-8, 1e-7, 1.0000001e-7, 2e-7, 1e-6, 0.0125, 1, -1e-9];
out.almostBigger = probes.map(v => ({ value: v, empty: TB.almostBigger(0, v) }));

process.stdout.write(JSON.stringify(out));
