using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// What a POLYGLOT play (<c>PG</c>, the game's <c>TypeBeatModPolyglot</c>) on one stored difficulty
/// is judged on and priced from, computed by the server's own mirror of the game's Polyglot
/// flattening (<see cref="TypingLine.ForMods"/>, <see cref="PolyglotDifficulty"/>) over the
/// difficulty's Polyglot parse (<see cref="LyricTiming.ParseSection"/> with <c>polyglot: true</c>).
///
/// <para>RANKEDNESS (PR 43): the mod is ranked at 1.0x on every map except a Chinese or Korean one,
/// where it stays local only (the game's <c>TypeBeatModPolyglot.IsLocalOnlyFor</c>, read off the
/// map's DECLARED language). It earns pp only where the game can rate it
/// (<see cref="Supported"/>, <c>PolyglotRatingSupport.Supports</c>): Latin and Cyrillic originals
/// rate on their own script, Japanese on the shortest romaji input; a map whose originals carry any
/// other script still ranks but prices at 0, exactly as the game's performance calculator does.</para>
///
/// <para>THE JUDGEMENT STREAM. Every typeable cell of the flattening is one nested scoring object
/// (<c>TypeBeatHitObject.CreateNestedHitObjects</c>). On a Latin or Cyrillic map each object is one
/// judgement, so an honest submission's <c>maximum_statistics</c> counts exactly
/// <see cref="Stream.Cells"/>. A JAPANESE character is scored per romaji INPUT
/// (<c>TypeBeatScoreProcessor.GetScoringResults</c>): its maximum starts at the character's
/// minimum accepted input (<see cref="TypingCell.MinimumRomajiInputs"/>, the dictionary reading's
/// shortest spelling) and the processor grows it by whatever longer spelling the player actually
/// typed, or shrinks it for kana input, so the claimed count of a Japanese play is not one number:
/// it lies between <see cref="Stream.Cells"/> (one input per object, the least any object records)
/// and <see cref="Stream.MaximumInputs"/> (four keys per kana, the longest spelling the input grammar
/// accepts, <c>xtsu</c> for a small tsu). The COMBO is fixed: <see cref="Stream.Combo"/>, the
/// minimum inputs summed (<c>TypeBeatScoreProcessor.MaximumComboFor</c>), and it is the
/// <c>comboNotes</c> the pp formula clamps a Japanese play's combo to.</para>
///
/// <para>Stored inside <c>beatmaps.ratings</c> (<see cref="Scoring.BeatmapRatings"/>, schema 2) beside
/// the Polyglot rating cells, so it is rewritten by the same sweep (<see cref="LyricPace.VERSION"/>)
/// and read by the same lookup the pricing paths already make.</para>
/// </summary>
public sealed class PolyglotSummary
{
    /// <summary>The figures of one typed stream (default or Literate).</summary>
    public readonly record struct Stream(int Cells, int Combo, int MaximumInputs, double PlayedDurationS);

    /// <summary>The romanisation language the originals are read under (map language, else detected).</summary>
    public string? Language { get; init; }

    /// <summary>The map's own <c>[Metadata] Language:</c>, normalised, or <c>""</c> when it states none.</summary>
    public string Declared { get; init; } = string.Empty;

    /// <summary>Whether the map records any original at all (the game offers the mod only then).</summary>
    public bool Available { get; init; }

    /// <summary>Whether the game can rate a Polyglot play here (<c>PolyglotRatingSupport.Supports</c>).</summary>
    public bool Supported { get; init; }

    /// <summary>Whether the romanisation language is Japanese (input-scored characters).</summary>
    public bool Japanese { get; init; }

    public Stream Plain { get; init; }

    public Stream Literate { get; init; }

    /// <summary>The stream a play with or without Literate is judged on.</summary>
    public Stream For(bool literate) => literate ? Literate : Plain;

    /// <summary>
    /// The mod stack a Polyglot rating is computed under, as the acronyms the mirror reads
    /// (<see cref="PolyglotRatingSupport.Enabled"/>, and Literate's <c>LT</c>).
    /// </summary>
    public static IReadOnlyList<string> ModsFor(bool literate) => literate ? ["PG", "LT"] : ["PG"];

    /// <summary>The languages on which Polyglot stays local only (game's <c>IsLocalOnlyFor</c>).</summary>
    public static bool IsLocalOnlyLanguage(string? declared) => declared is "chinese" or "korean";

    /// <summary>
    /// The language a play's rules read: the difficulty's own declared language, or the set's stored
    /// one when the file states none (the game's beatmap metadata language, which the online lookup
    /// fills from the set's <c>song_language</c> when the file is silent).
    /// </summary>
    public static string EffectiveDeclared(string? declared, string? setLanguage)
        => string.IsNullOrEmpty(declared) ? setLanguage ?? string.Empty : declared;

    /// <summary>
    /// The summary of one difficulty from its Polyglot parse and its declared language (a member of
    /// <see cref="BeatmapLanguages.All"/>, or <c>""</c>).
    /// </summary>
    public static PolyglotSummary Compute(IReadOnlyList<LyricLine> lines, string? declared)
    {
        declared = BeatmapLanguages.Normalize(declared);
        string? language = PolyglotRatingSupport.LanguageFor(lines, LyricOriginals.RomanisationLanguage(declared, originals(lines)));
        bool japanese = JapaneseReading.IsJapanese(language);

        return new PolyglotSummary
        {
            Language = language,
            Declared = declared,
            Available = LyricOriginals.HasOriginals(lines),
            Supported = PolyglotRatingSupport.Supports(lines, ModsFor(false), language),
            Japanese = japanese,
            Plain = stream(lines, language, literate: false),
            Literate = stream(lines, language, literate: true),
        };
    }

    /// <summary>The map's originals, in the order the game's <c>TypeBeatModPolyglot.LanguageFor</c> reads them.</summary>
    private static IEnumerable<string?> originals(IReadOnlyList<LyricLine> lines)
        => lines.Select(l => l.Original)
                .Concat(lines.SelectMany(l => l.Units.Select(u => u.Original)))
                .Concat(lines.SelectMany(l => l.UnromanisedWords.Select(w => (string?)w.Original)));

    private static Stream stream(IReadOnlyList<LyricLine> lines, string? language, bool literate)
    {
        int cells = 0, combo = 0, maximumInputs = 0;

        foreach (var line in lines)
        {
            // The live flattening the nested scoring objects are built from: Polyglot on, Japanese
            // character timing on (TypeBeatHitObject.CreateNestedHitObjects).
            var typing = TypingLine.ForMods(line, literate, true, language, japaneseCharacterTiming: true);

            for (int i = 0; i < typing.Cells.Count; i++)
            {
                var cell = typing.Cells[i];

                if (!cell.IsTypeable)
                    continue;

                cells++;

                if (cell.MinimumRomajiInputs > 0)
                {
                    combo += cell.MinimumRomajiInputs;
                    maximumInputs += MAX_INPUTS_PER_KANA * Math.Max(1, typing.JapaneseInput?.CharacterReadingAt(i)?.Length ?? 0);
                }
                else
                {
                    combo++;
                    maximumInputs++;
                }
            }
        }

        var projected = PolyglotDifficulty.LinesFor(lines, ModsFor(literate), language);

        return new Stream(cells, combo, Math.Max(maximumInputs, combo), Scoring.PerformancePoints.PlayedDurationFor(projected, literate ? [new ScoreMod(Scoring.PerformancePoints.LITERATE_ACRONYM, null)] : null));
    }

    /// <summary>
    /// The most keys the romaji grammar accepts for one kana (<c>xtsu</c> / <c>ltsu</c> for a small
    /// tsu; every other spelling is at most three keys per kana, split small kana included). The upper
    /// bound of a Japanese character's input count is this times its reading's kana.
    /// </summary>
    public const int MAX_INPUTS_PER_KANA = 4;

    public JObject ToJson() => new JObject
    {
        ["language"] = Language,
        ["declared"] = Declared,
        ["available"] = Available,
        ["supported"] = Supported,
        ["japanese"] = Japanese,
        ["plain"] = streamJson(Plain),
        ["literate"] = streamJson(Literate),
    };

    private static JObject streamJson(Stream s) => new JObject
    {
        ["cells"] = s.Cells,
        ["combo"] = s.Combo,
        ["max_inputs"] = s.MaximumInputs,
        ["duration_s"] = s.PlayedDurationS,
    };

    /// <summary>The stored object back, or null when it is absent or malformed (read as not yet computed).</summary>
    public static PolyglotSummary? Parse(JToken? token)
    {
        if (token is not JObject o || o["plain"] is not JObject plain || o["literate"] is not JObject literate)
            return null;

        try
        {
            return new PolyglotSummary
            {
                Language = o["language"]?.Type == JTokenType.String ? o["language"]!.Value<string>() : null,
                Declared = o["declared"]?.Value<string>() ?? string.Empty,
                Available = o["available"]?.Value<bool>() ?? false,
                Supported = o["supported"]?.Value<bool>() ?? false,
                Japanese = o["japanese"]?.Value<bool>() ?? false,
                Plain = parseStream(plain),
                Literate = parseStream(literate),
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    private static Stream parseStream(JObject s)
        => new Stream(s["cells"]!.Value<int>(), s["combo"]!.Value<int>(), s["max_inputs"]!.Value<int>(), s["duration_s"]!.Value<double>());
}
