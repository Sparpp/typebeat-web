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
/// The cross-repo pin on the DISPLACED CLAIM FOLD (backlog 262): a break that takes the claim off an
/// older break folds that claim into its own rather than discarding it, so TWO accidents, both fully
/// corrected, cost the run NOTHING.
///
/// <para>The report is score 13383. The player was 477 combo deep and clean when they typo'd the first
/// letter of a word, noticed nothing and typed the second letter correctly (a run of 1 they really
/// earned, so the next break was NOT passive under backlog 243), then typo'd the word gap after it.
/// That second break stood on a streak of its own, so it took the claim, and the overwrite arm of
/// <c>snapshotRedeemableBreak</c> threw the 477 away. Three backspaces and a perfect retype restored
/// 1, and the play finished with 0 misses, 100% completion and a max combo of 477 out of 894, which
/// breaks the law backlogs 243 and 260 wrote for the word skip.</para>
///
/// <para>Under the rule the displacing break's claim is <c>displacedStreak + brokenStreak</c> against
/// its OWN cell, with the displaced positions in front of its own in run order, so the NEWEST of the
/// broken cells redeems the whole chain and does so transitively. <c>typebeat-core.js</c> mirrors it
/// UNCONDITIONALLY, because the browser has no era axis: it only ever plays live.</para>
///
/// <para>The two clients submit to the SAME leaderboards, so this is not cosmetic: the fold moves
/// <c>max_combo</c> and the combo weight of every judgement after the redemption. A browser still
/// discarding the displaced claim would score the identical performance lower than the desktop.</para>
///
/// <para>TWO ARMS, the shape <see cref="SealComboBreakLiveParityTest"/> established and
/// <see cref="LosslessSkipReclaimLiveParityTest"/> repeated. The ENGINE's own run is compared step for
/// step, and the SUBMITTED account goes through <see cref="TypeBeatReplayScorer"/>, which is the
/// headless assembly of the seams <c>TypeBeatPlayfield</c> wires up in a live play. ONE COPY OF THE
/// KEYSTROKES: the scripts and the browser's readings of them come out of
/// <c>CoreFlexibleLinesHarness.cjs</c>'s <c>displacedClaimFold</c> section.</para>
/// </summary>
[TestFixture]
public class DisplacedClaimFoldLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("displacedClaimFold");

    private static JsonElement Run(string scenario) => Section().GetProperty("runs").GetProperty(scenario);

    /// <summary>The two corrected runs, plus the clean run they are read against.</summary>
    private static readonly string[] scenarios = ["reportedShape", "threeBreakChain", "cleanRun"];

    /// <summary>The two that break twice or more, which are the ones the era arm has an opinion about.</summary>
    private static readonly string[] corrected = ["reportedShape", "threeBreakChain"];

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    /// <summary>
    /// THE REPORTED SHAPE'S FIXTURE, the game's own <c>reportMap</c> in the window the browser's
    /// loader derives: a run of cells, then a two-letter word with a gap after it, which is the
    /// "... go to ..." the report broke on.
    ///
    /// <para>"abcde fg hi" on [1000, 4900), sung to 1900. Eleven cells (index: char = target) are
    /// 0:a = 1000, 1:b = 1100, 2:c = 1200, 3:d = 1300, 4:e = 1400, 5:' ' = 1500, 6:f = 1500,
    /// 7:g = 1600, 8:' ' = 1700, 9:h = 1700, 10:i = 1800. No seal grace, so nothing seals mid
    /// script.</para>
    /// </summary>
    private static LyricLine[] Lines() =>
    [
        new LyricLine
        {
            RawText = "abcde fg hi",
            StartTime = 1000,
            EndTime = 4900,
            SingEndTime = 1900,
            Units =
            [
                Unit("abcde", 1000, 1500),
                Unit("fg", 1500, 1700),
                Unit("hi", 1700, 1900),
            ],
        },
    ];

    /// <summary>The map as the ENGINE arm wants it.</summary>
    private static LyricBeatmap Map() => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata
        {
            Artist = "a",
            Title = "t",
            FolderPath = @"X:\nowhere",
            AudioFileName = "a.mp3",
        },
        Lines = Lines(),
        Granularity = TimingGranularity.Line,
    };

    /// <summary>The same map as the SUBMITTED-ACCOUNT arm wants it: a playable beatmap with the
    /// nested per-cell objects the score processor's maximum statistics come from.</summary>
    private static TypeBeatBeatmap Playable()
    {
        var map = new TypeBeatBeatmap();
        var lines = Lines();

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
    /// era flags the C# defaults OFF for replay decoding have to be set by hand.
    /// <see cref="TypingEngine.FoldsDisplacedClaim"/> is the one this file is about, and it is the
    /// reason a bare engine is not the live client: it defaults false so that every stored replay
    /// re-derives with the displaced claim discarded, which is the max combo its player was given.
    /// </summary>
    private static TypingEngine LiveEngine(bool foldsDisplacedClaim = true) => new TypingEngine(Map())
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
        FoldsDisplacedClaim = foldsDisplacedClaim,
    };

    /// <summary>
    /// The script as a replay, headed by the CONFIG frame the era bits travel in. Bit 12 is this
    /// file's subject and is a parameter rather than a constant, because clearing it is how
    /// <see cref="TheseScriptsReallySeparateTheTwoEras"/> proves the fixtures are about the rule at
    /// all.
    /// </summary>
    private static Replay Replay(bool foldsDisplacedClaim, string scenario)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: foldsDisplacedClaim));

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

    private static TypeBeatReplayAccount Play(string scenario, bool foldsDisplacedClaim = true)
        => TypeBeatReplayScorer.Score(Playable(), Array.Empty<Mod>(), Replay(foldsDisplacedClaim, scenario), TypoRule.Deferred, ComboRestoreRule.OnFix);

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
    /// <see cref="LosslessSkipReclaimLiveParityTest"/>'s filter, and for the same reason.
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

    private static int[] Restores(string scenario)
        => Run(scenario).GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray();

    #endregion

    /// <summary>
    /// THE FIXTURE BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both
    /// sides build their cells through their own loader (the browser's <c>buildBeatmap</c>, the
    /// game's <c>TypingLine.FromLyricLine</c>), so a fixture that drifted would show up below as an
    /// engine divergence and be blamed on the fold.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheFixture()
    {
        Assert.Multiple(() =>
        {
            var engine = LiveEngine();
            var lines = Section().GetProperty("fixture").GetProperty("lines");

            Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), "line count");

            for (int i = 0; i < engine.Lines.Count; i++)
            {
                var line = engine.Lines[i];
                var browserLine = lines[i];

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
    /// Each script replayed through the game's engine and compared to the browser's readings step for
    /// step: where the caret is, how far ahead of the playhead it sits, every cell's state, and the
    /// whole combo account.
    ///
    /// <para>The reading that matters is the one on the space that redeems the chain. Its
    /// <c>combo</c> must be 9 on the reported shape, which is exactly the nine cells a CLEAN run holds
    /// on that gap, rather than the 3 the discarded claim leaves.</para>
    ///
    /// <para><c>runPositions</c> is asserted by LENGTH rather than by content, because the C# list is
    /// private: what is worth pinning is the invariant the fold rests on,
    /// <c>runPositions.length == combo</c>, which is what makes the folded streak and the folded
    /// positions provably the same size, and therefore what makes a later back-dated seal able to take
    /// the right increments back.</para>
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOfEveryScript()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var engine = LiveEngine();
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
                    Assert.That(engine.CharsAheadOfPlayhead(t), Is.EqualTo(reading.GetProperty("charsAheadOfPlayhead").GetInt32()), $"{where}: the caret's lead");
                    Assert.That(engine.Combo, Is.EqualTo(reading.GetProperty("combo").GetInt32()), $"{where}: combo");
                    Assert.That(engine.MaxCombo, Is.EqualTo(reading.GetProperty("maxCombo").GetInt32()), $"{where}: max combo");
                    Assert.That(breaks, Is.EqualTo(reading.GetProperty("comboBreaks").GetInt32()), $"{where}: combo breaks");
                    Assert.That(restores, Is.EqualTo(reading.GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray()), $"{where}: combo restores");
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
    /// with no tolerance on the doubles. This is the arm the engine readings cannot stand in for: the
    /// increments the fold puts back are combo, and combo weights every judgement portion after the
    /// redemption, so a browser that agreed on the engine's combo and mirrored it late into the
    /// processor would still land a different total on the shared leaderboards.
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
    /// THE LAW ITSELF, stated as a comparison rather than as a literal: accidents fully corrected cost
    /// the run NOTHING, so both corrected scripts must reach the CLEAN run's max combo. Read off both
    /// clients, because the whole point of the change is that neither of them may be short.
    ///
    /// <para>The reported shape's redemption is the number the report is about: ONE restore worth 7,
    /// the six the first break took plus the one the second did, where the discarding arm restores the
    /// 1 the player was given. The chain's is one restore worth 4, which is 2 + 1 + 1 through three
    /// breaks.</para>
    /// </summary>
    [Test]
    public void BothCorrectedRunsReachTheCleanRunsCombo()
    {
        var clean = Play("cleanRun");

        Assert.Multiple(() =>
        {
            Assert.That(clean.MaxCombo, Is.EqualTo(11), "eleven cells, eleven increments");
            Assert.That(Run("cleanRun").GetProperty("submitted").GetProperty("maxCombo").GetInt32(), Is.EqualTo(11));

            foreach (string scenario in corrected)
            {
                var game = Play(scenario);

                Assert.That(game.MaxCombo, Is.EqualTo(clean.MaxCombo), $"{scenario}: the accidents cost the corrected run nothing at all");
                Assert.That(Run(scenario).GetProperty("maxCombo").GetInt32(), Is.EqualTo(11), $"{scenario}: and the browser agrees");
                Assert.That(Run(scenario).GetProperty("combo").GetInt32(), Is.EqualTo(11), $"{scenario}: the run is still standing at the end");
                Assert.That(game.Completion, Is.EqualTo(1.0), $"{scenario}: completion");
                Assert.That(Wire(game.Statistics).GetValueOrDefault("miss"), Is.Zero, $"{scenario}: every cell was typed in the end");
            }

            // THE REPORT'S OWN NUMBER: one redemption worth the whole chain, and the run standing at
            // the clean run's nine on the gap that redeemed it.
            Assert.That(Restores("reportedShape"), Is.EqualTo(new[] { 7 }), "the run of 6 the first break took, plus the 1 the second one did");
            Assert.That(Restores("threeBreakChain"), Is.EqualTo(new[] { 4 }), "one redemption, worth 2 + 1 + 1");

            var readings = Run("reportedShape").GetProperty("readings");
            int redemption = RedemptionStep("reportedShape");

            Assert.That(readings[redemption].GetProperty("combo").GetInt32(), Is.EqualTo(9),
                "the nine cells a clean run holds on that gap");
        });
    }

    /// <summary>
    /// NON-VACUITY, and the only assertions here that do not compare the two clients: the same replays
    /// re-derived with CONFIG bit 12 CLEAR, which is what every stored score was played under. If the
    /// browser's numbers matched THAT too, the tests above would be pinning a rule neither script
    /// reaches.
    ///
    /// <para>Both corrected scripts must part from the classic arm, and part in the direction the rule
    /// promises: the folded run is never shorter and never worth less, because the rule can only ever
    /// put back increments the old one dropped. It moves nothing else, which is why the judgements,
    /// the accuracy and the completion are asserted EQUAL across the two arms, and why the clean run
    /// (which breaks nothing) is bit-identical under both.</para>
    ///
    /// <para>The literals are the report in miniature: the reported shape's stored arm redeems 1 and
    /// tops out at 6, the run the FIRST break was holding and never reached again, against the live
    /// arm's 11.</para>
    /// </summary>
    [Test]
    public void TheseScriptsReallySeparateTheTwoEras()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in corrected)
            {
                var live = Play(scenario);
                var stored = Play(scenario, foldsDisplacedClaim: false);

                Assert.That(stored.MaxCombo, Is.LessThan(live.MaxCombo), $"{scenario}: the discarded claim should cost max_combo");
                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore), $"{scenario}: and total_score");
                Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics), $"{scenario}: the era must move no judgement");
                Assert.That(live.Accuracy, Is.EqualTo(stored.Accuracy), $"{scenario}: the era must move no accuracy");
                Assert.That(live.Completion, Is.EqualTo(stored.Completion), $"{scenario}: the era must move no completion");
            }

            Assert.That(Play("reportedShape", foldsDisplacedClaim: false).MaxCombo, Is.EqualTo(6),
                "the reported shape's stored arm tops out at the run the first break was holding");
            Assert.That(Play("threeBreakChain", foldsDisplacedClaim: false).MaxCombo, Is.EqualTo(8),
                "the chain's stored arm ends three lower, which is the two claims it dropped");

            var cleanLive = Play("cleanRun");
            var cleanStored = Play("cleanRun", foldsDisplacedClaim: false);

            Assert.That(cleanStored.MaxCombo, Is.EqualTo(cleanLive.MaxCombo), "a run that breaks nothing re-derives identically");
            Assert.That(cleanStored.TotalScore, Is.EqualTo(cleanLive.TotalScore));
        });
    }

    /// <summary>
    /// And the other half of non-vacuity, read off the BROWSER's own run rather than off either
    /// engine, because a comparison of two clients that both did nothing interesting passes. The
    /// scripts have to reach the shape the rule is about, which is a DISPLACING break: one that owns a
    /// streak of its own (so it is not passive under backlog 243) landing while a claim is already
    /// outstanding.
    ///
    /// <list type="bullet">
    /// <item>The reported shape breaks TWICE with exactly one earned character between, so the second
    /// break stands on a streak of 1 that is progress rather than its own press, and there is exactly
    /// ONE redemption for the two of them.</item>
    /// <item>The chain breaks THREE times the same way, and still redeems once: that is what
    /// "transitively" means, and a fold that only reached one level deep would show up here as two
    /// restores or as a smaller one.</item>
    /// <item>Neither script ever rushes. The rush cap is a different rule with a different remedy, and
    /// a script that tripped it would be measuring that one instead, so every lead reading is at or
    /// below zero.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheScriptsReallyReachADisplacingBreak()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Run("reportedShape").GetProperty("comboBreaks").GetInt32(), Is.EqualTo(2), "two accidents");
            Assert.That(Run("threeBreakChain").GetProperty("comboBreaks").GetInt32(), Is.EqualTo(3), "three accidents");
            Assert.That(Run("cleanRun").GetProperty("comboBreaks").GetInt32(), Is.Zero, "and the reference breaks nothing");

            foreach (string scenario in corrected)
            {
                var readings = Run(scenario).GetProperty("readings");

                Assert.That(Restores(scenario), Has.Length.EqualTo(1), $"{scenario}: one redemption for the whole chain");
                Assert.That(Restores(scenario)[0], Is.GreaterThan(1),
                    $"{scenario}: the redemption is worth more than the break that took the claim, which is the fold");

                // The break that DISPLACES: the last combo break in the script must land while the
                // combo stands at more than zero (a streak of its own) with a claim already
                // outstanding, which is exactly the arm backlog 262 rewrote. Read as "the step before
                // the last break had combo > 0", since a passive break stands on nothing.
                int lastBreak = -1;

                for (int i = 1; i < readings.GetArrayLength(); i++)
                {
                    if (readings[i].GetProperty("comboBreaks").GetInt32() > readings[i - 1].GetProperty("comboBreaks").GetInt32())
                        lastBreak = i;
                }

                Assert.That(lastBreak, Is.GreaterThan(1), $"{scenario}: no break in this script at all");
                Assert.That(readings[lastBreak - 1].GetProperty("combo").GetInt32(), Is.GreaterThan(0),
                    $"{scenario}: the displacing break has to own a streak, or backlog 243 makes it passive and this is a different rule");

                foreach (var reading in readings.EnumerateArray())
                {
                    Assert.That(reading.GetProperty("charsAheadOfPlayhead").GetInt32(), Is.LessThanOrEqualTo(0),
                        $"{scenario}: these scripts must never rush, or they are measuring the cap rather than the fold");
                }
            }
        });
    }

    /// <summary>The index of the step whose reading first shows a restore, which is the redemption.</summary>
    private static int RedemptionStep(string scenario)
    {
        var readings = Run(scenario).GetProperty("readings");

        for (int i = 0; i < readings.GetArrayLength(); i++)
        {
            if (readings[i].GetProperty("comboRestores").GetArrayLength() > 0) return i;
        }

        throw new InvalidOperationException($"{scenario} never redeemed its claim");
    }
}
