// Node harness for the COMBO RESTORE rule in the browser scoring core (backlog 140/143): a wrong
// keypress snapshots the streak it broke against the cell it spoiled, and correcting THAT cell
// resumes it, at the snapshot plus everything earned since.
//
// This fixture exists because of how the divergence it guards was found. Every other JS parity
// harness here pins the browser against golden literals transcribed once from the game suite, so
// when the game half of backlog 140 landed and the JS did not restore combo at all, all twenty of
// those tests still passed: the literals had been transcribed from a game that did not restore
// either, and nothing in them reaches the live C#. A silent divergence on the SHARED leaderboards
// is exactly what the parity suite is for, so this file drives the browser through the
// combo-restore SEQUENCES themselves, which is the shape of test that would have caught it.
//
// The keystroke sequences and the expected numbers come from the game's own pins:
//   - NonVisual/ComboRestoreTest.cs, whose fixture is the eight-cell line and whose three cases are
//     the plain fix, the intervening break and the repeated wrong/fix cycle;
//   - NonVisual/TypeBeatReplayScorerTest.AFixedTypoResumesTheStreakOnlyUnderTheLiveRule, whose
//     thirteen-cell map re-derives max_combo 13 under the live rule and 11 under the pre-140 one.
//     The browser is permanently live (see `restorable` in typebeat-core.js), so 13 is the only
//     answer available to it, and 11 is what it produced before this landed.
//   - the three backlog 176 pairs in ComboRestoreTest (adjacent typos, one cell fumbled twice, a
//     word skipped over a typo), whose live arm is the only one this file has: a break takes
//     ownership of the streak only if it HAS a streak to own, so an empty break leaves an
//     outstanding claim alone. The C# also pins each shape under ComboClaimRule.LatestBreakWins,
//     the arm every score stored before 176 was played under, which the browser cannot be in.
//
// Usage: node CoreComboRestoreHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// ---------------------------------------------------------------------------------------------
// Fixture 1: ComboRestoreTest's map. One line, "abcdefgh", eight cells over [1000, 5000], so cell i
// targets exactly 1000 + 500i and every press can be struck dead on its own target: nothing but the
// wrong keys can ever break a run. Line granularity, matching the C# fixture's TimingGranularity.Line.
// ---------------------------------------------------------------------------------------------
const WORD = 'abcdefgh';

const WORD_OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"line","version":2,"song_end_ms":20000}\n' +
    '{"text":"abcdefgh","start_ms":1000,"end_ms":5000,"words":[' +
    '{"text":"abcdefgh","start_ms":1000,"end_ms":5000,"score":1}]}\n';

const WRONG_KEY = 'z'; // not in "abcdefgh", so it is reliably wrong on every cell

function target(cellIndex) { return 1000 + 500 * cellIndex; }

// A started engine on the eight-cell line, with the restore events and combo breaks recorded the
// way the C# test records them (engine.ComboRestored / engine.ComboBroken).
function started() {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(WORD_OSU), false);
    const engine = new TB.TypingEngine(beatmap);
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;

    engine.restored = [];
    engine.breaks = 0;
    engine.onComboRestored = (streak) => engine.restored.push(streak);
    engine.onComboBroken = () => { engine.breaks++; };

    engine.update(1000);
    return engine;
}

/** Type cells [from, to) correctly, each on its own target. */
function typeCorrectly(engine, from, to) {
    for (let i = from; i < to; i++) engine.processKey(WORD[i], target(i));
}

/** Type a wrong char into the cell the caret is on (which must be cellIndex). */
function typo(engine, cellIndex) {
    if (engine.caretIndex !== cellIndex) throw new Error(`caret is ${engine.caretIndex}, expected ${cellIndex}`);
    engine.processKey(WRONG_KEY, target(cellIndex));
}

/** Backspace onto cellIndex and type it correctly. */
function fix(engine, cellIndex) {
    while (engine.caretIndex > cellIndex) engine.processBackspace();
    engine.processKey(WORD[cellIndex], target(cellIndex));
}

// Everything both accounts hold, because the two are separate and the whole class of bug lives in
// them disagreeing: `combo` / `maxCombo` are the engine's own live run (what the HUD shows), and
// `processorCombo` / `processorHighestCombo` are the osu-side account, the second of which is
// submitted as max_combo.
function snapshot(engine) {
    return {
        restored: engine.restored,
        breaks: engine.breaks,
        mistypes: engine.mistypes,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        processorCombo: engine.processor.combo,
        processorHighestCombo: engine.processor.highestCombo,
        lastJudgeType: engine.lines[0].cells.reduce((last, c) => (c.judgeType !== null ? c.judgeType : last), null)
    };
}

// ComboRestoreTest.FixingATypoResumesTheStreakItBroke. A streak of 3, a typo, two cells rebuilt,
// then the fix: the run resumes at 3 + 2 and the corrected retype is judged ON TOP of that, so it
// ends at 6 rather than at 3. That ordering is the difference between fixing a typo being worth
// score and being worth only accuracy.
function fixResumesTheStreak() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    const comboBeforeTypo = engine.combo;

    typo(engine, 3);
    const comboAfterTypo = engine.combo;

    typeCorrectly(engine, 4, 6); // rebuild a run of 2 while cell 3 sits wrong
    const comboBeforeFix = engine.combo;

    fix(engine, 3);

    return Object.assign(snapshot(engine), {
        comboBeforeTypo: comboBeforeTypo,
        comboAfterTypo: comboAfterTypo,
        comboBeforeFix: comboBeforeFix,
        // The cell the fix landed on: its judgement is the retype's own, earned at the resumed
        // streak, and it is the cell's first and only result (the typo deferred it).
        fixedCellJudgeType: engine.lines[0].cells[3].judgeType
    });
}

// ComboRestoreTest.AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing. Two wrong keys with
// a run REBUILT between them, so the second break really does cost something of its own and really
// does take the claim. Only the NEWEST wrong cell holds one, so fixing them in the order they
// happened restores nothing for the first and everything for the second.
//
// Since backlog 262 that is the bound only on WHICH CELL redeems. What the claim is WORTH carries
// the displaced one FOLDED IN, so the redemption is 5 rather than 2 and the fully corrected run
// reaches 7, which is exactly the seven cells 0 to 6 a clean run holds at the same point: the older
// break's streak of 3 was earned, its cells are resolved and inert on every retype, and discarding
// it lost it for good even though the player came back and typed everything out. The game pins the
// pre-262 arm beside this one (ComboRestoreTest.ThePre262RuleDropsTheDisplacedClaim); this file has
// no arm for it, because the browser only ever plays live (see typebeat-core.js's
// snapshotRedeemableBreak).
function interveningBreak() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    typo(engine, 3);            // snapshots 3
    typeCorrectly(engine, 4, 6);
    typo(engine, 6);            // the displacing break: a streak of 2 of its own, with cell 3's claim folded in

    const comboAfterSecondTypo = engine.combo;

    fix(engine, 3);

    const afterOlderFix = { restored: engine.restored.slice(), combo: engine.combo };

    // Cell 6 is the one still holding a claim. The caret is on cell 4 after the fix above, so cells
    // 4 and 5 are inert retypes on the way back out (already judged correct, so no combo of their
    // own) and cell 6 is the fix.
    typeCorrectly(engine, 4, 6);
    fix(engine, 6);

    return Object.assign(snapshot(engine), {
        comboAfterSecondTypo: comboAfterSecondTypo,
        restoredAfterOlderFix: afterOlderFix.restored,
        comboAfterOlderFix: afterOlderFix.combo
    });
}

// ComboRestoreTest.RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime. Each cycle snapshots
// whatever the run has grown back to, and the second fix is a scoring-inert retype (the cell was
// already judged by the first fix), which is exactly why the restore is not folded into the
// judgement: the streak belongs to the FIX, not to the cell's result.
function repeatedCycles() {
    const engine = started();

    typeCorrectly(engine, 0, 2);
    typo(engine, 2);
    fix(engine, 2);

    const comboAfterFirstCycle = engine.combo;

    engine.processBackspace();
    typo(engine, 2);
    const comboAfterSecondTypo = engine.combo;

    fix(engine, 2);

    return Object.assign(snapshot(engine), {
        comboAfterFirstCycle: comboAfterFirstCycle,
        comboAfterSecondTypo: comboAfterSecondTypo
    });
}

// NOT transcribed: the case that isolates the highest-combo watermark, which is the one thing the
// osu-side mirror has to do for itself. The restore is the LAST thing that moves combo, it takes
// the run past anything the play had reached before, and the retype that carries it is INERT (the
// cell was already judged by an earlier fix), so no result follows to raise the maximum. Restoring
// by writing the engine's combo over the processor's, or leaving highestCombo to the next result,
// both submit a max_combo the HUD never showed.
//
// Cells 0,1 correct (run 2, watermark 2); typo on cell 2 and an immediate fix (restore 2, the
// retype earns, run 3, watermark 3); backspace and typo cell 2 AGAIN on that run of 3; cells 3 and
// 4 correct (run 2, watermark still 3); backspace three times and fix cell 2. The snapshot of 3
// plus the 2 earned since is 5, past the watermark of 3, and the retype is inert.
function restoreBeyondTheWatermark() {
    const engine = started();

    typeCorrectly(engine, 0, 2);
    typo(engine, 2);
    fix(engine, 2);

    engine.processBackspace();
    typo(engine, 2);            // snapshots 3

    typeCorrectly(engine, 3, 5);
    const watermarkBeforeFix = engine.processor.highestCombo;

    fix(engine, 2);             // backspaces over cells 4 and 3, then retypes cell 2 (inert)

    return Object.assign(snapshot(engine), {
        watermarkBeforeFix: watermarkBeforeFix,
        // The retype applied no result, which is the whole point: nothing after the restore could
        // have raised the maximum on its behalf.
        judgementCount: engine.processor.judgementCount
    });
}

// ComboRestoreTest.TwoWrongKeysOnAdjacentCellsKeepTheStreakWhenBothAreFixed, and the shape a real
// submitted run took: score 6212 on "Joji - PIXELATED KISSES [Insane]", 447 combo deep into "if you
// never hear from me", where the player typed 'a' onto the 'm' cell and then 'm' onto the 'e' cell,
// backspaced twice and typed "me" out correctly. The second wrong key breaks a run of ZERO, because
// the first one already took the streak, so it has nothing to take the claim with and the older
// cell keeps it: fixing that cell resumes the run.
//
// Deliberately stronger than interveningBreak above, which is the same two wrong keys with a run
// REBUILT between them, so there the second break really does cost something and really does take
// the claim. Nothing is lost between these two, which is exactly why the older claim survives. This
// is the case the browser got wrong for as long as its two snapshot sites wrote unconditionally.
function adjacentTyposBothFixed() {
    const engine = started();

    typeCorrectly(engine, 0, 4);
    const comboBeforeTypos = engine.combo;

    typo(engine, 4);            // snapshots 4 against cell 4
    typo(engine, 5);            // breaks a run of 0, so it has nothing to take the claim with

    // Both spoiled cells corrected, oldest first, exactly as the reported run did.
    fix(engine, 4);
    engine.processKey(WORD[5], target(5));

    return Object.assign(snapshot(engine), { comboBeforeTypos: comboBeforeTypos });
}

// ComboRestoreTest.ASecondWrongKeyOnTheSameCellKeepsTheStreakTheFirstOneSnapshotted. The same-cell
// sibling: fumble cell 2, erase it, fumble it AGAIN, then correct it. It differs from
// repeatedCycles only in that no successful fix separates the two wrong keys, so there is no second
// streak to snapshot and the first one's claim is the only one there has ever been.
function sameCellFumbledTwice() {
    const engine = started();

    typeCorrectly(engine, 0, 2);

    typo(engine, 2);            // snapshots 2 against cell 2
    engine.processBackspace();  // erases it, caret back on cell 2
    typo(engine, 2);            // breaks a run of 0, on the SAME cell

    fix(engine, 2);

    return snapshot(engine);
}

// ComboRestoreTest.AnOffTimePressBetweenATypoAndItsFixKeepsTheClaim (backlog 199). Only a BREAK
// takes a claim away, and a right character struck outside the outermost Meh window is no longer a
// break: it earns no points, keeps the run, raises no combo break, and leaves the older cell
// redeemable. The C# pins the same keystrokes under OffTimeRule.BreaksCombo too, where the mistimed
// press loses the snapshot and the fix restores nothing at all; the browser has only the live arm
// (see `restorable` in typebeat-core.js), so the contrast here is against the numbers rather than
// against a second run.
//
// THE PRESS TIMES ARE THE BROWSER'S, NOT THE C# FIXTURE'S, for the reason CoreUntimedSpaceHarness
// records for its 5100: that fixture drives a bare TypingEngine judged on each cell's own point
// target, while the browser only ever plays live and judges a cell against its SYLLABLE's sung
// span. This line's spans are cells 0-2 over [1000, 2500] and cells 3-7 over [2500, 5000], so the
// off-time press is cell 3 struck at 4600 (cell 3 OPENS the second group, so since backlog 247 it is
// judged from that group's START: 2100 late, off the one ladder whose Meh bound is 600) and the
// ordinary press after it is cell 4 struck at 5500 (a non-opening cell, so it keeps the whole span
// and is 500 past its end, still on the ladder, an honest Meh). Same shape as the C# case, same
// tiers in the same order, and chronological.
//
// The follow-up press was written at 6700, which was an honest Meh against the old asymmetric
// ladder (MehLate 2000) and is off the end of the symmetric one. It moved with the ladder, because
// what the fixture needs is an ON-ladder press after an off-ladder one and not any particular
// number.
//
// The two presses sit one cell later than the C# fixture's for that reason alone: the hybrid gives
// an OPENING cell the only anchor early enough for an off-ladder press to be followed, in time, by
// an on-ladder one. Everything the case is about is unmoved, and the streak the typo breaks is 2
// rather than 1.
function offTimePressBetweenATypoAndItsFix() {
    const engine = started();

    typeCorrectly(engine, 0, 2);
    const comboBeforeTypo = engine.combo;

    typo(engine, 2);            // snapshots the run of 2 against cell 2

    engine.processKey(WORD[3], 4600);   // off the ladder: a hit worth nothing since backlog 199
    engine.processKey(WORD[4], 5500);   // still on it: an ordinary Meh
    const comboBeforeFix = engine.combo;

    // Read BEFORE the fix, which backspaces over both cells on its way to cell 2 and clears the
    // judgement each is displaying (their awarded judgements stand, in firstCorrectDelta).
    const offTimeJudgeType = engine.lines[0].cells[3].judgeType;
    const followUpJudgeType = engine.lines[0].cells[4].judgeType;

    fix(engine, 2);

    return Object.assign(snapshot(engine), {
        comboBeforeTypo: comboBeforeTypo,
        comboBeforeFix: comboBeforeFix,
        offTimeJudgeType: offTimeJudgeType,
        followUpJudgeType: followUpJudgeType
    });
}

// ---------------------------------------------------------------------------------------------
// Fixture 2: ComboRestoreTest's twoWordMap, for the skip sibling. One line, "abcd efg" on
// [1000, 8000): a@1000 b@2000 c@3000 d@4000 ' '@5000 e@5000 f@6000 g@7000. A word skip needs a word
// to give up on, which fixture 1 (one eight-letter word) has no room for.
// ---------------------------------------------------------------------------------------------
const TWO_WORD_OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"line","version":2,"song_end_ms":60000}\n' +
    '{"text":"abcd efg","start_ms":1000,"end_ms":8000,"words":[' +
    '{"text":"abcd","start_ms":1000,"end_ms":5000,"score":1},' +
    '{"text":"efg","start_ms":5000,"end_ms":8000,"score":1}]}\n';

// ComboRestoreTest.AWordSkipOverATypoLeavesThatTyposSnapshotAlone. The OTHER redeemable break under
// the same rule: type "ab", fumble 'c', give up on the word with a space (abandoning 'd'), then
// backspace into it and type both cells out. The skip's own break costs nothing, because the typo
// already zeroed the run, so it has no streak to claim the cell with and backlog 167's promise
// survives in the case it is worth most: the player who fumbles, gives up, then goes back and types
// the whole word out has undone everything they did wrong.
function wordSkippedOverATypo() {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(TWO_WORD_OSU), false);
    const engine = new TB.TypingEngine(beatmap);

    engine.spaceSkipsWord = true;
    engine.restored = [];
    engine.breaks = 0;
    engine.onComboRestored = (streak) => engine.restored.push(streak);
    engine.onComboBroken = () => { engine.breaks++; };

    engine.update(1000);

    engine.processKey('a', 1000);
    engine.processKey('b', 2000);
    const comboBeforeTypo = engine.combo;

    // The typo on 'c', which snapshots the run of 2 against cell 2.
    engine.processKey('z', 3000);

    // Space inside the word: 'd' is abandoned, on a run the typo has already zeroed.
    engine.processKey(' ', 4000);
    const caretAfterSkip = engine.caretIndex;

    // Back into the word (one press reclaims 'd' and erases the typo) and type it out.
    engine.processBackspace(); // erases the typed space
    engine.processBackspace(); // steps over 'd', erases the typo
    const caretAfterBackspaces = engine.caretIndex;

    engine.processKey('c', 3000);
    engine.processKey('d', 4000);

    return Object.assign(snapshot(engine), {
        comboBeforeTypo: comboBeforeTypo,
        caretAfterSkip: caretAfterSkip,
        caretAfterBackspaces: caretAfterBackspaces
    });
}

// ---------------------------------------------------------------------------------------------
// Fixture 3: TypeBeatReplayScorerTest's map, transcribed. One twelve-cell word on [0, 240000] with
// its line running to 300000, plus a one-cell second line. Thirteen cells, every one strikeable
// dead on its target, which is what makes 13 vs 11 a clean read on the rule alone.
// ---------------------------------------------------------------------------------------------
const REPLAY_WORD = 'abcdefghijkl';
const LINE_ZERO_END = 300000;

const REPLAY_OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"line","version":2,"song_end_ms":603000}\n' +
    '{"text":"abcdefghijkl","start_ms":0,"end_ms":240000,"words":[' +
    '{"text":"abcdefghijkl","start_ms":0,"end_ms":240000,"score":1}]}\n' +
    '{"text":"z","start_ms":300000,"end_ms":400000,"words":[' +
    '{"text":"z","start_ms":300000,"end_ms":400000,"score":1}]}\n';

function replayTarget(cellIndex) { return 20000 * cellIndex; }

/**
 * Plays the thirteen-cell map: every cell on its own target, with an optional typo-and-fix on
 * `typoAt` (press the wrong key, backspace, press the right one, all at that cell's target, which
 * is the replay's own frame sequence).
 */
function playReplayShaped(typoAt) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(REPLAY_OSU), false);
    const engine = new TB.TypingEngine(beatmap);
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;

    for (let i = 0; i < REPLAY_WORD.length; i++) {
        const t = replayTarget(i);
        engine.update(t);

        if (i === typoAt) {
            engine.processKey('q', t); // wrong
            engine.processBackspace();
        }

        engine.processKey(REPLAY_WORD[i], t);
    }

    engine.update(LINE_ZERO_END); // line 0 seals (nothing untyped), line 1 activates
    engine.processKey('z', LINE_ZERO_END);

    engine.update(1000000);
    const score = TB.computeScore(engine);

    return {
        totalCells: beatmap.totalCells,
        engineMaxCombo: engine.maxCombo,
        maxCombo: score.maxCombo,
        statistics: score.statistics,
        maximumStatistics: score.maximumStatistics,
        totalScore: score.totalScore,
        accuracy: score.accuracy,
        completion: score.completion,
        rank: score.rank
    };
}

const out = {
    fixResumesTheStreak: fixResumesTheStreak(),
    interveningBreak: interveningBreak(),
    repeatedCycles: repeatedCycles(),
    restoreBeyondTheWatermark: restoreBeyondTheWatermark(),

    // The backlog 176 shapes: an empty break never takes a live claim.
    adjacentTyposBothFixed: adjacentTyposBothFixed(),
    sameCellFumbledTwice: sameCellFumbledTwice(),
    wordSkippedOverATypo: wordSkippedOverATypo(),

    // The backlog 199 shape: an off-time press is not a break, so it takes no claim either.
    offTimePressBetweenATypoAndItsFix: offTimePressBetweenATypoAndItsFix(),

    // The replay-scorer pair: the same thirteen cells, clean and with a fixed typo on cell 2.
    replayClean: playReplayShaped(-1),
    replayFixedTypo: playReplayShaped(2)
};

process.stdout.write(JSON.stringify(out));
