// Node harness for the HOLD parity sweep: the browser's port of the game's holds
// (TypingEngine.Holds.cs, HeldWord.cs, HoldTicks.cs, HoldTailRule.cs and the hold half of the score
// and health processors) held against the GAME's own TypeBeatReplayScorer and TypeBeatHealthProcessor
// by HoldLiveParityTest.cs, field for field and with no tolerance.
//
// It is CoreFuzzHarness.cjs's shape with one difference that matters: the FIXTURES COME FROM THE
// C# SIDE. HoldLiveParityTest writes each map through the game's own LyricOsuFormat.GenerateOsu (the
// encoder an upload is written by) into a JSON file of { name: osuText } and passes its path as the
// second argument; this harness reads the very same text through parseLyricOsu and buildBeatmap, and
// the test decodes it through the game's own LyricBeatmapDecoder. So the hold fields are pinned from
// the stored bytes on both sides, not from two hand-written transcriptions of one fixture.
//
// What is generated: streams of presses, typos, backspaces, word skips, Enters, idles AND KEY
// RELEASES (TypeBeatReplayFrame.RELEASE, '\u0003', played through processRelease), with a release
// plan rolled for every hold a press opens: early, into the Ok and Meh bands, inside the Great slack,
// at or past the end (where the engine has completed the hold first and the release is inert, so it
// is not recorded, exactly as the desktop's releaseHold records only a release the engine took) or
// never. Each stream is played in one of two INPUT MODES. LIVE is the desktop key handler's
// discipline (TypeBeatPlayfield.KeyHandler.OnKeyDown, which the browser's key handler mirrors):
// any other key going down ends the hold first, as its own release frame. RAW records no such
// release, which is the shape autoplay writes and the one that reaches the engine's own closes: a
// hand-off pressed with its hold still down, a second head opened over an open hold, a hold that
// runs to its end under a word skip, a backspace or a typo, and a hold left down while the caret
// rushes on to the next line.
//
// Playback is TypeBeatReplayScorer.feed exactly (the 1000/60 ms cadence loop, each due frame applied
// as update(t) then the frame), and every run plays under the HP pool's No Fail veto, as the C# arm
// does, so the account and the bar can both be held against the game's.
//
// Usage: node CoreHoldsHarness.cjs <absolute path to typebeat-core.js> <fixtures json path>

'use strict';

const fs = require('fs');

const corePath = process.argv[2];
const fixturesPath = process.argv[3];

if (!corePath || !fixturesPath) {
    process.stderr.write('usage: CoreHoldsHarness.cjs <typebeat-core.js> <fixtures.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

// ReplayEngineFeed.FRAME_MS and TypeBeatReplayScorer.tail_ms (see CoreFuzzHarness.cjs).
const FRAME_MS = 1000.0 / 60;
const TAIL_MS = 10000;

// The replay feed's sentinels (TypeBeatReplayFrame.BACKSPACE / ENTER / RELEASE).
const BS = '\b';
const ENTER = '\n';
const RELEASE = '\u0003';

const FIXTURES = JSON.parse(fs.readFileSync(fixturesPath, 'utf8'));
const NAMES = Object.keys(FIXTURES);

function build(name) {
    return TB.buildBeatmap(TB.parseLyricOsu(FIXTURES[name]), false);
}

/** Numerical Recipes' LCG, so a seed reproduces a case exactly on any machine. */
function lcg(seed) {
    let s = (seed >>> 0) || 1;
    return function () {
        s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
        return s / 4294967296;
    };
}

// The fuzz sweep's offsets (CoreFuzzHarness.cjs), every band of the one ladder on both sides.
const OFFSETS = [0, 90, -90, 220, -220, 450, -450, 800, -800, 1400, -1100];
const LETTERS = 'abcdefghijklmnopqrstuvwxyz';

// ---------------------------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------------------------

/**
 * One generated run on a throwaway engine, so the generator always knows the character the caret
 * wants and which hold is down. Hold decisions are rolled on a stream of their own.
 */
function generate(name, seed, spaceSkipsWord, live) {
    const rnd = lcg(seed);
    const hr = lcg(seed ^ 0x6d2b79f5);
    const engine = new TB.TypingEngine(build(name));
    engine.spaceSkipsWord = spaceSkipsWord;
    engine.healthAccount.failed = () => false;

    const keys = [];
    let t = 0;
    let last = 0;
    let releaseAt = null;

    // A release at `time`, recorded only when the engine took it (the desktop's releaseHold).
    function release(time) {
        time = Math.max(time, last);
        engine.update(time);
        if (engine.processRelease(time)) {
            keys.push([time, RELEASE]);
            last = time;
        }
        releaseAt = null;
        t = Math.max(t, time);
    }

    // Everything that has to happen before a frame at `time` goes down: a planned release that is
    // due, then (LIVE) the release the key going down forces.
    function beforeFrame(time) {
        if (engine.activeHold === null) releaseAt = null;
        if (engine.activeHold !== null && releaseAt !== null && releaseAt <= time) release(releaseAt);
        if (live && engine.activeHold !== null) release(time);
    }

    // After a press: a hold it opened gets a release plan.
    function planRelease(before) {
        const hold = engine.activeHold;

        if (hold === null || hold === before) return;

        const span = hold.endTime - Math.max(hold.startTime, hold.pressTime);
        const slack = Math.max(80, 0.1 * Math.max(1, hold.endTime - hold.startTime));
        const r = hr();

        if (r < 0.15) releaseAt = hold.pressTime + Math.floor((0.05 + 0.4 * hr()) * span);
        else if (r < 0.35) releaseAt = Math.max(hold.startTime, hold.pressTime) + Math.floor((0.5 + 0.4 * hr()) * span);
        else if (r < 0.55) releaseAt = hold.endTime - 1 - Math.floor(hr() * slack);
        else if (r < 0.70) releaseAt = hold.endTime + Math.floor(hr() * 300);
        else releaseAt = null;

        if (releaseAt !== null && releaseAt < hold.pressTime) releaseAt = hold.pressTime;
    }

    function press(time, ch) {
        time = Math.max(time, last);
        beforeFrame(time);
        time = Math.max(time, last);
        const before = engine.activeHold;

        keys.push([time, ch]);
        last = time;
        engine.update(time);
        if (ch === BS) engine.processBackspace();
        else if (ch === ENTER) engine.processEnter(time);
        else engine.processKey(ch, time);
        t = Math.max(t, time);

        planRelease(before);
    }

    function idle(until) {
        if (engine.activeHold !== null && releaseAt !== null && releaseAt <= until) release(releaseAt);
        t = Math.max(t, until);
    }

    for (let step = 0; step < 160 && !engine.finished; step++) {
        engine.update(t);

        if (engine.activeLineIndex < 0) {
            idle(t + 120 + Math.floor(rnd() * 900));
            continue;
        }

        const cell = engine.caretCell;
        const line = engine.lines[engine.activeLineIndex];

        // A finished caret: the manual newline (a space or an Enter) most of the time, else a wait.
        if (cell === null) {
            const r = rnd();
            if (engine.activeLineIndex + 1 < engine.lines.length && r < 0.7) {
                press(t + Math.floor(rnd() * 700), r < 0.45 ? ' ' : ENTER);
                continue;
            }
            idle(t + 120 + Math.floor(rnd() * 900));
            continue;
        }

        if (engine.awaitingEntry) {
            idle(t + 100 + Math.floor(rnd() * 500));
            continue;
        }

        const roll = rnd();

        if (roll < 0.08) {
            idle(t + 250 + Math.floor(rnd() * 1400));
            continue;
        }

        if (roll < 0.17) {
            // A backspace, which under a hold just released is the release-and-repress shape: the
            // held letter erased and typed again.
            press(t + Math.floor(rnd() * 120), BS);
            continue;
        }

        if (roll < 0.19) {
            press(t, ENTER);
            continue;
        }

        let ch;

        if (roll < 0.28 && cell.expected !== ' ') {
            do { ch = LETTERS[Math.floor(rnd() * LETTERS.length)]; } while (ch === cell.expected);
        } else if (roll < 0.34) {
            ch = ' ';
        } else {
            ch = cell.expected;
        }

        // Press at the cell's own target plus an offset; a HELD head half the time near its hold's
        // start instead; a HAND-OFF half the time inside its hold's release slack.
        let aim = cell.target + OFFSETS[Math.floor(rnd() * OFFSETS.length)];

        if (cell.holdStart !== null && hr() < 0.5) aim = cell.holdStart + OFFSETS[Math.floor(hr() * 5)];

        if (cell.handsOffFrom !== null && hr() < 0.5) {
            const held = line.cells[cell.handsOffFrom];
            const slack = Math.max(80, 0.1 * Math.max(1, held.holdEnd - held.holdStart));
            aim = held.holdEnd - 1 - Math.floor(hr() * slack);
        }

        press(Math.max(t, aim), ch);
    }

    // A hold still down when the script runs out is let go at its plan, if it has one.
    if (engine.activeHold !== null && releaseAt !== null) release(releaseAt);

    return keys;
}

// ---------------------------------------------------------------------------------------------
// Scripted cases: the rules the generator might not roll the deciding shape for, written against
// the fixtures' own fields (held cells, hand-offs, hold spans) rather than against numbers, so a
// fixture retune cannot silently turn one into a different case.
// ---------------------------------------------------------------------------------------------

/** The first held cell of line `li` (optionally the n-th), with its line and index. */
function heldOf(beatmap, li, n = 0) {
    const line = beatmap.lines[li];
    let seen = 0;
    for (let i = 0; i < line.cells.length; i++) {
        if (line.cells[i].holdEnd !== null && seen++ === n) return { line: line, index: i, cell: line.cells[i] };
    }
    throw new Error('no held cell ' + n + ' on line ' + li);
}

/** Presses for cells [from, to) of line `li`, each on its own target (no earlier than `after`). */
function typeRange(beatmap, li, from, to, after) {
    const keys = [];
    let t = after;
    const cells = beatmap.lines[li].cells;
    for (let i = from; i < to; i++) {
        t = Math.max(t, cells[i].target);
        keys.push([t, cells[i].expected]);
    }
    return { keys: keys, time: t };
}

/** The whole line `li` typed on target, the held heads on their hold starts, and no release. */
function typeLine(beatmap, li, after) {
    const keys = [];
    let t = after;
    for (const c of beatmap.lines[li].cells) {
        t = Math.max(t, c.holdStart !== null ? Math.max(c.target, c.holdStart) : c.target);
        keys.push([t, c.expected]);
    }
    return { keys: keys, time: t };
}

const SCRIPTED = [
    {
        // A head pressed AT its hold's end: the tail misses on the spot, every tick a neutral miss.
        name: 'scripted/lateHeadMissesTheTail', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            return before.keys.concat([[h.cell.holdEnd, h.cell.expected]]);
        }
    },
    {
        // A head pressed between its ticks: the ones already due are credited as held.
        name: 'scripted/lateHeadCreditsTheTicksDue', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const press = h.cell.holdTickTimes[1] + 5;
            return before.keys.concat([[press, h.cell.expected], [h.cell.holdEnd - 10, RELEASE]]);
        }
    },
    {
        // RAW: the hand-off pressed inside the slack with the hold still down closes it at the
        // press, and the hand-off is judged dead on time.
        name: 'scripted/handoffClosesTheHold', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const next = bm.lines[0].cells[h.index + 1];
            return before.keys.concat([[h.cell.holdStart, h.cell.expected], [h.cell.holdEnd - 30, next.expected]]);
        }
    },
    {
        // Released early, then backspaced and pressed again: the retyped head cannot reopen it.
        name: 'scripted/releaseAndRepress', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const p = h.cell.holdStart;
            return before.keys.concat([[p, h.cell.expected], [p + 100, RELEASE], [p + 150, BS], [p + 200, h.cell.expected], [h.cell.holdEnd - 20, RELEASE]]);
        }
    },
    {
        // RAW: the held letter erased and typed again while still down leaves the hold as it is.
        name: 'scripted/backspaceUnderTheHold', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const p = h.cell.holdStart;
            return before.keys.concat([[p, h.cell.expected], [p + 60, BS], [p + 120, h.cell.expected], [h.cell.holdEnd - 40, RELEASE]]);
        }
    },
    {
        // RAW and LIVE side by side: a wrong key while holding, typed through with the hold down,
        // and the same key after the release the live handler records.
        name: 'scripted/wrongKeyWhileHolding', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const p = h.cell.holdStart;
            const mid = p + Math.floor((h.cell.holdEnd - p) / 2);
            return before.keys.concat([[p, h.cell.expected], [mid, 'x'], [mid + 40, BS], [h.cell.holdEnd + 50, bm.lines[0].cells[h.index + 1].expected]]);
        }
    },
    {
        name: 'scripted/wrongKeyAfterLiveRelease', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            const p = h.cell.holdStart;
            const mid = p + Math.floor((h.cell.holdEnd - p) / 2);
            return before.keys.concat([[p, h.cell.expected], [mid, RELEASE], [mid, 'x'], [mid + 40, BS]]);
        }
    },
    {
        // Never released: every hold of the map runs to its end, and the line seals behind it.
        name: 'scripted/neverReleased', fixture: 'lovesong',
        keys(bm) {
            let keys = [], t = 0;
            for (let li = 0; li < bm.lines.length; li++) {
                const typed = typeLine(bm, li, t);
                keys = keys.concat(typed.keys);
                t = typed.time;
                if (li + 1 < bm.lines.length) { t = Math.max(t, bm.lines[li].endTime - 200); keys.push([t, ' ']); }
            }
            return keys;
        }
    },
    {
        // RAW: a second head pressed while the first hold is still down releases the first at the
        // press (the next word rushed far ahead of the song).
        name: 'scripted/secondHeadOverAnOpenHold', fixture: 'lovesong',
        keys(bm) {
            const first = heldOf(bm, 0, 1);
            const second = heldOf(bm, 0, 2);
            const before = typeRange(bm, 0, 0, first.index, 0);
            let t = Math.max(before.time, first.cell.holdStart);
            const keys = before.keys.concat([[t, first.cell.expected]]);
            for (let i = first.index + 1; i <= second.index; i++) {
                t += 25;
                keys.push([t, bm.lines[0].cells[i].expected]);
            }
            return keys;
        }
    },
    {
        // The FINAL line typed out with a hold still down cannot finish early; the release lets it.
        name: 'scripted/finalLineWaitsForTheHold', fixture: 'finalHold',
        keys(bm) {
            const last = bm.lines.length - 1;
            const lead = typeLine(bm, 0, 0);
            const h = heldOf(bm, last, 0);
            const keys = lead.keys.concat([[bm.lines[0].endTime - 100, ' ']]);
            const cells = bm.lines[last].cells;
            let t = bm.lines[last].startTime;
            for (let i = 0; i < cells.length; i++) {
                t = Math.max(t, cells[i].target - 40);
                keys.push([t, cells[i].expected]);
                if (i === h.index) t = Math.max(t, cells[i].holdStart);
            }
            keys.push([Math.min(h.cell.holdEnd - 5, t + 30), RELEASE]);
            return keys;
        }
    },
    {
        // A release with no hold down, and one at the hold's own end (completed by the update that
        // precedes it): both inert on both sides.
        name: 'scripted/inertReleases', fixture: 'lovesong',
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            return [[before.keys[0][0] - 50, RELEASE]].concat(before.keys, [[h.cell.holdStart, h.cell.expected], [h.cell.holdEnd, RELEASE], [h.cell.holdEnd + 5, RELEASE]]);
        }
    },
    {
        // A word skip over the held letter, never reclaimed: the hold never opens and takes the
        // seal's neutral misses.
        name: 'scripted/skipPastTheHeadNeverReclaimed', fixture: 'lovesong',
        spaceSkipsWord: true,
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            return before.keys.concat([[before.time + 30, ' ']]);
        }
    },
    {
        // A word skip over the held letter, undone by one backspace, and the head reclaimed late.
        name: 'scripted/skipUndoneAndHeadReclaimed', fixture: 'lovesong',
        spaceSkipsWord: true,
        keys(bm) {
            const h = heldOf(bm, 0);
            const before = typeRange(bm, 0, 0, h.index, 0);
            return before.keys.concat([[before.time + 30, ' '], [before.time + 60, BS], [h.cell.holdTickTimes[0] + 1, h.cell.expected]]);
        }
    },
    {
        // RAW: the line's last letter held while the caret rushes on to the next line: the ticks and
        // the tail are credited on the held line while another is active.
        name: 'scripted/holdLeftDownAcrossTheNewline', fixture: 'crossLine',
        keys(bm) {
            const typed = typeLine(bm, 0, 0);
            const next = bm.lines[1].cells;
            let t = typed.time + 20;
            const keys = typed.keys.concat([[t, ' ']]);
            for (let i = 0; i < 3 && i < next.length; i++) {
                t += 40;
                keys.push([t, next[i].expected]);
            }
            return keys;
        }
    },
    {
        // The paused word: the letter held up to the rest hands off to nothing, the letter after
        // the rest opens its own hold.
        name: 'scripted/holdsEitherSideOfARest', fixture: 'pausedHold',
        keys(bm) {
            const a = heldOf(bm, 0, 0);
            const b = heldOf(bm, 0, 1);
            const before = typeRange(bm, 0, 0, a.index, 0);
            const keys = before.keys.concat([[Math.max(before.time, a.cell.holdStart), a.cell.expected], [a.cell.holdEnd - 10, RELEASE]]);
            const mid = typeRange(bm, 0, a.index + 1, b.index, a.cell.holdEnd);
            return keys.concat(mid.keys, [[Math.max(mid.time, b.cell.holdStart), b.cell.expected], [b.cell.holdStart + 120, RELEASE]]);
        }
    }
];

// ---------------------------------------------------------------------------------------------
// Playback: TypeBeatReplayScorer.feed, exactly.
// ---------------------------------------------------------------------------------------------
function endTimeFor(beatmap, keys) {
    const lastLine = beatmap.lines[beatmap.lines.length - 1];
    let end = beatmap.lines.length > 0 ? lastLine.endTime + lastLine.sealGraceMs + TAIL_MS : TAIL_MS;
    if (keys.length > 0) end = Math.max(end, keys[keys.length - 1][0] + TAIL_MS);
    return end;
}

function play(name, keys, spaceSkipsWord) {
    const beatmap = build(name);
    const engine = new TB.TypingEngine(beatmap);
    engine.spaceSkipsWord = spaceSkipsWord;

    // Coverage, measured on the engine's own answers rather than on the script.
    const cov = {
        tailGreat: 0, tailOk: 0, tailMeh: 0, tailMiss: 0, tailsAtTheirEnd: 0, sealTails: 0,
        tickHits: 0, tickMisses: 0, tickBreaks: 0, neutralTicks: 0, releasesTaken: 0, releasesInert: 0,
        handoffCloses: 0, handoffOnTime: 0, lateHeads: 0, lateHeadTicks: 0, holdsReleasedByAHead: 0,
        retypedHeads: 0, earlyFinishHeldOff: 0, creditsOffTheActiveLine: 0, holdsOpened: 0
    };

    engine.onHoldTailJudged = (j) => {
        if (j.atSeal) { cov.sealTails++; return; }
        cov['tail' + j.type]++;
    };

    engine.onHoldTickJudged = (j) => {
        if (j.neutral) cov.neutralTicks++;
        else if (j.hit) cov.tickHits++;
        else cov.tickMisses++;
    };

    const completeHoldsDueBy = engine.completeHoldsDueBy.bind(engine);
    engine.completeHoldsDueBy = function (time) {
        const open = engine.activeHold;
        completeHoldsDueBy(time);
        if (open !== null && engine.activeHold === null) cov.tailsAtTheirEnd++;
    };

    const closeHoldHandedOff = engine.closeHoldHandedOff.bind(engine);
    engine.closeHoldHandedOff = function (lineIndex, cellIndex, time) {
        const open = engine.activeHold;
        closeHoldHandedOff(lineIndex, cellIndex, time);
        if (open !== null && engine.activeHold === null) cov.handoffCloses++;
    };

    const openHoldIfHeld = engine.openHoldIfHeld.bind(engine);
    engine.openHoldIfHeld = function (lineIndex, cellIndex, time) {
        const cell = engine.lines[lineIndex].cells[cellIndex];
        const open = engine.activeHold;
        const ticksBefore = cov.tickHits;
        const judgedBefore = engine.judgedTails.has(lineIndex + ':' + cellIndex);
        openHoldIfHeld(lineIndex, cellIndex, time);

        if (cell.holdEnd === null) return;

        if (engine.activeHold !== null && engine.activeHold !== open) {
            cov.holdsOpened++;
            if (open !== null) cov.holdsReleasedByAHead++;
            if (cov.tickHits > ticksBefore) cov.lateHeadTicks++;
        } else if (!judgedBefore && engine.judgedTails.has(lineIndex + ':' + cellIndex)) {
            // Judged by the press itself, which only a head at or after its hold's end does.
            cov.lateHeads++;
        } else {
            // A retype of a head whose tail is judged, or of the open hold's own head.
            cov.retypedHeads++;
        }
    };

    const judgeTick = engine.judgeTick.bind(engine);
    engine.judgeTick = function (lineIndex, cellIndex, tickIndex, time, hit, neutral, breaks) {
        if (breaks) cov.tickBreaks++;
        judgeTick(lineIndex, cellIndex, tickIndex, time, hit, neutral, breaks);
    };

    const creditCombo = engine.creditCombo.bind(engine);
    engine.creditCombo = function (cellIndex, lineIndex = engine.activeLineIndex) {
        if (lineIndex !== engine.activeLineIndex) cov.creditsOffTheActiveLine++;
        creditCombo(cellIndex, lineIndex);
    };

    // A hand-off judged on time where the span alone would not have been.
    const judgedDelta = engine.judgedDeltaFor.bind(engine);
    engine.judgedDeltaFor = function (line, cellIndex, time) {
        const delta = judgedDelta(line, cellIndex, time);
        if (engine.inHandoffWindow(line, cellIndex, time)) {
            const cell = line.cells[cellIndex];
            const saved = cell.handsOffFrom;
            cell.handsOffFrom = null;
            if (judgedDelta(line, cellIndex, time) !== delta) cov.handoffOnTime++;
            cell.handsOffFrom = saved;
        }
        return delta;
    };

    // The final line typed out with a hold still down, which finishedEarly refuses.
    const finishedEarly = engine.finishedEarly.bind(engine);
    engine.finishedEarly = function (index) {
        const answer = finishedEarly(index);
        if (!answer && index === engine.lines.length - 1 && engine.holdOpenOn(index)) {
            const hold = engine.activeHold;
            engine.activeHold = null;
            if (finishedEarly(index)) cov.earlyFinishHeldOff++;
            engine.activeHold = hold;
        }
        return answer;
    };

    let failRequests = 0;
    let firstFailRequestAt = null;
    let clock = 0;
    const healthAfterKeys = [];
    let minHealth = 1;

    engine.healthAccount.failed = () => {
        failRequests++;
        if (firstFailRequestAt === null) firstFailRequestAt = clock;
        return false;
    };

    let totalScoreChecks = 0, totalScoreMismatches = 0;
    function checkTotalScore() {
        totalScoreChecks++;
        if (TB.totalScoreOf(engine) !== TB.computeScore(engine).totalScore) totalScoreMismatches++;
    }

    function apply(frame) {
        clock = frame[0];
        engine.update(frame[0]);
        if (frame[1] === BS) engine.processBackspace();
        else if (frame[1] === ENTER) engine.processEnter(frame[0]);
        else if (frame[1] === RELEASE) {
            if (engine.processRelease(frame[0])) cov.releasesTaken++;
            else cov.releasesInert++;
        }
        else engine.processKey(frame[1], frame[0]);
        healthAfterKeys.push(engine.health);
        minHealth = Math.min(minHealth, engine.health);
        checkTotalScore();
    }

    const end = endTimeFor(beatmap, keys);
    let next = 0;

    checkTotalScore();

    for (let now = 0; now <= end; now += FRAME_MS) {
        while (next < keys.length && keys[next][0] <= now) { apply(keys[next]); next++; }
        clock = now;
        engine.update(now);
        minHealth = Math.min(minHealth, engine.health);
        checkTotalScore();
    }

    while (next < keys.length) { apply(keys[next]); next++; }
    clock = end;
    engine.update(end);
    minHealth = Math.min(minHealth, engine.health);
    checkTotalScore();

    const score = TB.computeScore(engine);

    return {
        submitted: {
            statistics: score.statistics,
            maximumStatistics: score.maximumStatistics,
            maxCombo: score.maxCombo,
            totalScore: score.totalScore,
            accuracy: score.accuracy,
            completion: score.completion,
            rank: score.rank
        },
        totalScoreChecks: totalScoreChecks,
        totalScoreMismatches: totalScoreMismatches,
        engineFinished: engine.finished,
        coverage: cov,
        health: {
            afterKeys: healthAfterKeys,
            atEnd: engine.health,
            min: minHealth,
            failRequests: failRequests,
            firstFailRequestAt: firstFailRequestAt
        }
    };
}

// ---------------------------------------------------------------------------------------------
// Fixture dumps: what the browser's loader made of every line, for the C# loader to agree with.
// ---------------------------------------------------------------------------------------------
function cellsOf(name) {
    const beatmap = build(name);
    const engine = new TB.TypingEngine(beatmap);

    // The same maximum with the order taken as it comes (the browser's live targets, a stable
    // sort), so the test can prove the ported .NET order and the legacy targets decide something.
    let counter = 0, naive = 0;
    for (const line of beatmap.lines) {
        const objects = [];
        for (const c of line.cells) {
            if (!c.typeable) continue;
            objects.push({ time: c.target, base: 300 });
            for (const tick of (c.holdTickTimes || [])) objects.push({ time: tick, base: 30 });
            if (c.holdEnd !== null) objects.push({ time: c.holdEnd, base: 300 });
        }
        objects.sort((a, b) => a.time - b.time);
        for (const o of objects) { counter++; naive += o.base * Math.pow(counter, 0.5); }
    }

    return {
        maxComboPortion: TB.maxComboPortionOf(engine),
        naiveMaxComboPortion: naive,
        maximumStatistics: TB.computeScore(engine).maximumStatistics,
        lines: beatmap.lines.map(line => ({
            endTime: line.endTime,
            activationTime: line.activationTime,
            firstVocalTime: line.firstVocalTime,
            sealGraceMs: line.sealGraceMs,
            cells: line.cells.map(c => ({
                expected: c.expected,
                target: c.target,
                holdStart: c.holdStart,
                holdEnd: c.holdEnd,
                handsOffFrom: c.handsOffFrom,
                holdTickTimes: c.holdTickTimes
            })),
            cellSyllable: line.cellSyllable
        }))
    };
}

// ---------------------------------------------------------------------------------------------
const cases = [];

for (const scripted of SCRIPTED) {
    const spaceSkipsWord = scripted.spaceSkipsWord === true;
    const keys = scripted.keys(build(scripted.fixture));
    cases.push(Object.assign({ name: scripted.name, fixture: scripted.fixture, spaceSkipsWord: spaceSkipsWord, keys: keys },
        play(scripted.fixture, keys, spaceSkipsWord)));
}

for (const name of NAMES) {
    for (let seed = 1; seed <= 30; seed++) {
        for (const spaceSkipsWord of [false, true]) {
            for (const live of [true, false]) {
                const keys = generate(name, seed * 7919 + (spaceSkipsWord ? 104729 : 0) + (live ? 0 : 15485863), spaceSkipsWord, live);
                cases.push(Object.assign({
                    name: name + '/' + seed + '/' + (spaceSkipsWord ? 'skip' : 'noskip') + '/' + (live ? 'live' : 'raw'),
                    fixture: name,
                    spaceSkipsWord: spaceSkipsWord,
                    keys: keys
                }, play(name, keys, spaceSkipsWord)));
            }
        }
    }
}

const fixtures = {};
for (const name of NAMES) fixtures[name] = cellsOf(name);

process.stdout.write(JSON.stringify({ fixtures: fixtures, cases: cases }));
