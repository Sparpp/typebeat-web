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

    /// <summary>
    /// The two map-font keys (backlog 291): [General] "LyricFont: &lt;family&gt;" and
    /// "LyricFontFile: &lt;filename&gt;", surfaced like Title/Source, both empty when the file states
    /// none (every map authored before the editor's picker existed writes neither line).
    /// </summary>
    [Test]
    public void Parse_LyricFontKeys_AreExposedWhenPresent_AndEmptyWhenAbsent()
    {
        var with = BeatmapPackageParser.ParseDifficulty("map.osu",
            SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                lyricFont: "Blocky Pixels", lyricFontFile: "lyricfont.woff2")));

        var without = BeatmapPackageParser.ParseDifficulty("map.osu",
            SyntheticPackage.Utf8(SyntheticPackage.OsuText()));

        Assert.Multiple(() =>
        {
            Assert.That(with.LyricFont, Is.EqualTo("Blocky Pixels"));
            Assert.That(with.LyricFontFile, Is.EqualTo("lyricfont.woff2"));

            Assert.That(without.LyricFont, Is.Empty, "no key means no font, never a fabricated one");
            Assert.That(without.LyricFontFile, Is.Empty);
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
            // Stars (LyricDifficulty): "ab cd" rates EXACTLY ZERO since LyricPace v22. It is five
            // cells, and the chunked axis's 16-character floor prices a map with no window holding
            // that many at nothing (0.82 on the v21 chunked grid, 0.67 under the envelope model at
            // the 12.0 anchor, 0.59 at the 10.6 anchor, 0.63 under the strain model before that).
            Assert.That(diff.Pace.DifficultyRating, Is.Zero);

            // The rolling-window columns (028_wpm_curve.sql) are NULL here, and that is the
            // unmeasurable arm of their contract rather than an omission: "ab cd" is 5 cells
            // against LyricWpmCurve.WINDOW_CELLS = 30, so there is not one window to read.
            Assert.That(diff.PeakWpm, Is.Null);
            Assert.That(diff.PeakCpm, Is.Null);

            // target_wpm (033_target_wpm.sql) is NOT on that contract, and that is still the point,
            // though for a different reason since LyricPace v22: the target is the map's hardest
            // window by raw speed read off the difficulty model, FLOORED at the whole-map average.
            // The model finds no qualifying window here any more (five cells is under the
            // 16-character floor, where v21 read 21.84 WPM off the smallest scheduled window), so
            // the floor is what the column holds: a number, where peak_wpm is NULL.
            Assert.That(diff.TargetWpm, Is.EqualTo(diff.Pace.AverageWpm).Within(1e-12),
                "no window qualifies, so the target is the whole map's own pace");
            Assert.That(diff.TargetWpm, Is.EqualTo(20).Within(1e-9));

            // Last line end = min(song_end 4000, end_ms 3000 + 3000 tail) = 4000 ms.
            Assert.That(diff.TotalLengthS, Is.EqualTo(4.0).Within(1e-9));
            Assert.That(diff.DrainLengthS, Is.EqualTo(3.0).Within(1e-9));
        });
    }

    [Test]
    public void Parse_FreestyleLine_CountsTheSlotsSeparatelyFromTheTypedCells()
    {
        // A map blob carrying the editor's freestyle opt-in. Ingest must SEE the '&' slots, since
        // the star rating prices each at a quarter of a cell and the row stores their count; an
        // ampersand in an unflagged line stays lyric punctuation and is stripped as always.
        //
        // THEY ARE NOT IN char_count SINCE LyricPace v21, which is the reverse of what v6 decided
        // and is why this test is renamed rather than retuned. A slot takes ANY key, so no map can
        // ask for a particular speed in one, and the pace is about the speed a map asks for; the
        // count lives in freestyle_cell_count instead, as an addition to char_count rather than a
        // subset of it.
        //
        // THE LINE IS SIX TOKENS SINCE LyricPace v22 ("me & you" until then): its 7 cells rated
        // exactly zero under the chunked axis's 16-character floor with or without the slot, which
        // made the last assertion below vacuous.
        const string lyrics =
            """
            {"granularity":"word","version":2,"song_end_ms":20000}
            {"text":"take me & with you tonight","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"take","start_ms":1000,"end_ms":1500},{"text":"me","start_ms":1500,"end_ms":2000},{"text":"&","start_ms":2000,"end_ms":2500},{"text":"with","start_ms":2500,"end_ms":3000},{"text":"you","start_ms":3000,"end_ms":3500},{"text":"tonight","start_ms":3500,"end_ms":4000}]}
            """;

        const string legacyLyrics =
            """
            {"version":2,"song_end_ms":20000}
            {"text":"take me & with you tonight","start_ms":1000,"end_ms":4000}
            """;

        var free = BeatmapPackageParser.ParseDifficulty("free.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));
        var legacy = BeatmapPackageParser.ParseDifficulty("legacy.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: legacyLyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(free.Lines[0].RawText, Is.EqualTo("take me & with you tonight"));
            Assert.That(free.Pace.TypeableCellCount, Is.EqualTo(25)); // 20 letters + 5 spaces, and NOT the slot
            Assert.That(free.Pace.FreestyleCellCount, Is.EqualTo(1)); // which is counted here instead
            Assert.That(free.Pace.WordCount, Is.EqualTo(5), "a token of nothing but a slot asks for no typing");

            Assert.That(legacy.Lines[0].RawText, Is.EqualTo("take me with you tonight"));
            Assert.That(legacy.Pace.TypeableCellCount, Is.EqualTo(24));
            Assert.That(legacy.Pace.FreestyleCellCount, Is.Zero);
            Assert.That(legacy.Pace.WordCount, Is.EqualTo(5));

            // THE SLOT STILL COSTS SOMETHING, which is the half of the old claim that survives: it
            // is a cell of the map with a deadline, priced at a quarter by the star rating, so the
            // flagged map rates above the one where the same ampersand is stripped as punctuation.
            Assert.That(legacy.Pace.DifficultyRating, Is.GreaterThan(0), "the premise: the line clears the character floor");
            Assert.That(free.Pace.DifficultyRating, Is.Not.EqualTo(legacy.Pace.DifficultyRating));
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
            {"text":"we Type# at 100% Night","start_ms":4000,"end_ms":8000}
            """;

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.That(diff.LyricsText, Is.EqualTo("Neon SKYLINE, glowing!\nwe Type at 100% Night"));
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

    /// <summary>
    /// THE FORMAT VERSION GATE (backlog 255). The magic line is a prefix, so every version routes
    /// through the same parse; the number on it decides one thing, whether a bracket in a stored
    /// [Lyrics] line is a backing vocal to strip or a literal lyric mark to keep. An unversioned or
    /// unreadable line falls back to v1, the direction that cannot invent lyric content.
    /// </summary>
    [Test]
    public void ParseFormatVersion_ReadsTheDigitsAfterTheMagicAndFallsBackToV1()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapPackageParser.ParseFormatVersion("type!beat file format v1"), Is.EqualTo(1));
            Assert.That(BeatmapPackageParser.ParseFormatVersion("type!beat file format v2"), Is.EqualTo(2));
            Assert.That(BeatmapPackageParser.ParseFormatVersion("type!beat file format v17"), Is.EqualTo(17));

            // Only the digits immediately after the prefix are taken, so trailing junk is ignored
            // rather than fatal.
            Assert.That(BeatmapPackageParser.ParseFormatVersion("type!beat file format v2 (edited)"), Is.EqualTo(2));

            Assert.That(BeatmapPackageParser.ParseFormatVersion("type!beat file format v"), Is.EqualTo(BeatmapPackageParser.FallbackFormatVersion));
            Assert.That(BeatmapPackageParser.ParseFormatVersion("osu file format v14"), Is.EqualTo(BeatmapPackageParser.FallbackFormatVersion));
            Assert.That(BeatmapPackageParser.ParseFormatVersion(""), Is.EqualTo(BeatmapPackageParser.FallbackFormatVersion));

            Assert.That(BeatmapPackageParser.FallbackFormatVersion, Is.EqualTo(1));
            Assert.That(BeatmapPackageParser.LiteralBracketsFromVersion, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// A v1 file's brackets are backing vocals and are stripped exactly as they always were: an
    /// already-installed map re-parses byte-identically, which is the whole reason the gate is on
    /// the version rather than on the calendar.
    /// </summary>
    [Test]
    public void Parse_V1File_StillStripsBackingVocals()
    {
        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(
            SyntheticPackage.OsuText(lyrics: BracketLyrics, formatVersion: BeatmapPackageParser.FallbackFormatVersion)));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines.Select(l => l.RawText), Is.EqualTo(new[] { "hey now" }),
                "the span goes, and the whole-line backing vocal takes the line with it");
            Assert.That(diff.Pace.WordCount, Is.EqualTo(2));
            Assert.That(diff.Pace.TypeableCellCount, Is.EqualTo(7), "\"hey now\", spaces included");
        });
    }

    /// <summary>
    /// A v2 file, which is what the game's writer stamps now, keeps its brackets in the stored
    /// lyric and counts the cells the player really types through them.
    /// </summary>
    [Test]
    public void Parse_V2File_KeepsLiteralBrackets()
    {
        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(
            SyntheticPackage.OsuText(lyrics: BracketLyrics, formatVersion: BeatmapPackageParser.LiteralBracketsFromVersion)));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines.Select(l => l.RawText), Is.EqualTo(new[] { "hey (oh yeah) now", "(ooh aah)" }));
            Assert.That(diff.LyricsText, Does.Contain("(oh yeah)"), "the haystack keeps the author's form");

            // The marks themselves are never cells; what they no longer do is delete the words
            // between them.
            Assert.That(diff.Pace.WordCount, Is.EqualTo(6));
            Assert.That(diff.Pace.TypeableCellCount, Is.EqualTo(22), "\"hey oh yeah now\" + \"ooh aah\", spaces included");
        });
    }

    /// <summary>
    /// The underscore and the tilde joined the supported marks in backlog 255, so a stored line
    /// keeps them while the DEFAULT stream every stat is measured on still deletes them.
    /// </summary>
    [Test]
    public void Parse_UnderscoreAndTilde_SurviveIntoTheStoredLyric()
    {
        const string lyrics =
            """
            {"version":2,"song_end_ms":20000}
            {"text":"well_known ~vibe~","start_ms":1000,"end_ms":4000}
            """;

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu",
            SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines[0].RawText, Is.EqualTo("well_known ~vibe~"));
            Assert.That(diff.Pace.WordCount, Is.EqualTo(2));
            Assert.That(diff.Pace.TypeableCellCount, Is.EqualTo(14), "\"wellknown vibe\", the space included");
        });
    }

    /// <summary>
    /// One bracketed span inside a line, and one line that is nothing but a bracketed span: the two
    /// shapes the version gate has to separate.
    /// </summary>
    private const string BracketLyrics =
        """
        {"version":2,"song_end_ms":20000}
        {"text":"hey (oh yeah) now","start_ms":1000,"end_ms":4000}
        {"text":"(ooh aah)","start_ms":5000,"end_ms":6000}
        """;

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
