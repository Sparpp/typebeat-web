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
/// The cross-repo pin on PR 43's two engine rules that reach a browser play.
///
/// <para><b>BEST RETYPE JUDGEMENT</b> (bit 13 of the SECOND CONFIG flags word,
/// <c>TypingEngine.BestRetypeJudgement</c>): retyping a backspaced cell keeps the BETTER of its
/// grades. The engine's tier counts and the cell's stored delta move to the better press and the
/// submitted account amends the cell's existing result in place
/// (<c>TypeBeatScoreProcessor.UpdateCellResult</c>), with no combo increment and no HP. It is live
/// for every play, so a browser still keeping the first award would score the identical
/// performance lower than the desktop on the same board.</para>
///
/// <para>TWO ARMS, the shape <see cref="EarlyFinishParityTest"/> established. The browser has no
/// replay axis and bakes the live rule in; <c>CoreBestRetypeHarness.cjs</c> emits each script twice,
/// <c>live</c> and <c>legacy</c> (the rule stubbed back to "the first award stands"), and each is held
/// against the C# with the bit set and clear, through both the bare engine and the replay scorer, so a
/// JS that lost the upgrade fails the live arm and one that upgraded with the bit clear fails the
/// legacy arm.</para>
///
/// <para><b>PAUSED-WORD PUNCTUATION</b> (<c>TypingLine.build</c>, no era): a mark inside a paused word
/// interpolates within the stretch on its own side of the rest instead of across the whole token,
/// so it is never timed inside the silence. Held cell for cell on both streams.</para>
/// </summary>
[TestFixture]
public class BestRetypeJudgementParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreBestRetypeHarness.cjs"));

    private static JsonElement Retype() => harness.Value.GetProperty("retype");

    private static JsonElement Run(string scenario) => Retype().GetProperty("runs").GetProperty(scenario);

    private static readonly string[] scenarios = ["mehThenGreat", "greatStaysGreat", "repeatedRetypes", "offTimeToMeh", "correctionCap", "rewind"];

    private static readonly string[] arms = ["live", "legacy"];

    private static readonly JudgementType[] tiers = [JudgementType.Great, JudgementType.Ok, JudgementType.Meh, JudgementType.Premature, JudgementType.Lagging];

    #region The fixtures, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end, params WordPause[] pauses)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, Pauses = pauses };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's retype map: "ab cde" [1000, 7000), sung [1000, 4000], "ab" over [1000, 2000] and
    /// "cde" over [3000, 4000] (a = 1000, b = 1500, ' ' = 2000, c = 3000, d = 3333.33, e = 3666.67).
    /// The window is the browser loader's (a final line ends at its sung end + 3000), and the fixture
    /// test pins it.
    /// </summary>
    private static LyricLine[] RetypeLines() =>
    [
        Line("ab cde", 1000, 7000, 4000, Unit("ab", 1000, 2000), Unit("cde", 3000, 4000)),
    ];

    /// <summary>
    /// The paused-punctuation line: "well-known" [1000, 3000] resting [1500, 2000] after "well-",
    /// and "yes,no" [3000, 4000] resting [3400, 3600] after "yes".
    /// </summary>
    private static LyricLine PausedLine()
        => Line("well-known yes,no", 1000, 7000, 4000,
            Unit("well-known", 1000, 3000, new WordPause(1500, 2000, 5)),
            Unit("yes,no", 3000, 4000, new WordPause(3400, 3600, 3)));

    private static LyricBeatmap Map() => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata
        {
            Artist = "a",
            Title = "t",
            FolderPath = @"X:\nowhere",
            AudioFileName = "a.mp3",
        },
        Lines = RetypeLines(),
        Granularity = TimingGranularity.Line,
    };

    private static TypeBeatBeatmap Playable()
    {
        var map = new TypeBeatBeatmap();
        var lines = RetypeLines();

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
    /// A bare engine under every LIVE rule, with <paramref name="bestRetype"/> the ONE thing the arms
    /// differ in: true is the live stack (the engine's own default), false is what a replay recorded
    /// before PR 43 re-derives. The Japanese eras (bits 10 to 12) are left at their live defaults; no
    /// rule of theirs reads a cell outside a Polyglot Japanese line.
    /// </summary>
    private static TypingEngine Engine(bool bestRetype) => new TypingEngine(Map())
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
        // THIS FILE'S SUBJECT: bit 13 of the second CONFIG flags word.
        BestRetypeJudgement = bestRetype,
    };

    /// <summary>
    /// The script as a replay, headed by the two CONFIG frames the era bits travel in. The extended
    /// frame carries the live word PR 43 records (bits 10 to 13 on top of the earlier live ones), with
    /// bit 13 as the parameter: clear, the scorer re-derives the first-award rule.
    /// </summary>
    private static Replay Replay(string scenario, bool bestRetype)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true, manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true,
            earlyFinish: true, keyTimeJudgement: true, japaneseCharacterTiming: true, japaneseInputScoring: true, japaneseKanaInput: true, bestRetypeJudgement: bestRetype, stacksDisplacedClaims: true));

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

    private static TypeBeatReplayAccount Play(string scenario, bool bestRetype)
        => TypeBeatReplayScorer.Score(Playable(), Array.Empty<Mod>(), Replay(scenario, bestRetype), TypoRule.Deferred, ComboRestoreRule.OnFix);

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

    /// <summary>The fixture before the accounts: both loaders build the same cells and windows.</summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheRetypeFixture()
    {
        var engine = Engine(bestRetype: true);
        var lines = Retype().GetProperty("fixture").GetProperty("lines");

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
    /// Every script through the game's bare engine on both arms, step for step against the browser's
    /// readings: caret, seal, finish, every cell's state, the award and delta every CORRECT cell
    /// stands on, the five tier counts, and the engine's combo. The award and the counts are where
    /// an upgrade shows; the combo is where a retype that wrongly earned a second increment would.
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOnBothArms()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                foreach (string arm in arms)
                {
                    var engine = Engine(bestRetype: arm == "live");
                    var script = Run(scenario).GetProperty("script");
                    var readings = Run(scenario).GetProperty(arm).GetProperty("readings");

                    Assert.That(readings.GetArrayLength(), Is.EqualTo(script.GetArrayLength()), $"{scenario}/{arm}: one reading per step");

                    for (int i = 0; i < script.GetArrayLength(); i++)
                    {
                        var step = script[i];
                        var reading = readings[i];
                        double t = step.GetProperty("t").GetDouble();
                        string op = step.GetProperty("op").GetString()!;
                        string where = $"{scenario}/{arm}[{i}] {op} {t}";

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
                        Assert.That(engine.NextUnsealedLineIndex, Is.EqualTo(reading.GetProperty("nextUnsealedLineIndex").GetInt32()), $"{where}: the seal's position");
                        Assert.That(engine.IsFinished, Is.EqualTo(reading.GetProperty("finished").GetBoolean()), $"{where}: finished");
                        Assert.That(engine.Combo, Is.EqualTo(reading.GetProperty("combo").GetInt32()), $"{where}: combo");
                        Assert.That(engine.MaxCombo, Is.EqualTo(reading.GetProperty("maxCombo").GetInt32()), $"{where}: max combo");

                        var counts = engine.BuildResults().Counts;
                        var browserCounts = reading.GetProperty("counts");

                        foreach (var tier in tiers)
                            Assert.That(counts.GetValueOrDefault(tier), Is.EqualTo(browserCounts.GetProperty(tier.ToString()).GetInt32()), $"{where}: {tier} count");

                        var states = reading.GetProperty("states");
                        var judged = reading.GetProperty("judged");

                        for (int l = 0; l < engine.Lines.Count; l++)
                        {
                            var cells = engine.Lines[l].Cells;
                            Assert.That(states[l].GetArrayLength(), Is.EqualTo(cells.Count), $"{where}: line {l} cell count");

                            for (int c = 0; c < cells.Count; c++)
                            {
                                Assert.That(StateName(cells[c].State), Is.EqualTo(states[l][c].GetString()), $"{where}: line {l} cell {c} state");

                                if (cells[c].State != CellState.Correct)
                                    continue;

                                var browser = judged[l][c];
                                Assert.That(cells[c].JudgedTier?.ToString(), Is.EqualTo(browser.GetProperty("tier").GetString()), $"{where}: line {l} cell {c} award");
                                Assert.That(cells[c].JudgedDelta, Is.EqualTo(browser.GetProperty("delta").GetDouble()), $"{where}: line {l} cell {c} delta");
                            }
                        }
                    }
                }
            }
        });
    }

    /// <summary>
    /// The SUBMITTED account, browser against the game's own replay scorer with bit 13 set (live)
    /// and clear (legacy), field for field and with no tolerance on the doubles. This is where the
    /// in-place amend lives: the upgraded result has to replace the old one's accuracy and score
    /// contribution without a second combo increment.
    /// </summary>
    [Test]
    public void TheSubmittedAccountsAgreeOnBothArms()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                foreach (string arm in arms)
                {
                    var game = Play(scenario, bestRetype: arm == "live");
                    var submitted = Run(scenario).GetProperty(arm).GetProperty("submitted");
                    string where = $"{scenario}/{arm}";

                    Assert.That(game.UnconsumedFrames, Is.Zero, $"{where}: the game never reached some of the recorded keystrokes");
                    Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Statistics)), $"{where}: statistics");
                    Assert.That(Dict(submitted, "maximumStatistics"), Is.EquivalentTo(Wire(game.MaximumStatistics)), $"{where}: maximum_statistics");
                    Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.MaxCombo), $"{where}: max_combo");
                    Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.TotalScore), $"{where}: total_score");
                    Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Accuracy), $"{where}: accuracy");
                    Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(game.Completion), $"{where}: completion");
                    Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(game.Rank.ToString()), $"{where}: rank");
                }
            }
        });
    }

    /// <summary>
    /// NON-VACUITY, read off the browser's own two arms: the scripts must actually be about the rule.
    /// Every scenario but <c>greatStaysGreat</c> ends on different tier counts across the arms, every
    /// one whose upgrade crosses a submitted result (all but that one and <c>offTimeToMeh</c>, where
    /// Premature and Meh both submit as meh) ends on a different total, and on EVERY step of every
    /// scenario the two arms hold the same combo, submitted combo and HP, because an upgrade moves
    /// neither.
    /// </summary>
    [Test]
    public void TheTwoArmsReallySeparateAndOnlyOnTheGrade()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in scenarios)
            {
                var live = Run(scenario).GetProperty("live");
                var legacy = Run(scenario).GetProperty("legacy");
                var liveReadings = live.GetProperty("readings");
                var legacyReadings = legacy.GetProperty("readings");
                var liveLast = liveReadings[liveReadings.GetArrayLength() - 1];
                var legacyLast = legacyReadings[legacyReadings.GetArrayLength() - 1];

                bool countsMove = scenario != "greatStaysGreat";
                bool scoreMoves = scenario is not ("greatStaysGreat" or "offTimeToMeh");

                Assert.That(liveLast.GetProperty("counts").GetRawText() != legacyLast.GetProperty("counts").GetRawText(), Is.EqualTo(countsMove),
                    $"{scenario}: whether the tier counts part across the arms");
                Assert.That(live.GetProperty("submitted").GetProperty("totalScore").GetInt64() != legacy.GetProperty("submitted").GetProperty("totalScore").GetInt64(),
                    Is.EqualTo(scoreMoves), $"{scenario}: whether the submitted total parts across the arms");
                Assert.That(liveLast.GetProperty("finished").GetBoolean(), Is.True, $"{scenario}: the run reached its end");

                for (int i = 0; i < liveReadings.GetArrayLength(); i++)
                {
                    foreach (string field in new[] { "combo", "maxCombo", "processorCombo", "processorHighestCombo", "health" })
                    {
                        Assert.That(liveReadings[i].GetProperty(field).GetRawText(), Is.EqualTo(legacyReadings[i].GetProperty(field).GetRawText()),
                            $"{scenario}[{i}]: an upgrade moved {field}");
                    }
                }
            }

            // The reported case end to end: a Meh retyped on the beat submits as a full-score Great run.
            var mehThenGreat = Run("mehThenGreat").GetProperty("live").GetProperty("submitted");
            Assert.That(Dict(mehThenGreat, "statistics"), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 6 }), "mehThenGreat: every cell submits as great");
            Assert.That(mehThenGreat.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000), "mehThenGreat: a perfect total");
        });
    }

    /// <summary>
    /// PAUSED-WORD PUNCTUATION, cell for cell on both streams: the browser's loader against
    /// <c>TypingLine.FromLyricLine</c> on the aligned (live) target era. The default stream is what
    /// /play types (the comma is deleted and the hyphen typed as a space at the hyphen's own target);
    /// Literate types both marks.
    ///
    /// <para>Non-vacuity on the C# itself: the hyphen ending the first stretch of "well-known" sits at
    /// 1437.5, between 'l' (1375) and the rest's start (1500), where the token-wide rule put it at
    /// 1687.5 inside the rest; the comma opening the second stretch of "yes,no" sits at the rest's end
    /// (3600), where the token-wide rule put it at 3433.33.</para>
    /// </summary>
    [Test]
    public void PausedWordPunctuationIsTimedWithinItsOwnStretchOnBothSides()
    {
        var paused = harness.Value.GetProperty("paused");

        Assert.Multiple(() =>
        {
            foreach (string stream in new[] { "default", "literate" })
            {
                var line = TypingLine.FromLyricLine(PausedLine(), literate: stream == "literate", alignSubdivisionTargets: true);
                var browser = paused.GetProperty(stream)[0];

                Assert.That(browser.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{stream}: cell count");

                for (int c = 0; c < Math.Min(line.Cells.Count, browser.GetArrayLength()); c++)
                {
                    Assert.That(browser[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"{stream}[{c}]: expected");
                    Assert.That(browser[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"{stream}[{c}]: target");
                }
            }

            var literate = TypingLine.FromLyricLine(PausedLine(), literate: true, alignSubdivisionTargets: true);
            Assert.That(literate.Cells[4].Expected, Is.EqualTo('-'));
            Assert.That(literate.Cells[4].TargetTime, Is.EqualTo(1437.5), "the hyphen is timed inside its own stretch, before the rest");
            Assert.That(literate.Cells[14].Expected, Is.EqualTo(','));
            Assert.That(literate.Cells[14].TargetTime, Is.EqualTo(3600), "the comma is timed at its own stretch's start, after the rest");

            var plain = TypingLine.FromLyricLine(PausedLine(), alignSubdivisionTargets: true);
            Assert.That(plain.Cells[4].Expected, Is.EqualTo(' '), "the default stream types the hyphen as a space");
            Assert.That(plain.Cells[4].TargetTime, Is.EqualTo(1437.5), "and that space keeps the hyphen's target");
        });
    }
}
