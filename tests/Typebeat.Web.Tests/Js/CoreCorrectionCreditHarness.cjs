// Node harness for the CORRECTION CAP in the browser scoring core (backlog 210): a cell that held a
// wrong character before it was ever judged resolves at min(the retype's own tier, Ok).
//
// Shaped like CoreComboRestoreHarness rather than like the transcribed-literal harnesses, and for the
// same reason it was: the divergence this guards is a BEHAVIOUR one on a SHARED leaderboard, so a
// golden literal transcribed from a game that did not cap yet would keep passing while the browser
// went on paying a fixed typo the full 300. So this file drives the browser through the wrong/fix
// SEQUENCES themselves and reports what the engine announced, counted and submitted.
//
// The sequences are the game's own pins (NonVisual/CorrectionCreditTest.cs): the min() over the
// ladder, the announced tier being the stored one, the cap being a STATE so repeated cycles cap
// once, a cell judged clean before it was ever spoiled keeping its clean judgement, the combo
// restore being left alone, an off-time fix being left to the off-time rule, and the whole-account
// claim that the cap moves accuracy and total_score and nothing else.
//
// THE PRESS TIMES ARE THE BROWSER'S, NOT THE C# FIXTURE'S, exactly as CoreComboRestoreHarness
// records: that fixture drives a bare TypingEngine judged on each cell's own point target, while the
// browser only ever plays live and judges a cell against its SYLLABLE's sung span. This line's spans
// are cells 0-2 over [1000, 2500] and cells 3-7 over [2500, 5000], so the ladder can only be walked
// from the anchor the live rule gives the cell every case here spoils and fixes, cell 3.
//
// THAT ANCHOR IS THE SPAN'S START SINCE BACKLOG 247, and it used to be the span's end. Cell 3 OPENS
// the second group, and the first cell of a group is now judged on the signed distance from
// StartTime rather than paid 0 anywhere inside the span, so the offsets below run from 2500 where
// they used to run from 5000. Every number this file emits is unchanged by the move, because an
// offset past the anchor is the same delta either way: 2700 is a Great, 3200 an Ok, 4000 a Meh and
// 5000 off the ladder on the Line windows (Great [-250, 400], Ok [-600, 1000], Meh [-1200, 2000]),
// exactly as 5200 / 5700 / 6500 / 7500 were against the old edge.
//
// There is only ONE arm here, the capped one. The C# pins each shape twice, once under
// CorrectionCreditRule.Capped and once under Full (the rule every row stored before backlog 210 was
// played under), and the browser has no counterpart to Full: it plays live, writes no replay frames
// and re-derives no stored row. So the contrast is drawn against a CLEAN cell struck at the same
// moment instead, which is the same comparison the C# draws between its two arms.
//
// Usage: node CoreCorrectionCreditHarness.cjs <absolute path to typebeat-core.js>

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
// CorrectionCreditTest's map: one line, "abcdefgh", eight cells over [1000, 5000], so cell i targets
// exactly 1000 + 500i. Line granularity, matching the C# fixture's TimingGranularity.Line.
// ---------------------------------------------------------------------------------------------
const WORD = 'abcdefgh';
const SPAN_START = 2500; // the start of cell 3's sung span, the anchor its OPENING cell is judged
                         // on since backlog 247, and the edge every offset below is off
const SONG_END = 20000;

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

/** The moment a press on cell 3 lands `offsetMs` past the start of the span it opens. */
function pastAnchor(offsetMs) { return SPAN_START + offsetMs; }

function started() {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(WORD_OSU), false);
    const engine = new TB.TypingEngine(beatmap);
    // Authored on the skip-OFF arm; declared since backlog 198 flipped the engine default to skip-on.
    engine.spaceSkipsWord = false;

    engine.restored = [];
    engine.announced = [];
    engine.onComboRestored = (streak) => engine.restored.push(streak);
    engine.onCharJudged = (index, type) => engine.announced.push(type);

    engine.update(1000);
    return engine;
}

/** Type cells [from, to) correctly, each dead inside its own span. */
function typeCorrectly(engine, from, to) {
    for (let i = from; i < to; i++) engine.processKey(WORD[i], target(i));
}

/** Type a wrong char into the cell the caret is on (which must be cellIndex). */
function typo(engine, cellIndex) {
    if (engine.caretIndex !== cellIndex) throw new Error(`caret is ${engine.caretIndex}, expected ${cellIndex}`);
    engine.processKey(WRONG_KEY, target(cellIndex));
}

/** Backspace onto cellIndex and type it correctly, at `at`. */
function fix(engine, cellIndex, at) {
    while (engine.caretIndex > cellIndex) engine.processBackspace();
    engine.processKey(WORD[cellIndex], at);
}

/** The tier the last press was ANNOUNCED as, which is what the stage shows. */
function lastAnnounced(engine) { return engine.announced[engine.announced.length - 1]; }

// CorrectionCreditTest.TheCapIsAMinimumOverTheLadder, and its off-time sibling
// (AnOffTimeFixIsLeftToTheOffTimeRule). Both halves of the min() are reported for one offset: what a
// CLEAN cell struck at that moment is worth, and what a CORRECTED one is. The clean arm is this
// file's stand-in for the C#'s CorrectionCreditRule.Full.
function ladder(offsetMs) {
    const clean = started();
    typeCorrectly(clean, 0, 3);
    clean.processKey(WORD[3], pastAnchor(offsetMs));

    const corrected = started();
    typeCorrectly(corrected, 0, 3);
    typo(corrected, 3);
    fix(corrected, 3, pastAnchor(offsetMs));

    return {
        offsetMs: offsetMs,
        uncapped: lastAnnounced(clean),
        capped: lastAnnounced(corrected),
        // The DELTA is untouched by the cap, which is why it costs accuracy and nothing else: the
        // sync tint and typebeat-player.js's live sync percent read this field back.
        uncappedDelta: clean.lines[0].cells[3].judgedDelta,
        cappedDelta: corrected.lines[0].cells[3].judgedDelta
    };
}

// CorrectionCreditTest.TheAnnouncedJudgementIsTheStoredOne. The cap moves the TIER, so what the stage
// announces and what the cell stores are the same thing by construction. A fix struck dead inside the
// span is a Great before the cap and an Ok after it.
function announcedIsStored() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    typo(engine, 3);
    fix(engine, 3, SPAN_START);

    return {
        announced: lastAnnounced(engine),
        cellJudgeType: engine.lines[0].cells[3].judgeType,
        cellState: engine.lines[0].cells[3].state,
        judgedDelta: engine.lines[0].cells[3].judgedDelta,
        counts: engine.counts
    };
}

// CorrectionCreditTest.TheCapIsAStateSoRepeatedCyclesCapOnce. Wrong, fix, wrong, fix on ONE cell caps
// exactly once. The second fix is a scoring-inert retype (the cell was already judged), so nothing
// new is counted, and the tier it ANNOUNCES has to be the capped Ok the cell actually stored.
function repeatedCyclesCapOnce() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    typo(engine, 3);
    fix(engine, 3, SPAN_START);

    const announcedAfterFirstFix = lastAnnounced(engine);

    engine.processBackspace();
    typo(engine, 3);
    fix(engine, 3, SPAN_START);

    return {
        announcedAfterFirstFix: announcedAfterFirstFix,
        announced: lastAnnounced(engine),
        counts: engine.counts,
        mistypes: engine.mistypes
    };
}

// CorrectionCreditTest.TwoWrongCharactersOnOneCellAreStillOneCap. Wrong, backspace, wrong AGAIN, then
// fix: still one cap, because the rule is a flag on the cell and not a counter.
function twoWrongCharactersOnOneCell() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    typo(engine, 3);
    engine.processBackspace();
    typo(engine, 3);
    fix(engine, 3, SPAN_START);

    return { announced: lastAnnounced(engine), counts: engine.counts, mistypes: engine.mistypes };
}

// CorrectionCreditTest.ACellJudgedCleanBeforeItEverHeldWrongKeepsItsCleanJudgement. The flag is set
// only while the cell is still UNJUDGED, so a cell typed correctly, backspaced into, spoiled and
// retyped keeps the judgement it earned honestly: the retype is inert and there is nothing for the
// cap to govern.
function cleanBeforeWrongKeepsItsCleanJudgement() {
    const engine = started();

    typeCorrectly(engine, 0, 4); // cell 3 is judged Great, cleanly
    const announcedWhenClean = lastAnnounced(engine);

    engine.processBackspace();
    typo(engine, 3);
    fix(engine, 3, SPAN_START);

    return {
        announcedWhenClean: announcedWhenClean,
        announced: lastAnnounced(engine),
        counts: engine.counts
    };
}

// CorrectionCreditTest.TheCapLeavesTheComboRestoreAlone. Backlog 140 is untouched: the streak the
// wrong keypress broke is still resumed by the fix, and the resume still lands BEFORE the retype is
// judged, so the capped press is priced at the restored run.
function comboRestoreIsUntouched() {
    const engine = started();

    typeCorrectly(engine, 0, 3);
    typo(engine, 3);
    typeCorrectly(engine, 4, 6);
    fix(engine, 3, SPAN_START);

    return {
        restored: engine.restored,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        processorCombo: engine.processor.combo,
        processorHighestCombo: engine.processor.highestCombo,
        announced: lastAnnounced(engine)
    };
}

// ---------------------------------------------------------------------------------------------
// The whole SUBMITTED account, which is where the cap either costs what it is meant to cost or
// quietly costs something else. Three runs over the same eight cells, every press dead inside its
// own span so the only thing separating them is what happens to cell 3:
//   clean   nothing happens to it
//   fixed   spoiled and corrected, so it is capped at Ok
//   unfixed spoiled and left, so it seals as the unfixed typo
// which is the per-cell ordering clean 300 > corrected 100 > unfixed typo 50 read off a real run.
// ---------------------------------------------------------------------------------------------
function account(kind) {
    const engine = started();

    typeCorrectly(engine, 0, 3);

    if (kind !== 'clean') {
        typo(engine, 3);

        if (kind === 'fixed') fix(engine, 3, SPAN_START);
    }

    // The clean run has not touched cell 3 yet; the other two have moved past it (the typo advanced
    // the caret, and the fix put it back where the typo left it).
    typeCorrectly(engine, kind === 'clean' ? 3 : 4, WORD.length);

    engine.update(SONG_END);
    const score = TB.computeScore(engine);

    return {
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
    // The min() over the ladder, walked from the edge of cell 3's span.
    // Walked with the ladder: the offsets were 0 / 200 / 700 / 1500 / 2500 against the old
    // asymmetric three-tier one, and these are the same five positions on the symmetric
    // 150 / 300 / 600 that replaced it (inside Great, inside Great, inside Ok, inside Meh, off).
    ladderDeadOn: ladder(0),
    ladderGreat: ladder(100),
    ladderOk: ladder(200),
    ladderMeh: ladder(450),
    ladderOffTime: ladder(900),

    announcedIsStored: announcedIsStored(),

    // The cap is a STATE, not a counter.
    repeatedCyclesCapOnce: repeatedCyclesCapOnce(),
    twoWrongCharactersOnOneCell: twoWrongCharactersOnOneCell(),
    cleanBeforeWrongKeepsItsCleanJudgement: cleanBeforeWrongKeepsItsCleanJudgement(),

    // What the cap deliberately does NOT move.
    comboRestoreIsUntouched: comboRestoreIsUntouched(),

    accountClean: account('clean'),
    accountFixed: account('fixed'),
    accountUnfixed: account('unfixed')
};

process.stdout.write(JSON.stringify(out));
