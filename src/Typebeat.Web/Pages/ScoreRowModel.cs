using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Pages;

/// <summary>
/// View model for the shared score-row partial (Pages/Shared/_ScoreRow.cshtml): one full-width
/// list item — map thumb, "{title} [{artist}]" set link, grade, accuracy, mods, score, date —
/// used by the profile page's Best/Recent score sections.
/// </summary>
/// <param name="CoverUrl">Site-relative list-cover URL, or null → gradient placeholder.</param>
/// <param name="Rank">Raw ScoreRank string from scores.rank (X/XH read as SS, like the game).</param>
/// <param name="Completion">% of the map typed (0..1) — the metric the grade is awarded on.</param>
/// <param name="Date">ended_at; timestamptz arrives from Npgsql as UTC DateTime.</param>
public sealed record ScoreRowModel(
    long SetId,
    string Title,
    string Artist,
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

    /// <summary>Mod acronyms from the mods jsonb ([{acronym, settings}] wire shape).</summary>
    public IReadOnlyList<string> ModAcronyms =>
        JArray.Parse(string.IsNullOrEmpty(ModsJson) ? "[]" : ModsJson)
              .Select(m => m?["acronym"]?.Value<string>())
              .Where(a => !string.IsNullOrEmpty(a))
              .Select(a => a!)
              .ToList();
}
