namespace Typebeat.Web;

/// <summary>
/// Picks between a beatmapset's romanized title/artist and its original (non-romanized) text,
/// the Settings &gt; Preferences "show original title/artist" toggle. Falls back to the romanized
/// value whenever the original is blank (an unset *_unicode column, or a set that never had one),
/// so the toggle can never render an empty title.
/// </summary>
public static class MetadataDisplay
{
    public static string Pick(string romanized, string? original, bool preferOriginal)
        => preferOriginal && !string.IsNullOrWhiteSpace(original) ? original : romanized;
}
