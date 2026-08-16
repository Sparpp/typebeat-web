// Node harness for the RECLAIMABLE WORD SKIP in the browser scoring core (backlog 167, web half
// 168). A space pressed inside a word abandons the rest of it into a PHANTOM state instead of
// missing it on the spot: one backspace re-enters the word, re-typing the cells earns their
// ordinary judgements and the streak the skip broke, and a skip nobody goes back for resolves at
// the seal as the misses it turned out to be, costing exactly what it always cost.
//
// Shaped like CoreComboRestoreHarness.cjs rather than like the older parity harnesses, and for the
// same reason: a golden literal transcribed from a game that did not yet reclaim would still pass
// against a browser that does not reclaim either. So this file drives the browser through the
// SEQUENCES from the game's own spec, NonVisual/SpaceSkipWordTest.cs, and the C# test's asserted
// numbers are the pins on the other side (see WordSkipParityTest.cs).
//
// The two fixtures are that file's own, transcribed as the JSON a real map carries:
//   catDog  "cat dog" on [1000, 6000), sing end 5000, units cat [1000, 3000] and dog [3000, 5000].
//           Cells c@1000 a@1666.67 t@2333.33 ' '@3000 d@3000 o@3666.67 g@4333.33. A three-letter
//           word is the shortest one that can lose MORE than one cell to a skip.
//   abCd    "ab cd" on [1000, 4000), sing end 3000: a@1000 b@1500 ' '@2000 c@2000 d@2500.
//
// A third fixture (abCdOverrun) is the game's line-held-open case rebuilt around the LOADER rather
// than transcribed. The C# fixture hands TypingLine a LyricLine whose word units run past the
// line's own end with SealGraceMs left at 0, which no real map produces: both loaders clamp a word
// to the line (TimingJsonLoader.buildExplicitUnits, and buildExplicitUnits here), so feeding that
// JSON to either side gives a 700 ms overrun grace and a last cell at 2500, not the C# fixture's
// 250 ms bump and a cell on the boundary. The PROPERTY under test is identical: an abandoned cell
// must hold its line open through the grace, or the early seal ("nothing left to type") shuts the
// reclaim window in the very window that exists for finishing.
//
// Usage: node CoreWordSkipHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------
function osu(line, songEndMs) {
    return '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n' +
        JSON.stringify({ granularity: 'line', version: 2, song_end_ms: songEndMs }) + '\n' +
        JSON.stringify(line) + '\n';
}

const CAT_DOG = osu({
    text: 'cat dog', start_ms: 1000, end_ms: 5000,
    words: [{ text: 'cat', start_ms: 1000, end_ms: 3000, score: 1 },
            { text: 'dog', start_ms: 3000, end_ms: 5000, score: 1 }]
}, 6000);

const AB_CD = osu({
    text: 'ab cd', start_ms: 1000, end_ms: 3000,
    words: [{ text: 'ab', start_ms: 1000, end_ms: 2000, score: 1 },
            { text: 'cd', start_ms: 2000, end_ms: 3000, score: 1 }]
}, 4000);

// "cat dog" with a SECOND line after it, "hi" on [6000, 10000): h@6000, i@6500. The only fixture
// where a line seals on abandoned cells while the play CARRIES ON, which is the one shape in which
// the seal's combo-neutral marks are observable at all. In every single-line run the seal is the
// last thing that happens, so a Miss zeroing the submitted combo there costs nothing: max_combo is
// already banked and no judgement follows to be weighted by the wreckage.
const CAT_DOG_THEN_HI =
    '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n' +
    JSON.stringify({ granularity: 'line', version: 2, song_end_ms: 10000 }) + '\n' +
    JSON.stringify({
        text: 'cat dog', start_ms: 1000, end_ms: 5000,
        words: [{ text: 'cat', start_ms: 1000, end_ms: 3000, score: 1 },
                { text: 'dog', start_ms: 3000, end_ms: 5000, score: 1 }]
    }) + '\n' +
    JSON.stringify({
        text: 'hi', start_ms: 6000, end_ms: 7000,
        words: [{ text: 'hi', start_ms: 6000, end_ms: 7000, score: 1 }]
    }) + '\n';

// The vocals overrun the line boundary, so the line carries a finishing grace to come back into.
const AB_CD_OVERRUN = osu({
    text: 'ab cd', start_ms: 1000, end_ms: 4000,
    words: [{ text: 'ab', start_ms: 1000, end_ms: 2000, score: 1 },
            { text: 'cd', start_ms: 2000, end_ms: 4000, score: 1 }]
}, 3000);

// catDog cell targets, named as the C# fixture names them.
const A_TARGET = 1000 + 2000 / 3;
const T_TARGET = 1000 + 2 * 2000 / 3;
const O_TARGET = 3000 + 2000 / 3;
const G_TARGET = 3000 + 2 * 2000 / 3;

/** A started engine with the skip setting on, plus the recorders the C# test attaches. */
function started(source, spaceSkipsWord = true) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(source), false);
    const engine = new TB.TypingEngine(beatmap);

    engine.spaceSkipsWord = spaceSkipsWord;

    engine.restored = [];
    engine.breaks = 0;
    engine.rejected = [];
    engine.onComboRestored = (streak) => engine.restored.push(streak);
    engine.onComboBroken = () => { engine.breaks++; };
    engine.onWrongKey = (c) => engine.rejected.push(c);

    engine.update(1000);
    return engine;
}

/** The whole of "cat dog" typed in order, every cell dead on its target. */
function typeItAll(engine) {
    engine.processKey('c', 1000);
    engine.processKey('a', A_TARGET);
    engine.processKey('t', T_TARGET);
    engine.processKey(' ', 3000);
    engine.processKey('d', 3000);
    engine.processKey('o', O_TARGET);
    engine.processKey('g', G_TARGET);
}

function states(engine) { return engine.lines[0].cells.map(c => c.state); }
function judgeTypes(engine) { return engine.lines[0].cells.map(c => c.judgeType); }

/**
 * Everything both accounts hold. The engine's own `score` / `combo` / `maxCombo` / `liveAccuracy`
 * are what ResultsSummary reports on the desktop and what the HUD shows; `processor*` is the
 * osu-side account, whose `highestCombo` is submitted as max_combo and whose `counts.miss` is the
 * submitted miss count. The engine's own `counts` dict is deliberately NOT read for misses: it is
 * the scored-keypress dict here, and this mirror has never recorded seal misses in it.
 */
function snapshot(engine) {
    return {
        states: states(engine),
        judgeTypes: judgeTypes(engine),
        caretIndex: engine.caretIndex,
        activeLineIndex: engine.activeLineIndex,
        finished: engine.finished,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        score: engine.score,
        accuracy: engine.liveAccuracy,
        mistypes: engine.mistypes,
        breaks: engine.breaks,
        restored: engine.restored,
        rejected: engine.rejected,
        great: engine.counts.Great || 0,
        ok: engine.counts.Ok || 0,
        processorCombo: engine.processor.combo,
        processorHighestCombo: engine.processor.highestCombo,
        processorMisses: engine.processor.counts.miss,
        processorGreats: engine.processor.counts.great,
        judgementCount: engine.processor.judgementCount
    };
}

/** The SUBMITTED account, once the map has been played out and every line sealed. */
function submitted(engine) {
    const score = TB.computeScore(engine);
    return {
        totalCells: engine.beatmap.totalCells,
        maxCombo: score.maxCombo,
        engineMaxCombo: engine.maxCombo,
        totalScore: score.totalScore,
        accuracy: score.accuracy,
        completion: score.completion,
        rank: score.rank,
        statistics: score.statistics,
        maximumStatistics: score.maximumStatistics
    };
}

// ---------------------------------------------------------------------------------------------
// SpaceSkipWordTest.SpaceInsideAWordIsStillRejectedWhenTheSettingIsOff. The default path, and so
// also the pin that nothing here reaches a run with the setting off.
// ---------------------------------------------------------------------------------------------
function settingOff() {
    const engine = started(CAT_DOG, false);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600);

    return snapshot(engine);
}

// SpaceSkipWordTest.SpaceInsideAWordAbandonsTheRestOfItAndLandsOnTheNextWord. The feature itself:
// the rest of the word goes into the phantom state and the caret lands on the next word, with the
// word gap judged exactly like a typed space. NOTHING is resolved yet.
function skipAbandonsTheWord() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);  // Great, 300 * (1 + 0/50) = 300
    engine.processKey(' ', 2600);  // caret on 'a': abandon "at", the space lands on the gap

    const afterSkip = snapshot(engine);

    // Typing carries straight on from the next word.
    engine.update(3000);
    engine.processKey('d', 3000);

    return Object.assign(afterSkip, { stateOfDAfterTheNextPress: engine.lines[0].cells[4].state });
}

// SpaceSkipWordTest.AWrongCharInTheAbandonedWordIsNotGivenUp. A cell the player FINISHED is not
// given up, and since backlog 124 that group is the correct cells AND the wrong ones.
function wrongCharIsNotGivenUp() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey('x', A_TARGET); // typed through onto 'a'
    engine.processKey(' ', 2600);     // caret on 't': abandon "at"

    return Object.assign(snapshot(engine), { typedCharOfCellOne: engine.lines[0].cells[1].typedChar });
}

// SpaceSkipWordTest.ACellTypedCorrectlyAndThenBackspacedIsGivenUpLikeAnyUntypedCell.
function correctThenBackspacedIsGivenUp() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey('a', A_TARGET);
    const backspaced = engine.processBackspace();
    const caretAfterBackspace = engine.caretIndex;

    engine.processKey(' ', 2600);

    return Object.assign(snapshot(engine), {
        backspaced: backspaced,
        caretAfterBackspace: caretAfterBackspace
    });
}

// SpaceSkipWordTest.SpaceOnAWordGapIsUnchanged. A space pressed ON the gap is simply typed.
function spaceOnAWordGap() {
    const engine = started(AB_CD);

    engine.processKey('a', 1000); // Great, 300
    engine.processKey('b', 1500); // Great, 300 * 1.02 = 306
    engine.processKey(' ', 2000); // ON the gap: Great, 300 * 1.04 = 312

    return snapshot(engine);
}

// SpaceSkipWordTest.SkippingTheLastWordOfALineCompletesTheLine. The cells resolve at the seal, as
// the misses they turned out to be, and WITHOUT a second combo break.
function skippingTheLastWordOfALine() {
    const engine = started(AB_CD);

    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey(' ', 2000);
    engine.processKey(' ', 2100); // caret on 'c': the whole last word goes

    const afterSkip = snapshot(engine);

    engine.update(4000); // the line seals

    return {
        afterSkip: afterSkip,
        afterSeal: snapshot(engine),
        submitted: submitted(engine)
    };
}

// SpaceSkipWordTest.TheSkipWorksUnderGatekeeperToo. Orthogonal flags: one decides what happens to a
// wrong LETTER, the other lets you abandon a WORD.
function underGatekeeper() {
    const engine = started(CAT_DOG);
    engine.allowWrongInput = false;

    engine.processKey('c', 1000);
    engine.processKey('q', 1600); // wrong letter: rejected, caret holds on 'a'
    const caretAfterRejection = engine.caretIndex;
    const streakAfterRejection = engine.consecutiveWrongKeys;

    engine.processKey(' ', 2600); // ...and space gets them out of it

    return Object.assign(snapshot(engine), {
        caretAfterRejection: caretAfterRejection,
        streakAfterRejection: streakAfterRejection,
        consecutiveWrongKeys: engine.consecutiveWrongKeys
    });
}

// SpaceSkipWordTest.MashingLeavesNothingToSkip.
function mashingLeavesNothingToSkip() {
    const engine = started(CAT_DOG);
    engine.mashingEnabled = true;

    engine.processKey('c', 1000);
    engine.processKey(' ', A_TARGET); // judged as 'a', the expected char

    return Object.assign(snapshot(engine), { typedCharOfCellOne: engine.lines[0].cells[1].typedChar });
}

// ---------------------------------------------------------------------------------------------
// Backlog 167: the skipped word is RE-TYPEABLE.
// ---------------------------------------------------------------------------------------------

// SpaceSkipWordTest.OneBackspaceFromTheGapReOpensTheWholeSkippedWord. THE PROPERTY: one backspace
// crosses the whole abandoned run and lands the caret on the last character actually typed.
function oneBackspaceReOpensTheWord() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600);

    const offTheGap = engine.processBackspace(); // an ordinary erase of the typed gap
    const caretOffTheGap = engine.caretIndex;

    const throughTheRun = engine.processBackspace(); // the one under test

    return Object.assign(snapshot(engine), {
        offTheGap: offTheGap,
        caretOffTheGap: caretOffTheGap,
        throughTheRun: throughTheRun
    });
}

// SpaceSkipWordTest.RetypingAReclaimedWordEarnsRealJudgements. The cells really are earnable again:
// ordinary judgements with ordinary points, not the scoring-inert retype of an already-earned cell.
function retypingEarnsRealJudgements() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600);
    engine.processBackspace();
    engine.processBackspace();

    const scoreBefore = engine.score;

    engine.processKey('c', 1000);     // inert: this cell was already earned
    const scoreAfterInert = engine.score;

    engine.processKey('a', A_TARGET); // the first abandoned cell, earned for real
    const scoreAfterFirstReclaim = engine.score;

    engine.processKey('t', T_TARGET);

    return Object.assign(snapshot(engine), {
        pointsForTheInertRetype: scoreAfterInert - scoreBefore,
        pointsForTheFirstReclaimedCell: scoreAfterFirstReclaim - scoreAfterInert
    });
}

// SpaceSkipWordTest.AReclaimedSkipGivesTheComboBackToWhereItWouldHaveBeen. Typing the line out after
// a skip and a full reclaim ends on exactly the combo, and the exact max combo, a straight run has.
function reclaimedSkipGivesTheComboBack() {
    const straight = started(CAT_DOG);
    typeItAll(straight);

    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600); // skip "at": one break, the streak of 1 snapshotted on 'a'
    const restoredAfterTheSkip = engine.restored.slice();

    engine.processBackspace();    // off the gap
    engine.processBackspace();    // through the abandoned run, onto 'c'
    engine.processKey('c', 1000); // inert retype
    const restoredAfterTheErase = engine.restored.slice();

    engine.processKey('a', A_TARGET); // the snapshot cell: the run resumes here
    const restoredAfterTheSnapshotCell = engine.restored.slice();

    engine.processKey('t', T_TARGET);
    engine.processKey(' ', 3000);     // inert retype of the gap
    engine.processKey('d', 3000);
    engine.processKey('o', O_TARGET);
    engine.processKey('g', G_TARGET);

    return {
        straight: snapshot(straight),
        reclaimed: Object.assign(snapshot(engine), {
            restoredAfterTheSkip: restoredAfterTheSkip,
            restoredAfterTheErase: restoredAfterTheErase,
            restoredAfterTheSnapshotCell: restoredAfterTheSnapshotCell
        })
    };
}

// SpaceSkipWordTest.TheFirstWordOfALineIsReclaimableToo. A word abandoned at the very START of a
// line has no keypress behind it, and the ordinary "nothing to erase" answer would make it the one
// unreclaimable word on the map.
function firstWordOfALineIsReclaimable() {
    const engine = started(CAT_DOG);

    engine.processKey(' ', 1100); // nothing typed at all: the whole of "cat" goes
    const caretAfterSkip = engine.caretIndex;

    const offTheGap = engine.processBackspace();
    const caretOffTheGap = engine.caretIndex;

    const reclaim = engine.processBackspace(); // a reclaim IS a state change
    const caretAfterReclaim = engine.caretIndex;
    const statesAfterReclaim = states(engine);

    engine.processKey('c', 1000);

    return Object.assign(snapshot(engine), {
        caretAfterSkip: caretAfterSkip,
        offTheGap: offTheGap,
        caretOffTheGap: caretOffTheGap,
        reclaim: reclaim,
        caretAfterReclaim: caretAfterReclaim,
        statesAfterReclaim: statesAfterReclaim
    });
}

// SpaceSkipWordTest.BackspaceAtTheHeadOfALineIsStillInert. The pin that the reclaim branch did not
// widen "nothing to erase" for everyone else.
function backspaceAtTheHeadIsInert() {
    const engine = started(CAT_DOG);

    return { erased: engine.processBackspace(), caretIndex: engine.caretIndex };
}

// SpaceSkipWordTest.EveryAbandonedCellLeavesThePhantomStateExactlyOnce, read off the CELLS rather
// than off events (this mirror raises none of the three): skip "cat", come back for it, then skip
// "dog" and never return. No cell may still be phantom after the seal, and only the word nobody
// came back for is missed.
function everyAbandonedCellLeavesExactlyOnce() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600);
    engine.processBackspace();
    engine.processBackspace();
    engine.processKey('c', 1000);
    engine.processKey('a', A_TARGET);
    engine.processKey('t', T_TARGET);
    engine.processKey(' ', 3000);
    engine.processKey('d', 3000);
    engine.processKey(' ', 3800); // abandon the rest of "dog"

    const beforeSeal = states(engine);

    engine.update(6000);

    return Object.assign(snapshot(engine), {
        statesBeforeSeal: beforeSeal,
        submitted: submitted(engine)
    });
}

// SpaceSkipWordTest.AnAbandonedCellHoldsTheLineOpenLikeAnUntypedOne, on the loader-faithful fixture
// (see the header). Without it the early seal closes the reclaim window inside the very grace that
// exists for finishing.
function abandonedCellHoldsTheLineOpen() {
    const engine = started(AB_CD_OVERRUN);

    engine.processKey('a', 1000);
    engine.processKey('b', 1500);
    engine.processKey(' ', 2000);
    engine.processKey(' ', 2100); // abandon "cd", the rest of the line

    engine.update(3100); // past the deadline, inside the grace

    const activeInsideTheGrace = engine.activeLineIndex;

    const reclaim = engine.processBackspace();
    engine.processKey(' ', 3100);
    engine.processKey('c', 3100);

    const stateOfC = engine.lines[0].cells[3].state;

    engine.update(3800); // ...and the grace is still bounded

    return Object.assign(snapshot(engine), {
        sealGraceMs: engine.lines[0].sealGraceMs,
        activeInsideTheGrace: activeInsideTheGrace,
        reclaim: reclaim,
        stateOfC: stateOfC
    });
}

// SpaceSkipWordTest.ASkipNeverReturnedToCostsWhatItAlwaysCost, as a SUBMITTED account: the pin the
// whole design rests on. The same keystrokes the pre-167 browser played, played out to the seal.
function skipNeverReturnedTo() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600); // abandon "at", never come back
    engine.processKey('d', 3000);
    engine.processKey('o', O_TARGET);
    engine.processKey('g', G_TARGET);
    engine.update(6000);

    return Object.assign(snapshot(engine), { submitted: submitted(engine) });
}

// A skip nobody returns to on a line the play CARRIES ON PAST. The seal resolves the abandoned
// cells as Misses while the player is still holding the run they rebuilt after the skip, so the
// combo-neutral marks are load-bearing here and nowhere else: without them the submitted combo is
// wiped a second time for a break already taken, and every judgement on the next line is weighted
// by the wreckage.
function skipThenTheNextLine() {
    const engine = started(CAT_DOG_THEN_HI);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600); // abandon "at", never come back
    engine.processKey('d', 3000);
    engine.processKey('o', O_TARGET);
    engine.processKey('g', G_TARGET);

    engine.update(6000); // line 0 seals on the two phantom cells, and line 1 opens

    const afterSeal = snapshot(engine);

    engine.processKey('h', 6000);
    engine.processKey('i', 6500);
    engine.update(11000);

    return Object.assign(snapshot(engine), {
        afterSeal: afterSeal,
        statesLineOne: engine.lines[1].cells.map(c => c.state),
        submitted: submitted(engine)
    });
}

// The same map typed straight through, which is what a FULLY reclaimed skip has to be worth: the
// reference the two runs above are read against.
function cleanRun() {
    const engine = started(CAT_DOG);
    typeItAll(engine);
    engine.update(6000);

    return Object.assign(snapshot(engine), { submitted: submitted(engine) });
}

// A skip taken and fully reclaimed, played out to the seal, as a submitted account.
function fullyReclaimedRun() {
    const engine = started(CAT_DOG);

    engine.processKey('c', 1000);
    engine.processKey(' ', 2600);
    engine.processBackspace();
    engine.processBackspace();
    engine.processKey('c', 1000);
    engine.processKey('a', A_TARGET);
    engine.processKey('t', T_TARGET);
    engine.processKey(' ', 3000);
    engine.processKey('d', 3000);
    engine.processKey('o', O_TARGET);
    engine.processKey('g', G_TARGET);
    engine.update(6000);

    return Object.assign(snapshot(engine), { submitted: submitted(engine) });
}

const out = {
    settingOff: settingOff(),
    skipAbandonsTheWord: skipAbandonsTheWord(),
    wrongCharIsNotGivenUp: wrongCharIsNotGivenUp(),
    correctThenBackspacedIsGivenUp: correctThenBackspacedIsGivenUp(),
    spaceOnAWordGap: spaceOnAWordGap(),
    skippingTheLastWordOfALine: skippingTheLastWordOfALine(),
    underGatekeeper: underGatekeeper(),
    mashingLeavesNothingToSkip: mashingLeavesNothingToSkip(),

    oneBackspaceReOpensTheWord: oneBackspaceReOpensTheWord(),
    retypingEarnsRealJudgements: retypingEarnsRealJudgements(),
    reclaimedSkipGivesTheComboBack: reclaimedSkipGivesTheComboBack(),
    firstWordOfALineIsReclaimable: firstWordOfALineIsReclaimable(),
    backspaceAtTheHeadIsInert: backspaceAtTheHeadIsInert(),
    everyAbandonedCellLeavesExactlyOnce: everyAbandonedCellLeavesExactlyOnce(),
    abandonedCellHoldsTheLineOpen: abandonedCellHoldsTheLineOpen(),

    skipNeverReturnedTo: skipNeverReturnedTo(),
    skipThenTheNextLine: skipThenTheNextLine(),
    cleanRun: cleanRun(),
    fullyReclaimedRun: fullyReclaimedRun()
};

process.stdout.write(JSON.stringify(out));
