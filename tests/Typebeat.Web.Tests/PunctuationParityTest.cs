using System.Text.Json;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the PUNCTUATION derivation in the browser scoring core.
///
/// <para>A map stores the author's punctuated, case-sensitive line; what the player types (and
/// sees) is derived from it: the stripped lower-case DEFAULT stream normally, the authored line
/// verbatim under the desktop client's Literate mod. /play scores land on the SAME leaderboards as
/// desktop ones, so the hand-written wwwroot/js/typebeat-core.js has to derive exactly what the C#
/// does, char for char, or a browser play of any punctuated map scores against a different set of
/// cells and corrupts the boards.</para>
///
/// <para>The strong assertion here is <see cref="DerivationMatchesTheServersOwnTypeability"/>: the
/// Node harness runs a battery of strings through the SHIPPED JS and this test runs the SAME
/// strings through <see cref="Typeability"/> (itself the port of the game's authority), comparing
/// pairwise. Nothing is hardcoded twice, so the two implementations cannot silently drift apart.
/// The remaining tests pin the cell shapes and the score against golden values shared with
/// typebeat-osu's NonVisual/LiteratePunctuationTest.cs.</para>
///
/// <para>Note that the browser player NEVER enables Literate (see play.js / mountPlayer): /play is
/// always vanilla. The literate branch exists so buildCells stays a line-for-line mirror of
/// TypingLine.FromLyricLine and so this harness can pin it, exactly as engine.caseSensitive
/// already is.</para>
/// </summary>
public class PunctuationParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CorePunctuationHarness.cjs");

    private static string[] Strings(JsonElement root, string key) =>
        root.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Test]
    public void TheSupportedSetIsTheSameOnBothSides()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("punctuationConstant").GetString(), Is.EqualTo(Typeability.PUNCTUATION));
            Assert.That(root.GetProperty("wordBreakConstant").GetString(), Is.EqualTo(Typeability.WORD_BREAK.ToString()));

            // A mark is never a plain typeable char / cell on either side: that predicate feeds the
            // interpolation weights and the pace counts, neither of which may see punctuation.
            Assert.That(root.GetProperty("marksAreNotTypeable").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("marksAreRecognised").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("unsupportedAreNot").GetBoolean(), Is.True);

            foreach (char c in Typeability.PUNCTUATION)
            {
                Assert.That(Typeability.IsPunctuation(c), Is.True, $"'{c}'");
                Assert.That(Typeability.IsTypeable(c), Is.False, $"'{c}'");
                Assert.That(Typeability.IsCell(c), Is.False, $"'{c}'");
            }
        });
    }

    /// <summary>
    /// The seven marks backlog 202 added ($ % ^ * &lt; &gt; /) behave exactly like the original
    /// thirteen, which is what makes the widening safe for every stored per-map figure.
    /// </summary>
    [Test]
    public void TheMarksAddedByBacklog202AreDeletedFromTheDefaultStreamLikeAnyOther()
    {
        Assert.Multiple(() =>
        {
            foreach (char c in "$%^*<>/")
            {
                Assert.That(Typeability.IsPunctuation(c), Is.True, $"'{c}' is supported now");
                Assert.That(Typeability.IsTypeable(c), Is.False, $"'{c}' is not a plain typeable char");
                Assert.That(Typeability.IsCell(c), Is.False, $"'{c}' is not a cell");
                Assert.That(Typeability.DefaultChar(c), Is.Null, $"'{c}' is deleted without Literate");
            }

            // Normalize KEEPS them now, where it used to strip them outright, so the author's form
            // survives for the Literate branch to type.
            const string authored = "100% up/down x^2 <so> 2*3 $";
            Assert.That(Typeability.Normalize(authored), Is.EqualTo(authored));

            // The DEFAULT stream, which every stored stat is measured on, still drops them, so a
            // mark wedged inside a word derives exactly what it derived before it was supported.
            Assert.That(Typeability.ToDefaultStream("up/down"), Is.EqualTo("updown"));
            Assert.That(Typeability.ToDefaultStream("100% off"), Is.EqualTo("100 off"));
            Assert.That(Typeability.ToDefaultStream("x^2 and 3*4"), Is.EqualTo("x2 and 34"));

            // A mark standing as its OWN token is the one shape that does not, and it behaves
            // exactly as an original mark always has: both authored spaces around it survive, so
            // the stream carries one more cell than it would with the mark absent. Nothing stored
            // moves because of it, since LyricPace.VERSION is deliberately not bumped and only its
            // sweep re-derives an existing row (see the note on LyricPace.VERSION).
            Assert.That(Typeability.ToDefaultStream("ride / or"), Is.EqualTo("ride  or"));
            Assert.That(Typeability.ToDefaultStream("ride , or"), Is.EqualTo("ride  or"));
        });
    }

    [Test]
    public void DerivationMatchesTheServersOwnTypeability()
    {
        var root = Harness();

        string[] samples = Strings(root, "samples");
        string[] jsNormalized = Strings(root, "normalized");
        string[] jsDefault = Strings(root, "defaultStream");
        string[] jsNormalizedThenDefault = Strings(root, "normalizedThenDefault");

        Assert.That(samples, Has.Length.GreaterThan(15), "the harness must carry a real battery");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < samples.Length; i++)
            {
                string s = samples[i];

                // The JS normalize() folds backing vocals in (stripBackingVocals is called inside
                // it), so the C# side has to be composed the same way to compare.
                Assert.That(jsNormalized[i], Is.EqualTo(Typeability.Normalize(Typeability.StripBackingVocals(s))),
                    $"normalize(\"{s}\")");

                Assert.That(jsDefault[i], Is.EqualTo(Typeability.ToDefaultStream(s)),
                    $"toDefaultStream(\"{s}\")");

                Assert.That(jsNormalizedThenDefault[i],
                    Is.EqualTo(Typeability.ToDefaultStream(Typeability.Normalize(Typeability.StripBackingVocals(s)))),
                    $"the real pipeline on \"{s}\"");
            }
        });
    }

    [Test]
    public void TheNormativeExampleFlattensIdenticallyOnBothBranches()
    {
        var root = Harness();
        var plain = root.GetProperty("plainShape");
        var literate = root.GetProperty("literateShape");

        // Golden values shared with the game's LiteratePunctuationTest: one line
        // "The bad-cat sat." over words The [1000,2000], bad-cat [2000,4000], sat. [4000,6000].
        // "bad-cat" has k = 6 letters, step 2000/6, so d = 2000+2*(2000/6) and c = 2000+3*(2000/6);
        // the hyphen splits that step in half, and the default stream's space inherits its slot.
        double step = 2000.0 / 6;
        double hyphen = (2000 + 2 * step + (2000 + 3 * step)) / 2;

        Assert.Multiple(() =>
        {
            Assert.That(plain.GetProperty("text").GetString(), Is.EqualTo("The bad-cat sat."), "the stored line is the author's form");
            Assert.That(plain.GetProperty("stream").GetString(), Is.EqualTo("the bad cat sat"), "what the player types AND sees");
            Assert.That(plain.GetProperty("count").GetInt32(), Is.EqualTo(15));

            Assert.That(literate.GetProperty("text").GetString(), Is.EqualTo("The bad-cat sat."));
            Assert.That(literate.GetProperty("stream").GetString(), Is.EqualTo("The bad-cat sat."));
            Assert.That(literate.GetProperty("count").GetInt32(), Is.EqualTo(16));

            // Every cell is typed on both branches.
            foreach (var shape in new[] { plain, literate })
            {
                foreach (var t in shape.GetProperty("typeable").EnumerateArray())
                    Assert.That(t.GetBoolean(), Is.True);
            }

            double[] plainTargets = JsHarness.Doubles(plain, "targets");
            double[] literateTargets = JsHarness.Doubles(literate, "targets");

            // The hyphen's own slot, inherited by the default stream's space cell (index 7).
            Assert.That(plainTargets[7], Is.EqualTo(hyphen).Within(1e-9));
            Assert.That(literateTargets[7], Is.EqualTo(hyphen).Within(1e-9));

            // The trailing '.' attaches to the preceding char ('t' of "sat.").
            Assert.That(literateTargets[15], Is.EqualTo(literateTargets[14]).Within(1e-9));

            // Turning the mod on adds cells without moving any existing one: the first 15 targets
            // are identical, and the 16th is the mark that was appended.
            for (int i = 0; i < 15; i++)
                Assert.That(literateTargets[i], Is.EqualTo(plainTargets[i]).Within(1e-9), $"target {i}");
        });
    }

    [Test]
    public void APerfectBrowserPlayOfAPunctuatedMapStillScoresAMaxRun()
    {
        var root = Harness();
        var plain = root.GetProperty("plainRun");
        var literate = root.GetProperty("literateRun");

        Assert.Multiple(() =>
        {
            foreach (var run in new[] { plain, literate })
            {
                Assert.That(run.GetProperty("finished").GetBoolean(), Is.True);
                Assert.That(run.GetProperty("allCorrect").GetBoolean(), Is.True);
                Assert.That(run.GetProperty("accuracy").GetDouble(), Is.EqualTo(1.0));
                Assert.That(run.GetProperty("completion").GetDouble(), Is.EqualTo(1.0));
                Assert.That(run.GetProperty("rank").GetString(), Is.EqualTo("X"));
                Assert.That(run.GetProperty("totalScore").GetDouble(), Is.EqualTo(1_000_000));
            }

            // The one difference between the branches is how many keypresses the line costs.
            Assert.That(plain.GetProperty("maxCombo").GetInt32(), Is.EqualTo(15));
            Assert.That(literate.GetProperty("maxCombo").GetInt32(), Is.EqualTo(16));
        });
    }

    [Test]
    public void ServerIngestCountsTheSameCellsTheBrowserFlattensForAPunctuatedMap()
    {
        var root = Harness();
        var plain = root.GetProperty("plainShape");

        // The third leg of the parity triangle: the SERVER'S own parse of the identical blob must
        // store the author's line and count the DEFAULT stream's cells, which is exactly what the
        // browser flattens and therefore what the player is judged on.
        const string lyrics =
            """
            {"granularity":"word","version":2,"song_end_ms":20000}
            {"text":"The bad-cat sat.","start_ms":1000,"end_ms":6000,"words":[{"text":"The","start_ms":1000,"end_ms":2000,"score":1},{"text":"bad-cat","start_ms":2000,"end_ms":4000,"score":1},{"text":"sat.","start_ms":4000,"end_ms":6000,"score":1}]}
            """;

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines, Has.Count.EqualTo(1));
            Assert.That(diff.Lines[0].RawText, Is.EqualTo(plain.GetProperty("text").GetString()));
            Assert.That(diff.LyricsText, Is.EqualTo("The bad-cat sat."), "the haystack keeps the author's form");

            Assert.That(diff.Pace.TypeableCellCount, Is.EqualTo(plain.GetProperty("count").GetInt32()));
            Assert.That(diff.Pace.WordCount, Is.EqualTo(4), "the hyphen is a word break in the stream the player types");
        });
    }

    [Test]
    public void ALineWithNothingToTypeIsStillDropped()
    {
        // A line that is nothing but punctuation now normalizes NON-empty, but it gives the player
        // no cell at all, so it must still vanish and let the previous line extend over its span,
        // exactly as a whole-line backing vocal does. Both sides test the DEFAULT stream for this.
        const string lyrics =
            """
            {"version":2,"song_end_ms":20000}
            {"text":"real one","start_ms":1000,"end_ms":2000}
            {"text":"...","start_ms":3000,"end_ms":4000}
            {"text":"real two","start_ms":5000,"end_ms":6000}
            """;

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyrics)));

        Assert.Multiple(() =>
        {
            Assert.That(diff.Lines.Select(l => l.RawText), Is.EqualTo(new[] { "real one", "real two" }));
            Assert.That(diff.Lines[0].EndTime, Is.EqualTo(5000), "the previous line extends over the dropped one");
        });
    }
}
