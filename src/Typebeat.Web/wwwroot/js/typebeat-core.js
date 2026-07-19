/*
 * typebeat-core.js — a faithful, dependency-free reimplementation of the
 * type!beat typing gameplay for the browser.
 *
 * This mirrors the desktop client's headless gameplay core
 * (typebeat.Game.Rulesets.TypeBeat: TypingEngine, TypingLine, Judgement,
 * TypeBeatHealthProcessor) and the standardised scorer (osu ScoreProcessor +
 * TypeBeatScoreProcessor) closely enough that a play here produces the same
 * statistics / total_score the desktop would, so scores interoperate on the
 * same leaderboards. See the recon spec in the repo history for the exact rules
 * each block reproduces.
 *
 * Vanilla JS on purpose — no framework, no bundler (matches the rest of the site).
 * Exposes window.TypeBeatCore.
 */
(function (global) {
    'use strict';

    // ---------------------------------------------------------------------------
    // Typeability — text normalization (mirrors Typeability in LyricBeatmap.cs).
    // ---------------------------------------------------------------------------
    function isTypeable(ch) {
        return ch === ' ' ||
            (ch >= 'a' && ch <= 'z') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9');
    }

    function fold(ch) {
        return ch.toLowerCase();
    }

    // Remove bracketed backing-vocal spans — "(...)" and "[...]" — with a shared depth
    // counter across both bracket types; an UNCLOSED bracket strips to end-of-string
    // (mirrors Typeability.StripBackingVocals; regexes diverged on unclosed/nested spans).
    function stripBackingVocals(raw) {
        if (!raw) return '';
        let out = '', depth = 0;
        for (const c of raw) {
            if (c === '(' || c === '[') { depth++; continue; }
            if (c === ')' || c === ']') { if (depth > 0) depth--; continue; }
            if (depth === 0) out += c;
        }
        return out;
    }

    // Count typeable chars in a token (for interpolation weights).
    function typeableCount(text) {
        let n = 0;
        for (const c of text) if (isTypeable(c)) n++;
        return n;
    }

    // NFD-fold diacritics, map curly punctuation to ASCII, DROP every non-space
    // char that isn't typeable, collapse whitespace, trim. So "don't" -> "dont",
    // "well-being" -> "wellbeing". The typed surface is only [a-z0-9 ].
    function normalize(s) {
        if (s == null) return '';
        s = stripBackingVocals(s);
        s = s.normalize('NFD').replace(/[̀-ͯ]/g, '');
        s = s
            .replace(/[‘’‛′]/g, "'")
            .replace(/[“”″]/g, '"')
            .replace(/[–—―]/g, '-')
            .replace(/ /g, ' ');
        let out = '';
        for (const ch of s) {
            if (isTypeable(ch)) out += ch;
            else if (/\s/.test(ch)) out += ' ';
            // else: dropped entirely (punctuation etc.)
        }
        return out.replace(/\s+/g, ' ').trim();
    }

    // ---------------------------------------------------------------------------
    // Parse the ".osu"-derived type!beat lyric text.
    // ---------------------------------------------------------------------------
    function parseLyricOsu(text) {
        if (text && text.charCodeAt(0) === 0xFEFF) text = text.slice(1); // strip BOM
        const rows = String(text).split(/\r?\n/);
        const general = {}, metadata = {};
        const lyricObjs = [];
        let section = '';

        for (const raw of rows) {
            const t = raw.trim();
            if (t.length === 0) continue;
            if (t[0] === '[' && t[t.length - 1] === ']') { section = t.slice(1, -1); continue; }

            if (section === 'General' || section === 'Metadata') {
                const idx = raw.indexOf(':');
                if (idx >= 0) {
                    const key = raw.slice(0, idx).trim();
                    const val = raw.slice(idx + 1).trim();
                    (section === 'General' ? general : metadata)[key] = val;
                }
            } else if (section === 'Lyrics') {
                try { lyricObjs.push(JSON.parse(t)); } catch (e) { /* tolerate junk */ }
            }
        }

        // The [Lyrics] header is the first object WITHOUT a "text" key. No default
        // granularity — a header-less file derives it from whether lines carry words[].
        let header = { version: 2 };
        let lineObjs = lyricObjs;
        if (lyricObjs.length && !('text' in lyricObjs[0])) {
            header = lyricObjs[0];
            lineObjs = lyricObjs.slice(1);
        }

        const num = (v, d) => { const n = parseFloat(v); return isFinite(n) ? n : d; };
        return {
            audioFilename: general['AudioFilename'] || '',
            audioLeadIn: num(general['AudioLeadIn'], 0),
            previewTime: num(general['PreviewTime'], -1),
            title: metadata['Title'] || metadata['TitleUnicode'] || '',
            artist: metadata['Artist'] || metadata['ArtistUnicode'] || '',
            creator: metadata['Creator'] || '',
            beatmapId: parseInt(metadata['BeatmapID'] || '0', 10) || 0,
            beatmapSetId: parseInt(metadata['BeatmapSetID'] || '0', 10) || 0,
            header,
            lineObjs
        };
    }

    // ---------------------------------------------------------------------------
    // Build the playable beatmap: derive per-line timing (TimingJsonLoader.BuildLines)
    // and expand each line into typeable cells with per-char target times
    // (TypingLine.FromLyricLine).
    // ---------------------------------------------------------------------------
    const CUE_LEAD_MS = 1500;
    const LAST_LINE_TAIL_MS = 3000;
    const MAX_SEAL_GRACE_MS = 700;
    const MIN_BOUNDARY_GRACE_MS = 250;
    const BOUNDARY_EPSILON_MS = 30;
    const LOW_CONFIDENCE_SCORE = 0.15;

    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // Beatmap-level granularity: an explicit header value wins; otherwise Word if any
    // surviving line carries words[], else Line (mirrors LyricBeatmapDecoder.finalise).
    function granularityFor(header, rawLines) {
        const g = String((header && header.granularity) || '').toLowerCase();
        if (g === 'word') return 'Word';
        if (g === 'syllable') return 'Syllable';
        if (g === 'line') return 'Line';
        return rawLines.some(l => l.words.length > 0) ? 'Word' : 'Line';
    }

    // One TimedUnit per whitespace token, from explicit word times clamped into the line
    // (mirrors TimingJsonLoader.buildExplicitUnits).
    function buildExplicitUnits(tokens, words, lineStart, lineEnd) {
        const units = [];
        let prevEnd = lineStart;
        for (let m = 0; m < tokens.length; m++) {
            let ws = clamp(words[m].start, lineStart, lineEnd);
            let we = clamp(words[m].end, ws, lineEnd);
            if (ws < prevEnd) ws = prevEnd; // non-decreasing across units
            if (we < ws) we = ws;
            units.push({ text: tokens[m], start: ws, end: we, conf: clamp(words[m].score, 0, 1) });
            prevEnd = we;
        }
        return units;
    }

    // Per-line fallback: distribute [start, end] over tokens weighted by (typeableCount+1),
    // one interpolated unit per token, confidence 1 (mirrors LrcParser.InterpolateUnits).
    function interpolateUnits(normalizedText, start, end) {
        const units = [];
        if (!normalizedText) return units;
        const tokens = normalizedText.split(' ');
        const weights = tokens.map(t => typeableCount(t) + 1);
        let totalWeight = weights.reduce((a, b) => a + b, 0);
        if (totalWeight <= 0) totalWeight = tokens.length;
        const span = end - start;
        let cumulative = 0;
        for (let i = 0; i < tokens.length; i++) {
            const unitStart = start + span * (cumulative / totalWeight);
            cumulative += weights[i];
            const unitEnd = start + span * (cumulative / totalWeight);
            units.push({ text: tokens[i], start: unitStart, end: unitEnd, conf: 1 });
        }
        return units;
    }

    // Expand a line's tokens+units into per-char cells (mirrors TypingLine.FromLyricLine).
    // After normalization every char is typeable, so the punctuation passes are no-ops here.
    function buildCells(tokens, units, estimated, granularity) {
        const cells = [];
        for (let m = 0; m < tokens.length; m++) {
            const unit = units.length > 0 ? units[Math.min(m, units.length - 1)] : null;
            const unitStart = unit ? unit.start : 0;
            const unitEnd = unit ? unit.end : 0;
            const conf = unit ? unit.conf : 1;
            const tier = (estimated || conf < LOW_CONFIDENCE_SCORE) ? 'Line' : granularity;
            const token = tokens[m];
            const k = token.length;
            for (let j = 0; j < k; j++) {
                cells.push(newCell(token[j], unitStart + j * (unitEnd - unitStart) / k, tier));
            }
            if (m < tokens.length - 1) {
                cells.push(newCell(' ', unitEnd, tier)); // inter-word space cell
            }
        }
        // Guard: targets non-decreasing.
        let run = -Infinity;
        for (const c of cells) { if (c.target < run) c.target = run; else run = c.target; }
        return cells;
    }

    function buildBeatmap(parsed) {
        const header = parsed.header || {};
        const songEndMs = isFinite(header.song_end_ms) ? header.song_end_ms : null;

        // 1) Raw lines: strip backing vocals + normalize; DROP empty-normalized lines (whole-line
        //    backing vocals) so the previous line extends over their span (mirrors TryParseRawLine).
        const raw = [];
        for (const o of parsed.lineObjs) {
            if (o == null || typeof o.text !== 'string') continue;
            const normalized = normalize(o.text);
            if (normalized.length === 0) continue;
            if (!isFinite(+o.start_ms)) continue;

            const startMs = +o.start_ms;
            const endMs = isFinite(+o.end_ms) ? +o.end_ms : startMs;
            const estimated = o.estimated === true;

            const words = [];
            if (Array.isArray(o.words)) {
                for (const w of o.words) {
                    if (w == null || typeof w !== 'object') continue;
                    const ws = isFinite(+w.start_ms) ? +w.start_ms : startMs;
                    const we = isFinite(+w.end_ms) ? +w.end_ms : ws;
                    const score = isFinite(+w.score) ? +w.score : 1;
                    words.push({ text: typeof w.text === 'string' ? w.text : '', start: ws, end: we, score: score });
                }
            }
            const sealGraceMs = isFinite(+o.seal_grace_ms) ? +o.seal_grace_ms : null;
            raw.push({ text: normalized, startMs, endMs, estimated, words, sealGraceMs });
        }

        const granularity = granularityFor(header, raw);
        const lines = [];

        // 2) Resolve per-line boundaries + units (mirrors TimingJsonLoader.BuildLines).
        for (let i = 0; i < raw.length; i++) {
            const line = raw[i];
            const start = line.startMs;

            let endTime;
            if (i < raw.length - 1) endTime = raw[i + 1].startMs;
            else {
                const tailEnd = line.endMs + LAST_LINE_TAIL_MS;
                endTime = songEndMs != null ? Math.min(songEndMs, tailEnd) : tailEnd;
            }
            if (endTime < start) endTime = start;

            const singEndTime = clamp(line.endMs, start, endTime);

            // Seal grace from raw (pre-clamp) word overrun, unless an explicit grace was written.
            let rawWordsEnd = start;
            for (const w of line.words) rawWordsEnd = Math.max(rawWordsEnd, w.start, w.end);
            let sealGrace = line.sealGraceMs != null
                ? clamp(line.sealGraceMs, 0, MAX_SEAL_GRACE_MS)
                : Math.min(Math.max(0, rawWordsEnd - endTime), MAX_SEAL_GRACE_MS);

            const tokens = line.text.split(' ');
            const units = (tokens.length === line.words.length && tokens.length > 0)
                ? buildExplicitUnits(tokens, line.words, start, endTime)
                : interpolateUnits(line.text, start, singEndTime);

            const cells = buildCells(tokens, units, line.estimated, granularity);

            const firstTarget = cells.length ? cells[0].target : start;
            const activationTime = Math.max(start, firstTarget - CUE_LEAD_MS);

            // Boundary bump: a last cell on the seal boundary gets a minimum finish window.
            let grace = sealGrace;
            if (cells.length && cells[cells.length - 1].target >= endTime - BOUNDARY_EPSILON_MS) {
                grace = Math.max(grace, MIN_BOUNDARY_GRACE_MS);
            }
            grace = Math.min(grace, MAX_SEAL_GRACE_MS);

            lines.push({
                index: i,
                text: line.text,
                startTime: start,
                endTime: endTime,
                singEndTime: singEndTime,
                activationTime: activationTime,
                sealGraceMs: grace,
                estimated: line.estimated,
                cells: cells
            });
        }

        return {
            title: parsed.title,
            artist: parsed.artist,
            creator: parsed.creator,
            beatmapId: parsed.beatmapId,
            granularity: granularity,
            audioFilename: parsed.audioFilename,
            lines: lines,
            totalCells: lines.reduce((n, l) => n + l.cells.length, 0)
        };
    }

    function newCell(expected, target, tier) {
        return {
            expected: expected,
            target: target,
            tier: tier,
            typeable: true,          // after normalization every cell is typeable
            state: 'untyped',        // untyped | correct | missed
            judgeType: null,         // Perfect | Good | Ok | Premature | Lagging | Miss
            typedChar: null,
            judgedDelta: null,
            firstCorrectDelta: null
        };
    }

    // ---------------------------------------------------------------------------
    // Judgement windows (Judgement.cs SyncWindows).
    // ---------------------------------------------------------------------------
    const BASE_WINDOWS = { pe: 250, pl: 400, ge: 600, gl: 1000, oe: 1200, ol: 2000 };
    const TIER_SCALE = { Line: 1.0, Word: 0.6, Syllable: 0.45 };

    function windowsFor(tier) {
        const s = TIER_SCALE[tier] != null ? TIER_SCALE[tier] : 1.0;
        return { pe: BASE_WINDOWS.pe * s, pl: BASE_WINDOWS.pl * s, ge: BASE_WINDOWS.ge * s, gl: BASE_WINDOWS.gl * s, oe: BASE_WINDOWS.oe * s, ol: BASE_WINDOWS.ol * s };
    }

    function classify(delta, w) {
        if (delta >= -w.pe && delta <= w.pl) return 'Perfect';
        if (delta >= -w.ge && delta <= w.gl) return 'Good';
        if (delta >= -w.oe && delta <= w.ol) return 'Ok';
        return delta < -w.oe ? 'Premature' : 'Lagging';
    }

    function basePoints(type) {
        return type === 'Perfect' ? 300 : type === 'Good' ? 150 : type === 'Ok' ? 50 : 0;
    }

    // Engine judgement -> osu HitResult (DrawableTypeBeatHitObject.toHitResult).
    function toHitResult(judgeType) {
        switch (judgeType) {
            case 'Perfect': return 'great';
            case 'Good': return 'ok';
            case 'Ok': return 'meh';
            default: return 'miss'; // Premature, Lagging, Miss, or untyped
        }
    }

    // ---------------------------------------------------------------------------
    // TypingEngine — the frame-driven gameplay/judgement core.
    // ---------------------------------------------------------------------------
    const COMBO_CAP = 50;
    const WRONG_KEY_FAIL_STREAK = 13;

    class TypingEngine {
        constructor(beatmap) {
            this.beatmap = beatmap;
            this.lines = beatmap.lines;
            // Cells carry play state on the (shared) beatmap, so a fresh engine — e.g.
            // "play again" reusing the same beatmap — must clear it first.
            for (const line of this.lines) {
                for (const c of line.cells) {
                    c.state = 'untyped';
                    c.judgeType = null;
                    c.typedChar = null;
                    c.judgedDelta = null;
                    c.firstCorrectDelta = null;
                }
            }
            this.activeLineIndex = -1;
            this.caretIndex = 0;
            this.nextSealIndex = 0;
            this.combo = 0;
            this.maxCombo = 0;
            this.score = 0;
            this.totalKeypresses = 0;
            this.correctKeypresses = 0;
            this.errorCount = 0;
            this.consecutiveWrongKeys = 0;
            this.activeTimeMs = 0;
            this.lastUpdateTime = null;
            this.finished = false;
            this.failed = false;
            this.counts = {};                 // JudgementType -> count (scored only)
            // event hooks (optional; set by the renderer)
            this.onCharJudged = null;
            this.onWrongKey = null;
            this.onComboBroken = null;
            this.onFinished = null;
            this.onFailed = null;
        }

        get health() { return Math.max(0, 1 - this.consecutiveWrongKeys / WRONG_KEY_FAIL_STREAK); }

        get liveAccuracy() { return this.totalKeypresses > 0 ? this.correctKeypresses / this.totalKeypresses : 1; }

        countCorrectCells() {
            let n = 0;
            for (const line of this.lines) for (const c of line.cells) if (c.state === 'correct') n++;
            return n;
        }

        get liveWpm() {
            return this.activeTimeMs <= 0 ? 0 : (this.countCorrectCells() / 5.0) / (this.activeTimeMs / 60000.0);
        }

        isLineComplete(idx) {
            const line = this.lines[idx];
            return this.caretIndex >= line.cells.length;
        }

        noTypeableUntyped(line) {
            for (const c of line.cells) if (c.typeable && c.state === 'untyped') return false;
            return true;
        }

        canSeal(line, time) {
            return time >= line.endTime && (time >= line.endTime + line.sealGraceMs || this.noTypeableUntyped(line));
        }

        sealLine(idx) {
            const line = this.lines[idx];
            let missed = 0;
            for (const c of line.cells) {
                // A cell that was ever typed correctly keeps its first result (firstCorrectDelta
                // stands, even if later backspaced) — only never-correct cells seal as Miss.
                if (c.typeable && c.state === 'untyped' && c.firstCorrectDelta === null) {
                    c.state = 'missed';
                    c.judgeType = 'Miss';
                    missed++;
                }
            }
            if (missed > 0) {
                this.combo = 0;
                if (this.onComboBroken) this.onComboBroken();
            }
        }

        autoSkipForward() {
            if (this.activeLineIndex < 0) return;
            const cells = this.lines[this.activeLineIndex].cells;
            while (this.caretIndex < cells.length && !cells[this.caretIndex].typeable) {
                cells[this.caretIndex].state = 'autoskip';
                this.caretIndex++;
            }
        }

        update(time) {
            // (1) accrue active typing time using this frame's span.
            if (this.lastUpdateTime !== null && this.activeLineIndex >= 0 && !this.finished && !this.isLineComplete(this.activeLineIndex)) {
                this.activeTimeMs += Math.max(0, time - this.lastUpdateTime);
            }
            this.lastUpdateTime = time;

            // (2) seal every line whose deadline passed, in order.
            while (this.nextSealIndex < this.lines.length && this.canSeal(this.lines[this.nextSealIndex], time)) {
                this.sealLine(this.nextSealIndex);
                if (this.activeLineIndex === this.nextSealIndex) this.activeLineIndex = -1;
                this.nextSealIndex++;
            }

            // (3) finish, or activate the next line inside its cue window.
            if (this.nextSealIndex >= this.lines.length) {
                if (!this.finished) {
                    this.finished = true;
                    if (this.onFinished) this.onFinished();
                }
                return;
            }
            if (this.activeLineIndex < 0) {
                const cand = this.lines[this.nextSealIndex];
                if (time >= cand.activationTime && time < cand.endTime + cand.sealGraceMs) {
                    this.activeLineIndex = this.nextSealIndex;
                    this.caretIndex = 0;
                    this.autoSkipForward();
                }
            }
        }

        processKey(c, time) {
            if (this.finished || this.failed) return false;
            if (this.activeLineIndex < 0) return false; // dead zone / pre-roll: harmless
            const line = this.lines[this.activeLineIndex];
            this.autoSkipForward();
            if (this.caretIndex >= line.cells.length) return false; // line fully typed
            const cell = line.cells[this.caretIndex];
            const delta = time - cell.target;
            const matched = fold(c) === fold(cell.expected);

            if (!matched) {
                // Wrong key — REJECTED. Costs a keypress + combo + streak; caret unmoved.
                this.totalKeypresses++;
                this.errorCount++;
                this.consecutiveWrongKeys++;
                this.combo = 0;
                this.counts.WrongChar = (this.counts.WrongChar || 0) + 1;
                if (this.onComboBroken) this.onComboBroken();
                if (this.onWrongKey) this.onWrongKey(c, this.caretIndex);
                if (this.consecutiveWrongKeys >= WRONG_KEY_FAIL_STREAK) {
                    this.failed = true;
                    if (this.onFailed) this.onFailed();
                }
                return true;
            }

            this.consecutiveWrongKeys = 0;

            const w = windowsFor(cell.tier);
            const inertRetype = cell.firstCorrectDelta !== null;
            let type, points = 0;

            if (inertRetype) {
                const d = cell.firstCorrectDelta;
                type = classify(d, w);
                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = d;
                cell.judgeType = type;
            } else {
                this.totalKeypresses++;
                this.correctKeypresses++;
                type = classify(delta, w);
                const bp = basePoints(type);
                if (bp > 0) {
                    points = Math.round(bp * (1 + Math.min(this.combo, COMBO_CAP) / COMBO_CAP));
                    this.score += points;
                    this.combo++;
                    if (this.combo > this.maxCombo) this.maxCombo = this.combo;
                } else {
                    // right char, wrong time — Premature/Lagging: no points, combo breaks.
                    this.combo = 0;
                    if (this.onComboBroken) this.onComboBroken();
                }
                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = delta;
                cell.firstCorrectDelta = delta;
                cell.judgeType = type;
                this.counts[type] = (this.counts[type] || 0) + 1;
            }

            const judgedIndex = this.caretIndex;
            this.caretIndex++;
            this.autoSkipForward();
            if (this.onCharJudged) this.onCharJudged(judgedIndex, type, points);
            return true;
        }

        processBackspace() {
            if (this.activeLineIndex < 0) return;
            const cells = this.lines[this.activeLineIndex].cells;
            let i = this.caretIndex - 1;
            while (i >= 0 && cells[i].state === 'autoskip') { cells[i].state = 'untyped'; i--; }
            if (i < 0) { this.caretIndex = 0; return; }
            const cell = cells[i];
            cell.state = 'untyped';
            cell.typedChar = null;
            cell.judgedDelta = null;
            cell.judgeType = null;
            // firstCorrectDelta intentionally retained (inert-retype guard).
            this.caretIndex = i;
        }

        // The character to type right now (or null).
        get caretCell() {
            if (this.activeLineIndex < 0) return null;
            const cells = this.lines[this.activeLineIndex].cells;
            return this.caretIndex < cells.length ? cells[this.caretIndex] : null;
        }
    }

    // ---------------------------------------------------------------------------
    // Scoring — standardised total_score + statistics dicts + completion rank.
    // Reproduces osu ScoreProcessor.ComputeTotalScore and the completion cutoffs.
    // ---------------------------------------------------------------------------
    const COMPLETION_CUTOFFS = [[1.0, 'X'], [0.95, 'S'], [0.90, 'A'], [0.80, 'B'], [0.70, 'C']];

    function rankFromCompletion(completion) {
        for (const [cut, r] of COMPLETION_CUTOFFS) if (completion >= cut) return r;
        return 'D';
    }

    function computeScore(engine) {
        const beatmap = engine.beatmap;
        const total = beatmap.totalCells;

        // Per-cell osu result, in order. The FIRST correct judgement stands for a cell — even
        // if later backspaced (firstCorrectDelta), matching the drawable's "first result stands".
        // A rejected wrong key never produced a cell result, so it never appears here.
        const results = [];
        for (const line of engine.lines) {
            for (const cell of line.cells) {
                if (!cell.typeable) continue;
                if (cell.firstCorrectDelta !== null) {
                    results.push(toHitResult(classify(cell.firstCorrectDelta, windowsFor(cell.tier))));
                } else if (cell.state === 'missed') {
                    results.push('miss');
                } else {
                    results.push(null); // never judged (unreached on a failed run)
                }
            }
        }

        let great = 0, ok = 0, meh = 0, miss = 0;
        for (const r of results) {
            if (r === 'great') great++;
            else if (r === 'ok') ok++;
            else if (r === 'meh') meh++;
            else if (r === 'miss') miss++;
        }
        const judged = great + ok + meh + miss;

        // Whole-map accuracy (for display/completion); server overrides the submitted value.
        const acc = total > 0 ? (300 * great + 100 * ok + 50 * meh) / (300 * total) : 1;

        // Combo portions + max combo from the ordered result stream. Combo breaks only on a
        // Miss cell (never on a rejected wrong key — those aren't in `results`); unreached (null)
        // cells contribute nothing. maxComboPortion is the whole-map (all-Great) maximum.
        let combo = 0, comboPortion = 0, maxComboCounter = 0, maxComboPortion = 0, maxCombo = 0;
        for (const r of results) {
            maxComboCounter++;
            maxComboPortion += 300 * Math.sqrt(maxComboCounter);
            if (r === 'great' || r === 'ok' || r === 'meh') {
                combo++;
                if (combo > maxCombo) maxCombo = combo;
                comboPortion += 300 * Math.sqrt(combo);
            } else if (r === 'miss') {
                combo = 0;
            }
            // null (unreached): leave combo as-is; it accrues no portion.
        }
        const comboProgress = maxComboPortion > 0 ? comboPortion / maxComboPortion : 1;
        const accuracyProgress = total > 0 ? judged / total : 1;

        // total_score uses the JUDGED-only accuracy denominator (ScoreProcessor.Accuracy =
        // currentBaseScore / currentMaximumBaseScore, judged cells only) — equals whole-map
        // accuracy for a completed play, differs only for a failed/incomplete (unranked) run.
        const accJudged = judged > 0 ? (300 * great + 100 * ok + 50 * meh) / (300 * judged) : 1;
        const totalWithoutMods = Math.round(500000 * accJudged * comboProgress + 500000 * Math.pow(accJudged, 5) * accuracyProgress);
        const totalScore = totalWithoutMods; // scoreMultiplier = 1 (no mods)

        const completion = total > 0 ? (great + ok + meh) / total : 1;
        const passed = engine.finished && !engine.failed;

        const statistics = {};
        if (great) statistics.great = great;
        if (ok) statistics.ok = ok;
        if (meh) statistics.meh = meh;
        if (miss) statistics.miss = miss;

        return {
            passed: passed,
            totalScore: totalScore,
            totalScoreWithoutMods: totalWithoutMods,
            accuracy: acc,
            maxCombo: maxCombo,
            completion: completion,
            rank: passed ? rankFromCompletion(completion) : 'F',
            statistics: statistics,
            maximumStatistics: { great: total },
            // convenience for the results screen
            counts: { great, ok, meh, miss },
            wpm: engine.liveWpm
        };
    }

    global.TypeBeatCore = {
        // low-level
        isTypeable, normalize, parseLyricOsu, buildBeatmap,
        TypingEngine, computeScore, rankFromCompletion,
        windowsFor, classify, toHitResult,
        constants: { CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, LOW_CONFIDENCE_SCORE },
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
