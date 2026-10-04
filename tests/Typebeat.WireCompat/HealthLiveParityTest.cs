using System.Text.Json;
using osu.Framework.Utils;
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
using typebeat.Game.Scoring;
using Typebeat.Tools.ScoreRecalc;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the HP POOL and the fail it decides (backlog 306): the browser's
/// <c>HealthAccount</c> in <c>typebeat-core.js</c> against the desktop's real
/// <see cref="TypeBeatHealthProcessor"/>, fed through <see cref="TypeBeatHealthFeed"/>, over the same
/// keystrokes, with no tolerance.
///
/// <para>Until this landed the browser had no account at all: health was a read of the rejection
/// streak, the only fail was the 13-rejection branch /play can no longer reach, and a near-AFK or
/// typo-drowned run played out and submitted passed=true where the desktop fails the identical input
/// (rank F, unranked, pp 0). The two clients submit to the SAME leaderboards, so that was a free No
/// Fail on one of them.</para>
///
/// <para>THE ORACLE IS NEW. <see cref="TypeBeatReplayScorer"/> re-derives no health, and the fuzz
/// sweep's generator deliberately stayed clear of the fail, so neither could say when a run dies. The
/// C# arm here is <see cref="LiveHealthArm"/>: the replay scorer's live assembly with the health
/// processor added, the result-less health seams coming from the very
/// <see cref="TypeBeatHealthFeed.Attach"/> call the live playfield makes. For every run that survives,
/// <see cref="TheArmIsTheReplayScorerWhereARunSurvives"/> holds its account against the replay scorer,
/// so the score half of the arm is the scorer rather than a second copy of it.</para>
///
/// <para>THREE THINGS are compared: the bar after every step, the step the run fails on, and what the
/// failed run submits. A failed run is concluded at the end of the step that failed it, which is the
/// Player's scheduled <c>ConcludeFailedScore</c>, and both sides stop feeding there. The step runs to
/// its end, but the SCORE stops at the result that emptied the bar: the Player hands each result to
/// health first, health stamps it <c>FailedAtJudgement</c>, and the score processor drops every result
/// so stamped, which is why the AFK script banks 45 misses and not the 57 its two seals judge.</para>
///
/// <para>The scripts, and the browser's readings of them, come out of <c>CoreHealthHarness.cjs</c>.
/// Each is named for the rule it reaches, and <see cref="EachScriptReachesWhatItIsNamedFor"/> reads
/// that off the runs themselves, so a fixture that drifted into not exercising its rule fails there
/// rather than passing the comparisons vacuously.</para>
/// </summary>
[TestFixture]
public class HealthLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreHealthHarness.cjs"));

    private static readonly string[] scenarios =
    [
        "afkDeathOnTheSecondSeal",
        "typoEmptiesTheBarMidLine",
        "parkedGapErase",
        "stepBackReclaim",
        "abandonReclaimed",
        "abandonSealed",
        "cappedFixRecoversAtOk",
        "refundIntoAFullBar",
        "rejectionsEmptyTheBarAndTheSealFailsIt",
        "mashFailsOnTheThirteenth",
    ];

    private static JsonElement Scenario(string name) => harness.Value.GetProperty("scenarios").GetProperty(name);

    private static JsonElement BrowserRun(string name) => Scenario(name).GetProperty("run");

    #region The fixtures, built from the numbers the browser's loader used

    /// <summary>
    /// The scenario's lines in the game's terms, from the words the harness wrote and the windows the
    /// browser's loader derived from them, so both sides hold the same map rather than two nearly
    /// equal ones. <see cref="TheTwoLoadersAgreeOnEveryFixture"/> then holds the CELLS each loader
    /// made against each other before any reading is trusted.
    /// </summary>
    private static LyricLine[] Lines(string name)
    {
        var lines = new List<LyricLine>();

        foreach (var line in Scenario(name).GetProperty("fixture").EnumerateArray())
        {
            var units = line.GetProperty("words").EnumerateArray()
                            .Select(w => new TimedUnit { Text = w.GetProperty("text").GetString()!, StartTime = w.GetProperty("start").GetDouble(), EndTime = w.GetProperty("end").GetDouble() })
                            .ToArray();

            lines.Add(new LyricLine
            {
                RawText = line.GetProperty("text").GetString()!,
                StartTime = line.GetProperty("startTime").GetDouble(),
                EndTime = line.GetProperty("endTime").GetDouble(),
                SingEndTime = line.GetProperty("singEndTime").GetDouble(),
                Units = units,
            });
        }

        return lines.ToArray();
    }

    private static LyricBeatmap Map(string name) => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
        Lines = Lines(name),
        Granularity = TimingGranularity.Word,
    };

    private static TypeBeatBeatmap Playable(string name)
    {
        var map = new TypeBeatBeatmap();
        var lines = Lines(name);

        for (int i = 0; i < lines.Length; i++)
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Song";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    private static bool SpaceSkipsWord(string name) => Scenario(name).GetProperty("spaceSkipsWord").GetBoolean();

    /// <summary>
    /// A started engine under every LIVE rule, the only arm the browser can be compared against (see
    /// <see cref="SealComboBreakLiveParityTest"/> for why each era flag has to be set by hand), and the
    /// scenario's own space-skip arm: /play is permanently on, and the rejection scripts turn it off
    /// because that is the one arm where a key can still be rejected at all.
    /// </summary>
    internal static TypingEngine LiveEngine(LyricBeatmap map, bool spaceSkipsWord) => new TypingEngine(map)
    {
        SyllableTiming = true,
        CharTimedStretch = true,
        FirstCharTiming = true,
        WrongInputOnWordGaps = true,
        StrictSpaces = true,
        SpaceSkipsWord = spaceSkipsWord,
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
        // EARLY FINISH (bit 4 of the second CONFIG flags word, the 0x01 CONFIG_EXTENDED carrier):
        // the final line seals the moment it is fully typed, so a bare live engine needs it set.
        EarlyFinish = true,
        ManualNewlines = true,
        NewlineOnTypedLetter = true,
    };

    /// <summary>The game's counts as the wire spells them, minus the line containers' ignore_hit.</summary>
    internal static Dictionary<string, int> Wire(IReadOnlyDictionary<HitResult, int> counts)
        => WireCounts.From(counts.Where(entry => entry.Key != TypeBeatResultMapping.LINE_RESULT).ToDictionary(entry => entry.Key, entry => entry.Value));

    internal static Dictionary<string, int> Dict(JsonElement element, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in element.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    #endregion

    #region The game's run

    private sealed record Reading(string Op, double Health, bool Failed, bool? Handled, int ActiveLine, int Caret, int NextUnsealed, int WrongStreak);

    private sealed record GameRun(List<Reading> Readings, int FailStep, ScoreInfo Account, bool Finished);

    private static GameRun Play(string name)
    {
        var engine = LiveEngine(Map(name), SpaceSkipsWord(name));
        using var arm = new LiveHealthArm(Playable(name), engine);

        var readings = new List<Reading>();
        int failStep = -1;
        var script = Scenario(name).GetProperty("script");

        for (int i = 0; i < script.GetArrayLength(); i++)
        {
            var step = script[i];
            double t = step.GetProperty("t").GetDouble();
            string op = step.GetProperty("op").GetString()!;
            bool? handled = null;

            switch (op)
            {
                case "update":
                    engine.Update(t);
                    break;

                case "backspace":
                    handled = engine.ProcessBackspace();
                    break;

                case "key":
                    handled = engine.ProcessKey(step.GetProperty("c").GetString()![0], t);
                    break;

                default:
                    throw new ArgumentException($"the harness emitted an op this side does not have: {op}");
            }

            readings.Add(new Reading(op, arm.Health.Health.Value, arm.Health.HasFailed, handled, engine.ActiveLineIndex, engine.CaretIndex,
                engine.NextUnsealedLineIndex, engine.ConsecutiveWrongKeys));

            if (arm.Health.HasFailed)
            {
                failStep = i;
                break;
            }
        }

        return new GameRun(readings, failStep, arm.Conclude(), engine.IsFinished);
    }

    #endregion

    /// <summary>The fixtures before the accounts: both loaders made the same cells and windows.</summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryFixture()
    {
        Assert.Multiple(() =>
        {
            foreach (string name in scenarios)
            {
                var engine = LiveEngine(Map(name), SpaceSkipsWord(name));
                var browserLines = Scenario(name).GetProperty("fixture");

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), $"{name}: line count");

                for (int l = 0; l < engine.Lines.Count; l++)
                {
                    var line = engine.Lines[l];
                    var browserLine = browserLines[l];

                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{name}[{l}]: activationTime");
                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{name}[{l}]: endTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{name}[{l}]: sealGraceMs");

                    var cells = browserLine.GetProperty("cells");
                    Assert.That(cells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{name}[{l}]: cell count");

                    for (int c = 0; c < Math.Min(cells.GetArrayLength(), line.Cells.Count); c++)
                    {
                        Assert.That(cells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"{name}[{l}][{c}]: expected");
                        Assert.That(cells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"{name}[{l}][{c}]: target");
                        Assert.That(cells[c].GetProperty("typeable").GetBoolean(), Is.EqualTo(line.Cells[c].IsTypeable), $"{name}[{l}][{c}]: typeable");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Every number the account runs on is the desktop's own: the four HP deltas, the mash drain and
    /// its threshold, and the empty test's epsilon, which is osu-framework's
    /// <see cref="Precision.DOUBLE_EPSILON"/> (a retyped 1e-7 would still pass the scripts below).
    /// The empty test itself is then held against <see cref="Precision.AlmostBigger(double, double, double)"/>
    /// on both sides of its edge.
    /// </summary>
    [Test]
    public void TheAccountRunsOnTheDesktopsOwnNumbers()
    {
        var constants = harness.Value.GetProperty("constants");

        Assert.Multiple(() =>
        {
            Assert.That(constants.GetProperty("WRONG_KEY_FAIL_STREAK").GetInt32(), Is.EqualTo(TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK));
            Assert.That(constants.GetProperty("GREAT_HEALTH_INCREASE").GetDouble(), Is.EqualTo(TypeBeatHealthProcessor.GREAT_HEALTH_INCREASE));
            Assert.That(constants.GetProperty("OK_HEALTH_INCREASE").GetDouble(), Is.EqualTo(TypeBeatHealthProcessor.OK_HEALTH_INCREASE));
            Assert.That(constants.GetProperty("MEH_HEALTH_INCREASE").GetDouble(), Is.EqualTo(TypeBeatHealthProcessor.MEH_HEALTH_INCREASE));
            Assert.That(constants.GetProperty("MISS_HEALTH_DRAIN").GetDouble(), Is.EqualTo(TypeBeatHealthProcessor.MISS_HEALTH_DRAIN));
            Assert.That(constants.GetProperty("WRONG_KEY_HP_DRAIN").GetDouble(), Is.EqualTo(TypeBeatHealthProcessor.WRONG_KEY_HP_DRAIN));
            Assert.That(constants.GetProperty("HEALTH_EPSILON").GetDouble(), Is.EqualTo(Precision.DOUBLE_EPSILON));

            foreach (var probe in harness.Value.GetProperty("almostBigger").EnumerateArray())
            {
                double value = probe.GetProperty("value").GetDouble();
                Assert.That(probe.GetProperty("empty").GetBoolean(), Is.EqualTo(Precision.AlmostBigger(0.0, value)), $"empty test at {value:R}");
            }
        });
    }

    /// <summary>
    /// The bar, step for step and to the last bit, and the step the run fails on. The engine state
    /// beside it (caret, active line, how far the seal has got, the mash streak) is read too, so a
    /// divergence in the ENGINE is reported as one rather than surfacing as a mysterious HP delta.
    /// </summary>
    [Test]
    public void EveryScriptDrainsTheSameBarAndFailsOnTheSameStep()
    {
        Assert.Multiple(() =>
        {
            foreach (string name in scenarios)
            {
                var game = Play(name);
                var browser = BrowserRun(name);
                var readings = browser.GetProperty("readings");

                Assert.That(browser.GetProperty("failStep").GetInt32(), Is.EqualTo(game.FailStep), $"{name}: the step the run fails on");
                Assert.That(readings.GetArrayLength(), Is.EqualTo(game.Readings.Count), $"{name}: steps played");

                for (int i = 0; i < Math.Min(readings.GetArrayLength(), game.Readings.Count); i++)
                {
                    var js = readings[i];
                    var cs = game.Readings[i];
                    string where = $"{name}[{i}] {cs.Op} {js.GetProperty("t").GetDouble()}";

                    Assert.That(js.GetProperty("health").GetDouble(), Is.EqualTo(cs.Health), $"{where}: health");
                    Assert.That(js.GetProperty("failed").GetBoolean(), Is.EqualTo(cs.Failed), $"{where}: failed");
                    Assert.That(js.GetProperty("activeLineIndex").GetInt32(), Is.EqualTo(cs.ActiveLine), $"{where}: active line");
                    Assert.That(js.GetProperty("caretIndex").GetInt32(), Is.EqualTo(cs.Caret), $"{where}: caret");
                    Assert.That(js.GetProperty("nextUnsealedLineIndex").GetInt32(), Is.EqualTo(cs.NextUnsealed), $"{where}: the seal's position");
                    Assert.That(js.GetProperty("consecutiveWrongKeys").GetInt32(), Is.EqualTo(cs.WrongStreak), $"{where}: mash streak");

                    if (cs.Handled is bool handled)
                        Assert.That(js.GetProperty("handled").GetBoolean(), Is.EqualTo(handled), $"{where}: handled");
                }
            }
        });
    }

    /// <summary>
    /// What the run SUBMITS, field for field: a failed run is passed=false and rank F with the
    /// statistics it had banked when the failing step ended, which is what the server stores unranked
    /// with no pp. <c>accuracy</c> is compared for a surviving run only: the two clients put different
    /// numbers in that field for a failed one (the desktop its judged-only accuracy, the browser its
    /// whole-map one) and the server stores neither, recomputing its own from the two statistics
    /// dictionaries, which ARE compared.
    /// </summary>
    [Test]
    public void EveryRunSubmitsTheSameAccount()
    {
        Assert.Multiple(() =>
        {
            foreach (string name in scenarios)
            {
                var game = Play(name);
                var submitted = BrowserRun(name).GetProperty("submitted");
                bool failed = game.FailStep >= 0;

                Assert.That(submitted.GetProperty("passed").GetBoolean(), Is.EqualTo(!failed), $"{name}: passed");
                Assert.That(game.Account.Passed, Is.EqualTo(!failed), $"{name}: the game's own passed flag");
                Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Account.Statistics)), $"{name}: statistics");
                Assert.That(Dict(submitted, "maximumStatistics"), Is.EquivalentTo(Wire(game.Account.MaximumStatistics)), $"{name}: maximum_statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.Account.MaxCombo), $"{name}: max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.Account.TotalScore), $"{name}: total_score");
                Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(TypeBeatScoreProcessor.ComputeCompletion(game.Account)), $"{name}: completion");

                var rank = failed ? ScoreRank.F : TypeBeatScoreProcessor.RankFromCompletion(TypeBeatScoreProcessor.ComputeCompletion(game.Account));
                Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(rank.ToString()), $"{name}: rank");

                if (failed)
                    Assert.That(game.Account.Rank, Is.EqualTo(ScoreRank.F), $"{name}: FailScore ranks the run F");
                else
                    Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Account.Accuracy), $"{name}: accuracy");
            }
        });
    }

    /// <summary>
    /// THE FREEZE. The desktop stops at the step that failed (the Player concludes); the browser's
    /// engine keeps being ticked until typebeat-player.js notices, and a key can land in between. So
    /// the harness feeds every remaining step of a failed script to the frozen engine anyway, and
    /// neither the bar nor a single field of the account may move: the desktop's account, compared
    /// above, is the one those steps never reached.
    /// </summary>
    [Test]
    public void AFailedRunIsFrozenWhateverArrivesAfterIt()
    {
        int fed = 0;

        Assert.Multiple(() =>
        {
            foreach (string name in scenarios)
            {
                var run = BrowserRun(name);

                if (run.GetProperty("failStep").GetInt32() < 0)
                    continue;

                var after = run.GetProperty("afterFail");
                var readings = run.GetProperty("readings");
                fed += after.GetProperty("stepsFed").GetInt32();

                Assert.That(after.GetProperty("health").GetDouble(), Is.EqualTo(readings[readings.GetArrayLength() - 1].GetProperty("health").GetDouble()), $"{name}: the bar moved after the fail");
                Assert.That(after.GetProperty("submitted").GetRawText(), Is.EqualTo(run.GetProperty("submitted").GetRawText()), $"{name}: the account moved after the fail");

                // And the engine does not advance: the account alone cannot see that, because a result
                // landing after the fail is dropped from the score anyway (FailedAtJudgement).
                var last = readings[readings.GetArrayLength() - 1];
                Assert.That(after.GetProperty("nextUnsealedLineIndex").GetInt32(), Is.EqualTo(last.GetProperty("nextUnsealedLineIndex").GetInt32()), $"{name}: a line sealed after the fail");
                Assert.That(after.GetProperty("activeLineIndex").GetInt32(), Is.EqualTo(last.GetProperty("activeLineIndex").GetInt32()), $"{name}: the caret changed line after the fail");
                Assert.That(after.GetProperty("caretIndex").GetInt32(), Is.EqualTo(last.GetProperty("caretIndex").GetInt32()), $"{name}: the caret moved after the fail");
                Assert.That(after.GetProperty("finished").GetBoolean(), Is.EqualTo(run.GetProperty("finished").GetBoolean()), $"{name}: the run finished after it failed");
            }
        });

        Assert.That(fed, Is.GreaterThan(0), "no failed script had any step left to feed the frozen engine");
    }

    /// <summary>
    /// The arm's SCORE half is not a second copy of the replay scorer's: where a run survives, the
    /// same keystrokes through <see cref="TypeBeatReplayScorer"/> (display cadence, CONFIG frame and
    /// all) submit the identical account. So the failed-run comparisons above rest on the same score
    /// wiring every other parity test in this project trusts.
    /// </summary>
    [Test]
    public void TheArmIsTheReplayScorerWhereARunSurvives()
    {
        int survivors = 0;

        Assert.Multiple(() =>
        {
            foreach (string name in scenarios)
            {
                var game = Play(name);

                if (game.FailStep >= 0)
                    continue;

                survivors++;

                var scored = TypeBeatReplayScorer.Score(Playable(name), Array.Empty<Mod>(), Replay(name), TypoRule.Deferred, ComboRestoreRule.OnFix);

                Assert.That(scored.UnconsumedFrames, Is.Zero, $"{name}: unconsumed frames");
                Assert.That(Wire(game.Account.Statistics), Is.EquivalentTo(Wire(scored.Statistics)), $"{name}: statistics");
                Assert.That(game.Account.MaxCombo, Is.EqualTo(scored.MaxCombo), $"{name}: max_combo");
                Assert.That(game.Account.TotalScore, Is.EqualTo(scored.TotalScore), $"{name}: total_score");
                Assert.That(game.Account.Accuracy, Is.EqualTo(scored.Accuracy), $"{name}: accuracy");
            }
        });

        Assert.That(survivors, Is.GreaterThanOrEqualTo(5), "the surviving scripts are what this cross-check stands on");
    }

    private static Replay Replay(string name)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: SpaceSkipsWord(name), syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true, manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true, earlyFinish: true));

        foreach (var step in Scenario(name).GetProperty("script").EnumerateArray())
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
            }
        }

        return replay;
    }

    /// <summary>
    /// NON-VACUITY, read off the game's own run of each script: every fixture reaches the rule it is
    /// named for, so the comparisons above cannot pass on scripts that never exercised it.
    /// </summary>
    [Test]
    public void EachScriptReachesWhatItIsNamedFor()
    {
        const double miss = TypeBeatHealthProcessor.MISS_HEALTH_DRAIN;
        const double ulp = 1e-12;

        Assert.Multiple(() =>
        {
            // AFK: no key at all, the first seal survives, the SECOND fails, on its 15th Miss: the seal
            // runs to its end (27 cells judged) but the score banks only up to the result that emptied
            // the bar, 30 + 15 = 45, because every result after it arrives stamped FailedAtJudgement.
            var afk = Play("afkDeathOnTheSecondSeal");
            var afkFail = afk.Readings[afk.FailStep];
            Assert.That(afk.Readings.All(r => r.Op == "update"), "the AFK script pressed a key");
            Assert.That(afk.Readings[afk.FailStep - 1].NextUnsealed, Is.EqualTo(1), "afk: the first seal had landed, alive");
            Assert.That(afkFail.NextUnsealed, Is.EqualTo(2), "afk: the failing step is the second seal");
            Assert.That(afk.Account.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(45), "afk: the score should stop at the Miss that emptied the bar");

            // TYPO: the bar empties on a KEY, on line 1, which has not sealed.
            var typo = Play("typoEmptiesTheBarMidLine");
            var typoFail = typo.Readings[typo.FailStep];
            Assert.That(typoFail.Op, Is.EqualTo("key"), "typo: the run should fail on a keypress");
            Assert.That(typoFail.NextUnsealed, Is.EqualTo(1), "typo: line 1 should still be unsealed when it fails");
            Assert.That(typo.Readings[typo.FailStep - 2].Health, Is.GreaterThan(0), "typo: alive before the failing key");

            // PARKED GAP: two drains (the overwrite drains again) and ONE refund at the in-place clear.
            var parked = Play("parkedGapErase").Readings;
            int clear = parked.FindIndex(r => r.Op == "backspace");
            Assert.That(parked[clear - 2].Health - parked[clear - 1].Health, Is.EqualTo(miss).Within(ulp), "parked: the overwrite drains");
            Assert.That(parked[clear - 3].Health - parked[clear - 2].Health, Is.EqualTo(miss).Within(ulp), "parked: the first typo drains");
            Assert.That(parked[clear].Health - parked[clear - 1].Health, Is.EqualTo(miss).Within(ulp), "parked: the clear refunds one drain");
            Assert.That(parked[clear].Caret, Is.EqualTo(parked[clear - 1].Caret), "parked: the clear is in place");

            // STEP BACK: the backspace at the head of line 1 walks into line 0 and refunds the skip.
            var stepBack = Play("stepBackReclaim").Readings;
            int back = stepBack.FindIndex(r => r.Op == "backspace");
            Assert.That(stepBack[back - 1].ActiveLine, Is.EqualTo(1), "step back: the caret was on line 1");
            Assert.That(stepBack[back].ActiveLine, Is.EqualTo(0), "step back: the backspace walked back into line 0");
            Assert.That(stepBack[back].Health - stepBack[back - 1].Health, Is.EqualTo(miss).Within(ulp), "step back: the reclaimed cell's drain is refunded");

            // RECLAIM: the backspace that re-opens the two skipped cells refunds both.
            var reclaimed = Play("abandonReclaimed").Readings;
            Assert.That(Enumerable.Range(1, reclaimed.Count - 1).Any(i => reclaimed[i].Op == "backspace"
                && Math.Abs(reclaimed[i].Health - reclaimed[i - 1].Health - 2 * miss) < ulp), "reclaim: no backspace refunded the two skipped cells");

            // SEAL: the refund and the two Misses net to one charge, so the seal leaves the bar where
            // it found it (the bar is well off full, so no clamp hides a missing refund).
            var sealedRun = Play("abandonSealed").Readings;
            int seal = sealedRun.FindIndex(r => r.NextUnsealed == 2);
            Assert.That(sealedRun[seal - 1].Health, Is.LessThan(1 - 2 * miss), "sealed: the bar has to be off full for the netting to show");
            Assert.That(sealedRun[seal].Health, Is.EqualTo(sealedRun[seal - 1].Health).Within(ulp), "sealed: the refund and the misses should net to zero at the seal");

            // CAPPED FIX: the retype after the erase recovers at Ok, not Great.
            var capped = Play("cappedFixRecoversAtOk").Readings;
            int erase = capped.FindIndex(r => r.Op == "backspace");
            Assert.That(capped[erase + 1].Health - capped[erase].Health, Is.EqualTo(TypeBeatHealthProcessor.OK_HEALTH_INCREASE).Within(ulp), "capped: the fix should recover OK_HEALTH_INCREASE");

            // FULL BAR: a drain happened, the bar was back at 1, and the typo's refund left it at 1.
            var full = Play("refundIntoAFullBar").Readings;
            int refund = full.FindIndex(r => r.Op == "backspace") + 1;
            Assert.That(full.Any(r => r.Health < 1), "full: nothing ever drained");
            Assert.That(full[refund - 1].Health, Is.EqualTo(1), "full: the bar was full before the refund");
            Assert.That(full[refund].Health, Is.EqualTo(1), "full: the refund banked credit past full");
            Assert.That(full[refund].Caret, Is.EqualTo(0), "full: the second backspace should have erased the typo");

            // REJECTIONS: the bar reached 0 on a rejection WITHOUT failing, and the failing step is the
            // seal of line 1, which missed nothing (its only result the inert line container).
            var reject = Play("rejectionsEmptyTheBarAndTheSealFailsIt");
            Assert.That(reject.Readings.Any(r => r.Op == "key" && r.Health == 0 && !r.Failed && r.WrongStreak < TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK),
                "rejections: the bar never sat at 0 alive");
            Assert.That(reject.Readings[reject.FailStep].Op, Is.EqualTo("update"), "rejections: the fail should come at a seal");
            Assert.That(reject.Readings[reject.FailStep].NextUnsealed, Is.EqualTo(2), "rejections: the failing seal is line 1's");
            Assert.That(reject.Account.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(30), "rejections: line 1 should have missed nothing");

            // MASH: the thirteenth rejected space fails, through the streak.
            var mash = Play("mashFailsOnTheThirteenth");
            Assert.That(mash.Readings[mash.FailStep].WrongStreak, Is.EqualTo(TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK), "mash: the fail should land on the thirteenth");
        });
    }
}
