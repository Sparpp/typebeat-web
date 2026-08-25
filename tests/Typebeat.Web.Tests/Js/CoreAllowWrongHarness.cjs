// Node harness for the DEFAULT wrong-key model in the browser scoring core (backlog 107): a wrong
// (non-space) character is TYPED THROUGH into the cell and can be backspaced, instead of being
// rejected. The desktop client flipped its default the same way, and strict rejection survives only
// as the Gatekeeper mod, which the browser has no way to select. So this is what every /play score
// is now judged under, on the SAME leaderboards as desktop scores.
//
// CoreMistypeHarness.cjs is the sibling fixture for the rejection model (it sets allowWrongInput
// off explicitly). Since backlog 109 the two models account for the KEYPRESS identically (a combo
// break plus a mistype, and no judgement result either way); what still differs is the CELL, which
// rejection leaves waiting for the player and the default model consumes. Since backlog 124 a typo
// left alone resolves that cell as its OWN result at the seal rather than a miss, and since backlog
// 126 that result is the 'good' key and is NOT counted as a typed cell: the player finished the
// character but got it wrong, so it costs accuracy and the miss count stays clean, while completion
// and rank fall exactly as a miss makes them fall.
//
// Usage: node CoreAllowWrongHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const path = process.argv[2];
if (!path) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(path);
const TB = global.window.TypeBeatCore;

// The same 15-cell map the mistype harness uses, so the two fixtures are directly comparable.
// "the bad cat sat": 12 letters and 3 word gaps, no 'z' anywhere, so 'z' is reliably wrong.
// Cell indices: t0 h1 e2 _3 b4 a5 d6 _7 c8 a9 t10 _11 s12 a13 t14.
const OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"the bad cat sat","start_ms":1000,"end_ms":9000,"words":[' +
    '{"text":"the","start_ms":1000,"end_ms":3000,"score":1},' +
    '{"text":"bad","start_ms":3000,"end_ms":5000,"score":1},' +
    '{"text":"cat","start_ms":5000,"end_ms":7000,"score":1},' +
    '{"text":"sat","start_ms":7000,"end_ms":9000,"score":1}]}\n';

// The same line 0, plus a SECOND line, which is the only shape on which the seal's combo break is
// observable at all: with one line the deferred miss lands after every other cell has been judged,
// so it cannot lower max_combo however it behaves, it can only cut the run short of the next line.
// Line 1 is ten more cells ("on the mat": o0 n1 _2 t3 h4 e5 _6 m7 a8 t9), again with no 'z'.
const TWO_LINE_OSU =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n' +
    '{"granularity":"word","version":2,"song_end_ms":30000}\n' +
    '{"text":"the bad cat sat","start_ms":1000,"end_ms":9000,"words":[' +
    '{"text":"the","start_ms":1000,"end_ms":3000,"score":1},' +
    '{"text":"bad","start_ms":3000,"end_ms":5000,"score":1},' +
    '{"text":"cat","start_ms":5000,"end_ms":7000,"score":1},' +
    '{"text":"sat","start_ms":7000,"end_ms":9000,"score":1}]}\n' +
    '{"text":"on the mat","start_ms":11000,"end_ms":17000,"words":[' +
    '{"text":"on","start_ms":11000,"end_ms":13000,"score":1},' +
    '{"text":"the","start_ms":13000,"end_ms":15000,"score":1},' +
    '{"text":"mat","start_ms":15000,"end_ms":17000,"score":1}]}\n';

const WRONG_KEY = 'z';

/**
 * Walks the map cell by cell at each cell's own target time, consulting `script[i]` for cell i:
 *
 *   undefined       type the expected char (the ordinary case).
 *   'wrong'         press WRONG_KEY. On a letter cell the default model TAKES it, so the cell is
 *                   consumed and the caret is already on the next one; nothing else is pressed.
 *   'wrongFix'      as 'wrong', then Backspace and the expected char, which is the "I mistyped, let
 *                   me fix it" run.
 *   'wrongErase'    as 'wrong', then Backspace and nothing else. Only valid on the FINAL cell,
 *                   because it leaves the caret on the cell and everything after would land wrong.
 *   'spaceKey'      press a SPACE on a letter cell. Backlog 184 types that through as an ordinary
 *                   typo (with no word to skip it is nothing but a wrong character), so it is
 *                   followed by Backspace and the expected char, exactly as 'wrongFix' is, which is
 *                   what keeps every later cell aligned.
 *   'skip'          press nothing, so the cell seals as a Miss. Only valid on trailing cells, for
 *                   the same caret-alignment reason as 'wrongErase'.
 *
 * `probeAt` names the cell index whose immediate post-press state is captured, which is how the
 * rejection cases prove nothing was written rather than merely that the totals came out right.
 */
function play(script, probeAt, osu) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(osu || OSU), false);
    const engine = new TB.TypingEngine(beatmap);

    const cells = [];
    for (const line of beatmap.lines) for (const cell of line.cells) cells.push(cell);

    let probe = null;
    let i = 0;

    for (const line of beatmap.lines) {
        engine.update(line.activationTime);

        for (const cell of line.cells) {
            const action = script[i];
            engine.update(cell.target);

            if (action === 'skip') {
                i++;
                continue;
            }

            if (action === 'wrong' || action === 'wrongFix' || action === 'wrongErase' || action === 'spaceKey') {
                // The SPACE key on a letter cell, which since backlog 184 is typed through like any
                // other wrong character rather than refused.
                const key = action === 'spaceKey' ? ' ' : WRONG_KEY;
                engine.processKey(key, cell.target);

                if (probeAt === i) {
                    probe = {
                        state: cell.state,
                        typedChar: cell.typedChar,
                        caretIndex: engine.caretIndex,
                        consecutiveWrongKeys: engine.consecutiveWrongKeys,
                        mistypes: engine.mistypes,
                        // The backlog-109 pair: the cell has handed the processor NOTHING (its
                        // result is deferred), yet the submitted combo has already broken.
                        judged: cell.judged,
                        processorCombo: engine.processor.combo,
                        processorJudged: engine.processor.judgementCount
                    };
                }

                if (action === 'wrong') { i++; continue; }
                if (action === 'wrongFix' || action === 'wrongErase' || action === 'spaceKey') engine.processBackspace();
                if (action === 'wrongErase') { i++; continue; }
            }

            engine.processKey(cell.expected, cell.target);
            i++;
        }
    }

    engine.update(1000000);
    const score = TB.computeScore(engine);

    return {
        totalCells: cells.length,
        allowWrongInput: engine.allowWrongInput,
        engineMistypes: engine.mistypes,
        engineMaxCombo: engine.maxCombo,
        consecutiveWrongKeys: engine.consecutiveWrongKeys,
        failed: engine.failed,
        probe: probe,
        cellStates: cells.map(c => c.state).join(','),
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

const out = {
    // The reference: nothing wrong at all.
    clean: play({}),

    // A wrong char typed into the LAST cell and left there. Chosen last on purpose: the result it
    // takes lands in the same place in the judgement stream as the seal-miss of `lastCellSkipped`
    // below, so the two runs differ in the TIER and in nothing else.
    lastCellTypedWrong: play({ 14: 'wrong' }, 14),

    // The same cell simply never typed, i.e. a character the player never finished. This is the
    // MISS the run above is no longer (backlog 124), so it must cost completion and rank where the
    // typo does not.
    lastCellSkipped: play({ 14: 'skip' }),

    // Typed wrong, then backspaced and never fixed. The cell is 'untyped' again, which is a
    // character the player did NOT finish, so the seal misses it and the submitted account must
    // match `lastCellSkipped` exactly, mistype count aside. The distinction is drawn on the cell's
    // state, not on its history.
    lastCellWrongThenErased: play({ 14: 'wrongErase' }),

    // A typo left sitting MID-LINE. The combo break lands on the keypress and the cell's result
    // lands at the seal, i.e. after every later cell has been judged, so this is the run that says
    // the two no longer have to happen together. It is also where the combo-neutral mark is visible
    // on one line: the seal's result is a HIT, and applied normally it would extend the rebuilt run
    // from 9 to 10.
    midCellTypedWrong: play({ 5: 'wrong' }, 5),

    // Typed wrong mid-line, then backspaced and retyped correctly. Since backlog 109 the typo spent
    // no result, so the retype IS the cell's first and only one: it earns a real Great, and the play
    // recovers the cell, its completion and its rank. The mistype and the combo break it cost stay.
    midCellWrongThenFixed: play({ 5: 'wrongFix' }, 5),

    // A WORD GAP takes a wrong letter exactly as a lyric cell does (backlog 181): the gap holds the
    // typo, the caret moves past it, and the seal resolves it as an unfixed typo. Before that it
    // was the one wrong LETTER the browser rejected, which is why this case changed sides rather
    // than being added beside the old one.
    wordGapTypedWrong: play({ 3: 'wrong' }, 3),

    // ...and the fix cycle on the same cell, which is the whole reason to type a typo through
    // rather than refuse it: backspace takes it back and the corrected SPACE earns the cell's real
    // judgement plus the streak the typo broke.
    wordGapWrongThenFixed: play({ 3: 'wrongFix' }, 3),

    // The SPACE key on a letter cell, which backlog 184 moved onto this same type-through path: with
    // no word to skip it is nothing but a wrong character, so the cell takes it, the caret advances
    // and backspace fixes it. It was the last wrong key the browser rejected, so this case changed
    // sides exactly as the word gap did above, and with it went the browser's last route into the
    // 13-in-a-row mash guard (that guard belongs to Gatekeeper, which /play cannot select).
    spaceKeyOnLetter: play({ 0: 'spaceKey' }, 0),

    // The two-line pair (backlog 122). Same typo on cell 5, left alone, but now with a line 1 for
    // the combo run to carry into: line 0's cells 6..14 rebuild a run of 9, the seal resolves cell 5,
    // and line 1's ten cells either extend that run to 19 or start again from 1. The typo broke
    // combo once, at the keypress, so it must be 19, on the HUD's account as well as the submitted
    // one (backlog 123).
    twoLineClean: play({}, undefined, TWO_LINE_OSU),
    twoLineMidCellTypedWrong: play({ 5: 'wrong' }, 5, TWO_LINE_OSU),

    // Backlog 126's forcing case: every LETTER cell typed wrong and left that way, with the three
    // word gaps typed correctly (they are the run's only typed cells, which is what makes the
    // completion arithmetic below legible). So 12 of 15 cells end as uncorrected typos and the play
    // typed 3 of them: completion 0.2 and a D. Under backlog 124 the same run read completion 1 and
    // took an X, because every one of those cells resolved as a HIT and completion counted hits.
    everyLetterTypedWrong: play({
        0: 'wrong', 1: 'wrong', 2: 'wrong',
        4: 'wrong', 5: 'wrong', 6: 'wrong',
        8: 'wrong', 9: 'wrong', 10: 'wrong',
        12: 'wrong', 13: 'wrong', 14: 'wrong'
    })
};

process.stdout.write(JSON.stringify(out));
