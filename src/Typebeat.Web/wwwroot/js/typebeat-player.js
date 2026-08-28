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
    // No desktop analogue: how long a dead stretch has to be before the chip is worth drawing at
    // all. Shorter than a qualifying SKIP gap by an order of magnitude, so plenty of chips are
    // pure countdowns with no skip behind them.
    const GAP_CHIP_MIN_MS = 1800;

    // ---------------------------------------------------------------------------
    // INSTRUMENTAL GAPS: the skip rule, as a THIRD copy.
    // ---------------------------------------------------------------------------
    // CROSS-REPO INVARIANT. These four constants and computeGaps() below are a byte-for-byte port
    // of a rule that already exists twice in C#:
    //   typebeat-osu  typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs   (the authority)
    //   typebeat-web  src/Typebeat.Web/Packages/Lyrics/InstrumentalGaps.cs           (the server mirror)
    // and both CLAUDE.md mirror tables name it. The constants below were read off the server mirror,
    // not retyped from memory, and WebplayDisplayTest pins this port against that mirror's own
    // Compute() on the same lyric fixtures.
    //
    // DRIFT IS NOT COSMETIC HERE. The server stores InstrumentalGaps.SkippableSeconds on
    // beatmaps.skippable_s and PlayTimeGate SUBTRACTS it from the drain length a play has to have
    // spent. A browser skip more generous than that allowance unranks honest plays (which is what
    // backlog 47 was); one less generous just wastes the player's time. So this must agree.
    //
    // WHAT IT COMPUTES, exactly as the C# does: between two consecutive lyric lines it measures the
    // PERCEIVED instrumental (the earlier line's singEndTime to the later line's first vocal, never
    // the mechanical line boundaries, which are contiguous on decoder-built maps and carry no
    // information). A stretch of at least MIN_GAP_MS qualifies. The skip period then runs from
    // sungEnd + GAP_START_SETTLE_MS to the next line's activationTime - SKIP_LEAD_MS, and pressing
    // skip seeks to that latter time. A qualifying stretch whose usable window is shorter than
    // MIN_SKIP_WINDOW_MS is dropped rather than shown. There is no gap before the first line (that
    // is the separate INTRO skip, below) and none after the last.
    const MIN_GAP_MS = 10000;               // InstrumentalGaps.MIN_GAP_MS
    const GAP_START_SETTLE_MS = 1000;       // InstrumentalGaps.GAP_START_SETTLE_MS
    const MIN_SKIP_WINDOW_MS = 1000;        // InstrumentalGaps.MIN_SKIP_WINDOW_MS
    const SKIP_LEAD_MS = 3000;              // InstrumentalGaps.SKIP_LEAD_MS

    // SYNC TINT floor: how far along the untyped -> hit ramp the very WORST correct keypress is
    // still painted. It cannot be 0. syncQuality() returns exactly 0 at the Meh-window edges and
    // stays there beyond them, while the cell is still CellState.Correct, so an unfloored ramp
    // would paint a character the player did type in precisely the untyped colour, making it
    // indistinguishable from one they have not reached yet. That is a legibility regression, not
    // feedback. (Same argument as LyricLineDisplay.SYNC_TINT_FLOOR on the desktop.)
    //
    // The desktop's 0.35 is NOT transferable as a literal. It walks its ramp in LINEAR light
    // (osu-framework's Interpolation.ValueAt), which is a different curve from the oklab mix the
    // CSS below performs, so the same t lands somewhere else. Ported by OUTCOME instead: the
    // desktop's floor sits 47.3% of the way along its own untyped -> typed range measured in
    // oklab (dE 0.164 of a 0.346 total), and oklab is perceptually uniform, so the browser
    // reproduces that position at 0.473, rounded up to a round 0.5.
    //
    // What that buys, all figures in oklab dE / WCAG contrast against the tokens composited over
    // --bg (untyped .tb-c-todo = #898a89, Missed .tb-c-miss = #434446, hit .tb-c-hit = #c9f24d):
    //
    //   floor colour       #a9bf75   dE 0.169 vs untyped (desktop's own floor: dE 0.164)
    //                                1.71:1 vs untyped, 4.83:1 vs Missed
    //                                oklab chroma 0.101 vs untyped's 0.003
    //   ramp headroom      floor -> full hit, dE 0.160 (desktop's headroom: dE 0.182)
    //
    // so the worst correct char is separated from an untyped one by slightly MORE than the
    // desktop's shipped floor manages, along an extra axis the desktop's achromatic grey ->
    // off-white ramp cannot use at all (hue), and the ramp keeps essentially the desktop's
    // dynamic range for the actual signal. See the report note on why the untyped-vs-Missed step
    // is not usable as the yardstick here: in the browser that step is dE 0.247, which is 75% of
    // the whole untyped -> hit range and larger than the range's own WCAG ceiling (2.68:1 at
    // quality 1, against 2.83:1 for untyped-vs-Missed), so no point on this ramp can clear it.
    const SYNC_TINT_FLOOR = 0.5;

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

    /// Judgement.SyncQuality: 1 on target, decaying to 0 at the Meh window edges.
    function syncQuality(delta, w) {
        const q = 1 - (delta < 0 ? -delta / w.me : delta / w.ml);
        return q < 0 ? 0 : (q > 1 ? 1 : q);
    }

    // One pass over the map for both live readouts:
    //   completion: hits / cells seen so far (how "typed %" reads mid-play, and what rank keys off)
    //   sync:       mean sync quality x100 over resolved TIMED cells (mirrors
    //               TypingEngine.LiveSyncPercent); a cell in a SEALED line that never landed correct
    //               resolves at q = 0.
    //
    // The two denominators are deliberately different, and only the SYNC one changed in backlog 148.
    // Completion is every character of the map the player owes, word gaps included, so a space still
    // counts there and an unpressed one still misses. Sync is a timing mean, and a space is no
    // longer timed: it is judged on a zeroed delta (see typebeat-core.js), so counting it would add
    // a full quality of 1 that no press earned, lifting this readout and the grade computed from it.
    // Out of BOTH halves of the mean, so a space neither helps nor hurts.
    //
    // `typeable` is part of the same filter, mirroring the C#'s `if (!cell.IsTypeable) continue;`.
    // It was missing here before, so auto-skipped punctuation in a sealed line was resolving at
    // q = 0 in the browser and not on the desktop.
    function liveStats(engine) {
        let hit = 0, seen = 0, syncSum = 0, resolved = 0;
        const lines = engine.lines;
        for (let li = 0; li < lines.length; li++) {
            const sealed = li < engine.nextSealIndex;
            const cells = lines[li].cells;
            for (let ci = 0; ci < cells.length; ci++) {
                const c = cells[ci];
                const scored = c.state === 'correct' &&
                    (c.judgeType === 'Great' || c.judgeType === 'Ok' || c.judgeType === 'Meh');
                if (scored) { hit++; seen++; }
                else if (c.state === 'correct' || c.state === 'missed') seen++;

                const timed = c.typeable && c.expected !== ' ';
                if (timed && c.state === 'correct' && c.judgedDelta !== null) {
                    syncSum += syncQuality(c.judgedDelta, Core.windowsFor(c.tier));
                    resolved++;
                } else if (timed && sealed) {
                    resolved++;
                }
            }
        }
        return {
            completion: seen > 0 ? hit / seen : 1,
            sync: resolved === 0 ? 100 : 100 * syncSum / resolved
        };
    }

    /// Where on the untyped -> hit ramp a CORRECT character is painted, given the sync quality of
    /// the keypress that scored it (already in [0, 1], asymmetric early/late, per-cell granularity
    /// widening included): quality compressed onto [SYNC_TINT_FLOOR, 1]. Mirrors the SHAPE of the
    /// desktop's LyricLineDisplay.CorrectCharColour, not its colours: the browser re-skins gameplay
    /// onto the site's own tokens, so the endpoints stay .tb-c-todo and .tb-c-hit and the mix is
    /// left to CSS color-mix() rather than being duplicated here as literals that could drift.
    ///
    /// Deliberately driven by the SAME quality liveStats() sums into the HUD's sync readout, so the
    /// trail is a live preview of the number the play is graded on rather than a second opinion.
    /// Returned as the CSS percentage the .tb-c-hit mix consumes. Exactness at the top of the ramp
    /// is a contract: quality 1 gives '100%', which mixes to var(--violet) itself, so a perfectly
    /// timed line looks exactly as it did before this ramp existed. Out-of-range and NaN clamp.
    function syncTintFill(quality) {
        const q = !(quality > 0) ? 0 : (quality > 1 ? 1 : quality);
        if (q >= 1) return '100%';
        return ((SYNC_TINT_FLOOR + (1 - SYNC_TINT_FLOOR) * q) * 100).toFixed(2) + '%';
    }

    /// The --tb-sync-fill a cell's span should carry, or null for "no ramp, clear the property"
    /// (which falls the CSS back to its 100% default, i.e. the flat hit colour).
    ///
    /// Three exclusions, all deliberate:
    ///
    /// FREESTYLE cells take no ramp, matching the desktop exclusion and for the same reason: their
    /// colour is an IDENTITY signal ("this slot was free") that has to keep saying so for the rest
    /// of the play, not a state signal. .tb-c-free wins over .tb-c-hit by cascade order anyway, so
    /// this is belt and braces, but it keeps the intent legible at the call site.
    ///
    /// OFF-TIME cells (Premature/Lagging) keep .tb-c-off's flat warn tint. The ramp lives strictly
    /// INSIDE the .tb-c-hit bucket. The desktop has no warn tint and ramps those cells too; the
    /// browser's split is a deliberate browser-only affordance (see cellClass) and folding it into
    /// the ramp would throw it away.
    ///
    /// A correct cell with NO judged delta cannot arise from the engine, but if one ever did it
    /// falls back to the flat hit colour rather than to the dull floor, as the desktop does.
    function cellFill(cell) {
        if (cell.freestyle || cell.state !== 'correct' || cell.judgedDelta === null) return null;
        const jt = cell.judgeType;
        if (jt !== 'Great' && jt !== 'Ok' && jt !== 'Meh') return null;
        return syncTintFill(syncQuality(cell.judgedDelta, Core.windowsFor(cell.tier)));
    }

    /// The class list for a cell's span. Pure (state in, string out), and paired with cellFill():
    /// a cell's fill can only move on a frame its class moves too, since both are functions of the
    /// same state and the delta is written before the cell first repaints.
    function cellClass(cell, isCaret, popping) {
        let cls = 'tb-c';
        if (cell.state === 'correct') {
            const jt = cell.judgeType;
            // Typed but off-time (Premature/Lagging) scores nothing and pays the most accuracy a
            // judged cell can pay (backlog 199 made it a Meh rather than a Miss, so it no longer
            // breaks the run); desktop draws it like any other correct char, the browser keeps a
            // distinct warn tint as a free hint. The RENDERING is untouched by 199: the two tiers
            // are still their own judgement types, which is exactly where the distinction the
            // statistics blob gives up on survives.
            cls += (jt === 'Great' || jt === 'Ok' || jt === 'Meh') ? ' tb-c-hit' : ' tb-c-off';
        } else if (cell.state === 'wrong') {
            // Typed through wrong (the default model). The desktop shows the EXPECTED glyph in
            // error red on a LYRIC cell, not the char that was pressed, so only the colour changes
            // there; on a WORD GAP it shows the typed char instead, because a space painted red is
            // nothing at all (see cellGlyph, mirroring LyricLineDisplay.CellGlyph). Both keep
            // tb-c-wrong, so the error colour is the same one in both cases.
            //
            // The GAP takes a second, additive class (backlog 185). Its glyph is a letter standing
            // where a space was, so during a typo burst the run of them reads as solid text and the
            // word boundaries it was drawn to preserve vanish into it. Dimming that letter (the
            // opacity lives on .tb-c-wrong-gap in site.css) keeps it legible as an error while
            // letting the boundary read through again. A wrong LYRIC cell is deliberately left at
            // full strength: it is showing its OWN character, so it takes no space away, and the
            // desktop dims the same lane for the same reason.
            cls += ' tb-c-wrong';
            // Strictly expected === ' ', not "the typed char is a space": a mid-word space typo is
            // a wrong LYRIC cell showing its own letter in red, and must not be dimmed.
            if (cell.expected === ' ') cls += ' tb-c-wrong-gap';
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

    /// The GLYPH a non-freestyle cell shows, the companion of cellClass() and pure for the same
    /// reason (LyricLineDisplay.CellGlyph). Almost always the cell's own expected character: a
    /// lyric cell shows its lyric character in every state, and a WRONG one shows that character
    /// in the error red rather than the char the player pressed, so a mistyped line still reads as
    /// the line it was meant to be.
    ///
    /// The one exception is a WRONG WORD GAP (backlog 181, the cell state that could not exist
    /// before it): there the expected character is a space, and a space painted red is nothing at
    /// all, so the cell shows the TYPED character instead. That is the whole of the difference, and
    /// it is forced rather than chosen: the state has to be visible, and the gap has no glyph of
    /// its own to make visible. A gap in any other state, the typo once backspaced included, is a
    /// space again.
    ///
    /// Layout does not move with it, which is the desktop's rule reached by a different road: there
    /// the advances were measured once at load, here the lyric stack is set in JetBrains Mono (see
    /// --font-display), so the letter occupies exactly the advance the nbsp did and no cell after it
    /// moves. Re-measuring is not even reached: measureRow() runs on a line change and a resize,
    /// never on a keypress, so a reflow here would silently unregister the caret, the sweep and the
    /// cue bars from the glyphs rather than move them with it.
    function cellGlyph(cell) {
        return cell.expected === ' ' && cell.state === 'wrong' && cell.typedChar !== null
            ? cell.typedChar
            : cell.expected;
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

    // Which line carries the sung sweep and the sung caret: the line the SONG is on, which is not
    // the line the CARET is on (mirrors LyricStage.sungLineFor). With a pinned caret the engine
    // keeps the two identical and this is exactly the old behaviour. Under the flexible caret (the
    // default since backlog 208) they come apart, which is the point of it: finish a line early and
    // the caret is parked at the head of the next one while the vocal is still singing the line
    // behind, so only the typing caret follows the player. Falls back to the active line once every
    // line has sealed.
    //
    // The rule, exactly (backlog 223): the index the seal cursor WOULD have if drag protection did
    // not defer the seal. It starts at the cursor (nextUnsealedLineIndex) and walks off every line
    // the playhead has already left, stepping on endTime + sealGraceMs. That instant is the upper
    // bound of TypingEngine.songWindowOpen and the deadline canSeal uses, so while the playhead is
    // inside the first unsealed line's window the loop does not run at all and the answer is the
    // cursor's, byte for byte what shipped before. It also cannot fire on an ordinary hand-over: a
    // line with nothing left untyped seals on its own endTime and is never drag-deferred, so the
    // cursor has already moved before this could.
    //
    // Reading the cursor alone (what this did before 223) can never report the row the song has
    // moved to, because drag protection (TypingEngine.sealPermitted) deliberately holds the caret's
    // own line unsealed while the player is still typing it, and the seal loop hands the caret on
    // whenever it seals the caret's line: so the cursor is never AHEAD of the caret, and a dragging
    // player's playhead stranded at the tail of the row they were still typing while the row
    // actually being sung got no head, no sweep and no caret. The walk is pure presentation, a read
    // of line times against the gameplay clock; it writes no engine state and must never be turned
    // into one, since which line may still be typed is judgement-bearing.
    //
    // A multi-step walk is reachable, hence a while rather than an if: the seal loop stops at the
    // first line it may not seal, so while the head of the queue is drag-deferred (up to
    // FLETCHER_DRAG_GRACE_MS past its own deadline) the windows of short lines behind it can close
    // too. It never steps on to the next line's startTime: line windows overlap by design, and the
    // one that is still open is the one being sung.
    function sungLineFor(fletcherEnabled, activeLineIndex, nextUnsealedLineIndex, lines, nowMs) {
        if (!fletcherEnabled) return activeLineIndex;

        let songLine = nextUnsealedLineIndex;
        if (songLine < 0 || songLine >= lines.length) return activeLineIndex;

        while (songLine + 1 < lines.length && nowMs >= songWindowClosesAt(lines[songLine])) songLine++;

        return songLine;
    }

    // The instant the playhead leaves `line`: its hard deadline plus whatever grace its overrunning
    // vocals were given, the same sum TypingEngine.songWindowOpen ends on.
    function songWindowClosesAt(line) { return line.endTime + line.sealGraceMs; }

    // How much of a row's underline is filled: the vocal position on the row the SONG is on, and
    // nothing at all on any other row (mirrors LyricStage.setSungSweep, which feeds one display and
    // zeroes the one leaving the role). Exactly one row ever shows a fill, which used to be true
    // here by accident: the row only stopped being the sung row once the song had left it, so its
    // own time-derived fill was clamped 100% full and scrolling away. Since 223 the row moves off a
    // DRAGGING player's line while they are still reading it, and a full fill sitting there would
    // claim the vocals are still on it.
    function sweepFillFor(rowLineIndex, sungLineIndex, points, time) {
        return rowLineIndex === sungLineIndex ? sungPositionAt(points, time) : 0;
    }

    // Whether each head is drawn (mirrors LyricStage.Update's setCaretsVisible call). The two answer
    // to different facts, and backlog 223 is where they stopped sharing one boolean.
    //
    // The TYPING caret hides the moment its line is complete: there is nothing left to type on it,
    // and that absence IS the "you are done, wait for the song" signal. Deliberate and unchanged.
    //
    // The MAP PLAYHEAD is not the player's, so it must not take that term. The vocals go on being
    // sung under a finished caret, and since backlog 218 a refused roll PARKS a complete caret for
    // as long as entryOpensAt(next) is away, which blanked the playhead for seconds at a time while
    // the sweep beneath it kept moving. It hides only for its own reasons: the run is over, or its
    // row is off the visible stack (the desktop's |sungLine - active| <= 1 and its CaretStyle.None,
    // both of which arrive here as "no row is showing it").
    function caretsVisible(active, lineComplete, finished, hasSungRow) {
        const running = active && !finished;
        return { player: running && !lineComplete, sung: running && hasSungRow };
    }

    // OutQuint, the easing every desktop stage animation uses.
    function outQuint(p) { return 1 - Math.pow(1 - p, 5); }

    // ---------------------------------------------------------------------------
    // The gap rule (see the constants banner above). Pure, and exported, because the whole point
    // of a third copy is that a test can hold it against the second one.
    // ---------------------------------------------------------------------------

    // InstrumentalGaps.FirstVocalTime: when a line's vocals begin, i.e. its first typeable cell's
    // target, falling back to its own startTime when it has none.
    function firstVocalTime(line) {
        for (let i = 0; i < line.cells.length; i++) {
            if (line.cells[i].typeable) return line.cells[i].target;
        }
        return line.startTime;
    }

    // InstrumentalGaps.LastTypeableTarget: the target of the line's last typeable cell, or null.
    //
    // The C# reconstructs this from the raw token/unit layout because the server does not decode
    // words[].syllables; here the decoded cells are to hand, so it is a scan. The two agree wherever
    // the value can matter: it is only ever consulted through max(singEndTime, ...), so it moves the
    // answer at all only on weird data where a word overruns the line's reported sing end, and a
    // subdivided word's last char still sits inside the same [unitStart, unitEnd] the flat ramp does.
    function lastTypeableTarget(line) {
        for (let i = line.cells.length - 1; i >= 0; i--) {
            if (line.cells[i].typeable) return line.cells[i].target;
        }
        return null;
    }

    // InstrumentalGaps.Compute: the qualifying gaps of a decoded map, in order. Consecutive pairs
    // only, qualification on the perceived stretch, then the usability filter.
    function computeGaps(lines) {
        const gaps = [];
        if (!lines || lines.length < 2) return gaps;

        for (let i = 0; i < lines.length - 1; i++) {
            if (firstVocalTime(lines[i + 1]) - lines[i].singEndTime < MIN_GAP_MS) continue;

            // The last moment the earlier line is genuinely being sung/typed: normally its
            // singEndTime, but the later of that and its last typeable target, because word times
            // can overrun the reported line end on weird data.
            const last = lastTypeableTarget(lines[i]);
            const sungEnd = last === null ? lines[i].singEndTime : Math.max(lines[i].singEndTime, last);

            const gapStart = sungEnd + GAP_START_SETTLE_MS;
            const activation = lines[i + 1].activationTime;
            const skipTarget = activation - SKIP_LEAD_MS;

            if (skipTarget - gapStart >= MIN_SKIP_WINDOW_MS) {
                // `line` is the index of the line the gap runs INTO, which is how the player looks
                // one up: the gap you are sitting in is the one before the line you are waiting for.
                gaps.push({ line: i + 1, gapStartTime: gapStart, activationTime: activation, skipTarget: skipTarget });
            }
        }

        return gaps;
    }

    // Total map-time ms a player may legally remove with the skip button, the quantity the server
    // stores as beatmaps.skippable_s (in seconds) and the play-time gate spends. Exported for the
    // parity pin rather than used at runtime.
    function skippableMs(lines) {
        let total = 0;
        for (const g of computeGaps(lines)) total += g.skipTarget - g.gapStartTime;
        return total;
    }

    // The INTRO skip, which is NOT an InstrumentalGaps gap: on the desktop it is the separate intro
    // SkipOverlay, landing at MasterGameplayClockContainer.Skip's GameplayStartTime -
    // MINIMUM_SKIP_TIME, i.e. (first object - 2000) - 1000. So: the first vocal less SKIP_LEAD_MS,
    // the same 3000 the mid-song skips leave in front of a line. It is gate-free by construction:
    // drain_length_s already starts at the first line, so this removes only run-up the play-time
    // gate never asked for. null when there is nothing in front of the first vocal to remove.
    function introSkipTarget(lines) {
        if (!lines || lines.length === 0) return null;
        const target = firstVocalTime(lines[0]) - SKIP_LEAD_MS;
        return target > 0 ? target : null;
    }

    // The line the player is WAITING FOR, which is what both the countdown chip and the skip look
    // up. Not simply nextSealIndex: a decoder-built line's window runs to the next line's start, so
    // all the way through an instrumental the finished line is still unsealed and the cursor still
    // points AT it. Three states, and they are the same three the chip's own gate distinguishes:
    // no line active (pre-roll, or a dead zone) is the seal cursor; a caret parked COMPLETE on its
    // own line is waiting for the one after it; a caret parked on a line it has not finished (the
    // rush roll-forward, or ordinary play) is waiting for nothing but that line.
    function upcomingLineIndex(activeLineIndex, activeLineComplete, nextSealIndex) {
        if (activeLineIndex < 0) return nextSealIndex;
        return activeLineComplete ? activeLineIndex + 1 : activeLineIndex;
    }

    // TypeBeatPlayfield's key-handler fall-through, which is what decides whether Space is a skip or
    // a character. The desktop swallows every typeable key while a line is active and INCOMPLETE, so
    // a skip can never eat a live keystroke; it lets the key fall through to GlobalAction.SkipCutscene
    // when no line is active at all, and when the active line IsLineComplete with no live retype
    // SELECTION (backlog 182: collapsing a selection re-opens the cells it covers, so the key
    // consuming it is a typing key again even though the line reads complete).
    //
    // This is also the WPM-CLOCK GUARD, and that is the load-bearing half. TypingEngine accrues
    // activeTimeMs only while the active line is incomplete (and, under the flexible caret, only
    // from that line's activationTime or its lazy arm), so a seek performed in any state this
    // returns true for adds nothing to the clock: exactly the desktop's behaviour, where the skip is
    // reachable from exactly these states. A seek from anywhere else would inject the whole skipped
    // span into the WPM the score is submitted with.
    function skipAllowed(lineActive, lineComplete, hasSelection) {
        if (!lineActive) return true;
        return lineComplete && !hasSelection;
    }

    // The skip target live at `time` for a player waiting on line `upcoming`, or null. The intro
    // before the first line; otherwise the qualifying gap that runs into that line, and only while
    // the clock is inside its skip period. Never past a line the player has not been offered:
    // skipTarget is SKIP_LEAD_MS BEFORE the next line's activation, so the seek lands in front of
    // the cue rather than inside the line.
    function skipTargetAt(gaps, introTarget, upcoming, time) {
        if (upcoming === 0) return introTarget !== null && time < introTarget ? introTarget : null;

        for (const g of gaps) {
            if (g.line !== upcoming) continue;
            return (time >= g.gapStartTime && time < g.skipTarget) ? g.skipTarget : null;
        }

        return null;
    }

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

        // The skippable stretches of this map, computed once: the qualifying instrumental gaps
        // (the mirror of what the server priced into beatmaps.skippable_s) and the intro run-up.
        const gaps = computeGaps(beatmap.lines);
        const introTarget = introSkipTarget(beatmap.lines);

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

        // Carets and the wrong-key pops live INSIDE the row they belong to, so they inherit its
        // scale and its position without a second coordinate space. That is the active row for
        // everything the PLAYER owns; the sung caret is moved to whichever row the VOCAL is on
        // (updateCarets), which is the same row in the ordinary case, the row behind when the caret
        // has been parked ahead, and the row ahead when it is dragging. So does the retype-selection
        // wash (backlog 182), which is painted BEHIND the glyphs (CSS z-index, see .tb-selection) so
        // every character keeps the colour its own state gives it: the highlight says "these are
        // about to go", not "these are wrong".
        const playerCaret = el('div', 'tb-caret tb-caret-player');
        const sungCaret = el('div', 'tb-caret tb-caret-sung');
        const wrongLayer = el('div', 'tb-wrongkeys');
        const selectionBox = el('div', 'tb-selection');
        rowCur.row.append(selectionBox, sungCaret, playerCaret, wrongLayer);

        // The chip is a BUTTON since backlog 230: through a qualifying gap it is the skip, and a
        // pointer has to be able to reach it too (Space is the desktop's key, but the browser
        // player is also played with a mouse in hand). It stays disabled, and looks exactly like
        // the countdown it always was, whenever there is no live skip window behind it.
        const gap = el('button', 'tb-gap');
        gap.type = 'button';
        gap.disabled = true;
        const gapLabel = el('span', 'tb-gap-label', '');
        const gapTrack = el('span', 'tb-gap-track');
        const gapFill = el('span', 'tb-gap-fill');
        const gapSkip = el('span', 'tb-gap-skip', '');
        gapTrack.appendChild(gapFill);
        gap.append(gapLabel, gapTrack, gapSkip);
        gap.addEventListener('click', () => performSkip(skipTarget));
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

        // Start (or restart) the buffer at `offsetMs` into the track and rebase the clock onto it,
        // so nowMs() jumps with the audio and everything downstream (the engine, every animation)
        // follows one clock as it always did. A BufferSource is single-use, hence the fresh node.
        //
        // The 60 ms scheduling lead is the same one begin() has always taken: `when` is when the
        // audio actually starts, and startedAt is back-dated by the offset so that at `when` the
        // gameplay clock reads exactly offsetMs.
        function startSourceAt(offsetMs) {
            cleanupAudio();
            source = audioCtx.createBufferSource();
            source.buffer = audioBuffer;
            // Default playback at 10% (90% quieter); the map audio is loud on its own.
            const gainNode = audioCtx.createGain();
            gainNode.gain.value = 0.1;
            source.connect(gainNode);
            gainNode.connect(audioCtx.destination);
            const offsetSec = Math.max(0, offsetMs) / 1000;
            const when = audioCtx.currentTime + 0.06; // small scheduling lead
            source.start(when, offsetSec);
            startedAt = when - offsetSec;
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
        // The live RETYPE SELECTION (backlog 182), or null: the half-open cell range
        // [startCell, endCell) of line lineIndex that a Ctrl+A has offered to erase and retype.
        // endCell is always the caret index it was taken at, which is what makes the selection
        // self-invalidating: any caret move that did not go through the consume path leaves it
        // stale and the render loop drops it (see dropStaleSelection).
        //
        // PURE UI STATE, exactly as on the desktop (TypeBeatPlayfield.CurrentRetypeSelection): the
        // engine never learns it exists, and consuming it is composed out of ordinary engine calls,
        // which is why neither gesture needs anything new in typebeat-core.js beyond the two pure
        // queries that say where each one stops.
        let selection = null;

        // TypeBeatKeyHandler.OnKeyDown's word-gesture carve-out: Control without Alt, on exactly
        // two keys, so every other browser and OS shortcut still reaches the browser. Meta is
        // excluded here where the desktop does not name it, because a Cmd combo on macOS is the
        // browser's own shortcut surface and gameplay must not start eating it.
        function isWordGesture(e) {
            if (!e.ctrlKey || e.altKey || e.metaKey) return false;
            return e.key === 'Backspace' || e.key === 'a' || e.key === 'A' || e.code === 'KeyA';
        }

        // TypeBeatKeyHandler.eraseBackTo. Erase back to target with ordinary processBackspace
        // calls: the same run of erases a player holding the plain key down would have made, which
        // is the whole point of composing the gesture rather than teaching the engine a wider one.
        //
        // The trailing check is defensive termination only. Every erase that reports a mutation
        // moves the caret back, but one that reclaimed abandoned cells at the head of a line can
        // land on 0 and be auto-skipped forward again, and a gesture must never spin.
        function eraseBackTo(target) {
            while (engine.caretIndex > target) {
                const before = engine.caretIndex;
                if (!engine.processBackspace()) break;
                if (engine.caretIndex >= before) break;
            }
        }

        // TypeBeatKeyHandler.collapseSelection. Collapse a live retype selection: a mass backspace
        // to its anchor. Returns whether there was one to collapse, so the caller can tell "the
        // selection ate this key" from "there was nothing there". The selection is dropped BEFORE
        // the erases so the staleness check cannot race them.
        function collapseSelection() {
            if (!selection) return false;
            const start = selection.startCell;
            setSelection(null);
            eraseBackTo(start);
            return true;
        }

        function onKeyDown(e) {
            if (!running || !engine) return;

            // The two word-level gestures the player owns (backlog 182). Carved out BEFORE the
            // fall-through below, which is otherwise unchanged: every other Ctrl/Alt/Meta combo is
            // left to the browser.
            const wordGesture = isWordGesture(e);
            if ((e.ctrlKey || e.altKey || e.metaKey) && !wordGesture) return;

            // Backspace, gated exactly as TypeBeatPlayfield's key handler gates it: erasing only
            // ever has something to undo where a wrong char can land, so it reads the engine's
            // allowWrongInput flag rather than a rule of its own. That flag is on for every browser
            // play (the browser has no mods payload and so can never be Gatekeeper), which means
            // backspace is LIVE here, where under the old strict-only model it was inert. The key is
            // still swallowed either way, and the engine is still where the erase is decided:
            // processBackspace no-ops when there is nothing behind the caret.
            //
            // preventDefault covers the plain key (which navigates back on older browsers) and the
            // Ctrl combo (which the browser reads as "delete the previous word"). Repeat is honoured
            // for BOTH widths, exactly as on the desktop: this branch returns above the e.repeat
            // guard, so holding either erases.
            //
            // CTRL takes the whole word. A live SELECTION takes precedence over either width: an
            // erase key over one collapses it and types nothing, which is the same mass erase a
            // letter would do before landing.
            if (e.key === 'Backspace') {
                e.preventDefault();
                if (!engine.allowWrongInput) return;
                if (!collapseSelection()) {
                    if (wordGesture) eraseBackTo(engine.wordBackspaceTarget);
                    else engine.processBackspace();
                }
                return;
            }

            if (wordGesture) {
                // CTRL+A: offer the run back to the earliest unfixed typo for retyping (backlog 184
                // widened it from the nearest one, so one press offers every mistake). preventDefault
                // because the browser's own Ctrl+A selects the whole page. Gated on the same flag the
                // erase is, and for the same reason: with no wrong character able to land there would
                // never be a typo to select.
                e.preventDefault();
                if (!engine.allowWrongInput) return;

                const anchor = engine.retypeSelectionAnchor;

                // No typo behind the caret: a genuine no-op, nothing to select and nothing to clear
                // (a selection can only exist where the query just answered). Pressing it again with
                // one already open simply recomputes the same range.
                if (anchor >= 0) {
                    setSelection({ lineIndex: engine.activeLineIndex, startCell: anchor, endCell: engine.caretIndex });
                }
                return;
            }

            if (e.repeat) return;

            // SPACE AS THE SKIP KEY, on exactly the desktop's terms (see skipAllowed): only where
            // TypeBeatPlayfield's key handler would let the press fall through to
            // GlobalAction.SkipCutscene, and only while a skip window is actually live. Anywhere
            // else it falls straight into the typeable branch below and is a word-gap character,
            // which is the ONE thing that must not change: a skip that could fire mid-line would
            // both eat a keystroke and inject the skipped span into the WPM clock.
            if ((e.key === ' ' || e.code === 'Space') && skipAllowedNow()) {
                const target = pendingSkipTarget(nowMs());
                if (target !== null) {
                    e.preventDefault();
                    performSkip(target);
                    return;
                }
            }

            let ch = null;
            if (e.key === ' ' || e.code === 'Space') ch = ' ';
            else if (e.key && e.key.length === 1 && KEY_RE.test(e.key)) ch = e.key;
            if (ch !== null) {
                e.preventDefault();
                // A retype selection is consumed FIRST, so this key lands on the anchor cell: mass
                // backspace, then the ordinary judged keypress. Space is not special here, nor is any
                // other typeable key: "collapse, then process normally" is the whole rule. The
                // desktop suspends its line-complete fall-through to the skip overlay while a
                // selection is live, and since backlog 230 so does this file: skipAllowed() takes
                // the selection, so a key arriving over one is a typing key even on a line that
                // reads complete, and control has already fallen through to here.
                collapseSelection();
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
                // fills[i] is the --tb-sync-fill last WRITTEN to spans[i] (null = property absent),
                // so paintRow can skip the style write on every frame that did not move it.
                spans: [], fills: [], offsets: [0], line: null, index: -1, scale: 1
            };
        }

        function buildRow(rowObj, index) {
            const line = beatmap.lines[index] || null;
            rowObj.line = line;
            rowObj.index = line ? index : -1;
            rowObj.cellsBox.textContent = '';
            rowObj.spans = [];
            rowObj.fills = [];
            rowObj.offsets = [0];
            rowObj.scale = 1;
            rowObj.row.style.transform = 'none';
            rowObj.sweep.style.display = line ? '' : 'none';
            if (!line) return;

            const frag = document.createDocumentFragment();
            for (let i = 0; i < line.cells.length; i++) {
                const span = el('span');
                rowObj.spans.push(span);
                rowObj.fills.push(null);
                frag.appendChild(span);
            }
            rowObj.cellsBox.appendChild(frag);
        }

        function cellText(cell, shimmerTick, i) {
            let ch = cellGlyph(cell);
            if (cell.freestyle) ch = cell.typedChar !== null ? cell.typedChar : Core.freestyleGlyph(shimmerTick, i);
            // A space (a word gap's expected char, or a space typed into a freestyle slot) must
            // render as nbsp or the browser collapses it away.
            return ch === ' ' ? ' ' : ch;
        }

        // Repaint a row in place: only the spans whose class, sync tint or glyph actually changed
        // are written, so a settled line costs nothing and the shimmer only touches its own cells.
        //
        // The sync tint is NOT recomputed into the DOM every frame. cellFill() is a couple of
        // multiplies off state the engine already holds, but the write is guarded by the row's own
        // last-written value: a cell's fill can only move when its state or judged delta moves,
        // both of which happen once, on the frame the key lands (or on a backspace), so a line the
        // player has finished typing writes nothing at all for the rest of its time on screen.
        function paintRow(rowObj, caretIndex, shimmerTick, time) {
            const line = rowObj.line;
            if (!line) return;
            for (let i = 0; i < rowObj.spans.length; i++) {
                const span = rowObj.spans[i];
                const cell = line.cells[i];
                const popping = popEnd[i] > time && rowObj === rowCur;
                const cls = cellClass(cell, i === caretIndex, popping);
                if (span.className !== cls) span.className = cls;
                const fill = cellFill(cell);
                if (rowObj.fills[i] !== fill) {
                    rowObj.fills[i] = fill;
                    if (fill === null) span.style.removeProperty('--tb-sync-fill');
                    else span.style.setProperty('--tb-sync-fill', fill);
                }
                const txt = cellText(cell, shimmerTick, i);
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
        let caretX = 0, sungX = 0, caretSnap = true, sungSnap = true;
        let sweptLine = -1;          // the line whose row carries the underline fill; -1 = none yet
        let lastTypedAt = -1e9, lastFrameMs = null;
        let scrollPitch = 0, scrollStart = -1;
        let popEnd = [];             // per-cell audio-clock deadline for the top-tier pop
        let gapActive = false, gapFrom = 0, gapTo = 0;
        // The skip the chip is currently offering (its button reads this), or null for none.
        let skipTarget = null;
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
            sungSnap = true;
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

        // The sung underline: a faint full-width track under every visible line, a fill that sweeps
        // to the vocal position, and a bright head. Both the fill and the head belong to the SUNG
        // row rather than the caret's row: with the caret parked ahead, or dragging behind, the two
        // are different rows, and this is the vocal's position, not the player's.
        //
        // sweptLine is the desktop's field of the same name: it holds the last line that carried a
        // fill, so while nothing is being sung (pre-roll, or the dead zone between a seal and the
        // next line's cue) the row the song last left keeps its finished sweep, exactly as the
        // desktop's per-display fill does when Update takes its no-active-line branch. Tracked by
        // LINE index rather than by row, because the three rows are recycled: keying on the row
        // would leave a stale fill on the element the next line was just built into.
        function updateSweeps(time, sungRow) {
            if (sungRow) sweptLine = sungRow.index;

            for (const r of rows) {
                if (!r.line) continue;
                const pos = sweepFillFor(r.index, sweptLine, sungPoints[r.index], time);
                const x = xAt(r, pos);
                r.sweepFill.style.width = x.toFixed(2) + 'px';
                r.sweepGlow.style.transform = 'translateX(' + x.toFixed(2) + 'px)';
                r.sweepGlow.style.opacity = (r === sungRow && x > 0.5) ? '0.9' : '0';
            }
        }

        // Player caret follows the typing caret index on the CARET's row; sung caret follows the
        // vocal position on the SUNG row (LyricStage.Update). Normally one row, and then this is
        // the old behaviour; under a parked caret the sung caret moves to the row behind, which is
        // where the vocal actually is. Both damp toward their target and snap on a line jump
        // (Caret.MoveToTarget).
        //
        // The sung caret is REPARENTED into that row rather than offset within the caret's row: the
        // rows are their own coordinate spaces (each carries its own auto-shrink scale and its own
        // font size), and every overlay x in this file is a plain row-local offset inside the row it
        // is drawn on. That is how the sweep on a neighbour row already works, so the fill, the head
        // and the caret stay one object. The desktop hides the sung caret once the song is more than
        // one line from the focused line, since it is off the visible stack; here that is the same
        // test, expressed as "no row is showing it".
        function updateCarets(time, elapsed, active, sungRow) {
            const lineComplete = active && engine.caretIndex >= rowCur.line.cells.length;
            const shown = caretsVisible(active, lineComplete, engine.finished, !!sungRow);

            if (sungRow && sungCaret.parentNode !== sungRow.row) {
                sungRow.row.appendChild(sungCaret);
                sungSnap = true;
            }

            const caretTarget = active ? xAt(rowCur, engine.caretIndex) : caretX;
            const sungTarget = sungRow ? xAt(sungRow, sungPositionAt(sungPoints[sungRow.index], time)) : sungX;
            const lineHeight = rowCur.row.offsetHeight || 40;

            if (caretSnap) {
                caretX = caretTarget;
                caretSnap = false;
            } else {
                caretX = approach(caretX, caretTarget, CARET_DAMP_HALF_TIME, elapsed, lineHeight);
            }

            // The sung caret snaps on a row change of its own as well as on a relayout: its x is
            // row-local, so damping it across two rows would slide it through a position that means
            // nothing in either space.
            if (sungSnap) {
                sungX = sungTarget;
                sungSnap = false;
            } else {
                sungX = approach(sungX, sungTarget, SUNG_DAMP_HALF_TIME, elapsed, lineHeight);
            }

            playerCaret.style.transform = 'translateX(' + caretX.toFixed(2) + 'px)';
            sungCaret.style.transform = 'translateX(' + sungX.toFixed(2) + 'px)';

            const moving = Math.abs(caretTarget - caretX) > CARET_MOVING_EPSILON;
            playerCaret.style.opacity = shown.player ? caretAlpha(time - lastTypedAt, moving, true).toFixed(3) : '0';
            sungCaret.style.opacity = shown.sung ? '1' : '0';
        }

        function approach(current, target, halfTime, elapsed, lineHeight) {
            if (Math.abs(target - current) > lineHeight * CARET_SNAP_FACTOR) return target;
            return dampContinuously(current, target, halfTime, elapsed);
        }

        // TypeBeatPlayfield.applyRetypeSelection: the ONE write site for the selection, so the
        // highlight and the state it is drawn from cannot drift apart.
        function setSelection(next) {
            selection = next;
            paintSelection();
        }

        // LyricLineDisplay.SetSelection. The wash over the half-open cell range the gesture offered,
        // in the row-local (pre-scale) coordinate space the caret already lives in, so it stays
        // registered to the glyphs through a fit or a resize. An empty (or stale, hence clamped by
        // xAt) range paints nothing rather than a zero-width sliver, which is the desktop's
        // width > 0 alpha rule.
        function paintSelection() {
            if (!selection || !rowCur.line) { selectionBox.style.display = 'none'; return; }

            const x = xAt(rowCur, selection.startCell);
            const width = xAt(rowCur, selection.endCell) - x;

            if (!(width > 0)) { selectionBox.style.display = 'none'; return; }

            selectionBox.style.display = '';
            selectionBox.style.transform = 'translateX(' + x.toFixed(2) + 'px)';
            selectionBox.style.width = width.toFixed(2) + 'px';
        }

        // TypeBeatPlayfield.Update's staleness drop. A retype selection is a gesture held open on the
        // ACTIVE line between two keystrokes, so anything that moves out from under it drops it: the
        // line deactivating or sealing (the index changes, or goes to -1), and any caret move that
        // did not go through the consume path. Checked per frame because both of those can happen on
        // a plain clock tick, with no key event to notice them.
        function dropStaleSelection() {
            if (selection && (engine.activeLineIndex !== selection.lineIndex || engine.caretIndex !== selection.endCell)) {
                setSelection(null);
            }
        }

        // A long instrumental stretch (and the pre-roll before the first line) is dead air the
        // desktop covers with osu's skip overlay. The browser now covers it the same way: the chip
        // labels the wait and counts it down, and inside a qualifying gap's skip window it is also
        // the skip button (backlog 230 reversed 218's scope call, which had reasoned that "the
        // browser cannot seek its scheduled audio source without moving the gameplay clock" was a
        // blocker; moving the gameplay clock is what a skip IS, and a fresh BufferSource started at
        // an offset with startedAt rebased is an exact seek, see startSourceAt). The cue bars still
        // land on the line itself.
        //
        // Gated on songIsOnTheCaretsLine rather than on "a line is active", which is the same
        // distinction the desktop's Space fall-through makes (TypeBeatPlayfield's key handler) and
        // for the same reason. Since backlog 208 finishing a line parks the caret at the head of
        // the next one, so a line stays ACTIVE straight through an instrumental and the old gate
        // would have hidden the countdown from exactly the players who earned the wait. The chip
        // asks "is the song asking me for characters right now", and a parked caret is the case
        // where the answer is no while a line is active.
        //
        // Backlog 218 gave that question a SECOND parked state to answer, which is why the gate is
        // two clauses now: the rush bound holds a finished caret past the last cell of its OWN line
        // until the next one is nearly due, and there the song is still on the caret's line while
        // nothing whatever is being asked of the player. The desktop's key handler makes the same
        // pair, reaching its skip overlay through IsLineComplete in that state and through this
        // predicate in the other one; a single clause here would hide the countdown for all but the
        // last 1500 ms of every instrumental, from exactly the players who earned the wait.
        //
        // THE LINE IT COUNTS DOWN TO is upcomingLineIndex, not the seal cursor. The cursor was what
        // shipped in 218, and on a decoder-built map it is the line the player has just FINISHED for
        // the whole length of an instrumental (line windows are contiguous, so a typed-out line does
        // not seal until the next one starts), whose activation is by then far in the past: the
        // chip's own "is this gap worth drawing" test therefore refused every mid-song gap and only
        // the intro ever drew one. The two-clause gate below was already written for the mid-song
        // case; this is the other half of it.
        function updateGap(time) {
            const active = engine.activeLineIndex >= 0;
            const parkedComplete = active && engine.isLineComplete(engine.activeLineIndex);
            const upcoming = upcomingLineIndex(engine.activeLineIndex, parkedComplete, engine.nextSealIndex);
            const hasNext = !engine.finished && !engine.failed && upcoming >= 0 && upcoming < beatmap.lines.length;

            if ((engine.songIsOnTheCaretsLine && !parkedComplete) || !hasNext) {
                if (gapActive) { gapActive = false; gap.classList.remove('tb-gap-on'); }
                setSkipAffordance(null);
                return;
            }

            const line = beatmap.lines[upcoming];
            const vocal = line.cells.length ? line.cells[0].target : line.startTime;

            if (!gapActive) {
                if (line.activationTime - time < GAP_CHIP_MIN_MS) { setSkipAffordance(null); return; }
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

            // Live only where the key would be: a chip whose gap does not qualify, or one the
            // player is watching from a state the desktop would still be taking characters in,
            // stays the plain countdown it was.
            setSkipAffordance(skipAllowedNow() ? skipTargetAt(gaps, introTarget, upcoming, time) : null);
        }

        // Whether Space is a skip right now rather than a character, on the desktop's own predicate.
        function skipAllowedNow() {
            const active = engine.activeLineIndex >= 0;
            return skipAllowed(active, active && engine.isLineComplete(engine.activeLineIndex), selection !== null);
        }

        // The skip target live at `time`, or null. Kept apart from the chip so the key path and the
        // button path answer to exactly one rule.
        function pendingSkipTarget(time) {
            if (!running || engine.finished || engine.failed) return null;

            const active = engine.activeLineIndex >= 0;
            const upcoming = upcomingLineIndex(
                engine.activeLineIndex, active && engine.isLineComplete(engine.activeLineIndex), engine.nextSealIndex);

            if (upcoming < 0 || upcoming >= beatmap.lines.length) return null;

            return skipTargetAt(gaps, introTarget, upcoming, time);
        }

        function setSkipAffordance(target) {
            const live = target !== null;
            if (gap.disabled !== !live) gap.disabled = !live;
            gap.classList.toggle('tb-gap-ready', live);
            const label = live ? 'skip' : '';
            if (gapSkip.textContent !== label) gapSkip.textContent = label;
            skipTarget = target;
        }

        // Player.PerformSkipTo: a no-op if the clock is already past the target, else seek. Here the
        // seek IS the audio restart (there is no separate gameplay clock to move), and the engine
        // simply meets the new time on the next tick, sealing anything it passed through the normal
        // loop. A skip target is always SKIP_LEAD_MS in front of the next line's activation, so the
        // walk can never step over a line the player has not been offered.
        function performSkip(target) {
            if (!running || !audioCtx || !audioBuffer || target === null) return;
            if (!(target > nowMs())) return;
            if (target / 1000 >= audioBuffer.duration) return;

            startSourceAt(target);

            // The clock has jumped, so this frame's damping delta is meaningless and both heads
            // belong at their new positions rather than sliding there through the gap.
            lastFrameMs = null;
            caretSnap = true;
            sungSnap = true;
            gapActive = false;
            gap.classList.remove('tb-gap-on');
            setSkipAffordance(null);
            if (typeof gap.blur === 'function') gap.blur();
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

            // Before anything is painted, so a selection the clock has just invalidated is gone in
            // the same frame the caret it no longer matches moves.
            dropStaleSelection();
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

            // The row the vocal is on, resolved AFTER any rebuild above, since rowFor() reads the
            // stack's current focus. null while nothing is active, and null when the song is more
            // than one line away from the caret (a player who ran several lines ahead, or who fell
            // that far behind): it is off the visible stack, so nothing draws it rather than
            // parking it at a phantom position.
            const sungIdx = active
                ? sungLineFor(engine.fletcherEnabled, engine.activeLineIndex, engine.nextUnsealedLineIndex, beatmap.lines, time)
                : -1;
            const sungRowCandidate = sungIdx >= 0 ? rowFor(sungIdx) : null;
            const sungRow = sungRowCandidate && sungRowCandidate.line ? sungRowCandidate : null;

            updateScroll(time);
            updateSweeps(time, sungRow);
            updateCarets(time, elapsed, active, sungRow);
            // Repainted per frame like the caret, and for the same reason: a fit or a resize moves
            // every cell offset under it.
            paintSelection();
            updateCue(time, active);
            updateGap(time);

            const stats = liveStats(engine);
            scoreEl.textContent = fmtInt(engine.score);
            // The engine's own live combo, which breaks on a wrong key as you watch. The results
            // screen shows the SUBMITTED peak instead (computeScore's maxCombo, off the score
            // processor mirror); the two agree in strict vanilla play, which is all /play has.
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
                if (type === 'Great' && index < popEnd.length) popEnd[index] = nowMs() + PERFECT_POP_MS;
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
            setSkipAffordance(null);
            stack.style.transform = 'none';
            wrongLayer.textContent = '';
            setSelection(null);
            rolling = makeRollingWpm(ROLLING_WPM_WINDOW);
            removeStartKey();
            // A fresh play (including "play again") starts a new scoring session; let the host
            // mint a fresh score token here so its min-play-time gate keys off this play's start.
            if (opts.onPlayStart) { try { opts.onPlayStart(); } catch (e) { console.error(e); } }
            overlay.className = 'tb-overlay';
            overlay.innerHTML = '';
            startSourceAt(0);
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
                // ONE typo number (backlog 140), counting wrong KEYPRESSES. Beside misses, never
                // folded into them: a miss is a character the song left behind, a typo is a key you
                // got wrong. There used to be a second number here, the cells still holding a wrong
                // character at the seal; every one of those implied a wrong keypress, so this count
                // already covers them. What such a cell COSTS is unchanged (accuracy, completion and
                // rank all still fall), it is just no longer counted at the player twice.
                statCell('typos', results.counts.mistypes);
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
        syncTintFill,
        cellFill,
        cellClass,
        cellGlyph,
        liveStats,
        cueTargetLine,
        sungLineFor,
        sweepFillFor,
        caretsVisible,
        outQuint,
        // The instrumental-skip rule (a third copy of a cross-repo-pinned one), exported so
        // WebplayDisplayTest can hold it against the server's own InstrumentalGaps.Compute.
        firstVocalTime,
        lastTypeableTarget,
        computeGaps,
        skippableMs,
        introSkipTarget,
        upcomingLineIndex,
        skipAllowed,
        skipTargetAt,
        constants: {
            CUE_LEAD_MS, CUE_BAR_MAX_PX, CARET_DAMP_HALF_TIME, SUNG_DAMP_HALF_TIME,
            CARET_BLINK_PERIOD, LINE_SCROLL_MS, CARET_SNAP_FACTOR, PERFECT_POP_MS,
            ROLLING_WPM_WINDOW, GAP_CHIP_MIN_MS, SYNC_TINT_FLOOR,
            MIN_GAP_MS, GAP_START_SETTLE_MS, MIN_SKIP_WINDOW_MS, SKIP_LEAD_MS
        }
    };
})(window.TypeBeatCore);
