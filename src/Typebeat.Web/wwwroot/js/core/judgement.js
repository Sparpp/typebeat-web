/*
 * core/judgement.js: the judgement windows and the result a judgement maps to (mirrors
 * Gameplay/Judgement.cs, SyncWindows).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

    // ---------------------------------------------------------------------------
    // Judgement windows (Judgement.cs SyncWindows).
    // ---------------------------------------------------------------------------
    // ONE LADDER FOR EVERY CELL, symmetric around the cell's target: milliseconds between the
    // keypress and the target, with the same bound on either side. Unreliable timing buys no extra
    // tolerance, and a well-subdivided map is not judged more tightly than a coarse one.
    // History: docs/typebeat-core-history.md, "The judgement ladder".
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

    // A FREESTYLE cell is graded on a WIDER ladder than a normal one (PR 16, mirrors
    // SyncWindows.FREESTYLE_WINDOW_SCALE and TypingEngine.WindowsFor): every bound is doubled, so a
    // slot that accepts any key also accepts it across twice the span. It is the same scale on both
    // sides, so a wrong-key read is a 'Premature'/'Lagging' only past the doubled bound.
    const FREESTYLE_WINDOW_SCALE = 2;

    const FREESTYLE_WINDOWS = {
        ge: GREAT_WINDOW_MS * FREESTYLE_WINDOW_SCALE, gl: GREAT_WINDOW_MS * FREESTYLE_WINDOW_SCALE,
        oe: OK_WINDOW_MS * FREESTYLE_WINDOW_SCALE, ol: OK_WINDOW_MS * FREESTYLE_WINDOW_SCALE,
        me: MEH_WINDOW_MS * FREESTYLE_WINDOW_SCALE, ml: MEH_WINDOW_MS * FREESTYLE_WINDOW_SCALE
    };

    // The ladder a cell is graded against: its own widened one for a freestyle slot, the common one
    // otherwise. Mirrors TypingEngine.WindowsFor.
    function windowsFor(cell) { return cell.freestyle ? FREESTYLE_WINDOWS : WINDOWS; }

    function classify(delta, w) {
        if (delta >= -w.ge && delta <= w.gl) return 'Great';
        if (delta >= -w.oe && delta <= w.ol) return 'Ok';
        if (delta >= -w.me && delta <= w.ml) return 'Meh';
        return delta < -w.me ? 'Premature' : 'Lagging';
    }

    // TypeBeatResultMapping.AwardedTier (backlog 210). The tier a correct keypress is AWARDED, given
    // the tier the clock classified it as and whether its cell held a wrong character before it was
    // ever judged: a CORRECTED cell resolves at min(that tier, 'Ok'), so perfect play strictly beats
    // corrected play per cell.
    // History: docs/typebeat-core-history.md, "The correction cap (awardedTier)".
    //
    // ONLY 'Great' MOVES, and that is the whole of the min(): of the tiers a correct press can be
    // classified as, 'Great' is the only one ABOVE 'Ok'. 'Meh' is already below the cap, and
    // 'Premature' / 'Lagging' are off the ladder entirely (they resolve as a 'meh' through
    // toHitResult), so a capped cell struck off time is untouched here and that rule keeps its own
    // answer. Written as a min over the ladder rather than as "Great becomes Ok" because the ladder
    // is what the rule is about.
    //
    // APPLIED TO THE TIER AND NOT TO THE osu RESULT, exactly as the C# applies it, and both of that
    // decision's reasons bind here too. Everything downstream follows from the one tier (`counts`,
    // the result applyCellResult stores, the judgement onCharJudged announces to
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

    Object.assign(parts, {
        WINDOWS, FREESTYLE_WINDOW_SCALE, FREESTYLE_WINDOWS, windowsFor, classify, awardedTier,
        toHitResult
    });
    parts.loaded.push('judgement.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
