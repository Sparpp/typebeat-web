using System.Text;
using Typebeat.Web.Packages;
using typebeat.Game.Beatmaps;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the song-language contract (task 58).
///
/// Unlike the explicit-content flag, language does NOT travel as a JSON field: the only channel it
/// reaches the server on is the <c>[Metadata] Language:</c> line inside the uploaded package. That
/// makes the .osu text format the wire surface here, and it makes two things drift-prone in exactly
/// the way this harness exists to catch:
///
///  1. THE VOCABULARY. The game's <see cref="BeatmapLanguage"/> enum and the server's
///     <see cref="BeatmapLanguages.All"/> list must name the same set of languages, spelled the
///     same way. A language added to one side only would be written by the client and silently
///     folded to "unset" by the server (or offered by the search box and never matched).
///  2. THE SPELLING RULE. The client lowercases the member name; the server matches
///     case-insensitively and folds anything unrecognised to unset. Both halves are asserted below
///     against the OTHER repo's real code, not against a copied string list.
///
/// This file compiles only where the game repo is resolvable (a sibling checkout locally, the
/// pinned submodule in CI), which is the same condition every other test in this project has.
/// </summary>
[TestFixture]
public class LanguageWireTest
{
    /// <summary>Every game-side language that is an actual language (i.e. not the unset state).</summary>
    private static IEnumerable<BeatmapLanguage> realLanguages =>
        Enum.GetValues<BeatmapLanguage>().Where(l => l != BeatmapLanguage.Unspecified);

    [Test]
    public void ClientAndServerAgreeOnTheExactVocabulary()
    {
        var clientNames = realLanguages.Select(l => l.ToCanonicalName()).ToList();

        Assert.That(clientNames, Is.EquivalentTo(BeatmapLanguages.All),
            "the game's BeatmapLanguage enum and the server's BeatmapLanguages.All must name the same languages");
    }

    [Test]
    public void EveryClientLanguageSurvivesServerNormalisation()
    {
        foreach (var language in realLanguages)
        {
            string onTheWire = language.ToCanonicalName();

            Assert.That(BeatmapLanguages.Normalize(onTheWire), Is.EqualTo(onTheWire),
                $"{language} must reach beatmapsets.language unchanged");
        }
    }

    [Test]
    public void ServerNormalisationRoundTripsBackToTheClientEnum()
    {
        // The other direction: a value read out of the database (or typed into lang:) has to be
        // something the client can display and pre-select in its editor dropdown.
        foreach (string stored in BeatmapLanguages.All)
        {
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName(stored).ToCanonicalName(), Is.EqualTo(stored));
        }
    }

    [Test]
    public void UnspecifiedIsNotAStorableValue()
    {
        // The client's "not chosen yet" state must never become a stored language: it writes no
        // Language line at all, and the name itself folds to unset if some client sends it anyway.
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapLanguage.Unspecified.ToCanonicalName(), Is.Empty);
            Assert.That(BeatmapLanguages.Normalize("unspecified"), Is.EqualTo(BeatmapLanguages.Unset));
            Assert.That(BeatmapLanguages.All, Does.Not.Contain("unspecified"));
        });
    }

    [Test]
    public void ServerParsesTheLanguageLineTheClientWrites()
    {
        foreach (var language in realLanguages)
        {
            var diff = BeatmapPackageParser.ParseDifficulty("map.osu", osuWithLanguage(language.ToCanonicalName()));

            Assert.That(BeatmapLanguages.Normalize(diff.Language), Is.EqualTo(language.ToCanonicalName()), language.ToString());
        }
    }

    [Test]
    public void ServerTreatsAMissingLanguageLineAsUnset()
    {
        // Every pre-task-58 client, and every map whose mapper has not picked a language: the
        // server must read "not stated" and leave the stored value alone (PackageIngest).
        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", osuWithLanguage(null));

        Assert.That(BeatmapLanguages.Normalize(diff.Language), Is.EqualTo(BeatmapLanguages.Unset));
    }

    [Test]
    public void ServerFoldsAnUnknownLanguageToUnset_AndTheClientAgrees()
    {
        // A language a future client knows and this build does not: neither side may invent a value.
        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", osuWithLanguage("klingon"));

        Assert.Multiple(() =>
        {
            Assert.That(BeatmapLanguages.Normalize(diff.Language), Is.EqualTo(BeatmapLanguages.Unset));
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName("klingon"), Is.EqualTo(BeatmapLanguage.Unspecified));
        });
    }

    /// <summary>
    /// A minimal "type!beat file format v1" difficulty, shaped like the game's writer
    /// (LyricOsuFormat.GenerateOsu): the Language line sits in [Metadata] after Tags, and is
    /// omitted entirely when <paramref name="language"/> is null.
    /// </summary>
    private static byte[] osuWithLanguage(string? language)
    {
        var sb = new StringBuilder();

        sb.Append("type!beat file format v1\n\n");
        sb.Append("[General]\nAudioFilename: audio.mp3\nAudioLeadIn: 0\nPreviewTime: -1\n\n");
        sb.Append("[Metadata]\n");
        sb.Append("Title:Neon Nights\nTitleUnicode:Neon Nights\n");
        sb.Append("Artist:Synth Rider\nArtistUnicode:Synth Rider\n");
        sb.Append("Creator:wc_player\nVersion:type!beat\nTags:typing\n");

        if (language != null)
            sb.Append($"Language:{language}\n");

        sb.Append("\n[TimingPoints]\n0,500,4,2,0,100,1,0\n\n");
        sb.Append("[Lyrics]\n");
        sb.Append("{\"version\":2,\"song_end_ms\":4000}\n");
        sb.Append("{\"text\":\"ab cd\",\"start_ms\":1000,\"end_ms\":3000}\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
