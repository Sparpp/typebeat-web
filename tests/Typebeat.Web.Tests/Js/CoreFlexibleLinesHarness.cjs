// Node harness that loads the browser scoring core (typebeat-core.js) and exercises the FLEXIBLE
// LINES default (backlog 208): the caret is decoupled from the song's playhead, so finishing a line
// opens the next one at once (RUSH FREEDOM), a line the player is still typing is not snatched at
// its deadline (DRAG FREEDOM), a press that puts the caret too far past the playhead earns no combo
// (the RUSH CAP), and a caret parked past the end of a FINISHED line is handed on the moment the
// next line starts (the LINE-START SNAP). The observations are emitted as JSON on stdout so the C#
// fidelity test (FlexibleLinesParityTest) can assert them against the game's golden values, which
// come from typebeat-osu's NonVisual/FletcherEngineTest.cs.
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

/** Where the caret is, as the two numbers every observation below is written in. */
function at(engine) { return { line: engine.activeLineIndex, cell: engine.caretIndex }; }

// ---------------------------------------------------------------------------------------------

const out = {};

// The browser's own settings, which are the LIVE ones unconditionally: it has no mods payload, so
// the strict pinning mod (acronym FC) is unreachable in it, and no replay input, so the pinned era
// every pre-208 row was played in is unreachable too.
{
    const engine = new TB.TypingEngine(build(TWO_LINES));

    out.defaults = {
        fletcherEnabled: engine.fletcherEnabled,
        flexibleLineSnap: engine.flexibleLineSnap,
        maxCharsAhead: TB.constants.FLETCHER_MAX_CHARS_AHEAD,
        dragGraceMs: TB.constants.FLETCHER_DRAG_GRACE_MS
    };
}

// RUSH FREEDOM: typing L0 out at 2500, a second and a half before L1's own 4000 cue, puts the caret
// on L1 at once, and a press then lands on L1's first cell. Under a pinned caret the same press is
// inert (no line is active until 4000) and L1's 'e' would seal a miss.
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
        afterFinishing: afterFinishing,
        pressHandled: handled,
        afterPress: at(engine),
        firstCellOfNextLine: engine.lines[1].cells[0].state,
        // The line left behind is UNSEALED and stays that way until its own deadline: rush freedom
        // moves the player, never the song.
        nextSealIndex: engine.nextSealIndex
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

// THE LINE-START SNAP. Typing L0 out rolls the caret straight on to the cell-less L1, where it is
// complete on arrival and stuck: no press of the player's can ever finish it. One frame short of
// L2's 10500 cue nothing has moved (the snap is the LINE STARTING, not the caret being idle); at
// 10500 L2 takes it, with L1 still unsealed, which is what proves the SNAP moved the caret rather
// than a seal.
{
    const engine = new TB.TypingEngine(parked());

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(1500);
    engine.processKey('b', 1500);

    const parkedOn = at(engine);

    engine.update(10499);
    const oneFrameShort = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    engine.update(10500);
    const snapped = { at: at(engine), nextSealIndex: engine.nextSealIndex };

    const handled = engine.processKey('c', 12000);

    out.lineStartSnap = {
        middleLineCellCount: engine.lines[1].cells.length,
        nextLineActivation: engine.lines[2].activationTime,
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
{
    const engine = new TB.TypingEngine(build(GAPPED));

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('b', 2000);

    const readings = [];

    for (const t of [2000, 5000, 12000, 20000]) {
        engine.update(t);
        readings.push({
            time: t,
            at: at(engine),
            songWindowOpen: engine.songWindowOpen,
            songIsOnTheCaretsLine: engine.songIsOnTheCaretsLine
        });
    }

    out.songOnTheCaretsLine = { readings: readings };
}

// THE WPM CLOCK is suspended while the caret is parked ahead of the cue. A player who finishes L0
// at 2000 and waits out an eighteen-second instrumental has not been typing for eighteen seconds,
// so the clock runs only from the point the playhead reaches the parked line's own activation.
{
    const engine = new TB.TypingEngine(build(GAPPED));

    engine.update(1000);
    engine.processKey('a', 1000);
    engine.update(2000);
    engine.processKey('b', 2000);

    // Typing L0 took a second, which is the whole of the clock so far.
    const afterTyping = engine.activeTimeMs;

    engine.update(19000); // seventeen seconds of parked instrumental
    const parkedTime = engine.activeTimeMs;

    engine.update(20000); // the frame that ENDS at the cue still measures parked time
    const atTheCue = engine.activeTimeMs;

    engine.update(21000); // and this one is real typing time again
    const runningTime = engine.activeTimeMs;

    out.wpmClock = {
        nextLineActivation: engine.lines[1].activationTime,
        activeTimeAfterTyping: afterTyping,
        activeTimeWhileParked: parkedTime,
        activeTimeAtTheCue: atTheCue,
        activeTimeAfterTheCue: runningTime
    };
}

process.stdout.write(JSON.stringify(out));
