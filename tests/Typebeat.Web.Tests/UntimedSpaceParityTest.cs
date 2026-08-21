using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for UNTIMED SPACES in the browser scoring core (backlog 148). A space typed on a
/// SPACE CELL is judged as though it landed dead on that cell's target, so it takes the top tier
/// whatever the clock said and can never fall into one of the two zero-point tiers that break combo.
/// The hand-written wwwroot/js/typebeat-core.js must reproduce the desktop rule exactly, or /play
/// scores diverge from desktop on the SHARED leaderboards: this one changes what a word gap is
/// worth, which is every few characters of every map.
///
/// <para>Golden values mirror typebeat-osu's NonVisual/UntimedSpaceTest.cs, whose fixture is the
/// line "ab cd" (cells a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500) at Line granularity.
/// Js/CoreUntimedSpaceHarness.cjs drives the actual shipped JS through the same keystroke sequences
/// and emits what came out; this asserts it. Assert.Ignore when node is absent.</para>
///
/// <para>Half of this is the NEGATIVE half. The exemption is keyed on the CELL being a space, not on
/// the KEY being one, and the three surfaces that would have opened up otherwise are pinned here:
/// the rejection of a space on a lyric character, the consecutive-wrong-key streak that accrues on
/// it, and (on the desktop side only, since the browser has no Fletcher mod) the rush cap.</para>
/// </summary>
public class UntimedSpaceParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreUntimedSpaceHarness.cjs");

    private static string? Str(JsonElement e, string key)
    {
        var v = e.GetProperty(key);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
    }

    private static double Num(JsonElement e, string key) => e.GetProperty(key).GetDouble();

    private static bool Flag(JsonElement e, string key) => e.GetProperty(key).GetBoolean();

    private static string?[] Arr(JsonElement e, string key) => JsHarness.Strings(e, key);

    /// <summary>One entry of a per-cell delta array, which is null for every cell nothing landed in.</summary>
    private static double? Delta(JsonElement e, int index)
    {
        var v = e.GetProperty("judgedDeltas")[index];
        return v.ValueKind == JsonValueKind.Null ? null : v.GetDouble();
    }

    [Test]
    public void TheFixtureLineHasTheSameCellsTheGameFixtureHas()
    {
        var shape = Harness().GetProperty("shape");

        Assert.Multiple(() =>
        {
            Assert.That(Arr(shape, "expected"), Is.EqualTo(new[] { "a", "b", " ", "c", "d" }));
            Assert.That(JsHarness.Doubles(shape, "targets"), Is.EqualTo(new[] { 1000d, 1500, 2000, 2000, 2500 }));

            // The word gap is a TYPEABLE cell, which is what makes it something the player owes and
            // therefore something they can miss. Backlog 148 changes how it is JUDGED, not what it is.
            foreach (var t in shape.GetProperty("typeable").EnumerateArray())
                Assert.That(t.GetBoolean(), Is.True);

            foreach (var t in shape.GetProperty("tiers").EnumerateArray())
                Assert.That(t.GetString(), Is.EqualTo("Line"));
        });
    }

    /// <summary>
    /// The headline, asserted as an equality between two runs that differ only in when the space was
    /// pressed (5 seconds late against dead on target), because "the spacebar is not part of the
    /// timing challenge" IS that equality. Before backlog 148 the late one was a Lagging: no points,
    /// and a combo break in the middle of the line.
    /// </summary>
    [Test]
    public void ASpaceIsJudgedTheSameHoweverLateItIsPressed()
    {
        var root = Harness();
        var late = root.GetProperty("late");
        var onTime = root.GetProperty("onTime");

        Assert.Multiple(() =>
        {
            foreach (string field in new[] { "caretIndex", "combo", "maxCombo", "breaks", "score", "mistypes", "liveAccuracy", "processorHighestCombo" })
                Assert.That(Num(late, field), Is.EqualTo(Num(onTime, field)), field);

            Assert.That(Arr(late, "judgeTypes"), Is.EqualTo(Arr(onTime, "judgeTypes")));
            Assert.That(Delta(late, 2), Is.EqualTo(Delta(onTime, 2)));

            // And the absolute values, so a change to BOTH sides still trips this. 300 + 306 + 312
            // with the combo multiplier, exactly as the game's fixture computes it.
            Assert.That(Num(late, "score"), Is.EqualTo(918));
            Assert.That(Arr(late, "judgeTypes"), Is.EqualTo(new[] { "Great", "Great", "Great", null, null }));
            Assert.That(Num(late, "combo"), Is.EqualTo(3), "an untimed space cannot break the run it sits in");
            Assert.That(Num(late, "breaks"), Is.Zero);

            // Judged on a ZEROED delta, and that is the delta stored, because typebeat-player.js
            // reads it back for the cell tint and for the live sync percent.
            Assert.That(Delta(late, 2), Is.EqualTo(0));
        });
    }

    /// <summary>
    /// The same press one cell later is NOT exempt: it was the spacebar that left the timing
    /// challenge, not the player's sense of rhythm.
    /// </summary>
    [Test]
    public void ALyricCharacterPressedJustAsLateIsStillLagging()
    {
        var r = Harness().GetProperty("lyricLate");

        Assert.Multiple(() =>
        {
            Assert.That(Num(r, "comboAfterSpace"), Is.EqualTo(3));
            Assert.That(Arr(r, "judgeTypes")[3], Is.EqualTo("Lagging"));

            // 4100, where the game's own fixture pins 5100, and the gap is the ERA rather than a
            // drift: that fixture drives a bare TypingEngine, whose SyllableTiming defaults OFF, so
            // it measures the press against 'c''s own 2000 target. The browser has no era axis at
            // all (it only plays live), so it measures the press against the sung span of the
            // syllable "cd", [2000, 3000], exactly as live desktop play does. Everything the case
            // is actually about is unchanged: the press is just as late, still Lagging, still worth
            // nothing, and it still breaks the combo the space kept alive.
            Assert.That(Delta(r, 3), Is.EqualTo(4100));
            Assert.That(Num(r, "combo"), Is.Zero);
            Assert.That(Num(r, "breaks"), Is.EqualTo(1));
            Assert.That(Num(r, "score"), Is.EqualTo(918), "Lagging scores nothing, so the total is unmoved");
        });
    }

    /// <summary>
    /// Scoped to the CELL, hole 1: with space-skip off (the default), a space pressed on a LYRIC
    /// character is still rejected outright. Keying the exemption off "the key was a space" instead
    /// would have made space-mashing free.
    /// </summary>
    [Test]
    public void ASpaceOnALyricCharacterIsStillRejected()
    {
        var r = Harness().GetProperty("rejectedSpace");

        Assert.Multiple(() =>
        {
            Assert.That(Flag(r, "spaceSkipsWord"), Is.False);
            Assert.That(Str(r, "rejected"), Is.EqualTo(" "));
            Assert.That(Num(r, "caretIndex"), Is.EqualTo(1), "caret unmoved: nothing entered the cell");
            Assert.That(Arr(r, "states")[1], Is.EqualTo("untyped"));
            Assert.That(Num(r, "combo"), Is.Zero);
            Assert.That(Num(r, "breaks"), Is.EqualTo(1));
            Assert.That(Num(r, "mistypes"), Is.EqualTo(1));
            Assert.That(Num(r, "consecutiveWrongKeys"), Is.EqualTo(1));
            Assert.That(Num(r, "liveAccuracy"), Is.EqualTo(0.5), "the rejected space still costs accuracy");
        });
    }

    /// <summary>
    /// Scoped to the CELL, hole 2: the consecutive-wrong-key streak still accrues on that same
    /// rejection path and still fails the play at 13. The browser is permanently on the
    /// typed-through model, where a wrong LETTER never touches this streak, so a mashed spacebar is
    /// one of the only ways to build it at all. (The desktop splits the same rule across two types:
    /// the engine counts the streak and TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK fails on it;
    /// the browser core does both inline, which is why `failed` is observable here and not there.)
    /// </summary>
    [Test]
    public void TheMashFailStreakStillAccruesOnRejectedSpaces()
    {
        var r = Harness().GetProperty("mashed");

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(r, "streak"), Is.EqualTo(new[] { 1d, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
            Assert.That(Num(r, "consecutiveWrongKeys"), Is.EqualTo(13));
            Assert.That(Flag(r, "failed"), Is.True);
            Assert.That(Num(r, "caretIndex"), Is.Zero, "13 mashed spaces got the player nowhere");
            Assert.That(Num(r, "mistypes"), Is.EqualTo(13));

            // ...and any accepted char resets it, exactly as before.
            Assert.That(Num(r, "streakBeforeReset"), Is.EqualTo(3));
            Assert.That(Num(r, "streakAfterAccepted"), Is.Zero);
        });
    }

    /// <summary>
    /// An untimed space is not a FREE space: a space cell nobody pressed is a character of the map
    /// left untyped, and seals a Miss with every other one. Backlog 148 is about spaces not being a
    /// TIMING hazard.
    /// </summary>
    [Test]
    public void AnUntypedSpaceCellStillSealsAsAMiss()
    {
        var r = Harness().GetProperty("sealed");

        Assert.Multiple(() =>
        {
            Assert.That(Str(r, "gapState"), Is.EqualTo("missed"));
            Assert.That(Str(r, "gapJudgeType"), Is.EqualTo("Miss"));
            Assert.That(Num(r, "missCount"), Is.EqualTo(3)); // the gap, 'c' and 'd'
            Assert.That(Num(r, "breaks"), Is.EqualTo(1), "one break for the whole sealed line");
        });
    }

    /// <summary>
    /// The anti-farming rule is untouched: re-typing a space after backspacing over it is
    /// scoring-inert, and it re-classifies the stored firstCorrectDelta, which for a space is the
    /// zeroed one. So the retype is a top-tier judgement too, however late it lands, and it still
    /// adds nothing.
    /// </summary>
    [Test]
    public void RetypingASpaceIsInertAndStaysAtTheTopTier()
    {
        var r = Harness().GetProperty("retyped");

        Assert.Multiple(() =>
        {
            Assert.That(Str(r, "reopenedState"), Is.EqualTo("untyped"));
            Assert.That(Num(r, "scoreAfterFirst"), Is.EqualTo(918));
            Assert.That(Num(r, "score"), Is.EqualTo(Num(r, "scoreAfterFirst")), "a retype earns nothing");
            Assert.That(Arr(r, "judgeTypes")[2], Is.EqualTo("Great"));
            Assert.That(Delta(r, 2), Is.EqualTo(0));
            Assert.That(Num(r, "processorHighestCombo"), Is.EqualTo(3), "and is not counted twice");
        });
    }
}
