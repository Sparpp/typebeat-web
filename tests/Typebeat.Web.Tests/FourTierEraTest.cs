using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// THE ROWS BACKLOG 147 CANNOT UN-JUDGE, and the two server behaviours that keep them readable.
///
/// <para>Backlog 133 and 134 replaced the millisecond judgement with a four-tier character-distance
/// one and SHIPPED it (game <c>66d8ae6</c>). Backlog 147 reverted the whole arc, so the client
/// grades on three millisecond tiers again and can never produce a <c>perfect</c> key or a
/// 200-weighted <c>great</c>. Reverting the client does not un-judge what was already submitted,
/// and this server re-runs its own contract over stored rows (<c>GateRefund.Qualifies</c>) and
/// re-prices them from their stored mods (<c>PpBackfill</c>). So the four-tier reading has to
/// survive the revert, and these are the tests that say so.</para>
///
/// <para>What replaced what: <c>CharacterDistanceParityTest</c> pinned the character axis into the
/// browser engine and went with it, but four of its cases were about the ERA rather than the axis,
/// and those are here, inverted. The parity of the JUDGEMENT itself is back where it was before
/// backlog 134, in the millisecond ladders that <c>ScoringContractTest</c> and the JS parity suites
/// already hold.</para>
/// </summary>
public class FourTierEraTest
{
    /// <summary>
    /// TODAY'S rules, stated directly rather than through a play: <c>great</c> is the whole cell at
    /// 300, <c>ok</c> 100, <c>meh</c> 50, and <c>good</c> (the uncorrected typo) 0 since backlog
    /// 213, against a
    /// per-cell maximum of 300. This is the arm the era discriminator falls to for anything that is
    /// not stamped as four-tier, including a malformed dictionary.
    /// </summary>
    [Test]
    public void AThreeTierRowIsPricedWithGreatWorthTheWholeCell()
    {
        var greatMax = new Dictionary<string, int> { ["great"] = 15 };

        var allGreat = ScoringContract.Recompute(new Dictionary<string, int> { ["great"] = 15 }, greatMax, 15);
        var allOk = ScoringContract.Recompute(new Dictionary<string, int> { ["ok"] = 15 }, greatMax, 15);
        var allMeh = ScoringContract.Recompute(new Dictionary<string, int> { ["meh"] = 15 }, greatMax, 15);
        var allTypo = ScoringContract.Recompute(new Dictionary<string, int> { ["good"] = 15 }, greatMax, 15);

        Assert.Multiple(() =>
        {
            Assert.That(allGreat.Accuracy, Is.EqualTo(1));
            Assert.That(allOk.Accuracy, Is.EqualTo(100d / 300).Within(1e-12));
            Assert.That(allMeh.Accuracy, Is.EqualTo(50d / 300).Within(1e-12));

            // The typo is re-weighted to a MISS's 0 since backlog 213, where backlog 124 had put it
            // at the meh's 50: an uncorrected typo is a miss, so it pays the whole cell in accuracy
            // and not merely the most a judged cell could pay. It is still NOT typed, so it still
            // costs rank exactly as a miss does, which is the half backlog 126 had already done.
            var allMiss = ScoringContract.Recompute(new Dictionary<string, int> { ["miss"] = 15 }, greatMax, 0);

            Assert.That(allTypo.Accuracy, Is.Zero);
            Assert.That(allTypo.Accuracy, Is.EqualTo(allMiss.Accuracy));
            Assert.That(allTypo.Accuracy, Is.LessThan(allMeh.Accuracy), "the fold is a real drop, not a relabelling");
            Assert.That(allTypo.Completion, Is.Zero);
            Assert.That(allTypo.Completion, Is.EqualTo(allMiss.Completion));
            Assert.That(allTypo.Rank, Is.EqualTo("D"));

            // ...and all three quality tiers ARE typed, so any of them alone is a full completion.
            Assert.That(allGreat.Completion, Is.EqualTo(1));
            Assert.That(allMeh.Completion, Is.EqualTo(1));
            Assert.That(allMeh.Rank, Is.EqualTo("X"), "rank is graded on cells typed, never on timing");

            // A clean three-tier full combo sits exactly ON its ceiling, which is the test
            // GateRefund applies to a stored row before it will re-rank it.
            Assert.That(allGreat.StatisticsValid, Is.True);
            Assert.That(allGreat.TotalScoreCeiling, Is.EqualTo(1_000_000));
            Assert.That(allGreat.TheoreticalMaxCombo, Is.EqualTo(15));
        });
    }

    /// <summary>
    /// A ROW STORED UNDER THE FOUR-TIER LADDER STILL READS EXACTLY AS IT WAS SUBMITTED, which is
    /// what the era discriminator is for. Such a row carries <c>perfect</c> as its top tier and a
    /// <c>great</c> that was the SECOND tier, worth 200, and there is no way to tell that
    /// <c>great</c> from today's top-tier one by the key alone. Its own <c>maximum_statistics</c> is
    /// the stamp: the MaxResult moved from Great to Perfect in the same change that re-weighted
    /// <c>great</c>, and back with it, so a maximum keyed <c>perfect</c> can only be a row judged
    /// under that ladder.
    ///
    /// <para>Without it a four-tier SS would recompute ABOVE the accuracy it was submitted with (its
    /// second-tier cells priced at 300 against an unmoved per-cell maximum), so its stored numbers
    /// and its recomputed ones would disagree, and <c>GateRefund.Qualifies</c>, the one path that
    /// re-runs this contract over rows the database already holds, would read the row as
    /// tampered.</para>
    /// </summary>
    [Test]
    public void AScoreStoredUnderTheFourthTierRecomputesToWhatItWasSubmittedWith()
    {
        var fourTierMax = new Dictionary<string, int> { ["perfect"] = 15 };

        // A four-tier SS: every cell in the tightest window.
        var fourTierClean = ScoringContract.Recompute(
            new Dictionary<string, int> { ["perfect"] = 15 }, fourTierMax, maxCombo: 15);

        // Fifteen SECOND-tier cells. Under that ladder they were worth 200 of the cell's 300.
        var fourTierGreats = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 15 }, fourTierMax, maxCombo: 15);

        // The identical statistics on a row stamped with TODAY's maximum, where the same key is the
        // top tier and worth the whole cell.
        var threeTierGreats = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 15 },
            new Dictionary<string, int> { ["great"] = 15 },
            maxCombo: 15);

        Assert.Multiple(() =>
        {
            Assert.That(fourTierClean.StatisticsValid, Is.True, "a stored row must never become unrankable");
            Assert.That(fourTierClean.Accuracy, Is.EqualTo(1));
            Assert.That(fourTierClean.JudgedAccuracy, Is.EqualTo(1));
            Assert.That(fourTierClean.Completion, Is.EqualTo(1));
            Assert.That(fourTierClean.Rank, Is.EqualTo("X"));
            Assert.That(fourTierClean.TheoreticalMaxCombo, Is.EqualTo(15));
            Assert.That(fourTierClean.TotalScoreCeiling, Is.EqualTo(1_000_000));

            // The discriminator is doing real work: the same dictionary reads 2/3 under the old
            // stamp and 1 under today's, and it is the maximum's key that decides which.
            Assert.That(fourTierGreats.Accuracy, Is.EqualTo(200d / 300).Within(1e-12));
            Assert.That(threeTierGreats.Accuracy, Is.EqualTo(1));
            Assert.That(fourTierGreats.Accuracy, Is.Not.EqualTo(threeTierGreats.Accuracy));

            // A mixed four-tier row, where the two eras genuinely disagree on the total:
            // (300*10 + 200*2 + 100*2) / (300*15).
            var mixed = ScoringContract.Recompute(
                new Dictionary<string, int> { ["perfect"] = 10, ["great"] = 2, ["ok"] = 2, ["miss"] = 1 },
                fourTierMax,
                maxCombo: 12);

            Assert.That(mixed.Accuracy, Is.EqualTo(3600d / 4500).Within(1e-12));
            Assert.That(mixed.StatisticsValid, Is.True);
        });
    }

    /// <summary>
    /// A dictionary carrying NEITHER stamp, or BOTH, falls to today's rules. That is the right
    /// default for anything malformed (a hostile submission cannot pick the reading it prefers by
    /// omitting a key), and it is also what an empty maximum does, which is the shape score-submit
    /// must never throw on.
    /// </summary>
    [Test]
    public void AnUnstampedOrDoublyStampedMaximumFallsToTodaysRules()
    {
        var neither = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 3 },
            new Dictionary<string, int> { ["ok"] = 3 },
            maxCombo: 3);

        var both = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 4 },
            new Dictionary<string, int> { ["perfect"] = 2, ["great"] = 2 },
            maxCombo: 4);

        Assert.Multiple(() =>
        {
            // 300 per great against a 300 maximum: today's weight, not the four-tier 200.
            Assert.That(neither.Accuracy, Is.EqualTo(1));
            Assert.That(both.Accuracy, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// pp counts a cell of the map per NOTE key, and a four-tier row's <c>perfect</c> cells are
    /// cells of the map like any others. Dropping the key from
    /// <c>PerformancePoints</c>'s note list with the tier would make each of those rows read as a
    /// map with almost no notes at all, shrinking the length bonus and inflating the combo ratio,
    /// every time <c>PpBackfill</c> repriced it.
    /// </summary>
    [Test]
    public void PpStillCountsAStoredPerfectAsANoteOfTheMap()
    {
        var fourTier = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["perfect"] = 10,
            ["great"] = 2,
            ["ok"] = 1,
            ["miss"] = 2,
        });

        var threeTier = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["great"] = 12,
            ["ok"] = 1,
            ["miss"] = 2,
        });

        Assert.Multiple(() =>
        {
            Assert.That(fourTier.Notes, Is.EqualTo(15), "every judged cell is a note, whichever tier it landed in");
            Assert.That(fourTier.Misses, Is.EqualTo(2));
            Assert.That(fourTier.Notes, Is.EqualTo(threeTier.Notes), "the same map, read under either stamp");
        });
    }
}
