/*
 * core/typeability.js: text normalization, the special letters and the default typed stream
 * (mirrors Typeability in LyricBeatmap.cs).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
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
    // It does NOT strip backing vocals: that sits at the one call site that knows the map's format
    // version (buildBeatmap), so this function is the plain mirror of Typeability.Normalize and a
    // literal bracket in a v2 map survives to the cells.
    // History: docs/typebeat-core-history.md, "normalize and backing vocals".
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
            .replace(/[\u2013\u2014\u2015\u2212]/g, '-')
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

    // Math.Clamp. Declared in this file, the first one loaded, because the line build and the
    // syllabifier both use it.
    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

    Object.assign(parts, {
        isTypeable, FREESTYLE_MARKER, FREESTYLE_AUTO_CHAR, isFreestyle, PUNCTUATION, WORD_BREAK,
        isPunctuation, isCell, fold, stripBackingVocals, typeableCount, SPECIAL_LETTERS,
        spellSpecialLetters, normalize, defaultChar, projectDefault, toDefaultStream, clamp
    });
    parts.loaded.push('typeability.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
