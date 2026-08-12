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

    // ---------------------------------------------------------------------------
    // The CHARACTER-DISTANCE axis (mirrors TypingLine's judgeTargets / cellPositions /
    // extrapolationSpacingMs, backlog 133). Judgement measures how many CHARACTERS a keypress is
    // from the character the playhead is on, so every line carries the axis that question is asked
    // on: its TYPEABLE cells' targets in display order, where index k is "the k'th character of
    // this line".
    //
    // SPACES are on the axis on purpose. A word gap is a cell with a target the playhead crosses,
    // so leaving it out would put a hole in the axis and make the interpolation jump. It is
    // deliberately NOT the countable stream (typeable and not a space), which measures a keypress
    // BUDGET for other questions and has a different right answer.
    //
    // The axis is PER LINE, and that is why its ends need extrapolating. A map-wide axis would
    // bracket a press made during the cue lead between the PREVIOUS line's last character and this
    // line's first, so a ten-second instrumental gap would compress into one character of distance
    // and a press two seconds early would read as a fifth of a character out, i.e. a Perfect. Per
    // line, the gaps between lines are simply not on the axis.
    // ---------------------------------------------------------------------------

    // The last resort for a line whose data offers no spacing at all (mirrors
    // TypingLine.FALLBACK_CHAR_SPACING_MS).
    const FALLBACK_CHAR_SPACING_MS = 200;

    // Milliseconds per character to extrapolate at beyond the ends of the axis (mirrors
    // TypingLine.computeExtrapolationSpacing). The line's MEAN typeable spacing, which is its own
    // pace and is immune to a single degenerate gap in a way the first/last interval would not be.
    // Two fallbacks, for data that offers no spacing: a line with one typeable cell, or one whose
    // targets all sit on the same millisecond, falls back to its sung span over its cell count, and
    // a line with no span either falls back to FALLBACK_CHAR_SPACING_MS. The result is always
    // strictly positive, so no caller can divide by zero.
    function computeExtrapolationSpacing(judgeTargets, singEndTime) {
        const m = judgeTargets.length;

        if (m >= 2) {
            const mean = (judgeTargets[m - 1] - judgeTargets[0]) / (m - 1);
            if (mean > 0) return mean;
        }

        if (m >= 1) {
            const sung = (Math.max(singEndTime, judgeTargets[m - 1]) - judgeTargets[0]) / m;
            if (sung > 0) return sung;
        }

        return FALLBACK_CHAR_SPACING_MS;
    }

    // The last index of targets at or before time, or -1 (mirrors TypingLine.lastAtOrBefore).
    function lastAtOrBefore(targets, time) {
        let found = -1, lo = 0, hi = targets.length - 1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (targets[mid] <= time) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // The first index of targets at or after time, or the length (mirrors TypingLine.firstAtOrAfter).
    function firstAtOrAfter(targets, time) {
        let found = targets.length, lo = 0, hi = targets.length - 1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (targets[mid] >= time) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return found;
    }

    // Where the playhead is on this line's character axis at `time`, as the INCLUSIVE RANGE
    // [first, last] of fractional cell positions it covers (mirrors TypingLine.playheadSpan). The
    // two ends are equal for almost every time; they separate only when the time lands exactly on a
    // run of characters sharing a target, and then the range is that whole run.
    //
    // INSIDE the line, between two targets, it is the exact piecewise-linear interpolation between
    // them, which is the inverse of the per-character target interpolation buildCells already does.
    // OUTSIDE it, at the line's first and last characters, one bracket is missing and the position
    // is EXTRAPOLATED at the line's mean spacing rather than clamped: clamping would make every
    // early press on a line's first character a distance of exactly 0, i.e. a Perfect however early
    // it was, which is the one answer that must not come out.
    function playheadSpan(axis, time) {
        const t = axis.judgeTargets;
        const m = t.length;

        // A line with nothing typeable has no characters to be distant from, and no keypress can
        // reach one either (the caret auto-skips straight past it).
        if (m === 0) return [0, 0];

        const last = lastAtOrBefore(t, time);   // -1 when the time precedes every target
        const first = firstAtOrAfter(t, time);  // m  when the time follows every target

        // The time lands exactly on one or more targets: the playhead is on all of them.
        if (first <= last) return [first, last];

        let position;

        if (last < 0) position = (time - t[0]) / axis.extrapolationSpacingMs;
        else if (first >= m) position = (m - 1) + (time - t[m - 1]) / axis.extrapolationSpacingMs;
        // Strictly between two targets, so first === last + 1 and the bracket has real width: the
        // divisor cannot be zero however many characters share a millisecond elsewhere.
        else position = last + (time - t[last]) / (t[first] - t[last]);

        return [position, position];
    }

    function buildCharacterAxis(cells, singEndTime) {
        const judgeTargets = [];
        for (const cell of cells) if (cell.typeable) judgeTargets.push(cell.target);

        const axis = {
            judgeTargets: judgeTargets,
            extrapolationSpacingMs: computeExtrapolationSpacing(judgeTargets, singEndTime),
            cellPositions: new Array(cells.length)
        };

        // Where each DISPLAY cell sits on that axis. A typeable cell sits exactly on its own
        // integer index; a non-typeable one (auto-skipped punctuation) is interpolated onto the
        // axis from its target, so a caller never has to special-case it. Nothing judges a
        // non-typeable cell (the caret hops it), so which end of a tie it takes cannot reach a
        // score.
        let rank = 0;
        for (let i = 0; i < cells.length; i++)
            axis.cellPositions[i] = cells[i].typeable ? rank++ : playheadSpan(axis, cells[i].target)[1];

        return axis;
    }

    // The cell's own position on the character axis (mirrors TypingLine.CellPosition).
    function cellPosition(axis, cellIndex) {
        return axis.cellPositions.length === 0
            ? 0
            : axis.cellPositions[clamp(cellIndex, 0, axis.cellPositions.length - 1)];
    }

    // How many characters a keypress at `time` on the cell at `cellIndex` is from the character the
    // playhead is on (mirrors TypingLine.CharacterDistanceAt). NEGATIVE means the press is AHEAD of
    // the playhead (the player is rushing), positive that it is behind (dragging), matching the sign
    // of the millisecond delta it replaces. Fractional: a press halfway between two characters'
    // targets is half a character out.
    //
    // The playhead is a SPAN of characters, not a point, and a cell inside that span is exactly 0
    // characters out. For every ordinary press that is the same thing as subtracting one position
    // from another, because the span is a single point; it differs only where several characters
    // share one target time, which is the ordinary case at a word boundary (a word gap takes its
    // unit's end time and the next word's first letter takes the next unit's start time, and for
    // contiguous words those are the same millisecond). Both of those characters are equally "the
    // one the playhead is on", so a press dead on that time has to read as 0 for both; picking
    // either end of the run instead would charge a rhythm-perfect player a whole character for the
    // other one.
    function characterDistanceAt(line, time, cellIndex) {
        const axis = line.axis;
        const cell = cellPosition(axis, cellIndex);
        const span = playheadSpan(axis, time);

        if (cell < span[0]) return span[0] - cell; // the playhead has gone past it: the press is behind
        if (cell > span[1]) return span[1] - cell; // the playhead has not reached it: the press is ahead

        return 0;                                  // the playhead is ON this character
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
                cells: cells,
                // The axis judgement is measured on (backlog 133). Built here, once, exactly where
                // TypingLine's constructor builds it, so a keypress costs a binary search and not a
                // rebuild.
                axis: buildCharacterAxis(cells, singEndTime)
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
            state: 'untyped',        // untyped | correct | wrong | missed | autoskip
            // Perfect | Great | Ok | Meh | Premature | Lagging | Miss | WrongChar. The four QUALITY
            // tiers are named for the osu results they map to (backlog 133); before that the two
            // vocabularies disagreed and "Perfect" meant two different things depending on which
            // side of the mapping you were reading.
            judgeType: null,
            typedChar: null,
            // The awarded correct keypress's signed lead/lag in MILLISECONDS. Kept whatever measure
            // the play is judged in: it is the honest read-out of WHEN the key was pressed.
            judgedDelta: null,
            // ...and its offset in the measure the play is JUDGED in (TypingCell.JudgedOffset): a
            // fractional CHARACTER DISTANCE by default, equal to judgedDelta under the millisecond
            // measure. THIS, not judgedDelta, is what classify() and syncQuality() read.
            judgedOffset: null,
            // syncQuality of the awarded keypress, BANKED at the moment it was judged
            // (TypingCell.JudgedSyncQuality) rather than recomputed by the renderer, because the
            // windows it comes from depend on the play's measure and the engine is the only thing
            // that knows the measure.
            judgedSyncQuality: null,
            firstCorrectDelta: null,
            // The judgedOffset of the FIRST correct judgement, kept alongside firstCorrectDelta and
            // for the same reason: a scoring-inert retype replays the judgement the cell already
            // earned rather than earning a new one, and under the character measure the offset is
            // what that judgement was derived from.
            firstCorrectOffset: null,
            // Mirrors DrawableTypeBeatCharObject.Judged, i.e. "this cell has already handed the
            // score processor its one and only result". ApplyEngineResult bails on an already-judged
            // cell (`if (Judged) return;`) and ApplySealResults goes through the same call, so a cell
            // contributes to the submitted account exactly once whatever happens to it afterwards.
            // Since backlog 109 a wrong char sets nothing here, so the flag is also what says a typo
            // is still OPEN: fix it and the retype is the cell's first result, leave it and the seal
            // (or a word skip) is.
            judged: false
        };
    }

    // ---------------------------------------------------------------------------
    // Judgement windows (Judgement.cs SyncWindows), the single tuning point.
    //
    // Backlog 133: an offset is measured in CHARACTERS from the character the playhead is on, not
    // in milliseconds off the cell's target, and there are FOUR quality tiers instead of three. So
    // there are TWO ladders, one per measure, because a window is a distance in whatever the offset
    // is measured in and the two measures do not share a unit.
    //
    // CHARACTER DISTANCE is the live ladder. Geometric: every tier is exactly 1.6x late-biased,
    // which is what the old 250/400 millisecond pair was, and exactly double the tier inside it.
    // Wherever cell spacing is locally uniform a character distance reduces to the millisecond
    // delta divided by that spacing, which is the whole point: per-character targets are already
    // interpolated (buildCells), so "characters behind the playhead" and "milliseconds off target"
    // were always the same axis, and this rescales it to the map's own pace.
    //
    // MILLISECONDS is backlog 135's Rhythmic mod. Its Great/Ok/Meh rows are EXACTLY the windows
    // this game judged in up to backlog 133 (they were then called Perfect/Good/Ok and mapped onto
    // those same three osu results), so selecting the measure reproduces the old game rather than
    // approximating it; the fourth tier subdivides the TOP of the ladder at the same halving, so
    // nothing that used to be a Great becomes anything worse. NOTHING SELECTS IT YET, and /play has
    // no mods payload to select it with. It is mirrored here so the mod does not have to come back
    // and re-derive it, and so the two files stay a line-for-line pair.
    // ---------------------------------------------------------------------------
    const MEASURE_CHARACTER_DISTANCE = 'CharacterDistance';
    const MEASURE_MILLISECONDS = 'Milliseconds';

    const CHARACTER_WINDOWS = { pe: 1.25, pl: 2.00, ge: 2.50, gl: 4.00, oe: 5.00, ol: 8.00, me: 10.00, ml: 16.00 };
    const MILLISECOND_WINDOWS = { pe: 125, pl: 200, ge: 250, gl: 400, oe: 600, ol: 1000, me: 1200, ml: 2000 };

    // Granularity scales: unreliable timing gets the widest tolerance, never the tightest.
    const TIER_SCALE = { Line: 1.0, Word: 0.6, Syllable: 0.45 };

    function windowsFor(tier, measure) {
        const s = TIER_SCALE[tier] != null ? TIER_SCALE[tier] : 1.0;
        const b = measure === MEASURE_MILLISECONDS ? MILLISECOND_WINDOWS : CHARACTER_WINDOWS;
        return {
            pe: b.pe * s, pl: b.pl * s,
            ge: b.ge * s, gl: b.gl * s,
            oe: b.oe * s, ol: b.ol * s,
            me: b.me * s, ml: b.ml * s
        };
    }

    // SyncWindows.Classify: nested asymmetric ranges, tested Perfect -> Great -> Ok -> Meh; outside
    // Meh the sign decides Premature (too far ahead) vs Lagging (too far behind).
    function classify(offset, w) {
        if (offset >= -w.pe && offset <= w.pl) return 'Perfect';
        if (offset >= -w.ge && offset <= w.gl) return 'Great';
        if (offset >= -w.oe && offset <= w.ol) return 'Ok';
        if (offset >= -w.me && offset <= w.ml) return 'Meh';
        return offset < -w.me ? 'Premature' : 'Lagging';
    }

    // SyncWindows.SyncQuality: asymmetric quality in [0, 1] over the WIDEST scoring window. Exactly
    // 1 dead on the playhead and exactly 0 at the edges of the Meh window, so every offset a correct
    // keypress can still score at maps somewhere inside the ramp and everything beyond it
    // (Premature / Lagging) sits on the floor.
    function syncQuality(offset, w) {
        const q = 1 - (offset < 0 ? -offset / w.me : offset / w.ml);
        return q < 0 ? 0 : (q > 1 ? 1 : q);
    }

    // SyncWindows.BasePoints: the engine's own per-character points, matching the osu base score
    // each tier's result carries (HIT_BASE_SCORE below), so the engine's running score and the
    // submitted one grade a keypress the same way.
    function basePoints(type) {
        switch (type) {
            case 'Perfect': return 300;
            case 'Great': return 200;
            case 'Ok': return 100;
            case 'Meh': return 50;
            default: return 0; // Premature, Lagging, Miss, WrongChar
        }
    }

    // Engine judgement -> osu HitResult (TypeBeatResultMapping.CellResult). The four QUALITY tiers
    // are the IDENTITY on the results they are named for since backlog 133; Premature, Lagging and
    // Miss all resolve as a miss. A WrongChar resolves NOTHING and never reaches here.
    function toHitResult(judgeType) {
        switch (judgeType) {
            case 'Perfect': return 'perfect';
            case 'Great': return 'great';
            case 'Ok': return 'ok';
            case 'Meh': return 'meh';
            default: return 'miss'; // Premature, Lagging, Miss, or untyped
        }
    }

    // ---------------------------------------------------------------------------
    // ScoreProcessorMirror: the SUBMITTED-score accounting, mirroring osu's
    // ScoreProcessor (ScoreProcessor.ApplyResultInternal / updateScore) as the desktop client
    // runs it under TypeBeatScoreProcessor.
    //
    // This is a running, INCREMENTAL account kept during play, exactly like the C# one, and that
    // is load-bearing rather than stylistic: the combo portion of the total score accumulates
    // per judgement using the combo value AT THAT MOMENT (GetComboScoreChange reads
    // result.ComboAfterJudgement), so a combo break costs every contribution that follows it.
    // An end-of-play reconstruction from the per-cell result stream CANNOT reproduce that,
    // because a rejected wrong key leaves no cell result behind to reconstruct from: it is a
    // break that happened between two results. That reconstruction is what this file used to do,
    // and it is why a browser play used to submit a higher max_combo and total_score than the
    // desktop client for the identical mistyped performance (see backlog 72/73).
    //
    // What moves the C# processor, in the order the events happen (all four events below are
    // routed by TypeBeatPlayfield, which subscribes to the engine):
    //
    //   1. TypeBeatPlayfield.onCharJudged -> DrawableTypeBeatHitObject.ApplyCharJudgement ->
    //      DrawableTypeBeatCharObject.ApplyEngineResult -> ApplyResult(toHitResult(type)).
    //      Perfect/Great/Ok/Meh are the IDENTITY on the osu results of those names (backlog 133)
    //      and INCREASE combo; Premature/Lagging become
    //      Miss, which BREAKS it. A WrongChar becomes NOTHING (backlog 109): ApplyCharJudgement
    //      returns before applying anything, so a typo defers its cell's result instead of spending
    //      it on a Miss. The cell drawable applies at most ONE result ever (`if (Judged) return;`),
    //      so a backspace-and-retype after a typo is that cell's real (first) result, while an
    //      inert retype of an already-correct cell moves nothing.
    //   2. TypeBeatPlayfield.onMistyped -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatScoreProcessor.RecordMistype. One seam for BOTH input models since backlog 109,
    //      because neither raises a result for a wrong keypress any more: a rejected key never did,
    //      and a typed-through one no longer does. osu's combo is maintained incrementally off
    //      results, so the break has to be mirrored by hand or max_combo would count on through it.
    //      Nothing else moves: no result count, no accuracy, no combo portion, and RecordMistype is
    //      a pure counter (routing it through ApplyResult would move the judged count too).
    //   3. TypeBeatPlayfield.onWrongKeyRejected -> the mash-guard HP drain only, which is why this
    //      mirror has no counterpart for it beyond engine.consecutiveWrongKeys: the browser models
    //      health as a derived read (see `health`), not as an account.
    //   4. DrawableTypeBeatHitObject.ApplySealResults, when the engine seals a line: every
    //      still-unjudged cell resolves, in cell order, and then the LINE object itself resolves
    //      as IgnoreHit. IgnoreHit is not scorable, does not affect combo and does not affect
    //      accuracy, so the line objects are inert and are not modelled at all. TWO results, not
    //      one (backlog 124, TypeBeatResultMapping.UnresolvedCellResult): a cell nobody typed is a
    //      MISS, a cell left sitting WRONG is an unfixed TYPO, which since backlog 126 is a key of
    //      its OWN, `good` (TypeBeatResultMapping.UNFIXED_TYPO). Backlog 124 had spent `meh` on it,
    //      which is also what a correct-but-late keypress resolves as, so no consumer of the
    //      submitted statistics could tell the two apart; `good` is the only result a type!beat cell
    //      may legally take that nothing else uses. Cell order matters now that the two differ,
    //      which is why the C# walks a SortedDictionary and this walks line.cells.
    //      The typo's result is a HIT, so it would extend the run the player rebuilt after the
    //      keypress that broke it; it is applied COMBO-NEUTRAL instead
    //      (TypeBeatPlayfield.onLineSealed -> TypeBeatScoreProcessor.MarkComboNeutral, which is
    //      markComboNeutral and the branch in applyResult below). It is a hit for accuracy and for
    //      the note count, and NOT for completion (see computeScore), which is backlog 126: a cell
    //      typed wrong is not a cell typed, and it costs rank exactly as a miss does.
    //
    // Judgement rewind (ScoreProcessor.RevertResultInternal) has no counterpart: gameplay here is
    // never rewound, and neither is the desktop client's outside replay seeking.
    // ---------------------------------------------------------------------------

    // ScoreProcessor.GetBaseScoreForResult for the six results a type!beat cell can take, i.e. the
    // 300 / 200 / 100 / 50 quality ladder plus the typo and the miss. Two of them are NOT the base
    // ruleset's numbers, and both departures live in TypeBeatScoreProcessor.GetBaseScoreForResult:
    //
    //  - `great` is 200, not 300. Backlog 133's fourth tier put `perfect` on top of the ladder, and
    //    the base game already scores a Perfect at 300 ("Perfect doesn't actually give more score /
    //    accuracy directly"), so the tier was made by moving GREAT DOWN rather than Perfect up. That
    //    is what keeps the per-cell maximum, and therefore the accuracy denominator, exactly where
    //    it was.
    //  - `good` is the uncorrected-typo tier and is 50, not 200, so a typo pays the most accuracy a
    //    judged cell can pay, i.e. exactly what it paid while backlog 124 stored it as a `meh`.
    //
    // The server's ScoringContract.BaseScore carries both numbers.
    const HIT_BASE_SCORE = { perfect: 300, great: 200, ok: 100, meh: 50, good: 50, miss: 0 };

    // Every cell judgement declares MaxResult = Perfect (TypeBeatCharJudgement, raised from Great by
    // backlog 133), which is what both the accuracy denominator (currentMaximumBaseScore) and the
    // combo-portion weight (GetComboScoreChange) are taken from, whatever the result actually was.
    // Still 300: that is Perfect's stock base score, which is why the ceiling did not move.
    const MAX_RESULT_BASE_SCORE = 300;

    // ScoreProcessor.COMBO_EXPONENT.
    const COMBO_EXPONENT = 0.5;

    class ScoreProcessorMirror {
        constructor() {
            this.combo = 0;              // ScoreProcessor.Combo
            this.highestCombo = 0;       // ScoreProcessor.HighestCombo, submitted as max_combo
            this.comboPortion = 0;       // currentComboPortion
            this.baseScore = 0;          // currentBaseScore
            this.maximumBaseScore = 0;   // currentMaximumBaseScore
            this.judgementCount = 0;     // currentAccuracyJudgementCount
            this.counts = { perfect: 0, great: 0, ok: 0, meh: 0, good: 0, miss: 0 }; // ScoreResultCounts
            // TypeBeatScoreProcessor.comboNeutralCells: the cells whose combo consequence has
            // ALREADY been taken by hand, at the keypress that spoiled them, so the result they
            // finally resolve with must leave combo exactly as it finds it. The C# keys this by
            // (line, cell), which is what a TypeBeatCharObject carries; here the cell object itself
            // is the identity, and it is held on the PROCESSOR rather than on the cell for the same
            // reason: cells live on the shared beatmap and outlive a play, this account does not.
            this.comboNeutral = new Set();
        }

        // ScoreProcessor.ApplyResultInternal, for the accuracy-affecting basic results a cell can
        // take (perfect/great/ok/meh/good/miss). All six are scorable, none is a bonus, and all six
        // affect combo: the five hits increase it, a miss breaks it. `good` only ever arrives
        // combo-neutral, so its increment is suppressed below.
        applyResult(result, cell) {
            this.counts[result]++;

            // TypeBeatScoreProcessor.MarkComboNeutral / ApplyScoreChange, folded into one branch
            // here. The C# cannot do that (ApplyResultInternal is sealed, so it moves combo first
            // and the ruleset hook puts it back afterwards), but the observable rule is this: an
            // unfixed typo's result neither breaks the run nor extends it, and it takes its
            // combo-portion weight from the combo it FOUND. Its break was already paid at the
            // keypress (breakCombo below), which is the whole combo cost of getting a character
            // wrong. highestCombo is skipped with it: unlike a suppressed break, a suppressed
            // INCREMENT can raise a running maximum, so leaving it would inflate max_combo by one
            // per typo.
            if (!this.comboNeutral.has(cell)) {
                if (result === 'miss') this.combo = 0;
                else this.combo++;

                if (this.combo > this.highestCombo) this.highestCombo = this.combo;
            }

            this.maximumBaseScore += MAX_RESULT_BASE_SCORE;
            this.judgementCount++;
            this.baseScore += HIT_BASE_SCORE[result];

            // GetComboScoreChange: the MAX result's base score weighted by the combo AFTER this
            // judgement. A miss therefore contributes 300 * 0^0.5 = 0, and every later hit is
            // weighted by a combo that this break restarted from zero. A combo-neutral cell moved
            // nothing, so this is exactly the combo it found.
            this.comboPortion += MAX_RESULT_BASE_SCORE * Math.pow(this.combo, COMBO_EXPONENT);
        }

        // TypeBeatScoreProcessor.MarkComboNeutral: the result about to be applied to this cell must
        // leave combo alone, because the cell's break was taken by hand at the keypress that spoiled
        // it. Marked at the seam that APPLIES it (the seal), never at the keypress, which is what
        // keeps a CORRECTED typo working: the retype resolves the cell with an ordinary
        // combo-increasing hit that never consults this set.
        markComboNeutral(cell) {
            this.comboNeutral.add(cell);
        }

        // TypeBeatPlayfield.onMistyped: `scoreProcessor.Combo.Value = 0`, and nothing else. Every
        // wrong keypress in either input model comes through here, because none of them raises a
        // judgement result. HighestCombo needs no update (it only ever grows, this only shrinks
        // Combo), which is exactly why a break mirrored here cannot inflate max_combo.
        breakCombo() {
            this.combo = 0;
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
                    c.judgedOffset = null;
                    c.judgedSyncQuality = null;
                    c.firstCorrectDelta = null;
                    c.firstCorrectOffset = null;
                    c.judged = false;
                }
            }
            this.activeLineIndex = -1;
            this.caretIndex = 0;
            this.nextSealIndex = 0;
            // The engine's OWN live combo/score (TypingEngine.Combo / Score): what the HUD shows.
            // The submitted numbers do not come from here; they come from the score processor
            // mirror below, which the engine drives at exactly the points TypeBeatPlayfield drives
            // the real one. In vanilla play the two combos happen to track each other, but
            // they are separate accounts with separate rules and only one of them is submitted.
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
            // The osu-side account the SUBMITTED score is read off (computeScore); the stand-in for
            // the TypeBeatScoreProcessor a Player would cache. Fed incrementally from here, exactly
            // where TypeBeatPlayfield feeds the real one.
            this.processor = new ScoreProcessorMirror();
            // Mods (TypingEngine.CaseSensitive / MashingEnabled). The browser player always plays
            // vanilla and never turns these on; they exist so processKey stays a line-for-line
            // mirror of the C# and so the freestyle exemptions in it are pinned by the JS-vs-C#
            // fidelity harness. Both default off, i.e. today's exact behaviour. Exposing either in
            // the UI would also mean wiring the submitted mods payload (both are unranked).
            this.caseSensitive = false;       // Literate: the typed char must match the exact case
            this.mashingEnabled = false;      // Mashing (Relax): any key is the right key
            // What a keypress's offset from its cell is MEASURED IN, and therefore what unit the
            // windows are expressed in (TypingEngine.Measure). CharacterDistance is the live rule
            // and the only thing /play can be in: MEASURE_MILLISECONDS is the pre-backlog-133 rule,
            // kept live for the Rhythmic mod, and the browser has no mods payload to select it with.
            this.measure = MEASURE_CHARACTER_DISTANCE;
            // The wrong-key MODEL, and unlike the two above this one is ON, because it is the
            // DEFAULT gameplay on both sides since backlog 107: a wrong (non-space) char is typed
            // through and marked wrong instead of being rejected, and backspace can take it back.
            // The desktop client turns it off only for the Gatekeeper mod (acronym GK), and the
            // browser has no mods payload at all, so /play is permanently non-Gatekeeper, which is
            // exactly the default the shared leaderboards are now judged under. If /play ever grows
            // a mods payload, this is the flag GK would clear.
            this.allowWrongInput = true;
            // "Space to skip current word" (TypeBeatRulesetSetting.SpaceSkipsWord, backlog 110): a
            // space pressed inside a word abandons the rest of it as misses and moves on to the next
            // word. DEFAULTED OFF, like caseSensitive/mashingEnabled above and for the same reason:
            // the browser has no settings payload, so /play is permanently non-skipping and the
            // default path stays byte-identical to the desktop's default. If /play ever grows one,
            // this is the flag it sets, and it would ALSO have to travel in whatever the browser's
            // equivalent of the replay CONFIG frame is (the desktop carries it as bit 1), because it
            // changes how a recorded space is judged.
            this.spaceSkipsWord = false;
            // event hooks (optional; set by the renderer)
            this.onCharJudged = null;
            this.onWrongKey = null;
            this.onComboBroken = null;
            this.onFinished = null;
            this.onFailed = null;
        }

        // The mash guard, and it survives the backlog-107 flip on both sides for the same reason:
        // the streak only ever grows on the REJECTION path, and the two space cases (a space pressed
        // on a lyric char, any key pressed on a word gap) take that path in every model. So a
        // browser player mashing space still fails at 13 exactly as a desktop one does, while a
        // player mashing LETTERS no longer fails at all unless they picked Gatekeeper.
        get health() { return Math.max(0, 1 - this.consecutiveWrongKeys / WRONG_KEY_FAIL_STREAK); }

        // Wrong KEYPRESSES so far: the play's persisted mistype stat (TypingEngine.Mistypes).
        // Counted per keypress in BOTH models, so it means the same thing whether the key was
        // typed through (the default, and everything the browser can do) or rejected.
        get mistypes() { return this.counts.WrongChar || 0; }

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

        // DrawableTypeBeatCharObject.ApplyEngineResult: a cell hands the score processor its ONE
        // result and every later attempt on the same cell is dropped (`if (Judged) return;`). Every
        // processor.applyResult in this engine goes through here, so the submitted account can never
        // hold two entries for one cell however the play reaches it.
        applyCellResult(cell, result) {
            if (cell.judged) return;
            cell.judged = true;
            this.processor.applyResult(result, cell);
        }

        sealLine(idx) {
            const line = this.lines[idx];
            let missed = 0;
            for (const c of line.cells) {
                // The engine's own miss count (TypingEngine.Update's seal loop), which drives the
                // HUD combo below and nothing that is submitted. A cell the line ran out of time on,
                // and ONLY that (backlog 124, reversing the predicate backlog 109 widened): a cell
                // left sitting WRONG is a character the player FINISHED, so it is a mistype and not
                // a miss, it keeps 'wrong' on screen, and it does not break the HUD combo here,
                // because its break was taken at the keypress. That is what puts the HUD combo back
                // in agreement with the submitted max_combo (backlog 123).
                if (c.typeable && c.state === 'untyped') {
                    c.state = 'missed';
                    c.judgeType = 'Miss';
                    missed++;
                }

                // DrawableTypeBeatHitObject.ApplySealResults: EVERY still-unjudged nested char
                // drawable of the line takes its result at seal time, in cell order, and the loop
                // skips cells that already carry one. `judged` is deliberately not the state test
                // above: the two come apart for a cell typed correctly and then BACKSPACED, which is
                // 'untyped' again and so is counted a miss for display, while its drawable keeps the
                // Great it already took.
                //
                // The result is a MISS for a cell nobody typed and a 'good' (the uncorrected-typo
                // key, TypeBeatResultMapping.UNFIXED_TYPO) for one left holding a wrong character,
                // and the typo is marked COMBO-NEUTRAL immediately before it is applied, exactly as
                // TypeBeatPlayfield.onLineSealed does it.
                if (c.typeable && !c.judged) {
                    if (c.state === 'wrong') {
                        this.processor.markComboNeutral(c);
                        this.applyCellResult(c, 'good');
                    } else {
                        this.applyCellResult(c, 'miss');
                    }
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

        // TypingEngine.skipCurrentWord. Abandon the whole word the caret is inside and leave the
        // caret on the word gap after it (or at the end of the line, for a word with no gap).
        // No `time` parameter, unlike the C#, which needs it only for the per-cell judgement deltas
        // this mirror does not raise (see the onCharJudged note below).
        skipCurrentWord() {
            const cells = this.lines[this.activeLineIndex].cells;

            // The word: the run between the typeable SPACE cells either side of the caret. The whole
            // word rather than the tail from the caret, which gives up the same cells (everything
            // behind the caret is already resolved) but says what the feature promises.
            let start = this.caretIndex;
            let end = this.caretIndex;
            while (start > 0 && !(cells[start - 1].typeable && cells[start - 1].expected === ' ')) start--;
            while (end < cells.length && !(cells[end].typeable && cells[end].expected === ' ')) end++;

            let missed = 0;

            for (let i = start; i < end; i++) {
                const c = cells[i];

                if (!c.typeable) {
                    c.state = 'autoskip'; // exactly what autoSkipForward would have done to it
                    continue;
                }

                // Only a cell nobody has put anything into is given up. A CORRECT one has already
                // handed its drawable the one result it will ever have and a Great cannot be revoked
                // (applyCellResult's `judged` guard is the same rule). A WRONG one is not given up
                // either, and since backlog 124 that is for its own reason: a typed-through wrong
                // character is a cell the player FINISHED, so abandoning the word cannot turn it
                // into a miss. Its deferred result is decided at the seal like every other unfixed
                // typo, which also leaves the promise intact that backspacing back into the word can
                // still fix it. Backlog 109 had it given up here, because at the time the only fate
                // available to an unfixed typo was a Miss.
                if (c.state !== 'untyped') continue;

                c.state = 'missed';
                c.judgeType = 'Miss';
                missed++;

                // The C# announces each abandoned cell IMMEDIATELY (CharJudged carrying a Miss ->
                // ApplyCharJudgement -> ApplyEngineResult), so the submitted account takes the miss
                // here rather than at seal time and osu's combo breaks with the engine's instead of
                // counting on to the end of the line. sealLine is then a no-op on these cells.
                this.applyCellResult(c, 'miss');
            }

            this.caretIndex = end;

            if (missed === 0) return;

            // AT MOST ONE combo break for the whole word, the rule sealLine's misses follow. The
            // processor's own combo was already broken by the first applyCellResult('miss').
            this.combo = 0;
            if (this.onComboBroken) this.onComboBroken();

            // NO onCharJudged for the abandoned cells, deliberately, and the omission mirrors the
            // wrong-char path above: that hook is this renderer's rolling-WPM tap, and the C#
            // pushRollingSample() sits on the accepted-keypress path only, so tapping it here would
            // drift the browser's WPM readout away from the desktop's. The cells repaint from state.
            // engine `counts` is left alone too: it is the scored-only dict, and sealLine does not
            // record its misses there either (the submitted miss count comes off the processor).
        }

        update(time) {
            // (1) accrue active typing time using this frame's span.
            //
            // The C# TypingEngine.Update takes a clockRate here and accrues dt / rate, because a
            // speed-adjusting mod makes beatmap milliseconds and real ones diverge and every WPM
            // readout is otherwise wrong by 1/rate. This mirror deliberately has no such parameter:
            // the browser player offers no rate mods at all (nothing here touches playbackRate), so
            // its rate is permanently 1 and the two accumulators agree. Adding one here is the
            // first thing to do if browser play ever gains a speed mod.
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
            let cell = line.cells[this.caretIndex];

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

            // SPACE-SKIP (spaceSkipsWord), evaluated BEFORE the match: a space pressed while the
            // caret sits on a lyric character abandons that word. The caret cell is typeable here
            // (autoSkipForward ran above), so "expected is not a space" is exactly "inside a word",
            // and a space pressed ON the word gap keeps its ordinary meaning. After the Mashing
            // rewrite on purpose: mashing has already turned the press into the expected char, so
            // this is unreachable under it.
            if (this.spaceSkipsWord && c === ' ' && cell.expected !== ' ') {
                this.skipCurrentWord();
                if (this.caretIndex >= line.cells.length) return true; // the word ran to the line end
                cell = line.cells[this.caretIndex]; // the word gap, judged as an ordinary space below
            }

            // The press's lead/lag in MILLISECONDS, which is what the renderer and the timing
            // read-out want, and its offset in the measure the play is JUDGED in, which is what the
            // windows classify. Identical under the millisecond measure.
            const delta = time - cell.target;
            const offset = this.judgementOffset(line, this.caretIndex, time);
            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule is bypassed for it). The press is then judged exactly
            // like a correct char: same windows, points, combo, accuracy and completion, with the
            // pressed char kept in typedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // rejection is the only outcome available to it, because the allow-wrong-input path
            // already refuses to type a space through (c !== ' ').
            const matched = (cell.freestyle && c !== ' ') ||
                (this.caseSensitive ? c === cell.expected : fold(c) === fold(cell.expected));

            if (!matched) {
                // DEFAULT (allowWrongInput): a wrong LETTER is typed through, marked wrong,
                // backspaceable, instead of rejected. The space key stays strict on BOTH sides (no
                // wrong space, and no wrong char consuming a word boundary), and this path never
                // feeds the mash-fail streak, so the browser has no 13-key fail at all, which is
                // correct: that guard belongs to the rejection model (the Gatekeeper mod) and the
                // browser can never be in it.
                if (this.allowWrongInput && c !== ' ' && cell.expected !== ' ') {
                    this.totalKeypresses++;
                    this.errorCount++;
                    this.combo = 0;
                    this.counts.WrongChar = (this.counts.WrongChar || 0) + 1;

                    cell.state = 'wrong';
                    cell.typedChar = c;
                    cell.judgeType = 'WrongChar';

                    this.caretIndex++;
                    this.autoSkipForward();

                    if (this.onComboBroken) this.onComboBroken();
                    // NO result for the cell (backlog 109), exactly as in the C#: the CELL's
                    // judgement still travels on CharJudged for the stage, but
                    // DrawableTypeBeatHitObject.ApplyCharJudgement returns before applying anything
                    // for a WrongChar. A miss is a character the line ran out of time on; a typo is a
                    // typo, and backspace can still fix this one, so the cell's one result is
                    // DEFERRED: the fix earns its real Perfect/Great/Ok/Meh, and one left alone
                    // resolves at the seal as an unfixed typo, its own key and not a miss
                    // (backlog 124, re-keyed to `good` by 126).
                    //
                    // Which leaves the submitted COMBO with nothing to break it, because osu's combo
                    // is maintained incrementally off results. So the break is mirrored by hand here,
                    // exactly as the rejection path below does it (TypeBeatPlayfield.onMistyped,
                    // which fires for both models). Without this the browser's max_combo would count
                    // on through the rest of the line after a break the engine has already taken.
                    this.processor.breakCombo();
                    // ...and that is the ONLY break this keypress costs (backlog 122), and since
                    // backlog 124 it is the cell's whole combo consequence in BOTH directions. The
                    // cell's deferred result is now a hit, so the danger has flipped from a second
                    // break to a free increment; sealLine handles it there, at the seam that applies
                    // the result, rather than here, because a typo the player goes back for must
                    // still earn its retype's combo normally.
                    // NEITHER renderer hook fires here, and both omissions mirror the desktop.
                    // onWrongKey is the REJECTED-key feedback (shake + the char popping off the
                    // caret, mirroring LyricStage.onWrongKeyRejected), which the desktop does not
                    // play for a char it accepted into a cell; the cell painting itself wrong is the
                    // feedback on both sides. onCharJudged is this renderer's rolling-WPM tap, and
                    // the C# pushRollingSample() sits on the ACCEPTED path only, below the branch we
                    // are in, so logging a wrong char here would drift the browser's WPM readout
                    // away from the desktop's.
                    return true;
                }

                // GATEKEEPER (strict). Wrong key REJECTED: costs a keypress + combo + streak; caret
                // unmoved. Unreachable from the browser for a letter, reached for the two space
                // cases the default path refuses above.
                this.totalKeypresses++;
                this.errorCount++;
                this.consecutiveWrongKeys++;
                this.combo = 0;
                // ...and the SUBMITTED combo breaks with it (TypeBeatPlayfield.onMistyped sets
                // scoreProcessor.Combo.Value = 0), the same hand-written break the typed-through
                // path above now makes. No judgement is raised, so this is the only trace the
                // rejected key leaves in the score account: it lowers max_combo and every
                // combo-portion contribution that comes after it.
                this.processor.breakCombo();
                // ...and it is a MISTYPE (TypingEngine.Mistyped -> TypeBeatScoreProcessor
                // .RecordMistype): the one thing about a rejected key that outlives the play,
                // reported to the server as the combo_break statistics key by computeScore.
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

            const w = windowsFor(cell.tier, this.measure);
            const inertRetype = cell.firstCorrectDelta !== null;
            let type, points = 0;

            if (inertRetype) {
                // Scoring-inert on BOTH accounts: the engine leaves its own combo/score alone, and
                // no result reaches the processor, because the cell drawable already carries one
                // (DrawableTypeBeatCharObject.ApplyEngineResult: `if (Judged) return;`). The branch
                // below relies on the SAME guard rather than on this condition, because with
                // allowWrongInput a cell can carry a result without ever having been correct.
                const d = cell.firstCorrectDelta;
                const o = cell.firstCorrectOffset;
                type = classify(o, w);
                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = d;
                cell.judgedOffset = o;
                cell.judgedSyncQuality = syncQuality(o, w);
                cell.judgeType = type;
            } else {
                this.totalKeypresses++;
                this.correctKeypresses++;
                type = classify(offset, w);
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
                cell.judgedOffset = offset;
                cell.judgedSyncQuality = syncQuality(offset, w);
                cell.firstCorrectDelta = delta;
                cell.firstCorrectOffset = offset;
                cell.judgeType = type;
                this.counts[type] = (this.counts[type] || 0) + 1;
                // The cell's one-and-only osu result, applied the moment it is judged: this is
                // TypeBeatPlayfield.onCharJudged -> ApplyCharJudgement -> ApplyResult. The four
                // quality tiers increase the submitted combo, Premature/Lagging break it (they map
                // to Miss), and the combo portion is weighted by the combo as it stands right here.
                // Guarded rather than unconditional, because the guard is the mirror of
                // ApplyEngineResult's `if (Judged) return;` and not of the inert-retype rule. A cell
                // typed WRONG and then backspaced comes back through here with firstCorrectDelta
                // still null AND with no result yet (backlog 109 defers it), so this call is where
                // the fix is finally paid for: the cell earns its real Perfect/Great/Ok/Meh, which
                // is the whole point. The guard still matters for the cells the seal or a word skip
                // resolved first, which must not be re-judged.
                this.applyCellResult(cell, toHitResult(type));
            }

            const judgedIndex = this.caretIndex;
            this.caretIndex++;
            this.autoSkipForward();
            if (this.onCharJudged) this.onCharJudged(judgedIndex, type, points);
            return true;
        }

        // TypingEngine.judgementOffset: the offset a keypress at `time` on cell `cellIndex` of
        // `line` is JUDGED by, in the current measure. How many characters it is from the character
        // the playhead is on (negative = ahead of it), or the plain millisecond delta under the
        // millisecond measure.
        judgementOffset(line, cellIndex, time) {
            return this.measure === MEASURE_MILLISECONDS
                ? time - line.cells[cellIndex].target
                : characterDistanceAt(line, time, cellIndex);
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
            cell.judgedOffset = null;
            cell.judgedSyncQuality = null;
            cell.judgeType = null;
            // firstCorrectDelta / firstCorrectOffset intentionally retained (inert-retype guard).
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

        // The running account the play built up, judgement by judgement, wrong key by wrong key
        // (see ScoreProcessorMirror). Nothing is reconstructed here: reconstruction is precisely
        // what cannot see a rejected key.
        const processor = engine.processor;
        const perfect = processor.counts.perfect;
        const great = processor.counts.great;
        const ok = processor.counts.ok;
        const meh = processor.counts.meh;
        const typos = processor.counts.good; // uncorrected typos (TypeBeatResultMapping.UNFIXED_TYPO)
        const miss = processor.counts.miss;
        const judged = processor.judgementCount; // == perfect + great + ok + meh + good + miss

        // Whole-map accuracy (for display/completion); server overrides the submitted value.
        const acc = total > 0 ? processor.baseScore / (MAX_RESULT_BASE_SCORE * total) : 1;

        // The maximum combo portion, i.e. what an all-Perfect run of the whole map accumulates
        // (ScoreProcessor.maximumComboPortion, stored from the autoplay simulation). One nested
        // char object exists per TYPEABLE cell (TypeBeatHitObject.CreateNestedHitObjects skips the
        // rest), so the simulated combo runs 1..N over exactly those cells.
        let maxComboCounter = 0, maxComboPortion = 0;
        for (const line of engine.lines) {
            for (const cell of line.cells) {
                if (!cell.typeable) continue;
                maxComboCounter++;
                maxComboPortion += MAX_RESULT_BASE_SCORE * Math.pow(maxComboCounter, COMBO_EXPONENT);
            }
        }

        const comboProgress = maxComboPortion > 0 ? processor.comboPortion / maxComboPortion : 1;
        const accuracyProgress = total > 0 ? judged / total : 1;

        // total_score uses the JUDGED-only accuracy denominator (ScoreProcessor.Accuracy =
        // currentBaseScore / currentMaximumBaseScore, judged cells only); equals whole-map
        // accuracy for a completed play, differs only for a failed/incomplete (unranked) run.
        const accJudged = processor.maximumBaseScore > 0 ? processor.baseScore / processor.maximumBaseScore : 1;
        const totalWithoutMods = Math.round(500000 * accJudged * comboProgress + 500000 * Math.pow(accJudged, 5) * accuracyProgress);
        const totalScore = totalWithoutMods; // scoreMultiplier = 1 (no mods)

        // TypeBeatScoreProcessor.CountsAsTyped: every HIT except an uncorrected typo. A cell typed
        // wrong is not a cell typed, so it is in the denominator and out of the numerator and costs
        // completion, and therefore rank, exactly as a miss does (backlog 126). Between backlog 124
        // and 126 `typos` was folded into `meh` and counted here, so a run typed entirely wrong read
        // completion 1 and took an X.
        const completion = total > 0 ? (perfect + great + ok + meh) / total : 1;
        const passed = engine.finished && !engine.failed;

        // Wrong keypresses ride along as their own key. HitResult.ComboBreak is combo-only and
        // NOT accuracy-affecting on either side, so adding it changes no other number here and the
        // server's ScoringContract recomputes the identical accuracy / completion / rank; it is
        // priced only by pp's own mistyping term. maximumStatistics stays one PERFECT per cell (the
        // judgement's MaxResult, raised from Great by backlog 133), so mashing can never inflate the
        // denominator of anything.
        // Zero is omitted exactly as the other keys are, matching the desktop client, which strips
        // zero-valued entries before submitting (SoloScoreInfo.ForSubmission).
        const mistypes = engine.mistypes;

        const statistics = {};
        if (perfect) statistics.perfect = perfect;
        if (great) statistics.great = great;
        if (ok) statistics.ok = ok;
        if (meh) statistics.meh = meh;
        if (typos) statistics.good = typos;
        if (miss) statistics.miss = miss;
        if (mistypes) statistics.combo_break = mistypes;

        return {
            passed: passed,
            totalScore: totalScore,
            totalScoreWithoutMods: totalWithoutMods,
            accuracy: acc,
            // ScoreProcessor.HighestCombo, submitted as max_combo: the longest run of judged cells
            // uninterrupted by a missed cell OR a wrong keypress (in either input model).
            maxCombo: processor.highestCombo,
            completion: completion,
            rank: passed ? rankFromCompletion(completion) : 'F',
            statistics: statistics,
            maximumStatistics: { perfect: total },
            // convenience for the results screen
            counts: { perfect, great, ok, meh, typos, miss, mistypes },
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
        characterDistanceAt, playheadSpan, cellPosition,
        TypingEngine, computeScore, rankFromCompletion,
        windowsFor, classify, syncQuality, basePoints, toHitResult,
        freestyleTick, freestyleGlyph,
        constants: {
            CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, LOW_CONFIDENCE_SCORE, FREESTYLE_MARKER,
            SHIMMER_INTERVAL_MS, PUNCTUATION, WORD_BREAK, FALLBACK_CHAR_SPACING_MS,
            MEASURE_CHARACTER_DISTANCE, MEASURE_MILLISECONDS
        },
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
