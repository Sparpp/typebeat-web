namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Boundary-window typing-pace statistics + star rating for a lyric map, the numbers the
/// client's song select shows and the exact difficulty formula it stores. Ports, from
/// typebeat-osu:
///
///  - pace: LyricPaceStatistics.Compute
///    (typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricPaceStatistics.cs): a line's typing
///    window is EndTime - StartTime (the boundary-to-boundary time a player actually gets),
///    floored at 500 ms; per line CPM = real typeable cells / window (chars + one inter-word
///    space per token gap; TypingLine's cell arithmetic) and WPM is that CPM over
///    <see cref="CHARS_PER_WORD"/>; the map pace is the unweighted mean of per-line rates, so
///    instrumental gaps between lines never dilute it. The word convention is the TYPING-TEST
///    one, so WPM here means what it means on MonkeyType, on the in-game HUD and on the results
///    screen; the map's true word length is published separately as
///    <see cref="PaceStatistics.AverageCharsPerWord"/> rather than left implicit in a CPM:WPM
///    ratio nobody could read off the page.
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
    /// v14 = the star rating gains a LENGTH term (backlog 152).
    /// <see cref="LyricDifficulty.Compute"/> now adds <c>0.12 * max(0, log10(cells/100))</c> to the
    /// strain curve, so every rating of every map over 100 typeable cells rises, by up to +0.17 on
    /// the live catalogue and monotonically in the cell count. All six stored star columns move
    /// together (the bonus depends on the CELL COUNT, so it is the same on every rate and larger on
    /// the Literate ones), which also finally reprices the <c>sr_dt</c> rows still carrying the old
    /// flat ceiling of 10. The pace columns themselves are untouched: no word or cell count and no
    /// WPM figure changes, only the ratings. This one is NOT free for scores and is not meant to
    /// be: <c>PerformancePoints.VERSION</c> bumps to 16 in the same change, having DELETED its own
    /// length factor, so every stored row reprices against both halves of the migration at once.
    ///
    /// v15 = WPM IS REDEFINED as CPM / <see cref="CHARS_PER_WORD"/> (backlog 169/170), mirroring the
    /// game. A word is now five typeable cells, the typing-test convention, instead of a real
    /// whitespace-delimited word, so <c>beatmaps.wpm</c> and <c>peak_wpm</c> and every point of
    /// <c>wpm_curve</c> move on every map whose average word is not exactly 5 cells long, which is
    /// every map: the five shipped ones measure 4.11 to 4.57 cells per word, so their advertised
    /// WPM falls by the same proportion (charsPerWord / 5, i.e. to between 0.82x and 0.91x). The
    /// word length the old CPM:WPM ratio encoded implicitly is now published outright as
    /// <see cref="PaceStatistics.AverageCharsPerWord"/>, which the set page prints under Average
    /// WPM; it needs no column, being <c>char_count / word_count</c> off two columns that already
    /// exist and do not move. NOTHING ELSE MOVES, and that is checked rather than assumed:
    /// <c>difficulty_rating</c> and the five rate/Literate ratings, <c>word_count</c>,
    /// <c>char_count</c>, <c>peak_cpm</c>, <c>skippable_s</c> and <c>lyrics</c> all rewrite
    /// byte-identically, because only the FORMULA CONSUMING the counts changed and no count did.
    /// <c>PerformancePoints.VERSION</c> deliberately stays where it is: pp reads star ratings, never
    /// a WPM or a CPM. The sweep is still not free for scores, for the mechanical reason v13 was
    /// not: it stamps <c>pp_version = 0</c> on every score of every row it rewrites, so the whole
    /// score table reprices at the next boot, to identical values. That has been accepted rather
    /// than gated. The <c>wpm:</c> search filter is deliberately NOT rescaled either: a saved search
    /// or a shared URL carrying a wpm range keeps working and simply means the new figure. The
    /// <c>cpm:</c> filter is a different matter and WAS a bug, fixed in the same change:
    /// <see cref="Search.BeatmapSearchSql"/> derived it as <c>wpm * char_count / word_count</c>,
    /// which was the old identity exactly, and would now read <c>cpm * charsPerWord / 5</c>. It is
    /// <c>wpm * 5</c> from here on.
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
    public const int VERSION = 15;

    /// <summary>
    /// Typeable cells per word, the typing-test convention. Same 5 as the game's
    /// <c>LyricPaceStatistics.CHARS_PER_WORD</c> and <c>TypingEngine.LiveWpm</c>, and as
    /// <see cref="LyricWpmCurve.CHARS_PER_WORD"/> next door; all of them must agree or the map's
    /// advertised pace stops meaning what the HUD shows.
    /// </summary>
    public const double CHARS_PER_WORD = 5.0;

    // LyricPaceStatistics.cs: guards degenerate data from exploding the rate.
    private const double min_line_window_ms = 500;

    /// <param name="AverageCpm">Mean of per-line (typeable cells / boundary window) rates.</param>
    /// <param name="DifficultyRating">Stars from <see cref="LyricDifficulty"/> (no-mod baseline).</param>
    public readonly record struct PaceStatistics(
        double AverageCpm,
        int TypeableCellCount,
        int WordCount,
        double DifficultyRating)
    {
        /// <summary>
        /// <see cref="AverageCpm"/> over <see cref="CHARS_PER_WORD"/>. DERIVED rather than
        /// accumulated in its own sum, mirroring the game, so the two figures cannot drift apart by
        /// so much as a rounding step whatever the map does.
        /// </summary>
        public double AverageWpm => AverageCpm / CHARS_PER_WORD;

        /// <summary>
        /// Typeable cells per word over the WHOLE map (<see cref="TypeableCellCount"/> /
        /// <see cref="WordCount"/>), inter-word spaces included because the 5 in
        /// <see cref="CHARS_PER_WORD"/> counts them too: a "word" in a typing test is five
        /// keystrokes, and the space after a word is a keystroke. This is the number the old
        /// CPM:WPM ratio encoded implicitly; 0 for a map with no words, rather than a NaN the set
        /// page would print.
        ///
        /// <para>A map total, not a mean of per-line ratios, so it is the length of the average word
        /// the player types rather than the average of the lines' averages. WPM and CPM go the other
        /// way (unweighted per-line means) because they are RATES and a per-line mean is what keeps
        /// a long instrumental gap from diluting them, while this is a pure count ratio with no time
        /// in it to dilute.</para>
        /// </summary>
        public double AverageCharsPerWord => WordCount == 0 ? 0 : (double)TypeableCellCount / WordCount;
    }

    public static PaceStatistics Compute(IReadOnlyList<LyricLine> lines)
    {
        int totalCells = 0;
        int totalWords = 0;
        int lineCount = 0;
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

            // WPM is not accumulated here: it is CPM / CHARS_PER_WORD by definition (see
            // PaceStatistics.AverageWpm), so a second sum could only introduce a way for the two to
            // disagree. The word COUNT is still accumulated, because AverageCharsPerWord needs it.
            cpmSum += cells / windowMinutes;
            totalCells += cells;
            totalWords += words;
            lineCount++;
        }

        if (lineCount == 0)
            return default;

        return new PaceStatistics(
            cpmSum / lineCount,
            totalCells,
            totalWords,
            LyricDifficulty.Compute(lines));
    }
}
