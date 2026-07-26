namespace Typebeat.Web.Scoring;

/// <summary>
/// The server mirror of the client's <c>TypeBeatScoreMultiplierCalculator</c>: what a submitted mod
/// stack is allowed to multiply a play's base score by.
///
/// <para>
/// The client computes <c>TotalScore = round(TotalScoreWithoutMods * multiplier)</c>
/// (<c>ScoreProcessor.cs:400-401</c>) where the multiplier is the product of the per-mod values
/// below. <see cref="ScoringContract"/> already bounds <c>TotalScoreWithoutMods</c> against a
/// provable no-mod ceiling, so bounding the multiplier here pins the submitted total to within one
/// unit of the only value an honest client could have sent, instead of the old flat "some stack
/// under 2x" allowance.
/// </para>
///
/// <para>
/// Per-mod values, mirroring the calculator: No Fail 0.5, Sudden Death 1.0 (absent), Flashlight 1.2,
/// Literate 1.05, Fletcher 0.98, Muted 1.0 (absent), Mashing 0.1 (unranked, still priced for
/// display parity), and the rate mods on the continuous <see cref="RateMultiplier"/> curve. The
/// fattest RANKED stack is DT@2.00 (1.46) x FL (1.2) x LT (1.05) = 1.8396.
/// </para>
///
/// <para>
/// Two deliberate imprecisions, both conservative:
/// <list type="bullet">
/// <item>An acronym this table does not know is priced at <see cref="UNKNOWN_MOD_MULTIPLIER"/>, the
/// same flat allowance the endpoint used before this existed, so a mod shipped by a newer client
/// than the server still submits honestly instead of being clamped.</item>
/// <item>Wind Up / Wind Down are paid on a rate RAMP whose endpoints the server does not persist,
/// so they are priced at the most any ramp could pay, <c>For(2.00)</c>. They are unranked at every
/// configuration regardless.</item>
/// </list>
/// </para>
///
/// <para>
/// PRE-TASK-27 CLIENTS priced the rate mods on osu's bucketed V2 curve. Double Time's old value was
/// always at or below the new one (it floored the rate to 0.1 first), so an old DT play stays in
/// bounds. Half Time's old value was HIGHER than the new one below 0.75x (0.2 vs 0.1 at 0.50x), so
/// an old client's non-default Half Time play lands out of bounds and is stored unranked with a
/// clamped total. That play was ALREADY unranked under the old server rule, so nothing that counts
/// is lost; it only matters in the window between deploying this and reshipping the client.
/// </para>
/// </summary>
public static class ModMultiplier
{
    /// <summary>
    /// What an acronym the server does not know is allowed to multiply by. This is the flat
    /// pre-task-27 allowance kept as a forward-compatibility escape hatch: a client that ships a new
    /// mod before the server learns its multiplier still gets its scores through.
    /// </summary>
    public const double UNKNOWN_MOD_MULTIPLIER = 2.0;

    /// <summary>
    /// Absolute backstop on a whole stack, whatever it contains. No stack the client can actually
    /// assemble comes near it (the fattest ranked one is 1.8396, pinned by a test), so this only
    /// ever bites tamper-shaped input such as DT and NC submitted together, which the client makes
    /// mutually exclusive. Raise it only if a genuinely reachable stack ever exceeds it.
    /// </summary>
    public const double STACK_CAP = 2.0;

    /// <summary>
    /// Relative slack on the ceiling, absorbing the last-ulp difference between the client's product
    /// order (a hash-set iteration, so not deterministic) and ours. At a 1,000,000 base this is
    /// 0.001 of a score point; the +1 in <see cref="TotalScoreCeiling"/> covers the client's
    /// <c>Math.Round</c> of the same product.
    /// </summary>
    private const double tolerance = 1e-9;

    /// <summary>
    /// The multiplier a single mod contributes. <paramref name="speedChange"/> is the submitted rate
    /// for a rate mod (already snapped and clamped, see <see cref="RateMods.ReadSpeedChange"/>);
    /// null means the client sent none, which is read as its default (the only rate a pre-task-27
    /// client omitted the key at).
    /// </summary>
    public static double For(string? acronym, double? speedChange)
    {
        string key = (acronym ?? string.Empty).Trim().ToUpperInvariant();

        if (RateMods.TryGetRange(key, out var range))
            return RateMultiplier.For(speedChange ?? range.Default);

        return key switch
        {
            "NF" => 0.5,
            "SD" => 1.0,
            "FL" => 1.2,
            "LT" => 1.05,
            "FT" => 0.98,
            "MU" => 1.0,
            "RX" => 0.1,
            // Rate ramps: the endpoints are not persisted, so price them at the most any ramp could
            // pay. 0.8·For(min) + 0.2·For(max) <= For(max) <= For(2.00), the curve being monotonic.
            "WU" or "WD" => RateMultiplier.For(2.00),
            _ => UNKNOWN_MOD_MULTIPLIER,
        };
    }

    /// <summary>
    /// The largest multiplier a submitted mod stack could justify. Duplicated acronyms collapse to
    /// their dearest instance (the client keys its mods by type, so a stack cannot really contain
    /// two Double Times), and the whole product is held under <see cref="STACK_CAP"/>.
    /// </summary>
    public static double MaxForStack(IEnumerable<(string? Acronym, double? SpeedChange)> mods)
    {
        var dearestByAcronym = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (acronym, speedChange) in mods)
        {
            if (string.IsNullOrWhiteSpace(acronym))
                continue;

            string key = acronym.Trim().ToUpperInvariant();
            double multiplier = For(key, speedChange);

            if (!dearestByAcronym.TryGetValue(key, out double existing) || multiplier > existing)
                dearestByAcronym[key] = multiplier;
        }

        double product = 1;

        foreach (double multiplier in dearestByAcronym.Values)
            product *= multiplier;

        return Math.Min(product, STACK_CAP);
    }

    /// <summary>
    /// The highest total score a play with this base and this mod multiplier could have submitted.
    /// The client sends <c>round(base * multiplier)</c>, so the bound is that value plus a unit of
    /// rounding slack; a non-positive base can justify nothing.
    /// </summary>
    public static long TotalScoreCeiling(long totalScoreWithoutMods, double multiplier)
    {
        if (totalScoreWithoutMods <= 0 || multiplier <= 0 || !double.IsFinite(multiplier))
            return 0;

        double exact = (double)totalScoreWithoutMods * multiplier * (1 + tolerance);

        if (exact >= long.MaxValue - 1)
            return long.MaxValue;

        return (long)Math.Floor(exact) + 1;
    }
}
