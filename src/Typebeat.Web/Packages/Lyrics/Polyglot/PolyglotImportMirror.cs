#nullable enable

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Typebeat.Web.Packages.Lyrics
{
    /// <summary>
    /// The PART of the game's <c>Beatmaps/LyricOriginals.cs</c> the Polyglot mirror reads: the
    /// original rules (<see cref="CarriesOriginal"/>, <see cref="CollapseWhitespace"/>,
    /// <see cref="OriginalFor"/>), the per-word romanisation with its cut map
    /// (<see cref="RomaniseWord"/>, <see cref="RomanisedWord"/>) and the language reading
    /// (<see cref="DetectLanguage"/>, <see cref="RomanisationLanguage"/>). Bodies are the game's,
    /// with two stated differences: <see cref="RomanisationLanguage"/> takes the map's stored
    /// language as its canonical NAME (the server has no <c>BeatmapLanguage</c> enum, and the
    /// game's enum test is the same test on the name), and <see cref="RomaniseWord"/> normalises
    /// without the split-marker option the server's <see cref="Typeability.Normalize"/> does not
    /// have (the mirror never asks for markers, so the call is the same call). The editor and
    /// import halves of the file (whole-line romanisation, submission refusal) are not mirrored.
    /// </summary>
    public static class LyricOriginals
    {
        public static bool CarriesOriginal(string? source)
        {
            if (string.IsNullOrEmpty(source))
                return false;

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] <= '\u007F')
                    continue;

                switch (CharUnicodeInfo.GetUnicodeCategory(source, i))
                {
                    case UnicodeCategory.UppercaseLetter:
                    case UnicodeCategory.LowercaseLetter:
                    case UnicodeCategory.TitlecaseLetter:
                    case UnicodeCategory.ModifierLetter:
                    case UnicodeCategory.OtherLetter:
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                    case UnicodeCategory.EnclosingMark:
                        return true;
                }
            }

            return false;
        }

        public static string CollapseWhitespace(string? text)
            => string.IsNullOrEmpty(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        public static string? OriginalFor(string? source, string typed)
        {
            if (!CarriesOriginal(source))
                return null;

            string collapsed = CollapseWhitespace(source);
            return collapsed.Length == 0 || collapsed == typed ? null : collapsed;
        }

        public sealed class RomanisedWord
        {
            internal RomanisedWord(string source, string text, bool flagged, Romaniser.Result? result, int[]? textIndex)
            {
                Source = source;
                Text = text;
                Flagged = flagged;
                this.result = result;
                this.textIndex = textIndex;
            }

            private readonly Romaniser.Result? result;

            // Romaniser text index -> index in Text (the spaces a CJK mark inserts are removed).
            private readonly int[]? textIndex;

            public string Source { get; }

            public string Text { get; }

            public bool Flagged { get; }

            public string? Original => Flagged ? CollapseWhitespace(Source) : OriginalFor(Source, Text);

            public int? SourceIndexOfTextSplit(int textSplit)
            {
                if (Flagged || result == null || textIndex == null || textSplit <= 0 || textSplit >= Text.Length)
                    return null;

                foreach (var unit in result.Units)
                {
                    if (unit.TextLength <= 0)
                        continue;

                    int at = textIndex[Math.Min(unit.TextStart, textIndex.Length - 1)];

                    if (at == textSplit)
                        return unit.SourceStart;

                    if (at > textSplit)
                        break;
                }

                return null;
            }
        }

        public static RomanisedWord RomaniseWord(string source, string? language, bool keepMarkers = false)
        {
            source ??= string.Empty;

            if (!CarriesOriginal(source) && !Romaniser.NeedsRomanising(source, language))
                return new RomanisedWord(source, Typeability.Normalize(source, keepFreestyleMarkers: keepMarkers), false, null, null);

            var result = JapaneseReading.Romanise(source, language) ?? Romaniser.Romanise(source, language);

            if (!result.IsComplete)
                return new RomanisedWord(source, string.Empty, true, result, null);

            var sb = new StringBuilder(result.Text.Length);
            int[] index = new int[result.Text.Length + 1];

            for (int i = 0; i < result.Text.Length; i++)
            {
                index[i] = sb.Length;

                if (result.Text[i] != ' ')
                    sb.Append(result.Text[i]);
            }

            index[result.Text.Length] = sb.Length;

            string joined = sb.ToString();
            string text = Typeability.Normalize(joined);

            // The contract says the romanisation is Normalize-clean; if some input ever breaks it, the
            // text is still stored clean and the cuts are simply not carried.
            return text == joined
                ? new RomanisedWord(source, text, false, result, index)
                : new RomanisedWord(source, text, false, null, null);
        }

        public static string? DetectLanguage(IEnumerable<string?> texts)
        {
            var counts = new Dictionary<string, int>();
            var cyrillicHints = new HashSet<string>();

            foreach (string? text in texts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;

                foreach (char c in text)
                {
                    string? script = scriptOf(c);

                    if (script == null)
                        continue;

                    counts[script] = counts.GetValueOrDefault(script) + 1;

                    if (script == "cyrillic" && cyrillicHint(c) is string hint)
                        cyrillicHints.Add(hint);
                }
            }

            if (counts.Count == 0)
                return null;

            if (counts.GetValueOrDefault("kana") > 0)
                counts["kana"] += counts.GetValueOrDefault("han");

            string majority = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;

            switch (majority)
            {
                case "kana":
                    return "japanese";

                case "han":
                    return "chinese";

                case "hangul":
                    return "korean";

                case "cyrillic":
                    foreach (string preferred in new[] { "ukrainian", "belarusian", "serbian", "macedonian", "kazakh" })
                    {
                        if (cyrillicHints.Contains(preferred))
                            return preferred;
                    }

                    return "russian";

                default:
                    return majority;
            }
        }

        /// <summary>
        /// The language a map's originals romanise under: the map's own stored language when it names
        /// one (anything but unset, <c>other</c> and <c>instrumental</c>), else the one read off the
        /// originals (game's <c>LyricOriginals.RomanisationLanguage</c> on a <c>BeatmapLanguage</c>).
        /// </summary>
        public static string? RomanisationLanguage(string? mapLanguage, IEnumerable<string?> texts)
        {
            if (!string.IsNullOrEmpty(mapLanguage) && mapLanguage != "other" && mapLanguage != "instrumental")
                return mapLanguage;

            return DetectLanguage(texts);
        }

        private static string? scriptOf(char ch)
        {
            int c = ch;

            if (c < 0x0370)
                return null;

            if (c <= 0x03FF || (c >= 0x1F00 && c <= 0x1FFF))
                return "greek";
            if (c >= 0x0400 && c <= 0x052F)
                return "cyrillic";
            if (c >= 0x0530 && c <= 0x058F)
                return "armenian";
            if (c >= 0x0590 && c <= 0x05FF)
                return "hebrew";
            if (c >= 0x0600 && c <= 0x06FF)
                return "arabic";
            if (c >= 0x0900 && c <= 0x097F)
                return "hindi";
            if (c >= 0x0E00 && c <= 0x0E7F)
                return "thai";
            if ((c >= 0x10A0 && c <= 0x10FF) || (c >= 0x1C90 && c <= 0x1CBF))
                return "georgian";
            if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7AF))
                return "hangul";
            // Hiragana and katakana (the katakana middle dot, 0x30FB, is punctuation), the phonetic
            // extensions and halfwidth katakana.
            if ((c >= 0x3041 && c <= 0x30FF && c != 0x30FB) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0xFF66 && c <= 0xFF9F))
                return "kana";
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF))
                return "han";

            return null;
        }

        private static string? cyrillicHint(char c)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'є':
                case 'ї':
                case 'ґ':
                    return "ukrainian";

                case 'ў':
                    return "belarusian";

                case 'ђ':
                case 'ћ':
                case 'џ':
                case 'љ':
                case 'њ':
                case 'ј':
                    return "serbian";

                case 'ѓ':
                case 'ќ':
                case 'ѕ':
                    return "macedonian";

                case 'ә':
                case 'ғ':
                case 'қ':
                case 'ң':
                case 'ө':
                case 'ұ':
                case 'ү':
                case 'һ':
                    return "kazakh";

                default:
                    return null;
            }
        }

        /// <summary>Whether any line records an original (game's <c>LyricOriginals.HasOriginals</c>).</summary>
        public static bool HasOriginals(IEnumerable<LyricLine> lines)
            => lines.Any(l => !string.IsNullOrWhiteSpace(l.Original) || l.UnromanisedWords.Count > 0 || l.Units.Any(u => !string.IsNullOrWhiteSpace(u.Original)));
    }

    /// <summary>The one member of the game's <c>Beatmaps/LrcParser.cs</c> the Polyglot mirror reads.</summary>
    internal static class LrcParser
    {
        /// <summary><paramref name="unit"/> with <paramref name="original"/> as its original, everything else kept.</summary>
        internal static TimedUnit WithOriginal(TimedUnit unit, string? original) => new TimedUnit
        {
            Objects = unit.Objects,
            Text = unit.Text,
            StartTime = unit.StartTime,
            EndTime = unit.EndTime,
            Source = unit.Source,
            Confidence = unit.Confidence,
            SyllableBoundaries = unit.SyllableBoundaries,
            SyllableSplits = unit.SyllableSplits,
            Pauses = unit.Pauses,
            Original = original,
            OriginalSplits = original == unit.Original ? unit.OriginalSplits : Array.Empty<OriginalLyricSplit>(),
        };
    }

    /// <summary>
    /// The one member of the game's <c>Import/OriginalLyricGenerator.cs</c> the Polyglot mirror reads,
    /// <see cref="MatchWords"/>, with its private helpers. The language arrives as the canonical
    /// name (the game passes the <c>BeatmapLanguage</c> enum and compares it with
    /// <c>BeatmapLanguage.Japanese</c>; the only caller passes Japanese).
    /// </summary>
    internal static class OriginalLyricGenerator
    {
        /// <summary>Preserves word timing when the source-to-romanized boundaries can be recovered exactly.</summary>
        internal static string?[] MatchWords(LyricLine line, string original, string language)
        {
            var result = new string?[line.Units.Count];
            string[] words = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == line.Units.Count && words.Select((w, i) =>
                    (letters(LyricOriginals.RomaniseWord(w, language).Text) == letters(line.Units[i].Text)
                     || language == "japanese" && japaneseWordMatches(w, line.Units[i].Text))).All(equal => equal))
                return words.Select((w, i) => LyricOriginals.OriginalFor(w, line.Units[i].Text)).ToArray();

            string joined = string.Concat(words);
            if (language == "japanese")
                return matchJapaneseWords(line, joined) ?? result;
            if (!OriginalLyricAlignment.Matches(joined, string.Concat(line.Units.Select(u => u.Text)), language))
                return result;

            int sourceStart = 0;
            int textEnd = 0;

            for (int i = 0; i < line.Units.Count; i++)
            {
                textEnd += line.Units[i].Text.Length;
                int? sourceEnd = i == line.Units.Count - 1 ? joined.Length : OriginalLyricAlignment.SourceCut(joined, string.Concat(line.Units.Select(u => u.Text)), textEnd, language);

                if (sourceEnd == null || sourceEnd <= sourceStart)
                    return new string?[line.Units.Count];

                result[i] = LyricOriginals.OriginalFor(joined.Substring(sourceStart, sourceEnd.Value - sourceStart), line.Units[i].Text);
                sourceStart = sourceEnd.Value;
            }

            return result;
        }

        // Validate each authored reading independently. A full-line tagger can choose a different
        // homophone (一人 -> ichinin) or merge 身の程知らず across several timed words. Neither
        // should stop an original-script correction from reaching those words in gameplay.
        private static string?[]? matchJapaneseWords(LyricLine line, string original)
        {
            var failed = new HashSet<(int Word, int Start)>();
            var result = new string?[line.Units.Count];
            return match(0, 0) ? result : null;

            bool match(int word, int start)
            {
                if (word == line.Units.Count)
                    return start == original.Length;
                if (failed.Contains((word, start)))
                    return false;

                int last = word + 1 == line.Units.Count ? original.Length : original.Length - (line.Units.Count - word - 1);
                for (int end = word + 1 == line.Units.Count ? last : start + 1; end <= last; end++)
                {
                    // Closing marks travel with the preceding word, opening marks with the next.
                    if (end < original.Length && ("、。，．！？!?,.」』）)]".Contains(original[end])
                                                  || "「『（([".Contains(original[end - 1])))
                        continue;
                    string surface = original[start..end];
                    if (!OriginalLyricAlignment.Matches(surface, line.Units[word].Text, "japanese") || !match(word + 1, end))
                        continue;
                    result[word] = LyricOriginals.OriginalFor(surface, line.Units[word].Text);
                    return true;
                }

                failed.Add((word, start));
                return false;
            }
        }

        private static bool japaneseWordMatches(string original, string authored)
            => OriginalLyricAlignment.Matches(original, authored, "japanese");

        private static string letters(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>
    /// The reading half of the game's <c>Import/JapaneseLyricGenerator.cs</c> the Polyglot mirror
    /// reads: <see cref="HasDictionaryReading"/>, its <c>normaliseReading</c> and the forward
    /// <c>SurfaceDictionary</c> (<c>JapaneseLyricGenerator.SurfaceReadings.cs</c>) that walks IPAdic's
    /// own surface trie. Bodies are the game's. The romaji-to-kanji GENERATOR (the reverse index and
    /// the connection matrix) is import-only and is not mirrored.
    /// </summary>
    internal static class JapaneseLyricGenerator
    {
        private static readonly Lazy<SurfaceDictionary> surfaces = new Lazy<SurfaceDictionary>(() => new SurfaceDictionary());

        internal static bool HasDictionaryReading(string surface, string authored)
        {
            string? kana = JapaneseRomaji.ReadAuthored(authored);
            return kana != null && surfaces.Value.MatchesReading(normaliseReading(kana), surface);
        }

        private static string normaliseReading(string reading)
        {
            string kana = JapaneseRomaji.Hiragana(reading);
            return kana.Contains('ー') ? JapaneseRomaji.ReadAuthored(Romaniser.Romanise(kana, "japanese").Text) ?? kana : kana;
        }

        /// <summary>
        /// Forward lookups walk IPAdic's existing surface trie. Checking a reading in the editor
        /// must not build the complete reverse index and connection matrix used by Generate.
        /// </summary>
        private sealed class SurfaceDictionary
        {
            private readonly byte[] data;
            private readonly int trieCount;
            private readonly int tokenStart;
            private readonly int featureStart;
            private readonly JapaneseTextCache<string, string[]> readings = new JapaneseTextCache<string, string[]>(4096);

            internal SurfaceDictionary()
            {
                data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "IpaDic", "sys.dic"));
                if (data.Length < 72 || (unchecked((uint)readInt(0)) ^ 0xef718f77u) != data.Length || readInt(4) != 102
                    || Encoding.ASCII.GetString(data, 40, 5) != "UTF-8")
                    throw new InvalidDataException("The bundled Japanese dictionary has an unsupported format.");
                int trieSize = readInt(24), tokenSize = readInt(28), featureSize = readInt(32);
                tokenStart = checked(72 + trieSize);
                featureStart = checked(tokenStart + tokenSize);
                if (trieSize % 8 != 0 || tokenSize % 16 != 0 || checked(featureStart + featureSize) != data.Length)
                    throw new InvalidDataException("The bundled Japanese dictionary is incomplete.");
                trieCount = trieSize / 8;
            }

            internal bool MatchesReading(string reading, string surface)
            {
                var failed = new HashSet<(int Surface, int Kana)>();
                return match(0, 0);

                bool match(int source, int kana)
                {
                    if (source == surface.Length || kana == reading.Length)
                        return source == surface.Length && kana == reading.Length;
                    if (!failed.Add((source, kana)))
                        return false;
                    // Retain alternate compound readings such as 一人 + で = hitoride.
                    for (int length = 1; length <= surface.Length - source; length++)
                    {
                        foreach (string candidate in readings.GetOrAdd(surface.Substring(source, length), lookup))
                        {
                            if (reading.AsSpan(kana).StartsWith(candidate.AsSpan(), StringComparison.Ordinal)
                                && match(source + length, kana + candidate.Length))
                                return true;
                        }
                    }
                    string direct = JapaneseRomaji.Hiragana(surface[source].ToString());
                    return reading.AsSpan(kana).StartsWith(direct.AsSpan(), StringComparison.Ordinal)
                           && match(source + 1, kana + direct.Length);
                }
            }

            private string[] lookup(string surface)
            {
                // Match the generation dictionary's exclusion of symbol/number readings.
                if (!surface.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or '々'
                    or >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff'))
                    return Array.Empty<string>();
                int node = readInt(72);
                foreach (byte value in Encoding.UTF8.GetBytes(surface))
                {
                    int next = node + value + 1;
                    if (next < 0 || next >= trieCount || readInt(76 + next * 8) != node)
                        return Array.Empty<string>();
                    node = readInt(72 + next * 8);
                }
                if (node < 0 || node >= trieCount || readInt(76 + node * 8) != node)
                    return Array.Empty<string>();
                int terminal = readInt(72 + node * 8);
                if (terminal >= 0)
                    return Array.Empty<string>();
                int packed = -terminal - 1, first = packed >> 8;
                var result = new HashSet<string>(StringComparer.Ordinal);
                for (int token = first; token < first + (packed & 255); token++)
                {
                    int feature = featureStart + readInt(tokenStart + token * 16 + 8);
                    int end = Array.IndexOf(data, (byte)0, feature);
                    string[] fields = Encoding.UTF8.GetString(data, feature, end - feature).Split(',');
                    if (fields.Length < 9 || fields[7] == "*")
                        continue;
                    result.Add(normaliseReading(fields[7]));
                    if (fields[8] != "*")
                        result.Add(normaliseReading(fields[8]));
                }
                if (surface == "を")
                    result.Add("お");
                return result.ToArray();
            }

            private int readInt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
    }
}
