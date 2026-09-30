namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// THE ONE derivation of how a word's authored pauses (<see cref="TimedUnit.Pauses"/>, the Map
/// Editor's Insert Pause) cut that word into the stretches it is sung in.
///
/// <para>Mirrors the game's <c>typebeat.Game.Rulesets.TypeBeat.Gameplay.PausedWord</c>, REDUCED to
/// the two things the server reads: which of a word's rests survive
/// (<see cref="UsableRests"/>, which the lyric parser keeps exactly as the game's
/// <c>TimingJsonLoader</c> does) and the span each sung stretch covers (<see cref="Of"/>'s
/// <see cref="Piece.StartTime"/> and <see cref="Piece.EndTime"/>, which
/// <see cref="LyricDifficulty"/>'s <c>BuildWords</c> turns into judgement groups). The game's
/// pieces also carry each stretch's characters, cells, boundaries and char cuts, and the type
/// carries the editor's display runs; none of those reach a rating or a pace figure (a stretch's
/// span depends on the rests alone), so they are the one sanctioned difference, and the server's
/// <see cref="TimedUnit"/> has no authored char split for them to read anyway. Any change to
/// which rests survive or to where a stretch begins or ends must be made on both sides, with
/// <see cref="LyricPace.VERSION"/> bumped.</para>
///
/// <para>A rest is a SUBDIVISION that happens to have no characters in it, and every reader of a
/// subdivision reads it: the ENGINE times each stretch's characters independently, so no cell has
/// a target inside a rest and the characters after one are timed FROM its end; the judgement
/// groups gain a pair of edges at every rest, so the characters on the far side are judged
/// against their own sung span exactly as a syllable subdivider's are.</para>
///
/// <para>N rests make N + 1 stretches. A rest that CANNOT cut its word is IGNORED here, and the
/// stretches close over it: edges that have left the word's own span, an inverted rest, a split
/// that leaves every typeable cell on one side of it (a split sitting on punctuation, which would
/// be a rest before the word's first character or after its last rather than one INSIDE it), and
/// one that overlaps a rest already counted. That is the same conservative answer the loader gives
/// a rest that no longer fits, and it is why a word whose every rest is ignored reads exactly as
/// if it had none.</para>
///
/// <para>Since PR 3 both entry points take the CELL RULE as an optional parameter, as the game's do
/// (its Polyglot play cuts a word in its original script, where a cell is any script's letter). The
/// default is <see cref="Typeability.IsCell"/>, the rule these always used, so every stored map, the
/// ratings and the parser read exactly what they read before; the server never passes another
/// rule, because it never rates a Polyglot play.</para>
/// </summary>
internal static class PausedWord
{
    /// <summary>
    /// One sung stretch of a word: the span it is sung over. <see cref="StartTime"/> and
    /// <see cref="EndTime"/> never cover a rest: consecutive stretches do NOT meet where one sits
    /// between them, and that gap belongs to no stretch, exactly as a space belongs to no syllable.
    /// </summary>
    internal readonly record struct Piece(double StartTime, double EndTime);

    /// <summary>How a word's rests cut it: its sung stretches.</summary>
    internal sealed class Cut
    {
        /// <summary>The sung stretches, in text and in time order.</summary>
        internal IReadOnlyList<Piece> Pieces { get; init; } = Array.Empty<Piece>();
    }

    /// <summary>
    /// How <paramref name="unit"/>'s authored rests cut it, or null when it has none this derivation
    /// can honour (see the type's remarks), in which case every reader keeps the plain word it had
    /// before the feature existed.
    /// </summary>
    internal static Cut? Of(string token, double unitStart, double unitEnd, TimedUnit? unit, Func<char, bool>? isCell = null)
    {
        if (unit == null || unit.Pauses.Count == 0)
            return null;

        isCell ??= Typeability.IsCell;
        var rests = UsableRests(token, unitStart, unitEnd, unit.Pauses, isCell);

        if (rests.Count == 0)
            return null;

        int stretches = rests.Count + 1;
        var pieces = new List<Piece>(stretches);

        for (int i = 0; i < stretches; i++)
            pieces.Add(new Piece(stretchStart(rests, unitStart, i), stretchEnd(rests, unitEnd, i)));

        return new Cut { Pieces = pieces };
    }

    /// <summary>
    /// The rests a word really has, in time order: each one strictly inside the word with a split
    /// that separates typeable characters, none overlapping an earlier one, no two sharing a
    /// character, and every later rest sitting on a later character than the one before it, so the
    /// word's dividers read the same way from left to right in TIME and in TEXT.
    ///
    /// <para>Shared with the LOADER (<see cref="LyricTiming"/>), which keeps exactly this set when
    /// it reads a map, so a rest the play would ignore is never stored in the first place.</para>
    /// </summary>
    internal static List<WordPause> UsableRests(string token, double unitStart, double unitEnd, IEnumerable<WordPause> pauses, Func<char, bool>? isCell = null)
    {
        isCell ??= Typeability.IsCell;
        var rests = new List<WordPause>();
        int totalCells = token.Count(c => isCell(c));

        foreach (var pause in pauses.OrderBy(p => p.StartTime))
        {
            if (pause.StartTime <= unitStart || pause.EndTime >= unitEnd || pause.StartTime >= pause.EndTime)
                continue;

            int cut = Math.Clamp(pause.SplitChar, 0, token.Length);
            int cells = cellsBefore(token, cut, isCell);

            if (cells <= 0 || cells >= totalCells)
                continue;

            if (rests.Count > 0 && (pause.StartTime < rests[^1].EndTime || cut <= rests[^1].SplitChar))
                continue;

            rests.Add(pause);
        }

        return rests;
    }

    /// <summary>The start of stretch <paramref name="index"/>: the word's own start, or the end of the
    /// rest before it.</summary>
    private static double stretchStart(IReadOnlyList<WordPause> rests, double unitStart, int index)
        => index == 0 ? unitStart : rests[index - 1].EndTime;

    /// <summary>The end of stretch <paramref name="index"/>: the word's own end, or the start of the
    /// rest after it.</summary>
    private static double stretchEnd(IReadOnlyList<WordPause> rests, double unitEnd, int index)
        => index == rests.Count ? unitEnd : rests[index].StartTime;

    /// <summary>
    /// How many of a token's typeable cells sit before <paramref name="charIndex"/>.
    /// </summary>
    private static int cellsBefore(string token, int charIndex, Func<char, bool> isCell)
    {
        int cells = 0;

        for (int i = 0; i < charIndex && i < token.Length; i++)
        {
            if (isCell(token[i]))
                cells++;
        }

        return cells;
    }
}
