// Node harness for PR 43's two browser-visible engine rules, emitted as JSON on stdout for the
// cross-repo arm (Typebeat.WireCompat.BestRetypeJudgementParityTest), which replays the same steps
// through the game's own TypingEngine and TypeBeatReplayScorer.
//
//   retype     BEST RETYPE JUDGEMENT (extended CONFIG bit 13, TypingEngine.BestRetypeJudgement):
//              retyping a backspaced cell keeps the BETTER of its two grades. The tier counts and
//              the cell's stored delta move to the better press, the submitted account amends the
//              cell's existing result in place, and neither combo nor HP moves. Every scenario runs
//              on two arms: `live` is the browser engine as /play runs it, `legacy` stubs the rule
//              back to the pre-PR 43 one (the first award stands), which is what the C# re-derives
//              with the bit clear.
//
//   paused     PAUSED-WORD PUNCTUATION (TypingLine.build, no era): a mark inside a paused word is
//              timed within the stretch on its own side of the rest, never inside the silence.
//              Emitted as cell targets, both streams (the default one /play types, and Literate).
//
// Usage: node CoreBestRetypeHarness.cjs <absolute path to typebeat-core.js>

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

function word(text, start, end, extra) { return Object.assign({ text: text, start_ms: start, end_ms: end, score: 1 }, extra || {}); }

function build(text, literate) { return TB.buildBeatmap(TB.parseLyricOsu(text), literate === true); }

const out = {};

// ---------------------------------------------------------------------------------------------
// THE RETYPE FIXTURE. One line, "ab cde", with a second of silence between its words so a press on
// "cde"'s opening cell can land well EARLY and still be the next cell the caret takes.
//   "ab" [1000, 2000]: one syllable group (an unsubdivided word is one group since backlog 363).
//   "cde" [3000, 4000]: likewise. Its OPENING cell 'c' is judged from the span's START (bit 8), so a
//   press at t is graded on t - 3000; 'd' and 'e' are judged on their distance outside [3000, 4000].
// Every scenario leaves 'e' for last, because the line seals the moment it is fully typed (early
// finish) and a sealed cell can no longer be retyped.
// The ladder is the symmetric one (Great 150, Ok 300, Meh 600, inclusive), so on 'c':
//   2300 -> -700 Premature, 2450 -> -550 Meh, 2500 -> -500 Meh, 2600 -> -400 Meh, 2700 -> -300 Ok,
//   2750 -> -250 Ok, 2850 -> -150 Great, 2900 -> -100 Great, 3700 -> +700 Lagging.
// Every retype lands LATER than the press it follows (replay frames are in time order), which is why
// an improvement here is always an early press retyped closer to the beat.
const RETYPE = osu([
    { text: 'ab cde', start_ms: 1000, end_ms: 4000, words: [word('ab', 1000, 2000), word('cde', 3000, 4000)] }
], 20000);

// "ab" and its gap on the beat: a run of 3 before any scenario touches "cd".
const HEAD = [
    { op: 'update', t: 1000 },
    { op: 'key', c: 'a', t: 1000 },
    { op: 'key', c: 'b', t: 1100 },
    { op: 'key', c: ' ', t: 2100 }
];

const bs = t => ({ op: 'backspace', t: t });
const key = (c, t) => ({ op: 'key', c: c, t: t });

const RETYPE_SCENARIOS = {
    // The reported case: a Meh retyped on the beat becomes a Great.
    mehThenGreat: [key('c', 2600), bs(2610), key('c', 2850), key('d', 2900), key('e', 3000)],
    // A worse retype never takes a better award away (here an off-time Lagging after a Great).
    greatStaysGreat: [key('c', 2900), bs(2910), key('c', 3700), key('d', 3750), key('e', 3800)],
    // Meh, then Ok (an upgrade), then another Ok (EQUAL, so the stored delta stays -300), then Great
    // (an upgrade), then another Great (equal again, the -100 stands).
    repeatedRetypes: [key('c', 2500), bs(2510), key('c', 2700), bs(2710), key('c', 2750), bs(2760),
        key('c', 2900), bs(2910), key('c', 2950), key('d', 3000), key('e', 3100)],
    // An off-time press (0 on the tier ladder) retyped inside the Meh window: the engine moves the
    // count from Premature to Meh while the submitted result (meh either way) does not move at all.
    offTimeToMeh: [key('c', 2300), bs(2310), key('c', 2500), key('d', 2600), key('e', 3000)],
    // A cell that held a wrong character before it was first judged keeps the CORRECTION CAP on its
    // retype: Meh climbs to Ok, and a Great-timed retype is awarded Ok, not Great.
    correctionCap: [key('x', 2500), bs(2510), key('c', 2550), bs(2560), key('c', 2950), key('d', 3000), key('e', 3100)],
    // REWIND: two judged cells backspaced together and retyped in order, each upgraded on its own.
    rewind: [key('c', 2450), key('d', 2500), bs(2510), bs(2520), key('c', 2900), key('d', 2950), key('e', 3000)]
};

function at(engine) { return { line: engine.activeLineIndex, cell: engine.caretIndex }; }

const TIERS = ['Great', 'Ok', 'Meh', 'Premature', 'Lagging'];

function tierCounts(engine) {
    const counts = {};
    for (const tier of TIERS) counts[tier] = engine.counts[tier] || 0;
    return counts;
}

function play(steps, arm) {
    const engine = new TB.TypingEngine(build(RETYPE));

    // The pre-PR 43 rule, the one the C# re-derives with bit 13 clear: the first award stands.
    if (arm === 'legacy') engine.bestRetypeTier = (cell, standing) => standing;

    const readings = [];

    for (const step of steps) {
        let handled = null;

        if (step.op === 'update') engine.update(step.t);
        else if (step.op === 'backspace') handled = engine.processBackspace();
        else handled = engine.processKey(step.c, step.t);

        readings.push({
            op: step.op,
            t: step.t,
            handled: handled,
            at: at(engine),
            nextUnsealedLineIndex: engine.nextUnsealedLineIndex,
            finished: engine.finished,
            states: engine.lines.map(l => l.cells.map(c => c.state)),
            // The award and the delta a CORRECT cell stands on (both sides clear or keep them
            // differently on an erased cell, so only correct cells are read).
            judged: engine.lines.map(l => l.cells.map(c => c.state === 'correct' ? { tier: c.judgeType, delta: c.judgedDelta } : null)),
            counts: tierCounts(engine),
            combo: engine.combo,
            maxCombo: engine.maxCombo,
            processorCombo: engine.processor.combo,
            processorHighestCombo: engine.processor.highestCombo,
            health: engine.healthAccount.value
        });
    }

    const score = TB.computeScore(engine);

    return {
        readings: readings,
        submitted: {
            maxCombo: score.maxCombo,
            totalScore: score.totalScore,
            accuracy: score.accuracy,
            completion: score.completion,
            rank: score.rank,
            statistics: score.statistics,
            maximumStatistics: score.maximumStatistics
        }
    };
}

{
    const runs = {};

    for (const name of Object.keys(RETYPE_SCENARIOS)) {
        // Every press is followed by the frame update a live play runs at the same instant, so the
        // bare engines see the seal exactly when the replay scorer's frame loop does.
        const steps = [];
        for (const step of [...HEAD, ...RETYPE_SCENARIOS[name]]) {
            steps.push(step);
            if (step.op !== 'update') steps.push({ op: 'update', t: step.t });
        }
        steps.push({ op: 'update', t: 8000 });
        runs[name] = { script: steps, live: play(steps, 'live'), legacy: play(steps, 'legacy') };
    }

    const fixture = new TB.TypingEngine(build(RETYPE));

    out.retype = {
        fixture: {
            lines: fixture.lines.map(l => ({
                startTime: l.startTime,
                activationTime: l.activationTime,
                endTime: l.endTime,
                sealGraceMs: l.sealGraceMs,
                cells: l.cells.map(c => ({ expected: c.expected, target: c.target }))
            }))
        },
        runs: runs
    };
}

// ---------------------------------------------------------------------------------------------
// THE PAUSED-PUNCTUATION FIXTURE. One line, "well-known yes,no".
//   "well-known" [1000, 3000], rest [1500, 2000] cut at 5, so its stretches are "well-" over
//     [1000, 1500] and "known" over [2000, 3000]. The hyphen ENDS the first stretch: it is timed
//     between 'l' (1375) and the stretch's own end (1500), 1437.5, where interpolating across the
//     whole token put it at 1687.5, inside the rest.
//   "yes,no" [3000, 4000], rest [3400, 3600] cut at 3, so its stretches are "yes" over [3000, 3400]
//     and ",no" over [3600, 4000]. The comma OPENS the second stretch: it is timed between that
//     stretch's start (3600) and 'n' (3600), where the token-wide rule put it at 3433.33.
// The default stream deletes the comma and types the hyphen as a space, which keeps the hyphen's
// target; Literate types both marks.
const PAUSED = osu([
    {
        text: 'well-known yes,no', start_ms: 1000, end_ms: 4000,
        words: [
            word('well-known', 1000, 3000, { pauses: [{ start_ms: 1500, end_ms: 2000, split: 5 }] }),
            word('yes,no', 3000, 4000, { pauses: [{ start_ms: 3400, end_ms: 3600, split: 3 }] })
        ]
    }
], 20000);

out.paused = {};

for (const stream of ['default', 'literate']) {
    const beatmap = build(PAUSED, stream === 'literate');
    out.paused[stream] = beatmap.lines.map(l => l.cells.map(c => ({ expected: c.expected, target: c.target, typeable: c.typeable })));
}

process.stdout.write(JSON.stringify(out));
