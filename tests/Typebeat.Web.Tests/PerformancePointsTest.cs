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
    private const double reference_pp = 223.272161; // pp[f.compute(4, 500, 0, 0.9, 500)]

    [Test]
    public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
    {
        double pp = PerformancePoints.Compute(starRating: 4, notes: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

        Assert.That(pp, Is.EqualTo(reference_pp).Within(1e-5));
    }

    // ---------------------------------------------------------------------------------------------
    // Length bonus: the raw term crosses ZERO at exactly 1 note and the floor at ~1.585, so the
    // clamp is what stops a degenerate map computing zero or negative pp from its length alone.
    //
    // The weight moved 0.70 to 0.50 (backlog 103), which pushed both crossings a long way in: they
    // used to sit at ~3.73 and ~5.18 notes, so 3, 4 and 5 were clamped cases. They are not any more
    // (at 2 notes the raw term is already 0.1505, above the floor), so the clamp now bites at 1
    // note and below. Kept as a range rather than a single case so the NEXT weight change fails
    // here loudly instead of silently testing nothing.
    // ---------------------------------------------------------------------------------------------

    [TestCase(0)]
    [TestCase(1)]
    public void LengthBonus_ClampsToTheFloorWhereTheRawTermWouldSinkBelowIt(int notes)
    {
        double raw = 1 + 0.50 * Math.Log10(Math.Max(notes, 1) / 100.0); // pp:const length_weight=0.50 reference_notes=100.0

        Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(0.1).Within(1e-12), // pp[f.length_floor]
            $"raw term at {notes} notes is {raw:0.####}");
    }

    [Test]
    public void LengthBonus_RawTermCrossesZeroAtOneNote()
    {
        // Pins the reason the clamp exists at all: below the crossing the unclamped term is
        // NEGATIVE, which would make pp negative. At a weight of 0.50 the crossing sits at exactly
        // 1 note (0.50 * log10(1/100) = -1), so the window where the clamp does anything is now
        // very narrow. It is still real, and a fractional note count is not reachable in practice,
        // but the floor is close to vestigial at this weight and would stop mattering entirely if
        // the weight dropped further.
        static double raw(double notes) => 1 + 0.50 * Math.Log10(notes / 100.0); // pp:const length_weight=0.50 reference_notes=100.0

        Assert.Multiple(() =>
        {
            Assert.That(raw(0.99), Is.LessThan(0));
            Assert.That(raw(1), Is.EqualTo(0).Within(1e-12));
            Assert.That(raw(1.01), Is.GreaterThan(0));
            // And it stays under the 0.1 floor until about 1.585 notes.
            Assert.That(raw(1.58), Is.LessThan(0.1));
            Assert.That(raw(1.59), Is.GreaterThan(0.1));
        });
    }

    [TestCase(6, 0.389076)] // pp[f.length_bonus(6)]
    [TestCase(100, 1.0)] // pp[f.length_bonus(100)]
    [TestCase(500, 1.349485)] // pp[f.length_bonus(500)]
    [TestCase(1000, 1.5)] // pp[f.length_bonus(1000)]
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
        double raw = 1 + 0.02 + 0.06 * Math.Log10(notes / 100.0); // pp:const flashlight_offset=0.02 flashlight_weight=0.06 reference_notes=100.0

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.LessThan(1.0), "the raw term is below 1 here, which is what the clamp is for");
            Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(1.0).Within(1e-12)); // pp[f.flashlight_floor]
        });
    }

    [TestCase(47, 1.000326)] // pp[f.flashlight(47)]
    [TestCase(100, 1.02)] // pp[f.flashlight(100)]
    [TestCase(500, 1.061938)] // pp[f.flashlight(500)]
    public void FlashlightMultiplier_GrowsWithLengthOnceAboveTheFloor(int notes, double expected)
        => Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(expected).Within(1e-6));

    [Test]
    public void FlashlightMultiplier_CrossesOneAtAboutFortySixNotes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.FlashlightMultiplier(46), Is.EqualTo(1.0).Within(1e-12)); // pp[f.flashlight_floor]
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
        // part of the map, which is what the 10 exponent is for.
        //
        // THE MISS COUNT HAS MOVED THREE TIMES NOW, AND NOT ALWAYS IN THE SAME DIRECTION. Backlog 96
        // squared the RATIO, which softened the term so far that the case only held at 150 misses.
        // Backlog 97 squared the COUNT, which hardened it so far that 150 misses was a flat ZERO and
        // the comparison went degenerate (any positive number beats zero, so the test asserted
        // nothing about the miss term at all); it was restated at 10 misses to dodge the 23-miss
        // cliff. Backlog 101 drops the power to 1.2, which moves that cliff out to 178 and takes the
        // 10-miss term back up from 0.107 to 0.725, and at 0.725 the ACCURATE play wins: the
        // crossover sits between 11 and 12 misses, so 10 no longer tested the claim at all and would
        // have failed.
        //
        // Restated at 25 misses, i.e. 5% of the map. Both plays price properly (the miss term is
        // 0.368), the sloppy one lands at ~129 against ~69, and the case is decided by the miss term
        // rather than by a clamp.
        double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.60, maxCombo: 500, no_mods);
        double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 25, accuracy: 0.93, maxCombo: 350, no_mods);

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
            Assert.That(pp, Is.EqualTo(31.250000).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1)]
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
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NF", null)], 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("FT", null)], 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.fletcher_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("LT", null)], 300), Is.EqualTo(1.06).Within(1e-12)); // pp[f.literate_multiplier]
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
            Assert.That(stacked, Is.EqualTo(1.06 * 0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12)); // pp:const literate_multiplier=1.06 no_fail_multiplier=0.90

            // A duplicated acronym is tamper-shaped; it must be applied once, not squared.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NF", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
        });
    }

    [Test]
    public void Compute_AppliesTheModMultiplierToTheWholeFormula()
    {
        double bare = PerformancePoints.Compute(3, 300, 5, 0.8, 250, no_mods);

        Assert.Multiple(() =>
        {
            // Backlog 101 moves this from 29.377848 (which is where 97 put it, from 96's 69.935719
            // and 95's 59.280683), ONLY through the miss term: the play carries no mistypes, so its
            // mistyping term is exactly 1.0 whatever the power, and the whole change is
            // max(0, 1 - 5^1.2/300)^10 = 0.97700^10 replacing 0.91667^10. Five misses is far under
            // the 116-miss cliff on a 300-note map, so this prices comfortably.
            Assert.That(bare, Is.EqualTo(37.781351).Within(1e-5)); // pp[f.compute(3, 300, 5, 0.8, 250)]
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("NF", null)]),
                Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("FT", null)]),
                Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const fletcher_multiplier=0.90
            Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, [new ScoreMod("LT", null)]),
                Is.EqualTo(bare * 1.06).Within(1e-9)); // pp:const literate_multiplier=1.06
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

            // Half Time is sr_ht AND the mirror multiplier on top of it (backlog 90); Double Time
            // and no-mod are the rating alone.
            Assert.That(htPp, Is.EqualTo(
                PerformancePoints.Compute(3, 500, 0, 0.9, 500, [], 0, PerformancePoints.HalfTimeMultiplier(4, 6, 3))).Within(1e-9));
            Assert.That(noModPp, Is.EqualTo(reference_pp).Within(1e-5));

            // The rate lands entirely in the star rating: harder up-rate, easier down-rate.
            Assert.That(dtPp, Is.GreaterThan(noModPp));
            Assert.That(htPp, Is.LessThan(noModPp));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The Half Time mirror penalty (backlog 90). A base-rate HT play is priced by sr_ht AND by
    // 1/(D·H), the reciprocal of what Double Time is emergently worth on the same map, so the two
    // rates are equal and opposite per map. The buff guard is the interesting half.
    // ---------------------------------------------------------------------------------------------

    /// <summary>What Double Time is emergently worth on a map, purely through SR^2.70.</summary>
    private static double doubleTimeFactor(double baseStars, double starsDoubleTime)
        => Math.Pow(starsDoubleTime / baseStars, 2.00); // pp:const sr_exponent=2.00

    /// <summary>What Half Time is emergently worth on a map, before the mirror penalty.</summary>
    private static double halfTimeFactor(double baseStars, double starsHalfTime)
        => Math.Pow(starsHalfTime / baseStars, 2.00); // pp:const sr_exponent=2.00

    [Test]
    public void HalfTimeMultiplier_IsTheReciprocalOfTheDoubleTimeFactorOnTheDecidedSpread()
    {
        // The parity fixture's own spread, and the numbers the change was decided on: DT is already
        // worth +111% here while HT only costs -34.5%, which is exactly the asymmetry being closed.
        const double basestars = 4.2, dt = 6.1, ht = 3.4;

        double d = doubleTimeFactor(basestars, dt);
        double h = halfTimeFactor(basestars, ht);
        double m = PerformancePoints.HalfTimeMultiplier(basestars, dt, ht);

        Assert.Multiple(() =>
        {
            Assert.That(d, Is.EqualTo(2.109410).Within(1e-6), "the premise: Double Time is +111% on this map"); // pp[f.rate_factor(4.2, 6.1)]
            Assert.That(h, Is.EqualTo(0.655329).Within(1e-6), "and Half Time is only -34.5% before this change"); // pp[f.rate_factor(4.2, 3.4)]

            Assert.That(m, Is.EqualTo(1.0 / (d * h)).Within(1e-12), "the mirror is used, not the clamp");
            Assert.That(m, Is.EqualTo(0.723402).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4)]

            // The whole point: HT's TOTAL rate factor is now exactly 1/D.
            Assert.That(m * h, Is.EqualTo(1.0 / d).Within(1e-12));
            Assert.That(m * h, Is.EqualTo(0.474066).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4) * f.rate_factor(4.2, 3.4)]
        });
    }

    [Test]
    public void HalfTimeMultiplier_ClampsToAFlatCutWhereTheMirrorWouldBuffHalfTime()
    {
        // A map whose SR curve is concave in log-rate: sr_dt · sr_ht < sr_base², so slowing down
        // helps far more than speeding up hurts, and the unguarded mirror would REWARD Half Time on
        // exactly this map. This is what the guard exists for.
        const double basestars = 4.2, dt = 4.5, ht = 2.0;

        double d = doubleTimeFactor(basestars, dt);
        double h = halfTimeFactor(basestars, ht);
        double mirror = 1.0 / (d * h);
        double m = PerformancePoints.HalfTimeMultiplier(basestars, dt, ht);

        Assert.Multiple(() =>
        {
            Assert.That(dt * ht, Is.LessThan(basestars * basestars), "the premise of the concave case");
            Assert.That(mirror, Is.GreaterThan(1), "the unguarded mirror is a buff here");
            Assert.That(mirror * h, Is.EqualTo(0.871111).Within(1e-6), "and it would raise HT's factor six-fold"); // pp[1 / f.rate_factor(4.2, 4.5)]

            Assert.That(m, Is.EqualTo(0.70).Within(1e-12), "so the flat cut is used instead"); // pp[f.half_time_buff_clamp]

            // And the outcome is a NERF against what this play is worth today, not a buff.
            Assert.That(m * h, Is.LessThan(h));
            Assert.That(m * h, Is.EqualTo(0.158730).Within(1e-6)); // pp[f.half_time_buff_clamp * f.rate_factor(4.2, 2.0)]
        });
    }

    [Test]
    public void HalfTimeMultiplier_UsesAMildMirrorAsIsRatherThanDeepeningItToTheClamp()
    {
        // THE ANTI-Math.Min CASE. This spread's mirror sits strictly between 0.70 and 1.0: it is a
        // mild, correct nerf and must be applied exactly. Math.Min(mirror, 0.70) would return 0.70
        // here and quietly throw away the per-map symmetry the term exists for.
        const double basestars = 4.0, dt = 4.5, ht = 3.7;

        double mirror = 1.0 / (doubleTimeFactor(basestars, dt) * halfTimeFactor(basestars, ht));
        double m = PerformancePoints.HalfTimeMultiplier(basestars, dt, ht);

        Assert.Multiple(() =>
        {
            Assert.That(mirror, Is.GreaterThan(0.70).And.LessThan(1.0), "the premise: a mild nerf, not a buff");
            Assert.That(mirror, Is.EqualTo(0.923446).Within(1e-6)); // pp[f.half_time_multiplier(4.0, 4.5, 3.7)]

            Assert.That(m, Is.EqualTo(mirror).Within(1e-12));
            Assert.That(m, Is.Not.EqualTo(0.70).Within(1e-6), "a Math.Min would have collapsed this to the flat cut"); // pp[f.half_time_buff_clamp]
        });
    }

    [TestCase(0.0, 6.0, 3.0)]
    [TestCase(-4.0, 6.0, 3.0)]
    [TestCase(4.0, 0.0, 3.0)]
    [TestCase(4.0, -6.0, 3.0)]
    [TestCase(4.0, 6.0, 0.0)]
    [TestCase(4.0, 6.0, -3.0)]
    [TestCase(double.NaN, 6.0, 3.0)]
    [TestCase(4.0, double.NaN, 3.0)]
    [TestCase(4.0, 6.0, double.NaN)]
    [TestCase(double.PositiveInfinity, 6.0, 3.0)]
    [TestCase(4.0, double.PositiveInfinity, 3.0)]
    [TestCase(4.0, 6.0, double.PositiveInfinity)]
    public void HalfTimeMultiplier_IsZeroOnDegenerateRatingsRatherThanNaN(double baseStars, double dt, double ht)
    {
        // A negative rating under a fractional exponent is not merely wrong but non-real, and a NaN
        // multiplier would survive Compute's own guard by poisoning the product. The file's rule is
        // that a degenerate play yields 0, never NaN, Infinity or a negative.
        double m = PerformancePoints.HalfTimeMultiplier(baseStars, dt, ht);

        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(m), Is.True);
            Assert.That(m, Is.EqualTo(0));
        });
    }

    [Test]
    public void HalfTimeMultiplier_IsFiniteAndNonNegativeOverAWideSpread()
    {
        double[] ratings = [0, -1, 1e-9, 0.5, 1, 4.2, 10, 1e9, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

        foreach (double baseStars in ratings)
        foreach (double dt in ratings)
        foreach (double ht in ratings)
        {
            double m = PerformancePoints.HalfTimeMultiplier(baseStars, dt, ht);

            string context = $"base={baseStars} dt={dt} ht={ht}";

            Assert.That(double.IsFinite(m), Is.True, context);
            Assert.That(m, Is.GreaterThanOrEqualTo(0), context);
        }
    }

    [Test]
    public void StarsFor_OnlyHalfTimeCarriesARateMultiplier()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.StarsFor([], 4.2, 6.1, 3.4).Multiplier, Is.EqualTo(1.0), "no mods");
            Assert.That(PerformancePoints.StarsFor([new ScoreMod("LT", null)], 4.2, 6.1, 3.4).Multiplier, Is.EqualTo(1.0), "a non-rate mod");
            Assert.That(PerformancePoints.StarsFor([new ScoreMod("DT", 1.50)], 4.2, 6.1, 3.4).Multiplier, Is.EqualTo(1.0), "Double Time");
            Assert.That(PerformancePoints.StarsFor([new ScoreMod("NC", 1.50)], 4.2, 6.1, 3.4).Multiplier, Is.EqualTo(1.0), "Nightcore");

            Assert.That(PerformancePoints.StarsFor([new ScoreMod("HT", 0.75)], 4.2, 6.1, 3.4).Multiplier,
                Is.EqualTo(PerformancePoints.HalfTimeMultiplier(4.2, 6.1, 3.4)).Within(1e-12));
        });
    }

    [Test]
    public void StarsFor_HalfTimeWithNoSrDtIsPendingRatherThanPricedOffSrHtAlone()
    {
        // The new data dependency: an HT play needs sr_dt to mirror against. Pricing it off sr_ht
        // alone would stamp a value the very next SR sweep has to disagree with, so the row is left
        // stale for PpBackfill exactly as a map with no rate rating at all is.
        var stars = PerformancePoints.StarsFor([new ScoreMod("HT", 0.75)], baseStars: 4.2, starsDoubleTime: null, starsHalfTime: 3.4);

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.Null);
            Assert.That(stars.Pending, Is.True, "not settled: the sweep will fill sr_dt and this must be retried");
        });
    }

    [Test]
    public void StarsFor_DoubleTimeStillDoesNotCareAboutSrHt()
    {
        // The dependency runs one way only. An up-rate play is priced off sr_dt and nothing else,
        // so a map that has sr_dt but not sr_ht still prices its DT plays.
        var stars = PerformancePoints.StarsFor([new ScoreMod("DT", 1.50)], baseStars: 4.2, starsDoubleTime: 6.1, starsHalfTime: null);

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.EqualTo(6.1));
            Assert.That(stars.Pending, Is.False);
            Assert.That(stars.Multiplier, Is.EqualTo(1.0));
        });
    }

    [Test]
    public void ForScore_HalfTimePaysTheMirrorPenaltyAndIsExactlyDoubleTimesReciprocal()
    {
        // Twelve misses and FIFTEEN mistypes, not the thirty this used to carry: thirty was past the
        // backlog-97 mistype cliff at 500 notes (22.87), so every one of the three plays priced to
        // zero and the two ratios below became 0/0, i.e. NaN. This is a test about the RATE factors,
        // so the play has to stay priced for the ratios to exist at all. Backlog 101 moves that
        // cliff out to 248.37, so these counts are now comfortably clear of it rather than barely.
        var counts = new PerformancePoints.NoteCounts(500, 12, 15);

        var (nomod, _) = PerformancePoints.ForScore(true, [], counts, 0.9, 480, 4.2, 6.1, 3.4);
        var (dt, _) = PerformancePoints.ForScore(true, [new ScoreMod("DT", 1.50)], counts, 0.9, 480, 4.2, 6.1, 3.4);
        var (ht, _) = PerformancePoints.ForScore(true, [new ScoreMod("HT", 0.75)], counts, 0.9, 480, 4.2, 6.1, 3.4);

        // Every non-rate factor is shared, so the ratios ARE the rate factors.
        double upFactor = dt!.Value / nomod!.Value;
        double downFactor = ht!.Value / nomod.Value;

        Assert.Multiple(() =>
        {
            Assert.That(upFactor, Is.EqualTo(2.109410).Within(1e-6)); // pp[f.rate_factor(4.2, 6.1)]
            Assert.That(downFactor, Is.EqualTo(0.474066).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4) * f.rate_factor(4.2, 3.4)]

            // Equal and opposite by construction, which is the whole point of the mirror.
            Assert.That(downFactor, Is.EqualTo(1.0 / upFactor).Within(1e-9));

            // And it is strictly harsher than pricing off sr_ht alone used to be.
            Assert.That(ht.Value, Is.LessThan(PerformancePoints.Compute(3.4, 500, 12, 0.9, 480, [], 15)));
        });
    }

    [Test]
    public void ForScore_ADegenerateRatingOnAHalfTimePlayEarnsZeroRatherThanNaN()
    {
        foreach ((double baseStars, double? dt, double? ht) in new (double, double?, double?)[]
                 {
                     (0, 6.0, 3.0), (-4, 6.0, 3.0), (4, -6.0, 3.0), (4, 6.0, -3.0),
                     (double.NaN, 6.0, 3.0), (4, double.NaN, 3.0), (4, 6.0, double.NaN),
                 })
        {
            var (pp, settled) = PerformancePoints.ForScore(
                true, [new ScoreMod("HT", 0.75)], new PerformancePoints.NoteCounts(500, 0), 0.9, 500, baseStars, dt, ht);

            string context = $"base={baseStars} dt={dt} ht={ht}";

            Assert.That(settled, Is.True, context);
            Assert.That(pp, Is.Not.Null, context);
            Assert.That(double.IsFinite(pp!.Value), Is.True, context);
            Assert.That(pp.Value, Is.EqualTo(0), context);
        }
    }

    [Test]
    public void Compute_TakesTheRateMultiplierAsAPlainFactorAndDefaultsItToOne()
    {
        double bare = PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30);

        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30, 1), Is.EqualTo(bare));
            Assert.That(PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30, 0.7), Is.EqualTo(bare * 0.7).Within(1e-9));

            // Hostile values fall out through the same finite/positive guard as everything else.
            Assert.That(PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30, double.NaN), Is.EqualTo(0));
            Assert.That(PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30, -1), Is.EqualTo(0));
            Assert.That(PerformancePoints.Compute(4, 500, 12, 0.9, 480, no_mods, 30, double.PositiveInfinity), Is.EqualTo(0));
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
            Assert.That(RateMods.DoubleTimeBaseRate, Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
            Assert.That(RateMods.HalfTimeBaseRate, Is.EqualTo(0.75)); // pp[f.half_time_base_rate]
            Assert.That(RateMods.DefaultSpeed("DT"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("NC"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("HT"), Is.EqualTo(RateMods.HalfTimeBaseRate));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Mistypes (backlog 72, rebalanced by backlog 89, 95, 96, 97 and 101): wrong keypresses are
    // read off the combo_break key and priced by their OWN term, at exponent 6, independently of
    // the misses.
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

    /// <summary>
    /// The two penalty terms in isolation. Nothing else in the formula reads misses or mistypes, so
    /// dividing a play's pp by the pp of the same play with neither is EXACTLY
    /// <c>max(0, 1 - miss^1.2/notes)^10 * max(0, 1 - mistypes^1.2/(notes+mistypes))^6</c>, with
    /// every other factor cancelling. Every expected number below is that product.
    /// </summary>
    private static double penaltyFactor(int notes, int misses, int mistypes)
    {
        double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, mistypes: 0);

        return PerformancePoints.Compute(4, notes, misses, 0.9, notes, no_mods, mistypes) / spotless;
    }

    [Test]
    public void Compute_ReproducesTheDecidedRebalanceWorkedExamples()
    {
        // The two cases every rebalance since backlog 89 has been signed off on, stated as exact
        // values. Backlog 89 split the terms apart and SOFTENED both; 95 raised both exponents and
        // took that back; 96 squared the RATIO and softened them far past 89; 97 powered the raw
        // COUNT instead, at 2, which hardened them past every earlier generation and zeroed both
        // cases; 101 leaves the shape alone and drops that power to 1.2. Every value in the chain is
        // quoted so the direction is unmistakable.
        Assert.Multiple(() =>
        {
            // BOTH counts were past their cliffs at a power of 2 (60^2 = 3600 against 500 notes,
            // 80^2 = 6400 against a denominator of 580), so this play was worth EXACTLY nothing. At
            // 1.2 it is a live number again: 60^1.2 = 136.4 against 500 and 80^1.2 = 190.6 against
            // 580, giving 0.041726 and 0.089375. Against 0.000000 at a power of 2, 0.770823 under
            // the squared ratio, 0.114309 at the linear shape, 0.200678 after the backlog-89 split
            // and 0.125946 before it: a sloppy play is priced harshly again rather than zeroed.
            Assert.That(penaltyFactor(notes: 500, misses: 60, mistypes: 80), Is.EqualTo(0.000000).Within(1e-6)); // pp[f.penalty(500, 60, 80)]

            // The near-clean case, which is the headline figure: the bases are 1 - 15.849/500 =
            // 0.96830 and 1 - 36.411/520 = 0.92998, giving 0.724618 and 0.646893. A play with ten
            // misses and twenty mistypes keeps 0.469 of a spotless one, against 0.000016 at a power
            // of 2, 0.987200 under the squared ratio and 0.645745 at the linear shape. THAT IS THE
            // POINT OF THE CHANGE: it lands almost exactly where backlog 95 had it.
            Assert.That(penaltyFactor(notes: 500, misses: 10, mistypes: 20), Is.EqualTo(0.151677).Within(1e-6)); // pp[f.penalty(500, 10, 20)]
        });
    }

    [Test]
    public void Compute_ZeroMistypesLeavesThePlayPricedByItsMissesAlone()
    {
        // The property that makes the split legible: at zero mistypes the mistyping term is EXACTLY
        // 1.0, so the whole penalty is max(0, 1 - miss^1.2/notes)^10 and nothing else. The sweep
        // deliberately straddles the cliff, so the restatement is checked both where it is a live
        // number and where the clamp has taken over. It USED to straddle 23, which backlog 101 moves
        // out to 178, so 17 and 250 no longer sit either side of anything.
        foreach (int misses in new[] { 0, 1, 100, 177, 178, 500 })
        {
            double withArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods, mistypes: 0);
            double withoutArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods);

            Assert.That(withArgument, Is.EqualTo(withoutArgument), $"misses={misses}");
            Assert.That(penaltyFactor(500, misses, 0), Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(misses, 1.6) / 500.0), 10)).Within(1e-12), // pp:const count_power=1.6 miss_exponent=10
                $"misses={misses}");
        }
    }

    [Test]
    public void Compute_APlayWithNeitherAMissNorAMistypeIsUntouchedByEitherExponent()
    {
        // The cheapest proof that a rebalance of the two exponents is CONFINED to their terms: both
        // bases are exactly 1.0 at a count of zero, and 1.0 raised to any finite power is exactly
        // 1.0. A spotless play must therefore be BIT-identical across any such change, not merely
        // close, so it is asserted against the remaining factors spelled out rather than against a
        // recorded number. If this ever moves, something leaked out of the two penalty terms.
        foreach (int notes in new[] { 1, 100, 500, 2137 })
        {
            double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, mistypes: 0);
            double withoutEitherPenaltyTerm = 12.5 * Math.Pow(4, 2.00) * PerformancePoints.LengthBonus(notes) * Math.Pow(0.9, 1.80); // pp:const scale=12.5 sr_exponent=2.00 accuracy_exponent=1.80

            Assert.That(spotless, Is.EqualTo(withoutEitherPenaltyTerm), $"notes={notes}");
        }
    }

    [Test]
    public void Compute_PricesMissesAndMistypesIndependently()
    {
        // The whole point of the split. What a miss costs must not depend on the keypress count and
        // vice versa, so the penalty factorises: the RATIO between two miss counts is the same
        // whatever mistype count both carry. Under the old combined term it was not.
        //
        // Every count here is BELOW its cliff on purpose. Past the cliff both plays price to zero
        // and the ratio is 0/0, which says nothing about factorisation either way. Backlog 97 pulled
        // this sweep back to 20 mistypes to clear a cliff at 23; at 1.2 the cliff is 249, so 20 was
        // testing almost nothing and the sweep runs out to 248, the last count that prices at all.
        foreach (int mistypes in new[] { 0, 10, 30, 51 })
        {
            double clean = penaltyFactor(500, 0, mistypes);
            double missy = penaltyFactor(500, 10, mistypes);

            Assert.That(missy / clean, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.6) / 500.0), 10)).Within(1e-12), // pp:const count_power=1.6 miss_exponent=10
                $"the miss term must not be diluted by {mistypes} mistypes");
        }

        // And the mistyping term likewise, read across two miss counts.
        Assert.That(penaltyFactor(500, 10, 20) / penaltyFactor(500, 10, 0),
            Is.EqualTo(penaltyFactor(500, 0, 20)).Within(1e-12));
    }

    [Test]
    public void Compute_MistypesCostPpAndMonotonicallySo()
    {
        // Both counts sit under the mistype cliff, because "many" has to stay STRICTLY above zero
        // for the last assertion to mean anything: past the cliff "still positive" would be a claim
        // about the clamp rather than about monotonicity. Backlog 97 pulled these down to 5 and 15
        // to clear a cliff at 23; backlog 101 moves that cliff to 249, so they are back at 50 and
        // 200 where the difference between them is worth asserting.
        double clean = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 0);
        double few = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 15);
        double many = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 45);

        Assert.Multiple(() =>
        {
            Assert.That(few, Is.LessThan(clean), "this is the point of the stat: sloppy play stops farming pp");
            Assert.That(many, Is.LessThan(few));
            Assert.That(many, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Compute_EachPenaltyIsMonotonicWhileTheOtherIsHeldFixed()
    {
        // Raising either count, with the other pinned, must move pp strictly DOWN. Both directions,
        // because the terms are separate and either could be wired up backwards on its own.
        //
        // STRICTLY is only true UNDER THE CLIFF, and that is a property of the clamp rather than a
        // weakness of the test: past notes^(1/1.2) misses (or the mistype root) every count prices
        // to exactly the same zero, so a sweep running to 499 misses would be asserting 0 < 0. Both
        // sweeps and both held-fixed values therefore stay below their cliffs; the behaviour AT and
        // past the cliff has tests of its own below.
        //
        // The upper ends were 22 under backlog 97, which is where a cliff at 23 left them. Backlog
        // 101 moves the cliffs to 178 and 249, so the sweeps run to 177 and 248: the last counts
        // that price, and the ones where a term wired up backwards would show.
        foreach (int mistypes in new[] { 0, 30 })
        {
            double previous = double.MaxValue;

            foreach (int misses in new[] { 0, 1, 10, 25, 40, 48 })
            {
                double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                Assert.That(pp, Is.LessThan(previous), $"misses={misses} at mistypes={mistypes}");
                previous = pp;
            }
        }

        foreach (int misses in new[] { 0, 25 })
        {
            double previous = double.MaxValue;

            foreach (int mistypes in new[] { 0, 1, 10, 25, 40, 51 })
            {
                double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                Assert.That(pp, Is.LessThan(previous), $"mistypes={mistypes} at misses={misses}");
                previous = pp;
            }
        }
    }

    [Test]
    public void Compute_TheMistypingTermStaysInRangeForAnyMistypeCount()
    {
        // LANDMINE 6, closed by keeping mistypes on BOTH sides of the MISTYPING fraction and by
        // CLAMPING the base at 0: however absurd the keypress count the result is a real number in
        // [0, 1]. An absurd count must price to zero, never to a negative base, a NaN, or (with a
        // fractional exponent on a negative base) an imaginary result. int.MaxValue is in the sweep
        // for TWO reasons: notes + mistypes would overflow an int there, and so would an int square,
        // whose true value is about 4.6e18. Math.Pow converts to double and the sum is taken in
        // double, so the ratio comes out at about 74 and the clamp turns it into a well-defined
        // zero. The NEGATIVE entry matters more than it used to: the count is clamped before it
        // reaches Math.Pow, and Math.Pow(-1, 1.2) is NaN rather than merely a wrong sign.
        foreach (int notes in new[] { 1, 10, 500 })
        foreach (int misses in new[] { 0, notes / 2, notes })
        foreach (int mistypes in new[] { -1, 0, 1, notes * 10, notes * 1000, int.MaxValue })
        {
            double pp = PerformancePoints.Compute(6, notes, misses, 0.9, notes, no_mods, mistypes);

            Assert.That(pp, Is.Not.NaN, $"notes={notes} miss={misses} mistypes={mistypes}");
            Assert.That(double.IsFinite(pp), Is.True, $"notes={notes} miss={misses} mistypes={mistypes}");
            Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"notes={notes} miss={misses} mistypes={mistypes}");
            Assert.That(pp, Is.LessThan(reference_pp * 10), $"notes={notes} miss={misses} mistypes={mistypes}");
        }

        // Ten times the note count, spelled out. Even at the softened power of 1.2 this is far past
        // the cliff (5000^1.2 is 27464 against a denominator of 5500), so the base clamps and the
        // play prices to EXACTLY zero rather than to something merely small. That is the clamp doing
        // its job: unclamped the base would be about -3.99, and a fractional exponent on it would
        // not be a real number at all.
        double absurd = penaltyFactor(500, 0, 5000);

        Assert.Multiple(() =>
        {
            Assert.That(absurd, Is.Zero);
            Assert.That(absurd, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(5000.0, 1.6) / 5500.0), 4)).Within(1e-12)); // pp:const count_power=1.6 mistype_exponent=4
        });
    }

    [Test]
    public void Compute_TheMissPenaltyFallsOffACliffAtTheCountPowerRootOfTheNoteCount()
    {
        // THE DEFINING BEHAVIOUR OF THE POWERED COUNT, and the reason count_power is the lever a
        // rebalance pulls rather than the exponents. The base is 1 - miss^1.2/notes, which reaches
        // zero at miss = notes^(1/1.2) and would go NEGATIVE past it; Math.Max clamps it, so the
        // term is a cliff rather than a curve. On a 500-note map that is 177.48, so 177 misses still
        // price and 178 do not. Under backlog 97's power of 2 it was 22.36, i.e. 23 misses or 4.6%
        // of the map, against 35% of it now.
        //
        // THE THRESHOLDS ARE LIFTED INTO CONSTANTS so the pp tool can rewrite them. A cliff sitting
        // in a call argument is invisible to it, which is why the last two retunes moved these three
        // numbers by hand and why one of them was left describing the wrong power.
        const int cliff500 = 49; // pp[math.ceil(f.miss_cliff(500))]
        const int cliff2000 = 116; // pp[math.ceil(f.miss_cliff(2000))]
        const int cliff100 = 18; // pp[math.ceil(f.miss_cliff(100))]

        Assert.Multiple(() =>
        {
            Assert.That(penaltyFactor(500, cliff500 - 1, 0), Is.GreaterThan(0), "one below the cliff still prices");
            Assert.That(penaltyFactor(500, cliff500, 0), Is.Zero, "at the cliff the clamp takes over exactly");
            Assert.That(penaltyFactor(500, 500, 0), Is.Zero, "and it stays there rather than turning around");

            // THE CLIFF MOVES WITH THE MAP, which is what makes it a shape and not a constant:
            // notes^(1/1.2) is 563.45 on a 2000-note map and 46.42 on a 100-note one. It moves far
            // less STEEPLY than it did, though, and that is the second half of the argument for 1.2:
            // as a FRACTION of the map the cliff is notes^(1/1.2 - 1), which runs 46% to 28% across
            // this span where 1/sqrt(notes) ran 10% to 2.2%.
            Assert.That(penaltyFactor(2000, cliff2000 - 1, 0), Is.GreaterThan(0));
            Assert.That(penaltyFactor(2000, cliff2000, 0), Is.Zero);
            Assert.That(penaltyFactor(100, cliff100 - 1, 0), Is.GreaterThan(0));
            Assert.That(penaltyFactor(100, cliff100, 0), Is.Zero);
        });
    }

    [Test]
    public void Compute_TheMistypePenaltyFallsOffACliffAtThePositiveRootOfItsOwnEquation()
    {
        // The mistype base is 1 - mistypes^1.2/(notes + mistypes), so the count is in the
        // denominator too and the zero moves out to the positive root of m^1.2 - m - notes = 0. At
        // the old power of 2 that had the closed form (1 + sqrt(1 + 4·notes))/2; at 1.2 it has none
        // and is solved numerically. It is 248.37 at 500 notes, 730.32 at 2000 and 73.45 at 100:
        // LATER than the miss cliff on every map, which is the mistype term staying the cheaper of
        // the two failures.
        const int cliff500 = 52; // pp[math.ceil(f.mistype_cliff(500))]
        const int cliff2000 = 120; // pp[math.ceil(f.mistype_cliff(2000))]
        const int cliff100 = 20; // pp[math.ceil(f.mistype_cliff(100))]
        const int missCliff500 = 49; // pp[math.ceil(f.miss_cliff(500))]

        Assert.Multiple(() =>
        {
            Assert.That(penaltyFactor(500, 0, cliff500 - 1), Is.GreaterThan(0), "one below the cliff still prices");
            Assert.That(penaltyFactor(500, 0, cliff500), Is.Zero, "at the cliff the clamp takes over exactly");
            Assert.That(penaltyFactor(500, 0, 5000), Is.Zero, "and it stays there however absurd the count");

            Assert.That(penaltyFactor(2000, 0, cliff2000 - 1), Is.GreaterThan(0));
            Assert.That(penaltyFactor(2000, 0, cliff2000), Is.Zero);
            Assert.That(penaltyFactor(100, 0, cliff100 - 1), Is.GreaterThan(0));
            Assert.That(penaltyFactor(100, 0, cliff100), Is.Zero);

            // The ordering, asserted rather than left to the six numbers above agreeing by luck:
            // whatever the power, the mistype cliff is the LATER of the two, so a mistype count that
            // would already have zeroed the same number of MISSES still prices.
            Assert.That(penaltyFactor(500, 0, missCliff500), Is.GreaterThan(0));
            Assert.That(penaltyFactor(500, missCliff500, 0), Is.Zero);
        });
    }

    [Test]
    public void Compute_APlayPastEitherCliffEarnsExactlyZeroPp()
    {
        // Not merely a small factor: the whole play is worth nothing, whatever its difficulty,
        // accuracy or combo. That is a deliberate consequence of the shape and not a rounding
        // artefact, so it is asserted on Compute itself rather than on the penalty factor.
        const int missCliff = 49; // pp[math.ceil(f.miss_cliff(500))]
        const int mistypeCliff = 52; // pp[math.ceil(f.mistype_cliff(500))]

        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.Compute(6, 500, missCliff, 0.95, 500 - missCliff, no_mods), Is.Zero,
                "the miss cliff");
            Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, mistypeCliff), Is.Zero,
                "the mistype cliff");

            // One below each, the same play is positive, so the zeros above are the clamp and not
            // some unrelated guard swallowing the play.
            Assert.That(PerformancePoints.Compute(6, 500, missCliff - 1, 0.95, 501 - missCliff, no_mods), Is.GreaterThan(0));
            Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, mistypeCliff - 1), Is.GreaterThan(0));
        });
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
    public void Version_IsBumpedBecauseTheRebalanceRepricesStoredRows()
    {
        // v7 = backlog 101, count_power dropping from 2 to 1.2, which reprices every stored row
        // carrying even one miss or one mistype (upwards, and away from zero for most of them).
        // v6 = backlog 97, the squaring of both penalty COUNTS, which reprices every stored row
        // carrying even one miss or one mistype (downwards, and to zero for most of them).
        // v5 = backlog 96, the squaring of both penalty RATIOS, which reprices every stored row
        // carrying even one miss or one mistype (upwards, that time).
        // v4 = backlog 95, the penalty rebalance (miss 8.5 to 10, mistype 3.5 to 6), which reprices
        // every stored row carrying even one miss or one mistype.
        // v3 = backlog 90, the Half Time mirror penalty, which reprices every stored HT row.
        // v2 = backlog 89. The mistype term (backlog 72) deliberately did NOT bump, on the proof
        // that no stored row could carry a combo_break count and so no stored value could move.
        // That proof does not survive a steeper MISS exponent, which reprices every stored row with
        // even one miss, so PpBackfill has to sweep. If this moves, so do the game's
        // PerformancePoints.VERSION and docs/pp.md.
        Assert.That(PerformancePoints.VERSION, Is.EqualTo(11)); // pp:version
    }

    [Test]
    public void Decay_IsTheDocumentedStartingValue()
    {
        // Intended to be raised towards osu's 0.95 as the ranked pool grows; if this value moves,
        // docs/pp.md moves with it.
        Assert.That(PerformancePoints.DECAY, Is.EqualTo(0.85)); // pp[f.decay]
    }
}
