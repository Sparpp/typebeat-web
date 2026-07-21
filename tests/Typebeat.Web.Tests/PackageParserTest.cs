using System.Security.Cryptography;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests;

/// <summary>
/// DB-free parser tests over synthetic packages (fixtures modeled on the game's
/// LyricOsuFormat writer — see <see cref="SyntheticPackage"/>).
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
            Assert.That(diff.Pace.AverageWpm, Is.EqualTo(40).Within(1e-9));
            Assert.That(diff.Pace.AverageCpm, Is.EqualTo(100).Within(1e-9));
            // Strain-based stars (LyricDifficulty): "ab cd" -> per-word strain sum -> 0.61.
            Assert.That(diff.Pace.DifficultyRating, Is.EqualTo(0.61).Within(0.01));

            // Last line end = min(song_end 4000, end_ms 3000 + 3000 tail) = 4000 ms.
            Assert.That(diff.TotalLengthS, Is.EqualTo(4.0).Within(1e-9));
            Assert.That(diff.DrainLengthS, Is.EqualTo(3.0).Within(1e-9));
        });
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

    [Test]
    public void Parse_OverlongFilename_Throws422able()
    {
        string name = new string('a', BeatmapPackageParser.MaxFilenameLength + 1) + ".txt";

        using var zip = SyntheticPackage.Zip((name, SyntheticPackage.Utf8("x")));

        Assert.Throws<PackageValidationException>(() => BeatmapPackageParser.Parse(zip));
    }
}
