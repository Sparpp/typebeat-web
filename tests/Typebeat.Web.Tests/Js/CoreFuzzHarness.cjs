// Node harness for the DIFFERENTIAL parity guard on the browser scoring core (backlog 172).
//
// Every other harness beside this one drives ONE named behaviour through a hand-written keystroke
// sequence, so each pins the rule it was written for and nothing else. This one exists for the
// rules NOBODY thought to write a scenario for: it generates long, mixed keystroke streams
// (correct chars at every judgement tier, typed-through typos, backspaces, rejected keys, word
// skips, idle stretches that let lines seal) and emits BOTH the stream it played and the account
// the browser produced. EngineFuzzLiveParityTest.cs replays the very same stream through the
// game's own TypeBeatReplayScorer and compares the two accounts field for field, so a divergence
// anywhere in processKey / processBackspace / sealLine / computeScore surfaces as a failing case
// rather than waiting for somebody to guess it.
//
// THE STREAM IS EMITTED, NOT RE-DERIVED. The generator drives a throwaway engine so it always
// knows which character the caret wants, which is what makes the stream interesting; the browser's
// answer is then measured on a FRESH engine fed exactly as TypeBeatReplayScorer.feed feeds the
// C# one (a 1000/60 ms cadence loop, each due frame applied as update(t) then the key, mirroring
// ReplayEngineFeed.Apply). So the generator can lean on browser behaviour without the comparison
// inheriting it: whatever it produced, both sides then play the identical script.
//
// ONE DELIBERATE LIMIT ON WHAT IS GENERATED: press times are monotonic non-decreasing. The replay
// feed consumes frames in list order as the clock passes them, so an out-of-order frame would be
// applied at a time the recorded run never had, on both sides equally but for no useful reason.
//
// THE SECOND IS A CAP ON REJECTED KEYS, and it is back. The browser fails a play at 13 consecutive
// rejections (WRONG_KEY_FAIL_STREAK) while TypeBeatReplayScorer simulates no health at all and would
// carry on, so the generator stays clear of that streak rather than pinning a difference that is by
// design. Backlog 184 had made it moot (the mid-word space was the last key any fixture could be
// rejected on, and it is typed through now), and the note left behind said a fixture with a
// FREESTYLE slot, the one cell that still refuses the space key, would need it back. Backlog 209
// added exactly that fixture, so the cap is restored, in the narrowest form that reaches it: a space
// about to be the streak's last rejection is swapped for a key the slot accepts.
//
// Usage: node CoreFuzzHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

// ReplayEngineFeed.FRAME_MS, and TypeBeatReplayScorer.tail_ms. Both sides accumulate `now` the
// same way from the same literal, and IEEE 754 doubles add identically in C# and JS, so the two
// clocks land on bit-identical frame times.
const FRAME_MS = 1000.0 / 60;
const TAIL_MS = 10000;

// ---------------------------------------------------------------------------------------------
// Fixtures. Each is written as the JSON a real map carries AND rebuilt on the C# side as the
// LyricLine/TimedUnit shape the game's own tests use; the test asserts the two loaders agree on
// every cell before it compares a single account, so a fixture that drifted cannot masquerade as
// engine drift.
// ---------------------------------------------------------------------------------------------
function osu(lines, songEndMs, granularity) {
    return '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n' +
        JSON.stringify({ granularity: granularity || 'line', version: 2, song_end_ms: songEndMs }) + '\n' +
        lines.map(l => JSON.stringify(l)).join('\n') + '\n';
}

const FIXTURES = {
    // "cat dog" on [1000, 6000), sing end 5000: c@1000 a@1666.67 t@2333.33 ' '@3000 d@3000
    // o@3666.67 g@4333.33. The word-skip fixture, so the skip cases have a three-letter word to
    // lose more than one cell from.
    catDog: osu([{
        text: 'cat dog', start_ms: 1000, end_ms: 5000,
        words: [{ text: 'cat', start_ms: 1000, end_ms: 3000, score: 1 },
                { text: 'dog', start_ms: 3000, end_ms: 5000, score: 1 }]
    }], 6000),

    // "ab cd" on [1000, 4000), sing end 3000: a@1000 b@1500 ' '@2000 c@2000 d@2500. The tightest
    // map here, so a single stray press moves a large share of the account.
    abCd: osu([{
        text: 'ab cd', start_ms: 1000, end_ms: 3000,
        words: [{ text: 'ab', start_ms: 1000, end_ms: 2000, score: 1 },
                { text: 'cd', start_ms: 2000, end_ms: 3000, score: 1 }]
    }], 4000),

    // Two lines with a gap between them, which is the only shape where a seal happens while the
    // play CARRIES ON: the combo-neutral marks, the restorable snapshot a seal drops, and every
    // judgement weighted by a combo the seal did or did not wipe are all observable only here.
    catDogThenHi: osu([{
        text: 'cat dog', start_ms: 1000, end_ms: 5000,
        words: [{ text: 'cat', start_ms: 1000, end_ms: 3000, score: 1 },
                { text: 'dog', start_ms: 3000, end_ms: 5000, score: 1 }]
    }, {
        text: 'hi', start_ms: 6000, end_ms: 7000,
        words: [{ text: 'hi', start_ms: 6000, end_ms: 7000, score: 1 }]
    }], 10000),

    // The longest fixture: 5 words over 2 lines, 25 cells. Enough combo for the combo cap and the
    // combo-portion exponent to matter, and enough words for several skips in one run.
    quickBrownFox: osu([{
        text: 'the quick brown', start_ms: 1000, end_ms: 4000,
        words: [{ text: 'the', start_ms: 1000, end_ms: 1600, score: 1 },
                { text: 'quick', start_ms: 1600, end_ms: 2800, score: 1 },
                { text: 'brown', start_ms: 2800, end_ms: 4000, score: 1 }]
    }, {
        text: 'fox jumps', start_ms: 5000, end_ms: 7000,
        words: [{ text: 'fox', start_ms: 5000, end_ms: 5800, score: 1 },
                { text: 'jumps', start_ms: 5800, end_ms: 7000, score: 1 }]
    }], 12000),

    // Everything the four maps above leave out of the CELL side, in one fixture, because the
    // judgement windows a press is graded against are a property of the cell and not of the engine:
    //   * WORD granularity, so the windows are the 0.6 ladder rather than the 1.0 one;
    //   * a SYLLABLE subdivision inside "bad-cat", which warps the char-to-time mapping within the
    //     word instead of running one flat ramp across it;
    //   * a LOW-CONFIDENCE word ("sat.", score 0.1 under LOW_CONFIDENCE_SCORE), whose cells fall
    //     back to the widest Line ladder while the rest of the line stays at Word;
    //   * an ESTIMATED line, which does the same thing a whole line at a time;
    //   * PUNCTUATION and CASE, so the cells are the derived default stream ("the bad cat sat")
    //     rather than the authored text: a hyphen becomes a typed space on the slot the hyphen
    //     held, a period disappears, and capitals fold.
    // A press graded on the wrong ladder lands in a different tier, which moves the statistics, the
    // accuracy and the score, so this is scoring surface and not decoration.
    mixedTiers: osu([{
        text: 'The bad-cat sat.', start_ms: 1000, end_ms: 4000,
        words: [{ text: 'The', start_ms: 1000, end_ms: 1600, score: 1 },
                { text: 'bad-cat', start_ms: 1600, end_ms: 2800, score: 1, syllables: [{ start_ms: 2200 }] },
                { text: 'sat.', start_ms: 2800, end_ms: 4000, score: 0.1 }]
    }, {
        text: 'Oh no', start_ms: 5000, end_ms: 6500, estimated: true,
        words: [{ text: 'Oh', start_ms: 5000, end_ms: 5700, score: 1 },
                { text: 'no', start_ms: 5700, end_ms: 6500, score: 1 }]
    }], 12000, 'word'),

    // The tightest ladder there is: SYLLABLE granularity scales every window to 0.45, so the same
    // press offsets the other fixtures grade as Great land two tiers lower here. Nothing else about
    // it is unusual, which is the point: it isolates the tier scale.
    syllabic: osu([{
        text: 'one two', start_ms: 1000, end_ms: 3000,
        words: [{ text: 'one', start_ms: 1000, end_ms: 2000, score: 1, syllables: [{ start_ms: 1500 }] },
                { text: 'two', start_ms: 2000, end_ms: 3000, score: 1 }]
    }], 6000, 'syllable'),

    // Backlog 179's own surface. Every fixture above is built from ONE-syllable words, so each of
    // their tokens resolves to a single group spanning the whole word: real span judgement, but
    // never the case where a word carries SEVERAL spans and a press has to land in the right one.
    // These words do: "tonight" splits to|night, "little" to lit|tle and "people" to peo|ple (a
    // pinned exception), so the boundary between two spans of the same word is scoring surface here
    // and a syllabifier that split one character off moves the account.
    //
    // "cake" is the shape the rule was asked for: one group over the whole word, so every character
    // of it is perfectly timed anywhere inside the sung span rather than only on its own point.
    syllableWords: osu([{
        text: 'cake tonight', start_ms: 1000, end_ms: 4000,
        words: [{ text: 'cake', start_ms: 1000, end_ms: 2400, score: 1 },
                { text: 'tonight', start_ms: 2400, end_ms: 4000, score: 1 }]
    }, {
        text: 'little people', start_ms: 5000, end_ms: 8000,
        words: [{ text: 'little', start_ms: 5000, end_ms: 6500, score: 1 },
                { text: 'people', start_ms: 6500, end_ms: 8000, score: 1 }]
    }], 12000),

    // The STYLISED gate (backlog 178) under the span rule. "ohhh" is not an English spelling the
    // syllabifier can defend a boundary in, so it gets NO groups and its cells keep the classic
    // per-character POINT judgement, while "little" beside it is grouped and judged on spans. Both
    // halves are in one line on purpose: the fixture pins that the gate is read per TOKEN and that
    // an ungrouped token leaves a gap between groups rather than swallowing its neighbour.
    stylised: osu([{
        text: 'ohhh little', start_ms: 1000, end_ms: 3500,
        words: [{ text: 'ohhh', start_ms: 1000, end_ms: 2000, score: 1 },
                { text: 'little', start_ms: 2000, end_ms: 3500, score: 1 }]
    }], 8000),

    // A SUBTIMED word whose syllable count comes from the mapper rather than the rules: two
    // boundaries force "tonight" to three groups, whose edges are the boundary times themselves
    // (2800 and 3300) rather than anything read off the char targets. The forced count also drives
    // the syllabifier's reconciliation, which the natural arm never exercises.
    subtimed: osu([{
        text: 'cake tonight', start_ms: 1000, end_ms: 4000,
        words: [{ text: 'cake', start_ms: 1000, end_ms: 2400, score: 1, syllables: [{ start_ms: 1700 }] },
                { text: 'tonight', start_ms: 2400, end_ms: 4000, score: 1, syllables: [{ start_ms: 2800 }, { start_ms: 3300 }] }]
    }], 8000),

    // Backlog 181's surface: the same subtimed shape, plus a word-level AUTHORED character split
    // (split_chars) on BOTH words, chosen so it disagrees with the syllabifier's forced answer in
    // each. "beautiful" derives beau|tiful and is authored beauti|ful ([6]); "tonight" forced to
    // three derives to|ni|ght and is authored to|nig|ht ([2,5]). So the authored cut moves the
    // judgement GROUPS (which span a press is graded against) and the per-char TARGETS together,
    // and a browser that ignored the field would put cells 4 and 5 of "beautiful" and the 'g' of
    // "tonight" in the neighbouring syllable. The cosmetic syllable[].text strings are deliberately
    // NOT the authored cut here, because the loader must never read them.
    authoredSplit: osu([{
        text: 'beautiful tonight', start_ms: 1000, end_ms: 4000,
        words: [{ text: 'beautiful', start_ms: 1000, end_ms: 1900, score: 1,
                  syllables: [{ text: 'beau', start_ms: 1000 }, { text: 'tiful', start_ms: 1450 }], split_chars: [6] },
                { text: 'tonight', start_ms: 1900, end_ms: 4000, score: 1,
                  syllables: [{ text: 'to', start_ms: 1900 }, { text: 'ni', start_ms: 2600 }, { text: 'ght', start_ms: 3200 }],
                  split_chars: [2, 5] }]
    }], 8000),

    // Backlog 209's surface: the two shapes the span rule cannot judge, on one line beside cells it
    // still judges. "&&&&" is a FREESTYLE token, syllabifiable (only three identical LETTERS fail
    // that gate) and so one group over [1000, 5000], every cell of which accepts any key; "aaaaa" is
    // subtimed at 9000 and cut "aa|aaa", so its second group holds a run of THREE identical
    // characters while the first holds a run of two that stays on the span.
    //
    // The two cuts DISAGREE on purpose: the derived split cuts the word at 2.5 characters in
    // index-space while the target spread walks the five characters evenly, so cell 7 (the run's
    // first) is timed 8200, 800 ms before its own syllable opens at 9000. That is the sharpest shape
    // there is for the narrowing: the cell's target is not merely somewhere else inside its span, it
    // is outside it.
    freestyleStretch: osu([{
        text: '&&&& aaaaa', start_ms: 1000, end_ms: 17000, freestyle: true,
        words: [{ text: '&&&&', start_ms: 1000, end_ms: 5000, score: 1 },
                { text: 'aaaaa', start_ms: 5000, end_ms: 17000, score: 1, syllables: [{ start_ms: 9000 }] }]
    }], 30000),

    // Backlog 208's LINE-START SNAP fixture, mirroring the game's own FletcherEngineTest
    // .parkedLineMap. See withParkedMiddleLine below for the two lines this text carries and the
    // third one the harness has to splice in by hand.
    parkedLine: osu([{
        text: 'ab', start_ms: 1000, end_ms: 2000,
        words: [{ text: 'ab', start_ms: 1000, end_ms: 2000, score: 1 }]
    }, {
        text: 'cd', start_ms: 10000, end_ms: 13000,
        words: [{ text: 'cd', start_ms: 12000, end_ms: 13000, score: 1 }]
    }], 30000),

    // Backlog 218's RUSH BOUND fixture, mirroring the game's FletcherEngineTest.instrumentalGapMap,
    // and unlike parkedLine above every number in it IS loader-derived: line windows are contiguous,
    // so the twelve-second instrumental lives inside L0's own window.
    //   L0 "ab"       [1000, 14000), sung to 2000: a = 1000, b = 1500.
    //   L1 "cdefghij" [14000, 18000), sung [14000, 15000]: eight chars, step 125, c = 14000 through
    //                 j = 14875. Activation is clamped to the line's own start, so entry into it
    //                 opens at 14000 - FLETCHER_DRAG_GRACE_MS = 12500, a second and a half before L0
    //                 could seal at all, and a player who types "ab" out on time is REFUSED and
    //                 parked for eleven seconds. Eight cells in the second line so the rush cap has
    //                 room to bite on the far side of the deferred roll.
    instrumentalGap: osu([{
        text: 'ab', start_ms: 1000, end_ms: 2000,
        words: [{ text: 'ab', start_ms: 1000, end_ms: 2000, score: 1 }]
    }, {
        text: 'cdefghij', start_ms: 14000, end_ms: 15000,
        words: [{ text: 'cdefghij', start_ms: 14000, end_ms: 15000, score: 1 }]
    }], 30000)
};

// THE ONE FIXTURE THAT IS NOT LOADER-DERIVED, and it has to be, because the state it reaches is one
// the browser's own loader cannot express. The line-start snap only ever decides anything while a
// FINISHED caret is parked on a line the SEAL has not yet reached and the next line has ALREADY
// started, and that needs two things at once:
//
//   * a line with NO CELLS, so the caret is complete the instant it lands there and no press of the
//     player's can ever finish it (which is exactly the state the keypress roll-forward cannot
//     cover). The authored text is pure punctuation, which the default stream strips entirely; the
//     browser's loader goes further and DROPS such a line from the map altogether, so it has to be
//     put back here.
//   * OVERLAPPING WINDOWS: that line's window must outlive the next line's cue. The loader makes
//     windows strictly contiguous (a line ends where the next one starts) and a line's activation
//     is never before its own start, so on any map it CAN build the seal loop's own hand-over
//     already carries a finished caret across every boundary and the snap is inert.
//
// So both sides hand-build the same three lines: the C# fixture declares the end times outright
// (LyricLine carries its own EndTime) and this splices them in. The loader-agreement test still
// holds every one of these numbers against the game's TypingLine, so the hand-build is pinned
// rather than merely asserted, and the CELLS of the two real lines are loader-derived as ever.
//
//   L0 "ab"  [1000, 3000):   a = 1000, b = 1500, activation 1000.
//   L1 "..." [3000, 20000):  no cells at all, activation 3000 (a line with no typeable cell
//                            activates at its own start).
//   L2 "cd"  [10000, 30000): c = 12000, d = 12500, activation 12000 - CUE_LEAD_MS = 10500, which
//                            is 9500 ms BEFORE L1's window closes.
function withParkedMiddleLine(beatmap) {
    beatmap.lines[0].endTime = 3000;

    beatmap.lines.splice(1, 0, {
        index: 1,
        text: '',
        startTime: 3000,
        endTime: 20000,
        singEndTime: 19000,
        activationTime: 3000,
        sealGraceMs: 0,
        estimated: false,
        cells: [],
        syllables: [],
        cellSyllable: [],
        charTimedStretch: []
    });

    beatmap.lines[2].index = 2;
    beatmap.lines[2].endTime = 30000;

    beatmap.totalCells = beatmap.lines.reduce((n, l) => n + l.cells.length, 0);

    return beatmap;
}

function build(name) {
    const beatmap = TB.buildBeatmap(TB.parseLyricOsu(FIXTURES[name]), false);

    return name === 'parkedLine' ? withParkedMiddleLine(beatmap) : beatmap;
}

// ---------------------------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------------------------

/** Numerical Recipes' LCG, so a seed reproduces a case exactly on any machine. */
function lcg(seed) {
    let s = (seed >>> 0) || 1;
    return function () {
        s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
        return s / 4294967296;
    };
}

// Press offsets against the cell's own target, chosen to land in every band the Line-granularity
// ladder has: Great [-250, 400], Ok [-600, 1000], Meh [-1200, 2000], and Premature / Lagging
// outside it. A run therefore exercises the scoring tiers, the two zero-point tiers (which since
// backlog 199 are hits that pay accuracy rather than breaks that end the run), and the sync quality
// ramp, rather than only the happy path.
const OFFSETS = [0, 120, -180, 380, 700, -520, 1400, -900, 1900, 2600, -1600];

const LETTERS = 'abcdefghijklmnopqrstuvwxyz';

/**
 * One generated run: the keystroke script, produced by walking a throwaway engine so the generator
 * always knows the character the caret wants. `spaceSkipsWord` is fed to the throwaway too, so a
 * skip run really does generate skips rather than rejections.
 */
function generate(name, seed, spaceSkipsWord) {
    const rnd = lcg(seed);
    const engine = new TB.TypingEngine(build(name));
    engine.spaceSkipsWord = spaceSkipsWord;

    const keys = [];
    let skipPresses = 0;
    let t = 0;

    for (let step = 0; step < 140 && !engine.finished && !engine.failed; step++) {
        engine.update(t);

        if (engine.activeLineIndex < 0) {
            // Pre-roll or the dead zone between two lines: nothing is typeable, so move the clock.
            t += 120 + Math.floor(rnd() * 900);
            continue;
        }

        const cell = engine.caretCell;

        if (cell === null) {
            t += 120 + Math.floor(rnd() * 900);
            continue;
        }

        const roll = rnd();

        if (roll < 0.10) {
            // Idle: let the clock run, which is how cells reach a seal untyped.
            t += 250 + Math.floor(rnd() * 1400);
            continue;
        }

        if (roll < 0.20) {
            keys.push([t, '\b']);
            engine.update(t);
            engine.processBackspace();
            continue;
        }

        let ch;

        if (roll < 0.32 && cell.expected !== ' ') {
            // A typed-through typo (or, with the skip setting on and the roll below, a skip).
            do { ch = LETTERS[Math.floor(rnd() * LETTERS.length)]; } while (ch === cell.expected);
        } else if (roll < 0.40) {
            // A space, and it means four different things depending on where it lands and which arm
            // the run is on, which is why it is rolled everywhere rather than only inside a word.
            // On a word GAP it is simply the right key; on a gap a typo has PARKED the caret on it
            // steps over that typo (backlog 184); inside a word it skips the word (setting on) or,
            // since backlog 184, is typed through as an ordinary typo (setting off, which is the
            // browser's permanent arm and the one midWordSpaceTypos below counts).
            ch = ' ';
        } else if (roll < 0.44 && cell.expected === ' ') {
            // A wrong letter on a WORD GAP. Rejected in every model before backlog 181, typed
            // through into the gap since, which is the shape gapTypos below counts: the browser is
            // live-only and so is always on the type-through arm, and the C# arm of the parity test
            // has to set CONFIG flags bit 3 to be on it too. With the skip setting ON it also PARKS
            // the caret on the gap (backlog 184, CONFIG flags bit 4), so the next roll is made with
            // the caret still sitting on the spoiled cell: that is how the sweep reaches both the
            // overwrite and the step-over without either being scripted.
            ch = LETTERS[Math.floor(rnd() * LETTERS.length)];
        } else {
            ch = cell.expected;
        }

        // The rejection cap (see the header): a space is the one key a FREESTYLE slot still refuses,
        // and the browser fails a play at WRONG_KEY_FAIL_STREAK consecutive rejections while the C#
        // scorer simulates no health at all. Stop one short of that streak by pressing a key the slot
        // takes instead, which also resets the count rather than merely dodging it. With the skip
        // setting on the space never reaches the slot at all (the word skip consumes it), so the
        // guard is scoped to the arm that can reject.
        if (ch === ' ' && cell.freestyle && !spaceSkipsWord
            && engine.consecutiveWrongKeys + 1 >= TB.constants.WRONG_KEY_FAIL_STREAK) {
            ch = LETTERS[Math.floor(rnd() * LETTERS.length)];
        }

        // Press at the cell's own target plus an offset, never earlier than the clock already is.
        const pressTime = Math.max(t, cell.target + OFFSETS[Math.floor(rnd() * OFFSETS.length)]);

        if (ch === ' ' && cell.expected !== ' ' && spaceSkipsWord) skipPresses++;

        keys.push([pressTime, ch]);
        engine.update(pressTime);
        engine.processKey(ch, pressTime);
        t = pressTime;
    }

    return { keys: keys, skipPresses: skipPresses };
}

// ---------------------------------------------------------------------------------------------
// Playback: TypeBeatReplayScorer.feed, exactly.
// ---------------------------------------------------------------------------------------------
function endTimeFor(beatmap, keys) {
    const last = beatmap.lines[beatmap.lines.length - 1];
    let end = beatmap.lines.length > 0 ? last.endTime + last.sealGraceMs + TAIL_MS : TAIL_MS;
    if (keys.length > 0) end = Math.max(end, keys[keys.length - 1][0] + TAIL_MS);
    return end;
}

function play(name, keys, spaceSkipsWord) {
    const beatmap = build(name);
    const engine = new TB.TypingEngine(beatmap);
    engine.spaceSkipsWord = spaceSkipsWord;

    // TypingEngine.ComboRestored, counted so the coverage guard can prove the sweep really does
    // reach the fix-a-typo / reclaim-a-skip seam rather than only the happy and hopeless paths.
    let restores = 0;
    engine.onComboRestored = () => { restores++; };

    // Backlog 176 coverage: a REDEEMABLE break (a wrong key, or a word skip) landing at a streak of
    // ZERO while a claim is still outstanding, which is the one case that rule decides and the one
    // the browser used to get wrong. Counted by wrapping the engine's single snapshot write site, so
    // it counts what the engine actually reached rather than what a script looks like it reaches.
    //
    // Backlog 243 splits a SECOND counter off the same wrapper: a redeemable break landing on a
    // streak the outstanding claim's OWN press credited, which is the arm 176 alone got wrong. It
    // is counted only where the two rules disagree (a streak of more than zero, spared anyway),
    // so it says "the credit decided this break" rather than "the credit was looked at", and a
    // sweep that stopped reaching the shape cannot pass for coverage.
    let passiveBreaks = 0;
    let ownCreditBreaks = 0;
    const snapshotBreak = engine.snapshotRedeemableBreak.bind(engine);

    // The trailing arguments are forwarded rather than named, because this wrapper counts and
    // decides nothing about them: backlog 259 appended the broken run's POSITIONS to the call, and a
    // wrapper that spelled out only the two it reads would silently drop the ledger the redemption
    // needs and leave every restore with no positions to put back.
    engine.snapshotRedeemableBreak = function (cellIndex, brokenStreak, ...rest) {
        const claim = engine.restorable;

        if (brokenStreak <= 0 && claim !== null) passiveBreaks++;
        else if (claim !== null && brokenStreak <= claim.ownPressCredit) ownCreditBreaks++;

        snapshotBreak(cellIndex, brokenStreak, ...rest);
    };

    // Backlog 179 coverage: presses the SPAN rule actually decided, counted by wrapping the one
    // place a judged delta is produced. A press is only counted when its cell is in a syllable
    // group AND the span answer differs from the classic point answer, so a sweep that reached
    // groups but only ever pressed dead on target cannot pass for coverage. Without this the whole
    // port could silently stop being exercised (a fixture edit that ungrouped every token would
    // leave both sides agreeing on point deltas, green and meaningless).
    //
    // Backlog 209 splits that count in two, because a cell in a group now reaches one of TWO arms:
    // the span, or the narrowing back to its own character target (a freestyle slot, or a cell of a
    // run of three identical characters in one syllable). Each is counted only when the OTHER rule
    // would have answered differently, so both counters say "this arm decided a press" rather than
    // "this arm ran", and the sweep has to reach both.
    //
    // Backlog 247 adds a THIRD arm and a third counter: the cell that OPENS a group is judged on the
    // distance from the span's start. It needs its own attribution rather than a slot in the pair
    // above, because on a derived fixture an opening cell's own target IS the span's start, so its
    // answer coincides with the POINT answer and would otherwise be miscounted as a stretch
    // narrowing. So the arm that owns the press is decided first (the engine's own precedence: the
    // stretch exclusion, then the opening cell, then the span), and each counter then fires only
    // where its arm's answer differs from the arm that would otherwise have applied. For the opener
    // that is the span, which it matches on the early side by construction, so the counter only ever
    // sees a press that landed LATE into the syllable it opened, which is exactly the set backlog
    // 247 moved.
    let spanJudgements = 0;
    let stretchPointJudgements = 0;
    let firstCharJudgements = 0;
    const judgedDelta = engine.judgedDeltaFor.bind(engine);

    engine.judgedDeltaFor = function (line, cellIndex, time) {
        const delta = judgedDelta(line, cellIndex, time);
        const syllable = TB.syllableIndexOf(line, cellIndex);

        if (syllable >= 0) {
            const group = line.syllables[syllable];
            const point = time - line.cells[cellIndex].target;
            const span = time < group.startTime ? time - group.startTime
                : (time > group.endTime ? time - group.endTime : 0);
            const firstChar = time - group.startTime;

            // Which arm ANSWERED, read off the answer itself rather than off the predicate, and only
            // where the arms disagree. Counting the predicate would leave a counter positive even if
            // judgedDeltaFor stopped reading it, which is the one failure these counters exist to
            // catch.
            if (cellIndex === group.startCell && !TB.isCharTimedStretch(line, cellIndex)) {
                if (delta === firstChar && firstChar !== span) firstCharJudgements++;
            }
            else if (span !== point) {
                if (delta === point) stretchPointJudgements++;
                else if (delta === span) spanJudgements++;
            }
        }

        return delta;
    };

    // Backlog 181 coverage: presses the WORD-GAP arm actually decided. A gap cell can only ever be
    // left WRONG by the type-through this task ported, so counting the gaps that BECOME wrong
    // counts exactly the presses whose outcome differs from the strict rule the browser used to
    // have (which would have rejected them, moving nothing). Measured on the cells rather than on
    // the script, so a stream that happens to press a letter at a gap the caret is not on does not
    // count, and a run where the port silently stopped reaching gaps reads zero.
    let gapTypos = 0;

    // Backlog 184 coverage, the same shape and for the same reason, one counter per rule the task
    // added. A PARK is a gap typo the caret did not move away from (only the skip arm produces one);
    // a STEP OVER is a space pressed while the caret sat on such a cell, which advances past it and
    // leaves it wrong; a MID-WORD SPACE TYPO is a lyric cell left holding a space, which is the only
    // way one can get there. All three are measured on the engine's own state around the call rather
    // than on the script, so a generator that stopped rolling the shapes, or a port that quietly
    // went back to advancing and rejecting, reads zero instead of staying green.
    let parkedGapTypos = 0;
    let stepOvers = 0;
    let midWordSpaceTypos = 0;
    const processKey = engine.processKey.bind(engine);

    engine.processKey = function (c, time) {
        const before = wrongGapCount(engine);
        const beforeCaret = engine.caretIndex;
        const beforeMidWord = midWordSpaceCount(engine);
        const parkedOn = parkedCell(engine);
        const handled = processKey(c, time);

        if (wrongGapCount(engine) > before) {
            gapTypos++;
            if (engine.caretIndex === beforeCaret) parkedGapTypos++;
        }

        if (parkedOn !== null && c === ' ' && engine.caretIndex > beforeCaret && parkedOn.state === 'wrong') stepOvers++;
        if (midWordSpaceCount(engine) > beforeMidWord) midWordSpaceTypos++;

        return handled;
    };

    // Backlog 208 coverage, one counter per freedom the flexible caret grants, all four measured on
    // the ENGINE's own seams rather than on the script. The C# arm of the parity test sets CONFIG
    // flags bit 5, so both sides run the flexible caret and a port that quietly lost one of these
    // would leave the two arms agreeing (both pinned), green, and covering nothing. That is the same
    // failure mode the span, gap and stretch counters above exist for.
    //
    //   rollForwards   RUSH FREEDOM: a press finished a line and the caret went straight on to the
    //                  next one instead of waiting for its cue.
    //   lineSnaps      THE LINE-START SNAP: a caret parked past the end of a FINISHED line was
    //                  handed on by the next line starting, with no press of its own.
    //   dragHolds      DRAG FREEDOM: a line the player was still typing refused its own seal, which
    //                  is the only thing sealPermitted answering false can mean.
    //   rushCapBreaks  THE RUSH CAP: a press that put the caret more than
    //                  FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead, and so earned no
    //                  combo however well it was timed.
    //
    // Backlog 218 adds a fifth on the same principle, and it is the sharpest of the set because it
    // is the one whose ABSENCE would be invisible:
    //
    //   refusedRolls   THE RUSH BOUND: a press finished a line more than FLETCHER_DRAG_GRACE_MS
    //                  before the next line's cue, so the roll was REFUSED and the caret parked past
    //                  the last cell of the line it finished. Counted on entryPermitted, the
    //                  engine's own predicate, and only when it answered false. The C# arm sets
    //                  CONFIG flags bit 7, so a port that quietly lost the bound would leave both
    //                  sides on the UNBOUNDED roll, green, and covering nothing.
    let rollForwards = 0;
    let lineSnaps = 0;
    let dragHolds = 0;
    let rushCapBreaks = 0;
    let refusedRolls = 0;

    const rollForward = engine.rollForwardIfFinishedEarly.bind(engine);

    engine.rollForwardIfFinishedEarly = function (time) {
        const before = engine.activeLineIndex;
        rollForward(time);
        if (engine.activeLineIndex !== before) rollForwards++;
    };

    const entryPermitted = engine.entryPermitted.bind(engine);

    engine.entryPermitted = function (index, time) {
        const permitted = entryPermitted(index, time);
        if (!permitted) refusedRolls++;
        return permitted;
    };

    const snapForward = engine.snapForwardOnLineStart.bind(engine);

    engine.snapForwardOnLineStart = function (time) {
        const snapped = snapForward(time);
        if (snapped) lineSnaps++;
        return snapped;
    };

    const sealPermitted = engine.sealPermitted.bind(engine);

    engine.sealPermitted = function (index, time) {
        const permitted = sealPermitted(index, time);
        if (!permitted) dragHolds++;
        return permitted;
    };

    const rushesPastCap = engine.rushesPastCap.bind(engine);

    // EVERY argument forwarded, not just the two this counter reads: backlog 260 gave the cap a third
    // one (the caret as it stood BEFORE a word skip moved it), and a spy that dropped it would hand
    // the real method an undefined index, which reads out of the prefix table as NaN and silently
    // answers "no rush" for the whole sweep.
    engine.rushesPastCap = function (...args) {
        const rushed = rushesPastCap(...args);
        if (rushed) rushCapBreaks++;
        return rushed;
    };

    const end = endTimeFor(beatmap, keys);
    let next = 0;

    // ReplayEngineFeed.Apply: update(frame time) FIRST, then the key.
    function apply(frame) {
        engine.update(frame[0]);
        if (frame[1] === '\b') engine.processBackspace();
        else engine.processKey(frame[1], frame[0]);
    }

    for (let now = 0; now <= end; now += FRAME_MS) {
        while (next < keys.length && keys[next][0] <= now) { apply(keys[next]); next++; }
        engine.update(now);
    }

    while (next < keys.length) { apply(keys[next]); next++; }
    engine.update(end);

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
        engineFinished: engine.finished,
        engineFailed: engine.failed,
        engineMaxCombo: engine.maxCombo,
        engineScore: engine.score,
        mistypes: engine.mistypes,
        restores: restores,
        passiveBreaks: passiveBreaks,
        ownCreditBreaks: ownCreditBreaks,
        spanJudgements: spanJudgements,
        stretchPointJudgements: stretchPointJudgements,
        firstCharJudgements: firstCharJudgements,
        gapTypos: gapTypos,
        parkedGapTypos: parkedGapTypos,
        stepOvers: stepOvers,
        midWordSpaceTypos: midWordSpaceTypos,
        rollForwards: rollForwards,
        lineSnaps: lineSnaps,
        dragHolds: dragHolds,
        rushCapBreaks: rushCapBreaks,
        refusedRolls: refusedRolls
    };
}

/** Word-gap cells currently holding a typo, over the whole map (see gapTypos in play()). */
function wrongGapCount(engine) {
    let n = 0;
    for (const line of engine.lines) {
        for (const cell of line.cells) {
            if (cell.expected === ' ' && cell.state === 'wrong') n++;
        }
    }
    return n;
}

/** LYRIC cells currently holding a typed SPACE, which only backlog 184 can produce. */
function midWordSpaceCount(engine) {
    let n = 0;
    for (const line of engine.lines) {
        for (const cell of line.cells) {
            if (cell.expected !== ' ' && cell.state === 'wrong' && cell.typedChar === ' ') n++;
        }
    }
    return n;
}

/** The cell the caret is parked on when it is a spoiled word gap, else null (see stepOvers). */
function parkedCell(engine) {
    if (engine.activeLineIndex < 0) return null;

    const cells = engine.lines[engine.activeLineIndex].cells;
    if (engine.caretIndex >= cells.length) return null;

    const cell = cells[engine.caretIndex];
    return cell.expected === ' ' && cell.state === 'wrong' ? cell : null;
}

// ---------------------------------------------------------------------------------------------
// The cells each fixture resolves to, so the test can pin the two loaders against each other
// before it trusts a single account.
// ---------------------------------------------------------------------------------------------
function cellsOf(name) {
    const beatmap = build(name);
    return beatmap.lines.map(line => ({
        endTime: line.endTime,
        activationTime: line.activationTime,
        sealGraceMs: line.sealGraceMs,
        cells: line.cells.map(c => ({ expected: c.expected, target: c.target, tier: c.tier })),
        // The SYLLABLE groups and the per-cell membership map (backlog 179). Emitted beside the
        // cells and pinned before any account, for the same reason the cells are: the spans are now
        // what a press is judged against, so a group that drifted would reach the account
        // comparison wearing the engine's clothes.
        syllables: line.syllables.map(g => ({
            startCell: g.startCell, endCellExclusive: g.endCellExclusive, startTime: g.startTime, endTime: g.endTime
        })),
        cellSyllable: line.cellSyllable.slice(),
        // The STRETCH flags (backlog 209), pinned beside the membership map for the same reason and
        // in the same place: they are the second half of what decides which rule judges a press, and
        // they are DERIVED from the cells and that map, so a divergence here is a divergence in the
        // derivation rather than in the engine.
        charTimedStretch: line.charTimedStretch.slice()
    }));
}

// ---------------------------------------------------------------------------------------------
// SCRIPTED cases, played and compared exactly like the generated ones.
//
// The generator rolls its own shapes, and 360 of its runs never once reached the one backlog 176
// decides: a redeemable break landing at a streak of ZERO while another claim is still outstanding,
// with BOTH spoiled cells then corrected. It is a narrow target (two breaks in a row with nothing
// earned between them, and a walk back into the older one), which is why a random walk misses it and
// why the case is written out by hand here rather than waited for. Each is a real player shape:
// backlog 176 came out of a submitted run that hit the first one and got none of a 447 streak back.
//
// The times are the cells' own targets, so every press lands in a scoring tier rather than on a
// window edge, and they are monotonic non-decreasing, which is what the replay feed requires.

// The replay feed's BACKSPACE sentinel (TypeBeatReplayFrame.BACKSPACE), spelled without a
// string escape so the scripted keystrokes below read as data rather than as escapes.
const BS = String.fromCharCode(8);

const SCRIPTED = [
    {
        // The reported shape (ComboRestoreTest.TwoWrongKeysOnAdjacentCellsKeepTheStreakWhenBothAreFixed):
        // "cat " typed clean for a run of 4, a wrong key on the 'd' that snapshots it, a wrong key on
        // the 'o' at a combo of zero, then both erased and the word typed out. The second break has no
        // streak to take the claim with, so the 'd' keeps it and the fix resumes the 4.
        name: 'scripted/adjacentTypos', fixture: 'catDog', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1667, 'a'], [2333, 't'], [3000, ' '], [3000, 'z'], [3667, 'x'],
               [3667, '\b'], [3667, '\b'], [3700, 'd'], [3800, 'o'], [4333, 'g']]
    },
    {
        // The same-cell sibling: fumble the 'c' on a run of 3, erase it, fumble it AGAIN, then correct
        // it. No successful fix separates the two wrong keys, so there is no second streak to snapshot.
        name: 'scripted/sameCellTwice', fixture: 'abCd', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'a'], [1500, 'b'], [2000, ' '], [2000, 'z'], [2050, '\b'], [2100, 'y'],
               [2150, '\b'], [2200, 'c'], [2500, 'd']]
    },
    {
        // The OTHER redeemable break: a run of 6, a typo on the 'i' of "quick", then a space that
        // gives up on the rest of the word while the run is already zeroed, then back into the word
        // and out through both lines. The skip cost nothing, so the typo's claim survives it, and
        // two backspaces are all it takes to get back: the first erases the typed space, the second
        // reclaims both abandoned cells and erases the typo in one step.
        name: 'scripted/skipOverATypo', fixture: 'quickBrownFox', spaceSkipsWord: true, skipPresses: 1,
        keys: [[1000, 't'], [1200, 'h'], [1400, 'e'], [1600, ' '], [1600, 'q'], [1840, 'u'],
               [2080, 'z'], [2120, ' '], [2200, '\b'], [2200, '\b'],
               [2300, 'i'], [2320, 'c'], [2560, 'k'], [2800, ' '], [2800, 'b'], [3040, 'r'],
               [3280, 'o'], [3520, 'w'], [3760, 'n'],
               [5000, 'f'], [5267, 'o'], [5533, 'x'], [5800, ' '], [5800, 'j'], [6040, 'u'],
               [6280, 'm'], [6520, 'p'], [6760, 's']]
    },
    {
        // Backlog 179's asked-for shape, on "cake tonight" / "little people". Every character is
        // pressed WELL AHEAD of its own point target but still inside the sung span of the syllable
        // it belongs to, so under the span rule every one of them is delta 0 and the whole map is
        // typed clean. Under the classic point rule the same fingers would be graded Ok and Meh:
        // "cake" runs [1000, 2400] but its 'e' points at 2050, and the "night" span runs
        // [2857.14, 4000] while its 'n' points at 2857.14 and its trailing 't' at 3771.43, so a
        // press at 3900 is dead centre of the syllable and a full second late on the character.
        name: 'scripted/insideTheSpan', fixture: 'syllableWords', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1100, 'a'], [1200, 'k'], [1300, 'e'], [2400, ' '],
               [2500, 't'], [2600, 'o'], [3900, 'n'], [3910, 'i'], [3920, 'g'], [3930, 'h'], [3940, 't'],
               [5000, 'l'], [5100, 'i'], [5200, 't'], [6400, 't'], [6410, 'l'], [6420, 'e'], [6500, ' '],
               [6600, 'p'], [6700, 'e'], [6800, 'o'], [7900, 'p'], [7910, 'l'], [7920, 'e']]
    },
    {
        // The other side of the same rule: presses OUTSIDE the span, which are graded on the signed
        // distance to the nearer edge and so still move through the whole ladder. 'n' and 'i' land
        // before the "night" span opens at 2857.14 (early by 437 and 357, both nearer the edge than
        // their own points), and 'g', 'h', 't' land after it closes at 4000. The two engines have to
        // agree tier for tier here, not only on the zero inside.
        name: 'scripted/offSpanEdges', fixture: 'syllableWords', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1050, 'a'], [2380, 'k'], [2390, 'e'], [2400, ' '],
               [2400, 't'], [2410, 'o'], [2420, 'n'], [2500, 'i'], [4500, 'g'], [4700, 'h'], [4900, 't']]
    },
    {
        // The STYLISED token stays on POINT deltas while its grouped neighbour is judged on spans,
        // in one line and one run. The three 'h' presses of "ohhh" are 650, 450 and 240 off their
        // own characters and are graded on exactly that, because the word has no groups; had the
        // browser grouped it the way it groups "little", the first of them would have been a Great
        // instead of an Ok and the accounts would part. The "little" presses that follow are 450 and
        // 650 off their characters and are graded ZERO, because those cells ARE in a group.
        name: 'scripted/stylisedStaysOnPoints', fixture: 'stylised', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'o'], [1900, 'h'], [1950, 'h'], [1990, 'h'], [2000, ' '],
               [2000, 'l'], [2700, 'i'], [2740, 't'], [3400, 't'], [3450, 'l'], [3490, 'e']]
    },
    {
        // Backlog 181's fix cycle on the WORD GAP, which is the shape the generator can roll the
        // first half of but never the whole of: a wrong letter into the gap, backspaced, and the
        // space typed correctly, which resumes the streak the typo broke. "ab" is a run of 2, so
        // the corrected space lands at combo 3 and the map comes out with the clean run's combo
        // multiset (1..5) and its full 1000000.
        name: 'scripted/gapTypoFixed', fixture: 'abCd', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'a'], [1500, 'b'], [2000, 'x'], [2000, BS], [2000, ' '], [2000, 'c'], [2500, 'd']]
    },
    {
        // The same typo left alone through the seal: the gap resolves as an UNFIXED TYPO, a hit and
        // not a miss, so four of the map's five cells are typed and the run takes the completion
        // cost a miss would have cost it (the game's SpaceTypoTest pins the same 4/5).
        name: 'scripted/gapTypoUnfixed', fixture: 'abCd', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'a'], [1500, 'b'], [2000, 'x'], [2000, 'c'], [2500, 'd']]
    },
    {
        // A gap typo on a map judged by SYLLABLE SPANS, with every lyric press deep inside its own
        // span (the insideTheSpan case above, with the gap fumbled). The gap is in no group and is
        // untimed, so its typo carries a ZEROED delta on both sides; the two rules meeting on one
        // cell is what this case exists to hold, because the zeroing moved above the match to make
        // it true.
        name: 'scripted/gapTypoInSpans', fixture: 'syllableWords', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1100, 'a'], [1200, 'k'], [1300, 'e'], [2400, 'z'],
               [2500, 't'], [2600, 'o'], [3900, 'n'], [3910, 'i'], [3920, 'g'], [3930, 'h'], [3940, 't'],
               [5000, 'l'], [5100, 'i'], [5200, 't'], [6400, 't'], [6410, 'l'], [6420, 'e'], [6500, ' '],
               [6600, 'p'], [6700, 'e'], [6800, 'o'], [7900, 'p'], [7910, 'l'], [7920, 'e']]
    },
    {
        // The gap typo meeting the WORD SKIP, i.e. the two redeemable breaks on one cell. A space
        // inside "quick" abandons the rest of it and is typed on the gap that follows; a backspace
        // takes that space back; the wrong letter then lands on the SAME gap and is typed through;
        // and a second backspace pair walks out of it and back into the abandoned run, which is
        // reclaimed and typed out. Every seam backlog 176 arbitrates is in here at once.
        //
        // Backlog 243 MOVED THIS CASE'S ANSWER, and it was pinning the bug: the skip's own space is
        // judged on the gap and rebuilds the run to 1, so the wrong letter after it was breaking a
        // streak of 1, clearing 176's zero test and overwriting the six-deep claim on the 'i' with
        // a worthless one on the gap. Both engines agreed on that, which is exactly why a parity
        // case could hold it. The 1 belongs to the break's own press, so the typo is passive now,
        // the claim on the 'i' survives, and the retype at the end restores the 6.
        name: 'scripted/gapTypoAfterWordSkip', fixture: 'quickBrownFox', spaceSkipsWord: true, skipPresses: 1,
        keys: [[1000, 't'], [1200, 'h'], [1400, 'e'], [1600, ' '], [1600, 'q'], [1840, 'u'],
               [2080, ' '], [2200, BS], [2300, 'z'], [2350, BS], [2400, BS],
               [2450, 'u'], [2500, 'i'], [2560, 'c'], [2600, 'k'], [2800, ' '], [2800, 'b'],
               [3040, 'r'], [3280, 'o'], [3520, 'w'], [3760, 'n'],
               [5000, 'f'], [5267, 'o'], [5533, 'x'], [5800, ' '], [5800, 'j'], [6040, 'u'],
               [6280, 'm'], [6520, 'p'], [6760, 's']]
    },
    {
        // Backlog 243's own replay, without the backspace the case above puts between the skip and
        // the typo. "the qu" is a run of 6, the space at 2080 gives up on "ick" and claims the 'i'
        // (cell 6) with that 6, and the SAME press is then judged on the word gap and rebuilds the
        // run to 1. The two wrong letters that follow land on the 'b' and the 'r' of "brown": the
        // first breaks a run of nothing but the skip's own space, so it is passive and spends the
        // credit, and the second breaks a run of zero, so 176 spares it as it always did. Four
        // backspaces then walk out of both typos, back over the typed gap and into the abandoned
        // run (reclaiming "ick" and erasing the 'u'), and the retyped 'i' restores the 6.
        //
        // The shape a real submitted play lost about 430 combo to, and the one the generator cannot
        // roll: it needs a skip, a typo on the very next press, and a walk all the way back.
        name: 'scripted/typoRightAfterAWordSkip', fixture: 'quickBrownFox', spaceSkipsWord: true, skipPresses: 1,
        keys: [[1000, 't'], [1200, 'h'], [1400, 'e'], [1600, ' '], [1600, 'q'], [1840, 'u'],
               [2080, ' '], [2120, 'z'], [2160, 'x'],
               [2200, BS], [2240, BS], [2280, BS], [2320, BS],
               [2360, 'u'], [2400, 'i'], [2440, 'c'], [2560, 'k'], [2800, ' '], [2800, 'b'],
               [3040, 'r'], [3280, 'o'], [3520, 'w'], [3760, 'n'],
               [5000, 'f'], [5267, 'o'], [5533, 'x'], [5800, ' '], [5800, 'j'], [6040, 'u'],
               [6280, 'm'], [6520, 'p'], [6760, 's']]
    },
    {
        // The other half of backlog 243, the SPEND. Same skip, same first typo spared by the
        // credit, but then a real character (the 'r' of "brown", struck at 2900 where its own word
        // is being sung) puts the run back to 1 on the player's own fingers. That 1 is not the
        // claim's, because the credit was spent by the typo that went past it, so the second typo
        // TAKES the claim exactly as it did before 243: walking all the way back and typing "ick"
        // out restores nothing, and it is the late 'o' that redeems the streak of 1 instead.
        //
        // Without the spend the credit would still be standing here and the second typo would be
        // spared too, which is a different account on both engines: this case is what stops the
        // fix from turning into "a skip makes every later break passive".
        name: 'scripted/skipCreditSpentByTheFirstTypo', fixture: 'quickBrownFox', spaceSkipsWord: true, skipPresses: 1,
        keys: [[1000, 't'], [1200, 'h'], [1400, 'e'], [1600, ' '], [1600, 'q'], [1840, 'u'],
               [2080, ' '], [2400, 'z'], [2900, 'r'], [3000, 'z'],
               [3100, BS], [3140, BS], [3180, BS], [3220, BS], [3260, BS],
               [3300, 'u'], [3340, 'i'], [3380, 'c'], [3420, 'k'], [3460, ' '], [3500, 'b'],
               [3540, 'r'], [3580, 'o'], [3620, 'w'], [3760, 'n'],
               [5000, 'f'], [5267, 'o'], [5533, 'x'], [5800, ' '], [5800, 'j'], [6040, 'u'],
               [6280, 'm'], [6520, 'p'], [6760, 's']]
    },
    {
        // A SUBTIMED word: the mapper's boundary times are the span edges, so "tonight" is three
        // groups over [2400, 2800], [2800, 3300] and [3300, 4000] whatever the char targets say, and
        // the syllabifier is asked for a FORCED count of 3 rather than the natural 2. Each press
        // sits deep inside its own hand-authored window and nowhere near its point.
        name: 'scripted/subtimedSpans', fixture: 'subtimed', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1600, 'a'], [1650, 'k'], [2300, 'e'], [2400, ' '],
               [2400, 't'], [2700, 'o'], [2900, 'n'], [3200, 'i'], [3400, 'g'], [3900, 'h'], [3950, 't']]
    },
    {
        // Backlog 247's asked-for shape, on the same subtimed map and for the sharpest reason: its
        // span edges are the mapper's boundary times, so a group's OPENING cell is one whose own
        // character target need not be the span start at all. Every press here is the LAST character
        // its syllable will take, struck 10 ms before that syllable's window closes, and each is the
        // cell that OPENS the next one: 'c' at 1690 opens [1000, 1700], 'k' at 2390 opens
        // [1700, 2400], 't' at 2790 opens [2400, 2800], 'n' at 3290 opens [2800, 3300] and 'g' at
        // 3990 opens [3300, 4000].
        //
        // Under the pure span rule every one of them is delta 0 and the map is typed clean. Under the
        // hybrid they are paid 690, 690, 390, 490 and 690 respectively, which on the Line ladder
        // (Great [-250, 400], Ok [-600, 1000]) is four Oks and one Great, so the two engines part on
        // the STATISTICS and not only on a total. The presses in between ('a', 'e', 'o', 'i', 'h' and
        // the trailing 't') are non-opening cells sitting inside their spans, so they stay at 0 under
        // both rules and the case isolates the opening cell.
        name: 'scripted/firstCharLateInSpan', fixture: 'subtimed', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1690, 'c'], [1695, 'a'], [2390, 'k'], [2395, 'e'], [2400, ' '],
               [2790, 't'], [2795, 'o'], [3290, 'n'], [3295, 'i'], [3990, 'g'], [3995, 'h'], [3999, 't']]
    },
    {
        // Backlog 181: every press deep inside the span the AUTHORED cut gives its cell, which for
        // three of them is the neighbouring span under the derived cut. Under the authored split
        // the whole map is delta 0; had the browser fallen back to the syllabifier, 't' and 'i' of
        // "beautiful" (pressed at 1200 and 1250, inside beauti|ful's first span but 250 and 200
        // before beau|tiful's second one opens at 1450) and the 'g' of "tonight" (pressed at 2900,
        // inside to|nig|ht's middle span but 300 before to|ni|ght's last one opens at 3200) would
        // each be graded off an edge instead. The C# arm reads the same split_chars, so the two
        // sides part on this case the moment either stops honouring it.
        name: 'scripted/authoredSpans', fixture: 'authoredSplit', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'b'], [1050, 'e'], [1100, 'a'], [1150, 'u'], [1200, 't'], [1250, 'i'],
               [1500, 'f'], [1600, 'u'], [1700, 'l'], [1900, ' '],
               [1950, 't'], [2100, 'o'], [2700, 'n'], [2800, 'i'], [2900, 'g'], [3300, 'h'], [3400, 't']]
    },
    {
        // Backlog 209's asked-for shape, the field report's own: the freestyle section MASHED the
        // instant it opens (four keys at 1000, where the pure span rule paid every one of them delta
        // 0 seconds ahead of the vocal) and the stretched run mashed the same way at the top of its
        // syllable. Every cell in between keeps the span, so one script puts both rules side by side:
        // the markers target 1000 / 2000 / 3000 / 4000 and are judged on those, the two 'a's of the
        // first syllable are inside [5000, 9000] and are judged 0, and the run's three cells target
        // 8200 / 10600 / 13800 against presses at 9100 / 9200 / 9300.
        name: 'scripted/mashedStretch', fixture: 'freestyleStretch', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'q'], [1000, 'q'], [1000, 'q'], [1000, 'q'], [5000, ' '],
               [5000, 'a'], [6000, 'a'], [9100, 'a'], [9200, 'a'], [9300, 'a']]
    },
    {
        // Backlog 208, THE LINE-START SNAP, on the one fixture that can reach it. "ab" is typed out,
        // which rolls the caret straight on to the cell-less L1 and leaves it parked there with
        // nothing sealed and L2 not yet started. At 10500 (L2's cue, 9500 ms before L1's window even
        // closes) the snap hands the caret to L2, and "cd" is then typed on its own targets for a
        // clean map.
        //
        // Without the snap the caret sits on L1 until L1's own seal at 20000 hands it over, so both
        // presses are eaten by a complete line and L2 seals with two characters nobody typed. The
        // two arms therefore part on the STATISTICS, not on a tier, which is what makes this case
        // worth having in a differential sweep rather than only in a JS pin.
        name: 'scripted/parkedSnap', fixture: 'parkedLine', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'a'], [1500, 'b'], [12000, 'c'], [12500, 'd']]
    },
    {
        // Backlog 208, DRAG FREEDOM. "cat dog" runs to 6000 (where "hi" starts), and the 'g' is
        // pressed at 6500, half a second past that deadline and well inside FLETCHER_DRAG_GRACE_MS.
        // The line the player is still typing is not force-sealed out from under them, so the 'g'
        // lands on its own cell (1500 ms late, which the ladder grades honestly) and the line then
        // seals with nothing missed.
        //
        // Under a pinned caret the same press is a different run entirely: L0 seals at 6000 with the
        // 'g' a miss, the caret is on "hi", and the 6500 press is a typo on its 'h'. So this case
        // parts on where the caret was, not on what a press was worth.
        name: 'scripted/dragPastTheDeadline', fixture: 'catDogThenHi', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'c'], [1667, 'a'], [2333, 't'], [3000, ' '], [3000, 'd'], [3667, 'o'],
               [6500, 'g'], [7000, 'h'], [7500, 'i']]
    },
    {
        // Backlog 208, THE RUSH CAP and its RE-ARM. Nine presses at 1000, where the playhead has
        // reached exactly one countable character: the caret is 1, 2, 3, 4 and then 5 characters
        // ahead through 't','h','e','q','u' (the word gap spends no budget), all of which still earn
        // combo, and the sixth countable press 'i' puts it 6 ahead, which is over
        // FLETCHER_MAX_CHARS_AHEAD and earns none. 'c' and 'k' follow it out past the cap and take
        // no further break, because the run is already at zero.
        //
        // The clock then catches up: by 3000 the playhead has passed nine countable targets and the
        // caret is on the ninth, so "brown" is typed back INSIDE the cap and the run rebuilds, which
        // is the re-arm. Typing 'n' finishes the line and rolls the caret straight on to "fox
        // jumps", where the same excursion happens again at 3800 and breaks the rebuilt run: two
        // breaks with an earned streak between them is what says the cap re-arms rather than
        // latching.
        // Backlog 218, THE RUSH BOUND, written out rather than left to a seed because it is the one
        // shape whose absence is invisible: both arms would agree on the UNBOUNDED roll and stay
        // green. "ab" is typed out on time at 1000 and 1500, which is eleven seconds before entry
        // into "cdefghij" opens at 12500, so the roll is REFUSED and the caret parks past "ab"'s last
        // cell. The two presses at 6000 and 9000 land in that park and are INERT: no cell, no
        // judgement, no typo, no combo break and nothing in the accuracy denominator. At 12500 the
        // line-start snap performs the deferred roll and the rest of the line is typed out from
        // there, the first five presses inside the rush cap and the sixth over it.
        //
        // Under the pre-218 UNBOUNDED era the same script is a different run entirely: the caret goes
        // to "cdefghij" on the 'b', so the 6000 and 9000 presses spoil its first two cells. The two
        // arms therefore part on the STATISTICS, which is what makes this worth a differential sweep
        // slot rather than only a JS pin (see ClearingTheConfigFrameBoundedRushBitReDerivesTheRoll).
        name: 'scripted/rushBoundPark', fixture: 'instrumentalGap', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 'a'], [1500, 'b'], [6000, 'c'], [9000, 'd'],
               [12500, 'c'], [12500, 'd'], [12500, 'e'], [12500, 'f'], [12500, 'g'], [12500, 'h'],
               [14750, 'i'], [14875, 'j']]
    },
    {
        name: 'scripted/rushPastTheCap', fixture: 'quickBrownFox', spaceSkipsWord: false, skipPresses: 0,
        keys: [[1000, 't'], [1000, 'h'], [1000, 'e'], [1000, ' '], [1000, 'q'], [1000, 'u'],
               [1000, 'i'], [1000, 'c'], [1000, 'k'],
               [3000, ' '], [3000, 'b'], [3000, 'r'], [3000, 'o'], [3000, 'w'], [3000, 'n'],
               [3800, 'f'], [3800, 'o'], [3800, 'x'], [3800, ' '], [3800, 'j'], [3800, 'u'],
               [3800, 'm'], [3800, 'p'], [3800, 's']]
    }
];

// ---------------------------------------------------------------------------------------------
const names = ['catDog', 'abCd', 'catDogThenHi', 'quickBrownFox', 'mixedTiers', 'syllabic',
               'syllableWords', 'stylised', 'subtimed', 'authoredSplit', 'freestyleStretch',
               'parkedLine', 'instrumentalGap'];
const cases = [];

for (const scripted of SCRIPTED) {
    cases.push(Object.assign({
        name: scripted.name,
        fixture: scripted.fixture,
        spaceSkipsWord: scripted.spaceSkipsWord,
        skipPresses: scripted.skipPresses,
        keys: scripted.keys
    }, play(scripted.fixture, scripted.keys, scripted.spaceSkipsWord)));
}

for (const name of names) {
    for (let seed = 1; seed <= 30; seed++) {
        for (const spaceSkipsWord of [false, true]) {
            const generated = generate(name, seed * 7919 + (spaceSkipsWord ? 104729 : 0), spaceSkipsWord);
            const run = play(name, generated.keys, spaceSkipsWord);

            cases.push(Object.assign({
                name: name + '/' + seed + '/' + (spaceSkipsWord ? 'skip' : 'noskip'),
                fixture: name,
                spaceSkipsWord: spaceSkipsWord,
                skipPresses: generated.skipPresses,
                keys: generated.keys
            }, run));
        }
    }
}

const fixtures = {};
for (const name of names) fixtures[name] = cellsOf(name);

process.stdout.write(JSON.stringify({ fixtures: fixtures, cases: cases }));
