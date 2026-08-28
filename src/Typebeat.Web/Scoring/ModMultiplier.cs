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
/// Per-mod values, mirroring the calculator: Easy 0.5, No Fail 0.5, Hard Rock 1.10, Sudden Death 1.0
/// (absent), Gatekeeper 1.0 (absent), Flashlight 1.05, Literate 1.05, Recite 1.01, Rhythmic 1.10,
/// Fletcher 1.02 ("FC", the pinning mod) and the retired Fletcher 0.98 ("FT", the unpinning one),
/// Muted 1.0 (absent), Conductor 1.0 and Dyslexia 1.0 (both unranked, both 1.0x in the calculator),
/// Mashing 0.1 (unranked, still priced for display parity), and the rate mods on
/// the continuous <see cref="RateMultiplier"/> curve. The fattest RANKED stack a current client can
/// assemble is DT@2.00 (1.46) x FL (1.05) x LT (1.05) x HR (1.10) x FC (1.02) x RE (1.01) =
/// 1.824087573; without Recite it is 1.80602730 and without Fletcher too the 1.770615 that stack
/// paid before, since a 1.0x mod changes nothing.
/// </para>
///
/// <para>
/// RHYTHMIC IS THE ONE COMBINATION THAT NOW EXCEEDS THE CAP, and it is unreachable rather than a
/// problem: RH pays 1.10 and 1.824087573 x 1.10 = 2.006496..., just over <see cref="STACK_CAP"/>.
/// No row can carry both, because backlog 147 removed Rhythmic from the client long before Recite
/// (backlog 229) existed, so no client ever offered the pair. The pre-Recite six (RH alongside the
/// 1.80602730 stack) is still 1.986630..., under the cap, and is still pinned by a test. If a mod
/// above 1.0 is ever added to the reachable set, check this product before the arm, not after.
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
/// The same window applies to Flashlight after its 1.2x to 1.05x trim (task 36): an old client's
/// FL play prices at 1.2x, lands over the new ceiling and is stored unranked until that client
/// updates. Unlike Half Time those plays used to rank, so ship the client promptly after deploy.
/// </para>
///
/// <para>
/// THE HALF TIME NERF (task 44) reuses that same window mechanism, no new machinery. The rate
/// curve's decrease slope went 1.80 to 3.00, so every down-rate now prices strictly lower than it
/// used to (0.75x pays 0.25 where it paid 0.55). A client still running the old curve submits
/// round(base x 0.55), which is above <see cref="TotalScoreCeiling"/> for the 0.25 the server now
/// allows, so the submission is stored UNRANKED with the total clamped to the no-mod ceiling and
/// never reaches a board. Nothing is silently accepted at the old price. Ship the client promptly
/// after deploying this, exactly as for the Flashlight trim, because these plays used to rank.
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
    /// assemble comes near it (the fattest ranked one is 1.824087573, pinned by a test), so this only
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
            // Easy (backlog 149): doubled judgement windows, priced at osu's 0.5x for a difficulty
            // reduction, the same value No Fail carries. Listing it MATTERS even though an unlisted
            // acronym is priced generously rather than tightly: left out, every EZ play would be
            // allowed UNKNOWN_MOD_MULTIPLIER, i.e. a ceiling of 2.0x on a play whose honest total is
            // 0.5x its base, so a tampered submission could inflate an EZ play FOURFOLD and still
            // land in bounds. The direction of the danger is the opposite of the Flashlight and Half
            // Time trims above (those clamped honest plays out of bounds); here it is the ceiling
            // that would be too loose, not too tight, so no honest play is at risk either way.
            "EZ" => 0.5,
            // Hard Rock (backlog 150): HALVED judgement windows, the exact mirror of Easy. The
            // client's calculator prices it at 1.10, and the risk runs the OPPOSITE way to Easy's:
            // an unlisted acronym is allowed UNKNOWN_MOD_MULTIPLIER (2.0), which is ABOVE 1.10, so
            // leaving HR out would not clamp an honest play, it would leave a laundering slot nearly
            // twice as wide as the mod can justify. Listing it TIGHTENS the ceiling and can only
            // ever reject a submission an honest client could not have sent. It also has to fit
            // under STACK_CAP: DT@2.00 x FL x LT x HR = 1.770615, which is why the client prices the
            // mod at 1.10 rather than at the 1.25 it is worth for pp (that product would be 2.0121,
            // over the cap, and would unrank an honest maximal play).
            "HR" => 1.10,
            "SD" => 1.0,
            // Gatekeeper (backlog 107): the strict wrong-key model, which used to be the client's
            // default and is now a ranked mod. Listed at 1.0 rather than left to fall through to
            // UNKNOWN_MOD_MULTIPLIER, so a GK play is bounded exactly like the no-mod play it scores
            // as instead of being allowed a 2.0x ceiling it can never justify.
            "GK" => 1.0,
            // Trimmed from the old flashlight's 1.2x with the character-window rework (task 36);
            // must match the game calculator's value.
            "FL" => 1.05,
            "LT" => 1.05,
            // Recite (backlog 229): hides every character until it is typed. 1.01, and the value is
            // the game calculator's own (TypeBeatScoreMultiplierCalculator prices Recite at 1.01
            // deliberately below Flashlight's 1.05, which hides strictly less, because the handicap
            // is memory rather than reflexes and a rehearsed run must not be paid a premium it never
            // earned). Listing it TIGHTENS the ceiling exactly as "HR" and "FC" do: unlisted it
            // would be allowed UNKNOWN_MOD_MULTIPLIER (2.0x) on a play whose honest total is 1.01x
            // its base, a laundering slot nearly twice as wide as the mod can justify. Recite is a
            // RANKED mod, so the ranked gate needs nothing for it (that gate is the deny list in
            // ScoreEndpoints, and an acronym absent from it ranks); this is only the price.
            "RE" => 1.01,
            // Rhythmic (backlog 135): the millisecond judgement ladder, which was the tighter one
            // on any map slower than 10 characters per second.
            //
            // THE MOD IS GONE AND THIS LINE MUST NOT GO WITH IT (backlog 147). RH shipped, so rows
            // carrying it exist, and this table is what bounds a STORED row's total on every path
            // that re-derives one (the recalc tool prices the ceiling from the row's mods). Delete
            // the arm and RH falls to UNKNOWN_MOD_MULTIPLIER on the acronym path but to 1.0 in any
            // exact-stack pricing, which puts the row's own submitted total 10% over its ceiling:
            // the total is then clamped and the row stored UNRANKED. That failure mode has already
            // bitten twice, on Literate and on the supersede sweep. Nothing new can reach the arm,
            // because no mod the ruleset offers carries the acronym any more.
            "RH" => 1.10,
            // Fletcher, both halves of it (backlog 208 reversed the mod and gave the new one a new
            // acronym). "FC" is the live one: it PINS the caret to the playhead, taking back slack
            // the default now hands out at both ends of every line, and the client prices that at
            // 1.02x. "FT" is the RETIRED one, which unpinned the caret, and its 0.98x must not go
            // with it: rows carrying that acronym exist, were priced at 0.98x when they were
            // submitted, and this table is what bounds a stored row's total on every path that
            // re-derives one. Delete the arm and every FT row's own submitted total lands 2% over
            // the ceiling UNKNOWN_MOD_MULTIPLIER would then not even be consulted for, exactly the
            // failure mode "RH" above records.
            //
            // FC is a ranked CONVERSION mod, so it needs no entry in the ranked gate: that gate is
            // a deny list (ScoreEndpoints' always-unranked "RX"/"WU"/"WD"), and an acronym absent
            // from it ranks. Listing it here is about the CEILING: unlisted it would be allowed
            // UNKNOWN_MOD_MULTIPLIER (2.0x) on a play whose honest total is 1.02x its base, which
            // is a laundering slot nearly twice as wide as the mod can justify (the same argument
            // "HR" carries above). It also has to fit under STACK_CAP: DT@2.00 x FL x LT x HR x FC
            // = 1.770615 x 1.02 = 1.80602730, still clear of 2.0.
            "FC" => 1.02,
            "FT" => 0.98,
            "MU" => 1.0,
            // Conductor (backlog 226) and Dyslexia (backlog 231), both priced at exactly the 1.0
            // their game-side multiplier is. Conductor is not a ModRateAdjust and never touches the
            // score multiplier or the window scale (it steers the TRACK, chasing the player's pace),
            // and Dyslexia is simply unlisted in the game's calculator, which is 1.0x by
            // construction. Both are ALWAYS UNRANKED (they are in ScoreEndpoints' deny list), so a
            // ceiling here can never rescue or ruin a board placement; it is anti-laundering only,
            // and 1.0 is the honest bound. Left unlisted they would each be allowed
            // UNKNOWN_MOD_MULTIPLIER (2.0x), i.e. an unranked play could still be stored at double
            // the total it earned, which is the profile total and the play history lying rather than
            // a leaderboard doing it. Listing them can only ever tighten.
            "CT" => 1.0,
            "DX" => 1.0,
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
