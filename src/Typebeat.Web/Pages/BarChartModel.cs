using System.Globalization;

namespace Typebeat.Web.Pages;

/// <summary>One bar: the period it covers and the value measured over it.</summary>
/// <param name="Label">
/// Short axis label ("Aug"). Supplied for EVERY bar; the chart thins them down to what fits, so a
/// caller never has to reason about collisions.
/// </param>
/// <param name="Period">
/// The period spelled out ("August 2026"), for the hover tooltip and the table view, where there
/// is no neighbouring bar to disambiguate a bare month name against.
/// </param>
/// <param name="Value">The magnitude. Zero is a real value here (a month with no plays).</param>
public sealed record BarChartBar(string Label, string Period, long Value);

/// <summary>
/// View model for the shared bar-chart partial (Pages/Shared/_BarChart.cshtml): a server-rendered
/// inline SVG column chart of one series over an ordered categorical axis, with no JavaScript and
/// no external assets.
///
/// <para>
/// Deliberately generic (ordered labels + values), because more than one surface wants this shape:
/// the profile's play history is the first, replay views over time is the next. Nothing about
/// months, plays or profiles appears below; the caller supplies the labels, the titles and the
/// noun, and gets geometry back.
/// </para>
///
/// <para>
/// ALL GEOMETRY IS COMPUTED HERE, not in the .cshtml. Razor is a poor place for arithmetic (no
/// tests can reach it, and every expression has to fight the culture of the request), so the
/// partial is pure markup over <see cref="Slots"/> and the layout can be asserted directly in a
/// unit test. Coordinates are in the fixed <see cref="ViewWidth"/> x <see cref="ViewHeight"/>
/// viewBox and formatted invariantly; the SVG scales to its container from CSS.
/// </para>
/// </summary>
public sealed class BarChartModel
{
    /// <summary>viewBox width. The SVG is rendered at 100% of its container and scales uniformly.</summary>
    public const int ViewWidth = 720;

    /// <summary>viewBox height, giving roughly a 3.6:1 plot: wide and short, like osu's.</summary>
    public const int ViewHeight = 200;

    private const double pad_left = 46;   // room for the y-axis value labels
    private const double pad_right = 12;
    private const double pad_top = 24;    // headroom for the direct label on the peak bar
    private const double pad_bottom = 28; // the axis labels

    /// <summary>Bars are capped, never stretched to fill their band; the leftover is air.</summary>
    private const double max_bar_width = 24;

    /// <summary>The surface gap that separates adjacent bars (never a stroke).</summary>
    private const double bar_gap = 2;

    /// <summary>Rounded data-end; the baseline end stays square.</summary>
    private const double corner_radius = 4;

    /// <summary>A non-zero value never renders as nothing, however small it is next to the peak.</summary>
    private const double min_visible_height = 2;

    /// <summary>How tall the baseline stub under a zero-valued slot is (a hover target, not a value).</summary>
    public const double ZeroStubHeight = 2;

    private const int max_axis_labels = 8;

    public BarChartModel(IReadOnlyList<BarChartBar> bars, string valueNoun, string ariaLabel, string? caption = null)
    {
        Bars = bars;
        ValueNoun = valueNoun;
        AriaLabel = ariaLabel;
        Caption = caption;

        Peak = bars.Count == 0 ? 0 : bars.Max(b => b.Value);
        AxisMax = NiceCeiling(Peak);
        Slots = layOut(bars);
    }

    public IReadOnlyList<BarChartBar> Bars { get; }

    /// <summary>Plural noun for the values ("plays"), used in the table view's value column.</summary>
    public string ValueNoun { get; }

    /// <summary>What the chart is, for the SVG's role="img" label.</summary>
    public string AriaLabel { get; }

    /// <summary>Optional line of faint text under the chart (the window it covers, a total, ...).</summary>
    public string? Caption { get; }

    /// <summary>The largest value plotted; the bar carrying it gets the one direct label.</summary>
    public long Peak { get; }

    /// <summary>The rounded value the top gridline sits at, always &gt;= <see cref="Peak"/>.</summary>
    public long AxisMax { get; }

    public IReadOnlyList<BarChartSlot> Slots { get; }

    /// <summary>y of the baseline (value 0) in viewBox units.</summary>
    public static double Baseline => ViewHeight - pad_bottom;

    /// <summary>y of the top gridline (value <see cref="AxisMax"/>).</summary>
    public static double TopLine => pad_top;

    public static double PlotLeft => pad_left;

    public static double PlotRight => ViewWidth - pad_right;

    /// <summary>Right edge of the y-axis label column (labels are right-aligned to it).</summary>
    public static double AxisLabelX => pad_left - 8;

    /// <summary>
    /// Where the peak bar's direct label is centred, pulled inside the plot so a peak on the first
    /// or last bar cannot hang the text off the edge of the viewBox.
    /// </summary>
    public double PeakLabelX
    {
        get
        {
            var peak = Slots.FirstOrDefault(s => s.IsPeak);
            return peak is null ? PlotLeft : Math.Clamp(peak.CenterX, PlotLeft + 16, PlotRight - 16);
        }
    }

    /// <summary>Invariant formatting for a viewBox coordinate (never the request's culture).</summary>
    public static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Thousands-separated value, for labels and the table view.</summary>
    public static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The hover tooltip (and screen-reader text) for one bar: "August 2026: 1,204 plays".</summary>
    public string Tooltip(BarChartBar bar) => $"{bar.Period}: {Num(bar.Value)} {ValueNoun}";

    private IReadOnlyList<BarChartSlot> layOut(IReadOnlyList<BarChartBar> bars)
    {
        if (bars.Count == 0)
            return [];

        double plotWidth = PlotRight - PlotLeft;
        double plotHeight = Baseline - TopLine;
        double band = plotWidth / bars.Count;
        double barWidth = Math.Max(1, Math.Min(max_bar_width, band - bar_gap));

        // Ties go to the FIRST occurrence: with one label to give away, the earlier month is the
        // one the reader is less likely to infer from context.
        int peakIndex = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].Value == Peak && Peak > 0)
            {
                peakIndex = i;
                break;
            }
        }

        // Thin the axis labels to what fits, anchored at the LAST bar (the most recent period is
        // the one a reader dates the chart from) and stepping backwards.
        int step = (int)Math.Ceiling(bars.Count / (double)max_axis_labels);
        int last = bars.Count - 1;

        var slots = new List<BarChartSlot>(bars.Count);

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];

            double x = PlotLeft + (i * band) + ((band - barWidth) / 2);
            double centerX = x + (barWidth / 2);

            double height = AxisMax <= 0 || bar.Value <= 0
                ? 0
                : Math.Max(min_visible_height, plotHeight * bar.Value / AxisMax);

            double y = Baseline - height;

            slots.Add(new BarChartSlot(
                Bar: bar,
                X: x,
                Width: barWidth,
                Y: y,
                Height: height,
                CenterX: centerX,
                IsPeak: i == peakIndex,
                AxisLabel: (last - i) % step == 0 ? bar.Label : null,
                Path: barPath(x, y, barWidth, height)));
        }

        return slots;
    }

    /// <summary>
    /// A column with a rounded data-end and a square baseline end. Drawn as a path rather than a
    /// &lt;rect rx&gt; because rx rounds all four corners, which detaches the bar from the axis it
    /// is measured against.
    /// </summary>
    private static string barPath(double x, double y, double w, double h)
    {
        if (h <= 0)
            return string.Empty;

        double r = Math.Min(corner_radius, Math.Min(w / 2, h));
        double bottom = y + h;

        return string.Join(' ',
            $"M{Px(x)},{Px(bottom)}",
            $"L{Px(x)},{Px(y + r)}",
            $"Q{Px(x)},{Px(y)} {Px(x + r)},{Px(y)}",
            $"L{Px(x + w - r)},{Px(y)}",
            $"Q{Px(x + w)},{Px(y)} {Px(x + w)},{Px(y + r)}",
            $"L{Px(x + w)},{Px(bottom)}",
            "Z");
    }

    /// <summary>
    /// The value the top gridline sits at: the peak rounded up to a clean 1/2/5 x 10^k, so the
    /// axis reads in round numbers. Small peaks are their own ceiling (a 3-play month would waste
    /// 40% of the plot height under a "round up to 5" rule), and an all-zero series still gets a
    /// non-zero axis so nothing divides by it.
    /// </summary>
    public static long NiceCeiling(long peak)
    {
        if (peak <= 5)
            return Math.Max(peak, 1);

        long pow = 1;
        while (pow * 10 < peak)
            pow *= 10;

        foreach (long m in (long[])[1, 2, 5])
        {
            if (peak <= m * pow)
                return m * pow;
        }

        return 10 * pow;
    }
}

/// <summary>One laid-out bar, in viewBox coordinates. Produced by <see cref="BarChartModel"/>.</summary>
/// <param name="Path">SVG path data for the column, or empty when the value is zero.</param>
/// <param name="AxisLabel">The label to print under this bar, or null when it was thinned out.</param>
public sealed record BarChartSlot(
    BarChartBar Bar,
    double X,
    double Width,
    double Y,
    double Height,
    double CenterX,
    bool IsPeak,
    string? AxisLabel,
    string Path)
{
    public bool IsZero => Bar.Value <= 0;
}
