namespace Typebeat.Web.Scoring;

/// <summary>
/// Verbatim server port of the game's
/// <c>typebeat.Game.Rulesets.TypeBeat.Scoring.TypeBeatRateMultiplier</c>: the score multiplier
/// awarded for playing at a track rate other than 1.0x (Half Time, Double Time, Nightcore, and the
/// Wind Up / Wind Down ramps).
///
/// <para>
/// type!beat ranks rate mods at EVERY speed, not just at their default, so the multiplier has to be
/// a real function of the rate rather than a flat per-mod constant: two players who both wear "DT"
/// must not be paid the same when one ran 1.01x and the other 2.00x. The function is:
/// </para>
///
/// <code>
/// r = round(rate, 2)                                   // the sliders step by 0.01
/// raw = r >= 1 ? 1 + 0.46 * (r - 1)                    // speeding up
///              : 1 - 1.80 * (1 - r)                    // slowing down
/// multiplier = round(max(0.10, raw), 4)
/// </code>
///
/// <para>
/// Two rounding steps with fixed decimal counts and <see cref="MidpointRounding.AwayFromZero"/> on
/// both, so this independent implementation lands on the same double as the client's and the
/// contract can be stated as exact decimal values rather than "within some epsilon". The pinned
/// values (see <c>RateMultiplierTest</c>) are 0.50 → 0.1000, 0.75 → 0.5500, 0.99 → 0.9820,
/// 1.00 → 1.0000, 1.01 → 1.0046, 1.50 → 1.2300, 2.00 → 1.4600.
/// </para>
///
/// <para>
/// The slopes are fixed by the two defaults, not by taste: 0.46 is (1.23 - 1) / (1.50 - 1) and 1.80
/// is (1 - 0.55) / (1 - 0.75), so default Double Time / Nightcore still pays 1.23x and default Half
/// Time still pays 0.55x. No existing default-speed score is re-based by the change.
/// </para>
///
/// <para>
/// KEEP IN SYNC with the game file; it is the authority. Any edit there needs the same edit here or
/// browser-side score bounds diverge from what the client actually submits.
/// </para>
/// </summary>
public static class RateMultiplier
{
    /// <summary>Multiplier gained per +1.0x of rate above 1.0x. Fixed by the 1.50x → 1.23x anchor.</summary>
    public const double INCREASE_SLOPE = 0.46;

    /// <summary>Multiplier lost per -1.0x of rate below 1.0x. Fixed by the 0.75x → 0.55x anchor.</summary>
    public const double DECREASE_SLOPE = 1.8;

    /// <summary>Floor on the returned multiplier (the reachable rate floor, 0.50x, lands exactly on it).</summary>
    public const double MINIMUM = 0.1;

    /// <summary>Decimal places the rate is snapped to before use (the slider's Precision).</summary>
    public const int RATE_DECIMALS = 2;

    /// <summary>Decimal places the returned multiplier is snapped to.</summary>
    public const int MULTIPLIER_DECIMALS = 4;

    /// <summary>The multiplier for a given track rate. 1.0 in, 1.0 out.</summary>
    public static double For(double rate)
    {
        double snapped = Math.Round(rate, RATE_DECIMALS, MidpointRounding.AwayFromZero);

        double raw = snapped >= 1
            ? 1 + INCREASE_SLOPE * (snapped - 1)
            : 1 - DECREASE_SLOPE * (1 - snapped);

        return Math.Round(Math.Max(MINIMUM, raw), MULTIPLIER_DECIMALS, MidpointRounding.AwayFromZero);
    }
}
