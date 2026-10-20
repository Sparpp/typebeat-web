namespace Typebeat.WireCompat;

/// <summary>
/// The SOURCE half of PR 43's Polyglot mirror, in <see cref="RomaniserParityTest"/>'s pattern: the
/// server's copies of the game's Polyglot and Japanese files are the game's own text, and the few
/// lines that cannot be (the game's mod and language types, its logger, its editor-only members) are
/// named here one by one, so any OTHER drift fails as source rather than waiting for a fixture to
/// reach it. The behavioural half is <see cref="PolyglotParityTest"/> and
/// <see cref="JapaneseReadingParityTest"/>.
/// </summary>
[TestFixture]
public class PolyglotMirrorSourceTest
{
    private static string read(string side, string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PolyglotMirror", side, file + ".txt"));

    /// <summary>
    /// The comparable body of a file: line endings normalised, the game's licence header and every
    /// <c>using typebeat.*</c> / <c>using osu.*</c> line dropped, the namespace line blanked, and
    /// leading blank lines trimmed. These are the mechanical differences every mirror is allowed.
    /// </summary>
    private static string body(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
                        .Where(l => !l.StartsWith("// Copyright (c) ppy", StringComparison.Ordinal)
                                    && !l.StartsWith("// See the LICENCE file", StringComparison.Ordinal)
                                    && !l.StartsWith("using typebeat.", StringComparison.Ordinal)
                                    && !l.StartsWith("using osu.", StringComparison.Ordinal))
                        .Select(l => l.StartsWith("namespace ", StringComparison.Ordinal) ? "namespace" : l)
                        .SkipWhile(l => l.Length == 0);
        return string.Join("\n", lines);
    }

    /// <summary>The verbatim copies: identical below the mechanical differences.</summary>
    [TestCase("PausedWord")]
    [TestCase("SyllableSegments")]
    [TestCase("Syllabifier")]
    [TestCase("PolyglotText")]
    [TestCase("JapaneseRomaji")]
    [TestCase("JapaneseCharacterReadings")]
    [TestCase("JapaneseInputPlan")]
    [TestCase("OriginalLyricAlignment")]
    [TestCase("JapaneseTextCache")]
    public void TheVerbatimCopyIsTheGamesText(string file)
        => Assert.That(body(read("server", file)), Is.EqualTo(body(read("game", file))));

    /// <summary>
    /// The adapted copies: the game's text with exactly these replacements (game line, server text),
    /// and nothing else different.
    /// </summary>
    private static readonly Dictionary<string, (string Game, string Server)[]> adaptations = new()
    {
        ["PolyglotLine"] =
        [
            ("                : Import.OriginalLyricGenerator.MatchWords(line, line.Original, typebeat.Game.Beatmaps.BeatmapLanguage.Japanese);",
             "                : OriginalLyricGenerator.MatchWords(line, line.Original, \"japanese\");"),
        ],
        ["OriginalLyricSubdivisions"] =
        [
            // The editor's pipe text reads the split marker the server's normaliser does not have.
            ("        internal static string PipeText(TimedUnit unit, string? language, bool original)\n        {\n            unit = original ? Project(unit, language) : unit;\n"
             + "            var cuts = PausedWord.Cuts(unit, unit.StartTime, unit.EndTime, original ? Typeability.IsPolyglotTypeCell : null);\n"
             + "            return string.Join(Typeability.SPLIT_MARKER, SyllableSegments.SegmentTexts(unit.Text, cuts));\n        }\n\n", ""),
        ],
        ["JapaneseReading"] =
        [
            ("                Logger.Error(e, \"Japanese reading dictionary could not be loaded; kanji will need manual readings\");",
             "                // The game logs through osu.Framework's Logger; the server has no logger in this\n"
             + "                // static path, so the one failure line goes to standard error (the container log).\n"
             + "                Console.Error.WriteLine($\"Japanese reading dictionary could not be loaded; kanji will need manual readings: {e}\");"),
        ],
        ["PolyglotDifficulty"] =
        [
            // The server reads a stack as its acronyms.
            ("IReadOnlyList<Mod>? mods, string? language = null, bool forPace = false)", "IReadOnlyList<string>? mods, string? language = null, bool forPace = false)"),
            ("            bool literate = Scoring.PerformancePoints.IsLiterate(mods);",
             "            bool literate = mods?.Any(m => string.Equals(m, \"LT\", StringComparison.OrdinalIgnoreCase)) == true;"),
        ],
        ["PolyglotRatingSupport"] =
        [
            ("        internal static bool Enabled(IReadOnlyList<Mod>? mods) => mods?.Any(m => m is TypeBeatModPolyglot) == true;",
             "        internal static bool Enabled(IReadOnlyList<string>? mods) => mods?.Any(m => string.Equals(m, POLYGLOT_ACRONYM, StringComparison.OrdinalIgnoreCase)) == true;\n\n"
             + "        /// <summary>The Polyglot mod's acronym (game's <c>TypeBeatModPolyglot.Acronym</c>).</summary>\n"
             + "        internal const string POLYGLOT_ACRONYM = \"PG\";"),
            ("internal static bool Supports(IEnumerable<LyricLine> lines, IReadOnlyList<Mod>? mods, string? language = null)",
             "internal static bool Supports(IEnumerable<LyricLine> lines, IReadOnlyList<string>? mods, string? language = null)"),
            // The server has no BeatmapLanguage enum: the declared language is its stored canonical name.
            ("        internal static bool SupportsDeclaredLanguage(BeatmapLanguage language, IReadOnlyList<Mod>? mods)\n"
             + "            => !Enabled(mods) || language is not (BeatmapLanguage.Chinese or BeatmapLanguage.Korean);",
             "        /// <summary>The game's test on the map's DECLARED language, here its stored canonical name.</summary>\n"
             + "        internal static bool SupportsDeclaredLanguage(string? language, IReadOnlyList<string>? mods)\n"
             + "            => !Enabled(mods) || language is not (\"chinese\" or \"korean\");"),
        ],
    };

    [TestCase("PolyglotLine")]
    [TestCase("OriginalLyricSubdivisions")]
    [TestCase("JapaneseReading")]
    [TestCase("PolyglotDifficulty")]
    [TestCase("PolyglotRatingSupport")]
    public void TheAdaptedCopyIsTheGamesTextWithItsNamedReplacementsOnly(string file)
    {
        string game = body(read("game", file));

        foreach (var (from, to) in adaptations[file])
        {
            Assert.That(game, Does.Contain(from), $"{file}: the game no longer reads '{from[..Math.Min(60, from.Length)]}', so the adaptation is stale");
            game = game.Replace(from, to);
        }

        Assert.That(body(read("server", file)), Is.EqualTo(game));
    }

    /// <summary>
    /// The PARTIAL mirrors: the server holds only some members of these game files (the rest is
    /// import, editor or gameplay-engine code no server figure reads), each member's text the game's
    /// own. A member is cut from its signature line to the closing line at the signature's own
    /// indentation (an expression-bodied one to its terminating semicolon), and compared with the
    /// indentation normalised.
    /// </summary>
    private static readonly (string GameFile, string ServerFile, string Signature, (string Game, string Server)[] Replacements)[] members =
    [
        ("LyricOriginals", "PolyglotImportMirror", "public static bool CarriesOriginal(string? source)", []),
        ("LyricOriginals", "PolyglotImportMirror", "public static string CollapseWhitespace(string? text)", []),
        ("LyricOriginals", "PolyglotImportMirror", "public static string? OriginalFor(string? source, string typed)", []),
        ("LyricOriginals", "PolyglotImportMirror", "public int? SourceIndexOfTextSplit(int textSplit)", []),
        ("LyricOriginals", "PolyglotImportMirror", "public static RomanisedWord RomaniseWord(string source, string? language, bool keepMarkers = false)",
        [
            // The server's normaliser has no split-marker option; the mirror never asks for markers.
            ("Typeability.Normalize(source, keepFreestyleMarkers: keepMarkers, keepSplitMarkers: keepMarkers)", "Typeability.Normalize(source, keepFreestyleMarkers: keepMarkers)"),
        ]),
        ("LyricOriginals", "PolyglotImportMirror", "public static string? DetectLanguage(IEnumerable<string?> texts)", []),
        ("LyricOriginals", "PolyglotImportMirror", "private static string? scriptOf(char ch)", []),
        ("LyricOriginals", "PolyglotImportMirror", "private static string? cyrillicHint(char c)", []),
        ("LyricOriginals", "PolyglotImportMirror", "public static bool HasOriginals(IEnumerable<LyricLine> lines)", []),
        ("LrcParser", "PolyglotImportMirror", "internal static TimedUnit WithOriginal(TimedUnit unit, string? original) => new TimedUnit", []),
        ("OriginalLyricGenerator", "PolyglotImportMirror", "internal static string?[] MatchWords(LyricLine line, string original",
        [
            ("internal static string?[] MatchWords(LyricLine line, string original, BeatmapLanguage language)", "internal static string?[] MatchWords(LyricLine line, string original, string language)"),
            ("LyricOriginals.RomaniseWord(w, language.ToCanonicalName())", "LyricOriginals.RomaniseWord(w, language)"),
            ("language == BeatmapLanguage.Japanese && japaneseWordMatches", "language == \"japanese\" && japaneseWordMatches"),
            ("if (language == BeatmapLanguage.Japanese)", "if (language == \"japanese\")"),
            ("string.Concat(line.Units.Select(u => u.Text)), language.ToCanonicalName()))", "string.Concat(line.Units.Select(u => u.Text)), language))"),
            ("textEnd, language.ToCanonicalName());", "textEnd, language);"),
        ]),
        ("OriginalLyricGenerator", "PolyglotImportMirror", "private static string?[]? matchJapaneseWords(LyricLine line, string original)", []),
        ("OriginalLyricGenerator", "PolyglotImportMirror", "private static bool japaneseWordMatches(string original, string authored)", []),
        ("OriginalLyricGenerator", "PolyglotImportMirror", "private static string letters(string text)", []),
        ("JapaneseLyricGenerator", "PolyglotImportMirror", "internal static bool HasDictionaryReading(string surface, string authored)", []),
        ("JapaneseLyricGenerator", "PolyglotImportMirror", "private static string normaliseReading(string reading)", []),
        ("JapaneseLyricGenerator.SurfaceReadings", "PolyglotImportMirror", "private sealed class SurfaceDictionary", []),
        ("TypingLine", "TypingLine", "private void configureJapaneseEndpoints()", []),
        ("TypingLine", "TypingLine", "internal double JapaneseCharacterEnd(int cell)", []),
        ("TypingLine", "TypingLine", "internal sealed class CellRules", []),
        ("TypingLine", "TypingLine", "private static void interpolateUntimedTargets(double?[] targets, int start, int count, double fallback,", []),
        ("TypingLine", "TypingLine", "internal static double syllableCharTarget(double unitStart, double unitEnd, IReadOnlyList<double> boundaries, int k, int j, IReadOnlyList<int>? cellCuts = null)", []),
        ("TypingLine", "TypingLine", "private static double[] tokenCellTargets(string token, double unitStart, double unitEnd, TimedUnit? unit, int k, Func<char, bool>? isCell = null, bool alignSubdivisions = false)", []),
        ("TypingLine", "TypingLine", "private static void fillPausedStretches(PausedWord.Cut cut, double[] ramp, string token, Func<char, bool>? isCell, bool alignSubdivisions)", []),
        ("TypingLine", "TypingLine", "internal static double[] CellTargetsFor(TimedUnit unit, int typeableCount, Func<char, bool>? isCell = null)", []),
    ];

    private static IEnumerable<TestCaseData> memberCases()
        => members.Select(m => new TestCaseData(m.GameFile, m.ServerFile, m.Signature, m.Replacements).SetName($"PartialMirror({m.GameFile}: {m.Signature[..Math.Min(50, m.Signature.Length)]})"));

    [TestCaseSource(nameof(memberCases))]
    public void ThePartialMirrorsMemberIsTheGamesText(string gameFile, string serverFile, string signature, (string Game, string Server)[] replacements)
    {
        string? game = member(read("game", gameFile), signature);
        string? server = member(read("server", serverFile), signature.Replace("BeatmapLanguage language)", "string language)"));

        Assert.That(game, Is.Not.Null, $"the game's {gameFile} no longer declares '{signature}'");
        Assert.That(server, Is.Not.Null, $"the server's {serverFile} does not declare '{signature}'");

        foreach (var (from, to) in replacements)
        {
            Assert.That(game, Does.Contain(from), $"the adaptation '{from}' is stale");
            game = game!.Replace(from, to);
        }

        Assert.That(server, Is.EqualTo(game));
    }

    /// <summary>One member's text with its indentation removed, or null when the signature is absent.</summary>
    private static string? member(string text, string signature)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(signature, StringComparison.Ordinal));

        if (start < 0)
            return null;

        int indent = lines[start].Length - lines[start].TrimStart().Length;
        int end = start;

        if (lines[start].TrimEnd().EndsWith(';'))
            end = start;
        else if (lines.Length > start + 1 && lines[start + 1].TrimStart().StartsWith("=>", StringComparison.Ordinal))
        {
            end = start + 1;
            while (!lines[end].TrimEnd().EndsWith(';'))
                end++;
        }
        else
        {
            end = start + 1;
            while (end < lines.Length && !(lines[end].Length - lines[end].TrimStart().Length == indent && lines[end].TrimStart().StartsWith('}')))
                end++;
        }

        return string.Join("\n", lines[start..(end + 1)].Select(l => l.Length >= indent && l[..indent].Trim().Length == 0 ? l[indent..] : l.TrimStart()));
    }
}
