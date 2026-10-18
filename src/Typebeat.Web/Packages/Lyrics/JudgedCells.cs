namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// How many cells of a stored map the game JUDGES, which is what an honest submission's
/// <c>maximum_statistics</c> counts: the game builds one nested scoring object per typeable cell
/// (<c>TypeBeatHitObject.CreateNestedHitObjects</c>), each with a Great maximum, and its score
/// processor reports one Great per object. Stored per difficulty at ingest
/// (<c>048_judged_cells.sql</c>) so a submission can be held against the map instead of against
/// its own claim (<see cref="Scoring.ScoreSubmission.MaximumMatchesMap"/>).
///
/// <para>
/// This is a server MIRROR of the cell rules in the game's <c>TypingLine.build</c>
/// (<c>Gameplay/TypingLine.cs</c>), reduced to the count: targets, groups and words do not move
/// it. <c>JudgedCellCountParityTest</c> in WireCompat holds it against the game's converter, mods
/// applied, and against the browser's cell list, on the same .osu bytes.
/// </para>
///
/// <list type="bullet">
/// <item>DEFAULT stream (every play without Literate): the line is projected through
/// <see cref="Typeability.ProjectDefault"/> and every surviving char that
/// <see cref="Typeability.IsCell"/> accepts is judged (letters, digits, spaces and freestyle
/// slots).</item>
/// <item>LITERATE stream (the <c>LT</c> mod): one cell per authored char, and a char is judged
/// when it is a cell char, a token-separating space, or a supported
/// <see cref="Typeability.IsPunctuation"/> mark.</item>
/// </list>
///
/// <para>
/// NOT <c>char_count</c>: that is a pace figure (freestyle slots excluded, default stream only), and
/// it is pinned against the game's pace statistics, not against its converter. Polyglot also
/// changes the count, but it is local only and the server refuses it before a count is read.
/// </para>
///
/// <para>
/// Any change to these cell rules, or to the stored <c>[Lyrics]</c> parse they read
/// (<see cref="LyricTiming"/>, <see cref="Typeability"/>), must bump <see cref="VERSION"/>.
/// </para>
///
/// <para>
/// A map the game decodes DIFFERENTLY is stored with no count (<see cref="ForMap"/>): the game's
/// legacy decoder cuts any line at a <c>//</c> past its first character
/// (<c>LegacyDecoder.StripComments</c>), so a <c>[Lyrics]</c> line carrying one (a URL in the
/// text, an original "x//y") is dropped by the game while this parse and the browser keep it. The
/// parse is not changed to match, because it feeds the ratings; such a map is simply not checked.
/// </para>
/// </summary>
public static class JudgedCells
{
    /// <summary>
    /// Stamps <c>beatmaps.judged_cells_version</c>. Bump it with any change to the rules above (or
    /// to the parse they read), so <see cref="Packages.JudgedCellBackfill"/> recounts every row and
    /// a submission is never refused against a count the current rules did not produce.
    /// </summary>
    public const int VERSION = 1;

    /// <summary>
    /// What ingest and <see cref="Packages.JudgedCellBackfill"/> store for a parsed difficulty: the
    /// default and Literate counts, or both null when the game would not decode the same lines
    /// (<see cref="GameReadsLikeTheServer"/>). Null under the current <see cref="VERSION"/> is read
    /// as unknown, so the map's submissions are accepted unchecked and the backfill does not retry it.
    /// </summary>
    public static (int? Cells, int? Literate) ForMap(IReadOnlyList<string> lyricSectionLines, IEnumerable<LyricLine> lines)
    {
        if (!GameReadsLikeTheServer(lyricSectionLines))
            return (null, null);

        var all = lines as IReadOnlyCollection<LyricLine> ?? lines.ToList();
        return (Count(all, literate: false), Count(all, literate: true));
    }

    /// <summary>
    /// False when a <c>[Lyrics]</c> line (trimmed, as the server keeps them; a line STARTING with
    /// <c>//</c> is a comment on both sides and never reaches here) has a <c>//</c> past its first
    /// character, which the game's decoder strips as a comment and so drops the line.
    /// </summary>
    public static bool GameReadsLikeTheServer(IReadOnlyList<string> lyricSectionLines)
    {
        foreach (string line in lyricSectionLines)
        {
            if (line.IndexOf("//", StringComparison.Ordinal) > 0)
                return false;
        }

        return true;
    }

    /// <summary>The judged cells of the whole map, on the default or the Literate stream.</summary>
    public static int Count(IEnumerable<LyricLine> lines, bool literate)
    {
        int total = 0;

        foreach (var line in lines)
            total += literate ? CountLiterate(line.RawText) : CountDefault(line.RawText);

        return total;
    }

    /// <summary>One line's judged cells on the default stream.</summary>
    public static int CountDefault(string rawText) => Typeability.TypeableCount(Typeability.ToDefaultStream(rawText));

    /// <summary>
    /// One line's judged cells on the Literate stream. Every space in the raw text separates two
    /// tokens and is a typed cell, which <see cref="Typeability.IsCell"/> already says of a space.
    /// </summary>
    public static int CountLiterate(string rawText)
    {
        int count = 0;

        foreach (char c in rawText)
        {
            if (Typeability.IsCell(c) || Typeability.IsPunctuation(c))
                count++;
        }

        return count;
    }
}
