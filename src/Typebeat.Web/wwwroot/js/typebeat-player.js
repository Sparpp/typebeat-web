/*
 * typebeat-player.js: the browser renderer + Web Audio driver on top of
 * typebeat-core.js. Exposes TypeBeatCore.mountPlayer(container, opts).
 *
 * opts:
 *   osuText            : string   (the .osu lyric text)
 *   audioArrayBuffer   : ArrayBuffer (raw encoded audio bytes; decoded here)
 *   title, artist      : strings  (for the results header; falls back to the map)
 *   onFinish(results, api) : called once when a play ends (pass or fail), with the card, after
 *                      the results delay or fail wind-down (the results are taken at the end itself)
 *   onExit()           : called when the player wants to leave (back button)
 *   discordNudge(results) : optional. Consulted once per results card; answers with an invite URL
 *                      to show the player, or null for "show nothing". The decision is the host
 *                      page's because it turns on localStorage and on a server-rendered invite
 *                      constant, neither of which belongs in this layer.
 *
 * Timing is driven by the Web AudioContext clock (sample-accurate); gameplay
 * time = (ctx.currentTime - startedAt) * 1000, matching the desktop's
 * audio-sourced gameplay clock with LyricOffset = 0.
 *
 * PRESENTATION PARITY. Everything below the "display" banner is a port of the desktop
 * client's gameplay presentation (typebeat.Game.Rulesets.TypeBeat.UI: LyricStage, Caret,
 * LyricLineDisplay, TypeBeatHudOverlay) into HTML/CSS/JS: the damped+blinking player caret,
 * the sung sweep and sung caret driven by the line's per-char target times, the two depleting
 * cue-in bars, the 3-line dim ladder and the eased scroll on line change, the rolling WPM
 * readout, and the per-cell judgement feedback. Backlog 251 removed the sync readout and its
 * per-cell tint: the desktop metric they mirrored is off by default too, and the browser has no
 * settings UI to gate it behind, so it mirrors the default by dropping it entirely.
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
    const PUSH_FADE_IN_MS = 400;            // LyricStage.push_fade_in_ms: the push warning's entrance
    const CARET_DAMP_HALF_TIME = 35;        // TypeBeatStyle.CARET_DAMP_HALF_TIME (ms)
    const SUNG_DAMP_HALF_TIME = 45;         // TypeBeatStyle.SUNG_DAMP_HALF_TIME (ms)
    const CARET_BLINK_PERIOD = 530;         // TypeBeatStyle.CARET_BLINK_PERIOD (ms)
    const LINE_SCROLL_MS = 220;             // TypeBeatStyle.LINE_SCROLL_DURATION (ms), OutQuint
    const CARET_SNAP_FACTOR = 1.5;          // Caret.MoveToTarget: snap past 1.5 line heights
    const CARET_MOVING_EPSILON = 0.75;      // Caret.Update: "still moving" threshold, px
    const PERFECT_POP_MS = 120;             // LyricLineDisplay.PlayJudgementFeedback, OutQuint
    const CARET_FADE_MS = 120;              // LyricStage.setCaretsVisible, OutQuint
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

    // ---------------------------------------------------------------------------
    // Pure display math. No DOM, no engine mutation; exported on Core.display purely so the
    // Node fidelity harness can drive it (see tests/Typebeat.Web.Tests/Js/PlayerDisplayHarness.cjs).
    // ---------------------------------------------------------------------------

    /// osu.Framework Interpolation.DampContinuously: frame-rate independent approach to a target.
    function dampContinuously(current, target, halfTime, elapsedMs) {
        if (!(halfTime > 0) || !(elapsedMs > 0)) return current;
        return current + (target - current) * (1 - Math.pow(0.5, elapsedMs / halfTime));
    }

    // The last WORD's own close: the anchor the final sung-polyline segment closes on (mirrors the
    // fix to TypingLine.FromLyricLine / TypingLine.cs:343, which captures the last token's own unit
    // end instead of the line's singEndTime). typebeat-player.js is handed the already-built line,
    // not its units, so this is read off the line's own syllable groups (buildSyllables, in
    // typebeat-core.js) instead: the LAST group of a line always closes on its own token's unit end
    // ("last group of the token closes on the WORD's end"; since backlog 363 an unsubdivided last
    // token is ONE group over its whole unit, which closes there too), and every non-empty last
    // token gets a group unless it is a stylised triple-letter run (isSyllabifiable), so this recovers the true
    // value for real content. Falls back to singEndTime only when the line produced no groups at
    // all, mirroring the game's Units.Count == 0 fallback for a line with no word timing.
    //
    // Since backlog 317 the built line carries the true value (typebeat-core.js's lastUnitEnd, the
    // walk the desktop captures), which is read first: the group reading above disagrees with the
    // desktop whenever the last token is stylised and owns no group, and the pace hue's parity pin
    // against UnderlinePace.SungEndOf is what caught it. The group reading stays as the fallback for
    // a hand-built line that carries no lastUnitEnd.
    function lastUnitEndOf(line) {
        if (typeof line.lastUnitEnd === 'number') return line.lastUnitEnd;
        return line.syllables.length > 0
            ? line.syllables[line.syllables.length - 1].endTime
            : line.singEndTime;
    }

    // The sung-position polyline for one line (mirrors TypingLine's sungPoints constructor):
    // (startTime, 0), each cell's (target, cellIndex), (last word's own end, cellCount), with times
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
        // Close on the last word's own end, not the line's declared sung end: an editor drag of the
        // sung-end flag must not stretch or compress the caret's pace through the final character.
        // Kept as a max() against `last` (not lastUnitEndOf alone), because the game proved that
        // guard reachable with overlapping units on inverted or malformed data.
        points.push({ t: Math.max(lastUnitEndOf(line), last), i: line.cells.length });
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

    // The first TYPEABLE cell of a line, or -1 when it has none (mirrors
    // LyricStage.firstTypeableIndex). Every cell of the default stream /play builds is typeable, so
    // on a served map this is always 0 for a non-empty line; it is read through the flag anyway so
    // the cue and the push warning anchor where the desktop's do on any line that ever carries one
    // that is not.
    function firstTypeableIndex(line) {
        for (let i = 0; i < line.cells.length; i++) {
            if (line.cells[i].typeable) return i;
        }
        return -1;
    }

    // The two "get ready" bars for the line `target` (mirrors LyricStage.updateApproachCue's
    // drawing half): the first-word bar lands on the FIRST TYPEABLE cell's target, and the boundary
    // bar on the line's startTime, pinned caret only. Returns null when there is nothing to anchor
    // on (no such line, or a line with no typeable cell), which hides both.
    //
    // The first-word bar is the 50%-opaque whisper while the line is still to come, and FULL
    // strength when the cued line is the one the player is already on (target === the active line:
    // the line self-activated into its own lead-in, or the pinned caret was handed it). That cue is
    // not a hint about a line they cannot type yet, it is the line under their caret.
    function approachCueBars(lines, target, activeLineIndex, pinnedCaret, time) {
        if (target < 0 || target >= lines.length) return null;
        const line = lines[target];
        const firstCell = firstTypeableIndex(line);
        if (firstCell < 0) return null;

        const wordOpacity = target === activeLineIndex ? 1 : 0.5;
        return {
            firstCell: firstCell,
            word: cueBar(line.cells[firstCell].target - time, wordOpacity),
            boundary: pinnedCaret ? cueBar(line.startTime - time, 1) : { shown: false, width: 0, alpha: 0 }
        };
    }

    // The instant the PUSH WARNING opens (mirrors LyricStage.pushWarningOpensAt): when the line
    // about to take the player counts as starting, which is that line's FIRST WORD (its first
    // typeable cell's target, the instant the first-word cue lands on) and not the boundary a mapper
    // may have set earlier. The caret is unpinned wherever a cutoff exists at all, and an unpinned
    // caret cannot type an unopened line, so that boundary is no moment the player acts on.
    //
    // Falls back to CUE_LEAD_MS before the cutoff when there is no next typeable line to anchor on
    // (the map's last line, a line of pure punctuation) or when its first word begins at or after
    // the push itself, where a word-anchored window would have no width and the player would get no
    // warning at all rather than a short one.
    function pushWarningOpensAt(lines, activeLineIndex, cutoff) {
        const fallback = cutoff - CUE_LEAD_MS;
        const next = activeLineIndex + 1;
        if (next < 0 || next >= lines.length) return fallback;

        const firstCell = firstTypeableIndex(lines[next]);
        if (firstCell < 0) return fallback;

        const wordStart = lines[next].cells[firstCell].target;
        return wordStart < cutoff ? wordStart : fallback;
    }

    // One frame of the push warning (mirrors LyricStage.updatePushWarning's bar). The window runs
    // from pushWarningOpensAt for a FIXED CUE_LEAD_MS, so the bar always drains at the one rate the
    // player has learned from the cues; it is not stretched to fill a longer span to the push, and
    // the push (the cutoff) cuts it short rather than reshaping it. Width and brightness are the
    // cue's own depleting shape measured to the window's close, and the whole alpha is multiplied by
    // a PUSH_FADE_IN_MS linear fade-in: a cue may snap on, a warning the player did not ask for may
    // not. So the opening frame is `shown` (the window is open) at full width and alpha 0.
    //
    // `cutoff` is TypingEngine.dragCutoffAt, null wherever no push is coming, which hides the bar.
    function pushWarningBar(lines, activeLineIndex, cutoff, time) {
        if (cutoff === null || activeLineIndex < 0 || activeLineIndex >= lines.length) {
            return { shown: false, width: 0, alpha: 0 };
        }

        const opensAt = pushWarningOpensAt(lines, activeLineIndex, cutoff);
        const closesAt = opensAt + CUE_LEAD_MS;
        if (time >= opensAt && time < cutoff && time < closesAt) {
            const progress = (closesAt - time) / CUE_LEAD_MS;   // 1 -> 0 as it lands
            const fadeIn = Math.min(1, Math.max(0, (time - opensAt) / PUSH_FADE_IN_MS));
            return {
                shown: true,
                width: CUE_BAR_MAX_PX * progress,
                alpha: (0.85 - 0.35 * progress) * fadeIn
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

    // Caret show/hide over time (mirrors LyricStage.setCaretsVisible): a change of visibility
    // starts a FadeTo(1 or 0, CARET_FADE_MS, OutQuint) from wherever the fade currently stands, so
    // a reversal mid-fade turns round without a jump. It multiplies caretAlpha rather than being a
    // CSS transition, because the blink writes the same opacity every frame and a transition would
    // smear it. Starts hidden, as the desktop carets are built with Alpha 0.
    function makeCaretFade(durationMs) {
        let target = false, from = 0, start = -Infinity;
        return function (visible, time) {
            const p = durationMs > 0 ? Math.min(1, Math.max(0, (time - start) / durationMs)) : 1;
            const current = from + ((target ? 1 : 0) - from) * outQuint(p);
            if (visible === target) return current;
            target = visible;
            from = current;
            start = time;
            return current;
        };
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

    // THE LIVE HUD (backlog 319): the two numbers the desktop's default HUD shows, both read off the
    // score processor mirror, so neither is a second account of the play.
    //
    // score: the standardised total climbing live (GameplayScoreCounter, bound to
    // ScoreProcessor.TotalScore). ScoreProcessor.updateScore runs the very formula computeScore
    // does, so the HUD asks computeScore rather than restating it, and the last frame's figure is
    // the one the card shows and /play submits. engine.score, the combo-weighted internal points,
    // stays engine data (the fuzz harnesses pin it for parity) and is no longer on screen: it is
    // on a different scale from the 1,000,000 total.
    //
    // accuracy: JUDGED-only accuracy (GameplayAccuracyCounter's Standard mode, bound to
    // ScoreProcessor.Accuracy = currentBaseScore / currentMaximumBaseScore), 1 before anything is
    // judged. Not engine.liveAccuracy, which is a keypress ratio and blind to tier.
    //
    // It replaces the old 'typed' readout (liveStats), which counted correct cells over cells seen,
    // ignored tier, counted an off-time press as unhit and never counted a sealed typo, so it could
    // disagree with the card's own completion. The desktop HUD has no completion readout at all;
    // the card keeps completion, read off computeScore, where rank is decided.
    function hudReadouts(engine) {
        const processor = engine.processor;
        return {
            score: Core.computeScore(engine).totalScore,
            accuracy: processor.maximumBaseScore > 0 ? processor.baseScore / processor.maximumBaseScore : 1
        };
    }

    // FormatUtils.FormatAccuracy: floored to four decimal digits so 89.99999% never reads as 90%
    // (the rank cutoffs sit on whole numbers), then shown to two decimals of a percent.
    function fmtAccuracy(accuracy) {
        return (Math.floor(accuracy * 10000) / 100).toFixed(2) + '%';
    }

    // SONG PROGRESS (backlog 319), SongProgress over the playable bounds rather than the decoded
    // file: FirstHitTime is the first hit object's StartTime (line 0's startTime) and LastHitTime
    // the latest GetEndTime (a TypeBeatHitObject ends at Line.EndTime + SealGraceMs). Before
    // FirstHitTime the bar is in its INTRO phase, which runs from the clock's own start
    // (GameplayClock.StartTime, i.e. gameplayStartTime, negative on a pre-roll) to FirstHitTime.
    function songProgressBounds(lines, clockStart) {
        if (!lines || lines.length === 0) return { clockStart: clockStart || 0, first: 0, last: 0 };
        let last = -Infinity;
        for (const line of lines) last = Math.max(last, line.endTime + line.sealGraceMs);
        return { clockStart: clockStart, first: lines[0].startTime, last: last };
    }

    // SongProgress.Update's split plus ArgonSongProgress's reading of it: the bar holds at 0 through
    // the intro (UpdateProgress's isIntro arm) and then runs 0..1 over the playable span, with the
    // time clamped to LastHitTime. introProgress is the desktop's intro fraction, which the Argon bar
    // does not draw; it is exposed for the pin and the intro styling.
    //
    // The time text is SongProgressInfo, which counts from FirstHitTime and does NOT clamp: elapsed
    // is floor((time - first) / 1000) whole seconds (negative through the intro), remaining is
    // last - time, and both freeze once elapsed reaches the span's length (the desktop only
    // rewrites them while songCurrentTime < songLength), which the caller honours through
    // `textLive`.
    function songProgressAt(bounds, time) {
        const current = Math.min(time, bounds.last);
        const isIntro = current < bounds.first;
        let barProgress, introProgress;
        if (isIntro) {
            const introDuration = bounds.first - bounds.clockStart;
            introProgress = introDuration > 0 ? (current - bounds.clockStart) / introDuration : 1;
            barProgress = 0;
        } else {
            const duration = bounds.last - bounds.first;
            introProgress = 1;
            barProgress = duration === 0 ? 0 : (current - bounds.first) / duration;
        }
        const songCurrentTime = time - bounds.first;
        return {
            isIntro: isIntro,
            introProgress: introProgress,
            barProgress: Math.max(0, Math.min(1, barProgress)),
            textLive: songCurrentTime < bounds.last - bounds.first,
            elapsedText: fmtSongTime(Math.floor(songCurrentTime / 1000) * 1000),
            remainingText: fmtSongTime(bounds.last - time)
        };
    }

    // SongProgressInfo.formatTime: a sign, whole minutes and zero-padded seconds of the magnitude,
    // both truncated (TimeSpan.Duration().TotalMinutes floored, .Seconds).
    function fmtSongTime(ms) {
        const abs = Math.abs(ms);
        const minutes = Math.floor(abs / 60000);
        const seconds = Math.floor(abs / 1000) % 60;
        return (ms < 0 ? '-' : '') + minutes + ':' + (seconds < 10 ? '0' : '') + seconds;
    }

    // THE HP BAR (backlog 306). The fill is the engine's health account (engine.health, the port of
    // TypeBeatHealthProcessor's Health), and the danger tint comes on below LOW_HEALTH_THRESHOLD,
    // the fraction the desktop's FailingLayer calls low health. Only the tint is borrowed: that
    // layer's red vignette never shows for a processor with no passive drain, which this one is, so
    // the browser adds none either. It used to read the rejection streak, which no /play key can
    // grow any more, so the bar sat full through a run the desktop would fail.
    const LOW_HEALTH_THRESHOLD = 0.2;       // FailingLayer.low_health_threshold

    function healthBar(health) {
        const clamped = Math.min(1, Math.max(0, health));
        return { widthPct: clamped * 100, danger: clamped < LOW_HEALTH_THRESHOLD };
    }

    // The start gate's one-line brief. The HP clause describes the account the play really runs on
    // (see healthBar): it used to promise a 13-wrong-keys fail, the rejection model's mash guard,
    // which a /play run cannot reach.
    const START_GATE_HINT = 'type the lyrics as they are sung · the blue underline is the vocal, the bar under the next line counts you in · '
        + 'the thin bar under the stats is your health: characters typed right refill it, missed characters, typos and skipped words drain it, and the run fails if it empties';

    // THE SPACE ERROR DOT (backlog 316, the desktop's backlog 197 marker). A user setting on the
    // desktop that has shipped ON since PR 2 (TypeBeatRulesetConfigManager's SpaceErrorDots
    // default, pinned by ConfigDefaultsTest). /play has no settings surface, so it takes that
    // shipped default unconditionally, the move backlogs 198 and 307 made for the engine settings.
    const SPACE_ERROR_DOTS_ENABLED = true;
    // LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / _SCALE: the bounce every typo landing on a gap
    // plays on that gap's dot, so a mashed space still acknowledges each press.
    const SPACE_ERROR_DOT_PULSE_MS = 150;
    const SPACE_ERROR_DOT_PULSE_SCALE = 0.5;
    // LyricLineDisplay.PlayJudgementFeedback's WrongChar shake: 2 px left over 25 ms, 2 px right
    // over 25 ms, home over 15 ms. The keyframes live on .tb-c-shake in site.css; this is how long
    // the class stays on.
    const TYPO_SHAKE_MS = 65;

    /// A WORD GAP (LyricLineDisplay.IsWordGap, the engine's own isWordGap): the typeable space cell
    /// between two words. Punctuation is not typeable, so it rides inside its word.
    function isWordGap(cell) {
        return cell.typeable && cell.expected === ' ';
    }

    /// LyricLineDisplay.ComputeSpaceErrorDots, one flag per cell, pure. Since PR 3 a gap is dotted
    /// only when it holds a wrong character of its OWN (state wrong, with a typed character): a typo
    /// inside the word before it no longer dots a correctly typed space, and a skipped word leaves
    /// no dot on the space the skip passed. The word-flaw half of the old rule is gone on both
    /// sides.
    function spaceErrorDots(cells) {
        const into = new Array(cells.length);
        for (let i = 0; i < cells.length; i++) {
            const cell = cells[i];
            into[i] = isWordGap(cell) && cell.state === 'wrong' && cell.typedChar !== null && cell.typedChar !== undefined;
        }
        return into;
    }

    /// LyricLineDisplay.SpaceErrorDotPulseScale: 1 at rest and at both ends of the pulse,
    /// 1 + SPACE_ERROR_DOT_PULSE_SCALE at its midpoint, a half sine between.
    function spaceErrorDotPulseScale(elapsedMs) {
        if (!(elapsedMs > 0) || elapsedMs >= SPACE_ERROR_DOT_PULSE_MS) return 1;
        return 1 + SPACE_ERROR_DOT_PULSE_SCALE * Math.sin(Math.PI * (elapsedMs / SPACE_ERROR_DOT_PULSE_MS));
    }

    /// The class list for a cell's span. Pure (state in, string out).
    function cellClass(cell, isCaret, popping, shaking, inSung) {
        let cls = 'tb-c';
        if (cell.state === 'correct') {
            const jt = cell.judgeType;
            // Typed but off-time (Premature/Lagging) scores nothing and pays the most accuracy a
            // judged cell can pay (backlog 199 made it a Meh rather than a Miss, so it no longer
            // breaks the run); desktop draws it like any other correct char, the browser keeps a
            // distinct warn tint as a free hint. The RENDERING is untouched by 199: the two tiers
            // are still their own judgement types, which is exactly where the distinction the
            // statistics blob gives up on survives.
            //
            // A press out past the RUSH CAP took the same warn tint from backlog 347 (it was awarded
            // Meh for where the caret was, marked judgedPastRushCap) until PR 3 removed the cap from
            // every live run. No browser cell can carry that mark any more, so the tier alone
            // decides: a Meh is a Meh the clock struck.
            const onTime = (jt === 'Great' || jt === 'Ok' || jt === 'Meh');
            cls += onTime ? ' tb-c-hit' : ' tb-c-off';
        } else if (cell.state === 'wrong') {
            // Typed through wrong (the default model). The desktop shows the EXPECTED glyph in
            // error red on a LYRIC cell, not the char that was pressed, so only the colour changes
            // there. A wrong WORD GAP draws the space error dot and a BLANK glyph instead (see
            // cellGlyph and spaceErrorDots, mirroring LyricLineDisplay.GapGlyph with the dot ON,
            // the shipped default); the typed letter is what the dot-OFF presentation drew there,
            // because a space painted red is nothing at all. Both keep tb-c-wrong.
            //
            // The GAP keeps a second, additive class (backlog 185), the desktop's WRONG_GAP_ALPHA
            // lane. With the dot on its glyph is blank, so the dim has nothing left to act on, and
            // the dot is drawn on an overlay of its own rather than inside this span, so the dim
            // cannot reach it either. It stays because it is the desktop's state alpha for the
            // cell. A wrong LYRIC cell is deliberately left at full strength: it is showing its OWN
            // character, so it takes no space away, and the desktop dims the same lane for the
            // same reason.
            cls += ' tb-c-wrong';
            // Strictly expected === ' ', not "the typed char is a space": a mid-word space typo is
            // a wrong LYRIC cell showing its own letter in red, and must not be dimmed.
            if (cell.expected === ' ') cls += ' tb-c-wrong-gap';
        } else if (cell.state === 'missed') {
            cls += ' tb-c-miss';
        } else if (cell.state === 'abandoned') {
            // Given up to a word skip (backlog 167) and not yet lost: one backspace, or a Ctrl+A,
            // re-opens it. The desktop draws it at ABANDONED_ALPHA (0.7), between full untyped
            // brightness and the missed 0.4, so the damage a stray space did reads as reclaimable
            // rather than as nothing or as a miss. It keeps the untyped colour (tb-c-todo, and the
            // current line's todo shade with it) and tb-c-abandoned adds the alpha on top.
            cls += ' tb-c-todo tb-c-abandoned';
        } else {
            cls += ' tb-c-todo';
            // The SUNG-SYLLABLE HIGHLIGHT (backlog 317, LyricLineDisplay.CellFillColour): an UNTYPED
            // cell of the group the vocal is inside lifts to the lighter SungChar grey. Untyped only,
            // since the highlight is for characters that can still be typed on time (abandoned,
            // missed and auto-skipped cells stay put), and never a freestyle slot, whose violet is
            // an identity that nothing repaints.
            if (inSung && cell.state === 'untyped' && !cell.freestyle) cls += ' tb-c-sung';
        }
        // A FREESTYLE cell never shows the authoring marker: while it is still open it
        // shimmers through the glyph pool (the desktop client's exact sequence), and once
        // filled it freezes on the char the player actually pressed, so a finished line still
        // shows which slots were free. tb-c-free colours it in both states.
        if (cell.freestyle) cls += ' tb-c-free';
        if (isCaret) cls += ' tb-c-at';
        if (popping) cls += ' tb-c-pop';
        // A typo that just LANDED on this cell (engine.onTypoLanded): the desktop's 2 px WrongChar
        // shake, run by .tb-c-shake's keyframes for TYPO_SHAKE_MS.
        if (shaking) cls += ' tb-c-shake';
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
    /// all, so the state needs a mark of its own. Which mark is LyricLineDisplay.GapGlyph's rule
    /// (backlog 316): a DOTTED gap (`dotted`, spaceErrorDots' flag for this cell) is BLANK, because
    /// the space error dot drawn over it is the whole mark and the typed letter would stack a
    /// second one in the same slot; an undotted wrong gap shows the TYPED character, the dot-OFF
    /// presentation. The dot is on for every /play run (SPACE_ERROR_DOTS_ENABLED) and a wrong gap
    /// always earns it, so the typed letter is only reached by a caller that passes no flag. A gap
    /// in any other state, the typo once backspaced included, is a space again.
    ///
    /// Layout does not move with it, which is the desktop's rule reached by a different road: there
    /// the advances were measured once at load, here the lyric stack is set in JetBrains Mono (see
    /// --font-display), so the letter occupies exactly the advance the nbsp did and no cell after it
    /// moves. Re-measuring is not even reached: measureRow() runs on a line change and a resize,
    /// never on a keypress, so a reflow here would silently unregister the caret, the sweep and the
    /// cue bars from the glyphs rather than move them with it.
    function cellGlyph(cell, dotted) {
        if (cell.expected === ' ' && SPACE_ERROR_DOTS_ENABLED && dotted) return ' ';
        return cell.expected === ' ' && cell.state === 'wrong' && cell.typedChar !== null
            ? cell.typedChar
            : cell.expected;
    }

    // ---------------------------------------------------------------------------
    // Syllable and pace reading aids (backlog 317). Pure, no DOM; exported on Core.display.
    // ---------------------------------------------------------------------------

    /// The SUNG-SYLLABLE HIGHLIGHT's feed (mirrors LyricStage.currentSyllableIn): the index of the
    /// group of `line` whose [startTime, endTime] span contains `time`, or -1 between spans (and over
    /// a stylised token, which owns no group). Since backlog 363 a word the map does not subdivide is
    /// ONE group over its unit, so it lights whole, exactly as the editor shows it. The desktop feeds this every frame under every
    /// playhead style, and the untyped, non-freestyle cells of that group lift to SungChar
    /// (LyricLineDisplay.CellFillColour); cellClass() is where the lift lands here.
    function currentSyllableIn(line, time) {
        const groups = line ? line.syllables : null;
        if (!groups) return -1;
        for (let g = 0; g < groups.length; g++) {
            if (time >= groups[g].startTime && time <= groups[g].endTime) return g;
        }
        return -1;
    }

    // THE UNDERLINE PACE HUE (backlog 228 on the desktop, 317 here), a port of UI/UnderlinePace.cs.
    // The rail under a line is cut into one band per WORD (the word gap that closes it included),
    // and since PR 3 per syllable subdivision too (a word is cut again at each of its
    // syllableMarkerCells). Each band is tinted by its speed in countable cells per millisecond, in
    // one of two modes. The RELATIVE mode (UnderlinePace.BuildRelativeBands, the one the desktop's
    // LyricStage draws since PR 3 and therefore what /play draws) compares each band with the band
    // before it, across line breaks: a DEFAULT_MAX_CHANGE_PERCENT rise reaches the full error red, the
    // same fall the full slow green. The WHOLE-MAP mode (UnderlinePace.BuildBands, the pre-PR 3 draw,
    // kept for the mirror) ranks every band against the map: the middle half stays the neutral rail,
    // the fast quartile shades red and the slow one green. Both lift from 0.20 to 0.34 alpha. Display
    // only; nothing judges, scores or submits off it. Pinned against both C# builders in WireCompat.
    const PACE_NEUTRAL_ALPHA = 0.20;      // UnderlinePace.NEUTRAL_ALPHA
    const PACE_HUED_ALPHA = 0.34;         // UnderlinePace.HUED_ALPHA
    const PACE_NEUTRAL_LO_RANK = 0.25;    // UnderlinePace.NEUTRAL_LO_RANK
    const PACE_NEUTRAL_HI_RANK = 0.75;    // UnderlinePace.NEUTRAL_HI_RANK
    const PACE_MIN_SEGMENT_SPAN_MS = 30;  // UnderlinePace.MIN_SEGMENT_SPAN_MS
    // UnderlinePace.DEFAULT_MAX_CHANGE_PERCENT, the desktop's PaceColourMaxChange setting default.
    // /play has no settings surface, so it takes the default, as it does every display setting.
    const PACE_DEFAULT_MAX_CHANGE_PERCENT = 100;
    // TypeBeatStyle.SungAccent (#7ec8e3), ErrorChar (#ca4754) and PaceSlowAccent (#6ed26e), as the
    // desktop's byte-constructed Color4s: channels in [0, 1].
    const PACE_SUNG_ACCENT = { r: 126 / 255, g: 200 / 255, b: 227 / 255 };
    const PACE_FAST_END = { r: 202 / 255, g: 71 / 255, b: 84 / 255 };
    const PACE_SLOW_END = { r: 110 / 255, g: 210 / 255, b: 110 / 255 };

    // osu.Framework's Color4Extensions.ToLinear / ToSRGB (gamma 2.4 with the linear toe), which is
    // what Interpolation.ValueAt blends a colour through.
    function srgbToLinear(c) {
        return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }

    function linearToSrgb(c) {
        return c < 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055;
    }

    /// UnderlinePace.ColourForRank: the band colour { r, g, b, a } (channels in [0, 1]) for a
    /// map-wide percentile rank. Neutral (the pre-hue rail, exactly) inside [p25, p75] inclusive,
    /// otherwise a linear-light blend from the sung accent toward the end colour, linear in RANK,
    /// with the alpha lifted linearly alongside. Exact at t = 1; a NaN rank is neutral.
    function paceColourForRank(percentileRank) {
        const r = isNaN(percentileRank) ? 0.5 : Math.min(1, Math.max(0, percentileRank));
        if (r >= PACE_NEUTRAL_LO_RANK && r <= PACE_NEUTRAL_HI_RANK) {
            return { r: PACE_SUNG_ACCENT.r, g: PACE_SUNG_ACCENT.g, b: PACE_SUNG_ACCENT.b, a: PACE_NEUTRAL_ALPHA };
        }
        const fast = r > PACE_NEUTRAL_HI_RANK;
        const t = fast
            ? (r - PACE_NEUTRAL_HI_RANK) / (1 - PACE_NEUTRAL_HI_RANK)
            : (PACE_NEUTRAL_LO_RANK - r) / PACE_NEUTRAL_LO_RANK;
        const end = fast ? PACE_FAST_END : PACE_SLOW_END;
        if (t >= 1) return { r: end.r, g: end.g, b: end.b, a: PACE_HUED_ALPHA };
        // Interpolation.ValueAt(t, start, end, 0, 1): t = 0 is the start colour untouched, anything
        // else is blended channelwise in linear light and brought back to sRGB.
        const blend = function (a, b) {
            if (t === 0) return a;
            const la = srgbToLinear(a);
            return linearToSrgb(la + t * (srgbToLinear(b) - la));
        };
        return {
            r: blend(PACE_SUNG_ACCENT.r, end.r),
            g: blend(PACE_SUNG_ACCENT.g, end.g),
            b: blend(PACE_SUNG_ACCENT.b, end.b),
            a: PACE_NEUTRAL_ALPHA + (PACE_HUED_ALPHA - PACE_NEUTRAL_ALPHA) * t
        };
    }

    /// UnderlinePace.ColourForPreviousSpeed: the RELATIVE mode's band colour, from the change against
    /// the band before. At the default threshold a 100% rise reaches the red end and a 100% fall the
    /// green end, both through paceColourForRank's ramps. The first band (no previous) is neutral; a
    /// positive speed after a zero-speed band is fully red. The threshold is clamped to [25, 150]
    /// percent, and a non-finite one falls back to the default.
    function paceColourForPreviousSpeed(speed, previousSpeed, maxChangePercent) {
        if (previousSpeed === null || previousSpeed === undefined) return paceColourForRank(0.5);
        if (previousSpeed <= 0) return speed > 0 ? paceColourForRank(1) : paceColourForRank(0.5);
        const max = maxChangePercent === undefined ? PACE_DEFAULT_MAX_CHANGE_PERCENT : maxChangePercent;
        const change = (speed - previousSpeed) / previousSpeed;
        const threshold = isFinite(max)
            ? Math.min(150, Math.max(25, max)) / 100
            : PACE_DEFAULT_MAX_CHANGE_PERCENT / 100;
        if (change > 0) {
            return paceColourForRank(PACE_NEUTRAL_HI_RANK + Math.min(change / threshold, 1) * (1 - PACE_NEUTRAL_HI_RANK));
        }
        if (change < 0) {
            return paceColourForRank(PACE_NEUTRAL_LO_RANK - Math.min(-change / threshold, 1) * PACE_NEUTRAL_LO_RANK);
        }
        return paceColourForRank(0.5);
    }

    /// UnderlinePace.firstVocalTime: the first TYPEABLE cell's target in [from, toExclusive), else
    /// the range's first cell's (0 past the end).
    function paceFirstVocalTime(cells, from, toExclusive) {
        for (let i = from; i < toExclusive; i++) {
            if (cells[i].typeable) return cells[i].target;
        }
        return from < cells.length ? cells[from].target : 0;
    }

    /// UnderlinePace.segmentLine: one { startCell, endCellExclusive, speed } per word, a segment
    /// running through the word gap that closes it, cut again at every subdivision start inside the
    /// line (PR 3: the caller passes the line's syllableMarkerCells, as UnderlinePace.SegmentLine(line)
    /// passes TypingLine.SyllableMarkerCells; omitted, the cut is per word only). Its span reaches
    /// the NEXT segment's first vocal target (the line's sung end for the last), and its speed is in
    /// COUNTABLE cells (typeable, not a space) per millisecond with the span floored at
    /// PACE_MIN_SEGMENT_SPAN_MS.
    function paceSegmentLine(cells, lineSungEndMs, subdivisionStarts) {
        const n = cells.length;
        if (n === 0) return [];
        const starts = [0];
        for (let i = 0; i < n; i++) {
            if (isWordGap(cells[i]) && i + 1 < n) starts.push(i + 1);
        }
        for (const start of subdivisionStarts || []) {
            if (start > 0 && start < n) starts.push(start);
        }
        // starts.Sort() then the adjacent-duplicate sweep: a NUMERIC sort, the JS default being
        // lexicographic.
        starts.sort(function (a, b) { return a - b; });
        for (let i = starts.length - 1; i > 0; i--) {
            if (starts[i] === starts[i - 1]) starts.splice(i, 1);
        }
        const result = new Array(starts.length);
        for (let k = 0; k < starts.length; k++) {
            const start = starts[k];
            const endExclusive = k + 1 < starts.length ? starts[k + 1] : n;
            const startTime = paceFirstVocalTime(cells, start, endExclusive);
            const endTime = k + 1 < starts.length
                ? paceFirstVocalTime(cells, endExclusive, k + 2 < starts.length ? starts[k + 2] : n)
                : lineSungEndMs;
            let countable = 0;
            for (let i = start; i < endExclusive; i++) {
                if (cells[i].typeable && cells[i].expected !== ' ') countable++;
            }
            let span = endTime - startTime;
            if (!(span >= PACE_MIN_SEGMENT_SPAN_MS)) span = PACE_MIN_SEGMENT_SPAN_MS;
            result[k] = { startCell: start, endCellExclusive: endExclusive, speed: countable / span };
        }
        return result;
    }

    /// UnderlinePace.RanksOf: the MID-RANK percentile of each speed in the whole set (count strictly
    /// below plus half the count equal, over the total), so ties share a rank and a uniformly paced
    /// map ranks every segment at 0.5.
    function paceRanksOf(speeds) {
        const n = speeds.length;
        const ranks = new Array(n).fill(0);
        if (n === 0) return ranks;
        const order = new Array(n);
        for (let i = 0; i < n; i++) order[i] = i;
        order.sort(function (a, b) { return speeds[a] < speeds[b] ? -1 : speeds[a] > speeds[b] ? 1 : 0; });
        let at = 0;
        while (at < n) {
            let last = at;
            while (last + 1 < n && speeds[order[last + 1]] === speeds[order[at]]) last++;
            const rank = (at + last + 1) * 0.5 / n;
            for (let k = at; k <= last; k++) ranks[order[k]] = rank;
            at = last + 1;
        }
        return ranks;
    }

    /// UnderlinePace.buildBands, the whole-map precompute: one array of { startCell,
    /// endCellExclusive, colour } per line. Each line's last segment closes on the END of its sung
    /// polyline (UnderlinePace.SungEndOf reads TypingLine.SweepEndTime, which is that anchor), taken
    /// from buildSungPoints so the band and the fill drawn over it cannot drift apart, and each line
    /// is cut at its own syllableMarkerCells as well as its word gaps. `relativeToPrevious` picks the
    /// colour: each band against the band before it, across line breaks
    /// (paceColourForPreviousSpeed), or its map-wide rank (paceColourForRank). Run ONCE per map,
    /// beside sungPoints; never per frame.
    function paceBandsOf(lines, sungPointsPerLine, relativeToPrevious, maxChangePercent) {
        const perLine = new Array(lines.length);
        const speeds = [];
        for (let k = 0; k < lines.length; k++) {
            const points = sungPointsPerLine ? sungPointsPerLine[k] : buildSungPoints(lines[k]);
            perLine[k] = paceSegmentLine(lines[k].cells, points[points.length - 1].t, lines[k].syllableMarkerCells);
            for (const segment of perLine[k]) speeds.push(segment.speed);
        }
        const ranks = relativeToPrevious ? [] : paceRanksOf(speeds);
        let at = 0;
        let previousSpeed = null;
        return perLine.map(function (segments) {
            return segments.map(function (segment) {
                const colour = relativeToPrevious
                    ? paceColourForPreviousSpeed(segment.speed, previousSpeed, maxChangePercent)
                    : paceColourForRank(ranks[at]);
                previousSpeed = segment.speed;
                at++;
                return {
                    startCell: segment.startCell,
                    endCellExclusive: segment.endCellExclusive,
                    colour: colour
                };
            });
        });
    }

    /// UnderlinePace.BuildRelativeBands at DEFAULT_MAX_CHANGE_PERCENT: what the desktop's LyricStage
    /// draws since PR 3, and therefore what /play draws.
    function buildPaceBands(lines, sungPointsPerLine) {
        return paceBandsOf(lines, sungPointsPerLine, true, PACE_DEFAULT_MAX_CHANGE_PERCENT);
    }

    /// UnderlinePace.BuildBands, the whole-map percentile mode the desktop drew before PR 3. Not
    /// drawn by /play; kept, and pinned, because the C# keeps it.
    function buildRankedPaceBands(lines, sungPointsPerLine) {
        return paceBandsOf(lines, sungPointsPerLine, false);
    }

    /// A band colour as a CSS rgba() string.
    function paceColourCss(c) {
        const byte = function (v) { return Math.round(Math.min(1, Math.max(0, v)) * 255); };
        return 'rgba(' + byte(c.r) + ', ' + byte(c.g) + ', ' + byte(c.b) + ', ' + c.a.toFixed(3) + ')';
    }

    // Which line the cue-in bars belong to (mirrors LyricStage.updateApproachCue). A line
    // activates at the very moment its cue window opens, so in a continuous map the PREVIOUS
    // line is still active and carries the cue; but after a gap the line self-activates with
    // nobody before it, and while its own first word (first TYPEABLE cell) is still ahead the cue
    // is its own.
    function cueTargetLine(lines, activeLineIndex, nextSealIndex, time) {
        if (activeLineIndex < 0) return nextSealIndex;
        const active = lines[activeLineIndex];
        const activeFirst = firstTypeableIndex(active);
        const inOwnLeadIn = activeFirst >= 0 && active.cells[activeFirst].target > time;
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
    // The rule, exactly (backlog 223, re-based on the SONG's own times by backlog 318 to follow the
    // desktop's PR 2): start at the seal cursor (nextUnsealedLineIndex), step BACK to whatever line
    // the song has not finished, then walk FORWARD off every line the song has left, both loops
    // judging a line by songWindowClosesAt. They cannot fight: neither moves past a line whose own
    // song window is still open.
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
    // The forward walk used to step on endTime + sealGraceMs, the TYPING deadline, and that was the
    // defect 318 fixes: a line with any seal grace (authored, derived from a word overrun, or the
    // boundary bump) kept the sung row for that long after the next line had started singing, so the
    // sweep and the sung caret sat on the previous row and then opened part way along the next one,
    // as if it had begun mid-word. It now steps where the song leaves the line, so on an ordinary
    // hand-over it fires at the boundary, while a line still inside its grace keeps the cursor.
    //
    // The STEP BACK is the other half of the desktop's rule: there a player who types an OVERRUN
    // line out early seals it at its own boundary while the song is still singing its tail, and
    // taking the cursor at its word would blank the sweep that is running. It is ported literally
    // but is inert on /play: both loaders clamp every word's end into its line, so a line's sweep
    // never ends after its endTime, songWindowClosesAt is exactly endTime, and no line can seal
    // before its endTime passes.
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

        while (songLine > 0 && nowMs < songWindowClosesAt(lines[songLine - 1])) songLine--;

        while (songLine + 1 < lines.length && nowMs >= songWindowClosesAt(lines[songLine])) songLine++;

        return songLine;
    }

    // The instant the playhead leaves `line` (mirrors LyricStage.songWindowClosesAt): the LATER of
    // the line's own boundary and the moment its sweep reaches its last character (the tail of
    // buildSungPoints, TypingLine.SweepEndTime), which runs past the boundary only when the line's
    // vocals genuinely overrun it. NOT the line's typing deadline: a seal grace is time the PLAYER is
    // still allowed to type the line in, and the song's line answers to the song's times alone.
    function songWindowClosesAt(line) { return Math.max(line.endTime, sweepEndTimeOf(line)); }

    // TypingLine.SweepEndTime: the time of the sung polyline's closing anchor, so the one formula
    // for it lives in buildSungPoints.
    function sweepEndTimeOf(line) {
        const points = buildSungPoints(line);
        return points[points.length - 1].t;
    }

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

    // Where the gameplay clock STARTS, in map time: MasterGameplayClockContainer's
    // findEarliestStartTime over DrawableRuleset.GameplayStartTime (the first hit object less 2000,
    // and the first hit object is line 0, so its startTime, not its first vocal). That is
    // min(0, line0.startTime - 2000), widened by the map's AudioLeadIn when it has one. /play has no
    // storyboard, so the desktop's storyboard term has nothing to contribute. A map whose first
    // line starts inside 2 s therefore runs its clock through silent negative time first, with the
    // stage and the cue on screen, exactly as the desktop's pre-roll does.
    function gameplayStartTime(beatmap) {
        const lines = beatmap && beatmap.lines;
        if (!lines || lines.length === 0) return 0;
        const first = lines[0].startTime;
        let time = Math.min(0, first - 2000);
        const leadIn = beatmap.audioLeadIn || 0;
        if (leadIn > 0) time = Math.min(time, first - leadIn);
        return time;
    }

    // The INTRO skip, which is NOT an InstrumentalGaps gap: on the desktop it is the separate intro
    // SkipOverlay, landing at MasterGameplayClockContainer.Skip's GameplayStartTime -
    // MINIMUM_SKIP_TIME, i.e. (line0.startTime - 2000) - 1000. So: the first LINE'S START less
    // SKIP_LEAD_MS. That is not the first vocal less SKIP_LEAD_MS (what this used to compute, and
    // what backlog 230 assumed was the same thing): a first word sung more than 3 s after its line's
    // stamp put the old target past line 0's start, inside drain_length_s, so the skip removed drain
    // time the play-time gate counts and an honest play could be stored unranked. Anchored on the
    // line start it lands before drain_length_s begins, so it removes only run-up the gate never
    // asked for, and is gate-free by construction.
    //
    // null when the target is not after the clock start (`clockStart`, gameplayStartTime): the
    // desktop's intro SkipOverlay expires at once when its fadeOutBeginTime is not past the time it
    // was loaded at, which is the same rule. clockStart defaults to the map with no AudioLeadIn,
    // which is what every caller that hands over only the lines means.
    function introSkipTarget(lines, clockStart) {
        if (!lines || lines.length === 0) return null;
        const start = clockStart === undefined ? gameplayStartTime({ lines: lines }) : clockStart;
        const target = lines[0].startTime - SKIP_LEAD_MS;
        return target > start ? target : null;
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
    // a character. The desktop lets the key fall through to GlobalAction.SkipCutscene when no line
    // is active at all (outside the first line's head start), and when the active line
    // IsLineComplete with no live retype SELECTION (backlog 182: collapsing a selection re-opens the
    // cells it covers, so the key consuming it is a typing key again even though the line reads
    // complete). On an active INCOMPLETE line it swallows every typeable key with ONE exception,
    // which this predicate does not cover and routeKeyDown carries on its own: a Space on a caret
    // parked at the head of a line it has not touched, while the song is not on that line (see
    // spaceIsDropped). Everywhere else on an incomplete line a skip can never eat a live keystroke.
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

    // Whether Space is a skip right now rather than a character, on the desktop's own predicate.
    //
    // The FIRST LINE'S HEAD START (PR 2) counts as a live line here, as it does in
    // TypeBeatPlayfield's key handler (`!engine.LineIsActive && !engine.FirstLineTypingOpensAt`):
    // a press inside it opens the line and types, rather than falling through to the skip.
    // Unreachable as a skip today (the intro target sits SKIP_LEAD_MS before line 0's start, which
    // is never after its activation, so far outside the head start), and mirrored so the two
    // predicates cannot drift.
    function skipAllowedFor(engine, time, hasSelection) {
        const active = engine.activeLineIndex >= 0;
        const typing = active || engine.firstLineTypingOpensAt(time);
        return skipAllowed(typing, active && engine.isLineComplete(engine.activeLineIndex), hasSelection);
    }

    // The skip target live at `time`, or null. Kept apart from the chip so the key path and the
    // button path answer to exactly one rule.
    function pendingSkipTargetFor(engine, gaps, introTarget, time) {
        if (engine.finished || engine.failed) return null;

        const active = engine.activeLineIndex >= 0;
        const upcoming = upcomingLineIndex(
            engine.activeLineIndex, active && engine.isLineComplete(engine.activeLineIndex), engine.nextSealIndex);

        if (upcoming < 0 || upcoming >= engine.lines.length) return null;

        return skipTargetAt(gaps, introTarget, upcoming, time);
    }

    // ---------------------------------------------------------------------------
    // THE KEY HANDLER, as a pure router (backlog 305). mountPlayer's keydown listener is a thin
    // wrapper around routeKeyDown; everything that decides what a press does lives here, over a
    // `host` that supplies the engine, the clock and the few pieces of player state the routing
    // reads (the retype selection, the skip gaps, the seek). That is what lets the display harness
    // send keydowns between fake ticks and the cross-repo parity test hold the result against the
    // desktop's key handler.
    // ---------------------------------------------------------------------------

    // C# Math.Round(double): MidpointRounding.ToEven, "banker's rounding". JS Math.round sends every
    // .5 up, so 2000.5 would be 2001 here and 2000 on the desktop, a millisecond apart on the very
    // value a press is judged at.
    function roundHalfEven(x) {
        const f = Math.floor(x);
        const d = x - f;
        if (d < 0.5) return f;
        if (d > 0.5) return f + 1;
        return f % 2 === 0 ? f : f + 1;
    }

    // TypeBeatKeyHandler.OnKeyDown's word-gesture resolution against the default bindings
    // (Ctrl+Backspace, Ctrl+A). The desktop's gesture container matches under
    // KeyCombinationMatchingMode.Any, so a binding is satisfied with EXTRA modifiers held: Alt, and
    // with it AltGr (which browsers report as Ctrl plus Alt), and Shift all still fire the gesture.
    // Meta is excluded where the desktop does not name it, because a Cmd combo on macOS is the
    // browser's own shortcut surface and gameplay must not start eating it.
    function isWordGesture(e) {
        if (!e.ctrlKey || e.metaKey) return false;
        return e.key === 'Backspace' || e.key === 'a' || e.key === 'A' || e.code === 'KeyA';
    }

    function isSpace(e) { return e.key === ' ' || e.code === 'Space'; }

    // KEYSTROKE TO CHARACTER (backlog 309), for every key that is not Space. The desktop maps
    // PHYSICAL positions through KeyCharMap under the player's KeyboardLayout setting; on the
    // non-Literate surface /play plays that means Shift and Caps change a letter's case and nothing
    // else, the digit row types digits whatever the modifier or layout, a dead-key position is
    // inert and the vowel after it types its base letter, and a non-Latin OS layout still types by
    // position. The browser reads the OS layout's own e.key instead, which is the better answer for
    // Dvorak, Colemak and a default-setting Azerty player, so it is kept as rule 1 and the
    // positional model is only borrowed where e.key has nothing typeable to say:
    //
    //   1. e.key itself, when it is already a letter or digit.
    //   2. After a DEAD keydown, the composed vowel folded to its base letter ('ê' to 'e'), which
    //      is right because the lyric is diacritic-folded on both sides (typebeat-core.js's
    //      normalizer). Without this the vowel was dropped and every key after it landed one cell
    //      early as a typo. Only on a LETTER position: a dead key followed by a key it cannot
    //      compose with reports that key's own character, and folding the QWERTZ 'ö' (Semicolon)
    //      or the Azerty 'ù' (Quote) or 'ç' (Digit9) there would type a letter where the desktop
    //      types nothing, or a digit.
    //   3. The digit row and keypad give their digit whatever e.key says: Shift+1 ('!'), an Azerty
    //      digit key unshifted ('é'), a keypad key with Num Lock off ('End'). KeyCharMap.cs's
    //      tryMapLower answers the digit for all of these, since Shift only cases letters.
    //   4. A letter POSITION gives its letter only when e.key is a letter outside Latin script (a
    //      Cyrillic or Greek layout), cased as e.key is. Never for Latin punctuation: the Azerty
    //      ',' on KeyM stays inert exactly as KeyCharMap keeps it inert, so a habitual comma is
    //      never a wrong key.
    //   5. Everything else is dropped (null), including the QWERTZ umlaut and eszett positions,
    //      which KeyCharMap leaves unmapped too.
    //
    // prevWasDead: whether the previous non-modifier keydown was a dead key (routeKeyDown tracks it
    // on the host).
    const DIGIT_CODE_RE = /^(?:Digit|Numpad)([0-9])$/;
    const LETTER_CODE_RE = /^Key([A-Z])$/;
    const NON_LATIN_LETTER_RE = /^(?=\p{L}$)\P{Script=Latin}$/u;

    function keyToChar(e, prevWasDead) {
        const key = typeof e.key === 'string' ? e.key : '';
        const code = typeof e.code === 'string' ? e.code : '';

        if (key.length === 1 && KEY_RE.test(key)) return key;

        const letter = LETTER_CODE_RE.exec(code);

        if (prevWasDead && letter && key.length === 1) {
            const folded = key.normalize('NFD').replace(/[̀-ͯ]/g, '');
            if (folded.length === 1 && KEY_RE.test(folded)) return folded;
        }

        const digit = DIGIT_CODE_RE.exec(code);
        if (digit) return digit[1];

        if (letter && NON_LATIN_LETTER_RE.test(key)) {
            const lower = letter[1].toLowerCase();
            return key !== key.toLowerCase() ? lower.toUpperCase() : lower;
        }

        return null;
    }

    // The keys that never end a dead-key sequence: pressing Shift (or AltGr, which Windows reports
    // as Control then AltGraph) between the dead key and its vowel is how a capital is composed.
    const MODIFIER_KEYS = new Set(['Shift', 'Control', 'Alt', 'AltGraph', 'Meta', 'CapsLock', 'OS']);

    // TypeBeatPlayfield's narrow Space carve-out for the UNPINNED caret: finishing a line parks the
    // caret at the head of the next one, and a habitual trailing space there must not be typed into
    // it (where it would skip the line's first word, break combo and arm the WPM clock early). Only
    // while the song is not on the caret's line and only before the player has started it; one
    // keystroke in, or once the song reaches the line, Space is a typing key again.
    function spaceIsDropped(engine) {
        return engine.fletcherEnabled && !engine.songIsOnTheCaretsLine && engine.activeLineUntouched;
    }

    // TypeBeatPlayfield's `engine.IsLineComplete && CurrentRetypeSelection is null` block under
    // ManualNewlines (backlog 307): the state in which a Space is offered to the engine as the
    // newline before anything may treat it as a skip. A live selection suspends it exactly as it
    // suspends the skip (the key consuming the selection is a typing key).
    function completeLineTakesNewline(engine, host) {
        return engine.manualNewlines && engine.activeLineIndex >= 0
            && engine.isLineComplete(engine.activeLineIndex) && !host.getSelection();
    }

    // TypeBeatKeyHandler.eraseBackTo. Erase back to target with ordinary processBackspace
    // calls: the same run of erases a player holding the plain key down would have made, which
    // is the whole point of composing the gesture rather than teaching the engine a wider one.
    //
    // The trailing check is defensive termination only. Every erase that reports a mutation
    // moves the caret back, but one that reclaimed abandoned cells at the head of a line can
    // land on 0 and be auto-skipped forward again, and a gesture must never spin.
    //
    // THE PARKED EXCEPTION IS NOT THAT CASE (PR 2): clearing a PARKED typo is a real mutation
    // of the cell the caret is on with the caret deliberately unmoved, so breaking there would
    // leave the rest of the selection standing. Read BEFORE the press, which is the only
    // iteration the guard has to let through; the next press steps back normally.
    //
    // No time is passed down: the engine's backspace takes none (it is judged at no instant), and
    // the desktop's time argument here only stamps the replay frame the browser does not write.
    // The press's time has already been applied by routeKeyDown's update.
    function eraseBackTo(engine, target) {
        while (engine.caretIndex > target) {
            const before = engine.caretIndex;
            const parked = engine.caretOnParkedTypo;
            if (!engine.processBackspace()) break;
            if (engine.caretIndex >= before && !parked) break;
        }
    }

    // TypeBeatKeyHandler.collapseSelection. Collapse a live retype selection: a mass backspace
    // to its anchor. Returns whether there was one to collapse, so the caller can tell "the
    // selection ate this key" from "there was nothing there". The selection is dropped BEFORE
    // the erases so the staleness check cannot race them.
    function collapseSelection(host) {
        const selection = host.getSelection();
        if (!selection) return false;
        const start = selection.startCell;
        host.setSelection(null);
        eraseBackTo(host.engine, start);
        return true;
    }

    // TypeBeatPlayfield's key handler (TypeBeatKeyHandler.OnKeyDown), for one keydown.
    //
    // host: {
    //   engine,                 the live TypingEngine
    //   now(),                  the audio clock in (fractional) ms
    //   gaps, introTarget,      the map's skip windows (computeGaps / introSkipTarget)
    //   getSelection(), setSelection(sel),   the retype selection (backlog 182)
    //   performSkip(target)     the seek
    //   prevWasDead             written here: whether the last non-modifier keydown was a dead
    //                           key (keyToChar's rule 2)
    // }
    function routeKeyDown(e, host) {
        const engine = host.engine;

        // Read and advance the dead-key state FIRST, before any branch can return, so every
        // non-modifier keydown (a gesture, an Enter, a repeat, a dropped key) ends a sequence.
        const prevWasDead = !!host.prevWasDead;
        if (!MODIFIER_KEYS.has(e.key)) host.prevWasDead = e.key === 'Dead';

        // The two word-level gestures the player owns (backlog 182), and Enter's line skip, are
        // carved out BEFORE the modifier fall-through: every other Ctrl/Alt/Meta combo is left to
        // the browser. Enter reaches its gesture under Ctrl or Alt because the desktop's SkipLine
        // binding matches with KeyCombinationMatchingMode.Any (TypeBeatInputManager); Meta stays
        // excluded for the reason isWordGesture gives (backlog 283).
        const wordGesture = isWordGesture(e);
        const enter = e.key === 'Enter' && !e.metaKey;
        if ((e.ctrlKey || e.altKey || e.metaKey) && !wordGesture && !enter) return;

        // THE KEYSTROKE PROTOCOL (backlog 20's replay-determinism contract, which the desktop has
        // always followed and the browser now does too): quantise the press to whole milliseconds
        // ONCE, with the desktop's rounding, and advance the engine to that instant BEFORE any gate
        // or judgement reads it. Without this every decision below was taken against engine state
        // last advanced by the render tick, so anything that fell due between that frame and the
        // press (a seal, a drag cutoff, a rush snap, a line's activation) was applied AFTER the
        // key: a press was judged on a line the desktop had already sealed, lost against a caret
        // the desktop had already moved on, or dropped in a dead zone the desktop had already left.
        //
        // A rounded time can sit up to 0.5 ms under the last tick; both engines clamp the WPM
        // clock's accrual at zero for that, and nothing else reads a backwards step.
        const t = roundHalfEven(host.now());
        engine.update(t);

        // Backspace, gated exactly as TypeBeatPlayfield's key handler gates it: erasing only
        // ever has something to undo where a wrong char can land, so it reads the engine's
        // allowWrongInput flag rather than a rule of its own. That flag is on for every browser
        // play (the browser has no mods payload and so can never be Gatekeeper), which means
        // backspace is LIVE here. Under Gatekeeper the one thing an erase key still does is
        // consume a live SELECTION (backlog 244), which is why the gate reads it too. The key is
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
            if (!engine.allowWrongInput && !host.getSelection()) return;
            if (!collapseSelection(host)) {
                if (wordGesture) eraseBackTo(engine, engine.wordBackspaceTarget);
                else engine.processBackspace();
            }
            return;
        }

        if (wordGesture) {
            // CTRL+A: offer the run back to the earliest unfixed mistake for retyping (backlog
            // 184 widened it from the nearest one, so one press offers every mistake).
            // preventDefault because the browser's own Ctrl+A selects the whole page. NOT gated
            // on allowWrongInput, unlike the erase above: backlog 244 is where that stopped being
            // the same question, since a word skip is orthogonal to the input model and can leave
            // abandoned cells behind under Gatekeeper even with no typo possible.
            e.preventDefault();

            const anchor = engine.retypeSelectionAnchor;

            // No typo behind the caret: a genuine no-op, nothing to select and nothing to clear
            // (a selection can only exist where the query just answered). Pressing it again with
            // one already open simply recomputes the same range.
            if (anchor >= 0) {
                host.setSelection({ lineIndex: engine.activeLineIndex, startCell: anchor, endCell: engine.caretIndex });
            }
            return;
        }

        // ENTER GIVES UP THE REST OF THE LINE (backlog 241), the desktop's
        // TypeBeatAction.SkipLine on its default key. One engine call, which parks the caret
        // past the last cell and lets the roll or the snap carry it onward; the cells left
        // behind are judged by the seal at the line's own deadline, exactly as they would be
        // for a player who just stopped typing.
        //
        // Placed above the repeat guard because the desktop's gesture branches sit above its
        // own (holding the key repeats there too).
        //
        // A live retype SELECTION is deliberately NOT collapsed first: collapsing erases back
        // to the anchor, and a player abandoning the line is not asking to unmake the
        // characters they got right. Moving the caret makes it stale and the render loop drops
        // it. Nothing is gated on allowWrongInput either, unlike the two erasing gestures: a
        // skip writes nothing into a cell.
        //
        // Only an EFFECTIVE press is swallowed, which is the desktop's swallow rule: on a caret
        // already parked (or a line typed out) the engine no-ops and the key is left to the
        // browser exactly as it was before. Nothing else on this page is listening for Enter
        // during play; the pre-play start gate is its own capturing listener and is removed
        // when play begins.
        if (enter) {
            if (engine.processEnter(t)) e.preventDefault();
            return;
        }

        // THE MANUAL NEWLINE'S SPACE (backlog 307, TypeBeatPlayfield's IsLineComplete arm under
        // ManualNewlines). On a finished line with no live selection, Space is LIVE input, the
        // newline, and it reaches the ENGINE before the skip below, in the desktop's order: the
        // parked-head drop first (a caret whose line reads untouched and is not the song's keeps
        // falling through to the skip, which is also where a SECOND Space after a hand-over goes),
        // then the engine, then the skip only for a press the engine refused. So the skip's
        // WPM-clock guard is kept: it still fires only from a line that reads complete after the
        // engine has declined the press. That desktop arm carries no repeat guard, so neither does
        // this one; a repeat that is dropped, or that the engine refuses, still ends here as before.
        if (isSpace(e) && !spaceIsDropped(engine) && completeLineTakesNewline(engine, host)) {
            if (engine.processKey(' ', t)) {
                e.preventDefault();
                return;
            }
        }

        if (e.repeat) return;

        if (isSpace(e)) {
            // THE PARKED-HEAD DROP (spaceIsDropped). The desktop returns the key unconsumed here, so
            // it reaches GlobalAction.SkipCutscene; the browser's counterpart is the seek, taken only
            // if a skip window is live, and otherwise the press is simply swallowed and does nothing.
            if (spaceIsDropped(engine)) {
                e.preventDefault();
                const target = pendingSkipTargetFor(engine, host.gaps, host.introTarget, t);
                if (target !== null) host.performSkip(target);
                return;
            }

            // SPACE AS THE SKIP KEY, on exactly the desktop's terms (see skipAllowed): only where
            // TypeBeatPlayfield's key handler would let the press fall through to
            // GlobalAction.SkipCutscene, and only while a skip window is actually live. Anywhere
            // else it falls straight into the typeable branch below and is a word-gap character,
            // which is the ONE thing that must not change: a skip that could fire mid-line would
            // both eat a keystroke and inject the skipped span into the WPM clock.
            if (skipAllowedFor(engine, t, host.getSelection() !== null)) {
                const target = pendingSkipTargetFor(engine, host.gaps, host.introTarget, t);
                if (target !== null) {
                    e.preventDefault();
                    host.performSkip(target);
                    return;
                }
            }
        }

        let ch = null;
        if (isSpace(e)) ch = ' ';
        else ch = keyToChar(e, prevWasDead);
        if (ch !== null) {
            e.preventDefault();
            // A retype selection is consumed FIRST, so this key lands on the anchor cell: mass
            // backspace, then the ordinary judged keypress. Space is not special here, nor is any
            // other typeable key: "collapse, then process normally" is the whole rule. The
            // desktop suspends its line-complete fall-through to the skip overlay while a
            // selection is live, and so does this file: skipAllowed() takes the selection, so a
            // key arriving over one is a typing key even on a line that reads complete, and
            // control has already fallen through to here.
            collapseSelection(host);
            engine.processKey(ch, t);
        }
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

    // ---------------------------------------------------------------------------
    // The first-clear Discord nudge (backlog 289), render half.
    // ---------------------------------------------------------------------------
    // The results card can carry one invitation to the project's Discord server. WHETHER it does is
    // not decided here: "has this browser been asked already" is localStorage and the invite itself
    // is a server-rendered constant on the /play stage root, so both belong to the host page. The
    // host hands mountPlayer a decision hook (opts.discordNudge), this layer consults it once per
    // results card and renders whatever it answers. That is what keeps the presentation layer free
    // of storage, so the display harness can drive the whole chain with no browser at all.

    // The answer, normalised: a URL string to link, or null for "show nothing".
    function nudgeUrlFor(hook, results) {
        if (typeof hook !== 'function') return null;
        let url;
        // A host that throws costs the player the nudge and nothing else. The card behind it is the
        // record of a play that already happened, and it still has to render.
        try { url = hook(results); } catch (e) { console.error(e); return null; }
        return typeof url === 'string' && url !== '' ? url : null;
    }

    function nudgeHtml(url) {
        return '<span class="tb-result-nudge-text">Did you enjoy playing? Then join the official Discord server</span>'
            + `<a class="tb-btn tb-btn-discord" href="${escapeHtml(url)}" target="_blank" rel="noopener">join discord</a>`;
    }

    // ---- the results card's content (backlog 320) ------------------------------
    // What the card says, as data, so the harness can pin it without a DOM; showResults only lays
    // it out. It mirrors the desktop results panel (ExpandedPanelMiddleContent and its statistics):
    //
    //  - The tier row is great / ok / meh / miss, and MISS FOLDS IN the uncorrected typos
    //    (counts.typos, the `good` statistic): a cell the song sealed holding a wrong character is a
    //    miss to the player, as it is on the desktop panel (TypeBeatRuleset's statistic mapping,
    //    backlog 213) and in the set page's score rows (ScoreRowModel's MissColumn). The separate
    //    'typos' figure is the other number, wrong KEYPRESSES (counts.mistypes, backlog 140).
    //  - Max combo reads 'N / total' against maximumStatistics.great (one per typeable cell, the
    //    most a run can reach), with the desktop ComboStatistic's PERFECT marker when they are equal.
    //  - A FAILED run shows the JUDGED-only accuracy (computeScore's accuracyJudged), which is what
    //    the desktop attaches to a failed score and what the server stores and the profile shows.
    //    The whole-map `accuracy` would read lower, since it charges every cell the fail left
    //    unplayed. A passed run judges every cell, so there the two are the same number.
    //  - The map's metadata: title and artist, stars, difficulty name, mapper and the play date.
    //    Anything the host did not hand over (a deep link has no stars, say) is left out, not faked.
    // Every browser play is nomod, so there is no mods cell.
    const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August',
        'September', 'October', 'November', 'December'];

    // The site's "0.0#" star format, as play.js's formatStars draws the difficulty pills.
    function fmtStars(stars) {
        const s = Number(stars || 0).toFixed(2);
        return s.charAt(s.length - 1) === '0' ? s.slice(0, -1) : s;
    }

    // The desktop's PlayedOnText wording ("played on 28 September 2026, 14:05"), in local time.
    function fmtPlayedOn(date) {
        if (!(date instanceof Date) || isNaN(date.getTime())) return null;
        const pad = (n) => (n < 10 ? '0' : '') + n;
        return 'played on ' + date.getDate() + ' ' + MONTHS[date.getMonth()] + ' ' + date.getFullYear()
            + ', ' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    function resultCells(results, meta) {
        const m = meta || {};
        const counts = results.counts;
        const judgedOnly = !results.passed && typeof results.accuracyJudged === 'number';
        const comboMax = results.maximumStatistics ? results.maximumStatistics.great : 0;
        const perfect = comboMax > 0 && results.maxCombo === comboMax;
        const hasStars = typeof m.stars === 'number' && isFinite(m.stars);
        return {
            stats: [
                { key: 'score', label: 'score', value: fmtInt(results.totalScore) },
                { key: 'typed', label: 'typed', value: fmtPct(results.completion) },
                { key: 'accuracy', label: 'accuracy', value: fmtPct(judgedOnly ? results.accuracyJudged : results.accuracy) },
                { key: 'combo', label: 'max combo', value: comboMax > 0 ? results.maxCombo + ' / ' + comboMax : results.maxCombo + 'x', perfect: perfect },
                { key: 'wpm', label: 'wpm', value: String(Math.round(results.wpm)) },
                { key: 'typos', label: 'typos', value: String(counts.mistypes) }
            ],
            tiers: [
                { key: 'great', label: 'great', value: String(counts.great) },
                { key: 'ok', label: 'ok', value: String(counts.ok) },
                { key: 'meh', label: 'meh', value: String(counts.meh) },
                { key: 'miss', label: 'miss', value: String(counts.miss + counts.typos) }
            ],
            meta: {
                title: m.title || '',
                artist: m.artist || '',
                stars: hasStars ? '★ ' + fmtStars(m.stars) : null,
                difficulty: m.difficulty || null,
                mapper: m.creator ? 'mapped by ' + m.creator : null,
                played: fmtPlayedOn(m.playedAt)
            }
        };
    }

    // The metadata block of the card: title, artist, then stars, difficulty and mapper on one line
    // and the play date under it. Lines with nothing to say are left out.
    function resultMetaHtml(meta) {
        const detail = [meta.stars, meta.difficulty, meta.mapper].filter(Boolean).map(escapeHtml).join(' · ');
        return (meta.title ? `<div class="tb-result-meta-title">${escapeHtml(meta.title)}</div>` : '')
            + (meta.artist ? `<div class="tb-result-meta-artist">${escapeHtml(meta.artist)}</div>` : '')
            + (detail ? `<div class="tb-result-meta-detail">${detail}</div>` : '')
            + (meta.played ? `<div class="tb-result-meta-played">${escapeHtml(meta.played)}</div>` : '');
    }

    // ---- the playback-validity veto (backlog 312) ------------------------------
    // A port of MasterGameplayClockContainer.checkPlaybackValidity: the gameplay clock (here the
    // audio clock, nowMs()) is held against wall time (performance.now()) frame by frame. Each
    // frame adds the clock's delta to one running total and the wall's delta to the other; once
    // the two totals disagree by more than PLAYBACK_DISCREPANCY_MS the frame is a DISCREPANCY and
    // the wall total re-seeds from the clock's. The desktop's `count++ > allowed` is kept exactly:
    // the check reads the count BEFORE incrementing, so the first ALLOWED_PLAYBACK_DISCREPANCIES + 1
    // discrepancies are tolerated and the next one trips it. A tripped run stays tripped.
    //
    // DISCONTINUITIES. A seek (startSourceAt: the start, the intro skip, every gap skip) moves the
    // clock on purpose, and a frame whose audio context is not 'running' (still resuming from the
    // start gate's gesture, or suspended/interrupted by the browser) is the desktop's
    // !GameplayClock.IsRunning. Both drop the baseline: the next frame only re-seeds, so neither
    // the jump nor the frozen stretch is ever measured. They do NOT clear the count; the desktop
    // never forgets a discrepancy within a play, and a seek that did would launder a stalled clock.
    //
    // HIDDEN TABS need no special case, and that is deliberate. A hidden tab loses rAF and its
    // interval backstop is throttled (to 1 s, and in Chrome's intensive mode to 1 min), but the
    // audio keeps playing, so the frame after the gap sees BOTH clocks jump by the same wall time
    // and the difference stays ~0. Pinned by PlaybackValidityTest.
    //
    // PAUSES (backlog 311) are a discontinuity at both ends: the pause drops the baseline, and so
    // does the resume, because a context resumed synchronously would otherwise hand the first
    // frame back a frozen clock delta against the whole paused wall span. Pinned by
    // PlayerPauseTest.
    const PLAYBACK_DISCREPANCY_MS = 300;
    const ALLOWED_PLAYBACK_DISCREPANCIES = 5;

    function makePlaybackValidity() {
        let lastClock = null, lastWall = null;
        let elapsedClock = 0, elapsedWall = null;
        const state = { valid: true, discrepancies: 0 };
        state.discontinuity = function () {
            lastClock = null;
            lastWall = null;
            elapsedWall = null;
        };
        state.observe = function (clockMs, wallMs, running) {
            if (!running) { state.discontinuity(); return state.valid; }
            if (lastClock === null) { lastClock = clockMs; lastWall = wallMs; return state.valid; }
            const clockDelta = clockMs - lastClock, wallDelta = wallMs - lastWall;
            lastClock = clockMs;
            lastWall = wallMs;
            elapsedClock += clockDelta;
            if (elapsedWall === null) elapsedWall = elapsedClock;
            else elapsedWall += wallDelta; // the desktop scales by GameplayClock.Rate; /play is 1.0
            if (Math.abs(elapsedClock - elapsedWall) > PLAYBACK_DISCREPANCY_MS) {
                if (state.discrepancies++ > ALLOWED_PLAYBACK_DISCREPANCIES) state.valid = false;
                elapsedWall = null;
            }
            return state.valid;
        };
        return state;
    }

    // ---- pause, focus-loss pause, retry and quit (backlog 311) -----------------
    // The desktop Player's pause surface, ported onto the audio clock:
    //
    //   Pause()                  audioCtx.suspend(). Suspending freezes currentTime, so nowMs()
    //                            freezes with it and needs no rebase; rAF and the backstop are
    //                            stopped, and every game key is ignored until resume.
    //   PauseCooldownDuration    PAUSE_COOLDOWN_MS of REAL time (performance.now()), measured from
    //                            the last pause, as the desktop measures it on the screen's own
    //                            clock since backlog 267. Resume is never gated.
    //   Resume()                 audioCtx.resume() from the key press or click itself. Instant:
    //                            this ruleset has no ResumeOverlay, so there is no countdown.
    //   PauseOnFocusLost         a hidden tab, a window blur and an AudioContext the browser
    //                            suspended or interrupted all pause, except that FOCUS loss is
    //                            waived while the clock is inside a skippable instrumental (the
    //                            desktop's BreakTracker.IsBreakTime exception, see
    //                            inSkippableInstrumental). A refused attempt is retried on every
    //                            tick, which is the desktop's Scheduler.AddOnce reschedule, so a
    //                            hide during the cooldown or a break still pauses once it can.
    //   HotkeyRetryOverlay       hold ` for HOLD_TO_CONFIRM_MS (UIHoldActivationDelay's default):
    //   HotkeyExitOverlay        a fresh begin() / Ctrl+` for the same hold: quit. Both DISCARD
    //                            the run; neither ever reaches onFinish, so nothing is submitted
    //                            (the owner's call: the desktop's quit submission is not mirrored).
    const PAUSE_COOLDOWN_MS = 1000;
    const HOLD_TO_CONFIRM_MS = 200;
    const PAUSE_HINT = 'esc to resume · hold ` to retry · hold ctrl+` to quit';

    // Whether a pause taken at wall time `wallMs` is refused by the cooldown of the last one.
    function pauseCooldownActive(lastPauseWallMs, wallMs) {
        return lastPauseWallMs !== null && wallMs < lastPauseWallMs + PAUSE_COOLDOWN_MS;
    }

    // The break exception, on TIME alone as the desktop's BreakTracker decides it: the intro run-up
    // before the intro skip target, and every qualifying gap from its start to its skip target.
    // Deliberately NOT pendingSkipTargetFor, which also asks whether the player is waiting on the
    // gap's line: a player still dragging on the line behind an instrumental is inside the break on
    // the desktop too, and a hide there is not a pause.
    function inSkippableInstrumental(gaps, introTarget, time) {
        if (introTarget !== null && introTarget !== undefined && time < introTarget) return true;
        for (const g of gaps) {
            if (time >= g.gapStartTime && time < g.skipTarget) return true;
        }
        return false;
    }

    // END OF PLAY (backlog 314), the desktop's hand-over from gameplay to the results:
    //   RESULTS_DISPLAY_DELAY    Player.RESULTS_DISPLAY_DELAY: a completed run's score is taken the
    //                            instant the engine finishes, and the card (and onFinish, so the
    //                            submit) follows this long after. Escape asks for it at once
    //                            (Player.PerformExit's progressToResults(false)).
    //   LINES_FADE_OUT_MS        TypeBeatStyle.SCREEN_FADE_DURATION, LyricStage's OutQuint fade of
    //                            every row once the last line has sealed (the CSS carries it).
    //   FAIL_WIND_DOWN_MS        FailAnimationContainer's duration: the track's frequency ramped
    //                            linearly to 0, a low-pass swept to FAIL_FILTER_CUTOFF_HZ OutCubic
    //                            beside a high-pass set straight to it, and the volume at
    //                            FAIL_VOLUME. The fail card follows the wind-down, and Escape
    //                            finishes it early (PerformExitWithConfirmation's FinishTransforms).
    //                            The desktop's Gameplay/failsound sample is NOT played: its asset is
    //                            CC-BY-NC, so the browser's wind-down is silent by decision.
    //   LOWPASS_OPEN_HZ          AudioFilter.MAX_LOWPASS_CUTOFF, where the low-pass sweep starts.
    const RESULTS_DISPLAY_DELAY_MS = 1000;
    const LINES_FADE_OUT_MS = 300;
    const FAIL_WIND_DOWN_MS = 2500;
    const FAIL_FILTER_CUTOFF_HZ = 300;
    const FAIL_VOLUME = 0.5;
    const LOWPASS_OPEN_HZ = 22049;
    const FAIL_SWEEP_POINTS = 64;
    const RESULTS_HINT = 'hold ` to retry · esc to go back';

    // Easing.OutCubic from `from` to `to`, sampled at `n` evenly spaced points (both ends included),
    // for AudioParam.setValueCurveAtTime.
    function outCubicCurve(from, to, n) {
        const curve = new Float32Array(n);
        for (let i = 0; i < n; i++) {
            const p = n === 1 ? 1 : i / (n - 1);
            const e = 1 - Math.pow(1 - p, 3);
            curve[i] = from + (to - from) * e;
        }
        return curve;
    }

    // A key the results wait swallows: anything that could type or scroll, i.e. no Ctrl/Alt/Meta
    // chord (those stay the browser's, F5 and Ctrl+R included).
    function isSwallowedDuringWait(e) {
        return !(e.ctrlKey || e.altKey || e.metaKey);
    }

    function mountPlayer(container, opts) {
        const beatmap = Core.buildBeatmap(Core.parseLyricOsu(opts.osuText));
        const title = opts.title || beatmap.title || 'untitled';
        const artist = opts.artist || beatmap.artist || '';

        // The SONG's playhead, per line, precomputed once: independent of where the player is,
        // which is the whole point (you can see yourself rushing or dragging against it).
        const sungPoints = beatmap.lines.map(buildSungPoints);
        // The underline PACE HUE's bands (backlog 317, UnderlinePace.BuildBands), one array per line,
        // precomputed once beside the playhead: the colours are map constants, ranked across the
        // whole map, so no per-frame path may ever reach this.
        const paceBands = buildPaceBands(beatmap.lines, sungPoints);

        // The skippable stretches of this map, computed once: the qualifying instrumental gaps
        // (the mirror of what the server priced into beatmaps.skippable_s) and the intro run-up.
        const gaps = computeGaps(beatmap.lines);
        // And where the clock starts (the desktop's pre-roll), which the intro skip is offered from.
        const clockStart = gameplayStartTime(beatmap);
        const introTarget = introSkipTarget(beatmap.lines, clockStart);

        container.innerHTML = '';
        const root = el('div', 'tb-player');
        container.appendChild(root);

        // --- scaffold --------------------------------------------------------
        const hud = el('div', 'tb-hud');
        const hudScore = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-score">0</span><span class="tb-hud-lbl">score</span>');
        const hudCombo = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-combo">0</span><span class="tb-hud-lbl">combo</span>');
        const hudAcc = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-acc">100.00%</span><span class="tb-hud-lbl">accuracy</span>');
        const hudWpm = el('div', 'tb-hud-stat', '<span class="tb-hud-val" id="tb-wpm">0</span><span class="tb-hud-lbl">wpm</span>');
        hud.append(hudScore, hudCombo, hudAcc, hudWpm);

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
        // The bar runs over the playable span, not the decoded file (songProgressBounds).
        const progressBounds = songProgressBounds(beatmap.lines, clockStart);
        const progressTime = el('div', 'tb-progress-time');
        const progressElapsed = el('span', 'tb-progress-elapsed', '');
        const progressRemaining = el('span', 'tb-progress-remaining', '');
        progressTime.append(progressElapsed, progressRemaining);

        const meta = el('div', 'tb-meta', `<span class="tb-meta-title">${escapeHtml(title)}</span>${artist ? ' <span class="tb-meta-artist">' + escapeHtml(artist) + '</span>' : ''}`);

        const overlay = el('div', 'tb-overlay');
        root.append(hud, health, stage, progress, progressTime, meta, overlay);
        // The hold-` / hold-Ctrl+` fill (backlog 311), empty and hidden until a hold begins.
        const holdBox = el('div', 'tb-hold');
        root.appendChild(holdBox);

        const scoreEl = root.querySelector('#tb-score');
        const comboEl = root.querySelector('#tb-combo');
        const accEl = root.querySelector('#tb-acc');
        const wpmEl = root.querySelector('#tb-wpm');

        // --- audio + engine state -------------------------------------------
        let audioCtx = null, audioBuffer = null, source = null;
        let startedAt = 0, raf = 0, backstop = 0, engine = null, concluded = false, running = false;
        let startKeyHandler = null; // capturing keydown listener while the start gate is up
        // The pause machinery (backlog 311, see PAUSE_COOLDOWN_MS above).
        let paused = false;
        let lastPauseWall = null;      // performance.now() of the last pause taken, for the cooldown
        let blurred = false;           // the window lost focus (a hidden tab is document.hidden)
        let ctxInterrupted = false;    // the BROWSER suspended or interrupted the context mid-play
        let hold = null;               // the live hold-` / hold-Ctrl+` gesture: { kind, timer }
        // The end of play (backlog 314, see RESULTS_DISPLAY_DELAY_MS above).
        let sourceGain = null;         // the live source's gain node, halved by the fail wind-down
        let failNodes = [];            // the wind-down's filters, torn down with the source
        let resultsTimer = 0;          // the pending hand-over to the card
        let pendingResults = null;     // the results taken at the finish or fail instant
        let waitKeyHandler = null;     // capturing keydown listener during the results wait
        let cardKeyHandler = null;     // capturing keydown listener while the results card is up
        let cardKeyUpHandler = null;

        function nowMs() { return audioCtx ? (audioCtx.currentTime - startedAt) * 1000 : 0; }

        // The desktop's playback-validity accumulator (makePlaybackValidity), fresh per play in
        // begin(), fed once per tick(), told of every seek by startSourceAt.
        let validity = makePlaybackValidity();

        function cleanupAudio() {
            if (source) { try { source.stop(); } catch (e) {} try { source.disconnect(); } catch (e) {} source = null; }
            for (const n of failNodes) { try { n.disconnect(); } catch (e) {} }
            failNodes = [];
            if (sourceGain) { try { sourceGain.disconnect(); } catch (e) {} sourceGain = null; }
        }

        // Start (or restart) the buffer at `offsetMs` into the track and rebase the clock onto it,
        // so nowMs() jumps with the audio and everything downstream (the engine, every animation)
        // follows one clock as it always did. A BufferSource is single-use, hence the fresh node.
        //
        // The 60 ms scheduling lead is the same one begin() has always taken: `when` is when the
        // audio actually starts, and startedAt is back-dated by the offset so that at `when` the
        // gameplay clock reads exactly offsetMs.
        //
        // A NEGATIVE offset (the pre-roll, see gameplayStartTime) is not clamped: the clock still
        // reads offsetMs at `when` and runs through silent negative time, and the source is
        // scheduled to start from the top of the track at the moment the clock crosses 0, which is
        // `when - offsetSec`, i.e. startedAt itself.
        //
        // Refused while PAUSED (backlog 311): a seek under a suspended context would rebase the clock
        // the pause froze. A retry clears the pause before its begin() reaches here.
        function startSourceAt(offsetMs) {
            if (paused) return false;
            cleanupAudio();
            source = audioCtx.createBufferSource();
            source.buffer = audioBuffer;
            // Default playback at 10% (90% quieter); the map audio is loud on its own.
            const gainNode = audioCtx.createGain();
            gainNode.gain.value = 0.1;
            source.connect(gainNode);
            gainNode.connect(audioCtx.destination);
            sourceGain = gainNode;
            const offsetSec = offsetMs / 1000;
            const when = audioCtx.currentTime + 0.06; // small scheduling lead
            startedAt = when - offsetSec;
            validity.discontinuity(); // the clock jumps on purpose here
            if (offsetSec >= 0) source.start(when, offsetSec);
            else source.start(startedAt, 0);
            return true;
        }

        function removeStartKey() {
            if (startKeyHandler) { document.removeEventListener('keydown', startKeyHandler, true); startKeyHandler = null; }
        }

        function destroy() {
            running = false;
            paused = false;
            abortHold();
            stopLoops();
            detachRunListeners();
            cancelResultsWait();
            removeCardKeys();
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

        // The one keydown listener, a thin wrapper: routeKeyDown (above mountPlayer) is the whole
        // key handler, over this host. The engine is read through a getter because begin() makes a
        // fresh one for every play.
        const keyHost = {
            get engine() { return engine; },
            now: nowMs,
            gaps: gaps,
            introTarget: introTarget,
            getSelection: function () { return selection; },
            setSelection: setSelection,
            performSkip: function (target) { performSkip(target); },
            prevWasDead: false
        };

        // THE KEY GATE (backlog 311). The lifecycle keys (Escape, hold-`, hold-Ctrl+`) are taken
        // first and never reach the engine. Every other key is IGNORED while paused, and while the
        // context is not running (resuming, or suspended by the browser ahead of its pause): the
        // clock is frozen there, and a press judged at a frozen clock is a free look at the lyric.
        // While paused the key is also left to the browser, so Tab and Enter work on the card.
        function onKeyDown(e) {
            if (!running || !engine) return;
            if (routeLifecycleKey(e)) return;
            if (paused || audioCtx.state !== 'running') return;
            routeKeyDown(e, keyHost);
        }

        function isHoldKey(e) {
            return e.code === 'Backquote' || (!e.code && e.key === '`');
        }

        function routeLifecycleKey(e) {
            if (isHoldKey(e)) {
                e.preventDefault();
                if (!e.repeat && !e.metaKey) beginHold(e.ctrlKey ? 'exit' : 'retry');
                return true;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                // GlobalAction.Back: pause, and on the pause card the Back action is Continue.
                if (!e.repeat) { if (paused) resume(); else tryPause(); }
                return true;
            }
            return false;
        }

        function onKeyUp(e) {
            if (hold && isHoldKey(e)) abortHold();
        }

        // --- pause / resume (backlog 311) -------------------------------------
        function startLoops() {
            stopLoops();
            raf = requestAnimationFrame(rafLoop);
            // The backstop now only covers the stretch between a hide and its pause (a cooldown or
            // a break waiving it), and a break the player sits out hidden.
            backstop = setInterval(function () { if (running && !paused && document.hidden) tick(); }, 250);
        }

        function stopLoops() {
            if (raf) { cancelAnimationFrame(raf); raf = 0; }
            if (backstop) { clearInterval(backstop); backstop = 0; }
        }

        // Player.Pause(): refused outside a live run, on a failed or finished engine, and inside the
        // cooldown. Returns whether the pause was taken.
        function tryPause() {
            if (!running || paused || !engine || engine.failed || engine.finished) return false;
            const wall = performance.now();
            if (pauseCooldownActive(lastPauseWall, wall)) return false;
            paused = true;
            lastPauseWall = wall;
            stopLoops();
            validity.discontinuity();
            try { const p = audioCtx.suspend(); if (p && p.catch) p.catch(function () {}); } catch (e) {}
            showPauseCard();
            return true;
        }

        // Player.Resume(): instant, from the gesture that asked for it.
        function resume() {
            if (!running || !paused) return;
            paused = false;
            ctxInterrupted = false;
            overlay.className = 'tb-overlay';
            overlay.innerHTML = '';
            validity.discontinuity();
            lastFrameMs = null;
            try { const p = audioCtx.resume(); if (p && p.catch) p.catch(function () {}); } catch (e) {}
            startLoops();
        }

        // Mid-play retry: the run is DISCARDED (never concluded, so onFinish never runs) and begin()
        // mints a fresh token. The context is resumed here, inside the gesture.
        function retry() {
            if (!running) return;
            paused = false;
            if (audioCtx.state !== 'running') {
                try { const p = audioCtx.resume(); if (p && p.catch) p.catch(function () {}); } catch (e) {}
            }
            begin();
        }

        // Quit: the run is DISCARDED, never submitted. With a host to return to, the player is torn
        // down and handed back; without one it falls back to its own start gate.
        function quit() {
            if (!running) return;
            if (opts.onExit) {
                destroy();
                opts.onExit();
                return;
            }
            running = false;
            paused = false;
            abortHold();
            stopLoops();
            detachRunListeners();
            cleanupAudio();
            showStartGate('press space to start');
        }

        // Whether focus loss (or the browser's own suspension) wants the run paused right now.
        function autoPauseWanted() {
            if (ctxInterrupted) return true;
            if (!(document.hidden || blurred)) return false;
            return !inSkippableInstrumental(gaps, introTarget, nowMs());
        }

        function maybeAutoPause() {
            if (running && !paused && autoPauseWanted()) tryPause();
        }

        function onVisibilityChange() {
            if (document.hidden) { abortHold(); maybeAutoPause(); }
        }
        function onBlur() { blurred = true; abortHold(); maybeAutoPause(); }
        function onFocus() { blurred = false; }
        function onCtxStateChange() {
            const st = audioCtx ? audioCtx.state : 'closed';
            if (st === 'running') { ctxInterrupted = false; return; }
            if ((st === 'suspended' || st === 'interrupted') && running && !paused) {
                ctxInterrupted = true;
                maybeAutoPause();
            }
        }

        // Leaving the page mid-run (F5, Ctrl+R, a closed tab) asks first; a run has no other save.
        function onBeforeUnload(e) {
            if (!running) return undefined;
            e.preventDefault();
            e.returnValue = '';
            return '';
        }

        function attachRunListeners() {
            detachRunListeners();
            document.addEventListener('keydown', onKeyDown, true);
            document.addEventListener('keyup', onKeyUp, true);
            document.addEventListener('visibilitychange', onVisibilityChange);
            window.addEventListener('blur', onBlur);
            window.addEventListener('focus', onFocus);
            window.addEventListener('beforeunload', onBeforeUnload);
            if (audioCtx && audioCtx.addEventListener) audioCtx.addEventListener('statechange', onCtxStateChange);
        }

        function detachRunListeners() {
            document.removeEventListener('keydown', onKeyDown, true);
            document.removeEventListener('keyup', onKeyUp, true);
            document.removeEventListener('visibilitychange', onVisibilityChange);
            window.removeEventListener('blur', onBlur);
            window.removeEventListener('focus', onFocus);
            window.removeEventListener('beforeunload', onBeforeUnload);
            if (audioCtx && audioCtx.removeEventListener) audioCtx.removeEventListener('statechange', onCtxStateChange);
        }

        // HoldToConfirmContainer: the action fires once the key has been held HOLD_TO_CONFIRM_MS,
        // with a fill that runs over the same span; letting go, a blur or a hide aborts it.
        // `action`, when given, replaces the mid-play retry/quit (the results card's retry uses it).
        function beginHold(kind, action) {
            abortHold();
            const timer = setTimeout(function () {
                hold = null;
                holdBox.className = 'tb-hold';
                if (action) action();
                else if (kind === 'retry') retry(); else quit();
            }, HOLD_TO_CONFIRM_MS);
            hold = { kind: kind, timer: timer };
            // A fresh fill element restarts its CSS animation from empty.
            holdBox.innerHTML = '';
            holdBox.appendChild(el('span', 'tb-hold-label', kind === 'retry' ? 'retry' : 'quit'));
            const track = el('span', 'tb-hold-track');
            track.appendChild(el('span', 'tb-hold-fill'));
            holdBox.appendChild(track);
            holdBox.className = 'tb-hold tb-hold-on tb-hold-' + kind;
        }

        function abortHold() {
            if (!hold) return;
            clearTimeout(hold.timer);
            hold = null;
            holdBox.className = 'tb-hold';
        }

        function showPauseCard() {
            overlay.className = 'tb-overlay tb-overlay-on tb-overlay-pause';
            overlay.innerHTML = '';
            const card = el('div', 'tb-card tb-pause');
            card.appendChild(el('div', 'tb-card-title', 'paused'));
            const actions = el('div', 'tb-pause-actions');
            const resumeBtn = el('button', 'tb-btn tb-btn-primary tb-pause-resume', 'resume');
            const retryBtn = el('button', 'tb-btn tb-btn-ghost tb-pause-retry', 'retry');
            const quitBtn = el('button', 'tb-btn tb-btn-ghost tb-pause-quit', 'quit');
            for (const b of [resumeBtn, retryBtn, quitBtn]) b.type = 'button';
            resumeBtn.addEventListener('click', resume);
            retryBtn.addEventListener('click', retry);
            quitBtn.addEventListener('click', quit);
            actions.append(resumeBtn, retryBtn, quitBtn);
            card.appendChild(actions);
            card.appendChild(el('div', 'tb-card-hint', escapeHtml(PAUSE_HINT)));
            overlay.appendChild(card);
            if (typeof resumeBtn.focus === 'function') resumeBtn.focus();
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
            // The PUSH WARNING (backlog 263) shares the cue bars' band and their depleting shape,
            // but it is anchored at the END of the line and it is red, so it gets a box of its own
            // rather than a third child of the cue's: the cue box is pinned to the start of the
            // UPCOMING line, and while the player is being pushed those are two different rows.
            const push = el('div', 'tb-push');
            const pushBar = el('div', 'tb-cue-bar tb-push-bar');
            push.append(pushBar);
            row.append(sweep, cellsBox, cue, push);
            return {
                row, cellsBox, sweep, sweepFill, sweepGlow, cue, cueWord, cueBoundary, push, pushBar,
                spans: [], offsets: [0], line: null, index: -1, scale: 1,
                // The syllable group lit on this row (backlog 317), -1 for none: only the sung row
                // ever carries one, and updateSungSyllable moves it off a row as it moves on.
                litSyllable: -1
            };
        }

        function buildRow(rowObj, index) {
            const line = beatmap.lines[index] || null;
            rowObj.line = line;
            rowObj.index = line ? index : -1;
            rowObj.cellsBox.textContent = '';
            rowObj.spans = [];
            rowObj.offsets = [0];
            rowObj.scale = 1;
            rowObj.row.style.transform = 'none';
            rowObj.sweep.style.display = line ? '' : 'none';
            // The SPACE ERROR DOTS (backlog 316): one overlay dot per word gap, in a layer of the
            // row's own rather than as a pseudo-element of the gap's span, because that span can
            // carry .tb-c-wrong-gap's dim and a child of it would be dimmed with it. Created with
            // the row's first build, emptied and refilled with every line change like the spans.
            if (!rowObj.dotsBox) {
                rowObj.dotsBox = el('div', 'tb-dots');
                rowObj.row.appendChild(rowObj.dotsBox);
            }
            rowObj.dotsBox.textContent = '';
            rowObj.dots = [];
            // The SYLLABLE MARKERS (backlog 317, LyricLineDisplay.addSyllableMarkers): one small
            // apex-up triangle per mid-word boundary of a SUBTIMED word, read straight off the line's
            // own syllableMarkerCells (the same derivation as the judgement groups), in a layer of
            // the row's own like the dots. Only a real inter-character gap is drawable, never the
            // line's leading edge nor past its last cell.
            if (!rowObj.marksBox) {
                rowObj.marksBox = el('div', 'tb-marks');
                rowObj.row.appendChild(rowObj.marksBox);
            }
            rowObj.marksBox.textContent = '';
            rowObj.marks = [];
            // The PACE BANDS (backlog 317, LyricLineDisplay.buildPaceTracks): the rail becomes one
            // div per word band, inside the sweep and BEFORE the fill and the glow so both paint
            // over it, exactly as the desktop's track boxes sit under its fill.
            if (!rowObj.bandsBox) {
                rowObj.bandsBox = el('div', 'tb-bands');
                rowObj.sweep.insertBefore(rowObj.bandsBox, rowObj.sweepFill);
            }
            rowObj.bandsBox.textContent = '';
            rowObj.bands = [];
            rowObj.sweep.classList.remove('tb-sweep-banded');
            rowObj.litSyllable = -1;
            if (!line) return;

            const n = line.cells.length;
            for (const i of line.syllableMarkerCells || []) {
                if (!(i > 0 && i < n)) continue;
                const mark = el('span', 'tb-mark');
                rowObj.marks.push({ index: i, el: mark });
                rowObj.marksBox.appendChild(mark);
            }
            // Clamped rather than trusted, as the desktop does: a stale band list can only
            // under-paint. A line with no band at all keeps the sweep's own flat neutral rail.
            for (const band of paceBands[index] || []) {
                const lo = Math.min(Math.max(band.startCell, 0), n);
                const hi = Math.min(Math.max(band.endCellExclusive, lo), n);
                if (hi <= lo) continue;
                const box = el('div', 'tb-band');
                box.style.background = paceColourCss(band.colour);
                rowObj.bands.push({ lo: lo, hi: hi, el: box });
                rowObj.bandsBox.appendChild(box);
            }
            if (rowObj.bands.length > 0) rowObj.sweep.classList.add('tb-sweep-banded');

            const frag = document.createDocumentFragment();
            for (let i = 0; i < line.cells.length; i++) {
                const span = el('span');
                rowObj.spans.push(span);
                frag.appendChild(span);
                if (isWordGap(line.cells[i])) {
                    const dot = el('span', 'tb-dot');
                    rowObj.dots.push({ index: i, el: dot, shown: false, scale: 1 });
                    rowObj.dotsBox.appendChild(dot);
                }
            }
            rowObj.cellsBox.appendChild(frag);
        }

        // Centre each dot in its gap's slot, off the same measured offsets the caret and the sweep
        // are placed from (LyricLineDisplay.measureAndLayout: cellX + advance / 2). Runs wherever
        // measureRow does, so a fit or a resize moves the dots with the glyphs.
        function placeDots(rowObj) {
            if (!rowObj.dots) return;
            for (const d of rowObj.dots) {
                const x = (rowObj.offsets[d.index] + rowObj.offsets[d.index + 1]) / 2;
                d.el.style.left = (isFinite(x) ? x : 0).toFixed(2) + 'px';
            }
        }

        // Each marker sits on its cell's LEFT EDGE, the inter-character gap the boundary falls in
        // (LyricLineDisplay.measureAndLayout: X = cellX[i], Origin TopCentre), and each band spans
        // exactly its own cells' measured extent, so the bands tile the line. Both read the same
        // getBoundingClientRect offsets as the caret and the sweep, so no advance is assumed.
        function placeMarks(rowObj) {
            for (const m of rowObj.marks || []) {
                const x = rowObj.offsets[m.index];
                m.el.style.left = (isFinite(x) ? x : 0).toFixed(2) + 'px';
            }
            for (const b of rowObj.bands || []) {
                const left = rowObj.offsets[b.lo];
                const right = rowObj.offsets[b.hi];
                const l = isFinite(left) ? left : 0;
                const w = isFinite(right) ? Math.max(0, right - l) : 0;
                b.el.style.left = l.toFixed(2) + 'px';
                b.el.style.width = w.toFixed(2) + 'px';
            }
        }

        function cellText(cell, shimmerTick, i, dotted) {
            let ch = cellGlyph(cell, dotted);
            if (cell.freestyle) ch = cell.typedChar !== null ? cell.typedChar : Core.freestyleGlyph(shimmerTick, i);
            // A space (a word gap's expected char, or a space typed into a freestyle slot) must
            // render as nbsp or the browser collapses it away.
            return ch === ' ' ? ' ' : ch;
        }

        // Repaint a row in place: only the spans whose class or glyph actually changed are written,
        // so a settled line costs nothing and the shimmer only touches its own cells. Backlog 251
        // removed the per-cell sync tint this used to guard the same way (--tb-sync-fill): a
        // correct cell now takes .tb-c-hit's flat colour straight from CSS, so there is no longer a
        // fill to compute or write here at all.
        function paintRow(rowObj, caretIndex, shimmerTick, time) {
            const line = rowObj.line;
            if (!line) return;
            // The dot rule is a whole-line read, so it runs once per repaint and not per cell.
            const dots = SPACE_ERROR_DOTS_ENABLED ? spaceErrorDots(line.cells) : null;
            const isCur = rowObj === rowCur;
            const lit = rowObj.litSyllable;
            for (let i = 0; i < rowObj.spans.length; i++) {
                const span = rowObj.spans[i];
                const cell = line.cells[i];
                const popping = popEnd[i] > time && isCur;
                const shaking = shakeEnd[i] > time && isCur;
                const inSung = lit >= 0 && line.cellSyllable[i] === lit;
                const cls = cellClass(cell, i === caretIndex, popping, shaking, inSung);
                if (span.className !== cls) span.className = cls;
                const txt = cellText(cell, shimmerTick, i, dots !== null && dots[i]);
                if (span.textContent !== txt) span.textContent = txt;
            }
            // Each gap's dot: shown on the flag, and swelling through its pulse
            // (LyricLineDisplay.Update's bounce, a clock rather than a state change).
            for (const d of rowObj.dots || []) {
                const shown = dots !== null && dots[d.index];
                if (d.shown !== shown) {
                    d.shown = shown;
                    d.el.classList.toggle('tb-dot-on', shown);
                }
                const pulseEnd = isCur && d.index < dotPulseEnd.length ? dotPulseEnd[d.index] : -1;
                const scale = pulseEnd > time
                    ? spaceErrorDotPulseScale(SPACE_ERROR_DOT_PULSE_MS - (pulseEnd - time))
                    : 1;
                if (d.scale !== scale) {
                    d.scale = scale;
                    d.el.style.transform = scale === 1 ? '' : 'scale(' + scale.toFixed(3) + ')';
                }
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
        let shakeEnd = [];           // per-cell deadline for a landed typo's shake (backlog 316)
        let dotPulseEnd = [];        // per-cell deadline for a gap dot's pulse (backlog 316)
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
            shakeEnd = new Array(rowCur.spans.length).fill(-1);
            dotPulseEnd = new Array(rowCur.spans.length).fill(-1);
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
            for (const r of rows) placeDots(r);
            for (const r of rows) placeMarks(r);
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

        // Two depleting bars under the upcoming line's first TYPEABLE char: a SOLID one landing on
        // the line boundary (StartTime) and a first-word one landing on the FIRST WORD. A mapper may
        // set the boundary earlier than the first word, so the two can be distinct signals; when
        // they coincide they read as one solid bar. Mirrors LyricStage.updateApproachCue, with the
        // bars themselves computed by approachCueBars.
        //
        // The first-word bar is 50% opaque while its line is still to come, and FULL strength when
        // the cued line is the engine's active line (it self-activated into its own lead-in): that is
        // the line under the player's caret, not a hint about one they cannot type yet (backlog 318,
        // which ported this and the first-typeable anchor from PR 2).
        //
        // Since PR 2 the BOUNDARY bar belongs to the PINNED caret only (FletcherEnabled false): a
        // pinned caret is handed the line at its boundary, so that moment is one the player acts on,
        // while a flexible caret enters on the first word and the boundary is no signal at all. The
        // browser always runs the flexible caret (engine.fletcherEnabled is permanently true), so in
        // practice only the first-word bar is drawn; the gate reads the engine flag rather than being
        // deleted so it stays a line-for-line mirror of the desktop's.
        function updateCue(time, active) {
            const activeIndex = active ? engine.activeLineIndex : -1;
            const target = engine.finished ? -1
                : cueTargetLine(beatmap.lines, activeIndex, engine.nextSealIndex, time);
            const rowObj = (target >= 0 && target < beatmap.lines.length) ? rowFor(target) : null;

            for (const r of rows) {
                if (r !== rowObj) r.cue.style.display = 'none';
            }
            if (!rowObj || !rowObj.line) return;

            const bars = approachCueBars(beatmap.lines, target, activeIndex, !engine.fletcherEnabled, time);
            if (!bars || (!bars.word.shown && !bars.boundary.shown)) { rowObj.cue.style.display = 'none'; return; }

            rowObj.cue.style.display = '';
            rowObj.cue.style.left = xAt(rowObj, bars.firstCell).toFixed(2) + 'px';
            applyCueBar(rowObj.cueWord, bars.word);
            applyCueBar(rowObj.cueBoundary, bars.boundary);
        }

        function applyCueBar(bar, state) {
            bar.style.width = state.width.toFixed(2) + 'px';
            bar.style.opacity = state.shown ? state.alpha.toFixed(3) : '0';
        }

        // THE PUSH WARNING (backlog 263), mirrors LyricStage.updatePushWarning. A player lagging
        // behind on a line the song has already left keeps it only until the drag cutoff, where the
        // engine force-seals it and lands the caret on the next line (TypingEngine.dragCutoffAt).
        // That used to arrive with no notice at all, so the same depleting bar the cue-in bars use
        // counts it down, RIGHT-ANCHORED at the end of the line and in the error red: the opposite
        // corner and the opposite colour from a cue, because it is the opposite message, a line
        // about to be taken rather than a line about to be given.
        //
        // WHEN it shows is pushWarningBar's (backlog 318, porting PR 2): it opens on the NEXT line's
        // first word (falling back to CUE_LEAD_MS before the cutoff), drains for a fixed CUE_LEAD_MS
        // from there, is cut off by the push itself, and fades in over PUSH_FADE_IN_MS rather than
        // snapping on. It used to be the final CUE_LEAD_MS before the cutoff, snapped on.
        //
        // Display only. It reads a nullable engine readout and nothing else, so every path where no
        // push is coming (the line typed out, the caret rolled on ahead of an abandoned line, the
        // run finished) falls through to the same hide below.
        function updatePushWarning(time) {
            const cutoff = engine.dragCutoffAt;
            const target = cutoff === null ? -1 : engine.activeLineIndex;
            const rowObj = (target >= 0 && target < beatmap.lines.length) ? rowFor(target) : null;
            const bar = rowObj && rowObj.line ? pushWarningBar(beatmap.lines, target, cutoff, time) : null;
            const shown = bar !== null && bar.shown;

            for (const r of rows) {
                if (!shown || r !== rowObj) r.push.style.display = 'none';
            }
            if (!shown) return;

            // The END of the line (offsets[n]), not the caret, which is somewhere mid-line by
            // definition while the player is dragging. The box is pinned there and the bar hangs
            // off its right edge (CSS translateX(-100%)), which is the TopRight origin the desktop
            // gives its Box: the width depletes leftward while the right edge stays put.
            rowObj.push.style.display = '';
            rowObj.push.style.left = xAt(rowObj, rowObj.line.cells.length).toFixed(2) + 'px';
            applyCueBar(rowObj.pushBar, bar);
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
        // THE SUNG-SYLLABLE HIGHLIGHT (backlog 317), mirrors LyricStage.setSungSyllable: every frame,
        // whatever the playhead does, the group the vocal is inside lights on the SUNG row, which
        // since backlogs 217/223 need not be the caret's row, and the row it leaves is cleared. A row
        // repaints only when its lit group changes, so the steady state costs one scan of the sung
        // line's groups per frame.
        function updateSungSyllable(time, sungRow, caretIndex, shimmerTick) {
            const lit = sungRow ? currentSyllableIn(sungRow.line, time) : -1;
            for (const r of rows) {
                const want = r === sungRow ? lit : -1;
                if (r.litSyllable === want) continue;
                r.litSyllable = want;
                paintRow(r, r === rowCur ? caretIndex : -1, shimmerTick, time);
            }
        }

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
        const playerCaretFade = makeCaretFade(CARET_FADE_MS);
        const sungCaretFade = makeCaretFade(CARET_FADE_MS);

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
            const playerFade = playerCaretFade(shown.player, time);
            const sungFade = sungCaretFade(shown.sung, time);
            playerCaret.style.opacity = (playerFade * caretAlpha(time - lastTypedAt, moving, true)).toFixed(3);
            sungCaret.style.opacity = sungFade.toFixed(3);
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
            setSkipAffordance(skipAllowedNow(time) ? skipTargetAt(gaps, introTarget, upcoming, time) : null);
        }

        // The chip's half of the skip-or-character predicate (skipAllowedFor), over the live
        // selection. The key's half is routeKeyDown's, over the same function.
        function skipAllowedNow(time) {
            return skipAllowedFor(engine, time, selection !== null);
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
        //
        // Refused while paused or while the context is not running (backlog 311): the chip is still
        // clickable under the pause card's backdrop, and a seek against a frozen clock would move
        // the song while the player looks at the card.
        function performSkip(target) {
            if (!running || !audioCtx || !audioBuffer || target === null) return;
            if (paused || audioCtx.state !== 'running') return;
            if (!(target > nowMs())) return;
            if (target / 1000 >= audioBuffer.duration) return;

            if (!startSourceAt(target)) return;

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
        // caret (alternating sides), falls away and fades (LyricStage.onWrongKeyRejected). The
        // letter is anchored ON the caret; the desktop's side offset, lift and motion are all in
        // .tb-wrongkey, in em, so they follow the lyric size.
        function popWrongKey(c) {
            wrongDirection = -wrongDirection;
            const letter = el('span', 'tb-wrongkey');
            letter.textContent = c === ' ' ? '_' : c;
            letter.style.setProperty('--dir', String(wrongDirection));
            letter.style.left = caretX.toFixed(2) + 'px';
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
            // GREY WHILE THE WINDOW IS SHUT (backlog 307, LyricStage's SetLineDim(awaiting ? 0.4 :
            // 0), alpha = 1 - dim): a line the player has been handed by a manual newline but may
            // not type on yet (engine.awaitingEntry) waits at the upcoming-line grey, and undims the
            // moment its window opens. On the glyphs only, as the desktop fades the line's content
            // and not the bars drawn over it.
            const awaitingAlpha = active && engine.awaitingEntry ? '0.6' : '';
            if (rowCur.cellsBox.style.opacity !== awaitingAlpha) rowCur.cellsBox.style.opacity = awaitingAlpha;

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
            updateSungSyllable(time, sungRow, caretIndex, shimmerTick);
            updateSweeps(time, sungRow);
            updateCarets(time, elapsed, active, sungRow);
            // Repainted per frame like the caret, and for the same reason: a fit or a resize moves
            // every cell offset under it.
            paintSelection();
            updateCue(time, active);
            updatePushWarning(time);
            updateGap(time);

            const readouts = hudReadouts(engine);
            scoreEl.textContent = fmtInt(readouts.score);
            // The engine's own live combo, which breaks on a wrong key as you watch. The results
            // screen shows the SUBMITTED peak instead (computeScore's maxCombo, off the score
            // processor mirror); the two agree in strict vanilla play, which is all /play has.
            comboEl.textContent = engine.combo + 'x';
            accEl.textContent = fmtAccuracy(readouts.accuracy);
            wpmEl.textContent = Math.round(rolling.value(engine.liveWpm));

            const bar = healthBar(engine.health);
            healthFill.style.width = bar.widthPct + '%';
            healthFill.classList.toggle('tb-health-danger', bar.danger);

            const songProgress = songProgressAt(progressBounds, time);
            progressFill.style.width = (songProgress.barProgress * 100) + '%';
            progress.classList.toggle('tb-progress-intro', songProgress.isIntro);
            if (songProgress.textLive) {
                progressElapsed.textContent = songProgress.elapsedText;
                progressRemaining.textContent = songProgress.remainingText;
            }

            if (wrongFlash > 0) { root.classList.add('tb-shake'); wrongFlash--; }
            else root.classList.remove('tb-shake');
        }

        // --- loop / lifecycle ------------------------------------------------
        // One tick = advance the audio-clocked engine + repaint. Driven by rAF while
        // the tab is visible (vsync-smooth); a low-rate setInterval backstop keeps it
        // ticking when the tab is hidden and rAF is paused, so state stays consistent
        // with the still-playing audio instead of freezing.
        //
        // Since backlog 311 a hidden tab PAUSES, so the backstop only runs through a hide the pause
        // did not take (the cooldown, or a skippable instrumental), and each tick first retries the
        // auto-pause that was refused, which is the desktop's rescheduled updatePauseOnFocusLostState.
        function tick() {
            if (!running || paused) return;
            if (autoPauseWanted() && tryPause()) return;
            validity.observe(nowMs(), performance.now(), audioCtx.state === 'running');
            engine.update(nowMs());
            render();
            if ((engine.finished || engine.failed) && !concluded) conclude();
        }
        function rafLoop() {
            raf = 0;
            if (!running || paused) return;
            tick();
            if (running && !paused) raf = requestAnimationFrame(rafLoop);
        }

        // The score is taken HERE, at the instant the engine finished or failed, exactly as it always
        // was; only the card and onFinish wait (backlog 314). A completed run fades its rows out and
        // keeps its audio playing through RESULTS_DISPLAY_DELAY_MS; a failed one runs the silent
        // wind-down and hands over when it ends. Either wait ends early on Escape.
        function conclude() {
            concluded = true;
            running = false;
            abortHold();
            stopLoops();
            detachRunListeners();
            const failed = !!engine.failed;
            const results = Core.computeScore(engine);
            // The veto's verdict rides on the results; play.js declines to submit a false one.
            results.playbackValid = validity.valid;
            pendingResults = results;
            root.classList.add(failed ? 'tb-failing' : 'tb-concluded');
            if (failed) startFailWindDown();
            waitKeyHandler = function (e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    if (e.stopPropagation) e.stopPropagation();
                    if (!e.repeat) finishConclusion();
                    return;
                }
                if (isSwallowedDuringWait(e)) {
                    e.preventDefault();
                    if (e.stopPropagation) e.stopPropagation();
                }
            };
            document.addEventListener('keydown', waitKeyHandler, true);
            resultsTimer = setTimeout(finishConclusion, failed ? FAIL_WIND_DOWN_MS : RESULTS_DISPLAY_DELAY_MS);
        }

        // The hand-over: the card, then onFinish (and so the submit), once per concluded run.
        function finishConclusion() {
            const results = pendingResults;
            if (!results) return;
            cancelResultsWait();
            // A failed track has wound down to a standstill; an early Escape skips the rest of it.
            if (engine && engine.failed) cleanupAudio();
            showResults(results);
            if (opts.onFinish) { try { opts.onFinish(results, publicApi); } catch (e) { console.error(e); } }
        }

        function cancelResultsWait() {
            if (resultsTimer) { clearTimeout(resultsTimer); resultsTimer = 0; }
            if (waitKeyHandler) { document.removeEventListener('keydown', waitKeyHandler, true); waitKeyHandler = null; }
            pendingResults = null;
        }

        // FailAnimationContainer.Start, on the Web Audio graph: source -> low-pass -> high-pass ->
        // the source's own gain. The playback rate (the desktop's track frequency) ramps linearly to
        // 0, the low-pass sweeps OutCubic from fully open to the cutoff, the high-pass goes straight
        // to it, and the gain is halved at once. No sample plays.
        function startFailWindDown() {
            if (!audioCtx || !source || !sourceGain) return;
            const t = audioCtx.currentTime;
            const dur = FAIL_WIND_DOWN_MS / 1000;
            try {
                const lowPass = audioCtx.createBiquadFilter();
                lowPass.type = 'lowpass';
                const highPass = audioCtx.createBiquadFilter();
                highPass.type = 'highpass';
                highPass.frequency.setValueAtTime(FAIL_FILTER_CUTOFF_HZ, t);
                lowPass.frequency.setValueAtTime(LOWPASS_OPEN_HZ, t);
                lowPass.frequency.setValueCurveAtTime(outCubicCurve(LOWPASS_OPEN_HZ, FAIL_FILTER_CUTOFF_HZ, FAIL_SWEEP_POINTS), t, dur);
                source.disconnect();
                source.connect(lowPass);
                lowPass.connect(highPass);
                highPass.connect(sourceGain);
                failNodes = [lowPass, highPass];
            } catch (e) { console.error(e); }
            try {
                const rate = source.playbackRate;
                rate.setValueAtTime(rate.value, t);
                rate.linearRampToValueAtTime(0, t + dur);
            } catch (e) { console.error(e); }
            try {
                const g = sourceGain.gain;
                g.setValueAtTime(g.value * FAIL_VOLUME, t);
            } catch (e) { console.error(e); }
        }

        function begin() {
            // A mid-play retry (backlog 311) reaches here with the old run's loops and hold still
            // live, and possibly paused: all of it is dropped with the run, and the cooldown and
            // focus state start over as a fresh desktop Player's do.
            abortHold();
            stopLoops();
            // A retry from the results card (or its wait) drops the old run's hand-over and keys.
            cancelResultsWait();
            removeCardKeys();
            root.classList.remove('tb-concluded');
            root.classList.remove('tb-failing');
            paused = false;
            lastPauseWall = null;
            blurred = false;
            ctxInterrupted = false;
            engine = new Core.TypingEngine(beatmap);
            validity = makePlaybackValidity();
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
            // A wrong key TYPED THROUGH into a cell (backlog 316), the desktop's
            // CharJudged(WrongChar) feedback: the cell shakes, the gap's dot pulses (a dot exists
            // only on a word gap, so a typo on a lyric cell pulses nothing), and the caret blink
            // resets (Caret.NotifyTyped runs on every judgement, typos included). Deliberately NOT
            // the rolling WPM, which stays on onCharJudged's accepted presses as on the desktop.
            engine.onTypoLanded = function (index, lineIndex) {
                const now = nowMs();
                lastTypedAt = now;
                if (lineIndex !== lastCurIdx || index >= shakeEnd.length) return;
                shakeEnd[index] = now + TYPO_SHAKE_MS;
                if (isWordGap(beatmap.lines[lineIndex].cells[index])) dotPulseEnd[index] = now + SPACE_ERROR_DOT_PULSE_MS;
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
            // From the desktop's clock start, not from 0: a first line inside 2 s (or a map with a
            // longer AudioLeadIn) gets its silent pre-roll, with the stage and the cue on screen.
            startSourceAt(clockStart);
            running = true;
            attachRunListeners();
            window.removeEventListener('resize', onResize);
            window.addEventListener('resize', onResize);
            startLoops();
        }

        function showStartGate(label) {
            overlay.className = 'tb-overlay tb-overlay-on';
            overlay.innerHTML = '';
            const card = el('div', 'tb-card');
            card.appendChild(el('div', 'tb-card-title', escapeHtml(title)));
            if (artist) card.appendChild(el('div', 'tb-card-sub', escapeHtml(artist)));
            card.appendChild(el('div', 'tb-card-hint', START_GATE_HINT));
            const btn = el('button', 'tb-btn tb-btn-primary', label || 'press space to start');
            card.appendChild(btn);
            overlay.appendChild(card);
            const go = () => {
                if (audioCtx.state === 'suspended') audioCtx.resume();
                begin(); // begin() removes the start-key listener and mints a fresh token
            };
            btn.addEventListener('click', go);
            removeCardKeys();
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
            // The layout of resultCells (above mountPlayer), which decides every figure. The tier
            // row's miss is the fold of misses and uncorrected typos, the desktop panel's Miss; the
            // 'typos' stat beside it counts wrong KEYPRESSES (backlog 140), a different number that
            // no fold touches. A cell sealed holding a wrong character therefore shows in both, once
            // as the miss it cost and once as the key that caused it, which is what the desktop shows.
            const cells = resultCells(results, {
                title: title,
                artist: artist,
                stars: opts.stars,
                difficulty: opts.difficulty,
                creator: opts.creator || beatmap.creator,
                playedAt: new Date()
            });
            const info = el('div', 'tb-result-meta');
            info.innerHTML = resultMetaHtml(cells.meta);
            card.appendChild(info);
            const grid = el('div', 'tb-result-grid');
            grid.innerHTML = cells.stats.map(statCellOf).join('');
            card.appendChild(grid);
            const tiers = el('div', 'tb-result-grid tb-result-tiers');
            tiers.innerHTML = cells.tiers.map(statCellOf).join('');
            card.appendChild(tiers);
            // One invitation to the Discord server, between the numbers and the submit line: it
            // reads as part of the result, and 'play again' / 'back to maps' stay exactly where the
            // player last left them instead of being pushed down a row.
            const nudgeUrl = nudgeUrlFor(opts.discordNudge, results);
            if (nudgeUrl) card.appendChild(el('div', 'tb-result-nudge', nudgeHtml(nudgeUrl)));
            const status = el('div', 'tb-submit-status', '');
            card.appendChild(status);
            const actions = el('div', 'tb-result-actions');
            const again = el('button', 'tb-btn tb-btn-primary tb-result-again', 'play again');
            again.type = 'button';
            // ResultsScreen's RetryButton: straight back into play, no start gate. The click is a
            // user gesture, so the context may be resumed from it.
            again.addEventListener('click', retryFromResults);
            actions.appendChild(again);
            if (opts.onExit) {
                const back = el('button', 'tb-btn tb-btn-ghost tb-result-back', 'back to maps');
                back.type = 'button';
                back.addEventListener('click', leaveResults);
                actions.appendChild(back);
            }
            card.appendChild(actions);
            card.appendChild(el('div', 'tb-card-hint tb-result-hint', escapeHtml(RESULTS_HINT)));
            overlay.appendChild(card);
            publicApi._status = status;
            attachCardKeys();
            // Nothing was focused before: Enter and Space now play again from the keyboard.
            if (typeof again.focus === 'function') again.focus();
        }

        // THE RESULTS CARD'S KEYS (backlog 314), ResultsScreen's: hold ` (HotkeyRetryOverlay, the
        // same hold and fill as mid-play) retries straight into begin(); Escape (GlobalAction.Back)
        // and Ctrl+` (QuickExit, on press) go back. Everything else is left to the browser, so Tab,
        // Enter and Space work the focused button.
        function attachCardKeys() {
            removeCardKeys();
            cardKeyHandler = function (e) {
                if (isHoldKey(e)) {
                    e.preventDefault();
                    if (e.repeat || e.metaKey) return;
                    if (e.ctrlKey) { abortHold(); leaveResults(); }
                    else beginHold('retry', retryFromResults);
                    return;
                }
                if (e.key === 'Escape') {
                    e.preventDefault();
                    if (!e.repeat) leaveResults();
                }
            };
            cardKeyUpHandler = function (e) { if (hold && isHoldKey(e)) abortHold(); };
            document.addEventListener('keydown', cardKeyHandler, true);
            document.addEventListener('keyup', cardKeyUpHandler, true);
            window.addEventListener('blur', abortHold);
        }

        function removeCardKeys() {
            if (cardKeyHandler) { document.removeEventListener('keydown', cardKeyHandler, true); cardKeyHandler = null; }
            if (cardKeyUpHandler) { document.removeEventListener('keyup', cardKeyUpHandler, true); cardKeyUpHandler = null; }
            window.removeEventListener('blur', abortHold);
        }

        function retryFromResults() {
            if (running || !audioCtx) return;
            removeCardKeys();
            if (audioCtx.state !== 'running') {
                try { const p = audioCtx.resume(); if (p && p.catch) p.catch(function () {}); } catch (e) {}
            }
            begin();
        }

        // Back: to the host when there is one, else to the player's own start gate.
        function leaveResults() {
            removeCardKeys();
            abortHold();
            if (opts.onExit) { destroy(); opts.onExit(); return; }
            showStartGate('press space to start');
        }

        function statCellOf(cell) {
            const marker = cell.perfect ? '<span class="tb-result-perfect">perfect</span>' : '';
            return `<div class="tb-result-cell tb-result-${cell.key}"><span class="tb-result-val">${escapeHtml(cell.value)}${marker}</span><span class="tb-result-lbl">${escapeHtml(cell.label)}</span></div>`;
        }

        // --- public api ------------------------------------------------------
        const publicApi = {
            beatmap,
            destroy,
            get engine() { return engine; },
            // Read-only views of the run's clock and pause state (backlog 311), for the lifecycle
            // harness; nothing on the page reads them.
            now: nowMs,
            get paused() { return paused; },
            get running() { return running; },
            get playbackValidity() { return { valid: validity.valid, discrepancies: validity.discrepancies }; },
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
            (buf) => {
                // The map's own track gain, baked into the samples once here, ahead of the fixed 0.1
                // level startSourceAt applies, so a boosted map clips where the desktop's does.
                if (beatmap.audioGain !== Core.constants.DEFAULT_AUDIO_GAIN) {
                    const channels = [];
                    for (let c = 0; c < buf.numberOfChannels; c++) channels.push(buf.getChannelData(c));
                    Core.applyTrackGain(channels, beatmap.audioGain);
                }
                audioBuffer = buf;
                showStartGate('press space to start');
            },
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
    // The playback-validity veto (backlog 312), for PlaybackValidityHarness's fake-clock pins.
    Core.playbackValidity = {
        makePlaybackValidity,
        PLAYBACK_DISCREPANCY_MS,
        ALLOWED_PLAYBACK_DISCREPANCIES
    };
    // The pause surface (backlog 311), for PlayerPauseHarness's pins.
    Core.pause = {
        PAUSE_COOLDOWN_MS,
        HOLD_TO_CONFIRM_MS,
        PAUSE_HINT,
        pauseCooldownActive,
        inSkippableInstrumental
    };
    // The end of play (backlog 314), for the same harness.
    Core.endOfPlay = {
        RESULTS_DISPLAY_DELAY_MS,
        LINES_FADE_OUT_MS,
        FAIL_WIND_DOWN_MS,
        FAIL_FILTER_CUTOFF_HZ,
        FAIL_VOLUME,
        LOWPASS_OPEN_HZ,
        RESULTS_HINT,
        outCubicCurve
    };
    Core.escapeHtml = escapeHtml;

    // Read-only surface for the display-math fidelity harness. Nothing in typebeat-core.js
    // reads any of it; it exists so the ported presentation formulas can be pinned by a test
    // instead of only by eye.
    Core.display = {
        dampContinuously,
        lastUnitEndOf,
        buildSungPoints,
        sungPositionAt,
        cueBar,
        caretAlpha,
        makeCaretFade,
        rollingWpmValue,
        makeRollingWpm,
        cellClass,
        cellGlyph,
        // The syllable and pace reading aids (backlog 317): the sung-syllable feed and the underline
        // pace hue's port of UnderlinePace, pinned by PlayerDisplayHarness and, for the bands,
        // against UnderlinePace.BuildBands in WireCompat.
        currentSyllableIn,
        paceColourForRank,
        paceColourForPreviousSpeed,
        paceSegmentLine,
        paceRanksOf,
        buildPaceBands,
        buildRankedPaceBands,
        paceColourCss,
        // The cell-state feedback (backlog 316): the space error dot rule and its pulse curve,
        // pure, so the display harness pins them against the desktop's SpaceErrorDotTest cases.
        isWordGap,
        spaceErrorDots,
        spaceErrorDotPulseScale,
        SPACE_ERROR_DOTS_ENABLED,
        SPACE_ERROR_DOT_PULSE_MS,
        SPACE_ERROR_DOT_PULSE_SCALE,
        TYPO_SHAKE_MS,
        cueTargetLine,
        sungLineFor,
        sweepFillFor,
        caretsVisible,
        outQuint,
        // The results card's Discord nudge (backlog 289). Both halves are pure, so the harness can
        // drive the render decision and the markup without a DOM: the storage half of the same
        // feature lives in play.js, which publishes it as window.TypeBeatPlayPage.
        nudgeUrlFor,
        nudgeHtml,
        // The instrumental-skip rule (a third copy of a cross-repo-pinned one), exported so
        // WebplayDisplayTest can hold it against the server's own InstrumentalGaps.Compute.
        firstVocalTime,
        lastTypeableTarget,
        computeGaps,
        skippableMs,
        gameplayStartTime,
        introSkipTarget,
        upcomingLineIndex,
        skipAllowed,
        skipTargetAt,
        skipAllowedFor,
        pendingSkipTargetFor,
        // The key handler (backlog 305), exported so the display harness can drive keydowns
        // between fake ticks and the cross-repo parity test can hold its protocol against the
        // desktop's.
        roundHalfEven,
        isWordGesture,
        spaceIsDropped,
        routeKeyDown,
        // Keystroke to character (backlog 309), pinned by the display harness and held against
        // the game's KeyCharMap by WireCompat's KeyToCharParityTest.
        keyToChar,
        // The HP bar and the start gate's copy (backlog 306), exported so the display harness pins
        // the bar's read of the health account and the copy's HP clause.
        healthBar,
        START_GATE_HINT,
        LOW_HEALTH_THRESHOLD,
        // The live HUD's readouts and song progress (backlog 319), pure, so the display harness
        // pins them off real runs.
        hudReadouts,
        fmtAccuracy,
        songProgressBounds,
        songProgressAt,
        fmtSongTime,
        // The cue and push-warning timing (backlog 318), pinned by the display harness against the
        // desktop's LyricStage.
        firstTypeableIndex,
        approachCueBars,
        pushWarningOpensAt,
        pushWarningBar,
        songWindowClosesAt,
        PUSH_FADE_IN_MS,
        // The results card's content (backlog 320), pure, so the display harness pins the miss
        // fold, the combo out of its maximum, a failed run's judged accuracy and the metadata.
        resultCells,
        resultMetaHtml,
        constants: {
            CUE_LEAD_MS, CUE_BAR_MAX_PX, CARET_DAMP_HALF_TIME, SUNG_DAMP_HALF_TIME,
            CARET_BLINK_PERIOD, LINE_SCROLL_MS, CARET_SNAP_FACTOR, PERFECT_POP_MS,
            ROLLING_WPM_WINDOW, GAP_CHIP_MIN_MS,
            MIN_GAP_MS, GAP_START_SETTLE_MS, MIN_SKIP_WINDOW_MS, SKIP_LEAD_MS,
            CARET_FADE_MS
        }
    };
})(window.TypeBeatCore);
