using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Typebeat.Tools.ScoreRecalc;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the BACK-DATED SEAL BREAK (backlog 259): a line's one combo break belongs
/// to the cells it runs out of time on, not to the instant its seal happens to land.
///
/// <para>A line's misses only exist at its SEAL, and under the flexible caret that seal lands up to
/// <c>FLETCHER_DRAG_GRACE_MS</c> after the song left the line, by which time the player is on the
/// next line and rebuilding. The break was landing on the run they hold NOW. Dated at the cells it
/// misses instead, it destroys only what was earned AT OR BEFORE the line's LAST unforeseen missed
/// cell in (line, cell) order, and every increment earned strictly past it survives. The player's
/// report is the sharpest case: a typo takes its break at the KEYPRESS, so the empty cell it leaves
/// behind once backspaced made the seal take a SECOND break for the same fumble a line later.</para>
///
/// <para>The two clients submit to the SAME leaderboards, so this is not a cosmetic difference: the
/// combo a seal leaves behind weights every combo portion after it, and wherever the surviving run
/// outgrows the old maximum it moves <c>max_combo</c> too. A browser still wiping the run would
/// score the identical performance lower than the desktop.</para>
///
/// <para>TWO ARMS, because the rule lives in two accounts. The ENGINE's own combo is what the ledger
/// (<c>runPositions</c>) cuts back, and it is compared step for step below. The SUBMITTED account is
/// the half those readings cannot see: the break moved OFF the Miss results (they are all
/// combo-neutral now) and onto a hand-mirror written before them, so a browser that marked the
/// misses neutral and forgot the mirror, or wrote it after the results, would show the right engine
/// combo and submit the wrong score. That arm goes through
/// <see cref="TypeBeatReplayScorer"/>, which is the headless assembly of the very seams
/// <c>TypeBeatPlayfield</c> wires up in a live play.</para>
///
/// <para>ONE COPY OF THE KEYSTROKES, the shape <see cref="LineSkipLiveParityTest"/> established: the
/// scripts and the browser's readings of them come out of <c>CoreFlexibleLinesHarness.cjs</c>'s
/// <c>sealComboBreak</c> section. They live in that harness because the rule is only REACHABLE under
/// the flexible caret: with the caret pinned to the playhead there is no way to have earned combo
/// past a cell the line went on to miss, so the two arms would agree cell for cell whatever either
/// of them did.</para>
/// </summary>
[TestFixture]
public class SealComboBreakLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("sealComboBreak");

    private static JsonElement Run(string scenario) => Section().GetProperty("runs").GetProperty(scenario);

    private static JsonElement Fixture(string scenario) => Section().GetProperty("fixtures").GetProperty(scenario);

    /// <summary>The two scenarios, so every test below sweeps both rather than naming one.</summary>
    private static readonly string[] scenarios = ["theReport", "trailingCellsNeverTouched"];

    #region The fixtures, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's two maps, in the game's terms. Every window is what the browser's loader
    /// derived (contiguous lines, a final line stretched past its vocals), written out here so the
    /// two sides hold the same map rather than two nearly equal ones.
    ///
    /// <para><c>theReport</c>: L0 "ab cd" [1000, 4000) sung to 3000 (a = 1000, b = 1500, ' ' = 2000,
    /// c = 2000, d = 2500), L1 "ef" [4000, 8000) sung to 5000, L2 "gh" [8000, 12000) sung to 9000.
    /// Entry into L1 opens at 2500, which is where the Enter lands.</para>
    ///
    /// <para><c>trailingCellsNeverTouched</c>: L0 "abc" [0, 5500) sung to 3000 (a = 0, b = 1000,
    /// c = 2000), L1 "defgh" [5500, 12500) sung to 9500 (d = 5500, then every 800 ms). L0's drag
    /// grace runs to 7000, so its seal lands between the presses on 'e' and 'f'.</para>
    /// </summary>
    private static LyricLine[] Lines(string scenario) => scenario switch
    {
        "theReport" =>
        [
            Line("ab cd", 1000, 4000, 3000, Unit("ab", 1000, 2000), Unit("cd", 2000, 3000)),
            Line("ef", 4000, 8000, 5000, Unit("ef", 4000, 5000)),
            Line("gh", 8000, 12000, 9000, Unit("gh", 8000, 9000)),
        ],
        "trailingCellsNeverTouched" =>
        [
            Line("abc", 0, 5500, 3000, Unit("abc", 0, 3000)),
            Line("defgh", 5500, 12500, 9500, Unit("defgh", 5500, 9500)),
        ],
        _ => throw new ArgumentException($"unknown scenario {scenario}"),
    };

    /// <summary>The map as the ENGINE arm wants it.</summary>
    private static LyricBeatmap Map(string scenario) => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata
        {
            Artist = "a",
            Title = "t",
            FolderPath = @"X:\nowhere",
            AudioFileName = "a.mp3",
        },
        Lines = Lines(scenario),
        Granularity = TimingGranularity.Line,
    };

    /// <summary>The same map as the SUBMITTED-ACCOUNT arm wants it: a playable beatmap with the
    /// nested per-cell objects the score processor's maximum statistics come from.</summary>
    private static TypeBeatBeatmap Playable(string scenario)
    {
        var map = new TypeBeatBeatmap();
        var lines = Lines(scenario);

        for (int i = 0; i < lines.Length; i++)
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Line });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Song";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    /// <summary>
    /// A started engine under every LIVE rule, which is the only arm the browser can be compared
    /// against: it has no mods payload, writes no replay frames and re-derives no stored row, so the
    /// era flags the C# defaults OFF for replay decoding have to be set by hand here.
    /// <see cref="TypingEngine.BackDatedSealBreak"/> is the one this file is about, and it is the
    /// reason a bare engine is not the live client: it defaults false so that every stored replay
    /// re-derives the whole-run wipe its player was submitted under.
    /// </summary>
    private static TypingEngine LiveEngine(string scenario, bool backDatedSealBreak = true) => new TypingEngine(Map(scenario))
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
        BackDatedSealBreak = backDatedSealBreak,
        LosslessSkipReclaim = true,
        FoldsDisplacedClaim = true,
    };

    /// <summary>
    /// The script as a replay, headed by the CONFIG frame the era bits travel in. Bit 10 is this
    /// file's subject and is a parameter rather than a constant, because clearing it is how
    /// <see cref="TheseFixturesReallySeparateTheTwoEras"/> proves the fixtures are about the rule at
    /// all. Bit 1 (space-skips-word) is deliberately CLEAR: the browser hardcodes that setting off,
    /// and no script here presses a space inside a word.
    /// </summary>
    private static Replay Replay(string scenario, bool backDatedSealBreak = true)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: backDatedSealBreak, losslessSkipReclaim: true, foldsDisplacedClaim: true));

        foreach (var step in Run(scenario).GetProperty("script").EnumerateArray())
        {
            double time = step.GetProperty("t").GetDouble();

            switch (step.GetProperty("op").GetString())
            {
                case "update":
                    continue;

                case "enter":
                    replay.Frames.Add(new TypeBeatReplayFrame(time, TypeBeatReplayFrame.ENTER));
                    break;

                case "backspace":
                    replay.Frames.Add(new TypeBeatReplayFrame(time, TypeBeatReplayFrame.BACKSPACE));
                    break;

                case "key":
                    replay.Frames.Add(new TypeBeatReplayFrame(time, step.GetProperty("c").GetString()![0]));
                    break;

                default:
                    throw new ArgumentException($"the harness emitted an op this side does not have: {step.GetProperty("op").GetString()}");
            }
        }

        return replay;
    }

    private static TypeBeatReplayAccount Play(string scenario, bool backDatedSealBreak = true)
        => TypeBeatReplayScorer.Score(Playable(scenario), Array.Empty<Mod>(), Replay(scenario, backDatedSealBreak), TypoRule.Deferred, ComboRestoreRule.OnFix);

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

    /// <summary>
    /// The game's counts as the wire spells them, minus the LINE containers' <c>ignore_hit</c>: the
    /// line object is scoring-inert, so <c>typebeat-core.js</c> does not model it and the server's
    /// <c>ScoringContract</c> ignores it when it recomputes. Identical to
    /// <see cref="WordSkipLiveParityTest"/>'s filter, and for the same reason.
    /// </summary>
    private static Dictionary<string, int> Wire(IReadOnlyDictionary<HitResult, int> counts)
        => WireCounts.From(counts.Where(entry => entry.Key != TypeBeatResultMapping.LINE_RESULT).ToDictionary(entry => entry.Key, entry => entry.Value));

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    #endregion

    /// <summary>
    /// THE FIXTURES BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both
    /// sides build their cells through their own loader (the browser's <c>buildBeatmap</c>, the
    /// game's <c>TypingLine.FromLyricLine</c>), so a fixture that drifted would show up below as an
    /// engine divergence and be blamed on the seal.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnBothFixtures()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Section().GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(TypingEngine.FLETCHER_DRAG_GRACE_MS));

            foreach (string scenario in scenarios)
            {
                var engine = LiveEngine(scenario);
                var lines = Fixture(scenario).GetProperty("lines");

                Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), $"{scenario}: line count");

                for (int i = 0; i < engine.Lines.Count; i++)
                {
                    var line = engine.Lines[i];
                    var browserLine = lines[i];

                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{scenario}[{i}]: activationTime");
                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{scenario}[{i}]: endTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{scenario}[{i}]: sealGraceMs");

                    var browserCells = browserLine.GetProperty("cells");
                    Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{scenario}[{i}]: cell count");

                    for (int c = 0; c < line.Cells.Count; c++)
                    {
                        Assert.That(browserCells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"{scenario}[{i}][{c}]: expected");
                        Assert.That(browserCells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"{scenario}[{i}][{c}]: target");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Each script replayed through the game's engine and compared to the browser's readings step
    /// for step: where the caret is, which line the seal has reached, every cell's state, and the
    /// whole combo account.
    ///
    /// <para>The reading that matters is the combo on the step the seal lands. In
    /// <c>theReport</c> it must be 2 (a run rebuilt entirely on the next line, so nothing the break
    /// is entitled to touch), and in <c>trailingCellsNeverTouched</c> it must be 2 out of the 4 the
    /// player was holding (the pair earned on the sealed line dies, the pair earned past it lives).
    /// A wipe reads 0 in both, and every later reading is one short for the rest of the run.</para>
    ///
    /// <para><c>runPositions</c> is asserted by LENGTH rather than by content, because the C# list
    /// is private: what it is worth pinning is the invariant the whole rule rests on,
    /// <c>runPositions.length == combo</c>, which is what makes a partial break able to say which
    /// part it takes. The browser's entries are the ledger the C# cannot expose, so a drift in WHERE
    /// an increment was recorded shows up in the combo readings after the next seal instead.</para>
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOfEveryScript()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var engine = LiveEngine(scenario);
                var script = Run(scenario).GetProperty("script");
                var readings = Run(scenario).GetProperty("readings");

                int breaks = 0;
                engine.ComboBroken += () => breaks++;

                Assert.That(readings.GetArrayLength(), Is.EqualTo(script.GetArrayLength()), $"{scenario}: one reading per step");

                for (int i = 0; i < script.GetArrayLength(); i++)
                {
                    var step = script[i];
                    var reading = readings[i];
                    double t = step.GetProperty("t").GetDouble();
                    string op = step.GetProperty("op").GetString()!;
                    string where = $"{scenario}[{i}] {op} {t}";

                    switch (op)
                    {
                        case "update":
                            engine.Update(t);
                            break;

                        case "enter":
                            Assert.That(engine.ProcessEnter(t), Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where}: handled");
                            break;

                        case "backspace":
                            Assert.That(engine.ProcessBackspace(), Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where}: handled");
                            break;

                        case "key":
                            char c = step.GetProperty("c").GetString()![0];
                            Assert.That(engine.ProcessKey(c, t), Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where} '{c}': handled");
                            break;

                        default:
                            throw new ArgumentException($"the harness emitted an op this side does not have: {op}");
                    }

                    var at = reading.GetProperty("at");
                    Assert.That(engine.ActiveLineIndex, Is.EqualTo(at.GetProperty("line").GetInt32()), $"{where}: active line");
                    Assert.That(engine.CaretIndex, Is.EqualTo(at.GetProperty("cell").GetInt32()), $"{where}: caret");
                    Assert.That(engine.NextUnsealedLineIndex, Is.EqualTo(reading.GetProperty("nextUnsealedLineIndex").GetInt32()), $"{where}: the seal's position");
                    Assert.That(engine.Combo, Is.EqualTo(reading.GetProperty("combo").GetInt32()), $"{where}: combo");
                    Assert.That(engine.MaxCombo, Is.EqualTo(reading.GetProperty("maxCombo").GetInt32()), $"{where}: max combo");
                    Assert.That(breaks, Is.EqualTo(reading.GetProperty("comboBreaks").GetInt32()), $"{where}: combo breaks");
                    Assert.That(engine.Mistypes, Is.EqualTo(reading.GetProperty("mistypes").GetInt32()), $"{where}: mistypes");
                    Assert.That(engine.IsFinished, Is.EqualTo(reading.GetProperty("finished").GetBoolean()), $"{where}: finished");

                    Assert.That(reading.GetProperty("runPositions").GetArrayLength(), Is.EqualTo(engine.Combo),
                        $"{where}: the browser's ledger holds one entry per unit of combo");

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
    /// The other account: the SUBMITTED one, browser against the game's own scorer, field for field
    /// with no tolerance on the doubles.
    ///
    /// <para>This is the arm the engine readings cannot stand in for. The seal's break no longer
    /// travels on the Miss results at all (every seal miss is applied combo-neutral), so it reaches
    /// the submitted combo only through a hand-mirror written immediately BEFORE those results, from
    /// the run the engine was left holding. Forget that write and the submitted combo keeps a run
    /// the break was entitled to cut; make it after the results instead of before and the seal's own
    /// unfixed typos are weighted by the wrong combo. Neither shows up in
    /// <see cref="TheGameEngineMakesTheSameRunOfEveryScript"/>, and both change what lands on the
    /// shared leaderboards.</para>
    /// </summary>
    [Test]
    public void TheSubmittedAccountsAgree()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var game = Play(scenario);
                var submitted = Run(scenario).GetProperty("submitted");

                Assert.That(game.UnconsumedFrames, Is.Zero, $"{scenario}: the game never reached some of the recorded keystrokes");
                Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Statistics)), $"{scenario}: statistics");
                Assert.That(Dict(submitted, "maximumStatistics"), Is.EquivalentTo(Wire(game.MaximumStatistics)), $"{scenario}: maximum_statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.MaxCombo), $"{scenario}: max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.TotalScore), $"{scenario}: total_score");
                Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Accuracy), $"{scenario}: accuracy");
                Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(game.Completion), $"{scenario}: completion");
                Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(game.Rank.ToString()), $"{scenario}: rank");
            }
        });
    }

    /// <summary>
    /// NON-VACUITY, and the only assertion here that does not compare the two clients: the same
    /// replay re-derived with CONFIG bit 10 CLEAR, which is the whole-run wipe every stored score was
    /// played under. If the browser's numbers matched THAT too, the two tests above would be pinning
    /// a rule neither script reaches.
    ///
    /// <para>Both scenarios must part from the classic arm, and part in the direction the rule
    /// promises: the back-dated run is never shorter and never worth less, because the break can only
    /// ever destroy a subset of what the wipe destroyed. The scripts are built so the difference is
    /// visible in <c>max_combo</c> and not only in the combo portion, which is what makes them
    /// scenarios rather than rounding.</para>
    /// </summary>
    [Test]
    public void TheseFixturesReallySeparateTheTwoEras()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var backDated = Play(scenario);
                var classic = Play(scenario, backDatedSealBreak: false);

                Assert.That(classic.MaxCombo, Is.LessThan(backDated.MaxCombo), $"{scenario}: the wipe should cost max_combo");
                Assert.That(classic.TotalScore, Is.LessThan(backDated.TotalScore), $"{scenario}: the wipe should cost total_score");
                Assert.That(backDated.Statistics, Is.EquivalentTo(classic.Statistics), $"{scenario}: the era must move no judgement");
                Assert.That(backDated.Accuracy, Is.EqualTo(classic.Accuracy), $"{scenario}: the era must move no accuracy");
                Assert.That(backDated.Completion, Is.EqualTo(classic.Completion), $"{scenario}: the era must move no completion");
            }
        });
    }

    /// <summary>
    /// And the other half of non-vacuity, read off the BROWSER's own run rather than off either
    /// engine, because a comparison of two clients that both did nothing interesting passes. Three
    /// things have to be true of these scripts or the tests above are not about the rule:
    ///
    /// <list type="bullet">
    /// <item>A seal really BROKE combo (an unforeseen missed cell, not merely an abandoned one) while
    /// the play carried on, which is the only situation the rule decides.</item>
    /// <item>That break left the run STANDING rather than at zero, which is the rule itself.</item>
    /// <item>One of the scripts had the break destroy part of the run and spare the rest, so the
    /// coverage is not two copies of the degenerate "nothing to destroy" corner the report is.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheScriptsReallyReachAPartialSurvival()
    {
        int survivingBreaks = 0;
        int partialBreaks = 0;

        foreach (string scenario in scenarios)
        {
            var readings = Run(scenario).GetProperty("readings");

            for (int i = 1; i < readings.GetArrayLength(); i++)
            {
                var before = readings[i - 1];
                var after = readings[i];

                if (after.GetProperty("comboBreaks").GetInt32() == before.GetProperty("comboBreaks").GetInt32())
                    continue;

                if (after.GetProperty("op").GetString() != "update")
                    continue; // a keypress break (the typo), not a seal's

                int held = before.GetProperty("combo").GetInt32();
                int left = after.GetProperty("combo").GetInt32();

                Assert.That(left, Is.GreaterThan(0), $"{scenario}[{i}]: the seal wiped the whole run");
                Assert.That(after.GetProperty("processorCombo").GetInt32(), Is.EqualTo(left),
                    $"{scenario}[{i}]: the submitted combo did not follow the engine's through the seal");

                survivingBreaks++;

                if (left < held) partialBreaks++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(survivingBreaks, Is.EqualTo(2), "each script should seal exactly one line on cells nobody typed while the play carried on");
            Assert.That(partialBreaks, Is.EqualTo(1), "no script had the break destroy part of the run and spare the rest");
        });
    }
}
