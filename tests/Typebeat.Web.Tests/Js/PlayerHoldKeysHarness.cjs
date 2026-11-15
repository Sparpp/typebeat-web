// Node harness for the HOLD half of /play's key handler: the shipped router
// (TypeBeatCore.display.routeKeyDown / routeKeyUp / releaseHeldKey, the whole of the player's keydown
// and keyup listeners and its focus-loss release) driven through keydowns, keyups and a focus loss
// on a map with holds, between the 60 Hz ticks of a fake clock whose phase keeps every tick off a
// whole millisecond (PlayerDisplayHarness.cjs's keyOrder tick).
//
// It emits every engine call the router made (processKey, processRelease, processBackspace, with
// their times and answers) and the account the run ends with, for HoldKeysLiveParityTest.cs, which
// feeds the game's real TypingEngine the same ticks and the same events through a transcription of
// TypeBeatPlayfield.KeyHandler's hold rules (OnKeyDown's release-first, OnKeyUp, releaseHold) and
// compares the two call logs and accounts with no tolerance.
//
// The map comes from the C# side, written by the game's own encoder: the second argument is a JSON
// file of { osu: text }.
//
// Usage: node PlayerHoldKeysHarness.cjs <absolute path to typebeat-core.js> <fixture json path>

'use strict';

const fs = require('fs');
const nodePath = require('path');

const corePath = process.argv[2];
const fixturePath = process.argv[3];

if (!corePath || !fixturePath) {
    process.stderr.write('usage: PlayerHoldKeysHarness.cjs <typebeat-core.js> <fixture.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;
const OSU = JSON.parse(fs.readFileSync(fixturePath, 'utf8')).osu;

// PlayerDisplayHarness.cjs's keyOrder tick: 60 Hz, phased so no tick lands on a whole millisecond.
const TICK_MS = 1000 / 60;
const TICK_PHASE = 0.37;
const END = 14000;

function event(type, t, key, mods) {
    const m = mods || {};
    const code = m.code || (key === ' ' ? 'Space' : (key.length === 1 && /[a-z]/i.test(key) ? 'Key' + key.toUpperCase() : key));
    return {
        type: type, t: t, key: key, code: code,
        ctrlKey: !!m.ctrl, altKey: !!m.alt, metaKey: false, shiftKey: !!m.shift, repeat: !!m.repeat,
        prevented: false,
        preventDefault: function () { this.prevented = true; }
    };
}

const down = (t, key, mods) => event('down', t, key, mods);
const up = (t, key, mods) => event('up', t, key, mods);
const blur = (t) => ({ type: 'blur', t: t });

// The map (HoldKeysLiveParityTest's fixture): "love me" with the 'o' of "love" held over
// [1300, 1900] (ticks 1450, 1600, 1750), its hand-off 'v' floored to 1900, and the 'e' of "me" held
// over [2600, 3000]; then "Oh go" with the capital 'O' held from the word's start over [6000, 6500].
const SCENARIOS = {
    // The held key's own key-up ends the hold, at the key-up's quantised instant.
    keyUpReleases: [down(1000.2, 'l'), down(1300.4, 'o'), up(1650.6, 'o'), down(1900.3, 'v'), up(1950.2, 'v'), down(2100.2, 'e')],
    // Another key going down ends the hold first, at that key's instant, and the held key's later
    // key-up finds nothing to end.
    anotherKeyReleasesFirst: [down(1000.2, 'l'), down(1300.4, 'o'), down(1700.5, 'v'), up(1750.4, 'o'), up(1760.2, 'v')],
    // Modifiers never end a hold, going down or up; the release then lands in the Great slack.
    modifiersDoNotRelease: [down(1000.2, 'l'), down(1300.4, 'o'), down(1400.3, 'Shift', { code: 'ShiftLeft' }), down(1450.6, 'Control', { code: 'ControlRight' }),
        up(1500.2, 'Control', { code: 'ControlRight' }), up(1600.4, 'Shift', { code: 'ShiftLeft' }), up(1850.3, 'o')],
    // The held key's OS repeat neither ends the hold nor types.
    repeatDoesNotRelease: [down(1000.2, 'l'), down(1300.4, 'o'), down(1500.2, 'o', { repeat: true }), down(1533.6, 'o', { repeat: true }), up(1880.5, 'o')],
    // A focus loss (a hidden tab, a blurred window) ends the hold now: the page never sees that key-up.
    focusLossReleases: [down(1000.2, 'l'), down(1300.4, 'o'), blur(1500.5), up(1600.3, 'o'), down(1900.2, 'v')],
    // A key-up past the hold's end: the engine completed the hold as a Great at its end, so the
    // release finds no hold to end.
    keyUpPastTheEnd: [down(1000.2, 'l'), down(1300.4, 'o'), up(2100.6, 'o'), down(2200.2, 'v')],
    // A backspace is another key: it ends the hold before it erases.
    backspaceReleasesFirst: [down(1000.2, 'l'), down(1300.4, 'o'), down(1500.4, 'Backspace'), up(1520.2, 'Backspace'), up(1600.2, 'o'), down(1700.3, 'o'), up(1800.2, 'o')],
    // A capital typed under Shift opens the hold; the Shift key-up does not end it, the letter's does.
    // Enter gives up the first line, so the caret is on the second when its held capital comes.
    shiftedCapital: [down(5000.2, 'Enter'), down(5999.6, 'Shift', { code: 'ShiftLeft' }), down(6000.2, 'O', { shift: true, code: 'KeyO' }), up(6100.4, 'Shift', { code: 'ShiftLeft' }), up(6400.3, 'O', { code: 'KeyO' }), down(6500.4, 'h')],
    // Holding through the next line's newline and onto its first letters: each key-down ends the
    // hold it finds, and a hand-off typed late closes nothing.
    rolledOn: [down(1000.2, 'l'), down(1300.4, 'o'), down(1900.6, 'v'), down(1950.3, 'e'), down(2200.2, ' '), down(2300.3, 'm'), down(2600.2, 'e'), up(2700.6, 'v'), up(2990.1, 'e')]
};

function run(events) {
    const map = TB.buildBeatmap(TB.parseLyricOsu(OSU), false);
    const engine = new TB.TypingEngine(map);
    engine.healthAccount.failed = () => false;

    const host = {
        engine: engine,
        clock: 0,
        now: function () { return host.clock; },
        gaps: D.computeGaps(map.lines),
        introTarget: D.introSkipTarget(map.lines),
        selection: null,
        getSelection: function () { return host.selection; },
        setSelection: function (s) { host.selection = s; },
        performSkip: function () {}
    };

    const calls = [];
    const key = engine.processKey.bind(engine);
    const release = engine.processRelease.bind(engine);
    const backspace = engine.processBackspace.bind(engine);
    const enter = engine.processEnter.bind(engine);
    engine.processEnter = function (t) { const r = enter.apply(null, arguments); calls.push({ fn: 'enter', t: t, result: r }); return r; };
    engine.processKey = function (c, t) { const r = key.apply(null, arguments); calls.push({ fn: 'key', c: c, t: t, result: r }); return r; };
    engine.processRelease = function (t) { const r = release.apply(null, arguments); calls.push({ fn: 'release', t: t, result: r }); return r; };
    engine.processBackspace = function () { const r = backspace.apply(null, arguments); calls.push({ fn: 'backspace', result: r }); return r; };

    let next = 0;

    for (let k = 0; ; k++) {
        const tick = TICK_PHASE + k * TICK_MS;

        while (next < events.length && events[next].t < tick) {
            const e = events[next++];
            host.clock = e.t;
            if (e.type === 'down') D.routeKeyDown(e, host);
            else if (e.type === 'up') D.routeKeyUp(e, host);
            else D.releaseHeldKey(host, D.roundHalfEven(e.t));
        }

        if (tick > END) break;
        engine.update(tick);
    }

    const score = TB.computeScore(engine);

    return {
        calls: calls,
        submitted: {
            statistics: score.statistics,
            maxCombo: score.maxCombo,
            totalScore: score.totalScore
        }
    };
}

const out = {};
for (const [name, events] of Object.entries(SCENARIOS)) {
    out[name] = Object.assign({
        events: events.map(e => ({ type: e.type, t: e.t, key: e.key || null, code: e.code || null, repeat: !!e.repeat, shift: !!e.shiftKey }))
    }, run(events));
}

process.stdout.write(JSON.stringify(out));
