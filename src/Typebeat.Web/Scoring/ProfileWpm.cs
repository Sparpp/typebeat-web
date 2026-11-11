namespace Typebeat.Web.Scoring;

/// <summary>
/// The WPM on a profile's stats card: what pace this player has shown they can type at, read off
/// their top plays (the profile's own Best performance list, <see cref="Count"/> of them).
///
/// <para>
/// WHICH PACE OF A MAP. Neither of the two obvious ones says what a play proves. A map's AVERAGE
/// (<c>beatmaps.wpm</c>) understates a bursty map: a song that idles through its verses and then
/// races through its chorus averages slow, yet clearing it took the chorus's speed. Its PEAK
/// (<c>beatmaps.peak_wpm</c>, a 30-cell window) overstates every map: a few seconds of burst is a
/// speed a player can reach without being able to hold it. The figure in between already exists:
/// the map's TARGET pace (<c>beatmaps.target_wpm</c>, LyricPace's hardest 30-second window), the
/// speed the map asks to be SUSTAINED, which is a capability rather than a moment. A map whose
/// target has not been computed yet falls back to its average.
/// </para>
///
/// <para>
/// WHAT A PLAY PROVES OF THAT PACE. A play at a rate mod typed the map at that rate, so the pace
/// scales with it (read as the play-time gate reads a stack, <see cref="RateMods.EffectiveRate"/>:
/// the slowest rate in it, the reading that concedes the least). And a play that hit 85% of a pace
/// did not type that pace: it is weighted by its accuracy, the convention typing tests use for an
/// adjusted WPM. Each play is one map (the Best list folds a set to its best play), so a player
/// cannot raise the figure by playing one fast map ten times.
/// </para>
///
/// <para>
/// THE FIGURE is the plain mean of those plays, so one outlier among ten moves it a tenth of its
/// distance. A player with fewer plays averages what they have; one with none shows no figure.
/// </para>
/// </summary>
public static class ProfileWpm
{
    /// <summary>How many of the player's top plays the figure averages.</summary>
    public const int Count = 10;

    /// <summary>One of the player's top plays, as the figure reads it.</summary>
    /// <param name="MapWpm">The map's target pace, else its average; null when the map has neither.</param>
    /// <param name="Accuracy">The play's accuracy, 0 to 1.</param>
    /// <param name="ModsJson">The play's stored mods.</param>
    public readonly record struct Play(double? MapWpm, double Accuracy, string? ModsJson);

    /// <summary>The pace one play proves: the map's pace at the play's rate, weighted by its accuracy.</summary>
    public static double? Of(Play play)
    {
        if (play.MapWpm is not double wpm || !double.IsFinite(wpm) || wpm <= 0)
            return null;

        double rate = RateMods.EffectiveRate(GateRefund.ParseMods(play.ModsJson).Select(m => (m.Acronym, m.SpeedChange)));

        return wpm * rate * Math.Clamp(play.Accuracy, 0, 1);
    }

    /// <summary>The mean over the plays that have a pace at all, or null when none does.</summary>
    public static double? Average(IEnumerable<Play> plays)
    {
        var paces = plays.Select(Of).OfType<double>().ToList();

        return paces.Count > 0 ? paces.Average() : null;
    }
}
