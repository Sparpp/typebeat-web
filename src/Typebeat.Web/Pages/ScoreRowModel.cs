using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Pages;

/// <summary>
/// View model for the shared score-row partial (Pages/Shared/_ScoreRow.cshtml): one full-width
/// list item: map thumb, "{title} [{artist}]" set link, grade, accuracy, mods, score, date,
/// used by the profile page's Pinned/Best/First places/Recent score sections.
/// </summary>
/// <param name="CoverUrl">Site-relative list-cover URL, or null → gradient placeholder.</param>
/// <param name="Rank">Raw ScoreRank string from scores.rank (X/XH read as SS, like the game).</param>
/// <param name="Completion">% of the map typed (0..1), the metric the grade is awarded on.</param>
/// <param name="Date">ended_at; timestamptz arrives from Npgsql as UTC DateTime.</param>
/// <param name="Explicit">Creator-declared explicit content: renders the EXPLICIT badge beside
/// the set title, the same marker the set page and the set cards show.</param>
/// <param name="ScoreId">The score's own id; the replay download link is built from it.</param>
/// <param name="HasReplay">A replay was uploaded for this score (scores.replay_key IS NOT NULL),
/// so the row can offer the .osr download at /api/v2/scores/{ScoreId}/replay.</param>
public sealed record ScoreRowModel(
    long ScoreId,
    bool HasReplay,
    long SetId,
    string Title,
    string Artist,
    string? TitleUnicode,
    string? ArtistUnicode,
    bool Explicit,
    string? Version,
    string? CoverUrl,
    string Rank,
    double Completion,
    double Accuracy,
    long TotalScore,
    DateTime Date,
    string ModsJson)
{
    /// <summary>
    /// Render the pin/unpin control on this row (the profile does, for the signed-in owner of the
    /// score). Set in C# AFTER the query, never mapped: this and <see cref="IsPinned"/> are plain
    /// properties rather than constructor parameters precisely so they stay out of the positional
    /// Dapper mapping every score-row SELECT relies on. Do not alias a column to either name.
    /// </summary>
    public bool ShowPinControl { get; set; }

    /// <summary>This score is currently pinned, so the control reads "unpin".</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// How many times other players watched this score's replay (<c>scores.replay_views</c>,
    /// 025_replay_views.sql). Zero renders nothing, so a section that does not care about the
    /// number simply never shows one; the profile's "Most viewed replays" section sets it from its
    /// own ranking query.
    ///
    /// <para>
    /// Set in C# AFTER the query, never mapped, for the same reason <see cref="ShowPinControl"/>
    /// is: the score-row SELECT is materialized through this record's constructor, and Dapper
    /// requires a constructor matching the WHOLE column list, so an extra column would break every
    /// row rather than land here. Do not alias a column to this name either.
    /// </para>
    /// </summary>
    public long ReplayViews { get; set; }

    /// <summary>
    /// This score's stored performance points (<c>scores.pp</c>, docs/pp.md), or null to render no
    /// pp element at all. Only the profile's Best scores section sets this today; every other
    /// section (Pinned, First places, Recent, Most viewed replays) leaves it null on purpose, since
    /// pp is not yet a thing those sections surface. 0 is a legitimate, shown value (a custom-rate
    /// or not-yet-priced play), it is only null that means "opt out".
    ///
    /// <para>
    /// Set in C# AFTER the query, never mapped, for the same reason <see cref="ShowPinControl"/> and
    /// <see cref="ReplayViews"/> are: the score-row SELECT hydrates this record positionally through
    /// Dapper, so appending a column here would break materialization for every section that shares
    /// the select. The profile page instead runs a second small query keyed by score id over just
    /// the rows that opt in, and merges the values onto the already-hydrated rows. Do not alias a
    /// column to this name either.
    /// </para>
    /// </summary>
    public double? Pp { get; set; }

    public string GradeLabel => GradeDisplay.Label(Rank);

    public string GradeClass => GradeDisplay.CssClass(Rank);

    /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

    /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);

    /// <summary>
    /// Mod badges from the mods jsonb ([{acronym, settings}] wire shape), each carrying the track
    /// rate when it is a rate mod (see <see cref="ScoreMods.Parse"/>).
    /// </summary>
    public IReadOnlyList<ScoreMod> Mods => mods ??= ScoreMods.Parse(ModsJson);

    private IReadOnlyList<ScoreMod>? mods;
}

/// <summary>
/// Grade wording shared by every surface that shows one (the score-row partial, the set page's
/// leaderboard and podium, the /rankings top-plays board), so no two of them label the same stored
/// rank differently.
///
/// <para>
/// The stored value is the client's own ScoreRank string. The hidden-mod variants XH and SH are
/// silver-grade skins of X and S, not separate grades, so they READ as SS and S exactly like the
/// game shows them; anything else renders as itself (A/B/C/D, and F for a fail).
/// </para>
/// </summary>
public static class GradeDisplay
{
    public static string Label(string rank) => rank switch
    {
        "X" or "XH" => "SS",
        "SH" => "S",
        _ => rank,
    };

    /// <summary>The grade's colour class (site.css <c>.grade--ss</c> … <c>.grade--f</c>).</summary>
    public static string CssClass(string rank) => "grade--" + Label(rank).ToLowerInvariant();
}

/// <summary>
/// How a stored <c>statistics</c> jsonb becomes the JUDGEMENT COLUMNS a page shows, shared by every
/// surface that renders one (the set page's leaderboard and podium, the /rankings top-plays board),
/// so no two of them account for the same row differently.
///
/// <para><b>THE MISS COLUMN COUNTS UNCORRECTED TYPOS</b> (backlog 213). A cell left holding a wrong
/// character is stored under its own key, <c>good</c> (the game's
/// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>), and since backlog 140 that key has had no column of
/// its own: the typo number players are shown is <c>combo_break</c>, which counts wrong KEYPRESSES.
/// Between the two, a character the player never typed right appeared in NO column at all, which is
/// the field report that ended the split: a row reading MISS 0 while carrying <c>good: 2</c>. So the
/// MISS column is <c>miss + good</c>, the typo keeps no column, and the shown columns
/// (great + ok + meh + MISS) sum to the judged cell count again.</para>
///
/// <para>THE FOLD IS ON THE READING, NOT ON THE WIRE, so it reaches old rows and new ones alike with
/// no migration: nothing about what is stored moved. This is the site's half of the same fold the
/// game does through <c>TypeBeatRuleset.GetDisplayResultFor</c>, the server does in
/// <see cref="Scoring.ScoringContract"/>'s accuracy weight, and pp does in
/// <c>PerformancePoints.CountNotes</c>.</para>
///
/// <para>TYPOS ARE NOT FOLDED and are a different statement: that column counts wrong keypresses as
/// EVENTS, including the ones the player went back and fixed, where this one counts CELLS. pp does
/// subtract the uncorrected ones from its typo term (one flub, one term), but pp is pricing and this
/// is accounting: a player who mistyped nine characters and fixed five made nine mistakes.</para>
/// </summary>
public static class JudgementDisplay
{
    /// <summary>The statistics key an UNCORRECTED TYPO is stored under, folded into the miss column
    /// since backlog 213. Named here rather than inlined so this file and
    /// <c>ScoringContract.unfixed_typo_key</c> read as the same fact.</summary>
    private const string unfixed_typo_key = "good";

    private const string miss_key = "miss";

    /// <summary>
    /// The MISS column for one row: dropped cells plus cells left holding a wrong character, or
    /// null for a play with neither.
    ///
    /// <para>NULL AND NOT ZERO, which is the rule the column has had since backlog 140: a clean run
    /// renders a blank cell rather than a "0" next to the equally blank typo cell, because a "0
    /// misses" beside an empty typo column read as two different kinds of nothing. Negative counts
    /// cannot arrive from the submission path (<see cref="Scoring.ScoringContract"/> refuses them),
    /// but a hand-edited row is clamped rather than shown as a negative miss count.</para>
    /// </summary>
    public static int? MissColumn(JObject statistics)
    {
        int misses = Math.Max(0, statistics.Value<int?>(miss_key) ?? 0)
                     + Math.Max(0, statistics.Value<int?>(unfixed_typo_key) ?? 0);

        return misses > 0 ? misses : null;
    }
}
