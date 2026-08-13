using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The rate-mod score curve and the mod-stack bound it feeds.
///
/// <para>
/// <see cref="RateMultiplier"/> is a verbatim port of the game's <c>TypeBeatRateMultiplier</c>, so
/// the pinned table below is copied from the client's own pins: both implementations round to fixed
/// decimal counts (2 on the rate, 4 on the multiplier, away from zero on both), which makes the
/// contract exact decimal values rather than "within some epsilon". If the game's table ever
/// changes, this test must fail.
/// </para>
/// </summary>
public class RateMultiplierTest
{
    // (rate, multiplier) pins, identical to the game-side table. The decrease side is the
    // post-nerf curve (slope 3.00, task 44); the increase side is byte-identical to what it
    // always was.
    private static readonly (double rate, double multiplier)[] pinned =
    [
        (0.50, 0.1000), // below the floor's crossing point: everything down here pays the floor
        (0.70, 0.1000), // exactly where the floor is reached now (it used to be reached at 0.50)
        (0.71, 0.1300),
        (0.75, 0.2500), // the Half Time default: 0.55 before the nerf
        (0.80, 0.4000),
        (0.90, 0.7000),
        (0.99, 0.9700),
        (1.00, 1.0000),
        (1.01, 1.0046),
        (1.50, 1.2300),
        (2.00, 1.4600),
    ];

    // Every rate in [0.50, 0.70] is flattened onto the floor by the steeper slope.
    private const double floor_reached_at = 0.70;

    [Test]
    public void Curve_MatchesTheGamesPinnedTable()
    {
        Assert.Multiple(() =>
        {
            foreach (var (rate, multiplier) in pinned)
                Assert.That(RateMultiplier.For(rate), Is.EqualTo(multiplier).Within(1e-12), $"rate {rate}");
        });
    }

    [Test]
    public void Curve_IsContinuousAtOne_AndMonotonic()
    {
        // Exactly 1.0 at 1.0 from both sides: dialling a rate mod back toward no-mod pays what
        // no-mod pays, which is what makes the setting rankable at all.
        Assert.That(RateMultiplier.For(1.00), Is.EqualTo(1.0).Within(1e-12));
        Assert.That(RateMultiplier.For(0.99), Is.LessThan(1.0));
        Assert.That(RateMultiplier.For(1.01), Is.GreaterThan(1.0));

        // Non-decreasing across the whole reachable domain in the slider's 0.01 steps, and STRICTLY
        // increasing everywhere above the floor's crossing point: there is never a rate you can
        // pick for free. Below 0.70x the curve is flat on the 0.10 floor, which is the deliberate
        // shape of the nerf rather than a hole in the pricing (0.10 is already the cheapest a play
        // can be worth, so nothing is gained by slowing down further).
        double previous = double.NegativeInfinity;

        for (int step = 50; step <= 200; step++)
        {
            double rate = step / 100.0;
            double multiplier = RateMultiplier.For(rate);

            if (rate <= floor_reached_at)
                Assert.That(multiplier, Is.EqualTo(RateMultiplier.MINIMUM).Within(1e-12), $"floor holds at {rate}");
            else
                Assert.That(multiplier, Is.GreaterThan(previous), $"multiplier must strictly increase at {rate}");

            previous = multiplier;
        }
    }

    [Test]
    public void Curve_SnapsTheRateToTheSlidersPrecision()
    {
        // The slider steps by 0.01, so anything between two steps is snapped before pricing.
        Assert.That(RateMultiplier.For(1.504), Is.EqualTo(RateMultiplier.For(1.50)).Within(1e-12));
        Assert.That(RateMultiplier.For(1.505), Is.EqualTo(RateMultiplier.For(1.51)).Within(1e-12));
    }

    // ---- per-mod multipliers (mirror of TypeBeatScoreMultiplierCalculator) ----

    [Test]
    public void ModMultiplier_MirrorsTheClientsFlatValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModMultiplier.For("NF", null), Is.EqualTo(0.5));
            // Easy (backlog 149): osu's 0.5x for a difficulty reduction, the same value NF carries.
            Assert.That(ModMultiplier.For("EZ", null), Is.EqualTo(0.5));
            Assert.That(ModMultiplier.For("ez", null), Is.EqualTo(0.5));
            // Hard Rock (backlog 150): halved windows, the mirror of Easy, at the 1.10 the client's
            // calculator prices it at. Not its 1.25 pp value: see MaxForStack_PinsTheFattestRankedStack.
            Assert.That(ModMultiplier.For("HR", null), Is.EqualTo(1.10));
            Assert.That(ModMultiplier.For("hr", null), Is.EqualTo(1.10));
            Assert.That(ModMultiplier.For("SD", null), Is.EqualTo(1.0));
            // Gatekeeper (backlog 107): ranked, and priced at exactly 1.0 rather than left to the
            // unknown-mod allowance, so a GK play is bounded like the no-mod play it scores as.
            Assert.That(ModMultiplier.For("GK", null), Is.EqualTo(1.0));
            Assert.That(ModMultiplier.For("gk", null), Is.EqualTo(1.0));
            Assert.That(ModMultiplier.For("FL", null), Is.EqualTo(1.05));
            Assert.That(ModMultiplier.For("LT", null), Is.EqualTo(1.05));
            // Rhythmic (backlog 135), kept after backlog 147 removed the mod from the client.
            // See ModMultiplier_StillBoundsAStoredRhythmicPlay below for why deleting it unranks
            // every row that carries the acronym.
            Assert.That(ModMultiplier.For("RH", null), Is.EqualTo(1.10));
            Assert.That(ModMultiplier.For("rh", null), Is.EqualTo(1.10));
            Assert.That(ModMultiplier.For("FT", null), Is.EqualTo(0.98));
            Assert.That(ModMultiplier.For("MU", null), Is.EqualTo(1.0));
            Assert.That(ModMultiplier.For("RX", null), Is.EqualTo(0.1));

            // Rate mods ride the curve; no rate submitted means the client default.
            Assert.That(ModMultiplier.For("DT", 2.00), Is.EqualTo(1.46).Within(1e-12));
            Assert.That(ModMultiplier.For("dt", null), Is.EqualTo(1.23).Within(1e-12), "absent rate = the 1.50x default");
            Assert.That(ModMultiplier.For("NC", 1.01), Is.EqualTo(1.0046).Within(1e-12));
            Assert.That(ModMultiplier.For("HT", null), Is.EqualTo(0.25).Within(1e-12), "absent rate = the 0.75x default");
            Assert.That(ModMultiplier.For("HT", 0.50), Is.EqualTo(0.10).Within(1e-12));
            Assert.That(ModMultiplier.For("HT", 0.90), Is.EqualTo(0.70).Within(1e-12));

            // Ramps: rates are not persisted, so they are priced at the most any ramp could pay.
            Assert.That(ModMultiplier.For("WU", null), Is.EqualTo(1.46).Within(1e-12));
            Assert.That(ModMultiplier.For("WD", null), Is.EqualTo(1.46).Within(1e-12));

            // A mod the server has never heard of keeps the old flat allowance.
            Assert.That(ModMultiplier.For("ZZ", null), Is.EqualTo(ModMultiplier.UNKNOWN_MOD_MULTIPLIER));
        });
    }

    /// <summary>
    /// A STORED RHYTHMIC PLAY IS STILL BOUNDED AT THE PRICE IT WAS SUBMITTED AT. Backlog 147 removed
    /// the mod from the client, so no new play can carry RH, but the acronym shipped and the rows
    /// that carry it are re-priced by every path that re-derives a stored score. This table is what
    /// says how much of a play's base score its mods may buy, so if RH stopped being priced here the
    /// row's own submitted total would sit 10% ABOVE its ceiling, be clamped, and be stored
    /// UNRANKED, which is a live board losing a legitimate score rather than a cosmetic slip.
    ///
    /// <para>Stated on a whole STACK as well as on the acronym, because the ceiling is a product and
    /// the two paths are separate code: a stack that quietly lost one factor still returns a
    /// plausible number.</para>
    /// </summary>
    [Test]
    public void ModMultiplier_StillBoundsAStoredRhythmicPlay()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModMultiplier.For("RH", null), Is.EqualTo(1.10),
                "an unpriced RH would fall to a different allowance and unrank the row");
            Assert.That(ModMultiplier.For("RH", null), Is.Not.EqualTo(ModMultiplier.UNKNOWN_MOD_MULTIPLIER));

            // A whole stored stack: RH alongside the mods it could be selected with.
            Assert.That(ModMultiplier.MaxForStack([("RH", null)]), Is.EqualTo(1.10).Within(1e-12));
            Assert.That(ModMultiplier.MaxForStack([("RH", null), ("FL", null)]), Is.EqualTo(1.10 * 1.05).Within(1e-12));

            // ...and a no-mod ceiling would be 10% short of what an RH row actually submitted, which
            // is the exact gap that clamps the total and clears the ranked flag.
            Assert.That(ModMultiplier.MaxForStack([("RH", null)]), Is.GreaterThan(ModMultiplier.MaxForStack([])));
        });
    }

    /// <summary>
    /// EASY MUST BE PRICED HERE, and the danger runs the OPPOSITE way to the Flashlight and Half
    /// Time trims: an acronym this table does not know is allowed
    /// <see cref="ModMultiplier.UNKNOWN_MOD_MULTIPLIER"/>, i.e. a 2.0x ceiling, so an unlisted EZ
    /// would never unrank an honest play (its honest total is HALF its base). It would instead leave
    /// the ceiling four times the honest total, which is exactly the laundering slot the table
    /// exists to close: attach EZ, submit four times what the play earned, stay in bounds.
    /// </summary>
    [Test]
    public void ModMultiplier_ClosesTheEasyLaunderingSlot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModMultiplier.For("EZ", null), Is.Not.EqualTo(ModMultiplier.UNKNOWN_MOD_MULTIPLIER));
            Assert.That(ModMultiplier.MaxForStack([("EZ", null)]), Is.EqualTo(0.5).Within(1e-12));

            // A 1,000,000-base play with Easy on it may submit 500,000 and no more; the unknown-mod
            // allowance would have let 2,000,000 through.
            Assert.That(ModMultiplier.TotalScoreCeiling(1_000_000, ModMultiplier.MaxForStack([("EZ", null)])),
                Is.EqualTo(500_001));
            Assert.That(ModMultiplier.TotalScoreCeiling(1_000_000, ModMultiplier.UNKNOWN_MOD_MULTIPLIER),
                Is.EqualTo(2_000_001));

            // It trims a stack rather than fattening one, so it can never lift the ceiling.
            Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("EZ", null)]),
                Is.LessThan(ModMultiplier.MaxForStack([("DT", 2.00)])));
        });
    }

    [Test]
    public void MaxForStack_PinsTheFattestRankedStack()
    {
        // DT@2.00 (1.46) × FL (1.05) × LT (1.05) × HR (1.10) = 1.770615, the dearest stack a current
        // client can assemble out of ranked mods. Everything else ranked is a trim (NF 0.5, FT 0.98)
        // or neutral, and Easy is excluded by Hard Rock. Hard Rock (backlog 150) inherited this slot
        // from Rhythmic, which paid the same 1.10 and can no longer be selected at all.
        //
        // THIS PRODUCT IS WHY HARD ROCK'S SCORE MULTIPLIER IS 1.10 AND NOT THE 1.25 IT IS WORTH FOR
        // pp: at 1.25 the same stack is 2.0121, over STACK_CAP, so the ceiling would clamp an honest
        // maximal play and store it unranked.
        double fattest = ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null)]);

        Assert.That(fattest, Is.EqualTo(1.770615).Within(1e-9));
        Assert.That(fattest, Is.LessThan(ModMultiplier.STACK_CAP), "the backstop must never bite a reachable stack");
        double atThePpValue = ModMultiplier.For("DT", 2.00) * ModMultiplier.For("FL", null) * ModMultiplier.For("LT", null) * 1.25;

        Assert.That(atThePpValue, Is.GreaterThan(ModMultiplier.STACK_CAP),
            "pricing HR at its pp value would put the fattest honest stack over the backstop");

        // A stored row could still carry Rhythmic alongside all of it (no client can send that pair
        // today, but the ceiling is re-derived for stored rows too), and even that stays under.
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null), ("RH", null)]),
            Is.EqualTo(1.9476765).Within(1e-9));
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null), ("RH", null)]),
            Is.LessThan(ModMultiplier.STACK_CAP));

        // Adding the neutral / trimming ranked mods cannot beat it.
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null), ("SD", null), ("MU", null), ("GK", null)]),
            Is.EqualTo(fattest).Within(1e-9),
            "a 1.0x mod cannot move the ceiling, which is why adding Gatekeeper reprices nothing");
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null), ("FT", null)]),
            Is.LessThan(fattest));
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null), ("HR", null), ("NF", null)]),
            Is.LessThan(fattest));
    }

    /// <summary>
    /// HARD ROCK MUST BE PRICED HERE TOO, and the direction is the same as Easy's rather than the
    /// Flashlight / Half Time one, even though the multiplier is above 1.0: an unlisted acronym is
    /// allowed <see cref="ModMultiplier.UNKNOWN_MOD_MULTIPLIER"/>, which is a CEILING of 2.0, and
    /// 2.0 is far ABOVE the 1.10 an HR play can justify. So leaving it out could never unrank an
    /// honest play; it would leave a laundering slot 1.8 times wider than the mod earns. Listing it
    /// only ever tightens.
    /// </summary>
    [Test]
    public void ModMultiplier_ClosesTheHardRockLaunderingSlot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModMultiplier.For("HR", null), Is.Not.EqualTo(ModMultiplier.UNKNOWN_MOD_MULTIPLIER));
            Assert.That(ModMultiplier.For("HR", null), Is.LessThan(ModMultiplier.UNKNOWN_MOD_MULTIPLIER),
                "listing Hard Rock must tighten the ceiling, never loosen it");

            // A 1,000,000-base play with Hard Rock on it may submit 1,100,000 and no more; the
            // unknown-mod allowance would have let 2,000,000 through.
            Assert.That(ModMultiplier.TotalScoreCeiling(1_000_000, ModMultiplier.MaxForStack([("HR", null)])),
                Is.EqualTo(1_100_001));
            Assert.That(ModMultiplier.TotalScoreCeiling(1_000_000, ModMultiplier.UNKNOWN_MOD_MULTIPLIER),
                Is.EqualTo(2_000_001));
        });
    }

    [Test]
    public void MaxForStack_NoMods_IsOne_AndBlanksAreIgnored()
    {
        Assert.That(ModMultiplier.MaxForStack([]), Is.EqualTo(1.0));
        Assert.That(ModMultiplier.MaxForStack([(null, null), ("  ", 1.5)]), Is.EqualTo(1.0));
    }

    [Test]
    public void MaxForStack_IsTamperProof()
    {
        // Duplicated acronyms collapse (the client keys mods by type, so a stack cannot hold two
        // Double Times); the dearest instance wins so an honest stack is never under-priced.
        Assert.That(ModMultiplier.MaxForStack([("DT", 1.01), ("DT", 2.00)]), Is.EqualTo(1.46).Within(1e-12));

        // Mutually exclusive rate mods stacked together, and long piles of unknown acronyms, are
        // both held under the absolute backstop (the pre-task-27 flat allowance).
        Assert.That(ModMultiplier.MaxForStack([("DT", 2.00), ("NC", 2.00)]), Is.EqualTo(ModMultiplier.STACK_CAP));
        Assert.That(ModMultiplier.MaxForStack([("ZZ", null), ("YY", null), ("XX", null)]), Is.EqualTo(ModMultiplier.STACK_CAP));

        // An unknown mod alongside a known trim is still tightened by the known part.
        Assert.That(ModMultiplier.MaxForStack([("HT", 0.75), ("ZZ", null)]), Is.EqualTo(0.5).Within(1e-9));
    }

    // ---- the per-score ceiling ----

    [Test]
    public void TotalScoreCeiling_IsTheClientsOwnProductPlusRoundingSlack()
    {
        // The client sends round(base × multiplier), so the ceiling sits one unit above it and
        // nowhere near the old flat base × 2.
        Assert.Multiple(() =>
        {
            // No mods: a play cannot beat its own base score.
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, 1.0), Is.EqualTo(400_001));

            // DT at 1.01x: 400000 × 1.0046 = 401840.
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, ModMultiplier.For("DT", 1.01)), Is.EqualTo(401_841));

            // The fattest ranked stack on a perfect 1,000,000 base: 1,000,000 × 1.60965 plus slack.
            Assert.That(ModMultiplier.TotalScoreCeiling(1_000_000, ModMultiplier.MaxForStack([("DT", 2.00), ("FL", null), ("LT", null)])),
                Is.EqualTo(1_609_651));

            // Half Time trims, and after the nerf it trims hard: 400,000 × 0.25 = 100,000 at the
            // default rate (it was 220,000 under the 0.55 curve). This is the bound that makes an
            // un-updated client's 0.55-scaled submission land out of bounds and store unranked.
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, ModMultiplier.For("HT", 0.75)), Is.EqualTo(100_001));

            // The floor now bites from 0.70x down: 400,000 × 0.10 either way.
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, ModMultiplier.For("HT", 0.70)), Is.EqualTo(40_001));
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, ModMultiplier.For("HT", 0.50)), Is.EqualTo(40_001));

            // Half Time stacked with the ranked boosters is still a net trim: 0.25 × 1.05 × 1.05.
            Assert.That(ModMultiplier.TotalScoreCeiling(400_000, ModMultiplier.MaxForStack([("HT", 0.75), ("FL", null), ("LT", null)])),
                Is.EqualTo(110_251));

            // A zero or negative base justifies nothing.
            Assert.That(ModMultiplier.TotalScoreCeiling(0, 1.46), Is.EqualTo(0));
            Assert.That(ModMultiplier.TotalScoreCeiling(-5, 1.46), Is.EqualTo(0));
        });
    }

    [Test]
    public void TotalScoreCeiling_CoversEveryRateTheClientCouldRound()
    {
        // For every reachable DT/HT rate and a spread of bases, the value the client would actually
        // submit, round(base × multiplier), must be inside the ceiling: the tolerance exists to
        // absorb last-ulp differences, not to reject honest plays.
        long[] bases = [1, 999, 400_000, 646_853, 1_000_000];

        Assert.Multiple(() =>
        {
            for (int step = 50; step <= 200; step++)
            {
                double rate = step / 100.0;

                if (rate is > 0.99 and < 1.01)
                    continue; // no rate mod reaches exactly 1.00x

                string acronym = rate < 1 ? "HT" : "DT";
                double multiplier = ModMultiplier.For(acronym, rate);

                foreach (long baseScore in bases)
                {
                    long clientTotal = (long)Math.Round(baseScore * multiplier);

                    Assert.That(ModMultiplier.TotalScoreCeiling(baseScore, multiplier), Is.GreaterThanOrEqualTo(clientTotal),
                        $"{acronym} at {rate} on base {baseScore}");
                }
            }
        });
    }
}
