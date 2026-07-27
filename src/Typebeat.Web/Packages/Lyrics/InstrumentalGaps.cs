namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Server-side mirror of the game's instrumental-skip qualification rule
/// (typebeat-osu typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs, class
/// <c>InstrumentalGaps</c>), reduced to the one number the anti-cheat play-time gate needs: how
/// much wall-clock a player is ALLOWED to remove from a map by pressing every skip at the earliest
/// possible moment.
///
/// <para>
/// CROSS-REPO INVARIANT. The four constants below and the qualification arithmetic in
/// <see cref="Compute"/> are a byte-for-byte mirror of the game's, exactly like
/// <c>Scoring/RateMultiplier.cs</c> mirrors the game's rate curve and
/// <c>Scoring/ScoringContract.cs</c> mirrors its score processor. If the game changes what
/// qualifies as a skippable gap or where a skip lands, this file must change with it, or the gate
/// in <see cref="Typebeat.Web.Scoring.PlayTimeGate"/> starts unranking honest plays again (which is
/// precisely what backlog task 47 was). The game's authority:
///   typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs  (MIN_GAP_MS, GAP_START_SETTLE_MS,
///                                                                  MIN_SKIP_WINDOW_MS, SKIP_LEAD_MS, Compute)
///   typebeat.Game.Rulesets.TypeBeat/Gameplay/TypingEngine.cs:24   (CUE_LEAD_MS)
///   typebeat.Game.Rulesets.TypeBeat/Gameplay/TypingLine.cs:86-162 (FirstVocalTime, ActivationTime, cell targets)
///   typebeat.Game/Screens/Play/Player.cs:536-552, 774-797         (the overlay + the seek it performs)
/// </para>
///
/// <para>
/// WHAT THE GAME ACTUALLY DOES. Between two consecutive lyric lines it measures the PERCEIVED
/// instrumental (previous line's <c>SingEndTime</c> to the next line's <c>FirstVocalTime</c>, i.e.
/// vocal-end to vocal-start, never the mechanical line boundaries, which are contiguous on real
/// maps and therefore carry no information). A stretch of at least <see cref="MIN_GAP_MS"/>
/// qualifies. The skip button then lives from <c>sungEnd + GAP_START_SETTLE_MS</c> to
/// <c>nextLine.ActivationTime - SKIP_LEAD_MS</c>, and pressing it seeks the clock to that latter
/// time. A qualifying stretch whose usable window is shorter than <see cref="MIN_SKIP_WINDOW_MS"/>
/// is dropped rather than shown. There is NO intro gap (the pre-first-line lead-in is the separate
/// intro <c>SkipOverlay</c>, which skips only the run-up the drain length already excludes) and NO
/// outro gap (nothing follows the last line; the outro overlay ends the play, it does not seek).
/// </para>
///
/// <para>
/// All times are milliseconds of BEATMAP time, never real time: the game applies these constants to
/// decoded lyric times and rate mods change the clock rate, not the object times. So a gap qualifies
/// identically at 0.75x and at 2.00x, and the real seconds a skip removes are the map-time figure
/// divided by the rate. <see cref="SkippableSeconds"/> therefore returns map-time seconds, which is
/// the same unit <c>beatmaps.drain_length_s</c> is in.
/// </para>
/// </summary>
public static class InstrumentalGaps
{
    /// <summary>Perceived vocal-to-vocal stretch that qualifies for a skip. (InstrumentalGaps.cs:61.)</summary>
    public const double MIN_GAP_MS = 10_000;

    /// <summary>How long after the previous line's last sung moment the skip period opens. (:69.)</summary>
    public const double GAP_START_SETTLE_MS = 1_000;

    /// <summary>Shortest usable skip period; a qualifying gap squeezed below it is dropped. (:76.)</summary>
    public const double MIN_SKIP_WINDOW_MS = 1_000;

    /// <summary>How far before the next line's activation a skip lands. (:86.)</summary>
    public const double SKIP_LEAD_MS = 3_000;

    /// <summary>
    /// The cue lead that pulls a line's activation ahead of its first vocal
    /// (typebeat.Game.Rulesets.TypeBeat/Gameplay/TypingEngine.cs:24, consumed by
    /// TypingLine.ActivationTime).
    /// </summary>
    public const double CUE_LEAD_MS = 1_500;

    /// <summary>
    /// One qualifying gap. <see cref="SkippableMs"/> is the maximum a player can remove from it:
    /// the whole skip period, pressed at the first frame it is live.
    /// </summary>
    public readonly record struct Gap(double GapStartTime, double ActivationTime, double SkipTarget)
    {
        public double SkippableMs => SkipTarget - GapStartTime;
    }

    /// <summary>
    /// The qualifying gaps of a parsed map, in order. Mirrors <c>InstrumentalGaps.Compute</c>:
    /// consecutive pairs only, qualification on the perceived stretch, then the usability filter.
    /// </summary>
    public static IReadOnlyList<Gap> Compute(IReadOnlyList<LyricLine>? lines)
    {
        var gaps = new List<Gap>();

        if (lines == null || lines.Count < 2)
            return gaps;

        for (int i = 0; i < lines.Count - 1; i++)
        {
            double perceived = FirstVocalTime(lines[i + 1]) - lines[i].SingEndTime;

            if (perceived < MIN_GAP_MS)
                continue;

            // The last moment the earlier line is genuinely being sung/typed. Normally its
            // SingEndTime; the game takes the later of that and its last typeable cell target,
            // because word times can overrun the reported line end on weird data.
            double sungEnd = Math.Max(lines[i].SingEndTime, LastTypeableTarget(lines[i]) ?? double.NegativeInfinity);

            double gapStart = sungEnd + GAP_START_SETTLE_MS;
            double activation = ActivationTime(lines[i + 1]);
            double skipTarget = activation - SKIP_LEAD_MS;

            if (skipTarget - gapStart >= MIN_SKIP_WINDOW_MS)
                gaps.Add(new Gap(gapStart, activation, skipTarget));
        }

        return gaps;
    }

    /// <summary>
    /// Total map-time SECONDS a player may legally remove from this map with the skip button: the
    /// sum of every qualifying gap's whole skip period. Zero for a map with fewer than two lines
    /// (including the blank maps task 45 made importable, which cannot be played or submitted
    /// anyway). This is deliberately the MAXIMUM removable time: the play-time gate is a lower
    /// bound, so it must assume the fastest legal play.
    /// </summary>
    public static double SkippableSeconds(IReadOnlyList<LyricLine>? lines)
    {
        double total = 0;

        foreach (var gap in Compute(lines))
            total += gap.SkippableMs;

        return total / 1000;
    }

    /// <summary>
    /// When a line's vocals begin: its first typeable cell's target time, falling back to
    /// <c>StartTime</c> when it has none (TypingLine.cs:147-162).
    /// </summary>
    public static double FirstVocalTime(LyricLine line) => FirstTypeableTarget(line) ?? line.StartTime;

    /// <summary>
    /// When a line becomes typeable: <see cref="CUE_LEAD_MS"/> before its first vocal, never before
    /// its own StartTime (TypingLine.cs:158-160).
    /// </summary>
    public static double ActivationTime(LyricLine line)
        => FirstTypeableTarget(line) is double first ? Math.Max(line.StartTime, first - CUE_LEAD_MS) : line.StartTime;

    // ---- cell target times, the two extremes the gap arithmetic needs ----
    //
    // TypingLine.FromLyricLine lays a line out as: for each whitespace token, one cell per char
    // (typeable iff Typeability.IsCell) with the typeable ones taking target
    // syllableCharTarget(unitStart, unitEnd, boundaries, k, j); plus, after every token but the
    // last, an inter-word SPACE cell which is also typeable and targets that token's unit end.
    // Only the FIRST and LAST typeable targets are needed here, and for those the syllable
    // subdivision drops out or is not available:
    //   * char j = 0 always lands exactly on unitStart, whatever the boundaries are
    //     (TypingLine.syllableCharTarget: segment 0, offset 0), so the first vocal is exact;
    //   * the last typeable char uses the flat ramp unitStart + (k-1)(unitEnd-unitStart)/k, which
    //     is what the game computes for every word the SERVER can see: LyricTiming does not decode
    //     words[].syllables (only the browser player's typebeat-core.js does), and a subdivided
    //     word's last char still sits inside [unitStart, unitEnd]. It only matters at all in the
    //     weird-data case where a word overruns SingEndTime.
    // The game's monotonic-clamp pass over targets cannot move either extreme: units are already
    // non-decreasing out of LyricTiming.BuildLines.

    /// <summary>Target time of the line's first typeable cell, or null when it has none.</summary>
    public static double? FirstTypeableTarget(LyricLine line)
    {
        string[] tokens = line.RawText.Split(' ');

        for (int m = 0; m < tokens.Length; m++)
        {
            var (unitStart, unitEnd) = UnitSpan(line, m);
            int k = CellCount(tokens[m]);

            if (k > 0)
                return unitStart; // char j = 0

            // A token with no typeable char at all (pure punctuation) contributes no cell, but the
            // inter-word space that follows it does, and it targets the unit end.
            if (m < tokens.Length - 1)
                return unitEnd;
        }

        return null;
    }

    /// <summary>Target time of the line's last typeable cell, or null when it has none.</summary>
    public static double? LastTypeableTarget(LyricLine line)
    {
        string[] tokens = line.RawText.Split(' ');

        for (int m = tokens.Length - 1; m >= 0; m--)
        {
            var (unitStart, unitEnd) = UnitSpan(line, m);
            int k = CellCount(tokens[m]);

            if (k > 0)
                return unitStart + (double)(k - 1) * (unitEnd - unitStart) / k; // char j = k - 1

            // Nothing typeable in this token: the previous cell is the space that precedes it.
            if (m > 0)
                return UnitSpan(line, m - 1).UnitEnd;
        }

        return null;
    }

    /// <summary>
    /// The timed unit token <paramref name="index"/> is laid out against, with the game's guards for
    /// malformed data where the token count and the unit count disagree (TypingLine.cs:238-241).
    /// </summary>
    private static (double UnitStart, double UnitEnd) UnitSpan(LyricLine line, int index)
    {
        if (line.Units.Count == 0)
            return (line.StartTime, line.SingEndTime);

        var unit = line.Units[Math.Min(index, line.Units.Count - 1)];
        return (unit.StartTime, unit.EndTime);
    }

    private static int CellCount(string token)
    {
        int k = 0;

        foreach (char ch in token)
        {
            if (Typeability.IsCell(ch))
                k++;
        }

        return k;
    }
}
