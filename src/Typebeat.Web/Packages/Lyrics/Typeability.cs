using System.Globalization;
using System.Text;

namespace Typebeat.Web.Packages.Lyrics;

/// <summary>
/// Server-side port of the game's text-normalization / typeability authority
/// (typebeat-osu typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricBeatmap.cs:33-166, class
/// Typeability). The upload pipeline must count exactly the cells the client will make the
/// player type, so this logic is copied verbatim; any change there must be mirrored here.
/// </summary>
public static class Typeability
{
    /// <summary>
    /// Authoring marker for a FREESTYLE character: a cell the player may satisfy with ANY key,
    /// whose typed char is then displayed for the rest of the play. Deliberately outside
    /// <see cref="IsTypeable"/>, which is what keeps it invisible to every legacy path
    /// (<see cref="Normalize"/> strips it unless the caller explicitly opts in).
    /// (LyricBeatmap.cs:35-42.)
    /// </summary>
    public const char FREESTYLE_MARKER = '&';

    // LyricBeatmap.cs:39-43: accepted set is a subset of what the client's KeyCharMap can
    // produce (ASCII letters/digits/space); everything else auto-skips. Deliberately excludes
    // FREESTYLE_MARKER: a freestyle cell matches every key rather than this one. Deliberately
    // excludes PUNCTUATION too: a mark is only ever typed under the client's Literate mod, so it
    // must not count as a plain typeable char for the difficulty model, the interpolation weights
    // or the pace statistics.
    public static bool IsTypeable(char c)
        => c == ' '
           || (c >= 'a' && c <= 'z')
           || (c >= 'A' && c <= 'Z')
           || (c >= '0' && c <= '9');

    public static bool IsFreestyle(char c) => c == FREESTYLE_MARKER;

    /// <summary>
    /// The punctuation type!beat supports inside an authored lyric line, defined ONCE here:
    /// comma, period, apostrophe, hyphen, question mark, exclamation mark, semicolon, colon,
    /// round brackets, square brackets, straight double quote, dollar sign, percent sign, caret,
    /// asterisk, angle brackets, forward slash.
    ///
    /// <para>A map stores the AUTHOR'S form: punctuated and case-sensitive. What the player types
    /// (and sees) is derived from it: verbatim under the client's LITERATE mod, and through
    /// <see cref="ToDefaultStream"/> otherwise. (LyricBeatmap.cs, Typeability.PUNCTUATION.)</para>
    ///
    /// <para>Widening this set cannot move a stored per-map STAT: every mark but
    /// <see cref="WORD_BREAK"/> is deleted by <see cref="DefaultChar"/>, so a char that used to be
    /// dropped by <see cref="Normalize"/> as unsupported is now kept in the author's line and
    /// dropped one step later, leaving the DEFAULT stream (which every stat is measured on)
    /// byte-identical.</para>
    /// </summary>
    public const string PUNCTUATION = ",.'-?!;:()[]\"$%^*<>/";

    /// <summary>
    /// The one supported mark that reads as a WORD BREAK rather than as decoration: without
    /// Literate, "bad-cat" is typed "bad cat", not "badcat".
    /// </summary>
    public const char WORD_BREAK = '-';

    /// <summary>A mark from the supported <see cref="PUNCTUATION"/> set.</summary>
    public static bool IsPunctuation(char c) => PUNCTUATION.IndexOf(c) >= 0;

    /// <summary>
    /// A character that occupies a TYPEABLE CELL: a normal typeable char, or a freestyle slot.
    /// This is what the cell counting (<see cref="LyricPace"/>) and the text statistics count;
    /// <see cref="IsTypeable"/> stays the narrower "this exact glyph must be typed" predicate.
    /// (LyricBeatmap.cs:66-72.)
    /// </summary>
    public static bool IsCell(char c) => IsTypeable(c) || IsFreestyle(c);

    /// <summary>
    /// Removes bracketed backing-vocal spans, "(...)" and "[...]", the player never types.
    /// Unclosed brackets strip to end of string. Call BEFORE <see cref="Normalize"/>.
    /// (LyricBeatmap.cs:53-81.)
    /// </summary>
    public static string StripBackingVocals(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        int depth = 0;

        foreach (char c in raw)
        {
            if (c == '(' || c == '[')
            {
                depth++;
                continue;
            }

            if (c == ')' || c == ']')
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (depth == 0)
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Latin diacritics stripped (FormD, combining marks dropped), curly quotes/dashes/NBSP
    /// mapped to ASCII, then every char that is neither typeable nor one of the supported
    /// <see cref="PUNCTUATION"/> marks REMOVED; whitespace runs collapse to a single space,
    /// trimmed. (LyricBeatmap.cs, Typeability.Normalize.)
    ///
    /// <para>The result is the AUTHOR'S form of the line: original case, supported punctuation
    /// intact. It is what the blob stores and what <c>ParsedDifficulty.LyricsText</c> carries. It
    /// is NOT what the player types by default; that is <see cref="ToDefaultStream"/>, which every
    /// per-map STAT is measured on so the numbers stay comparable.</para>
    ///
    /// <para><paramref name="keepFreestyleMarkers"/> additionally preserves
    /// <see cref="FREESTYLE_MARKER"/>s, which are otherwise stripped like any other untypeable
    /// punctuation. The only server-side caller that opts in is the decoder of a line the map
    /// explicitly flagged (<c>"freestyle": true</c>), so an ampersand that merely occurs in a
    /// song's lyrics ("R&amp;B") still disappears exactly as it always has.</para>
    /// </summary>
    public static string Normalize(string raw, bool keepFreestyleMarkers = false)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        try
        {
            raw = raw.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Invalid Unicode (broken surrogates), carry on undecomposed.
        }

        var sb = new StringBuilder(raw.Length);
        bool pendingSpace = false;
        bool wroteAny = false;

        foreach (char original in raw)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(original) == UnicodeCategory.NonSpacingMark)
                continue;

            char c = original switch
            {
                '‘' or '’' or '‚' or '′' => '\'', // ‘ ’ ‚ ′
                '“' or '”' or '„' or '″' => '"',  // “ ” „ ″
                '–' or '—' or '―' or '−' => '-',  // – — ― −
                ' ' or ' ' or ' ' => ' ',              // NBSP, figure space, narrow NBSP
                _ => original
            };

            // Supported punctuation survives into the stored line (the author's form); freestyle
            // markers survive only for the callers that asked for them.
            if (!char.IsWhiteSpace(c) && !IsTypeable(c) && !IsPunctuation(c) && !(keepFreestyleMarkers && IsFreestyle(c)))
                continue;

            if (char.IsWhiteSpace(c))
            {
                if (wroteAny)
                    pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
            wroteAny = true;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The DEFAULT (no-Literate) typed char for one authored char, or null when the default
    /// stream deletes it: <see cref="WORD_BREAK"/> becomes a SPACE ("bad-cat" is typed
    /// "bad cat"), every other supported mark disappears, everything else folds to lower case.
    /// (LyricBeatmap.cs, Typeability.DefaultChar.)
    /// </summary>
    public static char? DefaultChar(char c)
    {
        if (c == WORD_BREAK)
            return ' ';

        if (IsPunctuation(c))
            return null;

        return char.ToLowerInvariant(c);
    }

    /// <summary>
    /// THE derivation, mirrored char for char from the client (LyricBeatmap.cs,
    /// Typeability.ProjectDefault) and, in turn, from wwwroot/js/typebeat-core.js: projects an
    /// authored line onto the DEFAULT typed stream.
    ///
    /// <para>Spaces are handled a RUN at a time (consecutive space-producing chars, authored
    /// spaces and hyphens alike, with deleted marks skipped over). A run with NO hyphen in it is
    /// emitted verbatim, space for space, which is what makes the projection exactly
    /// <c>ToLowerInvariant</c> for every hyphen-free, mark-free line, i.e. every line of every blob
    /// written before punctuation existed. A run that DOES contain a hyphen collapses to one space
    /// ("a - b" is "a b"), and to none at either end of the line ("-a-" is "a").</para>
    /// </summary>
    public static void ProjectDefault(string raw, StringBuilder text)
    {
        if (string.IsNullOrEmpty(raw))
            return;

        bool wroteAny = false;
        int i = 0;

        while (i < raw.Length)
        {
            if (DefaultChar(raw[i]) is not char c)
            {
                i++;
                continue;
            }

            if (c != ' ')
            {
                text.Append(c);
                wroteAny = true;
                i++;
                continue;
            }

            bool hasBreak = false;
            int end = i;

            while (end < raw.Length)
            {
                if (DefaultChar(raw[end]) is not char d)
                {
                    end++; // a deleted mark inside the run does not end it
                    continue;
                }

                if (d != ' ')
                    break;

                if (raw[end] == WORD_BREAK)
                    hasBreak = true;

                end++;
            }

            if (!hasBreak)
            {
                for (int k = i; k < end; k++)
                {
                    if (DefaultChar(raw[k]) is not ' ')
                        continue;

                    text.Append(' ');
                    wroteAny = true;
                }
            }
            else if (wroteAny && end < raw.Length)
            {
                text.Append(' ');
            }

            i = end;
        }
    }

    /// <summary>
    /// The DEFAULT (no-Literate) typed stream of an authored line: lower-cased, hyphens turned
    /// into word breaks, every other supported mark deleted. "The bad-cat sat." becomes
    /// "the bad cat sat". Every stored per-map STAT (word count, char count, wpm, pace) is measured
    /// on this, never on the authored line, so the numbers describe the play everyone shares and
    /// stay comparable with every figure computed before punctuation existed.
    /// </summary>
    public static string ToDefaultStream(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        ProjectDefault(raw, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Cells the player must type in <paramref name="text"/>: typeable chars plus freestyle
    /// slots. Punctuation is deliberately NOT counted (it is only a cell under the Literate mod).
    /// Identical to the historical typeable-only count for every text that has been
    /// through a default <see cref="Normalize"/> (which has no markers to count).
    /// (LyricBeatmap.cs, Typeability.TypeableCount.)
    /// </summary>
    public static int TypeableCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0;

        foreach (char c in text)
        {
            if (IsCell(c))
                count++;
        }

        return count;
    }
}
