namespace Typebeat.Web;

/// <summary>
/// Presentation metadata for the mod acronyms stored on scores: the category (which drives the
/// icon colour, osu-style: reduction green, increase red, automation violet) and a human name
/// (the icon's tooltip). Unknown acronyms fall back to a neutral "other" badge so a mod added
/// later still renders something sensible.
/// </summary>
public static class ModInfo
{
    /// <summary>CSS modifier suffix (mod-icon--{class}) grouping the acronym by mod type.</summary>
    public static string CategoryClass(string? acronym) => (acronym ?? string.Empty).ToUpperInvariant() switch
    {
        "DT" or "NC" or "FL" or "SD" or "LT" or "GK" or "RH" or "HR" => "increase",
        "HT" or "NF" or "EZ" => "reduction",
        "RX" => "automation",
        _ => "other",
    };

    /// <summary>Full mod name for the icon tooltip.</summary>
    public static string Name(string? acronym) => (acronym ?? string.Empty).ToUpperInvariant() switch
    {
        "DT" => "Double Time",
        "NC" => "Nightcore",
        "HT" => "Half Time",
        "EZ" => "Easy",
        "HR" => "Hard Rock",
        "NF" => "No Fail",
        "SD" => "Sudden Death",
        "FL" => "Flashlight",
        "RX" => "Mashing",
        "LT" => "Literate",
        "GK" => "Gatekeeper",
        "RH" => "Rhythmic",
        // Backlog 208 REVERSED Fletcher and moved it to a new acronym. "FC" is the live mod, which
        // PINS the caret to the playhead; "FT" is the retired one, which unpinned it, and the
        // unpinned caret is now what every play does. Both names mirror the game's own
        // (TypeBeatModFletcher.Name and TypeBeatModLegacyFletcher.Name), so a stored FT row's icon
        // says what that row was played under rather than what the name means today.
        "FC" => "Fletcher",
        "FT" => "Fletcher (retired)",
        "MU" => "Muted",
        _ => acronym ?? string.Empty,
    };
}
