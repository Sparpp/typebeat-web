/*
 * core/score-and-health.js: the submitted-score account, the HP pool and the final score and grade
 * (mirrors osu's ScoreProcessor as TypeBeatScoreProcessor runs it, and TypeBeatHealthProcessor).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

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
    // break that happened between two results.
    // History: docs/typebeat-core-history.md, "The incremental score account".
    //
    // What moves the C# processor, in the order the events happen (all four events below are
    // routed by TypeBeatComboFeed and TypeBeatHealthFeed, which subscribe to the engine):
    //
    //   1. TypeBeatPlayfield.applyCharJudgement -> DrawableTypeBeatHitObject.ApplyCharJudgement ->
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
    //   2. TypeBeatComboFeed.OnMistyped -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatScoreProcessor.RecordMistype. One seam for BOTH input models since backlog 109,
    //      because neither raises a result for a wrong keypress any more: a rejected key never did,
    //      and a typed-through one no longer does. osu's combo is maintained incrementally off
    //      results, so the break has to be mirrored by hand or max_combo would count on through it.
    //      Nothing else moves: no result count, no accuracy, no combo portion, and RecordMistype is
    //      a pure counter (routing it through ApplyResult would move the judged count too).
    //   3. TypeBeatComboFeed.OnComboRestored -> TypeBeatScoreProcessor.RestoreCombo (backlog 140).
    //      Correcting the cell a wrong keypress spoiled RESUMES the streak that keypress broke, and
    //      like the break in 2 it travels on no judgement result, so it is mirrored by hand at the
    //      same seam. Applied as a DELTA and pushing HighestCombo itself, for the reasons written on
    //      restoreCombo below.
    //   4. TypeBeatHealthFeed.OnWrongKeyRejected -> the mash-guard HP drain only, which moves
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
    //      (TypeBeatComboFeed.SealedCellResult -> TypeBeatScoreProcessor.MarkComboNeutral, which is
    //      markComboNeutral and the branch in applyResult below). Since backlog 259 EVERY seal
    //      result is marked, the misses included, and the seal's one combo break is mirrored by
    //      hand at the same seam instead (breakComboTo), because that break is BACK-DATED to the
    //      cells the line missed and a Miss result would land it on the run the player holds now.
    //      It is a hit for accuracy and for
    //      the note count, and NOT for completion (see computeScore), which is backlog 126: a cell
    //      typed wrong is not a cell typed, and it costs rank exactly as a miss does.
    //   6. TypeBeatComboFeed.OnWordAbandoned -> scoreProcessor.Combo.Value = 0, and
    //      TypeBeatComboFeed.OnAbandonSealed -> MarkComboNeutral on every cell the skip gave up
    //      (backlog 167). A word skip resolves NOTHING at the keypress, so like the seams in 2 and 3
    //      it has no result to carry its break and mirrors it by hand; and the Misses those cells
    //      finally take at the seal must not take that break a second time, which is the same ledger
    //      as the typo in 5, redeemed in the same place but in the opposite direction (a break to
    //      suppress rather than an increment). AbandonReclaimed moves nothing here: it carries
    //      health alone (TypeBeatHealthFeed.OnAbandonReclaimed), and lands on the health account
    //      (HealthAccount below).
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

    // type!beat's score weighting (owner, 2026-10-04): the base osu! 500000/500000 split moved onto
    // the accuracy half. The first term is scaled by cumulative COMBO POSITION, the second by map
    // PROGRESS (both multiplied by accuracy, the second to the fifth power). Mirrors
    // TypeBeatScoreProcessor.COMBO_PORTION_MAX / ACCURACY_PORTION_MAX and the server's
    // ScoringContract; a perfect play still totals exactly 1000000.
    const COMBO_PORTION_MAX = 300000;
    const ACCURACY_PORTION_MAX = 700000;
    // The accuracy term's exponent: TypeBeatScoreProcessor.ACCURACY_PORTION_EXPONENT, the server's
    // ScoringContract.AccuracyExponent.
    const ACCURACY_PORTION_EXPONENT = 5;

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

        // TypeBeatComboFeed.OnMistyped: `scoreProcessor.Combo.Value = 0`, and nothing else. Every
        // wrong keypress in either input model comes through here, because none of them raises a
        // judgement result. HighestCombo needs no update (it only ever grows, this only shrinks
        // Combo), which is exactly why a break mirrored here cannot inflate max_combo.
        breakCombo() {
            this.combo = 0;
        }

        // TypeBeatComboFeed.OnLineSealed, the hand-mirrored seal break (backlog 259):
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
    // HealthProcessor (backlog 306).
    // History: docs/typebeat-core-history.md, "The HP pool (HealthAccount)".
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
    // Scoring: standardised total_score + statistics dicts + accuracy grade.
    // Reproduces osu ScoreProcessor.ComputeTotalScore and the accuracy cutoffs.
    // ---------------------------------------------------------------------------
    // Grades are awarded on timing ACCURACY, with a missed-cell condition on SS (no cell missed at
    // all) and S (under S_MISS_LIMIT of the map's cells missed). Mirrors TypeBeatScoreProcessor's
    // ACCURACY_CUTOFF_* / S_MISS_LIMIT / RankFromAccuracy; keep the two in step.
    // History: docs/typebeat-core-history.md, "Grades".
    const ACCURACY_CUTOFF_X = 0.98;
    const ACCURACY_CUTOFF_S = 0.92;
    const ACCURACY_CUTOFF_A = 0.85;
    const ACCURACY_CUTOFF_B = 0.75;
    const ACCURACY_CUTOFF_C = 0.60;
    const S_MISS_LIMIT = 0.03;

    function rankFromAccuracy(accuracy, missedFraction) {
        const acc = Number.isFinite(accuracy) ? Math.min(1, Math.max(0, accuracy)) : 0;
        const missed = Number.isFinite(missedFraction) ? Math.min(1, Math.max(0, missedFraction)) : 1;

        if (acc >= ACCURACY_CUTOFF_X && missed === 0) return 'X';
        if (acc >= ACCURACY_CUTOFF_S && missed < S_MISS_LIMIT) return 'S';
        if (acc >= ACCURACY_CUTOFF_A) return 'A';
        if (acc >= ACCURACY_CUTOFF_B) return 'B';
        if (acc >= ACCURACY_CUTOFF_C) return 'C';
        return 'D';
    }

    // The missed fraction over judged cells, from the same counts TypeBeatScoreProcessor's
    // RankFromStatistics derives: notes are every NOTE_RESULTS count (great, ok, meh, the uncorrected
    // typo and the miss), and the uncorrected typo counts as a MISSED cell exactly as it does for pp.
    function rankFromStatistics(accuracy, counts) {
        const unfixed = counts.good || 0;
        const miss = counts.miss || 0;
        const notes = counts.great + counts.ok + counts.meh + unfixed + miss;
        const missedFraction = notes > 0 ? (miss + unfixed) / notes : 0;
        return rankFromAccuracy(accuracy, missedFraction);
    }

    // The all-Great combo portion over the typeable cells, the one term of computeScore that
    // depends on nothing the play does (and so one term of totalScoreOf too). The live HUD
    // (backlog 319) asks totalScoreOf every frame, so the loop is run once per engine and
    // remembered here, OUTSIDE the engine: a WeakMap the engine never sees, keyed on the engine
    // and checked against the lines array it walked, so a
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

    // The standardised total_score of the play so far, and nothing else: the live HUD asks for it
    // every frame, where computeScore would build a whole result (statistics, counts, rank) to
    // throw all but one field away. The same arithmetic as computeScore's totalScore, kept apart so
    // the fuzz sweep can hold the two equal after every key and frame of every run.
    function totalScoreOf(engine) {
        const total = engine.beatmap.totalCells;
        const processor = engine.processor;
        const maxComboPortion = maxComboPortionOf(engine);
        const comboProgress = maxComboPortion > 0 ? processor.comboPortion / maxComboPortion : 1;
        const accuracyProgress = total > 0 ? processor.judgementCount / total : 1;
        const accJudged = processor.maximumBaseScore > 0 ? processor.baseScore / processor.maximumBaseScore : 1;
        return Math.round(COMBO_PORTION_MAX * accJudged * comboProgress + ACCURACY_PORTION_MAX * Math.pow(accJudged, ACCURACY_PORTION_EXPONENT) * accuracyProgress);
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
        const totalWithoutMods = Math.round(COMBO_PORTION_MAX * accJudged * comboProgress + ACCURACY_PORTION_MAX * Math.pow(accJudged, ACCURACY_PORTION_EXPONENT) * accuracyProgress);
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
            rank: passed ? rankFromStatistics(acc, { great: great, ok: ok, meh: meh, good: typos, miss: miss }) : 'F',
            statistics: statistics,
            maximumStatistics: { great: total },
            // convenience for the results screen
            counts: { great, ok, meh, typos, miss, mistypes },
            wpm: engine.liveWpm
        };
    }

    Object.assign(parts, {
        COMBO_PORTION_MAX, ACCURACY_PORTION_MAX, ACCURACY_PORTION_EXPONENT, ScoreProcessorMirror,
        WRONG_KEY_FAIL_STREAK, GREAT_HEALTH_INCREASE, OK_HEALTH_INCREASE, MEH_HEALTH_INCREASE,
        MISS_HEALTH_DRAIN, WRONG_KEY_HP_DRAIN, HEALTH_EPSILON, almostBigger, HealthAccount,
        rankFromAccuracy, rankFromStatistics, uncachedMaxComboPortion, maxComboPortionOf,
        totalScoreOf, computeScore
    });
    parts.loaded.push('score-and-health.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
