using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Tools.RepriceReport;

/// <summary>
/// The two length terms backlog 152 swapped, restated here as EXPECTATIONS the report checks the
/// data against. Neither is reachable from the code that owns it: <c>LyricDifficulty</c>'s
/// <c>length_stars</c>, <c>reference_cells</c> and its per-map cell count are private, and pp's old
/// length factor was deleted outright, so a tool that wants to say "this map moved by exactly its
/// length bonus and nothing else" has to be able to say what that bonus was.
///
/// <para>THESE ARE MIRRORS, AND MIRRORS DRIFT, so nothing here is trusted on its own. Every number
/// below is FITTED BACK OUT OF THE DATA as well (see <see cref="Fit"/>): the report prints the
/// constant it assumed next to the constant the catalogue actually moved by, and a disagreement is
/// a finding rather than something the arithmetic quietly absorbs. That is also what validates
/// <see cref="Count"/>: a wrong cell count cannot fit a clean line through the observed deltas.</para>
/// </summary>
internal static class Length
{
    /// <summary>
    /// <c>LyricDifficulty.length_stars</c>: stars gained per decade of typing above the pivot.
    /// Backlog 152 set it to 0.12.
    /// </summary>
    public const double LENGTH_STARS = 0.12;

    /// <summary>
    /// <c>LyricDifficulty.reference_cells</c>: the pivot, where the bonus is exactly zero. The
    /// max(0, .) clamp below it is what leaves every sub-100-cell map (and every short synthetic
    /// fixture) rating byte-identically to what it did before 152.
    /// </summary>
    public const double REFERENCE_CELLS = 100;

    /// <summary>
    /// <c>LyricDifficulty.freestyle_cost_weight</c>: what one freestyle slot is worth as a fraction
    /// of an ordinary cell. Backlog 211 set it to 0.25, having priced it at nothing before that.
    /// </summary>
    public const double FREESTYLE_COST_WEIGHT = 0.25;

    /// <summary>
    /// pp's DELETED length factor, <c>max(0.1, 1 + 0.50*log10(notes/100))</c>. It is gone from
    /// <c>PerformancePoints</c>, which is the whole point of 152, and it lives on here as the
    /// prediction for what a stored pp should divide by: a row's stored price ought to be its
    /// today-formula price times exactly this, and the report lists every row where it is not.
    ///
    /// <para>NOTE THE FLOOR AND THE SIGN. Below 100 notes the factor is BELOW 1, so deleting it
    /// INFLATES those plays rather than deflating them, up to 10x at the floor. "Deflation graded by
    /// map length" is therefore a statement about the gradient, not about every row, and the report
    /// says so instead of flagging a short map as a violation.</para>
    /// </summary>
    public const double LENGTH_WEIGHT = 0.50;

    /// <summary>The floor the deleted factor clamped at, i.e. its value for a nearly empty play.</summary>
    public const double LENGTH_FLOOR = 0.10;

    /// <summary>
    /// The additive star bonus a map of <paramref name="cells"/> typeable cells gains under 152.
    /// Mirrors the tail of <c>LyricDifficulty.Compute</c>.
    /// </summary>
    public static double StarBonus(double cells)
        => cells <= 0 ? 0 : LENGTH_STARS * Math.Max(0, Math.Log10(cells / REFERENCE_CELLS));

    /// <summary>
    /// The factor a play of <paramref name="notes"/> notes used to be multiplied by, i.e. what its
    /// stored pp carries and its repriced pp does not.
    /// </summary>
    public static double DeletedPpFactor(int notes)
        => notes <= 0 ? LENGTH_FLOOR : Math.Max(LENGTH_FLOOR, 1 + LENGTH_WEIGHT * Math.Log10(notes / REFERENCE_CELLS));

    /// <summary>
    /// A map's PRICED CELL COUNT as the length bonus counts it: mirrors
    /// <c>LyricDifficulty</c>'s own accumulator summed over the tokens of every line, which is a
    /// different number from <c>LyricPace.PaceStatistics.TypeableCellCount</c> (that one adds a
    /// cell per token gap and counts a freestyle slot as a whole cell).
    ///
    /// <para>FRACTIONAL since backlog 211, because that accumulator is: a freestyle slot is worth
    /// <c>LyricDifficulty.freestyle_cost_weight</c>, a quarter of an ordinary cell, and counting it
    /// as nothing (which is what this did while the rating did too) would now understate the length
    /// of every map with a freestyle section and make its bonus look unexplained.</para>
    ///
    /// <para>Restated rather than read because 152 deliberately made the count private: "computed
    /// inside Compute from the lines it already walks, never threaded in from outside, so client and
    /// web cannot disagree about it". A REPORT needs the number anyway, so this is the one place it
    /// is re-derived, it shares the <see cref="Typeability"/> primitives the real one keys on, and
    /// <see cref="Fit"/> is what proves it right on real data.</para>
    /// </summary>
    /// <param name="literate">
    /// The cell stream to count. False is the default stripped stream (typeable chars only, marks
    /// excluded); true is the Literate mod's, where every supported punctuation mark is a real cell,
    /// so the three <c>sr_literate*</c> ratings gain a bonus computed off a LARGER count than the
    /// three plain ones.
    /// </param>
    public static double Count(IReadOnlyList<LyricLine> lines, bool literate)
    {
        double cells = 0;

        foreach (var line in lines)
        {
            foreach (string token in line.RawText.Split(' '))
            {
                foreach (char c in token)
                {
                    if (Typeability.IsTypeable(c) || (literate && Typeability.IsPunctuation(c)))
                        cells++;
                    else if (Typeability.IsFreestyle(c))
                        cells += FREESTYLE_COST_WEIGHT;
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// The constant the DATA moved by: the least-squares slope through the origin of
    /// (log10(cells / pivot), observed delta), over the samples where the term is even active.
    /// Fitting through the origin is the right model because the bonus is exactly that, a slope with
    /// no intercept, and it makes the fit falsifiable in both ways that matter: a retuned
    /// <c>length_stars</c> moves <see cref="Slope"/>, and a wrong cell count (or a stored rating
    /// stale for some OTHER reason) shows up as scatter in <see cref="MaxResidual"/>.
    /// </summary>
    public readonly record struct Fit(double Slope, double MaxResidual, int Samples)
    {
        public static Fit Over(IEnumerable<(double X, double Y)> samples)
        {
            double sxy = 0, sxx = 0;
            int n = 0;

            var kept = new List<(double X, double Y)>();

            foreach (var (x, y) in samples)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0)
                    continue;

                sxy += x * y;
                sxx += x * x;
                n++;
                kept.Add((x, y));
            }

            if (n == 0 || sxx <= 0)
                return new Fit(double.NaN, double.NaN, n);

            double slope = sxy / sxx;
            double worst = 0;

            foreach (var (x, y) in kept)
                worst = Math.Max(worst, Math.Abs(y - slope * x));

            return new Fit(slope, worst, n);
        }
    }
}
