using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests;

/// <summary>
/// The 422-able semantic invariants (PackageValidator) over synthetic parsed packages.
/// </summary>
public class PackageValidationTest
{
    private const long set_id = 1;
    private static readonly long[] allocated = [1001, 1002];

    private static ParsedPackage parse(params (string Name, byte[] Content)[] entries)
    {
        using var zip = SyntheticPackage.Zip(entries);
        return BeatmapPackageParser.Parse(zip);
    }

    private static ParsedPackage validPackage(string creator = "uploader")
        => parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(creator: creator))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

    [Test]
    public void ValidPackage_Passes()
        => Assert.DoesNotThrow(() => PackageValidator.Validate(validPackage(), set_id, allocated, "uploader"));

    [Test]
    public void CreatorMatch_IsCaseInsensitive()
        => Assert.DoesNotThrow(() => PackageValidator.Validate(validPackage(), set_id, allocated, "UPLOADER"));

    [Test]
    public void WrongCreator_Throws()
    {
        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(validPackage(creator: "somebody_else"), set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("creator"));
    }

    [Test]
    public void NoDifficulties_Throws()
    {
        var package = parse(("audio.mp3", SyntheticPackage.Utf8("audio")));

        Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));
    }

    [Test]
    public void TooManyDifficulties_Throws()
    {
        var entries = new List<(string, byte[])> { ("audio.mp3", SyntheticPackage.Utf8("audio")) };

        for (int i = 0; i < PackageValidator.MaxDifficulties + 1; i++)
            entries.Add(($"map{i}.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 1001 + i, version: $"v{i}"))));

        var package = parse(entries.ToArray());

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, Enumerable.Range(0, 200).Select(i => 1001L + i).ToArray(), "uploader"));

        Assert.That(ex!.Message, Does.Contain("129"));
    }

    [Test]
    public void CrossDiffMetadataMismatch_Throws()
    {
        var package = parse(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", beatmapId: 1001))),
            ("b.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Different Title", beatmapId: 1002, version: "hard"))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("Title"));
    }

    [Test]
    public void WrongEmbeddedSetId_Throws()
    {
        var package = parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapSetId: 999))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("BeatmapSetID"));
    }

    [Test]
    public void MissingEmbeddedSetId_Throws()
    {
        var package = parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapSetId: null))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));
    }

    [Test]
    public void UnallocatedBeatmapId_Throws()
    {
        var package = parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 5555))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("5555"));
    }

    [Test]
    public void DuplicateBeatmapIds_Throw()
    {
        var package = parse(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 1001))),
            ("b.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 1001, version: "hard"))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));
    }

    [Test]
    public void MissingAudioFile_Throws()
    {
        var package = parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(audioFilename: "gone.mp3"))));

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(package, set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("gone.mp3"));
    }

    [Test]
    public void AudioFilenameLookup_IsCaseInsensitive()
    {
        var package = parse(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(audioFilename: "AUDIO.MP3"))),
            ("audio.mp3", SyntheticPackage.Utf8("audio")));

        Assert.DoesNotThrow(() => PackageValidator.Validate(package, set_id, allocated, "uploader"));
    }

    [Test]
    public void OversizedPackage_Throws()
    {
        // PackageSize comes from the stream length; fake it rather than allocating 95 MB.
        var real = validPackage();
        var oversized = new ParsedPackage
        {
            Files = real.Files,
            Difficulties = real.Difficulties,
            PackageSize = PackageValidator.MaxPackageBytes + 1,
        };

        var ex = Assert.Throws<PackageValidationException>(
            () => PackageValidator.Validate(oversized, set_id, allocated, "uploader"));

        Assert.That(ex!.Message, Does.Contain("size limit"));
    }
}
