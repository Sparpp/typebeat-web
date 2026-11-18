// Node harness for the HOLD LEAD-IN parity test (backlog 494): the browser's port of
// TypingLine.leadIntoHolds (typing-line.js) held against the GAME's own cells and its own
// TypeBeatReplayScorer by HoldLeadInParityTest.cs, under BOTH eras: buildBeatmap's holdLeadIn, which
// the browser plays with on, and its stored-era arm, which the game re-derives every replay recorded
// before TypingEngine.HoldLeadIn (bit 15 of the second CONFIG word) on.
//
// CoreHoldsHarness.cjs's shape: the fixtures come from the C# side, each map written through the
// game's own LyricOsuFormat.GenerateOsu into a JSON file of { name: osuText } whose path is the second
// argument, so both loaders read the very same stored bytes.
//
// What it writes: per fixture, per era and per stream (default and Literate), every line's cells and
// the line fields the targets feed (activationTime, firstVocalTime, sealGraceMs); and a set of
// scripted runs, each cue script (every letter pressed on one era's targets, a held head on its
// hold's start, shifted by an offset) played under each era, so the test can hold the submitted
// account against the game's under the header that era writes.
//
// Usage: node CoreHoldLeadInHarness.cjs <absolute path to typebeat-core.js> <fixtures json path>

'use strict';

const fs = require('fs');

const corePath = process.argv[2];
const fixturesPath = process.argv[3];

if (!corePath || !fixturesPath) {
    process.stderr.write('usage: CoreHoldLeadInHarness.cjs <typebeat-core.js> <fixtures.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

// ReplayEngineFeed.FRAME_MS and TypeBeatReplayScorer.tail_ms (see CoreFuzzHarness.cjs).
const FRAME_MS = 1000.0 / 60;
const TAIL_MS = 10000;

const FIXTURES = JSON.parse(fs.readFileSync(fixturesPath, 'utf8'));
const NAMES = Object.keys(FIXTURES);

function build(name, leadIn, literate = false) {
    return TB.buildBeatmap(TB.parseLyricOsu(FIXTURES[name]), literate, leadIn);
}

// ---------------------------------------------------------------------------------------------
// Fixture dumps
// ---------------------------------------------------------------------------------------------
function linesOf(beatmap) {
    return beatmap.lines.map(line => ({
        endTime: line.endTime,
        activationTime: line.activationTime,
        firstVocalTime: line.firstVocalTime,
        sealGraceMs: line.sealGraceMs,
        cells: line.cells.map(c => ({
            expected: c.expected,
            target: c.target,
            holdStart: c.holdStart,
            holdEnd: c.holdEnd,
            handsOffFrom: c.handsOffFrom
        }))
    }));
}

// ---------------------------------------------------------------------------------------------
// Scripts: every cell pressed on its target under one era (a held head no earlier than its hold's
// start), shifted by `offset`, kept monotonic. No release: a hold is let go by the next press or
// runs to its end, the RAW shape autoplay writes.
// ---------------------------------------------------------------------------------------------
function cues(beatmap, offset) {
    const keys = [];
    let t = 0;

    for (const line of beatmap.lines) {
        for (const c of line.cells) {
            if (!c.typeable) continue;
            const aim = (c.holdStart !== null ? Math.max(c.target, c.holdStart) : c.target) + offset;
            t = Math.max(t, aim, line.startTime);
            keys.push([t, c.expected]);
        }
    }

    return keys;
}

// ---------------------------------------------------------------------------------------------
// Playback: TypeBeatReplayScorer.feed, exactly (CoreHoldsHarness.cjs).
// ---------------------------------------------------------------------------------------------
function endTimeFor(beatmap, keys) {
    const lastLine = beatmap.lines[beatmap.lines.length - 1];
    let end = beatmap.lines.length > 0 ? lastLine.endTime + lastLine.sealGraceMs + TAIL_MS : TAIL_MS;
    if (keys.length > 0) end = Math.max(end, keys[keys.length - 1][0] + TAIL_MS);
    return end;
}

function play(name, leadIn, keys) {
    const beatmap = build(name, leadIn);
    const engine = new TB.TypingEngine(beatmap);
    // The CONFIG frame HoldLeadInParityTest writes leaves bit 1 clear.
    engine.spaceSkipsWord = false;
    engine.healthAccount.failed = () => false;

    function apply(frame) {
        engine.update(frame[0]);
        engine.processKey(frame[1], frame[0]);
    }

    const end = endTimeFor(beatmap, keys);
    let next = 0;

    for (let now = 0; now <= end; now += FRAME_MS) {
        while (next < keys.length && keys[next][0] <= now) { apply(keys[next]); next++; }
        engine.update(now);
    }

    while (next < keys.length) { apply(keys[next]); next++; }
    engine.update(end);

    const score = TB.computeScore(engine);

    return {
        statistics: score.statistics,
        maximumStatistics: score.maximumStatistics,
        maxCombo: score.maxCombo,
        totalScore: score.totalScore,
        accuracy: score.accuracy,
        completion: score.completion,
        rank: score.rank
    };
}

// ---------------------------------------------------------------------------------------------
const fixtures = {};
const cases = [];

for (const name of NAMES) {
    fixtures[name] = {
        leadIn: { plain: linesOf(build(name, true)), literate: linesOf(build(name, true, true)) },
        stored: { plain: linesOf(build(name, false)), literate: linesOf(build(name, false, true)) }
    };

    for (const offset of [0, 90, -90, 220]) {
        for (const cueEra of [true, false]) {
            const keys = cues(build(name, cueEra), offset);

            for (const leadIn of [true, false]) {
                cases.push({
                    name: name + '/' + (cueEra ? 'leadInCues' : 'evenCues') + '/' + offset + '/' + (leadIn ? 'leadIn' : 'stored'),
                    fixture: name,
                    leadIn: leadIn,
                    keys: keys,
                    submitted: play(name, leadIn, keys)
                });
            }
        }
    }
}

process.stdout.write(JSON.stringify({ fixtures: fixtures, cases: cases }));
