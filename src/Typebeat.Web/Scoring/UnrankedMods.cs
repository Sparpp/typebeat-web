namespace Typebeat.Web.Scoring;

/// <summary>
/// THE ONE LIST OF MODS THAT ARE UNRANKED AT ANY CONFIGURATION, mirroring the client's per-mod
/// <c>Mod.Ranked = false</c>. Every server-side consumer reads it from here.
///
/// <para>
/// IT EXISTS BECAUSE IT DRIFTED (backlog 270). The list lived twice: once in
/// <c>ScoreEndpoints</c>, which decides whether a NEW submission may rank, and once in
/// <see cref="GateRefund"/>, which decides whether an OLD row may be re-ranked. The second copy
/// claimed in its own summary to mirror the first and held only <c>{ RX, WU, WD }</c> while the
/// first had grown to <c>{ RX, WU, WD, CT, DX, PT }</c>, so a refund pass would happily re-rank a
/// Conductor, Dyslexia or Puppeteer play the submit path had refused. A comment saying "mirrors
/// X" is not a mirror; one declaration is.
/// </para>
///
/// <para>
/// NEW MEMBERS HAVE TO BE LISTED, because this is a DENY LIST: an acronym it does not name is not
/// unranked BY THIS LIST. An acronym <see cref="ModMultiplier"/> does not price is
/// unranked anyway (<see cref="ScoreSubmission.ModRanked"/>), but a new unranked mod that is given
/// a price there and forgotten here would store ranked and land on the shared boards. Why each of
/// the seven is here:
/// </para>
///
/// <list type="bullet">
/// <item><c>RX</c> (Mashing) and <c>WU</c> / <c>WD</c> (the time-ramp Wind Up / Wind Down): the
/// original three.</item>
/// <item><c>CT</c> (Conductor): the song meets the player, since the playback rate chases their
/// typing, so the judgement it earns is intentionally generous and no two plays are even on the
/// same map in time.</item>
/// <item><c>DX</c> (Dyslexia): the input model is relaxed, since the letters of a word may be
/// typed in any order, so a keystroke is matched against a SET rather than against the one
/// character the caret is on.</item>
/// <item><c>PT</c> (Puppeteer): the same shape of reason as Conductor, only stricter. It is a
/// clock-slaving follower where the song strictly follows the typing and timing judgement is
/// forgiven outright, so its plays must never rank.</item>
/// <item><c>SH</c> (Shorthand): only the first letter of each word and syllable is typed and the rest
/// completes itself. The game ships it as experimental and unranked.</item>
/// </list>
///
/// <para>
/// <c>RE</c> (Recite) is deliberately ABSENT: it hides untyped text and is a difficulty INCREASE
/// that ranks, the same shape as <c>FC</c> (Fletcher). Neither needs anything here, only a price
/// in <see cref="ModMultiplier"/> and in <see cref="PerformancePoints"/>.
/// </para>
///
/// <para>
/// THE RATE MODS ARE NOT HERE EITHER, at any speed. The client pays them on a continuous curve
/// (<see cref="RateMultiplier"/>), so an odd rate is priced rather than banned; only membership of
/// this set disqualifies a play.
/// </para>
/// </summary>
public static class UnrankedMods
{
    /// <summary>The acronyms themselves, compared case-insensitively as the wire spells them.</summary>
    public static readonly IReadOnlySet<string> ACRONYMS =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RX", "WU", "WD", "CT", "DX", "PT", "SH" };

    /// <summary>
    /// Whether this acronym is unranked at every configuration. A null, blank or unknown acronym
    /// is not, which is the deny-list reading and the reason the summary above insists a new mod
    /// be added here rather than assumed. Whether a submitted acronym RANKS is
    /// <see cref="ScoreSubmission.ModRanked"/>, which also refuses one the server does not know.
    /// </summary>
    public static bool IsAlwaysUnranked(string? acronym)
        => !string.IsNullOrWhiteSpace(acronym) && ACRONYMS.Contains(acronym.Trim());
}
