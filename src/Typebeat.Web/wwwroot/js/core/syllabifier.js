/*
 * core/syllabifier.js: rule-based English syllabification of one gameplay word (mirrors
 * Gameplay/Syllabifier.cs).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

    const {
        clamp
    } = parts;

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

    Object.assign(parts, {
        isSyllabifiable, countSyllables, splitPoints
    });
    parts.loaded.push('syllabifier.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
