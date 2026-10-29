using System.Text;

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
/// WORD OBJECTS AND SHORTHAND (v2, <see cref="CountLine"/>): every word under Shorthand (<c>SH</c>)
/// keeps only its onsets typed (marks and spaces complete); a usable hold adds its tail and each
/// usable tick. Shorthand has its own pair of counts
/// (<see cref="Counts"/>), checked like Polyglot's.
/// </para>
///
/// <para>
/// NOT <c>char_count</c>: that is a pace figure (freestyle slots excluded, default stream only), and
/// it is pinned against the game's pace statistics, not against its converter. Polyglot changes the
/// count too: a Polyglot play is held against its own stream instead
/// (<see cref="PolyglotSummary"/>, <see cref="Scoring.ScoreSubmission.PolyglotMaximumMatchesMap"/>).
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
    public const int VERSION = 2;

    /// <summary>The default and Literate counts, and both again under Shorthand.</summary>
    public readonly record struct Counts(int? Cells, int? Literate, int? Shorthand, int? ShorthandLiterate);

    /// <summary>
    /// What ingest and <see cref="Packages.JudgedCellBackfill"/> store for a parsed difficulty, or all
    /// null when the game would not decode the same lines (<see cref="GameReadsLikeTheServer"/>). Null
    /// under the current <see cref="VERSION"/> is read as unknown: submissions are accepted unchecked.
    /// </summary>
    public static Counts ForMap(IReadOnlyList<string> lyricSectionLines, IEnumerable<LyricLine> lines)
    {
        if (!GameReadsLikeTheServer(lyricSectionLines))
            return default;

        var all = lines as IReadOnlyCollection<LyricLine> ?? lines.ToList();
        return new Counts(Count(all, literate: false), Count(all, literate: true),
            Count(all, literate: false, shorthand: true), Count(all, literate: true, shorthand: true));
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

    /// <summary>The judged cells of the whole map, on the default or the Literate stream, with or without Shorthand.</summary>
    public static int Count(IEnumerable<LyricLine> lines, bool literate, bool shorthand = false)
    {
        int total = 0;

        foreach (var line in lines)
            total += CountLine(line, literate, shorthand);

        return total;
    }

    /// <summary>
    /// One line's judgements: its cells (<see cref="CountDefault"/> or <see cref="CountLiterate"/> on a
    /// line without objects played without Shorthand), else the cells the onsets rule leaves typed
    /// plus each usable hold's tail and ticks (<c>TypingLine.build</c> with <c>wordObjects</c> on, then
    /// <c>TypeBeatHitObject.CreateNestedHitObjects</c>).
    /// </summary>
    public static int CountLine(LyricLine line, bool literate, bool shorthand = false)
    {
        if (!shorthand && line.Units.All(u => u.Objects.IsEmpty))
            return literate ? CountLiterate(line.RawText) : CountDefault(line.RawText);

        string text = line.RawText;
        int n = text.Length;
        var units = line.Units.Select(u => u.AsPlayed()).ToArray();
        string[] tokens = text.Split(' ');

        // The cells (TypingLine.build's Literate and default passes, reduced to char and typeability).
        char[] expected;
        bool[] typeable;
        List<int>? defaultSources = null;

        if (literate)
        {
            expected = text.ToCharArray();
            typeable = expected.Select(c => Typeability.IsCell(c) || Typeability.IsPunctuation(c)).ToArray();
        }
        else
        {
            var sb = new StringBuilder(n);
            defaultSources = new List<int>(n);
            Typeability.ProjectDefault(text, sb, defaultSources);
            expected = sb.ToString().ToCharArray();
            typeable = expected.Select(Typeability.IsCell).ToArray();
        }

        int cellCount = expected.Length;
        int[] rawGroup = authoredGroups(line, units, tokens, n);

        // The authored syllable of a cell (TypingLine.buildSyllables: a space or untyped cell is in none).
        int groupOf(int i) => typeable[i] && expected[i] != ' ' ? rawGroup[defaultSources?[i] ?? i] : -1;

        // Word objects (TypingLine.build): found only when some word carries one.
        Dictionary<int, WordHold>? held = null;

        if (units.Any(u => !u.Objects.IsEmpty))
            held = heldCellsOf(units, cellPositions(tokens, n, cellCount, defaultSources), typeable);

        bool[] typed = (bool[])typeable.Clone();

        if (shorthand)
            applyOnsetsOnly(expected, typeable, typed, groupOf, held);

        int count = typed.Count(t => t);

        // Each held cell's tail, and its usable ticks (TypeBeatHitObject.CreateNestedHitObjects).
        if (held != null)
        {
            foreach (var hold in held.Values)
                count += 1 + HoldTicks.UsableTicks(hold).Length;
        }

        return count;
    }

    /// <summary>
    /// The game's Shorthand onsets-only pass (<c>TypingLine.applyOnsetsOnly</c>) on the typed flags:
    /// only the first letter of each word and of each authored syllable stays typed; freestyle slots
    /// and held cells stay typed; marks and every space are completed.
    /// </summary>
    private static void applyOnsetsOnly(char[] expected, bool[] typeable, bool[] typed, Func<int, int> groupOf, IReadOnlyDictionary<int, WordHold>? held)
    {
        int? lastGroup = null;

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == ' ')
            {
                lastGroup = null;

                if (typeable[i])
                    typed[i] = false;

                continue;
            }

            if (!typeable[i])
                continue;

            bool freestyle = Typeability.IsFreestyle(expected[i]);

            if (!freestyle && !char.IsLetterOrDigit(expected[i]))
            {
                typed[i] = false;
                continue;
            }

            int group = groupOf(i);
            bool onset = lastGroup == null || (group >= 0 && group != lastGroup);

            lastGroup = group;

            if (!(onset || freestyle || held?.ContainsKey(i) == true))
                typed[i] = false;
        }
    }

    /// <summary>
    /// Per raw char, its AUTHORED syllable (<c>TypingLine.buildSyllables</c> with <c>authoredOnly</c>),
    /// as a group id distinct per syllable, or -1 for a char in no group. Only the ids' identity is
    /// read, so the game's compaction of empty groups and the spans are left out.
    /// </summary>
    private static int[] authoredGroups(LyricLine line, IReadOnlyList<TimedUnit> units, string[] tokens, int n)
    {
        int[] rawGroup = new int[n];
        Array.Fill(rawGroup, -1);

        int groups = 0;
        int tokStart = 0;

        for (int m = 0; m < tokens.Length; m++)
        {
            string token = tokens[m];
            TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

            double unitStart = unit?.StartTime ?? line.StartTime;
            double unitEnd = unit?.EndTime ?? line.SingEndTime;
            var boundaries = unit?.SyllableBoundaries ?? Array.Empty<double>();
            bool subtimed = boundaries.Count > 0;
            var paused = unit != null ? PausedWord.Of(token, unitStart, unitEnd, unit, Typeability.IsCell) : null;

            if (token.Length > 0 && (subtimed || paused != null || Syllabifier.IsSyllabifiable(token)))
            {
                IReadOnlyList<int> splits = paused != null
                    ? paused.Splits
                    : subtimed
                        ? SyllableSegments.SplitsFor(token, boundaries.Count + 1, unit?.SyllableSplits)
                        : Array.Empty<int>();

                int groupBase = groups;
                groups += splits.Count + 1;
                int inGroup = 0;

                for (int t = 0; t < token.Length; t++)
                {
                    if (inGroup < splits.Count && t == splits[inGroup])
                        inGroup++;

                    rawGroup[tokStart + t] = groupBase + inGroup;
                }
            }

            tokStart += token.Length + 1;
        }

        return rawGroup;
    }

    /// <summary>Per typeable cell, the usable hold on the first cell of its character (<c>TypingLine.heldCellsOf</c>); null when there are none.</summary>
    private static Dictionary<int, WordHold>? heldCellsOf(IReadOnlyList<TimedUnit> units, (int Token, int Offset)[] positions, bool[] typeable)
    {
        Dictionary<int, WordHold>? result = null;
        var usable = new IReadOnlyList<WordHold>?[units.Count];
        var claimed = new HashSet<(int, int)>();

        for (int i = 0; i < typeable.Length; i++)
        {
            var (m, offset) = positions[i];

            if (!typeable[i] || m < 0 || m >= units.Count || units[m].Objects.Holds.Count == 0)
                continue;

            foreach (var hold in usable[m] ??= HeldWord.UsableHolds(units[m], Typeability.IsCell))
            {
                if (hold.Char == offset && claimed.Add((m, offset)))
                    (result ??= new Dictionary<int, WordHold>())[i] = hold;
            }
        }

        return result;
    }

    /// <summary>Per cell, its whitespace token and offset in it; (-1, -1) for an inter-word space (<c>TypingLine.cellPositions</c>).</summary>
    private static (int Token, int Offset)[] cellPositions(string[] tokens, int rawLength, int cellCount, List<int>? defaultSources)
    {
        var raw = new (int Token, int Offset)[rawLength];
        Array.Fill(raw, (-1, -1));

        int pos = 0;

        for (int m = 0; m < tokens.Length; m++)
        {
            for (int t = 0; t < tokens[m].Length && pos + t < rawLength; t++)
                raw[pos + t] = (m, t);

            pos += tokens[m].Length + 1;
        }

        var result = new (int Token, int Offset)[cellCount];

        for (int i = 0; i < cellCount; i++)
        {
            int src = defaultSources?[i] ?? i;
            result[i] = src >= 0 && src < rawLength ? raw[src] : (-1, -1);
        }

        return result;
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
