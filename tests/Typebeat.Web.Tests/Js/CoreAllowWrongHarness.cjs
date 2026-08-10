// Node harness for the DEFAULT wrong-key model in the browser scoring core (backlog 107): a wrong
// (non-space) character is TYPED THROUGH into the cell and can be backspaced, instead of being
// rejected. The desktop client flipped its default the same way, and strict rejection survives only
// as the Gatekeeper mod, which the browser has no way to select. So this is what every /play score
// is now judged under, on the SAME leaderboards as desktop scores.
//
// CoreMistypeHarness.cjs is the sibling fixture for the rejection model (it sets allowWrongInput
// off explicitly). Since backlog 109 the two models account for the KEYPRESS identically (a combo
// break plus a mistype, and no judgement result either way); what still differs is the CELL, which
// rejection leaves waiting for the player and the default model consumes, so a typo left alone
// costs the cell at the seal while a rejected key never costs one at all.
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
 *   'reject'        press a key the default model still refuses (WRONG_KEY on a word gap, or a
 *                   space on a letter), which is rejected with the caret held, then the expected
 *                   char, which therefore still lands on this cell.
 *   'skip'          press nothing, so the cell seals as a Miss. Only valid on trailing cells, for
 *                   the same caret-alignment reason as 'wrongErase'.
 *
 * `probeAt` names the cell index whose immediate post-press state is captured, which is how the
 * rejection cases prove nothing was written rather than merely that the totals came out right.
 */
function play(script, probeAt) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(OSU), false);
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

            if (action === 'wrong' || action === 'wrongFix' || action === 'wrongErase' || action === 'reject') {
                const key = (action === 'reject' && cell.expected !== ' ') ? ' ' : WRONG_KEY;
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
                if (action === 'wrongFix' || action === 'wrongErase') engine.processBackspace();
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

    // A wrong char typed into the LAST cell and left there. Chosen last on purpose: the miss it
    // costs lands in the same place in the judgement stream as the seal-miss of `lastCellSkipped`
    // below, which makes the two runs comparable number for number.
    lastCellTypedWrong: play({ 14: 'wrong' }, 14),

    // The same cell simply never typed. Every submitted number must match the run above, because
    // both are one HitResult.Miss on the last cell; only the mistype count tells them apart.
    lastCellSkipped: play({ 14: 'skip' }),

    // Typed wrong, then backspaced and never fixed. The cell is 'untyped' again for display, and
    // still unjudged, so the seal misses it: the submitted account must be identical to the two runs
    // above rather than carrying two misses.
    lastCellWrongThenErased: play({ 14: 'wrongErase' }),

    // A typo left sitting MID-LINE. The combo break lands on the keypress and the cell's miss lands
    // at the seal, i.e. after every later cell has been judged, so this is the run that says the two
    // no longer have to happen together. It is also the regression pin for "an uncorrected typo
    // costs what it always cost": the account is byte-identical to what the OLD model produced for
    // this play, because nothing scores between the break and the miss.
    midCellTypedWrong: play({ 5: 'wrong' }, 5),

    // Typed wrong mid-line, then backspaced and retyped correctly. Since backlog 109 the typo spent
    // no result, so the retype IS the cell's first and only one: it earns a real Great, and the play
    // recovers the cell, its completion and its rank. The mistype and the combo break it cost stay.
    midCellWrongThenFixed: play({ 5: 'wrongFix' }, 5),

    // A WORD GAP still refuses a wrong key in every model, so this one is rejected: caret held, no
    // cell written, and the mash-fail streak fed.
    wrongKeyOnWordGap: play({ 3: 'reject' }, 3),

    // ...and so does a SPACE pressed on a letter cell, the other half of the same carve-out.
    spaceKeyOnLetter: play({ 0: 'reject' }, 0)
};

process.stdout.write(JSON.stringify(out));
