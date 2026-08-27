using Newtonsoft.Json.Linq;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Tests;

/// <summary>
/// BACKLOG 213, THE DISPLAY HALF: the MISS column of every judgement readout on the site counts
/// uncorrected typos as well as dropped cells.
///
/// <para>A cell left holding a wrong character is stored under its own key, <c>good</c>. Backlog 140
/// took away the column it used to have (the typo number players are shown became
/// <c>combo_break</c>, which counts wrong KEYPRESSES), and until backlog 213 nothing put it back
/// anywhere, so a character the player never typed right appeared in NO column at all. The field
/// report that ended that was a stored score reading MISS 0 while carrying <c>good: 2</c>.</para>
///
/// <para>Pinned here as a pure function rather than through a rendered page, because the fold is one
/// expression shared by the set page's leaderboard and podium and the /rankings top-plays board, and
/// the property that matters (the shown columns SUM to the judged cell count) is arithmetic rather
/// than markup.</para>
/// </summary>
public class JudgementDisplayTest
{
    private static JObject Statistics(string json) => JObject.Parse(json);

    /// <summary>
    /// THE FIELD REPORT'S OWN SHAPE. A play with no dropped cell at all and two cells left holding a
    /// wrong character used to read MISS blank; it reads MISS 2.
    /// </summary>
    [Test]
    public void ARowWithNoDroppedCellsStillShowsItsUncorrectedTypos()
        => Assert.That(JudgementDisplay.MissColumn(Statistics("""{"great":98,"good":2,"combo_break":5}""")), Is.EqualTo(2));

    /// <summary>
    /// The column is a SUM and not a substitution, and the property the fold buys: the four shown
    /// columns account for every judged cell, which they had not done since backlog 124 gave the
    /// uncorrected typo a key nothing displayed.
    /// </summary>
    [Test]
    public void TheMissColumnIsTheSumOfBothKindsAndTheColumnsAddUp()
    {
        var statistics = Statistics("""{"great":90,"ok":5,"meh":2,"good":3,"miss":7,"combo_break":11}""");

        int great = statistics.Value<int>("great");
        int ok = statistics.Value<int>("ok");
        int meh = statistics.Value<int>("meh");
        int miss = JudgementDisplay.MissColumn(statistics) ?? 0;

        Assert.Multiple(() =>
        {
            Assert.That(miss, Is.EqualTo(10), "7 cells nobody typed plus 3 left holding a wrong character");
            Assert.That(great + ok + meh + miss, Is.EqualTo(107), "and the shown columns account for every judged cell");
        });
    }

    /// <summary>
    /// BLANK AT ZERO, which is the rule the column has had since backlog 140: a clean run renders an
    /// empty cell rather than a "0" next to the equally empty typo cell, because those read as two
    /// different kinds of nothing. The fold must not turn a clean run into a "0".
    /// </summary>
    [TestCase("""{"great":100}""")]
    [TestCase("""{"great":100,"combo_break":4}""")]
    [TestCase("""{"great":100,"miss":0,"good":0}""")]
    public void ACleanRunRendersNothingRatherThanAZero(string json)
        => Assert.That(JudgementDisplay.MissColumn(Statistics(json)), Is.Null);

    /// <summary>
    /// A row from before the key existed, and a row that carries only one of the two, both read as
    /// they always did: the fold reduces to the old column at <c>good = 0</c>.
    /// </summary>
    [Test]
    public void TheFoldReducesToTheOldColumnWhenThereIsNoUncorrectedTypo()
    {
        Assert.Multiple(() =>
        {
            Assert.That(JudgementDisplay.MissColumn(Statistics("""{"great":90,"miss":10}""")), Is.EqualTo(10));
            Assert.That(JudgementDisplay.MissColumn(Statistics("""{"great":90,"good":10}""")), Is.EqualTo(10),
                "and a row carrying only the typo key reads as the ten dropped characters it is");
        });
    }

    /// <summary>
    /// Hostile input. Negative counts cannot reach a stored row through the submission path
    /// (<c>ScoringContract</c> refuses them), but a hand-edited jsonb must not make the fold read as
    /// a SUBTRACTION: each side is clamped before they are added, so a negative on either key
    /// contributes nothing rather than eating the other.
    /// </summary>
    [TestCase("""{"great":10,"miss":-5,"good":3}""", 3)]
    [TestCase("""{"great":10,"miss":4,"good":-2}""", 4)]
    public void TamperShapedCountsAreClampedRatherThanSubtracted(string json, int expected)
        => Assert.That(JudgementDisplay.MissColumn(Statistics(json)), Is.EqualTo(expected));
}
