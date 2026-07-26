namespace Typebeat.Web.Pages;

/// <summary>
/// View model for the shared score-row partial (Pages/Shared/_ScoreRow.cshtml): one full-width
/// list item: map thumb, "{title} [{artist}]" set link, grade, accuracy, mods, score, date,
/// used by the profile page's Best/Recent score sections.
/// </summary>
/// <param name="CoverUrl">Site-relative list-cover URL, or null → gradient placeholder.</param>
/// <param name="Rank">Raw ScoreRank string from scores.rank (X/XH read as SS, like the game).</param>
/// <param name="Completion">% of the map typed (0..1), the metric the grade is awarded on.</param>
/// <param name="Date">ended_at; timestamptz arrives from Npgsql as UTC DateTime.</param>
/// <param name="Explicit">Creator-declared explicit content: renders the EXPLICIT badge beside
/// the set title, the same marker the set page and the set cards show.</param>
public sealed record ScoreRowModel(
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
