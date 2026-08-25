// Node harness for the WORD-LEVEL EDITING GESTURES in the browser scoring core (backlog 182).
// Ctrl+Backspace erases the previous word (the gaps behind the caret, then the word behind them);
// Ctrl+A offers back the run from the caret to the start of the EARLIEST word holding an unfixed
// typo (backlog 184 widened it from the nearest one), so every mistake can be retyped in one go.
// Since backlog 184 the same step scripts also carry the SPACE DISCIPLINE, because both of its
// halves move the caret: a gap typo parks it, and a mid-word space is a typo rather than a
// rejection.
//
// The engine's whole share of that is TWO PURE QUERIES, engine.wordBackspaceTarget and
// engine.retypeSelectionAnchor, mirrored from TypingEngine.WordBackspaceTarget and
// TypingEngine.RetypeSelectionAnchor. Everything else is COMPOSED out of engine calls that already
// exist (a run of processBackspace plus at most one processKey) and lives in the input layer, which
// on the browser is typebeat-player.js.
//
// So this file carries a small INTERPRETER for that input layer: the same collapse-then-type rule,
// the same defensive erase loop, and the same per-frame staleness drop the player runs, driving the
// real engine. WordInputParityTest.cs runs the identical interpreter over the C# TypingEngine and
// holds the two probe streams against each other, which is what makes this a comparison against a
// live engine rather than against a literal transcribed from one.
//
// The scenarios are NOT carried here: they arrive as a JSON file written by the C# side, so the two
// arms cannot drift onto separately maintained copies of the same map or the same keystrokes.
//
// Usage: node CoreWordInputHarness.cjs <absolute path to typebeat-core.js> <scenario json path>

'use strict';

const fs = require('fs');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

const scenarioPath = process.argv[3];
if (!scenarioPath) {
    process.stderr.write('missing scenario json path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

const input = JSON.parse(fs.readFileSync(scenarioPath, 'utf8'));

// ---------------------------------------------------------------------------------------------
// The input layer, mirrored from typebeat-player.js (which mirrors TypeBeatKeyHandler).
// ---------------------------------------------------------------------------------------------

/**
 * typebeat-player.js eraseBackTo / TypeBeatKeyHandler.eraseBackTo. Ordinary processBackspace calls
 * back to a target, with the defensive no-progress break: an erase that reclaimed abandoned cells
 * at the head of a line can land on 0 and be auto-skipped forward again, and a gesture must never
 * spin. Returns how many erases actually mutated, which is how many BACKSPACE frames a desktop run
 * would have recorded.
 */
function eraseBackTo(engine, target) {
    let erases = 0;

    while (engine.caretIndex > target) {
        const before = engine.caretIndex;
        if (!engine.processBackspace()) break;
        erases++;
        if (engine.caretIndex >= before) break;
    }

    return erases;
}

/** One run of the interpreter: the engine plus the pure UI state the player holds beside it. */
function makeSession(scenario) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(scenario.osu), scenario.literate === true);
    const engine = new TB.TypingEngine(beatmap);

    engine.spaceSkipsWord = scenario.spaceSkipsWord === true;
    // The C# takes literate as a CONSTRUCTOR argument and derives CaseSensitive from it; here the
    // stream is chosen at buildBeatmap and the flag is set by hand, exactly as the other harnesses
    // that pin the Literate branch do.
    engine.caseSensitive = scenario.literate === true;

    // The browser is always on the live arm of the wrong-input rules, so there is no flag here to
    // set: allowWrongInput defaults on, and wrong input on word gaps and the space discipline are
    // unconditional. The C# arm has to select all three explicitly (see WordInputParityTest.Started).
    engine.update(scenario.startTime);

    return { engine: engine, selection: null, erases: 0 };
}

/**
 * The player's collapseSelection: a mass backspace to the anchor, the selection dropped BEFORE the
 * erases so the staleness check cannot race them. Returns whether there was one to collapse, so the
 * caller can tell "the selection ate this key" from "there was nothing there".
 */
function collapseSelection(session) {
    if (!session.selection) return false;

    const start = session.selection.startCell;
    session.selection = null;
    session.erases += eraseBackTo(session.engine, start);
    return true;
}

/**
 * TypeBeatPlayfield.Update's staleness drop, which the browser runs per animation frame: a
 * selection whose line or caret no longer match it is stale and goes. Run after every step here,
 * which is the same "a frame happened between two keystrokes" the player gets.
 */
function dropStaleSelection(session) {
    const s = session.selection;
    if (!s) return;
    if (session.engine.activeLineIndex !== s.lineIndex || session.engine.caretIndex !== s.endCell) session.selection = null;
}

function step(session, op) {
    const engine = session.engine;

    switch (op.op) {
        case 'update':
            engine.update(op.t);
            break;

        case 'key':
            // A typeable key: the selection is consumed FIRST, so the key lands on the anchor cell.
            collapseSelection(session);
            engine.processKey(op.c, op.t);
            break;

        case 'backspace':
            // Plain backspace. A live selection takes precedence over the single-cell erase: the key
            // collapses it and types nothing.
            if (!collapseSelection(session)) {
                if (engine.processBackspace()) session.erases++;
            }
            break;

        case 'ctrlBackspace':
            // The word width. A live selection still wins, exactly as for the plain key.
            if (!collapseSelection(session)) session.erases += eraseBackTo(engine, engine.wordBackspaceTarget);
            break;

        case 'churn':
            // PURITY. Both queries are pure, which is what lets them be mirrored as plain getters
            // and what keeps the whole feature in the input layer. Reading each of them n times
            // over a mid-run engine must leave every observable exactly where the probe before this
            // step found it.
            for (let i = 0; i < op.n; i++) {
                if (engine.wordBackspaceTarget < -1) throw new Error('impossible');
                if (engine.retypeSelectionAnchor < -1) throw new Error('impossible');
            }
            break;

        case 'ctrlA': {
            const anchor = engine.retypeSelectionAnchor;
            // No typo behind the caret: a genuine no-op, nothing to select and nothing to clear.
            if (anchor >= 0) session.selection = { lineIndex: engine.activeLineIndex, startCell: anchor, endCell: engine.caretIndex };
            break;
        }

        default:
            throw new Error('unknown op ' + op.op);
    }

    dropStaleSelection(session);
}

/** Everything both arms can see, taken after every step. */
function probe(session) {
    const engine = session.engine;
    const line = engine.lines[0];

    return {
        caretIndex: engine.caretIndex,
        activeLineIndex: engine.activeLineIndex,
        finished: engine.finished,
        wordBackspaceTarget: engine.wordBackspaceTarget,
        retypeSelectionAnchor: engine.retypeSelectionAnchor,
        selectionStart: session.selection ? session.selection.startCell : -1,
        selectionEnd: session.selection ? session.selection.endCell : -1,
        erases: session.erases,
        states: line.cells.map(c => c.state),
        typed: line.cells.map(c => (c.typedChar === null || c.typedChar === undefined) ? '' : c.typedChar),
        score: engine.score,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        mistypes: engine.mistypes,
        accuracy: engine.liveAccuracy,
    };
}

const out = { scenarios: [] };

for (const scenario of input.scenarios) {
    const session = makeSession(scenario);
    const probes = [probe(session)]; // the state every scenario starts from

    for (const op of scenario.steps) {
        step(session, op);
        probes.push(probe(session));
    }

    out.scenarios.push({
        name: scenario.name,
        expected: session.engine.lines[0].cells.map(c => c.expected),
        targets: session.engine.lines[0].cells.map(c => c.target),
        typeable: session.engine.lines[0].cells.map(c => c.typeable),
        probes: probes,
    });
}

process.stdout.write(JSON.stringify(out));
