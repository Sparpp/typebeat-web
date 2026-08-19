using System.Diagnostics;
using System.Text;
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
/// The DIFFERENTIAL cross-repo pin on the browser scoring core (backlog 172): long, mixed,
/// generated keystroke streams replayed through the browser's <c>typebeat-core.js</c> and through
/// the GAME's own <see cref="TypeBeatReplayScorer"/>, compared field for field with no tolerance.
///
/// <para>Every other JS fidelity guard, this project's <see cref="WordSkipLiveParityTest"/>
/// included, pins ONE named rule through a hand-written sequence. That is the right shape for a
/// rule somebody thought about, and it is why each of them exists; what none of them can do is
/// catch a rule nobody wrote a scenario for, or an interaction between two of them. The two
/// clients submit to the SAME leaderboards, so any such gap is a browser play that scores
/// differently from the identical desktop performance. This test is the sweep: the harness
/// generates streams that mix correct presses at every judgement tier, typed-through typos,
/// backspaces, rejected keys, word skips and idle stretches that seal lines, and emits both what
/// it played and what the browser made of it. The C# side plays the identical script.</para>
///
/// <para>The FIXTURES are pinned before the accounts are. Both sides build their cells through
/// their own loader (the browser's <c>buildBeatmap</c>, the game's
/// <see cref="TypingLine.FromLyricLine"/>), so a fixture that drifted would show up as an account
/// divergence and be blamed on the engine. <see cref="TheTwoLoadersAgreeOnEveryFixture"/> runs
/// first in intent: it holds every cell's expected char, target time and judge tier, plus the
/// line's deadline, cue and seal grace, against each other.</para>
///
/// <para>The C# side is fed under every LIVE rule, because the browser selects no era on any axis:
/// it has no mods payload, no replay input, and nothing anywhere re-scores a stored row through
/// that file.</para>
/// </summary>
[TestFixture]
public class EngineFuzzLiveParityTest
{
    #region Fixtures, mirroring CoreFuzzHarness.cjs

    private static TimedUnit Unit(string text, double start, double end, double confidence = 1, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, Confidence = confidence, SyllableBoundaries = syllables };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    private static LyricLine Estimated(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units, Estimated = true };

    private static TypeBeatBeatmap Map(TimingGranularity granularity, params LyricLine[] lines)
    {
        var map = new TypeBeatBeatmap();

        for (int i = 0; i < lines.Length; i++)
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = granularity });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Song";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    /// <summary>
    /// The same four maps the harness writes as map JSON, in the LyricLine shape the game's own
    /// tests use. The line deadlines are what the browser's loader derives: a line ends where the
    /// NEXT one starts, and the last one at min(song end, vocal end + 3000).
    /// </summary>
    private static LyricLine[] Fixture(string name)
    {
        switch (name)
        {
            case "catDog":
                return [Line("cat dog", 1000, 6000, 5000, Unit("cat", 1000, 3000), Unit("dog", 3000, 5000))];

            case "abCd":
                return [Line("ab cd", 1000, 4000, 3000, Unit("ab", 1000, 2000), Unit("cd", 2000, 3000))];

            case "catDogThenHi":
                return
                [
                    Line("cat dog", 1000, 6000, 5000, Unit("cat", 1000, 3000), Unit("dog", 3000, 5000)),
                    Line("hi", 6000, 10000, 7000, Unit("hi", 6000, 7000)),
                ];

            case "quickBrownFox":
                return
                [
                    Line("the quick brown", 1000, 5000, 4000, Unit("the", 1000, 1600), Unit("quick", 1600, 2800), Unit("brown", 2800, 4000)),
                    Line("fox jumps", 5000, 10000, 7000, Unit("fox", 5000, 5800), Unit("jumps", 5800, 7000)),
                ];

            case "mixedTiers":
                return
                [
                    Line("The bad-cat sat.", 1000, 5000, 4000,
                        Unit("The", 1000, 1600),
                        Unit("bad-cat", 1600, 2800, 1, 2200),
                        Unit("sat.", 2800, 4000, 0.1)),
                    Estimated("Oh no", 5000, 9500, 6500, Unit("Oh", 5000, 5700), Unit("no", 5700, 6500)),
                ];

            case "syllabic":
                return [Line("one two", 1000, 6000, 3000, Unit("one", 1000, 2000, 1, 1500), Unit("two", 2000, 3000))];

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture");
        }
    }

    /// <summary>
    /// The beatmap granularity each fixture's header declares, which decides the base window ladder
    /// every cell of it is judged on unless the cell's own timing is unreliable.
    /// </summary>
    private static TimingGranularity GranularityOf(string name)
    {
        switch (name)
        {
            case "mixedTiers":
                return TimingGranularity.Word;

            case "syllabic":
                return TimingGranularity.Syllable;

            default:
                return TimingGranularity.Line;
        }
    }

    #endregion

    #region Driving the two sides

    /// <summary>
    /// The generated stream as a replay, headed by the CONFIG frame the two judgement-relevant
    /// settings travel in: bit 0 allow-wrong-input (on, the default model and all the browser has)
    /// and bit 1 space-skips-word (whichever half of the case matrix this run is).
    /// </summary>
    private static Replay Keystrokes(JsonElement keys, bool spaceSkipsWord)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: spaceSkipsWord));

        foreach (var key in keys.EnumerateArray())
        {
            double time = key[0].GetDouble();
            string character = key[1].GetString()!;

            replay.Frames.Add(new TypeBeatReplayFrame(time, character[0]));
        }

        return replay;
    }

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

    #region The browser side: the generated harness

    private static readonly Lazy<JsonElement> browser_runs = new Lazy<JsonElement>(RunHarness);

    /// <summary>
    /// Runs the fuzz harness against the served <c>wwwroot/js/typebeat-core.js</c> and parses its
    /// stdout as JSON. Node is optional on a dev box, so a missing node ignores the test rather
    /// than failing it (CI has node), which is the rule every other JS guard already applies.
    /// </summary>
    private static JsonElement RunHarness()
    {
        string root = RepoRoot();
        string core = Path.Combine(root, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js");
        string harness = Path.Combine(root, "tests", "Typebeat.Web.Tests", "Js", "CoreFuzzHarness.cjs");

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add(core);

        Process process;

        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Assert.Ignore($"node is not available to run the JS fidelity harness: {ex.Message}");
            throw; // unreachable
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"harness failed: {stderr}");
        return JsonDocument.Parse(stdout).RootElement;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("could not locate the repo root");
    }

    #endregion

    /// <summary>
    /// The two loaders resolve every fixture to the same cells. Asserted separately, and before any
    /// account is compared, because a fixture divergence would otherwise reach the account
    /// comparison wearing the engine's clothes.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryFixture()
    {
        var fixtures = browser_runs.Value.GetProperty("fixtures");

        Assert.Multiple(() =>
        {
            foreach (var fixture in fixtures.EnumerateObject())
            {
                var lines = Fixture(fixture.Name);
                var browserLines = fixture.Value;

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Length), $"{fixture.Name}: line count");

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = TypingLine.FromLyricLine(lines[i], GranularityOf(fixture.Name));
                    var browserLine = browserLines[i];

                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{fixture.Name}[{i}]: endTime");
                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{fixture.Name}[{i}]: activationTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{fixture.Name}[{i}]: sealGraceMs");

                    var browserCells = browserLine.GetProperty("cells");
                    Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{fixture.Name}[{i}]: cell count");

                    for (int c = 0; c < line.Cells.Count && c < browserCells.GetArrayLength(); c++)
                    {
                        var cell = line.Cells[c];
                        var browserCell = browserCells[c];

                        Assert.That(browserCell.GetProperty("expected").GetString(), Is.EqualTo(cell.Expected.ToString()), $"{fixture.Name}[{i}][{c}]: expected");
                        Assert.That(browserCell.GetProperty("target").GetDouble(), Is.EqualTo(cell.TargetTime), $"{fixture.Name}[{i}][{c}]: target");
                        Assert.That(browserCell.GetProperty("tier").GetString(), Is.EqualTo(cell.JudgeGranularity.ToString()), $"{fixture.Name}[{i}][{c}]: tier");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Every generated run's SUBMITTED account, browser against game, field for field and with no
    /// tolerance on the doubles: the two are the same sequence of operations on the same inputs, so
    /// any difference at all is a divergence rather than rounding.
    ///
    /// <para>One test rather than one per case, because the cases are generated: naming them
    /// individually would pin a case count that the next widening of the harness would have to
    /// churn, and <see cref="Assert.Multiple"/> already reports every failing case by name.</para>
    /// </summary>
    [Test]
    public void EveryGeneratedRunAgrees()
    {
        var cases = browser_runs.Value.GetProperty("cases");

        Assert.That(cases.GetArrayLength(), Is.GreaterThan(50), "the harness should be generating a real sweep");

        Assert.Multiple(() =>
        {
            foreach (var browserCase in cases.EnumerateArray())
            {
                string scenario = browserCase.GetProperty("name").GetString()!;
                bool spaceSkipsWord = browserCase.GetProperty("spaceSkipsWord").GetBoolean();
                string fixture = browserCase.GetProperty("fixture").GetString()!;
                var map = Map(GranularityOf(fixture), Fixture(fixture));
                var replay = Keystrokes(browserCase.GetProperty("keys"), spaceSkipsWord);

                var game = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
                var submitted = browserCase.GetProperty("submitted");

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
    /// The sweep is only worth what it exercises, so this pins the SHAPE of what was generated
    /// rather than any one run: the cases have to reach every scoring tier, both exits from a typo,
    /// real misses and a spread of ranks, or a green run above means nothing. It reads the browser
    /// side alone, which is the side the harness measures.
    /// </summary>
    [Test]
    public void TheSweepReachesTheRulesItIsMeantTo()
    {
        var cases = browser_runs.Value.GetProperty("cases");

        int withTypos = 0, withMisses = 0, withOk = 0, withMeh = 0, perfect = 0;
        int skipPresses = 0, comboRestores = 0, backspaces = 0;

        foreach (var browserCase in cases.EnumerateArray())
        {
            var statistics = Dict(browserCase.GetProperty("submitted"), "statistics");

            if (statistics.ContainsKey("good")) withTypos++;
            if (statistics.ContainsKey("miss")) withMisses++;
            if (statistics.ContainsKey("ok")) withOk++;
            if (statistics.ContainsKey("meh")) withMeh++;
            if (browserCase.GetProperty("submitted").GetProperty("rank").GetString() == "X") perfect++;

            skipPresses += browserCase.GetProperty("skipPresses").GetInt32();
            comboRestores += browserCase.GetProperty("restores").GetInt32();
            backspaces += browserCase.GetProperty("keys").EnumerateArray().Count(key => key[1].GetString() == "");
        }

        Assert.Multiple(() =>
        {
            Assert.That(withTypos, Is.GreaterThan(0), "no run left an uncorrected typo");
            Assert.That(withMisses, Is.GreaterThan(0), "no run missed a cell");
            Assert.That(withOk, Is.GreaterThan(0), "no run landed a press in the Ok band");
            Assert.That(withMeh, Is.GreaterThan(0), "no run landed a press in the Meh band");
            Assert.That(perfect, Is.GreaterThan(0), "no run typed the map out clean");
            Assert.That(backspaces, Is.GreaterThan(0), "no run ever erased anything");
            Assert.That(skipPresses, Is.GreaterThan(0), "no run abandoned a word");
            Assert.That(comboRestores, Is.GreaterThan(0), "no run walked back into a break and resumed the streak");
        });
    }
}
