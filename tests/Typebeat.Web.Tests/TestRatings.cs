using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// A beatmap RATING MATRIX built by hand, for the tests that need a map to be priceable without
/// parsing one (<c>beatmaps.ratings</c>, 034_ratings_matrix.sql).
///
/// <para>WHY EVERY pp TEST THAT TOUCHES THE DATABASE NEEDS THIS NOW. Until
/// <see cref="PerformancePoints.VERSION"/> 22 a play was priced from the star COLUMNS, and
/// <c>difficulty_rating</c> is NOT NULL, so a fixture that inserted a beatmap row got a priceable
/// map for free. A price needs the map's DIFFICULT CHARACTERS too now, and those live only in the
/// matrix, which is a nullable column: a fixture that does not write one leaves every play on its
/// map PENDING, which is the column's whole contract and is exactly what a real unswept row does.
/// So a test about pricing has to give its map a matrix, and a test about PENDING has to withhold
/// one.</para>
///
/// <para>The documents are written through <see cref="BeatmapRatings.Parse"/> rather than
/// assembled in C#, so a fixture exercises the same parse the server does and a change to the
/// document's shape fails here rather than silently producing a matrix nothing can read.</para>
/// </summary>
internal static class TestRatings
{
    /// <summary>
    /// The difficult-character count a fixture gives a map when it does not care what the number is:
    /// its own cell count, which is the largest value the model can produce and therefore the
    /// gentlest miss penalty. A fixture that IS about the penalty states its own.
    /// </summary>
    public const double DEFAULT_DIFFICULT_CHARACTERS = 500;

    /// <summary>
    /// A matrix of exactly the cells given, and nothing else, so a lookup for anything absent is
    /// PENDING. The difficult-character count defaults per cell rather than being derived from the
    /// stars, because on a real map the two move independently.
    /// </summary>
    public static BeatmapRatings Of(params (LyricDifficulty.JudgementArm Arm, bool Literate, double Rate, double Stars, double Difficult)[] cells)
    {
        var body = new JObject();

        foreach (var cell in cells)
            body[BeatmapRatings.Key(cell.Arm, cell.Literate, cell.Rate)] = new JObject { ["sr"] = cell.Stars, ["dc"] = cell.Difficult };

        return BeatmapRatings.Parse(Document(body))!;
    }

    /// <summary>
    /// All eighteen cells from the six star figures a fixture already holds: the arm-none ones are
    /// those figures exactly, and the two other arms take the same numbers, so a fixture that is not
    /// about the arms reads the rating it asked for whatever stack the play carries. A null rate or
    /// stream rating is OMITTED rather than defaulted, so "this map has no sr_dt yet" stays
    /// expressible.
    /// </summary>
    public static BeatmapRatings FromStars(
        double baseStars,
        double? doubleTime = null,
        double? halfTime = null,
        double? literate = null,
        double? literateDoubleTime = null,
        double? literateHalfTime = null,
        double difficult = DEFAULT_DIFFICULT_CHARACTERS)
    {
        var cells = new List<(LyricDifficulty.JudgementArm, bool, double, double, double)>();

        void add(bool stream, double rate, double? stars)
        {
            if (stars is not double value)
                return;

            foreach ((string _, LyricDifficulty.JudgementArm arm) in BeatmapRatings.Arms)
                cells.Add((arm, stream, rate, value, difficult));
        }

        add(false, 1.0, baseStars);
        add(false, RateMods.DoubleTimeBaseRate, doubleTime);
        add(false, RateMods.HalfTimeBaseRate, halfTime);
        add(true, 1.0, literate);
        add(true, RateMods.DoubleTimeBaseRate, literateDoubleTime);
        add(true, RateMods.HalfTimeBaseRate, literateHalfTime);

        return Of([.. cells]);
    }

    /// <summary>The stored document, for a fixture writing the column with raw SQL.</summary>
    public static string Json(
        double baseStars,
        double? doubleTime = null,
        double? halfTime = null,
        double? literate = null,
        double? literateDoubleTime = null,
        double? literateHalfTime = null,
        double difficult = DEFAULT_DIFFICULT_CHARACTERS)
        => FromStars(baseStars, doubleTime, halfTime, literate, literateDoubleTime, literateHalfTime, difficult).ToJson();

    private static string Document(JObject cells)
        => new JObject { ["version"] = BeatmapRatings.SCHEMA_VERSION, ["cells"] = cells }.ToString();
}
