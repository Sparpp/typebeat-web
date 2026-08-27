using System.Globalization;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Tools.RepriceReport;

/// <summary>
/// The report, in the order a reader needs it.
///
/// <list type="number">
/// <item>the HEADLINE: what moved, what this run could not look at, and whether the catalogue has
/// already been repriced (in which case every delta below is legitimately zero and the run proves
/// nothing about 152);</item>
/// <item>the STAR RATINGS: the constants assumed against the constants fitted back out of the data,
/// then the per-map before/after, then the three predicates 152's acceptance clause names;</item>
/// <item>the SPEC ANCHORS: the five figures 152 measured, next to what this run measures;</item>
/// <item>the pp DEFLATION: the same shape, the length factor fitted back out of the stored prices,
/// the deflation graded into length bands, and the anchors at 340 / 800 / 2300 cells;</item>
/// <item>the BOARDS, which is what the decision is actually about: who is above whom;</item>
/// <item>the VERDICT, one line per predicate, and a plain statement of what was NOT checked.</item>
/// </list>
///
/// <para>EVERY FAILING ROW IS NAMED. A summary that says "all within range" while hiding one outlier
/// is the failure mode this whole tool exists to avoid, so counts are always followed by ids, and
/// the only thing that truncates a list is <c>--full-table</c>, which says how many it hid.</para>
/// </summary>
internal static class Report
{
    private const int list_cap = 25;
    private const int table_cap = 200;

    /// <summary>
    /// The catalogue figures backlog 152 measured before the change, quoted so this run can be held
    /// against them. THEY ARE NOT ASSERTIONS: the tool prints its own numbers next to them and says
    /// how far apart they are. If the two disagree the finding is real and belongs in the writeup,
    /// not in a fudge factor here.
    /// </summary>
    private static readonly (string Fragment, string Version, double Before, double After)[] spec_maps =
    [
        ("nanana", "Extreme", 7.81, 7.97),
        ("hyper4id", "Hyper", 7.47, 7.60),
        ("riptide", "Seaside", 6.20, 6.35),
        ("spectator", "Wolf", 4.54, 4.65),
    ];

    /// <summary>The spec's catalogue MEAN, which is a population figure rather than a map.</summary>
    private static readonly (double Before, double After) spec_mean = (3.27, 3.33);

    public static void Print(RunContext run, TextWriter output)
    {
        PrintHeadline(run, output);
        PrintStarRatings(run, output);
        PrintSpecAnchors(run, output);
        PrintPp(run, output);
        PrintBoards(run, output);
        PrintVerdict(run, output);
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintHeadline(RunContext run, TextWriter output)
    {
        var resolved = run.Maps.Where(m => m.Resolved).ToList();

        output.WriteLine();
        output.WriteLine("== BACKLOG 152 REPRICE, DRY RUN: nothing here is written anywhere ==");
        output.WriteLine();
        output.WriteLine($"site                      {run.Site}");
        output.WriteLine($"beatmaps read             {run.Maps.Count}  ({resolved.Count} re-rated, {run.Maps.Count - resolved.Count} not)");
        output.WriteLine($"scores read               {run.Scores.Count}");
        output.WriteLine($"pace version              stored {Distribution(run.Maps.Select(m => m.Stored.PaceVersion))}, code is {LyricPace.VERSION}");
        output.WriteLine($"pp version                stored {Distribution(run.Scores.Select(s => s.Stored.PpVersion))}, code is {PerformancePoints.VERSION}");

        int alreadyCurrent = run.Maps.Count(m => m.Stored.PaceVersion >= LyricPace.VERSION);

        if (alreadyCurrent > 0)
        {
            output.WriteLine();
            output.WriteLine($"  NOTE: {alreadyCurrent} of {run.Maps.Count} beatmap rows are ALREADY at pace v{LyricPace.VERSION}, i.e. already");
            output.WriteLine("  repriced. Their deltas below are legitimately zero and say nothing about 152. Read this");
            output.WriteLine("  report against a snapshot taken BEFORE the deploy, or expect the SR half to be vacuous.");
        }

        if (run.UnavailableSets.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"  {run.UnavailableSets.Count} set package(s) could not be fetched: {Ids(run.UnavailableSets)}");
            output.WriteLine("  That is a FETCH failure, not a fact about the data. Their maps and scores are excluded from");
            output.WriteLine("  every predicate below and listed by id.");
        }
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintStarRatings(RunContext run, TextWriter output)
    {
        var f = run.Sr;

        output.WriteLine();
        output.WriteLine("-- STAR RATINGS ------------------------------------------------------------------");
        output.WriteLine();
        output.WriteLine($"assumed, from 152:        length_stars {Length.LENGTH_STARS:0.####}, pivot {Length.REFERENCE_CELLS:0} cells");

        if (f.FittedBonus.Samples > 0 && double.IsFinite(f.FittedBonus.Slope))
        {
            output.WriteLine($"fitted, from the data:    length_stars {f.FittedBonus.Slope:0.######} over {f.FittedBonus.Samples} map(s),");
            output.WriteLine($"                          worst residual {f.FittedBonus.MaxResidual:0.000000} stars");
            output.WriteLine("  (the fit is what validates the cell count this report computes: a wrong count cannot");
            output.WriteLine("   put a clean line through the observed deltas.)");
        }
        else
        {
            output.WriteLine("fitted, from the data:    not computed, no map above the 100-cell pivot moved");
        }

        PrintMapTable(run, output);

        output.WriteLine();
        output.WriteLine($"delta in [0, +{run.Tolerances.MaxStarGain:0.00}]           {Verdict(f.InRangePassed)}   {f.OutOfRange.Count} violation(s)");
        output.WriteLine($"monotone in cells         {Verdict(f.MonotonePassed)}   {f.MonotonicityBreaks.Count} break(s)");
        output.WriteLine($"delta IS the length bonus {Verdict(f.BonusExplainsEveryMove)}   {f.BonusMismatch.Count} map/variant(s) moved by something else");
        output.WriteLine($"crosses a whole star      {(f.WholeStarCrossing.Count == 0 ? "none" : f.WholeStarCrossing.Count + " map(s)")}");

        PrintFindings(output, "OUT OF RANGE", f.OutOfRange, run.FullTable);
        PrintFindings(output, "MOVED BY SOMETHING OTHER THAN THE LENGTH BONUS", f.BonusMismatch, run.FullTable);
        PrintFindings(output, "CROSSED A WHOLE STAR", f.WholeStarCrossing, run.FullTable);
        PrintFindings(output, "STORED char_count DISAGREES WITH THE REPARSE (stale for other reasons)", f.StaleCharCount, run.FullTable);

        if (f.MonotonicityBreaks.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("MONOTONICITY BREAKS (a longer map gained less than a shorter one):");

            foreach (var b in Take(f.MonotonicityBreaks, run.FullTable))
            {
                output.WriteLine($"  {b.Variant,-18} beatmap {b.LongerBeatmapId} ({Cells(b.LongerCells)} cells) gained {b.LongerDelta:+0.0000;-0.0000}");
                output.WriteLine($"  {string.Empty,-18} beatmap {b.ShorterBeatmapId} ({Cells(b.ShorterCells)} cells) gained {b.ShorterDelta:+0.0000;-0.0000}  <- shorter, gained more");
            }

            Hidden(output, f.MonotonicityBreaks.Count, run.FullTable);
        }

        if (f.Unresolved.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("NOT RE-RATED (excluded from every predicate above):");

            foreach (var row in Take(f.Unresolved, run.FullTable))
                output.WriteLine($"  beatmap {row.Stored.BeatmapId,-8} set {row.Stored.SetId,-6} {row.Resolution,-20} {row.Detail}");

            Hidden(output, f.Unresolved.Count, run.FullTable);
        }
    }

    private static void PrintMapTable(RunContext run, TextWriter output)
    {
        var resolved = run.Maps.Where(m => m.Resolved).OrderByDescending(m => m.New(SrVariant.Base) ?? 0).ToList();

        if (resolved.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine($"{"beatmap",-9}{"cells",-8}{"lit",-8}{"stars",-18}{"delta",-10}{"bonus",-10}{"residual",-11}map");

        foreach (var m in run.FullTable ? resolved : resolved.Take(table_cap))
        {
            double before = m.Stored.DifficultyRating;
            double after = m.New(SrVariant.Base)!.Value;

            output.WriteLine(
                $"{m.Stored.BeatmapId,-9}{Cells(m.Cells),-8}{Cells(m.LiterateCells),-8}"
                + $"{before.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + after.ToString("0.00", CultureInfo.InvariantCulture),-18}"
                + $"{(after - before).ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture),-10}"
                + $"{m.ExpectedBonus(SrVariant.Base).ToString("0.0000", CultureInfo.InvariantCulture),-10}"
                + $"{(m.Residual(SrVariant.Base) ?? 0).ToString("+0.000000;-0.000000", CultureInfo.InvariantCulture),-11}"
                + Truncate(m.Stored.Name, 60));
        }

        if (!run.FullTable && resolved.Count > table_cap)
            output.WriteLine($"  ... {resolved.Count - table_cap} more; --full-table prints them, --out writes every variant of every row");
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintSpecAnchors(RunContext run, TextWriter output)
    {
        var resolved = run.Maps.Where(m => m.Resolved).ToList();

        if (resolved.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine("-- WHAT 152 SAID THE CATALOGUE WOULD DO --------------------------------------------");
        output.WriteLine();
        output.WriteLine($"{"map",-42}{"spec",-20}{"this run",-20}agrees");

        foreach (var (fragment, version, before, after) in spec_maps)
        {
            var match = resolved.FirstOrDefault(m =>
                m.Stored.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                && m.Stored.VersionName.Contains(version, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                output.WriteLine($"{Truncate($"{fragment} [{version}]", 40),-42}{before:0.00} -> {after:0.00}       {"not in this run",-20}n/a");
                continue;
            }

            double now = match.New(SrVariant.Base)!.Value;
            bool agrees = Math.Abs(match.Stored.DifficultyRating - before) <= 0.02 && Math.Abs(now - after) <= 0.02;

            output.WriteLine(
                $"{Truncate(match.Stored.Name, 40),-42}{before:0.00} -> {after:0.00}       "
                + $"{match.Stored.DifficultyRating.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + now.ToString("0.00", CultureInfo.InvariantCulture),-20}"
                + (agrees ? "yes" : "NO"));
        }

        var ranked = resolved.Where(m => string.Equals(m.Stored.SetStatus, "ranked", StringComparison.OrdinalIgnoreCase)).ToList();

        if (ranked.Count > 0)
        {
            double meanBefore = ranked.Average(m => m.Stored.DifficultyRating);
            double meanAfter = ranked.Average(m => m.New(SrVariant.Base)!.Value);
            bool agrees = Math.Abs(meanBefore - spec_mean.Before) <= 0.02 && Math.Abs(meanAfter - spec_mean.After) <= 0.02;

            output.WriteLine(
                $"{$"mean over {ranked.Count} ranked difficulties",-42}{spec_mean.Before:0.00} -> {spec_mean.After:0.00}       "
                + $"{meanBefore.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + meanAfter.ToString("0.00", CultureInfo.InvariantCulture),-20}"
                + (agrees ? "yes" : "NO"));
        }

        output.WriteLine();
        output.WriteLine("  These are the spec's own measurements, CHECKED and not enforced. A disagreement is a");
        output.WriteLine("  finding about the spec or about the data, and is worth reporting rather than tuning away.");
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintPp(RunContext run, TextWriter output)
    {
        var f = run.Pp;

        output.WriteLine();
        output.WriteLine("-- PERFORMANCE POINTS --------------------------------------------------------------");
        output.WriteLine();
        output.WriteLine($"assumed, the DELETED factor:  max({Length.LENGTH_FLOOR:0.0#}, 1 + {Length.LENGTH_WEIGHT:0.00}*log10(notes/{Length.REFERENCE_CELLS:0}))");

        if (f.FittedWeight.Samples > 0 && double.IsFinite(f.FittedWeight.Slope))
        {
            output.WriteLine($"fitted, from stored prices:   weight {f.FittedWeight.Slope:0.######} over {f.FittedWeight.Samples} row(s),");
            output.WriteLine($"                              worst residual {f.FittedWeight.MaxResidual:0.000000}");
        }
        else
        {
            output.WriteLine("fitted, from stored prices:   not computed, no row above the 100-note pivot");
        }

        var repriced = run.Scores.Where(r => r.Repriced && r.Ratio is not null).ToList();

        if (repriced.Count > 0)
        {
            var ratios = repriced.Select(r => r.Ratio!.Value).OrderBy(v => v).ToList();

            output.WriteLine();
            output.WriteLine($"repriced rows                 {repriced.Count}");
            output.WriteLine($"kept of their pp              min {ratios[0]:0.000}   median {PpAnalysis.Median(ratios):0.000}   max {ratios[^1]:0.000}");
            output.WriteLine($"deflated / unchanged / inflated  {ratios.Count(v => v < 1)} / {ratios.Count(v => v == 1)} / {ratios.Count(v => v > 1)}");
            output.WriteLine($"total pp on the table         {repriced.Sum(r => r.Stored.Pp):0.0} -> {repriced.Sum(r => r.NewPp!.Value):0.0}");
        }

        if (f.Buckets.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("deflation graded by map length (the ratio of new pp to stored pp):");
            output.WriteLine($"  {"cells",-14}{"rows",-8}{"median",-10}{"min",-10}max");

            foreach (var b in f.Buckets)
                output.WriteLine($"  {b.Label,-14}{b.Rows,-8}{b.MedianRatio,-10:0.000}{b.MinRatio,-10:0.000}{b.MaxRatio:0.000}");

            output.WriteLine("  (a band under 100 cells legitimately INFLATES: the deleted factor was below 1 there.)");
        }

        if (f.Anchors.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("152's pp anchors, checked and not enforced:");

            foreach (var a in f.Anchors)
            {
                string observed = a.ObservedMedianRatio is double median
                    ? $"{Pct(median - 1)} over {a.Rows} row(s)"
                    : "no rows within 15% of that length";

                output.WriteLine($"  {a.Cells,5} cells   spec {Pct(a.SpecRatio - 1),-10} this run {observed}");
            }
        }

        output.WriteLine();
        output.WriteLine($"no sign flips                 {Verdict(f.SignsPassed)}   {f.SignFlips.Count} row(s)");
        output.WriteLine($"deflation graded by length    {Verdict(f.GradingPassed)}   {f.GradingBreaks.Count} band(s) out of order");
        output.WriteLine($"move IS the deleted factor    {(f.UnexplainedMoves.Count == 0 ? "yes" : f.UnexplainedMoves.Count + " row(s) moved by something else")}");
        output.WriteLine($"refused / pending / unpriceable  {f.Refused} / {f.Pending} / {f.Unpriceable.Count}");

        foreach (var line in f.GradingBreaks)
            output.WriteLine($"  BAND OUT OF ORDER: {line}");

        PrintScoreFindings(output, "SIGN FLIPS", f.SignFlips, run.FullTable);
        PrintScoreFindings(output, "MOVED BY SOMETHING OTHER THAN THE DELETED LENGTH FACTOR", f.UnexplainedMoves, run.FullTable);
        PrintScoreFindings(output, "NOT PRICEABLE (their map was not re-rated)", f.Unpriceable, run.FullTable);
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintBoards(RunContext run, TextWriter output)
    {
        var b = run.Boards;

        output.WriteLine();
        output.WriteLine("-- BOARDS -------------------------------------------------------------------------");

        if (b.Filtered)
        {
            output.WriteLine();
            output.WriteLine("  not computed: this run selected a subset of the catalogue (--beatmap or --limit), so it");
            output.WriteLine("  holds only a slice of each board and could not tell a real place change from a missing row.");
            return;
        }

        output.WriteLine();
        output.WriteLine($"boards in this run            {b.Boards}");
        output.WriteLine($"boards that REORDER           {b.BoardsReordered}");
        output.WriteLine($"boards whose #1 changes       {b.BoardsTopChanged}");
        output.WriteLine($"players whose best play changes  {b.RepresentativeChanges}");
        output.WriteLine($"rows leaving / joining        {b.RowsLeavingBoards} / {b.RowsJoiningBoards}");
        output.WriteLine($"per-map order changes         {b.BoardOrderChanges.Count}");
        output.WriteLine($"global top-play order changes {b.GlobalOrderChanges.Count}");
        output.WriteLine($"ranked players                {b.RankedUsers}, of whom {b.RankMoves.Count} change global rank");
        output.WriteLine($"order changes need length     {Verdict(b.Passed)}   {b.Violations.Count()} pair(s) swapped with no length between them");

        if (b.RankMoves.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("biggest global rank moves:");

            foreach (var move in Take(b.RankMoves, run.FullTable, 10))
                output.WriteLine($"  user {move.UserId,-8} #{move.RankBefore} -> #{move.RankAfter}  ({move.TotalBefore:0.0} -> {move.TotalAfter:0.0} pp)");
        }

        var violations = b.Violations.ToList();

        if (violations.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("ORDER CHANGES WITH NO LENGTH BETWEEN THE TWO PLAYS (the predicate 152 names):");

            foreach (var v in Take(violations, run.FullTable))
                output.WriteLine($"  {v.Where,-22} score {v.ScoreAbove} fell below score {v.ScoreBelow}  (notes {v.NotesAbove} vs {v.NotesBelow}, cells {v.CellsAbove} vs {v.CellsBelow})");

            Hidden(output, violations.Count, run.FullTable);
        }

        var explained = b.BoardOrderChanges.Concat(b.GlobalOrderChanges).Where(c => c.LengthWasTheDifferentiator).ToList();

        if (explained.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("order changes WHERE LENGTH WAS THE DIFFERENTIATOR (the feature, not a fault):");

            foreach (var v in Take(explained, run.FullTable, 10))
                output.WriteLine($"  {v.Where,-22} score {v.ScoreAbove} fell below score {v.ScoreBelow}  (notes {v.NotesAbove} vs {v.NotesBelow}, cells {v.CellsAbove} vs {v.CellsBelow})");

            Hidden(output, explained.Count, run.FullTable, 10);
        }
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintVerdict(RunContext run, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("-- VERDICT ------------------------------------------------------------------------");
        output.WriteLine();
        output.WriteLine($"  SR   every delta in [0, +0.17]                {Verdict(run.Sr.InRangePassed)}");
        output.WriteLine($"  SR   monotone in cells                        {Verdict(run.Sr.MonotonePassed)}");
        output.WriteLine($"  pp   no sign flips                            {Verdict(run.Pp.SignsPassed)}");
        output.WriteLine($"  pp   deflation graded by map length           {Verdict(run.Pp.GradingPassed)}");
        output.WriteLine($"  pp   order changes only where length differed {(run.Boards.Filtered ? "not run" : Verdict(run.Boards.Passed))}");
        output.WriteLine();
        output.WriteLine($"  checked over              {run.MapsReRated} of {run.Maps.Count} beatmap(s), {run.ScoresRepriced} of {run.Scores.Count} score(s)");

        if (run.Vacuous)
        {
            output.WriteLine();
            output.WriteLine("  VACUOUS: not one map was re-rated, so every predicate above held over an empty set.");
            output.WriteLine("  That is not a pass. Check --site and the unavailable set list at the top.");
        }

        output.WriteLine();
        output.WriteLine($"  overall: {(run.Passed ? "every predicate 152 names holds over the rows this run could read" : "AT LEAST ONE PREDICATE FAILED, see the ids above")}");
        output.WriteLine();
        output.WriteLine("  What this run did NOT check:");
        output.WriteLine("   - rows on a map it could not fetch or could not match by checksum. They are listed by id,");
        output.WriteLine("     and no predicate above claims anything about them.");
        output.WriteLine("   - whether the STORED numbers were correct to begin with. The 'before' column is read, never");
        output.WriteLine("     re-derived: a row stale for a reason older than 152 shows up as a residual, which is why");
        output.WriteLine("     the residual is reported next to the two predicates rather than folded into them.");
        output.WriteLine("   - the game client's own copies of either formula. This is the server's arithmetic only;");
        output.WriteLine("     tests/Typebeat.WireCompat is what holds the two repos' mirrors together.");
        output.WriteLine();
        output.WriteLine("DRY RUN. Nothing was written. This tool has no apply command and opens no write path.");
    }

    // -----------------------------------------------------------------------------------------

    private static void PrintFindings(TextWriter output, string label, IReadOnlyList<SrFinding> findings, bool full)
    {
        if (findings.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine($"{label}:");

        foreach (var f in Take(findings, full))
            output.WriteLine($"  beatmap {f.BeatmapId,-8} {f.Variant,-18} {f.Value,12:+0.000000;-0.000000}  {f.What}   {Truncate(f.Name, 40)}");

        Hidden(output, findings.Count, full);
    }

    private static void PrintScoreFindings(TextWriter output, string label, IReadOnlyList<PpFinding> findings, bool full)
    {
        if (findings.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine($"{label}:");

        foreach (var f in Take(findings, full))
            output.WriteLine($"  score {f.ScoreId,-9} beatmap {f.BeatmapId,-8} notes {f.Notes,-7} {f.Value,12:+0.000000;-0.000000}  {f.What}");

        Hidden(output, findings.Count, full);
    }

    private static IEnumerable<T> Take<T>(IReadOnlyList<T> items, bool full, int cap = list_cap)
        => full ? items : items.Take(cap);

    private static void Hidden(TextWriter output, int total, bool full, int cap = list_cap)
    {
        if (!full && total > cap)
            output.WriteLine($"  ... {total - cap} more not shown; --full-table prints them all, --out writes them as JSON");
    }

    private static string Distribution(IEnumerable<int> versions)
    {
        var counts = versions.GroupBy(v => v).OrderBy(g => g.Key).Select(g => $"v{g.Key} x{g.Count()}").ToList();

        return counts.Count == 0 ? "none" : string.Join(", ", counts);
    }

    private static string Ids(IEnumerable<long> ids)
    {
        var list = ids.OrderBy(i => i).ToList();

        return list.Count <= 20
            ? string.Join(", ", list)
            : string.Join(", ", list.Take(20)) + $", ... ({list.Count} total)";
    }

    private static string Verdict(bool passed) => passed ? "HOLDS " : "FAILED";

    private static string Pct(double fraction)
        => (fraction >= 0 ? "+" : string.Empty) + (100 * fraction).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A PRICED cell count, which is fractional since backlog 211 (a freestyle slot is a quarter of
    /// a cell). Whole counts still print whole, so the table reads exactly as it always has on the
    /// maps that carry no markers, which is nearly all of them.
    /// </summary>
    private static string Cells(double cells) => cells.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "~";
}
