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
    // FREESTYLE_MARKER: a freestyle cell matches every key rather than this one.
    public static bool IsTypeable(char c)
        => c == ' '
           || (c >= 'a' && c <= 'z')
           || (c >= 'A' && c <= 'Z')
           || (c >= '0' && c <= '9');

    public static bool IsFreestyle(char c) => c == FREESTYLE_MARKER;

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
    /// mapped to ASCII, then every untypeable char REMOVED; whitespace runs collapse to a
    /// single space, trimmed. (LyricBeatmap.cs:90-149.)
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

            // Freestyle markers survive only for the callers that asked for them.
            if (!char.IsWhiteSpace(c) && !IsTypeable(c) && !(keepFreestyleMarkers && IsFreestyle(c)))
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
    /// Cells the player must type in <paramref name="text"/>: typeable chars plus freestyle
    /// slots. Identical to the historical typeable-only count for every text that has been
    /// through a default <see cref="Normalize"/> (which has no markers to count).
    /// (LyricBeatmap.cs:189-208.)
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
