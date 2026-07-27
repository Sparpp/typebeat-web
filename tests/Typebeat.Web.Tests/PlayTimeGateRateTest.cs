using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The rate term of the minimum-play-time gate (task 54). Drain and the skip allowance are MAP
/// time, the gate measures REAL time, and the rate is the conversion: a map played at 1.5x is over
/// in drain / 1.5 real seconds. The bound is therefore
/// <c>0.9 x (drain - skippable) / rate</c>.
///
/// <para>
/// The numbers below are all taken against a 100 s map, so the pre-task-47 bound is 90 s and the
/// skip-adjusted one (40 s of allowance) is 54 s, matching the fixtures the other gate tests use.
/// </para>
/// </summary>
[TestFixture]
public class PlayTimeGateRateTest
{
    private const double drain_s = 100;
    private const double skippable_s = 40;

    [Test]
    public void WithoutARateMod_TheBoundIsExactlyWhatItWas()
    {
        Assert.Multiple(() =>
        {
            // Rate 1.0 is the identity, and it is also the default, so every pre-task-54 call site
            // and every stored no-mod score keeps the bound it already had.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 1.0), Is.EqualTo(90).Within(1e-9));
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0), Is.EqualTo(90).Within(1e-9));
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, skippable_s, 1.0), Is.EqualTo(54).Within(1e-9));
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, skippable_s), Is.EqualTo(54).Within(1e-9));
        });
    }

    [Test]
    public void AnUpRate_ShortensTheBound_ByExactlyThatFactor()
    {
        Assert.Multiple(() =>
        {
            // The bug this fixes: default Double Time.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 1.5), Is.EqualTo(60).Within(1e-9));
            Assert.That(PlayTimeGate.Passes(60, drain_s, 0, 1.5), Is.True, "the bound is inclusive");
            Assert.That(PlayTimeGate.Passes(59.9, drain_s, 0, 1.5), Is.False, "and it is still a bound");

            // The slowest up-rate the slider offers barely moves it, which is the point: the term
            // is continuous, not a DT-shaped exemption.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 1.01), Is.EqualTo(90 / 1.01).Within(1e-9));

            // The fastest one halves it.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 2.00), Is.EqualTo(45).Within(1e-9));
        });
    }

    [Test]
    public void SkipsAndRate_Compose()
    {
        // Skips remove MAP time, so they come off inside the bracket; the rate converts the whole
        // of what is left into real time, so it divides the lot: 0.9 x (100 - 40) / 1.5 = 36.
        Assert.Multiple(() =>
        {
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, skippable_s, 1.5), Is.EqualTo(36).Within(1e-9));
            Assert.That(PlayTimeGate.Passes(36, drain_s, skippable_s, 1.5), Is.True);
            Assert.That(PlayTimeGate.Passes(35.9, drain_s, skippable_s, 1.5), Is.False);

            // A player who skips both gaps at 1.5x really does spend (100 - 40) / 1.5 = 40 s.
            Assert.That(PlayTimeGate.Passes((drain_s - skippable_s) / 1.5, drain_s, skippable_s, 1.5), Is.True);
        });
    }

    [Test]
    public void ADownRate_LengthensTheBound()
    {
        Assert.Multiple(() =>
        {
            // Half Time takes MORE real time, so the requirement grows: 90 / 0.75 = 120 s. The
            // missing term never wronged these plays (the old bound asked less of them than it
            // should have), so this direction is a tightening, not a refund.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 0.75), Is.EqualTo(120).Within(1e-9));
            Assert.That(PlayTimeGate.Passes(95, drain_s, 0, 0.75), Is.False, "95 s cleared the old bound, not this one");
            Assert.That(PlayTimeGate.Passes(120, drain_s, 0, 0.75), Is.True);

            // The slider's floor is the longest bound reachable: 90 / 0.5 = 180 s.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 0.50), Is.EqualTo(180).Within(1e-9));
        });
    }

    [Test]
    public void TheOldGateBecameUnclearableAbove_TenNinths()
    {
        // A full, skipless play at rate r takes drain / r real seconds, which the rate-blind bound
        // (0.9 x drain) accepted only while r <= 1 / 0.9 = 1.111... Above that, no honest play
        // could clear the gate at all, which is precisely the bug.
        const double breaking_point = 1 / PlayTimeGate.MINIMUM_FRACTION;

        Assert.Multiple(() =>
        {
            Assert.That(breaking_point, Is.EqualTo(1.1111).Within(0.0001));

            // Just under it, a perfect play cleared even the rate-blind bound.
            Assert.That(PlayTimeGate.Passes(drain_s / 1.11, drain_s, 0, 1.0), Is.True);

            // Just over it, it did not, however honest.
            Assert.That(PlayTimeGate.Passes(drain_s / 1.12, drain_s, 0, 1.0), Is.False);

            // With the rate term both clear, and so does the default Double Time that could not.
            Assert.That(PlayTimeGate.Passes(drain_s / 1.12, drain_s, 0, 1.12), Is.True);
            Assert.That(PlayTimeGate.Passes(drain_s / 1.50, drain_s, 0, 1.50), Is.True);
            Assert.That(PlayTimeGate.Passes(drain_s / 1.50, drain_s, 0, 1.0), Is.False, "the bug, in one line");
        });
    }

    [Test]
    public void AHostileRate_CannotBreakTheBound()
    {
        Assert.Multiple(() =>
        {
            // Everything is clamped into the sliders' own reachable span before it is used, so the
            // requirement can never be driven to zero, negative, infinite or NaN.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 1e9), Is.EqualTo(45).Within(1e-9), "clamped to 2.00x");
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 0), Is.EqualTo(180).Within(1e-9), "clamped to 0.50x");
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, -5), Is.EqualTo(180).Within(1e-9), "a negative rate cannot flip it");
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, double.NaN), Is.EqualTo(90).Within(1e-9), "NaN falls back to 1.0x");
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, double.PositiveInfinity), Is.EqualTo(90).Within(1e-9));

            // And the composition stays sane at the extremes of every input at once.
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 500, 2.00), Is.EqualTo(0), "an oversized allowance still floors at 0");
            Assert.That(PlayTimeGate.RequiredSeconds(-10, -10, 2.00), Is.EqualTo(0));
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, 1e-300), Is.EqualTo(180).Within(1e-9), "no division blow-up");
        });
    }

    [Test]
    public void TheStackRate_IsReadTheWayTheStackIsPriced()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RateMods.EffectiveRate(Array.Empty<(string?, double?)>()), Is.EqualTo(1.0), "no mods");
            Assert.That(RateMods.EffectiveRate([("FL", null), ("LT", null)]), Is.EqualTo(1.0), "no RATE mods");
            Assert.That(RateMods.EffectiveRate([("DT", 1.5)]), Is.EqualTo(1.5));
            Assert.That(RateMods.EffectiveRate([("dt", 1.5)]), Is.EqualTo(1.5), "acronyms are case-insensitive");
            Assert.That(RateMods.EffectiveRate([("NC", 1.2), ("FL", null)]), Is.EqualTo(1.2));
            Assert.That(RateMods.EffectiveRate([("HT", 0.6)]), Is.EqualTo(0.6));

            // No speed_change means the client's default, the only rate a pre-task-27 client
            // omitted the key at.
            Assert.That(RateMods.EffectiveRate([("DT", null)]), Is.EqualTo(1.5));
            Assert.That(RateMods.EffectiveRate([("HT", null)]), Is.EqualTo(0.75));

            // The client makes the rate mods mutually exclusive, so a stack holding two of them is
            // tamper-shaped and is read at its SLOWEST member, the strictest bound available.
            Assert.That(RateMods.EffectiveRate([("DT", 2.0), ("HT", 0.5)]), Is.EqualTo(0.5));
        });
    }

    [Test]
    public void ATamperedSpeedChange_BuysTheSliderCeilingAndNoMore()
    {
        // RateMods clamps before anything reads it, so "speed_change": 40 buys the 2.00x bound
        // (45 s of a 100 s map), not a 2.25 s one.
        double rate = RateMods.EffectiveRate([("DT", RateMods.ReadSpeedChange("DT", new Dictionary<string, object> { ["speed_change"] = 40.0 }))]);

        Assert.Multiple(() =>
        {
            Assert.That(rate, Is.EqualTo(2.00));
            Assert.That(PlayTimeGate.RequiredSeconds(drain_s, 0, rate), Is.EqualTo(45).Within(1e-9));
            Assert.That(PlayTimeGate.Passes(30, drain_s, 0, rate), Is.False);
        });
    }
}
