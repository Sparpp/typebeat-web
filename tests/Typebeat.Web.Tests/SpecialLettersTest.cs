using System.Text;
using System.Text.Json;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 329, step a, on the server and in the browser: the Latin special letters FormD cannot
/// decompose are SPELLED OUT by <see cref="Typeability.Normalize"/> instead of being deleted, so
/// "straße" is stored, rated and typed as "strasse" rather than "strae". The cases are the game's
/// NonVisual/SpecialLettersTest.cs, restated so a one-sided edit fails here as well as there; the
/// JS half runs the SAME strings through the shipped typebeat-core.js (CorePunctuationHarness) and
/// is compared pairwise, not against a second hardcoded list.
/// </summary>
public class SpecialLettersTest
{
    private static readonly (char Letter, string Spelled)[] table =
    [
        ('ß', "ss"), ('ẞ', "SS"),
        ('æ', "ae"), ('Æ', "AE"),
        ('œ', "oe"), ('Œ', "OE"),
        ('ø', "o"), ('Ø', "O"),
        ('ł', "l"), ('Ł', "L"),
        ('đ', "d"), ('Đ', "D"),
        ('þ', "th"), ('Þ', "Th"),
        ('ð', "d"), ('Ð', "D"),
        ('ı', "i"),
        ('ŋ', "ng"), ('Ŋ', "Ng"),
        ('ĸ', "k"),
    ];

    [Test]
    public void TheTableIsExactlyTheDocumentedLetters()
    {
        Assert.That(Typeability.SPECIAL_LETTERS, Is.EquivalentTo(table.Select(t => new KeyValuePair<char, string>(t.Letter, t.Spelled))));
        Assert.That(Typeability.SPECIAL_LETTERS.Count, Is.EqualTo(20));
    }

    [Test]
    public void EveryLetterSpellsOutAndIsOneFormDCannotReach()
    {
        Assert.Multiple(() =>
        {
            foreach ((char letter, string spelled) in table)
            {
                Assert.That(Typeability.Normalize(letter.ToString()), Is.EqualTo(spelled), $"'{letter}' alone");
                Assert.That(Typeability.Normalize($"a{letter}b"), Is.EqualTo($"a{spelled}b"), $"'{letter}' inside a word");
                Assert.That(letter.ToString().Normalize(NormalizationForm.FormD), Is.EqualTo(letter.ToString()), $"'{letter}' has no decomposition");
            }
        });
    }

    [TestCase("straße", "strasse")]
    [TestCase("Øresund", "Oresund")]
    [TestCase("łódź", "lodz")] // ó and ź decompose, ł is spelled by the table
    [TestCase("Łódź", "Lodz")]
    [TestCase("þú", "thu")]
    [TestCase("Þór", "Thor")]
    [TestCase("ÞÚ", "ThU")]
    [TestCase("STRAẞE", "STRASSE")]
    [TestCase("Ærø", "AEro")]
    [TestCase("Œuvre cœur", "OEuvre coeur")]
    [TestCase("Đorđe", "Dorde")]
    [TestCase("Ðað", "Dad")]
    [TestCase("kalı", "kali")]
    [TestCase("Ŋaŋ", "Ngang")]
    [TestCase("ĸ", "k")]
    [TestCase("Ǿ ǿ ǽ Ǣ", "O o ae AE")]
    [TestCase("å ä ö é ñ", "a a o e n")]
    [TestCase("Grüße, Straße!", "Grusse, Strasse!")]
    public void Normalizes(string raw, string expected)
    {
        Assert.That(Typeability.Normalize(raw), Is.EqualTo(expected));
        Assert.That(Typeability.Normalize(expected), Is.EqualTo(expected), "idempotent");
    }

    /// <summary>
    /// The server's own ingest of a map carrying the raw letters (which is what an import stores:
    /// the .osu re-emits the aligner's line verbatim), so the stored rating and the search haystack
    /// count the spelled cells.
    /// </summary>
    [Test]
    public void TheIngestStoresAndCountsTheSpelledLetters()
    {
        const string lyrics =
            """
            {"version":2,"song_end_ms":9000}
            {"text":"Straße nach Łódź","start_ms":1000,"end_ms":4000,"words":[{"text":"Straße","start_ms":1000,"end_ms":2000},{"text":"nach","start_ms":2000,"end_ms":3000},{"text":"Łódź","start_ms":3000,"end_ms":4000}]}
            """;

        var parsed = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Lines.Select(l => l.RawText), Is.EqualTo(new[] { "Strasse nach Lodz" }));
            Assert.That(parsed.Lines[0].Units.Select(u => u.Text), Is.EqualTo(new[] { "Strasse", "nach", "Lodz" }));
            Assert.That(parsed.Lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 2000.0, 3000.0 }), "word timings survive the longer spelling");
            Assert.That(parsed.LyricsText, Is.EqualTo("Strasse nach Lodz"));
        });
    }

    /// <summary>The browser's table is the server's, entry for entry.</summary>
    [Test]
    public void TheBrowserCarriesTheSameTable()
    {
        JsonElement root = JsHarness.Run("CorePunctuationHarness.cjs");
        var js = root.GetProperty("specialLetters").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        Assert.That(js, Is.EquivalentTo(Typeability.SPECIAL_LETTERS.Select(kv => new KeyValuePair<string, string?>(kv.Key.ToString(), kv.Value))));
    }
}
