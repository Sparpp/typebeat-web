namespace Typebeat.Web.Scoring;

/// <summary>
/// The minimum-play-time anti-cheat gate, shared by both submission paths (the game client's
/// <c>ScoreEndpoints.SubmitScore</c> and the browser player's <c>PlayEndpoints.SubmitScoreAsync</c>).
///
/// <para>
/// A submitted play must have taken at least <see cref="MINIMUM_FRACTION"/> of the map's
/// SKIP-ADJUSTED drain length, measured from score-token creation (the server's own wall clock) to
/// the submission arriving. It is a LOWER bound and nothing more: elapsed also contains pause time,
/// menu time and network latency, all of which only ever inflate it, so the gate can catch "this
/// play cannot physically have happened" and never anything subtler. Failing it is not an error;
/// the score is stored, just not ranked.
/// </para>
///
/// <para>
/// The skip adjustment (backlog task 47) is what makes it honest. The game offers a skip button
/// over instrumental gaps of at least <see cref="Packages.Lyrics.InstrumentalGaps.MIN_GAP_MS"/>
/// (backlog task 5, added after this gate), so a legitimate play of a gap-heavy map finishes well
/// under the un-adjusted bound. Before the adjustment that unranked honest players outright: on
/// "Immortal Flame", whose two qualifying gaps make it impossible to reach the old 109.12 s
/// requirement at all, EVERY skip-using play was unranked. The requirement is therefore taken
/// against drain MINUS the time the skip button can legally remove
/// (<c>beatmaps.skippable_s</c>, written at ingest and by the pace backfill from the stored .osu).
/// </para>
///
/// <para>
/// The rate adjustment (backlog task 54) is the other half of the same honesty. Drain and the skip
/// allowance are MAP time; the gate measures REAL time, and a rate mod is exactly the conversion
/// between the two: a map played at 1.5x takes drain/1.5 real seconds. Without the division a
/// default Double Time play (drain/1.5 = 0.667 x drain of real time) could never reach 0.9 x drain,
/// so every DT play above roughly 1.111x was unranked outright, the same bug as the missing skip
/// allowance with a different cause. Down-rates were never wrongly caught: they take LONGER, which
/// the un-divided bound only ever under-demanded.
/// </para>
///
/// <para>
/// Fail toward the OLD behaviour: a map whose skippable time could not be computed (blob missing,
/// unparseable, a row the backfill has not reached yet) carries <c>skippable_s = 0</c>, which makes
/// the requirement exactly the pre-task-47 one, and a stack with no rate mod is rate 1.0, which
/// makes it exactly the pre-task-54 one. A gate that cannot compute an allowance must not become
/// more lenient than it was.
/// </para>
/// </summary>
public static class PlayTimeGate
{
    /// <summary>Fraction of the skip-adjusted drain length a play must have taken.</summary>
    public const double MINIMUM_FRACTION = 0.9;

    /// <summary>
    /// Seconds of REAL time a submitted play must have taken to be rankable on this map:
    /// <c>MINIMUM_FRACTION x (drain - skippable) / rate</c>. Skips remove MAP time, so they come off
    /// inside the bracket; the rate converts the whole of that map time into real time, so it
    /// divides the lot.
    ///
    /// <para>
    /// Every input is clamped before it is used, so no submitted number can make the bound negative,
    /// infinite or NaN: drain floors at 0, the allowance is held inside [0, drain], and the rate is
    /// held inside the sliders' own reachable span (<see cref="RateMods.SlowestRate"/> to
    /// <see cref="RateMods.FastestRate"/>), which is strictly positive, so the division is safe and
    /// the result is a finite value in [0, MINIMUM_FRACTION x drain / SlowestRate].
    /// </para>
    /// </summary>
    /// <param name="rate">
    /// The play's track rate: 1.0 for a stack with no rate mod (the browser player always), or the
    /// submitted speed_change of DT/NC/HT/DC (see <see cref="RateMods.EffectiveRate"/>). Below 1 it
    /// makes the requirement LONGER, which is correct: a Half Time play does take more real time.
    /// </param>
    public static double RequiredSeconds(double drainLengthS, double skippableS, double rate = 1.0)
    {
        double drain = Math.Max(0, drainLengthS);
        double skippable = Math.Clamp(skippableS, 0, drain);
        double safeRate = double.IsFinite(rate) ? Math.Clamp(rate, RateMods.SlowestRate, RateMods.FastestRate) : 1.0;

        return MINIMUM_FRACTION * (drain - skippable) / safeRate;
    }

    /// <summary>Whether a play that took <paramref name="elapsedSeconds"/> clears the gate.</summary>
    public static bool Passes(double elapsedSeconds, double drainLengthS, double skippableS, double rate = 1.0)
        => elapsedSeconds >= RequiredSeconds(drainLengthS, skippableS, rate);
}
