/*
 * core/typing-line.js: builds the playable beatmap, per-line timing and the typeable cells with
 * their targets, syllable groups and stretches (mirrors TimingJsonLoader.BuildLines,
 * Gameplay/SyllableSegments.cs, Gameplay/PausedWord.cs and Gameplay/TypingLine.cs).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

    const {
        isFreestyle, isPunctuation, isCell, fold, stripBackingVocals, typeableCount, normalize,
        projectDefault, toDefaultStream, FALLBACK_FORMAT_VERSION, LITERAL_BRACKETS_FROM_VERSION,
        DEFAULT_AUDIO_GAIN, clamp, isSyllabifiable, splitPoints
    } = parts;

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

    // Beatmap-level granularity: an explicit header value wins; otherwise Word if any
    // surviving line carries words[], else Line (mirrors LyricBeatmapDecoder.finalise).
    //
    // METADATA ONLY. It is still derived, and still mirrored, because the game's decoder still
    // stamps it on every TypeBeatHitObject and the parity harnesses hold the two readings against
    // each other.
    // History: docs/typebeat-core-history.md, "Granularity".
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

    // TypingLine.interpolateUntimedTargets (PR 43): time the still-untimed chars of
    // targets[start, start + count) using only anchors INSIDE that span. A run of them between two
    // timed chars is spread evenly across the gap (mark m of a run of len takes
    // prev + (m+1)*(next-prev)/(len+1)); at the span's edges `leading` / `trailing` stand in for the
    // missing anchor, and a run with neither side attaches to the one it has, or to `fallback`.
    // The whole line is the span with no edge anchors; a paused word's stretch is a span whose
    // edges are its own sung start and end.
    function interpolateUntimedTargets(targets, start, count, fallback, leading = null, trailing = null) {
        const limit = start + count;

        for (let i = start; i < limit; i++) {
            if (targets[i] !== null) continue;

            let end = i;
            while (end + 1 < limit && targets[end + 1] === null) end++;

            const before = i > start ? targets[i - 1] : leading;
            const after = end + 1 < limit ? targets[end + 1] : trailing;
            const len = end - i + 1;

            for (let q = 0; q < len; q++) {
                targets[i + q] = before !== null
                    ? (after !== null ? before + (q + 1) * (after - before) / (len + 1) : before)
                    : (after !== null ? after : fallback);
            }

            i = end;
        }
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

            // PR 43: in a PAUSED word, the marks interpolate within the stretch on their own
            // authored side of each rest (TypingLine.build), so none is timed inside the silence.
            // Each stretch (pausedWordOf's pieces) is resolved on its own, its edges standing in for
            // a missing letter anchor; the letter targets above are untouched. No era gate on either
            // side.
            if (k < token.length && unit) {
                const paused = pausedWordOf(token, unitStart, unitEnd, unit);

                if (paused !== null) {
                    const tokenStart = pos - token.length;

                    for (const piece of paused.pieces) {
                        interpolateUntimedTargets(targets, tokenStart + piece.firstChar, piece.charCount, piece.startTime, piece.startTime, piece.endTime);
                    }
                }
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
        // Paused stretches have already resolved their marks without crossing a rest.
        interpolateUntimedTargets(targets, 0, n, 0);

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

    Object.assign(parts, {
        CUE_LEAD_MS, isAuthoredValid, derivedSplits, splitsFor, cellCuts, segmentOf, usableRests,
        pausedWordOf, tokenCellTargets, buildSyllables, STRETCH_RUN_LENGTH, buildCharTimedStretch,
        syllableIndexOf, isCharTimedStretch, syllableCharTarget, buildBeatmap
    });
    parts.loaded.push('typing-line.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
