using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Typebeat.Web.Packages.Lyrics
{
    internal static class PolyglotRatingSupport
    {
        internal static bool Enabled(IReadOnlyList<string>? mods) => mods?.Any(m => string.Equals(m, POLYGLOT_ACRONYM, StringComparison.OrdinalIgnoreCase)) == true;

        /// <summary>The Polyglot mod's acronym (game's <c>TypeBeatModPolyglot.Acronym</c>).</summary>
        internal const string POLYGLOT_ACRONYM = "PG";

        internal static string? LanguageFor(IEnumerable<LyricLine> lines, string? language = null)
            => language ?? LyricOriginals.DetectLanguage(Originals(lines));

        private static IEnumerable<string?> Originals(IEnumerable<LyricLine> lines)
            => lines.Select(l => l.Original).Concat(lines.SelectMany(l => l.Units.Select(u => u.Original)))
                .Concat(lines.SelectMany(l => l.UnromanisedWords.Select(w => (string?)w.Original)));

        internal static bool Supports(IEnumerable<LyricLine> lines, IReadOnlyList<string>? mods, string? language = null)
        {
            if (!Enabled(mods))
                return true;
            var material = lines as IReadOnlyList<LyricLine> ?? lines.ToArray();
            language = LanguageFor(material, language);
            if (JapaneseReading.IsJapanese(language))
                return true;
            if (language is "chinese" or "korean")
                return false;
            return Originals(material).Where(t => t != null).SelectMany(t => t!.EnumerateRunes()).All(c =>
                !Rune.IsLetter(c) || IsAlphabeticLetter(c.Value));
        }

        private static bool IsAlphabeticLetter(int c)
            => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                or >= '\u00c0' and <= '\u024f' or >= '\u1e00' and <= '\u1eff'
                or >= '\u0400' and <= '\u052f' or >= '\ua640' and <= '\ua69f';

        /// <summary>The game's test on the map's DECLARED language, here its stored canonical name.</summary>
        internal static bool SupportsDeclaredLanguage(string? language, IReadOnlyList<string>? mods)
            => !Enabled(mods) || language is not ("chinese" or "korean");

        // Direct native-layout letters do not pay a compose surcharge, including the national
        // AltGr letters on Polish/Romanian layouts. Dead-key accents still cost their extra key.
        // Layout references: https://unicode.org/cldr/charts/43/keyboards/keyboards2chars.html
        internal static bool IsNativeLetter(char c, string? language)
        {
            string native = language switch
            {
                "spanish" or "catalan" => "ñç",
                "italian" or "neapolitan" => "àèéìòù",
                "german" or "bavarian" or "lowgerman" => "äöüß",
                "swissgerman" => "äöüéèà",
                "polish" => "ąćęłńóśźż",
                "swedish" or "finnish" => "åäö",
                "danish" or "norwegian" => "æøå",
                "french" => "àçéèù",
                "portuguese" => "ç",
                "romanian" => "ăâîșțşţ",
                "hungarian" => "áéíóöőúüű",
                "czech" => "ěščřžýáíéúů",
                "slovak" => "äáčéíľňôšťúýž",
                "croatian" or "serbian" => "čćđšž",
                "russian" or "cyrillic" => "ёй",
                "ukrainian" => "йї",
                "belarusian" => "ёўй",
                "bulgarian" => "й",
                _ => "",
            };
            return native.Contains(char.ToLowerInvariant(c));
        }
    }
}
