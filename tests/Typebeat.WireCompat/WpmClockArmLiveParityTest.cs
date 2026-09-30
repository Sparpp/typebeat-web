using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the WPM CLOCK'S LAZY ARM (backlog 222). Since backlog 218 a caret that
/// finished its line is handed the next one from <c>FLETCHER_DRAG_GRACE_MS</c> before that line's
/// cue, and neither engine's key path has a time gate, so the player really can type there. Both
/// engines counted those characters in the WPM numerator while their clocks stayed stopped, and both
/// readouts climbed for free. The fix arms the clock on the FIRST press made on such a line and runs
/// it from that press's own time, and it has to land in both files identically or the browser's
/// readout and the desktop's disagree on the same run.
///
/// <para>WPM is not on the wire (a <c>/play</c> submission carries the aggregate account alone), so
/// this is not a leaderboard corruption the way a scoring divergence is: it is the number both
/// clients show the player for the same performance, and the mirror is the only thing keeping them
/// equal. That is why the pin is here rather than folded into
/// <see cref="EngineFuzzLiveParityTest"/>, whose comparison is the submitted account and whose C#
/// arm goes through <c>TypeBeatReplayScorer</c>, which reports no clock at all.</para>
///
/// <para>ONE COPY OF THE KEYSTROKES. The scripts, and the readings the browser made of them, come
/// out of <c>CoreFlexibleLinesHarness.cjs</c>'s <c>wpmClockArm</c> section; this side replays the
/// emitted steps through the game's real <see cref="TypingEngine"/> and compares after every one.
/// The web-side transcription of the same numbers is
/// <c>Typebeat.Web.Tests.FlexibleLinesParityTest</c>, which needs no game checkout; what this adds
/// is that the golden values are the GAME's live behaviour rather than a transcription of it.</para>
/// </summary>
[TestFixture]
public class WpmClockArmLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement ClockArm() => harness.Value.GetProperty("wpmClockArm");

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The two maps the harness names, in the game's own terms.
    ///
    /// <para><c>twoLine</c> is the game's <c>FletcherEngineTest.twoLineMap</c>: L0 "ab cd"
    /// [1000, 4000), sung to 3000, so a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500; L1 "ef"
    /// sung [4000, 5000), so e = 4000, f = 4500, cue-clamped to activate at its own 4000 start,
    /// which puts entry into it at 2500. <c>longTail</c> widens L1 to "efgh" over the same second
    /// (step 250) and changes nothing else, so the second press of a pair still lands on an
    /// INCOMPLETE line.</para>
    ///
    /// <para>The game's own fixture ends L1 at 6000 and the browser's loader stretches a FINAL
    /// line's window past its vocals instead, which is a loader difference and not an engine one
    /// (a <c>LyricLine</c> carries the window it is handed). 8000 is written here so the two sides
    /// hold the same map and <see cref="TheTwoLoadersAgreeOnTheClockArmFixtures"/> can compare every
    /// field rather than skipping one. It reaches nothing below: every script is over by 4500, well
    /// inside either window.</para>
    /// </summary>
    private static LyricBeatmap Map(string name) => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata
        {
            Artist = "a",
            Title = "t",
            FolderPath = @"X:\nowhere",
            AudioFileName = "a.mp3",
        },
        Lines =
        [
            Line("ab cd", 1000, 4000, 3000, Unit("ab", 1000, 2000), Unit("cd", 2000, 3000)),
            name switch
            {
                "twoLine" => Line("ef", 4000, 8000, 5000, Unit("ef", 4000, 5000)),
                "longTail" => Line("efgh", 4000, 8000, 5000, Unit("efgh", 4000, 5000)),
                _ => throw new ArgumentException($"the harness named a map this side does not have: {name}", nameof(name)),
            },
        ],
        Granularity = TimingGranularity.Line,
    };

    /// <summary>
    /// A started engine under every LIVE rule, which is the only arm the browser can be compared
    /// against: it has no mods payload, writes no replay frames and re-derives no stored row. The
    /// era flags the C# defaults OFF for replay decoding have to be set by hand, exactly as
    /// <see cref="EngineFuzzLiveParityTest"/> sets the equivalent CONFIG frame bits.
    ///
    /// <para>MANUAL NEWLINES are the one flag a run chooses (backlog 307): the browser runs them
    /// always, at the desktop's shipped default, but the harness's first four scripts transcribe the
    /// game's FletcherEngineTest, whose bare engines run the automatic hand-over, so each run says
    /// which arm it declared and this side follows it.</para>
    /// </summary>
    private static TypingEngine LiveEngine(string map, bool manualNewlines = true) => new TypingEngine(Map(map))
    {
        SyllableTiming = true,
        CharTimedStretch = true,
        FirstCharTiming = true,
        WrongInputOnWordGaps = true,
        StrictSpaces = true,
        SpaceSkipsWord = true,
        FletcherEnabled = true,
        FlexibleLineSnap = true,
        BoundedRush = true,
        BackDatedSealBreak = true,
        LosslessSkipReclaim = true,
        FoldsDisplacedClaim = true,
        FirstLineLeadIn = true,
        // Backlog 347, the first bit of the SECOND CONFIG flags word: the browser takes it unconditionally.
        RushCapCostsAccuracy = true,
        InputEra2 = true,
        ManualNewlines = manualNewlines,
        NewlineOnTypedLetter = manualNewlines,
    };

    #endregion

    /// <summary>
    /// THE FIXTURES BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both
    /// sides build their cells through their own loader (the browser's <c>buildBeatmap</c>, the
    /// game's <c>TypingLine.FromLyricLine</c>), so a fixture that drifted would show up below as a
    /// clock divergence and be blamed on the arm.
    ///
    /// <para>The one number the whole region hangs on is <c>entryOpensAt(1)</c> = 2500: line 1's
    /// 4000 cue less the 1500 ms grace, which is exactly where the 'd' that finishes line 0 lands.
    /// If that ever stops being true the scripts stop reaching the pre-cue state at all, and every
    /// assertion below would pass vacuously.</para>
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheClockArmFixtures()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ClockArm().GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(TypingEngine.FLETCHER_DRAG_GRACE_MS));

            foreach (var named in ClockArm().GetProperty("fixtures").EnumerateObject())
            {
                var engine = LiveEngine(named.Name);
                var lines = named.Value.GetProperty("lines");

                Assert.That(named.Value.GetProperty("entryOpensAt").GetDouble(),
                    Is.EqualTo(engine.Lines[1].ActivationTime - TypingEngine.FLETCHER_DRAG_GRACE_MS),
                    $"{named.Name}: entry into line 1 opens exactly where 'd' finishes line 0");

                Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), $"{named.Name}: line count");

                for (int i = 0; i < engine.Lines.Count; i++)
                {
                    var line = engine.Lines[i];
                    var browserLine = lines[i];

                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{named.Name}[{i}]: activationTime");
                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{named.Name}[{i}]: endTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{named.Name}[{i}]: sealGraceMs");

                    var browserCells = browserLine.GetProperty("cells");
                    Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{named.Name}[{i}]: cell count");

                    for (int c = 0; c < line.Cells.Count; c++)
                    {
                        Assert.That(browserCells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"{named.Name}[{i}][{c}]: expected");
                        Assert.That(browserCells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"{named.Name}[{i}][{c}]: target");
                    }
                }
            }
        });
    }

    /// <summary>
    /// The scripts, replayed through the game's engine and compared to the browser's readings
    /// step for step: the caret after every step, whether every press was handled, and the WPM
    /// readout with NO TOLERANCE, because both engines compute
    /// <c>(correctCells / 5) / (activeMs / 60000)</c> over the same doubles and any difference at all
    /// is a difference in the clock.
    ///
    /// <list type="bullet">
    /// <item><c>typed</c>: the head start USED. The press at 2600 arms and is credited no elapsed
    /// time; the frame to 2700 credits exactly the 100 ms since the arm. Before the fix both engines
    /// credited nothing and read 56 WPM for those seven characters instead of 52.5.</item>
    /// <item><c>idle</c>: the head start NOT used, so nothing is credited for it and the clock picks
    /// up on the ordinary rule at line 1's own cue. This is the half the arm must not have broken,
    /// and it is what stops the fix from being "run the clock from entryOpensAt".</item>
    /// <item><c>aheadOfTheFrame</c>: a press stamped ahead of the frame that follows it, where the
    /// clock must credit zero rather than negative time.</item>
    /// <item><c>secondPressKeepsTheFirstArm</c>: two presses ahead of the cue before the next frame,
    /// where the arm must stay on the first of them or the time the player spent typing between the
    /// two is swallowed. On the wide-tailed map, since a two-cell line is COMPLETE after the second
    /// press and stops accruing either way.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameClockOfTheBrowsersScripts()
    {
        var runs = ClockArm().GetProperty("runs");

        Assert.Multiple(() =>
        {
            foreach (var run in runs.EnumerateObject())
            {
                var engine = LiveEngine(run.Value.GetProperty("map").GetString()!, run.Value.GetProperty("manual").GetBoolean());
                var script = run.Value.GetProperty("script");
                var readings = run.Value.GetProperty("readings");

                Assert.That(readings.GetArrayLength(), Is.EqualTo(script.GetArrayLength()), $"{run.Name}: one reading per step");

                for (int i = 0; i < script.GetArrayLength(); i++)
                {
                    var step = script[i];
                    var reading = readings[i];
                    double t = step.GetProperty("t").GetDouble();
                    string op = step.GetProperty("op").GetString()!;
                    string where = $"{run.Name}[{i}] {op} {t}";

                    if (op == "update")
                    {
                        engine.Update(t);
                    }
                    else
                    {
                        char c = step.GetProperty("c").GetString()![0];
                        bool handled = engine.ProcessKey(c, t);

                        Assert.That(handled, Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where} '{c}': handled");
                    }

                    var at = reading.GetProperty("at");
                    Assert.That(engine.ActiveLineIndex, Is.EqualTo(at.GetProperty("line").GetInt32()), $"{where}: active line");
                    Assert.That(engine.CaretIndex, Is.EqualTo(at.GetProperty("cell").GetInt32()), $"{where}: caret");
                    Assert.That(engine.LiveWpm, Is.EqualTo(reading.GetProperty("wpm").GetDouble()), $"{where}: LiveWpm");
                }

                Assert.That(engine.MaxCombo, Is.EqualTo(run.Value.GetProperty("maxCombo").GetInt32()), $"{run.Name}: max combo");
                Assert.That(engine.Mistypes, Is.EqualTo(run.Value.GetProperty("mistypes").GetInt32()), $"{run.Name}: mistypes");
            }
        });
    }

    /// <summary>
    /// NON-VACUITY, on the harness's own output rather than on either engine: the <c>typed</c> script
    /// has to actually reach the pre-cue state and actually accrue time there, or the comparison
    /// above is two engines agreeing that nothing happened. So the caret must be on line 1 before
    /// that line's cue, and the clock must move between the arming press and the one after it while
    /// the song is still short of the line.
    /// </summary>
    [Test]
    public void TheTypedScriptReallyTypesAheadOfTheCue()
    {
        double cue = LiveEngine("twoLine").Lines[1].ActivationTime;
        var readings = ClockArm().GetProperty("runs").GetProperty("typed").GetProperty("readings");

        double armedAt = 0;
        double clockAtTheArm = 0;
        double clockAtTheEnd = 0;
        int stepsAheadOfTheCue = 0;

        foreach (var reading in readings.EnumerateArray())
        {
            double t = reading.GetProperty("t").GetDouble();

            if (reading.GetProperty("at").GetProperty("line").GetInt32() != 1 || t >= cue)
                continue;

            stepsAheadOfTheCue++;

            if (armedAt == 0 && reading.GetProperty("op").GetString() == "key")
            {
                armedAt = t;
                clockAtTheArm = reading.GetProperty("activeTimeMs").GetDouble();
            }

            clockAtTheEnd = reading.GetProperty("activeTimeMs").GetDouble();
        }

        Assert.Multiple(() =>
        {
            Assert.That(stepsAheadOfTheCue, Is.GreaterThan(2), "the script never sat on line 1 ahead of its cue");
            Assert.That(armedAt, Is.GreaterThan(0).And.LessThan(cue), "no press was made ahead of the cue, so nothing ever armed");
            Assert.That(clockAtTheEnd - clockAtTheArm, Is.EqualTo(100), "the arm credited no typing time at all");
        });
    }
}
