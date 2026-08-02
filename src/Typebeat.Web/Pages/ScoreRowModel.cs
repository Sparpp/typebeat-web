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

    public string GradeLabel => Rank switch
    {
        "X" or "XH" => "SS",
        "SH" => "S",
        _ => Rank,
    };

    public string GradeClass => "grade--" + GradeLabel.ToLowerInvariant();

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
