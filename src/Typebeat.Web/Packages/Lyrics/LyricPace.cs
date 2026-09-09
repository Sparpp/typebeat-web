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
///    ratio nobody could read off the page. <see cref="PaceStatistics.TargetWpm"/> is that same
///    unweighted mean taken over the FASTEST fifth of the lines instead of all of them, one
///    estimator over two selections of the one pool, which is why it lives here and not on the
///    rolling-window curve next door.
///  - stars: <see cref="LyricDifficulty"/>, the map's pace over sliding windows measured against
///    human typing capability, where the hardest window sets a RANGE and the characters sitting
///    near that peak difficulty fill it (backlog 273, replacing 269's greedy feats), mirroring
///    the game's typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty. That file's own summary
///    is the model description, and the prototype it is a literal port of is
///    docs/sr-envelope-model.js in the parent superrepo. Unlike the pace, this DOES use unit target
///    times (a word occupies the timeline from its onset to the end of its sung span).
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
    /// <para>BACKLOG 202 WIDENED <see cref="Typeability.PUNCTUATION"/> by seven marks (dollar sign,
    /// percent sign, caret, asterisk, the two angle brackets and the forward slash) and DELIBERATELY
    /// DID NOT BUMP THIS. Every mark but the hyphen is still deleted from the DEFAULT stream that
    /// every stat here is measured on, so a newly supported mark WEDGED INSIDE a word ("up/down",
    /// "100% off") derives exactly what it derived while it was being stripped outright. A mark
    /// standing as its OWN token does not: "ride / or" used to normalize to "ride or" and now
    /// normalizes to "ride / or", whose default stream keeps both authored spaces instead of one,
    /// i.e. one extra cell. That is the behaviour the original thirteen have always had ("a , b" has
    /// always been "a  b"), and it can only reach a stored row through this sweep, so leaving VERSION
    /// alone is exactly what keeps every existing beatmap row (word count, cell count, pace, the six
    /// ratings and therefore pp) byte-identical. The next bump, whenever one comes for its own
    /// reasons, will drag that re-derive along just as v9 dragged task 59's.</para>
    ///
    /// <para>v16 = A FREESTYLE SLOT IS PRICED AT A QUARTER OF A CELL by the star rating (backlog
    /// 211). <see cref="LyricDifficulty"/> used to drop freestyle markers from its cell stream
    /// outright, so a freestyle section was worth exactly nothing to the rating while paying full
    /// price in scoring; a slot is now worth 0.25 of an ordinary cell in the per-word cost, in the
    /// per-character window floor and in the length bonus' cell accumulator, and a token of nothing
    /// but markers is a word rather than being dropped from the map. This is v6's other half: v6
    /// made those markers count towards the PACE, and the rating has caught up. All six star
    /// columns rise on a map with any flagged freestyle line, and NOTHING moves on a map without
    /// one, which is not a tolerance claim: the weight is a cell COUNT, that count is zero there,
    /// so every such row rewrites bit-identically. The pace columns are untouched either way, no
    /// word or cell count and no WPM figure changes. It is NOT free for scores and is not meant to
    /// be: the sweep stamps <c>pp_version = 0</c> on every score of every row it rewrites, so a
    /// play on a freestyle map reprices against its honest rating.
    /// <c>PerformancePoints.VERSION</c> deliberately stays where it is, exactly as at v12: the
    /// quarter reaches pp through the star ratings and the pp FORMULA does not move. It also fills
    /// a new column, as v7, v8, v11 and v13 did for theirs: <c>beatmaps.freestyle_cell_count</c>
    /// (031_freestyle_cell_count.sql), the map's freestyle slot count as parsed. AND IT IS THE
    /// BUMP THE PARAGRAPH ABOVE NAMES: backlog 202's widened punctuation reaches stored rows only
    /// through a sweep, so an .osz-conversion map whose lyrics carry a mark STANDING AS ITS OWN
    /// TOKEN re-derives one cell longer here. That was deferred to whatever bump came next for its
    /// own reasons, and this is that bump.</para>
    ///
    /// <para>BACKLOG 255 MADE BRACKETS LITERAL AND ADDED TWO MORE MARKS (underscore and tilde),
    /// AND DELIBERATELY DID NOT BUMP THIS EITHER. Two changes, and neither can move a stored row.
    /// The brackets are gated on the file's FORMAT VERSION
    /// (<c>BeatmapPackageParser.LiteralBracketsFromVersion</c>): every map stored before that
    /// change carries a v1 magic line, so it re-parses with the backing-vocal strip exactly as it
    /// always did, and only a v2 file (which nothing has uploaded yet) keeps a literal bracket. The
    /// two new marks are the backlog 202 case again, verbatim: both are deleted from the DEFAULT
    /// stream by <see cref="Typeability.DefaultChar"/> like every non-hyphen mark, so one wedged
    /// inside a word ("well_known") derives exactly what it derived while it was stripped outright,
    /// and only one STANDING AS ITS OWN TOKEN ("ride _ or") would re-derive one cell longer. As at
    /// 202, that can reach a stored row only through this sweep, so leaving VERSION alone is what
    /// keeps every existing beatmap row byte-identical, and the next bump taken for its own reasons
    /// will drag the re-derive along just as v16 dragged 202's.</para>
    ///
    /// <para>v17 = THE STAR RATING IS A DIFFERENT MODEL (backlog 269). The strain curve is gone:
    /// per-word density and endurance, line pressure, the rhythm cv multiplier, the run and
    /// word-repetition factors, the duration-weighted soft maximum and <c>star_scale</c> with its
    /// power all went with it. <see cref="LyricDifficulty"/> now lays the map out as a timeline of
    /// 50 ms bins, measures the pace of sliding windows from 1.36 seconds up to the map's own length
    /// against S(t), the WPM the fastest humans sustain for t seconds, and sums the map's FEATS:
    /// the highest-ratio window at any duration, then the highest of what is left over after its
    /// bins are consumed, and so on, weighted 0.25^k and normalised so that record pace throughout
    /// rates 10.6. Two consequences beyond the numbers. First, INTER-WORD SPACES ARE CELLS here now,
    /// as they have always been for the pace, which also widens the length bonus' own accumulator
    /// (its clamp still gives a sub-100-cell map nothing). Second, every feat is scored against
    /// HUMAN ABILITY rather than against the map's own peak, so a cut version can no longer outrate
    /// the full version it was cut from. Measured over the 94 ranked difficulties with a per-word
    /// extract, the mean shift is +0.10 stars at a Spearman of 0.935, the Double Time premium falls
    /// from x1.55 to x1.34 and Half Time reads x0.82; a map whose whole sung timeline is under
    /// about a second and a half now rates its length term alone, since no window fits on it. All
    /// six star columns move, so the sweep re-rates the whole catalogue and stamps
    /// <c>pp_version = 0</c> on every score it touches, exactly as at v9, v10, v12 and v14. The
    /// pace columns are untouched: no word or cell count that this file publishes and no WPM figure
    /// changes, only the ratings. <c>PerformancePoints.VERSION</c> deliberately stays where it is,
    /// as at v12 and v16: the new ratings reach pp through the star columns and the pp FORMULA does
    /// not move. This backfill has to COMPLETE before any pp version bump lands, or PpBackfill
    /// prices the catalogue against ratings this sweep is still rewriting.</para>
    ///
    /// <para>v18 = <c>beatmaps.target_wpm</c> IS WRITTEN ALONGSIDE (backlog 272,
    /// 033_target_wpm.sql): <see cref="PaceStatistics.TargetWpm"/>, the average WPM across the
    /// FASTEST FIFTH of the map's lyric lines, which is the pace its demanding stretches ask for.
    /// It is the same per-line estimator <c>wpm</c> itself is (a line's typeable cells over its
    /// boundary window, unweighted across lines), taken over the top
    /// <c>target_line_fraction</c> of the lines instead of all of them, so it can never read below
    /// the stored average. As at v7, v8, v11 and v13 THE ARITHMETIC OF EVERY EXISTING COLUMN IS
    /// UNCHANGED: the new figure is a second selection over the same per-line rates the average is
    /// already taken over, nothing that feeds a count, a pace or a rating moves, and every column
    /// that already existed rewrites BYTE-IDENTICALLY. The bump exists purely to make the sweep
    /// revisit every row and fill the new one from the stored blob. The pages this feeds drop the
    /// CPM readouts in the same change (a CPM has been its WPM times five exactly since v15, so
    /// <c>peak_cpm</c> told a reader nothing <c>peak_wpm</c> did not); the COLUMN stays and stays
    /// written, since the <c>cpm:</c> search filter derives from <c>wpm</c> anyway and dropping a
    /// stored column would buy nothing.
    /// It is still not free for scores, for the mechanical reason v13 and v15 were not:
    /// <see cref="PaceBackfill"/> stamps <c>pp_version = 0</c> on every score of every row it
    /// rewrites, so the whole score table reprices at the next boot, TO IDENTICAL VALUES (pp reads
    /// star ratings, never a WPM figure, and no rating moves here).
    /// <c>PerformancePoints.VERSION</c> deliberately stays where it is. Note the ordering against
    /// v17: that sweep re-rated the whole catalogue and has already COMPLETED on production
    /// (shipped 2026-09-09), so this bump follows it cleanly rather than racing it.</para>
    ///
    /// <para>v19 = THE STAR RATING IS A DIFFERENT MODEL AGAIN (backlog 273, the ENVELOPE model; the
    /// item's own text calls it "SR v18" because it was written before backlog 272 took v18 for
    /// <c>target_wpm</c>, so the model may still be called that in prose but the constant is 19).
    /// The v17 FEATS TAIL is gone (the greedy non-overlapping extraction, <c>feat_decay</c>,
    /// <c>feat_max</c>, <c>feat_floor</c>, <c>feat_min_seconds</c> and the weight epsilon) and SO IS
    /// THE LENGTH TERM (backlog 152's additive <c>0.12 * max(0, log10(cells/100))</c>). The timeline,
    /// the cells, the 50 ms bins, the capability curve and the window schedule are all unchanged.
    /// What replaces the tail: the hardest window at any scheduled duration sets the RANGE
    /// (<c>10.6 / (1 + 1/3) * ratio_0</c> is its floor), and where the map lands inside that range is
    /// decided by how many of its CHARACTERS sit near that peak difficulty, each weighted
    /// <c>(env/ratio_0)^8</c> with no cutoff and summed to N, the range filling as
    /// <c>1 - exp(-N/500)</c>. The reason is that the feats tail credited a map for how many windows
    /// its difficulty happened to SPLIT INTO: eight two-second bursts filled eight slots while two
    /// sixty-second sections filled two, so burst-built maps read about 7% too high, a map that was
    /// one long feat had nothing to add, and a cut sharing its full version's hardest part could tie
    /// it. Counting characters instead of windows cannot be gamed by chopping. Length now counts
    /// ONLY through the characters it adds, which is a much softer signal than the old term: padding
    /// a 123 s sustain with 60 s of 60 WPM singing is worth about +0.09%.
    /// Measured over the 94 ranked difficulties with a per-word extract, the mean shift is -0.69
    /// stars against v17 at a Spearman of 0.985 and -0.59 against the pre-269 strain rating at 0.948;
    /// the median map fills 48% of its range (p10 15%, p90 78%), no cut outrates its full version in
    /// any of the 7 pairs, and the Double Time premium reads x1.386 against Half Time's x0.800. The
    /// biggest falls are the burst-built maps and the biggest rises the sustained ones. A map whose
    /// whole sung timeline is under the smallest scheduled window now rates EXACTLY zero rather than
    /// its length term (there is no length term to rate).
    /// All six star columns move, so the sweep re-rates the whole catalogue and stamps
    /// <c>pp_version = 0</c> on every score it touches, exactly as at v9, v10, v12, v14 and v17. The
    /// pace columns are untouched: no word or cell count and no WPM figure changes, only the ratings.
    /// <c>PerformancePoints.VERSION</c> deliberately stays at 21, as at v12, v16 and v17: the new
    /// ratings reach pp through the star columns and the pp FORMULA does not move. As at v17, this
    /// backfill has to COMPLETE before any pp version bump lands, or PpBackfill prices the catalogue
    /// against ratings this sweep is still rewriting.</para>
    ///
    /// <para>v20 = THE STAR ANCHOR MOVES FROM 10.6 TO 12.0 (backlog 273's recorded alternative, now
    /// taken). This is a ONE CONSTANT change: <c>LyricDifficulty.stars_at_human_peak</c>, what a map
    /// typing at the record pace throughout with its range filled is worth. Nothing else about the
    /// envelope model moves, and the model is LINEAR in that constant, so every stored rating simply
    /// multiplies by <c>12 / 10.6</c> (about x1.132) and NOTHING can reorder: the six star columns
    /// keep their ordering exactly, cuts still sit under their full versions, and the rate premiums
    /// (Double Time x1.386, Half Time x0.800) are ratios and do not move at all. The reason is the
    /// one v19 measured and left on the table: the envelope at 10.6 read a mean 0.59 star BELOW the
    /// pre-269 strain ratings the pool had been shown, because the median map fills only 48% of its
    /// range and therefore lands near the floor rather than near the anchor. 12.0 restores that mean
    /// without touching a single shape in the model. Record pace with the range filled now rates
    /// 12.0 and the hardest window alone rates 12.0 / 1.3333 (about 9.0).
    /// All six star columns move, so <see cref="PaceBackfill"/> re-rates the whole catalogue at boot
    /// and stamps <c>pp_version = 0</c> on every score of every row it rewrites, exactly as at v9,
    /// v10, v12, v14, v17 and v19, so <see cref="PpBackfill"/> reprices the score table in the same
    /// startup. The pace columns are untouched: no word or cell count and no WPM figure changes,
    /// only the ratings. <c>PerformancePoints.VERSION</c> deliberately stays at 21, as at v12, v16,
    /// v17 and v19: pp reads the ratings as INPUTS, so every pp value here moves through
    /// <c>SR_eff</c> alone and the pp FORMULA does not move, which is exactly the intended
    /// repricing. Note the ordering against v19: that sweep has already COMPLETED on production
    /// (shipped 2026-09-09, the catalogue re-rated at 10.6), so this bump follows it cleanly rather
    /// than racing it, and it is the bump that INVALIDATES those 10.6-scale rows.</para>
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
    public const int VERSION = 20;

    /// <summary>
    /// Typeable cells per word, the typing-test convention. Same 5 as the game's
    /// <c>LyricPaceStatistics.CHARS_PER_WORD</c> and <c>TypingEngine.LiveWpm</c>, and as
    /// <see cref="LyricWpmCurve.CHARS_PER_WORD"/> next door; all of them must agree or the map's
    /// advertised pace stops meaning what the HUD shows.
    /// </summary>
    public const double CHARS_PER_WORD = 5.0;

    // LyricPaceStatistics.cs: guards degenerate data from exploding the rate.
    private const double min_line_window_ms = 500;

    /// <summary>
    /// LyricPaceStatistics.cs: how much of the map <see cref="PaceStatistics.TargetWpm"/> averages,
    /// the fastest 0.20 of its counted lines, rounded UP so every map has at least one line in the
    /// selection however short it is.
    /// </summary>
    private const double target_line_fraction = 0.20;

    /// <param name="AverageCpm">Mean of per-line (typeable cells / boundary window) rates.</param>
    /// <param name="TargetWpm">
    /// The pace to SUSTAIN: the average WPM across the FASTEST <see cref="target_line_fraction"/>
    /// of the map's lyric lines (rounded up, at least one), stored as <c>beatmaps.target_wpm</c>
    /// (033_target_wpm.sql). Exactly the estimator <see cref="PaceStatistics.AverageWpm"/> is,
    /// restricted to the demanding lines, so the two read as a pair and
    /// <see cref="PaceStatistics.AverageWpm"/> &lt;= this ALWAYS (a mean over the top fifth cannot
    /// sit below the mean over all of them), with equality exactly when every counted line runs at
    /// the same rate.
    ///
    /// <para>Selection is by LINE, not by keystroke and not by time, so an instrumental gap between
    /// lines cannot dilute it and a burst inside one line cannot inflate it: a line is one vote
    /// whatever it holds, the same convention <paramref name="AverageCpm"/> already uses. 0 for a
    /// map with no counted line.</para>
    /// </param>
    /// <param name="DifficultyRating">Stars from <see cref="LyricDifficulty"/> (no-mod baseline).</param>
    /// <param name="FreestyleCellCount">
    /// How many of <paramref name="TypeableCellCount"/> are FREESTYLE slots, i.e. cells with a
    /// deadline and no letter (<see cref="Typeability.IsFreestyle"/>), stored as
    /// <c>beatmaps.freestyle_cell_count</c> (031_freestyle_cell_count.sql). A SUBSET of that count
    /// and never an addition to it: markers have counted towards the pace since v6, and this says
    /// how much of it they are. 0 on every map without a line flagged <c>"freestyle": true</c>,
    /// which is nearly all of them, and the number the star rating prices at a quarter each since
    /// v16 (<see cref="LyricDifficulty"/>).
    /// </param>
    public readonly record struct PaceStatistics(
        double AverageCpm,
        int TypeableCellCount,
        int WordCount,
        double DifficultyRating,
        int FreestyleCellCount,
        double TargetWpm)
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
        int totalFreestyle = 0;
        int lineCount = 0;
        double cpmSum = 0;

        // Every counted line's own rate, kept so TargetWpm can average the fastest fifth of them.
        // One entry per line, in the same order cpmSum accumulates, so the selection pool is
        // EXACTLY the set of lines the map average is taken over: a line skipped for holding no
        // cell is skipped by both or by neither.
        var lineCpms = new List<double>();

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
            int freestyle = 0;

            foreach (string token in tokens)
            {
                int typeable = 0;

                foreach (char ch in token)
                {
                    // Freestyle slots are keypresses too, so they count towards the pace.
                    if (Typeability.IsCell(ch))
                        typeable++;

                    // And they are counted AGAIN on their own, as a subset rather than an
                    // addition: beatmaps.freestyle_cell_count says how much of the map is cells
                    // with a deadline and no letter, which is what the star rating prices at a
                    // quarter each (v16). Counted here, on the same walk over the same default
                    // stream every other per-map stat is measured on, so it cannot end up
                    // describing a different text from the one the counts above describe.
                    if (Typeability.IsFreestyle(ch))
                        freestyle++;
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
            double lineCpm = cells / windowMinutes;

            cpmSum += lineCpm;
            lineCpms.Add(lineCpm);
            totalCells += cells;
            totalWords += words;
            totalFreestyle += freestyle;
            lineCount++;
        }

        if (lineCount == 0)
            return default;

        // The fastest fifth, by line: sort the per-line rates DESCENDING and take the head of the
        // list. The count rounds up, so a map of four lines still selects its one hardest line
        // rather than an empty slice, and Math.Max is belt and braces around a retune of
        // target_line_fraction (at 0.20 the ceiling is already at least 1 for every lineCount this
        // line is reachable with).
        lineCpms.Sort((a, b) => b.CompareTo(a));

        int targetLines = Math.Max(1, (int)Math.Ceiling(target_line_fraction * lineCount));
        double targetCpmSum = 0;

        for (int i = 0; i < targetLines; i++)
            targetCpmSum += lineCpms[i];

        return new PaceStatistics(
            cpmSum / lineCount,
            totalCells,
            totalWords,
            LyricDifficulty.Compute(lines),
            totalFreestyle,
            targetCpmSum / targetLines / CHARS_PER_WORD);
    }
}
