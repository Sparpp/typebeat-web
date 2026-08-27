using Typebeat.Web;
using Typebeat.Web.Scoring;

namespace Typebeat.Tools.RepriceReport;

/// <summary>One score row, exactly as stored. Nothing here is derived.</summary>
internal sealed record StoredScoreRow(
    long ScoreId,
    long UserId,
    long BeatmapId,
    double Pp,
    int PpVersion,
    bool Ranked,
    bool Passed,
    double Accuracy,
    int MaxCombo,
    string? ModsJson,
    string? StatisticsJson);

/// <summary>
/// One play's before/after, where BEFORE is the stored <c>pp</c> column and AFTER is
/// <see cref="PerformancePoints.ForScore"/> under today's formula and the map's RECOMPUTED ratings.
/// That is the same call <see cref="Typebeat.Web.Packages.PpBackfill"/> makes, over the same inputs,
/// so the "after" column is what a real backfill would write.
///
/// <para>THERE IS A THIRD COLUMN, and it is what makes the pp half checkable rather than merely
/// observable: <see cref="PpAtStoredStars"/>, today's formula over the STORED ratings. It splits the
/// move into its two halves. Stored to that is the DELETED LENGTH FACTOR alone, which the report can
/// predict exactly (<see cref="Length.DeletedPpFactor"/>) and hold every row against; that to the
/// new value is the STAR BONUS alone, i.e. <c>((SR + bonus)/SR)^sr_exponent</c>, the few percent 152
/// says is all the length pp should still see.</para>
/// </summary>
internal sealed class PpRow
{
    public required StoredScoreRow Stored { get; init; }

    /// <summary>The map this play is on, re-rated or not.</summary>
    public required SrRow Map { get; init; }

    public PerformancePoints.NoteCounts Notes { get; init; }

    /// <summary>Whether the play used Literate, i.e. which cell stream priced it.</summary>
    public bool Literate { get; init; }

    /// <summary>Today's formula over the STORED ratings: the deleted length factor's contribution alone.</summary>
    public double? PpAtStoredStars { get; init; }

    /// <summary>Today's formula over the RECOMPUTED ratings: what a backfill would store.</summary>
    public double? NewPp { get; init; }

    /// <summary>False when a rating the play needs is not stored yet, i.e. the row is not priceable at all.</summary>
    public bool Settled { get; init; }

    /// <summary>
    /// The formula REFUSED this play permanently (unranked, or a custom rate): settled, with no price
    /// at all. Kept apart from a settled 0, which is a real price and an assertion that the play is
    /// worth nothing, because only one of the two can ever become non-zero again.
    /// </summary>
    public bool Refused { get; init; }

    public double? StarBefore { get; init; }
    public double? StarAfter { get; init; }

    public bool Repriced => Map.Resolved && NewPp is not null;

    /// <summary>The cells the play's own stream carries, i.e. the length that priced it.</summary>
    public double Cells => Map.CellsFor(Literate ? SrVariant.Literate : SrVariant.Base);

    /// <summary>New over stored. Null when there is no stored price to compare against.</summary>
    public double? Ratio => Stored.Pp > 0 && NewPp is double now ? now / Stored.Pp : null;

    /// <summary>The factor the stored row carries that today's formula does not.</summary>
    public double ExpectedDeletedFactor => Length.DeletedPpFactor(Notes.Notes);

    /// <summary>
    /// The length factor the ROW implies, i.e. stored divided by today's price at the same ratings.
    /// Held against <see cref="ExpectedDeletedFactor"/>: agreement means the whole pp move on this
    /// row is the deleted term plus the star bonus, and nothing else.
    /// </summary>
    public double? ImpliedDeletedFactor
        => Stored.Pp > 0 && PpAtStoredStars is double atStored && atStored > 0 ? Stored.Pp / atStored : null;

    /// <summary>Relative disagreement between the implied and the predicted length factor.</summary>
    public double? FactorResidual
        => ImpliedDeletedFactor is double implied ? implied / ExpectedDeletedFactor - 1 : null;

    /// <summary>The star bonus' own contribution: new over today's price at the stored ratings.</summary>
    public double? StarBonusRatio
        => PpAtStoredStars is double atStored && atStored > 0 && NewPp is double now ? now / atStored : null;

    /// <summary>
    /// Prices one row twice under today's code, once on each set of ratings. An unresolved map yields
    /// a row that carries its stored values and no new ones, which is what keeps it on BOTH sides of
    /// every board comparison instead of inventing a place change by dropping it.
    /// </summary>
    public static PpRow Price(StoredScoreRow stored, SrRow map)
    {
        var mods = ScoreMods.Parse(stored.ModsJson);
        var notes = PerformancePoints.CountNotes(stored.StatisticsJson);
        bool literate = PerformancePoints.IsLiterate(mods);

        var storedLiterate = new PerformancePoints.LiterateStars(map.Stored.SrLiterate, map.Stored.SrLiterateDt, map.Stored.SrLiterateHt);

        var atStored = PerformancePoints.ForScore(
            stored.Ranked, mods, notes, stored.Accuracy, stored.MaxCombo,
            map.Stored.DifficultyRating, map.Stored.SrDt, map.Stored.SrHt, storedLiterate);

        var starBefore = PerformancePoints.StarsFor(mods, map.Stored.DifficultyRating, map.Stored.SrDt, map.Stored.SrHt, storedLiterate);

        if (!map.Resolved)
        {
            return new PpRow
            {
                Stored = stored,
                Map = map,
                Notes = notes,
                Literate = literate,
                PpAtStoredStars = atStored.Pp,
                Settled = atStored.Settled,
                Refused = atStored.Settled && atStored.Pp is null,
                StarBefore = starBefore.Stars,
            };
        }

        var newLiterate = new PerformancePoints.LiterateStars(
            map.New(SrVariant.Literate), map.New(SrVariant.LiterateDoubleTime), map.New(SrVariant.LiterateHalfTime));

        double newBase = map.New(SrVariant.Base)!.Value;
        double? newDt = map.New(SrVariant.DoubleTime);
        double? newHt = map.New(SrVariant.HalfTime);

        var priced = PerformancePoints.ForScore(
            stored.Ranked, mods, notes, stored.Accuracy, stored.MaxCombo, newBase, newDt, newHt, newLiterate);

        return new PpRow
        {
            Stored = stored,
            Map = map,
            Notes = notes,
            Literate = literate,
            PpAtStoredStars = atStored.Pp,
            // The column is NOT NULL and a refusal stores 0, so that is what the "after" column holds:
            // this report is about what the row would READ as, not about which of the three outcomes
            // produced it (Settled carries that).
            NewPp = priced.Pp ?? 0,
            Settled = priced.Settled,
            Refused = priced.Settled && priced.Pp is null,
            StarBefore = starBefore.Stars,
            StarAfter = PerformancePoints.StarsFor(mods, newBase, newDt, newHt, newLiterate).Stars,
        };
    }
}

/// <summary>A score and the number that failed, named by id.</summary>
internal sealed record PpFinding(long ScoreId, long BeatmapId, int Notes, double Value, string What);

/// <summary>One length band of the deflation table.</summary>
internal sealed record LengthBucket(long FromCells, long ToCells, int Rows, double MedianRatio, double MinRatio, double MaxRatio)
{
    public string Label => ToCells == long.MaxValue ? $"{FromCells}+" : $"{FromCells}-{ToCells - 1}";
}

/// <summary>
/// What 152 says the anchors should read: roughly -18% on a 340-cell map, -28% at 800, -38% at 2300.
/// CHECKED, NOT ENFORCED. The tool computes its own answer from real rows near each length and prints
/// the two side by side; a disagreement is a finding about the spec or the data, and bending the tool
/// towards the spec's number would destroy the only evidence either way.
/// </summary>
internal sealed record DeflationAnchor(int Cells, double SpecRatio, int Rows, double? ObservedMedianRatio)
{
    public double? Divergence => ObservedMedianRatio is double observed ? observed - SpecRatio : null;
}

internal sealed class PpFindings
{
    /// <summary>Rows that were priced above zero and now are not, or the reverse.</summary>
    public List<PpFinding> SignFlips { get; } = new();

    /// <summary>Rows whose move is not explained by the deleted factor plus the star bonus.</summary>
    public List<PpFinding> UnexplainedMoves { get; } = new();

    /// <summary>Rows on a map that could not be re-rated, so nothing about them was checked.</summary>
    public List<PpFinding> Unpriceable { get; } = new();

    /// <summary>Rows today's formula refuses to price at all (unranked, custom rate).</summary>
    public int Refused { get; set; }

    /// <summary>Rows left pending by today's code, i.e. a rating they need is still not stored.</summary>
    public int Pending { get; set; }

    public List<LengthBucket> Buckets { get; } = new();

    /// <summary>Bucket pairs where a longer band deflated LESS than a shorter one.</summary>
    public List<string> GradingBreaks { get; } = new();

    public List<DeflationAnchor> Anchors { get; } = new();

    /// <summary>The pp length weight fitted back out of the stored prices (152 deleted 0.50).</summary>
    public Length.Fit FittedWeight { get; set; }

    public bool SignsPassed => SignFlips.Count == 0;
    public bool GradingPassed => GradingBreaks.Count == 0;
    public bool Passed => SignsPassed && GradingPassed;
}

internal static class PpAnalysis
{
    /// <summary>
    /// Length bands for the deflation table. Boundaries are decade-ish rather than round because the
    /// term they are measuring is a log: 100 is where the deleted factor crosses 1.0 (below it the
    /// reprice INFLATES), and the rest double.
    /// </summary>
    private static readonly long[] bucket_bounds = [0, 100, 200, 400, 800, 1600, 3200, long.MaxValue];

    /// <summary>The three lengths 152's spec names, with the ratio it predicts at each.</summary>
    private static readonly (int Cells, double Ratio)[] spec_anchors = [(340, 0.82), (800, 0.72), (2300, 0.62)];

    public static PpFindings Run(IReadOnlyList<PpRow> rows, Tolerances tolerances)
    {
        var findings = new PpFindings();

        foreach (var row in rows)
        {
            if (!row.Map.Resolved)
            {
                findings.Unpriceable.Add(new PpFinding(row.Stored.ScoreId, row.Stored.BeatmapId, row.Notes.Notes, row.Stored.Pp, $"map not re-rated ({row.Map.Resolution})"));
                continue;
            }

            if (!row.Settled)
            {
                findings.Pending++;
                continue;
            }

            if (row.NewPp is not double now)
                continue;

            if (row.Refused)
                findings.Refused++;

            bool wasPriced = row.Stored.Pp > 0;
            bool isPriced = now > 0;

            if (wasPriced != isPriced)
            {
                findings.SignFlips.Add(new PpFinding(
                    row.Stored.ScoreId, row.Stored.BeatmapId, row.Notes.Notes, now - row.Stored.Pp,
                    wasPriced ? $"stored {row.Stored.Pp:0.####} pp, reprices to 0" : $"stored 0 pp, reprices to {now:0.####}"));
            }

            if (row.FactorResidual is double residual && Math.Abs(residual) > tolerances.PpRelative)
            {
                findings.UnexplainedMoves.Add(new PpFinding(
                    row.Stored.ScoreId, row.Stored.BeatmapId, row.Notes.Notes, residual,
                    $"implied length factor {row.ImpliedDeletedFactor:0.0000}, predicted {row.ExpectedDeletedFactor:0.0000} (pp_version {row.Stored.PpVersion})"));
            }
        }

        var priced = rows.Where(r => r.Repriced && r.Ratio is not null).ToList();

        BuildBuckets(priced, findings);
        BuildAnchors(priced, findings);

        findings.FittedWeight = Length.Fit.Over(
            rows.Where(r => r.Repriced && r.ImpliedDeletedFactor is not null && r.Notes.Notes > Length.REFERENCE_CELLS)
                .Select(r => (Math.Log10(r.Notes.Notes / Length.REFERENCE_CELLS), r.ImpliedDeletedFactor!.Value - 1)));

        return findings;
    }

    /// <summary>
    /// DEFLATION GRADED BY MAP LENGTH, as a table rather than as a single number: the median reprice
    /// ratio per length band, and a break recorded whenever a longer band keeps MORE of its pp than a
    /// shorter one. The gradient is the predicate; the absolute values are not, since a short map
    /// legitimately gains (its deleted factor was below 1).
    /// </summary>
    private static void BuildBuckets(IReadOnlyList<PpRow> priced, PpFindings findings)
    {
        for (int i = 0; i + 1 < bucket_bounds.Length; i++)
        {
            long from = bucket_bounds[i], to = bucket_bounds[i + 1];
            var inBand = priced.Where(r => r.Cells >= from && r.Cells < to).Select(r => r.Ratio!.Value).OrderBy(v => v).ToList();

            if (inBand.Count == 0)
                continue;

            findings.Buckets.Add(new LengthBucket(from, to, inBand.Count, Median(inBand), inBand[0], inBand[^1]));
        }

        for (int i = 1; i < findings.Buckets.Count; i++)
        {
            var longer = findings.Buckets[i];
            var shorter = findings.Buckets[i - 1];

            if (longer.MedianRatio > shorter.MedianRatio + Tolerances.BOUND_EPSILON)
            {
                findings.GradingBreaks.Add(
                    $"cells {longer.Label} keep {longer.MedianRatio:0.000} of their pp, more than the shorter band {shorter.Label} at {shorter.MedianRatio:0.000}");
            }
        }
    }

    /// <summary>
    /// The spec's three anchors against real rows: every play whose map is within 15% of the named
    /// length, and what those rows actually did.
    /// </summary>
    private static void BuildAnchors(IReadOnlyList<PpRow> priced, PpFindings findings)
    {
        foreach (var (cells, ratio) in spec_anchors)
        {
            var near = priced.Where(r => Math.Abs(r.Cells - cells) <= 0.15 * cells).Select(r => r.Ratio!.Value).OrderBy(v => v).ToList();

            findings.Anchors.Add(new DeflationAnchor(cells, ratio, near.Count, near.Count > 0 ? Median(near) : null));
        }
    }

    public static double Median(IReadOnlyList<double> sorted)
        => sorted.Count == 0 ? double.NaN
            : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
            : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
}
