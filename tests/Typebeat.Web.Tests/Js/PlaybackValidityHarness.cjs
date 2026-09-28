// Node harness for /play's playback-validity veto (backlog 312): makePlaybackValidity in
// typebeat-player.js, the port of the desktop's MasterGameplayClockContainer.checkPlaybackValidity.
// Every scenario drives one accumulator with FAKE clocks (an audio clock and a wall clock, both in
// ms) exactly as tick() does, observe(nowMs(), performance.now(), audioCtx.state === 'running'),
// and reports what it concluded. PlaybackValidityTest asserts the numbers.
//
// Usage: node PlaybackValidityHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const PV = global.window.TypeBeatCore.playbackValidity;
const FRAME = 1000 / 60;

// A tiny fake player: the audio clock and the wall clock advance separately, and every frame is
// one tick() observation.
function rig() {
    const v = PV.makePlaybackValidity();
    const r = { v: v, audio: 0, wall: 0, running: true };
    r.frame = function (audioDelta, wallDelta) {
        r.audio += audioDelta;
        r.wall += wallDelta;
        v.observe(r.audio, r.wall, r.running);
    };
    r.frames = function (n, audioDelta, wallDelta) { for (let i = 0; i < n; i++) r.frame(audioDelta, wallDelta); };
    // startSourceAt: the clock is rebased onto the seek target, and the player says so.
    r.seek = function (toMs, announce) {
        r.audio = toMs;
        if (announce) v.discontinuity();
    };
    r.out = function () { return { valid: v.valid, discrepancies: v.discrepancies }; };
    return r;
}

const out = { constants: { discrepancyMs: PV.PLAYBACK_DISCREPANCY_MS, allowed: PV.ALLOWED_PLAYBACK_DISCREPANCIES } };

// 1. An honest, slightly drifting audio device over a four-minute song: the audio clock runs
//    0.05% fast (a 44.1 kHz device against a 44.122 kHz truth) and advances in coarse 10 ms render
//    quanta, so individual frame deltas swing between 10 and 20 ms against a steady 16.7 ms wall.
{
    const r = rig();
    let audioTrue = 0;
    for (let i = 0; i < 4 * 60 * 60; i++) {
        audioTrue += FRAME * 1.0005;
        const quantised = Math.floor(audioTrue / 10) * 10;
        r.frame(quantised - r.audio, FRAME);
    }
    out.normalDrift = r.out();
}

// 2. A stalled audio clock: the song plays normally for 5 s, then the audio clock freezes while
//    wall time keeps running. The frame at which the veto trips is recorded with the count of
//    discrepancies that had been seen when it did.
{
    const r = rig();
    r.frames(300, FRAME, FRAME);
    let trippedAtDiscrepancy = null, lastValidDiscrepancies = null, stalledMs = 0;
    for (let i = 0; i < 60 * 30 && trippedAtDiscrepancy === null; i++) {
        r.frame(0, FRAME);
        stalledMs += FRAME;
        if (r.v.valid) lastValidDiscrepancies = r.v.discrepancies;
        else trippedAtDiscrepancy = r.v.discrepancies;
    }
    out.stalled = { trippedAtDiscrepancy: trippedAtDiscrepancy, lastValidDiscrepancies: lastValidDiscrepancies, stalledMs: Math.round(stalledMs), final: r.out() };
}

// 3. A stalled clock that never gets past the allowance: exactly six discrepancies' worth of stall
//    (each costs ~300 ms plus the re-seed frame), then the audio recovers. Still valid.
{
    const r = rig();
    r.frames(60, FRAME, FRAME);
    while (r.v.discrepancies < 6) r.frame(0, FRAME);
    r.frames(600, FRAME, FRAME);
    out.stalledWithinAllowance = r.out();
}

// 4. A hidden tab. rAF stops and the setInterval backstop is throttled: first to one tick a second
//    for a minute, then Chrome's intensive throttling (one tick a minute) for five minutes, then
//    the tab comes back to rAF. The audio kept playing throughout, so BOTH clocks jump together.
{
    const r = rig();
    r.frames(600, FRAME, FRAME);
    r.frames(60, 1000, 1000);
    r.frames(5, 60000, 60000);
    r.frames(600, FRAME, FRAME);
    const clean = r.out();
    // The control: the same gap with the audio clock NOT moving (a suspended device while hidden
    // that the context still reports as running) is caught on the very first backstop tick back.
    const c = rig();
    c.frames(600, FRAME, FRAME);
    c.frame(0, 60000);
    out.hiddenTab = { clean: clean, frozenControl: c.out() };
}

// 5. A seek (the intro skip, a gap skip). The clock jumps 20 s forward in one frame; announced
//    (as startSourceAt does), it is not measured at all. The unannounced control shows the jump
//    WOULD be a discrepancy, so the announcement is what keeps it out.
{
    const r = rig();
    r.frames(120, FRAME, FRAME);
    r.seek(r.audio + 20000, true);
    r.frames(120, FRAME, FRAME);
    const announced = r.out();

    const c = rig();
    c.frames(120, FRAME, FRAME);
    c.seek(c.audio + 20000, false);
    c.frames(120, FRAME, FRAME);

    // The seek clears the BASELINE, not the COUNT: six discrepancies before it and one after it
    // still trip the veto, so a skip cannot launder a stalled clock.
    const k = rig();
    k.frames(60, FRAME, FRAME);
    while (k.v.discrepancies < 6) k.frame(0, FRAME);
    k.seek(k.audio + 20000, true);
    k.frames(60, FRAME, FRAME);
    const beforeRestall = k.out();
    while (k.v.discrepancies < 7) k.frame(0, FRAME);

    out.seek = { announced: announced, unannouncedControl: c.out(), countSurvivesSeek: { beforeRestall: beforeRestall, after: k.out() } };
}

// 6. The audio context not running (resuming after the start gate's click, or suspended by the
//    browser): the audio clock is frozen but the frames say so. Five seconds of it cost nothing,
//    and the first running frame only re-seeds.
{
    const r = rig();
    r.running = false;
    r.frames(300, 0, FRAME);
    r.running = true;
    r.frames(600, FRAME, FRAME);
    const notRunning = r.out();
    // The control: the same frozen five seconds reported as running trips the veto.
    const c = rig();
    c.frames(300, 0, FRAME);
    out.notRunning = { clean: notRunning, runningControl: c.out() };
}

process.stdout.write(JSON.stringify(out));
