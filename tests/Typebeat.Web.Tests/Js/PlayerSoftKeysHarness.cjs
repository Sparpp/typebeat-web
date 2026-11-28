// Node harness for /play's SOFT-KEYBOARD input path (backlog 501). It MOUNTS the real player
// (typebeat-player.js's mountPlayer) over a minimal fake DOM, a fake AudioContext and fake timers,
// as PlayerPauseHarness.cjs does, and drives the hidden text field the player owns on a touch
// device: plain input events, an IME's 229 keydown followed by its input, a multi-character
// predictive insert, a backspace on the sentinel, an open composition, a paste, and a physical
// keydown whose text also reaches the field (which must be typed once, not twice). Every engine call
// the router makes is logged with its time, beside the quantised clock it should carry.
//
// It also reports the desktop case: with no coarse pointer and no touch, no field is ever made.
//
// Emits { scenario: observations } as JSON for PlayerSoftKeysTest.
//
// Usage: node PlayerSoftKeysHarness.cjs <absolute path to typebeat-core.js>

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
        this.value = '';
        this.selection = null;
        this.focusCalls = 0;
        this.attributes = {};
        this.style = { setProperty() {}, removeProperty() {} };
        this.offsetWidth = 0;
        this.offsetHeight = 0;
        this.offsetTop = 0;
        this.clientWidth = 0;
        this.rect = { left: 0, right: 0, top: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 };
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
    getBoundingClientRect() { return this.rect; }
    addEventListener(type, fn) { this._listeners.add(type, fn); }
    removeEventListener(type, fn) { this._listeners.remove(type, fn); }
    click() { this._listeners.fire('click', { preventDefault() {} }); }
    focus() {
        this.focusCalls++;
        const was = global.document.activeElement;
        if (was === this) return;
        if (was && was.blur) was.blur();
        global.document.activeElement = this;
    }
    blur() {
        if (global.document.activeElement !== this) return;
        global.document.activeElement = null;
        this._listeners.fire('blur', {});
    }
    setSelectionRange(a, b) { this.selection = [a, b]; }
    setAttribute(k, v) { this.attributes[k] = String(v); }
    getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attributes, k) ? this.attributes[k] : null; }
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
    activeElement: null,
    createElement(tag) { return new FakeElement(tag); },
    createDocumentFragment() { const f = new FakeElement('#fragment'); f._fragment = true; return f; },
    addEventListener(type, fn) { docListeners.add(type, fn); },
    removeEventListener(type, fn) { docListeners.remove(type, fn); },
    getElementById() { return null; },
    hasFocus() { return true; }
};

// ---- fake clocks: wall time, timers, rAF -------------------------------------

let wall = 1000;
let nextTimerId = 1;
const timers = new Map();
let rafQueue = new Map();

const scrolls = [];
global.window = {
    addEventListener(type, fn) { winListeners.add(type, fn); },
    removeEventListener(type, fn) { winListeners.remove(type, fn); },
    scrollBy(x, y) { scrolls.push([x, y]); }
};

require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const TB = global.window.TypeBeatCore;
const D = TB.display;
const S = D.SOFT_INPUT_SENTINEL;

Object.defineProperty(global, 'performance', { value: { now: () => wall }, configurable: true, writable: true });
global.setTimeout = function (fn, ms) { const id = nextTimerId++; timers.set(id, { due: wall + (ms || 0), fn, every: 0 }); return id; };
global.clearTimeout = function (id) { timers.delete(id); };
global.setInterval = function (fn, ms) { const id = nextTimerId++; timers.set(id, { due: wall + ms, fn, every: ms }); return id; };
global.clearInterval = function (id) { timers.delete(id); };
global.requestAnimationFrame = function (fn) { const id = nextTimerId++; rafQueue.set(id, fn); return id; };
global.cancelAnimationFrame = function (id) { rafQueue.delete(id); };

// ---- fake AudioContext --------------------------------------------------------

let lastCtx = null;

class FakeAudioContext {
    constructor() {
        this.state = 'running';
        this.currentTime = 0;
        this.destination = {};
        this._listeners = makeListeners();
        lastCtx = this;
    }
    _set(state) { if (this.state === state) return; this.state = state; this._listeners.fire('statechange', {}); }
    suspend() { this._set('suspended'); return Promise.resolve(); }
    resume() { this._set('running'); return Promise.resolve(); }
    close() { this._set('closed'); return Promise.resolve(); }
    addEventListener(type, fn) { this._listeners.add(type, fn); }
    removeEventListener(type, fn) { this._listeners.remove(type, fn); }
    createBufferSource() { return { buffer: null, connect() {}, disconnect() {}, start() {}, stop() {} }; }
    createGain() { return { gain: { value: 1 }, connect() {} }; }
    decodeAudioData(bytes, ok) { ok({ duration: 60, numberOfChannels: 1, getChannelData() { return new Float32Array(1); } }); }
}
global.window.AudioContext = FakeAudioContext;
// The hold display measures a glyph through it; the fake DOM has no layout, so it reads the default.
global.getComputedStyle = function () { return { fontSize: '42px' }; };

// 16 ms frames; the audio clock moves only while 'running'. A fractional step keeps the clock off
// whole milliseconds, so the quantisation is actually exercised.
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

function advanceClockTo(api, ms) {
    let guard = 0;
    while (api.now() < ms && guard++ < 100000) advance(Math.min(16, Math.max(0.37, ms - api.now())));
}

function key(k, extra) {
    const ev = Object.assign({ key: k, code: '', keyCode: 0, repeat: false, isComposing: false,
        ctrlKey: false, altKey: false, metaKey: false, shiftKey: false,
        defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }, extra || {});
    if (ev.code === undefined || ev.code === null) ev.code = '';
    return ev;
}

// ---- maps ----------------------------------------------------------------------

const OSU_HEADER = '[General]\nAudioFilename: a.mp3\n[Metadata]\nTitle: t\nArtist: a\n[Lyrics]\n';

// "ab cd", sung [1000, 3000]. Cell targets: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
const abcdOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":4000}\n' +
    '{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[' +
    '{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},' +
    '{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}\n';

// "abcdef gh" for the longer scenarios.
const longOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":8000}\n' +
    '{"text":"abcdef gh","start_ms":1000,"end_ms":5000,"words":[' +
    '{"text":"abcdef","start_ms":1000,"end_ms":4000,"score":1},' +
    '{"text":"gh","start_ms":4000,"end_ms":5000,"score":1}]}\n';

// ---- mounting ----------------------------------------------------------------

function setCoarse(on) {
    if (on) global.window.matchMedia = function (q) { return { matches: q === '(pointer: coarse)' }; };
    else delete global.window.matchMedia;
}

function mount(osu, startBy) {
    global.document.hidden = false;
    global.document.activeElement = null;
    timers.clear();
    rafQueue = new Map();
    const log = { finishes: 0 };
    const container = new FakeElement('div');
    const api = TB.mountPlayer(container, {
        osuText: osu,
        audioArrayBuffer: new ArrayBuffer(8),
        title: 't',
        artist: 'a',
        onFinish() { log.finishes++; }
    });
    const root = container.children[0];
    if (startBy === 'touch') root._listeners.fire('pointerdown', { pointerType: 'touch' });
    if (startBy === 'touch' || startBy === 'click') {
        const btn = findByClass(container, 'tb-btn-primary');
        btn.click();
    } else {
        docListeners.fire('keydown', key(' ', { code: 'Space' }));
    }
    const calls = spy(api.engine);
    return { api, container, root, calls, log };
}

// Every engine call the router makes, with every argument forwarded.
function spy(engine) {
    const calls = [];
    for (const name of ['processKey', 'processBackspace', 'processEnter', 'processRelease']) {
        const fn = engine[name].bind(engine);
        engine[name] = function () {
            const r = fn.apply(null, arguments);
            const entry = { fn: name, result: r };
            if (name === 'processKey') { entry.c = arguments[0]; entry.t = arguments[1]; }
            else if (name !== 'processBackspace') entry.t = arguments[0];
            calls.push(entry);
            return r;
        };
    }
    return calls;
}

function fieldOf(run) { return findByClass(run.container, 'tb-soft-input'); }

// A physical (or IME) keydown as the browser delivers it: the document's capture listener first,
// then the focused field.
function keydown(run, ev) {
    docListeners.fire('keydown', ev);
    const f = fieldOf(run);
    if (f) f._listeners.fire('keydown', ev);
    return ev;
}

function keyup(run, ev) {
    docListeners.fire('keyup', ev);
    const f = fieldOf(run);
    if (f) f._listeners.fire('keyup', ev);
}

// The field's value becoming `value` through one edit of `inputType`: beforeinput (which may cancel
// it), then the value change and the input event.
function edit(run, value, inputType, composing) {
    const f = fieldOf(run);
    const before = { inputType: inputType, cancelable: !composing, isComposing: !!composing, defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; } };
    f._listeners.fire('beforeinput', before);
    if (before.defaultPrevented) return { cancelled: true };
    f.value = value;
    f._listeners.fire('input', { inputType: inputType, isComposing: !!composing });
    return { cancelled: false };
}

function expectedTime(api) { return D.roundHalfEven(api.now()); }

const out = {};

// ---- pure: the diff, the IME test ------------------------------------------------

out.diff = {
    plain: D.softKeysFor(S, S + 'a'),
    predictive: D.softKeysFor(S, S + 'hello '),
    backspaceOnSentinel: D.softKeysFor(S, ''),
    autocorrect: D.softKeysFor(S + 'teh', S + 'the '),
    composingStep: D.softKeysFor(S + 'he', S + 'hel'),
    enter: D.softKeysFor(S, S + '\r\n'),
    emoji: D.softKeysFor(S + '\u{1F600}', S + '\u{1F603}'),
    unchanged: D.softKeysFor(S + 'ab', S + 'ab')
};

out.ime = {
    keyCode229: D.isImeKeyDown(key('a', { keyCode: 229 })),
    unidentified: D.isImeKeyDown(key('Unidentified')),
    process: D.isImeKeyDown(key('Process', { code: 'KeyA' })),
    composing: D.isImeKeyDown(key('a', { isComposing: true })),
    noKey: D.isImeKeyDown(key('')),
    letter: D.isImeKeyDown(key('a', { code: 'KeyA', keyCode: 65 })),
    backspace: D.isImeKeyDown(key('Backspace', { code: 'Backspace', keyCode: 8 })),
    enter: D.isImeKeyDown(key('Enter', { code: 'Enter', keyCode: 13 }))
};

// ---- pure: a soft key's hold stays down until the next key press -----------------------------
// A stub engine that opens a hold on every 'o', driven through routeSoftKeys and the physical
// router at a clock the host sets, so each call's quantised instant is visible.
(function () {
    const calls = [];
    const engine = {
        activeHold: null,
        fletcherEnabled: false, manualNewlines: false, activeLineIndex: 0, allowWrongInput: true,
        isLineComplete() { return false; },
        update(t) { calls.push({ fn: 'update', t: t }); },
        processKey(c, t) { calls.push({ fn: 'key', c: c, t: t }); if (c === 'o') this.activeHold = { pressTime: t }; return true; },
        processRelease(t) { calls.push({ fn: 'release', t: t }); this.activeHold = null; return true; },
        processBackspace() { calls.push({ fn: 'backspace' }); return true; },
        processEnter(t) { calls.push({ fn: 'enter', t: t }); return true; }
    };
    const host = {
        engine: engine,
        clock: 0,
        now() { return host.clock; },
        gaps: [], introTarget: null,
        getSelection() { return null; }, setSelection() {}, performSkip() {}
    };
    const step = (t, fn) => {
        host.clock = t;
        const before = calls.filter(c => c.fn !== 'update').length;
        fn();
        return {
            calls: calls.filter(c => c.fn !== 'update').slice(before),
            heldKey: host.holdState ? host.holdState.key : null,
            holdOpen: engine.activeHold !== null
        };
    };
    out.softHold = {
        softHoldKey: D.SOFT_HOLD_KEY,
        // The hold-opening key comes last: nothing lets it go.
        opened: step(1300.5, () => D.routeSoftKeys(['l', 'o'], host)),
        // The SAME letter again is still the next press: it ends the hold first, at its own instant.
        sameLetter: step(1450.5, () => D.routeSoftKeys(['o'], host)),
        // Two keys in one change: the 'v' ends the hold already down, at the change's instant, and
        // the hold the 'o' then opens stays down.
        inOneChange: step(1600.5, () => D.routeSoftKeys(['v', 'o'], host)),
        // A physical key's key-up never ends a soft hold, even the key that types the same letter.
        physicalKeyUp: step(1700.5, () => D.routeKeyUp(key('o', { code: 'KeyO' }), host)),
        // A physical key going down does, at its instant.
        physicalDown: step(1750.5, () => D.routeKeyDown(key('v', { code: 'KeyV' }), host))
    };
})();

// ---- 1. the desktop: no coarse pointer and no touch, so no field, and the keydown path as before --
(function () {
    setCoarse(false);
    const run = mount(abcdOsu, 'key');
    advanceClockTo(run.api, 1000);
    const ev = keydown(run, key('a', { code: 'KeyA', keyCode: 65 }));
    run.root._listeners.fire('click', {});
    out.desktop = {
        field: !!fieldOf(run),
        prevented: ev.defaultPrevented,
        calls: run.calls.map(c => c.fn + ':' + (c.c || '')),
        caret: run.api.engine.caretIndex,
        rootListeners: ['pointerdown', 'touchstart', 'click'].map(t => run.root._listeners.count(t))
    };
    run.api.destroy();
})();

// ---- 2. a coarse pointer: the field is made, focused and set up at the start of play --------
(function () {
    setCoarse(true);
    const run = mount(abcdOsu, 'click');
    const f = fieldOf(run);
    out.setup = {
        field: !!f,
        tag: f ? f.tagName : null,
        focused: global.document.activeElement === f,
        value: f ? f.value : null,
        selection: f ? f.selection : null,
        attributes: f ? f.attributes : null,
        type: f ? f.type : null
    };

    // Plain soft keys: input events with no keydown at all.
    advanceClockTo(run.api, 1000.3);
    const tA = expectedTime(run.api);
    edit(run, S + 'a', 'insertText');
    const valueAfterA = f.value;

    // An IME's 229 keydown, then its input.
    advanceClockTo(run.api, 1500.6);
    const tB = expectedTime(run.api);
    const imeDown = keydown(run, key('Unidentified', { keyCode: 229 }));
    edit(run, S + 'b', 'insertText');
    keyup(run, key('Unidentified', { keyCode: 229 }));

    // A spacebar reported under an IME with code Space: the keydown path types it (and prevents it),
    // so the space the IME then commits into the field must not be typed again.
    advanceClockTo(run.api, 2000.2);
    const tSpace = expectedTime(run.api);
    const spaceDown = keydown(run, key('Unidentified', { keyCode: 229, code: 'Space' }));
    edit(run, S + ' ', 'insertText', true);
    keyup(run, key('Unidentified', { keyCode: 229, code: 'Space' }));

    // A predictive insert of two letters at once: one press each, at the one instant.
    advanceClockTo(run.api, 2100.4);
    const tCd = expectedTime(run.api);
    edit(run, S + 'cd', 'insertText');

    out.soft = {
        tA, tB, tSpace, tCd,
        valueAfterA,
        imeDownPrevented: imeDown.defaultPrevented,
        spaceDownPrevented: spaceDown.defaultPrevented,
        calls: run.calls,
        caret: run.api.engine.caretIndex,
        valueAtEnd: f.value
    };
    run.api.destroy();
})();

// ---- 3. backspace on the sentinel, the physical-key pair, composition, paste, pause ------------
(function () {
    setCoarse(true);
    const run = mount(longOsu, 'click');
    const f = fieldOf(run);

    advanceClockTo(run.api, 1000.3);
    edit(run, S + 'a', 'insertText');
    edit(run, S + 'x', 'insertText'); // a typo on 'b'
    const caretAfterTypo = run.api.engine.caretIndex;

    // Backspace with nothing typed since the reset deletes the sentinel itself.
    const n0 = run.calls.length;
    edit(run, '', 'deleteContentBackward');
    const backspace = { calls: run.calls.slice(n0), caret: run.api.engine.caretIndex, value: f.value, selection: f.selection };

    // A physical key on a phone (or a desktop browser that delivers the key's text into the focused
    // field anyway): routed once by the keydown path, its input swallowed. A beforeinput that can be
    // cancelled is cancelled; one that cannot is read and dropped.
    advanceClockTo(run.api, 1500.2);
    const n1 = run.calls.length;
    const tPhysB = expectedTime(run.api);
    const physDown = keydown(run, key('b', { code: 'KeyB', keyCode: 66 }));
    const cancellable = edit(run, S + 'b', 'insertText');
    const valueAfterCancelled = f.value;
    // The same key again (an OS repeat the keydown path ignores), whose text the field DOES receive.
    const repeatDown = keydown(run, key('b', { code: 'KeyB', keyCode: 66, repeat: true }));
    edit(run, S + 'b', 'insertText', true);
    const valueAfterRepeat = f.value;
    keyup(run, key('b', { code: 'KeyB', keyCode: 66 }));
    const physical = {
        tPhysB,
        prevented: physDown.defaultPrevented,
        repeatPrevented: repeatDown.defaultPrevented,
        cancelled: cancellable.cancelled,
        valueAfterCancelled,
        valueAfterRepeat,
        calls: run.calls.slice(n1)
    };

    // After the key-up the field reads input again.
    const n2 = run.calls.length;
    edit(run, S + 'c', 'insertText');
    const afterKeyUp = run.calls.slice(n2);

    // An open composition: each step types its new letter, and the field is left alone under it.
    advanceClockTo(run.api, 2000.7);
    const n3 = run.calls.length;
    f._listeners.fire('compositionstart', {});
    edit(run, S + 'd', 'insertCompositionText', true);
    const valueMidComposition = f.value;
    edit(run, S + 'de', 'insertCompositionText', true);
    f._listeners.fire('compositionend', {});
    const valueAtCompositionEnd = f.value;
    advance(1); // the deferred read
    const composition = { calls: run.calls.slice(n3), valueMidComposition, valueAtCompositionEnd, valueAfter: f.value };

    // A paste is never typing, whether its beforeinput is cancelled or not.
    const n4 = run.calls.length;
    const paste = edit(run, S + 'fgh', 'insertFromPaste');
    const pasteValue = f.value;
    f.value = S + 'fgh';
    f._listeners.fire('input', { inputType: 'insertFromPaste' });
    const pasted = { cancelled: paste.cancelled, valueAfterCancel: pasteValue, calls: run.calls.slice(n4), value: f.value };

    // Paused: a soft key does nothing, and resuming refocuses the field.
    const n5 = run.calls.length;
    docListeners.fire('keydown', key('Escape', { code: 'Escape' }));
    const pausedFocus = global.document.activeElement === f;
    edit(run, S + 'f', 'insertText');
    const whilePaused = run.calls.slice(n5);
    const focusBeforeResume = f.focusCalls;
    findByClass(run.container, 'tb-pause-resume').click();
    const pause = { whilePaused, pausedFocus, refocused: f.focusCalls > focusBeforeResume && global.document.activeElement === f, value: f.value };

    out.edits = { caretAfterTypo, backspace, physical, afterKeyUp, composition, pasted, pause };
    run.api.destroy();
    out.edits.fieldRemovedOnDestroy = !fieldOf(run);
})();

// ---- 4. a touch seen on the player turns it on without a coarse pointer; blur and tap refocus ----
(function () {
    setCoarse(false);
    const run = mount(abcdOsu, 'touch');
    const f = fieldOf(run);
    const made = !!f;
    const focused = global.document.activeElement === f;

    // Something else takes focus during play: the field takes it back on the next task.
    const other = new FakeElement('button');
    other.focus();
    const stolen = global.document.activeElement === other;
    advance(1);
    const refocused = global.document.activeElement === f;

    // A tap on the stage reopens the keyboard: blur, then focus again.
    const before = f.focusCalls;
    run.root._listeners.fire('click', {});
    const tapRefocus = f.focusCalls === before + 1 && global.document.activeElement === f;

    // The visual viewport shrinks under the keyboard: the current row is scrolled back into view.
    const row = findByClass(run.container, 'tb-line-cur');
    row.rect = { top: 500, bottom: 560, left: 0, right: 100, width: 100, height: 60 };
    const vvListeners = makeListeners();
    global.window.visualViewport = { offsetTop: 0, height: 300,
        addEventListener(t, fn) { vvListeners.add(t, fn); }, removeEventListener(t, fn) { vvListeners.remove(t, fn); } };
    // The listener is attached when soft input starts; retry from the pause card restarts it.
    docListeners.fire('keydown', key('Escape', { code: 'Escape' }));
    findByClass(run.container, 'tb-pause-resume').click();
    scrolls.length = 0;
    vvListeners.fire('resize', {});
    advance(16);
    const scrolled = scrolls.slice();

    run.api.destroy();
    out.touch = {
        made, focused, stolen, refocused, tapRefocus, scrolled,
        viewportListenersAfterDestroy: vvListeners.count('resize') + vvListeners.count('scroll')
    };
    delete global.window.visualViewport;
})();

// ---- 5. soft holds on a real map, through the mounted player ------------------------------------
// "so long" (PlayerDisplayHarness.cjs's hold map): the 's' of "so" held [1000, 1500], handing off to
// its 'o', due at 1500. Each run types the 's' as a soft key at ~1000, then ends the hold its own way.
const holdOsu = OSU_HEADER +
    '{"granularity":"word","version":2,"song_end_ms":5000}\n' +
    '{"text":"so long","start_ms":1000,"end_ms":3000,"words":[' +
    '{"text":"so","start_ms":1000,"end_ms":1800,"score":1,"holds":[{"char":0,"start_ms":1000,"end_ms":1500,"ticks":[1250]}]},' +
    '{"text":"long","start_ms":2000,"end_ms":3000,"score":1,"holds":[{"char":3,"start_ms":2600,"end_ms":3000}]}]}\n';

function holdRun(then) {
    setCoarse(true);
    const run = mount(holdOsu, 'click');
    const tails = [];
    const onTail = run.api.engine.onHoldTailJudged;
    run.api.engine.onHoldTailJudged = function (j) { tails.push({ type: j.type, releaseTime: j.releaseTime }); return onTail.apply(this, arguments); };
    advanceClockTo(run.api, 1000.3);
    const tPress = expectedTime(run.api);
    edit(run, S + 's', 'insertText');
    const heldAfterPress = run.api.engine.activeHold !== null;
    advanceClockTo(run.api, 1200.4);
    const tEnd = expectedTime(run.api);
    const extra = then(run) || {};
    const result = Object.assign({
        tPress, tEnd, heldAfterPress,
        calls: run.calls,
        tails,
        holdOpenAfter: run.api.engine.activeHold !== null
    }, extra);
    run.api.destroy();
    return result;
}

out.holds = {
    // No further press: the hold completes as a Great at its own end, with no release call.
    completesAtEnd: holdRun(function (run) { advanceClockTo(run.api, 1600); }),
    // The next soft key (the hand-off 'o') ends it first, at that press's instant.
    nextSoftKey: holdRun(function (run) { edit(run, S + 'o', 'insertText'); }),
    // An IME's 229 keydown is a key press too: it ends the hold at the keydown's instant.
    imeKeydown: holdRun(function (run) { keydown(run, key('Unidentified', { keyCode: 229 })); }),
    // Pausing lets it go at the pause instant, and the resumed run has nothing left to release.
    pause: holdRun(function (run) {
        docListeners.fire('keydown', key('Escape', { code: 'Escape' }));
        const pausedWithHold = run.api.engine.activeHold !== null;
        findByClass(run.container, 'tb-pause-resume').click();
        advanceClockTo(run.api, 1600);
        return { pausedWithHold, paused: run.api.paused };
    }),
    // The field losing focus during play (another element taking it) lets it go at that instant.
    fieldBlur: holdRun(function () { new FakeElement('button').focus(); })
};

process.stdout.write(JSON.stringify(out));
