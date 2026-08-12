using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Typebeat.Tools.ScoreRecalc;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 114: the score-recalculation tool (tools/score-recalc), which re-derives a stored
/// score's statistics from its replay because backlog 109 changed what a typo costs its cell.
///
/// <para>It lives here because this is the only project that compiles BOTH repos, which is exactly
/// what the tool needs: the statistics come from the GAME's engine and score processor, everything
/// derived from them comes from the SERVER's ScoringContract and PerformancePoints.</para>
///
/// <para>The load-bearing property under test is the one that makes the tool safe to point at
/// production: it re-derives every score under the OLD rule first and refuses any row whose stored
/// statistics it cannot reproduce exactly. These pins cover that gate in both directions, the two
/// era allowances it makes (the pre-backlog-72 mistype key, a retuned mod multiplier), and every
/// reason it declines a row.</para>
/// </summary>
[TestFixture]
public class ScoreRecalcTest
{
    #region Fixture: a twelve-cell line plus a short second one

    private const string word = "abcdefghijkl";
    private const double line_zero_end = 300000;

    private static TypeBeatBeatmap Beatmap()
    {
        var first = new LyricLine
        {
            RawText = word,
            StartTime = 0,
            EndTime = line_zero_end,
            SingEndTime = 240000,
            Units = new[] { new TimedUnit { Text = word, StartTime = 0, EndTime = 240000 } },
        };

        var second = new LyricLine
        {
            RawText = "z",
            StartTime = line_zero_end,
            EndTime = 600000,
            SingEndTime = 400000,
            Units = new[] { new TimedUnit { Text = "z", StartTime = line_zero_end, EndTime = 400000 } },
        };

        var map = new TypeBeatBeatmap();
        map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = first, Granularity = TimingGranularity.Line });
        map.HitObjects.Add(new TypeBeatHitObject { StartTime = line_zero_end, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Recalc";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    private static IReadOnlyList<double> Targets(IBeatmap map)
        => TypingLine.FromLyricLine(((TypeBeatHitObject)map.HitObjects[0]).Line, TimingGranularity.Line, false)
                     .Cells.Select(c => c.TargetTime).ToList();

    /// <summary>A run typing every cell correctly except one, which is typed wrong then FIXED.</summary>
    private static Replay FixedTypoReplay(IBeatmap map)
    {
        var targets = Targets(map);
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));
        replay.Frames.Add(new TypeBeatReplayFrame(targets[0], word[0]));
        replay.Frames.Add(new TypeBeatReplayFrame(targets[1], word[1]));
        replay.Frames.Add(new TypeBeatReplayFrame(targets[2], 'q'));
        replay.Frames.Add(new TypeBeatReplayFrame(targets[2], TypeBeatReplayFrame.BACKSPACE));

        for (int i = 2; i < word.Length; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

        replay.Frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));
        return replay;
    }

    /// <summary>A run typing every cell correctly except one, which is typed wrong and LEFT.</summary>
    private static Replay UnfixedTypoReplay(IBeatmap map)
    {
        var targets = Targets(map);
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

        for (int i = 0; i < word.Length; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(targets[i], i == 2 ? 'q' : word[i]));

        replay.Frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));
        return replay;
    }

    /// <summary>
    /// The combo-restore era every stored row belongs to, and therefore the one both this fixture
    /// and <c>Recalculation</c> pin (backlog 140, see <c>Recalculation.combo_restore_rule</c>). No
    /// score in the database was played under a rule that gives combo back for a corrected typo, so
    /// re-deriving one under <see cref="ComboRestoreRule.OnFix"/> would credit it with combo its
    /// fingers never earned. Both re-derivations in this file therefore hold this axis still and
    /// vary the TYPO rule alone, which is the axis the sweep is about.
    /// </summary>
    private const ComboRestoreRule combo_restore_rule = ComboRestoreRule.Never;

    /// <summary>The stored row a client of the OLD era would have produced for this run.</summary>
    private static StoredScore StoredFor(IBeatmap map, Replay replay, bool dropMistypeKey = false, double multiplier = 1)
    {
        var old = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.ImmediateMiss, combo_restore_rule);

        var statistics = ToWire(old.Statistics);

        if (dropMistypeKey)
            statistics.Remove("combo_break");

        // The stored pp is what the row's own statistics are worth, exactly as the submission path
        // and PpBackfill compute it. Without that, every synthetic row would look like a pp change.
        var (pp, _) = Typebeat.Web.Scoring.PerformancePoints.ForScore(
            true, Typebeat.Web.ScoreMods.Parse("[]"),
            Typebeat.Web.Scoring.PerformancePoints.CountNotes(statistics),
            old.Accuracy, old.MaxCombo, 4, null, null);

        return new StoredScore(
            ScoreId: 1,
            BeatmapId: 1,
            SetId: 1,
            TotalScore: (long)Math.Round(old.TotalScoreWithoutMods * multiplier, MidpointRounding.AwayFromZero),
            Accuracy: old.Accuracy,
            Completion: old.Completion,
            MaxCombo: old.MaxCombo,
            Rank: old.Rank.ToString(),
            Passed: true,
            Ranked: true,
            HasReplay: true,
            StatisticsJson: JsonConvert.SerializeObject(statistics),
            MaximumStatisticsJson: JsonConvert.SerializeObject(ToWire(old.MaximumStatistics)),
            ModsJson: "[]",
            Pp: pp ?? 0,
            PpKnown: true,
            BaseStars: 4,
            SrDt: null,
            SrHt: null);
    }

    private static Dictionary<string, int> ToWire(IReadOnlyDictionary<HitResult, int> counts)
    {
        var wire = new Dictionary<string, int>();

        foreach (var (result, count) in counts)
        {
            if (count != 0)
                wire[WireCounts.Key(result)] = count;
        }

        return wire;
    }

    private static ReplayArchive.DecodedReplay Decoded(IBeatmap map, Replay replay, params Mod[] mods)
    {
        var score = new Score
        {
            Replay = replay,
            ScoreInfo = new ScoreInfo
            {
                Ruleset = new TypeBeatRuleset().RulesetInfo,
                Mods = mods,
            },
        };

        return new ReplayArchive.DecodedReplay(score, map, "hash", null);
    }

    #endregion

    /// <summary>
    /// The gate the whole tool rests on. Given a row priced under the OLD rule, the tool reproduces
    /// it, accepts it, and then reports the NEW rule's account: the fixed typo earns its cell back,
    /// which is exactly what backlog 109 changed.
    /// </summary>
    [Test]
    public void AnOldRuleRowIsReproducedAndThenRepriced()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Recalculated, Is.True);

            // Old rule: the typo spent the cell on a Miss and the fix could not take it back.
            Assert.That(result.OldRuleStatistics!["miss"], Is.EqualTo(1));
            Assert.That(result.OldRuleStatistics!["perfect"], Is.EqualTo(12));

            // New rule: the cell recovers, so completion and rank recover with it.
            Assert.That(result.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(result.NewStatistics!["perfect"], Is.EqualTo(13));
            Assert.That(result.NewCompletion, Is.EqualTo(1));
            Assert.That(result.NewRank, Is.EqualTo("X"));
            Assert.That(result.NewStatisticsValid, Is.True);
            Assert.That(result.NewTotalWithinBounds, Is.True);
            Assert.That(result.Moves, Is.True);

            // The mistype is still on the record either way; only the CELL recovered.
            Assert.That(result.NewStatistics!["combo_break"], Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Backlog 126 against backlog 114's guarantee, which is the one that has to survive every
    /// change to what a typo costs: <c>TypoRule.ImmediateMiss</c> must still reproduce a PRE-109
    /// stored row byte for byte, or the tool loses its ability to check itself against history and
    /// every row it touches becomes unverifiable.
    ///
    /// <para>The run here is the one whose treatment moved: a typo typed and LEFT. Under the old
    /// rule the cell was a <c>miss</c>, and that is still exactly what the tool re-derives, key for
    /// key. Under the new rule it is the <c>good</c> key: no miss, and completion 12/13 rather than
    /// the 1 backlog 124 briefly gave it. So the row does NOT move on completion or rank, and moves
    /// only on the miss count, and therefore on pp.</para>
    /// </summary>
    [Test]
    public void AnUnfixedTypoRowReproducesUnderTheOldRuleAndTakesTheTypoKeyUnderTheNew()
    {
        var map = Beatmap();
        var replay = UnfixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            // The gate: reproduced exactly, so the row is safe to reprice.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Recalculated, Is.True);
            Assert.That(result.OldRuleStatistics, Is.EquivalentTo(WireCounts.Parse(stored.StatisticsJson)));

            // Pre-109: the wrong char spent the cell on a Miss the instant it landed.
            Assert.That(result.OldRuleStatistics!["miss"], Is.EqualTo(1));
            Assert.That(result.OldRuleStatistics!["perfect"], Is.EqualTo(12));
            Assert.That(result.OldRuleStatistics!.ContainsKey("good"), Is.False, "the pre-109 arm cannot emit the typo key");

            // Now: the typo key, no miss, and the SAME completion and rank the old rule gave it,
            // because backlog 126 makes a typo cost completion exactly as a miss does.
            Assert.That(result.NewStatistics!["good"], Is.EqualTo(1));
            Assert.That(result.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(result.NewStatistics!["perfect"], Is.EqualTo(12));
            Assert.That(result.NewCompletion, Is.EqualTo(12 / 13.0).Within(1e-12));
            Assert.That(result.NewCompletion, Is.EqualTo(stored.Completion).Within(1e-12));
            Assert.That(result.NewRank, Is.EqualTo(stored.Rank));
            Assert.That(result.NewStatisticsValid, Is.True);
            Assert.That(result.NewTotalWithinBounds, Is.True);
        });
    }

    /// <summary>
    /// A run with nothing to recover reproduces AND stands still: identical statistics in, identical
    /// statistics out. This is the case most stored scores are in, so a tool that quietly moved it
    /// would be worse than useless.
    /// </summary>
    [Test]
    public void ACleanRunDoesNotMoveAtAll()
    {
        var map = Beatmap();
        var targets = Targets(map);

        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

        for (int i = 0; i < word.Length; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

        replay.Frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

        var stored = StoredFor(map, replay);
        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Moves, Is.False);
            Assert.That(result.NewMaxCombo, Is.EqualTo(stored.MaxCombo));
            Assert.That(result.NewTotalScore, Is.EqualTo(stored.TotalScore));
            Assert.That(result.NewRank, Is.EqualTo(stored.Rank));
        });
    }

    /// <summary>
    /// The refusal. A row whose stored statistics disagree with the old-rule re-derivation is
    /// reported and left alone: whatever the tool would write for it would be a guess.
    /// </summary>
    [Test]
    public void ARowTheOldRuleCannotReproduceIsRefused()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var tampered = WireCounts.Parse(stored.StatisticsJson);
        tampered["perfect"] += 3;

        var result = Recalculation.Run(stored with { StatisticsJson = JsonConvert.SerializeObject(tampered) }, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.NotReproducible));
            Assert.That(result.Recalculated, Is.False);
            Assert.That(result.Moves, Is.False, "a refused row must never be written");
            Assert.That(result.NewStatistics, Is.Null);
            Assert.That(result.Detail, Does.Contain("perfect"));
        });
    }

    /// <summary>
    /// A max_combo that does not reproduce is a refusal too, even when every count matches: the
    /// combo history is where backlog 109's other consequence lives, so a disagreement there means
    /// the harness and the row are not describing the same run.
    /// </summary>
    [Test]
    public void AMaxComboThatDoesNotReproduceIsRefused()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var result = Recalculation.Run(stored with { MaxCombo = stored.MaxCombo + 1 }, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.NotReproducible));
            Assert.That(result.Detail, Does.Contain("max_combo"));
        });
    }

    /// <summary>
    /// A row from before the mistype stat existed (backlog 72) carries NO combo_break key. That is
    /// an era marker, not a disagreement, so it still reproduces; and by default the sweep does not
    /// introduce the count either, because doing so reprices the row on a dimension backlog 114 is
    /// not about (PerformancePoints reads mistypes).
    /// </summary>
    [Test]
    public void APreMistypeStatRowReproducesAndKeepsItsAbsentCount()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay, dropMistypeKey: true);

        var kept = Recalculation.Run(stored, Decoded(map, replay));
        var backfilled = Recalculation.Run(stored, Decoded(map, replay), backfillMistypes: true);

        Assert.Multiple(() =>
        {
            Assert.That(kept.Skip, Is.EqualTo(SkipReason.None), "the missing key is an era marker, not a mismatch");
            Assert.That(kept.NewStatistics!.ContainsKey("combo_break"), Is.False);

            Assert.That(backfilled.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(backfilled.NewStatistics!["combo_break"], Is.EqualTo(1), "opting in writes the real count");
        });
    }

    /// <summary>
    /// The mod score multipliers have been retuned since some stored plays (Flashlight went 1.2x to
    /// 1.05x). The row's OWN multiplier is carried across the recalculation rather than replaced, so
    /// this sweep moves total_score by the amount the judgement rule moved it and by nothing else.
    /// </summary>
    [Test]
    public void ARetunedModMultiplierIsPreservedRatherThanReapplied()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        // Priced under the old 1.2x Flashlight multiplier.
        var stored = StoredFor(map, replay, multiplier: 1.2) with { ModsJson = @"[{""acronym"":""FL""}]" };

        var result = Recalculation.Run(stored, Decoded(map, replay, new TypeBeatModFlashlight()));

        var newRule = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModFlashlight() }, replay, TypoRule.Deferred, combo_restore_rule);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            // Recovered from the row's own numbers, so it carries one unit of the rounding the
            // stored integer already went through.
            Assert.That(result.StoredScoreMultiplier, Is.EqualTo(1.2).Within(1e-5));
            Assert.That(result.NewTotalScore, Is.EqualTo((long)Math.Round(newRule.TotalScoreWithoutMods * 1.2, MidpointRounding.AwayFromZero)).Within(1));
            Assert.That(result.NewTotalScore, Is.Not.EqualTo(newRule.TotalScore), "today's 1.05x must not be applied to an old play");
        });
    }

    /// <summary>Every reason the tool declines a row, each reported rather than silently dropped.</summary>
    [Test]
    public void EveryDeclineReasonIsReportedAndWritesNothing()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var noReplay = Recalculation.Run(stored with { HasReplay = false }, null);
        var undecodable = Recalculation.Run(stored, null);
        var noBeatmap = Recalculation.Run(stored, new ReplayArchive.DecodedReplay(null, null, null, "deadbeef"));
        var failed = Recalculation.Run(stored with { Passed = false }, Decoded(map, replay));
        var empty = Recalculation.Run(stored, Decoded(map, new Replay()));

        Assert.Multiple(() =>
        {
            Assert.That(noReplay.Skip, Is.EqualTo(SkipReason.NoReplay));
            Assert.That(undecodable.Skip, Is.EqualTo(SkipReason.UndecodableReplay));
            Assert.That(noBeatmap.Skip, Is.EqualTo(SkipReason.BeatmapUnavailable));
            Assert.That(noBeatmap.Detail, Is.EqualTo("deadbeef"));
            Assert.That(failed.Skip, Is.EqualTo(SkipReason.FailedRun));
            Assert.That(empty.Skip, Is.EqualTo(SkipReason.EmptyReplay));

            foreach (var declined in new[] { noReplay, undecodable, noBeatmap, failed, empty })
                Assert.That(declined.Moves, Is.False);
        });
    }

    /// <summary>
    /// The wire keys the tool writes are the enum's own EnumMember names, which is what the client
    /// serializes with and what ScoringContract switches on. A drift here would silently strand a
    /// whole result type in the statistics jsonb.
    /// </summary>
    [Test]
    public void WireKeysMatchTheContractTheServerReads()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireCounts.Key(HitResult.Perfect), Is.EqualTo("perfect"));
            Assert.That(WireCounts.Key(HitResult.Great), Is.EqualTo("great"));
            Assert.That(WireCounts.Key(HitResult.Ok), Is.EqualTo("ok"));
            Assert.That(WireCounts.Key(HitResult.Meh), Is.EqualTo("meh"));
            Assert.That(WireCounts.Key(HitResult.Miss), Is.EqualTo("miss"));
            Assert.That(WireCounts.Key(HitResult.IgnoreHit), Is.EqualTo("ignore_hit"));
            Assert.That(WireCounts.Key(TypeBeatScoreProcessor.MISTYPE_RESULT), Is.EqualTo("combo_break"));
        });
    }

    /// <summary>
    /// End to end through the real file formats: encode the run as a legacy .osr exactly as the
    /// client uploads it, zip the map exactly as a package holds it, and require the tool to read
    /// both back and land on the same account. This is the only test that proves the tool can
    /// actually consume what production stores.
    /// </summary>
    [Test]
    public void ARealOsrAndPackageRoundTripIntoTheSameAccount()
    {
        var ruleset = new TypeBeatRuleset();
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        // The map as a package holds it: the ruleset's own .osu text, zipped.
        var osuText = new StringWriter();
        ruleset.EncodeToNativeFormat(map, null, osuText);
        byte[] osu = Encoding.UTF8.GetBytes(osuText.ToString());
        string md5 = Convert.ToHexStringLower(MD5.HashData(osu));

        string dir = Path.Combine(Path.GetTempPath(), "typebeat-recalc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string packagePath = Path.Combine(dir, "set.osz");

            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("map.osu").Open())
                entry.Write(osu);

            // The score as the client uploads it.
            var score = new Score
            {
                Replay = replay,
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = md5 },
                    User = new typebeat.Game.Online.API.Requests.Responses.APIUser { Username = "recalc" },
                    Date = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                },
            };

            byte[] osr;

            using (var stream = new MemoryStream())
            {
                new LegacyScoreEncoder(score, map).Encode(stream);
                osr = stream.ToArray();
            }

            using var source = new ReplayArchive("http://localhost", Path.Combine(dir, "cache"));
            source.IndexPackage(packagePath);

            var decoded = source.Decode(osr);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Score, Is.Not.Null, "the package must resolve the replay's beatmap hash");

            var stored = StoredFor(map, replay);
            var result = Recalculation.Run(stored, decoded);

            Assert.Multiple(() =>
            {
                Assert.That(result.Skip, Is.EqualTo(SkipReason.None), "an .osr off the wire must reproduce its own stored row");
                Assert.That(result.NewStatistics!["perfect"], Is.EqualTo(13));
                Assert.That(result.NewRank, Is.EqualTo("X"));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
