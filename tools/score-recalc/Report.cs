using System.Globalization;

namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// The dry-run report. Three sections, in the order a reader needs them:
///
/// <list type="number">
/// <item>the REPRODUCTION check, which says whether any of the rest can be believed;</item>
/// <item>what moved, per score, one line per field that actually changed;</item>
/// <item>the aggregates: how many move at all, rank changes, pp distribution, worst cases.</item>
/// </list>
/// </summary>
public static class Report
{
    public static void Print(IReadOnlyList<RecalcResult> results, TextWriter output)
    {
        var recalculated = results.Where(r => r.Recalculated).ToList();
        var skipped = results.Where(r => !r.Recalculated).ToList();

        PrintReproduction(results, recalculated, skipped, output);
        PrintMoves(recalculated, output);
        PrintAggregates(recalculated, output);
    }

    private static void PrintReproduction(IReadOnlyList<RecalcResult> all, IReadOnlyList<RecalcResult> recalculated, IReadOnlyList<RecalcResult> skipped, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== reproduction of the stored numbers under the OLD (pre-109) rule ==");
        output.WriteLine();

        var eligible = all.Where(r => r.Skip is SkipReason.None or SkipReason.NotReproducible).ToList();
        int reproduced = recalculated.Count;

        output.WriteLine($"scores considered            {all.Count}");
        output.WriteLine($"replayable and passed        {eligible.Count}");
        output.WriteLine($"reproduced exactly           {reproduced}"
                         + (eligible.Count > 0 ? $"  ({Percent(reproduced, eligible.Count)})" : string.Empty));

        foreach (var group in skipped.GroupBy(r => r.Skip).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            output.WriteLine($"  skipped: {Describe(group.Key),-28} {group.Count(),5}"
                             + (all.Count > 0 ? $"  ({Percent(group.Count(), all.Count)} of all)" : string.Empty));
        }

        int totalsReproduced = recalculated.Count(r => r.Stored.TotalScore == r.OldRuleTotalScore);

        if (recalculated.Count > 0)
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

        var unavailable = skipped.Where(r => r.Skip == SkipReason.BeatmapUnavailable).Select(r => r.Detail).Distinct().ToList();

        if (unavailable.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"  beatmap hashes with no downloadable package ({unavailable.Count}):");

            foreach (string? hash in unavailable.Take(10))
                output.WriteLine($"    {hash}");
        }
    }

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

        if (r.PpDeltaFromTheRuleChange is double delta && Math.Abs(delta) > 1e-9)
            yield return $"pp (rule change)      {r.OldRulePp!.Value,8:0.00} -> {r.NewPp!.Value,-8:0.00} ({Signed(delta)})";
    }

    private static void PrintAggregates(IReadOnlyList<RecalcResult> recalculated, TextWriter output)
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
        Count(output, "pp changed", recalculated, r => r.PpDeltaFromTheRuleChange is double d && Math.Abs(d) > 1e-9);
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

        var ppDeltas = recalculated.Select(r => r.PpDeltaFromTheRuleChange).OfType<double>().ToList();

        if (ppDeltas.Count > 0)
            PrintDistribution(output, "pp delta attributable to the rule change", ppDeltas, "0.00");
        else
            output.WriteLine("\npp delta: not priced in this run (no star ratings available).");

        output.WriteLine();
        output.WriteLine("not touched by this sweep, and worth knowing before applying it:");
        output.WriteLine("  - user_stats.hit_counts / total_score / play_time were accumulated at submission time from");
        output.WriteLine("    the OLD statistics and are not rewritten here, so profile aggregates keep their old values.");
        output.WriteLine("  - scores.passed is never re-derived: see the failed-run skip reason.");
        output.WriteLine("  - a row is never re-ranked upward; it can only lose scores.ranked (the other gates are not");
        output.WriteLine("    derivable from a replay).");

        PrintWorst(output, "largest max_combo losses", recalculated, r => r.NewMaxCombo - r.Stored.MaxCombo);
        PrintWorst(output, "largest total_score losses", recalculated, r => r.NewTotalScore - r.Stored.TotalScore);

        if (ppDeltas.Count > 0)
            PrintWorst(output, "largest pp losses", recalculated.Where(r => r.PpDeltaFromTheRuleChange is not null).ToList(), r => r.PpDeltaFromTheRuleChange!.Value);
    }

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

    private static void PrintWorst(TextWriter output, string label, IReadOnlyList<RecalcResult> results, Func<RecalcResult, double> delta)
    {
        var worst = results.Where(r => delta(r) != 0).OrderBy(delta).Take(5).ToList();

        if (worst.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine($"{label}:");

        foreach (var r in worst)
            output.WriteLine($"  score {r.Stored.ScoreId,-8} {Signed(delta(r))}   (mistypes {Mistypes(r)}, misses {Misses(r)})");
    }

    private static int Mistypes(RecalcResult r) => (r.NewStatistics ?? new Dictionary<string, int>()).GetValueOrDefault("combo_break");

    private static int Misses(RecalcResult r) => (r.NewStatistics ?? new Dictionary<string, int>()).GetValueOrDefault("miss");

    private static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.NoReplay => "no stored replay",
        SkipReason.UndecodableReplay => "replay did not decode",
        SkipReason.BeatmapUnavailable => "beatmap unavailable",
        SkipReason.EmptyReplay => "replay holds no frames",
        SkipReason.FailedRun => "failed run (not replayable)",
        SkipReason.NotReproducible => "old rule not reproduced",
        _ => reason.ToString(),
    };

    private static string Percent(int part, int total)
        => total == 0 ? "n/a" : (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Pct(double fraction) => (100 * fraction).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static string Signed(double value)
        => (value > 0 ? "+" : string.Empty) + value.ToString(Math.Abs(value % 1) < 1e-9 ? "0" : "0.00", CultureInfo.InvariantCulture);
}
