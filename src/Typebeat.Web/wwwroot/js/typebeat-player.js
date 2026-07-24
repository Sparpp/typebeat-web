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
 */
(function (Core) {
    'use strict';
    if (!Core) return;

    const KEY_RE = /^[a-zA-Z0-9]$/;

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

        container.innerHTML = '';
        const root = el('div', 'tb-player');
        container.appendChild(root);

        // --- scaffold --------------------------------------------------------
        const hud = el('div', 'tb-hud');
        const hudScore = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-score">0</span><span class="tb-hud-lbl">score</span>');
        const hudCombo = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-combo">0</span><span class="tb-hud-lbl">combo</span>');
        const hudAcc = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-acc">100%</span><span class="tb-hud-lbl">typed</span>');
        const hudWpm = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-wpm">0</span><span class="tb-hud-lbl">wpm</span>');
        hud.append(hudScore, hudCombo, hudAcc, hudWpm);

        const health = el('div', 'tb-health');
        const healthFill = el('div', 'tb-health-fill');
        health.appendChild(healthFill);

        const stage = el('div', 'tb-stage');
        const rowPrev = el('div', 'tb-line tb-line-prev');
        const rowCur = el('div', 'tb-line tb-line-cur');
        const rowNext = el('div', 'tb-line tb-line-next');
        stage.append(rowPrev, rowCur, rowNext);

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

        // --- render ----------------------------------------------------------
        let lastCurIdx = -2, wrongFlash = 0;

        function lineToSpans(line, isActive, caretIndex, shimmerTick) {
            const frag = document.createDocumentFragment();
            if (!line) return frag;
            for (let i = 0; i < line.cells.length; i++) {
                if (isActive && i === caretIndex) {
                    frag.appendChild(el('span', 'tb-caret'));
                }
                const cell = line.cells[i];
                let cls = 'tb-c';
                if (cell.state === 'correct') {
                    const jt = cell.judgeType;
                    if (jt === 'Perfect' || jt === 'Good' || jt === 'Ok') cls += ' tb-c-hit';
                    else cls += ' tb-c-off'; // typed but off-time (scores as a miss)
                } else if (cell.state === 'missed') {
                    cls += ' tb-c-miss';
                } else {
                    cls += ' tb-c-todo';
                }
                if (isActive && i === caretIndex) cls += ' tb-c-at';
                let ch = cell.expected;
                if (cell.freestyle) {
                    // A FREESTYLE cell never shows the authoring marker: while it is still open it
                    // shimmers through the glyph pool (the desktop client's exact sequence), and
                    // once filled it freezes on the char the player actually pressed, so a finished
                    // line still shows which slots were free. Backspace clears typedChar and the
                    // shimmer resumes. tb-c-free colours it in both states.
                    cls += ' tb-c-free';
                    ch = cell.typedChar !== null ? cell.typedChar : Core.freestyleGlyph(shimmerTick, i);
                }
                // A space (a word gap's expected char, or a space typed into a freestyle slot)
                // must render as nbsp or the browser collapses it away.
                if (ch === ' ') ch = ' ';
                const span = el('span', cls);
                span.textContent = ch;
                frag.appendChild(span);
            }
            if (isActive && caretIndex >= line.cells.length) {
                frag.appendChild(el('span', 'tb-caret'));
            }
            return frag;
        }

        // Shrink an over-long line to fit the stage width (the C# client's auto-shrink). Lines
        // never wrap (white-space:nowrap), so without this a long line would overflow/clip.
        // scrollWidth is the untransformed content width, so re-reading it under an applied scale
        // stays stable frame to frame.
        function fitLine(row) {
            const avail = row.parentElement ? row.parentElement.clientWidth : 0;
            const natural = row.scrollWidth;
            row.style.transform = (avail > 0 && natural > avail) ? 'scale(' + (avail / natural).toFixed(4) + ')' : 'none';
        }

        function render() {
            const curIdx = engine.activeLineIndex >= 0 ? engine.activeLineIndex
                : Math.min(engine.nextSealIndex, beatmap.lines.length - 1);
            const active = engine.activeLineIndex >= 0;
            // Freestyle shimmer tick: the current row repaints every frame so its open slots
            // animate; the neighbour rows only repaint on a line change, so theirs hold a still
            // glyph until the line becomes current (cheap, and never the raw marker).
            const shimmerTick = Core.freestyleTick(nowMs());

            rowCur.classList.toggle('tb-line-live', active);
            rowCur.innerHTML = '';
            rowCur.appendChild(lineToSpans(beatmap.lines[curIdx], active, engine.caretIndex, shimmerTick));
            fitLine(rowCur);

            if (curIdx !== lastCurIdx) {
                rowPrev.innerHTML = '';
                rowPrev.appendChild(lineToSpans(beatmap.lines[curIdx - 1], false, -1, shimmerTick));
                rowNext.innerHTML = '';
                rowNext.appendChild(lineToSpans(beatmap.lines[curIdx + 1], false, -1, shimmerTick));
                fitLine(rowPrev);
                fitLine(rowNext);
                lastCurIdx = curIdx;
            }

            scoreEl.textContent = fmtInt(engine.score);
            comboEl.textContent = engine.combo + 'x';
            accEl.textContent = Math.round(liveCompletion() * 100) + '%';
            wpmEl.textContent = Math.round(engine.liveWpm);

            healthFill.style.width = (engine.health * 100) + '%';
            healthFill.classList.toggle('tb-health-danger', engine.consecutiveWrongKeys >= 8);

            if (audioBuffer) {
                const p = Math.max(0, Math.min(1, nowMs() / (audioBuffer.duration * 1000)));
                progressFill.style.width = (p * 100) + '%';
            }

            if (wrongFlash > 0) { root.classList.add('tb-shake'); wrongFlash--; }
            else root.classList.remove('tb-shake');
        }

        // Live "typed %": hits / cells seen so far (matches how completion reads mid-play).
        function liveCompletion() {
            let hit = 0, seen = 0;
            for (const line of beatmap.lines) {
                for (const c of line.cells) {
                    if (c.state === 'correct' && (c.judgeType === 'Perfect' || c.judgeType === 'Good' || c.judgeType === 'Ok')) { hit++; seen++; }
                    else if (c.state === 'correct' || c.state === 'missed') seen++;
                }
            }
            return seen > 0 ? hit / seen : 1;
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
            engine.onWrongKey = function () { wrongFlash = 6; };
            concluded = false;
            lastCurIdx = -2;
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
            const gain = audioCtx.createGain();
            gain.gain.value = 0.1;
            source.connect(gain);
            gain.connect(audioCtx.destination);
            startedAt = audioCtx.currentTime + 0.06; // small scheduling lead
            source.start(startedAt);
            running = true;
            document.addEventListener('keydown', onKeyDown, true);
            raf = requestAnimationFrame(rafLoop);
            backstop = setInterval(function () { if (running && document.hidden) tick(); }, 250);
        }

        function showStartGate(label) {
            overlay.className = 'tb-overlay tb-overlay-on';
            overlay.innerHTML = '';
            const card = el('div', 'tb-card');
            card.appendChild(el('div', 'tb-card-title', escapeHtml(title)));
            if (artist) card.appendChild(el('div', 'tb-card-sub', escapeHtml(artist)));
            card.appendChild(el('div', 'tb-card-hint', 'type the lyrics as they are sung · 13 wrong keys in a row and you fail'));
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
                statCell('misses', results.counts.miss);
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
})(window.TypeBeatCore);
