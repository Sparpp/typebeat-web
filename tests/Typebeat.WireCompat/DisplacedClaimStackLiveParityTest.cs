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
/// The cross-repo pin on the DISPLACED CLAIM STACK (backlog 422): correcting a broken cell refunds the
/// run THAT break broke, and nothing owed to another break unless that break's cell is corrected too.
///
/// <para>The report: a miss on an earlier line, then a miss on the current line, and correcting the
/// latter put the combo back to what it was before the FORMER miss. Backlog 262's fold (CONFIG bit 12,
/// <see cref="DisplacedClaimFoldLiveParityTest"/>) credited the displaced claim whether or not its own
/// cell was ever fixed. Under <see cref="TypingEngine.StacksDisplacedClaims"/> (bit 14 of the SECOND
/// CONFIG word) the displaced claim is kept below the new one: each waits for its own cell, a
/// correction further down joins its run to the next newer claim, and a sealed line's claims are
/// dropped. <c>core/typing-engine.js</c> runs it UNCONDITIONALLY (<c>displacedClaims</c>,
/// <c>joinDisplacedClaimsOn</c>, <c>dropClaimOn</c>), because the browser only ever plays live.</para>
///
/// <para>The two clients submit to the SAME leaderboards: the rule moves <c>max_combo</c> and the combo
/// weight of every judgement after a refund, so a browser still folding would score the identical
/// performance HIGHER than the desktop. The shape is <see cref="DisplacedClaimFoldLiveParityTest"/>'s:
/// the ENGINE compared step for step, the SUBMITTED account through <see cref="TypeBeatReplayScorer"/>,
/// and ONE COPY OF THE KEYSTROKES, the <c>displacedClaimStack</c> section of
/// <c>CoreFlexibleLinesHarness.cjs</c>.</para>
/// </summary>
[TestFixture]
public class DisplacedClaimStackLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("displacedClaimStack");

    private static JsonElement Run(string scenario) => Section().GetProperty("runs").GetProperty(scenario);

    private static readonly string[] scenarios =
        ["reportedShape", "reportedShapeFixedBeforeTheSeal", "olderLeftWrong", "bothCorrectedNewerFirst", "threeBreaksOldestLeftWrong"];

    /// <summary>The scripts that leave an older broken cell WRONG, which are the ones the fold over-credits.</summary>
    private static readonly string[] leftWrong = ["reportedShape", "reportedShapeFixedBeforeTheSeal", "olderLeftWrong", "threeBreaksOldestLeftWrong"];

    #region The fixtures, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's two maps, with every window the browser's loader derives written out.
    ///
    /// <para>The reported shape: L0 "abcd" [1000, 4000) sung to 3000 (a = 1000, b = 1500, c = 2000,
    /// d = 2500), L1 "efgh" [4000, 9000) sung to 6000 (e = 4000, f = 4500, g = 5000, h = 5500).
    /// Entry into L1 opens at 2500.</para>
    ///
    /// <para>The one-line shapes: "abcde fg hi" [1000, 4900) sung to 1900, the fold section's own
    /// map (0:a = 1000 ... 5:' ' = 1500, 6:f = 1500, 7:g = 1600, 8:' ' = 1700, 9:h = 1700,
    /// 10:i = 1800).</para>
    /// </summary>
    private static LyricLine[] Lines(string scenario) => scenario.StartsWith("reportedShape", StringComparison.Ordinal)
        ?
        [
            Line("abcd", 1000, 4000, 3000, Unit("abcd", 1000, 3000)),
            Line("efgh", 4000, 9000, 6000, Unit("efgh", 4000, 6000)),
        ]
        :
        [
            Line("abcde fg hi", 1000, 4900, 1900, Unit("abcde", 1000, 1500), Unit("fg", 1500, 1700), Unit("hi", 1700, 1900)),
        ];

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

    /// <summary>A started engine under every LIVE rule; <see cref="TypingEngine.StacksDisplacedClaims"/> is this file's subject.</summary>
    private static TypingEngine LiveEngine(string scenario) => new TypingEngine(Map(scenario))
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
        StacksDisplacedClaims = true,
        FirstLineLeadIn = true,
        RushCapCostsAccuracy = true,
        InputEra2 = true,
        AuthoredSyllablesOnly = true,
        AlignSubdivisionTargets = true,
        EarlyFinish = true,
        KeyTimeJudgement = true,
        ManualNewlines = true,
        NewlineOnTypedLetter = true,
    };

    /// <summary>
    /// The script as a replay under the live client's headers. Bit 14 of the second word is a
    /// parameter, because clearing it is how <see cref="TheseScriptsReallySeparateTheTwoEras"/> proves
    /// the fixtures are about the rule, and how the stored era is shown to re-derive the fold.
    /// </summary>
    private static Replay Replay(bool stacksDisplacedClaims, string scenario)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true, manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true,
            earlyFinish: true, keyTimeJudgement: true, bestRetypeJudgement: true, stacksDisplacedClaims: stacksDisplacedClaims));

        foreach (var step in Run(scenario).GetProperty("script").EnumerateArray())
        {
            double time = step.GetProperty("t").GetDouble();

            switch (step.GetProperty("op").GetString())
            {
                case "update":
                    continue;

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

    private static TypeBeatReplayAccount Play(string scenario, bool stacksDisplacedClaims = true)
        => TypeBeatReplayScorer.Score(Playable(scenario), Array.Empty<Mod>(), Replay(stacksDisplacedClaims, scenario), TypoRule.Deferred, ComboRestoreRule.OnFix);

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

    /// <summary>The game's counts as the wire spells them, minus the scoring-inert line containers (see <see cref="DisplacedClaimFoldLiveParityTest"/>).</summary>
    private static Dictionary<string, int> Wire(IReadOnlyDictionary<HitResult, int> counts)
        => WireCounts.From(counts.Where(entry => entry.Key != TypeBeatResultMapping.LINE_RESULT).ToDictionary(entry => entry.Key, entry => entry.Value));

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static int[] Restores(string scenario)
        => Run(scenario).GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray();

    #endregion

    /// <summary>The fixtures before the accounts: both sides build their cells through their own loader.</summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheFixtures()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var engine = LiveEngine(scenario);
                var lines = Section().GetProperty("fixtures").GetProperty(scenario).GetProperty("lines");

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
    /// Each script through the game's engine against the browser's readings, step for step: the caret,
    /// every cell's state, the combo, the max, every break and every refund. <c>runPositions</c> is held
    /// by LENGTH against the combo, the invariant every join of the stack has to keep.
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
                var restores = new List<int>();
                engine.ComboBroken += () => breaks++;
                engine.ComboRestored += restores.Add;

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
                    Assert.That(engine.Combo, Is.EqualTo(reading.GetProperty("combo").GetInt32()), $"{where}: combo");
                    Assert.That(engine.MaxCombo, Is.EqualTo(reading.GetProperty("maxCombo").GetInt32()), $"{where}: max combo");
                    Assert.That(breaks, Is.EqualTo(reading.GetProperty("comboBreaks").GetInt32()), $"{where}: combo breaks");
                    Assert.That(restores, Is.EqualTo(reading.GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray()), $"{where}: combo restores");
                    Assert.That(engine.Mistypes, Is.EqualTo(reading.GetProperty("mistypes").GetInt32()), $"{where}: mistypes");

                    Assert.That(reading.GetProperty("runPositions").GetArrayLength(), Is.EqualTo(engine.Combo),
                        $"{where}: the browser's ledger holds one entry per unit of combo");

                    var states = reading.GetProperty("states");

                    for (int l = 0; l < engine.Lines.Count; l++)
                    {
                        var cells = engine.Lines[l].Cells;

                        for (int c = 0; c < cells.Count; c++)
                            Assert.That(StateName(cells[c].State), Is.EqualTo(states[l][c].GetString()), $"{where}: line {l} cell {c} state");
                    }
                }
            }
        });
    }

    /// <summary>The SUBMITTED account, browser against the game's own scorer, field for field with no tolerance.</summary>
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
    /// THE RULE'S OWN NUMBERS, read off the browser. The report: the fix on L1 refunds the 3 its own
    /// break took, whether L0 has sealed yet or not, and 'c' left wrong is never paid for. Both
    /// corrected newer first: two refunds (1, then 6) reaching the clean run's 11. The chain with its
    /// oldest cell left wrong: one refund of 2.
    /// </summary>
    [Test]
    public void EachRefundIsTheRunItsOwnBreakBroke()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in new[] { "reportedShape", "reportedShapeFixedBeforeTheSeal" })
            {
                Assert.That(Restores(scenario), Is.EqualTo(new[] { 3 }), $"{scenario}: the run of 3 the L1 typo broke, and nothing owed to L0's");
                Assert.That(Run(scenario).GetProperty("maxCombo").GetInt32(), Is.EqualTo(5), $"{scenario}: 3, the fix and 'h'");
            }

            Assert.That(Restores("olderLeftWrong"), Is.EqualTo(new[] { 1 }));
            Assert.That(Run("olderLeftWrong").GetProperty("combo").GetInt32(), Is.EqualTo(4));

            Assert.That(Restores("bothCorrectedNewerFirst"), Is.EqualTo(new[] { 1, 6 }), "each break's own run, when its own cell is fixed");
            Assert.That(Run("bothCorrectedNewerFirst").GetProperty("maxCombo").GetInt32(), Is.EqualTo(11), "both corrected cost nothing");

            Assert.That(Restores("threeBreaksOldestLeftWrong"), Is.EqualTo(new[] { 2 }), "the middle break's 1 joined the newest's 1");
            Assert.That(Run("threeBreaksOldestLeftWrong").GetProperty("maxCombo").GetInt32(), Is.EqualTo(8));
        });
    }

    /// <summary>
    /// NON-VACUITY and the STORED ERA: the same replays with bit 14 of the second word CLEAR, which
    /// every stored replay carries, re-derive backlog 262's fold exactly (the literals are today's
    /// numbers). Each script that leaves an older cell wrong must part from the live arm, in the
    /// direction the rule promises (the fold pays more), and move nothing but combo and score.
    /// </summary>
    [Test]
    public void TheseScriptsReallySeparateTheTwoEras()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in leftWrong)
            {
                var live = Play(scenario);
                var stored = Play(scenario, stacksDisplacedClaims: false);

                Assert.That(stored.MaxCombo, Is.GreaterThan(live.MaxCombo), $"{scenario}: the fold refunds the run owed to the cell left wrong");
                Assert.That(stored.TotalScore, Is.GreaterThan(live.TotalScore), $"{scenario}: and that combo weights the judgements after it");
                Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics), $"{scenario}: the era must move no judgement");
                Assert.That(live.Accuracy, Is.EqualTo(stored.Accuracy), $"{scenario}: the era must move no accuracy");
                Assert.That(live.Completion, Is.EqualTo(stored.Completion), $"{scenario}: the era must move no completion");
            }

            Assert.That(Play("reportedShape", stacksDisplacedClaims: false).MaxCombo, Is.EqualTo(7), "the fold: L0's 2 came back with L1's 3");
            Assert.That(Play("olderLeftWrong", stacksDisplacedClaims: false).MaxCombo, Is.EqualTo(10));
            Assert.That(Play("threeBreaksOldestLeftWrong", stacksDisplacedClaims: false).MaxCombo, Is.EqualTo(10));

            var bothLive = Play("bothCorrectedNewerFirst");
            var bothStored = Play("bothCorrectedNewerFirst", stacksDisplacedClaims: false);

            Assert.That(bothStored.MaxCombo, Is.EqualTo(bothLive.MaxCombo), "both corrected: the eras agree on the run");
            Assert.That(bothStored.MaxCombo, Is.EqualTo(11));
        });
    }
}
