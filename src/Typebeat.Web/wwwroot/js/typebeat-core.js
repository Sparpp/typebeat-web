/*
 * typebeat-core.js: a faithful, dependency-free reimplementation of the
 * type!beat typing gameplay for the browser.
 *
 * This mirrors the desktop client's headless gameplay core
 * (typebeat.Game.Rulesets.TypeBeat: TypingEngine, TypingLine, Syllabifier,
 * Judgement, TypeBeatHealthProcessor) and the standardised scorer (osu ScoreProcessor +
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
    // semicolon, colon, round brackets, square brackets, straight double quote, dollar sign,
    // percent sign, caret, asterisk, angle brackets, forward slash.
    //
    // A map stores the AUTHOR'S form: punctuated and case-sensitive. What the player types (and
    // sees) is derived from it: verbatim under the desktop client's LITERATE mod, and through
    // toDefaultStream otherwise. Deliberately outside isTypeable, so a mark never counts as a plain
    // typeable char for the interpolation weights or the cell counts.
    //
    // Widening this set cannot move a stored per-map stat: every mark but WORD_BREAK is deleted by
    // defaultChar, so a char that used to be dropped by normalize as unsupported is now kept in the
    // author's line and dropped one step later, leaving the DEFAULT stream byte-identical.
    const PUNCTUATION = ",.'-?!;:()[]\"$%^*<>/";

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
    //
    // The AUTHORED char split rides along (backlog 181) and is validated HERE rather than at parse
    // time, because only this function knows how many boundaries actually survived clamping. The
    // rule is deliberately strict, because a split is a char index and a wrong one would silently
    // re-cut a word rather than fail: it is kept only when the clamping above DROPPED NOTHING (a
    // boundary lost to a narrowed word would re-pair every later split with the wrong segment) and
    // the indices themselves are a valid split of this token. Anything else falls back to derived.
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
            const raw = words[m].syllables || EMPTY_BOUNDARIES;
            const boundaries = [];
            for (const b of raw) {
                if (b > ws && b < we && boundaries.indexOf(b) < 0) boundaries.push(b);
            }
            boundaries.sort((a, b) => a - b);

            const authored = words[m].splitChars || EMPTY_SPLITS;
            const splits = boundaries.length === raw.length && isAuthoredValid(tokens[m], boundaries.length + 1, authored)
                ? authored.slice()
                : EMPTY_SPLITS;

            units.push({ text: tokens[m], start: ws, end: we, conf: clamp(words[m].score, 0, 1), syllables: boundaries, splits: splits });
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
            units.push({ text: tokens[i], start: unitStart, end: unitEnd, conf: 1, syllables: EMPTY_BOUNDARIES, splits: EMPTY_SPLITS });
        }
        return units;
    }

    const EMPTY_BOUNDARIES = [];
    const EMPTY_SPLITS = [];

    // ---------------------------------------------------------------------------
    // SyllableSegments: WHERE a subdivided word's characters are cut into syllable segments
    // (mirrors typebeat.Game.Rulesets.TypeBeat/Gameplay/SyllableSegments.cs). The ONE derivation
    // both the per-char targets and the judgement groups read, so the split a mapper authored on
    // the desktop timeline strip ("ap|ple") is exactly the split the browser judges on.
    //
    // A word carrying N boundaries has N + 1 segments and therefore N character splits. They come
    // from one of two places: the mapper AUTHORED them (split_chars) or they are DERIVED by the
    // syllabifier forced to the boundary count. Authored wins, but only while it is still VALID for
    // the word it is attached to; anything else silently falls back to derived rather than
    // throwing, because a split is a CHAR INDEX and any edit that retypes a word or changes its
    // boundary count can invalidate one.
    //
    // Splits index the TOKEN string (punctuation included, exactly like splitPoints), never the
    // typed cell stream. The conversion to cell space, which is what the target spread needs, is
    // cellCuts.
    // ---------------------------------------------------------------------------

    // Whether `authored` is a usable char split of `token` into `segments` segments: exactly
    // segments - 1 indices, strictly ascending, every one strictly inside (0, token.length) so no
    // segment is empty. An EMPTY list is "derived", not "valid", and returns false (the caller
    // falls back). Mirrors SyllableSegments.IsAuthoredValid.
    function isAuthoredValid(token, segments, authored) {
        if (!authored || authored.length === 0 || !token) return false;
        if (segments < 2 || authored.length !== segments - 1) return false;

        let previous = 0;

        for (const split of authored) {
            if (split <= previous || split >= token.length) return false;
            previous = split;
        }

        return true;
    }

    // The split the syllabifier picks for a word forced to `segments` segments. Can return FEWER
    // than segments - 1 indices on an over-forced short word (the syllabifier degrades rather than
    // inventing splits); buildSyllables has always tolerated that. Mirrors SyllableSegments.Derived.
    function derivedSplits(token, segments) {
        return (segments < 2 || !token) ? EMPTY_SPLITS : splitPoints(token, segments);
    }

    // The EFFECTIVE split: `authored` when it is valid, the derived split otherwise. This is the
    // function every reader goes through. Mirrors SyllableSegments.SplitsFor.
    function splitsFor(token, segments, authored) {
        return isAuthoredValid(token, segments, authored) ? authored : derivedSplits(token, segments);
    }

    // The split expressed in CELL space: cuts[s] is the number of isCell characters of `token`
    // before segment s starts, so cuts[0] === 0 and cuts[last] === k (the word's typeable char
    // count). Length is splits.length + 2. Mirrors SyllableSegments.CellCuts.
    //
    // This is the bridge the target spread needs: it counts what buildCells counts, so punctuation
    // (which is timed by interpolation, never by the per-word spread) rides inside whichever
    // segment surrounds it without spending a slot.
    function cellCuts(token, splits) {
        const cuts = new Array(splits.length + 2).fill(0);
        let cells = 0;
        let next = 0;

        for (let i = 0; i < token.length; i++) {
            while (next < splits.length && splits[next] === i) cuts[++next] = cells;
            if (isCell(token[i])) cells++;
        }

        while (next < splits.length) cuts[++next] = cells;

        cuts[cuts.length - 1] = cells;
        return cuts;
    }

    // The segment holding typeable-char index j: the LAST segment whose cut is at or before it.
    // Taking the last (rather than the first) is what makes a segment with no typeable char of its
    // own, a lone hyphen between two letters, transparent in exactly the way buildSyllables's group
    // assignment already makes it. Mirrors SyllableSegments.SegmentOf.
    function segmentOf(cuts, j) {
        const segments = cuts.length - 1;
        let s = 0;

        for (let i = 1; i < segments; i++) {
            if (cuts[i] <= j) s = i;
            else break;
        }

        return s;
    }

    // ---------------------------------------------------------------------------
    // Syllabifier: rule-based English syllabification of ONE gameplay word (mirrors
    // typebeat.Game.Rulesets.TypeBeat/Gameplay/Syllabifier.cs, rule for rule). This is scoring
    // surface since backlog 179: the groups it produces are the time SPANS a keypress is judged
    // against, so a split that lands one character off moves a real judgement.
    //
    // The analysis is orthographic phonotactics, not a dictionary: vowel groups are syllable
    // nuclei (y is a vowel when it is not an onset glide, and u is silent in qu/gu+vowel), a run
    // of adjacent vowels is one nucleus except for the common hiatus pairs (ia io iu ua uo eo, and
    // ie before t, plus -Ving), silent terminal e is dropped (including -ed/-es) except the
    // syllabic C+le/C+re case, and boundaries fall V|CV, VC|CV with inseparable onset clusters and
    // digraphs kept whole, and doubled consonants split down the middle. A small table pins very
    // common lyric words the rules cannot get right.
    //
    // Non-letter input is defensive territory: punctuation is transparent (it attaches to the
    // surrounding syllable and never starts one), and a maximal DIGIT run is one syllable of its
    // own ("24" is one group, "b2b" is three). Case is folded before analysis; indices always refer
    // to the original string.
    // ---------------------------------------------------------------------------

    // char.IsLetter / char.IsDigit are Unicode category tests in the C#, so these are too rather
    // than the ASCII ranges isTypeable uses: normalize already narrows gameplay text to ASCII, but
    // the syllabifier answers for whatever it is handed.
    const LETTER_RE = /\p{L}/u;
    const DIGIT_RE = /\p{Nd}/u;

    function isLetterChar(c) { return c !== undefined && LETTER_RE.test(c); }

    function isDigitChar(c) { return c !== undefined && DIGIT_RE.test(c); }

    // char.ToLowerInvariant: ONE char in, one char out. JS toLowerCase can lengthen a char (the
    // dotted capital I becomes two), which would shift every index the analysis reports, so a
    // lengthening fold is refused and the char is left as it was, exactly as the C# leaves it.
    function lowerChar(ch) {
        const lowered = ch.toLowerCase();
        return lowered.length === 1 ? lowered : ch;
    }

    // Very common lyric words whose pronunciation the orthographic rules cannot recover (mostly
    // medial silent e in compounds, and the ev(e)ry family which is sung with the first e elided).
    // Keyed on the exact lower-cased word, and held in a Map so a word like "constructor" cannot
    // pick an answer off Object.prototype.
    const SYLLABLE_EXCEPTIONS = new Map([
        ['people', [3]], // peo|ple: "eo" is one nucleus here, unlike vid|e|o
        ['every', [2]], // ev|ery, 2 syllables (sung form; formal ev|er|y is 3)
        ['everything', [2, 5]], // ev|ery|thing
        ['everyone', [2, 5]], // ev|ery|one
        ['everybody', [2, 5, 7]], // ev|ery|bo|dy
        ['everywhere', [2, 5]], // ev|ery|where
        ['something', [4]], // some|thing: medial silent e
        ['sometimes', [4]], // some|times
        ['somewhere', [4]], // some|where
        ['someone', [4]], // some|one
        ['somebody', [4, 6]], // some|bo|dy
        ['lovely', [4]], // love|ly: medial silent e before suffix
        ['lonely', [4]], // lone|ly
        ['maybe', [3]], // may|be: compound of may + be, final e is a nucleus
        ['million', [3]], // mil|lion: "io" fuses here but splits in li|on
        ['billion', [3]], // bil|lion
        ['create', [3]] // cre|ate: "ea" is hiatus here, unlike dream
    ]);

    // Consonant pairs that stay together as the onset of the following syllable (inseparable
    // clusters and digraphs): ta|ble, a|pron, no|thing, e|qual.
    const TWO_CLUSTERS = new Set([
        'bl', 'br', 'ch', 'cl', 'cr', 'dr', 'fl', 'fr', 'gh', 'gl', 'gr', 'ph', 'pl', 'pr',
        'qu', 'sc', 'sh', 'sk', 'sl', 'sm', 'sn', 'sp', 'st', 'sw', 'th', 'tr', 'tw', 'wh', 'wr'
    ]);

    // Three-consonant onsets: in|stru|ment.
    const THREE_CLUSTERS = new Set(['chr', 'phr', 'sch', 'scr', 'shr', 'spl', 'spr', 'squ', 'str', 'thr']);

    function isVowelLetter(c) { return c === 'a' || c === 'e' || c === 'i' || c === 'o' || c === 'u'; }

    function isSoftener(c) { return c === 'c' || c === 'g' || c === 's' || c === 't' || c === 'x'; }

    function isVowelish(c) { return isVowelLetter(c) || c === 'y'; }

    // Whether word looks like an English word the rules above can actually analyse, rather than a
    // STYLISED spelling (mirrors Syllabifier.IsSyllabifiable, backlog 178). ONE rule: a run of
    // THREE OR MORE identical LETTERS is not standard English, so the word fails. DOUBLED letters
    // are ordinary and pass (little, good, hello, ooh), digits never fail a word ("1000", "b2b"),
    // and punctuation only ever BREAKS a run. Case is folded. Null/empty is false.
    //
    // This is the gate buildSyllables uses to decide whether a token gets groups at all: a word
    // that fails is left UNGROUPED, so its cells keep the classic per-character point judgement.
    function isSyllabifiable(word) {
        if (!word) return false;

        // Compare against the PREVIOUS character rather than carrying a sentinel: a run only ever
        // extends when both ends are the same letter, so a digit or a mark fails the test and
        // resets the count.
        let run = 1;

        for (let i = 1; i < word.length; i++) {
            const c = lowerChar(word[i]);
            const previous = lowerChar(word[i - 1]);

            run = (isLetterChar(c) && c === previous) ? run + 1 : 1;

            if (run >= 3) return false;
        }

        return true;
    }

    // Number of syllables in an English word: never less than 1 for a non-empty word, 0 for empty.
    function countSyllables(word) {
        if (!word) return 0;
        return naturalSplits(word).length + 1;
    }

    // The 0-based indices into word at which a new syllable STARTS, strictly ascending, never
    // containing 0, length == syllables - 1 (mirrors Syllabifier.SplitPoints).
    //
    // forcedCount, when given (a mapper's hand-authored subtimings say how many syllables the word
    // has), is authoritative and the natural analysis is bent to hit it: extra splits are added at
    // the best remaining interior position of the longest group, surplus boundaries are merged
    // weakest (smallest merged group) first. Over-forcing degrades gracefully, because a word
    // carries at most length - 1 splits. A forced count below 1 is treated as 1.
    function splitPoints(word, forcedCount) {
        if (!word) return [];

        const splits = naturalSplits(word);

        if (forcedCount === undefined || forcedCount === null) return splits;

        const target = clamp(forcedCount - 1, 0, word.length - 1);

        while (splits.length > target) mergeWeakest(splits, word.length);
        while (splits.length < target && addBestSplit(splits, word)) { /* keep adding while there is room */ }

        return splits;
    }

    function naturalSplits(word) {
        let lower = '';
        for (let i = 0; i < word.length; i++) lower += lowerChar(word[i]);

        const pinned = SYLLABLE_EXCEPTIONS.get(lower);
        if (pinned) return pinned.slice();

        const boundaries = [];
        const segment = []; // original indices of the current letter run (punctuation is transparent)
        let anyContent = false;
        let inDigits = false;

        for (let i = 0; i < lower.length; i++) {
            const c = lower[i];

            if (isLetterChar(c)) {
                if (inDigits) {
                    // the digit run just before this letter was its own syllable.
                    boundaries.push(i);
                    inDigits = false;
                }

                segment.push(i);
                anyContent = true;
            } else if (isDigitChar(c)) {
                if (!inDigits) {
                    flushSegment(lower, segment, boundaries);

                    if (anyContent) boundaries.push(i);

                    inDigits = true;
                    anyContent = true;
                }
            }

            // anything else (apostrophes, punctuation, the freestyle marker) is transparent: it
            // attaches to whichever syllable surrounds it and never starts one.
        }

        flushSegment(lower, segment, boundaries);
        return boundaries;
    }

    function flushSegment(lower, segment, boundaries) {
        if (segment.length === 0) return;
        analyzeLetters(lower, segment, boundaries);
        segment.length = 0;
    }

    // The phonotactic core: syllabifies one contiguous letter sequence (seg holds the original
    // index of each letter) and appends the resulting split indices to boundaries.
    function analyzeLetters(lower, seg, boundaries) {
        const n = seg.length;
        let s = '';

        for (let j = 0; j < n; j++) s += lower[seg[j]];

        const endsIng = n >= 4 && s[n - 3] === 'i' && s[n - 2] === 'n' && s[n - 1] === 'g';

        // 1. classify vowels.
        const vowel = new Array(n).fill(false);

        for (let j = 0; j < n; j++) {
            const c = s[j];

            if (isVowelLetter(c)) vowel[j] = true;
            else if (c === 'y') {
                const next = j + 1 < n ? s[j + 1] : '\0';

                if (!isVowelLetter(next)) vowel[j] = true; // no following vowel: y is the nucleus (rhythm, happy, cry)
                else if (next === 'e' && j + 1 === n - 1) vowel[j] = true; // word-final "ye": the e is silent (goodbye, dye)
                else if (endsIng && j === n - 4) vowel[j] = true; // "-ying": dy|ing, say|ing (the split is forced below)

                // otherwise y is an onset glide (yes, beyond, canyon)
            }
        }

        // u is not a nucleus in "qu" (quiet, equal) nor in "gu" + vowel (guard, guess, league).
        for (let j = 1; j < n; j++) {
            if (s[j] === 'u' && vowel[j] && (s[j - 1] === 'q' || (s[j - 1] === 'g' && j + 1 < n && isVowelLetter(s[j + 1])))) {
                vowel[j] = false;
            }
        }

        // 2. nucleus groups: maximal vowel runs, broken at hiatus pairs.
        const groups = [];

        for (let j = 0; j < n; j++) {
            if (!vowel[j]) continue;

            const start = j;

            while (j + 1 < n && vowel[j + 1] && !isHiatus(s, endsIng, j)) j++;

            groups.push({ start: start, end: j });
        }

        // 3. silent final e. Only ever a single-vowel group (a preceding vowel keeps it: goes,
        // memories) and only when another nucleus remains (the, be, bye keep theirs).
        let syllabicLe = false;

        if (groups.length >= 2) {
            const last = groups[groups.length - 1];

            if (last.start === last.end && s[last.start] === 'e') {
                const p = last.start;
                let silent = false;

                if (p === n - 1) {
                    // plain final e: silent (make, love, alone) except syllabic C+le / C+re (table,
                    // little, acre), where the e-group survives as the last syllable.
                    const prev = s[p - 1];
                    syllabicLe = (prev === 'l' || prev === 'r') && p >= 2 && !vowel[p - 2];
                    silent = !syllabicLe;
                } else if (p === n - 2 && s[n - 1] === 'd') {
                    // -ed: silent (loved, called) unless after t/d (wanted, needed).
                    silent = s[p - 1] !== 't' && s[p - 1] !== 'd';
                } else if (p === n - 2 && s[n - 1] === 's') {
                    // -es: silent (makes, times, clothes) unless after a sibilant (wishes, changes).
                    const prev = s[p - 1];
                    const sibilant = prev === 's' || prev === 'z' || prev === 'x' || prev === 'c' || prev === 'g'
                        || (prev === 'h' && p >= 2 && (s[p - 2] === 's' || s[p - 2] === 'c'));
                    silent = !sibilant;
                }

                if (silent) groups.pop();
            }
        }

        // 4. place one boundary between each pair of adjacent nuclei.
        for (let g = 1; g < groups.length; g++) {
            const prevEnd = groups[g - 1].end;
            const curStart = groups[g].start;
            const gap = curStart - prevEnd - 1;
            let split;

            if (gap === 0) split = curStart; // hiatus: the new nucleus starts the syllable (qui|et, radi|o)
            else if (gap === 1) split = prevEnd + 1; // V|CV: the consonant onsets the second syllable (o|pen)
            else if (syllabicLe && g === groups.length - 1) split = curStart - 2; // the syllabic-le/re syllable is Cle/Cre (tur|tle, a|cre)
            else if (s[prevEnd + 1] === s[prevEnd + 2]) split = prevEnd + 2; // doubled consonant splits down the middle (bet|ter)
            else if (gap >= 3 && THREE_CLUSTERS.has(s.slice(curStart - 3, curStart))) split = curStart - 3; // in|stru...
            else if (TWO_CLUSTERS.has(s.slice(curStart - 2, curStart))) split = curStart - 2; // ta|ble, no|thing
            else split = curStart - 1; // otherwise a single-consonant onset (VC|CV, pump|kin)

            boundaries.push(seg[split]);
        }
    }

    // Whether adjacent vowels at j, j+1 are two nuclei (hiatus) rather than one digraph.
    function isHiatus(s, endsIng, j) {
        const a = s[j], b = s[j + 1];
        const k = j + 1;

        // vowel + "-ing" is always two syllables: be|ing, go|ing, dy|ing, say|ing.
        if (endsIng && b === 'i' && k === s.length - 3) return true;

        // ...except -tion/-cial/-gion/-cious style endings, where the i fuses with the preceding
        // softened consonant: na|tion, spe|cial, but radi|o, med|i|a, gen|i|us.
        if (a === 'i' && (b === 'a' || b === 'o' || b === 'u')) return j === 0 || !isSoftener(s[j - 1]);

        // qui|et, di|et; believe, friend, die stay fused.
        if (a === 'i' && b === 'e') return k + 1 < s.length && s[k + 1] === 't';

        // vide|o, stere|o split; gor|geous (a softener before) does not.
        if (a === 'e' && b === 'o') return j === 0 || !isSoftener(s[j - 1]);

        // u|su|al, act|u|al, du|o
        if (a === 'u' && (b === 'a' || b === 'o')) return true;

        return false;
    }

    // Removes the boundary whose removal produces the smallest merged group (the weakest boundary
    // separates the two smallest neighbours); leftmost on ties.
    function mergeWeakest(splits, length) {
        let best = 0;
        let bestMerged = Infinity;

        for (let i = 0; i < splits.length; i++) {
            const left = i === 0 ? 0 : splits[i - 1];
            const right = i === splits.length - 1 ? length : splits[i + 1];
            const merged = right - left;

            if (merged < bestMerged) {
                bestMerged = merged;
                best = i;
            }
        }

        splits.splice(best, 1);
    }

    // Adds one split at the best interior position of the longest current group: prefer a
    // vowel-to-consonant transition (a fresh onset: fi|re), then consonant-to-vowel, then inside a
    // vowel run, then anywhere; nearest the group's midpoint on ties. Returns false when every
    // group is a single character, so no position remains.
    function addBestSplit(splits, word) {
        let bestStart = -1, bestLen = 1;

        for (let i = 0; i <= splits.length; i++) {
            const start = i === 0 ? 0 : splits[i - 1];
            const end = i === splits.length ? word.length : splits[i];

            if (end - start > bestLen) {
                bestLen = end - start;
                bestStart = start;
            }
        }

        if (bestStart < 0) return false;

        const mid = bestStart + bestLen / 2;
        let bestPos = -1;
        let bestClass = Infinity;
        let bestDist = Infinity;

        for (let p = bestStart + 1; p < bestStart + bestLen; p++) {
            const cls = transitionClass(word, p);
            const dist = Math.abs(p - mid);

            if (cls < bestClass || (cls === bestClass && dist < bestDist)) {
                bestClass = cls;
                bestDist = dist;
                bestPos = p;
            }
        }

        // List.BinarySearch + Insert(~idx): bestPos is strictly interior to a group and so is never
        // already present, which makes this exactly the insertion point that keeps splits sorted.
        let idx = 0;
        while (idx < splits.length && splits[idx] < bestPos) idx++;
        splits.splice(idx, 0, bestPos);
        return true;
    }

    function transitionClass(word, p) {
        const a = lowerChar(word[p - 1]);
        const b = lowerChar(word[p]);

        if (!isLetterChar(a) || !isLetterChar(b)) return 4;

        const va = isVowelish(a), vb = isVowelish(b);

        if (va && !vb) return 1; // V|C: the consonant onsets the new group
        if (!va && vb) return 2; // C|V
        if (va && vb) return 3; // splitting inside a vowel run (fi|re when over-forced)

        return 4; // C|C
    }

    // Groups a line's cells into SYLLABLES (mirrors TypingLine.buildSyllables, backlog 174/178/179):
    // per whitespace token, the syllabifier decides WHICH characters form each syllable and the
    // timing data decides WHEN it is sung. Pure derivation: no cell target moves, so the classic
    // sweep and every readout built on it stay byte-identical.
    //
    // A token whose unit carries mapper subtimings (N boundaries = N + 1 syllables) is split with
    // the count FORCED to N + 1: the boundary times are the window edges, so syllable i spans
    // [edge_i, edge_i+1] with edge_0 the unit's start, the interior edges the boundary times and
    // the last edge the unit's end. When the syllabifier degrades to G < N + 1 groups (an
    // over-forced short word) the first G - 1 boundary times are the interior edges and the last
    // group runs to the unit's end.
    //
    // A token WITHOUT subtimings is split naturally and each group's span is read off the EXISTING
    // flat-ramp char targets: it starts at its first cell's target and ends where the next group
    // starts (last group of the token: the unit's end). That natural arm is GATED on
    // isSyllabifiable: a stylised spelling like "wooooooords" gets NO groups and its cells stay at
    // syllableIndexOf -1, keeping the classic per-character point judgement. The gate does NOT
    // apply to a subtimed token: the mapper hand-authored its count, and that is authoritative.
    //
    // Split indices index the TOKEN string and are mapped to cells through the same projection that
    // assigned the targets, so punctuation lands inside the syllable of the letter it attaches to.
    // A SPACE cell (the inter-word gap, or a hyphen turned into a typed space) belongs to NO group,
    // a group whose every character the default stream deleted is dropped, and kept spans are
    // clamped monotonic non-decreasing, the same guard the targets themselves get.
    function buildSyllables(text, units, lineStart, singEndTime, cells, sources) {
        const rawGroup = new Array(text.length).fill(-1);

        // Provisional groups in token order: span edges, and whether the span is still to be
        // resolved from cell targets (NaN start; NaN end = "the next group's start").
        const starts = [];
        const ends = [];
        const tokens = text.split(' ');
        let tokStart = 0;

        for (let m = 0; m < tokens.length; m++) {
            const token = tokens[m];

            // Same malformed-data clamp as the target-time walk in buildCells.
            const unit = units.length > 0 ? units[Math.min(m, units.length - 1)] : null;
            const unitStart = unit ? unit.start : lineStart;
            const unitEnd = unit ? unit.end : singEndTime;
            const boundaries = (unit && unit.syllables) ? unit.syllables : EMPTY_BOUNDARIES;
            const subtimed = boundaries.length > 0;

            // A stylised spelling gets no groups at all UNLESS the mapper subtimed it, in which case
            // the hand-authored count wins over anything the rules would have guessed.
            if (token.length > 0 && (subtimed || isSyllabifiable(token))) {
                const splits = subtimed
                    ? splitsFor(token, boundaries.length + 1, unit ? unit.splits : null)
                    : splitPoints(token);
                const groupBase = starts.length;
                const groupCount = splits.length + 1;

                for (let g = 0; g < groupCount; g++) {
                    if (subtimed) {
                        starts.push(g === 0 ? unitStart : boundaries[g - 1]);
                        ends.push(g === groupCount - 1 ? unitEnd : boundaries[g]);
                    } else {
                        starts.push(NaN);
                        ends.push(g === groupCount - 1 ? unitEnd : NaN);
                    }
                }

                // Token char t belongs to the group of the last split at or before it, so
                // punctuation (transparent to the syllabifier) attaches to the syllable around it.
                let inGroup = 0;

                for (let t = 0; t < token.length; t++) {
                    if (inGroup < splits.length && t === splits[inGroup]) inGroup++;
                    rawGroup[tokStart + t] = groupBase + inGroup;
                }
            }

            tokStart += token.length + 1; // the inter-word space raw char stays in no group
        }

        // Map raw-index groups onto cells through the same projection that assigned the targets. A
        // SPACE cell is in no group whatever raw char produced it (hyphens too).
        const cellSyllable = new Array(cells.length).fill(-1);

        for (let i = 0; i < cells.length; i++) {
            if (!cells[i].typeable || cells[i].expected === ' ') continue;
            cellSyllable[i] = rawGroup[sources ? sources[i] : i];
        }

        const provisional = starts.length;
        const firstCell = new Array(provisional).fill(-1);
        const lastCell = new Array(provisional).fill(0);

        for (let i = 0; i < cellSyllable.length; i++) {
            const g = cellSyllable[i];

            if (g < 0) continue;

            if (firstCell[g] < 0) firstCell[g] = i;
            lastCell[g] = i;
        }

        // Resolve the target-derived spans. Starts first (a group starts at its first cell's
        // target), then the NaN ends: only a non-last group of an un-subtimed token has one, and its
        // successor sits in the same token and always owns a letter or digit cell, so its start is
        // known; the fallback degenerate span is defensive only.
        for (let g = 0; g < provisional; g++) {
            if (isNaN(starts[g]) && firstCell[g] >= 0) starts[g] = cells[firstCell[g]].target;
        }

        for (let g = 0; g < provisional; g++) {
            if (!isNaN(ends[g])) continue;

            const next = g + 1 < provisional ? starts[g + 1] : NaN;
            ends[g] = isNaN(next) ? starts[g] : next;
        }

        // Compact to the groups that own at least one cell, clamping spans monotonic.
        const groups = [];
        const remap = new Array(provisional).fill(-1);
        let clock = -Infinity;

        for (let g = 0; g < provisional; g++) {
            if (firstCell[g] < 0 || isNaN(starts[g])) continue;

            const start = Math.max(starts[g], clock);
            const end = Math.max(isNaN(ends[g]) ? start : ends[g], start);
            clock = end;

            remap[g] = groups.length;
            groups.push({ startCell: firstCell[g], endCellExclusive: lastCell[g] + 1, startTime: start, endTime: end });
        }

        for (let i = 0; i < cellSyllable.length; i++) {
            cellSyllable[i] = cellSyllable[i] >= 0 ? remap[cellSyllable[i]] : -1;
        }

        return { syllables: groups, cellSyllable: cellSyllable };
    }

    // How many identical characters in a row make a STRETCH (backlog 209, mirrors
    // TypingLine.STRETCH_RUN_LENGTH). Three, which is isSyllabifiable's own threshold for calling a
    // spelling stylised, so the two answers about "hey" versus "heyyy" cannot disagree.
    const STRETCH_RUN_LENGTH = 3;

    // The STRETCH flags (backlog 209, mirrors TypingLine.buildCharTimedStretch), derived once from
    // the cells and their group membership. ADDITIVE: it reads cellSyllable and never writes it, so
    // no cell's group, target or span moves and every construction pin on this line (the parity
    // fixtures included) sees exactly what it saw before.
    //
    // One left-to-right pass. A run extends while the next cell sits in the SAME group (a cell in no
    // group can never extend one, so spaces, punctuation the default stream isolated and whole
    // stylised tokens all break runs) and its character folds equal to the run's. A closed run of
    // three or more marks all of its cells; a freestyle cell is marked whatever its neighbours are.
    function buildCharTimedStretch(cells, cellSyllable) {
        const flags = new Array(cells.length);

        for (let i = 0; i < cells.length; i++) flags[i] = cells[i].freestyle;

        let runStart = 0;

        for (let i = 1; i <= cells.length; i++) {
            const extends_ = i < cells.length
                && cellSyllable[i] >= 0
                && cellSyllable[i] === cellSyllable[runStart]
                && fold(cells[i].expected) === fold(cells[runStart].expected);

            if (extends_) continue;

            if (cellSyllable[runStart] >= 0 && i - runStart >= STRETCH_RUN_LENGTH) {
                for (let j = runStart; j < i; j++) flags[j] = true;
            }

            runStart = i;
        }

        return flags;
    }

    // Index into line.syllables of the group that judges cell cellIndex, or -1 when the cell is in
    // no group (space cells, an unsyllabifiable token's cells, and any out-of-range index). Mirrors
    // TypingLine.SyllableIndexOf: membership is read through this and NEVER by cell range, because
    // an ungrouped cell can sit positionally inside a group's [startCell, endCellExclusive).
    function syllableIndexOf(line, cellIndex) {
        const map = line.cellSyllable;
        return (map && cellIndex >= 0 && cellIndex < map.length) ? map[cellIndex] : -1;
    }

    // Whether cell cellIndex is a STRETCH cell (backlog 209, mirrors TypingLine.IsCharTimedStretch):
    // one whose identity does not say WHEN inside its syllable it was meant to be pressed, so the
    // span rule would hand it a delta of 0 for a press anywhere in the syllable. Two kinds qualify:
    // a FREESTYLE cell, which accepts any key at all, and a cell inside a run of three or more
    // consecutive cells of the same syllable whose characters fold equal (the "000" of "1000", the
    // "yyyy" of a subtimed "hey|yyyy").
    //
    // Purely structural, and deliberately NOT a grouping change: these cells stay in their syllable,
    // because the lyric stack lights GROUPS and an ungrouped stretch would stop being highlighted
    // while it is sung. Only judgedDeltaFor reads it.
    function isCharTimedStretch(line, cellIndex) {
        const flags = line.charTimedStretch;
        return !!(flags && cellIndex >= 0 && cellIndex < flags.length && flags[cellIndex]);
    }

    // Target time of typeable char j (0-based, of k in the word) under piecewise-linear syllable
    // timing (mirrors TypingLine.syllableCharTarget). The word spans [unitStart, unitEnd]; each
    // entry of boundaries (absolute ms, strictly inside, ascending) splits it into one more
    // segment, and the k chars are distributed evenly by index across the segments. With no
    // boundaries this is exactly unitStart + j*(unitEnd-unitStart)/k; char j = 0 lands on unitStart.
    //
    // `cuts`, when given (cellCuts of an AUTHORED split, backlog 181), replaces that even
    // distribution with the mapper's own: segment s covers cell-index range [cuts[s], cuts[s+1])
    // instead of [s*k/S, (s+1)*k/S], so "ap|ple" puts two chars on the first syllable and three on
    // the second however long the word is. Null means derived, and the arithmetic below is then
    // untouched, which is what makes a map with no authored split flatten byte-identically.
    function syllableCharTarget(unitStart, unitEnd, boundaries, k, j, cuts) {
        if (k <= 0) return unitStart;
        if (boundaries.length === 0) return unitStart + j * (unitEnd - unitStart) / k;

        const segments = boundaries.length + 1;
        let s, segIndexLo, segIndexHi;

        if (cuts && cuts.length === segments + 1) {
            s = segmentOf(cuts, j);
            segIndexLo = cuts[s];
            segIndexHi = cuts[s + 1];
        } else {
            // Which segment holds char index j (floor of j scaled into segment-space), clamped to last.
            s = Math.floor(j * segments / k);
            if (s >= segments) s = segments - 1;

            segIndexLo = s * k / segments;
            segIndexHi = (s + 1) * k / segments;
        }

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

            // An AUTHORED char split (backlog 181, "ap|ple") replaces the even distribution within
            // the word: the mapper's own cut says how many chars ride each segment, so the same
            // split drives the targets here and the judgement groups in buildSyllables. Derived
            // (absent, or stale) leaves the index-even spread untouched.
            const cuts = (unit && isAuthoredValid(token, boundaries.length + 1, unit.splits))
                ? cellCuts(token, unit.splits)
                : null;

            let j = 0;
            for (let t = 0; t < token.length; t++) {
                const ch = token[t];
                expected[pos] = ch;
                tiers[pos] = tier;
                if (isCell(ch)) {
                    typeableFlags[pos] = true;
                    targets[pos] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j, cuts);
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

        // The projection each cell came through, or null under Literate where a cell IS its
        // authored char (mirrors TypingLine.FromLyricLine's defaultSources). buildSyllables maps
        // token split indices onto cells through exactly this, so it is returned rather than
        // recomputed: one projection, one answer.
        let sources = null;

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
            sources = projected.sources;
            for (let i = 0; i < projected.text.length; i++) {
                const src = projected.sources[i];
                const ch = projected.text[i];
                cells.push(newCell(ch, targets[src], tiers[src], isCell(ch)));
            }
        }

        return { cells: cells, sources: sources };
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
                    // Optional AUTHORED character split (type!beat editor extension, backlog 181):
                    // the char indices at which each syllable segment starts. Read RAW here and
                    // validated in buildExplicitUnits, which is the only place that knows how many
                    // boundaries actually survived clamping. A non-numeric or fractional entry is
                    // SKIPPED rather than fatal, exactly as TimingJsonLoader.tryGetInt skips it: a
                    // string "2" is not a JSON number and must not decode as one.
                    const splitChars = [];
                    if (Array.isArray(w.split_chars)) {
                        for (const s of w.split_chars) {
                            if (typeof s !== 'number' || !isFinite(s) || Math.floor(s) !== s) continue;
                            if (s < -2147483648 || s > 2147483647) continue; // int32, as the C# reader parses it
                            splitChars.push(s);
                        }
                    }
                    words.push({ text: typeof w.text === 'string' ? w.text : '', start: ws, end: we, score: score, syllables: syllables, splitChars: splitChars });
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

            const flattened = buildCells(line.text, units, line.estimated, granularity, literate);
            const cells = flattened.cells;

            // The line's syllable GROUPS, built ALWAYS (cheap and pure) because they are what a
            // keypress on a grouped cell is judged against (see TypingEngine.judgedDeltaFor).
            const grouped = buildSyllables(line.text, units, start, singEndTime, cells, flattened.sources);

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
                // TypingLine.Syllables: ordered, non-overlapping cell ranges with the time span each
                // syllable is sung over. Coverage is PARTIAL and every consumer must tolerate a gap,
                // so membership is read through syllableIndexOf and never by range.
                syllables: grouped.syllables,
                // TypingLine.cellSyllable: per display cell, the index into syllables, or -1.
                cellSyllable: grouped.cellSyllable,
                // TypingLine.charTimedStretch: per display cell, whether the span rule is NARROWED
                // back to the cell's own target for it (backlog 209). Derived from the cells and the
                // membership map above, which it only reads, so nothing it touches moves.
                charTimedStretch: buildCharTimedStretch(cells, grouped.cellSyllable)
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
            // untyped | correct | wrong | missed | autoskip | abandoned.
            //
            // ABANDONED (CellState.Abandoned, backlog 167) is the PHANTOM state a word skip leaves
            // behind: not a resolution, just a character the player has walked past and may still
            // come back into. It resolves nothing (no osu result is applied at the skip, which is
            // exactly what leaves the cell earnable), it holds its line open like an untyped cell,
            // and it leaves the state by exactly one of two exits: a backspace steps transparently
            // back over it (processBackspace) or the line seals on it and it becomes the miss it
            // turned out to be (sealLine).
            state: 'untyped',
            // Great | Ok | Meh | Premature | Lagging | Miss | WrongChar | Abandoned
            judgeType: null,
            typedChar: null,
            judgedDelta: null,
            firstCorrectDelta: null,
            // TypingCell.HeldWrongBeforeJudged (backlog 210): whether a wrong character was typed
            // into this cell at a time when it had not yet been judged, i.e. whether the cell's ONE
            // awarded judgement is (or will be) a CORRECTION rather than a clean first attempt. The
            // judging arms of processKey cap such a cell at 'Ok' (see awardedTier).
            //
            // HISTORY, unlike `state`: set once, never cleared, and it survives the backspace that
            // erases the wrong character, which is the whole of what it is for. Only a fresh engine
            // (the constructor's per-cell wipe, mirroring TypingEngine.reset) puts it back.
            heldWrongBeforeJudged: false,
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
    // Judgement windows (Judgement.cs SyncWindows).
    // ---------------------------------------------------------------------------
    const BASE_WINDOWS = { ge: 250, gl: 400, oe: 600, ol: 1000, me: 1200, ml: 2000 };
    const TIER_SCALE = { Line: 1.0, Word: 0.6, Syllable: 0.45 };

    // The C# carries one more factor here since backlog 149: TypingEngine.WindowScale, a
    // multiplicative scale a mod may put on every window. Easy doubles them, Hard Rock halves them,
    // and since backlog 150 a rate mod multiplies the clock rate in as well, so the real-time
    // tolerance is the same at every speed. None of it is mirrored, because /play has no mods
    // payload at all (see the scoreMultiplier note where the total is computed) and no rate control
    // either (see the update() note about clockRate), so the browser's scale is permanently 1 and
    // the C# at 1 is bit-identical to this. The day browser play gains a mods payload or a rate,
    // this is where the scale has to arrive.
    function windowsFor(tier) {
        const s = TIER_SCALE[tier] != null ? TIER_SCALE[tier] : 1.0;
        return { ge: BASE_WINDOWS.ge * s, gl: BASE_WINDOWS.gl * s, oe: BASE_WINDOWS.oe * s, ol: BASE_WINDOWS.ol * s, me: BASE_WINDOWS.me * s, ml: BASE_WINDOWS.ml * s };
    }

    function classify(delta, w) {
        if (delta >= -w.ge && delta <= w.gl) return 'Great';
        if (delta >= -w.oe && delta <= w.ol) return 'Ok';
        if (delta >= -w.me && delta <= w.ml) return 'Meh';
        return delta < -w.me ? 'Premature' : 'Lagging';
    }

    function basePoints(type) {
        return type === 'Great' ? 300 : type === 'Ok' ? 150 : type === 'Meh' ? 50 : 0;
    }

    // TypeBeatResultMapping.AwardedTier (backlog 210). The tier a correct keypress is AWARDED, given
    // the tier the clock classified it as and whether its cell held a wrong character before it was
    // ever judged: a CORRECTED cell resolves at min(that tier, 'Ok'), so perfect play strictly beats
    // corrected play per cell. Before it, a word typed wrong and fixed could score bit-identically to
    // a word typed right, because the mistype is accuracy-inert by design and the corrected cell's
    // deferred result was graded purely on the RETYPE's timing.
    //
    // ONLY 'Great' MOVES, and that is the whole of the min(): of the tiers a correct press can be
    // classified as, 'Great' is the only one ABOVE 'Ok'. 'Meh' is already below the cap, and
    // 'Premature' / 'Lagging' are off the ladder entirely (they resolve as a 'meh' through
    // toHitResult), so a capped cell struck off time is untouched here and that rule keeps its own
    // answer. Written as a min over the ladder rather than as "Great becomes Ok" because the ladder
    // is what the rule is about.
    //
    // APPLIED TO THE TIER AND NOT TO THE osu RESULT, exactly as the C# applies it, and both of that
    // decision's reasons bind here too. Everything downstream follows from the one tier (the point
    // ladder, `counts`, the result applyCellResult stores, the judgement onCharJudged announces to
    // typebeat-player.js), so a result-level cap would show the player a Great while storing an Ok,
    // and the inert-retype arm below announces a tier without applying any result at all, so there
    // would be nothing there for a result-level cap to reach.
    //
    // UNCONDITIONAL, with no era arm, for the reason written on `restorable` and on the span rule:
    // the browser only ever plays live, writes no replay frames and re-derives no stored row, so
    // CorrectionCreditRule.Full (the pre-210 arm, which a stored row is re-derived under by the
    // recalculation tool) is one it can never be in and the clause collapses to the live
    // CorrectionCreditRule.Capped.
    function awardedTier(type, heldWrongBeforeJudged) {
        if (!heldWrongBeforeJudged) return type;
        return type === 'Great' ? 'Ok' : type;
    }

    // Engine judgement -> osu HitResult (DrawableTypeBeatHitObject.toHitResult, i.e.
    // TypeBeatResultMapping.CellResult). The three quality tiers are the identity, an OFF-TIME
    // press ('Premature' / 'Lagging', the right character struck outside the outermost Meh window)
    // is a 'meh' since backlog 199, and only a genuinely untyped cell is a miss.
    //
    // THE OFF-TIME MAPPING IS HALF OF BACKLOG 199, the other half being the keypress arm in
    // processKey, and the two have to say the same thing or the engine's combo and the processor's
    // part company. 'meh' is the lowest weight a judged cell can take (50 of 300), so ACCURACY is
    // the whole punishment: the press stops counting as a miss statistic, stops costing completion
    // and rank, and EXTENDS the submitted combo exactly as the engine now extends its own, which is
    // why nothing about it has to be hand-mirrored at the seam.
    //
    // The collision this accepts (and it is accepted, not overlooked): an off-time press and a
    // press that landed just INSIDE the Meh window arrive here as the same result, so no consumer
    // of the submitted statistics can tell them apart. The candidate set is forced (a cell may only
    // ever resolve as one of miss/meh/ok/good/great, and 'good' is spent on the unfixed typo), so
    // there is no free key to keep them apart, and the distinction survives everywhere it is
    // actually used live: 'Premature' and 'Lagging' are still their own judgement tiers, still
    // counted separately in `counts`, and typebeat-player.js still tints them apart.
    //
    // The two DEFERRED judgements never reach this function, on either side: 'WrongChar' (backlog
    // 109) and 'Abandoned' (backlog 167) map to NO result at all, which is precisely what leaves
    // the cell's one and only result available to a later retype. Neither is passed here, because
    // neither path calls applyCellResult: the typo branch of processKey applies nothing, and
    // skipCurrentWord applies nothing. Both cells are finally resolved either by that retype or by
    // sealLine, which picks the result itself rather than asking here.
    //
    // There is no era axis, unlike the C#'s OffTimeRule.BreaksCombo arm, for the reason written on
    // `restorable`: the browser only ever plays live, so the pre-199 rule (an off-time press
    // resolving as a Miss that broke the run) is one it can never be in.
    function toHitResult(judgeType) {
        switch (judgeType) {
            case 'Great': return 'great';
            case 'Ok': return 'ok';
            case 'Meh': return 'meh';
            case 'Premature': return 'meh'; // an off-time press is a HIT since backlog 199...
            case 'Lagging': return 'meh';
            default: return 'miss'; // ...and only 'Miss' or an untyped cell reaches here now.
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
    //      Great/Ok/Meh are the identity on the osu results of the same names and INCREASE combo;
    //      Premature/Lagging become Meh since backlog 199 and increase it too, which is the whole
    //      of what an off-time press now costs the account: Meh is the lowest weight a judged cell
    //      can take, so it pays the most ACCURACY available and nothing else, and the engine
    //      extending its own run on the same press is what keeps the two counters together with
    //      nothing mirrored by hand. It also makes an off-time press indistinguishable in the
    //      submitted statistics from a press that landed just inside the Meh window, which is
    //      accepted rather than overlooked (see toHitResult). A WrongChar becomes NOTHING (backlog
    //      109): ApplyCharJudgement returns before applying anything, so a typo defers its cell's
    //      result instead of spending it on a Miss. The cell drawable applies at most ONE result
    //      ever (`if (Judged) return;`), so a backspace-and-retype after a typo is that cell's real
    //      (first) result, while an inert retype of an already-correct cell moves nothing.
    //   2. TypeBeatPlayfield.onMistyped -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatScoreProcessor.RecordMistype. One seam for BOTH input models since backlog 109,
    //      because neither raises a result for a wrong keypress any more: a rejected key never did,
    //      and a typed-through one no longer does. osu's combo is maintained incrementally off
    //      results, so the break has to be mirrored by hand or max_combo would count on through it.
    //      Nothing else moves: no result count, no accuracy, no combo portion, and RecordMistype is
    //      a pure counter (routing it through ApplyResult would move the judged count too).
    //   3. TypeBeatPlayfield.onComboRestored -> TypeBeatScoreProcessor.RestoreCombo (backlog 140).
    //      Correcting the cell a wrong keypress spoiled RESUMES the streak that keypress broke, and
    //      like the break in 2 it travels on no judgement result, so it is mirrored by hand at the
    //      same seam. Applied as a DELTA and pushing HighestCombo itself, for the reasons written on
    //      restoreCombo below.
    //   4. TypeBeatPlayfield.onWrongKeyRejected -> the mash-guard HP drain only, which is why this
    //      mirror has no counterpart for it beyond engine.consecutiveWrongKeys: the browser models
    //      health as a derived read (see `health`), not as an account.
    //   5. DrawableTypeBeatHitObject.ApplySealResults, when the engine seals a line: every
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
    //   6. TypeBeatPlayfield.onWordAbandoned -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatPlayfield.onAbandonSealed -> MarkComboNeutral on every cell the skip gave up
    //      (backlog 167). A word skip resolves NOTHING at the keypress, so like the seams in 2 and 3
    //      it has no result to carry its break and mirrors it by hand; and the Misses those cells
    //      finally take at the seal must not take that break a second time, which is the same ledger
    //      as the typo in 5, redeemed in the same place but in the opposite direction (a break to
    //      suppress rather than an increment). onAbandonReclaimed is not mirrored at all: it carries
    //      health, and health here is a derived read rather than an account.
    //
    // Judgement rewind (ScoreProcessor.RevertResultInternal) has no counterpart: gameplay here is
    // never rewound, and neither is the desktop client's outside replay seeking.
    // ---------------------------------------------------------------------------

    // ScoreProcessor.GetBaseScoreForResult for the five results a type!beat cell can take. `good`
    // is the uncorrected-typo tier and is 0 since backlog 213, NOT the base game's 200 and no
    // longer the 50 backlog 124 gave it: an uncorrected typo IS a miss, because the player did not
    // put that character in that cell. The desktop client re-weights the tier the same way
    // (TypeBeatScoreProcessor.GetBaseScoreForResult) and the server's ScoringContract.BaseScore
    // carries the same 0; all three must move together or a browser score and a desktop score of
    // the same run stop agreeing on the shared leaderboards.
    //
    // UNCONDITIONAL here, where the desktop client's re-weight is era-gated by
    // UnfixedTypoWorthRule. The browser has no era axis at all (it only plays live, and writes no
    // replay to re-derive), which is the same reason it judges on syllable spans unconditionally.
    //
    // MAX_RESULT_BASE_SCORE below does not move with it: the cell's maximum is still a Great, so
    // the cell stays in the accuracy fraction and pays 0 of 300 rather than quietly leaving it.
    const HIT_BASE_SCORE = { great: 300, ok: 100, meh: 50, good: 0, miss: 0 };

    // Every cell judgement declares MaxResult = Great (TypeBeatCharJudgement), which is what both
    // the accuracy denominator (currentMaximumBaseScore) and the combo-portion weight
    // (GetComboScoreChange) are taken from, whatever the result actually was.
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
            this.counts = { great: 0, ok: 0, meh: 0, good: 0, miss: 0 }; // ScoreResultCounts
            // TypeBeatScoreProcessor.comboNeutralCells: the cells whose combo consequence has
            // ALREADY been taken by hand, at the keypress that spoiled them, so the result they
            // finally resolve with must leave combo exactly as it finds it. The C# keys this by
            // (line, cell), which is what a TypeBeatCharObject carries; here the cell object itself
            // is the identity, and it is held on the PROCESSOR rather than on the cell for the same
            // reason: cells live on the shared beatmap and outlive a play, this account does not.
            this.comboNeutral = new Set();
        }

        // ScoreProcessor.ApplyResultInternal, for the accuracy-affecting basic results a cell can
        // take (great/ok/meh/good/miss). All five are scorable, none is a bonus, and all five
        // affect combo: the four hits increase it, a miss breaks it.
        applyResult(result, cell) {
            this.counts[result]++;

            // TypeBeatScoreProcessor.MarkComboNeutral / ApplyScoreChange, folded into one branch
            // here. The C# cannot do that (ApplyResultInternal is sealed, so it moves combo first
            // and the ruleset hook puts it back afterwards), but the observable rule is this: a
            // combo-neutral cell's result neither breaks the run nor extends it, because its combo
            // consequence was already paid by hand at the keypress or the skip that spoiled it.
            // highestCombo is skipped with it: unlike a suppressed break, a suppressed INCREMENT
            // can raise a running maximum, so leaving it would inflate max_combo by one per typo.
            //
            // TWO results are ever marked, and they move combo in OPPOSITE directions (backlog
            // 167): the unfixed typo's 'good', a hit that must not extend the run, and the SEAL
            // MISS of a cell a word skip abandoned and nobody came back for, a break that must not
            // be taken a second time.
            const neutral = this.comboNeutral.has(cell);

            if (!neutral) {
                if (result === 'miss') this.combo = 0;
                else this.combo++;

                if (this.combo > this.highestCombo) this.highestCombo = this.combo;
            }

            this.maximumBaseScore += MAX_RESULT_BASE_SCORE;
            this.judgementCount++;
            this.baseScore += HIT_BASE_SCORE[result];

            // GetComboScoreChange: the MAX result's base score weighted by the combo AFTER this
            // judgement. A miss therefore contributes 300 * 0^0.5 = 0, and every later hit is
            // weighted by a combo that this break restarted from zero.
            //
            // The C# override is gated on HitResultExtensions.IncreasesCombo, which is the whole of
            // the difference between the ledger's two marked results, and this is that gate. A
            // marked HIT moved nothing, so it is weighted by the combo it FOUND, which is what
            // `this.combo` still holds. A marked MISS is weighted by the combo AFTER the judgement,
            // which the base implementation leaves at zero: it is a character nobody typed, and
            // paying it a full combo-weighted portion because the break was taken elsewhere would
            // be paying for it twice (measured on the C# fixture as 492794 instead of 421582).
            const weight = (neutral && result === 'miss') ? 0 : this.combo;

            this.comboPortion += MAX_RESULT_BASE_SCORE * Math.pow(weight, COMBO_EXPONENT);
        }

        // TypeBeatScoreProcessor.MarkComboNeutral: the result about to be applied to this cell must
        // leave combo alone, because the cell's break was taken by hand at the keypress that spoiled
        // it or at the word skip that abandoned it. Marked at the seam that APPLIES it (the seal),
        // never at the keypress or the skip, which is what keeps a CORRECTED typo and a RECLAIMED
        // skip working: the retype resolves the cell with an ordinary combo-increasing hit that
        // never consults this set.
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

        // TypeBeatScoreProcessor.RestoreCombo (backlog 140): put back the streak a corrected typo's
        // wrong keypress broke. The engine decides WHETHER and BY HOW MUCH
        // (resumeStreakIfThisRedeemsTheBreak); this is the hand-mirror into the submitted account, the
        // exact counterpart of the hand-mirrored break above.
        //
        // A DELTA, not an overwrite with the engine's own combo, and the JS needs that care for the
        // same reason the C# does rather than by imitation: these are two separate accounts kept
        // equal by mirroring every move (the engine's combo only counts SCORING keypresses, the
        // processor's only counts applied results, and an inert retype moves neither), so writing
        // one into the other would replace a mirrored history with a guess. Adding back exactly what
        // the break took is what that break's undo is.
        //
        // highestCombo is pushed HERE rather than left to the next result, and this is the half that
        // is easy to leave out: a fix on a cell that was ALREADY judged (typo, fix, typo, fix on one
        // cell) applies no result at all, so nothing later would raise the maximum and the restored
        // run would never reach max_combo. Unlike a suppressed break, a restore can raise a running
        // maximum, so it has to be taken at the moment it happens.
        restoreCombo(streak) {
            if (streak <= 0) return;

            this.combo += streak;
            if (this.combo > this.highestCombo) this.highestCombo = this.combo;
        }
    }

    // ---------------------------------------------------------------------------
    // TypingEngine: the frame-driven gameplay/judgement core.
    // ---------------------------------------------------------------------------
    const COMBO_CAP = 50;
    const WRONG_KEY_FAIL_STREAK = 13;

    // TypingEngine.FLETCHER_MAX_CHARS_AHEAD: how many COUNTABLE characters (typeable and not a
    // space) the caret may sit ahead of the playhead before a keypress stops earning combo. The
    // press still lands and still scores; it simply cannot build a combo while the caret is out
    // past the cap. Measured on the caret position AFTER the press, so the fifth char ahead is
    // still fine and the sixth is not.
    const FLETCHER_MAX_CHARS_AHEAD = 5;

    // TypingEngine.FLETCHER_DRAG_GRACE_MS: extra time past a line's normal hard deadline
    // (endTime + sealGraceMs) that the engine holds the line open while the PLAYER is still on it,
    // so a dragging player may finish the line the song has already left. Deliberately the same
    // magnitude as CUE_LEAD_MS: the beat of grace given to get ready, granted at the other end of
    // the line as well. Bounded so a run always terminates.
    const FLETCHER_DRAG_GRACE_MS = 1500;

    // TypingCell.IsCountable: the currency the rush cap measures in. A space spends no budget, so
    // pressing one can never push the caret over the line by itself.
    function isCountable(cell) { return cell.typeable && cell.expected !== ' '; }

    // TypingEngine.isWordGap. A WORD GAP: the typeable SPACE cell that separates two words. The one
    // boundary both word-level queries below (wordBackspaceTarget, retypeSelectionAnchor) are
    // written against, and the same test skipCurrentWord scans a word with, so "word" means one
    // thing in this file. A non-typeable cell (punctuation the default stream kept, and every mark
    // under Literate) is NOT a boundary: it rides inside the word it is attached to.
    function isWordGap(cell) { return cell.typeable && cell.expected === ' '; }

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
                    // The one place backlog 210's correction flag is ever cleared: it survives a
                    // backspace by design, so only a whole-run rebuild puts it back (the C# clears
                    // it in exactly the same place, TypingEngine.reset).
                    c.heldWrongBeforeJudged = false;
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
            // The wrong-key MODEL, and unlike the two above this one is ON, because it is the
            // DEFAULT gameplay on both sides since backlog 107: a wrong (non-space) char is typed
            // through and marked wrong instead of being rejected, and backspace can take it back.
            // The desktop client turns it off only for the Gatekeeper mod (acronym GK), and the
            // browser has no mods payload at all, so /play is permanently non-Gatekeeper, which is
            // exactly the default the shared leaderboards are now judged under. If /play ever grows
            // a mods payload, this is the flag GK would clear.
            this.allowWrongInput = true;
            // "Space to skip current word" (TypeBeatRulesetSetting.SpaceSkipsWord, backlog 110): a
            // space pressed inside a word abandons the rest of it and moves on to the next word.
            // Since backlog 167 abandoning is not giving up: the cells enter a PHANTOM state, one
            // backspace re-enters the word, and re-typing them earns their ordinary judgements and
            // the streak the skip broke. What the skip takes immediately is a single combo break;
            // the miss COUNT and the osu RESULTS wait for the seal, so a skip nobody goes back for
            // costs exactly what it always cost and one the player returns to costs nothing beyond
            // the detour. There is no era switch here (the C# WordSkipRule), for the same reason
            // there is no ComboRestoreRule: the browser only ever plays LIVE.
            // DEFAULTED ON since backlog 198, tracking the desktop's SHIPPED default (its config
            // row, not the C# engine property, which stays off for replay decoding): the browser
            // has no settings payload, so /play is permanently skip-on and the default path stays
            // the desktop's default feel. If /play ever grows a toggle, this is the flag it sets,
            // and it would ALSO have to travel in whatever the browser's equivalent of the replay
            // CONFIG frame is (the desktop carries it as bit 1), because it changes how a recorded
            // space is judged.
            this.spaceSkipsWord = true;
            // FLEXIBLE LINES (backlog 208): the player's caret is decoupled from the song's
            // playhead. Four behaviours, all confined to these two flags so the pinned path stays
            // byte-identical:
            //   RUSH FREEDOM      finishing a line moves the caret straight on to the next one
            //                     instead of waiting for its cue (rollForwardIfFinishedEarly). The
            //                     finished line is left unsealed and seals on its own normal
            //                     deadline with nothing missed.
            //   DRAG FREEDOM      a line the player is still typing is not force-sealed at its
            //                     normal deadline; the seal is deferred by FLETCHER_DRAG_GRACE_MS
            //                     so the caret is never yanked off a line mid-word (sealPermitted).
            //   RUSH CAP          a press that puts the caret more than FLETCHER_MAX_CHARS_AHEAD
            //                     countable chars ahead of the playhead lands and scores as normal
            //                     but earns no combo (rushesPastCap).
            //   LINE-START SNAP   a caret sitting PAST the last character of its line is handed to
            //                     the next line the moment that line starts, so a player who has
            //                     FINISHED is still carried along by the song
            //                     (snapForwardOnLineStart). An UNFINISHED line is never taken,
            //                     which is the point of the freedom.
            //
            // Per-char judgement windows are untouched: rushing reads as early deltas and dragging
            // as late ones, so accuracy, sync% and the judgement counts report the drift honestly.
            //
            // BOTH TRUE UNCONDITIONALLY, the same shape every other live-only rule in this file
            // takes (the span judgement, the word-gap input model, the space discipline, the
            // stretch narrowing). Since backlog 208 this is the desktop's LIVE default for every
            // stack, and the mod named Fletcher is the one that turns it OFF and re-pins the caret
            // (acronym FC). The browser has no mods payload, so the strict FC arm is unreachable
            // here; if /play ever grows one, FC is the mod that would clear these two.
            //
            // In the C# they default FALSE, because that engine must also RE-DERIVE a stored replay
            // under the pinned era every pre-208 row was played in, and the pair travels as CONFIG
            // frame bit 5. The browser has no era axis: it plays live only, writes no replay frames
            // and re-scores no stored row, so the live value is the only value it can hold. The C#
            // arm of the fuzz parity test therefore has to SET bit 5 in the frames it feeds, the
            // same treatment bits 2, 3, 4 and 6 already get.
            this.fletcherEnabled = true;
            this.flexibleLineSnap = true;
            // The COUNTABLE-CHARACTER STREAM (the C# constructor's countableTargets /
            // countableBase / countablePrefix): the whole map read as one run of countable cells,
            // which is the currency the rush cap measures in. countableTargets holds every
            // countable cell's target time sorted ascending, so the playhead's position is a binary
            // search; countableBase[k] plus countablePrefix[k][i] says where line k cell i sits in
            // that stream, so the caret's position is a lookup. All immutable after construction.
            this.countableBase = new Array(this.lines.length);
            this.countablePrefix = new Array(this.lines.length);

            const countableTargets = [];

            for (let k = 0; k < this.lines.length; k++) {
                const cells = this.lines[k].cells;
                const prefix = new Array(cells.length + 1);

                this.countableBase[k] = countableTargets.length;
                prefix[0] = 0;

                for (let i = 0; i < cells.length; i++) {
                    prefix[i + 1] = prefix[i];

                    if (!isCountable(cells[i])) continue;

                    prefix[i + 1]++;
                    countableTargets.push(cells[i].target);
                }

                this.countablePrefix[k] = prefix;
            }

            // Overlapping lines can interleave their targets across a boundary, so sort rather than
            // assume the per-line order carries. NUMERIC comparator, because the JS default sorts
            // lexicographically and would put 10000 before 2000.
            countableTargets.sort((a, b) => a - b);
            this.countableTargets = countableTargets;
            // The one outstanding combo snapshot (TypingEngine.restorable, backlog 140, widened by
            // 167): { lineIndex, cellIndex, streak }, the cell a wrong keypress spoiled or a word
            // skip abandoned and the streak that break cost, or null when there is nothing to go
            // back for. Set by that keypress or skip (through snapshotRedeemableBreak, the one
            // write site the two share), redeemed by typing that same cell correctly, and discarded
            // by any other combo break that had a streak to take (discardRestorableStreak). The
            // seams that discard it here are the two the browser can reach: a seal with unforeseen
            // misses and a rejected key. A word skip TAKES a snapshot rather than discarding one
            // since backlog 167, because it is a break the player can walk back into and undo. An
            // off-time press left the list at backlog 199: it is a hit now, it breaks nothing, and
            // only a break discards a claim, so fumbling the beat between a typo and its fix no
            // longer costs the fix its restore. It rejoins the list in the C# under
            // OffTimeRule.BreaksCombo, the pre-199 era, which this file has no arm for. The FOURTH
            // discard seam is the flexible caret's RUSH CAP (backlog 208), which this file gained
            // with the flexible default: a press that puts the caret more than
            // FLETCHER_MAX_CHARS_AHEAD past the playhead zeroes a live run, and that break is as
            // final as the seal's.
            //
            // "That had a streak to take" is backlog 176: a break landing while the run is ALREADY
            // at zero costs nothing, so it leaves an outstanding claim alone rather than replacing
            // it with an empty one, and correcting the older cell still resumes the run (see
            // snapshotRedeemableBreak).
            //
            // There is no ComboRestoreRule here, and that is a statement about /play rather than a
            // simplification: the enum exists in the C# so that RE-DERIVING a score stored before
            // backlog 140 does not hand it combo its fingers never earned. The browser only ever
            // plays LIVE (it has no mods payload, no replay input, and nothing anywhere re-scores a
            // stored row through this file: computeScore is called once, at the end of the play it
            // just ran), so ComboRestoreRule.OnFix is the only rule it can be in and a snapshot is
            // always taken. There is no ComboClaimRule either, backlog 176's own era axis, for the
            // identical reason: LatestBreakWins exists in the C# to re-derive a row stored before
            // that rule, and StreakedBreakWins is the only arm a live play can be in.
            this.restorable = null;
            // event hooks (optional; set by the renderer)
            this.onCharJudged = null;
            this.onWrongKey = null;
            this.onComboBroken = null;
            // TypingEngine.ComboRestored: a corrected typo just resumed the streak its wrong
            // keypress broke, carrying how much combo was put back. Raised with combo and maxCombo
            // already restored and BEFORE the corrected retype is judged.
            this.onComboRestored = null;
            this.onFinished = null;
            this.onFailed = null;
        }

        // The mash guard. The streak only ever grows on the REJECTION path, and backlog 184 took the
        // last key the browser could reach it with off that path: a mid-word space is typed through
        // now, so a browser player mashing the spacebar spells the line wrong rather than failing at
        // 13. That is the desktop's live behaviour too, and the guard is not weakened by it: it
        // belongs to Gatekeeper, where every wrong key is still rejected, and the browser can never
        // be in that model. It used to be three cases, and the other two went the same way: a wrong
        // letter on the WORD GAP moved onto the type-through path in backlog 181, and a wrong letter
        // anywhere else in backlog 107. What still rejects here is the space key on a FREESTYLE slot,
        // which is not a key a player can mash a fail out of (the slot takes every other character).
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

        // The negation of TypingEngine.hasUntypedTypeable, which is what canSeal's EARLY seal
        // ("nothing left to type, do not hold the next line up") hangs off.
        //
        // An ABANDONED cell counts as untyped (backlog 167): the player owes that character exactly
        // as much as one they simply have not reached, and it is re-typeable until the line seals,
        // so a line the player can still come back into must not seal early. That is what keeps the
        // reclaim window running to the line's own deadline, which is the window the grace exists
        // to grant, and without it a skip near the end of a line closes its own escape hatch.
        noTypeableUntyped(line) {
            for (const c of line.cells) {
                if (c.typeable && (c.state === 'untyped' || c.state === 'abandoned')) return false;
            }
            return true;
        }

        canSeal(line, time) {
            return time >= line.endTime && (time >= line.endTime + line.sealGraceMs || this.noTypeableUntyped(line));
        }

        // TypingEngine.sealPermitted. DRAG FREEDOM (backlog 208): a line the player is still typing
        // must not be force-sealed out from under them at its normal deadline. The seal is deferred
        // while the caret is on the line, up to FLETCHER_DRAG_GRACE_MS past its hard deadline; past
        // that the line seals as usual (untyped cells become misses, one combo break) and the caret
        // is moved on. Always true with a pinned caret, and true under a flexible one for any line
        // the player is not currently on, so a finished-early line still seals exactly on its own
        // deadline.
        sealPermitted(index, time) {
            if (!this.fletcherEnabled || this.activeLineIndex !== index) return true;

            const line = this.lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline. (This is also what lets the FINAL line, which has no next line to roll on
            // to, finish the run on time once it is fully typed.)
            if (this.noTypeableUntyped(line)) return true;

            return time >= line.endTime + line.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        // TypingEngine.snapForwardOnLineStart. THE LINE-START SNAP (backlog 208): while the caret
        // sits PAST THE LAST CHARACTER of its line, the next line STARTING takes it, which is what
        // keeps the flexible default feeling like the pinned game it replaced (finish your line and
        // the song moves you on). A line the player has not finished is never touched: dragging
        // behind is precisely the freedom the flexible caret grants, and sealPermitted above makes
        // the same distinction for the same reason.
        //
        // "Finished" is isLineComplete, i.e. the caret has walked off the end of the cell list.
        // That is exact rather than approximate: every caret advance runs autoSkipForward, so a
        // caret at cells.length is a caret with no typeable cell left in front of it, and it is the
        // same predicate the keypress-driven rollForwardIfFinishedEarly gates on. Cells left BEHIND
        // the caret wrong or abandoned do not hold the line: the player is done with them, and the
        // seal resolves them exactly as it always did.
        //
        // A LOOP rather than a single step, because the line it lands on can be finished the
        // instant it is reached (a line whose cells are all non-typeable is complete at caret 0),
        // and the roll-forward this backs up does not recurse.
        snapForwardOnLineStart(time) {
            if (!this.fletcherEnabled || !this.flexibleLineSnap || this.finished) return false;

            let snapped = false;

            while (this.activeLineIndex >= 0
                   && this.isLineComplete(this.activeLineIndex)
                   && this.activeLineIndex + 1 < this.lines.length
                   && time >= this.lines[this.activeLineIndex + 1].activationTime) {
                this.activeLineIndex++;
                this.caretIndex = 0;
                this.autoSkipForward();
                snapped = true;
            }

            return snapped;
        }

        // TypingEngine.rollForwardIfFinishedEarly. RUSH FREEDOM (backlog 208): the moment a press
        // finishes a line, the caret moves straight on to the next one instead of waiting for its
        // activation cue. It is the KEYPRESS half of moving a finished caret on; the time-driven
        // half, for a caret that became finished without a press of its own, is
        // snapForwardOnLineStart above. The finished line is left UNSEALED and seals on its own
        // normal deadline (with nothing missed, since it is fully typed), so nothing about the
        // song's timeline moves; only the player's position does. No-op on the last line, which
        // keeps the default "line complete, wait for the song" behaviour.
        rollForwardIfFinishedEarly() {
            if (!this.fletcherEnabled || this.finished || this.activeLineIndex < 0) return;
            if (this.caretIndex < this.lines[this.activeLineIndex].cells.length) return;
            if (this.activeLineIndex + 1 >= this.lines.length) return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always
            // unsealed.
            this.activeLineIndex++;
            this.caretIndex = 0;
            this.autoSkipForward();
        }

        // TypingEngine.wpmClockRuns. Whether the WPM/active-time clock runs for the frame ending at
        // previousTime. Always, with a pinned caret. Under the flexible one the caret can be parked
        // at the head of a line the song has not reached yet (rush freedom rolls it forward the
        // instant a line is finished), and a clock that ran through a 20-second instrumental would
        // read the wait as typing time; so the clock runs only from the point the playhead reaches
        // that line's activationTime, which is exactly when the line would have gone active while
        // pinned.
        wpmClockRuns(previousTime) {
            return !this.fletcherEnabled || this.activeLineIndex < 0
                || previousTime >= this.lines[this.activeLineIndex].activationTime;
        }

        // TypingEngine.PlayheadCountablePosition. How many COUNTABLE characters the song has
        // reached by `time`: the count of countable cells across the whole map whose target time is
        // at or before it. The playhead's position in the countable stream, and the reference the
        // rush cap is measured against. Monotonic in time and a pure function of the beatmap.
        playheadCountablePosition(time) {
            let lo = 0;
            let hi = this.countableTargets.length;

            while (lo < hi) {
                const mid = (lo + hi) >> 1;

                if (this.countableTargets[mid] <= time) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        // TypingEngine.CaretCountablePosition. The player caret's position in the same countable
        // stream: every countable cell in the lines before the active one, plus the countable cells
        // behind the caret within it. 0 when no line is active.
        get caretCountablePosition() {
            if (this.activeLineIndex < 0) return 0;

            const prefix = this.countablePrefix[this.activeLineIndex];
            const at = Math.min(Math.max(this.caretIndex, 0), prefix.length - 1);

            return this.countableBase[this.activeLineIndex] + prefix[at];
        }

        // TypingEngine.CharsAheadOfPlayhead. Signed countable-character drift of the caret against
        // the playhead: positive = rushing ahead, negative = dragging behind. The quantity the rush
        // cap bounds, and the honest read-out of what the flexible caret is about.
        charsAheadOfPlayhead(time) {
            return this.caretCountablePosition - this.playheadCountablePosition(time);
        }

        // TypingEngine.rushesPastCap. Would accepting `cell` at `time` leave the caret more than
        // FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead? Measured on the caret
        // position AFTER the press, so with a cap of 5 the fifth char ahead is still fine and the
        // sixth is not. A non-countable cell (a space) spends no budget.
        rushesPastCap(cell, time) {
            const after = this.caretCountablePosition + (isCountable(cell) ? 1 : 0);

            return after - this.playheadCountablePosition(time) > FLETCHER_MAX_CHARS_AHEAD;
        }

        // TypingEngine.SongWindowOpen. Whether the PLAYHEAD is inside a typeable line window: the
        // plain time rule on the first unsealed line, read independently of where the player's
        // caret has got to. Equal to "a line is active" with a pinned caret; under the flexible one
        // the two diverge, because the caret can be parked on a line the song has not reached
        // (rush) or still finishing one the song has left (drag).
        get songWindowOpen() {
            if (this.finished || this.nextSealIndex >= this.lines.length || this.lastUpdateTime === null) return false;

            const line = this.lines[this.nextSealIndex];

            return this.lastUpdateTime >= line.activationTime && this.lastUpdateTime < line.endTime + line.sealGraceMs;
        }

        // TypingEngine.SongIsOnTheCaretsLine. Whether the SONG is asking for characters on the very
        // line the player's caret is on. songWindowOpen ALONE is not that question, and the
        // difference is the whole of a real map's instrumental gap: a decoder-built line's window
        // runs to the NEXT line's start (contiguous, no holes), so through a twelve-second
        // instrumental the playhead is still inside line N's window and songWindowOpen stays true,
        // while the player who finished line N is parked at the head of line N+1 with nothing being
        // asked of them.
        //
        // On the desktop this is what lets Space reach the mid-song skip overlay from a parked
        // caret (TypeBeatPlayfield's key handler). The BROWSER HAS NO SKIP OVERLAY at all: it
        // cannot seek its scheduled audio source without moving the gameplay clock, so a dead
        // stretch gets a labelled countdown chip instead (typebeat-player.js updateGap), and there
        // is no fall-through for Space to reach. It is ported and exported anyway because that chip
        // is exactly what the predicate now has to gate: with the caret parked on the next line,
        // "a line is active" no longer means "the song is asking for characters".
        get songIsOnTheCaretsLine() {
            return this.activeLineIndex >= 0 && this.activeLineIndex === this.nextSealIndex && this.songWindowOpen;
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

            // The cells the line really did run out of time on, as opposed to the ones a word skip
            // had already given up: the only group whose break has not been taken yet, and
            // therefore the only one that can break combo here. The C# keeps the wider MISSED count
            // alongside it for LineSealResult.MissedCells, which this mirror raises no event for.
            let unforeseen = 0;

            for (const c of line.cells) {
                // The engine's own miss count (TypingEngine.Update's seal loop), which drives the
                // HUD combo below and nothing that is submitted. A cell the line ran out of time on,
                // and ONLY that (backlog 124, reversing the predicate backlog 109 widened): a cell
                // left sitting WRONG is a character the player FINISHED, so it is a mistype and not
                // a miss, it keeps 'wrong' on screen, and it does not break the HUD combo here,
                // because its break was taken at the keypress. That is what puts the HUD combo back
                // in agreement with the submitted max_combo (backlog 123).
                //
                // An ABANDONED cell (backlog 167) is a miss, and this is where it becomes one: the
                // player skipped its word and never reclaimed it, so it turned out to be a character
                // they never typed. It counts and resolves exactly as an untyped cell does, and the
                // ONE thing it does not do is break combo, for precisely the reason a still-wrong
                // cell does not: that break was taken at the skip.
                const phantom = c.state === 'abandoned';

                if (c.typeable && (c.state === 'untyped' || phantom)) {
                    c.state = 'missed';
                    c.judgeType = 'Miss';
                    if (!phantom) unforeseen++;
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
                        // TypeBeatPlayfield.onAbandonSealed, which the C# raises immediately BEFORE
                        // the seal results land, for exactly the reason this line sits immediately
                        // before the call below: the ledger has to be written before the result
                        // consults it. A cell a word skip gave up already paid its break at the
                        // skip, so the Miss it is about to take must leave combo where it finds it,
                        // or it would wipe a run the player rebuilt through the rest of the line
                        // while the engine's own combo kept it.
                        if (phantom) this.processor.markComboNeutral(c);
                        this.applyCellResult(c, 'miss');
                    }
                }
            }
            if (unforeseen > 0) {
                this.combo = 0;

                // A real break, so it owns the streak (backlog 140). Distinct from the line-scoped
                // drop below, and since backlog 208 the two come apart HERE as well as in the C#:
                // the flexible caret can already be on a LATER line, holding a snapshot this break
                // has just cost it.
                this.discardRestorableStreak();
                if (this.onComboBroken) this.onComboBroken();
            }

            // A sealed line's cells can never be typed again, so a snapshot left on this one is
            // unredeemable whether or not the seal broke anything. Load-bearing since backlog 208:
            // the flexible caret DOES run ahead of the seal, so a run that finishes a line early
            // and then spoils a cell on the next one is holding a claim on a line the song is still
            // sealing behind it. Dropping it keeps the state truthful rather than relying on the
            // caret never going back.
            if (this.restorable !== null && this.restorable.lineIndex === idx) this.restorable = null;
        }

        // TypingEngine.discardRestorableStreak. A combo break that is nobody's fixable typo just
        // happened, so the outstanding snapshot (if any) is discarded: the streak it was holding has
        // been lost to THIS break, and correcting the older cell later cannot bring back a run that
        // ended after it. Called at every combo-break seam except the two REDEEMABLE ones, a wrong
        // keypress and a word skip, which snapshot instead (snapshotRedeemableBreak): a break of
        // either kind that cost a streak of its own takes the claim there, so it needs no case here.
        discardRestorableStreak() {
            this.restorable = null;
        }

        // TypingEngine.snapshotRedeemableBreak. Take the snapshot for a REDEEMABLE break (a wrong
        // keypress, or a word skip): the streak it cost, against the cell the player has to come
        // back to. The one write site for `restorable` other than the discards, so the two breaks
        // that can be walked back into cannot drift apart, which is the shape the C# refactored to
        // when backlog 176 gave the two of them a condition to share.
        //
        // A break takes ownership of the streak only if it HAS a streak to own (backlog 176). One
        // landing at a combo of zero costs the player nothing, so it does not get to end an older
        // cell's claim on a run that is still redeemable: the claim it would write is empty, and
        // swapping a live claim for an empty one is a pure loss to a player who then goes back and
        // fixes both cells. With NOTHING outstanding it still writes its own empty claim, so that
        // redeeming it restores nothing, which is what resumeStreakIfThisRedeemsTheBreak has always
        // done with a zero.
        //
        // The C# reads two era switches here that this file has no counterpart to, for the reason
        // set out on `restorable`: ComboRestoreRule, which decides whether any snapshot is taken at
        // all, and ComboClaimRule, which decides the condition below. The browser only ever plays
        // live, so both are pinned to their live arms and the condition stands unguarded.
        snapshotRedeemableBreak(cellIndex, brokenStreak) {
            if (brokenStreak <= 0 && this.restorable !== null) return;

            this.restorable = { lineIndex: this.activeLineIndex, cellIndex: cellIndex, streak: brokenStreak };
        }

        // TypingEngine.resumeStreakIfThisRedeemsTheBreak. Redeem the outstanding snapshot if the
        // cell about to be typed correctly is the cell it was taken against: the run resumes at that
        // streak plus everything earned since, which is exactly combo + streak because no break has
        // landed in between (any that had would have discarded the snapshot). The claim is spent
        // either way, so a second correct retype of the same cell restores nothing.
        //
        // Two breaks can be waiting here, and the redemption is identical for both: the wrong
        // keypress that spoiled the cell (backlog 140), and the word skip that abandoned it
        // (backlog 167). In both cases the cell is the one the player has to come back to, so typing
        // it is what says they came back.
        resumeStreakIfThisRedeemsTheBreak(cellIndex) {
            const claim = this.restorable;

            if (claim === null) return;
            if (claim.lineIndex !== this.activeLineIndex || claim.cellIndex !== cellIndex) return;

            this.restorable = null;

            // A break that cost nothing restores nothing, and announcing it would have every
            // consumer write back a combo it already holds.
            if (claim.streak <= 0) return;

            this.combo += claim.streak;
            if (this.combo > this.maxCombo) this.maxCombo = this.combo;

            // TypeBeatPlayfield.onComboRestored: the submitted account is moved by hand here, at the
            // same seam and for the same reason the break is (osu's combo is maintained
            // incrementally off results and no result carries this).
            this.processor.restoreCombo(claim.streak);

            if (this.onComboRestored) this.onComboRestored(claim.streak);
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
        //
        // Since backlog 167 the cells enter the PHANTOM state rather than being missed on the spot,
        // and everything except the BREAK moves out of here on to the two places a phantom cell can
        // end up: the backspace that reclaims it (processBackspace) and the seal that resolves it as
        // a miss (sealLine). What is left is the entry into that state, the break, and the SNAPSHOT
        // of the streak the break cost, taken against the first abandoned cell so that typing it
        // later resumes the run through the same backlog 140 machinery a corrected typo redeems.
        // That replaces the outright discard the skip used to do: a skip is a break the player can
        // walk back into, which is exactly what a typo's break is.
        skipCurrentWord() {
            const cells = this.lines[this.activeLineIndex].cells;

            // The word: the run between the typeable SPACE cells either side of the caret. The whole
            // word rather than the tail from the caret, which gives up the same cells (everything
            // behind the caret is already resolved) but says what the feature promises.
            let start = this.caretIndex;
            let end = this.caretIndex;
            while (start > 0 && !(cells[start - 1].typeable && cells[start - 1].expected === ' ')) start--;
            while (end < cells.length && !(cells[end].typeable && cells[end].expected === ' ')) end++;

            // The cells this skip puts into the phantom state, in ascending order. The FIRST of them
            // is the one the snapshot is taken against, and there is always at least one (the cell
            // the caret is sitting on), so the break always has a cell behind it.
            const abandoned = [];

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

                // The PHANTOM state, and NOT a resolution: no result is applied here, which is
                // precisely what leaves the cell's one and only result available to a retype (a
                // cell takes only its first, see applyCellResult). The miss COUNT waits for the
                // seal alongside it, because counting it now would say the character is lost while
                // the player can still walk back into it and type it.
                c.state = 'abandoned';
                c.judgeType = 'Abandoned';
                abandoned.push(i);
            }

            this.caretIndex = end;

            if (abandoned.length === 0) return;

            // AT MOST ONE combo break for the whole word, the rule sealLine's misses follow.
            const brokenStreak = this.combo;

            this.combo = 0;

            // Snapshotted against the FIRST abandoned cell, so re-typing that cell resumes the run.
            // A skip discards an older cell's claim the way any other intervening break would, but
            // only if it broke a streak of its own (backlog 176): a skip taken over a typo that has
            // already zeroed the run leaves that typo's claim redeemable, because the skip itself
            // cost nothing.
            //
            // The C# guards this call with its `reclaimable` flag and discards outright when that is
            // false, because under the pre-167 rule the abandoned cells are gone and there is
            // nothing to come back to. That arm has no counterpart here: the browser has no
            // WordSkipRule, so a skip in this file is permanently reclaimable.
            this.snapshotRedeemableBreak(abandoned[0], brokenStreak);

            if (this.onComboBroken) this.onComboBroken();

            // TypeBeatPlayfield.onWordAbandoned: the skip's one break on the SUBMITTED account, by
            // hand. It used to ride on the Miss the first abandoned cell took here; those results
            // now arrive at the seal, a whole line later, so with nothing left to carry the break
            // this seam carries it, exactly as the wrong-keypress path carries its own. The other
            // half of the C# seam is HEALTH (MISS_HEALTH_DRAIN per cell, refunded at either exit
            // from the phantom state), which has no counterpart here: the browser models health as
            // a derived read off consecutiveWrongKeys (see `health`), not as an account.
            this.processor.breakCombo();

            // NO onCharJudged for the abandoned cells, deliberately, and the omission mirrors the
            // wrong-char path above: that hook is this renderer's rolling-WPM tap, and the C#
            // pushRollingSample() sits on the accepted-keypress path only, so tapping it here would
            // drift the browser's WPM readout away from the desktop's. The cells repaint from state.
            // engine `counts` is left alone too: it is the scored-only dict, and the C# records
            // nothing for an abandoned cell either (its Miss is counted at the seal).
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
            //
            // wpmClockRuns is the flexible caret's own clause (backlog 208): a caret parked at the
            // head of a line the song has not reached yet is WAITING, not typing, so the clock does
            // not run through the instrumental it is waiting out.
            if (this.lastUpdateTime !== null && this.activeLineIndex >= 0 && !this.finished
                && !this.isLineComplete(this.activeLineIndex) && this.wpmClockRuns(this.lastUpdateTime)) {
                this.activeTimeMs += Math.max(0, time - this.lastUpdateTime);
            }
            this.lastUpdateTime = time;

            // Whether the caret ends this update on a line it was not on when the update started.
            // Only the DRAG CUTOFF inside the seal loop needs it: the ordinary activation arm and
            // the snap both auto-skip where they stand, while the cutoff's auto-skip is deferred to
            // the end exactly as the C# defers it (there it rides the single LineActivated raise).
            // The deferral is behaviour, not tidiness: it leaves the cutoff's caret at index 0 while
            // the snap arm below runs, so a cutoff onto a line of pure punctuation is NOT read as
            // complete until the next update, which is when the snap may take it.
            let pendingActivation = false;

            // (2) seal every line whose deadline passed, in order. sealPermitted is the DRAG
            //     freedom: a line the player is still typing holds off its own seal for
            //     FLETCHER_DRAG_GRACE_MS, so the loop stops at it rather than skipping it.
            while (this.nextSealIndex < this.lines.length
                   && this.canSeal(this.lines[this.nextSealIndex], time)
                   && this.sealPermitted(this.nextSealIndex, time)) {
                const index = this.nextSealIndex;

                this.sealLine(index);
                this.nextSealIndex++;

                if (this.activeLineIndex === index) {
                    if (this.fletcherEnabled && index + 1 < this.lines.length) {
                        // DRAG CUTOFF: the player ran out of borrowed time mid-line. Land them on
                        // the next line immediately rather than in a dead zone. Setting the caret
                        // here, inside the loop, is also what stops one cutoff cascading into the
                        // line the player just landed on: the next sealPermitted call sees them on
                        // it and grants it its own drag grace. A cascade still happens when that
                        // grace has ALSO expired (an idle player), which is the intended catch-up
                        // to the song.
                        this.activeLineIndex = index + 1;
                        this.caretIndex = 0;
                        pendingActivation = true;
                    } else {
                        this.activeLineIndex = -1;
                        // The caret goes back to 0 with it, exactly as TypingEngine.Update's seal
                        // loop does it. Nothing here reads the caret while no line is active
                        // (typebeat-player.js gates every read on `active`, and processKey /
                        // processBackspace / autoSkipForward all bail), and the next activation
                        // sets it to 0 anyway, so this changes no behaviour today. It is mirrored
                        // because a stale caret sitting on a sealed line is a trap for the next
                        // reader of either file, not because anything can currently see it.
                        this.caretIndex = 0;
                    }
                }
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

            // (4) THE LINE-START SNAP (backlog 208): the caret is sitting past the last character
            //     of its line and the next line has just started, so hand the player onto it,
            //     exactly as the pinned arm above would have. Placed AFTER the activation arm, as
            //     in the C#, so a fresh activation onto an already-finished line (every cell
            //     non-typeable) is snapped on in the same update rather than a frame later.
            //
            //     The C# additionally folds this, the activation above and the drag cutoff in the
            //     seal loop into ONE LineActivated raise, so a catch-up cascade through several
            //     stale lines relayouts the stage exactly once. This mirror raises no such event
            //     (typebeat-player.js reads engine.activeLineIndex per frame), so there is no
            //     announcement to deduplicate and the discipline has nothing to port: the one
            //     activation is the one caret position each of these arms leaves behind. What DOES
            //     port is the deferred auto-skip that rides that raise, below.
            this.snapForwardOnLineStart(time);

            // The drag cutoff's auto-skip, at the seam the C# runs it (guarded on there being a
            // line to skip on: a cutoff that cascaded off the end of the map has already parked the
            // caret nowhere and finished the run above).
            if (pendingActivation && this.activeLineIndex >= 0) this.autoSkipForward();
        }

        // The delta a press on cell cellIndex is judged, stored and announced on (mirrors
        // TypingEngine.judgedDeltaFor). A cell inside a SYLLABLE GROUP is judged against the
        // group's sung SPAN: 0 anywhere inside [startTime, endTime] (edge-inclusive), the signed
        // distance to the nearer edge outside it (negative early, positive late), so the same
        // asymmetric classify ladder grades distance from the syllable's edge.
        //
        // A cell in NO group keeps the classic point delta (time minus the cell's own target), and
        // that fallback is what gives a stylised word its per-character judgement: space cells,
        // lines with no groups, and every cell of an unsyllabifiable token land in the same arm.
        //
        // Since backlog 209 a STRETCH cell lands there too, and it is IN a group: a freestyle slot
        // or a cell of a run of three or more identical characters inside one syllable (see
        // isCharTimedStretch). Those cells are interchangeable to the matcher, so the span paid a
        // whole mashed run a delta of zero seconds ahead of the vocal; they go back on their own
        // character's clock while the rest of the line keeps the span.
        //
        // No era arm here, unlike the C#. The desktop engine defaults to CLASSIC on both axes and
        // turns the span rule (CONFIG frame flags bit 2) and the stretch narrowing (bit 6) on for
        // live play, because it must also RE-DERIVE stored replays under the rules their fingers
        // were graded on. The browser only ever plays live: it has no mods payload, no replay input,
        // it writes no replay frames (a /play submission carries the aggregate account alone, and
        // PUT /api/v2/scores/{id}/replay is the desktop client's own upload path), and nothing
        // re-scores a stored row through this file. So the live rule is the only rule this engine
        // can be in, and both parts of it are unconditional.
        judgedDeltaFor(line, cellIndex, time) {
            const syllable = syllableIndexOf(line, cellIndex);

            if (syllable >= 0 && !isCharTimedStretch(line, cellIndex)) {
                const group = line.syllables[syllable];

                if (time < group.startTime) return time - group.startTime;
                if (time > group.endTime) return time - group.endTime;

                return 0;
            }

            return time - line.cells[cellIndex].target;
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

                if (this.caretIndex >= line.cells.length) {
                    // The abandoned word ran to the end of the line, so there is no word gap for
                    // the space to land on. The line is complete, exactly as it would be had the
                    // player typed that last word out, and the same end-of-line handling applies.
                    this.rollForwardIfFinishedEarly();
                    return true;
                }

                cell = line.cells[this.caretIndex]; // the word gap, judged as an ordinary space below
            }

            // STEP OVER A SPOILED GAP (backlog 184, mirroring TypingEngine's StrictSpaces branch):
            // the caret is PARKED on a word gap that a wrong letter took, and the space that gap was
            // owed has arrived. It walks the caret past the gap and leaves the typo exactly as it is.
            // The cell is NOT rewritten to correct, because the character sitting in it is not the one
            // that was owed: it stays an unfixed, backspace-redeemable claim, and the seal resolves it
            // as an unfixed typo like every other one. It judges nothing: no tier, no points, no combo
            // gained and none broken (the typo already took the break, and this press cannot be asked
            // to pay for it twice).
            //
            // Counted as a CORRECT keypress, which is the same argument once more: the space IS the
            // right key for the cell it lands on, the gap it was owed, and the typo has already paid
            // an error and a break of its own. correctKeypresses feeds liveAccuracy alone, so this
            // credits accuracy and moves nothing else: the cell still resolves as an unfixed typo and
            // still costs COMPLETION, which is where an unfixed typo is supposed to be paid for.
            //
            // Gated on the CELL STATE rather than on an era flag, exactly as the C# gates it: the
            // caret can only come to rest on a wrong cell through the park below, so this branch is
            // unreachable for any run that never parked.
            if (c === ' ' && cell.expected === ' ' && cell.state === 'wrong') {
                this.totalKeypresses++;
                this.correctKeypresses++;

                this.caretIndex++;
                this.autoSkipForward();

                this.rollForwardIfFinishedEarly();
                return true;
            }

            let delta = this.judgedDeltaFor(line, this.caretIndex, time);

            // SPACES ARE UNTIMED (backlog 148), decided here rather than after the match so that
            // EVERY reading of this press agrees on what its cell was worth: the correct press
            // below, and the wrong one typed through it since backlog 181, whose judgement carries
            // exactly the delta a correct press on the same cell would have carried. The rule and
            // its argument are on the untimed-space block further down; only the POSITION moved,
            // and the move reaches nothing new: the predicate is false for every LYRIC cell, so the
            // only press it can newly touch is the gap typo it was moved for.
            const untimedSpace = cell.expected === ' ';
            if (untimedSpace) delta = 0;

            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule is bypassed for it). The press is then judged exactly
            // like a correct char: same windows, points, combo, accuracy and completion, with the
            // pressed char kept in typedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // REJECTION is still the only outcome available to it on a freestyle slot, which is the
            // one cell backlog 184 left out of the type-through it opened to the space key: the slot
            // renders the character the player pressed, so a space typed into one would blank it
            // rather than mark it (see spaceMayLand below).
            const matched = (cell.freestyle && c !== ' ') ||
                (this.caseSensitive ? c === cell.expected : fold(c) === fold(cell.expected));

            if (!matched) {
                // DEFAULT (allowWrongInput): a wrong LETTER is typed through, marked wrong,
                // backspaceable, instead of rejected. This path never feeds the mash-fail streak, so
                // the browser has no 13-key fail at all for a letter, which is correct: that guard
                // belongs to the rejection model (the Gatekeeper mod) and the browser can never be
                // in it.
                //
                // The space KEY is admitted too since backlog 184 (the C# StrictSpaces era, which
                // the browser is permanently on for the reason the word-gap clause below states):
                // with spaceSkipsWord off there is no word for the press to skip, so it means nothing
                // but a wrong character and is treated as one, no differently from a wrong letter.
                // The cell still renders its own expected character in the error red (cellGlyph in
                // typebeat-player.js substitutes the typed char for GAPS only), which is what makes
                // an invisible red space a non-problem. With spaceSkipsWord on the same press never
                // arrives here: the skip gate above consumed it. A FREESTYLE slot is the one cell
                // that keeps refusing the key, because it has no expected glyph to redden and would
                // render blank instead of wrong (backlog 50's promise: any character EXCEPT the
                // word-advance key). The knock-on is deliberate: mid-word spaces stop feeding the
                // mash-fail streak, because they no longer reach the rejection branch that grows it.
                //
                // The WORD GAP takes a wrong letter exactly as a lyric cell does (backlog 181), and
                // unconditionally, which is where this parts from the C# gate
                // (`AllowWrongInput && c != ' ' && (WrongInputOnWordGaps || cell.Expected != ' ')`).
                // That third clause is an ERA arm the browser cannot be on the far side of: it plays
                // live and only live, writes no replay frames and re-derives no stored row, so the
                // live value (true, for every mod stack, Hard Rock included, see
                // DrawableTypeBeatRuleset.createEngine) is the only value it can hold, and the clause
                // collapses. Same shape as the span rule above, and for the same reason. The C# arm
                // of the fuzz parity test therefore has to SET that flag (CONFIG flags bit 3) or
                // every wrong key its script lands on a gap would be rejected there and typed here.
                // Backlog 184's space half is the same shape and needs the same treatment on that
                // arm: it is CONFIG flags bit 4 there and unconditional here.
                const spaceMayLand = !this.spaceSkipsWord && !cell.freestyle;

                if (this.allowWrongInput && (c !== ' ' || spaceMayLand)) {
                    this.totalKeypresses++;
                    this.errorCount++;

                    // The streak this keypress is about to break, snapshotted against the cell it
                    // spoils: correcting that cell resumes it (backlog 140, see restorable and
                    // resumeStreakIfThisRedeemsTheBreak). A wrong key on a SECOND cell discards the
                    // first cell's claim the way any other intervening break would, but only if it
                    // broke a streak of its own (backlog 176, see snapshotRedeemableBreak).
                    const brokenStreak = this.combo;

                    this.combo = 0;
                    this.counts.WrongChar = (this.counts.WrongChar || 0) + 1;

                    cell.state = 'wrong';
                    cell.typedChar = c;
                    cell.judgeType = 'WrongChar';

                    // The cell is now one whose eventual judgement, if the player goes back for it,
                    // will be a CORRECTION and not a clean first attempt, and backlog 210 prices
                    // those differently (see awardedTier). Recorded on the cell rather than counted,
                    // so a wrong-fix-wrong-fix cycle caps exactly once.
                    //
                    // Gated on the cell being UNJUDGED, which is what makes the flag mean what it
                    // says. A cell that was already judged CLEAN and then spoiled by a wrong key on
                    // the way back through keeps that clean judgement (a cell takes only its first
                    // result, and the retype that follows is inert), so flagging it would demote a
                    // judgement the player earned honestly before they ever fumbled it.
                    if (cell.firstCorrectDelta === null) cell.heldWrongBeforeJudged = true;

                    const wrongCellIndex = this.caretIndex;

                    this.snapshotRedeemableBreak(wrongCellIndex, brokenStreak);

                    // PARK on a spoiled word gap (backlog 184), instead of moving on: the space is
                    // still owed, so the player pays it (which steps over the typo, see the branch
                    // above) or backspaces it away, rather than being carried into the next word
                    // behind a gap the skip gate can no longer read as one. A further wrong letter
                    // lands on this same cell and overwrites this same character, so one park is one
                    // unfixed typo however many letters arrive; the snapshot above is idempotent for
                    // the same reason (a break with no streak behind it leaves the standing claim
                    // alone, see snapshotRedeemableBreak).
                    //
                    // Scoped to spaceSkipsWord because that is where the damage was: with the setting
                    // off, an advancing gap typo costs the player one cell, and with it on the next
                    // space fed the skip gate a spoiled gap and gave up a whole word. Every typo on a
                    // LYRIC cell advances exactly as it always has, under both arms. The browser
                    // hardcodes the setting OFF, so the park is unreachable in a live /play run and
                    // exists here to keep the mirror whole: the desktop can turn it on, and the two
                    // engines feed one leaderboard.
                    if (!(this.spaceSkipsWord && cell.expected === ' ')) {
                        this.caretIndex++;
                        this.autoSkipForward();
                    }

                    if (this.onComboBroken) this.onComboBroken();
                    // NO result for the cell (backlog 109), exactly as in the C#: the CELL's
                    // judgement still travels on CharJudged for the stage, but
                    // DrawableTypeBeatHitObject.ApplyCharJudgement returns before applying anything
                    // for a WrongChar. A miss is a character the line ran out of time on; a typo is a
                    // typo, and backspace can still fix this one, so the cell's one result is
                    // DEFERRED: the fix earns its real Great/Ok/Meh, and a typo left alone resolves
                    // at the seal as an unfixed typo, a 'meh' and not a miss (backlog 124).
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
                    //
                    // A typo on the line's LAST cell finishes it exactly as a correct press would
                    // (the character is finished, it is simply wrong), so this path rolls the caret
                    // forward too, at the same seam the C# does it.
                    this.rollForwardIfFinishedEarly();
                    return true;
                }

                // GATEKEEPER (strict). Wrong key REJECTED: costs a keypress + combo + streak; caret
                // unmoved. Unreachable from the browser for a letter, and since backlog 184 for a
                // mid-word space too: the one case the default path still refuses above is the space
                // KEY pressed on a FREESTYLE slot.
                this.totalKeypresses++;
                this.errorCount++;
                this.consecutiveWrongKeys++;
                this.combo = 0;
                // Nothing was written into a cell, so there is nothing to go back and correct: this
                // break is final, and it ends any older cell's claim on the streak (backlog 140).
                this.discardRestorableStreak();
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

            // SPACES ARE UNTIMED (backlog 148), mirroring TypingEngine.ProcessKey exactly; the
            // zeroing itself is done ABOVE the match, where a typed-through gap typo can read the
            // same value. Reaching HERE on a space CELL means a SPACE was typed on it: fold is only
            // toLowerCase so nothing but ' ' folds onto ' ', a freestyle cell refuses space
            // outright, and under mashing the press was already rewritten to the cell's expected
            // char. The spacebar is deliberately outside the timing challenge (the word gap is
            // where a typist's hands reset, not a note to hit), so the press is judged as though it
            // landed dead on target: top tier whatever the clock said, and never one of the two
            // zero-point tiers, which since backlog 199 cost accuracy rather than the run.
            //
            // A ZEROED DELTA rather than a forced judgement type, again as in the C#, so every
            // reader agrees with the judgement: classify(0) is 'Great', the inert retype
            // re-classifies the stored firstCorrectDelta, and typebeat-player.js reads judgedDelta
            // back for the sync tint and its live sync percent (the browser's mirror of
            // LiveSyncPercent), which would otherwise still dock a space for its timing.
            //
            // Scoped to the CELL and not to the KEY: a space that lands on a lyric character never
            // reaches here, it was consumed by the word skip (which has already given the abandoned
            // cells up and taken its one break before the caret ever reached the gap), typed through
            // as an ordinary typo above (backlog 184, the arm with no word to skip) or rejected there
            // on a freestyle slot. And an untimed space is not a
            // free one: a space cell nobody pressed still seals a miss like any other untyped
            // character (sealLine). Since backlog 181 the cell has a fourth way of being resolved,
            // a wrong LETTER typed into it, and that one takes the zeroed delta too, for the reason
            // the hoisted block above states: a typo is priced at what a correct press on the same
            // cell would have been priced at.
            //
            // The C# half of backlog 148 has one more clause with nothing to mirror here: it keeps
            // the exempt space OUT of its SyncTimeline, the offset-analysis series a play's results
            // screen is drawn from. This core has no such series (nothing here records per-press
            // samples), so there is no omission to fix, only an asymmetry to expect. The other
            // consumer of the zeroed delta IS mirrored: typebeat-player.js reads judgedDelta back
            // for the cell tint and for its live sync percent, and that readout excludes space cells
            // from both halves of its mean the way LiveSyncPercent does. A cell left WRONG is out
            // of that mean too, on both sides and in every state: the readout filters on the CELL
            // (typeable and not a space), never on what happened to it.

            // COMBO RESTORE (backlog 140, widened to the word skip by backlog 167), before anything
            // about this press is judged: if this is the cell a wrong keypress spoiled or a skip
            // abandoned, the run resumes at the streak that break cost plus everything earned since.
            // Placed here so the press below is
            // scored at the RESUMED streak, which is what makes fixing a typo worth score rather
            // than only accuracy: the combo portion weights every judgement by the combo AFTER it.
            // Not scoring-inert even for an inert retype, because the streak belongs to the FIX and
            // not to the cell's judgement.
            this.resumeStreakIfThisRedeemsTheBreak(this.caretIndex);

            const w = windowsFor(cell.tier);
            const inertRetype = cell.firstCorrectDelta !== null;
            let type, points = 0;

            if (inertRetype) {
                // Scoring-inert on BOTH accounts: the engine leaves its own combo/score alone, and
                // no result reaches the processor, because the cell drawable already carries one
                // (DrawableTypeBeatCharObject.ApplyEngineResult: `if (Judged) return;`). The branch
                // below relies on the SAME guard rather than on this condition, because with
                // allowWrongInput a cell can carry a result without ever having been correct.
                const d = cell.firstCorrectDelta;
                // The SAME award the first judgement took, re-derived: the stored delta through the
                // same ladder, and through the same backlog 210 cap, because the flag it reads is
                // set only before a cell is judged and never cleared. Announcing anything else here
                // would show a Great on a cell whose stored result is the capped Ok.
                type = awardedTier(classify(d, w), cell.heldWrongBeforeJudged);
                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = d;
                cell.judgeType = type;
            } else {
                this.totalKeypresses++;
                this.correctKeypresses++;
                // The clock classifies the press, then backlog 210's CORRECTION CAP decides what it
                // is awarded: a cell that held a wrong character before it was ever judged resolves
                // at min(that tier, 'Ok'), so a corrected cell can never be worth what a clean one
                // is. Applied here, above everything the tier decides, so the point ladder below,
                // `counts`, the announced onCharJudged and the cell's osu result all follow the one
                // decision and cannot say different things. The delta itself is untouched, so the
                // sync tint and typebeat-player.js's live sync percent see the press the player
                // actually made.
                type = awardedTier(classify(delta, w), cell.heldWrongBeforeJudged);
                const bp = basePoints(type);

                // THE RUSH CAP (backlog 208), evaluated BEFORE the caret moves: does this press put
                // the caret more than FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead?
                const rushedPastCap = this.fletcherEnabled && this.rushesPastCap(cell, time);

                if (bp > 0) {
                    // Multiplier reads combo BEFORE the increment; capped at COMBO_CAP => up to 2.0x.
                    points = Math.round(bp * (1 + Math.min(this.combo, COMBO_CAP) / COMBO_CAP));
                    this.score += points;
                }

                // An OFF-TIME press (Premature/Lagging: the right character, outside the outermost
                // Meh window) earns nothing above, and since backlog 199 that is the whole of what
                // it costs the score ladder. It is a HIT: it extends the combo like any other
                // accepted character, raises no onComboBroken, and leaves an outstanding restorable
                // claim alone, because only a BREAK discards one, so fumbling the beat between a
                // typo and its fix no longer costs the fix its restore. Its cell resolves as an osu
                // 'meh' below (toHitResult), which is what makes ACCURACY the punishment and what
                // lets the submitted combo follow the engine's with nothing mirrored by hand.
                //
                // So the increment is unconditional on THAT axis, exactly as the C# arm is with its
                // off-time era arm collapsed: OffTimeRule.BreaksCombo is the pre-199 era, which a
                // browser play (live only) can never be in.
                //
                // A space can never reach the off-time tiers at all: an untimed space is judged on a
                // zeroed delta and always takes the top tier (see the block above).
                //
                // What DOES stand between the press and the increment is the RUSH CAP, which the
                // browser gained with the flexible default (backlog 208). It is a combo penalty,
                // not a block: the char lands and scores exactly as it would without it, but no
                // combo may accumulate while the caret is out past the cap. The break therefore
                // fires ONCE, on the press that crosses the line, and RE-ARMS the moment a press
                // lands back inside it (combo starts building again, so the next excursion breaks
                // it again). It reaches an off-time press too, and that is the coherent reading of
                // both rules rather than an accident: the cap measures where the CARET is, not how
                // well the press was timed, so a press it would refuse combo for cannot earn combo
                // merely by also being mistimed.
                if (rushedPastCap) {
                    const hadCombo = this.combo > 0;

                    this.combo = 0;

                    if (hadCombo) {
                        this.discardRestorableStreak();
                        if (this.onComboBroken) this.onComboBroken();
                    }
                } else {
                    this.combo++;
                    if (this.combo > this.maxCombo) this.maxCombo = this.combo;
                }

                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = delta;
                cell.firstCorrectDelta = delta;
                cell.judgeType = type;
                this.counts[type] = (this.counts[type] || 0) + 1;
                // The cell's one-and-only osu result, applied the moment it is judged: this is
                // TypeBeatPlayfield.onCharJudged -> ApplyCharJudgement -> ApplyResult. Great/Ok/Meh
                // increase the submitted combo, and since backlog 199 so do Premature/Lagging
                // (they map to Meh, and the engine has just extended its own run on the same
                // press), and the combo portion is weighted by the combo as it stands right here.
                // Guarded rather than unconditional, because the guard is the mirror of
                // ApplyEngineResult's `if (Judged) return;` and not of the inert-retype rule. A cell
                // typed WRONG and then backspaced comes back through here with firstCorrectDelta
                // still null AND with no result yet (backlog 109 defers it), so this call is where
                // the fix is finally paid for: the cell earns its real Great/Ok/Meh, which is the
                // whole point. The guard still matters for the cells the seal or a word skip missed
                // first, which must not be re-judged.
                this.applyCellResult(cell, toHitResult(type));
            }

            const judgedIndex = this.caretIndex;
            this.caretIndex++;
            this.autoSkipForward();

            // TypeBeatPlayfield.onCharJudged's flexible-caret arm: the RUSH CAP breaks combo on a
            // press that is still judged Great/Ok/Meh, so the hit result alone (which INCREMENTS
            // osu's combo) cannot carry the break. Mirror the engine's own combo by hand, AFTER the
            // result has been applied, exactly as the wrong-keypress path does. Written against
            // "the combo this press left behind" (the C# judgement.ComboAfter) rather than against
            // rushedPastCap, because it has to cover the inert-retype branch too, which the C#
            // announces through the very same raise.
            //
            // Under a pinned caret this is inert, which is why the C# gates it on the flag: there
            // every combo-zero judgement either maps to a Miss (which breaks osu's combo itself) or
            // is a WrongChar, whose break the mistype path has already carried.
            if (this.fletcherEnabled && this.combo === 0) this.processor.breakCombo();

            if (this.onCharJudged) this.onCharJudged(judgedIndex, type, points);
            this.rollForwardIfFinishedEarly();
            return true;
        }

        // TypingEngine.ProcessBackspace. Erase the most recent typed cell within the active line,
        // stepping back transparently over auto-skipped punctuation (which is un-skipped so retyping
        // re-marks it) and, since backlog 167, over the ABANDONED cells of a skipped word (which go
        // back to 'untyped' for the same reason: retyping them re-earns them). Returns false when
        // there was nothing to do. The erased keypress stays in the accuracy counts.
        //
        // Both step-overs are transparent because neither cell holds anything the player put there,
        // so neither is an erase. That is what makes ONE press re-enter a skipped word and land on
        // the last character actually typed, however many characters were given up.
        //
        // The one case that does not erase BEHIND the caret is a typo the caret is parked ON, which
        // only the word-gap park can produce (backlog 184): that cell is cleared in place and the
        // caret does not move, because the gap it sits on is still owed its space.
        //
        // Scan first, mutate after, exactly as the C# does, because the two have to be told apart
        // before anything moves: a press with nothing typed behind it did SOMETHING if it reclaimed
        // a word, and nothing at all if it did not.
        processBackspace() {
            if (this.finished || this.activeLineIndex < 0) return false;

            const cells = this.lines[this.activeLineIndex].cells;

            // A typo the caret is PARKED ON (backlog 184): cleared where it sits, not erased from
            // behind. The gap is still owed its space, so the caret has no business retreating into
            // the perfectly good word in front of it, and the character the player wants back is the
            // one they are looking at. One press, one cell, caret unmoved.
            //
            // Keyed on the STATE rather than on an era flag, exactly as the C# keys it: only the park
            // ever leaves the caret sitting on a wrong cell, because everywhere else resolving a cell
            // is how the caret got past it. The C# raises TypoErased here, which this mirror has no
            // counterpart for on either backspace path: that event carries the HEALTH refund of the
            // drain a typo took, and the browser models health as a read off consecutiveWrongKeys
            // rather than as an account.
            if (this.caretIndex < cells.length && cells[this.caretIndex].state === 'wrong') {
                const parked = cells[this.caretIndex];

                parked.state = 'untyped';
                parked.typedChar = null;
                parked.judgedDelta = null;
                parked.judgeType = null;
                // firstCorrectDelta intentionally retained, as on the erase below.
                return true;
            }

            let target = this.caretIndex - 1;
            while (target >= 0 && (cells[target].state === 'autoskip' || cells[target].state === 'abandoned')) target--;

            let reclaimed = 0;
            for (let i = target + 1; i < this.caretIndex; i++) {
                if (cells[i].state === 'abandoned') reclaimed++;
            }

            if (target < 0 && reclaimed === 0) return false;

            // Un-skip the punctuation and re-open the abandoned cells we stepped back over. The C#
            // announces the reclaimed ones on AbandonReclaimed, which carries HEALTH alone (the
            // refund of what the skip drained); the browser has no health account, so there is
            // nothing for this mirror to raise.
            for (let i = target + 1; i < this.caretIndex; i++) {
                if (cells[i].state === 'autoskip' || cells[i].state === 'abandoned') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            if (target < 0) {
                // Nothing typed is left behind the caret. Ordinarily that is "nothing to erase", but
                // a word skipped at the very start of a line leaves phantom cells and no keypress
                // before them, and refusing here would make that one word the only unreclaimable one
                // on the map. The reclaim IS the state change, so the press did something: put the
                // caret back at the head of the word it just re-opened.
                this.caretIndex = 0;
                this.autoSkipForward();
                return true;
            }

            const cell = cells[target];
            cell.state = 'untyped';
            cell.typedChar = null;
            cell.judgedDelta = null;
            cell.judgeType = null;
            // firstCorrectDelta intentionally retained (inert-retype guard).
            this.caretIndex = target;
            return true;
        }

        // TypingEngine.WordBackspaceTarget. Where a CTRL+BACKSPACE (backlog 182, the typing-site
        // "erase the previous word" gesture) should leave the caret: a PURE QUERY, mutating nothing.
        // The caller composes the gesture out of ordinary processBackspace calls
        // (`while (caretIndex > target && processBackspace());`), which is what keeps the whole
        // gesture inside the vocabulary the engine already has: the desktop records the same run of
        // backspace frames a player holding the plain key down would have produced, and this mirror
        // needs no new call at all.
        //
        // The rule is the one every typing site implements. Walk back over the word GAPS immediately
        // behind the caret, then over the word behind them, and stop at that word's first cell. So a
        // caret sitting mid-word erases back to the start of the word it is inside, and a caret
        // sitting at the head of a word (the gap immediately behind it) erases that gap AND the whole
        // word before it. At the head of the line the answer is caretIndex itself, which makes the
        // composed gesture a no-op: it never calls the engine.
        //
        // The target is a FLOOR, not a promise: one processBackspace steps transparently back over
        // auto-skipped and abandoned cells, so a press over a word that was entirely given up to a
        // word skip can land the caret further back than this, exactly as a plain backspace there
        // would. That is the existing reclaim behaviour and is deliberately not fought here.
        //
        // Answers caretIndex unchanged when no line is active or the run has finished, so the caller
        // needs no second guard.
        get wordBackspaceTarget() {
            if (this.finished || this.activeLineIndex < 0) return this.caretIndex;

            const cells = this.lines[this.activeLineIndex].cells;
            let target = Math.min(this.caretIndex, cells.length);

            // The gaps directly behind the caret (normally one; a map never authors two in a row,
            // and the loop costs nothing for being written to survive one that did).
            while (target > 0 && isWordGap(cells[target - 1])) target--;

            // Then the word they follow, back to the gap that opens it or to the line's head.
            while (target > 0 && !isWordGap(cells[target - 1])) target--;

            return target;
        }

        // TypingEngine.RetypeSelectionAnchor. Where a CTRL+A (backlog 182, "select back to the
        // mistake I have to retype") should put the start of its selection: the first cell of the run
        // holding the EARLIEST unfixed typo behind the caret, or -1 when there is no typo behind the
        // caret at all (the gesture is then a no-op). The selection itself is the half-open range
        // [this, caretIndex), and it is pure UI state: nothing in the engine knows it exists.
        // Consuming it is composed, like the gesture above, out of ordinary processBackspace calls
        // back to this index plus at most one processKey (see typebeat-player.js).
        //
        // A typo is a cell in the 'wrong' state: a wrong character typed through and not yet
        // backspaced away. The scan takes the EARLIEST one on the line, so the selection covers every
        // unfixed typo behind the caret rather than only the most recent (backlog 184). The gesture
        // is "fix my mistakes", and it is one keystroke: offering the shortest retype would leave a
        // player with two spoiled words pressing it, retyping, pressing it again, and having no way
        // to see from the caret how many rounds are left. Retyping the cells in between costs
        // nothing, since a correct cell re-typed is scoring-inert.
        //
        // WHICH run the typo's cell opens has two cases, and they are the same rule stated twice: the
        // selection starts at the first cell the player must retype to fix the typo. For an ordinary
        // lyric character that is its WORD's first cell (walk back to the gap before it). For a WORD
        // GAP holding a typo (possible since backlog 181, and unconditional here: the browser is
        // always on the live arm of that rule) the gap IS the cell to retype and it belongs to no
        // word, so the selection starts on the gap itself; walking back from it would swallow the
        // perfectly good word in front of it for nothing.
        //
        // The answer is never equal to caretIndex when it is non-negative: the scan is over
        // [0, caretIndex), so a selection always covers at least one cell. The one typo that can sit
        // AT the caret, the gap a park is holding (backlog 184), is deliberately outside that range:
        // it needs no selection, being one backspace away under the same rule that parked it.
        get retypeSelectionAnchor() {
            if (this.finished || this.activeLineIndex < 0) return -1;

            const cells = this.lines[this.activeLineIndex].cells;
            const limit = Math.min(this.caretIndex, cells.length);
            let typo = -1;

            for (let i = 0; i < limit; i++) {
                if (cells[i].state === 'wrong') { typo = i; break; }
            }

            if (typo < 0) return -1;

            if (isWordGap(cells[typo])) return typo;

            let anchor = typo;

            while (anchor > 0 && !isWordGap(cells[anchor - 1])) anchor--;

            return anchor;
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
        const great = processor.counts.great;
        const ok = processor.counts.ok;
        const meh = processor.counts.meh;
        const typos = processor.counts.good; // uncorrected typos (TypeBeatResultMapping.UNFIXED_TYPO)
        const miss = processor.counts.miss;
        const judged = processor.judgementCount; // == great + ok + meh + good + miss

        // Whole-map accuracy (for display/completion); server overrides the submitted value.
        const acc = total > 0 ? processor.baseScore / (MAX_RESULT_BASE_SCORE * total) : 1;

        // The maximum combo portion, i.e. what an all-Great run of the whole map accumulates
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
        const completion = total > 0 ? (great + ok + meh) / total : 1;
        const passed = engine.finished && !engine.failed;

        // Wrong keypresses ride along as their own key. HitResult.ComboBreak is combo-only and
        // NOT accuracy-affecting on either side, so adding it changes no other number here and the
        // server's ScoringContract recomputes the identical accuracy / completion / rank; it is
        // priced only by pp's own mistyping term. maximumStatistics stays one great per cell, so
        // mashing can never inflate the denominator of anything.
        // Zero is omitted exactly as the other keys are, matching the desktop client, which strips
        // zero-valued entries before submitting (SoloScoreInfo.ForSubmission).
        const mistypes = engine.mistypes;

        const statistics = {};
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
            maximumStatistics: { great: total },
            // convenience for the results screen
            counts: { great, ok, meh, typos, miss, mistypes },
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
        // The syllabifier and the group derivation, exported so the fidelity harnesses can hold
        // them against the game's own Syllabifier / TypingLine.Syllables word for word.
        isSyllabifiable, countSyllables, splitPoints, buildSyllables, syllableIndexOf,
        // The stretch narrowing (backlog 209), exported for the same reason: the harnesses hold it
        // against the game's own TypingLine.IsCharTimedStretch cell for cell.
        buildCharTimedStretch, isCharTimedStretch,
        // SyllableSegments (backlog 181): the shared authored-vs-derived split derivation, exported
        // for the same reason, so the game's own SyllableSegments can be held against it.
        isAuthoredValid, derivedSplits, splitsFor, cellCuts, segmentOf,
        TypingEngine, computeScore, rankFromCompletion,
        windowsFor, classify, toHitResult,
        freestyleTick, freestyleGlyph,
        constants: {
            CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, LOW_CONFIDENCE_SCORE, FREESTYLE_MARKER,
            SHIMMER_INTERVAL_MS, PUNCTUATION, WORD_BREAK, STRETCH_RUN_LENGTH,
            // The flexible caret's two tuning points (backlog 208), exported so the harnesses pin
            // the same numbers the game's own FletcherEngineTest does rather than transcribing them.
            FLETCHER_MAX_CHARS_AHEAD, FLETCHER_DRAG_GRACE_MS
        },
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
