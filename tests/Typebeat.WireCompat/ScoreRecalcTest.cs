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
/// <para>The second half pins the SUPERSEDE sweep (backlog 136 and 142), where that gate cannot
/// hold: the sweep re-judges on rules the row was not played under, and for a row stored in the
/// backlog 133-to-147 window reproduction is impossible outright, because backlog 147 deleted the
/// four-tier character-distance ladder that graded it. So reproduction becomes a diagnostic and a
/// different predicate refuses instead, one that lets the JUDGEMENT of a run move while still
/// requiring it to be the same run over the same map. The tests there cover that inversion, what it
/// costs (a fixed typo ends up scoring exactly like a clean play), and the command surface that stops
/// it being reached by accident.</para>
///
/// <para>The last region pins backlog 151: the two judgement changes that shipped with no era switch
/// (the untimed spacebar, and rate mods scaling the windows) are now switches the reproduce pass sets
/// to the stored era and the supersede pass sets to today's. Without them the reproduce pass could
/// not re-derive a single row in the table, because every map has spaces, and it would have reported
/// the whole table as corrupt. It then pins how the pass decides WHICH era judged a given row: the
/// typo rule is read off the row's own keys (backlog 155), and the spacebar, the rate windows and
/// combo restore leave no key at all, so all three are proved by reconstruction (backlog 156 and
/// 157).</para>
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
        => TypingLine.FromLyricLine(((TypeBeatHitObject)map.HitObjects[0]).Line, false)
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
    /// The OLDEST combo-restore era (backlog 140), the one the reproduce pass tries first (see
    /// <c>Recalculation.stored_era_combo_rule</c>): the break a wrong keypress took was permanent.
    ///
    /// <para>It was a pin on every row until backlog 157 and is now a starting point, like the two
    /// axes below: a row played since backlog 140 was judged under <see cref="ComboRestoreRule.OnFix"/>
    /// and has its era proved by reconstruction. It remains the default a synthetic row is built
    /// under, because the majority of the table is still from before that release.</para>
    /// </summary>
    private const ComboRestoreRule combo_restore_rule = ComboRestoreRule.Never;

    /// <summary>
    /// The other two oldest-era axes (backlog 151). A client that graded the spacebar on the clock
    /// (pre-148) and left the judgement windows unscaled by the rate (pre-150) is what the majority of
    /// the table was submitted by, so a synthetic stored row is built under both unless a test is
    /// deliberately building a row from a later era. The fixture above happens to be indifferent to
    /// them (no spaces, no rate mod); the fixture in the era region at the bottom is not, which is the
    /// point of it.
    /// </summary>
    private const SpaceTimingRule space_timing_rule = SpaceTimingRule.Timed;

    private const RateWindowRule rate_window_rule = RateWindowRule.Unscaled;

    /// <summary>
    /// The fifth axis (backlog 199), and the same story again: every row in the table was played
    /// when an off-time press (the right character struck outside the outermost Meh window) zeroed
    /// the run and spent its cell on a Miss, so a synthetic stored row is built under that unless a
    /// test is deliberately building a row a client running today's rule produced.
    /// </summary>
    private const OffTimeRule off_time_rule = OffTimeRule.BreaksCombo;

    /// <summary>
    /// The sixth axis (backlog 210), and the same story a third time: every row in the table was
    /// played when a CORRECTED typo's cell was graded on the retype's own timing alone, so a fix
    /// struck inside the Great window was worth a full 300. A synthetic stored row is built under
    /// that unless a test is deliberately building a row a client running today's cap produced.
    /// </summary>
    private const CorrectionCreditRule correction_credit_rule = CorrectionCreditRule.Full;

    /// <summary>
    /// The seventh axis (backlog 213), and the same story a fourth time: every row in the table was
    /// played when a cell left holding a WRONG character was re-weighted to a Meh's 50 of 300, not
    /// to a miss's 0. A synthetic stored row is built under that unless a test is deliberately
    /// building a row a client running today's fold produced.
    ///
    /// <para>IT IS THE ONE AXIS THE SEARCH CANNOT PROVE, because the fold moved no key: both arms
    /// re-derive byte-identical <c>statistics</c> and <c>max_combo</c>, which are the only two
    /// quantities the reproduction gate compares. So the arm a row is pinned to is decided by the
    /// search ORDER alone, and it is this one, which is why a stored row must be built here.</para>
    /// </summary>
    private const UnfixedTypoWorthRule unfixed_typo_worth_rule = UnfixedTypoWorthRule.MehCredit;

    /// <summary>
    /// The stored row a client of the OLDEST era would have produced for this run.
    ///
    /// <para>Every era axis is a parameter, because every one of them moved while the score table was
    /// already filling and the table therefore holds rows from both sides of each: the typo rule
    /// (backlog 155), the spacebar and the rate windows (backlog 156), combo restore (backlog 157),
    /// what an off-time press cost (backlog 199), what a corrected typo was worth (backlog 210) and
    /// what an UNCORRECTED one was worth (backlog 213).
    /// A synthetic row for a later population has to be built under the rule that judged it, or it is
    /// a row no client ever produced.</para>
    /// </summary>
    /// <summary>
    /// The one-cell rating matrix (034_ratings_matrix.sql) every synthetic row here is priced from:
    /// the same 4 stars the rows always carried, on the arm-none plain 1.00x cell, which is the only
    /// cell a no-mod row reads. The 500 difficult characters sit above any miss count the fixtures
    /// carry, so the cleanliness term does not zero a play this file expects to be worth something.
    /// It is stored ON the row as well as used to price it, because the tool prices from the row.
    /// </summary>
    private const string fixture_ratings = """{"version":1,"cells":{"none/plain/1.00":{"sr":4,"dc":500}}}""";

    private static StoredScore StoredFor(
        IBeatmap map,
        Replay replay,
        bool dropMistypeKey = false,
        double multiplier = 1,
        SpaceTimingRule spaceRule = space_timing_rule,
        RateWindowRule rateRule = rate_window_rule,
        ComboRestoreRule comboRule = combo_restore_rule,
        TypoRule typoRule = TypoRule.ImmediateMiss,
        OffTimeRule offTimeRule = off_time_rule,
        CorrectionCreditRule creditRule = correction_credit_rule,
        UnfixedTypoWorthRule worthRule = unfixed_typo_worth_rule,
        params Mod[] mods)
    {
        var old = TypeBeatReplayScorer.Score(map, mods, replay, typoRule, comboRule, spaceRule, rateRule, offTimeRule: offTimeRule, creditRule: creditRule, worthRule: worthRule);

        var statistics = ToWire(old.Statistics);

        if (dropMistypeKey)
            statistics.Remove("combo_break");

        // The stored pp is what the row's own statistics are worth, exactly as the submission path
        // and PpBackfill compute it. Without that, every synthetic row would look like a pp change.
        var (pp, _) = Typebeat.Web.Scoring.PerformancePoints.ForScore(
            true, Typebeat.Web.ScoreMods.Parse("[]"),
            Typebeat.Web.Scoring.PerformancePoints.CountNotes(statistics),
            old.Accuracy, old.MaxCombo, Typebeat.Web.Scoring.BeatmapRatings.Parse(fixture_ratings));

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
            SrLiterateHt: null,
            Ratings: fixture_ratings);
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

        // The reproduce arm: today's TYPO rule alone, with every other axis held where the row was
        // judged. The credit axis has to be named here as the other four are, because this fixture
        // CORRECTS its typo and backlog 210 grades that cell differently on the two arms; leaving it
        // to the parameter default would compare the pre-210 row against a post-210 judgement and
        // report the difference as a multiplier fault.
        var newRule = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModFlashlight() }, replay, TypoRule.Deferred, combo_restore_rule, space_timing_rule, rate_window_rule,
            offTimeRule: off_time_rule, creditRule: correction_credit_rule);

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

    /// <summary>
    /// BACKLOG 347's SECOND CONFIG HEADER through the real file format. The rush-cap era does not
    /// ride the first flags word (it was full at bit 16) but a second header frame of its own,
    /// <see cref="TypeBeatReplayFrame.CONFIG_EXTENDED"/>, and this tool re-derives a row by decoding
    /// the uploaded .osr through the game's own decoder and scorer, so the header is read wherever
    /// the frames are, never by a table here. This pins that the header survives the .osr the client
    /// uploads, that the tool reads it, and that it is not decorative.
    ///
    /// <para>The run: "abcdefghijkl" timed 10 ms apart from 1000, nine presses at 1000 (the caret
    /// runs out to eight ahead, every press a Great by the clock), then the last three on time. With
    /// the header set, the presses seven and eight ahead are awarded Meh and the run never breaks
    /// (max_combo 12); without it (every row stored before 347) the press six ahead breaks the run at
    /// the old cap of five. Each row reproduces from its own replay. Crossed, the live replay cannot
    /// reproduce the row the old rule priced, which is only true if the tool read the header.</para>
    ///
    /// <para>PR 3 added a THIRD arm on the same header, bit 1 (<see cref="TypeBeatReplayFrame.InputEra2"/>,
    /// what every live client now writes): no rush cap at all, so the same nine presses are nine
    /// Greats by the clock and the run never breaks. That row too reproduces from its own replay, and
    /// the 347 replay cannot reproduce it, so the tool reads bit 1 as well as bit 0.</para>
    /// </summary>
    [Test]
    public void TheRushCapEraTravelsInTheSecondHeaderThroughTheOsr()
    {
        var ruleset = new TypeBeatRuleset();
        var map = RushMap();

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

            using var source = new ReplayArchive("http://localhost", Path.Combine(dir, "cache"));
            source.IndexPackage(packagePath);

            ReplayArchive.DecodedReplay throughOsr(Replay replay)
            {
                var score = new Score
                {
                    Replay = replay,
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        BeatmapInfo = new BeatmapInfo { MD5Hash = md5 },
                        User = new typebeat.Game.Online.API.Requests.Responses.APIUser { Username = "recalc" },
                        Date = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
                    },
                };

                using var stream = new MemoryStream();
                new LegacyScoreEncoder(score, map).Encode(stream);

                var decoded = source.Decode(stream.ToArray());
                Assert.That(decoded?.Score, Is.Not.Null, "the package must resolve the replay's beatmap hash");
                return decoded!;
            }

            var liveReplay = RushReplay(withExtendedHeader: true);
            var oldReplay = RushReplay(withExtendedHeader: false);
            var era2Replay = RushReplay(withExtendedHeader: true, inputEra2: true);

            var liveDecoded = throughOsr(liveReplay);
            var oldDecoded = throughOsr(oldReplay);
            var era2Decoded = throughOsr(era2Replay);

            // Priced on the map the package decodes to, which is the map the client really played:
            // the .osu round trip is not required to be identical to the in-memory fixture.
            var liveStored = StoredFor(liveDecoded.Playable!, liveReplay);
            var oldStored = StoredFor(oldDecoded.Playable!, oldReplay);
            var era2Stored = StoredFor(era2Decoded.Playable!, era2Replay);

            var live = Recalculation.Run(liveStored, liveDecoded);
            var old = Recalculation.Run(oldStored, oldDecoded);
            var crossed = Recalculation.Run(oldStored, liveDecoded);
            var era2 = Recalculation.Run(era2Stored, era2Decoded);
            var era2Crossed = Recalculation.Run(era2Stored, liveDecoded);

            Assert.Multiple(() =>
            {
                var frames = liveDecoded.Score!.Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
                Assert.That(frames[1].IsConfigExtended && frames[1].RushCapCostsAccuracy, Is.True, "the second header survives the .osr, set");
                Assert.That(oldDecoded.Score!.Replay.Frames.Cast<TypeBeatReplayFrame>().Any(f => f.IsConfigExtended), Is.False);

                Assert.That(live.Skip, Is.EqualTo(SkipReason.None), "the live-era row reproduces from its own .osr");
                Assert.That(live.NewMaxCombo, Is.EqualTo(12), "live era: the rush never broke the run");
                Assert.That(live.NewStatistics!.GetValueOrDefault("meh"), Is.EqualTo(2), "live era: the presses seven and eight ahead are Mehs");

                Assert.That(old.Skip, Is.EqualTo(SkipReason.None), "the pre-347 row reproduces from its own .osr");
                Assert.That(old.NewMaxCombo, Is.LessThan(12), "pre-347 era: the cap of five broke the run");
                Assert.That(old.NewStatistics!.GetValueOrDefault("meh"), Is.Zero, "pre-347 era: every press graded on the clock");

                Assert.That(crossed.Skip, Is.Not.EqualTo(SkipReason.None), "the header is read: the live replay cannot reproduce the old row");

                var era2Frames = era2Decoded.Score!.Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
                Assert.That(era2Frames[1].IsConfigExtended && era2Frames[1].InputEra2 && era2Frames[1].RushCapCostsAccuracy, Is.True,
                    "bit 1 survives the .osr beside bit 0");
                Assert.That(era2Frames[1].AuthoredSyllablesOnly, Is.True, "and bit 2 (backlog 363) beside them: the live word, 7, survives whole");
                Assert.That(frames[1].AuthoredSyllablesOnly, Is.False, "while the 347-era header leaves it clear");
                Assert.That(era2.Skip, Is.EqualTo(SkipReason.None), "the second-input-era row reproduces from its own .osr");
                Assert.That(era2.NewMaxCombo, Is.EqualTo(12), "second input era: no cap, the run never broke");
                Assert.That(era2.NewStatistics!.GetValueOrDefault("meh"), Is.Zero, "second input era: no cap, no Meh award");
                Assert.That(era2Crossed.Skip, Is.Not.EqualTo(SkipReason.None), "bit 1 is read: the 347 replay cannot reproduce the era's row");
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>One line, "abcdefghijkl" over [1000, 1120] (targets 10 ms apart), inside a window to 60000.</summary>
    private static TypeBeatBeatmap RushMap()
    {
        var line = new LyricLine
        {
            RawText = word,
            StartTime = 1000,
            EndTime = 60000,
            // Equal to the unit's end: the .osu writer times the words across the SUNG span, so a
            // longer one would come back from the package with its targets stretched.
            SingEndTime = 1120,
            Units = new[] { new TimedUnit { Text = word, StartTime = 1000, EndTime = 1120 } },
        };

        var map = new TypeBeatBeatmap();
        map.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Rush";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    /// <summary>
    /// Nine presses at 1000 on the unpinned caret, then 'j', 'k', 'l' at 1120. The extended header
    /// carries bit 0 (backlog 347) and, with <paramref name="inputEra2"/>, bit 1 (PR 3) and bit 2
    /// (backlog 363, the live grouping), which is the live word 7.
    /// </summary>
    private static Replay RushReplay(bool withExtendedHeader, bool inputEra2 = false)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, flexibleLines: true, boundedRush: true));

        if (withExtendedHeader)
            replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: inputEra2, authoredSyllablesOnly: inputEra2));

        for (int i = 0; i < 9; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(1000, word[i]));

        for (int i = 9; i < word.Length; i++)
            replay.Frames.Add(new TypeBeatReplayFrame(1120, word[i]));

        return replay;
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

            // Twelve of the thirteen cells, and not thirteen: this fixture CORRECTS its typo, and
            // backlog 210 caps a corrected cell at Ok however well the retype was timed, so the
            // thirteenth arrives as an `ok`. The four-tier ladder is still the thing this test is
            // about; the split is asserted so that the retired ladder's disappearance cannot be
            // confused with the cap's arrival.
            Assert.That(superseded.NewStatistics!["great"], Is.EqualTo(12));
            Assert.That(superseded.NewStatistics!["ok"], Is.EqualTo(1));
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
    /// it: under all of today's rules a run with a FIXED typo gets the STREAK its keypress broke back
    /// (backlog 140) and still pays for the typo in ACCURACY (backlog 210). So it lands on the clean
    /// run's max_combo, its completion and its rank, and strictly below the clean run's accuracy and
    /// total_score.
    ///
    /// <para>THIS TEST USED TO PIN THE EQUALITY, and inverting it is the whole of backlog 210. Until
    /// the cap, a superseded fixed-typo run reached the clean account BIT FOR BIT: the mistype travels
    /// as an accuracy-inert statistic and the corrected cell was graded on the retype's own timing, so
    /// the detour cost nothing that survived to the leaderboard. The equality was the bug, and it is
    /// asserted here as a strict ordering in the direction it was replaced by, per cell and therefore
    /// per run: clean 300 against a corrected cell's capped 100.</para>
    ///
    /// <para>The reproduce sweep holds combo restore AND the credit rule at the stored era, so it
    /// reaches neither account, and that gap is the decision the user made on 2026-08-13.</para>
    /// </summary>
    [Test]
    public void SupersedeGivesAFixedTypoItsStreakBackButNotACleanRunsAccuracy()
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

            // What the fix DOES get back, unchanged by the cap: the streak, and everything that
            // follows from a capped cell still being a hit that counts as typed.
            Assert.That(supersede.NewMaxCombo, Is.EqualTo(clean.MaxCombo), "the fix resumes the streak its keypress broke");
            Assert.That(supersede.NewCompletion, Is.EqualTo(clean.Completion).Within(1e-12));
            Assert.That(supersede.NewRank, Is.EqualTo("X"));

            // ...and what it does NOT: the corrected cell is capped at Ok, so one of the run's
            // thirteen cells is worth 100 where the clean run's is worth 300.
            Assert.That(clean.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(13), "the clean run is thirteen clean cells");
            Assert.That(supersede.NewStatistics!["great"], Is.EqualTo(12));
            Assert.That(supersede.NewStatistics!["ok"], Is.EqualTo(1), "the corrected cell, capped");
            Assert.That(supersede.NewAccuracy, Is.LessThan(clean.Accuracy));
            Assert.That(supersede.NewAccuracy, Is.EqualTo((12 * 300 + 100) / (13 * 300.0)).Within(1e-12));
            Assert.That(supersede.NewTotalScore, Is.LessThan(clean.TotalScore));

            // The reproduce sweep holds the combo axis still, so it does NOT reach the superseded
            // account either. If these two ever agree, one of the two rules stopped being expressible.
            Assert.That(reproduce.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(reproduce.NewMaxCombo, Is.LessThan(supersede.NewMaxCombo));

            // ... and what the combo the fix gives back is WORTH changed shape at backlog 270,
            // which flips this comparison. Combo used to be a MULTIPLIER on the whole play, so the
            // streak the fix resumes outweighed the accuracy the backlog-210 cap costs and the
            // superseded account came out AHEAD in pp. It is an additive BONUS now, worth
            // `maxcombo/notes * 12.5 * (SR_eff - 1)` and nothing more, while the capped cell still
            // costs 100 of this run's 3900 accuracy points through a term that goes as acc^1.80
            // and then through the 80% knee. On a thirteen-cell fixture the accuracy now wins:
            // 171.3 against 179.2.
            //
            // Asserted as the strict ordering it now is, rather than relaxed to "they differ", so
            // that a later retune which flips it back is a decision somebody makes and reads here
            // rather than a test that quietly starts passing again. What has NOT changed is that
            // the two accounts are different and both are priced, which is the claim this file
            // makes: the reproduce sweep holds the combo axis still and cannot reach the
            // superseded account.
            Assert.That(supersede.NewPp, Is.Not.Null);
            Assert.That(reproduce.NewPp, Is.Not.Null);
            Assert.That(supersede.NewPp!.Value, Is.LessThan(reproduce.NewPp!.Value));

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

        // ... and neither expectation stands in for the other. Knowing how many rows will be written
        // is not knowing how many of them are being written on a re-derivation nothing can check.
        Assert.That(await Cli.RunAsync(new[]
        {
            "supersede-apply",
            "--i-understand-this-writes-to-the-database",
            "--i-understand-this-discards-the-stored-numbers",
            "--expect-superseded", "0",
        }), Is.EqualTo(1), "--expect-unreproducible is the other count a blind sweep cannot produce");

        // A malformed expectation is an error too, rather than a null that reads as absent later.
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--expect-superseded", "lots" }), Is.EqualTo(1));
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--expect-unreproducible", "some" }), Is.EqualTo(1));
        Assert.That(await Cli.RunAsync(new[] { "supersede-report", "--expect-unreproducible", "-1" }), Is.EqualTo(1));

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
            var submitted = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.ImmediateMiss, combo_restore_rule, space_timing_rule, rate_window_rule);

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
                "--expect-unreproducible", "0",
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
    /// The population the revert leaves behind, and what the WINDOW RETUNE did to it. Every stored
    /// row is now unreproducible by construction: the three-tier ladder was collapsed to one
    /// symmetric set of windows with no CONFIG bit recording which ladder judged a run, so there is
    /// no axis to hold at a row's own value and reproduction stopped being a proof for anybody. That
    /// is what <c>JudgedOnTheDeletedLadder</c> says now, of every row.
    ///
    /// <para>The backlog 133-to-147 window is still told apart, through
    /// <c>JudgedOnTheFourTierCharacterLadder</c>, because its mismatch has a different SHAPE: an
    /// era-2 row was graded on CHARACTER DISTANCE in four tiers, so its <c>maximum_statistics</c>
    /// carries <c>perfect</c> and no <c>great</c> and the key itself moves, where every other row
    /// merely lands on different rungs of the same three. Both directions of that discriminator are
    /// pinned below, on the shape the classifier actually meets, and through
    /// <c>ScoringContract.JudgedUnderTheFourthTier</c> itself rather than the tool's own reading of
    /// those keys.</para>
    ///
    /// <para>The anomaly row (a stored max_combo the run never reached) is kept as the reminder of
    /// what the split used to be for. It is no longer separable BY REPRODUCTION, which is exactly
    /// the cost the retune imposed, and what still catches it is the same-run gate.</para>
    /// </summary>
    [Test]
    public void TheDeletedLadderPopulationIsTheWholeTableAndTheFourTierWindowIsStillNamed()
    {
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        var eraOne = StoredFor(map, replay);
        var eraTwo = FourTierEra(eraOne) with { ScoreId = 2 };

        // Not from the window, and still does not reproduce: the stored row claims a combo the run
        // never reached. That is the anomaly an operator has to see.
        var anomaly = eraOne with { ScoreId = 3, MaxCombo = eraOne.MaxCombo - 4 };

        var results = new[]
        {
            Recalculation.Run(eraOne, Decoded(map, replay), mode: RecalcMode.Supersede),
            Recalculation.Run(eraTwo, Decoded(map, replay), mode: RecalcMode.Supersede),
            Recalculation.Run(anomaly, Decoded(map, replay), mode: RecalcMode.Supersede),
        };

        var plan = WritePlan.Build(results, RecalcMode.Supersede, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

        var written = new StringWriter();
        Report.Print(results, plan, wholeTable: true, written);
        string text = written.ToString();

        var eraTwoMaximum = WireCounts.Parse(eraTwo.MaximumStatisticsJson);
        var eraOneMaximum = WireCounts.Parse(eraOne.MaximumStatisticsJson);

        Assert.Multiple(() =>
        {
            // The data shape the discriminator is reading, stated rather than assumed.
            Assert.That(eraTwoMaximum.ContainsKey("perfect"), Is.True, "an era-2 row's per-cell maximum is a perfect");
            Assert.That(eraTwoMaximum.ContainsKey("great"), Is.False);
            Assert.That(eraOneMaximum.ContainsKey("great"), Is.True, "an era-1 row's per-cell maximum is a great");
            Assert.That(eraOneMaximum.ContainsKey("perfect"), Is.False);

            // The server's own predicate, both ways, and the tool reading it through that predicate.
            Assert.That(Typebeat.Web.Scoring.ScoringContract.JudgedUnderTheFourthTier(eraTwoMaximum), Is.True);
            Assert.That(Typebeat.Web.Scoring.ScoringContract.JudgedUnderTheFourthTier(eraOneMaximum), Is.False);
            Assert.That(eraTwo.JudgedOnTheFourTierCharacterLadder, Is.True);
            Assert.That(eraOne.JudgedOnTheFourTierCharacterLadder, Is.False);
            Assert.That(anomaly.JudgedOnTheFourTierCharacterLadder, Is.False, "a row can fail to reproduce without being from the window");

            // ...and the wider fact the retune made true of all three, which is what the guard counts.
            Assert.That(results.Select(r => r.Stored.JudgedOnTheDeletedLadder), Is.All.True);

            // All three rows are superseded; only their VISIBILITY differs. No new refusal, no new
            // skip reason, and the era-2 row is not treated as unreplayable.
            Assert.That(results.Select(r => r.Skip), Is.All.EqualTo(SkipReason.None));
            Assert.That(results.Select(r => r.Reproduced), Is.EquivalentTo(new[] { true, false, false }));
            Assert.That(plan.Unreplayable, Is.Empty);

            // The population is the whole run now, and that is the count the guard reads.
            Assert.That(plan.DeletedLadderWindow.Select(r => r.Stored.ScoreId), Is.EquivalentTo(new long[] { 1, 2, 3 }));

            Assert.That(text, Does.Contain("ON A DELETED LADDER          3"));
            Assert.That(text, Does.Contain("--expect-unreproducible"));
            Assert.That(text, Does.Contain("reproduced exactly           1"));
            Assert.That(text, Does.Contain("did not reproduce            2"));
            Assert.That(text, Does.Contain("of those, 133-to-147       1"));

            // The anomaly is named by id so it can be looked at; the window row is not, because
            // there is nothing to look at.
            Assert.That(text, Does.Contain("score 3"));
        });
    }

    /// <summary>
    /// The offline path (<c>--offline</c>, docs/score-recalc-export.md) carries the era stamp, which
    /// is the path this sweep is most likely to be reviewed on. It has no <c>scores</c> table to read
    /// <c>maximum_statistics</c> out of, so the classification lives or dies on the <c>.osr</c>: its
    /// trailing <c>LegacyReplaySoloScoreInfo</c> blob carries <c>maximum_statistics</c> verbatim, and
    /// the decoder only synthesises that dictionary when the blob's is empty. So an exported era-2
    /// replay classifies exactly as its database row would, and the export needs no extra field.
    ///
    /// <para>Round-tripped through the real encoder and the real decoder rather than asserted about,
    /// because "the blob carries it" is a claim about a file format.</para>
    /// </summary>
    [Test]
    public void AnOfflineExportCarriesTheEraStampThroughTheOsr()
    {
        var ruleset = new TypeBeatRuleset();
        var map = Beatmap();
        var replay = FixedTypoReplay(map);

        var osuText = new StringWriter();
        ruleset.EncodeToNativeFormat(map, null, osuText);
        byte[] osu = Encoding.UTF8.GetBytes(osuText.ToString());
        string md5 = Convert.ToHexStringLower(MD5.HashData(osu));

        string dir = Path.Combine(Path.GetTempPath(), "typebeat-recalc-era-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "replays"));
        Directory.CreateDirectory(Path.Combine(dir, "sets"));

        try
        {
            using (var zip = ZipFile.Open(Path.Combine(dir, "sets", "set.osz"), ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("map.osu").Open())
                entry.Write(osu);

            var submitted = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.ImmediateMiss, combo_restore_rule, space_timing_rule, rate_window_rule);

            Write(77, submitted.Statistics, submitted.MaximumStatistics);
            Write(78, Perfected(submitted.Statistics), Perfected(submitted.MaximumStatistics));

            using var source = new ReplayArchive("http://localhost", Path.Combine(dir, "cache"));
            source.IndexPackageDirectory(Path.Combine(dir, "sets"));

            var loaded = OfflineScores.Load(dir, source, null).ToDictionary(s => s.ScoreId);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Keys, Is.EquivalentTo(new long[] { 77, 78 }));

                Assert.That(WireCounts.Parse(loaded[78].MaximumStatisticsJson).ContainsKey("perfect"), Is.True,
                    "the .osr blob is the offline run's only copy of maximum_statistics");
                Assert.That(loaded[78].JudgedOnTheFourTierCharacterLadder, Is.True, "an offline era-2 row classifies as one");
                Assert.That(loaded[77].JudgedOnTheFourTierCharacterLadder, Is.False, "and an era-1 row does not");
            });

            void Write(long scoreId, IReadOnlyDictionary<HitResult, int> statistics, IReadOnlyDictionary<HitResult, int> maximumStatistics)
            {
                var score = new Score
                {
                    Replay = replay,
                    ScoreInfo = new ScoreInfo
                    {
                        Ruleset = ruleset.RulesetInfo,
                        BeatmapInfo = new BeatmapInfo { MD5Hash = md5 },
                        User = new typebeat.Game.Online.API.Requests.Responses.APIUser { Username = "recalc" },
                        Date = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        Statistics = new Dictionary<HitResult, int>(statistics),
                        MaximumStatistics = new Dictionary<HitResult, int>(maximumStatistics),
                        MaxCombo = submitted.MaxCombo,
                        TotalScore = submitted.TotalScore,
                        Accuracy = submitted.Accuracy,
                        Rank = submitted.Rank,
                    },
                };

                using var stream = File.Create(Path.Combine(dir, "replays", $"{scoreId}.osr"));
                new LegacyScoreEncoder(score, map).Encode(stream);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The same re-tiering <see cref="FourTierEra"/> does, on the enum rather than the wire.</summary>
    private static Dictionary<HitResult, int> Perfected(IReadOnlyDictionary<HitResult, int> counts)
    {
        var copy = new Dictionary<HitResult, int>(counts);

        if (copy.Remove(HitResult.Great, out int top))
            copy[HitResult.Perfect] = copy.GetValueOrDefault(HitResult.Perfect) + top;

        return copy;
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

    #region The two eras backlog 151 made expressible

    /// <summary>
    /// "ab cd" as two words far apart: a = 0, b = 3000, ' ' = 6000 (the first unit's end),
    /// c = 20000, d = 23000. The gap is what lets the SPACE be pressed grossly late while every later
    /// press still lands dead on target, so the only thing the space era can move is the space.
    /// </summary>
    private static TypeBeatBeatmap SpacedBeatmap() => OneLine(new LyricLine
    {
        RawText = "ab cd",
        StartTime = 0,
        EndTime = 40000,
        SingEndTime = 26000,
        Units = new[]
        {
            new TimedUnit { Text = "ab", StartTime = 0, EndTime = 6000 },
            new TimedUnit { Text = "cd", StartTime = 20000, EndTime = 26000 },
        },
    });

    /// <summary>"abc" over [0, 12000], so the cells target 0, 4000 and 8000.</summary>
    private static TypeBeatBeatmap PlainBeatmap() => OneLine(new LyricLine
    {
        RawText = "abc",
        StartTime = 0,
        EndTime = 20000,
        SingEndTime = 12000,
        Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
    });

    private static TypeBeatBeatmap OneLine(LyricLine line)
    {
        var map = new TypeBeatBeatmap();
        map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Era";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    private static Replay Pressed(params (double time, char c)[] presses)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

        foreach ((double time, char c) in presses)
            replay.Frames.Add(new TypeBeatReplayFrame(time, c));

        return replay;
    }

    /// <summary>
    /// The run re-derived under one point of the search space, with every one of the six era axes
    /// taken from the candidate rather than written out at the call site. Since backlog 158 the typo
    /// rule is a member of <see cref="SearchedEra"/> like the others, since backlog 199 so is the
    /// off-time rule and since backlog 210 so is the correction cap; a test that passed a literal
    /// <c>TypoRule.ImmediateMiss</c> next to
    /// <c>era.Space</c> would silently stop sweeping the axis it claims to sweep.
    /// </summary>
    private static TypeBeatReplayAccount ScoreUnder(IBeatmap map, Replay replay, SearchedEra era, params Mod[] mods)
        => TypeBeatReplayScorer.Score(map, mods, replay, era.Typo, era.Combo, era.Space, era.Rate, offTimeRule: era.OffTime, creditRule: era.Credit, worthRule: era.Worth);

    /// <summary>Every cell on target except the SPACE, 2500 ms late: outside even the Meh window.</summary>
    private static Replay LateSpaceReplay()
        => Pressed((0, 'a'), (3000, 'b'), (8500, ' '), (20000, 'c'), (23000, 'd'));

    /// <summary>
    /// WHY BACKLOG 151 EXISTS. Backlog 148 took the spacebar out of the timing challenge, and every
    /// map has spaces, so before the era switch the reproduce pass re-graded every stored row's
    /// spaces on a rule no stored row was played under: a loosely hit space came back as a top-tier
    /// hit that never breaks combo, moving both <c>statistics</c> and <c>max_combo</c>, which are
    /// exactly the two quantities the gate compares. The whole table read as corrupt.
    ///
    /// <para>Both halves are asserted, because the first alone would pass if the tool were judging
    /// under the live rule AND the stored row had been built under it too. The second is a row built
    /// as today's client produces it, and the two eras have to come back as DIFFERENT accounts of the
    /// same run or the switch is doing nothing.</para>
    ///
    /// <para>THE SECOND HALF INVERTED AT BACKLOG 156, and the inversion is the point rather than a
    /// weakening. It used to assert that a row judged on today's space rule was REFUSED, on the
    /// premise that no stored client had produced one. That premise expired when 148 shipped: such
    /// rows exist now, they grow with every play, and refusing them was the bug. So the assertion is
    /// now that the pass reproduces it and pins it to the era that judged it, while the accounts
    /// staying different is what still proves the switch is live.</para>
    /// </summary>
    [Test]
    public void ARowPlayedBeforeTheSpaceExemptionReproducesAgain()
    {
        var map = SpacedBeatmap();
        var replay = LateSpaceReplay();

        var storedEra = Recalculation.Run(StoredFor(map, replay), Decoded(map, replay));

        var liveEra = Recalculation.Run(
            StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed),
            Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(storedEra.Skip, Is.EqualTo(SkipReason.None), "a pre-148 row must be re-derivable");
            Assert.That(storedEra.Reproduced, Is.True);
            Assert.That(storedEra.ReproducedUnderEra, Is.EqualTo(Recalculation.DefaultEra));
            Assert.That(storedEra.EraProvedByReconstruction, Is.False, "it came back under the default, so nothing was searched");

            // The era it reproduced: the late space was a Lagging press that spent its cell on a
            // Miss and ended the run two characters in.
            Assert.That(storedEra.OldRuleStatistics!["miss"], Is.EqualTo(1));
            Assert.That(storedEra.OldRuleStatistics!["great"], Is.EqualTo(4));
            Assert.That(storedEra.OldRuleMaxCombo, Is.EqualTo(2));

            // ...and the harness is really using it: the same run under today's space rule is a
            // materially different account, which is the whole reason the axis needs a switch.
            Assert.That(liveEra.Skip, Is.EqualTo(SkipReason.None), "a post-148 row is re-derivable too, under its own era");
            Assert.That(liveEra.Reproduced, Is.True);
            Assert.That(liveEra.EraProvedByReconstruction, Is.True, "the default did not re-derive it, so the era was searched for");
            Assert.That(liveEra.ReproducedUnderEra!.Value.Space, Is.EqualTo(SpaceTimingRule.Untimed));

            Assert.That(liveEra.OldRuleStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(liveEra.OldRuleStatistics!["great"], Is.EqualTo(5));
            Assert.That(liveEra.OldRuleMaxCombo, Is.EqualTo(5));
        });
    }

    /// <summary>
    /// The same for backlog 150. Three presses 200 ms late are an Ok apiece on the base ladder
    /// (Great is 150 ms), which is what a pre-150 Double Time client stored; today's rule stretches
    /// the ladder by the clock rate, so at 1.5x Great reaches 225 and all three are paid. Narrower
    /// than the space era (it only reaches DT / NC / HT rows) but it moves the same two quantities on
    /// the rows it does reach.
    ///
    /// <para>The offset is 200 and not the 500 it was written with because the ladder was retuned:
    /// against the old asymmetric one 500 late was an Ok that 1.5x lifted into Great, and against the
    /// symmetric 150 / 300 / 600 it is a Meh on BOTH arms, so the fixture would have stopped
    /// discriminating the axis it exists to discriminate while still passing every gate.</para>
    ///
    /// <para>Inverted at backlog 156 for the same reason as the space case above: a DT row played
    /// since 150 shipped exists now, so the pass proves its era instead of refusing it. The map has
    /// no spaces, so the space axis is inert here and the row is pinned to the live PAIR: that is the
    /// harmless ambiguity the search order resolves, and it is asserted on its own further down.</para>
    /// </summary>
    [Test]
    public void ARowPlayedBeforeTheRateWindowScalingReproducesAgain()
    {
        var map = PlainBeatmap();
        var replay = Pressed((200, 'a'), (4200, 'b'), (8200, 'c'));
        Mod[] doubleTime = { new TypeBeatModDoubleTime { SpeedChange = { Value = 1.5 } } };
        const string mods_json = @"[{""acronym"":""DT"",""settings"":{""speed_change"":1.5}}]";

        var storedEra = Recalculation.Run(
            StoredFor(map, replay, mods: doubleTime) with { ModsJson = mods_json },
            Decoded(map, replay, doubleTime));

        var liveEra = Recalculation.Run(
            StoredFor(map, replay, rateRule: RateWindowRule.ScaledByRate, mods: doubleTime) with { ModsJson = mods_json },
            Decoded(map, replay, doubleTime));

        Assert.Multiple(() =>
        {
            Assert.That(storedEra.Skip, Is.EqualTo(SkipReason.None), "a pre-150 rate row must be re-derivable");
            Assert.That(storedEra.Reproduced, Is.True);
            Assert.That(storedEra.ReproducedUnderEra, Is.EqualTo(Recalculation.DefaultEra));
            Assert.That(storedEra.OldRuleStatistics!["ok"], Is.EqualTo(3), "the base ladder graded 200 ms late as an Ok");

            Assert.That(liveEra.Skip, Is.EqualTo(SkipReason.None), "a post-150 rate row is re-derivable under its own era");
            Assert.That(liveEra.Reproduced, Is.True);
            Assert.That(liveEra.EraProvedByReconstruction, Is.True);
            Assert.That(liveEra.ReproducedUnderEra!.Value.Rate, Is.EqualTo(RateWindowRule.ScaledByRate));
            Assert.That(liveEra.OldRuleStatistics!["great"], Is.EqualTo(3), "scaled by 1.5x the same 200 ms is inside the Great window");
        });
    }

    /// <summary>
    /// The supersede sweep moves the two new axes as well, which is the other half of the switch:
    /// re-judged under all of today's rules the same run's late space becomes a top-tier hit and the
    /// run is unbroken. Worth pinning separately from the reproduce case, because a switch wired into
    /// only one of the two passes would leave the sweep writing numbers off the stored ladder.
    /// </summary>
    [Test]
    public void SupersedeAppliesTodaysSpaceRuleToAPreExemptionRow()
    {
        var map = SpacedBeatmap();
        var replay = LateSpaceReplay();
        var stored = StoredFor(map, replay);

        var superseded = Recalculation.Run(stored, Decoded(map, replay), mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            Assert.That(superseded.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(superseded.Reproduced, Is.True, "the diagnostic arm still uses the stored era");

            Assert.That(superseded.NewStatistics!["great"], Is.EqualTo(5));
            Assert.That(superseded.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(superseded.NewMaxCombo, Is.EqualTo(5));
            Assert.That(superseded.NewCompletion, Is.EqualTo(1));
            Assert.That(superseded.Moves, Is.True);
        });
    }

    /// <summary>
    /// The REPRODUCE sweep still varies the typo rule alone. Its second pass holds the spacebar at
    /// the stored era exactly as it holds combo restore there, so a row with a loosely hit space is
    /// not quietly handed backlog 148's gain by a sweep that only claims to be about typos.
    /// </summary>
    [Test]
    public void ReproduceHoldsTheSpaceEraStillOnBothArms()
    {
        var map = SpacedBeatmap();
        var replay = LateSpaceReplay();
        var stored = StoredFor(map, replay);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));

            // Nothing in this run is a typo, so varying the typo rule alone must move nothing at all.
            Assert.That(result.NewStatistics, Is.EquivalentTo(result.OldRuleStatistics!));
            Assert.That(result.NewMaxCombo, Is.EqualTo(result.OldRuleMaxCombo));
            Assert.That(result.Moves, Is.False, "a reproduce sweep must not apply backlog 148's gain");
        });
    }

    #endregion

    #region The typo era, which is the one axis decided PER ROW (backlog 155)

    /// <summary>
    /// WHY BACKLOG 155 EXISTS. The typo rule is the one era axis that moved while the score table was
    /// already filling: the deferred rule has been the only rule live play uses since backlog 109, and
    /// since backlog 126 the cell it leaves standing carries a key of its own (<c>good</c>, the game's
    /// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>). The reproduce pass pinned every row to the older
    /// rule regardless, so each of those rows came back with every uncorrected typo turned into a MISS
    /// and was reported as a row nobody could explain.
    ///
    /// <para>The signature is asserted rather than described, because it is what identified the
    /// population in production: re-derived <c>miss</c> equals stored <c>miss</c> plus stored
    /// <c>good</c>, on every affected row. That is also the non-vacuity of this test, in the same
    /// object: the row demonstrably does NOT reproduce under the rule the pass used to apply.</para>
    /// </summary>
    [Test]
    public void ARowJudgedSinceBacklog126IsReproducedUnderTheRuleThatJudgedIt()
    {
        var map = Beatmap();
        var replay = UnfixedTypoReplay(map);
        var stored = StoredFor(map, replay, typoRule: TypoRule.Deferred);

        var storedStatistics = WireCounts.Parse(stored.StatisticsJson);

        // The same run under the rule the pass used to pin every row to.
        var underTheRetiredRule = TypeBeatReplayScorer.Score(
            map, Array.Empty<Mod>(), replay, TypoRule.ImmediateMiss, combo_restore_rule, space_timing_rule, rate_window_rule);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            // The data shape the discriminator reads, stated rather than assumed.
            Assert.That(storedStatistics.GetValueOrDefault("good"), Is.EqualTo(1), "an uncorrected typo takes a key of its own");
            Assert.That(Typebeat.Web.Scoring.ScoringContract.CarriesAnUncorrectedTypo(storedStatistics), Is.True);
            Assert.That(stored.ProvablyJudgedUnderTheDeferredTypoRule, Is.True);
            Assert.That(Recalculation.StoredEraTypoRuleFor(stored), Is.EqualTo(TypoRule.Deferred));

            // The production signature, and this test's non-vacuity: under the retired rule the row
            // does not come back, and it comes back wrong in exactly one way.
            Assert.That(underTheRetiredRule.Statistics.GetValueOrDefault(HitResult.Miss),
                Is.EqualTo(storedStatistics.GetValueOrDefault("miss") + storedStatistics.GetValueOrDefault("good")),
                "re-derived miss == stored miss + stored good is what identified the population in prod");
            Assert.That(underTheRetiredRule.Statistics.GetValueOrDefault(HitResult.Good), Is.Zero,
                "the retired rule cannot produce the key at all, which is what makes the key a proof");

            // ...and the pass now reproduces it instead of refusing it.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.OldRuleStatistics!.GetValueOrDefault("good"), Is.EqualTo(1));

            // THE TYPO AXIS HAS NOTHING LEFT TO REPRICE, which is what this test has always said,
            // and it is now stated on the quantities that axis moves rather than on `Moves`:
            // today's typo rule already judged this row, so the varied arm re-derives the same
            // account, and the reproduce arm holds the worth era, so the total is the row's own.
            Assert.That(result.NewStatistics, Is.EquivalentTo(result.OldRuleStatistics!));
            Assert.That(result.NewMaxCombo, Is.EqualTo(stored.MaxCombo));
            Assert.That(result.NewTotalScore, Is.EqualTo(stored.TotalScore));
            Assert.That(result.NewCompletion, Is.EqualTo(stored.Completion).Within(1e-12));
            Assert.That(result.NewRank, Is.EqualTo(stored.Rank));

            // WHAT DOES MOVE IS ACCURACY, AND ONLY ACCURACY (backlog 213). The server's contract
            // weights the `good` key at a miss's 0 unconditionally, where this row was played (and
            // stored) under the 50 backlog 124 gave it, so re-reading the row through today's
            // contract costs it exactly the Meh credit those cells used to carry. Derived from the
            // row's own dictionaries rather than pasted: one cell's lost 50, over the map's total
            // base score of 300 per cell.
            int cells = Typebeat.Web.Scoring.ScoringContract.CountAccuracyAffecting(WireCounts.Parse(stored.MaximumStatisticsJson));
            double foldCost = 50.0 * storedStatistics.GetValueOrDefault("good") / (300.0 * cells);

            Assert.That(foldCost, Is.GreaterThan(0), "the fixture only means anything if there is a cell to reprice");
            Assert.That(stored.Accuracy - result.NewAccuracy, Is.EqualTo(foldCost).Within(1e-12));
            Assert.That(result.Moves, Is.True, "backlog 213 reprices this row's accuracy, and that is the whole of the move");
        });
    }

    /// <summary>
    /// The other direction of the pin, and the reason the discriminator cannot simply be "assume the
    /// newer rule". A row from before backlog 126 stores its uncorrected typo as a MISS, carries no
    /// <c>good</c> key, and must still be re-derived under the rule that judged it: pinning it to the
    /// deferred rule would hand it the cell back and report an honest row as unreproducible, which is
    /// the same failure backlog 155 fixes, aimed at the other population.
    /// </summary>
    [Test]
    public void ARowWithNoUncorrectedTypoKeyKeepsTheOlderTypoRule()
    {
        var map = Beatmap();
        var replay = UnfixedTypoReplay(map);
        var stored = StoredFor(map, replay);

        var storedStatistics = WireCounts.Parse(stored.StatisticsJson);
        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(storedStatistics.ContainsKey("good"), Is.False, "the retired rule spent the cell on a miss");
            Assert.That(storedStatistics.GetValueOrDefault("miss"), Is.EqualTo(1));
            Assert.That(stored.ProvablyJudgedUnderTheDeferredTypoRule, Is.False);
            Assert.That(Recalculation.StoredEraTypoRuleFor(stored), Is.EqualTo(TypoRule.ImmediateMiss));

            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);

            // And the sweep still reports what today's rule alone makes of the row, which for this
            // one is the whole point of a reproduce sweep: the typo stops being a miss.
            Assert.That(result.NewStatistics!.GetValueOrDefault("good"), Is.EqualTo(1));
            Assert.That(result.NewStatistics!.GetValueOrDefault("miss"), Is.Zero);
            Assert.That(result.Moves, Is.True);
        });
    }

    /// <summary>
    /// THE DISCRIMINATOR RUNS IN ONE DIRECTION ONLY, and this is the case that says so: a row judged
    /// under the DEFERRED rule that left no wrong character standing has no <c>good</c> key to carry,
    /// so the row proves nothing about its own era and is pinned to the older rule like any other
    /// unprovable row.
    ///
    /// <para>That is not a mistake being tolerated, it is why the one-directional test is sound: with
    /// no typo in the run the two rules are the same judgement, so the row reproduces under either.
    /// Only a bidirectional reading would be a bug, and it would be an expensive one, since it would
    /// send every clean modern row back to a rule it was never played under.</para>
    ///
    /// <para>THIS ROW IS THE HARMLESS HALF OF THE ABSENCE, and it still resolves exactly as backlog 155
    /// left it: the older rule is what it starts at and the older rule reproduces it, so backlog 158's
    /// search never runs for it. The other half of the absence, a typo that was CORRECTED, is where the
    /// two rules do NOT agree, and that one is settled by reconstruction in
    /// <see cref="AModernRowWhoseTypoWasCorrectedHasItsTypoEraProvedByReconstruction"/>.</para>
    /// </summary>
    [Test]
    public void AbsenceOfTheKeyProvesNothingSoACleanDeferredEraRowStaysOnTheOlderRule()
    {
        var map = Beatmap();
        var replay = CleanReplay(map);

        var deferredEra = StoredFor(map, replay, typoRule: TypoRule.Deferred);
        var olderEra = StoredFor(map, replay);

        var result = Recalculation.Run(deferredEra, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(deferredEra.StatisticsJson, Is.EqualTo(olderEra.StatisticsJson),
                "with no typo in the run the two rules produce the same account, which is why the absence is not evidence");
            Assert.That(WireCounts.Parse(deferredEra.StatisticsJson).ContainsKey("good"), Is.False,
                "a run with nothing left uncorrected has no key to prove its era with");

            Assert.That(deferredEra.ProvablyJudgedUnderTheDeferredTypoRule, Is.False);
            Assert.That(Recalculation.StoredEraTypoRuleFor(deferredEra), Is.EqualTo(TypoRule.ImmediateMiss),
                "absence of evidence pins the row to the default; it is not evidence of the older rule");

            // And the default costs this row nothing, which is what makes it safe.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
        });
    }

    /// <summary>
    /// The population is NAMED in both sweeps, on the precedent of the 133-to-147 window: a reader has
    /// to be able to see how many rows the pass pinned to today's typo rule, because that is what the
    /// re-derivation of those rows is being done under. Folding it into a reproduction rate would
    /// leave the fix visible only as a percentage that got better.
    /// </summary>
    [Test]
    public void ThePinnedPopulationIsNamedInBothSweeps()
    {
        var map = Beatmap();
        var unfixed = UnfixedTypoReplay(map);
        var corrected = FixedTypoReplay(map);

        var deferredEra = StoredFor(map, unfixed, typoRule: TypoRule.Deferred) with { ScoreId = 7 };
        var olderEra = StoredFor(map, corrected) with { ScoreId = 8 };

        foreach (var mode in new[] { RecalcMode.Reproduce, RecalcMode.Supersede })
        {
            var results = new[]
            {
                Recalculation.Run(deferredEra, Decoded(map, unfixed), mode: mode),
                Recalculation.Run(olderEra, Decoded(map, corrected), mode: mode),
            };

            var plan = WritePlan.Build(results, mode, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

            var written = new StringWriter();
            Report.Print(results, plan, wholeTable: true, written);
            string text = written.ToString();

            Assert.Multiple(() =>
            {
                Assert.That(plan.PinnedToTheDeferredTypoRule.Select(r => r.Stored.ScoreId), Is.EquivalentTo(new long[] { 7 }), $"{mode}: one row carries the key");
                Assert.That(text, Does.Contain("PINNED TO TypoRule.Deferred  1"), $"{mode}: the headline names the population");
                Assert.That(text, Does.Contain("of these, judged since 126 1"), $"{mode}: so does the reproduction section");
                Assert.That(results.Select(r => r.Reproduced), Is.All.True, $"{mode}: both rows are re-derived under the rule that judged them");
            });
        }
    }

    #endregion

    #region The eras PROVED BY RECONSTRUCTION: spacebar and rate windows (156), combo restore (157), the typo rule where no key proves it (158)

    /// <summary>
    /// The space 200 ms late: an Ok on the timed ladder (Great is 150 ms, Ok 300) and a top-tier hit
    /// on the untimed one, with no combo break either way. That is the PRODUCTION SIGNATURE of
    /// backlog 156 in its smallest form, <c>great</c> falling while <c>ok</c> rises, and it is a
    /// better fixture for the search than <see cref="LateSpaceReplay"/> because the two eras differ in
    /// exactly one cell's tier and in nothing else.
    ///
    /// <para>It was written at 500, which was an Ok on the old asymmetric ladder and is a Meh on the
    /// symmetric one that replaced it. The fixture is about the SMALLEST tier move the search has to
    /// resolve, so it follows the ladder: 200 is one rung off Great here exactly as 500 was
    /// there.</para>
    /// </summary>
    private static Replay MarginalSpaceReplay()
        => Pressed((0, 'a'), (3000, 'b'), (6200, ' '), (20000, 'c'), (23000, 'd'));

    /// <summary>
    /// The same run with a TYPO CORRECTED in it as well, which is what makes the combo axis bite: the
    /// wrong <c>x</c> breaks the streak, and only <see cref="ComboRestoreRule.OnFix"/> gives it back
    /// when the cell is retyped. So this fixture moves on the space axis (a tier) and on the combo
    /// axis (<c>max_combo</c>) independently, which is exactly the shape production shows at scores
    /// 5410 and 5414 and the reason backlog 157 exists.
    /// </summary>
    private static Replay MarginalSpaceWithACorrectedTypoReplay()
        => Pressed((0, 'a'), (3000, 'x'), (3000, TypeBeatReplayFrame.BACKSPACE), (3000, 'b'), (6200, ' '), (20000, 'c'), (23000, 'd'));

    /// <summary>
    /// WHY BACKLOG 156 EXISTS. Backlog 148 and 150 shipped while the score table was already filling,
    /// exactly as backlog 126 did for the typo rule, so the table now holds rows from both sides of
    /// the spacebar and the rate windows. Neither axis leaves a KEY the way an uncorrected typo does,
    /// so there is nothing to read off a row and 155's discriminator has no analogue here: the era has
    /// to be PROVED, by re-deriving the row under each combination and keeping the one that
    /// reproduces it exactly.
    ///
    /// <para>THE ERA IS NOT DATED, deliberately. The release instant is known to the second (the web
    /// push is the deploy), but the server deploying is not the client updating: a player still on the
    /// old build submits old-era statistics for as long as they do not update, and a timestamp
    /// boundary would put those rows on the wrong ladder without anything in the output showing
    /// it.</para>
    ///
    /// <para>The signature is asserted rather than described, and it doubles as this test's
    /// non-vacuity: under the pre-release default the row demonstrably does NOT come back, and it
    /// comes back wrong in exactly the way production reported (a top-tier hit demoted to an
    /// <c>ok</c>).</para>
    /// </summary>
    [Test]
    public void ARowPlayedSinceTheReleaseHasItsWindowAxesProvedByReconstruction()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceReplay();

        // The row today's client produces for this run.
        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed, rateRule: RateWindowRule.ScaledByRate);
        var storedStatistics = WireCounts.Parse(stored.StatisticsJson);

        // The same run under the era the pass tries first, i.e. what it used to pin every row to.
        var underTheDefault = ScoreUnder(map, replay, Recalculation.DefaultEra);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(storedStatistics["great"], Is.EqualTo(5), "the untimed spacebar takes the top tier however loosely it was hit");
            Assert.That(storedStatistics.ContainsKey("ok"), Is.False);

            // The production signature, and the non-vacuity: the default arm demotes the space.
            Assert.That(underTheDefault.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(4), "great falls...");
            Assert.That(underTheDefault.Statistics.GetValueOrDefault(HitResult.Ok), Is.EqualTo(1), "...while ok rises, which is how prod identified the population");

            // ...and the pass now proves the era instead of reporting the row as unexplained.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.EraProvedByReconstruction, Is.True);
            Assert.That(result.ReproducedUnderEra!.Value.Space, Is.EqualTo(SpaceTimingRule.Untimed));
            Assert.That(result.OldRuleStatistics!["great"], Is.EqualTo(5));
        });
    }

    /// <summary>
    /// THE PROPERTY MOST WORTH A TEST OF ITS OWN: a row NO combination reproduces is left
    /// unexplained, exactly as it is today, and never quietly assigned an era. A search that always
    /// found an answer would turn the reproduce pass from a proof into a shrug, and the pass is the
    /// only thing standing between a supersede sweep and a row it does not understand.
    ///
    /// <para>The row is built with the third signature production actually shows (scores 4534, 4912
    /// and 5035, which differ from their replays by ONE in <c>max_combo</c> or <c>combo_break</c>
    /// alone). Backlog 156 does not fix that signature and must not appear to: those rows have to
    /// still report as unexplained afterwards.</para>
    /// </summary>
    [Test]
    public void ARowNoCombinationReproducesIsLeftUnexplainedRatherThanGivenAnEra()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceReplay();

        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed) with { MaxCombo = 6 };

        var reproduce = Recalculation.Run(stored, Decoded(map, replay));
        var supersede = Recalculation.Run(stored, Decoded(map, replay), mode: RecalcMode.Supersede);

        var plan = WritePlan.Build(new[] { supersede }, RecalcMode.Supersede, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

        Assert.Multiple(() =>
        {
            // Stated rather than assumed: the whole search space really is exhausted on this row, so
            // "no era was assigned" is a fact about the data and not about the search stopping early.
            foreach (var era in Recalculation.EraSearchFor(stored))
            {
                var account = ScoreUnder(map, replay, era);

                Assert.That(account.MaxCombo, Is.Not.EqualTo(stored.MaxCombo), $"{era} must not reproduce this row");
            }

            Assert.That(reproduce.Skip, Is.EqualTo(SkipReason.NotReproducible), "nothing is written for a row nobody can explain");
            Assert.That(reproduce.Reproduced, Is.False);
            Assert.That(reproduce.ReproducedUnderEra, Is.Null, "no era reproduced it, so it has none");
            Assert.That(reproduce.EraProvedByReconstruction, Is.False);

            // The mismatch reported is the DEFAULT arm's, unchanged from before the search existed,
            // so an operator reading an unexplained row reads the same line they read yesterday.
            Assert.That(reproduce.Detail, Does.Contain("max_combo 6 -> 5"));

            Assert.That(supersede.Reproduced, Is.False, "and a supersede sweep still reports it as not understood");
            Assert.That(plan.PinnedByEraSearch, Is.Empty);
        });
    }

    /// <summary>
    /// AMBIGUITY IS THE NORMAL CASE, NOT AN EDGE CASE, and it is harmless. The rate axis is inert on a
    /// row with no rate mod and the space axis is inert on a map with no spaces, so a searched row
    /// usually reproduces under more than one combination. Reproducing means re-deriving
    /// <c>statistics</c> and <c>max_combo</c> EXACTLY, which are the two quantities the server stores
    /// verbatim, so every combination that reproduces has produced the same account: nothing
    /// downstream can tell which was picked.
    ///
    /// <para>What the search order buys is therefore determinism and a stable label in the report, not
    /// correctness. It is asserted as such: the arms are shown to AGREE first, and the pin is then
    /// checked against the order rather than against a hand-written expectation.</para>
    ///
    /// <para>Backlog 157 made this commoner rather than rarer: the combo axis is inert on a run with
    /// no typo corrected in it, which is most runs, so folding it in doubled the number of arms that
    /// reproduce this row from two to four. Backlog 158 doubled it again to eight: a run with NO typo
    /// in it is judged identically by both typo rules, so every
    /// arm that reproduces it under one reproduces it under the other. Backlog 199 doubled it a third
    /// time, to sixteen, on the axis that reaches the most rows of all: this run never strikes a
    /// character off the ladder, so the off-time arms grade it identically too. Backlog 210 doubled
    /// it a fourth time, to thirty-two: this run has no typo to correct, so the credit arms agree as
    /// well. Backlog 213 doubled it a fifth time, to sixty-four, and that one is not about this run
    /// at all: the worth axis moves accuracy and <c>total_score</c> and neither of the two quantities
    /// this gate compares, so its two arms agree on EVERY row there is, not merely on runs shaped
    /// like this one. That costs nothing
    /// precisely because they agree, and this test asserts the agreement rather than asserting the
    /// pin alone.</para>
    /// </summary>
    [Test]
    public void AnAmbiguousEraIsPinnedDeterministicallyBecauseEveryReproducingArmAgrees()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceReplay();
        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed);

        var reproducing = Recalculation.EraSearchFor(stored)
                                       .Select(era => (era, account: ScoreUnder(map, replay, era)))
                                       .Where(x => WireCounts.Parse(stored.StatisticsJson)
                                                             .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                                                             .SequenceEqual(ToWire(x.account.Statistics).OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                                                   && x.account.MaxCombo == stored.MaxCombo)
                                       .ToList();

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(reproducing, Has.Count.EqualTo(64),
                "the rate axis is inert on a row with no rate mod, the combo AND typo axes on a run with no typo, the off-time axis on a run with no mistimed press, the credit axis on a run with no typo to correct, and the worth axis on every row there is, so all sixty-four of their arms reproduce");
            Assert.That(reproducing.Select(x => x.era.Space), Is.All.EqualTo(SpaceTimingRule.Untimed), "the SPACE axis is the one this row can prove");
            Assert.That(reproducing.Select(x => x.account.TotalScore).Distinct().Count(), Is.EqualTo(1),
                "and this run leaves no wrong character standing, so even total_score, which the worth axis DOES move, is the same under every arm here");
            Assert.That(reproducing.Select(x => x.account.MaxCombo).Distinct().Count(), Is.EqualTo(1), "and the reproducing arms agree, which is why the choice cannot matter");

            Assert.That(reproducing.Count(x => x.era.Typo == TypoRule.ImmediateMiss), Is.EqualTo(32),
                "backlog 158 doubled the ambiguity rather than resolving any: a run with no typo reads the same under both rules");
            Assert.That(reproducing.Count(x => x.era.Typo == TypoRule.Deferred), Is.EqualTo(32));
            Assert.That(reproducing.Count(x => x.era.OffTime == OffTimeRule.BreaksCombo), Is.EqualTo(32),
                "and backlog 199 doubled it again: a run that never strikes a character off the ladder reads the same under both off-time rules");
            Assert.That(reproducing.Count(x => x.era.OffTime == OffTimeRule.MehHit), Is.EqualTo(32));
            Assert.That(reproducing.Count(x => x.era.Credit == CorrectionCreditRule.Full), Is.EqualTo(32),
                "and backlog 210 a fourth time: a run with no typo has no correction for the cap to reach, so both credit arms grade it identically");
            Assert.That(reproducing.Count(x => x.era.Credit == CorrectionCreditRule.Capped), Is.EqualTo(32));
            Assert.That(reproducing.Count(x => x.era.Worth == UnfixedTypoWorthRule.MehCredit), Is.EqualTo(32),
                "and backlog 213 a fifth time, on the one axis whose arms agree on every row: the gate compares statistics and max_combo, and the fold moves neither");
            Assert.That(reproducing.Count(x => x.era.Worth == UnfixedTypoWorthRule.Nothing), Is.EqualTo(32));

            Assert.That(result.ReproducedUnderEra, Is.EqualTo(reproducing[0].era), "the pin is the FIRST reproducing arm in the search order");
            Assert.That(result.ReproducedUnderEra,
                Is.EqualTo(new SearchedEra(SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, ComboRestoreRule.OnFix, TypoRule.Deferred, OffTimeRule.BreaksCombo, CorrectionCreditRule.Full, UnfixedTypoWorthRule.MehCredit)),
                "which is the combination a real client actually shipped (the 2026-08-13 release up to backlog 199), preferred over the corners no build ever offered");
        });
    }

    /// <summary>
    /// THE SEARCH RUNS ON THE RESIDUAL ALONE. A row that comes back under the default is pinned to the
    /// default and reports exactly what it reported before backlog 156, even though (as asserted here)
    /// every other combination would have reproduced it too. That is what keeps the cost on the rows
    /// that need it and keeps the meaning of the overwhelming majority of the table unchanged.
    /// </summary>
    [Test]
    public void ARowThatComesBackUnderTheDefaultIsNeverSearched()
    {
        var map = PlainBeatmap();
        var replay = Pressed((0, 'a'), (4000, 'b'), (8000, 'c'));
        var stored = StoredFor(map, replay);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            // No spaces, no rate mod and no typo, so the search would have had a free choice of
            // every one of the hundred and twenty-eight combinations.
            foreach (var era in Recalculation.EraSearchFor(stored))
            {
                var account = ScoreUnder(map, replay, era);

                Assert.That(ToWire(account.Statistics), Is.EquivalentTo(WireCounts.Parse(stored.StatisticsJson)), $"{era} reproduces this row too");
            }

            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.ReproducedUnderEra, Is.EqualTo(Recalculation.DefaultEra), "the default is tried first and wins outright");
            Assert.That(result.EraProvedByReconstruction, Is.False, "so this row is not in the searched population");
        });
    }

    /// <summary>
    /// The reproduce sweep varies the TYPO rule alone, and "alone" has to mean alone for a searched
    /// row too: its second arm holds the spacebar and the rate windows at the era the first arm
    /// PROVED, not at the default. Held at the default they would move underneath a sweep that claims
    /// to move one axis, and a row played since the release would be reported as moving backwards onto
    /// a ladder it was never on. The COMBO axis has to be held there too, which is asserted on its own
    /// in <see cref="ReproduceHoldsTheProvedComboRuleStillOnBothArms"/>.
    /// </summary>
    [Test]
    public void ReproduceHoldsTheProvedWindowEraStillOnBothArms()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceReplay();
        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.EraProvedByReconstruction, Is.True, "the fixture is only interesting if the era WAS searched for");

            // Nothing in this run is a typo, so varying the typo rule alone must move nothing at all.
            Assert.That(result.NewStatistics, Is.EquivalentTo(result.OldRuleStatistics!));
            Assert.That(result.NewMaxCombo, Is.EqualTo(result.OldRuleMaxCombo));
            Assert.That(result.Moves, Is.False, "a reproduce sweep must not walk a proved row back onto the pre-release windows");
        });
    }

    /// <summary>
    /// WHY BACKLOG 157 EXISTS, and the case that says the reported signature must not be chased on its
    /// own. Combo restore was the last era axis the pass held at ONE value for the whole table
    /// (<c>ComboRestoreRule.Never</c>), on the premise that backlog 140 shipped after the last row that
    /// could care. Production disproved that: rows exist whose re-derivation under the FULL live rule
    /// set matches stored in every field, which the reproduce pass still could not express.
    ///
    /// <para>THE SIGNATURE SUCH A ROW PRINTS IS MISLEADING, and this test asserts exactly why. Every
    /// arm with the RIGHT windows and the wrong combo rule comes back with the stored tiers EXACTLY
    /// and with <c>max_combo</c> short, so it does not qualify (reproducing is <c>statistics</c> AND
    /// <c>max_combo</c> together). No candidate qualifying means the DEFAULT arm's mismatch is what
    /// gets printed, and that one carries a window-width signature (<c>great</c> falling, <c>ok</c>
    /// rising) on top of the combo shortfall. A reader taking that detail at face value goes hunting
    /// for a fifth window rule that does not exist.</para>
    ///
    /// <para>The fixture holds the TYPO axis at the pass's default so that the combo axis is the only
    /// thing being proved here. A row a real client stored since backlog 140 also carries the deferred
    /// typo rule, and if its typo was CORRECTED that rule leaves no key to read, which was the residual
    /// backlog 157 left behind and backlog 158 closed: see
    /// <see cref="AModernRowWhoseTypoWasCorrectedHasItsTypoEraProvedByReconstruction"/>.</para>
    /// </summary>
    [Test]
    public void ARowPlayedSinceComboRestoreShippedHasItsComboRuleProvedByReconstruction()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceWithACorrectedTypoReplay();
        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed, comboRule: ComboRestoreRule.OnFix);

        // The era the pass tries first, i.e. what it used to pin every row to on all three axes.
        var underTheDefault = ScoreUnder(map, replay, Recalculation.DefaultEra);

        // The arms the search was limited to before this item: the right windows, the wrong combo
        // rule. Held to this row's own typo arm, because backlog 158 put that axis in the list too and
        // this row was stored under TypoRule.ImmediateMiss: the deferred arms of the same windows
        // disagree about the corrected cell's TIER, which is a different fault from the one this test
        // is about.
        var rightWindowsWrongCombo = Recalculation.EraSearchFor(stored)
                                                  .Where(e => e.Space == SpaceTimingRule.Untimed
                                                              && e.Combo == ComboRestoreRule.Never
                                                              && e.Typo == TypoRule.ImmediateMiss)
                                                  .Select(e => ScoreUnder(map, replay, e))
                                                  .ToList();

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            // What the reader is shown when nothing qualifies: a window-width mismatch, with the
            // combo shortfall sitting next to it looking like part of the same fault.
            Assert.That(ToWire(underTheDefault.Statistics).GetValueOrDefault("ok"), Is.EqualTo(1), "the default arm demotes the space, so `ok` rises...");
            Assert.That(ToWire(underTheDefault.Statistics).GetValueOrDefault("great"), Is.EqualTo(3), "...and `great` falls, which is 156's signature, not this item's");
            Assert.That(underTheDefault.MaxCombo, Is.EqualTo(stored.MaxCombo - 1));

            // And why fixing the windows alone was never going to be enough.
            Assert.That(rightWindowsWrongCombo, Is.Not.Empty, "the search really does contain such arms");

            foreach (var account in rightWindowsWrongCombo)
            {
                Assert.That(ToWire(account.Statistics), Is.EquivalentTo(WireCounts.Parse(stored.StatisticsJson)),
                    "the right window era fixes every tier exactly...");
                Assert.That(account.MaxCombo, Is.EqualTo(stored.MaxCombo - 1),
                    "...and the wrong combo rule still leaves max_combo short, so no such arm reproduces");
            }

            // ...and the pass now proves the combo rule instead of reporting the row as unexplained.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.EraProvedByReconstruction, Is.True);
            Assert.That(result.ReproducedUnderEra!.Value.Combo, Is.EqualTo(ComboRestoreRule.OnFix));
            Assert.That(result.OldRuleMaxCombo, Is.EqualTo(stored.MaxCombo), "the fix resumed the streak its wrong keypress broke");
        });
    }

    /// <summary>
    /// The second arm holds the proved era still on the COMBO axis too, which is the same property
    /// <see cref="ReproduceHoldsTheProvedWindowEraStillOnBothArms"/> pins for the spacebar. A reproduce
    /// sweep varies the TYPO rule alone, so a row proved to have been played under
    /// <see cref="ComboRestoreRule.OnFix"/> must not be re-judged with the streak taken back: that
    /// would report a row played since backlog 140 as LOSING max_combo to a typo rule change that has
    /// nothing to do with it.
    ///
    /// <para>Asserted against re-derivations rather than against literals, and with the contrast arm
    /// shown to differ, so the test cannot pass vacuously if the axis stops being held.</para>
    /// </summary>
    [Test]
    public void ReproduceHoldsTheProvedComboRuleStillOnBothArms()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceWithACorrectedTypoReplay();
        var stored = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed, comboRule: ComboRestoreRule.OnFix);

        var result = Recalculation.Run(stored, Decoded(map, replay));
        var era = result.ReproducedUnderEra!.Value;

        // Today's typo rule over the era the first arm proved, which is what the second arm must be.
        // Built through ScoreUnder off the proved era itself, so every axis but the typo rule is
        // carried across by construction: this fixture CORRECTS its typo, so backlog 210's credit
        // axis is one of the ones that has to be held still, and naming five axes by hand would
        // silently leave the sixth on the live arm.
        var heldStill = ScoreUnder(map, replay, era with { Typo = TypoRule.Deferred });

        // The same thing with the combo axis walked back to the default, which is what it must NOT be.
        var comboWalkedBack = ScoreUnder(map, replay, era with { Typo = TypoRule.Deferred, Combo = Recalculation.DefaultEra.Combo });

        Assert.Multiple(() =>
        {
            Assert.That(result.EraProvedByReconstruction, Is.True, "the fixture is only interesting if the era WAS searched for");
            Assert.That(era.Combo, Is.EqualTo(ComboRestoreRule.OnFix));

            Assert.That(comboWalkedBack.MaxCombo, Is.Not.EqualTo(heldStill.MaxCombo),
                "non-vacuity: the two arms of the combo axis really do disagree about max_combo on this run");

            Assert.That(result.NewStatistics, Is.EquivalentTo(ToWire(heldStill.Statistics)));
            Assert.That(result.NewMaxCombo, Is.EqualTo(heldStill.MaxCombo), "a reproduce sweep must not take the restored streak back");
        });
    }

    /// <summary>
    /// WHY BACKLOG 158 EXISTS, and THE ASSERTION THIS TEST INVERTED. Under backlog 157 this test held
    /// that the row was still UNEXPLAINED, and that was the correct reading of the code at the time:
    /// a row a real client stored since backlog 140 was judged under <see cref="TypoRule.Deferred"/> as
    /// well, and when its typo was CORRECTED that rule leaves no key behind (<c>good</c> only ever
    /// marks a typo left STANDING). So backlog 155's discriminator read nothing, the row kept the older
    /// typo rule, every candidate re-derived the corrected cell as a MISS the stored row does not
    /// carry, and no member of a search that did not include the typo axis could reach it. Those were
    /// production's last two unexplained rows, 5410 and 5414.
    ///
    /// <para>IT FLIPPED BECAUSE THE ABSENCE OF THE KEY NOW GETS SEARCHED, not because backlog 155 was
    /// overturned. 155 established the key as a ONE-DIRECTIONAL PROOF and documented its absence as "no
    /// proof either way"; what the pass then did with an absence was resolve it silently to the older
    /// rule, which is the only part backlog 158 changed. The proof still wins where it exists, and
    /// <see cref="ACarriedKeyStillPinsTheTypoAxisInsteadOfSearchingIt"/> is the test that says so.</para>
    ///
    /// <para>The properties from the old version that must NOT flip are asserted alongside: the row was
    /// never corrupt (the rule set that judged it re-derives it exactly), and the era it lands in is the
    /// one that judged it rather than the nearest thing the search could find. The old signature,
    /// <c>miss 0 -&gt; n</c> alongside a <c>max_combo</c> shortfall of the same n, is asserted here as
    /// the DEFAULT arm's disagreement, which is what the search now has to work past rather than what
    /// the report prints.</para>
    /// </summary>
    [Test]
    public void AModernRowWhoseTypoWasCorrectedHasItsTypoEraProvedByReconstruction()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceWithACorrectedTypoReplay();

        // What a client of the 2026-08-13 release stores for this run: the four axes this run can
        // tell apart at the live rule. The fifth (backlog 199's off-time rule) is left where
        // StoredFor puts it, because this run never strikes a character off the ladder and the two
        // arms therefore grade it identically, which is why the era it is pinned to below carries
        // the older arm: the search prefers it and nothing can tell the difference.
        //
        // The SIXTH is deliberately left there too, on the pre-210 arm (CorrectionCreditRule.Full),
        // and that one is NOT indifferent: this run's whole point is a typo that was CORRECTED, so a
        // client of this release graded that cell on its retype's own timing and the cap would grade
        // it an Ok. The row is a 2026-08-13-release row, and it is the CREDIT arm of that release
        // that judged it.
        var stored = StoredFor(
            map, replay,
            spaceRule: SpaceTimingRule.Untimed,
            rateRule: RateWindowRule.ScaledByRate,
            comboRule: ComboRestoreRule.OnFix,
            typoRule: TypoRule.Deferred);

        var judgedUnder = new SearchedEra(SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, ComboRestoreRule.OnFix, TypoRule.Deferred, OffTimeRule.BreaksCombo, CorrectionCreditRule.Full, UnfixedTypoWorthRule.MehCredit);

        var result = Recalculation.Run(stored, Decoded(map, replay));
        var plan = WritePlan.Build(new[] { result }, RecalcMode.Reproduce, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

        // The combination that reproduces it, which the search could not reach before backlog 158
        // because the typo axis was pinned before the search started.
        var underTheRuleThatJudgedIt = ScoreUnder(map, replay, judgedUnder);

        // The arm the row starts at, which is what used to be its last word. This is the production
        // signature of the two rows this item closed: a miss the stored row does not carry, and a
        // max_combo shortfall of the same size moving with it.
        var underItsStartingPoint = ScoreUnder(map, replay, Recalculation.DefaultEraFor(stored));

        Assert.Multiple(() =>
        {
            Assert.That(WireCounts.Parse(stored.StatisticsJson).ContainsKey("good"), Is.False,
                "a CORRECTED typo leaves no key, so the row can prove nothing about its own typo era");
            Assert.That(stored.ProvablyJudgedUnderTheDeferredTypoRule, Is.False);
            Assert.That(Recalculation.DefaultEraFor(stored), Is.EqualTo(Recalculation.DefaultEra),
                "proving nothing means starting at the table-wide default, exactly as backlog 155 left it");

            // The non-vacuity, and the shape production reported: the starting arm demotes the
            // corrected cell to a miss the row does not carry, and loses the combo the fix restored.
            // The two move TOGETHER, which is the diagnostic that identified 5410 and 5414 (there both
            // moved by exactly 2). The sizes are not equal in general and are not asserted to be: how
            // much max_combo a broken streak costs depends on where in the run the cell falls, and here
            // one corrected cell costs 1 miss and 2 combo.
            Assert.That(ToWire(underItsStartingPoint.Statistics).GetValueOrDefault("miss"), Is.EqualTo(1));
            Assert.That(WireCounts.Parse(stored.StatisticsJson).GetValueOrDefault("miss"), Is.Zero);
            Assert.That(underItsStartingPoint.MaxCombo, Is.LessThan(stored.MaxCombo));

            Assert.That(ToWire(underTheRuleThatJudgedIt.Statistics), Is.EquivalentTo(WireCounts.Parse(stored.StatisticsJson)),
                "the row was never corrupt: the rule set that judged it re-derives it exactly");
            Assert.That(underTheRuleThatJudgedIt.MaxCombo, Is.EqualTo(stored.MaxCombo));

            // ...and the pass now reaches that combination instead of refusing the row.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.EraProvedByReconstruction, Is.True);
            Assert.That(result.ReproducedUnderEra, Is.EqualTo(judgedUnder), "and it lands in the era that judged it, on all six axes");
            Assert.That(plan.PinnedByEraSearch.Select(r => r.Stored.ScoreId), Is.EquivalentTo(new[] { stored.ScoreId }));

            Assert.That(result.Moves, Is.False,
                "it was already judged under today's typo rule, so a reproduce sweep has nothing left to reprice it by");
        });
    }

    /// <summary>
    /// The third cell struck 2600 ms late on <see cref="PlainBeatmap"/>, which targets it at 8000:
    /// past the Line ladder's outermost Meh edge (2000 late), so it is an OFF-TIME press, the right
    /// character outside every window. The first two land dead on target, so the off-time rule is the
    /// only thing that can move this run and it moves both quantities the gate compares: under the
    /// pre-199 rule the press zeroes the run and spends its cell on a Miss, and under the live one it
    /// is a Meh that extends it.
    ///
    /// <para>A LYRIC character rather than the space <see cref="LateSpaceReplay"/> mistimes, so the
    /// space axis is not entangled with this one: a space cannot reach the off-time tiers at all
    /// under the live space rule.</para>
    /// </summary>
    private static Replay OffTimeReplay() => Pressed((0, 'a'), (4000, 'b'), (10600, 'c'));

    /// <summary>
    /// WHY BACKLOG 199 NEEDS AN ERA AXIS, and the two halves of what one is for. Every row in the
    /// table was played when an off-time press broke the run and took a Miss, so the reproduce pass
    /// has to re-derive one under THAT rule (it does, under the default, with no search), and the
    /// supersede pass has to be able to reprice it under today's (it does, and the row gains its
    /// combo back and loses the miss).
    ///
    /// <para>Reproduction is the gate for repricing, which is why both are asserted here rather than
    /// the supersede half alone: a harness that cannot reproduce the old numbers has no business
    /// writing new ones.</para>
    ///
    /// <para>A REPRODUCE sweep does NOT reprice it, and that is deliberate rather than an omission:
    /// that sweep varies the typo rule alone and holds every other axis at the era it proved, exactly
    /// as it does for the spacebar (see
    /// <see cref="ReproduceHoldsTheProvedWindowEraStillOnBothArms"/>). Backlog 199's repricing is a
    /// supersede question.</para>
    /// </summary>
    [Test]
    public void ARowPlayedBeforeTheOffTimeRuleReproducesUnderItAndIsRepricedBySupersede()
    {
        var map = PlainBeatmap();
        var replay = OffTimeReplay();
        var stored = StoredFor(map, replay);

        var reproduce = Recalculation.Run(stored, Decoded(map, replay));
        var supersede = Recalculation.Run(stored, Decoded(map, replay), mode: RecalcMode.Supersede);

        Assert.Multiple(() =>
        {
            // The row as a pre-199 client stored it, and the fixture's non-vacuity: the mistimed
            // press really did cost a cell and a streak.
            Assert.That(WireCounts.Parse(stored.StatisticsJson)["miss"], Is.EqualTo(1));
            Assert.That(WireCounts.Parse(stored.StatisticsJson)["great"], Is.EqualTo(2));
            Assert.That(stored.MaxCombo, Is.EqualTo(2), "the run ended on the mistimed press");

            Assert.That(reproduce.Skip, Is.EqualTo(SkipReason.None), "a pre-199 row must be re-derivable");
            Assert.That(reproduce.Reproduced, Is.True);
            Assert.That(reproduce.ReproducedUnderEra, Is.EqualTo(Recalculation.DefaultEra));
            Assert.That(reproduce.EraProvedByReconstruction, Is.False, "it came back under the default, so nothing was searched");
            Assert.That(reproduce.OldRuleStatistics!["miss"], Is.EqualTo(1));
            Assert.That(reproduce.OldRuleMaxCombo, Is.EqualTo(2));
            Assert.That(reproduce.Moves, Is.False, "a reproduce sweep varies the TYPO rule alone, and there is no typo in this run");

            // ...and today's rule is what the supersede sweep judges it under: the press is a Meh
            // that keeps the run, so the miss goes and max_combo runs to the end of the map.
            Assert.That(supersede.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(supersede.NewStatistics!.GetValueOrDefault("miss"), Is.Zero, "an off-time press is not a dropped character any more");
            Assert.That(supersede.NewStatistics!["meh"], Is.EqualTo(1), "it takes the lowest weight a judged cell can take, so accuracy is the punishment");
            Assert.That(supersede.NewStatistics!["great"], Is.EqualTo(2));
            Assert.That(supersede.NewMaxCombo, Is.EqualTo(3), "and the run is unbroken");
            Assert.That(supersede.NewCompletion, Is.EqualTo(1), "the cell counts as typed, which it always was");
            Assert.That(supersede.Moves, Is.True);
        });
    }

    /// <summary>
    /// The other side of the same axis, and the one that grows with every play: a row a client
    /// running today's rule submitted does not come back under the default, so the pass PROVES its
    /// off-time era by reconstruction exactly as it does the spacebar's (backlog 156) and combo
    /// restore's (157). Without the axis in the search such a row would report as unexplained, which
    /// is the failure mode every one of those items was written for.
    ///
    /// <para>The signature is asserted rather than described, and doubles as the non-vacuity: under
    /// the pre-199 default the row demonstrably does NOT come back, and it comes back wrong in the
    /// shape this rule change makes (a miss the row does not carry, and a max_combo shortfall moving
    /// with it).</para>
    /// </summary>
    [Test]
    public void ARowPlayedSinceTheOffTimeRuleHasThatAxisProvedByReconstruction()
    {
        var map = PlainBeatmap();
        var replay = OffTimeReplay();

        var stored = StoredFor(map, replay, offTimeRule: OffTimeRule.MehHit);
        var storedStatistics = WireCounts.Parse(stored.StatisticsJson);

        // The same run under the era the pass tries first, i.e. the rule every older row was played
        // under.
        var underTheDefault = ScoreUnder(map, replay, Recalculation.DefaultEra);

        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(storedStatistics["meh"], Is.EqualTo(1), "today's client stores the mistimed press as a Meh");
            Assert.That(storedStatistics.ContainsKey("miss"), Is.False);
            Assert.That(stored.MaxCombo, Is.EqualTo(3));

            // The non-vacuity: the default arm disagrees, and disagrees in the shape 199 makes.
            Assert.That(underTheDefault.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(1), "a miss the row does not carry...");
            Assert.That(underTheDefault.MaxCombo, Is.EqualTo(2), "...and a max_combo shortfall moving with it");

            // ...and the pass proves the era instead of reporting the row as unexplained.
            Assert.That(result.Skip, Is.EqualTo(SkipReason.None));
            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.EraProvedByReconstruction, Is.True);
            Assert.That(result.ReproducedUnderEra!.Value.OffTime, Is.EqualTo(OffTimeRule.MehHit));
            Assert.That(result.OldRuleStatistics!["meh"], Is.EqualTo(1));
            Assert.That(result.OldRuleMaxCombo, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// BACKLOG 155 IS COMPOSED WITH, NOT REVERSED BY, backlog 158, and this is the test that holds the
    /// line. A row whose <c>good</c> key PROVES the deferred rule judged it is PINNED to that arm: the
    /// typo axis is not searched for it, the candidate list it is offered contains no arm on the other
    /// rule, and a coincidence therefore cannot outvote a proof.
    ///
    /// <para>Asserted three ways, because "the axis was not searched" is easy to state and easy to
    /// implement vacuously. The candidate list is checked to be half the size and all on one arm; the
    /// row's starting point is checked to be that arm rather than the table-wide default; and the row is
    /// checked to report as NOT reconstructed, since nothing was searched for it.</para>
    /// </summary>
    [Test]
    public void ACarriedKeyStillPinsTheTypoAxisInsteadOfSearchingIt()
    {
        var map = Beatmap();
        var replay = UnfixedTypoReplay(map);
        var stored = StoredFor(map, replay, typoRule: TypoRule.Deferred);

        var candidates = Recalculation.EraSearchFor(stored);
        var result = Recalculation.Run(stored, Decoded(map, replay));

        Assert.Multiple(() =>
        {
            Assert.That(stored.ProvablyJudgedUnderTheDeferredTypoRule, Is.True, "the fixture only means anything if the key is there");

            Assert.That(Recalculation.EraSearch, Has.Count.EqualTo(128), "the whole space is seven axes wide");
            Assert.That(candidates, Has.Count.EqualTo(64), "a proved row is offered half of it");
            Assert.That(candidates.Select(e => e.Typo), Is.All.EqualTo(TypoRule.Deferred), "and only the arm its key proves");

            Assert.That(candidates.Select(e => e with { Typo = TypoRule.ImmediateMiss }),
                Is.EquivalentTo(Recalculation.EraSearch.Where(e => e.Typo == TypoRule.ImmediateMiss)),
                "pinning removes an axis, it does not remove a combination of the other six");

            Assert.That(candidates[0], Is.EqualTo(Recalculation.DefaultEraFor(stored)), "the row's starting point is the first thing it is offered");
            Assert.That(Recalculation.DefaultEraFor(stored), Is.Not.EqualTo(Recalculation.DefaultEra), "which is NOT the table-wide default, because the key moved it");

            Assert.That(result.Reproduced, Is.True);
            Assert.That(result.ReproducedUnderEra, Is.EqualTo(Recalculation.DefaultEraFor(stored)));
            Assert.That(result.EraProvedByReconstruction, Is.False,
                "a proof is not a reconstruction: nothing was searched for this row, so it must not be counted as though it had been");
        });
    }

    /// <summary>
    /// The two invariants the search order rests on, stated once rather than left to be inferred from
    /// the tests that depend on them.
    ///
    /// <para>THE STARTING POINT IS THE FIRST CANDIDATE, for both populations. If it were not, a row
    /// would be re-derived under its starting arm and then again as part of the sweep, and
    /// <see cref="RecalcResult.EraProvedByReconstruction"/> (which compares the pin against the
    /// starting point) would mislabel it.</para>
    ///
    /// <para>FILTERING THE LIST DOES NOT REORDER IT. Backlog 158 interleaved the deferred-arm
    /// candidates into a list that used to hold only one arm's worth, and the order within each arm is
    /// backlog 157's. Read only the deferred entries or only the older ones and you get the same
    /// combinations of the other axes in the same sequence, so neither population's label can
    /// move because the other population's arms were added around it. Backlog 199 and backlog 210
    /// each expanded the list again in place, which is the same property a third and fourth time: the
    /// off-time and credit arms sit beside the combination they belong to, so filtering on the typo
    /// axis still reads them in their own order.</para>
    /// </summary>
    [Test]
    public void TheSearchOrderStartsWhereEachRowStartsAndSurvivesBeingFiltered()
    {
        var map = Beatmap();
        var replay = UnfixedTypoReplay(map);

        var proved = StoredFor(map, replay, typoRule: TypoRule.Deferred);
        var unprovable = StoredFor(map, replay);

        static IEnumerable<SearchedEra> WithoutTypo(IEnumerable<SearchedEra> eras)
            => eras.Select(e => e with { Typo = TypoRule.ImmediateMiss });

        Assert.Multiple(() =>
        {
            Assert.That(Recalculation.EraSearch, Is.Unique, "sixty-four corners, no duplicates");
            Assert.That(Recalculation.EraSearch[0], Is.EqualTo(Recalculation.DefaultEra));

            foreach (var stored in new[] { proved, unprovable })
            {
                var candidates = Recalculation.EraSearchFor(stored);

                Assert.That(candidates[0], Is.EqualTo(Recalculation.DefaultEraFor(stored)),
                    "a row's first candidate is where it starts, or it would be re-derived twice under the same rules");
            }

            Assert.That(WithoutTypo(Recalculation.EraSearchFor(proved)),
                Is.EqualTo(WithoutTypo(Recalculation.EraSearchFor(unprovable).Where(e => e.Typo == TypoRule.ImmediateMiss))).AsCollection,
                "the two arms sweep the other three axes in the SAME order, so interleaving them cannot move a label");
        });
    }

    /// <summary>
    /// The population is NAMED in both sweeps, on the precedent of the 133-to-147 window and of
    /// backlog 155's typo pin, and BROKEN DOWN by which era each row landed in, because that
    /// breakdown is the finding. It is also the one population that grows with every play: every row
    /// submitted since the release is in it, so a reader has to be able to watch it move.
    ///
    /// <para>The breakdown names all FIVE axes (four since backlog 158, the off-time rule since
    /// backlog 199), which is what keeps a typo-rule or off-time
    /// reconstruction from hiding inside a line that only spells out the windows. The two populations
    /// stay separate in the headline: a row PINNED by its own <c>good</c> key is counted above, under
    /// PINNED TO TypoRule.Deferred, and is not in here unless something else about it was searched.</para>
    /// </summary>
    [Test]
    public void TheReconstructedPopulationIsNamedInBothSweeps()
    {
        var map = SpacedBeatmap();
        var replay = MarginalSpaceReplay();

        var sinceTheRelease = StoredFor(map, replay, spaceRule: SpaceTimingRule.Untimed) with { ScoreId = 7 };
        var beforeIt = StoredFor(map, replay) with { ScoreId = 8 };

        foreach (var mode in new[] { RecalcMode.Reproduce, RecalcMode.Supersede })
        {
            var results = new[]
            {
                Recalculation.Run(sinceTheRelease, Decoded(map, replay), mode: mode),
                Recalculation.Run(beforeIt, Decoded(map, replay), mode: mode),
            };

            var plan = WritePlan.Build(results, mode, new Dictionary<UnreplayableCase, UnreplayablePolicy>(), filtered: false);

            var written = new StringWriter();
            Report.Print(results, plan, wholeTable: true, written);
            string text = written.ToString();

            Assert.Multiple(() =>
            {
                Assert.That(results.Select(r => r.Reproduced), Is.All.True, $"{mode}: both rows are re-derived under the era that judged them");
                Assert.That(plan.PinnedByEraSearch.Select(r => r.Stored.ScoreId), Is.EquivalentTo(new long[] { 7 }), $"{mode}: only the newer row needed a search");

                Assert.That(text, Does.Contain("PINNED BY ERA RECONSTRUCTION 1"), $"{mode}: the headline names the population");
                Assert.That(text, Does.Contain("of these, era proved by    1"), $"{mode}: so does the reproduction section");
                Assert.That(text, Does.Contain("SpaceTimingRule.Untimed + RateWindowRule.ScaledByRate + ComboRestoreRule.OnFix + TypoRule.Deferred + OffTimeRule.BreaksCombo"),
                    $"{mode}: broken down by which era they landed in, on all FIVE axes, so a combo-rule, typo-rule or off-time pin is visible rather than silent");
            });
        }
    }

    #endregion
}
