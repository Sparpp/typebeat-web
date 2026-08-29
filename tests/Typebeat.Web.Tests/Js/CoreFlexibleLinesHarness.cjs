// Node harness that loads the browser scoring core (typebeat-core.js) and exercises the FLEXIBLE
// LINES default (backlog 208): the caret is decoupled from the song's playhead, so finishing a line
// opens the next one at once (RUSH FREEDOM), a line the player is still typing is not snatched at
// its deadline (DRAG FREEDOM), a press that puts the caret too far past the playhead earns no combo
// (the RUSH CAP), and a caret parked past the end of a FINISHED line is handed on the moment the
// next line is due (the LINE-START SNAP). The observations are emitted as JSON on stdout so the C#
// fidelity test (FlexibleLinesParityTest) can assert them against the game's golden values, which
// come from typebeat-osu's NonVisual/FletcherEngineTest.cs.
//
// Since backlog 218 the rush is BOUNDED, which is the fifth thing here: entry into a line opens
// FLETCHER_DRAG_GRACE_MS before its own cue, the exact mirror of the same constant the drag borrows
// past a line's end, so a finished caret PARKS past the last cell of its line until the next one is
// nearly due and the line-start snap performs the deferred roll. That moved the instant the snap
// fires (a line's activation minus the grace, not the activation itself), which is why the parked
// and instrumental sections below read 9000 and 12500 where they used to read a line's cue.
//
// This is the JS half of the guard. The cross-repo half is Typebeat.WireCompat's
// EngineFuzzLiveParityTest, which plays the SAME rules through the game's own replay scorer; this
// one needs no game checkout, so it still runs when that project cannot resolve one.
//
// typebeat-core.js is a plain browser script that attaches window.TypeBeatCore via an IIFE invoked
// with the bare `window` identifier; provide a global `window` before loading so it resolves, then
// read the export back off it.
//
// Usage: node CoreFlexibleLinesHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

function osu(lines, songEndMs) {
    return '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n' +
        JSON.stringify({ granularity: 'line', version: 2, song_end_ms: songEndMs }) + '\n' +
        lines.map(l => JSON.stringify(l)).join('\n') + '\n';
}

function word(text, start, end) { return { text: text, start_ms: start, end_ms: end, score: 1 }; }

function build(text) { return TB.buildBeatmap(TB.parseLyricOsu(text)); }

// Two back-to-back lines, the game's own dragMap shape.
//   L0 "ab cd" [1000, 4000), sung [1000, 3000): a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
//   L1 "ef"    [4000, 8000), sung [4000, 5000): e = 4000, f = 4500, cue-clamped so it activates at
//              its own 4000 start.
// L0's seal grace is 0 (its words end a second before its window does and its last cell is nowhere
// near the boundary), so its hard deadline is 4000 flat and its drag grace runs to 5500.
const TWO_LINES = osu([
    { text: 'ab cd', start_ms: 1000, end_ms: 3000, words: [word('ab', 1000, 2000), word('cd', 2000, 3000)] },
    { text: 'ef', start_ms: 4000, end_ms: 5000, words: [word('ef', 4000, 5000)] }
], 12000);

// One line whose ten characters are a second apart, so "how far ahead of the playhead is the caret"
// is countable by eye: "abcdef" targets 1000..6000, a word gap, then "ghi" at 7000, 8000, 9000. The
// gap is the only non-COUNTABLE typeable cell in it, which is what lets the space's exemption from
// the rush budget be seen at all.
const RUSH_LINE = osu([
    { text: 'abcdef ghi', start_ms: 1000, end_ms: 10000, words: [word('abcdef', 1000, 7000), word('ghi', 7000, 10000)] }
], 20000);

// Two lines with a twelve-second instrumental between them, which is the shape songIsOnTheCaretsLine
// exists for: a decoder-built line's window runs to the NEXT line's start, so through the gap the
// playhead is still inside L0's window while a player who finished L0 is parked at the head of L1
// with nothing being asked of them.
//   L0 "ab" [1000, 20000): a = 1000, b = 2000.  L1 "cd" [20000, 25000): c = 20000, d = 21000.
const GAPPED = osu([
    { text: 'ab', start_ms: 1000, end_ms: 3000, words: [word('ab', 1000, 3000)] },
    { text: 'cd', start_ms: 20000, end_ms: 22000, words: [word('cd', 20000, 22000)] }
], 30000);

// THE PARKED-CARET MAP, the game's own FletcherEngineTest.parkedLineMap. The snap only ever decides
// anything while a FINISHED caret sits on a line the seal has not reached and the next line has
// ALREADY started, which needs a line with NO CELLS (so no press can ever finish it, the one state
// the keypress roll-forward cannot cover) and a window that OUTLIVES the next line's cue.
//
// The browser's loader can build neither: it drops a line whose text is pure punctuation, and it
// makes windows strictly contiguous (a line ends where the next one starts) while a line's own
// activation is never before its start, so on any map it CAN build the seal's own hand-over always
// carries a finished caret across the boundary first. So the middle line is spliced in by hand, with
// the numbers the game's fixture declares outright. It is a statement about the ENGINE, and the
// harness is honest about the map not being one /play could load.
//
//   L0 "ab"  [1000, 3000):   a = 1000, b = 1500, activation 1000.
//   L1 "..." [3000, 20000):  no cells at all, activation 3000.
//   L2 "cd"  [10000, 30000): c = 12000, d = 12500, activation 12000 - CUE_LEAD_MS = 10500, which is
//                            9500 ms before L1's window closes.
const PARKED_ENDS = osu([
    { text: 'ab', start_ms: 1000, end_ms: 2000, words: [word('ab', 1000, 2000)] },
    { text: 'cd', start_ms: 10000, end_ms: 13000, words: [word('cd', 12000, 13000)] }
], 30000);

// THE INSTRUMENTAL-GAP SHAPE, mirroring the game's FletcherEngineTest.instrumentalGapMap, and the
// fixture the rush bound is measured on because it is what a decoder actually builds: line windows
// are CONTIGUOUS, so the twelve-second instrumental lives inside L0's own window rather than in a
// hole between the lines. Unlike PARKED_ENDS below, every number here is loader-derived.
//   L0 "ab"       [1000, 14000), sung to 2000: a = 1000, b = 1500, activation 1000.
//   L1 "cdefghij" [14000, 18000), sung [14000, 15000]: eight chars, step 125, so c = 14000 and
//                 j = 14875. Activation is clamped to the line's own start (14000), so the rush
//                 bound opens at 14000 - FLETCHER_DRAG_GRACE_MS = 12500, a second and a half before
//                 L0 could seal at all.
const INSTRUMENTAL_GAP = osu([
    { text: 'ab', start_ms: 1000, end_ms: 2000, words: [word('ab', 1000, 2000)] },
    { text: 'cdefghij', start_ms: 14000, end_ms: 15000, words: [word('cdefghij', 14000, 15000)] }
], 30000);

function parked() {
    const beatmap = build(PARKED_ENDS);

    beatmap.lines[0].endTime = 3000;
    beatmap.lines.splice(1, 0, {
        index: 1, text: '', startTime: 3000, endTime: 20000, singEndTime: 19000,
        activationTime: 3000, sealGraceMs: 0, estimated: false,
        cells: [], syllables: [], cellSyllable: [], charTimedStretch: []
    });
    beatmap.lines[2].index = 2;
    beatmap.lines[2].endTime = 30000;
    beatmap.totalCells = beatmap.lines.reduce((n, l) => n + l.cells.length, 0);

    return beatmap;
}

// A HOLE AT THE HEAD of line 1's window: its vocals are seven seconds into it, so the rush bound
// opens at 9000 while line 0 seals at 3000. The one shape where the SEAL's hand-over arrives inside
// the refusal window, which is exactly the case the bound must not refuse (entry there is the song
// arriving, late rather than early). Hand-built for the same reason PARKED_ENDS is: the loader makes
// windows contiguous and clamps a line's activation to its own start, so it cannot express a hole.
//   L0 "ab" [1000, 3000): a = 1000, b = 1500, seal grace 0, so it seals at 3000 flat and its drag
//           cutoff is 4500.
//   L1 "cd" [10000, 30000): c = 12000, d = 12500, activation 10500, so entry opens at 9000.
function holed() {
    const beatmap = build(PARKED_ENDS);

    beatmap.lines[0].endTime = 3000;
    beatmap.lines[1].endTime = 30000;

    return beatmap;
}

/** Where the caret is, as the two numbers every observation below is written in. */
function at(engine) { return { line: engine.activeLineIndex, cell: engine.caretIndex }; }

// ---------------------------------------------------------------------------------------------

const out = {};

// The browser's own settings, which are the LIVE ones unconditionally: it has no mods payload, so
// the strict pinning mod (acronym FC) is unreachable in it, and no replay input, so neither the
// pinned era every pre-208 row was played in nor the UNBOUNDED rush every pre-218 row was played
// with is reachable either.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    out.defaults = {
        fletcherEnabled: engine.fletcherEnabled,
        flexibleLineSnap: engine.flexibleLineSnap,
        boundedRush: engine.boundedRush,
        maxCharsAhead: TB.constants.FLETCHER_MAX_CHARS_AHEAD,
        dragGraceMs: TB.constants.FLETCHER_DRAG_GRACE_MS
    };
}

// RUSH FREEDOM INSIDE THE BOUND, which is what ordinary back-to-back play is and what backlog 218
// had to leave exactly as it was. Typing L0 out at 2500, a second and a half before L1's own 4000
// cue, puts the caret on L1 ON THE PRESS, because 2500 is precisely where entry into L1 opens
// (4000 - FLETCHER_DRAG_GRACE_MS): the earliest instant the bound permits, and the press lands on
// it. A press then lands on L1's first cell. Under a pinned caret the same press is inert (no line
// is active until 4000) and L1's 'e' would seal a miss.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    engine.update(1000);
    for (const [t, c] of [[1000, 'a'], [1500, 'b'], [2000, ' '], [2000, 'c'], [2500, 'd']]) {
        engine.update(t);
        engine.processKey(c, t);
    }

    const afterFinishing = at(engine);

    engine.update(2600);
    const handled = engine.processKey('e', 2600);

    out.rushFreedom = {
        nextLineActivation: engine.lines[1].activationTime,
        entryOpensAt: engine.entryOpensAt(1),
        finishedAt: 2500,
        afterFinishing: afterFinishing,
        pressHandled: handled,
        afterPress: at(engine),
        firstCellOfNextLine: engine.lines[1].cells[0].state,
        // The line left behind is UNSEALED and stays that way until its own deadline: rush freedom
        // moves the player, never the song.
        nextSealIndex: engine.nextSealIndex
    };
}

// THE RUSH BOUND (backlog 218) on the shape a decoder actually builds. L0 is finished twelve and a
// half seconds before entry into L1 opens, so the roll is REFUSED and the caret parks past L0's last
// cell. A press in the park is inert (no cell, no judgement, no typo, no combo break and nothing in
// the accuracy denominator), the caret has still not moved one frame short of the bound, and the
// line-start snap performs the deferred roll at 12500 exactly. The head start is real: the player is
// on L1 a second and a half before its cue, and the press is judged early (target 14000) exactly as
// rushing always was.
{
    const engine = new TB.TypingEngine(build(INSTRUMENTAL_GAP));

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(1500);
    engine.processKey('b', 1500);

    const parkedAt = at(engine);

    engine.update(6000);
    const inertHandled = engine.processKey('c', 6000);
    const inert = {
        handled: inertHandled,
        nextLineFirstCellState: engine.lines[1].cells[0].state,
        combo: engine.combo,
        comboBreaks: breaks,
        mistypes: engine.mistypes,
        liveAccuracy: engine.liveAccuracy
    };

    engine.update(12499);
    const oneFrameShort = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    engine.update(12500);
    const opened = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    const handled = engine.processKey('c', 12500);

    out.rushBound = {
        nextLineActivation: engine.lines[1].activationTime,
        thisLineEnd: engine.lines[0].endTime,
        entryOpensAt: engine.entryOpensAt(1),
        parkedAt: parkedAt,
        inertPress: inert,
        oneFrameShort: oneFrameShort,
        opened: opened,
        pressHandled: handled,
        firstCellState: engine.lines[1].cells[0].state,
        firstCellDelta: engine.lines[1].cells[0].judgedDelta
    };
}

// THE SYMMETRY, read off the one constant on one back-to-back fixture. L0's natural END
// (endTime + sealGraceMs) and L1's natural START (activationTime) are both 4000 here, so the drag
// cutoff sits at 5500 and entry into L1 opens at 2500, each exactly FLETCHER_DRAG_GRACE_MS from that
// shared edge. The rushing script finishes L0 at 2000, 500 ms too early, and the caret parks until
// 2500; the drag half of the same statement is the dragFreedom section above, which holds L0 to 5499
// and force-seals it at 5500.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    engine.update(2000);
    for (const c of ['a', 'b', ' ', 'c', 'd']) engine.processKey(c, 2000);

    const finishedEarly = at(engine);

    engine.update(2499);
    const oneFrameShort = at(engine);

    engine.update(2500);

    out.boundSymmetry = {
        naturalEnd: engine.lines[0].endTime + engine.lines[0].sealGraceMs,
        nextLineActivation: engine.lines[1].activationTime,
        entryOpensAt: engine.entryOpensAt(1),
        dragCutoff: engine.lines[0].endTime + engine.lines[0].sealGraceMs + TB.constants.FLETCHER_DRAG_GRACE_MS,
        finishedEarly: finishedEarly,
        oneFrameShort: oneFrameShort,
        atTheBound: at(engine)
    };
}

// THE RUSH CAP is untouched by the bound and still bites on the far side of a permitted roll: entry
// buys the player a line, never a licence to run away down it. At 12500 the playhead has reached two
// countable chars ('a' and 'b') and so has the caret, so five chars of L1 keep it inside the cap and
// the sixth is over it.
{
    const engine = new TB.TypingEngine(build(INSTRUMENTAL_GAP));

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(1500);
    engine.processKey('b', 1500);
    engine.update(12500); // the bound opens and the deferred roll lands the caret on L1

    const rolledOnto = at(engine);
    const playhead = engine.playheadCountablePosition(12500);
    const leadOnArrival = engine.charsAheadOfPlayhead(12500);

    for (const c of ['c', 'd', 'e', 'f', 'g']) engine.processKey(c, 12500);

    const insideTheCap = { lead: engine.charsAheadOfPlayhead(12500), combo: engine.combo, comboBreaks: breaks };

    engine.processKey('h', 12500);

    out.rushCapAfterARoll = {
        rolledOnto: rolledOnto,
        playhead: playhead,
        leadOnArrival: leadOnArrival,
        insideTheCap: insideTheCap,
        pastTheCap: { lead: engine.charsAheadOfPlayhead(12500), combo: engine.combo, comboBreaks: breaks }
    };
}

// THE SEAL'S HAND-OVERS, which the bound is never asked about and must never refuse: the song has
// moved off the old line there, so entry is LATE rather than early, and refusing it would leave the
// player in a dead zone the flexible caret does not otherwise have. Both of them land inside the
// refusal window on this fixture (entry opens at 9000, L0 seals at 3000 and its drag cutoff is
// 4500), which is what makes the two observations mean anything.
{
    const finished = new TB.TypingEngine(holed());

    finished.update(1000);
    finished.processKey('a', 1000);
    finished.update(1500);
    finished.processKey('b', 1500);

    const parkedByTheBound = at(finished);

    finished.update(3000);

    const lagging = new TB.TypingEngine(holed());

    lagging.update(1000);
    lagging.processKey('a', 1000);
    lagging.update(4500);

    const handedOver = at(lagging);
    const laggingHandled = lagging.processKey('c', 4600);

    out.sealHandOver = {
        nextLineActivation: finished.lines[1].activationTime,
        entryOpensAt: finished.entryOpensAt(1),
        thisLineEnd: finished.lines[0].endTime,
        sealGraceMs: finished.lines[0].sealGraceMs,
        parkedByTheBound: parkedByTheBound,
        // The ORDINARY seal: L0 is fully typed, so it seals on its own 3000 deadline and hands the
        // finished caret on, six seconds before the bound would have opened.
        ordinary: {
            at: at(finished),
            nextSealIndex: finished.nextSealIndex,
            cellStates: finished.lines[0].cells.map(c => c.state)
        },
        // The DRAG CUTOFF: the same hand-over for a player who never finished, at 3000 + the grace.
        // Also inside the refusal window, and also not refused.
        dragCutoff: {
            at: handedOver,
            untypedCellState: lagging.lines[0].cells[1].state,
            pressHandled: laggingHandled,
            firstCellState: lagging.lines[1].cells[0].state
        }
    };
}

// DRAG FREEDOM: 'd' is still owed when L0's 4000 deadline arrives, so the line is not sealed out
// from under the player. It is held for FLETCHER_DRAG_GRACE_MS and force-sealed at 5500, where the
// untyped cell becomes a miss (one combo break for the line, however many cells) and the caret is
// handed to L1 rather than left in a dead zone.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    for (const [t, c] of [[1000, 'a'], [1500, 'b'], [2000, ' '], [2000, 'c']]) {
        engine.update(t);
        engine.processKey(c, t);
    }

    engine.update(4000);
    const atDeadline = at(engine);
    const sealedAtDeadline = engine.nextSealIndex;

    engine.update(5499);
    const oneFrameShort = at(engine);

    engine.update(5500);

    out.dragFreedom = {
        atDeadline: atDeadline,
        sealedAtDeadline: sealedAtDeadline,
        oneFrameShort: oneFrameShort,
        afterTheGrace: at(engine),
        sealedAfterTheGrace: engine.nextSealIndex,
        // The 'd' the player never got to: a MISS, and the only one, taken at the force-seal.
        untypedCellState: engine.lines[0].cells[4].state,
        comboBreaks: breaks
    };
}

// The same drag, finished INSIDE the grace: the press lands on its own cell (late, which the ladder
// grades honestly) and the ordinary roll-forward then takes over, so nothing is missed at all.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    for (const [t, c] of [[1000, 'a'], [1500, 'b'], [2000, ' '], [2000, 'c']]) {
        engine.update(t);
        engine.processKey(c, t);
    }

    engine.update(5000);
    const handled = engine.processKey('d', 5000);

    out.dragFinishedInTime = {
        pressHandled: handled,
        lastCellState: engine.lines[0].cells[4].state,
        // 5000 against a 2500 target: accepted, and judged on exactly how late it was.
        lastCellDelta: engine.lines[0].cells[4].judgedDelta,
        afterPress: at(engine)
    };
}

// THE LINE-START SNAP. Typing L0 out rolls the caret straight on to the cell-less L1 (L1's own
// activation is its 3000 start, so entry into it opens at 1500, which is exactly when the 'b' lands:
// the bound is deliberately not what puts the caret here), where it is complete on arrival and
// stuck, since no press of the player's can ever finish it. One frame short of L2 coming DUE nothing
// has moved (the snap is the next line arriving, not the caret being idle); at 9000 L2 takes it,
// with L1 still unsealed, which is what proves the SNAP moved the caret rather than a seal.
//
// "Due" is entryOpensAt, which backlog 218 moved: the snap now takes a finished caret
// FLETCHER_DRAG_GRACE_MS before the line's own activation, because that is the head start the rush
// bound grants any finished caret and this is the arm that performs it. 10500 - 1500 = 9000.
{
    const engine = new TB.TypingEngine(parked());

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(1500);
    engine.processKey('b', 1500);

    const parkedOn = at(engine);

    engine.update(8999);
    const oneFrameShort = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    engine.update(9000);
    const snapped = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    const handled = engine.processKey('c', 12000);

    out.lineStartSnap = {
        middleLineCellCount: engine.lines[1].cells.length,
        nextLineActivation: engine.lines[2].activationTime,
        entryOpensAt: engine.entryOpensAt(2),
        middleLineEntryOpensAt: engine.entryOpensAt(1),
        middleLineEnd: engine.lines[1].endTime,
        parkedOn: parkedOn,
        oneFrameShort: oneFrameShort,
        snapped: snapped,
        pressHandled: handled,
        firstCellState: engine.lines[2].cells[0].state,
        firstCellDelta: engine.lines[2].cells[0].judgedDelta
    };
}

// THE LIMIT, and the reason the snap is gated on FINISHED rather than on time alone: a line the
// player is still typing is never taken from them, because dragging behind is precisely the freedom
// the flexible caret grants. The same two-line overlap the parked map is built on, minus the middle
// line: L0 runs to 20000 and L1 activates at 10500, so between those two times NOTHING but the snap
// could move the caret (L0 cannot even seal yet). 'b' is left owed for eight and a half seconds of
// it, and only finishing the line moves the caret.
{
    const beatmap = build(PARKED_ENDS);

    beatmap.lines[0].endTime = 20000;
    beatmap.lines[1].endTime = 30000;

    const engine = new TB.TypingEngine(beatmap);

    engine.update(1000);
    engine.processKey('a', 1000);

    engine.update(19000); // eight and a half seconds past L1's own cue
    const stillOwed = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    engine.update(19500);
    engine.processKey('b', 19500);

    out.unfinishedIsNeverSnapped = {
        nextLineActivation: engine.lines[1].activationTime,
        thisLineEnd: engine.lines[0].endTime,
        stillOwed: stillOwed,
        untouchedCellState: engine.lines[1].cells[0].state,
        afterFinishing: at(engine)
    };
}

// THE RUSH CAP, and the SPACE's exemption from it. Every press is at 1000, where the playhead has
// reached exactly one countable character, so the caret's lead is the press count:
//   a..f  the caret ends 0,1,2,3,4 then 5 characters ahead, all inside the cap, all earning combo.
//   ' '   a word gap is not COUNTABLE, so it spends no budget: the caret is still 5 ahead and the
//         press earns combo. With a budget it would have been the sixth and broken the run.
//   g     the caret ends 6 ahead, which is over the cap: the press still lands and still scores, and
//         earns no combo. ONE break.
//   h     further out still, and no second break: the run is already at zero.
// The clock then catches up to 8000, where the playhead has passed eight countable characters, and
// 'i' lands back inside the cap and the run RE-ARMS.
{
    const engine = new TB.TypingEngine(build(RUSH_LINE));

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    const lead = [];
    const combos = [];

    engine.update(1000);

    for (const c of ['a', 'b', 'c', 'd', 'e', 'f', ' ', 'g', 'h']) {
        engine.processKey(c, 1000);
        lead.push(engine.charsAheadOfPlayhead(1000));
        combos.push(engine.combo);
    }

    const cappedMaxCombo = engine.maxCombo;

    engine.update(8000);
    engine.processKey('i', 8000);

    out.rushCap = {
        // Every cell still LANDED: the cap is a combo penalty, not a block.
        states: engine.lines[0].cells.map(c => c.state),
        leadAfterEachPress: lead,
        comboAfterEachPress: combos,
        comboBreaks: breaks,
        maxComboBeforeTheCatchUp: cappedMaxCombo,
        leadAfterCatchUp: engine.charsAheadOfPlayhead(8000),
        comboAfterCatchUp: engine.combo
    };
}

// SONG-IS-ON-THE-CARET'S-LINE, the predicate the desktop's Space fall-through gates on and the one
// the browser's instrumental countdown chip now gates on. Through the gap the playhead is STILL
// inside L0's window (line windows are contiguous, so there is no hole), which is why the plain
// "is a line window open" question is not the one either consumer is asking.
//
// RE-TIMED by backlog 218 rather than re-aimed: L1's cue is 20000, so entry into it opens at 18500,
// and until then the rush bound holds the finished caret on L0. The predicate reads TRUE for the
// whole of that park, correctly, because the song IS on the line the caret is on, and it is the
// bound's own arm (not the predicate) that keeps the player out of the gap. From 18500 the caret is
// ahead of the song and the predicate is what the consumers need: the window is contiguous, so
// songWindowOpen could never have answered it.
{
    const engine = new TB.TypingEngine(build(GAPPED));

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('b', 2000);

    const readings = [];

    for (const t of [2000, 12000, 18499, 18500, 20000]) {
        engine.update(t);
        readings.push({
            time: t,
            at: at(engine),
            songWindowOpen: engine.songWindowOpen,
            songIsOnTheCaretsLine: engine.songIsOnTheCaretsLine
        });
    }

    out.songOnTheCaretsLine = { entryOpensAt: engine.entryOpensAt(1), readings: readings };
}

// THE WPM CLOCK is suspended through BOTH parked states, which since backlog 218 is what this run
// walks through in turn: the caret is held past the end of L0 by the rush bound from 2000 to 18500,
// then sits at the head of L1 ahead of its 20000 cue. A player who finishes L0 at 2000 and waits out
// an eighteen-second instrumental has not been typing for eighteen seconds either way. The first
// park is stopped by the caller's own "the active line is INCOMPLETE" clause (a parked-finished
// caret is complete by definition) and the second by clockRunsFrom, and the two must agree or the
// readout halves.
{
    const engine = new TB.TypingEngine(build(GAPPED));

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('b', 2000);

    // Typing L0 took a second, which is the whole of the clock so far.
    const afterTyping = engine.activeTimeMs;

    engine.update(12000); // ten seconds parked past the END of L0, held there by the rush bound
    const parkedPastTheEnd = { activeTimeMs: engine.activeTimeMs, at: at(engine) };

    engine.update(19000); // seventeen seconds of parked instrumental, now at the head of L1
    const parkedTime = engine.activeTimeMs;

    engine.update(20000); // the frame that ENDS at the cue still measures parked time
    const atTheCue = engine.activeTimeMs;

    engine.update(21000); // and this one is real typing time again
    const runningTime = engine.activeTimeMs;

    out.wpmClock = {
        nextLineActivation: engine.lines[1].activationTime,
        entryOpensAt: engine.entryOpensAt(1),
        activeTimeAfterTyping: afterTyping,
        parkedPastTheEnd: parkedPastTheEnd,
        activeTimeWhileParked: parkedTime,
        activeTimeAtTheCue: atTheCue,
        activeTimeAfterTheCue: runningTime
    };
}

// THE LAZY CLOCK ARM (backlog 222), and the hole the section above leaves: it parks and WAITS, and
// the state that was broken is parking and TYPING. A caret rolled on to the next line sits there
// from FLETCHER_DRAG_GRACE_MS before its cue and processKey has no time gate, so the player really
// can type there; every character they land counts in the WPM numerator for the rest of the run.
// Counting them while the clock stayed stopped walked the readout upward for free, once per line.
//
// So the clock ARMS LAZILY on the first press made on such a line and runs from that press's own
// time. The first two scripts are the game's own pins (FletcherEngineTest's
// ActiveTimeRunsFromTheFirstPressMadeAheadOfTheCue and its idle companion) on the same numbers; the
// other two are properties of the arm the game's fixtures cannot see (see each one).
//
// EMITTED WITH THEIR SCRIPTS, unlike every other section here, because the cross-repo arm
// (Typebeat.WireCompat.WpmClockArmLiveParityTest) replays THESE steps through the game's own
// TypingEngine and compares the readings step for step. One copy of the keystrokes, two engines.
{
    // Two maps, and the difference between them is only how many cells L1 has.
    //
    // twoLine is the game's twoLineMap: L0 "ab cd" with a = 1000, b = 1500, ' ' = 2000, c = 2000,
    // d = 2500, and L1 "ef" activating at its own 4000 start, so entry into L1 opens at
    // 4000 - FLETCHER_DRAG_GRACE_MS = 2500, which is exactly where 'd' finishes L0: the roll happens
    // on that press and the caret is on L1 a full 1500 ms early.
    //
    // longTail is the same map with L1 widened to "efgh" (sung over the same second, so the four
    // cells step 250: e = 4000, f = 4250, g = 4500, h = 4750). Its only purpose is that L1 is still
    // INCOMPLETE after two presses, which is what makes "only the first press arms" observable at
    // all: on a two-cell line the second press completes it and the caller stops accruing, so a
    // second press that wrongly re-armed would leave no trace.
    const CLOCK_ARM_MAPS = {
        twoLine: TWO_LINES,
        longTail: osu([
            { text: 'ab cd', start_ms: 1000, end_ms: 3000, words: [word('ab', 1000, 2000), word('cd', 2000, 3000)] },
            { text: 'efgh', start_ms: 4000, end_ms: 5000, words: [word('efgh', 4000, 5000)] }
        ], 12000)
    };

    const finishLineZero = [
        { op: 'update', t: 1000 }, { op: 'key', c: 'a', t: 1000 },
        { op: 'update', t: 1500 }, { op: 'key', c: 'b', t: 1500 },
        { op: 'update', t: 2000 }, { op: 'key', c: ' ', t: 2000 }, { op: 'key', c: 'c', t: 2000 },
        { op: 'update', t: 2500 }, { op: 'key', c: 'd', t: 2500 },
    ];

    const scripts = {
        // TYPING through the head start. The press at 2600 arms the clock and is itself credited no
        // elapsed time; the frame to 2700 credits exactly the 100 ms since the arm.
        typed: {
            map: 'twoLine',
            steps: [
                ...finishLineZero,
                { op: 'update', t: 2600 }, { op: 'key', c: 'e', t: 2600 },
                { op: 'update', t: 2700 }, { op: 'key', c: 'f', t: 2700 },
            ]
        },
        // The IDLE companion: the same head start, no press made on it, so nothing is credited for
        // it. The clock picks up on the ordinary rule at L1's own 4000 cue.
        idle: {
            map: 'twoLine',
            steps: [
                ...finishLineZero,
                { op: 'update', t: 2600 }, { op: 'update', t: 3000 }, { op: 'update', t: 3500 },
                { op: 'update', t: 4000 }, { op: 'update', t: 4500 },
            ]
        },
        // A press stamped AHEAD of the frame that follows it, which the browser can produce on its
        // own (a keypress reads the audio clock where the render loop carries the last frame's
        // stamp). The clock must credit zero for that frame rather than negative time, and must
        // still run from the press onward.
        aheadOfTheFrame: {
            map: 'twoLine',
            steps: [
                ...finishLineZero,
                { op: 'update', t: 2600 }, { op: 'key', c: 'e', t: 2900 },
                { op: 'update', t: 2700 }, { op: 'update', t: 3000 },
            ]
        },
        // ONLY THE FIRST PRESS ARMS. Two presses land ahead of the cue before the next frame, and
        // the frame that follows must credit from the FIRST of them (2700 - 2600 = 100). An arm that
        // moved with each press would credit 2700 - 2650 = 50 and swallow the 50 ms the player spent
        // typing between them, which is the same "characters counted over uncounted time" defect one
        // press smaller.
        secondPressKeepsTheFirstArm: {
            map: 'longTail',
            steps: [
                ...finishLineZero,
                { op: 'update', t: 2600 }, { op: 'key', c: 'e', t: 2600 }, { op: 'key', c: 'f', t: 2650 },
                { op: 'update', t: 2700 }, { op: 'key', c: 'g', t: 2700 },
            ]
        },
    };

    const runs = {};

    for (const name of Object.keys(scripts)) {
        const engine = new TB.TypingEngine(build(CLOCK_ARM_MAPS[scripts[name].map]));
        const readings = [];

        let breaks = 0;
        engine.onComboBroken = () => { breaks++; };

        for (const step of scripts[name].steps) {
            let handled = null;

            if (step.op === 'update') engine.update(step.t);
            else handled = engine.processKey(step.c, step.t);

            readings.push({
                op: step.op,
                t: step.t,
                c: step.c === undefined ? null : step.c,
                handled: handled,
                at: at(engine),
                activeTimeMs: engine.activeTimeMs,
                wpm: engine.liveWpm
            });
        }

        runs[name] = {
            map: scripts[name].map,
            script: scripts[name].steps,
            readings: readings,
            combo: engine.combo,
            maxCombo: engine.maxCombo,
            comboBreaks: breaks,
            mistypes: engine.mistypes
        };
    }

    const fixtures = {};

    for (const name of Object.keys(CLOCK_ARM_MAPS)) {
        const fixture = new TB.TypingEngine(build(CLOCK_ARM_MAPS[name]));

        fixtures[name] = {
            entryOpensAt: fixture.entryOpensAt(1),
            lines: fixture.lines.map(l => ({
                activationTime: l.activationTime,
                endTime: l.endTime,
                sealGraceMs: l.sealGraceMs,
                cells: l.cells.map(c => ({ expected: c.expected, target: c.target }))
            }))
        };
    }

    out.wpmClockArm = {
        // Pinned before the readings are, so a fixture that drifted cannot be read as an engine
        // divergence by the cross-repo arm.
        dragGraceMs: TB.constants.FLETCHER_DRAG_GRACE_MS,
        fixtures: fixtures,
        runs: runs
    };
}

// THE LINE SKIP (backlog 241): Enter gives up the rest of the active line and moves the player on.
// It is CARET MOVEMENT AND NOTHING ELSE, and that claim is the whole of what this section exists to
// hold, because it is what lets the skip exist without an era bit on the desktop: the cells left
// behind stay untyped and are judged by the SEAL, at the abandoned line's own deadline, with that
// line's one combo break, exactly as they would be for a player who simply stopped typing and sat
// there.
//
// The state that claim rests on is lineAbandoned: sealPermitted defers a line's seal by
// FLETCHER_DRAG_GRACE_MS while the player is still on it, and a skip moves the caret off, so without
// the flag the abandoned line would seal up to 1500 ms EARLY. That is not a cosmetic difference. The
// seal is where the misses land and where the combo break is taken, so an early seal re-prices every
// keypress the player makes on the NEXT line in between at a combo they had not actually lost yet,
// and the browser would then submit a different score for the same fingers than the desktop does.
//
// EMITTED WITH ITS SCRIPT, like the clock-arm section above and for the same reason: the cross-repo
// arm (Typebeat.WireCompat.LineSkipLiveParityTest) replays these very steps through the game's own
// TypingEngine, feeding ProcessEnter for the third op this section introduces, and compares the
// readings step for step. One copy of the keystrokes, two engines.
{
    // Three contiguous lines, all loader-built (no hand splicing), chosen so that both halves of the
    // skip are reachable on ONE map and the abandoned lines' seals are 1500 ms apart from where they
    // would land without the grace.
    //   L0 "ab cd" [1000, 4000), grace 0: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
    //   L1 "ef"    [4000, 8000), grace 0: e = 4000, f = 4500. Activation 4000, so entry opens 2500.
    //   L2 "gh"    [8000, 12000), grace 0: g = 8000, h = 8500. Activation 8000, entry opens 6500.
    //
    // So an Enter at 2500 is INSIDE line 1's entry window (the hand-over happens on the press) and
    // an Enter at 5600 is OUTSIDE line 2's (the caret parks and the snap performs the hand-over at
    // 6500), which are the two arms ProcessEnter delegates to and the two the script has to reach.
    // L0's untyped tail would seal at 4000 flat with the caret gone and does not seal until 5500
    // with the grace held, and L1's at 8000 against 9500: both windows contain a keypress on the
    // NEXT line, which is precisely where an early seal would show up as a different combo.
    const LINE_SKIP = osu([
        { text: 'ab cd', start_ms: 1000, end_ms: 3000, words: [word('ab', 1000, 2000), word('cd', 2000, 3000)] },
        { text: 'ef', start_ms: 4000, end_ms: 5000, words: [word('ef', 4000, 5000)] },
        { text: 'gh', start_ms: 8000, end_ms: 9000, words: [word('gh', 8000, 9000)] }
    ], 20000);

    const steps = [
        // Two characters of L0, then walk out of it with three cells (' ', c, d) still untyped.
        { op: 'update', t: 1000 }, { op: 'key', c: 'a', t: 1000 },
        { op: 'update', t: 1500 }, { op: 'key', c: 'b', t: 1500 },

        // THE SKIP INSIDE THE ENTRY WINDOW. 2500 is exactly where entry into L1 opens, so the park
        // and the roll happen on the one press and the caret is on L1 straight away, a full 1500 ms
        // before its cue: the same hand-over the 'd' that FINISHES L0 would have got.
        { op: 'update', t: 2500 }, { op: 'enter', t: 2500 },

        // A press on the line the player landed on, made while the line they abandoned has not
        // sealed. Its combo is the reading the grace is worth: L0's hard deadline is 4000, and with
        // the grace held nothing has broken yet, so this is the third of an unbroken run. Seal L0
        // early and it is the first of a fresh one.
        { op: 'update', t: 4000 }, { op: 'key', c: 'e', t: 4000 },

        // 5500 = L0's deadline plus the drag grace: the abandoned line seals HERE, three cells
        // missed and one combo break, which is the instant it would have sealed for a player still
        // sitting on it doing nothing.
        { op: 'update', t: 5500 },

        // THE SKIP OUTSIDE THE ENTRY WINDOW. L1 is walked out of with 'f' untyped at 5600, and entry
        // into L2 does not open until 6500, so the roll is refused and the caret PARKS past L1's
        // last cell.
        { op: 'update', t: 5600 }, { op: 'enter', t: 5600 },

        // An Enter on a caret that is already parked is INERT: nothing left to give up, so the
        // engine reports it did nothing (which is what tells the input layer not to swallow the key)
        // and no second line is abandoned by the same press.
        { op: 'update', t: 5700 }, { op: 'enter', t: 5700 },

        // One frame short of the bound, then the frame that opens it: the deferred hand-over is the
        // ordinary line-start snap, with no rule of its own for a caret the skip parked.
        { op: 'update', t: 6499 },
        { op: 'update', t: 6500 },

        // Typing on the line after the skip, again inside the abandoned line's held grace (L1 seals
        // at 9500, not at its 8000 deadline).
        { op: 'update', t: 8000 }, { op: 'key', c: 'g', t: 8000 },
        { op: 'update', t: 8500 }, { op: 'key', c: 'h', t: 8500 },

        // L1's seal, held to its own deadline plus the grace exactly as L0's was.
        { op: 'update', t: 9500 },

        // An Enter on a line the player TYPED OUT is inert for the same reason the parked one is:
        // the caret is past the last cell, and the two time-driven arms already own it.
        { op: 'update', t: 9600 }, { op: 'enter', t: 9600 },

        // The last line's own deadline: the run finishes with exactly the four cells the two skips
        // gave up missed, and nothing else.
        { op: 'update', t: 12000 },
    ];

    const engine = new TB.TypingEngine(build(LINE_SKIP));

    let breaks = 0;
    engine.onComboBroken = () => { breaks++; };

    const readings = [];

    for (const step of steps) {
        let handled = null;

        if (step.op === 'update') engine.update(step.t);
        else if (step.op === 'enter') handled = engine.processEnter(step.t);
        else handled = engine.processKey(step.c, step.t);

        readings.push({
            op: step.op,
            t: step.t,
            c: step.c === undefined ? null : step.c,
            handled: handled,
            at: at(engine),
            // The SEAL is what this section is about, so every reading carries where it has got to
            // and what it has resolved: a grace lost by a line the caret walked out of moves both,
            // and moves them before it moves any cell state.
            nextUnsealedLineIndex: engine.nextUnsealedLineIndex,
            states: engine.lines.map(l => l.cells.map(c => c.state)),
            combo: engine.combo,
            maxCombo: engine.maxCombo,
            comboBreaks: breaks,
            mistypes: engine.mistypes,
            finished: engine.finished
        });
    }

    const fixture = new TB.TypingEngine(build(LINE_SKIP));

    out.lineSkip = {
        // Pinned before the readings are, so a fixture that drifted cannot be read as an engine
        // divergence by the cross-repo arm.
        dragGraceMs: TB.constants.FLETCHER_DRAG_GRACE_MS,
        entryOpensAt: [fixture.entryOpensAt(1), fixture.entryOpensAt(2)],
        lines: fixture.lines.map(l => ({
            activationTime: l.activationTime,
            endTime: l.endTime,
            sealGraceMs: l.sealGraceMs,
            cells: l.cells.map(c => ({ expected: c.expected, target: c.target }))
        })),
        script: steps,
        readings: readings,
        combo: engine.combo,
        maxCombo: engine.maxCombo,
        comboBreaks: breaks,
        mistypes: engine.mistypes
    };
}

process.stdout.write(JSON.stringify(out));
