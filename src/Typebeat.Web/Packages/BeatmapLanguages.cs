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
    /// Every storable language: osu's original thirteen in their old dropdown order, then the twenty
    /// PR 43 appended (the game's persisted ids 14 to 33, in id order), then later appends (34 on). Order is display order only;
    /// nothing keys off the index, and the game's own dropdown order is a separate list
    /// (<c>BeatmapLanguageExtensions.DisplayOrder</c>).
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
        // PR 43's persisted ids 14 to 33, appended after the original thirteen (BeatmapLanguage.cs).
        "afrikaans",
        "bavarian",
        "danish",
        "dutch",
        "lowgerman",
        "norwegian",
        "swissgerman",
        "belarusian",
        "bulgarian",
        "croatian",
        "czech",
        "serbian",
        "slovak",
        "ukrainian",
        "finnish",
        "hungarian",
        "catalan",
        "neapolitan",
        "portuguese",
        "romanian",
        // Backlog 498: the game's persisted id 34.
        "latin",
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
        ["la"] = "latin", ["lat"] = "latin", ["latina"] = "latin",
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
    /// The separator between languages in the .osu <c>[Metadata] Languages:</c> line (backlog 472), the game's
    /// <c>BeatmapLanguageExtensions.LIST_SEPARATOR</c>. No canonical name contains it.
    /// </summary>
    public const char ListSeparator = ',';

    /// <summary>
    /// Folds a list of client- or user-supplied values onto a set's stored language list (backlog 472):
    /// each value through <see cref="Normalize"/>, unrecognised ones dropped, duplicates dropped (the first
    /// keeps its place), and <c>instrumental</c> exclusive: a list that LEADS with it is just
    /// <c>instrumental</c>, any other loses it. The game's <c>BeatmapLanguageExtensions.Normalise</c> applies
    /// the same rules; tests/Typebeat.WireCompat pins the two against each other.
    /// </summary>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string?> values)
    {
        var result = new List<string>();

        foreach (string? value in values)
        {
            string normalized = Normalize(value);

            if (normalized.Length == 0 || result.Contains(normalized))
                continue;

            result.Add(normalized);
        }

        if (result.Count > 1)
        {
            if (result[0] == "instrumental")
                return ["instrumental"];

            result.Remove("instrumental");
        }

        return result;
    }

    /// <summary>
    /// Splits a <c>Languages:</c> line value (<c>japanese,english</c>) and folds it with
    /// <see cref="NormalizeList"/>. A single name reads as a one-entry list, so this also reads a
    /// <c>Language:</c> line.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : NormalizeList(value.Split(ListSeparator));

    /// <summary>
    /// A stored <c>beatmapsets.languages</c> list as the SELECTs read it: <c>array_to_string(languages, ',')</c>,
    /// because Dapper matches a positional record's constructor by the reader's column types and a <c>text[]</c>
    /// column reports <c>System.Array</c>, which no <c>string[]</c> parameter matches. Canonical names never
    /// contain the separator, so the split is exact; an empty list reads back empty.
    /// </summary>
    public static string[] FromStoredList(string? joined)
        => string.IsNullOrEmpty(joined) ? [] : joined.Split(ListSeparator);

    /// <summary>
    /// The ONE language that speaks for a multi-language set wherever a single language is still read
    /// (<c>beatmapsets.language</c>, which the ratings, the Polyglot support rules, the SEO block and every
    /// client that predates the list read): the first entry that is not <c>english</c>, or <c>english</c>
    /// when it is the only one, or <see cref="Unset"/> for an empty list. The game's
    /// <c>BeatmapLanguageExtensions.PrimaryOf</c>, rule for rule (WireCompat pins it): English is what the
    /// game types natively, so every single-language consumer is asking about the OTHER language.
    /// </summary>
    public static string Primary(IReadOnlyList<string> languages)
    {
        string first = Unset;

        foreach (string language in languages)
        {
            if (language.Length == 0)
                continue;

            if (language != "english")
                return language;

            if (first.Length == 0)
                first = language;
        }

        return first;
    }

    /// <summary>
    /// Display casing for a stored value: "japanese" to "Japanese", <see cref="Unset"/> (or
    /// anything unrecognised) to null so callers can render nothing at all rather than an
    /// awkward placeholder.
    /// </summary>
    public static string? DisplayName(string? stored) => stored switch
    {
        // The two compound names the game labels with a Description attribute.
        "lowgerman" => "Low German",
        "swissgerman" => "Swiss German",
        _ => IsCanonical(stored) ? char.ToUpperInvariant(stored![0]) + stored[1..] : null,
    };

    /// <summary>
    /// The BCP 47 tag the pages put in a <c>lang</c> attribute for text in this language (backlog
    /// 332), so a browser picks a font that has the script and a screen reader picks the voice.
    /// Null for <c>instrumental</c>, <c>other</c> and <see cref="Unset"/>, which name no language:
    /// the page then declares nothing and the text inherits the document's own.
    /// </summary>
    public static string? LangTag(string? stored) => stored switch
    {
        "english" => "en",
        "japanese" => "ja",
        "chinese" => "zh",
        "korean" => "ko",
        "french" => "fr",
        "german" => "de",
        "spanish" => "es",
        "italian" => "it",
        "russian" => "ru",
        "polish" => "pl",
        "swedish" => "sv",
        "afrikaans" => "af",
        "bavarian" => "bar",
        "danish" => "da",
        "dutch" => "nl",
        "lowgerman" => "nds",
        "norwegian" => "no",
        "swissgerman" => "gsw",
        "belarusian" => "be",
        "bulgarian" => "bg",
        "croatian" => "hr",
        "czech" => "cs",
        "serbian" => "sr",
        "slovak" => "sk",
        "ukrainian" => "uk",
        "finnish" => "fi",
        "hungarian" => "hu",
        "catalan" => "ca",
        "neapolitan" => "nap",
        "portuguese" => "pt",
        "romanian" => "ro",
        "latin" => "la",
        _ => null,
    };

    /// <summary>
    /// The tag for this language's ROMANISED text: <c>-Latn</c> appended for a language whose own
    /// script is not Latin (Japanese, Chinese, Korean, and the Cyrillic Russian, Ukrainian,
    /// Belarusian, Bulgarian and Serbian), the bare tag otherwise.
    /// </summary>
    public static string? RomanisedLangTag(string? stored) => LangTag(stored) switch
    {
        null => null,
        "ja" or "zh" or "ko" or "ru" or "uk" or "be" or "bg" or "sr" => LangTag(stored) + "-Latn",
        string tag => tag,
    };
}
