using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the CHAR-TIMED STRETCH narrowing of the syllable-span rule (backlog 209) in
/// the browser scoring core. Backlog 179 grades a grouped cell on distance from its syllable's sung
/// SPAN, 0 anywhere inside it, and two shapes turned that into free points because inside one span
/// they make every cell interchangeable: a FREESTYLE token ("&amp;&amp;&amp;&amp;&amp;&amp;", which
/// accepts any key at all) and a STRETCHED RUN (three or more identical characters of one syllable,
/// the "000" of "1000"). A player who mashed either filled every slot on a judged delta of zero,
/// seconds ahead of the vocal. The fix reverts exactly those cells to their own character targets
/// while the rest of the line keeps the span.
///
/// <para>Golden values mirror typebeat-osu's NonVisual/CharTimedStretchTest.cs, fixture for fixture.
/// The desktop engine carries the narrowing as an ERA (the replay CONFIG frame's flags bit 6)
/// because it must re-derive stored replays under the rule their fingers were graded on; the browser
/// only ever plays live, so it applies the narrowing unconditionally, exactly as it applies the span
/// rule itself. The deltas asserted here are therefore the game's LIVE arm, and every one of the
/// narrowed ones would be 0 under the pure span rule, which is what makes them the contrast as well
/// as the pin.</para>
///
/// <para>Since backlog 247 the live arm carries a second narrowing the game's fixtures do not (their
/// engines leave FirstCharTiming at its stored-replay default): the cell that OPENS a syllable is
/// judged on the distance from that span's START rather than paid 0 anywhere inside it. So where a
/// case below presses an opening cell late, the delta is that distance and not the game copy's zero.
/// The stretch arm keeps precedence over it, which <see cref="ASubtimedStretchIsCharTimedAndItsOwnSyllableIsNot"/>
/// pins on the one cell that is both.</para>
///
/// <para>A Node harness (Js/CoreCharStretchHarness.cjs) drives the actual shipped JS and emits its
/// observations; this asserts them. Assert.Ignore when node is absent.</para>
/// </summary>
public class CharTimedStretchParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreCharStretchHarness.cjs");

    private static bool[] Flags(JsonElement element, string key)
        => element.GetProperty(key).EnumerateArray().Select(e => e.GetBoolean()).ToArray();

    private static int[] Ints(JsonElement element, string key)
        => element.GetProperty(key).EnumerateArray().Select(e => e.GetInt32()).ToArray();

    private static string?[] Types(JsonElement element)
        => element.GetProperty("types").EnumerateArray()
                  .Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()).ToArray();

    private static double?[] Deltas(JsonElement element)
        => element.GetProperty("deltas").EnumerateArray()
                  .Select(e => e.ValueKind == JsonValueKind.Null ? (double?)null : e.GetDouble()).ToArray();

    /// <summary>
    /// THE MEMBERSHIP PIN, and the reason the flags can be derived at all: a freestyle token is
    /// grouped like any other syllabifiable word (the syllabifier only refuses a run of three
    /// identical LETTERS, and '&amp;' is not a letter), which is exactly why the span rule reached
    /// it. Nothing about the fix ungroups it, because the lyric stack lights GROUPS and an ungrouped
    /// stretch would stop being highlighted while it is sung.
    /// </summary>
    [Test]
    public void EveryFreestyleCellIsAStretchCellAndKeepsItsGroup()
    {
        var root = Harness();
        var spam = root.GetProperty("freestyleSpam");
        var lone = root.GetProperty("loneFreestyle");

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(spam, "targets"), Is.EqualTo(new[] { 1000d, 3000, 5000, 7000, 9000, 11000 }));
            Assert.That(spam.GetProperty("syllables").GetArrayLength(), Is.EqualTo(1), "one group over the whole word");
            Assert.That(Ints(spam, "cellSyllable"), Is.EqualTo(new[] { 0, 0, 0, 0, 0, 0 }));
            Assert.That(Flags(spam, "stretch"), Is.EqualTo(new[] { true, true, true, true, true, true }));
            Assert.That(spam.GetProperty("stretchKeepsItsGroup").GetBoolean(), Is.True);

            // A LONE freestyle slot between two letters qualifies too: it is the "any key" rule that
            // makes it unjudgeable on a span, not the length of the run it sits in.
            Assert.That(Flags(lone, "stretch"), Is.EqualTo(new[] { false, true, false }));
            Assert.That(Ints(lone, "cellSyllable"), Is.EqualTo(new[] { 0, 0, 0 }), "and it is still in the group");
        });
    }

    /// <summary>
    /// The run arm, stated as sharply as one line can state it: inside ONE group, the lone '1' of
    /// "1000" keeps the span and the three '0's beside it lose it. A run of exactly TWO ("goo") is an
    /// ordinary spelling and keeps the span, because the threshold is three, which is
    /// <c>isSyllabifiable</c>'s own threshold for calling a spelling stylised.
    /// </summary>
    [Test]
    public void AThreeCharRunIsAStretchAndADoubledCharIsNot()
    {
        var root = Harness();
        var digits = root.GetProperty("digitRun");
        var doubled = root.GetProperty("doubledChar");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("stretchRunLength").GetInt32(), Is.EqualTo(3));

            Assert.That(JsHarness.Doubles(digits, "targets"), Is.EqualTo(new[] { 1000d, 4000, 7000, 10000 }));
            Assert.That(digits.GetProperty("syllables").GetArrayLength(), Is.EqualTo(1), "a digit run is one syllable");
            Assert.That(Flags(digits, "stretch"), Is.EqualTo(new[] { false, true, true, true }));

            Assert.That(JsHarness.Doubles(doubled, "targets"), Is.EqualTo(new[] { 1000d, 5000, 9000 }));
            Assert.That(Flags(doubled, "stretch"), Is.EqualTo(new[] { false, false, false }));
        });
    }

    /// <summary>
    /// Runs are cut at the SYLLABLE boundary, because a span is what the rule hands out: the authored
    /// split "hey|yyyy" leaves one 'y' alone in the first syllable, which keeps that syllable's span,
    /// while the four in the second are the stretch. A SPACE cell breaks a run outright (it is in no
    /// group at all), which is what keeps "a a a" three separate characters.
    /// </summary>
    [Test]
    public void ARunIsCutAtTheSyllableBoundaryAndBrokenBySpaces()
    {
        var root = Harness();
        var subtimed = root.GetProperty("subtimedStretch");
        var spaced = root.GetProperty("spaced");

        Assert.Multiple(() =>
        {
            Assert.That(Ints(subtimed, "cellSyllable"), Is.EqualTo(new[] { 0, 0, 0, 1, 1, 1, 1 }));
            Assert.That(Flags(subtimed, "stretch"), Is.EqualTo(new[] { false, false, false, true, true, true, true }));

            Assert.That(Ints(spaced, "cellSyllable"), Is.EqualTo(new[] { 0, -1, 1, -1, 2 }), "the gaps are in no group");
            Assert.That(Flags(spaced, "stretch"), Is.EqualTo(new[] { false, false, false, false, false }));
        });
    }

    /// <summary>
    /// Runs fold case, matching the matcher: default gameplay is case-insensitive, so the "YyY" of a
    /// subtimed "heY|YyY" is one run of three however the mapper capitalised it. Asserted on a
    /// LITERATE build, where the cells keep the authored capitals, so the fold is doing real work
    /// rather than reading an already lower-cased default stream.
    /// </summary>
    [Test]
    public void RunsFoldCase()
    {
        var folded = Harness().GetProperty("foldedLiterate");

        Assert.Multiple(() =>
        {
            Assert.That(folded.GetProperty("expected").GetString(), Is.EqualTo("heYYyY"), "literate keeps the capitals");
            Assert.That(Flags(folded, "stretch"), Is.EqualTo(new[] { false, false, false, true, true, true }));
        });
    }

    /// <summary>
    /// THE FIX, on the reported shape: six keys mashed the instant a freestyle section opens are
    /// judged on the characters' own targets, so only the first is on time and the other five are 2
    /// to 10 seconds early, straight off the ladder. Under the pure span rule all six were delta 0
    /// and every one of them a Great.
    /// </summary>
    [Test]
    public void FreestyleSpamIsJudgedPerCharacter()
    {
        var mash = Harness().GetProperty("spamMash");

        Assert.Multiple(() =>
        {
            Assert.That(Deltas(mash), Is.EqualTo(new double?[] { 0, -2000, -4000, -6000, -8000, -10000 }));
            Assert.That(Types(mash), Is.EqualTo(new[] { "Great", "Premature", "Premature", "Premature", "Premature", "Premature" }));

            // The mash still FILLS every cell: what moved is what the presses were worth, not
            // whether they landed.
            Assert.That(mash.GetProperty("maxCombo").GetInt32(), Is.EqualTo(6));
        });
    }

    /// <summary>And a freestyle slot played ON its own target is still a Great: the fix prices the
    /// mash, it does not make the section unplayable.</summary>
    [Test]
    public void AFreestyleCellPlayedOnItsTargetIsStillGreat()
    {
        var run = Harness().GetProperty("spamOnTarget");

        Assert.Multiple(() =>
        {
            Assert.That(Deltas(run), Is.EqualTo(new double?[] { 0, 0, 0, 0, 0, 0 }));
            Assert.That(Types(run), Is.EqualTo(new[] { "Great", "Great", "Great", "Great", "Great", "Great" }));
        });
    }

    /// <summary>
    /// The narrowing INSIDE one syllable: the three '0's of "1000" mashed with the '1' lose the span
    /// while the '1' keeps it, so the same press time is worth two different things four cells apart.
    /// </summary>
    [Test]
    public void AStretchedRunLosesTheSpanWhileItsNeighbourKeepsIt()
    {
        var mash = Harness().GetProperty("digitMash");

        Assert.Multiple(() =>
        {
            // '1' is inside [1000, 13000], so the span pays it 0. The '0's target 4000, 7000, 10000.
            Assert.That(Deltas(mash), Is.EqualTo(new double?[] { 0, -3000, -6000, -9000 }));
            Assert.That(Types(mash), Is.EqualTo(new[] { "Great", "Premature", "Premature", "Premature" }));
        });
    }

    /// <summary>
    /// The cells the narrowing must NOT touch, all of them pressed 11 seconds past their own targets:
    /// a lone character, and a doubled letter. Neither is char-timed, so neither is graded on its own
    /// target.
    ///
    /// <para>What they ARE graded on is now two rules rather than one, which is why the deltas below
    /// are not all zero. A cell in the middle of a syllable keeps the whole span and is paid 0
    /// wherever inside it the press lands (both 'o's of "goo"); the cell that OPENS the syllable is
    /// judged on the distance from the span's START since backlog 247, so pressing it 11 seconds late
    /// costs exactly that. The game's own fixture pins 0 for those openers because it drives a bare
    /// engine, whose FirstCharTiming defaults OFF for stored replays; the browser only ever plays
    /// live. That the narrowing did not touch them is what the stretch flags above say, and what
    /// <see cref="AStretchedRunLosesTheSpanWhileItsNeighbourKeepsIt"/> shows on a press the two rules
    /// answer differently.</para>
    /// </summary>
    [Test]
    public void ALoneCharacterAndADoubledOneAreNotCharTimed()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // The lone '1' of "1000" opens its group, so 11 seconds late is 11 seconds late.
            Assert.That(Deltas(root.GetProperty("loneCharLate"))[0], Is.EqualTo(11000));
            Assert.That(Types(root.GetProperty("loneCharLate"))[0], Is.EqualTo("Lagging"));

            // "goo": the same for the 'g' that opens it, while the doubled letters behind it are the
            // whole-span zeros, 11 seconds past their own 5000 and 9000 targets.
            Assert.That(Deltas(root.GetProperty("doubledLate")), Is.EqualTo(new double?[] { 11000, 0, 0 }));
            Assert.That(Types(root.GetProperty("doubledLate")), Is.EqualTo(new[] { "Lagging", "Great", "Great" }));
        });
    }

    /// <summary>
    /// The subtimed "hey|yyyy" played through: "hey" pressed at 4900 is the 'h' that OPENS
    /// [1000, 5000], paid the 3900 it is late by (backlog 247), then two characters deep inside that
    /// span and paid 0, the second of them a 'y' the split left out of the run; the run's four cells
    /// mashed at 5100 are graded on their own targets (5000, 7000, 9000, 11000), so only the first of
    /// them is on time.
    ///
    /// <para>That first one is the PRECEDENCE, and it is the browser's copy of the game's
    /// <c>AStretchCellOpeningAGroupKeepsItsPointTargetUnderTheHybrid</c>: cell 3 opens the second
    /// group AND is a stretch cell, and the stretch arm wins, so it is judged the 100 it is off its
    /// own 5000 target rather than the 100 it would be off the span start (the two coincide here) or
    /// the 0 the whole span would have paid it.</para>
    /// </summary>
    [Test]
    public void ASubtimedStretchIsCharTimedAndItsOwnSyllableIsNot()
    {
        var run = Harness().GetProperty("subtimedRun");

        Assert.Multiple(() =>
        {
            Assert.That(Deltas(run), Is.EqualTo(new double?[] { 3900, 0, 0, 100, -1900, -3900, -5900 }));
            Assert.That(Types(run), Is.EqualTo(new[] { "Lagging", "Great", "Great", "Great", "Premature", "Premature", "Premature" }));
        });
    }
}
