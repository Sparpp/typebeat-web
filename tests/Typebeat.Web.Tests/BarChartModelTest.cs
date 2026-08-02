using System.Globalization;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Tests;

/// <summary>
/// Geometry of the shared bar chart (Pages/BarChartModel.cs, rendered by Pages/Shared/_BarChart.cshtml).
/// Pure arithmetic, no database: the whole reason the layout lives in C# rather than in the .cshtml
/// is so the bars can be asserted where they are computed.
/// </summary>
public class BarChartModelTest
{
    private static BarChartModel Chart(params long[] values)
        => new(
            values.Select((v, i) => new BarChartBar($"m{i}", $"month {i}", v)).ToList(),
            valueNoun: "plays",
            ariaLabel: "Plays per month");

    [Test]
    public void EveryValue_GetsItsOwnSlot_InOrder_WithoutOverlapping()
    {
        var chart = Chart(4, 0, 9, 1, 7, 2, 6, 3, 5, 8, 10, 11);

        Assert.That(chart.Slots, Has.Count.EqualTo(12));

        for (int i = 1; i < chart.Slots.Count; i++)
        {
            var previous = chart.Slots[i - 1];
            var current = chart.Slots[i];

            Assert.That(current.X, Is.GreaterThanOrEqualTo(previous.X + previous.Width + 2),
                $"bars {i - 1} and {i} must be separated by at least the 2px surface gap");
        }

        Assert.Multiple(() =>
        {
            Assert.That(chart.Slots[0].X, Is.GreaterThanOrEqualTo(BarChartModel.PlotLeft));
            Assert.That(chart.Slots[^1].X + chart.Slots[^1].Width, Is.LessThanOrEqualTo(BarChartModel.PlotRight));
            // Capped, never stretched to fill the band: the leftover is air.
            Assert.That(chart.Slots.Select(s => s.Width), Is.All.LessThanOrEqualTo(24));
        });
    }

    [Test]
    public void BarsScaleToTheAxisCeiling_AndStandOnTheBaseline()
    {
        var chart = Chart(50, 25);

        double plotHeight = BarChartModel.Baseline - BarChartModel.TopLine;

        Assert.Multiple(() =>
        {
            // Peak 50 is already a clean ceiling, so the tallest bar fills the plot exactly.
            Assert.That(chart.AxisMax, Is.EqualTo(50));
            Assert.That(chart.Slots[0].Height, Is.EqualTo(plotHeight).Within(1e-9));
            Assert.That(chart.Slots[1].Height, Is.EqualTo(plotHeight / 2).Within(1e-9));
            Assert.That(chart.Slots[0].Y, Is.EqualTo(BarChartModel.TopLine).Within(1e-9));
            Assert.That(chart.Slots[1].Y + chart.Slots[1].Height, Is.EqualTo(BarChartModel.Baseline).Within(1e-9));
        });
    }

    [Test]
    public void AZeroValue_IsAGapWithNoColumn_ButKeepsItsSlot()
    {
        var chart = Chart(10, 0, 10);

        Assert.Multiple(() =>
        {
            Assert.That(chart.Slots[1].IsZero, Is.True);
            Assert.That(chart.Slots[1].Height, Is.Zero);
            Assert.That(chart.Slots[1].Path, Is.Empty, "a zero month draws no column at all");
            // The slot still sits between its neighbours, so the axis keeps real boundaries.
            Assert.That(chart.Slots[1].CenterX, Is.GreaterThan(chart.Slots[0].CenterX));
            Assert.That(chart.Slots[1].CenterX, Is.LessThan(chart.Slots[2].CenterX));
        });
    }

    [Test]
    public void ATinyValueNextToThePeak_StillDrawsSomething()
    {
        var chart = Chart(5000, 1);

        Assert.Multiple(() =>
        {
            Assert.That(chart.Slots[1].IsZero, Is.False);
            Assert.That(chart.Slots[1].Height, Is.GreaterThanOrEqualTo(2),
                "1 play out of 5000 must not round away to an invisible bar");
        });
    }

    [Test]
    public void ThePeakIsFlaggedOnce_AndItsLabelStaysInsideThePlot()
    {
        var first = Chart(9, 9, 3);      // ties go to the earliest
        var edge = Chart(1, 1, 1, 1, 40); // peak on the last bar

        Assert.Multiple(() =>
        {
            Assert.That(first.Slots.Count(s => s.IsPeak), Is.EqualTo(1));
            Assert.That(first.Slots[0].IsPeak, Is.True);
            Assert.That(first.Peak, Is.EqualTo(9));

            Assert.That(edge.PeakLabelX, Is.LessThanOrEqualTo(BarChartModel.PlotRight - 16));
            Assert.That(edge.PeakLabelX, Is.GreaterThanOrEqualTo(BarChartModel.PlotLeft + 16));
        });
    }

    [Test]
    public void AxisLabels_AreThinnedToWhatFits_AlwaysKeepingTheLatest()
    {
        var chart = Chart(Enumerable.Repeat(1L, 24).ToArray());

        var labelled = chart.Slots.Where(s => s.AxisLabel is not null).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(labelled, Has.Count.LessThanOrEqualTo(8));
            Assert.That(chart.Slots[^1].AxisLabel, Is.EqualTo("m23"), "the most recent period always carries a label");
            // Every bar still has its full label available to the tooltip and the table view.
            Assert.That(chart.Bars.Select(b => b.Label), Is.Unique);
        });

        // A short series is fully labelled: nothing to thin.
        var shortChart = Chart(1, 2, 3);
        Assert.That(shortChart.Slots.Select(s => s.AxisLabel), Is.All.Not.Null);
    }

    [Test]
    public void TheAxisCeiling_RoundsUpToACleanNumber_ButNotForSmallCounts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BarChartModel.NiceCeiling(0), Is.EqualTo(1), "an empty series still needs a non-zero scale");
            Assert.That(BarChartModel.NiceCeiling(3), Is.EqualTo(3));
            Assert.That(BarChartModel.NiceCeiling(7), Is.EqualTo(10));
            Assert.That(BarChartModel.NiceCeiling(42), Is.EqualTo(50));
            Assert.That(BarChartModel.NiceCeiling(51), Is.EqualTo(100));
            Assert.That(BarChartModel.NiceCeiling(1234), Is.EqualTo(2000));
        });
    }

    [Test]
    public void Coordinates_AreInvariant_NotTheRequestCulture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // A comma-decimal culture would emit d="M46,5" as "M46,5" for 46.5 and silently
            // corrupt every path in the SVG.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Multiple(() =>
            {
                Assert.That(BarChartModel.Px(46.5), Is.EqualTo("46.5"));
                Assert.That(BarChartModel.Num(1234), Is.EqualTo("1,234"));
                Assert.That(Chart(3, 7).Slots[0].Path, Does.Contain("."), "fractional coordinates keep a dot separator");
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public void TheTooltip_NamesThePeriodAndTheValue()
    {
        var chart = new BarChartModel(
            [new BarChartBar("Aug", "August 2026", 1204)], valueNoun: "plays", ariaLabel: "Plays per month");

        Assert.That(chart.Tooltip(chart.Bars[0]), Is.EqualTo("August 2026: 1,204 plays"));
    }
}
