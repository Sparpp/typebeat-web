using System.Security.Cryptography;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 445 on the server: a stored map's <c>[Lyrics]</c> line may end with a <c>"backing"</c>
/// array, the line's BACKING VOCALS (words shown above the lyric in gameplay, never typed). The
/// server reads nothing off it: the parse, the cells and every rating are the map's without it, and
/// the gameplay fingerprint excludes it, so adding, retiming or placing backing vocals on a ranked
/// map does not demote it. The cross-repo half (the game's own writer and status check against this
/// strip) is WireCompat <c>BackingVocalParityTest</c>.
/// </summary>
public class BackingVocalFingerprintTest
{
    /// <summary>A two-line word-timed map with no backing vocals, as the game's encoder writes it.</summary>
    private const string plain_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Word"}
        {"text":"i wanna let my hair","start_ms":1000,"end_ms":4000,"words":[{"text":"i","start_ms":1000,"end_ms":1300,"score":1},{"text":"wanna","start_ms":1300,"end_ms":2000,"score":1},{"text":"let","start_ms":2000,"end_ms":2500,"score":1},{"text":"my","start_ms":2500,"end_ms":3000,"score":1},{"text":"hair","start_ms":3000,"end_ms":3800,"score":1}]}
        {"text":"down","start_ms":4000,"end_ms":6000,"words":[{"text":"down","start_ms":4000,"end_ms":5500,"score":1}]}
        """;

    /// <summary>
    /// The SAME map with backing vocals, where the game's encoder writes them: the last key of the
    /// line, each word's text, times and (only when the mapper placed it) anchor. The text holds a
    /// bracket and an escaped quote, so the strip is shown to consume a JSON string whole.
    /// </summary>
    private const string backing_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Word"}
        {"text":"i wanna let my hair","start_ms":1000,"end_ms":4000,"words":[{"text":"i","start_ms":1000,"end_ms":1300,"score":1},{"text":"wanna","start_ms":1300,"end_ms":2000,"score":1},{"text":"let","start_ms":2000,"end_ms":2500,"score":1},{"text":"my","start_ms":2500,"end_ms":3000,"score":1},{"text":"hair","start_ms":3000,"end_ms":3800,"score":1}],"backing":[{"text":"down","start_ms":2600,"end_ms":3000,"anchor":0.05},{"text":"do]wn\"","start_ms":3100,"end_ms":3500}]}
        {"text":"down","start_ms":4000,"end_ms":6000,"words":[{"text":"down","start_ms":4000,"end_ms":5500,"score":1}],"backing":[{"text":"ooh","start_ms":4200,"end_ms":5800}]}
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
    /// THE FINGERPRINT PIN. Adding backing vocals leaves the fingerprint exactly where it was, and so
    /// does moving one or dragging its anchor, while retyping a single typed word still moves it.
    /// </summary>
    [Test]
    public void BackingVocalsDoNotMoveTheGameplayFingerprint()
    {
        string plain = GameplayFingerprint.Compute(parse(plain_lyrics), files);

        Assert.That(GameplayFingerprint.Compute(parse(backing_lyrics), files), Is.EqualTo(plain));
        Assert.That(GameplayFingerprint.CanonicalForm(parse(backing_lyrics), files), Does.Not.Contain("backing"));

        string retimed = backing_lyrics.Replace("\"start_ms\":2600,\"end_ms\":3000,\"anchor\":0.05", "\"start_ms\":1100,\"end_ms\":1500,\"anchor\":0.9");
        Assert.That(retimed, Is.Not.EqualTo(backing_lyrics));
        Assert.That(GameplayFingerprint.Compute(parse(retimed), files), Is.EqualTo(plain));

        string retyped = backing_lyrics.Replace("{\"text\":\"hair\"", "{\"text\":\"hare\"");
        Assert.That(retyped, Is.Not.EqualTo(backing_lyrics));
        Assert.That(GameplayFingerprint.Compute(parse(retyped), files), Is.Not.EqualTo(plain));

        Assert.That(plain, Does.StartWith($"v{GameplayFingerprint.VERSION}:"));
        Assert.That(GameplayFingerprint.VERSION, Is.EqualTo(3), "the backing exclusion is recipe version 3");
    }

    /// <summary>The strip takes the whole array, a bracket and an escaped quote inside a word included, and nothing after it.</summary>
    [Test]
    public void TheWholeBackingArrayIsStripped()
    {
        string[] withBacking = backing_lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        string[] without = plain_lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

        Assert.That(withBacking.Select(GameplayFingerprint.CanonicalLyricLine), Is.EqualTo(without));
    }

    /// <summary>A line without backing vocals is handed back as the very same string.</summary>
    [Test]
    public void ALineWithoutBackingVocalsCanonicalisesToItself()
    {
        foreach (string line in plain_lyrics.Split('\n').Select(l => l.Trim()))
            Assert.That(GameplayFingerprint.CanonicalLyricLine(line), Is.SameAs(line).Or.EqualTo(line));
    }

    /// <summary>
    /// The parse reads nothing off the backing vocals: the same lines, words and times as the map
    /// without them, so the cells, the judged-cell count and every rating are the map's own.
    /// </summary>
    [Test]
    public void TheParseIgnoresBackingVocals()
    {
        var plain = section(plain_lyrics);
        var backing = section(backing_lyrics);

        Assert.That(backing, Has.Count.EqualTo(plain.Count));

        for (int l = 0; l < plain.Count; l++)
        {
            Assert.That(backing[l].RawText, Is.EqualTo(plain[l].RawText), $"line {l} text");
            Assert.That(backing[l].StartTime, Is.EqualTo(plain[l].StartTime), $"line {l} start");
            Assert.That(backing[l].EndTime, Is.EqualTo(plain[l].EndTime), $"line {l} end");
            Assert.That(backing[l].Units.Select(u => (u.Text, u.StartTime, u.EndTime)), Is.EqualTo(plain[l].Units.Select(u => (u.Text, u.StartTime, u.EndTime))), $"line {l} words");
        }

        foreach (bool literate in new[] { false, true })
            Assert.That(JudgedCells.Count(backing, literate), Is.EqualTo(JudgedCells.Count(plain, literate)), $"judged cells, literate {literate}");
        Assert.That(parse(backing_lyrics).LyricsText, Is.EqualTo(parse(plain_lyrics).LyricsText));
    }
}
