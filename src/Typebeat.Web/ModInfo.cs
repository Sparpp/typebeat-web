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
        // Recite ("RE") joins the increase list because the game types it ModType.DifficultyIncrease,
        // beside the Flashlight it is modelled on. Conductor ("CT"), Dyslexia ("DX") and Puppeteer
        // ("PT") deliberately do NOT get an arm: CT and PT are both ModType.Fun rate followers and
        // sit with the Wind Up / Wind Down ramps, which this table has always left on the neutral
        // badge, and DX is ModType.Conversion, which is the bucket the four colours here do not have
        // (see the Fletcher note below). There is no assist family to put DX in: Mashing is
        // "automation" because it plays the map for you, and Dyslexia does not.
        "DT" or "NC" or "FL" or "SD" or "LT" or "GK" or "RH" or "HR" or "RE" => "increase",
        "HT" or "DC" or "NF" or "EZ" => "reduction",
        "RX" => "automation",
        _ => "other",
    };

    /// <summary>Full mod name for the icon tooltip.</summary>
    public static string Name(string? acronym) => (acronym ?? string.Empty).ToUpperInvariant() switch
    {
        "DT" => "Double Time",
        "NC" => "Nightcore",
        "HT" => "Half Time",
        "DC" => "Daycore",
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
        // RE and DX are two of the trio from backlog 226 / 229 / 231. Names mirror the game's own
        // (TypeBeatModRecite, TypeBeatModDyslexia), so a badge reads as the mod select does. The
        // trio's third member, Conductor, is no longer a current mod: see the retirement note below.
        "RE" => "Recite",
        "DX" => "Dyslexia",
        // Backlog 256 introduced Puppeteer, a clock-slaving follower where the song strictly follows
        // the typing and timing judgement is forgiven outright. Backlog 257 retired the older
        // rate-follower mod of backlog 226 / 229 / 231 and moved the "Conductor" name onto Puppeteer,
        // on the FT "Fletcher (retired)" precedent above: "PT" is the live mod (still
        // TypeBeatModPuppeteer, still acronym "PT", now labelled Conductor), "CT" is the retired one
        // (TypeBeatModConductor, keeps its class name and acronym so a stored CT row's icon says what
        // that row was played under rather than what the name means today).
        "PT" => "Conductor",
        "CT" => "Conductor (retired)",
        _ => acronym ?? string.Empty,
    };
}
