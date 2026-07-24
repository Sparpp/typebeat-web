namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Boundary-window typing-pace statistics + star rating for a lyric map, the numbers the
/// client's song select shows and the exact difficulty formula it stores. Ports, from
/// typebeat-osu:
///
///  - pace: LyricPaceStatistics.Compute
///    (typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricPaceStatistics.cs): a line's typing
///    window is EndTime - StartTime (the boundary-to-boundary time a player actually gets),
///    floored at 500 ms; per line WPM = real words / window and CPM = real typeable cells /
///    window (chars + one inter-word space per token gap; TypingLine's cell arithmetic);
///    the map pace is the unweighted mean of per-line rates, so instrumental gaps between
///    lines never dilute it. No "1 word = 5 chars" estimate anywhere: the CPM:WPM ratio is
///    the map's true word length.
///  - stars: <see cref="LyricDifficulty"/>, a duration-weighted soft maximum over per-word
///    typing strain (sr-formula-v1.md), mirroring the game's
///    typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty. Unlike the pace, this DOES use
///    unit target times (per-word windows drive strain).
/// </summary>
public static class LyricPace
{
    /// <summary>
    /// Bumped whenever the pace/star arithmetic changes shape. Stamped on beatmap rows at
    /// ingest (<c>beatmaps.pace_version</c>); rows below it are recomputed from their stored
    /// .osu blob at startup (<see cref="PaceBackfill"/>). v1 = perfect-play cells/5 pace,
    /// v2 = boundary-window real-count pace, v3 = strain-based star rating (LyricDifficulty;
    /// pace WPM/CPM unchanged), v4 = per-word strain sum (sustained difficulty counts),
    /// v5 = per-character window floor (fast multi-char words no longer over-capped),
    /// v6 = freestyle cells counted (a line flagged <c>"freestyle": true</c> keeps its '&amp;'
    /// markers, and each is a real cell). v6 is a no-op for every map without a flagged line,
    /// so the backfill rewrites existing rows with byte-identical values.
    /// </summary>
    public const int VERSION = 6;

    // LyricPaceStatistics.cs: guards degenerate data from exploding the rate.
    private const double min_line_window_ms = 500;

    /// <param name="DifficultyRating">Stars from <see cref="LyricDifficulty"/> (no-mod baseline).</param>
    public readonly record struct PaceStatistics(
        double AverageWpm,
        double AverageCpm,
        int TypeableCellCount,
        int WordCount,
        double DifficultyRating);

    public static PaceStatistics Compute(IReadOnlyList<LyricLine> lines)
    {
        int totalCells = 0;
        int totalWords = 0;
        int lineCount = 0;
        double wpmSum = 0;
        double cpmSum = 0;

        foreach (var line in lines)
        {
            // Cell arithmetic mirrors TypingLine.FromLyricLine: every typeable char is a
            // cell, plus one typeable space cell per token gap.
            string[] tokens = line.RawText.Split(' ');

            int cells = tokens.Length - 1;
            int words = 0;

            foreach (string token in tokens)
            {
                int typeable = 0;

                foreach (char ch in token)
                {
                    // Freestyle slots are keypresses too, so they count towards the pace.
                    if (Typeability.IsCell(ch))
                        typeable++;
                }

                cells += typeable;

                if (typeable > 0)
                    words++;
            }

            if (cells <= 0)
                continue;

            double windowMinutes = Math.Max(line.EndTime - line.StartTime, min_line_window_ms) / 60000.0;

            wpmSum += words / windowMinutes;
            cpmSum += cells / windowMinutes;
            totalCells += cells;
            totalWords += words;
            lineCount++;
        }

        if (lineCount == 0)
            return default;

        return new PaceStatistics(
            wpmSum / lineCount,
            cpmSum / lineCount,
            totalCells,
            totalWords,
            LyricDifficulty.Compute(lines));
    }
}
