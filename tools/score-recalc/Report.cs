using System.Globalization;

namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// The dry-run report, in the order a reader needs it.
///
/// <list type="number">
/// <item>the HEADLINE: which sweep this is, what rules it judged under, and how many rows it would
/// write, which is the number <c>--expect-superseded</c> wants;</item>
/// <item>the REPRODUCTION section, which says whether any of the rest can be believed (a GATE in
/// reproduce mode, a diagnostic in supersede mode) plus, in supersede mode, the same-run gate that
/// replaces it;</item>
/// <item>the rows with NO USABLE REPLAY, per cause, with what the policy does with each;</item>
/// <item>the before/after TABLE, one line per moved score across all six values a player sees;</item>
/// <item>what moved per score, one line per field, for anything the table cannot hold;</item>
/// <item>the AGGREGATES: how many move, rank changes, distributions, the biggest movers in BOTH
/// directions, and what a leaderboard does about it.</item>
/// </list>
///
/// <para>A supersede report is read to make a decision, not to confirm one, so it leads with the
/// things that would change a mind: the cost of the rules being applied, the rows nobody can check,
/// and the board places that move.</para>
/// </summary>
public static class Report
{
    /// <summary>Rows of the before/after table printed before it starts pointing at --out instead.</summary>
    private const int table_cap = 200;

    public static void Print(IReadOnlyList<RecalcResult> results, WritePlan plan, bool wholeTable, TextWriter output)
    {
        var recalculated = results.Where(r => r.Recalculated).ToList();
        var skipped = results.Where(r => !r.Recalculated).ToList();

        PrintHeadline(results, plan, output);
        PrintReproduction(results, recalculated, skipped, plan, output);
        PrintUnreplayable(plan, output);

        if (plan.Mode == RecalcMode.Supersede)
            PrintBeforeAfterTable(plan, wholeTable, output);

        PrintMoves(recalculated, output);
        PrintAggregates(recalculated, plan, output);
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintHeadline(IReadOnlyList<RecalcResult> all, WritePlan plan, TextWriter output)
    {
        output.WriteLine();

        if (plan.Mode == RecalcMode.Supersede)
        {
            output.WriteLine("== SUPERSEDE: every stored number below is being REPLACED ==");
            output.WriteLine();
            output.WriteLine("judged under                 TypoRule.Deferred + ComboRestoreRule.OnFix + SpaceTimingRule.Untimed");
            output.WriteLine("                             + RateWindowRule.ScaledByRate + OffTimeRule.MehHit");
            output.WriteLine("                             + CorrectionCreditRule.Capped + UnfixedTypoWorthRule.Nothing");
            output.WriteLine("                             (all of today's rules)");
            output.WriteLine("total_score priced with      today's mod multipliers");
            output.WriteLine();
            output.WriteLine("What that costs, deliberately, and what to look for in the numbers below:");
            output.WriteLine("  - every stored score containing a FIXED typo GAINS max_combo, total_score and pp,");
            output.WriteLine("    because correcting a typo now resumes the streak its keypress broke.");
            output.WriteLine("  - ...and LOSES accuracy on each of those fixed cells. Backlog 210 caps a corrected cell");
            output.WriteLine("    at Ok, so a `great` earned by a fix struck inside the Great window is re-judged as an");
            output.WriteLine("    `ok` and a fixed typo no longer scores identically to a clean play. Nothing else about");
            output.WriteLine("    such a row moves on this axis: max_combo, misses, mistypes, completion and rank are the");
            output.WriteLine("    same under both arms, because a capped cell is still a hit that counts as typed.");
            output.WriteLine("  - EVERY row is re-graded on a ladder it was NOT played on. The three-tier ladder");
            output.WriteLine("    (Line / Word / Syllable, late-biased, chosen per cell from the map's granularity) was");
            output.WriteLine("    retired for ONE symmetric set of windows: Great 150 ms, Ok 300, Meh 600. No CONFIG bit");
            output.WriteLine("    records which ladder a run was played against, so there is no axis to hold at the");
            output.WriteLine("    row's own value and nothing to search: the retune is simply applied. Tier counts move");
            output.WriteLine("    in both directions, and the tighter Great window moves most of them down.");
            output.WriteLine("  - a row stored in the backlog 133-to-147 window (a `perfect` key in its statistics) is");
            output.WriteLine("    the one whose mismatch has a different SHAPE on top of that: it was graded on");
            output.WriteLine("    CHARACTER DISTANCE in four tiers, so its per-cell maximum changes key as well.");
            output.WriteLine("  - EVERY row gains on its SPACES. Backlog 148 took the spacebar out of the timing");
            output.WriteLine("    challenge, so a loosely hit space that used to be an Ok, a Meh or a combo-breaking");
            output.WriteLine("    Lagging press is re-judged as a top-tier hit. Every map has spaces, so no row is");
            output.WriteLine("    indifferent to this one.");
            output.WriteLine("  - a DT / NC / HT row is re-judged on windows SCALED by its clock rate (backlog 150), so");
            output.WriteLine("    an up-rate row gains tolerance and a down-rate row loses it. No other row is affected.");
            output.WriteLine("  - EVERY row that ever fumbled a beat gains on its OFF-TIME presses. Backlog 199 made the");
            output.WriteLine("    right character struck outside every window a HIT worth no points: it is re-judged as a");
            output.WriteLine("    Meh rather than a Miss, so the row loses that miss, keeps the streak the press used to");
            output.WriteLine("    break, and gains max_combo, completion and rank while paying the same accuracy.");
            output.WriteLine("  - a row whose mods were retuned since it was played is re-priced at today's multiplier,");
            output.WriteLine("    unlike a reproduce sweep, which carries the row's own. That is the point: a superseded");
            output.WriteLine("    score has to be one today's client could actually produce.");
        }
        else
        {
            output.WriteLine("== REPRODUCE: verify, then reprice what today's TYPO rule alone changes ==");
            output.WriteLine();
            output.WriteLine("judged under                 TypoRule.Deferred, every other era axis held at the era proved");
            output.WriteLine("                             for the ROW (combo restore, the spacebar, the rate windows, the");
            output.WriteLine("                             off-time rule, the correction cap)");
            output.WriteLine("verified against             the typo rule that judged the ROW: Deferred where the row's own");
            output.WriteLine("                             statistics prove it (a `good` key), ImmediateMiss otherwise");
            output.WriteLine("                             and the spacebar / rate windows / combo restore / off-time rule /");
            output.WriteLine("                             correction cap that judged the ROW, proved by reconstruction where the oldest");
            output.WriteLine("                             arms do not re-derive it");
            output.WriteLine("total_score priced with      the row's OWN mod multiplier, recovered not reapplied");
        }

        output.WriteLine();
        output.WriteLine($"scores considered            {all.Count}");
        output.WriteLine($"re-derived                   {all.Count(r => r.Recalculated)}");
        output.WriteLine($"values moved                 {plan.Rejudged.Count}");

        if (plan.Unranked.Count > 0)
            output.WriteLine($"unranked by policy           {plan.Unranked.Count}");

        // Named, never folded into a reproduction percentage: a row from that window is a different
        // thing from a row that disagrees with the harness, and the operator has to see which is
        // which before deciding anything.
        output.WriteLine($"ON A DELETED LADDER          {plan.DeletedLadderWindow.Count}"
                         + (plan.Mode == RecalcMode.Supersede ? "   <- pass this to --expect-unreproducible" : string.Empty));

        // The other population that is a fact about the DATA rather than about the sweep, printed in
        // both modes for the same reason: it is what the pass is judging those rows on, and a reader
        // should not have to infer it from a reproduction rate.
        output.WriteLine($"PINNED TO TypoRule.Deferred  {plan.PinnedToTheDeferredTypoRule.Count}");

        // The third population, and the one that grows with every play (backlog 156, backlog 157,
        // backlog 158): rows the arms their own keys start them at did not re-derive, whose era the
        // pass therefore had to prove by re-deriving them under the other combinations of the
        // spacebar, rate-window, combo-restore and (where the row proves nothing about it) typo axes.
        // Which era each one landed in is broken down in the reproduction section below.
        output.WriteLine($"PINNED BY ERA RECONSTRUCTION {plan.PinnedByEraSearch.Count}");

        output.WriteLine($"ROWS THIS RUN WOULD WRITE    {plan.RowsWritten}"
                         + (plan.Mode == RecalcMode.Supersede ? "   <- pass this to --expect-superseded" : string.Empty));
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintReproduction(IReadOnlyList<RecalcResult> all, IReadOnlyList<RecalcResult> recalculated, IReadOnlyList<RecalcResult> skipped, WritePlan plan, TextWriter output)
    {
        bool supersede = plan.Mode == RecalcMode.Supersede;

        output.WriteLine();

        if (supersede)
        {
            output.WriteLine("== reproduction of the stored numbers: a DIAGNOSTIC here, not a gate ==");
            output.WriteLine();
            output.WriteLine("A row that does not reproduce is EXPECTED in this mode, and refusing on that basis would");
            output.WriteLine("refuse the whole sweep. Read this as coverage, not as a pass or a fail: a row that DOES");
            output.WriteLine("reproduce is one the tool understands completely.");
            output.WriteLine();
            output.WriteLine("NO ROW CAN BE EXPECTED TO REPRODUCE ANY MORE, and that is a change. The judgement");
            output.WriteLine("windows were retuned to one symmetric ladder with NO era bit recording which ladder a run");
            output.WriteLine("was played on, so there is nothing for the era search to hold at the row's own value and");
            output.WriteLine("every row is re-derived on windows its player never played against. A row that DOES come");
            output.WriteLine("back is one whose every press sat well inside the same rung on both ladders: welcome, but");
            output.WriteLine("luck rather than proof. This is the position the backlog 133-to-147 window (a `perfect` key");
            output.WriteLine("in its maximum_statistics) was always in, now widened to the whole table, which is why");
            output.WriteLine("superseding is the only sweep that still says something true about these rows.");
            output.WriteLine();
            output.WriteLine("The four expressible eras (the typo rule, combo restore, the untimed spacebar, the");
            output.WriteLine("rate-scaled windows) are still searched and still pinned per row: they decide WHICH RULES");
            output.WriteLine("judged the run, which the retune does not touch. What they can no longer do is make the");
            output.WriteLine("re-derivation come back exactly.");
            output.WriteLine();
            output.WriteLine("A row played SINCE the 2026-08-13 release should reproduce too (backlog 156), and so should a");
            output.WriteLine("row played since backlog 140 gave combo back for a corrected typo (backlog 157), and so should");
            output.WriteLine("one whose typo was CORRECTED and which therefore carries no key proving its typo era (backlog");
            output.WriteLine("158). Those four eras are proved per row by re-deriving it under each combination, so a client");
            output.WriteLine("updating whenever its player updates it cannot put a row on the wrong ladder. Where a row's own");
            output.WriteLine("`good` key PROVES its typo era, that proof pins it and the axis is not searched.");
        }
        else
        {
            output.WriteLine("== reproduction of the stored numbers under the rules the row was priced under ==");
        }

        var eligible = all.Where(r => r.Skip is SkipReason.None or SkipReason.NotReproducible or SkipReason.NotTheSameRun).ToList();
        int reproduced = all.Count(r => r.Reproduced);

        output.WriteLine();
        output.WriteLine($"replayable and passed        {eligible.Count}");
        output.WriteLine($"reproduced exactly           {reproduced}"
                         + (eligible.Count > 0 ? $"  ({Percent(reproduced, eligible.Count)})" : string.Empty));

        // Which rule the pass reproduced those rows UNDER, since backlog 155 it is no longer one
        // answer for the whole table. Printed in both modes, and as a count of rows rather than as a
        // share of them: it is the population whose re-derivation used to come back with every
        // uncorrected typo turned into a miss.
        int deferredEra = eligible.Count(r => r.Stored.ProvablyJudgedUnderTheDeferredTypoRule);

        output.WriteLine($"  of these, judged since 126 {deferredEra,-6}  their statistics hold an uncorrected typo (a `good` key),");
        output.WriteLine("                                     which PROVES the deferred typo rule judged them, so they are");
        output.WriteLine("                                     PINNED to it and the typo axis is not searched for them at all.");
        output.WriteLine("                                     Absence of the key proves nothing either way (a run with no typo");
        output.WriteLine("                                     LEFT STANDING has none, and a CORRECTED one leaves none), so every");
        output.WriteLine("                                     other row starts at TypoRule.ImmediateMiss as before and has the");
        output.WriteLine("                                     axis searched below if it does not come back under it.");

        PrintSearchedEras(eligible, output);
        PrintUnreproducible(eligible, plan, output);

        foreach (var group in skipped.GroupBy(r => r.Skip).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            output.WriteLine($"  skipped: {Describe(group.Key),-28} {group.Count(),5}"
                             + (all.Count > 0 ? $"  ({Percent(group.Count(), all.Count)} of all)" : string.Empty));
        }

        if (supersede)
            PrintSameRunGate(all, output);

        int totalsReproduced = recalculated.Count(r => r.Stored.TotalScore == r.OldRuleTotalScore);

        if (recalculated.Count > 0 && !supersede)
        {
            output.WriteLine($"  of those, total_score too       {totalsReproduced}  ({Percent(totalsReproduced, recalculated.Count)})");
            output.WriteLine("  (total_score is reported, not required: the server stores the CLIENT's value, and the mod");
            output.WriteLine("   score multipliers have been retuned since some plays. The row's own multiplier is carried");
            output.WriteLine("   across the recalculation rather than replaced, so this sweep never reprices a mod.)");

            var retuned = recalculated.Where(r => r.Stored.TotalScore != r.OldRuleTotalScore).ToList();

            foreach (var r in retuned.Take(10))
                output.WriteLine($"    score {r.Stored.ScoreId,-8} mods {r.Stored.ModsJson,-45} multiplier kept at {r.StoredScoreMultiplier:0.0000}");
        }

        var failures = skipped.Where(r => r.Skip == SkipReason.NotReproducible).ToList();

        if (failures.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  scores the old rule did NOT reproduce (nothing is written for these):");

            foreach (var r in failures.Take(40))
                output.WriteLine($"    score {r.Stored.ScoreId,-8} {r.Detail}");

            if (failures.Count > 40)
                output.WriteLine($"    ... and {failures.Count - 40} more");
        }

        var unavailable = skipped.Where(r => r.Skip is SkipReason.BeatmapUnavailable or SkipReason.BeatmapReuploaded)
                                 .Select(r => r.Detail)
                                 .Distinct()
                                 .ToList();

        if (unavailable.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"  beatmap hashes that did not resolve ({unavailable.Count}):");

            foreach (string? hash in unavailable.Take(10))
                output.WriteLine($"    {hash}");
        }
    }

    /// <summary>
    /// WHICH SPACEBAR, RATE-WINDOW, COMBO-RESTORE, TYPO, OFF-TIME AND CORRECTION-CREDIT ERA the pass
    /// reproduced these rows under,
    /// counted per combination (backlog 156, backlog 157, backlog 158). None of the first three leaves
    /// a key in the row at all, and the typo rule leaves one only when the typo was LEFT STANDING, so
    /// for the rows in here there was nothing to read: a row its own starting point does not re-derive
    /// is re-derived under each remaining combination and pinned to the one that reproduces it EXACTLY,
    /// which proves the era rather than inferring it from a submission time the client's update
    /// schedule makes a lie.
    ///
    /// <para>THE TYPO AXIS APPEARS HERE ONLY FOR ROWS THAT COULD NOT PROVE IT, which is the line
    /// between this section and the pinned count above and is worth not misreading. A row carrying a
    /// <c>good</c> key is counted above, is pinned by that proof, and never has this axis searched. A
    /// row here had no key to carry, either because nothing was left standing or because the typo was
    /// CORRECTED, and the second of those is a case the two rules genuinely disagree about.</para>
    ///
    /// <para>Printed as a breakdown rather than a single count because which era the searched rows
    /// landed in is the finding. Every row submitted since the 2026-08-13 release is in the searched
    /// population, so the line for the all-live combination growing run over run is the expected
    /// reading, and that line staying at zero while new rows appear would mean the search is not
    /// reaching them. A COMBO-RULE OR TYPO-RULE PIN IS VISIBLE HERE RATHER THAN SILENT for the same
    /// reason: the combination is spelled out on all seven axes, so a row reconstructed onto today's
    /// combo rule, today's typo rule, today's off-time rule or today's correction cap says so. The
    /// WORTH axis is spelled out too, and reads as the stored arm on all but the rows a later
    /// candidate carried onto the live one: it moves neither quantity the gate compares, so nothing
    /// can reconstruct it (see Recalculation.SearchedEra).</para>
    ///
    /// <para>Rows no combination reproduced are NOT here. They have no era, they are counted as
    /// unexplained below, and that is the property worth checking: a search that always found an
    /// answer would turn the reproduce pass from a proof into a shrug.</para>
    /// </summary>
    private static void PrintSearchedEras(IReadOnlyList<RecalcResult> eligible, TextWriter output)
    {
        var searched = eligible.Where(r => r.EraProvedByReconstruction).ToList();

        output.WriteLine($"  of these, era proved by    {searched.Count,-6}  the arms their own keys start them at did not re-derive");
        output.WriteLine("  reconstruction                     them, so each remaining combination of the spacebar, rate-window,");
        output.WriteLine("                                     combo-restore, typo, off-time, correction-credit and unfixed-typo-worth");
        output.WriteLine("                                     axes was tried and pinned to");
        output.WriteLine("                                     the one that reproduced it exactly. A row whose `good` key PROVES its typo");
        output.WriteLine("                                     era is not searched on that axis: the proof wins. No release time is");
        output.WriteLine("                                     used anywhere here, since a client updates when its player updates");
        output.WriteLine("                                     it and a submission time therefore cannot date the rules.");

        if (searched.Count == 0)
            return;

        foreach (var group in searched.GroupBy(r => r.ReproducedUnderEra!.Value)
                                      .OrderByDescending(g => g.Count())
                                      .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            output.WriteLine($"      {group.Key,-200} {group.Count(),5}");
        }
    }

    /// <summary>
    /// The rows that did NOT reproduce, split by WHY, which is the split this section has always
    /// existed to make. It used to be "from the backlog 133-to-147 window" against "unexplained",
    /// because only that window was unreproducible by construction while anything else failing was a
    /// fact nobody had named.
    ///
    /// <para>THE WINDOW RETUNE COLLAPSED THAT SPLIT: with one symmetric ladder and no era bit
    /// recording which ladder a run was played against, EVERY row is re-derived on windows it was not
    /// played on, so a row failing to reproduce is expected everywhere and there is no "unexplained"
    /// population left to name. What is still worth separating, and is printed instead, is the
    /// four-tier CHARACTER-DISTANCE window (see
    /// <see cref="StoredScore.JudgedOnTheFourTierCharacterLadder"/>), whose rows do not merely land
    /// on different rungs but change the KEY their per-cell maximum is stored under.</para>
    ///
    /// <para>The classification still comes from the server's own era discriminator, so it cannot
    /// start disagreeing with the code that prices those rows.</para>
    /// </summary>
    private static void PrintUnreproducible(IReadOnlyList<RecalcResult> eligible, WritePlan plan, TextWriter output)
    {
        var didNot = eligible.Where(r => !r.Reproduced).ToList();
        var fourTier = didNot.Where(r => r.Stored.JudgedOnTheFourTierCharacterLadder).ToList();

        output.WriteLine($"did not reproduce            {didNot.Count}"
                         + (eligible.Count > 0 ? $"  ({Percent(didNot.Count, eligible.Count)})" : string.Empty));
        output.WriteLine("  on a retuned ladder               EVERY row is re-derived on one symmetric set of windows");
        output.WriteLine("                                     (150 / 300 / 600) that no stored row was played against,");
        output.WriteLine("                                     and no era bit records which ladder judged it. Expected,");
        output.WriteLine("                                     and no longer a signal about any individual row.");
        output.WriteLine($"  of those, 133-to-147       {fourTier.Count,-6}  ALSO graded on character distance in four tiers, so");
        output.WriteLine("                                     their per-cell maximum changes key, not just its rung.");
        output.WriteLine($"  reproduced anyway          {eligible.Count - didNot.Count,-6}  every press sat inside the same rung on both");
        output.WriteLine("                                     ladders. Luck, not verification.");

        int elsewhere = plan.DeletedLadderWindow.Count - didNot.Count - (eligible.Count - didNot.Count);

        if (elsewhere > 0)
        {
            output.WriteLine($"  ({plan.DeletedLadderWindow.Count} row(s) in this run were played on a retired ladder in all; the other {elsewhere}");
            output.WriteLine("   had no usable replay, so the sweep never tried to reproduce them.)");
        }

        // THE PER-ROW LISTING IS GONE, and its absence is the point. A supersede sweep used to print
        // every non-reproducing row that was NOT from the 133-to-147 window, because such a row was
        // the one thing in this section an operator was being asked to act on. With the ladder
        // retuned and no era bit carrying it, that population is now the whole table and the listing
        // would be twenty arbitrary rows of expected noise per run: it would train a reader to skip
        // the section that still matters. Window rows were never listed either, and for the same
        // reason, that there is nothing to look at.
        //
        // What catches a row the tool does not understand is the same-run gate printed immediately
        // below, which compares the quantities a re-judgement cannot move. That is now the only
        // refusal a supersede sweep has, and it is where an operator should be reading.
    }

    /// <summary>
    /// The predicate that REPLACES reproduction as the supersede sweep's gate, reported next to it so
    /// nobody reads a mode with no gate into the section above. It is not a looser reproduction
    /// check: it compares quantities a re-judgement cannot move (how many cells the map has, how many
    /// the run judged, whether every frame was consumed), so it still catches a replay that is not
    /// about this row while tolerating the tier changes that are the whole point of the sweep.
    /// </summary>
    private static void PrintSameRunGate(IReadOnlyList<RecalcResult> all, TextWriter output)
    {
        var refused = all.Where(r => r.Skip == SkipReason.NotTheSameRun).ToList();
        int gated = all.Count(r => r.Recalculated) + refused.Count;

        output.WriteLine();
        output.WriteLine("  the gate that DOES apply here: same run, same map");
        output.WriteLine($"    passed                     {gated - refused.Count} of {gated}");
        output.WriteLine($"    REFUSED                    {refused.Count}");

        if (refused.Count == 0)
        {
            output.WriteLine("    (cell counts and consumed frames agree everywhere, so every row above describes the");
            output.WriteLine("     play its replay recorded. Only the JUDGEMENT of it moves.)");
            return;
        }

        output.WriteLine();
        output.WriteLine("    These are NOT the expected pre-133 mismatch. The replay and the row disagree about the");
        output.WriteLine("    run itself, so any number written for them would be a guess. supersede-apply refuses to");
        output.WriteLine("    run while they are outstanding unless --allow-refused-rows is passed.");

        foreach (var r in refused.Take(40))
            output.WriteLine($"      score {r.Stored.ScoreId,-8} {r.Detail}");

        if (refused.Count > 40)
            output.WriteLine($"      ... and {refused.Count - 40} more");
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintUnreplayable(WritePlan plan, TextWriter output)
    {
        if (plan.Unreplayable.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine("== rows with no usable replay ==");
        output.WriteLine();

        if (plan.Mode == RecalcMode.Supersede)
        {
            output.WriteLine("Backlog 136 asks for an explicit decision on these rather than a default, so there is no");
            output.WriteLine("default: supersede-apply refuses until every case BELOW has a --unreplayable policy, and");
            output.WriteLine("never asks about a case this data does not contain.");
            output.WriteLine();
        }

        foreach (var (value, rows) in plan.Unreplayable.OrderBy(kvp => WritePlan.Name(kvp.Key), StringComparer.Ordinal))
        {
            string policy = plan.Mode != RecalcMode.Supersede
                ? "left as stored"
                : plan.Policy.TryGetValue(value, out var chosen)
                    ? chosen == UnreplayablePolicy.Unrank ? "UNRANK (leaves the leaderboard)" : "keep (stays exactly as stored)"
                    : "NO POLICY GIVEN, apply will refuse";

            output.WriteLine($"  {WritePlan.Name(value),-18} {rows.Count,6}   {policy}");
            output.WriteLine($"      {Advice(value)}");

            // Every undecodable replay is listed by id: it is evidence of a storage bug rather than
            // a fact about a play, and the answer to a storage bug is to look at it.
            if (value == UnreplayableCase.Unreadable)
            {
                foreach (var r in rows.Take(40))
                    output.WriteLine($"        score {r.Stored.ScoreId}");

                if (rows.Count > 40)
                    output.WriteLine($"        ... and {rows.Count - 40} more");
            }
        }
    }

    private static string Advice(UnreplayableCase value) => value switch
    {
        UnreplayableCase.NoReplay =>
            "no replay was ever stored. Unverifiable, not wrong; keeping it leaves a real placement alone and "
            + "ScoringContract.JudgedUnderTheFourthTier still reads its own era's keys correctly.",
        UnreplayableCase.Unreadable =>
            "stored bytes did not decode. This is evidence of a storage or encoding bug, not a fact about the "
            + "play, so investigate rather than rewrite. Ids listed below.",
        UnreplayableCase.EmptyReplay =>
            "the file exists but holds no typing frames, so the run was never recorded. Same standing as no-replay.",
        UnreplayableCase.BeatmapMissing =>
            "the .osu could not be fetched though the row's map still hashes to it: a FETCH failure, fixable by "
            + "re-running where the packages are reachable. supersede-apply refuses while any remain.",
        UnreplayableCase.BeatmapChanged =>
            "the set was re-uploaded, so the exact .osu the run was judged on is gone. Re-running cannot fix it, "
            + "and judging the run against the map's current cells would score a different map.",
        UnreplayableCase.FailedRun =>
            "passed = false. Health is not simulated and both 109 and 133 moved when a cell costs it, so where this "
            + "run would have ended today is not derivable. It is also not competing for a board place.",
        UnreplayableCase.Pruned =>
            "the replay existed and the server's retention sweep deleted it (never a top play on its board, never pinned). "
            + "Recommended keep. A bare keep|unrank does not cover this case: pass pruned=keep or pruned=unrank.",
        _ => string.Empty,
    };

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The artifact a supersede decision is actually made from: one line per moved score carrying
    /// every value a player sees, stored on the left and superseding on the right. The stored side is
    /// the ROW's own value, never the tool's re-derivation of it, because the row is what the
    /// leaderboard shows today and the row is what is being replaced.
    /// </summary>
    private static void PrintBeforeAfterTable(WritePlan plan, bool wholeTable, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== per-score before/after (stored -> superseding) ==");
        output.WriteLine();

        if (plan.Rejudged.Count == 0)
        {
            output.WriteLine("no stored score moves under today's rules.");
            return;
        }

        output.WriteLine($"{"score",-9}{"map",-7}{"accuracy",-19}{"completion",-19}{"rank",-11}{"max_combo",-16}{"total_score",-22}pp");

        var shown = wholeTable ? plan.Rejudged : plan.Rejudged.Take(table_cap).ToList();

        foreach (var r in shown)
        {
            output.WriteLine(
                $"{r.Stored.ScoreId,-9}"
                + $"{r.Stored.BeatmapId,-7}"
                + $"{Pair(Pct(r.Stored.Accuracy), Pct(r.NewAccuracy)),-19}"
                + $"{Pair(Pct(r.Stored.Completion), Pct(r.NewCompletion)),-19}"
                + $"{Pair(r.Stored.Rank, r.NewRank ?? "?"),-11}"
                + $"{Pair(r.Stored.MaxCombo.ToString(CultureInfo.InvariantCulture), r.NewMaxCombo.ToString(CultureInfo.InvariantCulture)),-16}"
                + $"{Pair(r.Stored.TotalScore.ToString(CultureInfo.InvariantCulture), r.NewTotalScore.ToString(CultureInfo.InvariantCulture)),-22}"
                + Pair(
                    r.Stored.PpKnown ? r.Stored.Pp.ToString("0.00", CultureInfo.InvariantCulture) : "n/a",
                    r.NewPp is double pp ? pp.ToString("0.00", CultureInfo.InvariantCulture) : "unranked"));
        }

        if (shown.Count < plan.Rejudged.Count)
        {
            output.WriteLine($"... and {plan.Rejudged.Count - shown.Count} more. Pass --full-table for all of them, or --out <file.json>");
            output.WriteLine("    for the machine-readable detail (which is always complete).");
        }

        if (plan.Rejudged.Any(r => !r.Stored.PpKnown))
        {
            output.WriteLine();
            output.WriteLine("pp before reads n/a where the stored value was not available. An offline run has no pp at");
            output.WriteLine("all unless --scores supplies it: an .osr does not carry one.");
        }
    }

    private static string Pair(string before, string after) => before == after ? before : $"{before} -> {after}";

    // -----------------------------------------------------------------------------------------

    private static void PrintMoves(IReadOnlyList<RecalcResult> recalculated, TextWriter output)
    {
        var moved = recalculated.Where(r => r.Moves).OrderBy(r => r.Stored.ScoreId).ToList();

        output.WriteLine();
        output.WriteLine("== per-score changes ==");
        output.WriteLine();

        if (moved.Count == 0)
        {
            output.WriteLine("no score changes under the new rule.");
            return;
        }

        foreach (var r in moved)
        {
            output.WriteLine($"score {r.Stored.ScoreId} (beatmap {r.Stored.BeatmapId})");

            foreach (string line in FieldChanges(r))
                output.WriteLine($"    {line}");
        }
    }

    /// <summary>One line per field that actually moves; a field that stands still says nothing.</summary>
    public static IEnumerable<string> FieldChanges(RecalcResult r)
    {
        var was = WireCounts.Parse(r.Stored.StatisticsJson);
        var now = r.NewStatistics ?? new Dictionary<string, int>();

        foreach (string key in was.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            int a = was.GetValueOrDefault(key);
            int b = now.GetValueOrDefault(key);

            if (a != b)
                yield return $"statistics.{key,-12} {a,8} -> {b,-8} ({Signed(b - a)})";
        }

        if (r.Stored.MaxCombo != r.NewMaxCombo)
            yield return $"max_combo             {r.Stored.MaxCombo,8} -> {r.NewMaxCombo,-8} ({Signed(r.NewMaxCombo - r.Stored.MaxCombo)})";

        if (r.Stored.TotalScore != r.NewTotalScore)
            yield return $"total_score           {r.Stored.TotalScore,8} -> {r.NewTotalScore,-8} ({Signed(r.NewTotalScore - r.Stored.TotalScore)})";

        if (Math.Abs(r.Stored.Accuracy - r.NewAccuracy) > 1e-9)
            yield return $"accuracy              {Pct(r.Stored.Accuracy),8} -> {Pct(r.NewAccuracy),-8}";

        if (Math.Abs(r.Stored.Completion - r.NewCompletion) > 1e-9)
            yield return $"completion            {Pct(r.Stored.Completion),8} -> {Pct(r.NewCompletion),-8}";

        if (r.Stored.Rank != r.NewRank)
            yield return $"rank                  {r.Stored.Rank,8} -> {r.NewRank,-8}";

        if (r.Stored.Ranked != r.NewRanked)
            yield return $"ranked                {r.Stored.Ranked,8} -> {r.NewRanked,-8}";

        if (r.Stored.PpKnown && r.NewPp is double pp && Math.Abs(r.Stored.Pp - pp) > 1e-9)
            yield return $"pp                    {r.Stored.Pp,8:0.00} -> {pp,-8:0.00} ({Signed(pp - r.Stored.Pp)})";

        if (r.Mode == RecalcMode.Reproduce && r.PpDeltaFromTheRuleChange is double delta && Math.Abs(delta) > 1e-9)
            yield return $"pp (rule change)      {r.OldRulePp!.Value,8:0.00} -> {r.NewPp!.Value,-8:0.00} ({Signed(delta)})";

        // Under a supersede sweep the row's own multiplier is not carried, so say when today's is
        // different from the one the row was priced with: it is a second reason total_score moved,
        // and it is not the judgement.
        if (r.Mode == RecalcMode.Supersede && r.Reproduced && Math.Abs(r.AppliedMultiplier - r.StoredScoreMultiplier) > 1e-4)
            yield return $"mod multiplier        {r.StoredScoreMultiplier,8:0.0000} -> {r.AppliedMultiplier,-8:0.0000} (today's, not the row's)";
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintAggregates(IReadOnlyList<RecalcResult> recalculated, WritePlan plan, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== aggregates ==");
        output.WriteLine();

        if (recalculated.Count == 0)
        {
            output.WriteLine("nothing was recalculated.");
            return;
        }

        var moved = recalculated.Where(r => r.Moves).ToList();

        output.WriteLine($"recalculated                 {recalculated.Count}");
        output.WriteLine($"unchanged in every field     {recalculated.Count - moved.Count}  ({Percent(recalculated.Count - moved.Count, recalculated.Count)})");
        output.WriteLine($"changed in at least one      {moved.Count}  ({Percent(moved.Count, recalculated.Count)})");
        output.WriteLine();

        Count(output, "statistics changed", recalculated, r => !SameStatistics(r));
        Count(output, "max_combo changed", recalculated, r => r.Stored.MaxCombo != r.NewMaxCombo);
        Count(output, "total_score changed", recalculated, r => r.Stored.TotalScore != r.NewTotalScore);
        Count(output, "accuracy changed", recalculated, r => Math.Abs(r.Stored.Accuracy - r.NewAccuracy) > 1e-9);
        Count(output, "completion changed", recalculated, r => Math.Abs(r.Stored.Completion - r.NewCompletion) > 1e-9);
        Count(output, "rank changed", recalculated, r => r.Stored.Rank != r.NewRank);
        Count(output, "pp changed", recalculated, r => PpDelta(r) is double d && Math.Abs(d) > 1e-9);
        Count(output, "ranked flag changed", recalculated, r => r.Stored.Ranked != r.NewRanked);

        var rankChanges = recalculated.Where(r => r.Stored.Rank != r.NewRank)
                                      .GroupBy(r => $"{r.Stored.Rank} -> {r.NewRank}")
                                      .OrderByDescending(g => g.Count())
                                      .ToList();

        if (rankChanges.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("rank changes:");

            foreach (var g in rankChanges)
                output.WriteLine($"  {g.Key,-12} {g.Count(),5}");
        }

        PrintDistribution(output, "max_combo delta", recalculated.Select(r => (double)(r.NewMaxCombo - r.Stored.MaxCombo)).ToList(), "0");
        PrintDistribution(output, "total_score delta", recalculated.Select(r => (double)(r.NewTotalScore - r.Stored.TotalScore)).ToList(), "0");

        var ppDeltas = recalculated.Select(PpDelta).OfType<double>().ToList();

        if (ppDeltas.Count > 0)
            PrintDistribution(output, plan.Mode == RecalcMode.Supersede ? "pp delta (stored -> superseding)" : "pp delta attributable to the rule change", ppDeltas, "0.00");
        else
            output.WriteLine("\npp delta: not priced in this run (no star ratings, or no stored pp to compare against).");

        PrintLeaderboardImpact(recalculated, plan, output);

        output.WriteLine();
        output.WriteLine("not touched by this sweep, and worth knowing before applying it:");
        output.WriteLine("  - user_stats.hit_counts / total_score / play_time were accumulated at submission time from");
        output.WriteLine("    the OLD statistics and are not rewritten here, so profile aggregates keep their old values.");
        output.WriteLine("  - scores.passed is never re-derived: see the failed-run case.");
        output.WriteLine("  - a row is never re-ranked upward; it can only lose scores.ranked (the other gates, play");
        output.WriteLine("    time, build and set status at submission, are not derivable from a replay).");

        if (plan.Mode == RecalcMode.Supersede)
        {
            output.WriteLine("  - rows left as stored keep the statistics keys of whichever era judged them. That is safe");
            output.WriteLine("    to read: ScoringContract.JudgedUnderTheFourthTier tells the two eras apart by the row's");
            output.WriteLine("    own maximum_statistics, and backlog 142 keeps it alive for exactly these rows.");
        }

        PrintExtremes(output, "largest max_combo moves", recalculated, r => r.NewMaxCombo - r.Stored.MaxCombo);
        PrintExtremes(output, "largest total_score moves", recalculated, r => r.NewTotalScore - r.Stored.TotalScore);

        if (ppDeltas.Count > 0)
            PrintExtremes(output, "largest pp moves", recalculated.Where(r => PpDelta(r) is not null).ToList(), r => PpDelta(r)!.Value);
    }

    /// <summary>
    /// What the sweep does to the thing the decision is actually about. The site's leaderboard is
    /// best-per-user by total_score over ranked rows (<c>ix_scores_leaderboard</c>), so that is what
    /// is modelled here: per beatmap, who holds the top place before and after.
    ///
    /// <para>Only meaningful over an UNFILTERED run. With <c>--score</c> or <c>--limit</c> the tool
    /// is holding a slice of each board and any "the top place moved" it reported would be about the
    /// slice, so the section says so and prints nothing.</para>
    /// </summary>
    private static void PrintLeaderboardImpact(IReadOnlyList<RecalcResult> recalculated, WritePlan plan, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("leaderboard impact (best score per user, per beatmap):");

        if (plan.Filtered)
        {
            output.WriteLine("  not computed: this run selected a subset of the scores (--score or --limit), so it holds");
            output.WriteLine("  only a slice of each board and could not tell a real place change from a missing row.");
            return;
        }

        // Rows that were not re-derived still sit on the board with their stored values, so they are
        // in both sides of the comparison. Leaving them out would invent place changes.
        var all = recalculated.Concat(plan.Unreplayable.SelectMany(kvp => kvp.Value)).Concat(plan.Refused).ToList();

        if (all.All(r => r.Stored.BeatmapId == 0))
        {
            output.WriteLine("  not computed: no beatmap ids in this run (an offline run has none unless --scores gives them).");
            return;
        }

        int boards = 0, topChanged = 0, leftBoard = 0;

        foreach (var board in all.Where(r => r.Stored.BeatmapId != 0).GroupBy(r => r.Stored.BeatmapId))
        {
            var before = board.Where(r => r.Stored.Ranked)
                              .GroupBy(r => r.Stored.UserId)
                              .Select(u => u.OrderByDescending(r => r.Stored.TotalScore).ThenBy(r => r.Stored.ScoreId).First())
                              .OrderByDescending(r => r.Stored.TotalScore)
                              .ThenBy(r => r.Stored.ScoreId)
                              .ToList();

            var after = board.Where(Ranked)
                             .GroupBy(r => r.Stored.UserId)
                             .Select(u => u.OrderByDescending(Total).ThenBy(r => r.Stored.ScoreId).First())
                             .OrderByDescending(Total)
                             .ThenBy(r => r.Stored.ScoreId)
                             .ToList();

            if (before.Count == 0 && after.Count == 0)
                continue;

            boards++;
            leftBoard += board.Count(r => r.Stored.Ranked && !Ranked(r));

            long topBefore = before.Count > 0 ? before[0].Stored.ScoreId : 0;
            long topAfter = after.Count > 0 ? after[0].Stored.ScoreId : 0;

            if (topBefore != topAfter)
                topChanged++;
        }

        output.WriteLine($"  boards in this run         {boards}");
        output.WriteLine($"  boards whose #1 changes    {topChanged}" + (boards > 0 ? $"  ({Percent(topChanged, boards)})" : string.Empty));
        output.WriteLine($"  rows leaving the board     {leftBoard}");
        output.WriteLine("  (rows the sweep did not touch are included on BOTH sides at their stored values, since they");
        output.WriteLine("   stay on the board; leaving them out would invent place changes.)");
    }

    private static bool Ranked(RecalcResult r) => r.Recalculated ? r.NewRanked : r.Stored.Ranked;

    private static long Total(RecalcResult r) => r.Recalculated ? r.NewTotalScore : r.Stored.TotalScore;

    /// <summary>
    /// The pp move this report is about. In a supersede sweep that is STORED to new, because the
    /// stored value is what the site shows and what is being replaced. In a reproduce sweep it is
    /// the rule change's own contribution, which is a different and narrower question, and the one
    /// that sweep exists to answer.
    /// </summary>
    private static double? PpDelta(RecalcResult r)
        => r.Mode == RecalcMode.Supersede
            ? r.Stored.PpKnown && r.NewPp is double after ? after - r.Stored.Pp : null
            : r.PpDeltaFromTheRuleChange;

    private static bool SameStatistics(RecalcResult r)
    {
        var was = WireCounts.Parse(r.Stored.StatisticsJson);
        var now = r.NewStatistics ?? new Dictionary<string, int>();

        return was.Keys.Union(now.Keys).All(k => was.GetValueOrDefault(k) == now.GetValueOrDefault(k));
    }

    private static void Count(TextWriter output, string label, IReadOnlyList<RecalcResult> results, Func<RecalcResult, bool> predicate)
    {
        int n = results.Count(predicate);
        output.WriteLine($"{label,-28} {n,5}  ({Percent(n, results.Count)})");
    }

    private static void PrintDistribution(TextWriter output, string label, IReadOnlyList<double> values, string format)
    {
        if (values.Count == 0)
            return;

        var sorted = values.OrderBy(v => v).ToList();

        output.WriteLine();
        output.WriteLine($"{label}:");
        output.WriteLine($"  min {sorted[0].ToString(format, CultureInfo.InvariantCulture)}"
                         + $"   p05 {Quantile(sorted, 0.05).ToString(format, CultureInfo.InvariantCulture)}"
                         + $"   median {Quantile(sorted, 0.5).ToString(format, CultureInfo.InvariantCulture)}"
                         + $"   p95 {Quantile(sorted, 0.95).ToString(format, CultureInfo.InvariantCulture)}"
                         + $"   max {sorted[^1].ToString(format, CultureInfo.InvariantCulture)}");
        output.WriteLine($"  exactly zero: {sorted.Count(v => v == 0)} of {sorted.Count} ({Percent(sorted.Count(v => v == 0), sorted.Count)})");
    }

    private static double Quantile(IReadOnlyList<double> sorted, double q)
        => sorted[Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

    /// <summary>
    /// The five biggest movers in BOTH directions. Losses alone were enough for the reproduce sweep,
    /// where the typo rule can only give a cell back; a supersede sweep restores combo, so its
    /// headline effect is GAINS, and a report that only showed losses would hide the thing the
    /// decision was actually about.
    /// </summary>
    private static void PrintExtremes(TextWriter output, string label, IReadOnlyList<RecalcResult> results, Func<RecalcResult, double> delta)
    {
        var moved = results.Where(r => delta(r) != 0).ToList();

        if (moved.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine($"{label}:");

        foreach (var r in moved.OrderByDescending(delta).Take(5))
            output.WriteLine($"  gain   score {r.Stored.ScoreId,-8} {Signed(delta(r))}   (typos {Mistypes(r)}, misses {Misses(r)})");

        foreach (var r in moved.OrderBy(delta).Take(5).Where(r => delta(r) < 0))
            output.WriteLine($"  loss   score {r.Stored.ScoreId,-8} {Signed(delta(r))}   (typos {Mistypes(r)}, misses {Misses(r)})");
    }

    private static int Mistypes(RecalcResult r) => (r.NewStatistics ?? new Dictionary<string, int>()).GetValueOrDefault("combo_break");

    private static int Misses(RecalcResult r) => (r.NewStatistics ?? new Dictionary<string, int>()).GetValueOrDefault("miss");

    private static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.NoReplay => "no stored replay",
        SkipReason.ReplayPruned => "replay pruned by retention",
        SkipReason.UndecodableReplay => "replay did not decode",
        SkipReason.BeatmapUnavailable => "beatmap not fetched",
        SkipReason.BeatmapReuploaded => "beatmap re-uploaded since",
        SkipReason.EmptyReplay => "replay holds no frames",
        SkipReason.FailedRun => "failed run (not replayable)",
        SkipReason.NotReproducible => "old rule not reproduced",
        SkipReason.NotTheSameRun => "REFUSED: not the same run",
        _ => reason.ToString(),
    };

    private static string Percent(int part, int total)
        => total == 0 ? "n/a" : (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Pct(double fraction) => (100 * fraction).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static string Signed(double value)
        => (value > 0 ? "+" : string.Empty) + value.ToString(Math.Abs(value % 1) < 1e-9 ? "0" : "0.00", CultureInfo.InvariantCulture);
}
