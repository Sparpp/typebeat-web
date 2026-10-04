// Node harness for /play's pause, focus-loss pause, retry and quit (backlog 311). Unlike the display
// harness, which drives pure exports, this one MOUNTS the real player (typebeat-player.js's
// mountPlayer) over a minimal fake DOM, a fake AudioContext whose currentTime only advances while
// its state is 'running', and fake timers, rAF and performance.now(), so the lifecycle itself is
// under test: which listeners are live, which loops run, what the clock does across a pause, and
// whether onFinish (the only road to /play/submit) is ever reached.
//
// Emits { scenario: observations } as JSON for PlayerPauseTest.
//
// Usage: node PlayerPauseHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

// ---- fake DOM ----------------------------------------------------------------

function makeListeners() {
    const map = new Map();
    return {
        add(type, fn) {
            if (!map.has(type)) map.set(type, []);
            const list = map.get(type);
            if (!list.includes(fn)) list.push(fn);
        },
        remove(type, fn) {
            const list = map.get(type);
            if (!list) return;
            const i = list.indexOf(fn);
            if (i >= 0) list.splice(i, 1);
        },
        fire(type, ev) {
            const list = (map.get(type) || []).slice();
            for (const fn of list) fn(ev);
            return list.length;
        },
        count(type) { return (map.get(type) || []).length; }
    };
}

class FakeElement {
    constructor(tag) {
        this.tagName = String(tag).toUpperCase();
        this.children = [];
        this.parentNode = null;
        this.className = '';
        this._html = '';
        this.textContent = '';
        this.disabled = false;
        this.type = '';
        this.style = { setProperty() {}, removeProperty() {} };
        this.offsetWidth = 0;
        this.offsetHeight = 0;
        this.offsetTop = 0;
        this.clientWidth = 0;
        this._listeners = makeListeners();
        const self = this;
        this.classList = {
            add(c) { const s = new Set(self.className.split(/\s+/).filter(Boolean)); s.add(c); self.className = [...s].join(' '); },
            remove(c) { self.className = self.className.split(/\s+/).filter(x => x && x !== c).join(' '); },
            toggle(c, on) { if (on === undefined) on = !this.contains(c); if (on) this.add(c); else this.remove(c); return on; },
            contains(c) { return self.className.split(/\s+/).includes(c); }
        };
    }
    get innerHTML() { return this._html; }
    set innerHTML(v) { this._html = String(v); this.children = []; this.textContent = String(v); }
    appendChild(c) {
        if (c && c._fragment) { for (const k of c.children.slice()) this.appendChild(k); c.children = []; return c; }
        if (c.parentNode) c.parentNode.removeChild(c);
        c.parentNode = this;
        this.children.push(c);
        return c;
    }
    append(...cs) { for (const c of cs) this.appendChild(c); }
    insertBefore(c) { return this.appendChild(c); }
    removeChild(c) {
        const i = this.children.indexOf(c);
        if (i >= 0) this.children.splice(i, 1);
        c.parentNode = null;
        return c;
    }
    remove() { if (this.parentNode) this.parentNode.removeChild(this); }
    querySelector() { return new FakeElement('span'); }
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { left: 0, right: 0, top: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; }
    addEventListener(type, fn) { this._listeners.add(type, fn); }
    removeEventListener(type, fn) { this._listeners.remove(type, fn); }
    click() { this._listeners.fire('click', { preventDefault() {} }); }
    focus() {}
    blur() {}
    setAttribute() {}
    getAttribute() { return null; }
}

function findByClass(node, cls) {
    if (node.classList && node.classList.contains(cls)) return node;
    for (const c of node.children || []) {
        const hit = findByClass(c, cls);
        if (hit) return hit;
    }
    return null;
}

const docListeners = makeListeners();
const winListeners = makeListeners();

global.document = {
    hidden: false,
    createElement(tag) { return new FakeElement(tag); },
    createDocumentFragment() { const f = new FakeElement('#fragment'); f._fragment = true; return f; },
    addEventListener(type, fn) { docListeners.add(type, fn); },
    removeEventListener(type, fn) { docListeners.remove(type, fn); },
    getElementById() { return null; },
    hasFocus() { return true; }
};

// ---- fake clocks: wall time, timers, rAF -------------------------------------

let wall = 1000; // performance.now()
let nextTimerId = 1;
const timers = new Map(); // id -> { due, fn, every }
let rafQueue = new Map();  // id -> fn

global.window = {
    addEventListener(type, fn) { winListeners.add(type, fn); },
    removeEventListener(type, fn) { winListeners.remove(type, fn); }
};

require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;
const P = TB.pause;

// Installed only now, so the scripts' own load is untouched by them.
Object.defineProperty(global, 'performance', { value: { now: () => wall }, configurable: true, writable: true });
global.setTimeout = function (fn, ms) { const id = nextTimerId++; timers.set(id, { due: wall + (ms || 0), fn, every: 0 }); return id; };
global.clearTimeout = function (id) { timers.delete(id); };
global.setInterval = function (fn, ms) { const id = nextTimerId++; timers.set(id, { due: wall + ms, fn, every: ms }); return id; };
global.clearInterval = function (id) { timers.delete(id); };
global.requestAnimationFrame = function (fn) { const id = nextTimerId++; rafQueue.set(id, fn); return id; };
global.cancelAnimationFrame = function (id) { rafQueue.delete(id); };

let intervalsLive = () => [...timers.values()].filter(t => t.every > 0).length;

// ---- fake AudioContext --------------------------------------------------------

let lastCtx = null;

class FakeAudioContext {
    constructor() {
        this.state = 'running';
        this.currentTime = 0;
        this.destination = {};
        this.sourcesCreated = 0;
        this.suspendCalls = 0;
        this.resumeCalls = 0;
        this._listeners = makeListeners();
        lastCtx = this;
    }
    _set(state) {
        if (this.state === state) return;
        this.state = state;
        this._listeners.fire('statechange', {});
    }
    suspend() { this.suspendCalls++; this._set('suspended'); return Promise.resolve(); }
    resume() { this.resumeCalls++; this._set('running'); return Promise.resolve(); }
    close() { this._set('closed'); return Promise.resolve(); }
    addEventListener(type, fn) { this._listeners.add(type, fn); }
    removeEventListener(type, fn) { this._listeners.remove(type, fn); }
    createBufferSource() {
        this.sourcesCreated++;
        return { buffer: null, connect() {}, disconnect() {}, start() {}, stop() {} };
    }
    createGain() { return { gain: { value: 1 }, connect() {} }; }
    decodeAudioData(bytes, ok) {
        ok({ duration: 60, numberOfChannels: 1, getChannelData() { return new Float32Array(1); } });
    }
}
global.window.AudioContext = FakeAudioContext;

// Advance wall time in 16 ms frames. The audio clock moves with it only while 'running'. Timers
// due inside a frame fire; rAF callbacks fire once per frame unless the tab is hidden.
function advance(ms) {
    const FRAME = 16;
    let left = ms;
    while (left > 0) {
        const dt = Math.min(FRAME, left);
        left -= dt;
        wall += dt;
        if (lastCtx && lastCtx.state === 'running') lastCtx.currentTime += dt / 1000;
        for (const [id, t] of [...timers.entries()]) {
            if (!timers.has(id) || t.due > wall) continue;
            if (t.every > 0) t.due += t.every; else timers.delete(id);
            t.fn();
        }
        if (!global.document.hidden) {
            const q = rafQueue;
            rafQueue = new Map();
            for (const fn of q.values()) fn();
        }
    }
}

// Advance until the player's clock reads `ms` (it only moves while the context runs).
function advanceClockTo(api, ms) {
    let guard = 0;
    while (api.now() < ms && guard++ < 100000) advance(Math.min(16, Math.max(1, ms - api.now())));
}

function key(k, extra) {
    const ev = Object.assign({ key: k, code: '', repeat: false, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false,
        defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }, extra || {});
    if (!ev.code) {
        if (k === ' ') ev.code = 'Space';
        else if (/^[a-z]$/i.test(k)) ev.code = 'Key' + k.toUpperCase();
        else if (k === '`') ev.code = 'Backquote';
    }
    return ev;
}
function keydown(k, extra) { const ev = key(k, extra); docListeners.fire('keydown', ev); return ev; }
function keyup(k, extra) { const ev = key(k, extra); docListeners.fire('keyup', ev); return ev; }

function hide() { global.document.hidden = true; docListeners.fire('visibilitychange', {}); }
function show() { global.document.hidden = false; docListeners.fire('visibilitychange', {}); }
function blurWindow() { winListeners.fire('blur', {}); }
function focusWindow() { winListeners.fire('focus', {}); }

// ---- maps ----------------------------------------------------------------------

const OSU_HEADER =
    '[General]\n' +
    'AudioFilename: a.mp3\n' +
    '[Metadata]\n' +
    'Title: t\n' +
    'Artist: a\n' +
    '[Lyrics]\n';

// "ab cd", sung [1000, 3000]. Cell targets: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
// Its first line sits inside 2 s, so the clock starts in the negative pre-roll.
const abcdOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":4000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n';

// Two lines around a long instrumental: line 0 sings [1000, 2000], line 1 not until 20000.
const gapOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":30000}\n' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}\n' +
    '{"text":"cd","start_ms":20000,"end_ms":21000,"words":[{"text":"cd","start_ms":20000,"end_ms":21000,"score":1}]}\n';

// A long intro: the first line does not start until 15000, so the intro skip is offered.
const introOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":20000}\n' +
    '{"text":"ab","start_ms":15000,"end_ms":16000,"words":[{"text":"ab","start_ms":15000,"end_ms":16000,"score":1}]}\n';

// ---- mounting ----------------------------------------------------------------

function mount(osu, withExit) {
    global.document.hidden = false;
    timers.clear();
    rafQueue = new Map();
    const log = { finishes: [], playStarts: 0, exits: 0 };
    const container = new FakeElement('div');
    const opts = {
        osuText: osu,
        audioArrayBuffer: new ArrayBuffer(8),
        title: 't',
        artist: 'a',
        onPlayStart() { log.playStarts++; },
        onFinish(results) { log.finishes.push({ passed: results.passed, playbackValid: results.playbackValid }); }
    };
    if (withExit !== false) opts.onExit = function () { log.exits++; };
    const api = TB.mountPlayer(container, opts);
    const ctx = lastCtx;
    // The start gate: Space begins the play (begin() mints the token through onPlayStart).
    keydown(' ');
    return { api, ctx, log, container };
}

function overlayOf(run) { return findByClass(run.container, 'tb-overlay'); }
function pauseCardUp(run) { return !!findByClass(run.container, 'tb-pause'); }
function clickPause(run, cls) { const b = findByClass(run.container, cls); if (b) b.click(); return !!b; }

function live() {
    return {
        keydown: docListeners.count('keydown'),
        keyup: docListeners.count('keyup'),
        visibility: docListeners.count('visibilitychange'),
        blur: winListeners.count('blur'),
        beforeunload: winListeners.count('beforeunload'),
        raf: rafQueue.size,
        intervals: intervalsLive()
    };
}

const out = {};

out.constants = {
    cooldownMs: P.PAUSE_COOLDOWN_MS,
    holdMs: P.HOLD_TO_CONFIRM_MS,
    hint: P.PAUSE_HINT
};

// ---- 1. keys ignored and the clock frozen while paused ----------------------------
(function () {
    const run = mount(abcdOsu);
    const { api, ctx } = run;
    const startedNegative = api.now() < 0;
    advanceClockTo(api, 1000);
    const esc = keydown('Escape');
    const pausedAt = api.now();
    const caretBefore = api.engine.caretIndex;
    advance(5000);
    const pressedWhilePaused = keydown('a');
    const afterPress = api.engine.caretIndex;
    const nowAfterWait = api.now();
    const loopsWhilePaused = live();
    const card = pauseCardUp(run);
    keydown('Escape'); // Back on the pause card is Continue
    const resumedState = { paused: api.paused, ctx: ctx.state, card: pauseCardUp(run) };
    keydown('a');
    const afterResumePress = api.engine.caretIndex;
    advance(100);
    out.frozen = {
        startedNegative,
        escPrevented: esc.defaultPrevented,
        paused: true,
        suspendCalls: ctx.suspendCalls,
        pausedAt,
        nowAfterWait,
        caretBefore,
        caretAfterPausedPress: afterPress,
        pausedPressPrevented: pressedWhilePaused.defaultPrevented,
        cardShown: card,
        loopsWhilePaused,
        resumed: resumedState,
        caretAfterResumePress: afterResumePress,
        clockMovedAfterResume: api.now() > pausedAt + 50
    };
    api.destroy();
})();

// ---- 1b. pausing inside the negative pre-roll ---------------------------------------
(function () {
    const run = mount(abcdOsu);
    const { api } = run;
    advance(100);
    const before = api.now();
    keydown('Escape');
    const pausedAt = api.now();
    advance(3000);
    const held = api.now();
    keydown('Escape');
    advance(200);
    out.preRoll = { before, pausedAt, held, after: api.now(), negativeWhenPaused: pausedAt < 0 };
    api.destroy();
})();

// ---- 2. the cooldown refuses a second pause inside 1000 ms of real time -----------------
(function () {
    const run = mount(abcdOsu);
    const { api } = run;
    advance(200);
    keydown('Escape');                   // pause at wall W
    const first = api.paused;
    advance(100);
    keydown('Escape');                   // resume at W + 100 (resume is never gated)
    const resumed = !api.paused;
    advance(400);
    keydown('Escape');                   // W + 500: refused
    const secondRefused = !api.paused;
    // An auto-pause inside the cooldown is refused too, and then retried every tick until it can.
    blurWindow();
    const blurRefused = !api.paused;
    let tookAt = null;
    const blurWall = wall;
    for (let i = 0; i < 100 && tookAt === null; i++) { advance(16); if (api.paused) tookAt = wall; }
    const firstWall = blurWall - 500;
    focusWindow();
    keydown('Escape');
    advance(1100);
    keydown('Escape');
    const thirdAfterCooldown = api.paused;
    out.cooldown = {
        first,
        resumed,
        secondRefused,
        blurRefused,
        retriedPauseAfterMs: tookAt === null ? null : tookAt - firstWall,
        thirdAfterCooldown,
        pureInside: P.pauseCooldownActive(1000, 1999),
        pureAtEdge: P.pauseCooldownActive(1000, 2000),
        pureNever: P.pauseCooldownActive(null, 0)
    };
    api.destroy();
})();

// ---- 3. auto-pause is skipped inside a skip window, and taken outside one ---------------
(function () {
    const gapMap = TB.buildBeatmap(TB.parseLyricOsu(gapOsu));
    const gaps = D.computeGaps(gapMap.lines);
    const g = gaps[0];

    const run = mount(gapOsu);
    const { api } = run;
    advanceClockTo(api, g.gapStartTime + 1000);
    blurWindow();
    const blurInGap = api.paused;
    focusWindow();
    hide();
    const hideInGap = api.paused;
    // The song plays on through the hidden break (the backstop ticks it), and the moment the clock
    // leaves the skip window the retried auto-pause takes it.
    let pausedAtClock = null;
    for (let i = 0; i < 4000 && pausedAtClock === null; i++) { advance(16); if (api.paused) pausedAtClock = api.now(); }
    show();
    api.destroy();

    // Control: the same blur on a live line pauses at once.
    const ctl = mount(abcdOsu);
    advanceClockTo(ctl.api, 1500);
    blurWindow();
    const blurOnLine = ctl.api.paused;
    ctl.api.destroy();

    // Intro run-up: before the intro skip target is a break too.
    const introMap = TB.buildBeatmap(TB.parseLyricOsu(introOsu));
    const introTarget = D.introSkipTarget(introMap.lines, D.gameplayStartTime(introMap));
    const intro = mount(introOsu);
    advanceClockTo(intro.api, 3000);
    blurWindow();
    const blurInIntro = intro.api.paused;
    intro.api.destroy();

    // The browser suspending the context is NOT waived by a break: the clock is frozen either way.
    const ctxRun = mount(gapOsu);
    advanceClockTo(ctxRun.api, g.gapStartTime + 1000);
    ctxRun.ctx._set('interrupted');
    const interruptedInGap = ctxRun.api.paused;
    ctxRun.api.destroy();

    out.breakException = {
        gapStart: g.gapStartTime,
        skipTarget: g.skipTarget,
        introTarget,
        blurInGap,
        hideInGap,
        pausedAtClock,
        blurOnLine,
        blurInIntro,
        interruptedInGap,
        pure: {
            inGap: P.inSkippableInstrumental(gaps, null, g.gapStartTime + 1),
            atTarget: P.inSkippableInstrumental(gaps, null, g.skipTarget),
            beforeGap: P.inSkippableInstrumental(gaps, null, g.gapStartTime - 1),
            inIntro: P.inSkippableInstrumental([], 5000, 4999),
            atIntroTarget: P.inSkippableInstrumental([], 5000, 5000)
        }
    };
})();

// ---- 4. quit and mid-play retry send no submission -------------------------------------
(function () {
    // Quit from the pause card.
    const q = mount(abcdOsu);
    advanceClockTo(q.api, 1200);
    keydown('a');
    keydown('Escape');
    const quitClicked = clickPause(q, 'tb-pause-quit');
    const afterQuit = live();
    advance(10000);
    const cardQuit = { finishes: q.log.finishes.length, exits: q.log.exits, ctx: q.ctx.state, listeners: afterQuit, clicked: quitClicked };

    // Quit by holding Ctrl+`: released early it aborts, held 200 ms it quits.
    const h = mount(abcdOsu);
    advanceClockTo(h.api, 1200);
    keydown('`', { ctrlKey: true });
    advance(100);
    keyup('`', { ctrlKey: true });
    advance(300);
    const abortedStillRunning = h.api.running;
    keydown('`', { ctrlKey: true });
    advance(150);
    const at150 = h.api.running;
    advance(100);
    const hotkeyQuit = { abortedStillRunning, runningAt150: at150, running: h.api.running, exits: h.log.exits, listeners: live() };
    advance(10000);
    hotkeyQuit.finishes = h.log.finishes.length;

    // Retry by holding ` mid-play: the old run is dropped unsubmitted and a new token is minted.
    const r = mount(abcdOsu);
    advanceClockTo(r.api, 1200);
    keydown('a');
    const firstEngine = r.api.engine;
    const typed = firstEngine.caretIndex;
    keydown('`');
    advance(250);
    const retried = r.api.engine !== firstEngine;
    const afterRetry = { playStarts: r.log.playStarts, finishes: r.log.finishes.length, listeners: live(), running: r.api.running, now: r.api.now() };
    // Retry from the pause card too.
    advanceClockTo(r.api, 1200);
    keydown('Escape');
    const cardRetryClicked = clickPause(r, 'tb-pause-retry');
    const afterCardRetry = { playStarts: r.log.playStarts, finishes: r.log.finishes.length, paused: r.api.paused, ctx: r.ctx.state, listeners: live() };
    // The fresh run plays out to its end and is the ONE run that concludes.
    advance(8000);
    out.discard = {
        cardQuit,
        hotkeyQuit,
        retry: { typedBefore: typed, retried, afterRetry, cardRetryClicked, afterCardRetry, finishesAtEnd: r.log.finishes.length },
        noExitHost: (function () {
            const n = mount(abcdOsu, false);
            advanceClockTo(n.api, 1200);
            keydown('Escape');
            clickPause(n, 'tb-pause-quit');
            advance(10000);
            const res = { finishes: n.log.finishes.length, running: n.api.running, startGate: !!findByClass(n.container, 'tb-btn-primary'), listeners: live() };
            n.api.destroy();
            return res;
        })()
    };
    r.api.destroy();
})();

// ---- 5. the playback-validity accumulator does not trip across pauses --------------------
(function () {
    const run = mount(gapOsu);
    const { api, log } = run;
    // Eight pauses, each held for 5 s of wall time, spaced past the cooldown. Eight is more than
    // the ALLOWED_PLAYBACK_DISCREPANCIES + 1 the veto tolerates, so a pause that cost even one
    // discrepancy each would trip it.
    for (let i = 0; i < 8; i++) {
        advance(1200);
        keydown('Escape');
        advance(5000);
        keydown('Escape');
    }
    const mid = api.playbackValidity;
    advance(40000);
    out.validity = {
        mid,
        final: api.playbackValidity,
        finishes: log.finishes,
        pausesTaken: run.ctx.suspendCalls
    };
    api.destroy();
})();

// ---- 6. the skip is refused while paused; beforeunload guards a running play -------------
(function () {
    const gapMap = TB.buildBeatmap(TB.parseLyricOsu(gapOsu));
    const g = D.computeGaps(gapMap.lines)[0];
    const run = mount(gapOsu);
    const { api, ctx } = run;
    // Line 0 typed out, so the gap's skip is live (a player still owing the line is not offered it).
    advanceClockTo(api, 1000);
    keydown('a');
    advanceClockTo(api, 1500);
    keydown('b');
    advanceClockTo(api, g.gapStartTime + 1000);
    keydown('Escape');
    const sourcesBefore = ctx.sourcesCreated;
    const clockBefore = api.now();
    const gapChip = findByClass(run.container, 'tb-gap');
    if (gapChip) gapChip.click();
    const skipWhilePaused = { sourcesCreated: ctx.sourcesCreated - sourcesBefore, clockMoved: api.now() !== clockBefore };
    keydown('Escape');
    advance(32);
    // The control: once resumed the same state skips. Under the manual newline the first Space on
    // the finished line is the newline, and the second is the skip.
    keydown(' ');
    advance(32);
    keydown(' ');
    const skipAfterResume = { sourcesCreated: ctx.sourcesCreated - sourcesBefore, clock: api.now() };

    const ev = { defaultPrevented: false, returnValue: undefined, preventDefault() { this.defaultPrevented = true; } };
    const handlers = winListeners.fire('beforeunload', ev);
    const guard = { handlers, prevented: ev.defaultPrevented, returnValue: ev.returnValue };
    api.destroy();
    out.skipAndUnload = { skipWhilePaused, skipAfterResume, skipTarget: g.skipTarget, guard, afterDestroy: winListeners.count('beforeunload') };
})();

// =============================================================================================
// THE END OF PLAY (backlog 314): the results delay, the fail wind-down and the results card's
// keys. The fakes below are widened in place (focus is recorded, and the audio graph's nodes and
// AudioParam automation are logged) so the scenarios above run exactly as they did.
// =============================================================================================

const E = TB.endOfPlay;

FakeElement.prototype.focus = function () { global.document.activeElement = this; };

function fakeParam(v) {
    return {
        value: v,
        events: [],
        setValueAtTime(x, t) { this.events.push({ op: 'set', value: x, at: t }); this.value = x; },
        linearRampToValueAtTime(x, t) { this.events.push({ op: 'linear', value: x, at: t }); },
        setValueCurveAtTime(c, t, d) {
            this.events.push({ op: 'curve', first: c[0], last: c[c.length - 1], points: c.length, at: t, duration: d });
        }
    };
}
function fakeNode(kind, extra) {
    return Object.assign({
        kind,
        targets: [],
        stops: 0,
        connect(n) { this.targets.push(n); return n; },
        disconnect() { this.targets = []; }
    }, extra || {});
}
FakeAudioContext.prototype.createBufferSource = function () {
    this.sourcesCreated++;
    const n = fakeNode('source', { buffer: null, playbackRate: fakeParam(1), start() {}, stop() { this.stops++; } });
    (this.nodes = this.nodes || []).push(n);
    return n;
};
FakeAudioContext.prototype.createGain = function () {
    const n = fakeNode('gain', { gain: fakeParam(1) });
    (this.nodes = this.nodes || []).push(n);
    return n;
};
FakeAudioContext.prototype.createBiquadFilter = function () {
    const n = fakeNode('biquad', { type: 'lowpass', frequency: fakeParam(350) });
    (this.nodes = this.nodes || []).push(n);
    return n;
};

function mountFull(osu, withExit) {
    global.document.hidden = false;
    global.document.activeElement = null;
    timers.clear();
    rafQueue = new Map();
    const log = { finishes: [], finishWalls: [], playStarts: 0, exits: 0 };
    const container = new FakeElement('div');
    const opts = {
        osuText: osu,
        audioArrayBuffer: new ArrayBuffer(8),
        title: 't',
        artist: 'a',
        onPlayStart() { log.playStarts++; },
        onFinish(results) { log.finishes.push(JSON.stringify(results)); log.finishWalls.push(wall); }
    };
    if (withExit !== false) opts.onExit = function () { log.exits++; };
    const api = TB.mountPlayer(container, opts);
    const ctx = lastCtx;
    keydown(' ');
    return { api, ctx, log, container };
}

// Advance frame by frame until the engine ends; returns the wall time and the score read off the
// engine at that very frame (the tick that ended it has already concluded the run).
function playToEnd(run, limitMs) {
    for (let t = 0; t < limitMs; t += 16) {
        advance(16);
        const e = run.api.engine;
        if (e.finished || e.failed) return { wall, results: TB.computeScore(e) };
    }
    return null;
}

// Script "ab cd" through to a clear: each key on its target.
function typeAbcd(run) {
    const plan = [[1000, 'a'], [1500, 'b'], [2000, ' '], [2000, 'c'], [2500, 'd']];
    for (const [at, k] of plan) { advanceClockTo(run.api, at); keydown(k); }
}

function sansValidity(json) { const o = JSON.parse(json); delete o.playbackValid; return JSON.stringify(o); }

function rootOf(run) { return findByClass(run.container, 'tb-player'); }

// ---- 7. a completed run: the score is taken at the end, the card follows 1000 ms later ---------
(function () {
    const run = mountFull(abcdOsu);
    typeAbcd(run);
    const end = playToEnd(run, 20000);
    const atEnd = JSON.stringify(end.results);
    const cardAtEnd = !!findByClass(run.container, 'tb-results');
    const finishesAtEnd = run.log.finishes.length;
    const concludedClass = rootOf(run).classList.contains('tb-concluded');
    const listenersAtEnd = live();

    // Every key pressed during the wait is swallowed, and none of them moves the engine.
    const presses = ['a', 'b', ' ', 'Backspace', 'Enter', '`'].map(k => keydown(k));
    const swallowed = presses.every(ev => ev.defaultPrevented);
    const chord = keydown('r', { ctrlKey: true });
    const afterKeys = JSON.stringify(TB.computeScore(run.api.engine));

    // 600 ms into the wait is past the (PR 13) 500 ms deadline, so the card and the submit are up.
    advance(600);
    const cardAt600 = !!findByClass(run.container, 'tb-results');
    const finishesAt600 = run.log.finishes.length;
    advance(400);
    const delayed = run.log.finishes[0];
    const delayMs = run.log.finishWalls[0] - end.wall;
    const focused = global.document.activeElement;
    out.resultsDelay = {
        cardAtEnd,
        finishesAtEnd,
        concludedClass,
        listenersAtEnd,
        swallowed,
        chordLeftToBrowser: !chord.defaultPrevented,
        keysMovedEngine: afterKeys !== atEnd,
        cardAt600,
        finishesAt600,
        delayMs,
        finishes: run.log.finishes.length,
        passed: JSON.parse(delayed).passed,
        identical: sansValidity(delayed) === atEnd,
        playbackValid: JSON.parse(delayed).playbackValid,
        focusedClass: focused ? focused.className : null,
        cardListeners: live()
    };
    run.api.destroy();

    // The same run with Escape pressed 100 ms into the wait: the card is up at once, with the same
    // results, and the cancelled timer never delivers a second one.
    const esc = mountFull(abcdOsu);
    typeAbcd(esc);
    const escEnd = playToEnd(esc, 20000);
    advance(100);
    const escEv = keydown('Escape');
    const escCard = !!findByClass(esc.container, 'tb-results');
    const escFinishes = esc.log.finishes.length;
    const escDelay = esc.log.finishWalls[0] - escEnd.wall;
    advance(3000);
    out.resultsEscape = {
        prevented: escEv.defaultPrevented,
        card: escCard,
        finishesAtEscape: escFinishes,
        delayMs: escDelay,
        finishesLater: esc.log.finishes.length,
        sameAsDelayed: esc.log.finishes[0] === delayed
    };
    esc.api.destroy();
})();

// ---- 8. a failed run: the engine stays frozen, the track winds down, the card follows ----------
// The health harness's AFK shape: an idle player empties the bar on the second seal.
const afkOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":22000}\n' +
    [['the quick brown fox jumps over', 1000], ['the lazy dog sleeps all day', 9000], ['then wakes up', 17000]]
        .map(function (pair) {
            const words = pair[0].split(' ');
            return JSON.stringify({
                text: pair[0], start_ms: pair[1], end_ms: pair[1] + words.length * 1000,
                words: words.map((w, i) => ({ text: w, start_ms: pair[1] + i * 1000, end_ms: pair[1] + (i + 1) * 1000, score: 1 }))
            });
        }).join('\n') + '\n';

(function () {
    const run = mountFull(afkOsu);
    const { ctx } = run;
    const end = playToEnd(run, 60000);
    const failed = !!run.api.engine.failed;
    const atFail = JSON.stringify(end.results);
    const failT = ctx.currentTime;

    const source = ctx.nodes.filter(n => n.kind === 'source').pop();
    const gains = ctx.nodes.filter(n => n.kind === 'gain');
    const gain = gains[gains.length - 1];
    const filters = ctx.nodes.filter(n => n.kind === 'biquad');
    const lowPass = filters.find(f => f.type === 'lowpass');
    const highPass = filters.find(f => f.type === 'highpass');
    const rel = ev => Object.assign({}, ev, { at: ev.at - failT });
    const graph = {
        sourceTo: source.targets.map(n => n === lowPass ? 'lowpass' : n.kind),
        lowPassTo: lowPass ? lowPass.targets.map(n => n === highPass ? 'highpass' : n.kind) : [],
        highPassTo: highPass ? highPass.targets.map(n => n === gain ? 'gain' : n.kind) : []
    };
    const audio = {
        filters: filters.length,
        rate: source.playbackRate.events.map(rel),
        lowPass: lowPass ? lowPass.frequency.events.map(rel) : [],
        highPass: highPass ? highPass.frequency.events.map(rel) : [],
        gain: gain.gain.events.map(rel),
        // The only sources ever made are the track's: no fail sample is played.
        sources: ctx.sourcesCreated,
        stoppedAtFail: source.stops
    };

    // Mid wind-down: no card, nothing submitted, the statistics are the ones taken at the fail.
    advance(1500);
    const mid = {
        card: !!findByClass(run.container, 'tb-results'),
        finishes: run.log.finishes.length,
        stats: JSON.stringify(TB.computeScore(run.api.engine)) === atFail,
        failingClass: rootOf(run).classList.contains('tb-failing'),
        swallowed: keydown('a').defaultPrevented
    };
    advance(1100);
    const delivered = run.log.finishes[0];
    const delayMs = run.log.finishWalls[0] - end.wall;
    const stoppedAtCard = source.stops;
    advance(10000);
    out.failWindDown = {
        failed,
        graph,
        audio,
        mid,
        delayMs,
        finishes: run.log.finishes.length,
        identical: delivered !== undefined && sansValidity(delivered) === atFail,
        passed: delivered !== undefined && JSON.parse(delivered).passed,
        stoppedAtCard,
        curveMid: E.outCubicCurve(E.LOWPASS_OPEN_HZ, E.FAIL_FILTER_CUTOFF_HZ, 3)[1]
    };
    run.api.destroy();

    // Escape during the wind-down finishes it early, with the same statistics, once.
    const esc = mountFull(afkOsu);
    const escEnd = playToEnd(esc, 60000);
    advance(500);
    keydown('Escape');
    const early = { finishes: esc.log.finishes.length, delayMs: esc.log.finishWalls[0] - escEnd.wall };
    advance(5000);
    early.finishesLater = esc.log.finishes.length;
    early.identical = esc.log.finishes[0] !== undefined && sansValidity(esc.log.finishes[0]) === JSON.stringify(escEnd.results);
    out.failEscape = early;
    esc.api.destroy();
})();

// ---- 9. the results card's keys -------------------------------------------------------------
(function () {
    function toCard(withExit) {
        const run = mountFull(abcdOsu, withExit);
        typeAbcd(run);
        playToEnd(run, 20000);
        keydown('Escape');
        return run;
    }

    // Hold ` retries straight into a new play: no start gate, a fresh token, the card's keys gone.
    const r = toCard();
    const onCard = live();
    const firstEngine = r.api.engine;
    keydown('`');
    advance(100);
    keyup('`');
    advance(300);
    const abortedOnCard = { running: r.api.running, playStarts: r.log.playStarts };
    keydown('`');
    advance(150);
    const at150 = r.api.running;
    advance(100);
    const overlay = overlayOf(r);
    out.cardRetry = {
        onCard,
        abortedOnCard,
        runningAt150: at150,
        running: r.api.running,
        freshEngine: r.api.engine !== firstEngine,
        playStarts: r.log.playStarts,
        overlayOn: overlay.classList.contains('tb-overlay-on'),
        concludedClass: rootOf(r).classList.contains('tb-concluded'),
        listeners: live(),
        finishes: r.log.finishes.length
    };
    // The retried play runs and concludes on its own, once more (idle, then the full wait).
    advance(12000);
    out.cardRetry.finishesAfterSecondPlay = r.log.finishes.length;
    // Clicking 'play again' goes straight in too.
    const again = findByClass(r.container, 'tb-result-again');
    if (again) again.click();
    out.cardRetry.clickRunning = r.api.running;
    out.cardRetry.clickPlayStarts = r.log.playStarts;
    r.api.destroy();

    // Escape goes back to the host, and so does Ctrl+` (on press, no hold).
    const b = toCard();
    keydown('Escape');
    const escBack = { exits: b.log.exits, ctx: b.ctx.state, listeners: live() };
    const c = toCard();
    keydown('`', { ctrlKey: true });
    const ctrlBack = { exits: c.log.exits, ctx: c.ctx.state, listeners: live() };
    // Without a host, back is the player's own start gate.
    const n = toCard(false);
    keydown('Escape');
    const noHost = { exits: n.log.exits, startGate: !!findByClass(n.container, 'tb-btn-primary') && !findByClass(n.container, 'tb-results'), listeners: live() };
    n.api.destroy();
    // A card torn down by its host leaves nothing behind.
    const d = toCard();
    d.api.destroy();
    out.cardBack = { escBack, ctrlBack, noHost, afterDestroy: live() };
})();

out.endOfPlayConstants = {
    resultsDelayMs: E.RESULTS_DISPLAY_DELAY_MS,
    linesFadeMs: E.LINES_FADE_OUT_MS,
    windDownMs: E.FAIL_WIND_DOWN_MS,
    cutoffHz: E.FAIL_FILTER_CUTOFF_HZ,
    volume: E.FAIL_VOLUME,
    lowPassOpenHz: E.LOWPASS_OPEN_HZ
};

process.stdout.write(JSON.stringify(out));
