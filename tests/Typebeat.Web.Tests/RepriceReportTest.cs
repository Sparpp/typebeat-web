using System.Globalization;
using System.Text;
using Typebeat.Tools.RepriceReport;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The backlog-152 reprice report (tools/reprice-report), driven on synthetic data.
///
/// <para>The tool exists to answer 152's acceptance clause over PROD, which nothing here can reach,
/// so what is pinned is the arithmetic and the predicates: the two length terms it restates, the
/// three star-rating checks, the pp decomposition, and the board rule. Every fixture is built in
/// memory; no test here opens a database, a socket or a package on disk.</para>
///
/// <para>ONE THING IS DELIBERATELY NOT PINNED HERE, and the writeup says so: that the tool's copy of
/// the star bonus equals <c>LyricDifficulty</c>'s, whose constant and cell count are both private and
/// cannot be observed from outside (the term is added to a feats sum no caller can compute
/// separately). What answers that is the tool's own FIT of the constant out of the live catalogue,
/// which it prints next to the assumed value.</para>
/// </summary>
[TestFixture]
public class RepriceReportTest
{
    // ---------------------------------------------------------------------------------------
    // The two length terms
    // ---------------------------------------------------------------------------------------

    [Test]
    public void StarBonus_IsZeroBelowThePivotAndOneStepPerDecade()
    {
        Assert.Multiple(() =>
        {
            // The clamp. A sub-pivot map gains NOTHING, which is what keeps every short synthetic
            // fixture (and every regression pinned on one) rating byte-identically to pre-152.
            Assert.That(Length.StarBonus(0), Is.EqualTo(0));
            Assert.That(Length.StarBonus(5), Is.EqualTo(0));
            Assert.That(Length.StarBonus(99), Is.EqualTo(0));
            Assert.That(Length.StarBonus(100), Is.EqualTo(0).Within(1e-12));

            Assert.That(Length.StarBonus(1000), Is.EqualTo(0.12).Within(1e-12));
            Assert.That(Length.StarBonus(10000), Is.EqualTo(0.24).Within(1e-12));

            // The three lengths 152's pp fallout is quoted at.
            Assert.That(Length.StarBonus(340), Is.EqualTo(0.063777).Within(1e-6));
            Assert.That(Length.StarBonus(800), Is.EqualTo(0.108371).Within(1e-6));
            Assert.That(Length.StarBonus(2300), Is.EqualTo(0.163408).Within(1e-6));
        });
    }

    [Test]
    public void DeletedPpFactor_CrossesOneAtThePivotAndFloorsAtATenth()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Length.DeletedPpFactor(100), Is.EqualTo(1).Within(1e-12));
            Assert.That(Length.DeletedPpFactor(1000), Is.EqualTo(1.5).Within(1e-12));

            // BELOW the pivot the deleted factor was under 1, so deleting it INFLATES those plays.
            // The report has to be able to say that rather than calling it a violation.
            Assert.That(Length.DeletedPpFactor(10), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(Length.DeletedPpFactor(1), Is.EqualTo(0.1).Within(1e-12));
            Assert.That(Length.DeletedPpFactor(0), Is.EqualTo(0.1).Within(1e-12));

            Assert.That(Length.DeletedPpFactor(340), Is.EqualTo(1.265739).Within(1e-6));
            Assert.That(Length.DeletedPpFactor(800), Is.EqualTo(1.451545).Within(1e-6));
            Assert.That(Length.DeletedPpFactor(2300), Is.EqualTo(1.680864).Within(1e-6));
        });
    }

    [Test]
    public void CellCount_CountsTypeableCharsPerToken_AndPunctuationOnlyUnderLiterate()
    {
        var parsed = BeatmapPackageParser.ParseDifficulty("punctuated.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: PunctuatedLyrics)));

        // "Hello, world!" -> plain "hello" + "world" = 10 letters plus the space between them = 11
        // cells (backlog 269 put inter-word spaces into the count); under Literate the comma and
        // the exclamation mark are real cells too, so 13.
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Lines, Has.Count.EqualTo(1));
            Assert.That(Length.Count(parsed.Lines, literate: false), Is.EqualTo(11));
            Assert.That(Length.Count(parsed.Lines, literate: true), Is.EqualTo(13));

            // STILL NOT LyricPace's count, which is a different number on purpose: it counts a
            // freestyle slot whole rather than at a quarter, always measures the DEFAULT stream
            // (so it does not move under Literate at all), and takes its gaps between every split
            // token rather than between typed words. On this mark-free plain line the two happen
            // to coincide, which is why the LITERATE row above is the one that separates them.
            Assert.That(parsed.Pace.TypeableCellCount, Is.EqualTo(11));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Star ratings, end to end through the parser
    // ---------------------------------------------------------------------------------------

    [Test]
    public void Recompute_OverAPreDeployRow_ReportsTheMoveAsExactlyTheLengthBonus()
    {
        var row = PreDeployMap(beatmapId: 1, lines: 50);

        // 50 lines of 4 five-letter words plus the 3 inter-word spaces each line carries (backlog
        // 269 put those into the count that the bonus reads).
        const double cells = 50 * (4 * 5 + 3);
        double expected = Length.LENGTH_STARS * Math.Log10(cells / Length.REFERENCE_CELLS);

        Assert.That(row.Resolved, Is.True);
        Assert.That(row.Cells, Is.EqualTo(cells), "50 lines of 4 five-letter words and 3 spaces");

        var findings = SrAnalysis.Run([row], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(row.Delta(SrVariant.Base)!.Value, Is.EqualTo(expected).Within(1e-9));
            Assert.That(row.ExpectedBonus(SrVariant.Base), Is.EqualTo(expected).Within(1e-9));
            Assert.That(row.Residual(SrVariant.Base)!.Value, Is.EqualTo(0).Within(1e-12));

            // Every variant, including the three Literate ones, whose bonus is computed off the
            // LARGER literate cell count.
            foreach (var variant in SrAnalysis.Variants)
                Assert.That(row.Residual(variant)!.Value, Is.EqualTo(0).Within(1e-12), $"{variant} residual");

            Assert.That(findings.Passed, Is.True);
            Assert.That(findings.BonusExplainsEveryMove, Is.True);
            Assert.That(findings.OutOfRange, Is.Empty);
            Assert.That(findings.MonotonicityBreaks, Is.Empty);
        });
    }

    [Test]
    public void Fit_RecoversTheLengthConstantFromACatalogueOfDifferentLengths()
    {
        // Three maps of genuinely different lengths, each stored at what it rated before 152.
        var maps = new List<SrRow>
        {
            PreDeployMap(beatmapId: 1, lines: 10),
            PreDeployMap(beatmapId: 2, lines: 30),
            PreDeployMap(beatmapId: 3, lines: 90),
        };

        var findings = SrAnalysis.Run(maps, new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.FittedBonus.Samples, Is.EqualTo(3));
            Assert.That(findings.FittedBonus.Slope, Is.EqualTo(Length.LENGTH_STARS).Within(1e-9));
            Assert.That(findings.FittedBonus.MaxResidual, Is.LessThan(1e-9));
            Assert.That(findings.Passed, Is.True);
        });
    }

    [Test]
    public void Fit_CatchesACatalogueThatMovedByADifferentConstantThanTheToolAssumes()
    {
        // The fit is the tool's answer to the one thing it cannot verify in process: that its copy
        // of length_stars is still LyricDifficulty's. So here the catalogue is built as if the real
        // constant were 0.09, a number written out in this test and nowhere in the tool, and the
        // report has to notice: the fit lands on 0.09 while the residual against the ASSUMED 0.12
        // fires on every map. A tool that only ever printed its own constant back would pass this
        // silently.
        const double actual_constant = 0.09;

        var maps = new List<SrRow>
        {
            PreDeployMap(beatmapId: 1, lines: 10, bonusPerDecade: actual_constant),
            PreDeployMap(beatmapId: 2, lines: 30, bonusPerDecade: actual_constant),
            PreDeployMap(beatmapId: 3, lines: 90, bonusPerDecade: actual_constant),
        };

        var findings = SrAnalysis.Run(maps, new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.FittedBonus.Slope, Is.EqualTo(actual_constant).Within(1e-9));
            Assert.That(findings.FittedBonus.MaxResidual, Is.LessThan(1e-9), "the data is still a clean line, just a different one");

            Assert.That(findings.BonusExplainsEveryMove, Is.False);
            Assert.That(findings.BonusMismatch.Select(f => f.BeatmapId).Distinct(), Is.EquivalentTo(new long[] { 1, 2, 3 }));

            // And the two predicates 152 actually names still hold, because a smaller constant is
            // still a gain inside the cap and still monotone. That separation is the point of not
            // folding the residual into them.
            Assert.That(findings.Passed, Is.True);
        });
    }

    [Test]
    public void ShortMap_GainsNothing_SoThePinnedSyntheticRatingDoesNotMove()
    {
        // The fixture the game's own pace regression is pinned on. 152's clamp is what keeps this
        // map's LENGTH BONUS at exactly 0, and a report that claimed a move here would be reporting
        // on a broken clamp. The RATING itself moved to 0.82 with the difficulty rework (it was 0.67
        // under the envelope model), which is a different claim and not the one this pins: what
        // matters is that the bonus is 0, so the report says the map gains nothing.
        var parsed = BeatmapPackageParser.ParseDifficulty("short.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText()));

        Assert.Multiple(() =>
        {
            Assert.That(Length.Count(parsed.Lines, literate: false), Is.EqualTo(5), "ab + space + cd");
            Assert.That(Length.StarBonus(Length.Count(parsed.Lines, literate: false)), Is.EqualTo(0));
            Assert.That(parsed.Pace.DifficultyRating, Is.EqualTo(0.82).Within(0.01));
        });
    }

    [Test]
    public void OutOfRange_NamesBothDirections_AndTheBeatmapThatFailed()
    {
        var fell = Synthetic(beatmapId: 7, cells: 1000, stored: 5.00, recomputed: 4.90);
        var overshot = Synthetic(beatmapId: 8, cells: 1000, stored: 5.00, recomputed: 5.30);

        var findings = SrAnalysis.Run([fell, overshot], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.InRangePassed, Is.False);
            Assert.That(findings.Passed, Is.False);
            Assert.That(findings.OutOfRange.Select(f => f.BeatmapId).Distinct(), Is.EquivalentTo(new long[] { 7, 8 }));
            Assert.That(findings.OutOfRange.First(f => f.BeatmapId == 7).What, Does.Contain("FELL"));
            Assert.That(findings.OutOfRange.First(f => f.BeatmapId == 8).What, Does.Contain("cap"));
        });
    }

    [Test]
    public void Monotonicity_FailsWhenALongerMapGainsLess_AndNamesBothMaps()
    {
        var shorter = Synthetic(beatmapId: 11, cells: 200, stored: 4.00, recomputed: 4.10);
        var longer = Synthetic(beatmapId: 12, cells: 2000, stored: 6.00, recomputed: 6.05);

        var findings = SrAnalysis.Run([shorter, longer], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.MonotonePassed, Is.False);

            var breaks = findings.MonotonicityBreaks.Where(b => b.Variant == SrVariant.Base).ToList();

            Assert.That(breaks, Has.Count.EqualTo(1));
            Assert.That(breaks[0].LongerBeatmapId, Is.EqualTo(12));
            Assert.That(breaks[0].ShorterBeatmapId, Is.EqualTo(11));
        });
    }

    [Test]
    public void Monotonicity_TreatsEqualCellCountsAsEqual_NotAsMoreCells()
    {
        // Two maps of the SAME length that gained different amounts are not a monotonicity break:
        // "more cells" has to mean strictly more, or floating point noise between two equal-length
        // maps would be reported as a violation.
        var a = Synthetic(beatmapId: 21, cells: 1000, stored: 4.00, recomputed: 4.12);
        var b = Synthetic(beatmapId: 22, cells: 1000, stored: 5.00, recomputed: 5.10);

        Assert.That(SrAnalysis.Run([a, b], new Tolerances()).MonotonicityBreaks, Is.Empty);
    }

    [Test]
    public void UnresolvedMap_IsNeverSilentlyDropped()
    {
        var missing = SrRow.Unresolved(Stored(31, cells: 0, rating: 5), MapResolution.HashNotInPackage, "no .osu hashes to it");

        var findings = SrAnalysis.Run([missing], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.Unresolved.Single().Stored.BeatmapId, Is.EqualTo(31));
            Assert.That(findings.OutOfRange, Is.Empty, "an unresolved map asserts nothing either way");
            Assert.That(findings.Passed, Is.True);
        });
    }

    // ---------------------------------------------------------------------------------------
    // pp
    // ---------------------------------------------------------------------------------------

    [Test]
    public void Price_SplitsTheMoveIntoTheDeletedFactorAndTheStarBonus()
    {
        var row = PreDeployScore(scoreId: 100, beatmapId: 1, cells: 800, storedStars: 5.0);

        Assert.Multiple(() =>
        {
            // The stored price carries exactly the factor 152 deleted.
            Assert.That(row.ImpliedDeletedFactor!.Value, Is.EqualTo(Length.DeletedPpFactor(800)).Within(1e-9));
            Assert.That(row.FactorResidual!.Value, Is.EqualTo(0).Within(1e-9));

            // What pp still sees of length: a few percent, not 1.45x. It IS THE CLOSED FORM
            // ((SR + bonus)/SR)^sr_exponent AGAIN, and the round trip is worth recording. Through
            // v20 it was that form because every factor of the product was SR-independent bar the
            // difficulty. Backlog 270 broke it by ADDING a bonus that is LINEAR in SR_eff outside
            // the product, so the ratio became a blend of the two and this test could only bound it.
            // v22 restores it from the other side: the combo bonus is a FACTOR now, and one that
            // reads the note count and the miss count but NOT the rating, so it cancels in a ratio
            // of two plays on the same map exactly as every other factor does.
            double bonus = Length.StarBonus(800);

            // 2.30 is sr_exponent; this file is not on the pp tool's marked list, so it is spelled out.
            Assert.That(row.StarBonusRatio!.Value, Is.EqualTo(Math.Pow((5.0 + bonus) / 5.0, 2.30)).Within(1e-9));

            // And the whole move is the one divided by the other.
            Assert.That(row.Ratio!.Value, Is.EqualTo(row.StarBonusRatio!.Value / Length.DeletedPpFactor(800)).Within(1e-9));
        });
    }

    [Test]
    public void Deflation_MatchesTheThreeFiguresBacklog152Quotes()
    {
        // 152: "roughly -18% on a 340-cell map, -28% at 800, -38% at 2300". Star ratings chosen to
        // sit where the live catalogue does at those lengths, since the surviving term is
        // ((SR + bonus)/SR)^2 and so depends on the rating a little.
        var rows = new List<PpRow>
        {
            PreDeployScore(scoreId: 1, beatmapId: 1, cells: 340, storedStars: 3.5),
            PreDeployScore(scoreId: 2, beatmapId: 2, cells: 800, storedStars: 5.0),
            PreDeployScore(scoreId: 3, beatmapId: 3, cells: 2300, storedStars: 7.0),
        };

        var findings = PpAnalysis.Run(rows, new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Ratio!.Value, Is.EqualTo(0.82).Within(0.01), "340 cells, -18%");
            Assert.That(rows[1].Ratio!.Value, Is.EqualTo(0.72).Within(0.01), "800 cells, -28%");
            Assert.That(rows[2].Ratio!.Value, Is.EqualTo(0.62).Within(0.01), "2300 cells, -38%");

            // The anchors the report prints are the same numbers, observed rather than assumed.
            foreach (var anchor in findings.Anchors)
                Assert.That(anchor.Divergence!.Value, Is.EqualTo(0).Within(0.01), $"{anchor.Cells}-cell anchor");

            Assert.That(findings.GradingPassed, Is.True);
            Assert.That(findings.UnexplainedMoves, Is.Empty);
            Assert.That(findings.SignFlips, Is.Empty);
            Assert.That(findings.Passed, Is.True);
        });
    }

    [Test]
    public void Deflation_IsGradedByLength_AndAShortMapLegitimatelyInflates()
    {
        var rows = new List<PpRow>
        {
            PreDeployScore(scoreId: 1, beatmapId: 1, cells: 40, storedStars: 2.0),
            PreDeployScore(scoreId: 2, beatmapId: 2, cells: 340, storedStars: 3.5),
            PreDeployScore(scoreId: 3, beatmapId: 3, cells: 2300, storedStars: 7.0),
        };

        var findings = PpAnalysis.Run(rows, new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Ratio!.Value, Is.GreaterThan(1), "under the pivot the deleted factor was below 1");
            Assert.That(findings.Buckets.Select(b => b.MedianRatio), Is.Ordered.Descending);
            Assert.That(findings.GradingPassed, Is.True);
        });
    }

    [Test]
    public void GradingBreak_IsReportedWhenALongerBandKeepsMoreOfItsPp()
    {
        // A doctored row: a long map that barely moved. Nothing 152 does can produce this, which is
        // the point of checking for it.
        var normal = PreDeployScore(scoreId: 1, beatmapId: 1, cells: 340, storedStars: 3.5);
        var wrong = PreDeployScore(scoreId: 2, beatmapId: 2, cells: 2300, storedStars: 7.0, storedPpOverride: 1);

        var findings = PpAnalysis.Run([normal, wrong], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(findings.GradingPassed, Is.False);
            Assert.That(findings.GradingBreaks, Has.Count.EqualTo(1));
            Assert.That(findings.UnexplainedMoves.Select(f => f.ScoreId), Does.Contain(2L));
            Assert.That(findings.Passed, Is.False);
        });
    }

    [Test]
    public void SignFlip_IsReportedByScoreId()
    {
        // An unranked row that somehow carries a stored price: today's formula refuses to price it,
        // so it would be written down to zero. That is a sign flip and has to be named.
        var row = PreDeployScore(scoreId: 55, beatmapId: 1, cells: 800, storedStars: 5.0, ranked: false, storedPpOverride: 42);

        var findings = PpAnalysis.Run([row], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(row.Refused, Is.True);
            Assert.That(findings.SignsPassed, Is.False);
            Assert.That(findings.SignFlips.Single().ScoreId, Is.EqualTo(55));
            Assert.That(findings.Passed, Is.False);
        });
    }

    [Test]
    public void RowsOnAnUnresolvedMap_AreListedRatherThanPriced()
    {
        var map = SrRow.Unresolved(Stored(9, cells: 0, rating: 5), MapResolution.PackageUnavailable, "not fetched");
        var row = PpRow.Price(new StoredScoreRow(70, 1, 9, 100, PerformancePoints.VERSION, true, true, 1, 500, "[]", Statistics(500)), map);

        var findings = PpAnalysis.Run([row], new Tolerances());

        Assert.Multiple(() =>
        {
            Assert.That(row.Repriced, Is.False);
            Assert.That(findings.Unpriceable.Single().ScoreId, Is.EqualTo(70));
            Assert.That(findings.SignFlips, Is.Empty);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Boards
    // ---------------------------------------------------------------------------------------

    [Test]
    public void BoardOrder_ChangingWhereLengthDiffered_IsTheFeature()
    {
        // Same board, two players. The one who played the longer stretch of the map loses its length
        // premium and drops below the other. Length is what separated them, so this passes.
        var map = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);

        var longPlay = Row(scoreId: 1, userId: 1, map, notes: 2000, storedPp: 100, newPp: 62);
        var shortPlay = Row(scoreId: 2, userId: 2, map, notes: 400, storedPp: 95, newPp: 78);

        var findings = BoardAnalysis.Analyse([longPlay, shortPlay], new HashSet<long> { 1, 2 }, filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(findings.BoardsReordered, Is.EqualTo(1));
            Assert.That(findings.BoardsTopChanged, Is.EqualTo(1));
            Assert.That(findings.BoardOrderChanges, Has.Count.EqualTo(1));
            Assert.That(findings.BoardOrderChanges[0].LengthWasTheDifferentiator, Is.True);
            Assert.That(findings.Passed, Is.True);
        });
    }

    [Test]
    public void BoardOrder_ChangingWithNoLengthBetweenTheTwoPlays_IsAViolation()
    {
        // Same map, same note count, and they still trade places. Nothing in 152 can do that: both
        // plays are multiplied by exactly the same number.
        var map = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);

        var a = Row(scoreId: 1, userId: 1, map, notes: 2000, storedPp: 100, newPp: 60);
        var b = Row(scoreId: 2, userId: 2, map, notes: 2000, storedPp: 95, newPp: 70);

        var findings = BoardAnalysis.Analyse([a, b], new HashSet<long> { 1, 2 }, filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(findings.Passed, Is.False);
            // Both surfaces report it, and that is right: the per-map board and the global
            // top-plays board are two different places the pair traded on.
            Assert.That(findings.Violations.Select(v => v.Where), Is.EquivalentTo(new[] { "beatmap 1", "global top plays" }));
            Assert.That(findings.Violations.Select(v => (v.ScoreAbove, v.ScoreBelow)).Distinct(), Is.EquivalentTo(new[] { (1L, 2L) }));
        });
    }

    [Test]
    public void BoardOrder_HoldingItsOrder_ReportsNoChangeAtAll()
    {
        var map = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);

        var a = Row(scoreId: 1, userId: 1, map, notes: 2000, storedPp: 100, newPp: 62);
        var b = Row(scoreId: 2, userId: 2, map, notes: 2000, storedPp: 95, newPp: 59);

        var findings = BoardAnalysis.Analyse([a, b], new HashSet<long> { 1, 2 }, filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(findings.Boards, Is.EqualTo(1));
            Assert.That(findings.BoardsReordered, Is.Zero);
            Assert.That(findings.BoardOrderChanges, Is.Empty);
            Assert.That(findings.RankMoves, Is.Empty);
            Assert.That(findings.Passed, Is.True);
        });
    }

    [Test]
    public void RowLosingItsPriceEntirely_LeavesTheBoardRatherThanReordering()
    {
        var map = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);

        var stays = Row(scoreId: 1, userId: 1, map, notes: 2000, storedPp: 100, newPp: 62);
        var leaves = Row(scoreId: 2, userId: 2, map, notes: 2000, storedPp: 95, newPp: 0);

        var findings = BoardAnalysis.Analyse([stays, leaves], new HashSet<long> { 1, 2 }, filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(findings.RowsLeavingBoards, Is.EqualTo(1));
            Assert.That(findings.BoardsReordered, Is.Zero, "a departure is not a reorder of what is left");
            Assert.That(findings.BoardOrderChanges, Is.Empty);
        });
    }

    [Test]
    public void UnrepricedRowsStayOnBothSides_SoNoPlaceChangeIsInvented()
    {
        var resolved = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);
        var unresolved = SrRow.Unresolved(Stored(1, cells: 0, rating: 5), MapResolution.PackageUnavailable, "not fetched");

        var priced = Row(scoreId: 1, userId: 1, resolved, notes: 2000, storedPp: 100, newPp: 62);
        var untouched = new PpRow
        {
            Stored = new StoredScoreRow(2, 2, 1, 95, PerformancePoints.VERSION, true, true, 1, 2000, "[]", Statistics(2000)),
            Map = unresolved,
        };

        var findings = BoardAnalysis.Analyse([priced, untouched], new HashSet<long> { 1, 2 }, filtered: false);

        Assert.Multiple(() =>
        {
            Assert.That(findings.RowsLeavingBoards, Is.Zero);
            Assert.That(findings.BoardsReordered, Is.EqualTo(1), "the priced row fell below the untouched one");
            Assert.That(findings.BoardOrderChanges, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void FilteredRun_ComputesNoBoardsAtAll()
    {
        var map = Resolved(beatmapId: 1, cells: 2000, stored: 5.0);
        var row = Row(scoreId: 1, userId: 1, map, notes: 2000, storedPp: 100, newPp: 62);

        var findings = BoardAnalysis.Analyse([row], new HashSet<long> { 1 }, filtered: true);

        Assert.Multiple(() =>
        {
            Assert.That(findings.Filtered, Is.True);
            Assert.That(findings.Boards, Is.Zero);
            Assert.That(findings.Passed, Is.True, "a section that did not run cannot fail");
        });
    }

    // ---------------------------------------------------------------------------------------
    // The report itself
    // ---------------------------------------------------------------------------------------

    [Test]
    public void Report_NamesEveryFailingRowByIdAndSaysItWroteNothing()
    {
        var good = Resolved(beatmapId: 1, cells: 1000, stored: 4.0);
        var bad = Synthetic(beatmapId: 7, cells: 1000, stored: 5.00, recomputed: 5.40);
        var flipped = PreDeployScore(scoreId: 55, beatmapId: 1, cells: 1000, storedStars: 4.0, ranked: false, storedPpOverride: 42);

        string text = Render([good, bad], [flipped]);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("beatmap 7"), "the map that broke the cap is named");
            Assert.That(text, Does.Contain("score 55"), "the score that flipped sign is named");
            Assert.That(text, Does.Contain("FAILED"));
            Assert.That(text, Does.Contain("DRY RUN. Nothing was written."));
            Assert.That(text, Does.Not.Contain("VACUOUS"));
        });
    }

    [Test]
    public void Report_SaysSoWhenNotOneMapWasReRated()
    {
        var missing = SrRow.Unresolved(Stored(3, cells: 0, rating: 5), MapResolution.PackageUnavailable, "not fetched");

        string text = Render([missing], []);

        Assert.Multiple(() =>
        {
            // Every predicate "holds" here, over nothing at all. Saying so is the whole point.
            Assert.That(text, Does.Contain("VACUOUS"));
            Assert.That(text, Does.Contain("0 of 1 beatmap(s)"));
        });
    }

    private static string Render(IReadOnlyList<SrRow> maps, IReadOnlyList<PpRow> scores)
    {
        var tolerances = new Tolerances();

        var run = new RunContext
        {
            Site = "http://example.invalid",
            Maps = maps,
            Scores = scores,
            Sr = SrAnalysis.Run(maps, tolerances),
            Pp = PpAnalysis.Run(scores, tolerances),
            Boards = BoardAnalysis.Analyse(scores, new HashSet<long>(), filtered: false),
            Tolerances = tolerances,
        };

        var writer = new StringWriter();
        Report.Print(run, writer);

        return writer.ToString();
    }

    // ---------------------------------------------------------------------------------------
    // The command line, which refuses before it opens anything
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Cli_HelpSucceeds_AndAnUnknownCommandOrOptionFails()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await RepriceCli.RunAsync(["--help"]), Is.EqualTo(0));
            Assert.That(await RepriceCli.RunAsync([]), Is.EqualTo(1), "no arguments prints usage and fails");
            Assert.That(await RepriceCli.RunAsync(["apply"]), Is.EqualTo(1), "there is no apply command");
            Assert.That(await RepriceCli.RunAsync(["report", "--writes"]), Is.EqualTo(1), "an unknown option is a hard error");
            Assert.That(await RepriceCli.RunAsync(["report", "--limit", "nope"]), Is.EqualTo(1));
        });

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------

    private const string PunctuatedLyrics =
        """
        {"version":2,"song_end_ms":6000,"granularity":"Word"}
        {"text":"Hello, world!","start_ms":1000,"end_ms":3000,"words":[{"text":"Hello,","start_ms":1000,"end_ms":2000,"score":1},{"text":"world!","start_ms":2000,"end_ms":3000,"score":1}]}
        """;

    /// <summary>
    /// A map of <paramref name="lines"/> lines of four five-letter words (20 typeable cells each),
    /// parsed through the real parser, with its stored ratings set to what they would have been
    /// BEFORE 152: today's rating minus the length bonus for that variant's cell count. That is the
    /// shape of a prod snapshot taken before the deploy.
    /// </summary>
    /// <param name="bonusPerDecade">
    /// The constant the stored ratings are rolled back by. Defaults to the tool's own, i.e. a
    /// catalogue that moved by exactly what the tool assumes; pass a different one to build a
    /// catalogue that did not.
    /// </param>
    private static SrRow PreDeployMap(long beatmapId, int lines, double? bonusPerDecade = null)
    {
        var parsed = BeatmapPackageParser.ParseDifficulty("long.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: LongLyrics(lines))));

        double perDecade = bonusPerDecade ?? Length.LENGTH_STARS;
        double cells = Length.Count(parsed.Lines, literate: false);
        double literateCells = Length.Count(parsed.Lines, literate: true);
        double bonus = perDecade * Math.Max(0, Math.Log10(cells / Length.REFERENCE_CELLS));
        double literateBonus = perDecade * Math.Max(0, Math.Log10(literateCells / Length.REFERENCE_CELLS));

        var stored = new StoredBeatmap(
            beatmapId, beatmapId, "long.osu", parsed.ChecksumMd5, "Long", "Long map", "Fixture", "ranked",
            PaceVersion: 13, CharCount: parsed.Pace.TypeableCellCount,
            parsed.Pace.DifficultyRating - bonus,
            parsed.SrDoubleTime - bonus,
            parsed.SrHalfTime - bonus,
            parsed.SrLiterate - literateBonus,
            parsed.SrLiterateDoubleTime - literateBonus,
            parsed.SrLiterateHalfTime - literateBonus,
            FreestyleCellCount: parsed.Pace.FreestyleCellCount);

        return SrRow.Recompute(stored, SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: LongLyrics(lines))));
    }

    private static string LongLyrics(int lines)
    {
        var sb = new StringBuilder();
        int end = 1000 + lines * 2000;

        sb.Append(CultureInfo.InvariantCulture, $"{{\"version\":2,\"song_end_ms\":{end + 2000},\"granularity\":\"Word\"}}\n");

        for (int i = 0; i < lines; i++)
        {
            int start = 1000 + i * 2000;
            string[] words = ["alpha", "bravo", "delta", "gamma"];
            var units = words.Select((w, j) => $"{{\"text\":\"{w}\",\"start_ms\":{start + j * 500},\"end_ms\":{start + (j + 1) * 500},\"score\":1}}");

            sb.Append(CultureInfo.InvariantCulture,
                $"{{\"text\":\"{string.Join(' ', words)}\",\"start_ms\":{start},\"end_ms\":{start + 2000},\"words\":[{string.Join(',', units)}]}}\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>A map row with the numbers stated outright, for the predicate tests.</summary>
    private static SrRow Synthetic(long beatmapId, long cells, double stored, double recomputed)
        => new()
        {
            Stored = Stored(beatmapId, cells, stored),
            Resolution = MapResolution.Recomputed,
            Cells = cells,
            LiterateCells = cells,
            PaceCells = (int)cells,
            Recomputed = [recomputed, recomputed, recomputed, recomputed, recomputed, recomputed],
            // THE MATRIX A REAL REPARSE WOULD CARRY (034_ratings_matrix.sql), which the report
            // needs because since PerformancePoints v22 a price reads it and nothing else: a
            // synthetic row without one leaves every play on it PENDING, which is a correct reading
            // of a map that cannot be reparsed but makes this fixture price to zero everywhere.
            //
            // The difficult-character count is the map's own CELL COUNT, which is the model's upper
            // bound for it. This report's fixtures are clean full completions (see PreDeployScore),
            // so the cleanliness term is exactly 1.0 at any positive count and the figure cannot
            // move a single number the report is about: the whole file measures the STAR move.
            Ratings = TestRatings.FromStars(recomputed, recomputed, recomputed, recomputed, recomputed, recomputed, cells),
        };

    /// <summary>A map whose recomputed ratings are its stored ones plus the length bonus.</summary>
    private static SrRow Resolved(long beatmapId, long cells, double stored)
        => Synthetic(beatmapId, cells, stored, stored + Length.StarBonus(cells));

    private static StoredBeatmap Stored(long beatmapId, long cells, double rating)
        => new(beatmapId, beatmapId, "map.osu", new string('0', 32), "Fixture", "Map", "Artist", "ranked",
            PaceVersion: 13, CharCount: (int)cells, rating, rating, rating, rating, rating, rating,
            FreestyleCellCount: 0);

    /// <summary>
    /// A score whose STORED price is what the pre-152 formula would have produced: today's price at
    /// the stored ratings, times the factor 152 deleted. A full clean completion, so the play's note
    /// count is the map's cell count.
    /// </summary>
    private static PpRow PreDeployScore(
        long scoreId,
        long beatmapId,
        long cells,
        double storedStars,
        bool ranked = true,
        double? storedPpOverride = null)
    {
        var map = Resolved(beatmapId, cells, storedStars);
        int notes = (int)cells;

        // The DIFFICULT CHARACTERS the recomputed matrix carries for this map, which is what the
        // "at stored stars" column pairs the stored stars with (see PpRow.Price): no column ever
        // stored a count, so the recomputed one is the only one there is.
        double difficult = map.Ratings?.TryGet(LyricDifficulty.JudgementArm.None, false, 1.0)?.DifficultCharacters ?? 0;
        double atStoredStars = PerformancePoints.Compute(storedStars, notes, difficult, 0, 1, notes, null);
        double storedPp = storedPpOverride ?? atStoredStars * Length.DeletedPpFactor(notes);

        return PpRow.Price(
            new StoredScoreRow(scoreId, scoreId, beatmapId, storedPp, PerformancePoints.VERSION, ranked, true, 1, notes, "[]", Statistics(notes)),
            map);
    }

    /// <summary>A board row with its two prices stated outright, for the placement tests.</summary>
    private static PpRow Row(long scoreId, long userId, SrRow map, int notes, double storedPp, double newPp)
        => new()
        {
            Stored = new StoredScoreRow(scoreId, userId, map.Stored.BeatmapId, storedPp, PerformancePoints.VERSION, true, true, 1, notes, "[]", Statistics(notes)),
            Map = map,
            Notes = new PerformancePoints.NoteCounts(notes, 0),
            NewPp = newPp,
            Settled = true,
        };

    /// <summary>A clean play's statistics blob: every cell a top-tier hit, no misses, no typos.</summary>
    private static string Statistics(int notes) => $"{{\"great\":{notes}}}";
}
