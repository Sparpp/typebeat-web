using GameFileManifest = typebeat.Game.Beatmaps.SetFileManifest;
using ServerFileManifest = Typebeat.Web.Packages.SetFileManifest;

namespace Typebeat.WireCompat;

/// <summary>
/// The tracked-file manifest the UPDATE offer compares: the server builds it from a version's file list
/// (<see cref="ServerFileManifest"/>, published as <c>file_manifest</c>) and the game from its local copy's
/// (<see cref="GameFileManifest"/>), so both must spell the same files the same way, or a set would offer
/// an update forever (or never). The wire half, the DTO carrying the server's value to the client, is
/// pinned in <see cref="WireCompatTests"/> beside the stem flag it supersedes.
/// </summary>
[TestFixture]
public class SetFileManifestParityTest
{
    private static readonly string[][] file_lists =
    [
        [],
        ["audio.mp3", "bg.jpg", "Artist - Title (mapper) [type!beat].osu"],
        ["audio.mp3", "vocals.ogg"],
        ["VOCALS.WAV", "audio.mp3"],
        ["vocals.ogg", "vocals.wav"],
        ["stems/vocals.ogg", "vocals.ogg.bak"],
    ];

    [TestCaseSource(nameof(file_lists))]
    public void TheServerAndTheGameAgreeOnTheSameFiles(string[] filenames)
    {
        string server = ServerFileManifest.Of(filenames);

        Assert.Multiple(() =>
        {
            Assert.That(server, Is.EqualTo(GameFileManifest.Of(filenames)));
            Assert.That(GameFileManifest.Normalise(server), Is.EqualTo(server), "the server's spelling is already canonical");
            Assert.That(GameFileManifest.Online(server, hasVocalsStem: false), Is.EqualTo(server), "and the client reads it as published");
            Assert.That(ServerFileManifest.Names(server, ServerFileManifest.VocalsStem),
                Is.EqualTo(filenames.Any(typebeat.Game.Beatmaps.VocalsStem.IsStemFileName)), "the stem flag read off the manifest matches the game's stem test");
        });
    }

    [Test]
    public void TheStemKindIsSpelledTheSameOnBothSides()
        => Assert.That(ServerFileManifest.VocalsStem, Is.EqualTo(GameFileManifest.VOCALS_STEM));
}
