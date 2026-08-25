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
/// that file. Since backlog 179 that includes SYLLABLE-SPAN judgement and since backlog 181 the
/// WORD-GAP input model, both of which the game reads off each replay's own CONFIG frame and
/// defaults OFF, so the generated frames here have to set bits 2 and 3 or the C# arm would re-derive
/// on point targets while the browser judges spans, and reject the wrong keys the browser types into
/// word gaps (see <see cref="Keystrokes"/>). The places those rules are asserted APART rather than
/// together are <see cref="ClearingTheConfigFrameSyllableBitReDerivesTheClassicRule"/> and
/// <see cref="ClearingTheConfigFrameWordGapBitRejectsTheTypoInstead"/>.</para>
///
/// <para>A handful of the cases are SCRIPTED rather than generated, and they are played and
/// compared identically. The generator reaches what it happens to roll, and backlog 176 found a
/// rule it never once rolled the deciding shape for: two redeemable breaks in a row with nothing
/// earned between them, and a walk back into the older one. The scripted cases are written out in
/// the harness beside the generator, so a shape that matters is not left to a seed.</para>
/// </summary>
[TestFixture]
public class EngineFuzzLiveParityTest
{
    #region Fixtures, mirroring CoreFuzzHarness.cjs

    private static TimedUnit Unit(string text, double start, double end, double confidence = 1, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, Confidence = confidence, SyllableBoundaries = syllables };

    /// <summary>
    /// A subdivided word carrying an AUTHORED character split (backlog 181), the word-level
    /// <c>split_chars</c> the harness writes into the same fixture's JSON.
    /// </summary>
    private static TimedUnit Authored(string text, double start, double end, int[] splits, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables, SyllableSplits = splits };

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
    /// The same maps the harness writes as map JSON, in the LyricLine shape the game's own tests
    /// use. The line deadlines are what the browser's loader derives: a line ends where the NEXT one
    /// starts, and the last one at min(song end, vocal end + 3000).
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

            case "syllableWords":
                return
                [
                    Line("cake tonight", 1000, 5000, 4000, Unit("cake", 1000, 2400), Unit("tonight", 2400, 4000)),
                    Line("little people", 5000, 11000, 8000, Unit("little", 5000, 6500), Unit("people", 6500, 8000)),
                ];

            case "stylised":
                return [Line("ohhh little", 1000, 6500, 3500, Unit("ohhh", 1000, 2000), Unit("little", 2000, 3500))];

            case "subtimed":
                return
                [
                    Line("cake tonight", 1000, 7000, 4000,
                        Unit("cake", 1000, 2400, 1, 1700),
                        Unit("tonight", 2400, 4000, 1, 2800, 3300)),
                ];

            case "authoredSplit":
                return
                [
                    Line("beautiful tonight", 1000, 7000, 4000,
                        Authored("beautiful", 1000, 1900, [6], 1450),
                        Authored("tonight", 1900, 4000, [2, 5], 2600, 3200)),
                ];

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
    /// The generated stream as a replay, headed by the CONFIG frame the five judgement-relevant
    /// settings travel in: bit 0 allow-wrong-input (on, the default model and all the browser has),
    /// bit 1 space-skips-word (whichever half of the case matrix this run is), bit 2 syllable-span
    /// judgement, bit 3 wrong-input-on-word-gaps and bit 4 strict-spaces.
    ///
    /// <para>Bits 2, 3 and 4 are ON here, and getting any of them wrong would quietly gut the whole
    /// sweep rather than fail loudly in one place. <see cref="TypeBeatReplayScorer"/> follows the
    /// CONFIG frame for all three (<c>ReplayEngineFeed.Apply</c>), and the engine's DEFAULTS are the
    /// classic point rule, the strict word gap and the classic space rules, because a replay
    /// recorded before backlog 179, 181 or 184 must re-derive under the rules its fingers were
    /// graded on. The browser has no era axis at all: it only plays live, so it judges on spans,
    /// types wrong letters into word gaps and applies the space discipline unconditionally. A config
    /// frame without bit 2 would put the C# arm on point deltas while the JS arm is on spans, and
    /// every case that ever pressed inside a span would part; one without bit 3 would have the C#
    /// arm REJECT every wrong key the script lands on a gap, holding a caret the browser moved; one
    /// without bit 4 would have it reject every mid-word space and advance past every gap typo,
    /// which is the same failure again. All three end the same way: every keystroke after the first
    /// such press lands on a different cell.</para>
    /// </summary>
    private static Replay Keystrokes(JsonElement keys, bool spaceSkipsWord, bool syllableTiming = true, bool wrongInputOnWordGaps = true, bool strictSpaces = true)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: spaceSkipsWord, syllableTiming: syllableTiming, wrongInputOnWordGaps: wrongInputOnWordGaps, strictSpaces: strictSpaces));

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

    private static readonly Lazy<JsonElement> browser_runs = new Lazy<JsonElement>(() => RunHarness("CoreFuzzHarness.cjs"));

    /// <summary>
    /// The browser's own answers for the syllabifier corpus, from the harness that also self-checks
    /// them against the game's pinned splits. Held here so the SAME words can be put to the game's
    /// real <see cref="Syllabifier"/>, which only this project can reference.
    /// </summary>
    private static readonly Lazy<JsonElement> browser_syllabifier = new Lazy<JsonElement>(() => RunHarness("CoreSyllabifierHarness.cjs"));

    /// <summary>
    /// Runs one of the Node harnesses against the served <c>wwwroot/js/typebeat-core.js</c> and
    /// parses its stdout as JSON. Node is optional on a dev box, so a missing node ignores the test
    /// rather than failing it (CI has node), which is the rule every other JS guard already applies.
    /// </summary>
    private static JsonElement RunHarness(string harnessFileName) => NodeHarness.Run(harnessFileName);

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

                    // The SYLLABLE groups (backlog 179), pinned here for the same reason the cells
                    // are: they are what a press on a grouped cell is judged against, so a group
                    // that drifted would surface as an account divergence and be blamed on the
                    // engine. This holds the whole derivation at once, the syllabifier's splits,
                    // the forced count on a subtimed word, the stylised gate that leaves
                    // "ohhh" ungrouped, the dropped groups and the monotonic clamp.
                    var browserGroups = browserLine.GetProperty("syllables");
                    Assert.That(browserGroups.GetArrayLength(), Is.EqualTo(line.Syllables.Count), $"{fixture.Name}[{i}]: syllable count");

                    for (int g = 0; g < line.Syllables.Count && g < browserGroups.GetArrayLength(); g++)
                    {
                        var group = line.Syllables[g];
                        var browserGroup = browserGroups[g];

                        Assert.That(browserGroup.GetProperty("startCell").GetInt32(), Is.EqualTo(group.StartCell), $"{fixture.Name}[{i}] syllable {g}: startCell");
                        Assert.That(browserGroup.GetProperty("endCellExclusive").GetInt32(), Is.EqualTo(group.EndCellExclusive), $"{fixture.Name}[{i}] syllable {g}: endCellExclusive");
                        Assert.That(browserGroup.GetProperty("startTime").GetDouble(), Is.EqualTo(group.StartTime), $"{fixture.Name}[{i}] syllable {g}: startTime");
                        Assert.That(browserGroup.GetProperty("endTime").GetDouble(), Is.EqualTo(group.EndTime), $"{fixture.Name}[{i}] syllable {g}: endTime");
                    }

                    // Membership is read through SyllableIndexOf and never by range, so it is pinned
                    // cell by cell: an ungrouped cell can sit positionally inside a group's range.
                    var browserMembership = browserLine.GetProperty("cellSyllable");
                    Assert.That(browserMembership.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{fixture.Name}[{i}]: cellSyllable length");

                    for (int c = 0; c < line.Cells.Count && c < browserMembership.GetArrayLength(); c++)
                        Assert.That(browserMembership[c].GetInt32(), Is.EqualTo(line.SyllableIndexOf(c)), $"{fixture.Name}[{i}][{c}]: syllable membership");
                }
            }
        });
    }

    /// <summary>
    /// COVERAGE, not behaviour: the sweep's authored-split fixture has to actually exercise the
    /// authored arm, or every case above would keep passing while both sides quietly derived.
    ///
    /// <para>The counter is the number of cells whose target time or syllable membership MOVES when
    /// the same fixture's <c>split_chars</c> are taken away, and it is asserted greater than zero.
    /// Both halves of the split are counted, because the whole point of the feature is that one cut
    /// drives the two together: a port that honoured the field for the targets and not for the
    /// groups (or the reverse) would show a smaller count here and part from the browser above.</para>
    /// </summary>
    [Test]
    public void TheAuthoredSplitFixtureReallyExercisesTheAuthoredArm()
    {
        var authored = TypingLine.FromLyricLine(Fixture("authoredSplit")[0], GranularityOf("authoredSplit"));
        var derived = TypingLine.FromLyricLine(WithoutSplits(Fixture("authoredSplit")[0]), GranularityOf("authoredSplit"));

        Assert.That(derived.Cells.Count, Is.EqualTo(authored.Cells.Count), "stripping the split must not change the cells themselves");

        int movedTargets = 0;
        int movedMembership = 0;

        for (int c = 0; c < authored.Cells.Count; c++)
        {
            if (authored.Cells[c].TargetTime != derived.Cells[c].TargetTime)
                movedTargets++;

            if (authored.SyllableIndexOf(c) != derived.SyllableIndexOf(c))
                movedMembership++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(movedTargets, Is.GreaterThan(0), "the authored split moved no target: the sweep stopped exercising it");
            Assert.That(movedMembership, Is.GreaterThan(0), "the authored split moved no cell into another group: the sweep stopped exercising it");
        });
    }

    /// <summary>The same line with every authored split taken away, i.e. as a pre-181 map carries it.</summary>
    private static LyricLine WithoutSplits(LyricLine line)
    {
        var units = new List<TimedUnit>(line.Units.Count);

        foreach (var unit in line.Units)
        {
            units.Add(new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = unit.SyllableBoundaries,
            });
        }

        return new LyricLine
        {
            RawText = line.RawText,
            StartTime = line.StartTime,
            EndTime = line.EndTime,
            SingEndTime = line.SingEndTime,
            Units = units,
            SealGraceMs = line.SealGraceMs,
            Estimated = line.Estimated,
        };
    }

    /// <summary>
    /// The browser's ported syllabifier answers exactly what the game's does, word for word, for
    /// every word of the game's own pinned corpus plus the stylised, junk and forced-count probes.
    ///
    /// <para><c>Typebeat.Web.Tests.SyllabifierParityTest</c> already holds the JS against the
    /// corpus TRANSCRIBED into the harness, which is what makes that guard runnable without the
    /// game repo. This is the other half, and the one the transcription cannot do: it calls the
    /// REAL <see cref="Syllabifier"/>, so a rule that changes on the C# side without the JS moving
    /// fails here rather than being copied into a stale transcription and passing.</para>
    /// </summary>
    [Test]
    public void TheTwoSyllabifiersAgreeWordForWord()
    {
        var root = browser_syllabifier.Value;
        var words = root.GetProperty("words");

        Assert.That(words.GetArrayLength(), Is.GreaterThan(100), "the corpus should be the whole pinned word list");

        Assert.Multiple(() =>
        {
            foreach (var entry in words.EnumerateArray())
            {
                string word = entry.GetProperty("word").GetString()!;

                Assert.That(Ints(entry, "splits"), Is.EqualTo(Syllabifier.SplitPoints(word).ToArray()), $"{word}: splits");
                Assert.That(entry.GetProperty("count").GetInt32(), Is.EqualTo(Syllabifier.CountSyllables(word)), $"{word}: count");
                Assert.That(entry.GetProperty("syllabifiable").GetBoolean(), Is.EqualTo(Syllabifier.IsSyllabifiable(word)), $"{word}: gate");
            }

            // The forced-count reconciliation too, across the whole 0..length+2 sweep the game's own
            // fixture runs: merge-weakest, add-best-split and the graceful over-forcing degrade.
            foreach (var entry in root.GetProperty("forced").EnumerateArray())
            {
                string word = entry.GetProperty("word").GetString()!;
                int forced = entry.GetProperty("forced").GetInt32();

                Assert.That(Ints(entry, "splits"), Is.EqualTo(Syllabifier.SplitPoints(word, forced).ToArray()), $"{word} forced {forced}");
            }
        });
    }

    private static int[] Ints(JsonElement element, string key)
    {
        var values = new List<int>();

        foreach (var value in element.GetProperty(key).EnumerateArray())
            values.Add(value.GetInt32());

        return values.ToArray();
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
        int skipPresses = 0, comboRestores = 0, backspaces = 0, passiveBreaks = 0, spanJudgements = 0, gapTypos = 0;
        int parkedGapTypos = 0, stepOvers = 0, midWordSpaceTypos = 0;

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
            passiveBreaks += browserCase.GetProperty("passiveBreaks").GetInt32();
            spanJudgements += browserCase.GetProperty("spanJudgements").GetInt32();
            gapTypos += browserCase.GetProperty("gapTypos").GetInt32();
            parkedGapTypos += browserCase.GetProperty("parkedGapTypos").GetInt32();
            stepOvers += browserCase.GetProperty("stepOvers").GetInt32();
            midWordSpaceTypos += browserCase.GetProperty("midWordSpaceTypos").GetInt32();
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

            // Backlog 176's own rule: a redeemable break landing at a streak of ZERO with a claim
            // already outstanding, which is the only case where whose claim it is gets decided. The
            // scripted cases guarantee this one, because thirty seeds per fixture never reached the
            // full shape (they reach the break, but not the walk back into the older cell that makes
            // the two rules differ), and a sweep that never reaches it cannot pin it.
            Assert.That(passiveBreaks, Is.GreaterThan(0), "no run took a redeemable break that had no streak to claim with");

            // Backlog 179's own rule: a press the SPAN decided rather than the point. Counted only
            // when the cell was in a group AND the span answer differed from the point answer, so a
            // fixture edit that ungrouped every token (or a port that quietly stopped grouping)
            // leaves both sides agreeing on point deltas, green, and no longer covering the rule.
            // That is the failure mode this counter exists for: it cannot be caught by comparing
            // the two arms, because both would be wrong in the same direction.
            Assert.That(spanJudgements, Is.GreaterThan(0), "no run judged a press against its syllable's span");

            // Backlog 181's own rule: a wrong letter landing IN a word gap. Counted on the CELLS
            // (a gap that became Wrong), which is exactly the set of presses whose outcome differs
            // from the strict rule, because the strict arm rejects them and writes nothing. Same
            // failure mode as the counter above: a generator that stopped rolling letters at gaps,
            // or a port that quietly went back to rejecting them, would leave both arms agreeing on
            // rejections, green, and no longer covering the rule.
            Assert.That(gapTypos, Is.GreaterThan(0), "no run typed a wrong letter into a word gap");

            // Backlog 184's own three rules, counted the same way and for the same reason. The park
            // and the step-over only exist on the skip arm of the matrix and the mid-word typo only
            // on the other one, so all three together say the sweep is exercising both halves of the
            // space discipline. Each is measured on the ENGINE's state around the press: a port that
            // went back to advancing past a gap typo, or to rejecting a mid-word space, would leave
            // both arms agreeing (the C# arm follows the same CONFIG bit), green, and covering
            // nothing.
            Assert.That(parkedGapTypos, Is.GreaterThan(0), "no run parked the caret on a gap it spoiled");
            Assert.That(stepOvers, Is.GreaterThan(0), "no run pressed the space that steps over a parked typo");
            Assert.That(midWordSpaceTypos, Is.GreaterThan(0), "no run typed a space into a lyric character");
        });
    }

    /// <summary>
    /// The ERA arm, which the browser does not have and therefore cannot prove: a replay whose
    /// CONFIG frame leaves bit 2 CLEAR re-derives under the classic POINT rule, and the identical
    /// keystrokes re-derive as spans when it is set.
    ///
    /// <para>Everything else in this fixture asserts the two clients AGREE; this one asserts the
    /// stored era still parts them, which is what keeps a pre-179 score reproducing the judgement
    /// its fingers actually earned. It is also the guard on the reconciliation the sweep above
    /// depends on: if the scorer ever stopped following the flag, every generated case would still
    /// pass (both arms on spans) while every old replay silently re-scored under the new rule.</para>
    ///
    /// <para>The keystrokes are the harness's <c>scripted/insideTheSpan</c> case, written out here
    /// rather than read from it so this test needs no node: every character is pressed well ahead
    /// of its own point target but inside the sung span of its syllable, which is exactly the shape
    /// the two rules disagree about.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameSyllableBitReDerivesTheClassicRule()
    {
        (double time, char key)[] keys =
        [
            (1000, 'c'), (1100, 'a'), (1200, 'k'), (1300, 'e'), (2400, ' '),
            (2500, 't'), (2600, 'o'), (3900, 'n'), (3910, 'i'), (3920, 'g'), (3930, 'h'), (3940, 't'),
            (5000, 'l'), (5100, 'i'), (5200, 't'), (6400, 't'), (6410, 'l'), (6420, 'e'), (6500, ' '),
            (6600, 'p'), (6700, 'e'), (6800, 'o'), (7900, 'p'), (7910, 'l'), (7920, 'e'),
        ];

        var spans = ScoreWithEra(keys, syllableTiming: true);
        var classic = ScoreWithEra(keys, syllableTiming: false);

        Assert.Multiple(() =>
        {
            // Under the live rule every press sits inside its syllable's span, so every one of them
            // is delta 0 and the map is typed clean.
            Assert.That(Wire(spans.Statistics), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 25 }), "syllable era: statistics");
            Assert.That(spans.Rank.ToString(), Is.EqualTo("X"), "syllable era: rank");

            // Under the classic rule the same fingers are graded on the distance to each CHARACTER,
            // which those presses are nowhere near.
            Assert.That(Wire(classic.Statistics).GetValueOrDefault("great"), Is.LessThan(25), "classic era: some presses must fall out of Great");
            Assert.That(classic.TotalScore, Is.LessThan(spans.TotalScore), "classic era: total score");
            Assert.That(classic.Accuracy, Is.LessThan(spans.Accuracy), "classic era: accuracy");
        });
    }

    /// <summary>
    /// The other ERA arm the browser does not have and cannot prove: a replay whose CONFIG frame
    /// leaves bit 3 CLEAR REJECTS a wrong letter pressed on a word gap, exactly as the run was
    /// played before backlog 181, while the identical keystrokes type it through when the bit is
    /// set.
    ///
    /// <para>Judgement relevant in the strongest sense there is: the two arms disagree about whether
    /// the CARET MOVED, so a single such frame decoded under the wrong arm desynchronises every
    /// keystroke after it. That is what the numbers below say. Under the live arm the typo consumes
    /// the gap and "cd" lands on its own two cells, leaving one unfixed typo and a completion of
    /// 4/5; under the stored arm the caret never leaves the gap, so the 'c' and the 'd' are rejected
    /// there in turn and the line seals with three characters nobody typed.</para>
    ///
    /// <para>It is also the guard on the reconciliation the sweep above depends on: if the scorer
    /// ever stopped following the flag, every generated case would still pass (both arms typing gap
    /// typos through) while every old replay silently re-scored under a model its player never
    /// touched.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameWordGapBitRejectsTheTypoInstead()
    {
        // The harness's scripted/gapTypoUnfixed case, written out here rather than read from it so
        // this test needs no node: "ab cd" typed clean up to the word gap, a wrong letter on the
        // gap, then the second word pressed on its own targets.
        (double time, char key)[] keys = [(1000, 'a'), (1500, 'b'), (2000, 'x'), (2000, 'c'), (2500, 'd')];

        var through = ScoreGapTypo(keys, wrongInputOnWordGaps: true);
        var strict = ScoreGapTypo(keys, wrongInputOnWordGaps: false);

        Assert.Multiple(() =>
        {
            var live = Wire(through.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(4), "live era: the four lyric characters");
            Assert.That(live.GetValueOrDefault(WireCounts.Key(TypeBeatResultMapping.UNFIXED_TYPO)), Is.EqualTo(1), "live era: the gap is an unfixed typo");
            Assert.That(live.GetValueOrDefault("miss"), Is.Zero, "live era: a typo is not a miss");
            Assert.That(through.Completion, Is.EqualTo(4 / 5.0).Within(1e-9), "live era: completion");

            var stored = Wire(strict.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(2), "stored era: only the cells before the gap");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(3), "stored era: the caret never left the gap, so nothing after it was typed");
            Assert.That(strict.Completion, Is.EqualTo(2 / 5.0).Within(1e-9), "stored era: completion");
            Assert.That(strict.TotalScore, Is.LessThan(through.TotalScore), "stored era: total score");
        });
    }

    /// <summary>
    /// The THIRD era arm the browser does not have and cannot prove (backlog 184): a replay whose
    /// CONFIG frame leaves bit 4 CLEAR REJECTS a space pressed inside a word, exactly as the run was
    /// played before that task, while the identical keystrokes type it through when the bit is set.
    ///
    /// <para>Judgement relevant in the same strongest sense as the word-gap bit above: the two arms
    /// disagree about whether the CARET MOVED. Under the stored arm the space is refused, the 'b'
    /// then lands on its own cell and the line is typed out but for its last character, which seals
    /// as a miss (4/5). Under the live arm the space takes the 'b' cell, so everything after it is
    /// one cell out and every remaining press is a typo of its own (1/5).</para>
    ///
    /// <para>With word skipping OFF, which is what the browser hardcodes, the bit's other half (the
    /// gap-typo park) is inert by construction: that half is scoped to the skip setting, because the
    /// skip gate is what a spoiled gap used to be fed to. It is pinned on the game side, where the
    /// setting exists (<c>SpaceDisciplineTest</c>).</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameStrictSpaceBitRejectsTheMidWordSpaceInstead()
    {
        // "ab cd" with a space fumbled onto the 'b': then the 'b' itself, the real word gap, and the
        // 'c', each pressed on its own target, so the only thing that moves between the two arms is
        // where that first space left the caret.
        (double time, char key)[] keys = [(1000, 'a'), (1500, ' '), (1500, 'b'), (2000, ' '), (2000, 'c')];

        var through = ScoreSpaceDiscipline(keys, strictSpaces: true);
        var strict = ScoreSpaceDiscipline(keys, strictSpaces: false);

        Assert.Multiple(() =>
        {
            var live = Wire(through.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(1), "live era: only the 'a', pressed before the fumble");
            Assert.That(live.GetValueOrDefault(WireCounts.Key(TypeBeatResultMapping.UNFIXED_TYPO)), Is.EqualTo(4), "live era: every later press landed one cell early");
            Assert.That(live.GetValueOrDefault("miss"), Is.Zero, "live era: the line was typed out, wrongly");
            Assert.That(through.Completion, Is.EqualTo(1 / 5.0).Within(1e-9), "live era: completion");

            var stored = Wire(strict.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(4), "stored era: the space was refused, so nothing after it moved");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(1), "stored era: the 'd' the run never reached");
            Assert.That(strict.Completion, Is.EqualTo(4 / 5.0).Within(1e-9), "stored era: completion");
            Assert.That(through.TotalScore, Is.LessThan(strict.TotalScore), "live era: a typed-through space costs more than a refused one");
        });
    }

    private static TypeBeatReplayAccount ScoreGapTypo((double time, char key)[] keys, bool wrongInputOnWordGaps)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: wrongInputOnWordGaps, strictSpaces: true));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("abCd"), Fixture("abCd")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    private static TypeBeatReplayAccount ScoreSpaceDiscipline((double time, char key)[] keys, bool strictSpaces)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: strictSpaces));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("abCd"), Fixture("abCd")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    private static TypeBeatReplayAccount ScoreWithEra((double time, char key)[] keys, bool syllableTiming)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: syllableTiming));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("syllableWords"), Fixture("syllableWords")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }
}
