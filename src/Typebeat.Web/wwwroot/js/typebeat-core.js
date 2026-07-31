/*
 * typebeat-core.js: a faithful, dependency-free reimplementation of the
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
 * Vanilla JS on purpose, no framework, no bundler (matches the rest of the site).
 * Exposes window.TypeBeatCore.
 */
(function (global) {
    'use strict';

    // ---------------------------------------------------------------------------
    // Typeability: text normalization (mirrors Typeability in LyricBeatmap.cs).
    // ---------------------------------------------------------------------------
    function isTypeable(ch) {
        return ch === ' ' ||
            (ch >= 'a' && ch <= 'z') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9');
    }

    // Authoring marker for a FREESTYLE character: a cell the player may satisfy with ANY key but
    // space, whose typed char is then displayed for the rest of the play. Deliberately OUTSIDE
    // isTypeable, that is what keeps it invisible to every legacy path (normalize strips it
    // unless the caller explicitly opts in).
    const FREESTYLE_MARKER = '&';

    // The char an automated player presses on a freestyle cell; also what the Mashing mod
    // substitutes when a space lands on one (mirrors Typeability.FREESTYLE_AUTO_CHAR).
    const FREESTYLE_AUTO_CHAR = 'a';

    function isFreestyle(ch) { return ch === FREESTYLE_MARKER; }

    // The punctuation type!beat supports inside an authored lyric line, defined ONCE here (mirrors
    // Typeability.PUNCTUATION): comma, period, apostrophe, hyphen, question mark, exclamation mark,
    // semicolon, colon, round brackets, square brackets, straight double quote.
    //
    // A map stores the AUTHOR'S form: punctuated and case-sensitive. What the player types (and
    // sees) is derived from it: verbatim under the desktop client's LITERATE mod, and through
    // toDefaultStream otherwise. Deliberately outside isTypeable, so a mark never counts as a plain
    // typeable char for the interpolation weights or the cell counts.
    const PUNCTUATION = ",.'-?!;:()[]\"";

    // The one supported mark that reads as a WORD BREAK rather than as decoration: without
    // Literate, "bad-cat" is typed "bad cat", not "badcat" (mirrors Typeability.WORD_BREAK).
    const WORD_BREAK = '-';

    function isPunctuation(ch) { return PUNCTUATION.indexOf(ch) >= 0; }

    // A char that occupies a typeable CELL: a normal typeable char, or a freestyle slot. This is
    // what the line flattening and the text statistics count; isTypeable stays the narrower
    // "this exact glyph must be typed" predicate (mirrors Typeability.IsCell).
    function isCell(ch) { return isTypeable(ch) || isFreestyle(ch); }

    function fold(ch) {
        return ch.toLowerCase();
    }

    // Remove bracketed backing-vocal spans, "(...)" and "[...]", with a shared depth
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

    // Count the cells in a token (typeable chars plus freestyle slots) for interpolation weights.
    // Identical to the historical typeable-only count for any default-normalized text, which
    // carries no markers (mirrors Typeability.TypeableCount).
    function typeableCount(text) {
        let n = 0;
        for (const c of text) if (isCell(c)) n++;
        return n;
    }

    // NFD-fold diacritics, map curly quotes/apostrophes and en/em dashes to their ASCII forms, KEEP
    // the supported punctuation, DROP every other non-space char that isn't typeable, collapse
    // whitespace, trim (mirrors Typeability.Normalize).
    //
    // The result is the AUTHOR'S form of the line: original case, supported marks intact. It is
    // what a map stores. It is NOT what the player types here: the browser always plays vanilla,
    // and buildCells derives the default stream from it (see projectDefault).
    //
    // keepFreestyleMarkers additionally preserves FREESTYLE_MARKERs, which are otherwise stripped
    // like any other unsupported char. The only caller that opts in is the decoder of a line
    // the map explicitly flagged ("freestyle": true), so an ampersand that merely occurs in a
    // song's lyrics ("R&B") still disappears exactly as it always has.
    function normalize(s, keepFreestyleMarkers = false) {
        if (s == null) return '';
        s = stripBackingVocals(s);
        s = s.normalize('NFD').replace(/[̀-ͯ]/g, '');
        // The variant sets are the C# switch verbatim (U+2018 U+2019 U+201A U+2032 /
        // U+201C U+201D U+201E U+2033 / U+2013 U+2014 U+2015 U+2212). They were allowed to drift
        // while an unmapped variant was dropped as untypeable on both sides either way; now that
        // the ASCII forms SURVIVE normalization, a missing variant is a real divergence.
        s = s
            .replace(/[‘’‚′]/g, "'")
            .replace(/[“”„″]/g, '"')
            .replace(/[–—―−]/g, '-')
            .replace(/ /g, ' ');
        let out = '';
        for (const ch of s) {
            if (isTypeable(ch)) out += ch;
            else if (/\s/.test(ch)) out += ' ';
            else if (isPunctuation(ch)) out += ch;
            else if (keepFreestyleMarkers && isFreestyle(ch)) out += ch;
            // else: dropped entirely (unsupported punctuation etc.)
        }
        return out.replace(/\s+/g, ' ').trim();
    }

    // The DEFAULT (no-Literate) typed char for one authored char, or null when the default stream
    // deletes it (mirrors Typeability.DefaultChar): WORD_BREAK becomes a SPACE, every other
    // supported mark disappears, everything else folds to lower case.
    function defaultChar(ch) {
        if (ch === WORD_BREAK) return ' ';
        if (isPunctuation(ch)) return null;
        return fold(ch);
    }

    // THE derivation (mirrors Typeability.ProjectDefault): projects an authored line onto the
    // DEFAULT typed stream, returning { text, sources } where sources[i] is the index in raw the
    // i-th output char came from (so a cell keeps the timing slot of the authored char behind it).
    //
    // Spaces are handled a RUN at a time (consecutive space-producing chars, authored spaces and
    // hyphens alike, with deleted marks skipped over). A run with NO hyphen in it is emitted
    // verbatim, space for space, which is what makes the projection exactly toLowerCase for every
    // hyphen-free, mark-free line, i.e. every line of every map written before punctuation existed;
    // the default path cannot have moved under them. A run that DOES contain a hyphen collapses to
    // one space ("a - b" is "a b"), and to none at either end of the line ("-a-" is "a"), where it
    // would separate nothing. A collapsed run reports its FIRST space-producing index.
    function projectDefault(raw) {
        const out = { text: '', sources: [] };
        if (!raw) return out;

        let wroteAny = false;
        let i = 0;

        while (i < raw.length) {
            const c = defaultChar(raw[i]);
            if (c === null) { i++; continue; }

            if (c !== ' ') {
                out.text += c;
                out.sources.push(i);
                wroteAny = true;
                i++;
                continue;
            }

            let hasBreak = false;
            let firstSpace = -1;
            let end = i;

            while (end < raw.length) {
                const d = defaultChar(raw[end]);
                if (d === null) { end++; continue; } // a deleted mark inside the run does not end it
                if (d !== ' ') break;
                if (raw[end] === WORD_BREAK) hasBreak = true;
                if (firstSpace < 0) firstSpace = end;
                end++;
            }

            if (!hasBreak) {
                for (let k = i; k < end; k++) {
                    if (defaultChar(raw[k]) !== ' ') continue;
                    out.text += ' ';
                    out.sources.push(k);
                    wroteAny = true;
                }
            } else if (wroteAny && end < raw.length) {
                out.text += ' ';
                out.sources.push(firstSpace);
            }

            i = end;
        }

        return out;
    }

    // The DEFAULT (no-Literate) typed stream of an authored line: lower-cased, hyphens turned into
    // word breaks, every other supported mark deleted (mirrors Typeability.ToDefaultStream).
    // "The bad-cat sat." becomes "the bad cat sat".
    function toDefaultStream(raw) { return projectDefault(raw).text; }

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
        // granularity; a header-less file derives it from whether lines carry words[].
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
            // Keep only subdivisions that stayed strictly inside the (possibly clamped) word,
            // deduped + sorted ascending (mirrors TimingJsonLoader.buildExplicitUnits).
            const boundaries = [];
            for (const b of (words[m].syllables || [])) {
                if (b > ws && b < we && boundaries.indexOf(b) < 0) boundaries.push(b);
            }
            boundaries.sort((a, b) => a - b);
            units.push({ text: tokens[m], start: ws, end: we, conf: clamp(words[m].score, 0, 1), syllables: boundaries });
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
            units.push({ text: tokens[i], start: unitStart, end: unitEnd, conf: 1, syllables: EMPTY_BOUNDARIES });
        }
        return units;
    }

    const EMPTY_BOUNDARIES = [];

    // Target time of typeable char j (0-based, of k in the word) under piecewise-linear syllable
    // timing (mirrors TypingLine.syllableCharTarget). The word spans [unitStart, unitEnd]; each
    // entry of boundaries (absolute ms, strictly inside, ascending) splits it into one more
    // segment, and the k chars are distributed evenly by index across the segments. With no
    // boundaries this is exactly unitStart + j*(unitEnd-unitStart)/k; char j = 0 lands on unitStart.
    function syllableCharTarget(unitStart, unitEnd, boundaries, k, j) {
        if (k <= 0) return unitStart;
        if (boundaries.length === 0) return unitStart + j * (unitEnd - unitStart) / k;

        const segments = boundaries.length + 1;

        // Which segment holds char index j (floor of j scaled into segment-space), clamped to last.
        let s = Math.floor(j * segments / k);
        if (s >= segments) s = segments - 1;

        const segIndexLo = s * k / segments;
        const segIndexHi = (s + 1) * k / segments;
        const timeLo = s === 0 ? unitStart : boundaries[s - 1];
        const timeHi = s === segments - 1 ? unitEnd : boundaries[s];

        if (segIndexHi <= segIndexLo) return timeLo;

        return timeLo + (j - segIndexLo) / (segIndexHi - segIndexLo) * (timeHi - timeLo);
    }

    // Expand a line's AUTHORED text + units into per-char cells (mirrors TypingLine.FromLyricLine
    // exactly; this is the scoring-fidelity seam, so it is written pass for pass).
    //
    // literate selects WHICH STREAM the cells carry. Off (the only mode the browser player uses,
    // see buildBeatmap): the cells are projectDefault of the authored text, so capitals fold, a
    // hyphen becomes a typed space and every other supported mark is gone. On: one cell per
    // authored char, marks and capitals included, every one of them typeable.
    //
    // Letter timings are IDENTICAL either way: the per-word char spread counts only isCell chars
    // (never punctuation), so the mod adds cells without moving any of the existing ones.
    function buildCells(text, units, estimated, granularity, literate) {
        const n = text.length;
        const expected = new Array(n);
        const typeableFlags = new Array(n).fill(false);
        const targets = new Array(n).fill(null);
        const tiers = new Array(n).fill(granularity);

        // Pass 1: walk the authored text token by token (spaces delimit tokens; token m maps to
        // units[m]). Punctuation is placed but left UNTIMED, and is excluded from k, so adding a
        // mark to a word never moves the letters around it.
        const tokens = text.split(' ');
        let pos = 0;

        for (let m = 0; m < tokens.length; m++) {
            const unit = units.length > 0 ? units[Math.min(m, units.length - 1)] : null;
            const unitStart = unit ? unit.start : 0;
            const unitEnd = unit ? unit.end : 0;
            const conf = unit ? unit.conf : 1;
            const boundaries = (unit && unit.syllables) ? unit.syllables : EMPTY_BOUNDARIES;
            const tier = (estimated || conf < LOW_CONFIDENCE_SCORE) ? 'Line' : granularity;
            const token = tokens[m];

            // k = number of cells in this token, freestyle slots included (the player presses a key
            // for them, so they take a share of the word's time like any letter), marks excluded.
            let k = 0;
            for (let t = 0; t < token.length; t++) if (isCell(token[t])) k++;

            let j = 0;
            for (let t = 0; t < token.length; t++) {
                const ch = token[t];
                expected[pos] = ch;
                tiers[pos] = tier;
                if (isCell(ch)) {
                    typeableFlags[pos] = true;
                    targets[pos] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j);
                    j++;
                }
                pos++;
            }

            if (m < tokens.length - 1) {
                expected[pos] = ' '; // inter-word space cell: preceding unit's end
                typeableFlags[pos] = true;
                targets[pos] = unitEnd;
                tiers[pos] = tier;
                pos++;
            }
        }

        // Pass 2: time the chars pass 1 left untimed, which after normalize are exactly the
        // supported marks. A run of them between two timed chars is spread EVENLY across the gap
        // (mark m of a run of len takes prev + (m+1)*(next-prev)/(len+1)); a run with nothing after
        // it attaches to the PRECEDING char's target, one with nothing before it to the FOLLOWING.
        for (let i = 0; i < n; i++) {
            if (targets[i] !== null) continue;

            let end = i;
            while (end + 1 < n && targets[end + 1] === null) end++;

            const before = i > 0 ? targets[i - 1] : null;
            const after = end + 1 < n ? targets[end + 1] : null;
            const len = end - i + 1;

            for (let q = 0; q < len; q++) {
                targets[i + q] = before !== null
                    ? (after !== null ? before + (q + 1) * (after - before) / (len + 1) : before)
                    : (after !== null ? after : 0);
            }

            i = end;
        }

        // Guard: targets non-decreasing (clamp to previous if data is inverted).
        for (let i = 1; i < n; i++) {
            if (targets[i] < targets[i - 1]) targets[i] = targets[i - 1];
        }

        const cells = [];

        if (literate) {
            // One cell per authored char, all of them typed. A mark is a first-class typeable cell.
            for (let i = 0; i < n; i++) {
                cells.push(newCell(expected[i], targets[i], tiers[i], typeableFlags[i] || isPunctuation(expected[i])));
            }
        } else {
            // The default stream. Each surviving char keeps the timing and judge tier of the
            // authored char it came from, so a hyphen-turned-space lands on the interpolated slot
            // the hyphen held between the two letters it separated.
            const projected = projectDefault(text);
            for (let i = 0; i < projected.text.length; i++) {
                const src = projected.sources[i];
                const ch = projected.text[i];
                cells.push(newCell(ch, targets[src], tiers[src], isCell(ch)));
            }
        }

        return cells;
    }

    // literate mirrors the desktop client's Literate mod: the cells become the authored line
    // verbatim (marks and capitals typed) rather than the derived default stream. The browser
    // player NEVER passes it (see play.js / mountPlayer): /play is always vanilla. It exists so
    // buildCells stays a line-for-line mirror of TypingLine.FromLyricLine and so the JS-vs-C#
    // fidelity harness can pin both branches, exactly as engine.caseSensitive already does.
    function buildBeatmap(parsed, literate = false) {
        const header = parsed.header || {};
        const songEndMs = isFinite(header.song_end_ms) ? header.song_end_ms : null;

        // 1) Raw lines: strip backing vocals + normalize; DROP empty-normalized lines (whole-line
        //    backing vocals) so the previous line extends over their span (mirrors TryParseRawLine).
        const raw = [];
        for (const o of parsed.lineObjs) {
            if (o == null || typeof o.text !== 'string') continue;
            // Opt-in freestyle authoring (type!beat editor extension, mirrors TryParseRawLine):
            // "freestyle": true declares that the ampersands in this line's text are FREESTYLE CELL
            // markers rather than lyric punctuation. The flag must be present AND literally true;
            // without it the text normalizes exactly as it always has (ampersands stripped), so
            // every map produced before this feature, and every line whose lyrics genuinely contain
            // "&", decodes unchanged.
            const freestyle = o.freestyle === true;
            const normalized = normalize(o.text, freestyle);
            // A line with nothing to TYPE is dropped, and the previous line extends over its span.
            // Tested on the DEFAULT stream, not on the normalized text, because a line that is
            // nothing but punctuation ("...") now normalizes non-empty yet still gives the player no
            // cell at all. The two conditions coincide exactly for every other input, so this is the
            // same rule the parser has always applied (mirrors LyricTiming.TryParseRawLine).
            if (toDefaultStream(normalized).length === 0) continue;
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
                    // Optional syllable subdivisions: each syllable's start_ms strictly inside the
                    // (raw) word becomes an internal boundary; the first syllable starts at the
                    // word start so it contributes none (mirrors TimingJsonLoader.parseLine).
                    const syllables = [];
                    if (Array.isArray(w.syllables)) {
                        for (const s of w.syllables) {
                            if (s == null || typeof s !== 'object') continue;
                            const sm = +s.start_ms;
                            if (isFinite(sm) && sm > ws && sm < we) syllables.push(sm);
                        }
                    }
                    words.push({ text: typeof w.text === 'string' ? w.text : '', start: ws, end: we, score: score, syllables: syllables });
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

            const cells = buildCells(line.text, units, line.estimated, granularity, literate);

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

    function newCell(expected, target, tier, typeable) {
        return {
            expected: expected,
            target: target,
            tier: tier,
            // Normally true: every char of the default stream is typeable, and under Literate every
            // authored char is. False only for a char outside both the typeable surface and the
            // supported marks, which normalize strips, so it is the defensive auto-skip path.
            typeable: typeable !== false,
            // FREESTYLE cell: any key but space satisfies it and the char the player actually
            // pressed lands in typedChar and stays on screen. Judgement is otherwise a completely
            // normal typeable cell (same windows, points, combo, completion), and a space is
            // rejected exactly as a wrong key on any other cell is. Mirrors TypingCell.IsFreestyle,
            // which is (IsTypeable && marker) and so is exactly this here.
            freestyle: isFreestyle(expected),
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
    // TypingEngine: the frame-driven gameplay/judgement core.
    // ---------------------------------------------------------------------------
    const COMBO_CAP = 50;
    const WRONG_KEY_FAIL_STREAK = 13;

    class TypingEngine {
        constructor(beatmap) {
            this.beatmap = beatmap;
            this.lines = beatmap.lines;
            // Cells carry play state on the (shared) beatmap, so a fresh engine, e.g.
            // "play again" reusing the same beatmap, must clear it first.
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
            // Mods (TypingEngine.CaseSensitive / MashingEnabled). The browser player always plays
            // vanilla and never turns these on; they exist so processKey stays a line-for-line
            // mirror of the C# and so the freestyle exemptions in it are pinned by the JS-vs-C#
            // fidelity harness. Both default off, i.e. today's exact behaviour. Exposing either in
            // the UI would also mean wiring the submitted mods payload (both are unranked).
            this.caseSensitive = false;       // Literate: the typed char must match the exact case
            this.mashingEnabled = false;      // Mashing (Relax): any key is the right key
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
                // stands, even if later backspaced); only never-correct cells seal as Miss.
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

            // Mashing mod: any key is the right key; judge it as the caret cell's expected char.
            // A FREESTYLE cell is exempt: it already accepts any key, and rewriting c here would
            // stamp the authoring marker over the char the player actually pressed (the one thing
            // a freestyle cell must remember). Space is the single exception to that exemption: a
            // freestyle cell REJECTS space (see the match below), so mashing's "any key is the
            // right key" promise needs a substitute to hand it, and the char an automated player
            // presses into a freestyle slot is the canonical one.
            if (this.mashingEnabled) {
                if (!cell.freestyle) c = cell.expected;
                else if (c === ' ') c = FREESTYLE_AUTO_CHAR;
            }

            const delta = time - cell.target;
            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule is bypassed for it). The press is then judged exactly
            // like a correct char: same windows, points, combo, accuracy and completion, with the
            // pressed char kept in typedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is rejected exactly as a wrong key on any other cell would be.
            const matched = (cell.freestyle && c !== ' ') ||
                (this.caseSensitive ? c === cell.expected : fold(c) === fold(cell.expected));

            if (!matched) {
                // Wrong key, REJECTED. Costs a keypress + combo + streak; caret unmoved.
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
                    // right char, wrong time: Premature/Lagging, no points, combo breaks.
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
    // Scoring: standardised total_score + statistics dicts + completion rank.
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

        // Per-cell osu result, in order. The FIRST correct judgement stands for a cell, even
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
        // Miss cell (never on a rejected wrong key; those aren't in `results`); unreached (null)
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
        // currentBaseScore / currentMaximumBaseScore, judged cells only); equals whole-map
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

    // ---------------------------------------------------------------------------
    // Freestyle shimmer (mirrors FreestyleGlyphs). Purely cosmetic: the engine neither
    // knows nor cares which glyph is on screen. An open freestyle cell shows a DIFFERENT
    // glyph every tick, drawn from a pool that shares the cell's advance width, which is
    // what stops the line jittering as the glyph changes. The site renders lyrics in
    // JetBrains Mono, so every candidate already shares an advance and the pool is the
    // full candidate list (the C# FIXED_WIDTH_POOL case; no measuring needed).
    // Deterministic in (tick, position), so it needs no random source and is testable.
    // ---------------------------------------------------------------------------
    const FREESTYLE_CANDIDATES = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    const FREESTYLE_POOL = FREESTYLE_CANDIDATES.split('');
    const SHIMMER_INTERVAL_MS = 60;

    // Tick index for a clock time; the shimmer advances with the gameplay clock rather than wall
    // time, so a paused clock holds a stable glyph.
    function freestyleTick(timeMs) { return Math.floor(timeMs / SHIMMER_INTERVAL_MS); }

    // The glyph an open freestyle slot shows on `tick`. `position` (the cell index) decorrelates
    // neighbouring slots. Same 32-bit mix as FreestyleGlyphs.Glyph (Math.imul is the unchecked
    // uint multiply), so the browser shimmers through the identical sequence as the desktop client.
    function freestyleGlyph(tick, position) {
        let h = (Math.imul(tick | 0, 2654435761) ^ Math.imul((position | 0) + 1, 2246822519)) >>> 0;
        h = (h ^ (h >>> 15)) >>> 0;
        h = Math.imul(h, 2654435761) >>> 0;
        h = (h ^ (h >>> 13)) >>> 0;
        return FREESTYLE_POOL[h % FREESTYLE_POOL.length];
    }

    global.TypeBeatCore = {
        // low-level
        isTypeable, isFreestyle, isCell, isPunctuation, normalize,
        defaultChar, projectDefault, toDefaultStream,
        parseLyricOsu, buildBeatmap, syllableCharTarget,
        TypingEngine, computeScore, rankFromCompletion,
        windowsFor, classify, toHitResult,
        freestyleTick, freestyleGlyph,
        constants: { CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, LOW_CONFIDENCE_SCORE, FREESTYLE_MARKER, SHIMMER_INTERVAL_MS, PUNCTUATION, WORD_BREAK },
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
