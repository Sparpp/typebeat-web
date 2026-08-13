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
/// production: it refuses to write for any row it does not understand. The first half of this file
/// pins the REPRODUCE sweep's version of that, where understanding a row means re-deriving its
/// stored statistics exactly, along with the two era allowances it makes (the pre-backlog-72 mistype
/// key, a retuned mod multiplier) and every reason it declines a row.</para>
///
/// <para>The second half pins the SUPERSEDE sweep (backlog 136 and 142), where that gate provably
/// cannot hold: backlog 133 retired the ladder every stored row was graded on, so reproduction
/// becomes a diagnostic and a different predicate refuses instead, one that lets the JUDGEMENT of a
/// run move while still requiring it to be the same run over the same map. The tests there cover
/// that inversion, what it costs (a fixed typo ends up scoring exactly like a clean play), and the
/// command surface that stops it being reached by accident.</para>
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

    /// <summary>A run typing every cell correctly, with nothing to recover.</summary>
    private static Replay CleanReplay(IBeatmap map)
    {
        var targets = Targets(map);
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

        for (int i = 0; i < word.Length; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

        replay.Frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));
        return replay;
    }

    /// <summary>
    /// The combo-restore era every stored row was PLAYED in (backlog 140), and therefore the one a
    /// REPRODUCE sweep pins on both arms (see <c>Recalculation.stored_era_combo_rule</c>). No score
    /// in the database was played under a rule that gives combo back for a corrected typo, so a
    /// reproduce sweep holds this axis still and varies the TYPO rule alone, which is the axis it is
    /// about. A SUPERSEDE sweep deliberately moves it too, and the tests below pin what that costs.
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
            SrHt: null,
            SrLiterate: null,
            SrLiterateDt: null,
            SrLiterateHt: null);
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
            Assert.That(result.OldRuleStatistics!["great"], Is.EqualTo(12));

            // New rule: the cell recovers, so completion and rank recover with it.
            Assert.That(result.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(result.NewStatistics!["great"], Is.EqualTo(13));
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
            Assert.That(result.OldRuleStatistics!["great"], Is.EqualTo(12));
            Assert.That(result.OldRuleStatistics!.ContainsKey("good"), Is.False, "the pre-109 arm cannot emit the typo key");

            // Now: the typo key, no miss, and the SAME completion and rank the old rule gave it,
            // because backlog 126 makes a typo cost completion exactly as a miss does.
            Assert.That(result.NewStatistics!["good"], Is.EqualTo(1));
            Assert.That(result.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(result.NewStatistics!["great"], Is.EqualTo(12));
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
        var replay = CleanReplay(map);

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
        tampered["great"] += 3;

        var result = Recalculation.Run(stored with { StatisticsJson = JsonConvert.SerializeObject(tampered) }, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.NotReproducible));
            Assert.That(result.Recalculated, Is.False);
            Assert.That(result.Moves, Is.False, "a refused row must never be written");
            Assert.That(result.NewStatistics, Is.Null);
            Assert.That(result.Detail, Does.Contain("great"));
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
                Assert.That(result.NewStatistics!["great"], Is.EqualTo(13));
                Assert.That(result.NewRank, Is.EqualTo("X"));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #region Supersede (backlog 136 and 142)

    /// <summary>
    /// THE ROW THE REVERT LEAVES BEHIND. Backlog 133 and 134 shipped, so production judged on the
    /// four-tier character ladder for a day, and backlog 147 put the millisecond ladder back. Every
    /// row from that window carries <c>perfect</c> keys and a <c>great</c> that was worth 200, and
    /// the reproduce gate re-judges the keystrokes with TODAY's windows, so it can never re-derive
    /// one. Refusing it is correct, and is exactly why the supersede mode exists.
    ///
    /// <para>This test is the inversion of the one backlog 136 wrote, not a new one: it used to
    /// stand for the PRE-133 rows, which were then the ones the live ladder could not reproduce.
    /// Reverting the judgement swapped which side of the discriminator is history. What is unchanged
    /// is that both kinds of row exist, and that the tool must handle the ones it cannot reproduce
    /// by superseding them rather than by refusing the whole sweep.</para>
    /// </summary>
    [Test]
    public void AFourTierRowIsRefusedByReproduceAndSupersededInstead()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = FourTierEra(StoredFor(map, replay));

        var refused = Recalculation.Run(stored, Decoded(map, replay));
        var superseded = Recalculation.Run(stored, Decoded(map, replay), mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Skip, Is.EqualTo(SkipReason.NotReproducible), "the retired ladder cannot be re-derived");
            Assert.That(refused.Moves, Is.False);

            Assert.That(superseded.Skip, Is.EqualTo(SkipReason.None), "refusing a stored row is not an answer");
            Assert.That(superseded.Moves, Is.True);
            Assert.That(superseded.NewStatistics!["great"], Is.EqualTo(13));
            Assert.That(superseded.NewStatistics!.ContainsKey("perfect"), Is.False,
                "no ladder the client still runs can award one");
            Assert.That(superseded.NewRank, Is.EqualTo("X"));

            // The diagnostic survives the mode change. It stops being a gate; it does not stop being
            // reported, which is the whole difference between this and loosening a threshold.
            Assert.That(superseded.Reproduced, Is.False);
            Assert.That(superseded.ReproductionDetail, Is.Not.Null);
            Assert.That(superseded.ReproductionDetail, Does.Contain("perfect"));
        });
    }

    /// <summary>
    /// What superseding actually costs, stated as a test so nobody has to take the report's word for
    /// it: under all of today's rules a run with a FIXED typo lands on exactly the account a clean
    /// run lands on. Same max_combo, same total_score, same accuracy, same rank. The reproduce sweep,
    /// which holds combo restore at the stored era, does not, and that gap is the decision the user
    /// made on 2026-08-13.
    /// </summary>
    [Test]
    public void SupersedeMakesAFixedTypoScoreExactlyLikeACleanRun()
    {
        var map = Beatmap();
        var fixedTypo = FixedTypoReplay(map);

        var clean = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), CleanReplay(map), TypoRule.Deferred, ComboRestoreRule.OnFix);

        var stored = StoredFor(map, fixedTypo);
        var reproduce = Recalculation.Run(stored, Decoded(map, fixedTypo));
        var supersede = Recalculation.Run(stored, Decoded(map, fixedTypo), mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            Assert.That(supersede.Skip, Is.EqualTo(SkipReason.None));

            Assert.That(supersede.NewMaxCombo, Is.EqualTo(clean.MaxCombo), "the fix resumes the streak its keypress broke");
            Assert.That(supersede.NewTotalScore, Is.EqualTo(clean.TotalScore));
            Assert.That(supersede.NewAccuracy, Is.EqualTo(clean.Accuracy).Within(1e-12));
            Assert.That(supersede.NewRank, Is.EqualTo("X"));

            // The reproduce sweep holds the combo axis still, so it does NOT reach the clean
            // account. If these two ever agree, one of the two rules stopped being expressible.
            Assert.That(reproduce.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(reproduce.NewMaxCombo, Is.LessThan(supersede.NewMaxCombo));
            Assert.That(reproduce.NewTotalScore, Is.LessThan(supersede.NewTotalScore));

            // ... and the combo the fix gives back is worth pp, which is the other half of the cost.
            Assert.That(supersede.NewPp, Is.Not.Null);
            Assert.That(supersede.NewPp!.Value, Is.GreaterThan(reproduce.NewPp!.Value));

            // The mistype is still on the record. Only the cell and the streak came back.
            Assert.That(supersede.NewStatistics!["combo_break"], Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The gate still has teeth. Superseding lets the JUDGEMENT of a run move, which is the point;
    /// it does not let the RUN move. A row that accounts for more cells than the map has, or fewer
    /// than the replay judges, is refused exactly as the reproduce gate refuses it, and nothing is
    /// written for it.
    ///
    /// <para>The first case is the same tamper <see cref="ARowTheOldRuleCannotReproduceIsRefused"/>
    /// uses, which matters: the corruption the original gate was built to catch is still caught
    /// after the gate's meaning inverts.</para>
    /// </summary>
    [Test]
    public void SupersedeStillRefusesAReplayThatIsNotThisRun()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var inflatedStatistics = WireCounts.Parse(stored.StatisticsJson);
        inflatedStatistics["great"] += 3;

        var extraCells = Recalculation.Run(
            stored with { StatisticsJson = JsonConvert.SerializeObject(inflatedStatistics) },
            Decoded(map, replay),
            mode: RecalcMode.Supersede);

        var wrongMap = Recalculation.Run(
            stored with { MaximumStatisticsJson = JsonConvert.SerializeObject(new Dictionary<string, int> { ["great"] = 18 }) },
            Decoded(map, replay),
            mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            Assert.That(extraCells.Skip, Is.EqualTo(SkipReason.NotTheSameRun));
            Assert.That(extraCells.Moves, Is.False, "a refused row must never be written");
            Assert.That(extraCells.NewStatistics, Is.Null);
            Assert.That(extraCells.Detail, Does.Contain("16"), "the row claims 16 judged cells, the replay judges 13");

            Assert.That(wrongMap.Skip, Is.EqualTo(SkipReason.NotTheSameRun));
            Assert.That(wrongMap.Moves, Is.False);
            Assert.That(wrongMap.Detail, Does.Contain("18"));
        });
    }

    /// <summary>
    /// The mod multiplier goes the OTHER way in a supersede sweep, and deliberately. A reproduce
    /// sweep carries the row's own retired multiplier across, because it is moving one axis and the
    /// multiplier is not it; a supersede sweep applies today's, because a superseded score has to be
    /// one today's client could put on the wire. It also could not use the recovered ratio if it
    /// wanted to: that number is only a multiplier while both totals come off the same ladder.
    /// </summary>
    [Test]
    public void SupersedePricesTotalScoreWithTodaysMultiplierRatherThanTheRowsOwn()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        // Priced under the old 1.2x Flashlight multiplier.
        var stored = StoredFor(map, replay, multiplier: 1.2) with { ModsJson = @"[{""acronym"":""FL""}]" };

        var reproduce = Recalculation.Run(stored, Decoded(map, replay, new TypeBeatModFlashlight()));
        var supersede = Recalculation.Run(stored, Decoded(map, replay, new TypeBeatModFlashlight()), mode: RecalcMode.Supersede);

        var today = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModFlashlight() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

        Assert.Multiple(() =>
        {
            Assert.That(supersede.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(supersede.NewTotalScore, Is.EqualTo(today.TotalScore));
            Assert.That(supersede.AppliedMultiplier, Is.Not.EqualTo(1.2).Within(1e-3));

            Assert.That(reproduce.AppliedMultiplier, Is.EqualTo(1.2).Within(1e-5), "reproduce still keeps the row's own");
        });
    }

    /// <summary>
    /// The two ways a beatmap fails to resolve are not the same thing and must not be reported as
    /// one. A package the tool merely did not download is a FETCH failure that re-running fixes, and
    /// <c>supersede-apply</c> refuses while any remain; a set that was re-uploaded has lost the exact
    /// .osu the run was judged on, and no amount of re-running brings it back.
    /// </summary>
    [Test]
    public void AReuploadedBeatmapIsToldApartFromOneThatSimplyDidNotDownload()
    {
        var map = Beatmap();
        var stored = StoredFor(map, FixedTypoReplay(map));
        var unresolved = new ReplayArchive.DecodedReplay(null, null, null, "deadbeef");

        var reuploaded = Recalculation.Run(stored with { CurrentChecksumMd5 = "0123456789abcdef" }, unresolved, mode: RecalcMode.Supersede);
        var notFetched = Recalculation.Run(stored with { CurrentChecksumMd5 = "DEADBEEF" }, unresolved, mode: RecalcMode.Supersede);
        var unknown = Recalculation.Run(stored, unresolved, mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            Assert.That(reuploaded.Skip, Is.EqualTo(SkipReason.BeatmapReuploaded));
            Assert.That(notFetched.Skip, Is.EqualTo(SkipReason.BeatmapUnavailable), "same hash, so the map did not change");
            Assert.That(unknown.Skip, Is.EqualTo(SkipReason.BeatmapUnavailable), "with no checksum to compare, assume the fixable one");

            foreach (var declined in new[] { reuploaded, notFetched, unknown })
                Assert.That(declined.Moves, Is.False);
        });
    }

    /// <summary>
    /// Backlog 136 asks for an explicit decision on rows with no usable replay rather than a silent
    /// default, so there is no default: a supersede plan reports every cause it HIT and no policy was
    /// given for, and <c>supersede-apply</c> refuses while that list is non-empty. It also never asks
    /// about a cause the data does not contain, which is what keeps the requirement answerable.
    /// </summary>
    [Test]
    public void ASupersedePlanDemandsAPolicyForEveryUnreplayableCauseItActuallyHit()
    {
        var map = Beatmap();
        var stored = StoredFor(map, FixedTypoReplay(map));

        var results = new[]
        {
            Recalculation.Run(stored with { HasReplay = false }, null, mode: RecalcMode.Supersede),
            Recalculation.Run(stored with { Passed = false }, Decoded(map, FixedTypoReplay(map)), mode: RecalcMode.Supersede),
        };

        var undecided = WritePlan.Build(results, RecalcMode.Supersede, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

        var partly = WritePlan.Build(
            results,
            RecalcMode.Supersede,
            new Dictionary<UnreplayableCase, UnreplayablePolicy> { [UnreplayableCase.NoReplay] = UnreplayablePolicy.Unrank },
            filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(undecided.Undecided, Is.EquivalentTo(new[] { UnreplayableCase.NoReplay, UnreplayableCase.FailedRun }));
            Assert.That(undecided.RowsWritten, Is.Zero, "an undecided plan writes nothing");

            // A cause the run did not hit is never demanded: empty-replay, unreadable and the two
            // beatmap cases are absent here and stay absent.
            Assert.That(undecided.Undecided, Has.No.Member(UnreplayableCase.EmptyReplay));
            Assert.That(undecided.Undecided, Has.No.Member(UnreplayableCase.BeatmapMissing));

            Assert.That(partly.Undecided, Is.EquivalentTo(new[] { UnreplayableCase.FailedRun }));
            Assert.That(partly.Unranked.Select(r => r.Skip), Is.EquivalentTo(new[] { SkipReason.NoReplay }));
            Assert.That(partly.RowsWritten, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The unranking policy is a SUPERSEDE power and must not leak into the reproduce sweep, which
    /// has always left every row it could not re-derive exactly as it found it. Superseding is
    /// allowed to be harder to trigger than reproducing; it is never allowed to be easier, and a
    /// policy flag that quietly changed what 'apply' does would break that.
    /// </summary>
    [Test]
    public void TheUnrankPolicyDoesNothingToAReproduceSweep()
    {
        var map = Beatmap();
        var stored = StoredFor(map, FixedTypoReplay(map));

        var results = new[] { Recalculation.Run(stored with { HasReplay = false }, null) };

        var plan = WritePlan.Build(
            results,
            RecalcMode.Reproduce,
            new Dictionary<UnreplayableCase, UnreplayablePolicy> { [UnreplayableCase.NoReplay] = UnreplayablePolicy.Unrank },
            filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Unranked, Is.Empty);
            Assert.That(plan.Undecided, Is.Empty);
            Assert.That(plan.RowsWritten, Is.Zero);
        });
    }

    /// <summary>
    /// The command surface, which is the reason a supersede sweep cannot be started by accident. The
    /// mode rides on the COMMAND NAME, so no flag on 'apply' reaches it and a wrong guess at a flag
    /// is an error rather than a silently ignored option; and 'supersede-apply' wants three separate
    /// confirmations, the last of which cannot be produced without having read a report.
    ///
    /// <para>Every refusal here returns before the tool opens a connection or fetches a byte, which
    /// is what makes them testable and is also the property that matters.</para>
    /// </summary>
    [Test]
    public async Task SupersedingCannotBeReachedByAccident()
    {
        // There is no flag form. A caller guessing one gets an error, not a reproduce run that
        // quietly ignored the option it was asked for.
        Assert.That(await Cli.RunAsync(new[] { "report", "--supersede" }), Is.EqualTo(1));
        Assert.That(await Cli.RunAsync(new[] { "apply", "--supersede", "--i-understand-this-writes-to-the-database" }), Is.EqualTo(1));

        // Nor a half-typed command.
        Assert.That(await Cli.RunAsync(new[] { "supersede" }), Is.EqualTo(1));

        // The three confirmations, each missing in turn.
        Assert.That(await Cli.RunAsync(new[] { "supersede-apply" }), Is.EqualTo(1));

        Assert.That(await Cli.RunAsync(new[]
        {
            "supersede-apply",
            "--i-understand-this-writes-to-the-database",
        }), Is.EqualTo(1));

        Assert.That(await Cli.RunAsync(new[]
        {
            "supersede-apply",
            "--i-understand-this-writes-to-the-database",
            "--i-understand-this-discards-the-stored-numbers",
        }), Is.EqualTo(1), "--expect-superseded is the guard a blind sweep cannot satisfy");

        // A malformed expectation is an error too, rather than a null that reads as absent later.
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--expect-superseded", "lots" }), Is.EqualTo(1));

        // And the policy spelling is checked at parse time, so a typo cannot become a policy nobody
        // typed, nor an ignored flag that leaves the case undecided for a confusing reason.
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--unreplayable", "no-replay=delete" }), Is.EqualTo(1));
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--unreplayable", "no-replays=keep" }), Is.EqualTo(1));
    }

    /// <summary>
    /// The report is the ARTIFACT the supersede decision is made from, so it is rendered here rather
    /// than trusted. Two things are being checked: that it does not throw over the awkward rows (a
    /// refusal has no new statistics and a null rank, an unreplayable row has nothing at all), and
    /// that a reader is handed the six values a player sees plus the number the apply's
    /// <c>--expect-superseded</c> guard wants. A report that silently lost one of those would let a
    /// sweep be approved on incomplete information.
    /// </summary>
    [Test]
    public void ASupersedeReportShowsTheBeforeAndAfterADecisionNeeds()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);
        var stored = FourTierEra(StoredFor(map, replay));

        var results = new[]
        {
            Recalculation.Run(stored, Decoded(map, replay), mode: RecalcMode.Supersede),
            Recalculation.Run(stored with { ScoreId = 2, HasReplay = false }, null, mode: RecalcMode.Supersede),
            Recalculation.Run(stored with { ScoreId = 3, MaxCombo = 99 }, new ReplayArchive.DecodedReplay(null, null, null, "deadbeef"), mode: RecalcMode.Supersede),
            Recalculation.Run(
                stored with { ScoreId = 4, MaximumStatisticsJson = JsonConvert.SerializeObject(new Dictionary<string, int> { ["great"] = 18 }) },
                Decoded(map, replay),
                mode: RecalcMode.Supersede),
        };

        var plan = WritePlan.Build(
            results,
            RecalcMode.Supersede,
            new Dictionary<UnreplayableCase, UnreplayablePolicy> { [UnreplayableCase.NoReplay] = UnreplayablePolicy.Keep },
            filtered: false);

        var written = new StringWriter();
        Report.Print(results, plan, wholeTable: true, written);
        string text = written.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("SUPERSEDE"));
            Assert.That(text, Does.Contain("ComboRestoreRule.OnFix"), "the rules being applied have to be on the page");
            Assert.That(text, Does.Contain($"ROWS THIS RUN WOULD WRITE    {plan.RowsWritten}"));
            Assert.That(text, Does.Contain("--expect-superseded"));

            // The six values a player sees, before and after, in the table header.
            foreach (string column in new[] { "accuracy", "completion", "rank", "max_combo", "total_score", "pp" })
                Assert.That(text, Does.Contain(column), $"the before/after table must carry {column}");

            // Reproduction survives as a diagnostic, and the gate that replaced it is named.
            Assert.That(text, Does.Contain("DIAGNOSTIC"));
            Assert.That(text, Does.Contain("same run, same map"));

            // The undecided case is demanded, the decided one shows its policy, and the refusal is
            // reported rather than folded into a count.
            Assert.That(text, Does.Contain("no-replay"));
            Assert.That(text, Does.Contain("beatmap-missing"));
            Assert.That(text, Does.Contain("NO POLICY GIVEN"));
            Assert.That(text, Does.Contain("REFUSED"));
            Assert.That(text, Does.Contain("score 4"));

            Assert.That(text, Does.Contain("leaderboard impact"));
        });
    }

    /// <summary>
    /// The least-privilege handover, proved end to end: a directory of real <c>.osr</c> files, real
    /// packages and a <c>scores.json</c> drives a full SUPERSEDE report with no database anywhere.
    /// That matters because it is how a sweep gets reviewed by someone who should not be handed a
    /// production connection string, and because "it should work offline" is not the same as it
    /// working: the offline loader has to reconstruct the stored row from the replay's own blob plus
    /// the export, and a gap there would quietly produce a report about the wrong numbers.
    ///
    /// <para>The apply half stays impossible offline, which is checked here too.
    /// <c>docs/score-recalc-export.md</c> is the format.</para>
    /// </summary>
    [Test]
    public async Task AnOfflineExportDrivesAWholeSupersedeReportAndStillCannotApply()
    {
        var ruleset = new TypeBeatRuleset();
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        var osuText = new StringWriter();
        ruleset.EncodeToNativeFormat(map, null, osuText);
        byte[] osu = Encoding.UTF8.GetBytes(osuText.ToString());
        string md5 = Convert.ToHexStringLower(MD5.HashData(osu));

        string dir = Path.Combine(Path.GetTempPath(), "typebeat-recalc-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "replays"));
        Directory.CreateDirectory(Path.Combine(dir, "sets"));

        try
        {
            using (var zip = ZipFile.Open(Path.Combine(dir, "sets", "set.osz"), ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("map.osu").Open())
                entry.Write(osu);

            // The account the client of the day submitted, written into the .osr's score-info blob
            // exactly as the client writes it. That blob IS the offline run's copy of the stored
            // row (ScoreEndpoints.SubmitScore stores the submitted dictionaries verbatim), so a
            // fixture without it would be testing the loader against an empty row.
            var submitted = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.ImmediateMiss, combo_restore_rule);

            var score = new Score
            {
                Replay = replay,
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = md5 },
                    User = new typebeat.Game.Online.API.Requests.Responses.APIUser { Username = "recalc" },
                    Date = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                    Statistics = new Dictionary<HitResult, int>(submitted.Statistics),
                    MaximumStatistics = new Dictionary<HitResult, int>(submitted.MaximumStatistics),
                    MaxCombo = submitted.MaxCombo,
                    TotalScore = submitted.TotalScore,
                    Accuracy = submitted.Accuracy,
                    Rank = submitted.Rank,
                },
            };

            using (var stream = File.Create(Path.Combine(dir, "replays", "77.osr")))
                new LegacyScoreEncoder(score, map).Encode(stream);

            // The columns an .osr cannot carry. Without these the report has no pp BEFORE, cannot
            // tell a failed run from a passed one, and cannot group a leaderboard.
            File.WriteAllText(Path.Combine(dir, "scores.json"), JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["77"] = new { pp = 12.5, ranked = true, passed = true, beatmap_id = 3, user_id = 9, stars = 4.0 },
            }));

            string outFile = Path.Combine(dir, "supersede.json");

            string[] offline =
            {
                "supersede-report",
                "--offline", dir,
                "--scores", Path.Combine(dir, "scores.json"),
                "--cache", Path.Combine(dir, "cache"),
                "--out", outFile,
            };

            Assert.That(await Cli.RunAsync(offline), Is.Zero, "an export alone must be enough to produce the report");
            Assert.That(File.Exists(outFile), Is.True);

            string detail = File.ReadAllText(outFile);

            Assert.Multiple(() =>
            {
                Assert.That(detail, Does.Contain("\"Mode\": \"Supersede\""), "the report must say which sweep it describes");
                Assert.That(detail, Does.Contain("\"Skip\": \"None\""), "the export resolved its own beatmap and re-judged the run");
                Assert.That(detail, Does.Contain("\"Pp\": 12.5"), "--scores supplies the pp BEFORE an .osr does not carry");
            });

            // ... and the write half stays out of reach, whatever else is passed.
            Assert.That(await Cli.RunAsync(new[]
            {
                "supersede-apply",
                "--offline", dir,
                "--cache", Path.Combine(dir, "cache"),
                "--unreplayable", "keep",
                "--expect-superseded", "1",
                "--i-understand-this-writes-to-the-database",
                "--i-understand-this-discards-the-stored-numbers",
            }), Is.EqualTo(1), "offline is an analysis mode; it must never be able to write");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A row stored while backlog 133's FOUR-tier character-distance ladder was live: the top
    /// quality tier was <c>perfect</c>, and <c>maximum_statistics</c> carried one <c>perfect</c> per
    /// cell because the cell judgement's MaxResult had been raised from Great to Perfect. That key
    /// IS the era stamp the server reads (<c>ScoringContract.JudgedUnderTheFourthTier</c>), so
    /// rewriting it is the whole of what makes this row look like that era.
    ///
    /// <para>Backlog 147 reverted the ladder, so this helper inverted with it: it used to make a row
    /// look like PRE-133 history, and now it makes one look like the one-day window in between.
    /// Those rows are the ones that exist and cannot be re-derived, which is the same fact seen from
    /// the other side.</para>
    /// </summary>
    private static StoredScore FourTierEra(StoredScore stored) => stored with
    {
        StatisticsJson = JsonConvert.SerializeObject(Retier(WireCounts.Parse(stored.StatisticsJson))),
        MaximumStatisticsJson = JsonConvert.SerializeObject(Retier(WireCounts.Parse(stored.MaximumStatisticsJson))),
    };

    private static Dictionary<string, int> Retier(Dictionary<string, int> counts)
    {
        if (!counts.Remove("great", out int top))
            return counts;

        counts["perfect"] = counts.GetValueOrDefault("perfect") + top;
        return counts;
    }

    #endregion
}
