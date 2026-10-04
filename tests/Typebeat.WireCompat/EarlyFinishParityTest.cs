using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on EARLY FINISH (bit 4 of the SECOND CONFIG flags word, the 0x01
/// CONFIG_EXTENDED carrier): the map's FINAL line seals the moment every typeable cell of it is
/// typed correctly, rather than at the line's own end, so the run (and the results card that
/// follows) rides the player's last word instead of the song.
///
/// <para>The two clients submit to the SAME leaderboards, so this is not cosmetic: where the run
/// ends decides which cells the final line seals (a fully correct line is judgement-neutral, so only
/// the INSTANT moves) and therefore when the score is taken and the results shown. A browser still
/// waiting for the line's end would finish seconds after the desktop on the identical performance
/// and, worse, could take a different score if any cell were still fixable in the interval.</para>
///
/// <para>TWO ARMS, because the rule lives in two accounts. The browser has no replay axis: it only
/// plays live, writes no frames and re-derives no stored row, so it bakes the era in unconditionally,
/// exactly as it does <c>AuthoredSyllablesOnly</c> (bit 2) and PR 5's aligned targets (bit 3). The
/// C# arm sets <c>TypingEngine.EarlyFinish</c> for the live stack and leaves it CLEAR for a stored
/// replay, which is what every replay recorded before the era re-derives. The harness emits BOTH of
/// its own runs (live and early-seal-stubbed-out), and each C# arm is pinned against its browser
/// twin, so a JS that lost the early seal fails on the live arm and a JS that sealed early with the
/// bit clear would fail on the legacy arm.</para>
///
/// <para>ONE COPY OF THE KEYSTROKES, the shape <see cref="SealComboBreakLiveParityTest"/>
/// established: the script comes out of <c>CoreFlexibleLinesHarness.cjs</c>'s <c>earlyFinish</c>
/// section.</para>
/// </summary>
[TestFixture]
public class EarlyFinishParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("earlyFinish");

    private static JsonElement Fixture() => Section().GetProperty("fixture");

    private static JsonElement Run(string arm) => Section().GetProperty("runs").GetProperty(arm);

    /// <summary>The two arms the harness emits: the browser's live seal, and the legacy one.</summary>
    private static readonly string[] arms = ["live", "legacy"];

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's map in the game's terms. Two contiguous lines, every grace 0, so L0's deadline is
    /// L1's own start and L1's (the FINAL line, where the rule applies) is the song end.
    ///   L0 "ab" [1000, 5000), sung [1000, 2000): a = 1000, b = 1500.
    ///   L1 "cd" [5000, 10000), sung [5000, 7000]: c = 5000, d = 6000.
    /// </summary>
    private static LyricBeatmap Map() => new LyricBeatmap
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
            Line("ab", 1000, 5000, 2000, Unit("ab", 1000, 2000)),
            Line("cd", 5000, 10000, 7000, Unit("cd", 5000, 7000)),
        ],
        Granularity = TimingGranularity.Line,
    };

    /// <summary>
    /// A started engine under every LIVE rule, which is the only arm the browser can be compared
    /// against: it has no mods payload, writes no replay frames and re-derives no stored row, so the
    /// era flags the C# defaults OFF for replay decoding have to be set by hand here.
    /// <paramref name="earlyFinish"/> is this file's subject and is the ONE thing the two arms differ
    /// in: true is the live stack (DrawableTypeBeatRuleset sets it unconditionally), false is what
    /// every stored replay re-derives.
    ///
    /// <para>ManualNewlines is CLEAR, matching the harness's <c>automaticArm</c>: this script never
    /// presses a newline and the browser section declares the automatic hand-over.</para>
    /// </summary>
    private static TypingEngine Engine(bool earlyFinish) => new TypingEngine(Map())
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
        AuthoredSyllablesOnly = true,
        AlignSubdivisionTargets = true,
        // THIS FILE'S SUBJECT: bit 4 of that same word.
        EarlyFinish = earlyFinish,
    };

    /// <summary>The cell states as the JS mirror spells them (its own vocabulary, one for one).</summary>
    private static string StateName(CellState state) => state switch
    {
        CellState.Untyped => "untyped",
        CellState.Correct => "correct",
        CellState.Wrong => "wrong",
        CellState.Missed => "missed",
        CellState.AutoSkipped => "autoskip",
        CellState.Abandoned => "abandoned",
        _ => state.ToString(),
    };

    #endregion

    /// <summary>
    /// THE FIXTURE BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both sides
    /// build their cells through their own loader, so a fixture that drifted shows up here rather than
    /// being blamed on the seal.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheFixture()
    {
        var engine = Engine(earlyFinish: true);
        var lines = Fixture().GetProperty("lines");

        Assert.Multiple(() =>
        {
            Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), "line count");

            for (int i = 0; i < engine.Lines.Count; i++)
            {
                var line = engine.Lines[i];
                var browserLine = lines[i];

                Assert.That(browserLine.GetProperty("startTime").GetDouble(), Is.EqualTo(line.StartTime), $"[{i}]: startTime");
                Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"[{i}]: activationTime");
                Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"[{i}]: endTime");
                Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"[{i}]: sealGraceMs");

                var browserCells = browserLine.GetProperty("cells");
                Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"[{i}]: cell count");

                for (int c = 0; c < line.Cells.Count; c++)
                {
                    Assert.That(browserCells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"[{i}][{c}]: expected");
                    Assert.That(browserCells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"[{i}][{c}]: target");
                }
            }
        });
    }

    /// <summary>
    /// Each arm's script replayed through the game's engine (with <c>EarlyFinish</c> set or clear to
    /// match) and compared to the browser's readings step for step: where the caret is, which line the
    /// seal has reached, every cell's state, and whether the run has finished.
    ///
    /// <para>The reading that matters is the step the era DECIDES. Under <c>live</c> the run must be
    /// finished at 5400, 4600 ms before the final line's own end at 10000; under <c>legacy</c> it must
    /// still be running at 5400 and finish only at 10000. A browser that sealed early anyway would
    /// fail the legacy arm, and one that waited would fail the live arm.</para>
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOnBothArms()
    {
        Assert.Multiple(() =>
        {
            foreach (string arm in arms)
            {
                var engine = Engine(earlyFinish: arm == "live");
                var script = Run(arm).GetProperty("script");
                var readings = Run(arm).GetProperty("readings");

                Assert.That(readings.GetArrayLength(), Is.EqualTo(script.GetArrayLength()), $"{arm}: one reading per step");

                for (int i = 0; i < script.GetArrayLength(); i++)
                {
                    var step = script[i];
                    var reading = readings[i];
                    double t = step.GetProperty("t").GetDouble();
                    string op = step.GetProperty("op").GetString()!;
                    string where = $"{arm}[{i}] {op} {t}";

                    if (op == "update")
                        engine.Update(t);
                    else
                        engine.ProcessKey(step.GetProperty("c").GetString()![0], t);

                    var at = reading.GetProperty("at");
                    Assert.That(engine.ActiveLineIndex, Is.EqualTo(at.GetProperty("line").GetInt32()), $"{where}: active line");
                    Assert.That(engine.CaretIndex, Is.EqualTo(at.GetProperty("cell").GetInt32()), $"{where}: caret");
                    Assert.That(engine.NextUnsealedLineIndex, Is.EqualTo(reading.GetProperty("nextUnsealedLineIndex").GetInt32()), $"{where}: the seal's position");
                    Assert.That(engine.IsFinished, Is.EqualTo(reading.GetProperty("finished").GetBoolean()), $"{where}: finished");

                    var states = reading.GetProperty("states");
                    Assert.That(states.GetArrayLength(), Is.EqualTo(engine.Lines.Count), $"{where}: one state row per line");

                    for (int l = 0; l < engine.Lines.Count; l++)
                    {
                        var cells = engine.Lines[l].Cells;
                        Assert.That(states[l].GetArrayLength(), Is.EqualTo(cells.Count), $"{where}: line {l} cell count");

                        for (int c = 0; c < cells.Count; c++)
                            Assert.That(StateName(cells[c].State), Is.EqualTo(states[l][c].GetString()), $"{where}: line {l} cell {c} state");
                    }
                }
            }
        });
    }

    /// <summary>
    /// NON-VACUITY, read off the harness's OWN two arms rather than either engine: if the browser's
    /// live and legacy runs did not part, the tests above would be pinning a rule neither reaches.
    /// The live arm must finish at the step the rule promises (5400), the legacy arm must still be
    /// running there and finish only at its own deadline (10000), and the two must agree on every
    /// cell's state (the era moves the INSTANT, not a judgement).
    /// </summary>
    [Test]
    public void TheTwoArmsReallySeparateOnTheFinalLine()
    {
        var live = Run("live").GetProperty("readings");
        var legacy = Run("legacy").GetProperty("readings");

        // The decisive step, the one the early seal is the difference on, is the first update after
        // the final line is typed out.
        int decisive = -1;

        for (int i = 0; i < live.GetArrayLength(); i++)
        {
            if (live[i].GetProperty("t").GetDouble() == 5400)
                decisive = i;
        }

        Assert.That(decisive, Is.GreaterThanOrEqualTo(0), "the script should carry the decisive update at 5400");

        Assert.Multiple(() =>
        {
            Assert.That(live[decisive].GetProperty("finished").GetBoolean(), Is.True, "the live arm should have finished the run early");
            Assert.That(legacy[decisive].GetProperty("finished").GetBoolean(), Is.False, "the legacy arm should still be waiting for the final line's end");
            Assert.That(live[live.GetArrayLength() - 1].GetProperty("finished").GetBoolean(), Is.True, "the live arm stays finished");
            Assert.That(legacy[legacy.GetArrayLength() - 1].GetProperty("finished").GetBoolean(), Is.False, "the legacy arm never runs past the map's own end without a seal it does not take here");

            // The era is judgement-neutral on a fully correct line: every cell reads the same on both.
            Assert.That(live[decisive].GetProperty("states").GetRawText(), Is.EqualTo(legacy[decisive].GetProperty("states").GetRawText()),
                "the early seal must move only the instant, never a cell");

            // And the legacy arm really does seal at the final line's own end, not sooner.
            Assert.That(legacy[decisive].GetProperty("nextUnsealedLineIndex").GetInt32(), Is.EqualTo(1), "the final line is still unsealed under the legacy era");
            Assert.That(live[decisive].GetProperty("nextUnsealedLineIndex").GetInt32(), Is.EqualTo(-1), "the live era sealed it");
        });
    }
}
