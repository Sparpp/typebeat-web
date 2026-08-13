using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// DB-free tests for the server-side score recompute (ScoringContract). Every expected value is
/// hand-derived from the client's numeric weights (ScoreProcessor.GetBaseScoreForResult) and the
/// standardised total-score formula (ScoreProcessor.ComputeTotalScore) with comboProgress at its
/// maximum and the no-mod multiplier = 1.
/// </summary>
public class ScoringContractTest
{
    private static Dictionary<string, int> Dict(params (string key, int count)[] entries)
        => entries.ToDictionary(e => e.key, e => e.count);

    // ---- accuracy recompute ----

    [Test]
    public void Accuracy_RecomputedFromWeights_ButRankGradesOnCompletion()
    {
        // 8×great + 1×ok + 1×meh out of 10 max-great objects.
        // numerator   = 300·8 + 100·1 + 50·1 = 2550
        // denominator = 300·10              = 3000  → accuracy = 0.85
        // completion  = 10 typed / 10 cells = 1.0   → rank X, sloppy timing costs accuracy,
        // score and combo, but never the grade.
        var r = ScoringContract.Recompute(
            Dict(("great", 8), ("ok", 1), ("meh", 1)),
            Dict(("great", 10)),
            maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(0.85).Within(1e-9));
            Assert.That(r.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Rank, Is.EqualTo("X"));
            Assert.That(r.TheoreticalMaxCombo, Is.EqualTo(10));
            // ceiling = round(500000·0.85 + 500000·0.85^5·1) = round(646852.65625) = 646853
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(646853L));
        });
    }

    [Test]
    public void WorstTimingEverywhere_StillRankX_WhenEveryCellTyped()
    {
        // The headline rule: an all-meh play (every window scraped) has accuracy 50/300 ≈ 0.167
        // but typed 100% of the map: SS.
        var r = ScoringContract.Recompute(Dict(("meh", 10)), Dict(("great", 10)), maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(50.0 / 300.0).Within(1e-9));
            Assert.That(r.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Rank, Is.EqualTo("X"));
        });
    }

    /// <summary>
    /// The uncorrected-typo key (backlog 124 and 126). Held against the two things it must sit
    /// between: a <c>meh</c>, which is a cell TYPED (late, but right), and a <c>miss</c>, which is a
    /// cell the line ran out of time on. The typo weighs the same as the meh for accuracy and costs
    /// the same as the miss for completion, and stays its own key so pp can tell it from both.
    /// </summary>
    [Test]
    public void UncorrectedTypos_CostCompletionLikeAMiss_AndAccuracyLikeAMeh()
    {
        var typo = ScoringContract.Recompute(Dict(("great", 9), ("good", 1)), Dict(("great", 10)), maxCombo: 9);
        var meh = ScoringContract.Recompute(Dict(("great", 9), ("meh", 1)), Dict(("great", 10)), maxCombo: 10);
        var miss = ScoringContract.Recompute(Dict(("great", 9), ("miss", 1)), Dict(("great", 10)), maxCombo: 9);

        Assert.Multiple(() =>
        {
            Assert.That(typo.StatisticsValid, Is.True);

            // Accuracy: (300·9 + 50) / 3000, i.e. the meh weight and NOT the base ruleset's 200 for
            // `good`. The client re-weights the tier, and this table has to carry the same number.
            Assert.That(typo.Accuracy, Is.EqualTo(2750.0 / 3000.0).Within(1e-12));
            Assert.That(typo.Accuracy, Is.EqualTo(meh.Accuracy).Within(1e-12));

            // Completion: 9 of 10 typed, exactly as the miss reads, and NOT the meh's 10 of 10.
            Assert.That(typo.Completion, Is.EqualTo(0.9).Within(1e-12));
            Assert.That(typo.Completion, Is.EqualTo(miss.Completion).Within(1e-12));
            Assert.That(typo.Rank, Is.EqualTo("A"));
            Assert.That(meh.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(meh.Rank, Is.EqualTo("X"));

            // The typo is a JUDGEMENT, so it is in the denominator: a play made entirely of them is
            // completion 0 and a D, not 1-over-nothing.
            var allTypos = ScoringContract.Recompute(Dict(("good", 10)), Dict(("great", 10)), maxCombo: 0);
            Assert.That(allTypos.StatisticsValid, Is.True, "one judgement per cell, so still in bounds");
            Assert.That(allTypos.Completion, Is.Zero);
            Assert.That(allTypos.Rank, Is.EqualTo("D"));

            // ...and pp still counts it as a note that is not a miss, which is the whole reason it
            // is not simply stored as a miss.
            var counts = PerformancePoints.CountNotes(Dict(("great", 9), ("good", 1), ("combo_break", 1)));
            Assert.That(counts.Notes, Is.EqualTo(10));
            Assert.That(counts.Misses, Is.Zero);
            Assert.That(counts.Typos, Is.EqualTo(1));
        });
    }

    [Test]
    public void Accuracy_MissesCountTowardDenominator_AndCompletion()
    {
        // 5×great + 5×miss out of 10. accuracy 0.5; completion 5/10 = 0.5 → rank D.
        var r = ScoringContract.Recompute(
            Dict(("great", 5), ("miss", 5)),
            Dict(("great", 10)),
            maxCombo: 5);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(r.Completion, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(r.Rank, Is.EqualTo("D"));
            // misses do not increase combo → theoretical max combo is still the note count (10).
            Assert.That(r.TheoreticalMaxCombo, Is.EqualTo(10));
            // ceiling = round(500000·0.5 + 500000·0.5^5·1) = 250000 + 15625 = 265625
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(265625L));
        });
    }

    [Test]
    public void OneMissedCell_DeniesTheSS()
    {
        // 99/100 typed → completion 0.99 → S, however clean the timing was.
        var r = ScoringContract.Recompute(
            Dict(("great", 99), ("miss", 1)),
            Dict(("great", 100)),
            maxCombo: 99);

        Assert.Multiple(() =>
        {
            Assert.That(r.Completion, Is.EqualTo(0.99).Within(1e-9));
            Assert.That(r.Rank, Is.EqualTo("S"));
        });
    }

    [Test]
    public void FullCombo_Perfect_IsRankX_AndCeilingIsMaxScore()
    {
        var r = ScoringContract.Recompute(Dict(("great", 10)), Dict(("great", 10)), maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Rank, Is.EqualTo("X"));
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(1_000_000L));
        });
    }

    // ---- total-score ceiling ----

    [Test]
    public void TotalScoreWithinBounds_AcceptsAtOrBelowCeiling_RejectsAbove()
    {
        var r = ScoringContract.Recompute(Dict(("great", 10)), Dict(("great", 10)), maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(ScoringContract.TotalScoreWithinBounds(0, r), Is.True);
            Assert.That(ScoringContract.TotalScoreWithinBounds(999_999, r), Is.True);
            Assert.That(ScoringContract.TotalScoreWithinBounds(1_000_000, r), Is.True);
            Assert.That(ScoringContract.TotalScoreWithinBounds(1_000_001, r), Is.False);
            Assert.That(ScoringContract.TotalScoreWithinBounds(long.MaxValue, r), Is.False);
            Assert.That(ScoringContract.TotalScoreWithinBounds(-1, r), Is.False);
        });
    }

    // ---- hard bounds / tamper rejection ----

    [Test]
    public void Statistics_ExceedingMaximums_AreRejected()
    {
        // 11 greats claimed but only 10 objects exist → numerator > denominator → invalid.
        var r = ScoringContract.Recompute(Dict(("great", 11)), Dict(("great", 10)), maxCombo: 10);
        Assert.That(r.StatisticsValid, Is.False);
    }

    [Test]
    public void MaxCombo_AboveTheoretical_IsRejected()
    {
        // Theoretical max combo is 10 (10 combo-increasing greats); claiming 11 is impossible.
        var r = ScoringContract.Recompute(Dict(("great", 10)), Dict(("great", 10)), maxCombo: 11);
        Assert.Multiple(() =>
        {
            Assert.That(r.TheoreticalMaxCombo, Is.EqualTo(10));
            Assert.That(r.StatisticsValid, Is.False);
        });
    }

    [Test]
    public void MaxCombo_AtTheoretical_IsValid()
    {
        var r = ScoringContract.Recompute(Dict(("great", 10)), Dict(("great", 10)), maxCombo: 10);
        Assert.That(r.StatisticsValid, Is.True);
    }

    [Test]
    public void NegativeCounts_AreRejected()
    {
        var r = ScoringContract.Recompute(Dict(("great", -1)), Dict(("great", 10)), maxCombo: 0);
        Assert.That(r.StatisticsValid, Is.False);
    }

    [Test]
    public void EmptyMaximumStatistics_IsRejected_AndDoesNotThrow()
    {
        var r = ScoringContract.Recompute(Dict(("great", 5)), new Dictionary<string, int>(), maxCombo: 0);
        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.False);
            Assert.That(r.Accuracy, Is.EqualTo(0));
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(0));
        });
    }

    [Test]
    public void NullDictionaries_DoNotThrow()
    {
        var r = ScoringContract.Recompute(null, null, maxCombo: 0);
        Assert.That(r.StatisticsValid, Is.False);
    }

    [Test]
    public void UnknownResultKeys_ContributeNothing()
    {
        // A hostile client injecting an unrecognised high-value key cannot inflate accuracy or score.
        var r = ScoringContract.Recompute(
            Dict(("great", 10), ("super_ultra_bonus", 9999)),
            Dict(("great", 10)),
            maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(1_000_000L));
        });
    }

    // ---- rank cutoffs (TypeBeatScoreProcessor.RankFromCompletion, keep in sync) ----

    [TestCase(1.0, "X")]
    [TestCase(0.99, "S")]
    [TestCase(0.95, "S")]
    [TestCase(0.9499, "A")]
    [TestCase(0.90, "A")]
    [TestCase(0.8999, "B")]
    [TestCase(0.80, "B")]
    [TestCase(0.7999, "C")]
    [TestCase(0.70, "C")]
    [TestCase(0.6999, "D")]
    [TestCase(0.0, "D")]
    public void RankFromCompletion_MatchesCutoffs(double completion, string expected)
        => Assert.That(ScoringContract.RankFromCompletion(completion), Is.EqualTo(expected));

    // ---- failed (partial) plays: judged-only accuracy drives the ceiling ----
    // Regression for the review finding: the client's running accuracy denominator only counts
    // JUDGED cells (ScoreProcessor.cs:261,393), so a play failed 100 cells into a 1000-cell map
    // with all-greats has client accuracy 1.0; a whole-map ceiling would falsely flag its
    // honest total as tampered.

    [Test]
    public void FailedPlay_JudgedAccuracyMatchesClientRunningAccuracy()
    {
        // 100 greats judged, map has 1000 cells.
        // whole-map accuracy = 30000/300000 = 0.1; judged accuracy = 30000/30000 = 1.0.
        var r = ScoringContract.Recompute(
            Dict(("great", 100)),
            Dict(("great", 1000)),
            maxCombo: 100);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(0.1).Within(1e-9));
            Assert.That(r.JudgedAccuracy, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.AccuracyProgress, Is.EqualTo(0.1).Within(1e-9));
            // Completion is whole-map: this fail typed 10% of the map, not 100%-of-what-it-saw.
            Assert.That(r.Completion, Is.EqualTo(0.1).Within(1e-9));
            // ceiling = round(500000·1·1 + 500000·1^5·0.1) = 550000, computed from the JUDGED
            // accuracy. The whole-map value would have given ~50001 and rejected honest totals.
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(550_000L));
        });
    }

    [Test]
    public void FailedPlay_HonestTotalIsWithinBounds()
    {
        // The finding's concrete scenario: honest client total ≈ 65,800 for the play above.
        var r = ScoringContract.Recompute(Dict(("great", 100)), Dict(("great", 1000)), maxCombo: 100);
        Assert.That(ScoringContract.TotalScoreWithinBounds(65_800, r), Is.True);
    }

    [Test]
    public void FullyJudgedPlay_JudgedAndWholeMapAccuracyAgree()
    {
        // Completed play: every cell judged → the two accuracies (and hence the old and new
        // ceiling formulas) are identical, so ranked-score behavior is unchanged.
        var r = ScoringContract.Recompute(
            Dict(("great", 8), ("ok", 1), ("meh", 1)),
            Dict(("great", 10)),
            maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.JudgedAccuracy, Is.EqualTo(r.Accuracy).Within(1e-12));
            Assert.That(r.TotalScoreCeiling, Is.EqualTo(646_853L));
        });
    }

    // ---- mistypes (combo_break), backlog 72 ----

    [Test]
    public void Mistypes_DoNotUnrankTheirOwnPlay()
    {
        // LANDMINE 1. StatisticsValid fails when accuracy-affecting judged counts exceed
        // maximum_statistics. A mistype has no counterpart there (maximum_statistics stays one
        // great per cell), so counting combo_break as a judgement would make any mistyped play look
        // like it contained more cells than the map has, and unrank every one of them. 400 mistypes
        // on a 10-cell map is deliberately absurd: it must still be a perfectly valid submission.
        var r = ScoringContract.Recompute(
            Dict(("great", 10), ("combo_break", 400)),
            Dict(("great", 10)),
            maxCombo: 10);

        Assert.Multiple(() =>
        {
            Assert.That(r.StatisticsValid, Is.True);
            Assert.That(r.Accuracy, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Completion, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Rank, Is.EqualTo("X"), "typing every cell is an SS, stumbles included");
            Assert.That(r.TheoreticalMaxCombo, Is.EqualTo(10), "a mistype is not a combo-increasing hit");
        });
    }

    [Test]
    public void Mistypes_RecomputeToExactlyWhatTheSameScoreWithoutThemDoes()
    {
        // Two statements in one: the new key changes nothing the contract computes, AND an OLD
        // client that omits it entirely is handled identically. Whole-record equality, so a future
        // field cannot quietly start reacting to it.
        var maximums = Dict(("great", 200));

        var withoutKey = ScoringContract.Recompute(
            Dict(("great", 150), ("ok", 20), ("meh", 10), ("miss", 20)), maximums, maxCombo: 60);

        var withKey = ScoringContract.Recompute(
            Dict(("great", 150), ("ok", 20), ("meh", 10), ("miss", 20), ("combo_break", 73)), maximums, maxCombo: 60);

        var withZeroKey = ScoringContract.Recompute(
            Dict(("great", 150), ("ok", 20), ("meh", 10), ("miss", 20), ("combo_break", 0)), maximums, maxCombo: 60);

        Assert.Multiple(() =>
        {
            Assert.That(withKey, Is.EqualTo(withoutKey));
            Assert.That(withZeroKey, Is.EqualTo(withoutKey));
        });
    }

    [Test]
    public void Mistypes_NegativeCountIsStillTamperShaped()
    {
        // The key is ignored for every numeric purpose, but not for the sanity check: a negative
        // count describes no play and must not pass as a valid submission.
        var r = ScoringContract.Recompute(
            Dict(("great", 10), ("combo_break", -5)),
            Dict(("great", 10)),
            maxCombo: 10);

        Assert.That(r.StatisticsValid, Is.False);
    }

    [Test]
    public void MixedFailedPlay_JudgedAccuracyUsesJudgedDenominator()
    {
        // 50 greats + 10 oks + 5 misses judged (65 of 200 cells).
        // numerator = 300·50 + 100·10 = 16000; judged denominator = 300·65 = 19500.
        var r = ScoringContract.Recompute(
            Dict(("great", 50), ("ok", 10), ("miss", 5)),
            Dict(("great", 200)),
            maxCombo: 55);

        Assert.Multiple(() =>
        {
            Assert.That(r.JudgedAccuracy, Is.EqualTo(16000.0 / 19500.0).Within(1e-12));
            Assert.That(r.Accuracy, Is.EqualTo(16000.0 / 60000.0).Within(1e-12));
        });
    }
}
