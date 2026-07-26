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
        "DT" or "NC" or "FL" or "SD" or "LT" => "increase",
        "HT" or "NF" => "reduction",
        "RX" => "automation",
        _ => "other",
    };

    /// <summary>Full mod name for the icon tooltip.</summary>
    public static string Name(string? acronym) => (acronym ?? string.Empty).ToUpperInvariant() switch
    {
        "DT" => "Double Time",
        "NC" => "Nightcore",
        "HT" => "Half Time",
        "NF" => "No Fail",
        "SD" => "Sudden Death",
        "FL" => "Flashlight",
        "RX" => "Mashing",
        "LT" => "Literate",
        "FT" => "Fletcher",
        "MU" => "Muted",
        _ => acronym ?? string.Empty,
    };
}
