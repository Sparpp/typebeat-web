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
/// The cross-repo pin on the LOSSLESS SKIP RECLAIM (backlog 260): a word given up by accident and
/// then typed out in full costs the run NOTHING.
///
/// <para>A player finished a map with every one of its 920 cells typed, 0 misses, and a max combo of
/// 919. The increment missing was the WORD GAP the skipping space was itself judged on, and it was
/// being dropped three different ways, all of them present identically in <c>typebeat-core.js</c>:
///
/// <list type="bullet">
/// <item><b>The rush cap charged the space for the word it abandoned.</b> The skip walks the caret
/// past the whole word BEFORE the same press is judged on the gap it parked on, and the cap measures
/// the caret POSITIONALLY, so an abandoned tail plus any lead over
/// <see cref="TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/> refused that press its combo, silently: the
/// skip's own break had already zeroed the run, so nothing was announced and no claim discarded, and
/// the gap still resolved Correct, which makes every later retype of it inert.</item>
/// <item><b>The passive claim arm dropped the run it stood on.</b> A break taking no more than the
/// claim's own credit (backlog 243) keeps the older claim, but its call site has already run the
/// break, so its own spent run was gone with nothing left to redeem it. It is FOLDED into the claim
/// instead, streak and positions together.</item>
/// <item><b>The Ctrl+A anchor was one cell short of its own collapse.</b> That half carries no era
/// and is pinned by <see cref="WordInputParityTest"/>, where the whole gesture composition lives;
/// here the collapse appears as the plain backspaces it is made of, so the caret it lands on is
/// compared like any other reading.</item>
/// </list></para>
///
/// <para>The two clients submit to the SAME leaderboards, so this is not cosmetic: both of the first
/// two move <c>max_combo</c> and every combo portion after the skip. A browser still dropping the
/// increment would score the identical performance lower than the desktop.</para>
///
/// <para>TWO ARMS, the shape <see cref="SealComboBreakLiveParityTest"/> established. The ENGINE's own
/// run is compared step for step, and the SUBMITTED account goes through
/// <see cref="TypeBeatReplayScorer"/>, which is the headless assembly of the seams
/// <c>TypeBeatPlayfield</c> wires up in a live play. ONE COPY OF THE KEYSTROKES: the scripts and the
/// browser's readings of them come out of <c>CoreFlexibleLinesHarness.cjs</c>'s
/// <c>losslessSkipReclaim</c> section, which is where they belong because the rush-cap half is only
/// REACHABLE under the flexible caret (a caret pinned to the playhead can never be twelve countable
/// characters ahead of it).</para>
/// </summary>
[TestFixture]
public class LosslessSkipReclaimLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("losslessSkipReclaim");

    private static JsonElement Run(string scenario) => Section().GetProperty("runs").GetProperty(scenario);

    /// <summary>The two corrected runs, plus the clean run they are read against.</summary>
    private static readonly string[] scenarios = ["headOfWordSkip", "doubleSpace", "cleanRun"];

    /// <summary>The two that give a word up, which are the ones the era arm has an opinion about.</summary>
    private static readonly string[] corrected = ["headOfWordSkip", "doubleSpace"];

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    /// <summary>
    /// THE REPORTED SHAPE'S FIXTURE, in the game's terms and with the window the browser's loader
    /// derived: a short word, a LONG one, and two short ones, dense enough that the long word alone
    /// is far more than <see cref="TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/>.
    ///
    /// <para>"ab cdefghijkl mn op" on [1000, 5600), sung to 2600. Cells (index: char = target) are
    /// 0:a = 1000, 1:b = 1100, 2:' ' = 1200, 3:c = 1200 .. 12:l = 2100, 13:' ' = 2200, 14:m = 2200,
    /// 15:n = 2300, 16:' ' = 2400, 17:o = 2400, 18:p = 2500. Nineteen cells, sixteen of them
    /// countable (the three gaps are not), and no seal grace, so nothing seals mid-script.</para>
    /// </summary>
    private static LyricLine[] Lines() =>
    [
        new LyricLine
        {
            RawText = "ab cdefghijkl mn op",
            StartTime = 1000,
            EndTime = 5600,
            SingEndTime = 2600,
            Units =
            [
                Unit("ab", 1000, 1200),
                Unit("cdefghijkl", 1200, 2200),
                Unit("mn", 2200, 2400),
                Unit("op", 2400, 2600),
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
    /// <see cref="TypingEngine.LosslessSkipReclaim"/> is the one this file is about, and it is the
    /// reason a bare engine is not the live client: it defaults false so that every stored replay
    /// re-derives the dropped increment its player was submitted under.
    /// </summary>
    private static TypingEngine LiveEngine(bool losslessSkipReclaim = true) => new TypingEngine(Map())
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
        LosslessSkipReclaim = losslessSkipReclaim,
        FoldsDisplacedClaim = true,
        FirstLineLeadIn = true,
    };

    /// <summary>
    /// The script as a replay, headed by the CONFIG frame the era bits travel in. Bit 11 is this
    /// file's subject and is a parameter rather than a constant, because clearing it is how
    /// <see cref="TheseScriptsReallySeparateTheTwoEras"/> proves the fixtures are about the rule at
    /// all. Bit 1 (space-skips-word) is SET, unlike in <see cref="SealComboBreakLiveParityTest"/>:
    /// the whole subject here is a space struck inside a word.
    /// </summary>
    private static Replay Replay(bool losslessSkipReclaim, string scenario)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true,
            wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true,
            firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: losslessSkipReclaim, foldsDisplacedClaim: true, firstLineLeadIn: true));

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

    private static TypeBeatReplayAccount Play(string scenario, bool losslessSkipReclaim = true)
        => TypeBeatReplayScorer.Score(Playable(), Array.Empty<Mod>(), Replay(losslessSkipReclaim, scenario), TypoRule.Deferred, ComboRestoreRule.OnFix);

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
    /// THE FIXTURE BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both
    /// sides build their cells through their own loader (the browser's <c>buildBeatmap</c>, the
    /// game's <c>TypingLine.FromLyricLine</c>), so a fixture that drifted would show up below as an
    /// engine divergence and be blamed on the skip.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheFixture()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Section().GetProperty("maxCharsAhead").GetInt32(), Is.EqualTo(TypingEngine.FLETCHER_MAX_CHARS_AHEAD));

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
    /// step: where the caret is, how far ahead of the playhead it sits, what Ctrl+A would offer, every
    /// cell's state, and the whole combo account.
    ///
    /// <para>The readings that matter are the ones on the skipping space. Its <c>combo</c> must be 1
    /// (the gap credited) rather than 0, and its <c>charsAheadOfPlayhead</c> must be 9 (the caret
    /// really IS out past the cap after the skip, which is what makes the fix a statement about WHERE
    /// the measurement is taken rather than about the cap being loose). Then, on the step the
    /// collapse ends, the caret must equal the anchor the step before it offered: a browser whose
    /// anchor was one cell short lands behind its own selection and manufactures a typo on the next
    /// keystroke.</para>
    ///
    /// <para><c>runPositions</c> is asserted by LENGTH rather than by content, because the C# list is
    /// private: what is worth pinning is the invariant the fold rests on,
    /// <c>runPositions.length == combo</c>, which is what makes the folded streak and the folded
    /// positions provably the same size.</para>
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
                    Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(reading.GetProperty("retypeSelectionAnchor").GetInt32()), $"{where}: the Ctrl+A anchor");
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
    /// increments the skip drops are combo, and combo weights every judgement portion after it, so a
    /// browser that agreed on the engine's combo and mirrored it late into the processor would still
    /// land a different total on the shared leaderboards.
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
    /// THE LAW ITSELF, stated as a comparison rather than as a literal: an accidental skip, fully
    /// corrected, costs the run NOTHING, so both corrected scripts must reach the CLEAN run's max
    /// combo with the same tier counts and no misses. Read off both clients, because the whole point
    /// of the change is that neither of them may be one short.
    /// </summary>
    [Test]
    public void BothCorrectedRunsReachTheCleanRunsMaxCombo()
    {
        var clean = Play("cleanRun");

        Assert.Multiple(() =>
        {
            Assert.That(clean.MaxCombo, Is.EqualTo(19), "nineteen cells, nineteen increments");
            Assert.That(Run("cleanRun").GetProperty("submitted").GetProperty("maxCombo").GetInt32(), Is.EqualTo(19));

            foreach (string scenario in corrected)
            {
                var game = Play(scenario);

                Assert.That(game.MaxCombo, Is.EqualTo(clean.MaxCombo), $"{scenario}: the skip cost the corrected run nothing at all");
                Assert.That(Run(scenario).GetProperty("maxCombo").GetInt32(), Is.EqualTo(19), $"{scenario}: and the browser agrees");

                // Everything the rule does not reach: the same cells, the same tiers, no misses, and
                // no mistype, because nothing was ever typed wrong.
                Assert.That(Wire(game.Statistics), Is.EquivalentTo(Wire(clean.Statistics)), $"{scenario}: the same judgements as the clean run");
                Assert.That(game.Accuracy, Is.EqualTo(1.0), $"{scenario}: accuracy");
                Assert.That(game.Completion, Is.EqualTo(1.0), $"{scenario}: completion");
                Assert.That(Run(scenario).GetProperty("mistypes").GetInt32(), Is.Zero, $"{scenario}: nothing was typed wrong");
            }
        });
    }

    /// <summary>
    /// NON-VACUITY, and the only assertion here that does not compare the two clients: the same
    /// replays re-derived with CONFIG bit 11 CLEAR, which is what every stored score was played
    /// under. If the browser's numbers matched THAT too, the tests above would be pinning a rule
    /// neither script reaches.
    ///
    /// <para>Both corrected scripts must part from the classic arm, and part in the direction the
    /// rule promises: the reclaimed run is never shorter and never worth less, because the rule can
    /// only ever put back increments the old one dropped. It moves nothing else, which is why the
    /// judgements, the accuracy and the completion are asserted EQUAL across the two arms, and why
    /// the clean run (which gives up nothing) is bit-identical under both.</para>
    /// </summary>
    [Test]
    public void TheseScriptsReallySeparateTheTwoEras()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in corrected)
            {
                var live = Play(scenario);
                var stored = Play(scenario, losslessSkipReclaim: false);

                Assert.That(stored.MaxCombo, Is.LessThan(live.MaxCombo), $"{scenario}: the dropped increment should cost max_combo");
                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore), $"{scenario}: and total_score");
                Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics), $"{scenario}: the era must move no judgement");
                Assert.That(live.Accuracy, Is.EqualTo(stored.Accuracy), $"{scenario}: the era must move no accuracy");
                Assert.That(live.Completion, Is.EqualTo(stored.Completion), $"{scenario}: the era must move no completion");
            }

            var cleanLive = Play("cleanRun");
            var cleanStored = Play("cleanRun", losslessSkipReclaim: false);

            Assert.That(cleanStored.MaxCombo, Is.EqualTo(cleanLive.MaxCombo), "a run that gives up nothing re-derives identically");
            Assert.That(cleanStored.TotalScore, Is.EqualTo(cleanLive.TotalScore));
        });
    }

    /// <summary>
    /// And the other half of non-vacuity, read off the BROWSER's own run rather than off either
    /// engine, because a comparison of two clients that both did nothing interesting passes. Each
    /// defect has one thing that has to be true of these scripts, or the tests above are not about
    /// the rule:
    ///
    /// <list type="bullet">
    /// <item>DEFECT A: the skipping space really is judged at a caret far out past the cap, and still
    /// earns its gap. Nine countable characters ahead of the playhead, against a cap of five.</item>
    /// <item>DEFECT B: the second space really takes a PASSIVE break (one that keeps the deeper claim
    /// rather than replacing it), which is only visible as the redemption being bigger than the run
    /// the first break took: 4 rather than 3.</item>
    /// <item>DEFECT C: the collapse lands EXACTLY on the anchor the gesture offered, rather than one
    /// cell behind it, and the retype that follows makes no mistype.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheScriptsReallyReachAllThreeDefects()
    {
        Assert.Multiple(() =>
        {
            foreach (string scenario in corrected)
            {
                var readings = Run(scenario).GetProperty("readings");
                var script = Run(scenario).GetProperty("script");

                int lastSkip = -1;
                int lastBackspace = -1;

                for (int i = 0; i < script.GetArrayLength(); i++)
                {
                    string op = script[i].GetProperty("op").GetString()!;

                    if (op == "backspace") lastBackspace = i;

                    // A space press that MOVED the caret more than one cell is a skip.
                    if (op == "key" && script[i].GetProperty("c").GetString() == " " && i > 0
                        && readings[i].GetProperty("at").GetProperty("cell").GetInt32() > readings[i - 1].GetProperty("at").GetProperty("cell").GetInt32() + 1)
                    {
                        lastSkip = i;
                    }
                }

                Assert.That(lastSkip, Is.GreaterThan(0), $"{scenario}: no space in this script gave a word up");
                Assert.That(lastBackspace, Is.GreaterThan(lastSkip), $"{scenario}: the collapse should follow the skip");

                // DEFECT C: the anchor offered before the collapse, and the caret it landed on.
                int anchor = readings[lastSkip].GetProperty("retypeSelectionAnchor").GetInt32();

                Assert.That(anchor, Is.EqualTo(2), $"{scenario}: the gap in FRONT of the wholly abandoned word");
                Assert.That(readings[lastBackspace].GetProperty("at").GetProperty("cell").GetInt32(), Is.EqualTo(anchor),
                    $"{scenario}: the collapse ended behind its own selection, so the next letter is a manufactured typo");
            }

            // DEFECT A, on the script written for it: the caret really IS out past the cap when the
            // skipping space is judged, and the gap is credited anyway.
            var headOfWord = Run("headOfWordSkip").GetProperty("readings");
            var skipStep = headOfWord[4];

            Assert.That(skipStep.GetProperty("charsAheadOfPlayhead").GetInt32(), Is.EqualTo(9),
                "the caret is nine countable chars past the playhead after the skip, four over the cap");
            Assert.That(Section().GetProperty("maxCharsAhead").GetInt32(), Is.EqualTo(5), "against a cap of five");
            Assert.That(skipStep.GetProperty("combo").GetInt32(), Is.EqualTo(1), "and the gap is credited: the word given up is not the player's budget");
            Assert.That(headOfWord[3].GetProperty("charsAheadOfPlayhead").GetInt32(), Is.EqualTo(-1),
                "measured where the press was MADE, the player was not rushing at all");

            // DEFECT B, on the script written for it: the redemption is bigger than the run the first
            // break took, which is exactly the increment the passive break folded in.
            Assert.That(Run("doubleSpace").GetProperty("comboBreaks").GetInt32(), Is.EqualTo(2), "two spaces, two breaks");
            Assert.That(Run("doubleSpace").GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                Is.EqualTo(new[] { 4 }), "the deeper claim swallowed the run the passive break spent");
            Assert.That(Run("headOfWordSkip").GetProperty("comboRestores").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                Is.EqualTo(new[] { 3 }), "with one space there is nothing to fold, so the claim stands at what the break took");
        });
    }
}
