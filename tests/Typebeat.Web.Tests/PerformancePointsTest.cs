using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The per-play pp formula (docs/pp.md, <see cref="PerformancePoints"/>). Pure arithmetic, no
/// database. Every expected value below is written as the formula spells it out rather than as a
/// hard-coded number, EXCEPT the handful of independently-computed reference values, which are
/// there to catch a plausible-looking but wrong refactor of the formula itself.
/// </summary>
[TestFixture]
public class PerformancePointsTest
{
    private static readonly IReadOnlyList<ScoreMod> no_mods = [];

    /// <summary>A clean-ish reference play: 4 stars, 500 notes, no misses, 90% acc, full combo.</summary>
    private const double reference_pp = 219.337706;

    [Test]
    public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
    {
        double pp = PerformancePoints.Compute(starRating: 4, notes: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

        Assert.That(pp, Is.EqualTo(reference_pp).Within(1e-5));
    }

    // ---------------------------------------------------------------------------------------------
    // Length bonus: the raw term crosses ZERO at ~3.73 notes and the floor at ~5.18, so the clamp
    // is what stops a degenerate map computing zero or negative pp from its length alone.
    // ---------------------------------------------------------------------------------------------

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(4)] // just past the zero crossing, but still far under the floor
    [TestCase(5)]
    public void LengthBonus_ClampsToTheFloorWhereTheRawTermWouldSinkBelowIt(int notes)
    {
        double raw = 1 + 0.70 * Math.Log10(Math.Max(notes, 1) / 100.0);

        Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(0.1).Within(1e-12),
            $"raw term at {notes} notes is {raw:0.####}");
    }

    [Test]
    public void LengthBonus_RawTermCrossesZeroAroundFourNotes()
    {
        // Pins the spec's "crosses zero around 4 notes" claim, i.e. the reason the clamp exists at
        // all: below the crossing the unclamped term is NEGATIVE, which would make pp negative.
        static double raw(double notes) => 1 + 0.70 * Math.Log10(notes / 100.0);

        Assert.Multiple(() =>
        {
            Assert.That(raw(3), Is.LessThan(0));
            Assert.That(raw(3.72), Is.LessThan(0));
            Assert.That(raw(3.73), Is.GreaterThan(0));
            // And it stays under the 0.1 floor until about 5.2 notes.
            Assert.That(raw(5.1), Is.LessThan(0.1));
            Assert.That(raw(5.2), Is.GreaterThan(0.1));
        });
    }

    [TestCase(6, 0.144706)]
    [TestCase(100, 1.0)]
    [TestCase(500, 1.489279)]
    [TestCase(1000, 1.7)]
    public void LengthBonus_IsTheLogBonusAboveTheFloor(int notes, double expected)
        => Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(expected).Within(1e-6));

    // ---------------------------------------------------------------------------------------------
    // Flashlight: unclamped the raw term dips BELOW 1.0 under ~46 notes, which would make a bonus
    // mod a penalty on short maps.
    // ---------------------------------------------------------------------------------------------

    [TestCase(1)]
    [TestCase(20)]
    [TestCase(45)]
    [TestCase(46)]
    public void FlashlightMultiplier_ClampsToOneOnShortMaps(int notes)
    {
        double raw = 1 + 0.02 + 0.06 * Math.Log10(notes / 100.0);

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.LessThan(1.0), "the raw term is below 1 here, which is what the clamp is for");
            Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(1.0).Within(1e-12));
        });
    }

    [TestCase(47, 1.000326)]
    [TestCase(100, 1.02)]
    [TestCase(500, 1.061938)]
    public void FlashlightMultiplier_GrowsWithLengthOnceAboveTheFloor(int notes, double expected)
        => Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(expected).Within(1e-6));

    [Test]
    public void FlashlightMultiplier_CrossesOneAtAboutFortySixNotes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.FlashlightMultiplier(46), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.FlashlightMultiplier(47), Is.GreaterThan(1.0));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Cleanliness: the sharp signal. A give-up run must collapse to nothing.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_GiveUpRunCollapsesTowardsZero()
    {
        // 1000 notes on a 4-star map, 900 of them missed: exactly the shape the miss term exists to
        // kill. It must not merely be "smaller", it must be negligible next to a clean play.
        double giveUp = PerformancePoints.Compute(4, notes: 1000, misses: 900, accuracy: 0.1, maxCombo: 10, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(giveUp, Is.GreaterThanOrEqualTo(0));
            Assert.That(giveUp, Is.LessThan(0.001));
            Assert.That(giveUp, Is.LessThan(reference_pp / 1000));
        });
    }

    [Test]
    public void Compute_MissingEveryNoteIsExactlyZero()
        => Assert.That(PerformancePoints.Compute(6, notes: 400, misses: 400, accuracy: 0, maxCombo: 0, no_mods), Is.Zero);

    [Test]
    public void Compute_MissesDominateAccuracyAndCombo()
    {
        // Same map, same length. A sloppy-but-complete play beats a high-accuracy play that dropped
        // 10% of the map, which is the whole point of the 7.5 exponent.
        double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.60, maxCombo: 500, no_mods);
        double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 50, accuracy: 0.93, maxCombo: 450, no_mods);

        Assert.That(sloppyButClean, Is.GreaterThan(accurateButMissy));
    }

    // ---------------------------------------------------------------------------------------------
    // Degenerate inputs: never NaN, never Infinity, never negative.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_DegenerateInputsAreFiniteAndNonNegative()
    {
        double[] stars = [0, -1, 0.0001, 10, double.NaN, double.PositiveInfinity];
        int[] noteCounts = [0, 1, 2, 4, 100];
        double[] accuracies = [0, 0.5, 1, -1, 2, double.NaN];

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double acc in accuracies)
        {
            // Combo and misses deliberately out of range in both directions.
            foreach (int misses in new[] { -5, 0, notes, notes + 7 })
            foreach (int combo in new[] { -3, 0, notes, notes + 9 })
            {
                double pp = PerformancePoints.Compute(sr, notes, misses, acc, combo, no_mods);

                Assert.That(pp, Is.Not.NaN, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                Assert.That(double.IsFinite(pp), Is.True, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
            }
        }
    }

    [Test]
    public void Compute_ZeroNotesEarnsNothing()
        => Assert.That(PerformancePoints.Compute(5, notes: 0, misses: 0, accuracy: 1, maxCombo: 0, no_mods), Is.Zero);

    [Test]
    public void Compute_OneNoteIsTinyButPositive()
    {
        // A single perfect note on a 5-star map: the length floor is what keeps this finite and
        // positive rather than zero or negative.
        double pp = PerformancePoints.Compute(5, notes: 1, misses: 0, accuracy: 1, maxCombo: 1, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.EqualTo(30.851693).Within(1e-5));
            Assert.That(pp, Is.LessThan(reference_pp));
        });
    }

    [Test]
    public void Compute_ClampsAComboAboveTheNoteCountRatherThanRewardingIt()
    {
        double honest = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods);
        double tampered = PerformancePoints.Compute(4, 500, 0, 0.9, 5000, no_mods);

        Assert.That(tampered, Is.EqualTo(honest).Within(1e-9));
    }

    // ---------------------------------------------------------------------------------------------
    // notes = great + ok + meh + miss, EXCLUDING ignore_hit.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void CountNotes_ExcludesIgnoreHit()
    {
        var statistics = new Dictionary<string, int>
        {
            ["great"] = 300,
            ["ok"] = 40,
            ["meh"] = 10,
            ["miss"] = 50,
            // The line containers. Counting these would inflate notes by 12 and dilute every factor.
            ["ignore_hit"] = 12,
            ["ignore_miss"] = 3,
            // Not a typing-map judgement; still must not be counted as a note.
            ["large_bonus"] = 7,
        };

        var counts = PerformancePoints.CountNotes(statistics);

        Assert.Multiple(() =>
        {
            Assert.That(counts.Notes, Is.EqualTo(400));
            Assert.That(counts.Misses, Is.EqualTo(50));
        });
    }

    [Test]
    public void CountNotes_ReadsTheStoredJsonIdenticallyToTheDictionary()
    {
        const string json = """{"great":300,"ok":40,"meh":10,"miss":50,"ignore_hit":12}""";

        var fromJson = PerformancePoints.CountNotes(json);
        var fromDictionary = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["great"] = 300, ["ok"] = 40, ["meh"] = 10, ["miss"] = 50, ["ignore_hit"] = 12,
        });

        Assert.That(fromJson, Is.EqualTo(fromDictionary));
        Assert.That(fromJson.Notes, Is.EqualTo(400));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json at all")]
    [TestCase("[1,2,3]")]
    public void CountNotes_MalformedJsonYieldsNothingRatherThanThrowing(string? json)
        => Assert.That(PerformancePoints.CountNotes(json).Notes, Is.Zero);

    [Test]
    public void CountNotes_NegativeCountsContributeNothing()
    {
        var counts = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 100, ["miss"] = -50 });

        Assert.Multiple(() =>
        {
            Assert.That(counts.Notes, Is.EqualTo(100));
            Assert.That(counts.Misses, Is.Zero);
        });
    }

    [Test]
    public void Compute_IgnoreHitInflationWouldChangeTheAnswer()
    {
        // The reason CountNotes has to exclude it. Line containers are one ignore_hit per LINE, so
        // a 400-note map with 60 lines would read as 460 "notes". Three of the six factors take the
        // note count as a denominator, and the most visible casualty is the combo term: a genuine
        // full combo would stop reading as one.
        double fullCombo = PerformancePoints.Compute(4, 400, 0, 0.85, 400, no_mods);
        double inflated = PerformancePoints.Compute(4, 460, 0, 0.85, 400, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(inflated, Is.LessThan(fullCombo));
            Assert.That((fullCombo - inflated) / fullCombo, Is.GreaterThan(0.03),
                "counting the line containers would cost a full combo several percent of its pp");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Mod multipliers.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void ModMultiplier_NoFailAndFletcherEachCostTenPercent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NF", null)], 300), Is.EqualTo(0.90).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("FT", null)], 300), Is.EqualTo(0.90).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("LT", null)], 300), Is.EqualTo(1.06).Within(1e-12));
        });
    }

    [Test]
    public void ModMultiplier_SuddenDeathMutedAndUnknownModsAreNeutral()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("SD", null)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("MU", null)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("ZZ", null)], 300), Is.EqualTo(1.0).Within(1e-12));
        });
    }

    [Test]
    public void ModMultiplier_RateModsContributeNothing()
    {
        // The rate is priced through SR_eff alone; a flat DT/HT term here would double-count it.
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("DT", 1.5)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NC", 1.5)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("HT", 0.75)], 300), Is.EqualTo(1.0).Within(1e-12));
        });
    }

    [Test]
    public void ModMultiplier_StacksAndCollapsesDuplicates()
    {
        double stacked = PerformancePoints.ModMultiplier(
            [new ScoreMod("LT", null), new ScoreMod("NF", null), new ScoreMod("FL", null)], 500);

        Assert.Multiple(() =>
        {
            Assert.That(stacked, Is.EqualTo(1.06 * 0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12));

            // A duplicated acronym is tamper-shaped; it must be applied once, not squared.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NF", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(0.90).Within(1e-12));
        });
    }

    [Test]
    public void Compute_AppliesTheModMultiplierToTheWholeFormula()
    {
        double bare = PerformancePoints.Compute(3, 300, 5, 0.8, 250, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(bare, Is.EqualTo(61.824597).Within(1e-5));
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("NF", null)]),
                Is.EqualTo(bare * 0.90).Within(1e-9));
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("FT", null)]),
                Is.EqualTo(bare * 0.90).Within(1e-9));
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("LT", null)]),
                Is.EqualTo(bare * 1.06).Within(1e-9));
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("FL", null)]),
                Is.EqualTo(bare * PerformancePoints.FlashlightMultiplier(300)).Within(1e-9));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Rate eligibility: only the BASE rates earn pp, and they are priced off the stored rate SRs.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void StarsFor_NoRateModUsesTheBaseRating()
    {
        var stars = PerformancePoints.StarsFor([new ScoreMod("LT", null)], baseStars: 4.2, 6.0, 3.0);

        Assert.That(stars.Stars, Is.EqualTo(4.2));
    }

    [TestCase("DT", 1.50, 6.0)]
    [TestCase("NC", 1.50, 6.0)]
    [TestCase("HT", 0.75, 3.0)]
    public void StarsFor_BaseRatePlaysUseTheStoredRateRating(string acronym, double rate, double expected)
    {
        var stars = PerformancePoints.StarsFor([new ScoreMod(acronym, rate)], baseStars: 4.2, 6.0, 3.0);

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.EqualTo(expected));
            Assert.That(stars.Pending, Is.False);
        });
    }

    [Test]
    public void StarsFor_AHistoricRateModWithNoStoredRateReadsAsItsBaseRate()
    {
        // Pre-task-27 rows carry no speed_change at all; under the old rules a ranked bare DT could
        // only have been 1.50x, so they must stay pp-eligible.
        var stars = PerformancePoints.StarsFor([new ScoreMod("DT", null)], baseStars: 4.2, 6.0, 3.0);

        Assert.That(stars.Stars, Is.EqualTo(6.0));
    }

    [TestCase("DT", 1.01)]
    [TestCase("DT", 1.49)]
    [TestCase("DT", 1.51)]
    [TestCase("DT", 2.00)]
    [TestCase("HT", 0.50)]
    [TestCase("HT", 0.74)]
    [TestCase("HT", 0.99)]
    public void StarsFor_CustomRatePlaysArePermanentlyIneligible(string acronym, double rate)
    {
        var stars = PerformancePoints.StarsFor([new ScoreMod(acronym, rate)], baseStars: 4.2, 6.0, 3.0);

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.Null);
            // Not "pending": nothing will ever make this play pp-eligible, so the row settles.
            Assert.That(stars.Pending, Is.False);
        });
    }

    [Test]
    public void ForScore_CustomRatePlayIsNotPricedAtAllButStillSettles()
    {
        var (pp, settled) = PerformancePoints.ForScore(
            ranked: true,
            [new ScoreMod("DT", 1.75)],
            new PerformancePoints.NoteCounts(500, 0),
            accuracy: 0.9,
            maxCombo: 500,
            baseStars: 4,
            starsDoubleTime: 6,
            starsHalfTime: 3);

        Assert.Multiple(() =>
        {
            // NULL, not 0. The formula never ran, so there is no price to report; the caller stores
            // 0 because the column is NOT NULL, and the wire sends null so the game can say "no pp
            // was ever on offer" instead of "you earned zero".
            Assert.That(pp, Is.Null);
            Assert.That(settled, Is.True);
        });
    }

    [Test]
    public void ForScore_BaseRateDoubleTimePricesOffSrDtNotTheBaseRating()
    {
        var counts = new PerformancePoints.NoteCounts(500, 0);

        var (dtPp, _) = PerformancePoints.ForScore(true, [new ScoreMod("DT", 1.5)], counts, 0.9, 500, 4, 6, 3);
        var (htPp, _) = PerformancePoints.ForScore(true, [new ScoreMod("HT", 0.75)], counts, 0.9, 500, 4, 6, 3);
        var (noModPp, _) = PerformancePoints.ForScore(true, [], counts, 0.9, 500, 4, 6, 3);

        Assert.Multiple(() =>
        {
            Assert.That(dtPp, Is.EqualTo(PerformancePoints.Compute(6, 500, 0, 0.9, 500, [])).Within(1e-9));
            Assert.That(htPp, Is.EqualTo(PerformancePoints.Compute(3, 500, 0, 0.9, 500, [])).Within(1e-9));
            Assert.That(noModPp, Is.EqualTo(reference_pp).Within(1e-5));

            // The rate lands entirely in the star rating: harder up-rate, easier down-rate.
            Assert.That(dtPp, Is.GreaterThan(noModPp));
            Assert.That(htPp, Is.LessThan(noModPp));
        });
    }

    [Test]
    public void ForScore_MissingRateRatingIsPendingRatherThanZeroForever()
    {
        var (pp, settled) = PerformancePoints.ForScore(
            ranked: true,
            [new ScoreMod("DT", 1.5)],
            new PerformancePoints.NoteCounts(500, 0),
            0.9, 500,
            baseStars: 4,
            starsDoubleTime: null, // the sweep has not reached this map yet
            starsHalfTime: null);

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Null, "no rating means no price, which is not the same as a price of zero");
            Assert.That(settled, Is.False, "an unpriced row must be left stale so the backfill retries it");
        });
    }

    [Test]
    public void ForScore_TwoRateModsAtOnceIsTreatedAsIneligible()
    {
        // Tamper-shaped by construction: the client makes DT/NC/HT mutually exclusive.
        var (pp, settled) = PerformancePoints.ForScore(
            true, [new ScoreMod("DT", 1.5), new ScoreMod("HT", 0.75)],
            new PerformancePoints.NoteCounts(500, 0), 0.9, 500, 4, 6, 3);

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Null);
            Assert.That(settled, Is.True);
        });
    }

    [Test]
    public void ForScore_UnrankedScoresEarnNothing()
    {
        var (pp, settled) = PerformancePoints.ForScore(
            ranked: false, [], new PerformancePoints.NoteCounts(500, 0), 1.0, 500, 8, 10, 6);

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Null, "refused outright: an unranked play is never priced, it is not priced at zero");
            Assert.That(settled, Is.True, "nothing about an unranked row will change; it must not be rescanned forever");
        });
    }

    [Test]
    public void BaseRates_AreTheRateModSliderDefaults()
    {
        // The pp-eligible rates are not a second copy of 1.50/0.75 living here; they are the very
        // defaults the rate mods are parsed and priced against.
        Assert.Multiple(() =>
        {
            Assert.That(RateMods.DoubleTimeBaseRate, Is.EqualTo(1.50));
            Assert.That(RateMods.HalfTimeBaseRate, Is.EqualTo(0.75));
            Assert.That(RateMods.DefaultSpeed("DT"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("NC"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("HT"), Is.EqualTo(RateMods.HalfTimeBaseRate));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Mistypes (backlog 72): the cleanliness term learns about wrong keypresses.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void CountNotes_ReadsMistypesFromTheComboBreakKeyWithoutCountingThemAsNotes()
    {
        var counts = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["great"] = 300,
            ["ok"] = 40,
            ["meh"] = 10,
            ["miss"] = 50,
            ["combo_break"] = 137,
        });

        Assert.Multiple(() =>
        {
            // notes stays the map's CELL count. Letting keypresses in would inflate the LENGTH
            // bonus and shrink the COMBO denominator, paying a masher twice for mashing.
            Assert.That(counts.Notes, Is.EqualTo(400));
            Assert.That(counts.Misses, Is.EqualTo(50));
            Assert.That(counts.Mistypes, Is.EqualTo(137));
        });
    }

    [Test]
    public void CountNotes_AMissingMistypeKeyIsNotZeroGuessedButSimplyAbsent()
    {
        // Every score submitted before the stat existed omits the key entirely, and must price
        // exactly as it always did.
        var old = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 100, ["miss"] = 10 });

        Assert.Multiple(() =>
        {
            Assert.That(old.Mistypes, Is.Zero);
            Assert.That(PerformancePoints.CountNotes("""{"great":100,"miss":10}""").Mistypes, Is.Zero);
            Assert.That(PerformancePoints.CountNotes("""{"great":100,"miss":10,"combo_break":9}""").Mistypes, Is.EqualTo(9));
        });
    }

    [Test]
    public void CountNotes_NegativeMistypeCountsContributeNothing()
        => Assert.That(PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 100, ["combo_break"] = -50 }).Mistypes, Is.Zero);

    [Test]
    public void Compute_ZeroMistypesIsBitwiseTheOldFormula()
    {
        // The VERSION decision rests on this: with no mistypes the new cleanliness term collapses
        // to (1 - miss/notes)^7.5 EXACTLY, so no stored row's pp can move and a startup reprice
        // would rewrite every row with the value it already holds.
        foreach (int misses in new[] { 0, 1, 17, 250, 500 })
        {
            double withArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods, mistypes: 0);
            double withoutArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods);

            Assert.That(withArgument, Is.EqualTo(withoutArgument), $"misses={misses}");
        }
    }

    [Test]
    public void Compute_MistypesCostPpAndMonotonicallySo()
    {
        double clean = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 0);
        double few = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 10);
        double many = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 100);

        Assert.Multiple(() =>
        {
            Assert.That(few, Is.LessThan(clean), "this is the point of the stat: sloppy play stops farming pp");
            Assert.That(many, Is.LessThan(few));
            Assert.That(many, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Compute_TheCleannessTermStaysInRangeForAnyMistypeCount()
    {
        // LANDMINE 6, closed by the decision to add mistypes to BOTH sides of the fraction: misses
        // <= notes, so (miss+mistypes)/(notes+mistypes) can approach 1 but never pass it. An
        // absurd mistype count must therefore decay pp towards zero, never produce a negative base,
        // a NaN, or (with the 7.5 exponent on a negative base) an imaginary result.
        foreach (int notes in new[] { 1, 10, 500 })
        foreach (int misses in new[] { 0, notes / 2, notes })
        foreach (int mistypes in new[] { -1, 0, 1, notes * 1000, int.MaxValue })
        {
            double pp = PerformancePoints.Compute(6, notes, misses, 0.9, notes, no_mods, mistypes);

            Assert.That(pp, Is.Not.NaN, $"notes={notes} miss={misses} mistypes={mistypes}");
            Assert.That(double.IsFinite(pp), Is.True, $"notes={notes} miss={misses} mistypes={mistypes}");
            Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"notes={notes} miss={misses} mistypes={mistypes}");
        }
    }

    [Test]
    public void ForScore_PricesTheMistypesCarriedOnTheCounts()
    {
        var clean = PerformancePoints.ForScore(true, no_mods, new PerformancePoints.NoteCounts(500, 0), 0.9, 500, 4, null, null);
        var messy = PerformancePoints.ForScore(true, no_mods, new PerformancePoints.NoteCounts(500, 0, 60), 0.9, 500, 4, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(clean.Settled, Is.True);
            Assert.That(messy.Settled, Is.True);
            Assert.That(messy.Pp, Is.LessThan(clean.Pp));
            Assert.That(messy.Pp, Is.EqualTo(PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, 60)).Within(1e-12));
        });
    }

    [Test]
    public void Version_IsNotBumpedForAStatNoStoredRowCanCarry()
    {
        // Deliberate: see the VERSION docs. Bumping forces PpBackfill to reprice every row at boot,
        // and the mistype term is provably a no-op on all of them (no client emitted combo_break
        // before it existed). Bump it the moment a change values any stored row differently.
        Assert.That(PerformancePoints.VERSION, Is.EqualTo(1));
    }

    [Test]
    public void Decay_IsTheDocumentedStartingValue()
    {
        // Intended to be raised towards osu's 0.95 as the ranked pool grows; if this value moves,
        // docs/pp.md moves with it.
        Assert.That(PerformancePoints.DECAY, Is.EqualTo(0.85));
    }
}
