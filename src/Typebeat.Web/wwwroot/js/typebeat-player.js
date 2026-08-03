/*
 * typebeat-player.js: the browser renderer + Web Audio driver on top of
 * typebeat-core.js. Exposes TypeBeatCore.mountPlayer(container, opts).
 *
 * opts:
 *   osuText            : string   (the .osu lyric text)
 *   audioArrayBuffer   : ArrayBuffer (raw encoded audio bytes; decoded here)
 *   title, artist      : strings  (for the results header; falls back to the map)
 *   onFinish(results, api) : called once when a play ends (pass or fail)
 *   onExit()           : called when the player wants to leave (back button)
 *
 * Timing is driven by the Web AudioContext clock (sample-accurate); gameplay
 * time = (ctx.currentTime - startedAt) * 1000, matching the desktop's
 * audio-sourced gameplay clock with LyricOffset = 0.
 *
 * PRESENTATION PARITY. Everything below the "display" banner is a port of the desktop
 * client's gameplay presentation (typebeat.Game.Rulesets.TypeBeat.UI: LyricStage, Caret,
 * LyricLineDisplay, TypeBeatHudOverlay) into HTML/CSS/JS: the damped+blinking player caret,
 * the sung sweep and sung caret driven by the line's per-char target times, the two depleting
 * cue-in bars, the 3-line dim ladder and the eased scroll on line change, the rolling WPM /
 * sync readouts, and the per-cell judgement feedback.
 *
 * It is DISPLAY ONLY. typebeat-core.js (the hand-written mirror of the C# TypingEngine and
 * scorer) is not touched by any of it: nothing here calls back into judgement, and every
 * derived value is computed here from state core already exposes. Browser scores land on the
 * same leaderboards as desktop, so the engine must stay byte-compatible.
 *
 * Everything animated is clocked off the SAME audio clock the engine judges against
 * (nowMs()), never wall time, so the visuals can never drift from judgement. The only
 * exceptions are the wrong-key letter pop and the health/progress bar CSS transitions, which
 * are pure decoration with no gameplay reading.
 */
(function (Core) {
    'use strict';
    if (!Core) return;

    const KEY_RE = /^[a-zA-Z0-9]$/;

    // ---------------------------------------------------------------------------
    // Display constants, ported from the desktop client.
    // ---------------------------------------------------------------------------
    // LyricStage.approach_lead_ms is TypingEngine.CUE_LEAD_MS; core exposes the same value, so
    // the cue bar can never disagree with the window the engine actually activates a line in.
    const CUE_LEAD_MS = (Core.constants && Core.constants.CUE_LEAD_MS) || 1500;
    const CUE_BAR_MAX_PX = 140;             // LyricStage.approach_bar_max_width
    const CARET_DAMP_HALF_TIME = 35;        // TypeBeatStyle.CARET_DAMP_HALF_TIME (ms)
    const SUNG_DAMP_HALF_TIME = 45;         // TypeBeatStyle.SUNG_DAMP_HALF_TIME (ms)
    const CARET_BLINK_PERIOD = 530;         // TypeBeatStyle.CARET_BLINK_PERIOD (ms)
    const LINE_SCROLL_MS = 220;             // TypeBeatStyle.LINE_SCROLL_DURATION (ms), OutQuint
    const CARET_SNAP_FACTOR = 1.5;          // Caret.MoveToTarget: snap past 1.5 line heights
    const CARET_MOVING_EPSILON = 0.75;      // Caret.Update: "still moving" threshold, px
    const PERFECT_POP_MS = 140;             // LyricLineDisplay.PlayJudgementFeedback
    const ROLLING_WPM_WINDOW = 30;          // TypingEngine.rolling_wpm_window
    // No desktop analogue: the client shows osu's skip overlay through an instrumental stretch.
    // The browser cannot seek a scheduled AudioBufferSourceNode without changing the gameplay
    // clock, so a long dead zone gets a labelled countdown instead of a skip.
    const GAP_CHIP_MIN_MS = 1800;

    // ---------------------------------------------------------------------------
    // Pure display math. No DOM, no engine mutation; exported on Core.display purely so the
    // Node fidelity harness can drive it (see tests/Typebeat.Web.Tests/Js/PlayerDisplayHarness.cjs).
    // ---------------------------------------------------------------------------

    /// osu.Framework Interpolation.DampContinuously: frame-rate independent approach to a target.
    function dampContinuously(current, target, halfTime, elapsedMs) {
        if (!(halfTime > 0) || !(elapsedMs > 0)) return current;
        return current + (target - current) * (1 - Math.pow(0.5, elapsedMs / halfTime));
    }

    // The sung-position polyline for one line (mirrors TypingLine's sungPoints constructor):
    // (startTime, 0), each cell's (target, cellIndex), (singEndTime, cellCount), with times
    // clamped monotonic non-decreasing. Every cell of the DEFAULT stream is typeable (normalize
    // strips anything outside the typeable surface and the supported marks, and the derivation then
    // removes the marks), so every cell contributes a point, exactly as the C# does for its
    // typeable cells.
    function buildSungPoints(line) {
        const points = [{ t: line.startTime, i: 0 }];
        let last = line.startTime;
        for (let i = 0; i < line.cells.length; i++) {
            const t = Math.max(line.cells[i].target, last);
            points.push({ t: t, i: i });
            last = t;
        }
        points.push({ t: Math.max(line.singEndTime, last), i: line.cells.length });
        return points;
    }

    // Fractional cell index the VOCAL is on at `time` (mirrors TypingLine.SungPositionAt):
    // piecewise-linear through the polyline, clamped outside it, zero-length time segments
    // skipped so the position jumps rather than dividing by zero.
    function sungPositionAt(points, time) {
        if (time <= points[0].t) return points[0].i;
        const end = points[points.length - 1];
        if (time >= end.t) return end.i;

        // Left anchor: the LAST point at or before `time`, so among points sharing one time we
        // anchor at the greatest index.
        let left = 0;
        for (let k = 1; k < points.length; k++) {
            if (points[k].t <= time) left = k;
            else break;
        }
        const a = points[left], b = points[left + 1];
        if (b.t <= a.t) return b.i;
        return a.i + (b.i - a.i) * (time - a.t) / (b.t - a.t);
    }

    // One depleting cue-in bar (mirrors LyricStage.updateCueBar): the width shrinks 1 -> 0 over
    // the final CUE_LEAD_MS before `remaining` hits 0, brightening as it lands. A past instant
    // (remaining <= 0) is behind the clock, so a stale cue can never appear.
    function cueBar(remaining, opacityScale) {
        if (remaining > 0 && remaining <= CUE_LEAD_MS) {
            const progress = remaining / CUE_LEAD_MS;
            return {
                shown: true,
                width: CUE_BAR_MAX_PX * progress,
                alpha: (0.85 - 0.35 * progress) * opacityScale
            };
        }
        return { shown: false, width: 0, alpha: 0 };
    }

    // Caret visibility over time (mirrors Caret.Update): solid while moving or within one blink
    // period of the last keystroke, then a cosine blink.
    function caretAlpha(msSinceActivity, moving, blinks) {
        if (!blinks) return 1;
        if (moving || msSinceActivity < CARET_BLINK_PERIOD) return 1;
        const phase = (msSinceActivity - CARET_BLINK_PERIOD) / CARET_BLINK_PERIOD;
        return 0.5 + 0.5 * Math.cos(phase * Math.PI * 2);
    }

    // Gross WPM implied by `count` presses spanning `spanMs` of ACTIVE time (mirrors
    // TypingEngine.LiveRollingWpm): n presses bound n-1 inter-key gaps, so the span covers
    // (n-1) chars' worth of typing. Falls back until the window is meaningful.
    function rollingWpmValue(count, spanMs, fallback) {
        if (count < 2 || !(spanMs > 0)) return fallback;
        return ((count - 1) / 5.0) / (spanMs / 60000.0);
    }

    // Ring buffer of the ACTIVE-TIME stamps of the last `size` correct keypresses. The desktop
    // engine keeps this inside TypingEngine; here it lives in the renderer, fed from the engine's
    // onCharJudged hook, so typebeat-core.js stays untouched. Backspace deliberately does not pop:
    // the buffer records keystrokes, not cell states.
    function makeRollingWpm(size) {
        const samples = new Array(size).fill(0);
        let count = 0, next = 0;
        return {
            push(activeTimeMs) {
                samples[next] = activeTimeMs;
                next = (next + 1) % size;
                if (count < size) count++;
            },
            value(fallback) {
                if (count < 2) return fallback;
                // Once the ring is full, the next slot to write is also the oldest entry.
                const oldest = samples[count < size ? 0 : next];
                const newest = samples[(next + size - 1) % size];
                return rollingWpmValue(count, newest - oldest, fallback);
            }
        };
    }

    /// Judgement.SyncQuality: 1 on target, decaying to 0 at the Ok window edges.
    function syncQuality(delta, w) {
        const q = 1 - (delta < 0 ? -delta / w.oe : delta / w.ol);
        return q < 0 ? 0 : (q > 1 ? 1 : q);
    }

    // One pass over the map for both live readouts:
    //   completion: hits / cells seen so far (how "typed %" reads mid-play, and what rank keys off)
    //   sync:       mean sync quality x100 over resolved cells (mirrors TypingEngine.LiveSyncPercent);
    //               a cell in a SEALED line that never landed correct resolves at q = 0.
    function liveStats(engine) {
        let hit = 0, seen = 0, syncSum = 0, resolved = 0;
        const lines = engine.lines;
        for (let li = 0; li < lines.length; li++) {
            const sealed = li < engine.nextSealIndex;
            const cells = lines[li].cells;
            for (let ci = 0; ci < cells.length; ci++) {
                const c = cells[ci];
                const scored = c.state === 'correct' &&
                    (c.judgeType === 'Perfect' || c.judgeType === 'Good' || c.judgeType === 'Ok');
                if (scored) { hit++; seen++; }
                else if (c.state === 'correct' || c.state === 'missed') seen++;

                if (c.state === 'correct' && c.judgedDelta !== null) {
                    syncSum += syncQuality(c.judgedDelta, Core.windowsFor(c.tier));
                    resolved++;
                } else if (sealed) {
                    resolved++;
                }
            }
        }
        return {
            completion: seen > 0 ? hit / seen : 1,
            sync: resolved === 0 ? 100 : 100 * syncSum / resolved
        };
    }

    // Which line the cue-in bars belong to (mirrors LyricStage.updateApproachCue). A line
    // activates at the very moment its cue window opens, so in a continuous map the PREVIOUS
    // line is still active and carries the cue; but after a gap the line self-activates with
    // nobody before it, and while its own first char is still ahead the cue is its own.
    function cueTargetLine(lines, activeLineIndex, nextSealIndex, time) {
        if (activeLineIndex < 0) return nextSealIndex;
        const active = lines[activeLineIndex];
        const inOwnLeadIn = active.cells.length > 0 && active.cells[0].target > time;
        return inOwnLeadIn ? activeLineIndex : activeLineIndex + 1;
    }

    // OutQuint, the easing every desktop stage animation uses.
    function outQuint(p) { return 1 - Math.pow(1 - p, 5); }

    // ---------------------------------------------------------------------------
    // DOM helpers.
    // ---------------------------------------------------------------------------

    function el(tag, cls, html) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (html != null) e.innerHTML = html;
        return e;
    }

    function fmtInt(n) { return Math.round(n).toLocaleString('en-US'); }
    function fmtPct(x) { return (x * 100).toFixed(2) + '%'; }

    function mountPlayer(container, opts) {
        const beatmap = Core.buildBeatmap(Core.parseLyricOsu(opts.osuText));
        const title = opts.title || beatmap.title || 'untitled';
        const artist = opts.artist || beatmap.artist || '';

        // The SONG's playhead, per line, precomputed once: independent of where the player is,
        // which is the whole point (you can see yourself rushing or dragging against it).
        const sungPoints = beatmap.lines.map(buildSungPoints);

        container.innerHTML = '';
        const root = el('div', 'tb-player');
        container.appendChild(root);

        // --- scaffold --------------------------------------------------------
        const hud = el('div', 'tb-hud');
        const hudScore = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-score">0</span><span class="tb-hud-lbl">score</span>');
        const hudCombo = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-combo">0</span><span class="tb-hud-lbl">combo</span>');
        const hudAcc = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-acc">100%</span><span class="tb-hud-lbl">typed</span>');
        const hudWpm = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-wpm">0</span><span class="tb-hud-lbl">wpm</span>');
        const hudSync = el('div', 'tb-hud-stat tb-hud-sync', '<span class="tb-hud-val" id="tb-sync">100.0%</span><span class="tb-hud-lbl">sync</span>');
        hud.append(hudScore, hudCombo, hudAcc, hudWpm, hudSync);

        const health = el('div', 'tb-health');
        const healthFill = el('div', 'tb-health-fill');
        health.appendChild(healthFill);

        // The 3-line stack. Each row owns its own cells, sung sweep and cue bars, so every
        // display-local x is a plain offset inside that row and the auto-shrink scale applied
        // to the row carries them all together (the desktop's per-display content scale).
        const stage = el('div', 'tb-stage');
        const stack = el('div', 'tb-stack');
        const rowPrev = makeRow('tb-line-prev');
        const rowCur = makeRow('tb-line-cur');
        const rowNext = makeRow('tb-line-next');
        const rows = [rowPrev, rowCur, rowNext];
        stack.append(rowPrev.row, rowCur.row, rowNext.row);

        // Carets and the wrong-key pops live INSIDE the active row, so they inherit its scale
        // and its position without a second coordinate space.
        const playerCaret = el('div', 'tb-caret tb-caret-player');
        const sungCaret = el('div', 'tb-caret tb-caret-sung');
        const wrongLayer = el('div', 'tb-wrongkeys');
        rowCur.row.append(sungCaret, playerCaret, wrongLayer);

        const gap = el('div', 'tb-gap');
        const gapLabel = el('span', 'tb-gap-label', '');
        const gapTrack = el('span', 'tb-gap-track');
        const gapFill = el('span', 'tb-gap-fill');
        gapTrack.appendChild(gapFill);
        gap.append(gapLabel, gapTrack);
        stage.append(stack, gap);

        const progress = el('div', 'tb-progress');
        const progressFill = el('div', 'tb-progress-fill');
        progress.appendChild(progressFill);

        const meta = el('div', 'tb-meta', `<span class="tb-meta-title">${escapeHtml(title)}</span>${artist ? ' <span class="tb-meta-artist">' + escapeHtml(artist) + '</span>' : ''}`);

        const overlay = el('div', 'tb-overlay');
        root.append(hud, health, stage, progress, meta, overlay);

        const scoreEl = root.querySelector('#tb-score');
        const comboEl = root.querySelector('#tb-combo');
        const accEl = root.querySelector('#tb-acc');
        const wpmEl = root.querySelector('#tb-wpm');
        const syncEl = root.querySelector('#tb-sync');

        // --- audio + engine state -------------------------------------------
        let audioCtx = null, audioBuffer = null, source = null;
        let startedAt = 0, raf = 0, backstop = 0, engine = null, concluded = false, running = false;
        let startKeyHandler = null; // capturing keydown listener while the start gate is up

        function nowMs() { return audioCtx ? (audioCtx.currentTime - startedAt) * 1000 : 0; }

        function cleanupAudio() {
            if (source) { try { source.stop(); } catch (e) {} try { source.disconnect(); } catch (e) {} source = null; }
        }

        function removeStartKey() {
            if (startKeyHandler) { document.removeEventListener('keydown', startKeyHandler, true); startKeyHandler = null; }
        }

        function destroy() {
            running = false;
            if (raf) cancelAnimationFrame(raf);
            if (backstop) { clearInterval(backstop); backstop = 0; }
            document.removeEventListener('keydown', onKeyDown, true);
            window.removeEventListener('resize', onResize);
            removeStartKey();
            cleanupAudio();
            if (audioCtx) { try { audioCtx.close(); } catch (e) {} audioCtx = null; }
        }

        // --- input -----------------------------------------------------------
        function onKeyDown(e) {
            if (!running || !engine) return;
            if (e.ctrlKey || e.altKey || e.metaKey) return;
            if (e.key === 'Backspace') { e.preventDefault(); engine.processBackspace(); return; }
            if (e.repeat) return;
            let ch = null;
            if (e.key === ' ' || e.code === 'Space') ch = ' ';
            else if (e.key && e.key.length === 1 && KEY_RE.test(e.key)) ch = e.key;
            if (ch !== null) {
                e.preventDefault();
                engine.processKey(ch, nowMs());
            }
        }

        // --- row plumbing -----------------------------------------------------
        // A row's cell spans are built ONCE per line change and then only repainted (class/text
        // diffed in place). That keeps the measured per-cell offsets valid for the whole line,
        // which is what lets the caret, the sweep and the cue bars be plain arithmetic per frame.
        function makeRow(cls) {
            const row = el('div', 'tb-line ' + cls);
            const cellsBox = el('span', 'tb-cells');
            const sweep = el('div', 'tb-sweep');
            const sweepFill = el('div', 'tb-sweep-fill');
            const sweepGlow = el('div', 'tb-sweep-glow');
            sweep.append(sweepFill, sweepGlow);
            // Word cue first, boundary cue second: the solid boundary bar draws OVER the
            // translucent first-word bar where they overlap, as on desktop.
            const cue = el('div', 'tb-cue');
            const cueWord = el('div', 'tb-cue-bar');
            const cueBoundary = el('div', 'tb-cue-bar');
            cue.append(cueWord, cueBoundary);
            row.append(sweep, cellsBox, cue);
            return {
                row, cellsBox, sweep, sweepFill, sweepGlow, cue, cueWord, cueBoundary,
                spans: [], offsets: [0], line: null, index: -1, scale: 1
            };
        }

        function buildRow(rowObj, index) {
            const line = beatmap.lines[index] || null;
            rowObj.line = line;
            rowObj.index = line ? index : -1;
            rowObj.cellsBox.textContent = '';
            rowObj.spans = [];
            rowObj.offsets = [0];
            rowObj.scale = 1;
            rowObj.row.style.transform = 'none';
            rowObj.sweep.style.display = line ? '' : 'none';
            if (!line) return;

            const frag = document.createDocumentFragment();
            for (let i = 0; i < line.cells.length; i++) {
                const span = el('span');
                rowObj.spans.push(span);
                frag.appendChild(span);
            }
            rowObj.cellsBox.appendChild(frag);
        }

        function cellClass(cell, isCaret, popping) {
            let cls = 'tb-c';
            if (cell.state === 'correct') {
                const jt = cell.judgeType;
                // Typed but off-time (Premature/Lagging) scores as a miss; desktop draws it like
                // any other correct char, the browser keeps a distinct warn tint as a free hint.
                cls += (jt === 'Perfect' || jt === 'Good' || jt === 'Ok') ? ' tb-c-hit' : ' tb-c-off';
            } else if (cell.state === 'missed') {
                cls += ' tb-c-miss';
            } else {
                cls += ' tb-c-todo';
            }
            // A FREESTYLE cell never shows the authoring marker: while it is still open it
            // shimmers through the glyph pool (the desktop client's exact sequence), and once
            // filled it freezes on the char the player actually pressed, so a finished line still
            // shows which slots were free. tb-c-free colours it in both states.
            if (cell.freestyle) cls += ' tb-c-free';
            if (isCaret) cls += ' tb-c-at';
            if (popping) cls += ' tb-c-pop';
            return cls;
        }

        function cellText(cell, shimmerTick, i) {
            let ch = cell.expected;
            if (cell.freestyle) ch = cell.typedChar !== null ? cell.typedChar : Core.freestyleGlyph(shimmerTick, i);
            // A space (a word gap's expected char, or a space typed into a freestyle slot) must
            // render as nbsp or the browser collapses it away.
            return ch === ' ' ? ' ' : ch;
        }

        // Repaint a row in place: only the spans whose class or glyph actually changed are
        // written, so a settled line costs nothing and the shimmer only touches its own cells.
        function paintRow(rowObj, caretIndex, shimmerTick, time) {
            const line = rowObj.line;
            if (!line) return;
            for (let i = 0; i < rowObj.spans.length; i++) {
                const span = rowObj.spans[i];
                const popping = popEnd[i] > time && rowObj === rowCur;
                const cls = cellClass(line.cells[i], i === caretIndex, popping);
                if (span.className !== cls) span.className = cls;
                const txt = cellText(line.cells[i], shimmerTick, i);
                if (span.textContent !== txt) span.textContent = txt;
            }
        }

        // Shrink an over-long line to fit the stage width (the C# client's auto-shrink). Lines
        // never wrap (white-space:nowrap), so without this a long line would overflow/clip. The
        // scale lands on the row, which carries the cells AND the sweep/cue/caret overlays, so
        // everything stays registered to the glyphs.
        function fitRow(rowObj, avail) {
            const natural = rowObj.cellsBox.offsetWidth;
            const scale = (avail > 0 && natural > avail) ? avail / natural : 1;
            rowObj.scale = scale;
            rowObj.row.style.transform = scale === 1 ? 'none' : 'scale(' + scale.toFixed(4) + ')';
        }

        // Per-cell left edges in row-local (pre-scale) px; offsets[n] is the end of the line, so
        // a caret past the last cell has a position too. Read through getBoundingClientRect and
        // divided back out by the applied scale, which keeps sub-pixel advances exact.
        function measureRow(rowObj) {
            const n = rowObj.spans.length;
            if (n === 0) { rowObj.offsets = [0]; return; }
            const base = rowObj.row.getBoundingClientRect();
            const s = rowObj.scale || 1;
            const offs = new Array(n + 1);
            let last = null;
            for (let i = 0; i < n; i++) {
                last = rowObj.spans[i].getBoundingClientRect();
                offs[i] = (last.left - base.left) / s;
            }
            offs[n] = (last.right - base.left) / s;
            rowObj.offsets = offs;
        }

        /// Row-local x of a fractional cell index (mirrors LyricLineDisplay.localXFor).
        function xAt(rowObj, fractionalIndex) {
            const offs = rowObj.offsets;
            const max = offs.length - 1;
            const f = fractionalIndex < 0 ? 0 : (fractionalIndex > max ? max : fractionalIndex);
            const lo = Math.floor(f);
            const hi = Math.min(lo + 1, max);
            return offs[lo] + (offs[hi] - offs[lo]) * (f - lo);
        }

        function rowFor(index) {
            if (index === lastCurIdx) return rowCur;
            if (index === lastCurIdx - 1) return rowPrev;
            if (index === lastCurIdx + 1) return rowNext;
            return null;
        }

        // --- render state ----------------------------------------------------
        let lastCurIdx = -2, wrongFlash = 0, wrongDirection = 1;
        let caretX = 0, sungX = 0, caretSnap = true;
        let lastTypedAt = -1e9, lastFrameMs = null;
        let scrollPitch = 0, scrollStart = -1;
        let popEnd = [];             // per-cell audio-clock deadline for the Perfect pop
        let gapActive = false, gapFrom = 0, gapTo = 0;
        let rolling = makeRollingWpm(ROLLING_WPM_WINDOW);

        // Order matters: the spans must carry their glyphs BEFORE the row is fitted and measured,
        // or every cell offset would be read off an empty box and the caret, sweep and cue bars
        // would all sit at x = 0 until the next line change.
        function rebuildRows(curIdx, caretIndex, shimmerTick, time) {
            buildRow(rowPrev, curIdx - 1);
            buildRow(rowCur, curIdx);
            buildRow(rowNext, curIdx + 1);
            popEnd = new Array(rowCur.spans.length).fill(-1);
            paintRow(rowPrev, -1, shimmerTick, time);
            paintRow(rowCur, caretIndex, shimmerTick, time);
            paintRow(rowNext, -1, shimmerTick, time);
            relayout();
        }

        // Re-fit and re-measure every row. Run on a line change and on a viewport resize (the
        // lyric font size is a vw clamp, so a resize moves every cell); the caret snaps rather
        // than sliding to its new home.
        function relayout() {
            const avail = stack.clientWidth;
            for (const r of rows) fitRow(r, avail);
            for (const r of rows) measureRow(r);
            caretSnap = true;
        }

        function onResize() { if (engine) relayout(); }

        // The stack scroll happens the moment a line SEALS (the boundary, or grace-end for
        // overrunning vocals), not when the next line activates: exactly where the desktop
        // relayout runs. Clocked off the audio clock, eased OutQuint like every stage animation.
        function beginScroll(time, delta) {
            if (delta !== 1) { scrollStart = -1; stack.style.transform = 'none'; return; }
            const pitch = rowCur.row.offsetTop - rowPrev.row.offsetTop;
            if (!(pitch > 0)) { scrollStart = -1; stack.style.transform = 'none'; return; }
            scrollPitch = pitch;
            scrollStart = time;
        }

        function updateScroll(time) {
            if (scrollStart < 0) return;
            const p = Math.min(1, Math.max(0, (time - scrollStart) / LINE_SCROLL_MS));
            const y = scrollPitch * (1 - outQuint(p));
            stack.style.transform = y > 0.01 ? 'translateY(' + y.toFixed(2) + 'px)' : 'none';
            if (p >= 1) scrollStart = -1;
        }

        // Two depleting bars under the upcoming line's first char: a SOLID one landing on the
        // line boundary (StartTime) and a 50%-opaque one landing on the FIRST WORD. A mapper may
        // set the boundary earlier than the first word, so the two can be distinct signals; when
        // they coincide they read as one solid bar. Mirrors LyricStage.updateApproachCue.
        function updateCue(time, active) {
            const target = engine.finished ? -1
                : cueTargetLine(beatmap.lines, active ? engine.activeLineIndex : -1, engine.nextSealIndex, time);
            const rowObj = (target >= 0 && target < beatmap.lines.length) ? rowFor(target) : null;

            for (const r of rows) {
                if (r !== rowObj) r.cue.style.display = 'none';
            }
            if (!rowObj || !rowObj.line || rowObj.line.cells.length === 0) return;

            const line = rowObj.line;
            const word = cueBar(line.cells[0].target - time, 0.5);
            const boundary = cueBar(line.startTime - time, 1);
            if (!word.shown && !boundary.shown) { rowObj.cue.style.display = 'none'; return; }

            rowObj.cue.style.display = '';
            rowObj.cue.style.left = xAt(rowObj, 0).toFixed(2) + 'px';
            applyCueBar(rowObj.cueWord, word);
            applyCueBar(rowObj.cueBoundary, boundary);
        }

        function applyCueBar(bar, state) {
            bar.style.width = state.width.toFixed(2) + 'px';
            bar.style.opacity = state.shown ? state.alpha.toFixed(3) : '0';
        }

        // The sung underline: a faint full-width track under every visible line with a fill that
        // sweeps to the vocal position, plus a bright head. Every row draws its own, so a line
        // that just sealed keeps its finished sweep and the upcoming one starts empty.
        function updateSweeps(time) {
            for (const r of rows) {
                if (!r.line) continue;
                const pos = sungPositionAt(sungPoints[r.index], time);
                const x = xAt(r, pos);
                r.sweepFill.style.width = x.toFixed(2) + 'px';
                r.sweepGlow.style.transform = 'translateX(' + x.toFixed(2) + 'px)';
                r.sweepGlow.style.opacity = (r === rowCur && x > 0.5) ? '0.9' : '0';
            }
        }

        // Player caret follows the typing caret index; sung caret follows the vocal position on
        // the same line. Both damp toward their target and snap on a line jump (Caret.MoveToTarget).
        function updateCarets(time, elapsed, active) {
            const lineComplete = active && engine.caretIndex >= rowCur.line.cells.length;
            const show = active && !lineComplete && !engine.finished;

            const caretTarget = active ? xAt(rowCur, engine.caretIndex) : caretX;
            const sungTarget = active ? xAt(rowCur, sungPositionAt(sungPoints[rowCur.index], time)) : sungX;
            const lineHeight = rowCur.row.offsetHeight || 40;

            if (caretSnap) {
                caretX = caretTarget;
                sungX = sungTarget;
                caretSnap = false;
            } else {
                caretX = approach(caretX, caretTarget, CARET_DAMP_HALF_TIME, elapsed, lineHeight);
                sungX = approach(sungX, sungTarget, SUNG_DAMP_HALF_TIME, elapsed, lineHeight);
            }

            playerCaret.style.transform = 'translateX(' + caretX.toFixed(2) + 'px)';
            sungCaret.style.transform = 'translateX(' + sungX.toFixed(2) + 'px)';

            const moving = Math.abs(caretTarget - caretX) > CARET_MOVING_EPSILON;
            playerCaret.style.opacity = show ? caretAlpha(time - lastTypedAt, moving, true).toFixed(3) : '0';
            sungCaret.style.opacity = show ? '1' : '0';
        }

        function approach(current, target, halfTime, elapsed, lineHeight) {
            if (Math.abs(target - current) > lineHeight * CARET_SNAP_FACTOR) return target;
            return dampContinuously(current, target, halfTime, elapsed);
        }

        // A long instrumental stretch (and the pre-roll before the first line) is dead air the
        // desktop covers with osu's skip overlay. The browser cannot seek its scheduled audio
        // source without moving the gameplay clock, so it labels the wait and counts it down
        // instead; the cue bars still land on the line itself.
        function updateGap(time, active) {
            const upcoming = engine.nextSealIndex;
            const hasNext = !engine.finished && !engine.failed && upcoming < beatmap.lines.length;

            if (active || !hasNext) {
                if (gapActive) { gapActive = false; gap.classList.remove('tb-gap-on'); }
                return;
            }

            const line = beatmap.lines[upcoming];
            const vocal = line.cells.length ? line.cells[0].target : line.startTime;

            if (!gapActive) {
                if (line.activationTime - time < GAP_CHIP_MIN_MS) return;
                gapActive = true;
                gapFrom = time;
                gapTo = vocal;
                gap.classList.add('tb-gap-on');
                gapLabel.textContent = upcoming === 0 ? 'intro' : 'instrumental';
            }

            const remain = Math.max(0, vocal - time);
            const span = gapTo - gapFrom;
            const p = span > 0 ? Math.min(1, Math.max(0, (time - gapFrom) / span)) : 1;
            gapFill.style.width = (p * 100).toFixed(1) + '%';
            gapLabel.textContent = (upcoming === 0 ? 'intro' : 'instrumental') + ' · ' + (remain / 1000).toFixed(1) + 's';
        }

        // A rejected wrong key never enters the line; the offending letter pops up beside the
        // caret (alternating sides), falls away and fades (LyricStage.onWrongKeyRejected).
        function popWrongKey(c) {
            wrongDirection = -wrongDirection;
            const letter = el('span', 'tb-wrongkey');
            letter.textContent = c === ' ' ? '_' : c;
            letter.style.setProperty('--dir', String(wrongDirection));
            letter.style.left = (caretX + wrongDirection * 24).toFixed(2) + 'px';
            wrongLayer.appendChild(letter);
            const drop = () => { if (letter.parentNode) letter.parentNode.removeChild(letter); };
            letter.addEventListener('animationend', drop);
            setTimeout(drop, 1200);
        }

        function render() {
            const time = nowMs();
            const elapsed = lastFrameMs === null ? 0 : Math.max(0, time - lastFrameMs);
            lastFrameMs = time;

            const active = engine.activeLineIndex >= 0;
            const curIdx = active ? engine.activeLineIndex
                : Math.min(engine.nextSealIndex, beatmap.lines.length - 1);
            // Freestyle shimmer tick: the current row repaints every frame so its open slots
            // animate; the neighbour rows only repaint on a line change, so theirs hold a still
            // glyph until the line becomes current (cheap, and never the raw marker).
            const shimmerTick = Core.freestyleTick(time);

            const caretIndex = active ? engine.caretIndex : -1;

            if (curIdx !== lastCurIdx) {
                const delta = curIdx - lastCurIdx;
                rebuildRows(curIdx, caretIndex, shimmerTick, time);
                lastCurIdx = curIdx;
                beginScroll(time, delta);
            }

            stack.classList.toggle('tb-stack-live', active);
            // Only the current row repaints per frame; the neighbours were painted at the line
            // change and hold a still freestyle glyph until they become current.
            paintRow(rowCur, caretIndex, shimmerTick, time);

            updateScroll(time);
            updateSweeps(time);
            updateCarets(time, elapsed, active);
            updateCue(time, active);
            updateGap(time, active);

            const stats = liveStats(engine);
            scoreEl.textContent = fmtInt(engine.score);
            comboEl.textContent = engine.combo + 'x';
            accEl.textContent = Math.round(stats.completion * 100) + '%';
            wpmEl.textContent = Math.round(rolling.value(engine.liveWpm));
            syncEl.textContent = stats.sync.toFixed(1) + '%';

            healthFill.style.width = (engine.health * 100) + '%';
            healthFill.classList.toggle('tb-health-danger', engine.consecutiveWrongKeys >= 8);

            if (audioBuffer) {
                const p = Math.max(0, Math.min(1, time / (audioBuffer.duration * 1000)));
                progressFill.style.width = (p * 100) + '%';
            }

            if (wrongFlash > 0) { root.classList.add('tb-shake'); wrongFlash--; }
            else root.classList.remove('tb-shake');
        }

        // --- loop / lifecycle ------------------------------------------------
        // One tick = advance the audio-clocked engine + repaint. Driven by rAF while
        // the tab is visible (vsync-smooth); a low-rate setInterval backstop keeps it
        // ticking when the tab is hidden and rAF is paused, so state stays consistent
        // with the still-playing audio instead of freezing.
        function tick() {
            if (!running) return;
            engine.update(nowMs());
            render();
            if ((engine.finished || engine.failed) && !concluded) conclude();
        }
        function rafLoop() {
            if (!running) return;
            tick();
            if (running) raf = requestAnimationFrame(rafLoop);
        }

        function conclude() {
            concluded = true;
            running = false;
            if (raf) cancelAnimationFrame(raf);
            if (backstop) { clearInterval(backstop); backstop = 0; }
            document.removeEventListener('keydown', onKeyDown, true);
            if (engine.failed) cleanupAudio();
            const results = Core.computeScore(engine);
            showResults(results);
            if (opts.onFinish) { try { opts.onFinish(results, publicApi); } catch (e) { console.error(e); } }
        }

        function begin() {
            engine = new Core.TypingEngine(beatmap);
            // Display-only hooks. Neither reads anything back into the engine, so nothing here
            // can move a judgement or a score.
            engine.onWrongKey = function (c) { wrongFlash = 6; popWrongKey(c); };
            engine.onCharJudged = function (index, type) {
                // The desktop engine logs every ACCEPTED press (scoring-inert retypes included)
                // for its rolling WPM, and resets the caret blink on each one.
                rolling.push(engine.activeTimeMs);
                lastTypedAt = nowMs();
                if (type === 'Perfect' && index < popEnd.length) popEnd[index] = nowMs() + PERFECT_POP_MS;
            };
            concluded = false;
            lastCurIdx = -2;
            lastFrameMs = null;
            caretX = sungX = 0;
            caretSnap = true;
            lastTypedAt = -1e9;
            scrollStart = -1;
            gapActive = false;
            gap.classList.remove('tb-gap-on');
            stack.style.transform = 'none';
            wrongLayer.textContent = '';
            rolling = makeRollingWpm(ROLLING_WPM_WINDOW);
            removeStartKey();
            // A fresh play (including "play again") starts a new scoring session; let the host
            // mint a fresh score token here so its min-play-time gate keys off this play's start.
            if (opts.onPlayStart) { try { opts.onPlayStart(); } catch (e) { console.error(e); } }
            overlay.className = 'tb-overlay';
            overlay.innerHTML = '';
            cleanupAudio();
            source = audioCtx.createBufferSource();
            source.buffer = audioBuffer;
            // Default playback at 10% (90% quieter); the map audio is loud on its own.
            const gainNode = audioCtx.createGain();
            gainNode.gain.value = 0.1;
            source.connect(gainNode);
            gainNode.connect(audioCtx.destination);
            startedAt = audioCtx.currentTime + 0.06; // small scheduling lead
            source.start(startedAt);
            running = true;
            document.addEventListener('keydown', onKeyDown, true);
            window.removeEventListener('resize', onResize);
            window.addEventListener('resize', onResize);
            raf = requestAnimationFrame(rafLoop);
            backstop = setInterval(function () { if (running && document.hidden) tick(); }, 250);
        }

        function showStartGate(label) {
            overlay.className = 'tb-overlay tb-overlay-on';
            overlay.innerHTML = '';
            const card = el('div', 'tb-card');
            card.appendChild(el('div', 'tb-card-title', escapeHtml(title)));
            if (artist) card.appendChild(el('div', 'tb-card-sub', escapeHtml(artist)));
            card.appendChild(el('div', 'tb-card-hint', 'type the lyrics as they are sung · the blue underline is the vocal, the bar under the next line counts you in · 13 wrong keys in a row and you fail'));
            const btn = el('button', 'tb-btn tb-btn-primary', label || 'press space to start');
            card.appendChild(btn);
            overlay.appendChild(card);
            const go = () => {
                if (audioCtx.state === 'suspended') audioCtx.resume();
                begin(); // begin() removes the start-key listener and mints a fresh token
            };
            btn.addEventListener('click', go);
            removeStartKey();
            startKeyHandler = (e) => { if (e.key === ' ' || e.code === 'Space' || e.key === 'Enter') { e.preventDefault(); go(); } };
            document.addEventListener('keydown', startKeyHandler, true);
        }

        function showResults(results) {
            overlay.className = 'tb-overlay tb-overlay-on';
            overlay.innerHTML = '';
            const card = el('div', 'tb-card tb-results');
            card.appendChild(el('div', 'tb-rank tb-rank-' + results.rank.replace(/[^A-Z]/gi, ''), results.rank));
            card.appendChild(el('div', 'tb-card-title', results.passed ? 'cleared' : 'failed'));
            const grid = el('div', 'tb-result-grid');
            grid.innerHTML =
                statCell('score', fmtInt(results.totalScore)) +
                statCell('typed', fmtPct(results.completion)) +
                statCell('accuracy', fmtPct(results.accuracy)) +
                statCell('max combo', results.maxCombo + 'x') +
                statCell('wpm', Math.round(results.wpm)) +
                statCell('misses', results.counts.miss) +
                // Beside misses, never folded into them: a miss is a character the song left
                // behind, a mistype is a wrong key you pressed, and only the first costs you rank.
                statCell('mistypes', results.counts.mistypes);
            card.appendChild(grid);
            const status = el('div', 'tb-submit-status', '');
            card.appendChild(status);
            const actions = el('div', 'tb-result-actions');
            const again = el('button', 'tb-btn tb-btn-primary', 'play again');
            again.addEventListener('click', () => showStartGate('press space to start'));
            actions.appendChild(again);
            if (opts.onExit) {
                const back = el('button', 'tb-btn tb-btn-ghost', 'back to maps');
                back.addEventListener('click', () => { destroy(); opts.onExit(); });
                actions.appendChild(back);
            }
            card.appendChild(actions);
            overlay.appendChild(card);
            publicApi._status = status;
        }

        function statCell(label, val) {
            return `<div class="tb-result-cell"><span class="tb-result-val">${escapeHtml(String(val))}</span><span class="tb-result-lbl">${escapeHtml(label)}</span></div>`;
        }

        // --- public api ------------------------------------------------------
        const publicApi = {
            beatmap,
            destroy,
            get engine() { return engine; },
            setSubmitStatus(html, cls) {
                if (publicApi._status) {
                    publicApi._status.innerHTML = html;
                    publicApi._status.className = 'tb-submit-status' + (cls ? ' ' + cls : '');
                }
            }
        };

        // --- boot: decode audio then show the start gate ---------------------
        overlay.className = 'tb-overlay tb-overlay-on';

        // Guard an empty/unparseable map (no typeable cells); otherwise the first tick would
        // instantly "clear" it with rank X. Nothing to play.
        if (!beatmap.lines.length || !beatmap.totalCells) {
            overlay.appendChild(el('div', 'tb-card', '<div class="tb-card-title">unplayable map</div><div class="tb-card-hint">this map has no typeable lyrics.</div>'));
            return publicApi;
        }

        overlay.appendChild(el('div', 'tb-card', '<div class="tb-card-title">loading…</div>'));
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) {
            overlay.innerHTML = '';
            overlay.appendChild(el('div', 'tb-card', '<div class="tb-card-title">unsupported browser</div><div class="tb-card-hint">this browser has no Web Audio support.</div>'));
            return publicApi;
        }
        audioCtx = new AC();
        // decodeAudioData needs a fresh copy (some browsers detach the buffer).
        const bytes = opts.audioArrayBuffer.slice(0);
        audioCtx.decodeAudioData(bytes,
            (buf) => { audioBuffer = buf; showStartGate('press space to start'); },
            (err) => {
                overlay.innerHTML = '';
                overlay.appendChild(el('div', 'tb-card', '<div class="tb-card-title">could not load audio</div><div class="tb-card-hint">this map’s audio format isn’t supported by your browser.</div>'));
                console.error('decodeAudioData failed', err);
            });

        return publicApi;
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    Core.mountPlayer = mountPlayer;
    Core.escapeHtml = escapeHtml;

    // Read-only surface for the display-math fidelity harness. Nothing in typebeat-core.js
    // reads any of it; it exists so the ported presentation formulas can be pinned by a test
    // instead of only by eye.
    Core.display = {
        dampContinuously,
        buildSungPoints,
        sungPositionAt,
        cueBar,
        caretAlpha,
        rollingWpmValue,
        makeRollingWpm,
        syncQuality,
        liveStats,
        cueTargetLine,
        outQuint,
        constants: {
            CUE_LEAD_MS, CUE_BAR_MAX_PX, CARET_DAMP_HALF_TIME, SUNG_DAMP_HALF_TIME,
            CARET_BLINK_PERIOD, LINE_SCROLL_MS, CARET_SNAP_FACTOR, PERFECT_POP_MS,
            ROLLING_WPM_WINDOW, GAP_CHIP_MIN_MS
        }
    };
})(window.TypeBeatCore);
