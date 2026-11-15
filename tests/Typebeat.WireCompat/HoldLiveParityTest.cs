using System.Reflection;
using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
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
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using JudgedCells = Typebeat.Web.Packages.Lyrics.JudgedCells;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on HOLDS in the browser player: <c>typebeat-core.js</c>'s port of the game's
/// held letters (<see cref="TypingEngine"/>'s hold half, <c>HeldWord</c>, <see cref="HoldTicks"/>,
/// <see cref="HoldTailRule"/>, the hold tails and ticks the score and health processors take) against
/// the GAME's own <see cref="TypeBeatReplayScorer"/> and <see cref="TypeBeatHealthProcessor"/>, over
/// generated and scripted streams that carry KEY RELEASES, field for field and with no tolerance.
///
/// <para>It is <see cref="EngineFuzzLiveParityTest"/>'s shape with the fixtures taken one step
/// closer to what a player is served. Each map below is a timing.json document written through the
/// game's own <see cref="LyricOsuFormat.GenerateOsu"/>, the encoder an upload is made by, and that
/// one .osu text is read by BOTH loaders: the browser's <c>parseLyricOsu</c> / <c>buildBeatmap</c>
/// inside <c>CoreHoldsHarness.cjs</c>, and the game's <see cref="LyricBeatmapDecoder"/> here. So the
/// hold fields are pinned from the stored bytes, and <see cref="TheTwoLoadersAgreeOnEveryHoldFixture"/>
/// holds every cell's hold span, ticks and hand-off against the game's, beside the target and
/// expected character the other loader pins hold.</para>
///
/// <para>The C# arm is fed under every LIVE era, because the browser selects none: the same CONFIG
/// and extended CONFIG frames <see cref="EngineFuzzLiveParityTest"/> feeds (see its
/// <c>Keystrokes</c> for the bit by bit argument). A hold needs no era bit of its own: no replay was
/// ever recorded on a map with holds under another rule (<see cref="TypingCell.HandsOffFrom"/>), and
/// a release travels as its own frame (<see cref="TypeBeatReplayFrame.RELEASE"/>), which
/// <see cref="ReplayEngineFeed.Apply"/> plays through <see cref="TypingEngine.ProcessRelease"/>.</para>
///
/// <para>What the streams carry, and why both INPUT MODES matter, is written at the top of the
/// harness. In short: releases at every grade, at or past the end, or never; the live key handler's
/// release-on-another-key discipline, and the raw shape (autoplay's) that reaches the engine's own
/// closes instead. The scripted cases pin the rules a seed might not roll the deciding shape for, and
/// <see cref="TheHoldSweepReachesTheRulesItIsMeantTo"/> proves the sweep reaches each of them.</para>
/// </summary>
[TestFixture]
public class HoldLiveParityTest
{
    #region Fixtures

    /// <summary>
    /// The maps, as timing.json documents. Each is built to reach a named part of the rules (see the
    /// per-map notes), and several carry holds no play honours beside the ones it does, so the
    /// loaders are held to IGNORING the same holds too.
    /// </summary>
    private static readonly Dictionary<string, string> timing = new Dictionary<string, string>
    {
        // Three lines of everything ordinary: a hold with ticks (one on a half millisecond, which the
        // game rounds to even, and one past the window, which it drops) handing off to the rest of
        // its word; a word's LAST letter held to the word's end (no hand-off); a first-letter hold on
        // a capital; a hold too short, one overlapping an earlier one, one past the word's end and one
        // ending before its letter is due, all ignored; a hold before a hyphen (which the default
        // stream types as a space, so nothing is handed off) and one inside "don't" (whose apostrophe
        // the stream drops, so the hand-off skips it).
        ["lovesong"] = """
            {"version": 2, "song_end_ms": 16000, "lines": [
              {"text": "love me tender", "start_ms": 1000, "end_ms": 4600, "words": [
                {"text": "love", "start_ms": 1000, "end_ms": 2200, "score": 1, "holds": [{"char": 1, "start_ms": 1300, "end_ms": 1900, "ticks": [1450, 1600.5, 1750, 1950]}]},
                {"text": "me", "start_ms": 2200, "end_ms": 3000, "score": 1, "holds": [{"char": 1, "start_ms": 2600, "end_ms": 3000}]},
                {"text": "tender", "start_ms": 3000, "end_ms": 4600, "score": 1, "holds": [{"char": 9, "start_ms": 4300, "end_ms": 4500}, {"char": 2, "start_ms": 3400, "end_ms": 4200, "ticks": [3700]}, {"char": 5, "start_ms": 4200, "end_ms": 4320}]}]},
              {"text": "Oh hold on", "start_ms": 6000, "end_ms": 9000, "words": [
                {"text": "Oh", "start_ms": 6000, "end_ms": 6800, "score": 1, "holds": [{"char": 0, "start_ms": 6000, "end_ms": 6500, "ticks": [6200]}]},
                {"text": "hold", "start_ms": 6800, "end_ms": 8000, "score": 1, "holds": [{"char": 1, "start_ms": 7000, "end_ms": 7050}, {"char": 2, "start_ms": 7100, "end_ms": 7900}, {"char": 3, "start_ms": 7500, "end_ms": 7800}]},
                {"text": "on", "start_ms": 8000, "end_ms": 9000, "score": 1, "holds": [{"char": 1, "start_ms": 8400, "end_ms": 9000, "ticks": [8700, 8800]}]}]},
              {"text": "bad-cat don't", "start_ms": 10000, "end_ms": 13000, "words": [
                {"text": "bad-cat", "start_ms": 10000, "end_ms": 11400, "score": 1, "holds": [{"char": 2, "start_ms": 10400, "end_ms": 10690}]},
                {"text": "don't", "start_ms": 11400, "end_ms": 13000, "score": 1, "holds": [{"char": 2, "start_ms": 12000, "end_ms": 12500, "ticks": [12100]}]}]}
            ]}
            """,

        // SUBDIVIDED words with no split_chars, so the game's nested scoring objects (built on the
        // legacy target era) and its live cells (on the aligned one) disagree on targets, with ticks
        // among them and a tick on the very instant of a cell, in a line long enough (over sixteen
        // objects) for List.Sort's quicksort arm: the simulated maximum run's order is decided by
        // exactly that, so the total score is only right if the browser reproduces it.
        ["subdividedTicks"] = """
            {"version": 2, "song_end_ms": 9000, "lines": [
              {"text": "beautiful tonight", "start_ms": 1000, "end_ms": 4600, "words": [
                {"text": "beautiful", "start_ms": 1000, "end_ms": 3000, "score": 1, "syllables": [{"start_ms": 1000}, {"start_ms": 1800}, {"start_ms": 2400}],
                 "holds": [{"char": 0, "start_ms": 1000, "end_ms": 1250, "ticks": [1000, 1100]}, {"char": 3, "start_ms": 1300, "end_ms": 1800, "ticks": [1400, 1533, 1600, 1700]}]},
                {"text": "tonight", "start_ms": 3000, "end_ms": 4600, "score": 1, "syllables": [{"start_ms": 3000}, {"start_ms": 3500}, {"start_ms": 4000}],
                 "holds": [{"char": 5, "start_ms": 4100, "end_ms": 4500, "ticks": [4200, 4300]}]}]},
              {"text": "forever", "start_ms": 5000, "end_ms": 6500, "words": [
                {"text": "forever", "start_ms": 5000, "end_ms": 6500, "score": 1, "holds": [{"char": 3, "start_ms": 5600, "end_ms": 6200, "ticks": [5700, 5800, 5900, 6000]}]}]}
            ]}
            """,

        // A held LAST letter whose ticks sit on the very instants of the word's earlier letters, in a
        // line of eighteen objects: .NET's List.Sort is not stable, and on this line its quicksort
        // arm leaves two of the tied ticks in a different order from a stable sort, which moves a
        // 30 and a 300 between two positions of the simulated maximum run.
        ["tiedTicks"] = """
            {"version": 2, "song_end_ms": 6000, "lines": [
              {"text": "go abcdefgh", "start_ms": 500, "end_ms": 1800, "words": [
                {"text": "go", "start_ms": 500, "end_ms": 1000, "score": 1},
                {"text": "abcdefgh", "start_ms": 1000, "end_ms": 1800, "score": 1, "holds": [{"char": 7, "start_ms": 1050, "end_ms": 1800, "ticks": [1100, 1200, 1300, 1400, 1500, 1600]}]}]},
              {"text": "and stay", "start_ms": 2500, "end_ms": 4000, "words": [
                {"text": "and", "start_ms": 2500, "end_ms": 3000, "score": 1},
                {"text": "stay", "start_ms": 3000, "end_ms": 4000, "score": 1, "holds": [{"char": 2, "start_ms": 3400, "end_ms": 3900, "ticks": [3500, 3600]}]}]}
            ]}
            """,

        // The FINAL line, two holds on its one word: typed out with a hold still down it cannot
        // finish early, which is the one place an open hold reaches a seal decision.
        ["finalHold"] = """
            {"version": 2, "song_end_ms": 4600, "lines": [
              {"text": "say", "start_ms": 1000, "end_ms": 2000, "words": [{"text": "say", "start_ms": 1000, "end_ms": 2000, "score": 1}]},
              {"text": "yeah", "start_ms": 3000, "end_ms": 4000, "words": [
                {"text": "yeah", "start_ms": 3000, "end_ms": 4000, "score": 1, "holds": [{"char": 1, "start_ms": 3300, "end_ms": 3650}, {"char": 3, "start_ms": 3750, "end_ms": 4000, "ticks": [3800, 3850]}]}]}
            ]}
            """,

        // A line's last letter held to the line's own end, with the next line open to a rushing
        // caret: the hold can still be down while the player types on another line.
        ["crossLine"] = """
            {"version": 2, "song_end_ms": 8000, "lines": [
              {"text": "hey", "start_ms": 1000, "end_ms": 2000, "words": [{"text": "hey", "start_ms": 1000, "end_ms": 2000, "score": 1, "holds": [{"char": 2, "start_ms": 1600, "end_ms": 2000, "ticks": [1700, 1800, 1900]}]}]},
              {"text": "now you", "start_ms": 2000, "end_ms": 3500, "words": [
                {"text": "now", "start_ms": 2000, "end_ms": 2800, "score": 1},
                {"text": "you", "start_ms": 2800, "end_ms": 3500, "score": 1, "holds": [{"char": 1, "start_ms": 3000, "end_ms": 3400}]}]}
            ]}
            """,

        // HeldPausedWordParityTest's map: holds either side of a rest (the first letter handing off to
        // nothing, since the next one opens the stretch after the rest), and a hold through a rest,
        // which no play honours.
        ["pausedHold"] = """
            {"version": 2, "song_end_ms": 30000, "lines": [
              {"text": "please go", "start_ms": 1000, "end_ms": 5000, "words": [
                {"text": "please", "start_ms": 1000, "end_ms": 4000, "score": 1, "pauses": [{"start_ms": 2000, "end_ms": 2500, "split": 3}],
                 "holds": [{"char": 2, "start_ms": 1700, "end_ms": 2000}, {"char": 3, "start_ms": 2500, "end_ms": 2900, "ticks": [2700]}]},
                {"text": "go", "start_ms": 4000, "end_ms": 5000, "score": 1}]},
              {"text": "please go", "start_ms": 11000, "end_ms": 15000, "words": [
                {"text": "please", "start_ms": 11000, "end_ms": 14000, "score": 1, "pauses": [{"start_ms": 12000, "end_ms": 12500, "split": 3}],
                 "holds": [{"char": 2, "start_ms": 11700, "end_ms": 12700}]},
                {"text": "go", "start_ms": 14000, "end_ms": 15000, "score": 1}]}
            ]}
            """,
    };

    /// <summary>How many held cells (usable holds) each map has, so a map cannot drift into holding nothing.</summary>
    private static readonly Dictionary<string, int> held_cells = new Dictionary<string, int>
    {
        ["lovesong"] = 8,
        ["subdividedTicks"] = 4,
        ["tiedTicks"] = 2,
        ["finalHold"] = 2,
        ["crossLine"] = 2,
        ["pausedHold"] = 2,
    };

    private static readonly Lazy<Dictionary<string, string>> osu = new Lazy<Dictionary<string, string>>(() =>
        timing.ToDictionary(kvp => kvp.Key, kvp => LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", kvp.Value)));

    private static Beatmap decode(string name)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu.Value[name]));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    /// <summary>The playable beatmap a client builds from the .osu, with no mods: the nested per-cell, tail and tick objects built.</summary>
    private static IBeatmap playable(string name)
        => new FlatWorkingBeatmap(decode(name)).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, Array.Empty<Mod>());

    private static IReadOnlyList<LyricLine> lyricLines(IBeatmap map)
        => map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    #endregion

    #region The browser side

    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-holds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(osu.Value));

        try
        {
            return NodeHarness.Run("CoreHoldsHarness.cjs", path);
        }
        finally
        {
            File.Delete(path);
        }
    });

    #endregion

    #region Driving the game

    /// <summary>
    /// The stream as a replay, headed by the LIVE CONFIG frames (<see cref="EngineFuzzLiveParityTest"/>'s
    /// <c>Keystrokes</c>, every bit argued there). Releases are their own frames, as the desktop
    /// records them.
    /// </summary>
    private static Replay keystrokes(JsonElement keys, bool spaceSkipsWord)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: spaceSkipsWord, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true,
            charTimedStretch: true, flexibleLines: true, boundedRush: true, firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true,
            manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true,
            earlyFinish: true, keyTimeJudgement: true, bestRetypeJudgement: true, stacksDisplacedClaims: true));

        foreach (var key in keys.EnumerateArray())
            replay.Frames.Add(new TypeBeatReplayFrame(key[0].GetDouble(), key[1].GetString()![0]));

        return replay;
    }

    private static TypeBeatReplayAccount score(JsonElement browserCase)
        => TypeBeatReplayScorer.Score(playable(browserCase.GetProperty("fixture").GetString()!), Array.Empty<Mod>(),
            keystrokes(browserCase.GetProperty("keys"), browserCase.GetProperty("spaceSkipsWord").GetBoolean()),
            TypoRule.Deferred, ComboRestoreRule.OnFix);

    /// <summary>The rank the game's own ladder gives the derived run (<see cref="EngineFuzzLiveParityTest"/>'s <c>RankOf</c>, and why).</summary>
    private static ScoreRank rankOf(TypeBeatReplayAccount game)
        => game.Rank == ScoreRank.F ? ScoreRank.F : TypeBeatScoreProcessor.RankFromStatistics(game.Accuracy, game.Statistics);

    #endregion

    /// <summary>
    /// The two loaders resolve every hold fixture to the same cells, and above all to the same HOLD
    /// fields: each cell's span (<see cref="TypingCell.HoldStart"/> / <see cref="TypingCell.HoldEnd"/>),
    /// its usable ticks (<see cref="TypingCell.HoldTickTimes"/>, rounded and windowed by
    /// <see cref="HoldTicks.UsableTicks"/>), and the hand-off (<see cref="TypingCell.HandsOffFrom"/>,
    /// with the target it floors). Checked first in intent, so a loader drift cannot reach the account
    /// comparison wearing the engine's clothes. Held against the LIVE era's cells
    /// (<see cref="TypingLine.FromLyricLine"/> with aligned targets), which is what the browser plays.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryHoldFixture()
    {
        var fixtures = browser.Value.GetProperty("fixtures");

        Assert.Multiple(() =>
        {
            foreach (string name in timing.Keys)
            {
                var lines = lyricLines(playable(name));
                var browserLines = fixtures.GetProperty(name).GetProperty("lines");
                int held = 0;

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Count), $"{name}: line count");

                for (int i = 0; i < lines.Count && i < browserLines.GetArrayLength(); i++)
                {
                    var line = TypingLine.FromLyricLine(lines[i], alignSubdivisionTargets: true);
                    var browserLine = browserLines[i];

                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{name}[{i}]: endTime");
                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{name}[{i}]: activationTime");
                    Assert.That(browserLine.GetProperty("firstVocalTime").GetDouble(), Is.EqualTo(line.FirstVocalTime), $"{name}[{i}]: firstVocalTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{name}[{i}]: sealGraceMs");

                    var browserCells = browserLine.GetProperty("cells");
                    Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{name}[{i}]: cell count");

                    var membership = browserLine.GetProperty("cellSyllable");

                    for (int c = 0; c < line.Cells.Count && c < browserCells.GetArrayLength(); c++)
                    {
                        var cell = line.Cells[c];
                        var browserCell = browserCells[c];
                        string at = $"{name}[{i}][{c}]";

                        Assert.That(browserCell.GetProperty("expected").GetString(), Is.EqualTo(cell.Expected.ToString()), $"{at}: expected");
                        Assert.That(browserCell.GetProperty("target").GetDouble(), Is.EqualTo(cell.TargetTime), $"{at}: target");
                        Assert.That(nullableDouble(browserCell, "holdStart"), Is.EqualTo(cell.HoldStart), $"{at}: HoldStart");
                        Assert.That(nullableDouble(browserCell, "holdEnd"), Is.EqualTo(cell.HoldEnd), $"{at}: HoldEnd");
                        Assert.That(nullableInt(browserCell, "handsOffFrom"), Is.EqualTo(cell.HandsOffFrom), $"{at}: HandsOffFrom");
                        Assert.That(nullableDoubles(browserCell, "holdTickTimes"), Is.EqualTo(cell.HoldTickTimes?.ToArray()), $"{at}: HoldTickTimes");
                        Assert.That(membership[c].GetInt32(), Is.EqualTo(line.AuthoredGrouping.IndexOf(c)), $"{at}: syllable membership");

                        if (cell.HoldEnd != null)
                            held++;
                    }
                }

                Assert.That(held, Is.EqualTo(held_cells[name]), $"{name}: the usable holds the map was built with");
            }
        });
    }

    /// <summary>
    /// The browser's whole-map denominators are the game's: <c>maximum_statistics</c> (a Great per cell
    /// and per hold tail, a LargeTickHit per tick) and the SIMULATED MAXIMUM RUN's combo portion, which
    /// is read off the game's own <see cref="ScoreProcessor"/> after <see cref="ScoreProcessor.ApplyBeatmap"/>.
    /// That portion weighs every object by its position in the run, so it is only right if the
    /// browser orders a line's ticks among its cells exactly as the game's simulation does: by the
    /// nested objects' StartTime (the LEGACY era's targets), through .NET's unstable List.Sort. The
    /// subdivided fixture is built so the order matters, and that is asserted too, against the
    /// browser's own naive reading.
    /// </summary>
    [Test]
    public void TheMaximumRunIsTheGamesOnEveryHoldFixture()
    {
        var fixtures = browser.Value.GetProperty("fixtures");
        var maximumComboPortion = typeof(ScoreProcessor).GetField("maximumComboPortion", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Multiple(() =>
        {
            foreach (string name in timing.Keys)
            {
                var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
                processor.Mods.Value = Array.Empty<Mod>();
                processor.ApplyBeatmap(playable(name));

                var info = new ScoreInfo { Ruleset = new TypeBeatRuleset().RulesetInfo };
                processor.PopulateScore(info);

                var browserFixture = fixtures.GetProperty(name);

                Assert.That(HealthLiveParityTest.Dict(browserFixture, "maximumStatistics"), Is.EquivalentTo(HealthLiveParityTest.Wire(info.MaximumStatistics.Where(kvp => kvp.Value != 0).ToDictionary())), $"{name}: maximum_statistics");
                Assert.That(browserFixture.GetProperty("maxComboPortion").GetDouble(), Is.EqualTo((double)maximumComboPortion.GetValue(processor)!), $"{name}: the simulated maximum's combo portion");

                // And the SERVER's count of the same bytes, which /play/submit holds the claimed
                // maximum to (ScoreSubmission.MaximumMatchesMap): a browser play of a hold map is
                // refused unless the two agree on every tail and tick.
                var server = BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu.Value[name]));
                Assert.That(ScoringContract.CountAccuracyAffecting(HealthLiveParityTest.Dict(browserFixture, "maximumStatistics")),
                    Is.EqualTo(JudgedCells.Count(server.Lines, literate: false)), $"{name}: the server's judged cells");
            }

            // Non-vacuity: each of the two fixtures built for it still makes the order decide something,
            // the legacy targets on the one and the unstable sort on the other.
            foreach (string name in new[] { "subdividedTicks", "tiedTicks" })
            {
                var fixture = fixtures.GetProperty(name);
                Assert.That(fixture.GetProperty("maxComboPortion").GetDouble(), Is.Not.EqualTo(fixture.GetProperty("naiveMaxComboPortion").GetDouble()),
                    $"{name} no longer makes the order of the maximum run decide anything");
            }
        });
    }

    /// <summary>
    /// Every generated and scripted hold run's SUBMITTED account, browser against game, field for
    /// field with no tolerance (<see cref="EngineFuzzLiveParityTest.EveryGeneratedRunAgrees"/>'s
    /// comparison, on maps with holds and streams with releases).
    /// </summary>
    [Test]
    public void EveryGeneratedHoldRunAgrees()
    {
        var cases = browser.Value.GetProperty("cases");

        Assert.That(cases.GetArrayLength(), Is.GreaterThan(500), "the harness should be generating a real sweep");

        Assert.Multiple(() =>
        {
            foreach (var browserCase in cases.EnumerateArray())
            {
                string scenario = browserCase.GetProperty("name").GetString()!;
                var game = score(browserCase);
                var submitted = browserCase.GetProperty("submitted");

                Assert.That(game.UnconsumedFrames, Is.Zero, $"{scenario}: the game never reached some of the recorded frames");
                Assert.That(HealthLiveParityTest.Dict(submitted, "statistics"), Is.EquivalentTo(HealthLiveParityTest.Wire(game.Statistics)), $"{scenario}: statistics");
                Assert.That(HealthLiveParityTest.Dict(submitted, "maximumStatistics"), Is.EquivalentTo(HealthLiveParityTest.Wire(game.MaximumStatistics)), $"{scenario}: maximum_statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.MaxCombo), $"{scenario}: max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.TotalScore), $"{scenario}: total_score");
                Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Accuracy), $"{scenario}: accuracy");
                Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(game.Completion), $"{scenario}: completion");
                Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(rankOf(game).ToString()), $"{scenario}: rank");
                Assert.That(browserCase.GetProperty("totalScoreMismatches").GetInt32(), Is.Zero, $"{scenario}: the HUD's running total parted from the result's");
            }
        });
    }

    #region The health arm

    private sealed record HealthRun(List<double> AfterKeys, double AtEnd, double Min, int FailRequests, double? FirstFailRequestAt, ScoreInfo Account);

    /// <summary>
    /// One case through <see cref="LiveHealthArm"/> (whose hold seams are the playfield's) under NO
    /// FAIL, fed as <see cref="TypeBeatReplayScorer"/> feeds its engine, a release as
    /// <see cref="TypingEngine.ProcessRelease"/> (<see cref="ReplayEngineFeed.Apply"/>'s arm).
    /// </summary>
    private static HealthRun playHealth(JsonElement browserCase)
    {
        string fixture = browserCase.GetProperty("fixture").GetString()!;
        var map = playable(fixture);
        var engine = HealthLiveParityTest.LiveEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = lyricLines(map).ToList(),
            Granularity = map.HitObjects.OfType<TypeBeatHitObject>().First().Granularity,
        }, browserCase.GetProperty("spaceSkipsWord").GetBoolean());

        using var arm = new LiveHealthArm(map, engine, noFail: true);

        double clock = 0;
        double? firstFailRequestAt = null;
        arm.FailRequested += () => firstFailRequestAt ??= clock;

        var frames = browserCase.GetProperty("keys").EnumerateArray().Select(k => (time: k[0].GetDouble(), key: k[1].GetString()![0])).ToList();
        var afterKeys = new List<double>();
        double min = 1;

        double end = engine.Lines.Count > 0 ? engine.Lines[^1].EndTime + engine.Lines[^1].SealGraceMs + 10000 : 10000;
        if (frames.Count > 0)
            end = Math.Max(end, frames[^1].time + 10000);

        void apply((double time, char key) frame)
        {
            clock = frame.time;
            engine.Update(frame.time);

            if (frame.key == TypeBeatReplayFrame.BACKSPACE)
                engine.ProcessBackspace();
            else if (frame.key == TypeBeatReplayFrame.ENTER)
                engine.ProcessEnter(frame.time);
            else if (frame.key == TypeBeatReplayFrame.RELEASE)
                engine.ProcessRelease(frame.time);
            else
                engine.ProcessKey(frame.key, frame.time);

            afterKeys.Add(arm.Health.Health.Value);
            min = Math.Min(min, arm.Health.Health.Value);
        }

        int next = 0;

        for (double now = 0; now <= end; now += ReplayEngineFeed.FRAME_MS)
        {
            while (next < frames.Count && frames[next].time <= now)
                apply(frames[next++]);

            clock = now;
            engine.Update(now);
            min = Math.Min(min, arm.Health.Health.Value);
        }

        while (next < frames.Count)
            apply(frames[next++]);

        clock = end;
        engine.Update(end);
        min = Math.Min(min, arm.Health.Health.Value);

        return new HealthRun(afterKeys, arm.Health.Health.Value, min, arm.FailRequests, firstFailRequestAt, arm.Conclude());
    }

    /// <summary>
    /// THE HEALTH ARM: every hold run's HP bar, browser against the desktop's real
    /// <see cref="TypeBeatHealthProcessor"/>, after every frame (releases included) and at the end, to
    /// the last bit: a tick hit heals like a Great and a tick miss drains half a Miss, a tail takes a
    /// cell's grade, and the parts of a hold that never opened move nothing. The arm's own account is
    /// held against the browser's too, so its hold seams are proven on the score as well.
    /// </summary>
    [Test]
    public void EveryHoldRunDrainsTheSameBar()
    {
        var cases = browser.Value.GetProperty("cases");

        Assert.Multiple(() =>
        {
            foreach (var browserCase in cases.EnumerateArray())
            {
                string scenario = browserCase.GetProperty("name").GetString()!;
                var game = playHealth(browserCase);
                var health = browserCase.GetProperty("health");

                Assert.That(health.GetProperty("afterKeys").EnumerateArray().Select(v => v.GetDouble()).ToList(), Is.EqualTo(game.AfterKeys), $"{scenario}: the bar after every frame");
                Assert.That(health.GetProperty("atEnd").GetDouble(), Is.EqualTo(game.AtEnd), $"{scenario}: the bar at the end");
                Assert.That(health.GetProperty("min").GetDouble(), Is.EqualTo(game.Min), $"{scenario}: the lowest the bar went");
                Assert.That(health.GetProperty("failRequests").GetInt32(), Is.EqualTo(game.FailRequests), $"{scenario}: fails the veto refused");

                var first = health.GetProperty("firstFailRequestAt");
                Assert.That(first.ValueKind == JsonValueKind.Null ? (double?)null : first.GetDouble(), Is.EqualTo(game.FirstFailRequestAt), $"{scenario}: the first refused fail");

                var submitted = browserCase.GetProperty("submitted");
                Assert.That(HealthLiveParityTest.Dict(submitted, "statistics"), Is.EquivalentTo(HealthLiveParityTest.Wire(game.Account.Statistics)), $"{scenario}: the arm's statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.Account.MaxCombo), $"{scenario}: the arm's max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.Account.TotalScore), $"{scenario}: the arm's total_score");
            }
        });
    }

    #endregion

    /// <summary>
    /// The sweep is only worth what it exercises, so this pins its SHAPE, read off the browser
    /// engine's own answers (CoreHoldsHarness.cjs counts each on the engine, never on the script):
    /// every tail grade a release can earn, a hold completing at its end, releases taken and inert,
    /// tick hits, misses, the one break a run of missed ticks takes, and the neutral misses of a hold
    /// that never opened; a hand-off closing its hold and one judged on time where the span would
    /// not have; a late head missing its tail and one crediting the ticks already due; a head
    /// releasing another open hold; a retyped head left alone; the final line held off its early
    /// finish; and combo credited on a held line the caret has left.
    /// </summary>
    [Test]
    public void TheHoldSweepReachesTheRulesItIsMeantTo()
    {
        var totals = new Dictionary<string, int>();

        foreach (var browserCase in browser.Value.GetProperty("cases").EnumerateArray())
        {
            foreach (var counter in browserCase.GetProperty("coverage").EnumerateObject())
                totals[counter.Name] = totals.GetValueOrDefault(counter.Name) + counter.Value.GetInt32();
        }

        Assert.Multiple(() =>
        {
            foreach (string counter in new[]
                     {
                         "tailGreat", "tailOk", "tailMeh", "tailMiss", "tailsAtTheirEnd", "sealTails", "tickHits", "tickMisses", "tickBreaks", "neutralTicks",
                         "releasesTaken", "releasesInert", "handoffCloses", "handoffOnTime", "lateHeads", "lateHeadTicks", "holdsReleasedByAHead", "retypedHeads",
                         "earlyFinishHeldOff", "creditsOffTheActiveLine", "holdsOpened",
                     })
            {
                Assert.That(totals.GetValueOrDefault(counter), Is.GreaterThan(0), $"the hold sweep never reached {counter}");
            }
        });
    }

    private static double? nullableDouble(JsonElement element, string key)
        => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetDouble();

    private static int? nullableInt(JsonElement element, string key)
        => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetInt32();

    private static double[]? nullableDoubles(JsonElement element, string key)
        => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
}
