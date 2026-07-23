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
    // LyricBeatmap.cs:39-43: accepted set is a subset of what the client's KeyCharMap can
    // produce (ASCII letters/digits/space); everything else auto-skips.
    public static bool IsTypeable(char c)
        => c == ' '
           || (c >= 'a' && c <= 'z')
           || (c >= 'A' && c <= 'Z')
           || (c >= '0' && c <= '9');

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
    /// </summary>
    public static string Normalize(string raw)
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

            if (!char.IsWhiteSpace(c) && !IsTypeable(c))
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

    public static int TypeableCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0;

        foreach (char c in text)
        {
            if (IsTypeable(c))
                count++;
        }

        return count;
    }
}
