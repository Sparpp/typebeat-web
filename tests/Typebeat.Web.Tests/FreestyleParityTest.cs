using System.Text.Json;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for FREESTYLE characters in the browser scoring core. A mapper types '&amp;' into
/// a lyric line and gets a cell ANY key satisfies, whose pressed char is then displayed; judgement
/// is otherwise a completely normal typeable cell. The hand-written wwwroot/js/typebeat-core.js
/// must reproduce the desktop game's rules exactly, or /play scores on freestyle maps diverge from
/// desktop and corrupt the shared leaderboards.
///
/// <para>Golden values mirror typebeat-osu's NonVisual/FreestyleCharTest.cs, whose fixture map is
/// one line "a&amp;b" over a single word spanning [1000, 4000] (cells at 1000 / 2000 / 3000), plus
/// its storage cases ("me &amp; you" with and without the <c>"freestyle": true</c> opt-in).</para>
///
/// <para>A Node harness (Js/CoreFreestyleHarness.cjs) drives the actual shipped JS and emits its
/// observations; this asserts them. Assert.Ignore when node is absent.</para>
/// </summary>
public class FreestyleParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreFreestyleHarness.cjs");

    private static string Str(JsonElement e, string key) => e.GetProperty(key).GetString()!;

    private static double Num(JsonElement e, string key) => e.GetProperty(key).GetDouble();

    private static bool Flag(JsonElement e, string key) => e.GetProperty(key).GetBoolean();

    [Test]
    public void MarkerIsNotTypeableAndIsStrippedByDefault()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // The marker stays outside the typeable surface (nothing can be typed to produce it)
            // and outside default normalization, which is what keeps it invisible to every legacy
            // path. It is still a CELL.
            Assert.That(Flag(root, "isTypeableMarker"), Is.False);
            Assert.That(Flag(root, "isFreestyleMarker"), Is.True);
            Assert.That(Flag(root, "isCellMarker"), Is.True);

            Assert.That(Str(root, "normalizeDefault"), Is.EqualTo("RB rock roll"));

            // Opted in, the markers survive; everything else normalizes exactly as before
            // (punctuation dropped, whitespace collapsed and trimmed).
            Assert.That(Str(root, "normalizeKept"), Is.EqualTo("R&B rock & roll"));
            Assert.That(Str(root, "normalizeKeptPunctuation"), Is.EqualTo("hey &you"));
        });
    }

    [Test]
    public void FreestyleCellIsACellAndIsTimedLikeALetter()
    {
        var free = Harness().GetProperty("freestyleShape");

        Assert.Multiple(() =>
        {
            Assert.That(Str(free, "text"), Is.EqualTo("a&b"));
            Assert.That(Num(free, "count"), Is.EqualTo(3));
            Assert.That(Num(free, "freestyleCount"), Is.EqualTo(1));
            Assert.That(Flag(free, "typeable"), Is.True);

            // The slot takes its share of the word's span: 1000 + j*(4000-1000)/3.
            Assert.That(JsHarness.Doubles(free, "targets"), Is.EqualTo(new[] { 1000d, 2000d, 3000d }));

            var flags = free.GetProperty("freestyle").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
            Assert.That(flags, Is.EqualTo(new[] { false, true, false }));
        });
    }

    [TestCase("anyKeyQ", "q")]
    [TestCase("anyKeyZ", "Z")]
    [TestCase("anyKey7", "7")]
    [TestCase("anyKeySpace", " ")] // "any key" really is any key on the typeable surface.
    public void AnyKeyFillsAFreestyleCellAndTheTypedCharIsKept(string key, string pressed)
    {
        var r = Harness().GetProperty(key);

        Assert.Multiple(() =>
        {
            Assert.That(Num(r, "activeLineIndex"), Is.EqualTo(0));
            Assert.That(Flag(r, "first"), Is.True);
            Assert.That(Flag(r, "accepted"), Is.True);
            Assert.That(Str(r, "state"), Is.EqualTo("correct"));
            Assert.That(Str(r, "typedChar"), Is.EqualTo(pressed));
            Assert.That(Str(r, "judgeType"), Is.EqualTo("Perfect")); // on target
            Assert.That(Num(r, "judgedDelta"), Is.EqualTo(0));
            Assert.That(Num(r, "caretIndex"), Is.EqualTo(2));
            Assert.That(Num(r, "combo"), Is.EqualTo(2));
            Assert.That(Num(r, "consecutiveWrongKeys"), Is.EqualTo(0));
            Assert.That(Num(r, "liveAccuracy"), Is.EqualTo(1.0)); // no error was recorded
        });
    }

    [Test]
    public void FreestyleCellScoresExactlyLikeAnOrdinaryCell()
    {
        var root = Harness();
        var free = root.GetProperty("freeRun");
        var plain = root.GetProperty("plainRun");

        Assert.Multiple(() =>
        {
            // Same map shape, letters only, played identically: every submitted number must match.
            foreach (string field in new[] { "totalCells", "engineScore", "totalScore", "maxCombo", "accuracy", "completion", "wpm", "liveAccuracy" })
                Assert.That(Num(free, field), Is.EqualTo(Num(plain, field)), field);

            Assert.That(Str(free, "rank"), Is.EqualTo(Str(plain, "rank")));
            Assert.That(Num(free.GetProperty("counts"), "great"), Is.EqualTo(Num(plain.GetProperty("counts"), "great")));

            // And the absolute values, so a change to BOTH sides still trips this.
            Assert.That(Num(free, "engineScore"), Is.EqualTo(918)); // 300 + 306 + 312, combo multiplier
            Assert.That(Num(free, "totalScore"), Is.EqualTo(1000000));
            Assert.That(Num(free, "completion"), Is.EqualTo(1.0));
            Assert.That(Str(free, "rank"), Is.EqualTo("X"));
            Assert.That(Num(free.GetProperty("counts"), "great"), Is.EqualTo(3));
        });
    }

    [Test]
    public void LiterateModDoesNotConstrainAFreestyleCell()
    {
        var r = Harness().GetProperty("literate");

        Assert.Multiple(() =>
        {
            // A capital would be judged wrong on a normal lower-case target under Literate (the
            // control); the freestyle slot accepts it regardless of case.
            Assert.That(Flag(r, "accepted"), Is.True);
            Assert.That(Str(r, "state"), Is.EqualTo("correct"));
            Assert.That(Str(r, "typedChar"), Is.EqualTo("Q"));
            Assert.That(Num(r, "combo"), Is.EqualTo(2));
            Assert.That(Num(r, "liveAccuracy"), Is.EqualTo(1.0));

            Assert.That(Str(r, "controlState"), Is.EqualTo("untyped"));
            Assert.That(Num(r, "controlWrongKeys"), Is.EqualTo(1));
        });
    }

    [Test]
    public void MashingModLeavesTheTypedCharIntact()
    {
        var r = Harness().GetProperty("mashing");

        Assert.Multiple(() =>
        {
            // Mashing rewrites the pressed char to the cell's expected one (the control); on a
            // freestyle cell that would stamp the authoring marker over the player's char, so it
            // must not apply there.
            Assert.That(Str(r, "typedChar"), Is.EqualTo("q"));
            Assert.That(Str(r, "typedChar"), Is.Not.EqualTo("&"));
            Assert.That(Str(r, "state"), Is.EqualTo("correct"));
            Assert.That(Num(r, "combo"), Is.EqualTo(2));

            Assert.That(Str(r, "controlTypedChar"), Is.EqualTo("a"));
            Assert.That(Str(r, "controlState"), Is.EqualTo("correct"));
        });
    }

    [Test]
    public void BackspaceReopensAFreestyleCellAndANewCharLands()
    {
        var r = Harness().GetProperty("backspace");

        Assert.Multiple(() =>
        {
            Assert.That(Str(r, "reopenedState"), Is.EqualTo("untyped"));
            Assert.That(r.GetProperty("reopenedChar").ValueKind, Is.EqualTo(JsonValueKind.Null)); // shimmer resumes
            Assert.That(Num(r, "reopenedCaret"), Is.EqualTo(1));

            Assert.That(Flag(r, "accepted"), Is.True);
            Assert.That(Str(r, "retypedChar"), Is.EqualTo("7"));

            // Retyping a once-correct cell is scoring-inert (the first judgement stands), exactly
            // as for a normal cell; the only visible change is the displayed char.
            Assert.That(Num(r, "scoreAfterRetype"), Is.EqualTo(Num(r, "scoreAfterFirst")));
            Assert.That(Num(r, "judgedDelta"), Is.EqualTo(0));
        });
    }

    [Test]
    public void UntypedFreestyleCellSealsAsAMiss()
    {
        var r = Harness().GetProperty("sealed");

        Assert.Multiple(() =>
        {
            Assert.That(Str(r, "state"), Is.EqualTo("missed"));
            Assert.That(Num(r, "missCount"), Is.EqualTo(2)); // the slot and 'b'
            Assert.That(Num(r, "completion"), Is.EqualTo(1d / 3).Within(1e-12));
            Assert.That(Str(r, "rank"), Is.EqualTo("D"));
        });
    }

    [Test]
    public void AmpersandsInAnUnflaggedLineStayLyricPunctuation()
    {
        var r = Harness().GetProperty("legacyShape");

        Assert.Multiple(() =>
        {
            // Back-compat pin: a line whose lyrics genuinely contain "&" must decode exactly as it
            // always did, marker stripped, no freestyle cells.
            Assert.That(Str(r, "text"), Is.EqualTo("me you"));
            Assert.That(Num(r, "count"), Is.EqualTo(6));
            Assert.That(Num(r, "freestyleCount"), Is.EqualTo(0));
        });
    }

    [Test]
    public void AmpersandsInAFlaggedLineDecodeAsFreestyleCells()
    {
        var r = Harness().GetProperty("flaggedShape");

        Assert.Multiple(() =>
        {
            Assert.That(Str(r, "text"), Is.EqualTo("me & you"));
            Assert.That(Str(r, "expected"), Is.EqualTo("me & you"));
            // m e _ & _ y o u: 6 letters, the slot, 2 spaces.
            Assert.That(Num(r, "count"), Is.EqualTo(8));
            Assert.That(Num(r, "freestyleCount"), Is.EqualTo(1));

            var flags = r.GetProperty("freestyle").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
            Assert.That(flags, Is.EqualTo(new[] { false, false, false, true, false, false, false, false }));

            // The slot is the whole of its word unit, so it targets that unit's start.
            Assert.That(JsHarness.Doubles(r, "targets")[3], Is.EqualTo(2000));
        });
    }

    /// <summary>
    /// Third leg of the parity triangle: the SERVER's ingest pipeline (Typeability / LyricTiming /
    /// LyricPace, what stamps a beatmap row's char_count, word_count, wpm and stars at upload)
    /// counts exactly the cells the browser core flattens for the same .osu. Leaderboards take
    /// their statistics from the client, so a divergence here is metadata quality, not scoring,
    /// but an undercounted freestyle map would still show the wrong pace in song select.
    /// </summary>
    [TestCase("freestyleShape", """{"granularity":"word","version":2,"song_end_ms":20000}""", """{"text":"a&b","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"a&b","start_ms":1000,"end_ms":4000,"score":1}]}""")]
    [TestCase("plainShape", """{"granularity":"word","version":2,"song_end_ms":20000}""", """{"text":"axb","start_ms":1000,"end_ms":4000,"words":[{"text":"axb","start_ms":1000,"end_ms":4000,"score":1}]}""")]
    [TestCase("legacyShape", """{"version":2,"song_end_ms":20000}""", """{"text":"me & you","start_ms":1000,"end_ms":4000}""")]
    [TestCase("flaggedShape", """{"granularity":"word","version":2,"song_end_ms":20000}""", """{"text":"me & you","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"me","start_ms":1000,"end_ms":2000},{"text":"&","start_ms":2000,"end_ms":3000},{"text":"you","start_ms":3000,"end_ms":4000}]}""")]
    public void ServerIngestCountsTheSameCellsTheBrowserFlattens(string shape, string header, string line)
    {
        var browser = Harness().GetProperty(shape);

        var (_, lines) = LyricTiming.ParseSection([header, line]);
        var pace = LyricPace.Compute(lines);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].RawText, Is.EqualTo(Str(browser, "text")));
            Assert.That(pace.TypeableCellCount, Is.EqualTo(Num(browser, "count")));
        });
    }

    [Test]
    public void ShimmerNeverShowsTheMarkerAndVariesOverTime()
    {
        var r = Harness().GetProperty("shimmer");

        Assert.Multiple(() =>
        {
            // Display-only, but it is the promise that a freestyle cell never renders the raw '&'.
            // The mix is a line-for-line port of FreestyleGlyphs.Glyph, so the browser shimmers
            // through the identical glyph sequence as the desktop client.
            Assert.That(Flag(r, "markerSeen"), Is.False);
            Assert.That(Flag(r, "deterministic"), Is.True);
            Assert.That(Num(r, "distinctOver40Ticks"), Is.GreaterThan(5));
            Assert.That(Flag(r, "neighboursDiffer"), Is.True);
            Assert.That(Flag(r, "tickHoldsWithinInterval"), Is.True);
            Assert.That(Flag(r, "tickAdvances"), Is.True);
        });
    }
}
