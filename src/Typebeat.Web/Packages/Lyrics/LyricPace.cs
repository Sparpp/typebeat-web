namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Perfect-play typing-pace statistics + star rating for a lyric map — the numbers the client's
/// song select shows and the exact difficulty formula it stores. Ports, from typebeat-osu:
///
///  - cell/target arithmetic: TypingLine.FromLyricLine
///    (typebeat.Game.Rulesets.TypeBeat/Gameplay/TypingLine.cs:187-310) — typeable char j of k in
///    unit u targets u.Start + j*(u.End-u.Start)/k, inter-word spaces target the preceding unit's
///    EndTime, punctuation backfills neighbours, targets clamped non-decreasing;
///  - pace: LyricPaceStatistics.Compute
///    (typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricPaceStatistics.cs:31-65) — gross CPM = cells
///    over the per-line active windows (line start -> last typeable target, min 500 ms), spaces
///    included; WPM = CPM / 5;
///  - stars: TypeBeatDifficultyCalculator
///    (typebeat.Game.Rulesets.TypeBeat/TypeBeatDifficultyCalculator.cs:22-24,40) —
///    stars = min(10, WPM / 25).
///
/// Granularity is deliberately absent: in the client it only widens judgement windows
/// (TypingLine.cs:217-219), never cell target times, so WPM/stars are granularity-independent.
/// </summary>
public static class LyricPace
{
    // LyricPaceStatistics.cs:29 — guards degenerate data from exploding the rate.
    private const double min_line_active_ms = 500;

    // TypeBeatDifficultyCalculator.cs:23-24.
    private const double wpm_per_star = 25;
    private const double max_stars = 10;

    public readonly record struct PaceStatistics(
        double AverageWpm,
        int TypeableCellCount,
        int WordCount,
        double ActiveTypingMs)
    {
        public double AverageCpm => AverageWpm * 5;

        /// <summary>Star rating: min(10, WPM / 25) — TypeBeatDifficultyCalculator.cs:40.</summary>
        public double DifficultyRating => Math.Min(max_stars, AverageWpm / wpm_per_star);
    }

    public static PaceStatistics Compute(IReadOnlyList<LyricLine> lines)
    {
        int cells = 0;
        int words = 0;
        double activeMs = 0;

        foreach (var line in lines)
        {
            var (typeableCount, lastTarget) = computeCells(line);

            if (typeableCount == 0)
                continue;

            cells += typeableCount;
            words += line.RawText.Split(' ').Length;
            activeMs += Math.Max(lastTarget - line.StartTime, min_line_active_ms);
        }

        if (cells == 0 || activeMs <= 0)
            return default;

        double cpm = cells / (activeMs / 60000.0);

        return new PaceStatistics(cpm / 5.0, cells, words, activeMs);
    }

    /// <summary>
    /// The target-time arithmetic of TypingLine.FromLyricLine (TypingLine.cs:187-310) reduced to
    /// what the pace needs: the typeable-cell count and the max typeable target time. The full
    /// cell array is materialised the same way (including punctuation backfill and the
    /// non-decreasing clamp) so the numbers match the client exactly even for text containing
    /// cells <see cref="Typeability.Normalize"/> would have removed.
    /// </summary>
    private static (int TypeableCount, double LastTarget) computeCells(LyricLine line)
    {
        string text = line.RawText;
        var units = line.Units;

        int n = text.Length;

        if (n == 0)
            return (0, line.StartTime);

        bool[] isTypeable = new bool[n];
        double?[] targets = new double?[n];

        // First pass: walk the raw text token by token (spaces delimit tokens; token m maps to
        // Units[m], clamped against malformed data). (TypingLine.cs:201-258.)
        string[] tokens = text.Split(' ');
        int pos = 0;

        for (int m = 0; m < tokens.Length; m++)
        {
            string token = tokens[m];

            TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

            double unitStart = unit?.StartTime ?? line.StartTime;
            double unitEnd = unit?.EndTime ?? line.SingEndTime;

            int k = 0;

            foreach (char ch in token)
            {
                if (Typeability.IsTypeable(ch))
                    k++;
            }

            int j = 0;

            foreach (char ch in token)
            {
                if (Typeability.IsTypeable(ch))
                {
                    isTypeable[pos] = true;
                    targets[pos] = unitStart + j * (unitEnd - unitStart) / k;
                    j++;
                }

                pos++;
            }

            if (m < tokens.Length - 1)
            {
                // Inter-word space cell: preceding unit's EndTime.
                isTypeable[pos] = true;
                targets[pos] = unitEnd;
                pos++;
            }
        }

        // Second pass (a): non-typeable cells copy the NEXT typeable cell's target. (:260-269.)
        double? next = null;

        for (int i = n - 1; i >= 0; i--)
        {
            if (targets[i].HasValue)
                next = targets[i];
            else if (next.HasValue)
                targets[i] = next;
        }

        // Second pass (b): trailing punctuation copies the PREVIOUS target. (:271-280.)
        double? prev = null;

        for (int i = 0; i < n; i++)
        {
            if (targets[i].HasValue)
                prev = targets[i];
            else
                targets[i] = prev ?? line.StartTime;
        }

        // Guard: targets non-decreasing. (:282-287.)
        for (int i = 1; i < n; i++)
        {
            if (targets[i]!.Value < targets[i - 1]!.Value)
                targets[i] = targets[i - 1];
        }

        // LyricPaceStatistics.cs:43-52: max typeable target, floored at the line start.
        int typeable = 0;
        double lastTarget = line.StartTime;

        for (int i = 0; i < n; i++)
        {
            if (!isTypeable[i])
                continue;

            typeable++;
            lastTarget = Math.Max(lastTarget, targets[i]!.Value);
        }

        return (typeable, lastTarget);
    }
}
