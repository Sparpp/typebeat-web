using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// The browser core's FLEXIBLE LINES default (backlog 208), held against the game's own semantics.
/// The desktop unpinned the player's caret from the song's playhead for EVERY play and reversed the
/// mod that used to grant that (which now PINS the caret and takes acronym "FC"), so the browser had
/// to follow or a /play run and a desktop run of the same map would judge line transitions
/// differently on the SAME leaderboards.
///
/// <para>Four behaviours, and this fixture takes them one at a time in the order the C# engine
/// documents them:</para>
/// <list type="bullet">
/// <item>RUSH FREEDOM: finishing a line opens the next one at once, instead of waiting for its
/// cue.</item>
/// <item>DRAG FREEDOM: a line the player is still typing is held past its deadline for
/// <c>FLETCHER_DRAG_GRACE_MS</c>, then force-sealed with the caret handed on.</item>
/// <item>THE RUSH CAP: a press that leaves the caret more than <c>FLETCHER_MAX_CHARS_AHEAD</c>
/// countable characters past the playhead lands and scores as normal but earns no combo.</item>
/// <item>THE LINE-START SNAP: a caret parked past the end of a FINISHED line is handed to the next
/// line the moment that line starts.</item>
/// </list>
///
/// <para>The golden values are transcribed from typebeat-osu's
/// <c>NonVisual/FletcherEngineTest.cs</c>, which is why this guard runs without a game checkout.
/// The half it cannot do is prove the two engines agree on a whole submitted account, and that is
/// <c>Typebeat.WireCompat.EngineFuzzLiveParityTest</c>, which plays generated and scripted streams
/// through the game's real replay scorer with the same CONFIG bit set.</para>
/// </summary>
public class FlexibleLinesParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => JsHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section(string name) => harness.Value.GetProperty(name);

    /// <summary>The caret's position, as (line, cell), which is what every observation below is written in.</summary>
    private static (int Line, int Cell) At(JsonElement element)
        => (element.GetProperty("line").GetInt32(), element.GetProperty("cell").GetInt32());

    /// <summary>
    /// The browser's settings are the LIVE ones, unconditionally, and that is a statement about
    /// /play rather than a simplification. The C# engine defaults BOTH flags to false, because it
    /// must also re-derive a stored replay under the PINNED era every pre-208 row was played in, and
    /// the pair travels as CONFIG frame bit 5. The browser has no era axis at all: no mods payload
    /// (so the pinning mod "FC" is unreachable), no replay input, and nothing anywhere re-scores a
    /// stored row through that file. If /play ever grows a mods payload, these are the two flags FC
    /// would clear.
    ///
    /// <para>The two constants are pinned here as well as being used below, because they are the
    /// mod's whole tuning surface and a drift in either is a silent scoring divergence rather than a
    /// visible one.</para>
    /// </summary>
    [Test]
    public void TheBrowserRunsTheFlexibleCaretUnconditionally()
    {
        var defaults = Section("defaults");

        Assert.Multiple(() =>
        {
            Assert.That(defaults.GetProperty("fletcherEnabled").GetBoolean(), Is.True, "the browser plays live, and live means unpinned");
            Assert.That(defaults.GetProperty("flexibleLineSnap").GetBoolean(), Is.True, "and live means the line-start snap too");
            Assert.That(defaults.GetProperty("maxCharsAhead").GetInt32(), Is.EqualTo(5), "TypingEngine.FLETCHER_MAX_CHARS_AHEAD");
            Assert.That(defaults.GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(1500), "TypingEngine.FLETCHER_DRAG_GRACE_MS");
        });
    }

    /// <summary>
    /// RUSH FREEDOM. "ab cd" is typed out by 2500, a second and a half before "ef" would have opened
    /// on its own 4000 cue, and the caret is on "ef" at once: the next press lands on its first cell
    /// rather than falling into the dead zone a pinned caret would have left it in.
    ///
    /// <para>The line left behind is UNSEALED, which is the other half of the rule and the half that
    /// keeps the song's timeline still: rush freedom moves the PLAYER forward, never the map. That
    /// line seals on its own normal deadline, with nothing missed because it is fully typed.</para>
    /// </summary>
    [Test]
    public void FinishingALineOpensTheNextOneImmediately()
    {
        var rush = Section("rushFreedom");

        Assert.Multiple(() =>
        {
            Assert.That(At(rush.GetProperty("afterFinishing")), Is.EqualTo((1, 0)), "the last press of line 0 put the caret at the head of line 1");
            Assert.That(rush.GetProperty("pressHandled").GetBoolean(), Is.True, "a press at 2600 is a real keystroke, not a dead-zone no-op");
            Assert.That(At(rush.GetProperty("afterPress")), Is.EqualTo((1, 1)));
            Assert.That(rush.GetProperty("firstCellOfNextLine").GetString(), Is.EqualTo("correct"));
            Assert.That(rush.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "the finished line is left unsealed and seals on its own deadline");
        });
    }

    /// <summary>
    /// DRAG FREEDOM, and its bound. "ab cd" runs to 4000 with the 'd' still owed, so the line is NOT
    /// force-sealed out from under the player at that deadline: the caret is still on the 'd' one
    /// frame short of 5500, which is the deadline plus <c>FLETCHER_DRAG_GRACE_MS</c>. At 5500 the
    /// borrowed time is spent: the line seals, its one untyped cell becomes a miss taking exactly one
    /// combo break however many cells were left, and the caret is handed to the next line rather
    /// than parked in a dead zone.
    ///
    /// <para>The grace is BOUNDED for the reason the C# states outright: a run always has to
    /// terminate. What it buys is a line finished late rather than a line taken away.</para>
    /// </summary>
    [Test]
    public void AnUnfinishedLineSurvivesItsDeadlineAndForceSealsAfterTheGrace()
    {
        var drag = Section("dragFreedom");

        Assert.Multiple(() =>
        {
            Assert.That(At(drag.GetProperty("atDeadline")), Is.EqualTo((0, 4)), "the boundary does not snatch a line the player is on");
            Assert.That(drag.GetProperty("sealedAtDeadline").GetInt32(), Is.Zero, "and nothing has sealed at the deadline either");
            Assert.That(At(drag.GetProperty("oneFrameShort")), Is.EqualTo((0, 4)), "one millisecond short of the grace expiring, the line is still the player's");

            Assert.That(At(drag.GetProperty("afterTheGrace")), Is.EqualTo((1, 0)), "the force-seal hands the caret to the next line, not to a dead zone");
            Assert.That(drag.GetProperty("sealedAfterTheGrace").GetInt32(), Is.EqualTo(1));
            Assert.That(drag.GetProperty("untypedCellState").GetString(), Is.EqualTo("missed"));
            Assert.That(drag.GetProperty("comboBreaks").GetInt32(), Is.EqualTo(1), "at most one break per sealed line");
        });
    }

    /// <summary>
    /// The same drag finished INSIDE the grace, which is what the whole freedom is for: the press
    /// lands on its own cell and the line seals with nothing missed at all. The lateness is not
    /// forgiven, it is JUDGED: the press is 5000 against a syllable sung over [2000, 3000], so it
    /// carries a delta of 2000 and is graded on exactly that. Per-char windows are untouched by the
    /// flexible caret, so dragging reads as late deltas and reports the drift honestly.
    /// </summary>
    [Test]
    public void FinishingInsideTheGraceKeepsTheCellAndJudgesTheLateness()
    {
        var drag = Section("dragFinishedInTime");

        Assert.Multiple(() =>
        {
            Assert.That(drag.GetProperty("pressHandled").GetBoolean(), Is.True);
            Assert.That(drag.GetProperty("lastCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(drag.GetProperty("lastCellDelta").GetDouble(), Is.EqualTo(2000),
                "5000 against the 'cd' syllable's [2000, 3000] span: late by 2000, and graded as such");
            Assert.That(At(drag.GetProperty("afterPress")), Is.EqualTo((1, 0)), "finishing late still rolls the caret forward");
        });
    }

    /// <summary>
    /// THE LINE-START SNAP, the one behaviour the new default has that the retired mod never did.
    /// The state it covers is the one the keypress roll-forward cannot: a caret that arrived on a
    /// line ALREADY complete, so no press of the player's can ever finish it and nothing would ever
    /// move it on. The fixture's middle line has no cells at all (its authored text is pure
    /// punctuation) and its window outlives the next line's cue by nine and a half seconds.
    ///
    /// <para>One frame short of that cue nothing has moved, which is what says the snap is the LINE
    /// STARTING rather than the caret being idle; and the middle line is still UNSEALED when the
    /// caret leaves it, which is what says the snap moved it rather than a seal's hand-over.</para>
    /// </summary>
    [Test]
    public void AParkedFinishedCaretIsSnappedWhenTheNextLineStarts()
    {
        var snap = Section("lineStartSnap");

        Assert.Multiple(() =>
        {
            Assert.That(snap.GetProperty("middleLineCellCount").GetInt32(), Is.Zero, "the fixture's middle line must have no cells at all");
            Assert.That(snap.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(10500), "12000 - CUE_LEAD_MS");
            Assert.That(snap.GetProperty("middleLineEnd").GetDouble(), Is.EqualTo(20000),
                "the next line has to start before a seal could hand the caret over, or this proves nothing");

            Assert.That(At(snap.GetProperty("parkedOn")), Is.EqualTo((1, 0)), "rush freedom put the caret on a line it can never finish");

            var short_ = snap.GetProperty("oneFrameShort");
            Assert.That(At(short_.GetProperty("at")), Is.EqualTo((1, 0)), "one millisecond before the cue, nothing has moved");
            Assert.That(short_.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1));

            var snapped = snap.GetProperty("snapped");
            Assert.That(At(snapped.GetProperty("at")), Is.EqualTo((2, 0)), "the line started, so it takes the finished caret");
            Assert.That(snapped.GetProperty("nextSealIndex").GetInt32(), Is.EqualTo(1),
                "and the line the caret left is still unsealed, so no seal could have moved it");

            // ...and the player then types the new line from its head, on time.
            Assert.That(snap.GetProperty("pressHandled").GetBoolean(), Is.True);
            Assert.That(snap.GetProperty("firstCellState").GetString(), Is.EqualTo("correct"));
            Assert.That(snap.GetProperty("firstCellDelta").GetDouble(), Is.Zero);
        });
    }

    /// <summary>
    /// THE LIMIT on the snap, and the reason it is gated on FINISHED rather than on time alone: a
    /// line the player is still typing is never taken from them, because lagging behind is precisely
    /// the freedom the flexible caret exists to grant. The same predicate <c>sealPermitted</c> uses
    /// ("nothing left untyped means there is no drag to protect") draws the line here.
    ///
    /// <para>Non-vacuous by construction: the next line activates at 10500 and this one cannot even
    /// seal until 20000, so between them the snap is the ONLY thing that could have moved the caret,
    /// and it does not. Only finishing the line does.</para>
    /// </summary>
    [Test]
    public void AnUnfinishedLineIsNeverSnappedAwayFromThePlayer()
    {
        var limit = Section("unfinishedIsNeverSnapped");

        Assert.Multiple(() =>
        {
            Assert.That(limit.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(10500));
            Assert.That(limit.GetProperty("thisLineEnd").GetDouble(), Is.EqualTo(20000),
                "the line the player is on must outlive the next line's cue, or nothing is being tested");

            var owed = limit.GetProperty("stillOwed");
            Assert.That(At(owed.GetProperty("at")), Is.EqualTo((0, 1)), "'b' is still owed, so the line is still the player's");
            Assert.That(owed.GetProperty("nextSealIndex").GetInt32(), Is.Zero, "and no seal could have moved it either");
            Assert.That(limit.GetProperty("untouchedCellState").GetString(), Is.EqualTo("untyped"),
                "nothing has been put into the line that started underneath them");

            Assert.That(At(limit.GetProperty("afterFinishing")), Is.EqualTo((1, 0)),
                "finish it late and the ordinary roll-forward takes over, as it always did");
        });
    }

    /// <summary>
    /// THE RUSH CAP, which is what the flexible caret trades the timing lock for: a keypress may
    /// leave the caret at most <c>FLETCHER_MAX_CHARS_AHEAD</c> COUNTABLE characters past the
    /// playhead and still earn combo. Every press in this run is at 1000, where the playhead has
    /// reached exactly one countable character, so the caret's lead is simply the press count and
    /// the ladder can be read straight off it.
    ///
    /// <para>THE CAP IS A COMBO PENALTY, NOT A BLOCK. Every cell of the line still lands and still
    /// scores; what the excursion costs is the run. The break fires ONCE, on the press that crosses
    /// the line, and the presses that follow it out take no further break because there is no streak
    /// left to take. It measures where the CARET is and not how well the press was timed, which is
    /// why it can refuse combo to a press the ladder graded Great.</para>
    ///
    /// <para>THE SPACE IS EXEMPT, and that is the seventh press here rather than an aside: a word gap
    /// is not COUNTABLE, so it spends no budget. The caret is 5 ahead before it and 5 ahead after it,
    /// and the press earns combo. Had the gap cost a character it would have been the sixth and would
    /// have broken the run one press early.</para>
    /// </summary>
    [Test]
    public void PressingPastTheRushCapCostsTheComboOnceAndThenReArms()
    {
        var cap = Section("rushCap");

        Assert.Multiple(() =>
        {
            foreach (var state in cap.GetProperty("states").EnumerateArray())
                Assert.That(state.GetString(), Is.EqualTo("correct"), "the cap refuses combo, never the character");

            // a b c d e f  [gap]  g h, at a playhead that has reached one countable character.
            Assert.That(JsHarness.Doubles(cap, "leadAfterEachPress"), Is.EqualTo(new double[] { 0, 1, 2, 3, 4, 5, 5, 6, 7 }),
                "the word gap is the 7th press and leaves the lead where it found it: a space spends no rush budget");

            // The fifth character ahead is still fine and the sixth is not, so combo climbs to 7
            // (six letters plus the free gap) and the press that puts the caret 6 ahead zeroes it.
            Assert.That(JsHarness.Doubles(cap, "comboAfterEachPress"), Is.EqualTo(new double[] { 1, 2, 3, 4, 5, 6, 7, 0, 0 }));
            Assert.That(cap.GetProperty("comboBreaks").GetInt32(), Is.EqualTo(1),
                "one break for the excursion: the press after it is further out still and has no streak left to take");
            Assert.That(cap.GetProperty("maxComboBeforeTheCatchUp").GetInt32(), Is.EqualTo(7));

            // The clock catches up, the caret is back inside the cap, and the run rebuilds. That is
            // the re-arm: the cap does not latch, so the next excursion breaks the new streak too.
            Assert.That(cap.GetProperty("leadAfterCatchUp").GetInt32(), Is.EqualTo(1));
            Assert.That(cap.GetProperty("comboAfterCatchUp").GetInt32(), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The predicate the unpinned caret makes necessary: "is the SONG asking for characters on the
    /// line my caret is on", which is not the same question as "is a line window open" and not the
    /// same question as "is a line active".
    ///
    /// <para>The difference IS a real map's instrumental gap. A decoder-built line's window runs to
    /// the NEXT line's start, so windows are contiguous with no holes: through the twelve seconds
    /// between these two lines the playhead is still inside line 0's window and the plain reading
    /// stays true, while the player who finished line 0 is parked at the head of line 1 with nothing
    /// being asked of them. It goes true again at 20000, when the song reaches the line the caret has
    /// been waiting on.</para>
    ///
    /// <para>On the desktop this is what lets Space reach the mid-song skip overlay from a parked
    /// caret. The BROWSER HAS NO SKIP OVERLAY: it cannot seek its scheduled audio source without
    /// moving the gameplay clock, so a dead stretch gets a labelled countdown chip instead and there
    /// is no fall-through for Space to reach. The predicate is still load-bearing here, because that
    /// chip is what it now gates: under a pinned caret "a line is active" meant "the song is asking
    /// for characters", and it does not any more.</para>
    /// </summary>
    [Test]
    public void ThroughAnInstrumentalTheSongIsNotOnTheParkedCaretsLine()
    {
        var readings = Section("songOnTheCaretsLine").GetProperty("readings");

        Assert.Multiple(() =>
        {
            foreach (var reading in readings.EnumerateArray())
            {
                double time = reading.GetProperty("time").GetDouble();
                bool onTheCaretsLine = reading.GetProperty("songIsOnTheCaretsLine").GetBoolean();

                Assert.That(At(reading.GetProperty("at")), Is.EqualTo((1, 0)), $"{time}: the caret is parked on line 1 throughout");
                Assert.That(reading.GetProperty("songWindowOpen").GetBoolean(), Is.True,
                    $"{time}: a line window is open the whole way, which is exactly why it is the wrong question");

                if (time < 20000)
                    Assert.That(onTheCaretsLine, Is.False, $"{time}: the song is still on line 0, and asking nothing of the parked caret");
                else
                    Assert.That(onTheCaretsLine, Is.True, $"{time}: the song has reached the line the caret was waiting on");
            }
        });
    }

    /// <summary>
    /// The WPM clock is SUSPENDED while the caret is parked ahead of the cue. A player who finishes a
    /// line and then waits out a seventeen-second instrumental has not been typing for seventeen
    /// seconds, and a clock that ran through it would read the wait as typing time and halve the
    /// readout. So it runs only from the point the playhead reaches the parked line's own activation,
    /// which is exactly when that line would have gone active under a pinned caret.
    ///
    /// <para>Measured on the frame boundaries, because that is where the rule lives: the frame that
    /// ENDS at the cue was still a parked frame and accrues nothing, and the one after it is real
    /// typing time again. Unsuspended, the clock here would read 20000 rather than 2000.</para>
    /// </summary>
    [Test]
    public void TheWpmClockDoesNotRunWhileTheCaretWaitsAheadOfTheCue()
    {
        var clock = Section("wpmClock");

        Assert.Multiple(() =>
        {
            Assert.That(clock.GetProperty("nextLineActivation").GetDouble(), Is.EqualTo(20000));
            Assert.That(clock.GetProperty("activeTimeAfterTyping").GetDouble(), Is.EqualTo(1000), "typing the first line took a second");
            Assert.That(clock.GetProperty("activeTimeWhileParked").GetDouble(), Is.EqualTo(1000), "and seventeen seconds of parked instrumental added nothing");
            Assert.That(clock.GetProperty("activeTimeAtTheCue").GetDouble(), Is.EqualTo(1000), "the frame that ends AT the cue was still a parked frame");
            Assert.That(clock.GetProperty("activeTimeAfterTheCue").GetDouble(), Is.EqualTo(2000), "and the frame after it is typing time again");
        });
    }
}
