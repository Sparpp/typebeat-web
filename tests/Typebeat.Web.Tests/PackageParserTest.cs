using System.Security.Cryptography;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests;

/// <summary>
/// DB-free parser tests over synthetic packages (fixtures modeled on the game's
/// LyricOsuFormat writer; see <see cref="SyntheticPackage"/>).
/// </summary>
public class PackageParserTest
{
    [Test]
    public void Parse_ExtractsFilesAndMetadata()
    {
        byte[] osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            source: "Neon FM", previewTime: 12345, video: "clip.mp4"));
        byte[] audio = SyntheticPackage.Utf8("not really mp3 bytes");
        byte[] bg = SyntheticPackage.TinyPng();

        using var zip = SyntheticPackage.Zip(
            ("Synth Rider - Neon Nights (uploader) [type!beat].osu", osu),
            ("audio.mp3", audio),
            ("bg.jpg", bg),
            ("clip.mp4", SyntheticPackage.Utf8("fake video")));

        var package = BeatmapPackageParser.Parse(zip);

        Assert.Multiple(() =>
        {
            Assert.That(package.Files, Has.Count.EqualTo(4));
            Assert.That(package.Difficulties, Has.Count.EqualTo(1));
            Assert.That(package.HasVideo, Is.True);
        });

        var osuEntry = package.Files.Single(f => f.Filename.EndsWith(".osu", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(osuEntry.Size, Is.EqualTo(osu.Length));
            Assert.That(osuEntry.Sha256, Is.EqualTo(SHA256.HashData(osu)));
        });

        var diff = package.Difficulties[0];

        Assert.Multiple(() =>
        {
            // checksum_md5 = MD5 of the exact .osu bytes (the beatmap_hash identity contract).
            Assert.That(diff.ChecksumMd5, Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(osu))));
            Assert.That(diff.Title, Is.EqualTo("Neon Nights"));
            Assert.That(diff.TitleUnicode, Is.EqualTo("Neon Nights"));
            Assert.That(diff.Artist, Is.EqualTo("Synth Rider"));
            Assert.That(diff.ArtistUnicode, Is.EqualTo("Synth Rider"));
            Assert.That(diff.Creator, Is.EqualTo("uploader"));
            Assert.That(diff.VersionName, Is.EqualTo("type!beat"));
            Assert.That(diff.Source, Is.EqualTo("Neon FM"));
            Assert.That(diff.Tags, Is.EqualTo("typebeat lyrics typing"));
            Assert.That(diff.BeatmapId, Is.EqualTo(1001));
            Assert.That(diff.BeatmapSetId, Is.EqualTo(1));
            Assert.That(diff.AudioFilename, Is.EqualTo("audio.mp3"));
            Assert.That(diff.PreviewTime, Is.EqualTo(12345));
            Assert.That(diff.BackgroundFilename, Is.EqualTo("bg.jpg"));
            Assert.That(diff.VideoFilename, Is.EqualTo("clip.mp4"));
            // "0,500,4,2,0,100,1,0" -> 60000/500 = 120 BPM.
            Assert.That(diff.Bpm, Is.EqualTo(120).Within(1e-9));
        });
    }

    [Test]
    public void Parse_LyricsSection_DrivesPaceAndLengths()
    {
        byte[] osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText());

        using var zip = SyntheticPackage.Zip(
            ("map.osu", osu),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var diff = BeatmapPackageParser.Parse(zip).Difficulties[0];

        Assert.Multiple(() =>
        {
            // The game's own pace regression values (LyricPaceStatisticsTest.ComputesBoundaryWindowPace).
            Assert.That(diff.Lines, Has.Count.EqualTo(1));
            Assert.That(diff.Pace.TypeableCellCount, Is.EqualTo(5));
            Assert.That(diff.Pace.WordCount, Is.EqualTo(2));
            Assert.That(diff.Pace.AverageCpm, Is.EqualTo(100).Within(1e-9));
            Assert.That(diff.Pace.AverageWpm, Is.EqualTo(20).Within(1e-9)); // CPM/5 since LyricPace v15
            Assert.That(diff.Pace.AverageCharsPerWord, Is.EqualTo(2.5).Within(1e-9));
            // Strain-based stars (LyricDifficulty): "ab cd" -> per-word strain sum -> 0.63.
            Assert.That(diff.Pace.DifficultyRating, Is.EqualTo(0.63).Within(0.01));

            // Last line end = min(song_end 4000, end_ms 3000 + 3000 tail) = 4000 ms.
            Assert.That(diff.TotalLengthS, Is.EqualTo(4.0).Within(1e-9));
            Assert.That(diff.DrainLengthS, Is.EqualTo(3.0).Within(1e-9));
        });
    }

    [Test]
    public void Parse_FreestyleLine_CountsMarkersAsCells()
    {
        // A map blob carrying the editor's freestyle opt-in. Ingest must count the '&' slots as
        // cells (they are keypresses), or the stored pace/difficulty undercount the map; an
        // ampersand in an unflagged line stays lyric punctuation and is stripped as always.
        const string lyrics =
            """
            {"granularity":"word","version":2,"song_end_ms":20000}
            {"text":"me & you","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"me","start_ms":1000,"end_ms":2000},{"text":"&","start_ms":2000,"end_ms":3000},{"text":"you","start_ms":3000,"end_ms":4000}]}
            """;

        const string legacyLyrics =
            """
            {"version":2,"song_end_ms":20000}
            {"text":"me & you","start_ms":1000,"end_ms":4000}
            """;

        var free = BeatmapPackageParser.ParseDifficulty("free.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));
        var legacy = BeatmapPackageParser.ParseDifficulty("legacy.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: legacyLyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(free.Lines[0].RawText, Is.EqualTo("me & you"));
            Assert.That(free.Pace.TypeableCellCount, Is.EqualTo(8)); // 6 letters + the slot + 2 spaces
            Assert.That(free.Pace.WordCount, Is.EqualTo(3));

            Assert.That(legacy.Lines[0].RawText, Is.EqualTo("me you"));
            Assert.That(legacy.Pace.TypeableCellCount, Is.EqualTo(6));
            Assert.That(legacy.Pace.WordCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Parse_LyricsText_PreservesCasingAndLineStructure()
    {
        // The stored haystack doubles as the set page's display text, so it carries the AUTHOR'S
        // form: casing survives, the supported punctuation survives (better display, and search is
        // unaffected because a per-word ILIKE '%word%' still matches a word with a mark stuck to
        // it), and each [Lyrics] line stays its own '\n'-separated line. Unsupported chars still
        // normalize away as everywhere else.
        const string lyrics =
            """
            {"version":2,"song_end_ms":9000}
            {"text":"Neon SKYLINE, glowing!","start_ms":1000,"end_ms":3000}
            {"text":"we Type* at Night","start_ms":4000,"end_ms":8000}
            """;

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.That(diff.LyricsText, Is.EqualTo("Neon SKYLINE, glowing!\nwe Type at Night"));
    }

    [Test]
    public void Parse_EmptyLyrics_YieldsZeroPace()
    {
        byte[] osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: """{"version":2,"granularity":"Line"}"""));

        using var zip = SyntheticPackage.Zip(("map.osu", osu), ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var diff = BeatmapPackageParser.Parse(zip).Difficulties[0];

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines, Is.Empty);
            Assert.That(diff.Pace.AverageWpm, Is.Zero);
            Assert.That(diff.Pace.DifficultyRating, Is.Zero);
            Assert.That(diff.TotalLengthS, Is.Zero);
        });
    }

    [Test]
    public void Parse_BackslashArchivePaths_AreNormalized()
    {
        byte[] osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText());

        using var zip = SyntheticPackage.Zip(
            ("map.osu", osu),
            (@"sb\extra.png", SyntheticPackage.Utf8("x")),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var package = BeatmapPackageParser.Parse(zip);

        Assert.That(package.Files.Select(f => f.Filename), Does.Contain("sb/extra.png"));
    }

    [Test]
    public void Parse_NotAZip_Throws422able()
    {
        using var junk = new MemoryStream(SyntheticPackage.Utf8("this is not a zip"));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(junk));
    }

    [Test]
    public void Parse_OsuWithoutMagic_Throws422able()
    {
        using var zip = SyntheticPackage.Zip(
            ("map.osu", SyntheticPackage.Utf8("osu file format v14\n\n[Metadata]\nTitle:nope\n")),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(zip));
    }

    [Test]
    public void Parse_TraversalFilename_Throws422able()
    {
        using var zip = SyntheticPackage.Zip(("../evil.txt", SyntheticPackage.Utf8("x")));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(zip));
    }

    /// <summary>
    /// Regression: a title ending in an ellipsis ("I know youre hurting...mp3") contains "..", but
    /// is a perfectly ordinary filename. A substring check used to reject it as path traversal and
    /// blocked the upload outright — traversal is a ".." SEGMENT, not any run of dots.
    /// </summary>
    [Test]
    public void Parse_EllipsisInFilename_Succeeds()
    {
        const string audioName = "I know youre hurting...mp3";

        byte[] osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(audioFilename: audioName));

        using var zip = SyntheticPackage.Zip(
            ("Synth Rider - Neon Nights (uploader) [type!beat].osu", osu),
            (audioName, SyntheticPackage.Utf8("not really mp3 bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        var package = BeatmapPackageParser.Parse(zip);

        Assert.Multiple(() =>
        {
            Assert.That(package.Files.Select(f => f.Filename), Does.Contain(audioName));
            Assert.That(package.Difficulties[0].AudioFilename, Is.EqualTo(audioName));
        });
    }

    /// <summary>Traversal hidden mid-path must still be rejected (the segment check, not the prefix).</summary>
    [Test]
    public void Parse_NestedTraversalFilename_Throws422able()
    {
        using var zip = SyntheticPackage.Zip(("skin/../../evil.txt", SyntheticPackage.Utf8("x")));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(zip));
    }

    [Test]
    public void Parse_OverlongFilename_Throws422able()
    {
        string name = new string('a', BeatmapPackageParser.MaxFilenameLength + 1) + ".txt";

        using var zip = SyntheticPackage.Zip((name, SyntheticPackage.Utf8("x")));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(zip));
    }
}
