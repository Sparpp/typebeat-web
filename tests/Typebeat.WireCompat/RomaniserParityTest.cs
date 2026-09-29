using ClientRomaniser = typebeat.Game.Rulesets.TypeBeat.Beatmaps.Romaniser;
using ServerRomaniser = Typebeat.Web.Packages.Lyrics.Romaniser;
using ServerTypeability = Typebeat.Web.Packages.Lyrics.Typeability;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the ROMANISER (backlog 329): the game's
/// <c>Beatmaps/Romaniser.cs</c> and the server's <c>Packages/Lyrics/Romaniser.cs</c> must spell
/// every non-Latin lyric identically, or a map imported on one side stores different text from
/// the same map imported on the other.
///
/// <para>The fixtures are the GAME repo's own (<c>NonVisual/fixtures/romaniser/*.tsv</c>, one file
/// per script, real lyric lines with their expected romanisation), linked into this project's
/// output by the csproj, so the two repos are held to one set of expectations and not two copies of
/// it. Every line is run through BOTH copies and the whole result is compared: text, the flagged
/// characters and every unit's source and text span. The browser needs no third copy:
/// <c>typebeat-core.js</c> only ever decodes text a map already stores romanised.</para>
/// </summary>
[TestFixture]
public class RomaniserParityTest
{
    public sealed record Fixture(string File, int Line, string? Language, string Source, string Expected, string[] Unromanised)
    {
        public override string ToString() => $"{File}:{Line}";
    }

    private static string outputDir(string name) => Path.Combine(AppContext.BaseDirectory, name);

    public static IEnumerable<Fixture> LoadFixtures()
    {
        foreach (string path in Directory.GetFiles(outputDir("RomaniserFixtures"), "*.tsv").OrderBy(p => p, StringComparer.Ordinal))
        {
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0 || lines[i].StartsWith('#'))
                    continue;

                string[] cols = lines[i].Split('\t');
                yield return new Fixture(Path.GetFileName(path), i + 1, cols[0].Length == 0 ? null : cols[0], cols[1], cols[2],
                    cols.Length > 3 ? cols[3].Split(' ', StringSplitOptions.RemoveEmptyEntries) : []);
            }
        }
    }

    private static IEnumerable<TestCaseData> fixtureCases()
        => LoadFixtures().Select(f => new TestCaseData(f).SetName($"Romaniser({f})"));

    [TestCaseSource(nameof(fixtureCases))]
    public void BothCopiesRomaniseTheFixtureIdentically(Fixture f)
    {
        var client = ClientRomaniser.Romanise(f.Source, f.Language);
        var server = ServerRomaniser.Romanise(f.Source, f.Language);

        Assert.Multiple(() =>
        {
            Assert.That(server.Text, Is.EqualTo(client.Text), "text");
            Assert.That(server.Unromanised, Is.EqualTo(client.Unromanised), "unromanised");
            Assert.That(server.Units.Select(u => (u.SourceStart, u.SourceLength, u.TextStart, u.TextLength, u.Flagged)),
                Is.EqualTo(client.Units.Select(u => (u.SourceStart, u.SourceLength, u.TextStart, u.TextLength, u.Flagged))), "units");
            Assert.That(ServerRomaniser.HasNonLatin(f.Source), Is.EqualTo(ClientRomaniser.HasNonLatin(f.Source)), "HasNonLatin");
            Assert.That(ServerRomaniser.NeedsRomanising(f.Source, f.Language), Is.EqualTo(ClientRomaniser.NeedsRomanising(f.Source, f.Language)), "NeedsRomanising");

            // And both are what the fixture says, so an edit made identically to both copies still
            // has to answer to the expectations.
            Assert.That(server.Text, Is.EqualTo(f.Expected), "expected text");
            Assert.That(server.Unromanised, Is.EqualTo(f.Unromanised), "expected unromanised");

            if (server.IsComplete)
                Assert.That(ServerTypeability.Normalize(server.Text), Is.EqualTo(server.Text), "Normalize identity");
        });
    }

    [Test]
    public void TheFixturesAreThere()
    {
        var files = LoadFixtures().Select(f => f.File).Distinct().ToArray();

        Assert.That(files, Is.EquivalentTo(new[]
        {
            "armenian.tsv", "cyrillic.tsv", "georgian.tsv", "greek.tsv", "japanese.tsv", "korean.tsv", "latin.tsv", "unromanised.tsv",
        }), "the csproj link to the game repo's fixtures resolved to the wrong place, or a script lost its file");
    }

    /// <summary>
    /// The two files are the same text below their <c>using</c> lines except for the namespace
    /// line (the game's copy also carries the repo's licence header above them). Line endings are
    /// compared normalised, since a checkout may convert them.
    /// </summary>
    [Test]
    public void TheTwoCopiesDifferOnlyInTheirNamespace()
    {
        string game = body(File.ReadAllText(outputDir(Path.Combine("RomaniserMirror", "game.cs.txt"))));
        string server = body(File.ReadAllText(outputDir(Path.Combine("RomaniserMirror", "server.cs.txt"))));

        Assert.That(server, Is.EqualTo(game));

        static string body(string text)
        {
            text = text.Replace("\r\n", "\n");
            text = text.Substring(text.IndexOf("using System;", StringComparison.Ordinal));
            return string.Join("\n", text.Split('\n').Select(l => l.StartsWith("namespace ", StringComparison.Ordinal) ? "namespace" : l));
        }
    }
}
