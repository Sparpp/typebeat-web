using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the HOLD LEAD-IN (backlog 494): a held letter is due at its hold's start, and
/// the letters leading into it from the start of its syllable segment are re-spread to meet it. The game
/// gates the rule on <see cref="TypingEngine.HoldLeadIn"/> (bit 15 of the second CONFIG word, set for every
/// live stack, clear on every replay recorded before it); the browser plays only live and takes it by
/// default, with <c>buildBeatmap</c>'s <c>holdLeadIn</c> as the same flag for the stored-era arm.
///
/// <para>Both arms are held here, field for field and with no tolerance, through
/// <c>CoreHoldLeadInHarness.cjs</c>: every line's cells and the line fields the targets feed, for the
/// default stream and Literate, against <see cref="TypingLine.FromLyricLine"/> with and without the era;
/// and scripted runs (each letter on one era's targets, shifted by an offset) played by the browser under
/// each era, against the game's <see cref="TypeBeatReplayScorer"/> under the header that era writes.</para>
///
/// <para>The fixtures are <see cref="HoldLiveParityTest"/>'s kind: timing.json documents written through
/// the game's own <see cref="LyricOsuFormat.GenerateOsu"/> and read by both loaders. "brainPower" is the
/// reported case, NOMA - Brain Power (SamuelisHere) [Insane], with its own numbers; "edges" reaches the
/// rule's edges: two holds in one segment, a held first letter of a segment starting before the letter
/// ahead of it, a paused word whose hold pulls its letter later, an apostrophe inside a lead-in, and a
/// stylised word ("meeeee"), which has no syllable group and so is judged on its point targets: under the
/// span rule every live run plays, that is where the browser's two arms part on a judgement rather than
/// only on a cue.</para>
/// </summary>
[TestFixture]
public class HoldLeadInParityTest
{
    #region Fixtures

    private static readonly Dictionary<string, string> timing = new Dictionary<string, string>
    {
        ["brainPower"] = """
            {"version": 2, "song_end_ms": 72000, "lines": [
              {"text": "Blockbuster,", "start_ms": 63646.10083794706, "end_ms": 65234.33613206471, "words": [
                {"text": "Blockbuster,", "start_ms": 63646.10083794706, "end_ms": 65234.33613206471, "score": 1,
                 "syllables": [{"start_ms": 63646.10083794706}, {"start_ms": 63999.042014417646}, {"start_ms": 64351.98319088823}], "split_chars": [5, 8],
                 "holds": [{"char": 10, "start_ms": 64403.10083794706, "end_ms": 65057.86554382941}]}]},
              {"text": "brain power", "start_ms": 66822.57142618235, "end_ms": 68410.8067203, "words": [
                {"text": "brain", "start_ms": 66822.57142618235, "end_ms": 67175.51260265293, "score": 1},
                {"text": "power", "start_ms": 67175.51260265293, "end_ms": 68410.8067203, "score": 1,
                 "syllables": [{"start_ms": 67175.51260265293}, {"start_ms": 67528.45377912353}],
                 "holds": [{"char": 4, "start_ms": 67579.57142618235, "end_ms": 68234.33613206471}]}]}
            ]}
            """,

        ["edges"] = """
            {"version": 2, "song_end_ms": 30000, "lines": [
              {"text": "hello there", "start_ms": 1000, "end_ms": 2600, "words": [
                {"text": "hello", "start_ms": 1000, "end_ms": 2000, "score": 1, "holds": [{"char": 1, "start_ms": 1100, "end_ms": 1450}, {"char": 3, "start_ms": 1500, "end_ms": 1700}]},
                {"text": "there", "start_ms": 2000, "end_ms": 2600, "score": 1}]},
              {"text": "Blockbuster", "start_ms": 4000, "end_ms": 5600, "words": [
                {"text": "Blockbuster", "start_ms": 4000, "end_ms": 5600, "score": 1,
                 "syllables": [{"start_ms": 4000}, {"start_ms": 4400}, {"start_ms": 4800}], "split_chars": [5, 8],
                 "holds": [{"char": 5, "start_ms": 4310, "end_ms": 4500}, {"char": 10, "start_ms": 4850, "end_ms": 5400}]}]},
              {"text": "please go", "start_ms": 7000, "end_ms": 11000, "words": [
                {"text": "please", "start_ms": 7000, "end_ms": 10000, "score": 1, "pauses": [{"start_ms": 8000, "end_ms": 8500, "split": 3}],
                 "holds": [{"char": 2, "start_ms": 7700, "end_ms": 8000}, {"char": 3, "start_ms": 8500, "end_ms": 8900}]},
                {"text": "go", "start_ms": 10000, "end_ms": 11000, "score": 1}]},
              {"text": "don't", "start_ms": 12000, "end_ms": 13000, "words": [
                {"text": "don't", "start_ms": 12000, "end_ms": 13000, "score": 1, "holds": [{"char": 4, "start_ms": 12500, "end_ms": 12900}]}]},
              {"text": "meeeee", "start_ms": 20000, "end_ms": 21000, "words": [
                {"text": "meeeee", "start_ms": 20000, "end_ms": 21000, "score": 1, "holds": [{"char": 3, "start_ms": 20100, "end_ms": 20700}]}]}
            ]}
            """,
    };

    /// <summary>How many held cells each map has, so a map cannot drift into holding nothing.</summary>
    private static readonly Dictionary<string, int> held_cells = new Dictionary<string, int>
    {
        ["brainPower"] = 2,
        ["edges"] = 8,
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

    private static IBeatmap playable(string name)
        => new FlatWorkingBeatmap(decode(name)).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, Array.Empty<Mod>());

    private static IReadOnlyList<LyricLine> lyricLines(IBeatmap map)
        => map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-hold-lead-in-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(osu.Value));

        try
        {
            return NodeHarness.Run("CoreHoldLeadInHarness.cjs", path);
        }
        finally
        {
            File.Delete(path);
        }
    });

    #endregion

    #region Driving the game

    /// <summary>
    /// The stream as a replay, headed by the LIVE CONFIG frames (<see cref="HoldLiveParityTest"/>'s), with
    /// bit 15 of the second word as <paramref name="leadIn"/> says: set, a live run; clear, every replay
    /// recorded before the era.
    /// </summary>
    private static Replay keystrokes(JsonElement keys, bool leadIn)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true,
            charTimedStretch: true, flexibleLines: true, boundedRush: true, firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true,
            manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true,
            earlyFinish: true, keyTimeJudgement: true, bestRetypeJudgement: true, stacksDisplacedClaims: true, holdLeadIn: leadIn));

        foreach (var key in keys.EnumerateArray())
            replay.Frames.Add(new TypeBeatReplayFrame(key[0].GetDouble(), key[1].GetString()![0]));

        return replay;
    }

    private static TypeBeatReplayAccount score(JsonElement browserCase)
        => TypeBeatReplayScorer.Score(playable(browserCase.GetProperty("fixture").GetString()!), Array.Empty<Mod>(),
            keystrokes(browserCase.GetProperty("keys"), browserCase.GetProperty("leadIn").GetBoolean()), TypoRule.Deferred, ComboRestoreRule.OnFix);

    private static ScoreRank rankOf(TypeBeatReplayAccount game)
        => game.Rank == ScoreRank.F ? ScoreRank.F : TypeBeatScoreProcessor.RankFromStatistics(game.Accuracy, game.Statistics);

    #endregion

    /// <summary>
    /// The two loaders resolve every line to the same cells under each era and each stream: expected
    /// character, target, hold span and hand-off, and the line fields the targets feed (the activation and
    /// first vocal are read off the played targets, the seal grace off the hold-free ones).
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeUnderBothEras()
    {
        var fixtures = browser.Value.GetProperty("fixtures");

        Assert.Multiple(() =>
        {
            foreach (string name in timing.Keys)
            {
                var lines = lyricLines(playable(name));

                foreach (bool leadIn in new[] { true, false })
                {
                    foreach (bool literate in new[] { false, true })
                    {
                        var browserLines = fixtures.GetProperty(name).GetProperty(leadIn ? "leadIn" : "stored").GetProperty(literate ? "literate" : "plain");
                        string arm = $"{name}/{(leadIn ? "leadIn" : "stored")}/{(literate ? "literate" : "plain")}";
                        int held = 0;

                        Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Count), $"{arm}: line count");

                        for (int i = 0; i < lines.Count && i < browserLines.GetArrayLength(); i++)
                        {
                            var line = TypingLine.FromLyricLine(lines[i], literate, alignSubdivisionTargets: true, holdLeadIn: leadIn);
                            var browserLine = browserLines[i];

                            Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{arm}[{i}]: endTime");
                            Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{arm}[{i}]: activationTime");
                            Assert.That(browserLine.GetProperty("firstVocalTime").GetDouble(), Is.EqualTo(line.FirstVocalTime), $"{arm}[{i}]: firstVocalTime");
                            Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{arm}[{i}]: sealGraceMs");

                            var browserCells = browserLine.GetProperty("cells");
                            Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{arm}[{i}]: cell count");

                            for (int c = 0; c < line.Cells.Count && c < browserCells.GetArrayLength(); c++)
                            {
                                var cell = line.Cells[c];
                                var browserCell = browserCells[c];
                                string at = $"{arm}[{i}][{c}]";

                                Assert.That(browserCell.GetProperty("expected").GetString(), Is.EqualTo(cell.Expected.ToString()), $"{at}: expected");
                                Assert.That(browserCell.GetProperty("target").GetDouble(), Is.EqualTo(cell.TargetTime), $"{at}: target");
                                Assert.That(nullableDouble(browserCell, "holdStart"), Is.EqualTo(cell.HoldStart), $"{at}: HoldStart");
                                Assert.That(nullableDouble(browserCell, "holdEnd"), Is.EqualTo(cell.HoldEnd), $"{at}: HoldEnd");
                                Assert.That(nullableInt(browserCell, "handsOffFrom"), Is.EqualTo(cell.HandsOffFrom), $"{at}: HandsOffFrom");

                                if (cell.HoldEnd != null)
                                    held++;
                            }
                        }

                        Assert.That(held, Is.EqualTo(held_cells[name]), $"{arm}: the usable holds the map was built with");
                    }
                }
            }
        });
    }

    /// <summary>
    /// The era is not decorative on any fixture: the browser's two arms part on targets, and every held
    /// letter is due at its hold's start under the lead-in (the clamped first letter excepted, which the
    /// guard holds level with the letter ahead of it). Blockbuster's own numbers are pinned too.
    /// </summary>
    [Test]
    public void TheLeadInMovesTheHeldLetters()
    {
        var fixtures = browser.Value.GetProperty("fixtures");

        Assert.Multiple(() =>
        {
            foreach (string name in timing.Keys)
            {
                var on = fixtures.GetProperty(name).GetProperty("leadIn").GetProperty("plain");
                var off = fixtures.GetProperty(name).GetProperty("stored").GetProperty("plain");
                Assert.That(on.GetRawText(), Is.Not.EqualTo(off.GetRawText()), $"{name}: the two eras build the same cells");
            }

            var blockbuster = fixtures.GetProperty("brainPower").GetProperty("leadIn").GetProperty("plain")[0].GetProperty("cells");
            Assert.That(blockbuster[8].GetProperty("target").GetDouble(), Is.EqualTo(64351.98319088823), "t");
            Assert.That(blockbuster[9].GetProperty("target").GetDouble(), Is.EqualTo(64351.98319088823 + (64403.10083794706 - 64351.98319088823) / 2), "e");
            Assert.That(blockbuster[10].GetProperty("target").GetDouble(), Is.EqualTo(64403.10083794706), "r");

            var stored = fixtures.GetProperty("brainPower").GetProperty("stored").GetProperty("plain")[0].GetProperty("cells");
            Assert.That(stored[10].GetProperty("target").GetDouble(), Is.EqualTo(64351.98319088823 + 2 * (65234.33613206471 - 64351.98319088823) / 3).Within(1e-6), "the stored era's r");
        });
    }

    /// <summary>
    /// Every scripted run's submitted account, browser against game, field for field with no tolerance,
    /// each under its own era's header: the live cues and the even-spread cues, each played under both.
    /// </summary>
    [Test]
    public void EveryRunAgreesUnderItsOwnEra()
    {
        var cases = browser.Value.GetProperty("cases");

        Assert.That(cases.GetArrayLength(), Is.EqualTo(timing.Count * 4 * 2 * 2));

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
            }
        });
    }

    /// <summary>
    /// The bit decides the account: the same keystrokes scored under each era part somewhere, on the browser
    /// and the game alike, so neither arm above is a copy of the other.
    /// </summary>
    [Test]
    public void TheEraDecidesTheAccount()
    {
        var cases = browser.Value.GetProperty("cases").EnumerateArray().ToList();
        int parted = 0;

        foreach (var leadIn in cases.Where(c => c.GetProperty("leadIn").GetBoolean()))
        {
            string name = leadIn.GetProperty("name").GetString()!;
            var stored = cases.Single(c => c.GetProperty("name").GetString() == name[..^"leadIn".Length] + "stored");

            if (leadIn.GetProperty("submitted").GetRawText() != stored.GetProperty("submitted").GetRawText())
                parted++;
        }

        Assert.That(parted, Is.GreaterThan(0), "no keystrokes scored differently under the two eras");

        var liveOnLive = cases.Single(c => c.GetProperty("name").GetString() == "edges/leadInCues/0/leadIn");
        var liveOnStored = cases.Single(c => c.GetProperty("name").GetString() == "edges/leadInCues/0/stored");
        Assert.That(score(liveOnLive).TotalScore, Is.GreaterThan(score(liveOnStored).TotalScore), "the live cues are only perfect under the live era");
    }

    private static double? nullableDouble(JsonElement element, string key)
        => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetDouble();

    private static int? nullableInt(JsonElement element, string key)
        => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetInt32();
}
