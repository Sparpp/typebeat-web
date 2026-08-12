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
    /// v7 = <c>beatmaps.skippable_s</c> is written alongside the pace numbers
    /// (<see cref="InstrumentalGaps.SkippableSeconds"/>, the play-time gate's skip allowance;
    /// 016_refund_skip_gate.sql). The pace/star arithmetic itself is unchanged at v7, so the
    /// backfill rewrites every other column with byte-identical values; the bump exists purely to
    /// make it revisit every row and fill the new column from the stored blob.
    /// v8 = <c>beatmaps.lyrics</c> is written alongside (ParsedDifficulty.LyricsText, the
    /// lyrics: search operator's haystack and the set page's lyrics display text;
    /// 018_lyrics_search.sql). As at v7 the arithmetic is unchanged; the bump revisits every
    /// row to fill the new column.
    /// v9 = the star arithmetic really changes (backlog 115/119): per-word strain splits into a
    /// fast decaying DENSITY plus a slow ENDURANCE moving average, and <c>star_scale</c> is
    /// re-anchored on the live ranked catalogue. Every rating moves, so this bump exists to make
    /// the backfill re-rate the whole catalogue and hand every score on it to
    /// <see cref="PpBackfill"/>. IT ALSO SPENDS THE DEFERRAL BELOW: re-parsing the stored blob is
    /// how the re-rate happens, so `.osz`-conversion maps are now re-derived against the
    /// punctuated text and their word and cell counts move with it. That was always what a bump
    /// to 9 would mean; taking the star change forced the decision, and it has been taken
    /// deliberately rather than absorbed by accident.
    /// v10 = the star arithmetic changes again (backlog 127): `per_char_floor_ms` 25 to 50 and
    /// `star_scale` 0.1675 to 0.23. The floor is the one that matters, because unlike a rescale it
    /// changes SHAPE and therefore ORDER: it raises the minimum window a word can be judged over,
    /// which lowers the load of the fastest words most, so short dense difficulties fall relative
    /// to sustained ones. Measured over the live ranked catalogue every rating rises (mean 1.27x,
    /// range 2.72 to 7.81), no base rating clamps, and the pool reorders by up to 7 places. As at
    /// v9 the bump exists to make the sweep re-rate every stored row and hand its scores to
    /// PpBackfill.
    /// v11 = <c>beatmaps.peak_wpm</c>, <c>peak_cpm</c> and <c>wpm_curve</c> are written alongside
    /// (<see cref="LyricWpmCurve"/>, the rolling-window pace the set page's WPM tab graphs;
    /// 028_wpm_curve.sql). As at v7 and v8 the pace and star ARITHMETIC IS UNCHANGED, so every
    /// column that already existed rewrites byte-identically and no rating and no pp value moves;
    /// the bump exists purely to make the sweep revisit every row and fill the new columns from the
    /// stored blob.
    ///
    /// v12 = the star rating loses its CEILING (backlog 118). <see cref="LyricDifficulty"/> used to
    /// end in a flat clamp to 10, which was chosen to keep a star badge sane but also truncated the
    /// two ratings that exist only to price rate mods. So <c>difficulty_rating</c> is expected to
    /// rewrite byte-identically for the whole live catalogue (its hardest difficulty reads 7.81 and
    /// nothing has ever reached the ceiling at rate 1.0), while <c>sr_dt</c> moves on every map that
    /// was sitting at exactly 10, which was 3 of the 5 real reference maps. That reprices every
    /// stored Double Time score on those maps, upward and substantially (pp goes as SR^2.00, so a
    /// rating of 16.33 rather than 10.00 is 2.67x), and every stored Half Time score on them too,
    /// in both directions, because the mirror in <c>PerformancePoints.HalfTimeMultiplier</c> is a
    /// function of <c>sr_dt</c> and several maps were only taking its flat fallback because
    /// <c>sr_dt</c> had been truncated. As at v9 and v10 the bump exists to make the sweep re-rate
    /// every stored row and hand its scores to <see cref="PpBackfill"/>; the pp FORMULA does not
    /// move, so <c>PerformancePoints.VERSION</c> deliberately stays where it is.
    ///
    /// v13 = <c>beatmaps.sr_literate</c>, <c>sr_literate_dt</c> and <c>sr_literate_ht</c> are
    /// written alongside (029_literate_stars.sql): the same three star ratings for the map the
    /// client's LITERATE mod converts this one into, which since backlog 144 is what prices a
    /// Literate play, in place of the flat 1.06 pp multiplier that has been removed. As at v7, v8
    /// and v11 the pace and star ARITHMETIC IS UNCHANGED (<see cref="LyricDifficulty.Compute"/>
    /// defaults to the plain stream, so every existing column rewrites byte-identically and no
    /// existing rating moves); the bump exists purely to make the sweep revisit every row and fill
    /// the three new columns from the stored blob. It is NOT free for scores, unlike v7/v8/v11: the
    /// sweep stamps <c>pp_version = 0</c> on every score of every row it rewrites, so the whole
    /// score table reprices at the next boot. That is wanted here anyway, because
    /// <c>PerformancePoints.VERSION</c> bumps in the same change and every stored Literate play has
    /// to lose its 1.06 and gain its honest rating.
    ///
    /// <para>The paragraph below is now SPENT HISTORY, kept because it explains what v9 dragged
    /// along with it. It was NOT bumped for the punctuation change (backlog 59) at the time. The
    /// arithmetic now
    /// runs on <see cref="Typeability.ToDefaultStream"/> of each line rather than the line itself,
    /// which is a no-op (bar case, which no count sees) for any text carrying no hyphen and no
    /// mark. Every blob the game's own encoder wrote holds exactly such text, because the old
    /// normalizer stripped the marks before they were ever stored. Blobs the .osz conversion tool
    /// produced re-emit the ALIGNER's raw line objects, which do carry marks, so re-parsing those
    /// would now yield punctuated lines and different word/cell counts. Leaving VERSION alone is
    /// what kept the backfill away from them: existing rows were not touched, and only a re-upload
    /// re-derived. v9 is that moment, so no deferral remains.</para>
    /// </summary>
    public const int VERSION = 13;

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
            //
            // Measured on the DEFAULT stream, never the authored (punctuated, cased) line: a map's
            // pace has to be the pace of the play everyone shares, not of the harder Literate
            // variant, and it has to stay comparable with every figure computed before punctuation
            // existed. For a hyphen-free, mark-free line, which is every line of every blob written
            // before then, ToDefaultStream is exactly ToLowerInvariant, and case cannot change a
            // count, so those rows recompute byte-identically and VERSION does not move.
            string[] tokens = Typeability.ToDefaultStream(line.RawText).Split(' ');

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
