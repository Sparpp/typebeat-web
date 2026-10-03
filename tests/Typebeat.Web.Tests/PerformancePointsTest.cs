using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The per-play pp formula (docs/pp.md, <see cref="PerformancePoints"/>). Pure arithmetic, no
/// database. Every expected value below is written as the formula spells it out rather than as a
/// hard-coded number, EXCEPT the handful of independently-computed reference values, which are
/// there to catch a plausible-looking but wrong refactor of the formula itself.
///
/// <para>REWRITTEN FOR v22-v24, the fork tuned in the PP Sandbox. Four of the shapes this file used
/// to pin are gone and are pinned in their new form instead: the TYPO TERM (deleted outright, and
/// its absence is now the assertion), the MISS PENALTY (over the map's DIFFICULT CHARACTERS and on
/// the missed FRACTION, so its cliff is a miss RATE rather than a count that scales with map size),
/// the ACCURACY SHAPE (a normalised exponential above a floor, replacing the power curve and its
/// soft knee) and the COMBO BONUS (a percentage OF the price, capped by map length, where v21 added
/// a number of pp beside it). The rating lookup moved too: a play is priced from one cell of the
/// map's stored <see cref="BeatmapRatings"/> matrix, which carries the difficult characters as well
/// as the stars and is keyed on the play's JUDGEMENT ARM as well as its stream and rate.</para>
/// </summary>
[TestFixture]
public class PerformancePointsTest
{
    private static readonly IReadOnlyList<ScoreMod> no_mods = [];

    /// <summary>
    /// A clean-ish reference play: 4 stars, 500 notes, 500 difficult characters, no misses, 90% acc,
    /// full combo. Independently evaluated, so a refactor that looks right and prices wrong fails
    /// here rather than passing every structural assertion below.
    /// </summary>
    private const double reference_pp = 145.510390; // pp[f.compute(4, 500, 0, 0.9, 500, difficult=500)]

    [Test]
    public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
    {
        double pp = PerformancePoints.Compute(
            starRating: 4, notes: 500, difficultCharacters: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

        Assert.That(pp, Is.EqualTo(reference_pp).Within(1e-5));
    }

    // ---------------------------------------------------------------------------------------------
    // THERE IS NO LENGTH TEST HERE, because there is no length factor: backlog 152 deleted it and
    // moved length pricing into the star rating. pp sees a long map only through the SR_eff it is
    // handed, plus the COMBO CEILING, which scales with the note count and is pinned in its own
    // tests below rather than as a length term.
    // ---------------------------------------------------------------------------------------------

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
    // Cleanliness, over the map's DIFFICULT CHARACTERS (v22). A give-up run must collapse to
    // nothing, and it now collapses ALL the way: the combo bonus is a percentage of the price rather
    // than an amount beside it, so a zeroed price stays zero however long the run was.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_GiveUpRunCollapsesToExactlyZero()
    {
        // 1000 notes on a 4-star map with 1000 difficult characters, 900 of them missed: exactly the
        // shape the miss term exists to kill. The missed fraction is 0.9, so the base is
        // 1 - 0.9^1.2 = 0.1197 and the term is 0.1197^13.5134, about 2.5e-13, which is then
        // multiplied by a timing term of exactly 0 (10% accuracy is under the floor).
        //
        // IT IS AN EXACT ZERO SINCE v22, WHERE v21 LEFT 0.375 pp BEHIND. The combo bonus used to be
        // ADDED to the clamped product, so a run of 10 still collected its share; it is a PERCENTAGE
        // of the price now, and a price of zero has no percentage.
        double giveUp = PerformancePoints.Compute(4, notes: 1000, difficultCharacters: 1000, misses: 900, accuracy: 0.1, maxCombo: 10, mods: no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(giveUp, Is.Zero, "a price zeroed by the play cannot keep a consolation bonus");
            Assert.That(giveUp, Is.LessThan(reference_pp / 100));
        });
    }

    [Test]
    public void Compute_MissingEveryDifficultCharacterIsExactlyZero()
    {
        // THE BASE REACHES ZERO ONLY WHEN EVERY DIFFICULT CHARACTER WAS MISSED, which is the whole
        // of the new cliff: there is no count at which it clamps early, because the fraction is
        // bounded by 1 by construction (the clamp is for a map with FEWER difficult characters than
        // cells, where the fraction can exceed it).
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.Compute(6, 400, 400, 400, 0, 0, no_mods), Is.Zero, "every difficult character missed");
            Assert.That(PerformancePoints.Compute(6, 400, 200, 400, 1.0, 0, no_mods), Is.Zero, "and past it, where the clamp holds");
            Assert.That(PerformancePoints.Compute(6, 400, 400, 399, 1.0, 0, no_mods), Is.GreaterThan(0), "one short of it still prices");
        });
    }

    [Test]
    public void Compute_AMapWithNoDifficultCharactersCannotAbsorbAMiss()
    {
        // THE 0/0 CASE, and the reason it is a branch rather than an arithmetic accident. A map whose
        // difficulty sits entirely below its own peak has no difficult characters to spend, so a
        // dropped cell has nothing to be a fraction OF. It zeroes the term, which is the harsh
        // reading and the deliberate one: a fallback to the cell count would price a play against a
        // number the map's rating does not use.
        //
        // A SPOTLESS play is unmoved, because the miss == 0 branch comes first and is exactly 1.0.
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.Compute(4, 500, 0, 1, 0.9, 499, no_mods), Is.Zero, "one miss against no difficult characters");
            Assert.That(PerformancePoints.Compute(4, 500, 0, 0, 0.9, 500, no_mods),
                Is.EqualTo(PerformancePoints.Compute(4, 500, 12345, 0, 0.9, 500, no_mods)),
                "a spotless play does not read the count at all");
        });
    }

    [Test]
    public void Compute_TheSameMissRateCostsTheSameShareOnEveryMap()
    {
        // WHY THE PENALTY IS CALIBRATED IN FRACTIONS (v22). The count used to carry the power over a
        // plain note count, so the cliff moved with map size and the same miss RATE cost a long map
        // far more than a short one. It is the missed FRACTION now, so the share of the core price a
        // play keeps is a pure function of the rate, identical at every map size.
        //
        // Measured at no combo, so the bonus (which DOES scale with length) is exactly 0 on both
        // sides and the comparison is the cleanliness term alone.
        foreach (double rate in new[] { 0.01, 0.05, 0.10, 0.25 })
        {
            double? share = null;

            foreach (int difficult in new[] { 100, 500, 2000, 10000 })
            {
                int misses = (int)Math.Round(rate * difficult);
                double spotless = PerformancePoints.Compute(4, 20000, difficult, 0, 0.9, 0, no_mods);
                double kept = PerformancePoints.Compute(4, 20000, difficult, misses, 0.9, 0, no_mods) / spotless;

                share ??= kept;
                Assert.That(kept, Is.EqualTo(share!.Value).Within(1e-9), $"miss rate {rate} at {difficult} difficult characters");
            }
        }
    }

    [TestCase(0.01, 0.947522)]
    [TestCase(0.02, 0.883235)]
    [TestCase(0.05, 0.686380)]
    [TestCase(0.10, 0.414482)]
    [TestCase(0.25, 0.058506)]
    [TestCase(0.50, 0.000443)]
    public void Compute_TheCleanlinessCurveIsTheCalibratedOne(double missRate, double expectedShare)
    {
        // The live curve, quoted at six rates so a retune of either dial is unmistakable rather than
        // merely red. miss_exponent is ln(2)/ln(1/0.95) = 13.5134, which is the exponent that puts
        // HALF the core price at a 5% miss rate AT count_power 1; the live count_power is 1.2, which
        // bends the fraction and lands 5% at 0.686 instead, buying a grace region at low miss rates
        // and a steeper fall near the top.
        const int difficult = 1000;
        double spotless = PerformancePoints.Compute(4, 2000, difficult, 0, 1.0, 0, no_mods);
        double kept = PerformancePoints.Compute(4, 2000, difficult, (int)Math.Round(missRate * difficult), 1.0, 0, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(kept / spotless, Is.EqualTo(expectedShare).Within(1e-6));

            // And the shape spelled out, so the numbers above are checkable rather than recorded.
            Assert.That(kept / spotless,
                Is.EqualTo(Math.Pow(Math.Max(0, 1 - Math.Pow(missRate, 1.2)), 13.5134)).Within(1e-9)); // pp:const count_power=1.2 miss_exponent=13.5134
        });
    }

    [Test]
    public void Compute_ADroppedCellCostsMoreOnAConcentratedMap()
    {
        // The point of judging misses against the DIFFICULT characters rather than the cells: two
        // maps of the same length and rating, one whose difficulty is spread over all 500 cells and
        // one whose peak passages hold only 100 of them. The same single miss costs the second map
        // five times the fraction, and therefore far more pp.
        double spread = PerformancePoints.Compute(4, 500, 500, 5, 0.9, 0, no_mods);
        double concentrated = PerformancePoints.Compute(4, 500, 100, 5, 0.9, 0, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(concentrated, Is.LessThan(spread));

            // Both are the same play but for the count, so the ratio is exactly the two cleanliness
            // terms and nothing else.
            Assert.That(concentrated / spread,
                Is.EqualTo(Math.Pow(Math.Max(0, 1 - Math.Pow(5 / 100.0, 1.2)), 13.5134)
                           / Math.Pow(Math.Max(0, 1 - Math.Pow(5 / 500.0, 1.2)), 13.5134)).Within(1e-9));
        });
    }

    [Test]
    public void Compute_MissesDominateAccuracyAndCombo()
    {
        // Same map, same length, same difficult-character count. A sloppy-but-complete play beats a
        // high-accuracy play that dropped 5% of the map's difficult characters, which is what the
        // 13.5134 exponent is for.
        double sloppyButClean = PerformancePoints.Compute(4, 500, 500, misses: 0, accuracy: 0.85, maxCombo: 500, mods: no_mods);
        double accurateButMissy = PerformancePoints.Compute(4, 500, 500, misses: 25, accuracy: 0.93, maxCombo: 350, mods: no_mods);

        Assert.That(sloppyButClean, Is.GreaterThan(accurateButMissy));
    }

    // ---------------------------------------------------------------------------------------------
    // Accuracy: the normalised exponential above a floor (DEPARTURE 5), with the soft knee left in
    // the file at width 0, where it is exactly 1.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_APerfectPlayIsExactlyTheScaledDifficultyAtNoCombo()
    {
        // BOTH ENDS OF THE ACCURACY CURVE ARE PINNED BY CONSTRUCTION, which is the whole reason it is
        // NORMALISED: ExpCurve(1) is exactly 1 at every steepness, so a perfect play keeps the whole
        // of its core price and a retune of acc_steepness cannot move it. With no run at all the
        // combo bonus is exactly 0, so the price IS scale * SR^sr_exponent, bit for bit.
        foreach (double stars in new[] { 1.0, 4.0, 7.5 })
        {
            double perfect = PerformancePoints.Compute(stars, 500, 500, 0, 1.0, maxCombo: 0, mods: no_mods);

            Assert.That(perfect, Is.EqualTo(9.0 * Math.Pow(stars, 2.30)), $"stars={stars}"); // pp:const scale=9.0 sr_exponent=2.30
        }
    }

    [Test]
    public void Compute_AccuracyAtOrBelowTheFloorPricesExactlyNothing()
    {
        // THE OTHER END, and it is a hard zero rather than a small number: ExpCurve(0) is exactly 0,
        // so the floor is where the price reaches nothing and every accuracy below it is the same
        // nothing. That is a real behaviour change from the power curve, which only ever approached
        // zero, and it is what acc_floor is for.
        Assert.Multiple(() =>
        {
            foreach (double accuracy in new[] { 0.0, 0.25, 0.49, 0.5 }) // pp:const acc_floor=0.5
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, accuracy, 500, no_mods), Is.Zero, $"accuracy={accuracy}");

            Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.500001, 500, no_mods), Is.GreaterThan(0),
                "and a hair above it prices, so the zero is the floor and not some other guard");
        });
    }

    [Test]
    public void Compute_IsStrictlyIncreasingInAccuracyAboveTheFloor()
    {
        // The curve RESPREADS the accuracy axis and never permutes it: the exponential is strictly
        // increasing in t and t is strictly increasing in the accuracy. Swept at 0.005 from just
        // above the floor, on a play that is neither spotless nor an FC, so every other factor is a
        // fixed positive number and only the timing term moves.
        double previous = -1;

        for (int step = 101; step <= 200; step++)
        {
            double accuracy = step / 200.0;
            double pp = PerformancePoints.Compute(4.2, 500, 500, 25, accuracy, maxCombo: 0, mods: no_mods);

            Assert.That(pp, Is.GreaterThan(previous), $"accuracy={accuracy}");
            previous = pp;
        }
    }

    [Test]
    public void Compute_TheSoftKneeIsOffAndTheCurveIsTheWholeAccuracyShape()
    {
        // acc_knee_width is 0 at the sandbox's active dials, which is this file's DECLARED-ABSENCE
        // sentinel: AccuracyKnee returns exactly 1.0 rather than the step function the logistic
        // degenerates to. So the timing term is the curve alone, and the identity that proves it is
        // the one the knee would break: a play at accuracy exactly acc_knee is NOT worth half of
        // what it would be without a knee.
        double onTheOldKnee = PerformancePoints.Compute(4, 500, 500, 0, 0.80, maxCombo: 0, mods: no_mods);
        double curveAlone = 9.0 * Math.Pow(4, 2.30) * expCurve((0.80 - 0.5) / 0.5); // pp:const scale=9.0 sr_exponent=2.30 acc_floor=0.5*2

        Assert.Multiple(() =>
        {
            Assert.That(onTheOldKnee, Is.EqualTo(curveAlone).Within(1e-9));
            Assert.That(onTheOldKnee, Is.Not.EqualTo(curveAlone * 0.5).Within(1e-9), "a live knee would halve exactly this play");
        });
    }

    /// <summary>The normalised exponential, written out so the assertions above are checkable.</summary>
    private static double expCurve(double t)
        => (Math.Exp(1.75 * t) - 1) / (Math.Exp(1.75) - 1); // pp:const acc_steepness=1.75*2

    // ---------------------------------------------------------------------------------------------
    // The combo bonus: a PERCENTAGE of the price, capped, scaled by the map's length, with a kicker
    // for a spotless full combo (v22).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_TheComboBonusIsAPercentageOfThePriceAndNotAnAmountBesideIt()
    {
        // THE PLACEMENT, which is the one thing this shape invites getting wrong and is the opposite
        // of v21's. The bonus multiplies, so a play with no run at all is the bare price and a play
        // with a run is that same price scaled. Asserted as a RATIO, which is what an additive bonus
        // could not produce: under v21 the same two plays differed by a number of pp that did not
        // depend on the rest of the play at all.
        double noRun = PerformancePoints.Compute(4, 1000, 1000, 0, 0.9, maxCombo: 0, mods: no_mods);
        double halfRun = PerformancePoints.Compute(4, 1000, 1000, 0, 0.9, maxCombo: 500, mods: no_mods);
        double harder = PerformancePoints.Compute(7, 1000, 1000, 0, 0.9, maxCombo: 0, mods: no_mods);
        double harderHalfRun = PerformancePoints.Compute(7, 1000, 1000, 0, 0.9, maxCombo: 500, mods: no_mods);

        Assert.Multiple(() =>
        {
            // 1000 cells earns a 5% ceiling (1% per 200), and half the map collects half of it.
            // 1000 cells earns a 5% ceiling at combo_bonus_at_200_cells = 1.0 percent per 200,
            // and half the map collects half of it. Spelled as the 5% rather than as the
            // constant, because the derivation is what the assertion is about.
            Assert.That(halfRun / noRun, Is.EqualTo(1 + 0.05 * 0.5).Within(1e-12));
            Assert.That(harderHalfRun / harder, Is.EqualTo(halfRun / noRun).Within(1e-12),
                "the same percentage on a harder map, which an additive bonus could not be");
        });
    }

    [TestCase(100, 0.5)]
    [TestCase(200, 1.0)]
    [TestCase(400, 2.0)]
    [TestCase(1000, 5.0)]
    [TestCase(2000, 10.0)] // pp:const combo_bonus_cap=10.0
    [TestCase(5000, 10.0)] // pp:const combo_bonus_cap=10.0
    [TestCase(20000, 10.0)] // pp:const combo_bonus_cap=10.0
    public void Compute_TheComboCeilingIsAStraightLineThroughTheOriginUntilItCaps(int notes, double expectedPercent)
    {
        // The ceiling a map's LENGTH earns: combo_bonus_at_200_cells percent at 200 cells on a line
        // through the origin, and combo_bonus_cap from 2000 cells up. Measured one cell short of a
        // full combo, because a FULL one takes the spotless kicker on top and would be measuring two
        // things at once.
        double noRun = PerformancePoints.Compute(4, notes, notes, 0, 0.9, maxCombo: 0, mods: no_mods);
        double nearlyAll = PerformancePoints.Compute(4, notes, notes, 0, 0.9, notes - 1, no_mods);

        Assert.That(nearlyAll / noRun,
            Is.EqualTo(1 + expectedPercent / 100.0 * (notes - 1.0) / notes).Within(1e-12),
            $"notes={notes}");
    }

    [Test]
    public void Compute_ASpotlessFullComboTakesTheKickerAndOneCellShortDoesNot()
    {
        // THE KICKER IS A CLIFF, deliberately: it is worth combo_bonus_perfect times the ceiling and
        // it needs BOTH halves, every cell in one run AND nothing dropped. A 2000-cell map therefore
        // pays +15% for 2000/2000 and just under +10% for 1999/2000, and a play that held the whole
        // map in one run while dropping a cell somewhere cannot (the two are mutually exclusive on a
        // real play, and the count clamp makes them so here too).
        double noRun = PerformancePoints.Compute(4, 2000, 2000, 0, 0.9, 0, no_mods);
        double full = PerformancePoints.Compute(4, 2000, 2000, 0, 0.9, 2000, no_mods);
        double oneShort = PerformancePoints.Compute(4, 2000, 2000, 0, 0.9, 1999, no_mods);

        Assert.Multiple(() =>
        {
            // 2000 cells is where combo_bonus_cap (10 percent) takes over, and a spotless full
            // combo multiplies that ceiling by combo_bonus_perfect (1.5), so 1.15.
            Assert.That(full / noRun, Is.EqualTo(1.15).Within(1e-12));
            Assert.That(oneShort / noRun, Is.EqualTo(1.09995).Within(1e-12));
            Assert.That(full / oneShort, Is.GreaterThan(1.045), "the kicker is a real step, not a rounding");
        });
    }

    [Test]
    public void Compute_ClampsAComboAboveTheNoteCountRatherThanRewardingIt()
    {
        double honest = PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods);
        double tampered = PerformancePoints.Compute(4, 500, 500, 0, 0.9, 5000, no_mods);

        Assert.That(tampered, Is.EqualTo(honest).Within(1e-9));
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
        // The difficult-character count is a DOUBLE off a stored document, so it can arrive as
        // anything at all: negative, NaN, infinite, or larger than the map.
        double[] difficulties = [0, -3, 0.5, 100, 1e9, double.NaN, double.PositiveInfinity];

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double acc in accuracies)
        foreach (double difficult in difficulties)
        {
            // Combo and misses deliberately out of range in both directions.
            foreach (int misses in new[] { -5, 0, notes, notes + 7 })
            foreach (int combo in new[] { -3, 0, notes, notes + 9 })
            {
                double pp = PerformancePoints.Compute(sr, notes, difficult, misses, acc, combo, no_mods);
                string context = $"sr={sr} notes={notes} difficult={difficult} miss={misses} acc={acc} combo={combo}";

                Assert.That(pp, Is.Not.NaN, context);
                Assert.That(double.IsFinite(pp), Is.True, context);
                Assert.That(pp, Is.GreaterThanOrEqualTo(0), context);
            }
        }
    }

    [Test]
    public void Compute_ZeroNotesEarnsNothing()
        => Assert.That(PerformancePoints.Compute(5, notes: 0, difficultCharacters: 100, misses: 0, accuracy: 1, maxCombo: 0, mods: no_mods), Is.Zero);

    [Test]
    public void Compute_OneNoteIsNoLongerDiscountedForBeingOneNote()
    {
        // A single perfect note on a 5-star map, priced purely by its rating, accuracy and the
        // tiny combo ceiling one cell earns. The inversion against the reference play is deliberate
        // and is not reachable: pp is a pure function over primitives and this feeds it a rating no
        // one-cell map could carry, since the star rating is what knows how long a map is.
        double pp = PerformancePoints.Compute(5, notes: 1, difficultCharacters: 1, misses: 0, accuracy: 1, maxCombo: 1, mods: no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.EqualTo(364.675083).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1, difficult=1)]
            Assert.That(pp, Is.GreaterThan(reference_pp));
        });
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
            Assert.That(counts.DifficultCharacters, Is.Zero, "the counts carry no map figure; StarsFor supplies it");
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
        // The reason CountNotes has to exclude it. Line containers are one ignore_hit per LINE, so a
        // 400-note map with 60 lines would read as 460 "notes". The count sits under the combo
        // ceiling, the combo RATIO and Flashlight's bonus, and on a spotless play the visible
        // casualty is the combo: a genuine full combo would stop reading as one, so the kicker is
        // lost as well as part of the ratio.
        double fullCombo = PerformancePoints.Compute(4, 400, 400, 0, 0.85, 400, no_mods);
        double inflated = PerformancePoints.Compute(4, 460, 460, 0, 0.85, 400, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(inflated, Is.LessThan(fullCombo));
            Assert.That((fullCombo - inflated) / fullCombo, Is.GreaterThan(0.005),
                "counting the line containers would cost a full combo its kicker and part of its ratio");
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
        });
    }

    /// <summary>
    /// LITERATE CONTRIBUTES NOTHING HERE (backlog 144), and that is the whole point rather than an
    /// omission: it is a CONVERSION mod, so it is priced through the rating of the map it converts
    /// (the matrix's literate stream, <see cref="PerformancePoints.StarsFor"/>) and a flat multiplier
    /// on top would be exactly the double count docs/pp.md forbids for DT/HT. It used to be 1.06.
    /// </summary>
    [Test]
    public void ModMultiplier_LiterateIsNeutralBecauseItIsPricedThroughTheStarRating()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("LT", null)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("lt", null)], 300), Is.EqualTo(1.0).Within(1e-12));

            // Stacked with a mod that IS priced here, only that mod's value survives.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("LT", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(PerformancePoints.ModMultiplier([new ScoreMod("NF", null)], 300)).Within(1e-12));
        });
    }

    [Test]
    public void ModMultiplier_RhythmicIsUnpricedSinceTheModWasRemoved()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RH", null)], 300), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("rh", null)], 300), Is.EqualTo(1.0).Within(1e-12));

            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RH", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(PerformancePoints.ModMultiplier([new ScoreMod("NF", null)], 300)).Within(1e-12));
        });
    }

    /// <summary>
    /// DEPARTURE 6 (v23): Recite is a MULTIPLIED Flashlight bonus, not a flat term. The mod hides the
    /// lyric until the line is sung, which is what Flashlight charges for, and that cost grows with
    /// how much map there is to hold in the head, so <c>recite_multiplier</c> is the SCALE on that
    /// bonus rather than a bonus of its own.
    /// </summary>
    [Test]
    public void ModMultiplier_ReciteScalesFlashlightsBonusRatherThanPayingAFlatTerm()
    {
        Assert.Multiple(() =>
        {
            foreach (int notes in new[] { 1, 46, 47, 100, 300, 500, 5000 })
            {
                // The definition, held at every length including under Flashlight's own floor, where
                // Recite is therefore worth exactly nothing either.
                Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RE", null)], notes),
                    Is.EqualTo(1 + 2.0 * (PerformancePoints.FlashlightMultiplier(notes) - 1)).Within(1e-12), // pp:const recite_multiplier=2.0
                    $"notes={notes}");
            }

            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RE", null)], 46), Is.EqualTo(1.0).Within(1e-12),
                "under Flashlight's floor there is no bonus to scale");
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RE", null)], 100), Is.EqualTo(1.04).Within(1e-12), // pp[f.recite_multiplier_for(100)]
                "twice Flashlight's 2% at the pivot");
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("re", null)], 300),
                Is.EqualTo(PerformancePoints.ModMultiplier([new ScoreMod("RE", null)], 300)).Within(1e-12));

            // The two mods still MULTIPLY when both are selected, exactly as every other pair does.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RE", null), new ScoreMod("FL", null)], 300),
                Is.EqualTo(PerformancePoints.ReciteMultiplierFor(300) * PerformancePoints.FlashlightMultiplier(300)).Within(1e-12));

            // And a duplicated acronym is applied once.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("RE", null), new ScoreMod("RE", null)], 300),
                Is.EqualTo(PerformancePoints.ReciteMultiplierFor(300)).Within(1e-12));
        });
    }

    [Test]
    public void ModMultiplier_FletcherStrictIsStillPricedFlat()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("FC", null)], 300), Is.EqualTo(1.02).Within(1e-12)); // pp[f.fletcher_strict_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("fc", null)], 300), Is.EqualTo(1.02).Within(1e-12)); // pp[f.fletcher_strict_multiplier]

            // FC is NOT the retired FT acronym, which means the opposite thing (an unpinned caret,
            // back when that was the mod rather than the default) and keeps its own 0.90.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("FT", null)], 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.fletcher_multiplier]
        });
    }

    /// <summary>
    /// EASY AND HARD ROCK ARE NOT WHAT THEY WERE. Both mods move the engine's own windows, and since
    /// the difficulty rework the STAR RATING prices the intervals a press may land in, so each is a
    /// JUDGEMENT ARM of the rating (<see cref="PerformancePoints.JudgementArmFor"/>) and the flat
    /// term here is only what the sandbox charges for what is LEFT. Hard Rock's is therefore
    /// NEUTRAL: charging it here as well would pay for the same change twice.
    /// </summary>
    [Test]
    public void ModMultiplier_EasyTrimsAndHardRockIsNeutralBecauseTheArmPricesIt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("EZ", null)], 300), Is.EqualTo(0.85).Within(1e-12)); // pp[f.easy_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("ez", null)], 300), Is.EqualTo(0.85).Within(1e-12)); // pp[f.easy_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("HR", null)], 300), Is.EqualTo(1.0).Within(1e-12)); // pp[f.hard_rock_multiplier]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("hr", null)], 300), Is.EqualTo(1.0).Within(1e-12)); // pp[f.hard_rock_multiplier]

            // Stacks with the other flat multipliers, and a duplicated acronym is applied once.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("EZ", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(0.765).Within(1e-12)); // pp[f.mod_multiplier(["EZ", "NF"], 300)]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("HR", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(0.9).Within(1e-12)); // pp[f.mod_multiplier(["HR", "NF"], 300)]
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("EZ", null), new ScoreMod("EZ", null)], 300),
                Is.EqualTo(0.85).Within(1e-12)); // pp[f.easy_multiplier]

            // The client makes Easy and Hard Rock mutually exclusive, so a row carrying both is
            // tamper-shaped; it is priced as the product rather than guessed at.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("HR", null), new ScoreMod("EZ", null)], 300),
                Is.EqualTo(0.85).Within(1e-12));
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
            [new ScoreMod("NF", null), new ScoreMod("FL", null)], 500);

        Assert.Multiple(() =>
        {
            Assert.That(stacked, Is.EqualTo(0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12)); // pp:const no_fail_multiplier=0.90

            // A duplicated acronym is tamper-shaped; it must be applied once, not squared.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("NF", null), new ScoreMod("NF", null)], 300),
                Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
        });
    }

    /// <summary>
    /// THE MOD MULTIPLIER SCALES THE COMBO BONUS TOO (v22), which is the exact reverse of v21 and is
    /// what the bonus becoming a PERCENTAGE means: the bonus is a factor of the price, so anything
    /// that scales the price scales it. Stated as its own assertion because it is the one placement
    /// the shape invites getting wrong, in either direction.
    /// </summary>
    [Test]
    public void Compute_AppliesTheModMultiplierToTheWholePriceIncludingTheComboBonus()
    {
        double bare = PerformancePoints.Compute(3, 300, 300, 5, 0.8, 250, no_mods);

        Assert.Multiple(() =>
        {
            Assert.That(bare, Is.EqualTo(40.325363).Within(1e-5)); // pp[f.compute(3, 300, 5, 0.8, 250, difficult=300)]

            foreach ((string acronym, double multiplier) in new[] { ("NF", 0.90), ("FT", 0.90), ("EZ", 0.85) }) // pp:const no_fail_multiplier=0.90*2 easy_multiplier=0.85
            {
                Assert.That(PerformancePoints.Compute(3, 300, 300, 5, 0.8, 250, [new ScoreMod(acronym, null)]),
                    Is.EqualTo(bare * multiplier).Within(1e-9), acronym);
            }

            // Literate does not reach this function at all: it moves the star rating that was passed
            // IN, not the multiplier applied here (backlog 144). Nor does Hard Rock, whose flat term
            // is neutral because the judgement arm prices it.
            Assert.That(PerformancePoints.Compute(3, 300, 300, 5, 0.8, 250, [new ScoreMod("LT", null)]),
                Is.EqualTo(bare).Within(1e-9));
            Assert.That(PerformancePoints.Compute(3, 300, 300, 5, 0.8, 250, [new ScoreMod("HR", null)]),
                Is.EqualTo(bare).Within(1e-9));
            Assert.That(PerformancePoints.Compute(3, 300, 300, 5, 0.8, 250, [new ScoreMod("FL", null)]),
                Is.EqualTo(bare * PerformancePoints.FlashlightMultiplier(300)).Within(1e-9));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The typo term is GONE (v22). Its absence is the assertion.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Compute_ATypoCostsExactlyNothing()
    {
        // A wrong keypress the player recovered from is free since v22: the price is the rating,
        // cleanliness, timing, mods and the combo bonus alone. The parameter stays on the signature
        // so every call site that passes a count reads unchanged, and this is what it now means.
        double clean = PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods, typos: 0);

        Assert.Multiple(() =>
        {
            foreach (int typos in new[] { 1, 15, 45, 500, 5000, int.MaxValue, -1 })
            {
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods, typos),
                    Is.EqualTo(clean), $"typos={typos}");
            }

            // And omitting the argument entirely is the same play, which is what the default is for.
            Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods), Is.EqualTo(clean));
        });
    }

    [Test]
    public void ForScore_TheTyposCarriedOnTheCountsAreNotPricedEither()
    {
        var ratings = FullMatrix(4);

        var clean = PerformancePoints.ForScore(true, no_mods, new PerformancePoints.NoteCounts(500, 0), 0.9, 500, ratings);
        var messy = PerformancePoints.ForScore(true, no_mods, new PerformancePoints.NoteCounts(500, 0, 60), 0.9, 500, ratings);

        Assert.Multiple(() =>
        {
            Assert.That(clean.Settled, Is.True);
            Assert.That(messy.Settled, Is.True);
            Assert.That(messy.Pp, Is.EqualTo(clean.Pp), "the counts still carry the typos; the formula no longer reads them");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Backlog 213 still holds: an UNCORRECTED TYPO is a MISS. misses = miss + good, typos =
    // max(0, combo_break - good), notes untouched. The DERIVATION survives the typo term's deletion
    // because it is about what a miss IS, and the miss term is very much alive.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void CountNotes_ReadsTyposFromTheComboBreakKeyWithoutCountingThemAsNotes()
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
            Assert.That(counts.Notes, Is.EqualTo(400));
            Assert.That(counts.Misses, Is.EqualTo(50));
            Assert.That(counts.Typos, Is.EqualTo(137), "still derived, for the surfaces that display it");
        });
    }

    [Test]
    public void CountNotes_AMissingTypoKeyIsNotZeroGuessedButSimplyAbsent()
    {
        var old = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 100, ["miss"] = 10 });

        Assert.Multiple(() =>
        {
            Assert.That(old.Typos, Is.Zero);
            Assert.That(PerformancePoints.CountNotes("""{"great":100,"miss":10}""").Typos, Is.Zero);
            Assert.That(PerformancePoints.CountNotes("""{"great":100,"miss":10,"combo_break":9}""").Typos, Is.EqualTo(9));
        });
    }

    [Test]
    public void CountNotes_NegativeTypoCountsContributeNothing()
        => Assert.That(PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 100, ["combo_break"] = -50 }).Typos, Is.Zero);

    [Test]
    public void CountNotes_PricesAnUncorrectedTypoByTheMissTermAndTakesItOutOfTheTypoTerm()
    {
        var counts = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["great"] = 300,
            ["ok"] = 40,
            ["meh"] = 10,
            ["good"] = 7,
            ["miss"] = 43,
            ["combo_break"] = 20,
        });

        Assert.Multiple(() =>
        {
            Assert.That(counts.Notes, Is.EqualTo(400), "the typo cells stay in the note count");
            Assert.That(counts.Misses, Is.EqualTo(50), "43 cells nobody typed plus 7 left holding a wrong character");
            Assert.That(counts.Typos, Is.EqualTo(13), "20 wrong keypresses, 7 of which were never corrected");

            // The jsonb path reads it identically, which is the one PpBackfill actually walks.
            Assert.That(
                PerformancePoints.CountNotes("""{"great":300,"ok":40,"meh":10,"good":7,"miss":43,"combo_break":20}"""),
                Is.EqualTo(counts));
        });
    }

    [Test]
    public void CountNotes_AnUncorrectedTypoPricesIdenticallyToADroppedCell()
    {
        var dropped = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 399, ["miss"] = 1 });
        var leftWrong = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 399, ["good"] = 1, ["combo_break"] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(leftWrong, Is.EqualTo(dropped));
            Assert.That(PerformancePoints.Compute(4.2, leftWrong.Notes, 400, leftWrong.Misses, 0.9, 380, no_mods, leftWrong.Typos),
                Is.EqualTo(PerformancePoints.Compute(4.2, dropped.Notes, 400, dropped.Misses, 0.9, 380, no_mods, dropped.Typos)));
        });
    }

    /// <summary>
    /// NO DOUBLE JEOPARDY from the other side, and the incentive has CHANGED SIDES since v22. A typo
    /// the player FIXED stays a typo event, which used to cost the play its own term and now costs
    /// nothing at all, so correcting a flub is not merely cheaper than leaving it standing, it is
    /// FREE. The equality is the assertion; leaving it standing is still a miss.
    /// </summary>
    [Test]
    public void CountNotes_ACorrectedTypoIsFreeAndLeavingItStandingIsAMiss()
    {
        var corrected = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 399, ["ok"] = 1, ["combo_break"] = 1 });
        var leftWrong = PerformancePoints.CountNotes(new Dictionary<string, int> { ["great"] = 399, ["good"] = 1, ["combo_break"] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(corrected.Notes, Is.EqualTo(400));
            Assert.That(corrected.Misses, Is.Zero);
            Assert.That(corrected.Typos, Is.EqualTo(1));

            Assert.That(PerformancePoints.Compute(4.2, corrected.Notes, 400, corrected.Misses, 0.99, 400, no_mods, corrected.Typos),
                Is.GreaterThan(PerformancePoints.Compute(4.2, leftWrong.Notes, 400, leftWrong.Misses, 0.99, 400, no_mods, leftWrong.Typos)));

            // And the correction is FREE: the same play with the typo event and without it price
            // identically, because only the miss it avoided ever cost anything.
            Assert.That(PerformancePoints.Compute(4.2, corrected.Notes, 400, corrected.Misses, 0.99, 400, no_mods, corrected.Typos),
                Is.EqualTo(PerformancePoints.Compute(4.2, 400, 400, 0, 0.99, 400, no_mods, typos: 0)));
        });
    }

    [TestCase(0, 5, 0, TestName = "CountNotes_TheTypoSubtractionIsClamped(a pre-backlog-72 row with no combo_break key)")]
    [TestCase(3, 5, 0, TestName = "CountNotes_TheTypoSubtractionIsClamped(fewer keypresses stored than typo cells)")]
    [TestCase(5, 5, 0, TestName = "CountNotes_TheTypoSubtractionIsClamped(every keypress went uncorrected)")]
    [TestCase(9, 5, 4, TestName = "CountNotes_TheTypoSubtractionIsClamped(four of the nine were corrected)")]
    public void CountNotes_TheTypoSubtractionIsClamped(int mistypes, int unfixedTypos, int expectedTypos)
    {
        var statistics = new Dictionary<string, int> { ["great"] = 100, ["good"] = unfixedTypos };

        if (mistypes > 0)
            statistics["combo_break"] = mistypes;

        var counts = PerformancePoints.CountNotes(statistics);

        Assert.Multiple(() =>
        {
            Assert.That(counts.Typos, Is.EqualTo(expectedTypos));
            Assert.That(counts.Typos, Is.GreaterThanOrEqualTo(0));
            Assert.That(counts.Misses, Is.EqualTo(unfixedTypos));
            Assert.That(counts.Notes, Is.EqualTo(100 + unfixedTypos));
        });
    }

    [Test]
    public void CountNotes_ARowWithNoUncorrectedTypoIsUnmovedByTheFold()
    {
        var counts = PerformancePoints.CountNotes(new Dictionary<string, int>
        {
            ["great"] = 300, ["ok"] = 40, ["meh"] = 10, ["miss"] = 50, ["combo_break"] = 137,
        });

        Assert.That(counts, Is.EqualTo(new PerformancePoints.NoteCounts(400, 50, 137)));
    }

    // ---------------------------------------------------------------------------------------------
    // The RATING MATRIX (034_ratings_matrix.sql): which cell prices a play, and what a missing one
    // means.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A matrix built by hand, through the stored document so the parse is exercised too. The
    /// difficult-character count of each cell is DELIBERATELY not proportional to its stars: the two
    /// halves move independently on a real map, and a test whose fixture tied them together could
    /// not tell a lookup of the wrong half from a lookup of the right one.
    /// </summary>
    private static BeatmapRatings Matrix(params (LyricDifficulty.JudgementArm Arm, bool Literate, double Rate, double Stars, double Difficult)[] cells)
    {
        var body = new JObject();

        foreach (var cell in cells)
            body[BeatmapRatings.Key(cell.Arm, cell.Literate, cell.Rate)] = new JObject { ["sr"] = cell.Stars, ["dc"] = cell.Difficult };

        return BeatmapRatings.Parse(Document(cells))!;
    }

    /// <summary>
    /// The same cells as a raw stored DOCUMENT, for the cases where the parse is expected to refuse
    /// it outright and <see cref="Matrix"/> would therefore hand back a null to dereference.
    /// </summary>
    private static string Document(params (LyricDifficulty.JudgementArm Arm, bool Literate, double Rate, double Stars, double Difficult)[] cells)
    {
        var body = new JObject();

        foreach (var cell in cells)
            body[BeatmapRatings.Key(cell.Arm, cell.Literate, cell.Rate)] = new JObject { ["sr"] = cell.Stars, ["dc"] = cell.Difficult };

        return new JObject { ["version"] = BeatmapRatings.SCHEMA_VERSION, ["cells"] = body }.ToString();
    }

    /// <summary>All eighteen cells, each carrying a rating derived from its own coordinates.</summary>
    private static BeatmapRatings FullMatrix(double baseStars)
    {
        var cells = new List<(LyricDifficulty.JudgementArm, bool, double, double, double)>();

        foreach ((string _, LyricDifficulty.JudgementArm arm) in BeatmapRatings.Arms)
        foreach (bool literate in new[] { false, true })
        foreach (double rate in BeatmapRatings.Rates)
        {
            // Distinct per coordinate, so a lookup that reads the wrong axis reads a wrong number
            // rather than the right one by luck.
            double stars = baseStars * rate + (literate ? 0.3 : 0) + arm switch
            {
                LyricDifficulty.JudgementArm.Easy => -0.2,
                LyricDifficulty.JudgementArm.HardRock => 0.5,
                _ => 0,
            };

            cells.Add((arm, literate, rate, stars, 100 * stars));
        }

        return Matrix([.. cells]);
    }

    [Test]
    public void JudgementArmFor_KeysOnTheAcronymsThatTravelOnTheWire()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.JudgementArmFor(null), Is.EqualTo(LyricDifficulty.JudgementArm.None));
            Assert.That(PerformancePoints.JudgementArmFor([]), Is.EqualTo(LyricDifficulty.JudgementArm.None));
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("NF", null)]), Is.EqualTo(LyricDifficulty.JudgementArm.None));
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("EZ", null)]), Is.EqualTo(LyricDifficulty.JudgementArm.Easy));
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("ez", null)]), Is.EqualTo(LyricDifficulty.JudgementArm.Easy));
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("HR", null)]), Is.EqualTo(LyricDifficulty.JudgementArm.HardRock));
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod(" hr ", null)]), Is.EqualTo(LyricDifficulty.JudgementArm.HardRock));

            // The arm is orthogonal to everything else on the stack.
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("LT", null), new ScoreMod("DT", 1.50), new ScoreMod("HR", null)]),
                Is.EqualTo(LyricDifficulty.JudgementArm.HardRock));

            // A stack carrying both is tamper-shaped (the client makes them exclusive) and takes
            // whichever comes first, matching the client's own loop.
            Assert.That(PerformancePoints.JudgementArmFor([new ScoreMod("EZ", null), new ScoreMod("HR", null)]),
                Is.EqualTo(LyricDifficulty.JudgementArm.Easy));
        });
    }

    /// <summary>
    /// The whole storage decision stated as a table: the play's ARM, STREAM and RATE each select one
    /// axis of the matrix, independently, and together they name exactly one cell.
    /// </summary>
    [TestCase(new string[0], LyricDifficulty.JudgementArm.None, false, 1.00)]
    [TestCase(new[] { "NF" }, LyricDifficulty.JudgementArm.None, false, 1.00)]
    [TestCase(new[] { "DT" }, LyricDifficulty.JudgementArm.None, false, 1.50)]
    [TestCase(new[] { "NC" }, LyricDifficulty.JudgementArm.None, false, 1.50)]
    [TestCase(new[] { "HT" }, LyricDifficulty.JudgementArm.None, false, 0.75)]
    [TestCase(new[] { "LT" }, LyricDifficulty.JudgementArm.None, true, 1.00)]
    [TestCase(new[] { "LT", "DT" }, LyricDifficulty.JudgementArm.None, true, 1.50)]
    [TestCase(new[] { "LT", "HT" }, LyricDifficulty.JudgementArm.None, true, 0.75)]
    [TestCase(new[] { "EZ" }, LyricDifficulty.JudgementArm.Easy, false, 1.00)]
    [TestCase(new[] { "EZ", "DT" }, LyricDifficulty.JudgementArm.Easy, false, 1.50)]
    [TestCase(new[] { "EZ", "LT", "HT" }, LyricDifficulty.JudgementArm.Easy, true, 0.75)]
    [TestCase(new[] { "HR" }, LyricDifficulty.JudgementArm.HardRock, false, 1.00)]
    [TestCase(new[] { "HR", "LT", "DT" }, LyricDifficulty.JudgementArm.HardRock, true, 1.50)]
    public void StarsFor_SelectsExactlyOneCellByArmStreamAndRate(
        string[] acronyms, LyricDifficulty.JudgementArm arm, bool literate, double rate)
    {
        var ratings = FullMatrix(4);
        var mods = acronyms.Select(a => new ScoreMod(a, RateMods.IsRateMod(a) ? RateMods.DefaultSpeed(a) : null)).ToArray();

        var stars = PerformancePoints.StarsFor(mods, ratings);
        var expected = ratings.TryGet(arm, literate, rate)!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.EqualTo(expected.Stars));
            Assert.That(stars.DifficultCharacters, Is.EqualTo(expected.DifficultCharacters),
                "the count travels with the rating; a price needs both halves");
            Assert.That(stars.Pending, Is.False);
        });
    }

    [Test]
    public void StarsFor_AHistoricRateModWithNoStoredRateReadsAsItsBaseRate()
    {
        // Pre-task-27 rows carry no speed_change at all; under the old rules a ranked bare DT could
        // only have been 1.50x, so they must stay pp-eligible.
        var ratings = FullMatrix(4);
        var stars = PerformancePoints.StarsFor([new ScoreMod("DT", null)], ratings);

        Assert.That(stars.Stars, Is.EqualTo(ratings.TryGet(LyricDifficulty.JudgementArm.None, false, 1.50)!.Value.Stars));
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
        var stars = PerformancePoints.StarsFor([new ScoreMod(acronym, rate)], FullMatrix(4));

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.Null);
            // Not "pending": nothing will ever make this play pp-eligible, so the row settles.
            Assert.That(stars.Pending, Is.False);
            Assert.That(stars.DifficultCharacters, Is.Zero);
        });
    }

    [Test]
    public void StarsFor_TwoRateModsAtOnceIsIneligibleRatherThanGuessedAt()
    {
        var stars = PerformancePoints.StarsFor([new ScoreMod("DT", 1.50), new ScoreMod("HT", 0.75)], FullMatrix(4));

        Assert.Multiple(() =>
        {
            Assert.That(stars.Stars, Is.Null);
            Assert.That(stars.Pending, Is.False);
        });
    }

    [Test]
    public void StarsFor_ANullMatrixIsPendingAndNotARefusal()
    {
        // THE COLUMN'S WHOLE CONTRACT. A map the sweep has not reached carries no matrix, so every
        // play on it is left stale for PpBackfill rather than priced at a number the next sweep would
        // have to disagree with. That is the rule an unfilled sr_dt has had since backlog 90; it now
        // covers every stack rather than the rate ones alone, because the matrix is a NEW column and
        // is null on every pre-existing row.
        foreach (IReadOnlyList<ScoreMod> mods in new IReadOnlyList<ScoreMod>[]
                 {
                     [],
                     [new ScoreMod("NF", null)],
                     [new ScoreMod("DT", 1.50)],
                     [new ScoreMod("LT", null)],
                     [new ScoreMod("HR", null)],
                 })
        {
            var stars = PerformancePoints.StarsFor(mods, null);

            Assert.Multiple(() =>
            {
                Assert.That(stars.Stars, Is.Null);
                Assert.That(stars.Pending, Is.True);
            });
        }

        // A CUSTOM RATE IS STILL A REFUSAL even with no matrix, because the rate is checked before
        // the lookup: nothing about filling the column can ever make that play eligible, so it must
        // settle rather than be rescanned forever.
        var custom = PerformancePoints.StarsFor([new ScoreMod("DT", 1.75)], null);

        Assert.Multiple(() =>
        {
            Assert.That(custom.Stars, Is.Null);
            Assert.That(custom.Pending, Is.False);
        });
    }

    [Test]
    public void StarsFor_AMatrixMissingThisPlaysCellIsPendingToo()
    {
        // The half-filled case: a document that carries the plain arm-none cells and nothing else,
        // which is what a matrix written by an older, narrower shape would look like. The plays it
        // does cover price; the rest are pending, one cell at a time rather than all or nothing.
        var partial = Matrix(
            (LyricDifficulty.JudgementArm.None, false, 1.00, 4.0, 400),
            (LyricDifficulty.JudgementArm.None, false, 1.50, 6.1, 610));

        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.StarsFor([], partial).Stars, Is.EqualTo(4.0));
            Assert.That(PerformancePoints.StarsFor([new ScoreMod("DT", 1.50)], partial).Stars, Is.EqualTo(6.1));

            foreach (IReadOnlyList<ScoreMod> missing in new IReadOnlyList<ScoreMod>[]
                     {
                         [new ScoreMod("HT", 0.75)],
                         [new ScoreMod("LT", null)],
                         [new ScoreMod("EZ", null)],
                         [new ScoreMod("HR", null)],
                     })
            {
                var stars = PerformancePoints.StarsFor(missing, partial);

                Assert.That(stars.Stars, Is.Null);
                Assert.That(stars.Pending, Is.True);
            }
        });
    }

    [Test]
    public void ForScore_PricesOffTheCellTheStackSelects()
    {
        var ratings = FullMatrix(4);
        var counts = new PerformancePoints.NoteCounts(500, 12, 15);

        foreach (IReadOnlyList<ScoreMod> mods in new IReadOnlyList<ScoreMod>[]
                 {
                     [],
                     [new ScoreMod("DT", 1.50)],
                     [new ScoreMod("HT", 0.75)],
                     [new ScoreMod("LT", null)],
                     [new ScoreMod("EZ", null)],
                     [new ScoreMod("HR", null), new ScoreMod("LT", null), new ScoreMod("DT", 1.50)],
                 })
        {
            var cell = PerformancePoints.StarsFor(mods, ratings);
            var (pp, settled) = PerformancePoints.ForScore(true, mods, counts, 0.9, 480, ratings);

            Assert.Multiple(() =>
            {
                Assert.That(settled, Is.True);
                Assert.That(pp, Is.EqualTo(PerformancePoints.Compute(
                    cell.Stars!.Value, 500, cell.DifficultCharacters, 12, 0.9, 480, mods, 15)).Within(1e-12));
            });
        }
    }

    [Test]
    public void ForScore_TheRateAndTheArmBothMoveThePriceThroughTheRatingAlone()
    {
        var ratings = FullMatrix(4);
        var counts = new PerformancePoints.NoteCounts(500, 0);

        var (nomod, _) = PerformancePoints.ForScore(true, [], counts, 0.9, 480, ratings);
        var (dt, _) = PerformancePoints.ForScore(true, [new ScoreMod("DT", 1.50)], counts, 0.9, 480, ratings);
        var (ht, _) = PerformancePoints.ForScore(true, [new ScoreMod("HT", 0.75)], counts, 0.9, 480, ratings);
        var (hr, _) = PerformancePoints.ForScore(true, [new ScoreMod("HR", null)], counts, 0.9, 480, ratings);

        Assert.Multiple(() =>
        {
            // The rate lands entirely in the star rating: harder up-rate, easier down-rate.
            Assert.That(dt!.Value, Is.GreaterThan(nomod!.Value));
            Assert.That(ht!.Value, Is.LessThan(nomod.Value));

            // And so does the ARM. Hard Rock's flat multiplier is exactly 1.0, so its whole price
            // difference comes through the cell it selected, which is the point of making the arm a
            // rating input instead of a second multiplier.
            Assert.That(PerformancePoints.ModMultiplier([new ScoreMod("HR", null)], 500), Is.EqualTo(1.0));
            Assert.That(hr!.Value, Is.GreaterThan(nomod.Value));
        });
    }

    [Test]
    public void ForScore_MissingCellIsPendingRatherThanZeroForever()
    {
        var (pp, settled) = PerformancePoints.ForScore(
            ranked: true,
            [new ScoreMod("DT", 1.50)],
            new PerformancePoints.NoteCounts(500, 0),
            0.9, 500,
            ratings: null); // the sweep has not reached this map yet

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Null, "no rating means no price, which is not the same as a price of zero");
            Assert.That(settled, Is.False, "an unpriced row must be left stale so the backfill retries it");
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
            FullMatrix(4));

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
    public void ForScore_UnrankedScoresEarnNothing()
    {
        var (pp, settled) = PerformancePoints.ForScore(
            ranked: false, [], new PerformancePoints.NoteCounts(500, 0), 1.0, 500, FullMatrix(8));

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Null, "refused outright: an unranked play is never priced, it is not priced at zero");
            Assert.That(settled, Is.True, "nothing about an unranked row will change; it must not be rescanned forever");
        });
    }

    [Test]
    public void ForScore_ADegenerateRatingEarnsZeroAndStaysFinite()
    {
        // A stored cell can hold any number a jsonb document can, so a degenerate rating has to
        // price to a well-defined 0 rather than to a negative. It SETTLES, which is the other half:
        // the cell is there, it is just useless, and nothing about revisiting the row would change
        // that.
        foreach (double stars in new[] { 0.0, -3.0 })
        {
            var ratings = Matrix((LyricDifficulty.JudgementArm.None, false, 1.00, stars, 400));
            var (pp, settled) = PerformancePoints.ForScore(
                true, [], new PerformancePoints.NoteCounts(500, 0), 0.9, 500, ratings);

            Assert.Multiple(() =>
            {
                Assert.That(settled, Is.True, $"stars={stars}");
                Assert.That(pp, Is.Not.Null, $"stars={stars}");
                Assert.That(double.IsFinite(pp!.Value), Is.True, $"stars={stars}");
                Assert.That(pp.Value, Is.Zero, $"stars={stars}");
            });
        }
    }

    [Test]
    public void BeatmapRatings_ANonFiniteCellIsDroppedRatherThanStored()
    {
        // NaN AND THE INFINITIES ARE NOT A DEGENERATE RATING, they are an UNSTORABLE one, and the
        // difference is the whole of this test. PostgreSQL's jsonb refuses them outright ("cannot
        // convert NaN to jsonb"), so a document carrying one could not be written at all and an
        // ingest that tried would lose the whole upload rather than one cell of it. They are
        // therefore dropped at both ends of the round trip, which makes the play PENDING (the same
        // answer a missing cell gets) rather than settling it at 0 forever through Compute's own
        // guard.
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Multiple(() =>
            {
                // A cell whose STARS are unstorable, and one whose DIFFICULT CHARACTERS are: both
                // halves are written, so both have to be checked. A document of nothing but such a
                // cell reads as NO MATRIX, which is the same thing a NULL column reads as.
                Assert.That(BeatmapRatings.Parse(Document((LyricDifficulty.JudgementArm.None, false, 1.00, bad, 400))),
                    Is.Null, $"stars={bad}");
                Assert.That(BeatmapRatings.Parse(Document((LyricDifficulty.JudgementArm.None, false, 1.00, 4.0, bad))),
                    Is.Null, $"difficult={bad}");

                var stars = PerformancePoints.StarsFor(
                    [], BeatmapRatings.Parse(Document((LyricDifficulty.JudgementArm.None, false, 1.00, bad, 400))));

                Assert.That(stars.Stars, Is.Null, $"stars={bad}");
                Assert.That(stars.Pending, Is.True, $"stars={bad}: pending, so a later sweep can replace it");
            });
        }

        // And a matrix holding one bad cell keeps the others, one cell at a time rather than all or
        // nothing: the unstorable reading is the only play left pending.
        var mixed = Matrix(
            (LyricDifficulty.JudgementArm.None, false, 1.00, 4.0, 400),
            (LyricDifficulty.JudgementArm.None, false, 1.50, double.NaN, 600));

        Assert.Multiple(() =>
        {
            Assert.That(mixed.Count, Is.EqualTo(1));
            Assert.That(PerformancePoints.StarsFor([], mixed).Stars, Is.EqualTo(4.0));
            Assert.That(PerformancePoints.StarsFor([new ScoreMod("DT", 1.50)], mixed).Pending, Is.True);
        });
    }

    [Test]
    public void BaseRates_AreTheRateModSliderDefaults()
    {
        // The pp-eligible rates are not a second copy of 1.50/0.75 living here; they are the very
        // defaults the rate mods are parsed and priced against, and the matrix is keyed on the same
        // three rather than on literals of its own.
        Assert.Multiple(() =>
        {
            Assert.That(RateMods.DoubleTimeBaseRate, Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
            Assert.That(RateMods.HalfTimeBaseRate, Is.EqualTo(0.75)); // pp[f.half_time_base_rate]
            Assert.That(RateMods.DefaultSpeed("DT"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("NC"), Is.EqualTo(RateMods.DoubleTimeBaseRate));
            Assert.That(RateMods.DefaultSpeed("HT"), Is.EqualTo(RateMods.HalfTimeBaseRate));
            // Daycore is HT's pitch-preserving twin: same base rate, so it reads the HT matrix cell.
            Assert.That(RateMods.DefaultSpeed("DC"), Is.EqualTo(RateMods.HalfTimeBaseRate));
            Assert.That(RateMods.IsRateMod("DC"), Is.True);

            Assert.That(BeatmapRatings.Rates, Is.EqualTo(new[] { 1.0, RateMods.DoubleTimeBaseRate, RateMods.HalfTimeBaseRate }).AsCollection);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The matrix document itself.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void BeatmapRatings_RoundTripsEveryCellThroughItsStoredDocument()
    {
        var ratings = FullMatrix(4.2);
        var reparsed = BeatmapRatings.Parse(ratings.ToJson())!;

        Assert.Multiple(() =>
        {
            Assert.That(ratings.Count, Is.EqualTo(18), "three arms, two streams, three rates");
            Assert.That(reparsed.Count, Is.EqualTo(18));

            foreach ((string name, LyricDifficulty.JudgementArm arm) in BeatmapRatings.Arms)
            foreach (bool literate in new[] { false, true })
            foreach (double rate in BeatmapRatings.Rates)
            {
                // EXACT, not within a tolerance: the document has to reproduce the bits the ingest
                // computed or a reparsed row prices a play differently from the row that wrote it.
                Assert.That(reparsed.TryGet(arm, literate, rate), Is.EqualTo(ratings.TryGet(arm, literate, rate)),
                    $"{name} literate={literate} rate={rate}");
            }
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json at all")]
    [TestCase("[1,2,3]")]
    [TestCase("""{"version":1}""")]
    [TestCase("""{"version":1,"cells":{}}""")]
    [TestCase("""{"version":1,"cells":{"none/plain/1.00":{"sr":4}}}""")]
    [TestCase("""{"version":1,"cells":{"none/plain/1.00":{"dc":400}}}""")]
    [TestCase("""{"version":1,"cells":{"none/plain/1.00":"4"}}""")]
    public void BeatmapRatings_MalformedDocumentsReadAsNoMatrixRatherThanThrowing(string? json)
    {
        // A bad row must leave its plays PENDING, not take a submission path down with it, and a
        // HALF cell is dropped rather than defaulted: half a cell cannot price a play.
        Assert.That(BeatmapRatings.Parse(json), Is.Null);
    }

    [Test]
    public void BeatmapRatings_KeysAreInvariantWhateverTheLocale()
    {
        // The rate is formatted to two decimals with an invariant separator, so the document written
        // on one machine is read on every other. A locale that spells a decimal point as a comma
        // would otherwise key "1,50" and find nothing.
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapRatings.Key(LyricDifficulty.JudgementArm.None, false, 1.0), Is.EqualTo("none/plain/1.00"));
            Assert.That(BeatmapRatings.Key(LyricDifficulty.JudgementArm.None, true, 1.50), Is.EqualTo("none/literate/1.50"));
            Assert.That(BeatmapRatings.Key(LyricDifficulty.JudgementArm.Easy, false, 0.75), Is.EqualTo("ez/plain/0.75"));
            Assert.That(BeatmapRatings.Key(LyricDifficulty.JudgementArm.HardRock, true, 1.50), Is.EqualTo("hr/literate/1.50"));
        });
    }

    [Test]
    public void Version_IsBumpedBecauseTheForkRepricesEveryStoredRow()
    {
        // v24 = the PP Sandbox's live dials, on top of v22's shape fork and v23's accuracy curve and
        // Recite change. Every stored row reprices, which is what the bump is for, and unlike every
        // bump since v12 this one ALSO needs the pace sweep to have filled beatmaps.ratings: a row
        // whose cell is missing stays pending rather than being priced with a zeroed
        // difficult-character count. If this moves, so do the game's PerformancePoints.VERSION and
        // docs/pp.md.
        Assert.That(PerformancePoints.VERSION, Is.EqualTo(24)); // pp:version
    }

    [Test]
    public void Decay_IsTheDocumentedStartingValue()
    {
        // Intended to be raised towards osu's 0.95 as the ranked pool grows; if this value moves,
        // docs/pp.md moves with it.
        Assert.That(PerformancePoints.DECAY, Is.EqualTo(0.92)); // pp[f.decay]
    }
}
