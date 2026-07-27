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
/// Fail toward the OLD behaviour: a map whose skippable time could not be computed (blob missing,
/// unparseable, a row the backfill has not reached yet) carries <c>skippable_s = 0</c>, which makes
/// the requirement exactly the pre-task-47 one. A gate that cannot compute the allowance must not
/// become more lenient than it was.
/// </para>
/// </summary>
public static class PlayTimeGate
{
    /// <summary>Fraction of the skip-adjusted drain length a play must have taken.</summary>
    public const double MINIMUM_FRACTION = 0.9;

    /// <summary>
    /// Seconds a submitted play must have taken to be rankable on this map. Negative or oversized
    /// inputs cannot make the bound negative or exceed the un-adjusted one.
    /// </summary>
    public static double RequiredSeconds(double drainLengthS, double skippableS)
    {
        double drain = Math.Max(0, drainLengthS);
        double skippable = Math.Clamp(skippableS, 0, drain);

        return MINIMUM_FRACTION * (drain - skippable);
    }

    /// <summary>Whether a play that took <paramref name="elapsedSeconds"/> clears the gate.</summary>
    public static bool Passes(double elapsedSeconds, double drainLengthS, double skippableS)
        => elapsedSeconds >= RequiredSeconds(drainLengthS, skippableS);
}
