namespace Typebeat.Web.Packages;

/// <summary>
/// The canonical song-language vocabulary (019_language.sql): the closed set of values that may
/// ever appear in <c>beatmapsets.language</c>, the input aliases the search box and the package
/// parser accept for them, and their display casing.
///
/// The list is osu's language list minus its search-only "any" sentinel. The game mirrors it as
/// the <c>BeatmapLanguage</c> enum (typebeat-osu typebeat.Game/Beatmaps/BeatmapLanguage.cs), whose
/// members' lowercased names ARE these strings; tests/Typebeat.WireCompat pins the two lists
/// against each other, because the .osu <c>[Metadata] Language:</c> line the game writes and this
/// server reads is a cross-repo wire surface just like the JSON DTOs are.
///
/// <c>""</c> (empty) is not a member: it is the "not known yet" state, and
/// <see cref="Normalize"/> is what turns everything unrecognised, absent, or explicitly
/// "unspecified" into it.
/// </summary>
public static class BeatmapLanguages
{
    /// <summary>The stored value meaning "no language determined yet". Never a member of <see cref="All"/>.</summary>
    public const string Unset = "";

    /// <summary>
    /// Every storable language, in the game's dropdown order (which is osu's). Order is display
    /// order only; nothing keys off the index.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "english",
        "japanese",
        "chinese",
        "korean",
        "french",
        "german",
        "spanish",
        "italian",
        "russian",
        "polish",
        "swedish",
        "instrumental",
        "other",
    ];

    private static readonly HashSet<string> canonical = new(All, StringComparer.Ordinal);

    /// <summary>
    /// Input aliases, folded case-insensitively onto canonical names. Everything a user is likely
    /// to type into <c>lang:</c> lands here: ISO 639-1 two-letter codes, the common three-letter
    /// shorthands, endonyms, and the obvious spelling variants. Canonical names themselves are
    /// matched separately (case-insensitively) and are NOT repeated in this table.
    /// </summary>
    private static readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["eng"] = "english", ["engl"] = "english", ["en-us"] = "english", ["en-gb"] = "english",
        ["jp"] = "japanese", ["ja"] = "japanese", ["jpn"] = "japanese", ["jap"] = "japanese", ["nihongo"] = "japanese",
        ["cn"] = "chinese", ["zh"] = "chinese", ["chn"] = "chinese", ["chi"] = "chinese", ["zho"] = "chinese",
        ["mandarin"] = "chinese", ["cantonese"] = "chinese",
        ["kr"] = "korean", ["ko"] = "korean", ["kor"] = "korean", ["hangul"] = "korean",
        ["fr"] = "french", ["fra"] = "french", ["fre"] = "french", ["francais"] = "french",
        ["de"] = "german", ["ger"] = "german", ["deu"] = "german", ["deutsch"] = "german",
        ["es"] = "spanish", ["esp"] = "spanish", ["spa"] = "spanish", ["espanol"] = "spanish", ["castellano"] = "spanish",
        ["it"] = "italian", ["ita"] = "italian", ["italiano"] = "italian",
        ["ru"] = "russian", ["rus"] = "russian",
        ["pl"] = "polish", ["pol"] = "polish", ["polski"] = "polish",
        ["sv"] = "swedish", ["se"] = "swedish", ["swe"] = "swedish", ["svenska"] = "swedish",
        ["inst"] = "instrumental", ["instr"] = "instrumental", ["none"] = "instrumental", ["nolyrics"] = "instrumental",
        ["misc"] = "other", ["various"] = "other",
    };

    /// <summary>True when <paramref name="value"/> is exactly a stored canonical name.</summary>
    public static bool IsCanonical(string? value) => value != null && canonical.Contains(value);

    /// <summary>
    /// Folds any user- or client-supplied value onto a canonical name, or onto
    /// <see cref="Unset"/> when it is absent, blank, "unspecified", or unrecognised. This is the
    /// ONLY way a value reaches the <c>language</c> column, so the column can hold nothing else.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Unset;

        string trimmed = value.Trim();

        // Match the canonical names case-insensitively without allocating a second table for them.
        foreach (string name in All)
        {
            if (string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return aliases.TryGetValue(trimmed, out string? mapped) ? mapped : Unset;
    }

    /// <summary>
    /// Display casing for a stored value: "japanese" to "Japanese", <see cref="Unset"/> (or
    /// anything unrecognised) to null so callers can render nothing at all rather than an
    /// awkward placeholder.
    /// </summary>
    public static string? DisplayName(string? stored)
        => IsCanonical(stored) ? char.ToUpperInvariant(stored![0]) + stored[1..] : null;
}
