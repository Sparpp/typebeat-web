using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests;

/// <summary>
/// Backlog 472: a map may declare several song languages. Pins the server's list rules
/// (<see cref="BeatmapLanguages.NormalizeList"/>, <see cref="BeatmapLanguages.Primary"/>) and how the package
/// parser reads the <c>[Metadata] Languages:</c> line beside the old single <c>Language:</c> line. The game's
/// half of each rule is pinned against these by WireCompat's <c>LanguageWireTest</c>.
/// </summary>
[TestFixture]
public class LanguageListTest
{
    [Test]
    public void NormalizeList_FoldsDropsDuplicatesAndKeepsOrder()
        => Assert.That(BeatmapLanguages.NormalizeList(["Korean", "unspecified", "en", "klingon", "korean", null, ""]),
            Is.EqualTo(new[] { "korean", "english" }));

    [Test]
    public void NormalizeList_KeepsInstrumentalOnlyAlone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapLanguages.NormalizeList(["instrumental", "english"]), Is.EqualTo(new[] { "instrumental" }));
            Assert.That(BeatmapLanguages.NormalizeList(["english", "instrumental"]), Is.EqualTo(new[] { "english" }));
            Assert.That(BeatmapLanguages.NormalizeList(["english", "other"]), Is.EqualTo(new[] { "english", "other" }));
        });
    }

    [TestCase(new string[0], "")]
    [TestCase(new[] { "english" }, "english")]
    [TestCase(new[] { "english", "japanese" }, "japanese")]
    [TestCase(new[] { "korean", "japanese" }, "korean")]
    [TestCase(new[] { "english", "other" }, "other")]
    public void Primary_IsTheFirstLanguageThatIsNotEnglish(string[] languages, string expected)
        => Assert.That(BeatmapLanguages.Primary(languages), Is.EqualTo(expected));

    [Test]
    public void Parser_ReadsTheListAndItsPrimary()
    {
        var diff = parse(SyntheticPackage.OsuText(language: "japanese", languages: "english,japanese"));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Languages, Is.EqualTo(new[] { "english", "japanese" }));
            Assert.That(diff.Language, Is.EqualTo("japanese"));
        });
    }

    [Test]
    public void Parser_ReadsALegacySingleLanguageFileAsAOneEntryList()
    {
        var diff = parse(SyntheticPackage.OsuText(language: "Russian"));

        Assert.That(diff.Languages, Is.EqualTo(new[] { "russian" }));
    }

    [Test]
    public void Parser_ReadsAFileWithNoLanguageAsAnEmptyList()
        => Assert.That(parse(SyntheticPackage.OsuText()).Languages, Is.Empty);

    [Test]
    public void Parser_LetsTheListWinOverADisagreeingLanguageLine()
    {
        // A hand-edited file: the list is the authority, and the single language follows it, so the ratings
        // (which read Language) agree with what the set stores.
        var diff = parse(SyntheticPackage.OsuText(language: "french", languages: "korean,english"));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Languages, Is.EqualTo(new[] { "korean", "english" }));
            Assert.That(diff.Language, Is.EqualTo("korean"));
        });
    }

    [Test]
    public void Parser_FallsBackToTheLanguageLineWhenTheListNamesNothingItKnows()
    {
        // A newer client's languages this server has never heard of.
        var diff = parse(SyntheticPackage.OsuText(language: "german", languages: "klingon,elvish"));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Languages, Is.EqualTo(new[] { "german" }));
            Assert.That(diff.Language, Is.EqualTo("german"));
        });
    }

    private static ParsedDifficulty parse(string osu) => BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(osu));
}
