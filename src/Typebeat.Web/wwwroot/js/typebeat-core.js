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

    // The punctuation type!beat supports inside an authored lyric line, defined ONCE here, twenty-two
    // marks (mirrors Typeability.PUNCTUATION): comma, period, apostrophe, hyphen, question mark,
    // exclamation mark, semicolon, colon, round brackets, square brackets, straight double quote,
    // (added by backlog 202) dollar sign, percent sign, caret, asterisk, angle brackets, forward
    // slash, and (added by backlog 255) underscore and tilde.
    //
    // The round and square brackets are ORDINARY marks here since backlog 255: the strip that used
    // to run inside normalize() now runs only where the file being read is OLDER than the format
    // version that made brackets literal (see LITERAL_BRACKETS_FROM_VERSION and buildBeatmap), so a
    // v2 map's line carries a literal '(' exactly as it carries a comma.
    //
    // A map stores the AUTHOR'S form: punctuated and case-sensitive. What the player types (and
    // sees) is derived from it: verbatim under the desktop client's LITERATE mod, and through
    // toDefaultStream otherwise. Deliberately outside isTypeable, so a mark never counts as a plain
    // typeable char for the interpolation weights or the cell counts.
    //
    // Widening this set cannot move a stored per-map stat: every mark but WORD_BREAK is deleted by
    // defaultChar, so a char that used to be dropped by normalize as unsupported is now kept in the
    // author's line and dropped one step later, leaving the DEFAULT stream byte-identical.
    const PUNCTUATION = ",.'-?!;:()[]\"$%^*<>/_~";

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
    //
    // Called from buildBeatmap, NOT from normalize, since backlog 255: it applies only to a map
    // whose format version predates literal brackets, exactly as the desktop decoder applies it
    // (LyricBeatmapDecoder's version gate feeding TimingJsonLoader.TryParseRawLine). Keeping it out
    // of normalize is also what makes normalize a char-for-char mirror of Typeability.Normalize.
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

    // The Latin SPECIAL LETTERS normalize spells out in ASCII (backlog 329, step a): letters with no
    // canonical decomposition, so the NFD fold cannot reach them and they used to be DELETED
    // ("straße" stored as "strae"). Case preserved for Literate; a two-letter capital takes one fixed
    // form (Þ "Th", Ŋ "Ng") while ẞ and the capital ligatures spell out in full capitals. Applied
    // after the NFD fold, so a precomposed letter that decomposes to one of these plus a mark (Ǿ, ǽ)
    // is reached too (mirrors Typeability.SPECIAL_LETTERS, which carries the full reasoning, and the
    // server's copy; the three must stay byte for byte identical).
    // The ROMANISER (Cyrillic, Greek, kana, hangul and the rest to ASCII, backlog 329) deliberately
    // has NO copy here: it runs once, at import (Romaniser.cs in the game and on the server), and a
    // stored map already carries the romanised ASCII this file decodes.
    const SPECIAL_LETTERS = Object.freeze({
        'ß': 'ss', 'ẞ': 'SS',
        'æ': 'ae', 'Æ': 'AE',
        'œ': 'oe', 'Œ': 'OE',
        'ø': 'o', 'Ø': 'O',
        'ł': 'l', 'Ł': 'L',
        'đ': 'd', 'Đ': 'D',
        'þ': 'th', 'Þ': 'Th',
        'ð': 'd', 'Ð': 'D',
        'ı': 'i',
        'ŋ': 'ng', 'Ŋ': 'Ng',
        'ĸ': 'k',
    });
    const SPECIAL_LETTER_RE = new RegExp('[' + Object.keys(SPECIAL_LETTERS).join('') + ']', 'g');

    // Spell every SPECIAL_LETTERS letter out in ASCII, leaving every other char alone (mirrors
    // Typeability.SpellSpecialLetters).
    function spellSpecialLetters(s) {
        return s.replace(SPECIAL_LETTER_RE, ch => SPECIAL_LETTERS[ch]);
    }

    // NFD-fold diacritics, spell out the special letters NFD cannot reach (SPECIAL_LETTERS), map
    // curly quotes/apostrophes and en/em dashes to their ASCII forms, KEEP
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
    //
    // It does NOT strip backing vocals any more (backlog 255). That call used to be folded in here,
    // which was a structural divergence from the C#; it now sits at the one call site that knows
    // the map's format version (buildBeatmap), so this function is the plain mirror of
    // Typeability.Normalize and a literal bracket in a v2 map survives to the cells.
    function normalize(s, keepFreestyleMarkers = false) {
        if (s == null) return '';
        s = String(s).normalize('NFD').replace(/[̀-ͯ]/g, '');
        // The letters NFD cannot reach ('ß', 'ø', 'ł', ...) are spelled out rather than dropped
        // below as untypeable.
        s = spellSpecialLetters(s);
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

    // The magic first line, up to but not including the version number, and the two versions that
    // matter (mirrors LyricBeatmapDecoder.MAGIC / FALLBACK_FORMAT_VERSION /
    // LITERAL_BRACKETS_FROM_VERSION, and the server's BeatmapPackageParser copies of them).
    const FORMAT_MAGIC = 'type!beat file format v';

    // A file with no readable number is read as the ORIGINAL format, so an unversioned or
    // unparseable magic line falls back to the historical reading (brackets stripped) rather than
    // the current one: the direction that cannot invent lyric content for a map that never had it.
    const FALLBACK_FORMAT_VERSION = 1;

    // The first format version whose [Lyrics] brackets are LITERAL lyric marks rather than
    // backing-vocal spans to strip (backlog 255). Below it the parse strips, at or above it it
    // preserves. No v1 write path could store a literal bracket, so a '(' in a v1 file IS a backing
    // vocal by construction and the version decides with no ambiguity.
    const LITERAL_BRACKETS_FROM_VERSION = 2;

    // The version number off the magic line, or FALLBACK_FORMAT_VERSION when there is none to read.
    // Only the digits immediately after the prefix are taken, so anything else on the line is
    // ignored rather than fatal (mirrors LyricBeatmapDecoder.ParseFormatVersion).
    function parseFormatVersion(magicLine) {
        if (!magicLine || magicLine.indexOf(FORMAT_MAGIC) !== 0) return FALLBACK_FORMAT_VERSION;
        let end = FORMAT_MAGIC.length;
        while (end < magicLine.length && magicLine[end] >= '0' && magicLine[end] <= '9') end++;
        if (end === FORMAT_MAGIC.length) return FALLBACK_FORMAT_VERSION;
        const version = parseInt(magicLine.slice(FORMAT_MAGIC.length, end), 10);
        return isFinite(version) ? version : FALLBACK_FORMAT_VERSION;
    }

    // The mapper's FREESTYLE colour (backlog 384): [General] "FreestyleColour: #rrggbb", display
    // only. Mirrors the game's FreestyleColourKey.Parse: a '#' and exactly six hex digits, either
    // case, anything else reads as ABSENT, and absent is the default violet (TypeBeatStyle.
    // FreestyleChar, also the CSS fallback of .tb-c-free). Returned lowercase, as the game writes it.
    const DEFAULT_FREESTYLE_COLOUR = '#c792ea';
    const FREESTYLE_COLOUR_VALUE = /^#[0-9a-fA-F]{6}$/;

    function parseFreestyleColour(value) {
        const text = value === undefined || value === null ? '' : String(value).trim();
        return FREESTYLE_COLOUR_VALUE.test(text) ? text.toLowerCase() : null;
    }

    // The map's own TRACK GAIN (PR 1), a linear multiplier on the song's samples: 1 plays the file
    // as imported, and a hand-edited value is clamped to [0, MAX_AUDIO_GAIN] on read. Mirrors
    // BeatmapMetadata.DEFAULT_AUDIO_GAIN / MAX_AUDIO_GAIN and the [Metadata] AudioGain case of the
    // game's LegacyBeatmapDecoder.
    const DEFAULT_AUDIO_GAIN = 1;
    const MAX_AUDIO_GAIN = 4;

    // The decoder reads the value with double.TryParse(NumberStyles.Float, InvariantCulture), which
    // takes the WHOLE string or nothing: an optional sign, digits with at most one decimal point
    // (either side may be empty but not both), and an optional exponent, or the invariant infinity
    // symbol. parseFloat would take "2x" as 2; TryParse rejects it, so this does too. A number too
    // large for a double reads as infinity on both sides (and so clamps to the maximum).
    const AUDIO_GAIN_NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
    const AUDIO_GAIN_INFINITY = /^([+-]?)infinity$/i;

    // The parsed gain, or null where TryParse would fail (the caller then keeps what it had).
    // NaN is a value TryParse accepts that is refused here too: the game's decoder guards it the
    // same way (backlog 324), so both readers keep the default on a NaN line instead of one of
    // them clamping it straight through into a track of NaN samples.
    function parseAudioGain(value) {
        const v = String(value);
        let n;
        if (AUDIO_GAIN_NUMBER.test(v)) n = Number(v);
        else {
            const inf = AUDIO_GAIN_INFINITY.exec(v);
            if (!inf) return null;
            n = inf[1] === '-' ? -Infinity : Infinity;
        }
        if (n !== n) return null;
        return n < 0 ? 0 : n > MAX_AUDIO_GAIN ? MAX_AUDIO_GAIN : n;
    }

    // Applies a track gain to decoded audio IN PLACE: every sample of every channel (each a
    // Float32Array, as AudioBuffer.getChannelData hands them out) scaled by `gain` and clamped to
    // full scale. It runs once, on the decoded samples and ahead of every volume stage, because that
    // is where the desktop clips (ScaledAudioStream clamps the scaled sample to [-1, 1] before the
    // mixer sees it): a gain node ahead of the player's fixed 0.1 level would never reach full scale
    // (0.1 x 4 < 1), so a map boosted past what its samples hold would play clean here and clipped
    // there. A gain of exactly DEFAULT_AUDIO_GAIN touches nothing, as the desktop plays the file
    // itself then. Returns the number of samples that clipped.
    function applyTrackGain(channels, gain) {
        let clipped = 0;
        if (gain === DEFAULT_AUDIO_GAIN || !channels) return clipped;
        for (const data of channels) {
            for (let i = 0; i < data.length; i++) {
                const scaled = data[i] * gain;
                if (scaled > 1) { data[i] = 1; clipped++; }
                else if (scaled < -1) { data[i] = -1; clipped++; }
                else data[i] = scaled;
            }
        }
        return clipped;
    }

    function parseLyricOsu(text) {
        if (text && text.charCodeAt(0) === 0xFEFF) text = text.slice(1); // strip BOM
        const rows = String(text).split(/\r?\n/);
        const general = {}, metadata = {};
        const lyricObjs = [];
        // Read in the loop rather than off the key map, because the decoder only ASSIGNS on a
        // successful parse: a later unparseable AudioGain line leaves an earlier good one standing.
        let audioGain = DEFAULT_AUDIO_GAIN;
        let section = '';
        // The magic line is the first non-empty row, exactly as the server's parser locates it; a
        // file that does not carry one keeps the fallback version.
        let formatVersion = null;

        for (const raw of rows) {
            const t = raw.trim();
            if (t.length === 0) continue;
            if (formatVersion === null) formatVersion = parseFormatVersion(raw.replace(/^\s+/, ''));
            if (t[0] === '[' && t[t.length - 1] === ']') { section = t.slice(1, -1); continue; }

            if (section === 'General' || section === 'Metadata') {
                const idx = raw.indexOf(':');
                if (idx >= 0) {
                    const key = raw.slice(0, idx).trim();
                    const val = raw.slice(idx + 1).trim();
                    (section === 'General' ? general : metadata)[key] = val;
                    if (section === 'Metadata' && key === 'AudioGain') {
                        const g = parseAudioGain(val);
                        if (g !== null) audioGain = g;
                    }
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
            formatVersion: formatVersion === null ? FALLBACK_FORMAT_VERSION : formatVersion,
            audioGain,
            // The LAST FreestyleColour line wins, malformed or not, as in the game's decoder.
            freestyleColour: parseFreestyleColour(general['FreestyleColour']),
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

    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // Beatmap-level granularity: an explicit header value wins; otherwise Word if any
    // surviving line carries words[], else Line (mirrors LyricBeatmapDecoder.finalise).
    //
    // METADATA ONLY NOW. It used to pick the judgement ladder a cell was graded on, and the
    // widest-ladder fallback for an estimated line or a low-confidence word (the deleted
    // SyncWindows.LOW_CONFIDENCE_SCORE of 0.15) lived alongside it; the single symmetric ladder
    // retired both. It is still derived, and still mirrored, because the game's decoder still
    // stamps it on every TypeBeatHitObject and the parity harnesses hold the two readings against
    // each other.
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

            // THE AUTHORED PAUSES that survive the same clamping: which of a word's rests are kept
            // is the DERIVATION's own answer (usableRests, mirroring PausedWord.UsableRests), so a
            // map never loads a rest the play would ignore.
            const pauses = usableRests(tokens[m], ws, we, words[m].pauses || EMPTY_PAUSES);

            units.push({ text: tokens[m], start: ws, end: we, conf: clamp(words[m].score, 0, 1), syllables: boundaries, splits: splits, pauses: pauses });
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
            units.push({ text: tokens[i], start: unitStart, end: unitEnd, conf: 1, syllables: EMPTY_BOUNDARIES, splits: EMPTY_SPLITS, pauses: EMPTY_PAUSES });
        }
        return units;
    }

    const EMPTY_BOUNDARIES = [];
    const EMPTY_SPLITS = [];
    const EMPTY_PAUSES = [];

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
    // PausedWord: how a word's AUTHORED PAUSES (the Map Editor's Insert Pause, the word's
    // `pauses` array) cut it into the stretches it is sung in (mirrors
    // typebeat.Game.Rulesets.TypeBeat/Gameplay/PausedWord.cs, the gameplay half of it: Of and
    // UsableRests; the editor's display runs are not ported).
    //
    // A rest is a SUBDIVISION with no characters in it. N rests cut a word into N + 1 stretches,
    // each timed in its own right (so no cell's target sits inside a rest and the characters after
    // one are timed FROM its end), and the judgement GROUPS gain a pair of edges at every rest, so
    // the characters past a breath are judged against their own sung span. That makes this scoring
    // surface: a rest honoured on one side and not the other moves every target and span after it.
    //
    // A rest that cannot cut its word is IGNORED, and a word whose every rest is ignored reads
    // exactly as if it had none (pausedWordOf answers null).
    // ---------------------------------------------------------------------------

    // How many of a token's cells (isCell, the same count buildCells spreads over) sit before
    // charIndex. Mirrors PausedWord.cellsBefore.
    function cellsBefore(token, charIndex) {
        let cells = 0;
        for (let i = 0; i < charIndex && i < token.length; i++) if (isCell(token[i])) cells++;
        return cells;
    }

    // The rests a word really has, in time order: each strictly inside the word with a split that
    // separates typeable characters, none overlapping an earlier one, no two sharing a character,
    // and every later rest on a later character than the one before it. Shared with the loader
    // (buildExplicitUnits), exactly as the C# shares PausedWord.UsableRests with TimingJsonLoader.
    // The sort is stable, as LINQ's OrderBy is, so two rests starting together keep their order.
    function usableRests(token, unitStart, unitEnd, pauses) {
        const rests = [];
        const totalCells = typeableCount(token);
        const ordered = pauses.slice().sort((a, b) => a.start - b.start);

        for (const pause of ordered) {
            if (pause.start <= unitStart || pause.end >= unitEnd || pause.start >= pause.end) continue;

            const cut = clamp(pause.split, 0, token.length);
            const cells = cellsBefore(token, cut);

            if (cells <= 0 || cells >= totalCells) continue;

            if (rests.length > 0) {
                const last = rests[rests.length - 1];
                if (pause.start < last.end || cut <= last.split) continue;
            }

            rests.push(pause);
        }

        return rests;
    }

    // Boundary `index` of a list, or `fallback` when the list is too short (PausedWord.boundaryAt).
    function boundaryAt(boundaries, index, fallback) {
        return index >= 0 && index < boundaries.length ? boundaries[index] : fallback;
    }

    // How a unit's authored rests cut `token` (PausedWord.Of): { pieces, splits, runSpans }, or null
    // when it has none this derivation can honour, in which case every reader keeps the plain word.
    //
    //   pieces    the sung stretches, in text and time order: { firstChar, charCount, firstCell,
    //             cellCount, startTime, endTime, boundaries, cellCuts, cuts }. cellCuts is null when
    //             the word's char split is not authored (the derived even spread).
    //   splits    every character position between the RUNS, ascending: each rest's own cut with the
    //             stretches' interior cuts among them. What the judgement groups are split at.
    //   runSpans  the span each run is sung over, in the order splits divides the word. What the
    //             judgement groups take as their edges.
    function pausedWordOf(token, unitStart, unitEnd, unit) {
        if (!unit || !unit.pauses || unit.pauses.length === 0) return null;

        const rests = usableRests(token, unitStart, unitEnd, unit.pauses);

        if (rests.length === 0) return null;

        const boundariesAll = unit.syllables || EMPTY_BOUNDARIES;

        // The cuts this word's own subdivision describes: one per boundary, in boundary order.
        const authored = isAuthoredValid(token, boundariesAll.length + 1, unit.splits);
        const wordSplits = authored ? unit.splits : splitsFor(token, boundariesAll.length + 1, null);
        const wordCuts = authored && wordSplits.length === boundariesAll.length ? cellCuts(token, wordSplits) : null;

        const stretches = rests.length + 1;
        const stretchStart = (i) => i === 0 ? unitStart : rests[i - 1].end;
        const stretchEnd = (i) => i === rests.length ? unitEnd : rests[i].start;
        const owned = [];

        for (let i = 0; i < stretches; i++) owned.push([]);

        // Route every boundary to the stretch whose span holds it. One that falls inside a rest goes
        // to the NEARER stretch, keeping its relative position there.
        for (let slot = 0; slot < boundariesAll.length; slot++) {
            let time = boundariesAll[slot];
            let stretch = -1;

            for (let i = 0; i < stretches; i++) {
                if (time >= stretchStart(i) && time <= stretchEnd(i)) {
                    stretch = i;
                    break;
                }
            }

            if (stretch < 0) {
                let rest = rests.length - 1;
                for (let i = 0; i < rests.length; i++) {
                    if (time >= rests[i].start && time <= rests[i].end) { rest = i; break; }
                }

                const fraction = (time - rests[rest].start) / (rests[rest].end - rests[rest].start);
                stretch = time - rests[rest].start <= rests[rest].end - time ? rest : rest + 1;

                const lo = stretchStart(stretch);
                const hi = stretchEnd(stretch);
                time = lo + fraction * (hi - lo);
            }

            owned[stretch].push({ time: time, slot: slot });
        }

        // One stretch's cuts: the word's AUTHORED ones for its boundaries, each clamped inside the
        // stretch and forced ascending; when there are none to place, or they cannot all fit, the
        // syllabifier's own cut for the stretch's text, exactly as a whole word with a stale split
        // re-derives. The CELL cuts come from the resolved character cuts only when those are the
        // mapper's, and are null (derived) otherwise.
        function stretchCuts(firstChar, charCount, slots, firstCell, cells) {
            const lastChar = firstChar + charCount;

            if (charCount >= 2 && slots.length > 0 && wordCuts !== null && slots.length <= wordSplits.length) {
                const placed = [];
                let previous = firstChar;
                let fits = true;

                for (const slot of slots) {
                    const cut = clamp(wordSplits[slot], firstChar + 1, lastChar - 1);

                    if (cut <= previous) {
                        fits = false;
                        break;
                    }

                    placed.push(cut);
                    previous = cut;
                }

                if (fits) {
                    const splitCuts = new Array(placed.length + 2).fill(0);

                    for (let i = 0; i < placed.length; i++) splitCuts[i + 1] = cellsBefore(token, placed[i]) - firstCell;

                    splitCuts[splitCuts.length - 1] = cells;
                    let legal = true;

                    for (let i = 1; i < splitCuts.length; i++) {
                        if (splitCuts[i] <= splitCuts[i - 1] || splitCuts[i] > cells) legal = false;
                    }

                    if (legal) return { cuts: placed, cells: splitCuts };
                }
            }

            const derived = splitsFor(token.substring(firstChar, firstChar + charCount), slots.length + 1, null)
                .map(cut => cut + firstChar);

            return { cuts: derived, cells: null };
        }

        const pieces = [];
        const splits = [];
        const runSpans = [];
        let firstChar = 0;
        let firstCell = 0;

        for (let i = 0; i < stretches; i++) {
            const lastChar = i === stretches - 1 ? token.length : rests[i].split;
            const charCount = Math.max(0, lastChar - firstChar);
            const cells = Math.max(0, cellsBefore(token, lastChar) - firstCell);
            const pieceStart = stretchStart(i);
            const pieceEnd = stretchEnd(i);
            // Stable, as OrderBy is: two boundaries at the same time keep their slot order.
            const inOrder = owned[i].slice().sort((a, b) => a.time - b.time);
            const slots = inOrder.map(pair => pair.slot);
            const times = inOrder.map(pair => pair.time);

            const resolved = stretchCuts(firstChar, charCount, slots, firstCell, cells);

            pieces.push({
                firstChar: firstChar, charCount: charCount,
                firstCell: firstCell, cellCount: cells,
                startTime: pieceStart, endTime: pieceEnd,
                boundaries: times, cellCuts: resolved.cells, cuts: resolved.cuts
            });

            // The runs the stretch's text is drawn in and judged in, with the span each is sung over:
            // boundaries paired positionally with the cuts, the LAST run taking what the stretch has
            // left, and a clock guard so a stretch the cuts no longer describe never runs backwards.
            let clock = pieceStart;

            for (let run = 0; run <= resolved.cuts.length; run++) {
                let lo = run === 0 ? pieceStart : boundaryAt(times, run - 1, pieceStart);
                let hi = run === resolved.cuts.length ? pieceEnd : boundaryAt(times, run, pieceEnd);

                lo = Math.max(lo, clock);
                hi = Math.max(hi, lo);
                clock = hi;

                runSpans.push({ start: lo, end: hi });
            }

            for (const cut of resolved.cuts) splits.push(cut);
            firstChar = lastChar;
            firstCell += cells;

            if (i < stretches - 1) splits.push(rests[i].split);
        }

        return { pieces: pieces, splits: splits, runSpans: runSpans };
    }

    // Per-CELL target times for one token (TypingLine.tokenCellTargets): ramp[j] is cell j of k.
    // Without a usable pause this is exactly the old per-char syllableCharTarget call; a PAUSED word
    // is timed stretch by stretch, each over its own span with its own boundaries and cell cuts
    // (TypingLine.fillPausedStretches), so no cell has a target inside a rest.
    //
    // Since PR 5 the targets of a subdivided word follow its EFFECTIVE letter cut (the C#'s
    // AlignSubdivisionTargets, extended CONFIG bit 3, which the live factory sets): with no valid
    // split_chars the derived split (splitsFor) is turned into cell cuts and drives the spread, so
    // the caret meets the judgement groups at the same letters instead of an index-even spread
    // drifting into unrelated ones. The browser only plays live, so it takes the rule
    // unconditionally, as it takes bit 2.
    function tokenCellTargets(token, unitStart, unitEnd, unit, k) {
        const ramp = new Array(Math.max(0, k)).fill(0);

        if (k <= 0) return ramp;

        let boundaries = (unit && unit.syllables) ? unit.syllables : EMPTY_BOUNDARIES;
        const paused = unit ? pausedWordOf(token, unitStart, unitEnd, unit) : null;

        if (paused !== null) {
            for (const piece of paused.pieces) {
                let pb = piece.boundaries;
                let pc = piece.cellCuts;

                // A stretch with no authored cell cut of its own times its cells on the cut it is
                // judged on: the stretch's own char cuts, rebased onto the stretch's text.
                if (pc === null && pb.length > 0) {
                    const splits = piece.cuts.map(c => c - piece.firstChar);
                    pc = cellCuts(token.substring(piece.firstChar, piece.firstChar + piece.charCount), splits);
                    pb = pb.slice(0, splits.length);
                }

                for (let j = 0; j < piece.cellCount; j++) {
                    ramp[piece.firstCell + j] = syllableCharTarget(piece.startTime, piece.endTime, pb, piece.cellCount, j, pc);
                }
            }

            return ramp;
        }

        // The EFFECTIVE char split drives the spread within the word: an AUTHORED split (backlog
        // 181, "ap|ple") when it is valid, the derived one otherwise, the same split buildSyllables
        // builds the judgement groups from. A short word can yield fewer groups than requested; its
        // final group then owns the tail, so the boundaries past the split count are dropped.
        let cuts = null;

        if (boundaries.length > 0) {
            const splits = splitsFor(token, boundaries.length + 1, unit.splits);
            cuts = cellCuts(token, splits);
            boundaries = boundaries.slice(0, splits.length);
        }

        for (let j = 0; j < k; j++) ramp[j] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j, cuts);

        return ramp;
    }

    // ---------------------------------------------------------------------------
    // Syllabifier: rule-based English syllabification of ONE gameplay word (mirrors
    // typebeat.Game.Rulesets.TypeBeat/Gameplay/Syllabifier.cs, rule for rule). This is scoring
    // surface since backlog 179: the split it picks is a time SPAN a keypress is judged against,
    // so a split that lands one character off moves a real judgement. Since backlog 363 it no
    // longer cuts an UNSUBDIVIDED word at play time (that word is one group, see buildSyllables);
    // it is reached only through derivedSplits, for a subdivided word whose split_chars is missing
    // or invalid, and isSyllabifiable still gates which unsubdivided words are grouped at all.
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

    // Groups a line's cells into SYLLABLES (mirrors TypingLine.buildSyllables, backlog 174/178/179/363):
    // per whitespace token, the map's authored subdivisions decide WHICH characters form each
    // syllable and the timing data decides WHEN it is sung. Pure derivation: no cell target moves, so the classic
    // sweep and every readout built on it stay byte-identical.
    //
    // A token whose unit carries mapper subtimings (N boundaries = N + 1 syllables) is split with
    // the count FORCED to N + 1: the boundary times are the window edges, so syllable i spans
    // [edge_i, edge_i+1] with edge_0 the unit's start, the interior edges the boundary times and
    // the last edge the unit's end. When the syllabifier degrades to G < N + 1 groups (an
    // over-forced short word) the first G - 1 boundary times are the interior edges and the last
    // group runs to the unit's end.
    //
    // A token WITHOUT subtimings (and without a usable pause) is ONE group spanning the unit's
    // [start, end], the subtimed arm's own edge convention (backlog 363, the C# AuthoredGrouping):
    // only the mapper subdivides, so the browser judges, lights and marks exactly the word the
    // editor shows. The automatic syllabifier no longer cuts at play time; it runs once, at import,
    // and its cut reaches this code as authored syllables plus split_chars. The browser plays live
    // only, so it carries no copy of the C# stored-era NaturalGrouping. That one-group arm is still
    // GATED on isSyllabifiable (CHOICE A): a stylised spelling like "wooooooords" gets NO groups and
    // its cells stay at syllableIndexOf -1, keeping the classic per-character point judgement. The
    // gate does NOT apply to a subtimed token: the mapper hand-authored its count, and that is
    // authoritative.
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

        // Parallel to the two above, for the display marks (backlog 317, mirrors TypingLine's
        // groupTokenBase): which group each group's token started at, so a mark stays inside its
        // word even where the default stream turned a hyphen into a space. Every surviving interior
        // group is marked, and since backlog 363 every one of them is authored (a subdivision or a
        // pause cut), so there is no per-group "was this subtimed" flag beside it.
        const groupTokenBase = [];
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

            // A word carrying an authored PAUSE is a SUBTIMED word with one more divider: the rest's
            // own pair of edges (its start closing the group before it and its end opening the one
            // after) leave a gap no group covers, which gives the characters past the breath their
            // own sung span to be judged against. A rest that cannot cut the word changes nothing
            // here, exactly as it changes nothing in the targets (mirrors TypingLine.buildSyllables).
            const paused = unit ? pausedWordOf(token, unitStart, unitEnd, unit) : null;

            // A stylised spelling gets no groups at all UNLESS the mapper subtimed it, in which case
            // the hand-authored count wins over anything the rules would have guessed. An
            // unsubdivided, unpaused word is ONE group over its unit (backlog 363): no split at all.
            if (token.length > 0 && (subtimed || paused !== null || isSyllabifiable(token))) {
                const splits = paused !== null
                    ? paused.splits
                    : subtimed
                        ? splitsFor(token, boundaries.length + 1, unit ? unit.splits : null)
                        : EMPTY_SPLITS;
                const groupBase = starts.length;
                const groupCount = splits.length + 1;

                for (let g = 0; g < groupCount; g++) {
                    groupTokenBase.push(groupBase);

                    if (paused !== null) {
                        // The run's own span, rest's gap and all: no run covers a rest.
                        const run = paused.runSpans[Math.min(g, paused.runSpans.length - 1)];
                        starts.push(run.start);
                        ends.push(run.end);
                    } else if (subtimed) {
                        starts.push(g === 0 ? unitStart : boundaries[g - 1]);
                        ends.push(g === groupCount - 1 ? unitEnd : boundaries[g]);
                    } else {
                        // The one group of an unsubdivided word: the unit's own span.
                        starts.push(unitStart);
                        ends.push(unitEnd);
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

        // Resolve any target-derived span. Since backlog 363 no arm above pushes a NaN edge (the
        // un-subtimed natural cut that did is the C# stored-era NaturalGrouping, which the browser
        // never plays), so these loops only ever see a NaN that came in on the unit itself. They
        // stay because the C# resolution pass they mirror is shared by both of its groupings.
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

        // The display marks (backlog 317, mirrors TypingLine.SyllableMarkerCells), read off the
        // groups that survived: each group's startCell IS the gap its boundary falls in. PR 3 dropped
        // the authored-only gate, and since backlog 363 every split reaching here is authored anyway
        // (a subdivision, or an authored pause), so an unsubdivided word has nothing to mark. A mark
        // needs a surviving EARLIER group of the same token (something rendered to its left inside
        // the word), and a cut landing on or just after a word-gap SPACE cell (a dash the default
        // stream turned into a space) is suppressed. Write-only: nothing above reads it, so no
        // target, span or group moves and no judgement can.
        const syllableMarkerCells = [];
        let tokenBase = -1;
        let lastSurvivor = -1;

        for (let g = 0; g < provisional; g++) {
            if (groupTokenBase[g] !== tokenBase) {
                tokenBase = groupTokenBase[g];
                lastSurvivor = -1;
            }

            if (remap[g] >= 0 && lastSurvivor >= 0) {
                const startCell = groups[remap[g]].startCell;

                if (!isWordGapCell(cells, startCell) && !isWordGapCell(cells, startCell - 1)) syllableMarkerCells.push(startCell);
            }

            if (remap[g] >= 0) lastSurvivor = g;
        }

        return { syllables: groups, cellSyllable: cellSyllable, syllableMarkerCells: syllableMarkerCells };
    }

    // Whether the display cell at index is a SPACE the player types (false off either end of the
    // line). Mirrors TypingLine.isWordGapCell, the marker derivation's word-gap guard.
    function isWordGapCell(cells, index) {
        return index >= 0 && index < cells.length && cells[index].expected === ' ';
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

            if (cellSyllable[runStart] >= 0 && i - runStart >= STRETCH_RUN_LENGTH && !subdividedRun(cells, cellSyllable, runStart, i)) {
                for (let j = runStart; j < i; j++) flags[j] = true;
            }

            runStart = i;
        }

        return flags;
    }

    // Whether the identical run at [start, end) is SUBDIVIDED (mirrors TypingLine.subdividedRun): a
    // divider cuts through a long stretch of the same character, leaving a run of at least
    // STRETCH_RUN_LENGTH of them on the OTHER side of the boundary too ("yooooo|oooo|u"). The author
    // has paced that stretch into spans, so each group keeps its span rather than reverting to
    // character timing. A divider that merely STARTS a run ("hey|yyyy", one 'y' on the far side) is
    // not that shape, and its run stays char-timed.
    function subdividedRun(cells, cellSyllable, start, end) {
        const sameChar = (a, b) => fold(a.expected) === fold(b.expected);

        if (start > 0
            && cellSyllable[start - 1] >= 0
            && cellSyllable[start - 1] !== cellSyllable[start]
            && sameChar(cells[start - 1], cells[start])
            && sameRunLength(cells, cellSyllable, start - 1, -1) >= STRETCH_RUN_LENGTH) return true;

        return end < cells.length
            && cellSyllable[end] >= 0
            && cellSyllable[end] !== cellSyllable[start]
            && sameChar(cells[end], cells[start])
            && sameRunLength(cells, cellSyllable, end, 1) >= STRETCH_RUN_LENGTH;
    }

    // How many cells of the same folded character run from `index` in `direction` while staying
    // inside that cell's OWN group (mirrors TypingLine.sameRunLength).
    function sameRunLength(cells, cellSyllable, index, direction) {
        const group = cellSyllable[index];
        const expected = fold(cells[index].expected);
        let count = 0;

        for (let i = index; i >= 0 && i < cells.length && cellSyllable[i] === group; i += direction) {
            if (fold(cells[i].expected) !== expected) break;
            count++;
        }

        return count;
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
    // `cuts`, when given (cellCuts of the word's EFFECTIVE split: authored since backlog 181, and
    // since PR 5 the derived one too, see tokenCellTargets), replaces that even distribution with
    // the split's own: segment s covers cell-index range [cuts[s], cuts[s+1]) instead of
    // [s*k/S, (s+1)*k/S], so "ap|ple" puts two chars on the first syllable and three on the second
    // however long the word is. Null (or a cut of the wrong length) leaves the index-even
    // arithmetic below, which the C# keeps for its stored pre-PR 5 era; the browser only reaches it
    // for a word with no boundaries, where both branches agree.
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
    function buildCells(text, units, literate) {
        const n = text.length;
        const expected = new Array(n);
        const typeableFlags = new Array(n).fill(false);
        const targets = new Array(n).fill(null);

        // Pass 1: walk the authored text token by token (spaces delimit tokens; token m maps to
        // units[m]). Punctuation is placed but left UNTIMED, and is excluded from k, so adding a
        // mark to a word never moves the letters around it.
        const tokens = text.split(' ');
        let pos = 0;

        for (let m = 0; m < tokens.length; m++) {
            const unit = units.length > 0 ? units[Math.min(m, units.length - 1)] : null;
            const unitStart = unit ? unit.start : 0;
            const unitEnd = unit ? unit.end : 0;
            const token = tokens[m];

            // k = number of cells in this token, freestyle slots included (the player presses a key
            // for them, so they take a share of the word's time like any letter), marks excluded.
            let k = 0;
            for (let t = 0; t < token.length; t++) if (isCell(token[t])) k++;

            // Per-cell targets for this token, which is where syllable subdivisions, the word's
            // effective char split (authored since backlog 181, derived too since PR 5) and an
            // authored PAUSE warp the char-to-time mapping (see tokenCellTargets, mirroring
            // TypingLine.tokenCellTargets on its aligned live era).
            const ramp = tokenCellTargets(token, unitStart, unitEnd, unit, k);

            let j = 0;
            for (let t = 0; t < token.length; t++) {
                const ch = token[t];
                expected[pos] = ch;
                if (isCell(ch)) {
                    typeableFlags[pos] = true;
                    targets[pos] = ramp[j];
                    j++;
                }
                pos++;
            }

            if (m < tokens.length - 1) {
                expected[pos] = ' '; // inter-word space cell: preceding unit's end
                typeableFlags[pos] = true;
                targets[pos] = unitEnd;
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
                cells.push(newCell(expected[i], targets[i], typeableFlags[i] || isPunctuation(expected[i])));
            }
        } else {
            // The default stream. Each surviving char keeps the timing of the authored char it
            // came from, so a hyphen-turned-space lands on the interpolated slot the hyphen held
            // between the two letters it separated.
            const projected = projectDefault(text);
            sources = projected.sources;
            for (let i = 0; i < projected.text.length; i++) {
                const src = projected.sources[i];
                const ch = projected.text[i];
                cells.push(newCell(ch, targets[src], isCell(ch)));
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

        // THE VERSION GATE (backlog 255), the same one the desktop decoder applies and the same one
        // the server's BeatmapPackageParser applies to the identical bytes: from v2 on a bracket in
        // a stored [Lyrics] line is a literal lyric mark and stays, and below it (or with no
        // readable magic line) it is a backing vocal and is stripped exactly as it always was.
        // /play is served the STORED .osu blob, so this file is the browser's decoder and has to
        // read a bracket the same way desktop does or the two score different cells.
        const formatVersion = isFinite(parsed.formatVersion) ? parsed.formatVersion : FALLBACK_FORMAT_VERSION;
        const stripsBackingVocals = formatVersion < LITERAL_BRACKETS_FROM_VERSION;

        // 1) Raw lines: normalize (stripping backing vocals first only on a pre-v2 file); DROP
        //    lines with nothing to type so the previous line extends over their span (mirrors
        //    TryParseRawLine).
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
            const normalized = normalize(stripsBackingVocals ? stripBackingVocals(o.text) : o.text, freestyle);
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
                    // THE AUTHORED PAUSES (type!beat editor extension, the Map Editor's Insert Pause):
                    // the rests the singer takes INSIDE this word. Read RAW here and validated against
                    // the CLAMPED word in buildExplicitUnits, exactly as the splits above are (mirrors
                    // TimingJsonLoader.TryParseRawLine). `pauses` is the array a word writes when it
                    // takes more than one breath; the single `pause` object is the shape the feature
                    // had before a word could hold several, and reads the same way. Each field is taken
                    // on the C# reader's own terms: the two times must be JSON NUMBERS (tryGetDouble)
                    // and the split a whole number inside int32 (tryGetInt), and a rest missing any of
                    // them is skipped rather than guessed at.
                    const pauses = [];
                    const readPause = (p) => {
                        if (p == null || typeof p !== 'object' || Array.isArray(p)) return;
                        const a = p.start_ms, b = p.end_ms, s = p.split;
                        if (typeof a !== 'number' || !isFinite(a)) return;
                        if (typeof b !== 'number' || !isFinite(b)) return;
                        if (typeof s !== 'number' || !isFinite(s) || Math.floor(s) !== s) return;
                        if (s < -2147483648 || s > 2147483647) return;
                        pauses.push({ start: a, end: b, split: s });
                    };
                    if (Array.isArray(w.pauses)) {
                        for (const p of w.pauses) readPause(p);
                    } else if (Object.prototype.hasOwnProperty.call(w, 'pause')) {
                        readPause(w.pause);
                    }
                    words.push({ text: typeof w.text === 'string' ? w.text : '', start: ws, end: we, score: score, syllables: syllables, splitChars: splitChars, pauses: pauses });
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

            const flattened = buildCells(line.text, units, literate);
            const cells = flattened.cells;

            // The line's syllable GROUPS, built ALWAYS (cheap and pure) because they are what a
            // keypress on a grouped cell is judged against (see TypingEngine.judgedDeltaFor).
            const grouped = buildSyllables(line.text, units, start, singEndTime, cells, flattened.sources);

            const firstTarget = cells.length ? cells[0].target : start;
            const activationTime = Math.max(start, firstTarget - CUE_LEAD_MS);

            // TypingLine.FirstVocalTime: the first TYPEABLE cell's target, or the line's own start
            // when it has none. What the first line's head start (FIRST_LINE_LEAD_MS) is measured
            // back from.
            let firstVocalTime = start;
            for (const c of cells) {
                if (c.typeable) { firstVocalTime = c.target; break; }
            }

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
                // TypingLine's lastUnitEnd (backlog 317): the LAST token's own unit end (the same
                // malformed-count clamp as the target walk), or the sung end with no units at all.
                // The sung polyline closes on it (TypingLine.SweepEndTime) and so does the underline
                // pace hue's last band. Read by typebeat-player.js's lastUnitEndOf; the group-derived
                // reading it replaces missed it whenever the last token owned no group (a stylised
                // "uWooouououooo"), falling back to singEndTime where the desktop never does.
                lastUnitEnd: units.length > 0 ? units[Math.min(tokens.length - 1, units.length - 1)].end : singEndTime,
                activationTime: activationTime,
                firstVocalTime: firstVocalTime,
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
                charTimedStretch: buildCharTimedStretch(cells, grouped.cellSyllable),
                // TypingLine.SyllableMarkerCells (backlog 317): the display's mid-word subdivision
                // marks, one cell per mapper-authored boundary. Display only, read by no judgement.
                syllableMarkerCells: grouped.syllableMarkerCells
            });
        }

        return {
            title: parsed.title,
            artist: parsed.artist,
            creator: parsed.creator,
            beatmapId: parsed.beatmapId,
            granularity: granularity,
            audioFilename: parsed.audioFilename,
            // The map's track gain, applied by the player to the decoded samples (applyTrackGain).
            audioGain: parsed.audioGain === undefined ? DEFAULT_AUDIO_GAIN : parsed.audioGain,
            // The map's freestyle colour (backlog 384), '#rrggbb' or null for the default; display
            // only, the player paints .tb-c-free with it and nothing here reads it.
            freestyleColour: parsed.freestyleColour === undefined ? null : parsed.freestyleColour,
            // [General] AudioLeadIn, carried through untouched for the player's clock start
            // (gameplayStartTime). Nothing in the engine or the scorer reads it.
            audioLeadIn: parsed.audioLeadIn === undefined ? 0 : parsed.audioLeadIn,
            lines: lines,
            totalCells: lines.reduce((n, l) => n + l.cells.length, 0)
        };
    }

    function newCell(expected, target, typeable) {
        return {
            expected: expected,
            target: target,
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
            // and it leaves the state by exactly one of two exits: a backspace re-opens it (since
            // PR 3 the one backspace that undoes the whole skip, tryUndoWordSkip, or the transparent
            // walk in processBackspace for a skipped word the caret is no longer beside) or the line
            // seals on it and it becomes the miss it turned out to be (sealLine).
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
            // (TypingCell.JudgedPastRushCap, backlog 347's rush-cap mark, is not carried. PR 3's
            // second input era removed the rush cap from every live run, so no browser press can
            // ever be judged past one; the C# keeps the mark for the stored eras it re-derives.)
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
    // ONE LADDER FOR EVERY CELL, symmetric around the cell's target: milliseconds between the
    // keypress and the target, with the same bound on either side.
    //
    // It replaced a three-tier ladder (Line 250/400, Word 150/240, Syllable 112.5/180, six
    // late-biased constants scaled by 1.0 / 0.6 / 0.45) whose tier came from the beatmap's
    // granularity, widened back to the Line tier for an estimated line or a low-confidence word.
    // The whole of that went on both sides: the timing data a map carries still varies, but the
    // JUDGEMENT no longer does, so unreliable timing buys no extra tolerance and a well-subdivided
    // map is not judged more tightly than a coarse one. The per-cell `tier` this file used to carry
    // went with it, exactly as TypingCell.JudgeGranularity did, because a cell no longer selects
    // anything.
    //
    // A RETUNE OF THESE THREE NUMBERS IS NOT AN ERA on either side: no CONFIG bit records which
    // ladder a run was graded on, so a stored replay re-derives on whatever ladder ships today. That
    // is the recalculation tool's problem (tools/score-recalc) and never this file's, which only
    // ever plays live. The era bits AROUND the ladder still bind, because they are rules that
    // survive a retune: bit 13's Hard Rock halving multiplies whatever is here, and bit 8's
    // first-char rule narrows what the delta is measured from.
    const GREAT_WINDOW_MS = 150;
    const OK_WINDOW_MS = 300;
    const MEH_WINDOW_MS = 600;

    // The C# carries one more factor here since backlog 149: TypingEngine.WindowScale, a
    // multiplicative scale a mod may put on every window. Easy still doubles them live. Hard Rock
    // no longer does, since backlog 264 removed the live halving; it now only halves a STORED
    // replay's windows, and only when that replay's CONFIG frame lacks bit 13
    // (flag_unhalved_hard_rock_windows), which is how the era travels with the replay itself
    // instead of needing a recalc axis. Since backlog 150 a rate mod multiplies the clock rate in
    // as well, so the real-time tolerance is the same at every speed. None of it is mirrored,
    // because /play has no mods payload at all (see the scoreMultiplier note where the total is
    // computed) and no rate control either (see the update() note about clockRate), so the
    // browser's scale is permanently 1 and the C# at 1 is bit-identical to this. The day browser
    // play gains a mods payload or a rate, this is where the scale has to arrive.
    //
    // Kept as the six-field object the two consumers below already read, rather than collapsed to
    // three numbers, so `classify` and the sync ramp stay written against an early and a late bound
    // and a scale would have exactly one place to land. Mirrors SyncWindows.Default.
    const WINDOWS = {
        ge: GREAT_WINDOW_MS, gl: GREAT_WINDOW_MS,
        oe: OK_WINDOW_MS, ol: OK_WINDOW_MS,
        me: MEH_WINDOW_MS, ml: MEH_WINDOW_MS
    };

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
    // (TypeBeatResultMapping.RushCapTier, backlog 347's Meh award for a press out past the rush
    // cap, is not mirrored: the second input era, PR 3, removed the cap from every live run, and
    // the C# keeps the award only for the stored runs it re-derives.)

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
    //   4. TypeBeatPlayfield.onWrongKeyRejected -> the mash-guard HP drain only, which moves
    //      nothing in THIS account: it lands on the health account instead (HealthAccount below,
    //      applyWrongKeyStreak), the browser's port of TypeBeatHealthProcessor.
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
    //      markComboNeutral and the branch in applyResult below). Since backlog 259 EVERY seal
    //      result is marked, the misses included, and the seal's one combo break is mirrored by
    //      hand at the same seam instead (breakComboTo), because that break is BACK-DATED to the
    //      cells the line missed and a Miss result would land it on the run the player holds now.
    //      It is a hit for accuracy and for
    //      the note count, and NOT for completion (see computeScore), which is backlog 126: a cell
    //      typed wrong is not a cell typed, and it costs rank exactly as a miss does.
    //   6. TypeBeatPlayfield.onWordAbandoned -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatPlayfield.onAbandonSealed -> MarkComboNeutral on every cell the skip gave up
    //      (backlog 167). A word skip resolves NOTHING at the keypress, so like the seams in 2 and 3
    //      it has no result to carry its break and mirrors it by hand; and the Misses those cells
    //      finally take at the seal must not take that break a second time, which is the same ledger
    //      as the typo in 5, redeemed in the same place but in the opposite direction (a break to
    //      suppress rather than an increment). onAbandonReclaimed moves nothing here: it carries
    //      health alone, and lands on the health account (HealthAccount below).
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
            // MISS of a cell nobody came back for, a break that must not be taken a second time.
            // Backlog 259 widened the second one from the cells a word skip ABANDONED to EVERY
            // miss a line seals with: the seal's one break is back-dated to the cells it misses
            // and mirrored by hand at the same seam (breakComboTo below), so no Miss result may
            // carry it a second time and wipe a run the player built past those cells.
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
        // leave combo alone, because the cell's break was taken by hand elsewhere: at the keypress
        // that spoiled it, at the word skip that abandoned it, or, since backlog 259, at the seal
        // itself (breakComboTo above, which takes the whole seal's one back-dated break). Marked at
        // the seam that APPLIES it (the seal), never at the keypress or the skip, which is what
        // keeps a CORRECTED typo and a RECLAIMED skip working: the retype resolves the cell with an
        // ordinary combo-increasing hit that never consults this set.
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

        // TypeBeatPlayfield.onLineSealed's hand-mirrored seal break (backlog 259):
        // `scoreProcessor.Combo.Value = Math.Min(scoreProcessor.Combo.Value, SurvivingCombo)`.
        //
        // The seal's one break used to ride on the Miss results its cells took, which is exactly
        // the place a back-dated break must not land: a Miss carries a break to the combo it
        // FINDS, and the whole point of back-dating is that the break belongs to an earlier one.
        // So every seal miss is applied combo-neutral now (see markComboNeutral) and the break is
        // written here by hand instead, from the run the engine is left holding.
        //
        // A MIN, not an assignment: this is a BREAK, so it may only ever take combo away. The two
        // accounts hold increments for the very same cells, so the two values agree; the floor is
        // there because a break that CREDITED combo would be a defect in whichever account
        // happened to be behind, not a rule anyone wants.
        //
        // highestCombo is deliberately untouched, exactly as it is by breakCombo above: it
        // records a run the player really did hold, and a break dated in the past is not a claim
        // that the run never happened.
        breakComboTo(surviving) {
            if (surviving < this.combo) this.combo = surviving;
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
    // HealthAccount: the HP pool, mirroring TypeBeatHealthProcessor on top of osu's
    // HealthProcessor (backlog 306). Before it the browser had no account at all: health was a
    // read of the rejection streak, and the only fail was the 13-rejection branch, which a /play
    // run can no longer reach. So a near-AFK or typo-drowned run played out and submitted
    // passed=true, where the desktop fails the identical input (rank F, unranked, pp 0).
    //
    // HP moves NO score, accuracy or combo on either side: it only decides whether the run is
    // allowed to finish, and from which result on the score stops counting (FailedAtJudgement, see
    // applyCellResult). A surviving run's account is therefore untouched by it.
    //
    // RULE BY RULE, in the C#'s own order (TypeBeatHealthProcessor.cs):
    //   applyResult          HealthProcessor.ApplyResultInternal: frozen once failed, else move
    //                        by GetHealthIncreaseFor (Great/Ok/Meh recover, Miss drains, the unfixed
    //                        typo and the line container are inert), clamp, then the default fail
    //                        test. EVERY applied result runs the test, the inert ones included,
    //                        which is why the seal's line container is applied here too.
    //   applyWrongKeyStreak  ApplyWrongKeyStreak: 1/13 off the bar, and a fail at the streak
    //                        threshold. NO freeze guard and NO empty test, exactly as the C#: a
    //                        rejection can take the bar to 0 without failing, and the next
    //                        result or deferred drain is what then notices.
    //   applyDeferredDrain   applyDeferredDrain: one MISS_HEALTH_DRAIN per cell whose osu result is
    //                        deferred (the typo at its keypress, the word skip per abandoned cell),
    //                        then the empty test.
    //   refundDeferredDrain  refundDeferredDrain: the same amount given back (typo erased, skipped
    //                        cells reclaimed or about to take their seal Miss), clamped at full.
    //
    // THE CLAMP is BindableDouble's own ([0, 1], Health.MinValue / MaxValue), applied on every
    // write, so a refund into a full bar banks nothing and a drain past empty stops at 0.
    //
    // THE EMPTY TEST is osu-framework's Precision.AlmostBigger(Health.MinValue, Health.Value),
    // i.e. `0 > value - DOUBLE_EPSILON`: the bar counts as empty within 1e-7 of zero.
    //
    // THE VETO is HealthProcessor.Failed (`Failed?.Invoke() != false`): a handler answering false
    // refuses the fail and leaves the bar live, which is how osu's No Fail works. /play sets no
    // handler, so every fail it reaches is taken; the parity sweep sets one that refuses, so its
    // runs play out in full against a desktop scorer that simulates no health at all.
    // ---------------------------------------------------------------------------
    const WRONG_KEY_FAIL_STREAK = 13;                        // TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK
    const GREAT_HEALTH_INCREASE = 0.03;                      // TypeBeatHealthProcessor.GREAT_HEALTH_INCREASE
    const OK_HEALTH_INCREASE = 0.025;                        // TypeBeatHealthProcessor.OK_HEALTH_INCREASE
    const MEH_HEALTH_INCREASE = 0.02;                        // TypeBeatHealthProcessor.MEH_HEALTH_INCREASE
    const MISS_HEALTH_DRAIN = 0.0225;                        // TypeBeatHealthProcessor.MISS_HEALTH_DRAIN
    const WRONG_KEY_HP_DRAIN = 1.0 / WRONG_KEY_FAIL_STREAK;  // TypeBeatHealthProcessor.WRONG_KEY_HP_DRAIN
    const HEALTH_EPSILON = 1e-7;                             // osu.Framework.Utils.Precision.DOUBLE_EPSILON

    // Precision.AlmostBigger(double, double), with its default acceptable difference.
    function almostBigger(value1, value2) { return value1 > value2 - HEALTH_EPSILON; }

    // TypeBeatHealthProcessor.GetHealthIncreaseFor, keyed on the osu RESULT alone (the same five
    // keys the score mirror uses, plus null for the line container's IgnoreHit). 'good' is the
    // unfixed typo (TypeBeatResultMapping.UNFIXED_TYPO), HP-inert because its whole cost was
    // charged at the keypress that typed it.
    function healthIncreaseFor(result) {
        switch (result) {
            case 'great': return GREAT_HEALTH_INCREASE;
            case 'ok': return OK_HEALTH_INCREASE;
            case 'meh': return MEH_HEALTH_INCREASE;
            case 'miss': return -MISS_HEALTH_DRAIN;
            default: return 0;
        }
    }

    class HealthAccount {
        constructor(onFailed) {
            this.value = 1;              // HealthProcessor.Health
            this.hasFailed = false;      // HealthProcessor.HasFailed
            // HealthProcessor.Failed: an optional veto. Null, or anything but `false`, lets the fail
            // through (see THE VETO above).
            this.failed = null;
            this.onFailed = onFailed || null;
        }

        // Health.Value = v, through the bindable's [0, 1] clamp.
        set(v) { this.value = Math.min(1, Math.max(0, v)); }

        // HealthProcessor.TriggerFailure.
        triggerFailure() {
            if (this.hasFailed) return;
            if (this.failed !== null && this.failed() === false) return;

            this.hasFailed = true;
            if (this.onFailed) this.onFailed();
        }

        // HealthProcessor.ApplyResultInternal + CheckDefaultFailCondition. `result` is the cell's
        // osu result key, or null for the line container a seal resolves last.
        applyResult(result) {
            if (this.hasFailed) return;

            this.set(this.value + healthIncreaseFor(result));

            if (almostBigger(0, this.value)) this.triggerFailure();
        }

        // TypeBeatHealthProcessor.ApplyWrongKeyStreak.
        applyWrongKeyStreak(streak) {
            this.set(this.value - WRONG_KEY_HP_DRAIN);

            if (streak >= WRONG_KEY_FAIL_STREAK) this.triggerFailure();
        }

        // TypeBeatHealthProcessor.applyDeferredDrain (ApplyTypoDrain is one cell,
        // ApplyAbandonDrain is the abandoned count).
        applyDeferredDrain(cells) {
            if (this.hasFailed || cells <= 0) return;

            this.set(this.value - MISS_HEALTH_DRAIN * cells);

            if (almostBigger(0, this.value)) this.triggerFailure();
        }

        // TypeBeatHealthProcessor.refundDeferredDrain (RefundTypoDrain is one cell,
        // RefundAbandonDrain is the count that left the abandoned state).
        refundDeferredDrain(cells) {
            if (this.hasFailed || cells <= 0) return;

            this.set(this.value + MISS_HEALTH_DRAIN * cells);
        }
    }

    // ---------------------------------------------------------------------------
    // TypingEngine: the frame-driven gameplay/judgement core.
    // ---------------------------------------------------------------------------
    const COMBO_CAP = 50;

    // THERE IS NO RUSH CAP (PR 3, TypingEngine.InputEra2, bit 1 of the second CONFIG flags word).
    // TypingEngine.FLETCHER_MAX_CHARS_AHEAD bounded how many COUNTABLE characters the caret could
    // sit ahead of the playhead before a press was penalised: a combo break at five before backlog
    // 347, a Meh award at six after it. The second input era removed the cap outright, so a press
    // any distance past the playhead is judged on its timing alone and credits combo like any
    // other. The C# keeps both older caps as ERAS for the stored runs it re-derives; the browser
    // only plays live, so it takes the live rule unconditionally and carries neither the constant
    // nor TypingEngine.rushesPastCap. The countable stream below survives for charsAheadOfPlayhead,
    // the drift readout the C# keeps too.

    // TypingEngine.FLETCHER_DRAG_GRACE_MS: extra time past a line's normal hard deadline
    // (endTime + sealGraceMs) that the engine holds the line open while the PLAYER is still on it,
    // so a dragging player may finish the line the song has already left. Deliberately the same
    // magnitude as CUE_LEAD_MS: the beat of grace given to get ready, granted at the other end of
    // the line as well. Bounded so a run always terminates.
    //
    // Since backlog 218 it bounds BOTH directions, and the name is kept for the replays and the
    // mirrors that already use it. Drag holds a line open this long past its natural END
    // (sealPermitted); rush enters a line this long before its natural START (entryPermitted, gated
    // on boundedRush). One constant, so the two freedoms cannot drift apart: a player may run ahead
    // of the song by exactly the margin they may fall behind it.
    const FLETCHER_DRAG_GRACE_MS = 1500;

    // TypingEngine.FIRST_LINE_LEAD_MS (PR 2): how long before its first vocal the map's FIRST line
    // opens for typing. A later line is reachable early by rushing from the one before it, but the
    // first line has nothing to rush from and its boundary usually sits ON its first word, so the
    // activationTime clamp left the player unable to type a character until that word was already
    // being sung. A FLOOR, not a fixed window: a line whose own activation is earlier keeps it.
    const FIRST_LINE_LEAD_MS = 300;

    // TypingCell.IsCountable: the currency the drift readout measures in (and the rush cap did
    // until PR 3 removed it). A space spends no budget, so pressing one never moves the lead.
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
            // TypingEngine.lineAbandoned. Which lines the player WALKED OUT OF with a line skip
            // (processEnter), i.e. parked the caret past the last cell of while typeable cells were
            // still untyped. Read by exactly one thing, sealPermitted, which grants an abandoned
            // line the same drag grace a caret still sitting on it would have.
            //
            // WHY IT HAS TO BE REMEMBERED. Without it an Enter skip would move that line's seal
            // EARLIER (by up to FLETCHER_DRAG_GRACE_MS) than the identical run that simply stopped
            // typing there, because the deferral in sealPermitted keys on the caret and the caret
            // has moved on. The seal is where the abandoned cells become misses and where the
            // line's one combo break is taken, so an earlier seal would re-price every keypress
            // made on the NEXT line in between at a combo the player had not actually lost yet.
            // Holding the grace is what makes the skip PURE CARET MOVEMENT, and is therefore why
            // the desktop half carries no era bit for it: nothing judged changes value or timing.
            //
            // An array rather than a single index because a fast player can abandon line N and be
            // on line N+1 (and abandon that too) before N has sealed, and losing N's grace to N+1
            // is the very defect this exists to prevent. Entries are never cleared on seal, since
            // sealPermitted is only ever asked about nextSealIndex and that only moves forward. The
            // C# clears the array in TypingEngine.reset alongside lineSealed; this mirror has no
            // reset (no backwards seek and no replay rebuild), so allocating it here, where the
            // constructor already clears every cell's play state, is the same guarantee.
            this.lineAbandoned = new Array(this.lines.length).fill(false);
            // The engine's OWN live combo/score (TypingEngine.Combo / Score): what the HUD shows.
            // The submitted numbers do not come from here; they come from the score processor
            // mirror below, which the engine drives at exactly the points TypeBeatPlayfield drives
            // the real one. In vanilla play the two combos happen to track each other, but
            // they are separate accounts with separate rules and only one of them is submitted.
            this.combo = 0;
            this.maxCombo = 0;
            // TypingEngine.runPositions (backlog 259): WHERE each increment of the current run was
            // earned, one { line, cell } per unit of `combo`, in the order they were credited. The
            // ledger the back-dated seal break needs and the only thing that can answer "how much
            // of this run was earned past the cells this line is about to miss": combo is a single
            // integer, and a break that keeps part of a run has to know which part.
            //
            // `runPositions.length === combo` is the invariant, and it holds by construction
            // because the four ways combo moves all move this with it: an increment appends
            // (creditCombo), a break clears (breakRun, which hands the list to a redeemable break's
            // snapshot), a restore puts the snapshot's own entries back where they were earned, and
            // the back-dated seal drops exactly the entries it destroys (backDateBreakTo).
            //
            // Bounded by the map: a run is at most one increment per cell, since a cell judged
            // correct once is inert on every retype, so nothing here grows with the length of the
            // play.
            this.runPositions = [];
            this.score = 0;
            this.totalKeypresses = 0;
            this.correctKeypresses = 0;
            this.errorCount = 0;
            this.consecutiveWrongKeys = 0;
            this.activeTimeMs = 0;
            // THE LAZY CLOCK ARM (backlog 222), mirroring TypingEngine.wpmClockArmedLine /
            // wpmClockArmedAt: the line the WPM clock has been armed on AHEAD OF ITS CUE, and the
            // instant it was armed at, which is the time of the first press the player put on that
            // line while the song had not reached it yet. -1 / 0 when nothing is armed, which is
            // every ordinary in-sync frame. See clockRunsFrom for what it buys.
            //
            // Read only through clockRunsFrom, which validates the arm against the CURRENT
            // activeLineIndex rather than clearing it at each of the sites that can move the caret.
            // An arm left behind on a line the caret has left is simply not this line's arm, and a
            // line is only ever entered once (the caret advances, or goes to -1 and comes back on a
            // LATER line, never on an earlier one), so a stale arm can never be mistaken for a live
            // one.
            //
            // A pure function of (keypress times, beatmap): no wall clock and no frame cadence, so
            // it cannot make the browser's account depend on the display rate the desktop's replay
            // of the same run would not have had.
            this.wpmClockArmedLine = -1;
            this.wpmClockArmedAt = 0;
            this.lastUpdateTime = null;
            this.finished = false;
            // The HP pool (backlog 306, see HealthAccount): the stand-in for the
            // TypeBeatHealthProcessor a Player would cache, fed at the seams TypeBeatPlayfield and
            // TypeBeatHealthFeed feed the real one. `failed` below is read off it.
            this.healthAccount = new HealthAccount(() => { if (this.onFailed) this.onFailed(); });
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
            //                     instead of waiting for its cue (rollForwardIfFinishedEarly), and
            //                     since backlog 218 no earlier than FLETCHER_DRAG_GRACE_MS before
            //                     that cue (boundedRush). The finished line is left unsealed and
            //                     seals on its own normal deadline with nothing missed.
            //   DRAG FREEDOM      a line the player is still typing is not force-sealed at its
            //                     normal deadline; the seal is deferred by FLETCHER_DRAG_GRACE_MS
            //                     so the caret is never yanked off a line mid-word (sealPermitted).
            //   NO RUSH CAP       a press any distance ahead of the playhead lands, is judged on its
            //                     clock and credits combo (PR 3's second input era; before it a cap
            //                     broke combo, backlog 208, and then awarded Meh, backlog 347).
            //   LINE-START SNAP   a caret sitting PAST the last character of its line is handed to
            //                     the next line the moment that line starts, so a player who has
            //                     FINISHED is still carried along by the song
            //                     (snapForwardOnLineStart). An UNFINISHED line is never taken,
            //                     which is the point of the freedom.
            //
            // Per-char judgement windows are untouched: rushing reads as early deltas and dragging
            // as late ones, so accuracy, sync% and the judgement counts report the drift honestly.
            //
            // THE SECOND INPUT ERA (PR 3, TypingEngine.InputEra2, bit 1 of the second CONFIG flags
            // word) is live for every desktop stack and therefore unconditional here, with no flag:
            // no rush cap (above), one backspace undoing a word skip (tryUndoWordSkip), Space to skip
            // only under wrong input (the skip gate in processKey) and the refined retype anchor
            // (retypeSelectionAnchor). The C# defaults it FALSE so stored runs re-derive, so every
            // parity fixture feeding the C# arm sets InputEra2 = true beside RushCapCostsAccuracy.
            //
            // ALL THREE TRUE UNCONDITIONALLY, the same shape every other live-only rule in this file
            // takes (the span judgement, the word-gap input model, the space discipline, the
            // stretch narrowing). Since backlog 208 this is the desktop's LIVE default for every
            // stack, and the mod named Fletcher is the one that turns it OFF and re-pins the caret
            // (acronym FC). The browser has no mods payload, so the strict FC arm is unreachable
            // here; if /play ever grows one, FC is the mod that would clear these.
            //
            // In the C# they default FALSE, because that engine must also RE-DERIVE a stored replay
            // under the pinned era every pre-208 row was played in, and the pair travels as CONFIG
            // frame bit 5 (the rush bound as bit 7). The browser has no era axis: it plays live
            // only, writes no replay frames and re-scores no stored row, so the live value is the
            // only value it can hold, and the pre-218 UNBOUNDED era is unreachable in it exactly as
            // the pinned one is. The C# arm of the fuzz parity test therefore has to SET bits 5 and
            // 7 in the frames it feeds, the same treatment bits 2, 3, 4 and 6 already get.
            //
            // The backspace at the head of a line stepping back up into the line behind it is
            // mirrored too: the C# gates that on FletcherEnabled alone, with no era bit, so it is
            // live rule on an ordinary /play run (see processBackspace and stepBackIntoLine).
            this.fletcherEnabled = true;
            this.flexibleLineSnap = true;
            // THE SYMMETRIC RUSH BOUND (backlog 218): a finished caret may enter the next line only
            // from FLETCHER_DRAG_GRACE_MS before that line's own activationTime onward
            // (entryPermitted), the exact mirror of the drag side (sealPermitted), which holds a
            // line open exactly that long past its natural end. Without it RUSH was time-UNBOUNDED
            // while DRAG never was: rollForwardIfFinishedEarly handed the caret to the next line the
            // instant the last cell of the current one landed, however many seconds before that
            // line's cue, and the roll is TRANSITIVE, so a fast player could walk the whole map at
            // the top of the song with nothing but the rush cap (which cost combo and blocked
            // nothing, and is gone since PR 3) in the way.
            //
            // A refused roll PARKS the caret past the last cell of its line, the state the
            // line-start snap already understands: keypresses there are inert (processKey answers
            // false on a complete line, so no judgement, no typo, no combo break and nothing in the
            // accuracy denominator), the WPM clock does not run (update accrues only while the
            // active line is INCOMPLETE), and snapForwardOnLineStart performs the deferred roll the
            // moment the bound opens. The DRAG side is untouched, and neither the drag cutoff's
            // hand-over nor the seal loop's ordinary one is refused: those are the SONG arriving, an
            // entry that is late rather than early.
            //
            // Under manualNewlines (below, the browser's arm since backlog 307) neither roll nor snap
            // runs: a finished caret parks for the player's own newline, and the bound instead
            // decides whether the line that newline lands on may be TYPED yet (awaitingEntryAt).
            this.boundedRush = true;
            // MANUAL NEWLINES (TypingEngine.ManualNewlines, CONFIG frame bit 14, backlog 307): the
            // player closes a finished line themselves. The press that finishes a line does not
            // roll the caret (rollForwardIfFinishedEarly) and the line-start snap goes quiet
            // (snapForwardOnLineStart); a SPACE or ENTER on the finished caret is the newline
            // (rollForwardManually), and a finished line is HELD open to its drag cutoff
            // (manualNewlineHoldsLineOpen, read by sealPermitted and dragCutoffAt), so a player who
            // never presses is handed on exactly when the push warning's red bar completes. The
            // newline always lands, and a line handed over before its entry window opens WAITS
            // (awaitingEntry): its keys and Enter are swallowed and typebeat-player.js greys it.
            //
            // It is a desktop SETTING rather than a live-stack rule, and it has defaulted ON there
            // since PR 2 (TypeBeatRulesetConfigManager). The browser has no settings surface, so it
            // takes the desktop's SHIPPED default unconditionally, exactly as spaceSkipsWord does
            // above (backlog 198). The C# ENGINE property stays FALSE by default, because that
            // engine must also re-derive every run stored with the automatic hand-over; so the
            // parity harnesses SET bit 14 on the CONFIG frames they feed (and ManualNewlines on the
            // bare engines they build), the treatment bits 2, 3, 4, 5, 6, 7, 8, 10, 11, 12 and 16
            // already get.
            //
            // NEWLINE ON A TYPED LETTER (TypingEngine.NewlineOnTypedLetter, bit 15): a letter on a
            // finished caret hands the line over and lands on the next line's first slot. It rides
            // the same desktop setting (TypeBeatPlayfield sets it from ManualNewlines) and is gated
            // on manualNewlines here as there, so it is set with it and its bit travels with 14.
            this.manualNewlines = true;
            this.newlineOnTypedLetter = true;
            // THE FIRST LINE'S HEAD START (PR 2, TypingEngine.FirstLineLeadIn, CONFIG frame bit 16):
            // a press up to FIRST_LINE_LEAD_MS before the map's first vocal opens the first line (see
            // firstLineTypingOpensAt). Defaulted FALSE in the C# so a replay stored before it
            // re-derives under the old gate; set for every live stack there, and therefore
            // unconditionally here, the browser having no era axis.
            this.firstLineLeadIn = true;
            // The COUNTABLE-CHARACTER STREAM (the C# constructor's countableTargets /
            // countableBase / countablePrefix): the whole map read as one run of countable cells,
            // which is the currency the drift readout (charsAheadOfPlayhead) measures in, and the
            // rush cap did before PR 3 removed it. countableTargets holds every
            // countable cell's target time sorted ascending, so the playhead's position is a binary
            // search; countableBase[k] plus countablePrefix[k][i] says where line k cell i sits in
            // that stream, so the caret's position is a lookup. All immutable after construction.
            // The C# rebuilds this stream when its subdivision-target era flips (PR 5,
            // AlignSubdivisionTargets), because its lines can be re-laid after construction. The
            // browser never flips: beatmap.lines already carries buildCells' final, aligned targets
            // (tokenCellTargets takes the rule unconditionally), so one build per play reads the
            // same stream the live C# engine ends up with.
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
            // 167, again by 243, again by 259 and folded rather than replaced since 262):
            // { lineIndex, cellIndex, streak, ownPressCredit,
            // positions }, the cell a wrong keypress spoiled or a word
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
            // OffTimeRule.BreaksCombo, the pre-199 era, which this file has no arm for. The flexible
            // caret's RUSH CAP (backlog 208) left the list the same way at backlog 347 (an over-cap
            // press was awarded Meh and credited combo) and left the engine at PR 3, which removed
            // the cap. It rejoins the list in the C# only when both TypingEngine.InputEra2 and
            // TypingEngine.RushCapCostsAccuracy are clear, the pre-347 era, which this file has no
            // arm for either.
            //
            // "That had a streak to take" is backlog 176: a break landing while the run is ALREADY
            // at zero costs nothing, so it leaves an outstanding claim alone rather than replacing
            // it with an empty one, and correcting the older cell still resumes the run (see
            // snapshotRedeemableBreak).
            //
            // `positions` is backlog 259: the run this break took, cell for cell (see
            // runPositions), so redeeming the claim puts back not just HOW MUCH combo the break
            // cost but WHERE it was earned. Its length is always the `streak` beside it, and a
            // later seal on an earlier line back-dates against those very entries.
            //
            // `ownPressCredit` is backlog 243: how much of the CURRENT run was credited by the
            // claim's OWN press rather than typed after it. It is 1 for a claim a word skip took,
            // because the skipping space is judged on the word gap it lands on and rebuilds the run
            // to exactly 1 (creditTheClaimsOwnPress), and 0 for every other claim and for a skip
            // whose space credited nothing. A break standing on no more than that is passive the
            // same way a break landing at zero is, and SPENDS the credit when it does, so anything
            // the player really types afterwards arms the next break normally. There is no
            // SkipSpaceCreditRule here, for the reason the two rules above have no arm either: the
            // pre-243 era exists in the C# only to re-derive a stored row, and a live play is
            // permanently on NotAStreakOfItsOwn.
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
            // A wrong key TYPED THROUGH into a cell (the C#'s CharJudged(WrongChar)), carrying the
            // cell index and the line index it landed on. Display only (backlog 316).
            this.onTypoLanded = null;
            // TypingEngine.ComboRestored: a corrected typo just resumed the streak its wrong
            // keypress broke, carrying how much combo was put back. Raised with combo and maxCombo
            // already restored and BEFORE the corrected retype is judged.
            this.onComboRestored = null;
            this.onFinished = null;
            this.onFailed = null;
        }

        // The HP bar the HUD draws: the health account's value (HealthProcessor.Health), in [0, 1].
        // Until backlog 306 this was a read of the rejection streak, which a /play run can no longer
        // grow (every wrong letter and every mid-word space is typed through or consumed by the word
        // skip), so the bar sat at full whatever the player did.
        get health() { return this.healthAccount.value; }

        // Whether the run has failed (HealthProcessor.HasFailed). Read by processKey, update and
        // computeScore, and by typebeat-player.js, which concludes the play on it.
        get failed() { return this.healthAccount.hasFailed; }

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
        // deadline, unless manualNewlines is holding it (see manualNewlineHoldsLineOpen).
        //
        // Its mirror is entryPermitted below (backlog 218): this one is how far past a line's
        // natural END a dragging player may still be on it, that one is how far before a line's
        // natural START a rushing player may already be on it, and both distances are the one
        // FLETCHER_DRAG_GRACE_MS.
        //
        // A line the player ABANDONED with a line skip keeps the grace after the caret has left it
        // (see lineAbandoned): the skip is caret movement only, so the line it walked out of has to
        // reach its misses and its one combo break at the very instant it would have with the
        // player still sitting there doing nothing.
        sealPermitted(index, time) {
            // MANUAL NEWLINES: a line the player has TYPED OUT and not closed is held to the drag
            // cutoff rather than left to seal on its own deadline. That deadline is the next line's
            // first word in any ordinary map, so sealing there is exactly the pull the setting
            // exists to prevent; the cutoff is the instant the push warning's red bar completes.
            // Held together, the seal, the seal loop's hand-over of the caret and the closed step
            // back (see processBackspace) all land on that one instant.
            if (this.manualNewlineHoldsLineOpen(index)) {
                const held = this.lines[index];
                return time >= held.endTime + held.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
            }

            if (!this.fletcherEnabled || (this.activeLineIndex !== index && !this.lineAbandoned[index])) return true;

            const line = this.lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline. (This is also what lets the FINAL line, which has no next line to roll on
            // to, finish the run on time once it is fully typed.)
            if (this.noTypeableUntyped(line)) return true;

            return time >= line.endTime + line.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        // TypingEngine.manualNewlineHoldsLineOpen. Whether manualNewlines is holding `index` open:
        // a FINISHED line the player is still standing on, or one immediately behind a caret that
        // has been handed to the next line's head and could still step back up to it. The hold
        // ends at the drag cutoff, so a player who never presses is handed on exactly when they
        // would have been forced on with the setting off, not at the line's own deadline.
        //
        // A line that still owes a character is NOT held here (the drag rule already holds the
        // caret's own line, and holding one the player merely left behind untyped would put its
        // misses later than the song's own punishment), and neither is the LAST line, whose seal
        // is what ends the run.
        manualNewlineHoldsLineOpen(index) {
            if (!this.manualNewlines || !this.fletcherEnabled) return false;
            if (index + 1 >= this.lines.length || !this.noTypeableUntyped(this.lines[index])) return false;

            return this.activeLineIndex === index || (this.activeLineIndex === index + 1 && this.caretIndex === 0);
        }

        // TypingEngine.awaitingEntry. Whether the caret is on a line it may not TYPE on yet at
        // `time`: the player (or the song) has handed it on and that line's entry window has not
        // opened. The manual-newline era only, and only for a live line. processKey and
        // processEnter swallow presses while it holds; the getter below is the display's read.
        awaitingEntryAt(time) {
            return this.manualNewlines && this.fletcherEnabled && this.activeLineIndex >= 0
                && !this.entryPermitted(this.activeLineIndex, time);
        }

        // TypingEngine.AwaitingEntry, at the last update's time. typebeat-player.js greys the
        // current row while it is true (LyricStage's SetLineDim(0.4)).
        get awaitingEntry() {
            return this.awaitingEntryAt(this.lastUpdateTime === null ? -Infinity : this.lastUpdateTime);
        }

        // TypingEngine.entryPermitted. THE RUSH BOUND (see boundedRush), and the exact mirror of
        // sealPermitted above: may a FINISHED caret move on to line `index` at `time` yet? Drag
        // holds a line open up to FLETCHER_DRAG_GRACE_MS past its natural end (endTime +
        // sealGraceMs); rush enters a line up to the same FLETCHER_DRAG_GRACE_MS before its natural
        // start (activationTime). Always true under the unbounded era, which is what every run
        // stored before backlog 218 was played under and which this file cannot reach at all.
        //
        // Asked ONLY of a caret moving itself: the keypress roll (rollForwardIfFinishedEarly) and
        // the time-driven one (snapForwardOnLineStart). The hand-overs the SEAL LOOP performs, the
        // ordinary one and the drag cutoff's, do not consult it and must not: the song has moved off
        // the old line there, so entry is late rather than early, and refusing it would leave the
        // player in a dead zone the flexible caret does not otherwise have. On a loader-built map
        // that is never even a near thing, because a line's activation is clamped to its own
        // startTime, which IS the previous line's endTime, so this bound opens at most
        // FLETCHER_DRAG_GRACE_MS before the previous line could seal at all.
        entryPermitted(index, time) {
            return !this.boundedRush || time >= this.entryOpensAt(index);
        }

        // TypingEngine.entryOpensAt. The earliest instant the caret may be on line `index` by
        // RUSHING onto it: the line's own activationTime under the unbounded era, and
        // FLETCHER_DRAG_GRACE_MS before it under boundedRush, which is the head start a player earns
        // for having finished the line before it. Read by both arms that move a finished caret,
        // entryPermitted (the keypress roll) and snapForwardOnLineStart (the time-driven one).
        entryOpensAt(index) {
            return this.lines[index].activationTime - (this.boundedRush ? FLETCHER_DRAG_GRACE_MS : 0);
        }

        // TypingEngine.snapForwardOnLineStart. THE LINE-START SNAP (backlog 208): while the caret
        // sits PAST THE LAST CHARACTER of its line, the next line STARTING takes it, which is what
        // keeps the flexible default feeling like the pinned game it replaced (finish your line and
        // the song moves you on). A line the player has not finished is never touched: dragging
        // behind is precisely the freedom the flexible caret grants, and sealPermitted above makes
        // the same distinction for the same reason.
        //
        // Since backlog 218 this is also the DEFERRED ROLL arm (see boundedRush), which is why the
        // two are one method rather than two: both move a FINISHED caret onto the next line on a
        // TIME condition, they would fire on the same frame, and a second arm could only ever move a
        // caret the first one had already moved. The instant they fire at is entryOpensAt, the
        // line's activation under the unbounded era and FLETCHER_DRAG_GRACE_MS before it under the
        // bounded one, so the head start the bound grants a rushing player still exists: refusing
        // the keypress roll and then waiting for the full activation would take with one hand what
        // the mirror gives with the other.
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
            // boundedRush belongs in this gate as well as flexibleLineSnap: this is the only arm
            // that can move a caret the bound parked, and a live stack always sets both anyway.
            if (!this.fletcherEnabled || (!this.flexibleLineSnap && !this.boundedRush) || this.finished) return false;

            // MANUAL NEWLINES: a finished caret is the PLAYER's to hand over, so this arm does
            // nothing for them. What hands them on otherwise is the seal itself, the instant the
            // engine takes the line away (sealPermitted), and a FINISHED line is held to that
            // instant (manualNewlineHoldsLineOpen), which is when the push warning's red bar
            // completes: the seal loop's own hand-over moves a manual caret exactly when a dragging
            // one would be moved.
            if (this.manualNewlines) return false;

            let snapped = false;

            while (this.activeLineIndex >= 0
                   && this.isLineComplete(this.activeLineIndex)
                   && this.activeLineIndex + 1 < this.lines.length
                   && time >= this.entryOpensAt(this.activeLineIndex + 1)) {
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
        //
        // BOUNDED since backlog 218 (see boundedRush): "the moment a press finishes a line" is now
        // "the moment a press finishes a line, if that next line is within FLETCHER_DRAG_GRACE_MS of
        // starting". Refused, the caret parks past the last cell and snapForwardOnLineStart makes
        // the move for it when the bound opens. `time` is the keypress's own time, the same value
        // the press was judged on, so the bound is a pure function of (char, time) like everything
        // else here and the desktop's replay of the same run reproduces it exactly.
        rollForwardIfFinishedEarly(time) {
            if (!this.fletcherEnabled || this.finished || this.activeLineIndex < 0) return;

            // MANUAL NEWLINES: the press that finished the line does NOT hand the caret on. The
            // caret parks past the last cell and waits for the player's own newline
            // (rollForwardManually), or for the seal to force it, which is the same parked state a
            // refused rush leaves and therefore the same state every arm downstream understands.
            if (this.manualNewlines) return;

            if (this.caretIndex < this.lines[this.activeLineIndex].cells.length) return;
            if (this.activeLineIndex + 1 >= this.lines.length) return;
            if (!this.entryPermitted(this.activeLineIndex + 1, time)) return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always
            // unsealed.
            this.activeLineIndex++;
            this.caretIndex = 0;
            this.autoSkipForward();
        }

        // TypingEngine.rollForwardManually. THE MANUAL NEWLINE (see manualNewlines): the player's
        // own space-on-a-finished-line, Enter, or (under newlineOnTypedLetter) letter, which is the
        // ONLY thing that hands a parked caret on while the setting is armed. Returns whether the
        // caret moved, so a caller swallows only an effective press.
        //
        // Two conditions, and deliberately NOT the automatic roll's third: the caret must be
        // FINISHED and there must be a next line. The entry window does not gate the press: the
        // newline always lands, and the line it lands on waits greyed and untypeable
        // (awaitingEntry) until its window opens. No WPM clock work, for the reason processEnter
        // gives: a newline is not typing, so the landed line's clock arms lazily on its first real
        // press.
        rollForwardManually(time) {
            if (!this.manualNewlines || !this.fletcherEnabled || this.finished || this.activeLineIndex < 0) return false;
            if (this.caretIndex < this.lines[this.activeLineIndex].cells.length) return false;
            if (this.activeLineIndex + 1 >= this.lines.length) return false;

            this.activeLineIndex++;
            this.caretIndex = 0;
            this.autoSkipForward();
            return true;
        }

        // TypingEngine.clockRunsFrom. The instant from which the WPM/active-time clock runs across
        // the frame that STARTED at previousTime, or null when it does not run over that frame at
        // all. Normally that instant is previousTime itself, i.e. the whole frame counts; the one
        // case that returns something later is the lazy arm below.
        //
        // The whole frame, always, with a pinned caret. Under the flexible one the caret can be
        // sitting on a line the song has not reached yet, and a clock that ran through a 20-second
        // instrumental would read the wait as typing time; so being there is not by itself enough,
        // and the clock runs from that line's activationTime, which is exactly when the line would
        // have gone active while pinned.
        //
        // THE TWO PARKED STATES ARE NOT THE SAME FACT (backlog 222 correcting backlog 218, whose
        // note here said they were, and that claim is what hid the defect below). A caret the rush
        // bound left sitting past the last cell of its OWN line still needs nothing from this
        // method: the caller accrues only while the active line is INCOMPLETE, that caret's line is
        // complete by definition, and there is genuinely nothing the player can type. But a caret
        // that rollForwardIfFinishedEarly or snapForwardOnLineStart has moved on to the NEXT line
        // sits there from entryOpensAt, which is FLETCHER_DRAG_GRACE_MS BEFORE that line's
        // activation, and processKey has no time gate of its own: the player really can type there,
        // up to 1500 ms per line. Every character they land is counted by countCorrectCells for the
        // rest of the run, so counting them against a stopped clock walked liveWpm (and the HUD's
        // rolling readout in typebeat-player.js, which stamps its samples in this same currency)
        // upward for free.
        //
        // THE LAZY ARM. So the clock arms on the FIRST press the player puts on such a line
        // (armWpmClockAheadOfTheCue) and runs from that press's own time onward. Not from
        // entryOpensAt: that would hand the whole head start back as typing time for a player who is
        // sitting there NOT typing, which is the dilution the activationTime gate exists to prevent.
        // And nothing is back-dated: the arming press is credited no elapsed time at all, exactly
        // like a press made at activationTime + 0 on the ordinary path. Once the playhead reaches
        // the line, the activationTime branch above answers first and the arm is never consulted
        // again.
        clockRunsFrom(previousTime) {
            if (!this.fletcherEnabled || this.activeLineIndex < 0) return previousTime;

            if (previousTime >= this.lines[this.activeLineIndex].activationTime) return previousTime;

            // Ahead of the cue: the clock runs only from an arm, and only from an arm belonging to
            // the line the caret is on now (see wpmClockArmedLine for why that check is the reset).
            if (this.wpmClockArmedLine === this.activeLineIndex) {
                return Math.max(previousTime, this.wpmClockArmedAt);
            }

            return null;
        }

        // TypingEngine.armWpmClockAheadOfTheCue. Arm the WPM clock on the active line when the press
        // at `time` is the first one the player has put on it AHEAD OF ITS CUE (see clockRunsFrom).
        // No-op on every ordinary press, i.e. one made at or after the line's activationTime, where
        // the clock is already running.
        //
        // Called from processKey once the press is known not to be inert, so every press that can
        // resolve or spoil a cell arms the clock, a wrong key included: what the arm records is that
        // the player is TYPING here, not what the keystroke turned out to be worth. A press the
        // caret-parked guard refuses outright never reaches it, which is right, since that press
        // does nothing at all.
        armWpmClockAheadOfTheCue(time) {
            if (!this.fletcherEnabled || this.activeLineIndex < 0) return;

            if (time >= this.lines[this.activeLineIndex].activationTime) return;

            // Only the FIRST press arms. A later one must not push the arm forward, or the time
            // between the two presses (the player typing) would be swallowed.
            if (this.wpmClockArmedLine === this.activeLineIndex) return;

            this.wpmClockArmedLine = this.activeLineIndex;
            this.wpmClockArmedAt = time;
        }

        // TypingEngine.PlayheadCountablePosition. How many COUNTABLE characters the song has
        // reached by `time`: the count of countable cells across the whole map whose target time is
        // at or before it. The playhead's position in the countable stream, and the reference the
        // drift readout (and, before PR 3, the rush cap) is measured against. Monotonic in time and
        // a pure function of the beatmap.
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
            return this.countablePositionAt(this.caretIndex);
        }

        // TypingEngine.countablePositionAt. caretCountablePosition for an ARBITRARY caret index on
        // the active line. Split out for backlog 260, where the rush cap had to measure a skipping
        // space against the caret as it stood BEFORE the skip moved it (the cap is gone since PR 3):
        // the property above is exactly this at the live caret.
        countablePositionAt(index) {
            if (this.activeLineIndex < 0) return 0;

            const prefix = this.countablePrefix[this.activeLineIndex];
            const at = Math.min(Math.max(index, 0), prefix.length - 1);

            return this.countableBase[this.activeLineIndex] + prefix[at];
        }

        // TypingEngine.CharsAheadOfPlayhead. Signed countable-character drift of the caret against
        // the playhead: positive = rushing ahead, negative = dragging behind. The quantity the rush
        // cap bounded until PR 3 removed it, and still the honest read-out of what the flexible
        // caret is about. (TypingEngine.rushesPastCap is not mirrored: InputEra2 never consults it.)
        charsAheadOfPlayhead(time) {
            return this.caretCountablePosition - this.playheadCountablePosition(time);
        }

        // TypingEngine.NextUnsealedLineIndex. The first line that has not sealed yet; -1 once every
        // line has sealed. While no line is active (pre-roll, or the dead zone between a seal and
        // the next line's cue) this is the UPCOMING line. Read-only and judgement-free: nothing in
        // this file reads it, it exists because the line the SONG is on is no longer the line the
        // caret is on (backlog 208), and typebeat-player.js has to put the sung sweep on the former.
        get nextUnsealedLineIndex() {
            return this.nextSealIndex < this.lines.length ? this.nextSealIndex : -1;
        }

        // TypingEngine.SongWindowOpen. Whether the PLAYHEAD is inside a typeable line window: the
        // plain time rule on the first unsealed line, read independently of where the player's
        // caret has got to. Equal to "a line is active" with a pinned caret; under the flexible one
        // the two diverge, because the caret can be parked on a line the song has not reached
        // (rush) or still finishing one the song has left (drag).
        // TypingEngine.FirstLineTypingOpensAt. Whether the map's FIRST line is inside the head start
        // FIRST_LINE_LEAD_MS gives it at `time`: the window in which a press may open a line the
        // clock has not activated yet (see processKey). The line's own activationTime is NOT moved,
        // because the WPM clock is armed from it; a press made in the head start arms the clock
        // ahead of the cue exactly as a rushed press on any later line does (armWpmClockAheadOfTheCue).
        //
        // The C# gates it on FirstLineLeadIn, CONFIG frame bit 16, which the live playfield sets for
        // every stack and a replay stored before PR 2 carries clear. The browser only ever plays
        // live, so it takes the head start unconditionally (firstLineLeadIn below is always true),
        // and every parity fixture that feeds the C# arm has to set the bit, the same treatment bits
        // 2 through 12 already get.
        firstLineTypingOpensAt(time) {
            if (!this.firstLineLeadIn || this.finished || this.activeLineIndex !== -1) return false;
            if (this.nextSealIndex !== 0 || this.lines.length === 0) return false;

            const first = this.lines[0];

            return time >= Math.min(first.activationTime, first.firstVocalTime - FIRST_LINE_LEAD_MS)
                && time < first.endTime + first.sealGraceMs;
        }

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
        // caret (TypeBeatPlayfield's key handler). Since backlog 230 the browser has a real skip
        // too (typebeat-player.js: a fresh BufferSource started at an offset seeks exactly), and
        // this predicate gates the same two things there: the countdown chip that labels a dead
        // stretch, and the chip's own skip button. With the caret parked on the next line, "a line
        // is active" no longer means "the song is asking for characters".
        get songIsOnTheCaretsLine() {
            return this.activeLineIndex >= 0 && this.activeLineIndex === this.nextSealIndex && this.songWindowOpen;
        }

        // TypingEngine.ActiveLineUntouched. True while the player has put nothing into the active
        // line yet: no cell behind the caret is 'correct' or 'wrong' (leading auto-skipped
        // punctuation, and cells a word skip abandoned, are not progress). A PURE READ, changing
        // nothing. The key handler (typebeat-player.js, routeKeyDown) pairs it with
        // songIsOnTheCaretsLine to drop a habitual Space on a caret parked at the head of a line
        // the song has not reached, exactly as TypeBeatPlayfield's key handler does.
        get activeLineUntouched() {
            if (this.activeLineIndex < 0) return false;

            const cells = this.lines[this.activeLineIndex].cells;
            const end = Math.min(this.caretIndex, cells.length);

            for (let i = 0; i < end; i++) {
                const state = cells[i].state;
                if (state === 'correct' || state === 'wrong') return false;
            }

            return true;
        }

        // TypingEngine.DragCutoffAt. THE PUSH (backlog 263), read out for DISPLAY ONLY: the instant
        // the caret's own line will be force-sealed out from under it and the caret landed on the
        // next line, or null when no such push is coming. Nothing here decides anything, it only
        // reports the deadline sealPermitted already compares against, so typebeat-player.js can
        // warn the player before it arrives (the red right-anchored bar, the mirror of the cue-in
        // bars) instead of the push landing with no notice at all.
        //
        // Non-null under exactly the three conditions the drag cutoff's hand-over arm needs. The
        // caret must be UNPINNED (fletcherEnabled), or the line was never held open for the player
        // in the first place; the browser holds that true unconditionally, and the clause is kept
        // because this is a mirror of the C# and a divergence in the CONDITION is as real as one in
        // the value. The caret's line must also be the next line due to seal, or the seal loop
        // reaches it with the caret elsewhere and that arm does not run: a line the player walked
        // out of with a line skip is still held open by lineAbandoned, but nobody is standing on it
        // to be pushed. And the line must still owe a character (the same noTypeableUntyped scan the
        // seal asks), because a line with nothing left untyped seals on its ordinary deadline with
        // no drag to protect and no punishment to warn about: typing the last cell out calls the
        // push off there and then.
        //
        // ONE EXCEPTION, and it warns about a push that really is coming: under manualNewlines a
        // line the player has typed out but not closed is held to its own cutoff
        // (manualNewlineHoldsLineOpen), so the bar keeps counting down to the instant that line is
        // taken from them, the press being what moves the caret on and the cutoff the backstop.
        get dragCutoffAt() {
            if (this.finished || !this.fletcherEnabled || this.activeLineIndex === -1
                || this.activeLineIndex !== this.nextSealIndex) return null;

            const line = this.lines[this.activeLineIndex];

            if (this.noTypeableUntyped(line) && !this.manualNewlineHoldsLineOpen(this.activeLineIndex)) return null;

            return line.endTime + line.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        // DrawableTypeBeatCharObject.ApplyEngineResult: a cell hands the score processor its ONE
        // result and every later attempt on the same cell is dropped (`if (Judged) return;`). Every
        // processor.applyResult in this engine goes through here, so the submitted account can never
        // hold two entries for one cell however the play reaches it.
        //
        // The same one result is the one the HEALTH processor takes, so this is also where HP
        // recovers and where a seal Miss drains. The result is the AWARDED tier's, so a fix the
        // backlog 210 cap holds to Ok recovers OK_HEALTH_INCREASE however well it was timed, exactly
        // as on the desktop.
        //
        // HEALTH FIRST, then score, which is Player's own order (DrawableRuleset.NewResult) and is
        // not cosmetic: HealthProcessor stamps every result with JudgementResult.FailedAtJudgement
        // (whether the play had ALREADY failed when it arrived), and ScoreProcessor drops a result
        // so stamped. So the result that empties the bar still counts, and every result after it
        // (the rest of a failing seal, the word gap a failing skip's space lands on) reaches the
        // cell but not the submitted account.
        applyCellResult(cell, result) {
            if (cell.judged) return;
            cell.judged = true;

            const failedAtJudgement = this.healthAccount.hasFailed;

            this.healthAccount.applyResult(result);

            if (!failedAtJudgement) this.processor.applyResult(result, cell);
        }

        sealLine(idx) {
            const line = this.lines[idx];

            // The cells the line really did run out of time on, as opposed to the ones a word skip
            // had already given up: the only group whose break has not been taken yet, and
            // therefore the only one that can break combo here. The C# keeps the wider MISSED count
            // alongside it for LineSealResult.MissedCells, which this mirror raises no event for.
            let unforeseen = 0;

            // The LAST of them, in cell order: the position this line's break is back-dated to
            // (backlog 259, see runPositions). Meaningless while unforeseen is 0, which is exactly
            // when nothing reads it.
            let lastUnforeseenCell = -1;

            // The cells a word skip had abandoned and nobody reclaimed (the C#'s `abandoned` list,
            // raised as AbandonSealed): their skip drain is refunded before their Miss results land.
            let phantoms = 0;

            // PASS ONE, the C#'s own seal loop (TypingEngine.Update): resolve the STATES and count,
            // and nothing else. The results are a second pass now, because the seal's one combo
            // break has to be taken between the two (backlog 259): back-dated, that break is
            // mirrored into the submitted account by hand, and it must land before any of this
            // seal's results are weighted by the combo it leaves. The C# gets that ordering from
            // its event shape (the break is taken in the seal loop, the results arrive later in
            // TypeBeatPlayfield.onLineSealed); this file has to write it out.
            for (let i = 0; i < line.cells.length; i++) {
                const c = line.cells[i];

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
                // cell does not: that break was taken at the skip. It is excluded from the
                // back-dating pivot for the same reason: the break it is entitled to was taken then.
                const phantom = c.state === 'abandoned';

                if (c.typeable && (c.state === 'untyped' || phantom)) {
                    c.state = 'missed';
                    c.judgeType = 'Miss';

                    if (!phantom) {
                        unforeseen++;
                        lastUnforeseenCell = i;
                    } else {
                        phantoms++;
                    }
                }
            }

            if (unforeseen > 0) {
                // AT MOST ONE combo break per sealed line, no matter how many cells were missed, and
                // BACK-DATED to the last of them (backlog 259, TypingEngine.BackDatedSealBreak): the
                // break belongs to the cells the line ran out of time on, so it destroys the run as
                // it stood AT the last of them and leaves every increment earned strictly past it
                // standing. A line's misses only exist at its SEAL, which under the flexible caret
                // lands up to FLETCHER_DRAG_GRACE_MS after the song left the line, by which time the
                // player is on the next line rebuilding, so the wipe was taking a second break for a
                // fumble that had already cost one.
                //
                // UNCONDITIONAL here, where the C# reads the era flag its replays carry: the browser
                // only ever plays live (see `restorable`), and the live client sets the flag for
                // every mod stack, because when a break lands is not a window, an input model or a
                // caret and no mod has an opinion about it.
                const surviving = this.backDateBreakTo(idx, lastUnforeseenCell);

                // A real break, so it owns the streak (backlog 140). Distinct from the line-scoped
                // drop below, and since backlog 208 the two come apart HERE as well as in the C#:
                // the flexible caret can already be on a LATER line, holding a snapshot this break
                // has just cost it.
                //
                // Unconditional even when part of the run survived: a claim's streak was earned
                // EARLIER than the run this break cuts back, so redeeming it later could only put
                // back combo the break was entitled to take.
                this.discardRestorableStreak();

                // TypeBeatPlayfield.onLineSealed's hand-mirrored break, BEFORE the results below so
                // that every one of them, the seal's own unfixed typos included, is weighted by the
                // combo the break left. The Miss results no longer carry this break at all (they are
                // all combo-neutral now), which is what stops it landing a second time on a run the
                // player built past the missed cells.
                this.processor.breakComboTo(surviving);

                if (this.onComboBroken) this.onComboBroken();
            }

            // PASS TWO. DrawableTypeBeatHitObject.ApplySealResults: EVERY still-unjudged nested char
            // drawable of the line takes its result at seal time, in cell order, and the loop skips
            // cells that already carry one. `judged` is deliberately not the state test above: the
            // two come apart for a cell typed correctly and then BACKSPACED, which is 'untyped'
            // again and so is counted a miss for display, while its drawable keeps the Great it
            // already took.
            //
            // The result is a MISS for a cell nobody typed and a 'good' (the uncorrected-typo key,
            // TypeBeatResultMapping.UNFIXED_TYPO) for one left holding a wrong character, and BOTH
            // are marked COMBO-NEUTRAL immediately before they are applied, exactly as
            // TypeBeatPlayfield.onLineSealed does it. Every seal miss since backlog 259, where only
            // the typos and the word skip's abandoned cells were marked before: the seal's break has
            // moved off the results and onto the hand-mirror above, and a Miss carries a break to
            // exactly one place, the combo it FINDS when it lands, which is the run the player holds
            // NOW rather than the one the back-dated break was entitled to cut.
            // A sealed line's cells can never be typed again, so a snapshot left on this one is
            // unredeemable whether or not the seal broke anything. Load-bearing since backlog 208:
            // the flexible caret DOES run ahead of the seal, so a run that finishes a line early
            // and then spoils a cell on the next one is holding a claim on a line the song is still
            // sealing behind it. Dropping it keeps the state truthful rather than relying on the
            // caret never going back.
            if (this.restorable !== null && this.restorable.lineIndex === idx) this.restorable = null;

            // TypeBeatPlayfield.onAbandonSealed -> TypeBeatHealthFeed: the skip drained each phantom
            // cell at the keypress, and the Miss each is about to take below drains again, so the
            // skip's drain is given back FIRST (the C# raises AbandonSealed before LineSealed). The
            // pair nets to one charge per cell, unless the bar was clamped at full in between.
            this.healthAccount.refundDeferredDrain(phantoms);

            for (const c of line.cells) {
                if (!c.typeable || c.judged) continue;

                this.processor.markComboNeutral(c);
                this.applyCellResult(c, c.state === 'wrong' ? 'good' : 'miss');
            }

            // DrawableTypeBeatHitObject.ApplySealResults' last act: the LINE object resolves as
            // IgnoreHit. Inert on the score account (not modelled there at all), but not on the
            // health one: HealthProcessor runs its fail test on EVERY applied result, so a bar a
            // run of rejections left at 0 fails here even if the line took nothing else.
            this.healthAccount.applyResult(null);
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

        // TypingEngine.creditCombo. Credit one combo increment, earned on the cell the press LANDED
        // on (not on the caret, which is not the same thing under the C#'s AnyOrderWithinWord; the
        // browser has no Dyslexia mod, so here the two coincide and the landed cell is still what is
        // recorded, because that is what the ledger means). The one place `combo` grows by a
        // keypress, so runPositions cannot fall behind it.
        creditCombo(cellIndex) {
            this.combo++;
            if (this.combo > this.maxCombo) this.maxCombo = this.combo;
            this.runPositions.push({ line: this.activeLineIndex, cell: cellIndex });
        }

        // TypingEngine.breakRun. Zero the run and hand back the positions that composed it: a
        // REDEEMABLE break puts them on its snapshot (snapshotRedeemableBreak) so a redemption can
        // restore them, and every other break simply drops them. The one place a break empties the
        // ledger, which is what keeps runPositions.length === combo true through all of them.
        breakRun() {
            const broken = this.runPositions;

            this.runPositions = [];
            this.combo = 0;

            return broken;
        }

        // TypingEngine.backDateBreakTo (backlog 259). A break dated at (lineIndex, cellIndex) rather
        // than at now: every increment earned AT OR BEFORE that cell is destroyed and every
        // increment earned strictly past it survives, in place. Returns the surviving run, which is
        // also `combo`'s new value.
        //
        // maxCombo is deliberately not touched: it records a run the player really did hold, and
        // this break is dated in the past, not a claim that the run never happened.
        backDateBreakTo(lineIndex, cellIndex) {
            const survivors = this.runPositions.filter(
                p => p.line > lineIndex || (p.line === lineIndex && p.cell > cellIndex)
            );

            this.runPositions = survivors;
            this.combo = survivors.length;

            return survivors.length;
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
        // "A streak to own" excludes the streak the OUTSTANDING claim's own press credited (backlog
        // 243). One press can do both: a space struck inside a word abandons the rest of it and is
        // then judged on the word gap it lands on, which puts the combo back to 1. A break on that
        // very gap therefore broke a run of 1 rather than of 0, and under 176 alone that was enough
        // to overwrite a claim hundreds deep with a worthless one. The 1 was not progress, it was
        // the break's own press, so a break taking no more than the claim's own credit is passive
        // exactly as a zero-streak break is, and SPENDS that credit on the way past: whatever the
        // player rebuilds after this break is measured from zero, so the next break arms normally.
        // A correct character after the skipping space puts the run at 2 and the next break takes
        // the claim as it always did.
        //
        // A break that DOES own its streak takes the claim, and since backlog 262 it takes the
        // DISPLACED claim's streak with it rather than dropping it: the older break's increments are
        // just as unreachable as a passive break's, and a player who corrects both accidents in full
        // is entitled to both. The chain is then redeemed at the NEWEST of the broken cells, and the
        // seal's back-dated break (backlog 259) is what still takes back anything the line never
        // really earned. See the arm itself, below.
        //
        // The C# reads three era switches here that this file has no counterpart to, for the reason
        // set out on `restorable`: ComboRestoreRule, which decides whether any snapshot is taken at
        // all, ComboClaimRule, which decides the condition below, and SkipSpaceCreditRule, which
        // decides whether the ceiling is the claim's credit or a flat zero. The browser only ever
        // plays live, so all three are pinned to their live arms, the ceiling is just the credit,
        // and the condition stands unguarded.
        // `brokenPositions` is the run this break just took, cell for cell (breakRun, see
        // runPositions), so a redemption puts back not just HOW MUCH combo the break cost but WHERE
        // it was earned. Without it a restored streak would have to be dated at the cell that
        // redeemed it, and a later seal on an earlier line would then keep increments its misses are
        // entitled to destroy. Its length is always the `streak` beside it.
        snapshotRedeemableBreak(cellIndex, brokenStreak, brokenPositions) {
            const claim = this.restorable;

            if (claim !== null && brokenStreak <= claim.ownPressCredit) {
                // Passive: the claim stands where it is, with the credit this break just spent
                // taken off it.
                //
                // BACKLOG 260: it took nothing the player earned, but it still SPENT a run, so that
                // run FOLDS INTO the claim it left standing rather than being dropped. The call site
                // has already run breakRun, so anything the claim does not take is gone for good,
                // and the cells that earned it are resolved, so no retype can earn it back. Streak
                // and positions move together (positions.length === streak is the ledger's
                // invariant, see runPositions), and the broken ones append in run order because the
                // claim's own increments were earned before them. A NEW array either way: the broken
                // list is the old runPositions reference and must never be pushed onto.
                const positions = brokenStreak > 0 ? claim.positions.concat(brokenPositions) : claim.positions;

                this.restorable = {
                    lineIndex: claim.lineIndex, cellIndex: claim.cellIndex, streak: claim.streak + brokenStreak,
                    // The credit is still zeroed: the exemption is worth exactly one break (backlog
                    // 243), and folding the run in does not re-arm it.
                    ownPressCredit: 0,
                    positions: positions
                };
                return;
            }

            // BACKLOG 262: this break OWNS the streak it broke, so it takes the claim, but the claim
            // it displaces is not therefore worthless: the increments behind it were earned and the
            // cells that earned them are resolved, so discarding it loses them for good even though
            // the player can still go back and correct both accidents. The displaced claim FOLDS into
            // this one instead, which makes the NEWEST of the broken cells the one that redeems the
            // whole chain, and chains transitively through a third break and a fourth. Positions go in
            // front of this break's own, oldest first, because a redemption puts them back at the head
            // of the ledger (resumeStreakIfThisRedeemsTheBreak); the count still equals the streak;
            // and the credit still starts at zero, so backlog 243's one-break exemption is neither
            // granted nor extended by folding.
            //
            // A NEW array, for the reason the passive arm gives: the broken list is the old
            // runPositions reference and must never be pushed onto.
            //
            // The C# gates this on TypingEngine.FoldsDisplacedClaim (CONFIG frame bit 12) so that
            // every stored replay re-derives with the displaced claim discarded, which is the
            // max_combo its player was given. This file has no such arm, for the reason set out on
            // `restorable`: the browser only ever plays live, so the rule stands unguarded.
            //
            // What keeps it honest is backlog 259's back-dated seal break: the fold restores
            // increments earned before the older break without that break's own cell having been
            // fixed, but the restored positions go back WHERE THEY WERE EARNED, so a line sealing on
            // cells nobody typed still destroys every increment at or before its last unforeseen miss.
            if (claim !== null && claim.streak > 0) {
                this.restorable = {
                    lineIndex: this.activeLineIndex, cellIndex: cellIndex, streak: claim.streak + brokenStreak,
                    ownPressCredit: 0,
                    positions: claim.positions.concat(brokenPositions)
                };
                return;
            }

            this.restorable = {
                lineIndex: this.activeLineIndex, cellIndex: cellIndex, streak: brokenStreak, ownPressCredit: 0,
                positions: brokenPositions
            };
        }

        // TypingEngine.creditTheClaimsOwnPress. The press that just took (or passively kept) the
        // outstanding claim has itself credited one combo: record that on the claim, so a break
        // landing before the player has typed anything else takes nothing and leaves the claim alone
        // (backlog 243, see snapshotRedeemableBreak).
        //
        // Called from the one arm such a press can reach: a word skip's space, falling through to be
        // judged on the word gap the skip parked the caret on. Keyed on the press having ACTUALLY
        // credited combo rather than on the skip having happened, so a skip whose space earned
        // nothing (an inert retype of an already judged gap, the pre-347 rush cap refusing a caret
        // that was ALREADY out past its bound before the skip, an era the browser is never in since
        // PR 3 removed the cap, or a word abandoned all the way to the end of
        // a line, where there is no gap for the space to land on at all) records no credit and
        // behaves exactly as backlog 176 left it. The word this press gave up no longer counts
        // against that bound (backlog 260), which is why the cap refusing here is now a statement
        // about the player's own lead.
        creditTheClaimsOwnPress() {
            if (this.restorable !== null) this.restorable.ownPressCredit = 1;
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
        //
        // Backlog 243's ownPressCredit is not read here, and the C# does not read it either: it
        // widened a positional destructure and discarded the new member. The credit only ever
        // decides who OWNS the claim, never what redeeming it is worth.
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

            // The restored increments go back WHERE THEY WERE EARNED, at the head of the run, not at
            // the cell that redeemed them (backlog 259): a later seal on an earlier line back-dates
            // against those positions, and dating them here would let a break's misses keep combo
            // they are entitled to destroy. claim.positions.length is always claim.streak, so the
            // ledger comes back exactly as long as the run it is now describing.
            this.runPositions.unshift(...claim.positions);

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
        //
        // Returns whether a redeemable claim is outstanding when it hands the press back, which is
        // what tells processKey that the combo the SAME press is about to earn on the word gap
        // belongs to the break rather than to the player (backlog 243, see
        // creditTheClaimsOwnPress). True for the claim this skip took AND for an older one it
        // passively left alone, because the argument is about the press and not about which break
        // wrote the claim.
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

            if (abandoned.length === 0) return false;

            // AT MOST ONE combo break for the whole word, the rule sealLine's misses follow.
            const brokenStreak = this.combo;

            const brokenPositions = this.breakRun();

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
            this.snapshotRedeemableBreak(abandoned[0], brokenStreak, brokenPositions);

            if (this.onComboBroken) this.onComboBroken();

            // TypeBeatPlayfield.onWordAbandoned: the skip's one break on the SUBMITTED account, by
            // hand. It used to ride on the Miss the first abandoned cell took here; those results
            // now arrive at the seal, a whole line later, so with nothing left to carry the break
            // this seam carries it, exactly as the wrong-keypress path carries its own.
            this.processor.breakCombo();

            // The other half of the same C# seam (TypeBeatHealthFeed on WordAbandoned): HEALTH
            // drains MISS_HEALTH_DRAIN per abandoned cell now, at the skip, and gives it back at
            // whichever exit the cells take (the backspace that reclaims them, or the seal about to
            // miss them). Taken HERE, before the same press is judged on the word gap below, because
            // the C# drains before it recovers and the bar clamps at full in between.
            this.healthAccount.applyDeferredDrain(abandoned.length);

            // NO onCharJudged for the abandoned cells, deliberately, and the omission mirrors the
            // wrong-char path above: that hook is this renderer's rolling-WPM tap, and the C#
            // pushRollingSample() sits on the accepted-keypress path only, so tapping it here would
            // drift the browser's WPM readout away from the desktop's. The cells repaint from state.
            // engine `counts` is left alone too: it is the scored-only dict, and the C# records
            // nothing for an abandoned cell either (its Miss is counted at the seal).

            return this.restorable !== null;
        }

        update(time) {
            // (0) A FAILED run is frozen (backlog 306). The desktop fails inside whatever is being
            // processed (a keypress, or one of a seal's results), and Player.PerformFail SCHEDULES
            // ConcludeFailedScore rather than running it, so the step that failed runs to its end:
            // the rest of that seal pass, and every other line the same Engine.Update seals, is still
            // judged. None of it reaches the SUBMITTED account, though: each of those results arrives
            // stamped FailedAtJudgement and ScoreProcessor drops it (see applyCellResult). That is
            // this update() call running to its end, which is why the guard sits HERE, at the entry
            // of the NEXT one, and not inside the seal loop. processKey carries the same guard, and
            // typebeat-player.js concludes at the end of the tick.
            if (this.failed) return;

            // (1) accrue active typing time using this frame's span.
            //
            // The C# TypingEngine.Update takes a clockRate here and accrues dt / rate, because a
            // speed-adjusting mod makes beatmap milliseconds and real ones diverge and every WPM
            // readout is otherwise wrong by 1/rate. This mirror deliberately has no such parameter:
            // the browser player offers no rate mods at all (nothing here touches playbackRate), so
            // its rate is permanently 1 and the two accumulators agree. Adding one here is the
            // first thing to do if browser play ever gains a speed mod.
            //
            // clockRunsFrom is the flexible caret's own clause (backlog 208): a caret parked at the
            // head of a line the song has not reached yet is WAITING, not typing, so the clock does
            // not run through the instrumental it is waiting out. Since backlog 222 it answers WHERE
            // in the frame the clock starts rather than whether it ran at all, because such a caret
            // can also be TYPING: the frame the pre-cue lazy arm falls inside credits only the
            // stretch after the arm, and null is the no-accrual answer.
            //
            // KNOWN AND LEFT ALONE, exactly as in the C#: the predicate is evaluated on the CURRENT
            // activeLineIndex but with the PREVIOUS frame's time, so on a frame the caret rolled
            // forward inside, the part of it spent typing on the OLD line is tested against the NEW
            // line's cue and dropped. That is at most one frame of real typing time per line
            // transition (and only when a keypress-driven roll lands BETWEEN two frames), and
            // splitting the interval would need the old line index and the transition instant
            // carried as extra state, which is more machinery than the milliseconds are worth.
            if (this.lastUpdateTime !== null && this.activeLineIndex >= 0 && !this.finished
                && !this.isLineComplete(this.activeLineIndex)) {
                const from = this.clockRunsFrom(this.lastUpdateTime);

                if (from !== null) this.activeTimeMs += Math.max(0, time - from);
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
        // classify ladder grades distance from the syllable's edge.
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
        // Since backlog 247 the group's FIRST cell narrows again, and it is the one cell the span
        // never really described: it is judged on the signed distance from the span's START, so
        // pacing a syllable out beats bursting it at the window's edge. The early side is
        // byte-identical to the span rule (a press before the start already answered that distance),
        // only the late side tightens, and the stretch exclusion above still wins, so a stretch cell
        // that opens a group stays on its own character target.
        //
        // No era arm here, unlike the C#. The desktop engine defaults to CLASSIC on all three axes
        // and turns the span rule (CONFIG frame flags bit 2), the stretch narrowing (bit 6) and the
        // first-character hybrid (bit 8) on for live play, because it must also RE-DERIVE stored
        // replays under the rules their fingers were graded on. The browser only ever plays live: it
        // has no mods payload, no replay input, it writes no replay frames (a /play submission
        // carries the aggregate account alone, and PUT /api/v2/scores/{id}/replay is the desktop
        // client's own upload path), and nothing re-scores a stored row through this file. So the
        // live rule is the only rule this engine can be in, and all three parts of it are
        // unconditional.
        //
        // THE FOURTH SPAN, the EASY mod's word-level shelter (TypingEngine.WordShelter), is NOT
        // mirrored, and it is the one arm here that is a mod rather than an era. It draws the same
        // rule around the whole WORD (TypingLine.Words) instead of the syllable, so a press anywhere
        // inside the word is dead on. /play has no mods payload at all (see the scoreMultiplier note
        // where the total is computed), so the browser can no more turn Easy on than it can turn on
        // the window scale that travels with it, and porting the shelter would add a span nothing
        // could select. The day browser play gains a mods payload, this is the second place it has
        // to arrive, right after WINDOWS.
        judgedDeltaFor(line, cellIndex, time) {
            const syllable = syllableIndexOf(line, cellIndex);

            if (syllable >= 0 && !isCharTimedStretch(line, cellIndex)) {
                const group = line.syllables[syllable];

                // The precedence is the C#'s: the stretch exclusion above, then the group's opening
                // cell, then the span's own edges.
                if (cellIndex === group.startCell) return time - group.startTime;

                if (time < group.startTime) return time - group.startTime;
                if (time > group.endTime) return time - group.endTime;

                return 0;
            }

            return time - line.cells[cellIndex].target;
        }

        // TypingEngine.ProcessEnter. LINE SKIP (backlog 241): give up the rest of the active line
        // and move on. Deterministic in (input, time) exactly like processKey, and returns whether
        // it did anything, so the caller swallows the key only for an effective press.
        //
        // IT IS CARET MOVEMENT AND NOTHING ELSE. The caret parks past the line's last cell, which
        // is the SAME parked state a boundedRush-refused roll leaves behind (see
        // rollForwardIfFinishedEarly), and from there the machinery that already exists carries the
        // player onward: the roll below hands them the next line at once when its entry window is
        // open, and snapForwardOnLineStart performs the deferred hand over when it opens later. The
        // cells left behind are NOT judged here. They stay untyped and become misses in the seal
        // loop, all at once, with the line's one combo break, at the line's own deadline: precisely
        // what would have happened to a player who stopped typing and sat there. So no judged
        // quantity changes value or timing against the un-skipped run, and the abandoned line's
        // drag grace is held for it by lineAbandoned, which is the one piece of state that claim
        // depends on.
        //
        // Deliberately NOT the word skip's shape (skipCurrentWord): that one puts cells into the
        // phantom state, takes an immediate combo break and snapshots a redeemable claim, all of
        // which move the account at press time. A line skip gives up more and costs nothing extra
        // for it, because the player pays the same misses either way, just without having to sit
        // through them.
        //
        // NO-OP when there is nothing to skip: no active line, the run finished, or the caret
        // already past the last cell (a line typed out, or one already skipped). In particular
        // Enter on a COMPLETE line does NOT perform the roll the next keypress would UNDER THE
        // AUTOMATIC hand-over: the two time-driven arms already own that caret, so a second way in
        // could only duplicate them. Under manualNewlines (the browser's arm) there is no
        // time-driven arm left to duplicate and Enter IS the newline, so on a complete line it does
        // the same hand-over a space does (rollForwardManually).
        //
        // The WPM clock needs nothing here and is deliberately NOT armed: an Enter is not typing.
        // Accrual stops by itself the moment the caret parks (update accrues only while the active
        // line is INCOMPLETE, and a parked caret's line reads complete), and on the line the player
        // lands on clockRunsFrom answers exactly as it does after a refused rush: stopped until the
        // first press there arms it, because a stale arm belongs to a line index the caret has
        // left.
        //
        // The guard is the C# one verbatim, with no `failed` arm even though processKey has one:
        // that flag is the health account's (the desktop freezes a failed play outside the engine,
        // in Player), and a skip moves nothing judged, so refusing it there would buy nothing and
        // would be a rule the C# does not have.
        processEnter(time) {
            if (this.finished || this.activeLineIndex < 0) return false;

            // PINNED CARET: Enter is not a gameplay key at all, so the press does nothing here and
            // falls through to whatever the client binds it to. The skip below is CARET MOVEMENT,
            // and it is worth something only because the unpinned caret may carry the player on
            // early: with the caret pinned to the song there is nothing to move them to, and the
            // press would give the rest of the line up for NOTHING. The browser is permanently
            // unpinned (fletcherEnabled is set in the constructor and nothing turns it off), so this
            // is a mirror of the C# guard rather than an arm a /play run can reach; the harnesses
            // that build a pinned engine by hand do reach it.
            if (!this.fletcherEnabled) return false;

            // A line handed to the player before its window opens waits rather than judging: the
            // press is swallowed, exactly as a keypress on it is (see awaitingEntryAt).
            if (this.awaitingEntryAt(time)) return false;

            const line = this.lines[this.activeLineIndex];

            // Hop auto-skip cells before measuring, exactly as processKey does, so "the caret is at
            // the end" is asked of the same frontier a keypress would see.
            this.autoSkipForward();

            // Nothing left to give up: parked already, or the line is fully typed. That second
            // state is the manual newline's own, so Enter closes it here (and reports the move so
            // the caller swallows the key); under the automatic hand-over it stays inert.
            if (this.caretIndex >= line.cells.length) return this.rollForwardManually(time);

            // Only a line with something still untyped is ABANDONED. A caret walked to the end over
            // nothing but wrong cells owes no misses, so the seal has no drag to protect and the
            // flag would only hold the line open for cells that are already resolved.
            if (!this.noTypeableUntyped(line)) this.lineAbandoned[this.activeLineIndex] = true;

            this.caretIndex = line.cells.length;

            // The same call the last character of a line makes, so an Enter inside the next line's
            // entry window rolls on at once and one outside it parks, with no second rule. Under
            // manualNewlines that roll is the PLAYER'S own, so the skip hands the line over the way
            // the newline key does: one Enter still means "I am done with this line, move me on".
            if (!this.rollForwardManually(time)) this.rollForwardIfFinishedEarly(time);

            return true;
        }

        processKey(c, time) {
            if (this.finished || this.failed) return false;

            // THE MAP'S FIRST LINE OPENS EARLY (FIRST_LINE_LEAD_MS, mirrors the head of
            // TypingEngine.ProcessKey). There is no previous line to rush from, so the PRESS is what
            // opens it: the same hand-over update's activation arm performs, made on demand. The
            // clock alone still leaves the line alone.
            if (this.activeLineIndex < 0 && this.firstLineTypingOpensAt(time)) {
                this.activeLineIndex = 0;
                this.caretIndex = 0;
                this.autoSkipForward();
            }

            if (this.activeLineIndex < 0) return false; // dead zone / pre-roll: harmless

            // WAITING FOR THE WINDOW (see awaitingEntryAt): the player was handed this line early,
            // so nothing here is judged, not the character and not a typo, until the window opens.
            if (this.awaitingEntryAt(time)) return false;

            let line = this.lines[this.activeLineIndex];
            this.autoSkipForward();

            if (this.caretIndex >= line.cells.length) {
                // MANUAL NEWLINES: on a finished line the SPACEBAR is the newline, so it hands the
                // caret on instead of being inert.
                if (c === ' ') return this.rollForwardManually(time);

                // OR BY TYPING (newlineOnTypedLetter): ANY letter hands the caret on and then lands
                // on the next line's first slot, right or wrong. It moves on the space's own terms,
                // with NO window gate on the press, and the window then refuses the CHARACTER
                // exactly as it refuses every other press on a line that has not opened yet: the
                // move is reported (true) and the player types the letter again when it does.
                if (this.newlineOnTypedLetter && this.manualNewlines && this.fletcherEnabled
                    && this.activeLineIndex + 1 < this.lines.length
                    && this.rollForwardManually(time)) {
                    line = this.lines[this.activeLineIndex];

                    if (this.awaitingEntryAt(time)) return true;

                    // A landed line with nothing typeable on it (every cell auto-skipped) leaves the
                    // caret past its end with no slot for the letter: the move is kept and the letter
                    // goes nowhere, the answer the awaiting branch above already gives. The C# takes
                    // the same answer since backlog 326 (it used to read Cells[caretIndex] and throw),
                    // and KeyHandlerOrderLiveParityTest's cell-less section holds the two together.
                    if (this.caretIndex >= line.cells.length) return true;
                } else {
                    // Every other key stays inert here: the parked dead zone.
                    return false;
                }
            }

            // The press is going to do something, so the player is typing on this line; if the song
            // has not reached it yet, that is the instant the WPM clock starts counting (backlog
            // 222, see clockRunsFrom). Placed here, above every branch below, so it is one site and
            // no path can quietly skip it, and attributed to the line the press LANDS on: a press
            // that finishes this line and rolls the caret onward has typed here, not there.
            this.armWpmClockAheadOfTheCue(time);

            let cell = line.cells[this.caretIndex];

            // Backlog 243: set when this press is a skip that left a claim outstanding, so the combo
            // the SAME press goes on to earn on the word gap is recorded as the claim's OWN credit
            // rather than as a run the player built after the break. Read once, at the one arm that
            // increments the combo.
            let skipLeftAClaimOutstanding = false;


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
            //
            // Since PR 3 (the second input era) the skip also needs wrong input allowed: the C# gate
            // is `SpaceSkipsWord && (AllowWrongInput || !InputEra2)`, which collapses to this with
            // the era permanently on. Under Gatekeeper the press falls through to the strict
            // rejection below, like any other wrong key. /play is never on Gatekeeper
            // (allowWrongInput is always true here), so the term never refuses a live browser skip;
            // it is carried so the gate reads the same as the C# one.
            if (this.spaceSkipsWord && this.allowWrongInput && c === ' ' && cell.expected !== ' ') {
                skipLeftAClaimOutstanding = this.skipCurrentWord();

                if (this.caretIndex >= line.cells.length) {
                    // The abandoned word ran to the end of the line, so there is no word gap for
                    // the space to land on. The line is complete, exactly as it would be had the
                    // player typed that last word out, and the same end-of-line handling applies.
                    this.rollForwardIfFinishedEarly(time);
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

                this.rollForwardIfFinishedEarly(time);
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
            // rather than mark it (see spaceMayLand below). That is the skip-OFF arm, which /play
            // never plays: spaceSkipsWord is on there (backlog 198), so the skip gate above has
            // already taken a space on a freestyle slot as a word skip before it could get here.
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
                // arrives here: the skip gate above consumed it (except under Gatekeeper, where since
                // PR 3 it falls through to the rejection below). /play is always on that arm
                // (backlog 198), so spaceMayLand is false for every live browser press and this
                // clause is the skip-OFF arm, kept for the mirror. A FREESTYLE slot is the one cell
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

                    const brokenPositions = this.breakRun();

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

                    this.snapshotRedeemableBreak(wrongCellIndex, brokenStreak, brokenPositions);

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
                    // takes the desktop's shipped default and runs with the setting ON (backlog 198),
                    // so the park is LIVE on every /play run: a wrong letter on a word gap parks
                    // here, and the fuzz sweep counts those presses as parkedGapTypos.
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
                    // Neither of the two older renderer hooks fires here, and both omissions mirror
                    // the desktop. onWrongKey is the REJECTED-key feedback (the char popping off the
                    // caret, mirroring LyricStage.onWrongKeyRejected), which the desktop does not
                    // play for a char it accepted into a cell. onCharJudged is this renderer's
                    // rolling-WPM tap, and the C# pushRollingSample() sits on the ACCEPTED path only,
                    // below the branch we are in, so logging a wrong char there would drift the
                    // browser's WPM readout away from the desktop's. The typo's OWN feedback is
                    // onTypoLanded, raised below.
                    //
                    // HEALTH is charged here, at the keypress (backlog 166: TypeBeatHealthFeed on
                    // CharJudged(WrongChar) -> ApplyTypoDrain), and refunded if the character is
                    // erased; the unfixed typo the seal resolves later is HP-inert. It cannot hang
                    // off onCharJudged, which this branch never raises (see above). The C# raises that
                    // judgement for EVERY typed-through wrong key, a second letter overwriting a
                    // PARKED gap typo included, so the drain is taken every time too, and the erase
                    // later refunds one.
                    this.healthAccount.applyDeferredDrain(1);
                    // THE TYPO'S DISPLAY FEEDBACK (backlog 316): the other two readers of the C#'s
                    // CharJudged(WrongChar), which LyricStage.onCharJudged routes to the cell's
                    // 2 px shake, the gap dot's pulse (LyricLineDisplay.PlayJudgementFeedback) and
                    // the caret blink reset (Caret.NotifyTyped). Raised once per typed-through key,
                    // at the instant the C# raises that judgement: after the break and the drain
                    // above, before the roll below, with the cell and line the key landed on.
                    // DISPLAY ONLY: it reads nothing back into the engine, and it is a hook of its
                    // own rather than onCharJudged precisely so the rolling WPM stays accepted-only.
                    if (this.onTypoLanded) this.onTypoLanded(wrongCellIndex, this.activeLineIndex);
                    //
                    // A typo on the line's LAST cell finishes it exactly as a correct press would
                    // (the character is finished, it is simply wrong), so this path rolls the caret
                    // forward too, at the same seam the C# does it.
                    this.rollForwardIfFinishedEarly(time);
                    return true;
                }

                // GATEKEEPER (strict). Wrong key REJECTED: costs a keypress + combo + streak; caret
                // unmoved. Unreachable from the browser for a letter, and since backlog 184 for a
                // mid-word space too: the one case the default path still refuses above is the space
                // KEY pressed on a FREESTYLE slot, and only on the skip-OFF arm. /play runs skip-ON
                // (backlog 198), where the skip gate takes that space first, so no live /play press
                // reaches this branch at all; it and its mash fail stay for the mirror.
                this.totalKeypresses++;
                this.errorCount++;
                this.consecutiveWrongKeys++;
                this.breakRun();
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
                // TypeBeatPlayfield.onWrongKeyRejected -> TypeBeatHealthFeed -> ApplyWrongKeyStreak:
                // 1/13 of the bar, and the mash fail at WRONG_KEY_FAIL_STREAK. The fail is the
                // account's (a No Fail veto can refuse it), where this branch used to set it itself.
                this.healthAccount.applyWrongKeyStreak(this.consecutiveWrongKeys);
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
            // reader agrees with the judgement: classify(0) is 'Great', and the inert retype
            // re-classifies the stored firstCorrectDelta consistently. (Before backlog 251 removed
            // the browser's sync tint and live sync readout, typebeat-player.js also read this
            // zeroed delta back for both, which would otherwise still have docked a space for its
            // timing; the delta itself is unchanged, kept for WireCompat parity with the C#, only
            // that display consumer is gone.)
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
            // samples), so there is no omission to fix, only an asymmetry to expect. Before backlog
            // 251, the other consumer of the zeroed delta WAS mirrored: typebeat-player.js read
            // judgedDelta back for the cell tint and its live sync percent, excluding space cells
            // from both halves of that mean the way LiveSyncPercent does (and a cell left WRONG was
            // out of it too, on both sides and in every state, since that readout filtered on the
            // CELL, typeable and not a space, never on what happened to it). That readout and tint
            // are gone from the browser now (the desktop metric they mirrored is off by default
            // too), but the zeroing stays: it is engine data, kept for WireCompat parity with the
            // C#, not for a display consumer here.

            // COMBO RESTORE (backlog 140, widened to the word skip by backlog 167), before anything
            // about this press is judged: if this is the cell a wrong keypress spoiled or a skip
            // abandoned, the run resumes at the streak that break cost plus everything earned since.
            // Placed here so the press below is
            // scored at the RESUMED streak, which is what makes fixing a typo worth score rather
            // than only accuracy: the combo portion weights every judgement by the combo AFTER it.
            // Not scoring-inert even for an inert retype, because the streak belongs to the FIX and
            // not to the cell's judgement.
            this.resumeStreakIfThisRedeemsTheBreak(this.caretIndex);

            const w = WINDOWS;
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
                // decision and cannot say different things. The delta itself is untouched: it
                // stays the press the player actually made, for accuracy and WireCompat parity
                // with the C# engine (before backlog 251, this was also what the browser's sync
                // tint and live sync percent saw; that display is gone, the delta is not).
                type = awardedTier(classify(delta, w), cell.heldWrongBeforeJudged);

                // NO RUSH CAP (PR 3): the C# line is `FletcherEnabled && !RushCapExempt && !InputEra2
                // && rushesPastCap(...)`, which is always false in the second input era, so nothing
                // stands between the clock's tier and the point ladder however far past the playhead
                // the caret is. Backlog 347's Meh award and the pre-347 combo break are both stored
                // eras only.
                const bp = basePoints(type);

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
                // Nothing stands between the press and the increment: the RUSH CAP (which the
                // browser gained with the flexible default, backlog 208) used to be a combo penalty
                // here, a break once per excursion, then a Meh award (backlog 347), and is gone since
                // PR 3. The C# keeps both as stored eras; a browser play is always live.
                //
                // The cell the press LANDED on, which is where this increment is recorded in the
                // ledger (creditCombo): the caret has not moved yet (it rolls on below), and a word
                // skip has already re-pointed it at the gap the space is judged on.
                this.creditCombo(this.caretIndex);

                // The one press that can credit combo it also broke (backlog 243): the space that
                // skipped the word, now being judged on the gap the skip parked the caret on. The
                // combo is real and stands, but it belongs to the break, so the claim remembers it
                // and the next break has to beat it to take the claim away.
                if (skipLeftAClaimOutstanding) this.creditTheClaimsOwnPress();

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

            // TypeBeatPlayfield.onCharJudged's flexible-caret arm. It existed for the RUSH CAP's
            // combo break on a press still judged Great/Ok/Meh, which the hit result alone (it
            // INCREMENTS osu's combo) could not carry. Since backlog 347 the live rule broke nothing
            // there (the over-cap press was a Meh that credited combo), and since PR 3 there is no
            // cap at all, so on a scoring press the engine's combo is never zero here and this
            // carries nothing; the C# keeps the same seam for its pre-347 era. Kept rather than deleted because it is written against "the combo
            // this press left behind" (the C# judgement.ComboAfter), which covers the inert-retype
            // branch too, and the C# announces that through the very same raise.
            //
            // Under a pinned caret this is inert, which is why the C# gates it on the flag: there
            // every combo-zero judgement either maps to a Miss (which breaks osu's combo itself) or
            // is a WrongChar, whose break the mistype path has already carried.
            if (this.fletcherEnabled && this.combo === 0) this.processor.breakCombo();

            if (this.onCharJudged) this.onCharJudged(judgedIndex, type, points);
            this.rollForwardIfFinishedEarly(time);
            return true;
        }

        // TypingEngine.stepBackIntoLine. HAND BACK INTO A LINE the player was moved off: the undo for
        // a mid-line Enter that gave the rest of the line up (processEnter), and just as much the way
        // back to a line the SONG has handed them on from. The caret lands at the LAST CHARACTER THEY
        // ACTUALLY TYPED, just after the last cell they put something into, and everything that was
        // given up is handed back LIVE rather than left behind the caret to be missed at the seal.
        //
        // THE END OF THE LINE IS THE WRONG PLACE FOR IT: parked past the last cell the line reads
        // COMPLETE (isLineComplete), keypresses there are inert, the characters the player came back
        // for are unreachable, and the misses the return was meant to erase are the very ones it
        // guarantees. On the frontier they are all in front of the caret again, where typing them is
        // what erases them, the same "come back and type it" account the word skip's reclaim keeps.
        //
        // WHAT THE WALK BACK STOPS ON: a cell the player TYPED (correct or wrong) and a cell already
        // resolved as 'missed' both end it, so the caret lands after the last thing the line has an
        // answer for. The C#'s note about a Missed cell in that tail is about a stored run re-derived
        // under the pre-167 immediate-miss era, which the browser can never be in; the arm is
        // mirrored anyway, because it is the same walk and a state-keyed rule is not an era rule.
        //
        // The line's own abandonment goes with the skip (lineAbandoned): the flag exists to hold the
        // line open, past its deadline, for the misses the player walked away from, and there is
        // nothing left to hold it open for once they have walked back. An ABANDONED word caught in
        // the tail is re-opened the way processBackspace's own walk re-opens one, and the C# raises
        // AbandonReclaimed for it, which carries the HEALTH refund of what the skip drained (landed
        // on the account below, after the caret has moved, where the C# raises it). Its combo still
        // comes back at the retype, which is what the claim left in place is for.
        stepBackIntoLine(index) {
            const cells = this.lines[index].cells;

            let frontier = 0;

            for (let i = cells.length - 1; i >= 0; i--) {
                const state = cells[i].state;

                if (state === 'correct' || state === 'wrong' || state === 'missed') {
                    frontier = i + 1;
                    break;
                }
            }

            let reclaimed = 0;

            for (let i = frontier; i < cells.length; i++) {
                if (cells[i].state === 'abandoned') reclaimed++;

                if (cells[i].state === 'abandoned' || cells[i].state === 'autoskip') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            this.lineAbandoned[index] = false;

            this.activeLineIndex = index;
            this.caretIndex = frontier;
            this.autoSkipForward();

            // AbandonReclaimed -> TypeBeatHealthFeed -> RefundAbandonDrain.
            this.healthAccount.refundDeferredDrain(reclaimed);
        }

        // TypingEngine.ProcessBackspace. Erase the most recent typed cell within the active line,
        // stepping back transparently over auto-skipped punctuation (which is un-skipped so retyping
        // re-marks it) and, since backlog 167, over the ABANDONED cells of a skipped word (which go
        // back to 'untyped' for the same reason: retyping them re-earns them). Returns false when
        // there was nothing to do. The erased keypress stays in the accuracy counts.
        //
        // Since PR 3 (the second input era, TypingEngine.InputEra2) a word skip and the space it
        // typed on the following gap are undone TOGETHER by one press (tryUndoWordSkip): the
        // abandoned cells are re-opened and the caret returns to the FIRST of them, leaving every
        // correctly typed character of the word intact, and the same holds at the end of a line,
        // where a skipped final word has no following gap. The C# keeps the era before it (one
        // press takes the typed gap, the next steps transparently over the abandoned run onto the
        // last character actually typed and erases it) for the stored runs it re-derives; the
        // browser plays live only, so it takes the new rule unconditionally. The transparent walk
        // below still runs for everything the undo does not claim.
        //
        // The one case that does not erase BEHIND the caret is a typo the caret is parked ON, which
        // only the word-gap park can produce (backlog 184): that cell is cleared in place and the
        // caret does not move, because the gap it sits on is still owed its space.
        //
        // Scan first, mutate after, exactly as the C# does, because the two have to be told apart
        // before anything moves: a press with nothing typed behind it did SOMETHING if it reclaimed
        // a word, and nothing at all if it did not.
        // TypingEngine.CaretOnParkedTypo (PR 2): true when the caret sits on a PARKED typo, the one
        // cell state a backspace clears IN PLACE (see processBackspace below), reporting a mutation
        // without moving the caret. The player's erase runs (eraseBackTo in typebeat-player.js)
        // read it so an in-place clear is not mistaken for the end of a selection: a retype
        // selection opened over a word skip ends on exactly such a cell whenever the player has
        // since typed into the gap, and stopping there left the word it was opened for standing.
        get caretOnParkedTypo() {
            if (this.finished || this.activeLineIndex < 0) return false;

            const cells = this.lines[this.activeLineIndex].cells;

            return this.caretIndex < cells.length && cells[this.caretIndex].state === 'wrong';
        }

        processBackspace() {
            if (this.finished || this.activeLineIndex < 0) return false;

            // THE HEAD OF A LINE THE PLAYER ARRIVED ON: a backspace there steps BACK UP to the line
            // it came from. That is the way back to a line the SONG has just handed them on from, so
            // a typo noticed a beat too late is still the player's to fix, and equally the undo for a
            // mid-line Enter that gave the rest of a line up. It works for exactly as long as that
            // line is still theirs, whoever moved them: until the engine TAKES it, which is the seal
            // (sealPermitted, the instant the push warning's red bar has counted down to) and not the
            // vocals running out or the next cue arriving. Once the line is sealed the press is
            // inert, just as it is on any other line's head.
            //
            // AND IT LANDS ON WHAT THE PLAYER LAST TYPED rather than at the end of the line (see
            // stepBackIntoLine): the line it comes back to is one the player may have given the rest
            // of up to a mid-line Enter, and the characters that press gave up have to be in front of
            // the caret again for coming back to mean anything.
            //
            // NO ERA BIT. The C# gates it on FletcherEnabled alone, which the browser is permanently
            // in, so this is live rule and reachable on an ordinary /play run: a flexible caret that
            // finished a line early, or was moved on by the line-start snap, is one backspace from
            // the line behind it.
            if (this.caretIndex === 0 && this.activeLineIndex > 0 && this.fletcherEnabled) {
                const previous = this.activeLineIndex - 1;

                if (previous >= this.nextSealIndex) {
                    this.stepBackIntoLine(previous);
                    return true;
                }

                return false;
            }

            const cells = this.lines[this.activeLineIndex].cells;

            // A typo the caret is PARKED ON (backlog 184): cleared where it sits, not erased from
            // behind. The gap is still owed its space, so the caret has no business retreating into
            // the perfectly good word in front of it, and the character the player wants back is the
            // one they are looking at. One press, one cell, caret unmoved.
            //
            // Keyed on the STATE rather than on an era flag, exactly as the C# keys it: only the park
            // ever leaves the caret sitting on a wrong cell, because everywhere else resolving a cell
            // is how the caret got past it. The C# raises TypoErased here, which carries the HEALTH
            // refund of the drain the typo took (TypeBeatHealthFeed -> RefundTypoDrain): ONE drain
            // back, however many letters the park took, since the C# drained on each of them.
            if (this.caretIndex < cells.length && cells[this.caretIndex].state === 'wrong') {
                const parked = cells[this.caretIndex];

                parked.state = 'untyped';
                parked.typedChar = null;
                parked.judgedDelta = null;
                parked.judgeType = null;
                // firstCorrectDelta intentionally retained, as on the erase below.
                this.healthAccount.refundDeferredDrain(1);
                return true;
            }

            // THE SECOND INPUT ERA's undo of a word skip (TypingEngine.ProcessBackspace:
            // `if (InputEra2 && tryUndoWordSkip(cells)) return true;`), in the same place: after the
            // parked typo, before the transparent walk.
            if (this.tryUndoWordSkip(cells)) return true;

            let target = this.caretIndex - 1;
            while (target >= 0 && (cells[target].state === 'autoskip' || cells[target].state === 'abandoned')) target--;

            let reclaimed = 0;
            for (let i = target + 1; i < this.caretIndex; i++) {
                if (cells[i].state === 'abandoned') reclaimed++;
            }

            if (target < 0 && reclaimed === 0) return false;

            // Un-skip the punctuation and re-open the abandoned cells we stepped back over. The C#
            // announces the reclaimed ones on AbandonReclaimed, which carries HEALTH alone (the
            // refund of what the skip drained, landed on the account below at the C#'s raise sites).
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
                this.healthAccount.refundDeferredDrain(reclaimed);
                return true;
            }

            const cell = cells[target];

            // Read BEFORE the cell is cleared, as the C# reads it: a wrong character is being taken
            // back, which is the erase TypoErased (and so the health refund) is raised for.
            const erasedTypo = cell.state === 'wrong';

            cell.state = 'untyped';
            cell.typedChar = null;
            cell.judgedDelta = null;
            cell.judgeType = null;
            // firstCorrectDelta intentionally retained (inert-retype guard).
            this.caretIndex = target;

            // The C#'s raise order, reclaim first and then the typo, which the clamp at full makes
            // observable in the last bit of the bar.
            this.healthAccount.refundDeferredDrain(reclaimed);
            if (erasedTypo) this.healthAccount.refundDeferredDrain(1);
            return true;
        }

        // TypingEngine.CanUndoWordSkip (PR 3): whether one backspace can undo the word skip
        // adjacent to the caret. The C# answers false outside InputEra2; the browser is always in
        // it.
        get canUndoWordSkip() {
            return this.adjacentSkippedWord() !== null;
        }

        // TypingEngine.adjacentSkippedWord (PR 3). The skipped word a backspace here would undo, as
        // { firstAbandoned, wordEnd, gapIndex }, or null. The word is the one ENDING at the caret:
        // the caret sits ON the gap that closes it (wordEnd = caret, no gap to erase), or just PAST
        // that gap (the gap is behind the caret and is the one the skip typed), or at the end of the
        // line (a skipped final word, no gap). It counts only if a cell of it is still abandoned,
        // and firstAbandoned is the earliest such cell.
        adjacentSkippedWord() {
            if (this.finished || this.activeLineIndex < 0) return null;

            const cells = this.lines[this.activeLineIndex].cells;
            let wordEnd;
            let gapIndex = -1;

            if (this.caretIndex < cells.length && isWordGap(cells[this.caretIndex])) wordEnd = this.caretIndex;
            else if (this.caretIndex > 0 && isWordGap(cells[this.caretIndex - 1])) wordEnd = gapIndex = this.caretIndex - 1;
            else if (this.caretIndex === cells.length) wordEnd = this.caretIndex;
            else return null;

            let wordStart = wordEnd;

            while (wordStart > 0 && !isWordGap(cells[wordStart - 1])) wordStart--;

            for (let i = wordStart; i < wordEnd; i++) {
                if (cells[i].state === 'abandoned') return { firstAbandoned: i, wordEnd: wordEnd, gapIndex: gapIndex };
            }

            return null;
        }

        // TypingEngine.tryUndoWordSkip (PR 3). Re-open every abandoned (and auto-skipped) cell of the
        // adjacent skipped word from its first abandoned cell on, erase the space the skip typed onto
        // the following gap when that gap is CORRECT (a skip can also step over an already spoiled
        // gap, whose typo this space did not type and which keeps it for its own backspace), and park
        // the caret on the first abandoned cell. The C# raises AbandonReclaimed with the re-opened
        // cells, which carries HEALTH alone (the refund of what the skip drained), landed here as the
        // old walk lands it. firstCorrectDelta is left on the gap, as on every erase, so a retype of
        // it is inert.
        tryUndoWordSkip(cells) {
            const skip = this.adjacentSkippedWord();

            if (skip === null) return false;

            let reclaimed = 0;

            for (let i = skip.firstAbandoned; i < skip.wordEnd; i++) {
                if (cells[i].state === 'abandoned') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                    reclaimed++;
                } else if (cells[i].state === 'autoskip') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            if (skip.gapIndex >= 0) {
                const gap = cells[skip.gapIndex];

                if (gap.state === 'correct') {
                    gap.state = 'untyped';
                    gap.typedChar = null;
                    gap.judgedDelta = null;
                    gap.judgeType = null;
                }
            }

            this.caretIndex = skip.firstAbandoned;
            this.healthAccount.refundDeferredDrain(reclaimed);
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
        // auto-skipped cells (and, in the era before PR 3, over abandoned ones too), so a press can
        // land the caret further back than this, exactly as a plain backspace there would. Since
        // PR 3 undoing a word skip stops at the first abandoned cell, keeping the typed cells before
        // it.
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
        // holding the EARLIEST unfixed mistake behind the caret, or -1 when there is none at all (the
        // gesture is then a no-op). The selection itself is the half-open range [this, caretIndex),
        // and it is pure UI state: nothing in the engine knows it exists. Consuming it is composed,
        // like the gesture above, out of ordinary processBackspace calls back to this index plus at
        // most one processKey (see typebeat-player.js).
        //
        // An unfixed mistake is a cell in the 'wrong' state OR the 'abandoned' state (backlog 244): a
        // wrong character typed through and not yet backspaced away, or a cell a word skip gave up on
        // and nobody has come back for. Both are scanned in one pass, taking the EARLIEST cell in
        // EITHER state, so the selection covers every unfixed mistake behind the caret rather than
        // only the most recent (backlog 184) and a still-open typo wins over a later abandoned word,
        // or the reverse, purely on which one sits earlier on the line. The gesture is "fix what's
        // behind me", and it is one keystroke: offering the shortest retype would leave a player with
        // two spoiled words pressing it, retyping, pressing it again, and having no way to see from
        // the caret how many rounds are left. Retyping the cells in between costs nothing, since a
        // correct cell re-typed is scoring-inert.
        //
        // WHICH run the mistake's cell opens: for an ordinary lyric character (a typo or an abandoned
        // cell alike) it is its WORD's first cell, walking back to the gap before it. Since PR 3 (the
        // second input era, TypingEngine.InputEra2) three refinements ride on that, all live here
        // because the browser is always in the era: a WORD GAP holding a typo (possible since
        // backlog 181) selects from the beginning of the word BEFORE the gap, so the retype includes
        // its space; a word given up whole anchors on its own head, with no backlog 260 widening onto
        // the gap in front of it, because the era's backspace (tryUndoWordSkip) stops there; and
        // leading punctuation the word auto-skips is stepped over, since the first TYPEABLE cell is
        // the earliest place a backspace can stop without crossing the gap before the word. The C#
        // keeps the old anchor (the gap itself, and the widening) as legacyRetypeSelectionAnchor for
        // an engine without the era, so the anchor always agrees with the backspace it composes.
        //
        // The answer is never equal to caretIndex when it is non-negative: the scan is over
        // [0, caretIndex), so a selection always covers at least one cell. The one typo that can sit
        // AT the caret, the gap a park is holding (backlog 184), is deliberately outside that range:
        // it needs no selection, being one backspace away under the same rule that parked it.
        get retypeSelectionAnchor() {
            if (this.finished || this.activeLineIndex < 0) return -1;

            const cells = this.lines[this.activeLineIndex].cells;
            const limit = Math.min(this.caretIndex, cells.length);
            let mistake = -1;

            for (let i = 0; i < limit; i++) {
                // The two unfixed states, taken in one pass so the earliest of EITHER kind wins
                // (backlog 244 added the abandoned one).
                if (cells[i].state === 'wrong' || cells[i].state === 'abandoned') { mistake = i; break; }
            }

            if (mistake < 0) return -1;

            let anchor = mistake;

            // A typo on a space belongs to the word immediately before it for retyping. Step over
            // adjacent gaps first so even unusual repeated spaces find that word.
            if (isWordGap(cells[mistake])) {
                while (anchor > 0 && isWordGap(cells[anchor - 1])) anchor--;
            }

            while (anchor > 0 && !isWordGap(cells[anchor - 1])) anchor--;

            // A word can begin with punctuation that typing auto-skips. The first typeable cell is
            // the earliest place a backspace can stop without crossing the prior gap.
            while (anchor < mistake && !cells[anchor].typeable) anchor++;

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

    // The all-Great combo portion over the typeable cells, the one term of computeScore that
    // depends on nothing the play does. The live HUD (backlog 319) calls computeScore every frame,
    // so the loop is run once per engine and remembered here, OUTSIDE the engine: a WeakMap the
    // engine never sees, keyed on the engine and checked against the lines array it walked, so a
    // cache can never answer for cells it did not count. Which cells are typeable is fixed when the
    // beatmap is built, and play only ever writes a cell's state, never its typeable flag.
    // uncachedMaxComboPortion is the loop itself, exported so a harness can hold the two equal.
    const maxComboPortionCache = new WeakMap();

    function uncachedMaxComboPortion(lines) {
        let maxComboCounter = 0, maxComboPortion = 0;
        for (const line of lines) {
            for (const cell of line.cells) {
                if (!cell.typeable) continue;
                maxComboCounter++;
                maxComboPortion += MAX_RESULT_BASE_SCORE * Math.pow(maxComboCounter, COMBO_EXPONENT);
            }
        }
        return maxComboPortion;
    }

    function maxComboPortionOf(engine) {
        const hit = maxComboPortionCache.get(engine);
        if (hit && hit.lines === engine.lines) return hit.value;
        const value = uncachedMaxComboPortion(engine.lines);
        maxComboPortionCache.set(engine, { lines: engine.lines, value: value });
        return value;
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
        // rest), so the simulated combo runs 1..N over exactly those cells. Memoised per engine
        // (maxComboPortionOf), since the live HUD asks every frame.
        const maxComboPortion = maxComboPortionOf(engine);

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
            // The JUDGED-only accuracy the total above is built on (ScoreProcessor.Accuracy), for
            // the results card to show on a FAILED run (backlog 320), where it is what the desktop
            // attaches and the server stores. Display only: it is never submitted, and `accuracy`
            // above is untouched. Equal to it on a completed play.
            accuracyJudged: accJudged,
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
        isTypeable, isFreestyle, isCell, isPunctuation, normalize, spellSpecialLetters,
        // Exported since backlog 255 took it out of normalize: it is the pre-v2 half of the format
        // version gate, and the fidelity harness holds it against the C# copy on its own.
        stripBackingVocals,
        defaultChar, projectDefault, toDefaultStream,
        parseLyricOsu, parseFormatVersion, buildBeatmap, syllableCharTarget,
        // The map's track gain (PR 1): the TryParse-mirroring read and the in-place scale and clamp
        // the player runs on the decoded audio, exported so the harnesses can pin both.
        parseAudioGain, applyTrackGain,
        // The map's freestyle colour (backlog 384): its strict read and the default it falls back to.
        parseFreestyleColour, DEFAULT_FREESTYLE_COLOUR,
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
        WINDOWS, classify, toHitResult,
        freestyleTick, freestyleGlyph,
        constants: {
            CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, FREESTYLE_MARKER,
            SHIMMER_INTERVAL_MS, PUNCTUATION, SPECIAL_LETTERS, WORD_BREAK, STRETCH_RUN_LENGTH,
            // The format version gate (backlog 255), exported so the harnesses pin the same numbers
            // the C# decoder carries rather than transcribing them.
            FORMAT_MAGIC, FALLBACK_FORMAT_VERSION, LITERAL_BRACKETS_FROM_VERSION,
            DEFAULT_AUDIO_GAIN, MAX_AUDIO_GAIN,
            // The flexible caret's two tuning points (backlog 208), exported so the harnesses pin
            // the same numbers the game's own FletcherEngineTest does rather than transcribing them.
            FLETCHER_DRAG_GRACE_MS,
            // The first line's head start (PR 2), exported for the same reason.
            FIRST_LINE_LEAD_MS,
            // The HP pool (backlog 306), exported so the cross-repo parity test holds each one
            // against the TypeBeatHealthProcessor constant it mirrors, and the empty test's
            // epsilon against osu-framework's own Precision.DOUBLE_EPSILON.
            GREAT_HEALTH_INCREASE, OK_HEALTH_INCREASE, MEH_HEALTH_INCREASE, MISS_HEALTH_DRAIN,
            WRONG_KEY_HP_DRAIN, HEALTH_EPSILON
        },
        // Precision.AlmostBigger with its default difference, exported for the same pin.
        almostBigger,
        // PausedWord (PR 2): the authored-pause derivation, exported so the harnesses can hold it
        // against the game's own PausedWord and TypingLine.
        usableRests, pausedWordOf, tokenCellTargets,
        // computeScore's read-side memo (backlog 319), exported so a harness can hold the cached
        // answer against the loop it replaces.
        uncachedMaxComboPortion, maxComboPortionOf,
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
