using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;

namespace Typebeat.Tools.RepriceReport;

/// <summary>
/// The six star ratings a beatmap row stores (020_performance_points.sql, 029_literate_stars.sql).
/// The three LITERATE ones are computed off a LARGER cell stream (every supported punctuation mark
/// is a real cell under that mod), so their length bonus is computed off a larger cell count than
/// the three plain ones and the two are checked against their own counts.
/// </summary>
internal enum SrVariant
{
    Base,
    DoubleTime,
    HalfTime,
    Literate,
    LiterateDoubleTime,
    LiterateHalfTime,
}

/// <summary>Why a beatmap row could not be re-rated. Every one of these is reported BY ID.</summary>
internal enum MapResolution
{
    /// <summary>Re-rated from the exact .osu the stored rating came from.</summary>
    Recomputed,

    /// <summary>The set's package could not be fetched (a FETCH failure, not a fact about the data).</summary>
    PackageUnavailable,

    /// <summary>The package was fetched but holds no .osu with this row's checksum: re-uploaded since.</summary>
    HashNotInPackage,

    /// <summary>The .osu was found and did not parse.</summary>
    ParseFailed,
}

/// <summary>One beatmap row, exactly as stored. Nothing here is derived.</summary>
internal sealed record StoredBeatmap(
    long BeatmapId,
    long SetId,
    string? Filename,
    string ChecksumMd5,
    string VersionName,
    string Title,
    string Artist,
    string SetStatus,
    int PaceVersion,
    int? CharCount,
    double DifficultyRating,
    double? SrDt,
    double? SrHt,
    double? SrLiterate,
    double? SrLiterateDt,
    double? SrLiterateHt,
    // APPENDED, never inserted: this is a positional record and Dapper fills it by constructor
    // position when a query hands back columns in order. 031_freestyle_cell_count.sql, and NULL on
    // any row the v16 pace sweep has not reached, which is exactly what the column means.
    int? FreestyleCellCount)
{
    public string Name => $"{Artist} - {Title} [{VersionName}]";

    public double? Rating(SrVariant variant) => variant switch
    {
        SrVariant.Base => DifficultyRating,
        SrVariant.DoubleTime => SrDt,
        SrVariant.HalfTime => SrHt,
        SrVariant.Literate => SrLiterate,
        SrVariant.LiterateDoubleTime => SrLiterateDt,
        SrVariant.LiterateHalfTime => SrLiterateHt,
        _ => null,
    };
}

/// <summary>
/// One map's before/after, where BEFORE is what the row stores (rated under pre-152 code) and AFTER
/// is <see cref="BeatmapPackageParser.ParseDifficulty"/> over the same bytes under today's code.
/// That is deliberately the same call <see cref="Typebeat.Web.Packages.PaceBackfill"/> makes, so the
/// "after" column is what a real backfill would write and not a model of one.
/// </summary>
internal sealed class SrRow
{
    public required StoredBeatmap Stored { get; init; }

    public MapResolution Resolution { get; init; }

    /// <summary>Human-readable reason, for anything other than <see cref="MapResolution.Recomputed"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>PRICED cells on the default stream, i.e. what the plain trio's bonus is computed from.</summary>
    public double Cells { get; init; }

    /// <summary>PRICED cells under Literate, i.e. what the literate trio's bonus is computed from.</summary>
    public double LiterateCells { get; init; }

    /// <summary>
    /// <c>LyricPace</c>'s own cell count, which is a DIFFERENT number (it adds a cell per token gap
    /// and counts a freestyle slot whole, where the two above count it a quarter). Carried only so
    /// the report can hold it against the stored
    /// <c>char_count</c> column: a disagreement there says the stored row is stale for reasons that
    /// have nothing to do with 152, which is exactly the kind of thing a residual outlier turns out
    /// to be.
    /// </summary>
    public int PaceCells { get; init; }

    /// <summary>Recomputed ratings, indexed by <see cref="SrVariant"/>; empty when unresolved.</summary>
    public double[] Recomputed { get; init; } = Array.Empty<double>();

    /// <summary>
    /// The map's recomputed RATING MATRIX (034_ratings_matrix.sql), null when unresolved. It is a
    /// superset of <see cref="Recomputed"/> (those six are its arm-none stars) and carries the
    /// DIFFICULT CHARACTERS a price needs since PerformancePoints v22, which no stored column holds.
    /// </summary>
    public BeatmapRatings? Ratings { get; init; }

    public bool Resolved => Resolution == MapResolution.Recomputed;

    public double CellsFor(SrVariant variant)
        => variant is SrVariant.Literate or SrVariant.LiterateDoubleTime or SrVariant.LiterateHalfTime
            ? LiterateCells
            : Cells;

    public double? New(SrVariant variant)
        => Recomputed.Length == 0 ? null : Recomputed[(int)variant];

    /// <summary>Stored to recomputed. Null when either side is missing (an unfilled sr_* column).</summary>
    public double? Delta(SrVariant variant)
        => Stored.Rating(variant) is double before && New(variant) is double after ? after - before : null;

    /// <summary>What 152 says this variant should have gained: the bonus for ITS cell count.</summary>
    public double ExpectedBonus(SrVariant variant) => Length.StarBonus(CellsFor(variant));

    /// <summary>
    /// The part of the move 152 does NOT explain. Zero (to a tolerance) is the strong form of the
    /// acceptance predicate: the map moved by exactly its length bonus and by nothing else. A
    /// non-zero residual means the stored rating was ALSO stale for another reason, and the report
    /// lists the map rather than averaging it away.
    /// </summary>
    public double? Residual(SrVariant variant)
        => Delta(variant) is double delta ? delta - ExpectedBonus(variant) : null;

    /// <summary>
    /// The cell count the observed delta implies, i.e. what count would have produced this move. Only
    /// meaningful next to <see cref="Cells"/>, which is the point: when the two disagree the report
    /// can say whether the map looks like a DIFFERENT length or like something else entirely.
    /// </summary>
    public double? ImpliedCells(SrVariant variant)
        => Delta(variant) is double delta && delta > 0
            ? Length.REFERENCE_CELLS * Math.Pow(10, delta / Length.LENGTH_STARS)
            : null;

    public static SrRow Unresolved(StoredBeatmap stored, MapResolution resolution, string detail)
        => new() { Stored = stored, Resolution = resolution, Detail = detail };

    /// <summary>
    /// Re-rates one map from the exact .osu bytes its <c>checksum_md5</c> names. The six ratings come
    /// off one parse, exactly as an upload or a pace backfill produces them.
    /// </summary>
    public static SrRow Recompute(StoredBeatmap stored, byte[] osu)
    {
        ParsedDifficulty parsed;

        try
        {
            parsed = BeatmapPackageParser.ParseDifficulty(stored.Filename ?? "unknown.osu", osu);
        }
        catch (Exception e)
        {
            return Unresolved(stored, MapResolution.ParseFailed, $"{e.GetType().Name}: {e.Message}");
        }

        return new SrRow
        {
            Stored = stored,
            Resolution = MapResolution.Recomputed,
            Cells = Length.Count(parsed.Lines, literate: false),
            LiterateCells = Length.Count(parsed.Lines, literate: true),
            PaceCells = parsed.Pace.TypeableCellCount,
            Ratings = parsed.Ratings,
            Recomputed =
            [
                parsed.Pace.DifficultyRating,
                parsed.SrDoubleTime,
                parsed.SrHalfTime,
                parsed.SrLiterate,
                parsed.SrLiterateDoubleTime,
                parsed.SrLiterateHalfTime,
            ],
        };
    }
}

/// <summary>
/// How far a number may move before the report calls it a finding.
///
/// <para>A record CLASS with property initialisers rather than a record struct with optional
/// constructor parameters, deliberately: <c>new Tolerances()</c> on a struct skips the parameter
/// defaults entirely and hands back all zeros, which would silently turn the +0.17 cap into +0 and
/// fail every map in the catalogue. The defaults have to survive the parameterless construction that
/// callers and tests actually write.</para>
/// </summary>
internal sealed record Tolerances
{
    /// <summary>
    /// Absolute, in stars. A residual under this is arithmetic noise over a double; anything above it
    /// is a stored rating that moved for a reason other than the length bonus.
    /// </summary>
    public double Star { get; init; } = 1e-6;

    /// <summary>The ceiling 152's acceptance clause names, +0.17 stars.</summary>
    public double MaxStarGain { get; init; } = 0.17;

    /// <summary>Relative, on pp: |observed / predicted - 1|.</summary>
    public double PpRelative { get; init; } = 1e-6;

    /// <summary>Slack on the range bounds themselves, so a delta of exactly 0 or exactly the cap passes.</summary>
    public const double BOUND_EPSILON = 1e-9;
}

/// <summary>A map, a variant and the number that failed. Everything failing is reported by id.</summary>
internal sealed record SrFinding(long BeatmapId, string Name, SrVariant Variant, double Value, string What);

/// <summary>A pair of maps whose gains are the wrong way round: the longer one gained less.</summary>
internal sealed record MonotonicityBreak(
    SrVariant Variant,
    long LongerBeatmapId,
    string LongerName,
    double LongerCells,
    double LongerDelta,
    long ShorterBeatmapId,
    string ShorterName,
    double ShorterCells,
    double ShorterDelta);

/// <summary>Everything 152's SR acceptance clause asks, answered over the rows the run resolved.</summary>
internal sealed class SrFindings
{
    public List<SrFinding> OutOfRange { get; } = new();
    public List<SrFinding> BonusMismatch { get; } = new();
    public List<SrFinding> WholeStarCrossing { get; } = new();
    public List<MonotonicityBreak> MonotonicityBreaks { get; } = new();
    public List<SrRow> Unresolved { get; } = new();

    /// <summary>The length constant fitted back out of the observed base-rating deltas.</summary>
    public Length.Fit FittedBonus { get; set; }

    /// <summary>Maps whose stored <c>char_count</c> disagrees with the reparse, i.e. stale for other reasons.</summary>
    public List<SrFinding> StaleCharCount { get; } = new();

    public bool InRangePassed => OutOfRange.Count == 0;
    public bool MonotonePassed => MonotonicityBreaks.Count == 0;
    public bool BonusExplainsEveryMove => BonusMismatch.Count == 0;

    /// <summary>
    /// The two predicates 152 actually names. The residual check is strictly stronger than both and
    /// is reported next to them, but it is NOT folded in here: a map whose stored rating was stale
    /// for an unrelated reason fails the residual and can still satisfy the clause, and conflating
    /// the two would hide which one moved.
    /// </summary>
    public bool Passed => InRangePassed && MonotonePassed;
}

internal static class SrAnalysis
{
    public static SrFindings Run(IReadOnlyList<SrRow> rows, Tolerances tolerances)
    {
        var findings = new SrFindings();

        findings.Unresolved.AddRange(rows.Where(r => !r.Resolved));

        foreach (var row in rows.Where(r => r.Resolved))
        {
            foreach (var variant in Variants)
            {
                if (row.Delta(variant) is not double delta)
                    continue;

                if (delta < -Tolerances.BOUND_EPSILON)
                    findings.OutOfRange.Add(new SrFinding(row.Stored.BeatmapId, row.Stored.Name, variant, delta, "rating FELL; 152 only ever adds"));
                else if (delta > tolerances.MaxStarGain + Tolerances.BOUND_EPSILON)
                    findings.OutOfRange.Add(new SrFinding(row.Stored.BeatmapId, row.Stored.Name, variant, delta, $"gain exceeds the +{tolerances.MaxStarGain:0.00} cap"));

                if (row.Residual(variant) is double residual && Math.Abs(residual) > tolerances.Star)
                    findings.BonusMismatch.Add(new SrFinding(row.Stored.BeatmapId, row.Stored.Name, variant, residual, $"moved by {delta:0.0000}, the length bonus for {row.CellsFor(variant):0.##} cells is {row.ExpectedBonus(variant):0.0000}"));

                if (row.Stored.Rating(variant) is double before && row.New(variant) is double after && Math.Floor(before) != Math.Floor(after))
                    findings.WholeStarCrossing.Add(new SrFinding(row.Stored.BeatmapId, row.Stored.Name, variant, after - before, $"{before:0.00} -> {after:0.00} crosses a whole star"));
            }

            // Not a 152 predicate: a stored char_count that disagrees with the reparse says the row
            // was stale BEFORE this change, which is the usual explanation for a residual outlier.
            if (row.Stored.CharCount is int stored && stored != row.PaceCells)
                findings.StaleCharCount.Add(new SrFinding(row.Stored.BeatmapId, row.Stored.Name, SrVariant.Base, row.PaceCells - stored, $"stored char_count {stored}, reparse {row.PaceCells}"));
        }

        foreach (var variant in Variants)
            CheckMonotone(rows, variant, findings);

        findings.FittedBonus = Length.Fit.Over(
            rows.Where(r => r.Resolved && r.Delta(SrVariant.Base) is not null && r.Cells > Length.REFERENCE_CELLS)
                .Select(r => (Math.Log10(r.Cells / Length.REFERENCE_CELLS), r.Delta(SrVariant.Base)!.Value)));

        return findings;
    }

    public static readonly SrVariant[] Variants = Enum.GetValues<SrVariant>();

    /// <summary>
    /// MONOTONE IN CELLS: a map with more typeable cells never gains less than a shorter one.
    ///
    /// <para>Checked over CELL-COUNT GROUPS rather than row by row, because "more cells" has to mean
    /// STRICTLY more: two maps of identical length may gain identical amounts, and a row-by-row walk
    /// would report that as a break the moment floating point put them a hair apart. So each group's
    /// smallest gain is held against the largest gain seen at any strictly smaller count, and the
    /// pair that produced it is named on both sides.</para>
    /// </summary>
    private static void CheckMonotone(IReadOnlyList<SrRow> rows, SrVariant variant, SrFindings findings)
    {
        var groups = rows.Where(r => r.Resolved && r.Delta(variant) is not null)
                         .GroupBy(r => r.CellsFor(variant))
                         .OrderBy(g => g.Key)
                         .ToList();

        SrRow? bestSoFar = null;

        foreach (var group in groups)
        {
            var weakest = group.OrderBy(r => r.Delta(variant)!.Value).First();

            if (bestSoFar is SrRow best && weakest.Delta(variant)!.Value < best.Delta(variant)!.Value - Tolerances.BOUND_EPSILON)
            {
                findings.MonotonicityBreaks.Add(new MonotonicityBreak(
                    variant,
                    weakest.Stored.BeatmapId, weakest.Stored.Name, weakest.CellsFor(variant), weakest.Delta(variant)!.Value,
                    best.Stored.BeatmapId, best.Stored.Name, best.CellsFor(variant), best.Delta(variant)!.Value));
            }

            var strongest = group.OrderByDescending(r => r.Delta(variant)!.Value).First();

            if (bestSoFar is null || strongest.Delta(variant)!.Value > bestSoFar.Delta(variant)!.Value)
                bestSoFar = strongest;
        }
    }
}
