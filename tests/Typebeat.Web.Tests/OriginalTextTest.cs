using System.Security.Cryptography;
using System.Text;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 330 on the server: a stored map's <c>[Lyrics]</c> may carry an optional
/// <c>"original"</c> per line and per word (the lyric in its own script). The parse reads and
/// preserves it and rates nothing off it, the gameplay fingerprint excludes it (so adding originals
/// to a ranked map does not demote it), and a word the game's romaniser could not spell (an empty
/// text beside an original) is refused at upload.
/// </summary>
public class OriginalTextTest
{
    /// <summary>A word-timed, subdivided two-line map with no originals, as the game's encoder writes it.</summary>
    private const string plain_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Syllable"}
        {"text":"Privet mir","start_ms":1000,"end_ms":2800,"words":[{"text":"Privet","start_ms":1000,"end_ms":1900,"score":1,"syllables":[{"text":"Pri","start_ms":1000,"end_ms":1400},{"text":"vet","start_ms":1400,"end_ms":1900}]},{"text":"mir","start_ms":1900,"end_ms":2800,"score":1}]}
        {"text":"hello world","start_ms":3000,"end_ms":4800,"words":[{"text":"hello","start_ms":3000,"end_ms":3900,"score":1},{"text":"world","start_ms":3900,"end_ms":4800,"score":1}]}
        """;

    /// <summary>
    /// The SAME map with its originals, exactly where the game's encoder writes them: straight after
    /// each "text", line and word, and only where they differ.
    /// </summary>
    private const string original_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Syllable"}
        {"text":"Privet mir","original":"Привет мир","start_ms":1000,"end_ms":2800,"words":[{"text":"Privet","original":"Привет","start_ms":1000,"end_ms":1900,"score":1,"syllables":[{"text":"Pri","start_ms":1000,"end_ms":1400},{"text":"vet","start_ms":1400,"end_ms":1900}]},{"text":"mir","original":"мир","start_ms":1900,"end_ms":2800,"score":1}]}
        {"text":"hello world","start_ms":3000,"end_ms":4800,"words":[{"text":"hello","start_ms":3000,"end_ms":3900,"score":1},{"text":"world","start_ms":3900,"end_ms":4800,"score":1}]}
        """;

    private static readonly List<PackageFileEntry> files =
    [
        new PackageFileEntry(SHA256.HashData(SyntheticPackage.Utf8("audio")), 5, "audio.mp3"),
    ];

    private static ParsedDifficulty parse(string lyrics)
        => BeatmapPackageParser.ParseDifficulty("m.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

    private static IReadOnlyList<LyricLine> section(string lyrics)
        => LyricTiming.ParseSection(lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Lines;

    /// <summary>
    /// THE FINGERPRINT PIN. Adding originals to a map (the backlog 173 rule: only gameplay changes
    /// demote) leaves its gameplay fingerprint exactly where it was, while retyping a single word
    /// moves it, as does romanising an unromanised word.
    /// </summary>
    [Test]
    public void OriginalsDoNotMoveTheGameplayFingerprint()
    {
        string plain = GameplayFingerprint.Compute(parse(plain_lyrics), files);
        string withOriginals = GameplayFingerprint.Compute(parse(original_lyrics), files);

        Assert.That(withOriginals, Is.EqualTo(plain));
        Assert.That(GameplayFingerprint.CanonicalForm(parse(original_lyrics), files), Does.Not.Contain("original"));

        // A different ORIGINAL is still no gameplay change.
        string respelled = original_lyrics.Replace("\"original\":\"мир\"", "\"original\":\"миру\"");
        Assert.That(respelled, Is.Not.EqualTo(original_lyrics));
        Assert.That(GameplayFingerprint.Compute(parse(respelled), files), Is.EqualTo(plain));

        // A different TYPED word is.
        Assert.That(GameplayFingerprint.Compute(parse(original_lyrics.Replace("\"text\":\"mir\"", "\"text\":\"miir\"")), files), Is.Not.EqualTo(plain));

        // And so is romanising an unromanised word: an empty text becoming a real one.
        const string pending = """{"text":"mir","original":"x мир","start_ms":1000,"end_ms":2800,"words":[{"text":"","original":"x","start_ms":1000,"end_ms":1900},{"text":"mir","start_ms":1900,"end_ms":2800}]}""";
        const string romanised = """{"text":"eks mir","original":"x мир","start_ms":1000,"end_ms":2800,"words":[{"text":"eks","original":"x","start_ms":1000,"end_ms":1900},{"text":"mir","start_ms":1900,"end_ms":2800}]}""";
        Assert.That(GameplayFingerprint.CanonicalLyricLine(pending), Is.Not.EqualTo(GameplayFingerprint.CanonicalLyricLine(romanised)));

        // The stamp is the new recipe's.
        Assert.That(plain, Does.StartWith("v2:"));
    }

    /// <summary>
    /// The game's writers ESCAPE the original (its JSON encoder writes non-ASCII as backslash-u
    /// escapes, and a quote or a backslash escaped too): the strip runs to the end of the string all
    /// the same, and takes nothing after it.
    /// </summary>
    [Test]
    public void AnEscapedOriginalIsStrippedWhole()
    {
        const string bs = "\\";
        string escaped = "{\"text\":\"a b\",\"original\":\"" + bs + "u041F " + bs + "\"q" + bs + "\" " + bs + bs + "\",\"start_ms\":1,"
                         + "\"words\":[{\"text\":\"a\",\"original\":\"" + bs + "u0430\",\"start_ms\":1}]}";

        Assert.That(GameplayFingerprint.CanonicalLyricLine(escaped),
            Is.EqualTo("{\"text\":\"a b\",\"start_ms\":1,\"words\":[{\"text\":\"a\",\"start_ms\":1}]}"));
    }

    /// <summary>A line without an original is handed back as the very same string.</summary>
    [Test]
    public void ALineWithoutAnOriginalCanonicalisesToItself()
    {
        foreach (string line in plain_lyrics.Split('\n').Select(l => l.Trim()))
            Assert.That(GameplayFingerprint.CanonicalLyricLine(line), Is.SameAs(line).Or.EqualTo(line));
    }

    /// <summary>
    /// The parse READS and PRESERVES the originals and rates nothing off them: every unit, boundary,
    /// pace figure and rating is the no-original map's.
    /// </summary>
    [Test]
    public void TheParseReadsOriginalsAndRatesNothingOffThem()
    {
        var plain = parse(plain_lyrics);
        var withOriginals = parse(original_lyrics);

        Assert.That(withOriginals.Lines[0].Original, Is.EqualTo("Привет мир"));
        Assert.That(withOriginals.Lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
        Assert.That(withOriginals.Lines[1].Original, Is.Null);

        for (int i = 0; i < plain.Lines.Count; i++)
        {
            Assert.That(withOriginals.Lines[i].RawText, Is.EqualTo(plain.Lines[i].RawText));
            Assert.That(withOriginals.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime)),
                Is.EqualTo(plain.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime))));
            Assert.That(withOriginals.Lines[i].Units.SelectMany(u => u.SyllableBoundaries),
                Is.EqualTo(plain.Lines[i].Units.SelectMany(u => u.SyllableBoundaries)));
        }

        Assert.That(withOriginals.RatingsJson, Is.EqualTo(plain.RatingsJson));
        Assert.That(withOriginals.Pace, Is.EqualTo(plain.Pace));
        Assert.That(withOriginals.UnromanisedWords, Is.Empty);

        // PRESERVED: the stored section is the uploaded one, key for key, so the originals come back
        // down on the next download exactly as they went up.
        Assert.That(string.Join('\n', withOriginals.LyricSectionLines), Does.Contain("\"text\":\"Privet mir\",\"original\":\"Привет мир\""));
    }

    /// <summary>
    /// An UNROMANISED word (an empty text beside an original) is taken out of the words[]/token
    /// pairing, as the game's loader takes it out: the other words keep their own explicit times
    /// instead of the whole line falling back to interpolation.
    /// </summary>
    [Test]
    public void AnUnromanisedWordIsSkippedInThePairing()
    {
        const string lyrics =
            """
            {"version":2,"song_end_ms":9000,"granularity":"Word"}
            {"text":"ga suki","original":"君 が すき","start_ms":1000,"end_ms":3000,"words":[{"text":"","original":"君","start_ms":1000,"end_ms":1500},{"text":"ga","original":"が","start_ms":1500,"end_ms":2000},{"text":"suki","original":"すき","start_ms":2000,"end_ms":3000}]}
            {"text":"","original":"你好","start_ms":4000,"end_ms":5000,"words":[{"text":"","original":"你好","start_ms":4000,"end_ms":5000}]}
            {"text":"yeah","start_ms":6000,"end_ms":7000}
            """;

        var (header, lines) = LyricTiming.ParseSection(lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "ga suki", "yeah" }), "a line with nothing to type is dropped here, as always");
        Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1500.0, 2000.0 }), "the explicit words keep their own times");
        Assert.That(lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "が", "すき" }));
        Assert.That(header.Unromanised, Is.EqualTo(new[] { "君", "你好" }));
    }

    [Test]
    public void AMapWithUnromanisedWordsIsRefusedAtUpload()
    {
        const string lyrics =
            """
            {"version":2,"song_end_ms":9000,"granularity":"Word"}
            {"text":"ga suki","original":"君 が すき","start_ms":1000,"end_ms":3000,"words":[{"text":"","original":"君","start_ms":1000,"end_ms":1500},{"text":"ga","start_ms":1500,"end_ms":2000},{"text":"suki","start_ms":2000,"end_ms":3000}]}
            """;

        using var zip = SyntheticPackage.Zip(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));
        var package = BeatmapPackageParser.Parse(zip);

        var ex = Assert.Throws<PackageValidationException>(() => PackageValidator.Validate(package, 1, [1001], "uploader"));
        Assert.That(ex!.Message, Does.Contain("no romanisation yet"));
        Assert.That(ex.Message, Does.Contain("君"));

        // Originals alone are welcome.
        using var fine = SyntheticPackage.Zip(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: original_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));
        Assert.DoesNotThrow(() => PackageValidator.Validate(BeatmapPackageParser.Parse(fine), 1, [1001], "uploader"));
    }

    [Test]
    public void TheSectionParseMatchesTheFullParse()
        => Assert.That(section(original_lyrics).Select(l => l.Original), Is.EqualTo(parse(original_lyrics).Lines.Select(l => l.Original)));

    private static string utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
