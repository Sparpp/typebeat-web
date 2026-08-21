using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the browser core's SYLLABIFIER (backlog 179). Since the game made
/// syllable-span judgement the live rule, a keypress on a grouped cell is graded against the sung
/// span of the syllable that cell belongs to, so which characters form a syllable is scoring
/// surface: a split one character off moves a real judgement, and the browser and the desktop
/// client submit to the SAME leaderboards.
///
/// <para>The harness carries the game's own <c>SyllabifierTest</c> corpus transcribed word for
/// word (the pinned splits are the byte-compat contract) and checks the SHIPPED
/// <c>wwwroot/js/typebeat-core.js</c> against it, reporting every mismatch by name. This asserts
/// the report is empty and that the corpus is still the whole list, so deleting cases to make it
/// pass fails here instead.</para>
///
/// <para>The transcription can only catch the JS drifting from what was transcribed. A rule that
/// changes on the C# side without this file moving is caught by
/// <c>Typebeat.WireCompat.EngineFuzzLiveParityTest.TheTwoSyllabifiersAgreeWordForWord</c>, which is
/// the only place that compiles both repos and can call the real
/// <c>Syllabifier</c>.</para>
/// </summary>
public class SyllabifierParityTest
{
    private static JsonElement RunHarness() => JsHarness.Run("CoreSyllabifierHarness.cjs");

    [Test]
    public void TheBrowserSyllabifierReproducesTheGameCorpus()
    {
        var root = RunHarness();

        var failures = new List<string>();

        foreach (var failure in root.GetProperty("failures").EnumerateArray())
            failures.Add(failure.GetString()!);

        Assert.That(failures, Is.Empty, string.Join("\n", failures));
    }

    [Test]
    public void TheCorpusIsStillTheWholePinnedWordList()
    {
        var root = RunHarness();

        // The game's own fixture asserts the same floor on the list it reads off its TestCase
        // attributes, so a case quietly dropped from the transcription is a failure here rather
        // than a silently narrower guard.
        Assert.That(root.GetProperty("corpusSize").GetInt32(), Is.GreaterThan(100), "the corpus should be the whole pinned word list");

        // Every probe carries an answer for all three entry points, which is what the cross-repo
        // check consumes.
        var words = root.GetProperty("words");
        Assert.That(words.GetArrayLength(), Is.GreaterThan(100));

        Assert.Multiple(() =>
        {
            foreach (var word in words.EnumerateArray())
            {
                string text = word.GetProperty("word").GetString()!;
                Assert.That(word.GetProperty("count").GetInt32(), Is.GreaterThanOrEqualTo(1), $"{text}: count");
                Assert.That(word.GetProperty("splits").GetArrayLength(), Is.EqualTo(word.GetProperty("count").GetInt32() - 1), $"{text}: splits vs count");
            }
        });
    }
}
