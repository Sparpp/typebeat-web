using System.Globalization;
using System.Text;

namespace Typebeat.Web.Search;

/// <summary>
/// osu-web-style <c>key:value</c> filter operators for the /beatmapsets search box, plus a
/// bucket of leftover free text (the existing title/artist/creator/tags match). Pure and
/// unit-testable: <see cref="Parse"/> turns a raw query string into a whitelisted filter model,
/// and <see cref="BeatmapSearchSql"/> translates that model into parameterised SQL. The parser
/// NEVER emits column names or values into SQL itself; it only ever produces this typed model,
/// so an unknown key or a malformed value can only ever degrade to plain free text, never error.
/// </summary>
public sealed class BeatmapSearchQuery
{
    /// <summary>Un-keyed text (and any unparseable operator tokens), reassembled with single
    /// spaces. Fed to the page's existing full-text / ILIKE path unchanged.</summary>
    public string FreeText { get; }

    public IReadOnlyList<TextFilter> TextFilters { get; }
    public IReadOnlyList<NumericFilter> NumericFilters { get; }
    public IReadOnlyList<DateFilter> DateFilters { get; }
    public IReadOnlyList<BoolFilter> BoolFilters { get; }

    /// <summary>True when at least one recognised operator was parsed out of the query.</summary>
    public bool HasOperators =>
        TextFilters.Count + NumericFilters.Count + DateFilters.Count + BoolFilters.Count > 0;

    private BeatmapSearchQuery(string freeText,
        IReadOnlyList<TextFilter> text, IReadOnlyList<NumericFilter> numeric, IReadOnlyList<DateFilter> date,
        IReadOnlyList<BoolFilter> boolean)
    {
        FreeText = freeText;
        TextFilters = text;
        NumericFilters = numeric;
        DateFilters = date;
        BoolFilters = boolean;
    }

    public static BeatmapSearchQuery Parse(string? raw)
    {
        var text = new List<TextFilter>();
        var numeric = new List<NumericFilter>();
        var date = new List<DateFilter>();
        var boolean = new List<BoolFilter>();
        var freeTokens = new List<string>();

        foreach (string token in Tokenize(raw ?? string.Empty))
        {
            // Only a colon that follows a recognised key turns a token into an operator; a bare
            // "ratio:2" or "http://x" (unknown key) falls through to free text untouched.
            int colon = token.IndexOf(':');
            if (colon > 0 && Fields.Lookup(token[..colon]) is { } field)
            {
                string value = Unquote(token[(colon + 1)..]);
                if (value.Length > 0 && TryAddOperator(field, value, text, numeric, date, boolean))
                    continue;
            }

            freeTokens.Add(token);
        }

        return new BeatmapSearchQuery(string.Join(' ', freeTokens).Trim(), text, numeric, date, boolean);
    }

    private static bool TryAddOperator(FilterField field, string value,
        List<TextFilter> text, List<NumericFilter> numeric, List<DateFilter> date, List<BoolFilter> boolean)
    {
        switch (Fields.Kind(field))
        {
            case FieldKind.Text:
                text.Add(new TextFilter(field, value));
                return true;

            case FieldKind.Numeric:
                if (NumericFilter.TryParse(field, value) is { } nf)
                {
                    numeric.Add(nf);
                    return true;
                }
                return false;

            case FieldKind.Date:
                if (DateFilter.TryParse(value) is { } df)
                {
                    date.Add(df);
                    return true;
                }
                return false;

            case FieldKind.Bool:
                if (BoolFilter.TryParse(field, value) is { } bf)
                {
                    boolean.Add(bf);
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Splits on whitespace but keeps double-quoted spans intact (so <c>title:"two words"</c> and
    /// a free-text <c>"exact phrase"</c> survive as single tokens). Quote characters are preserved
    /// here and stripped later per-value, so free text keeps them for websearch phrase matching.
    /// </summary>
    private static IEnumerable<string> Tokenize(string input)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;

        foreach (char ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string Unquote(string value) => value.Replace("\"", string.Empty);
}

public enum FilterField
{
    Title, Artist, Creator, Source, Tag, Lyrics,
    Stars, Wpm, Cpm, Length, Bpm,
    Date,
    Explicit,
}

internal enum FieldKind { Text, Numeric, Date, Bool }

/// <summary>Keyword → field whitelist. The single source of truth for which keys are operators.</summary>
internal static class Fields
{
    private static readonly Dictionary<string, FilterField> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = FilterField.Title,
        ["artist"] = FilterField.Artist,
        ["creator"] = FilterField.Creator,
        ["mapper"] = FilterField.Creator,
        ["source"] = FilterField.Source,
        ["tag"] = FilterField.Tag,
        ["tags"] = FilterField.Tag,
        ["lyrics"] = FilterField.Lyrics,
        ["lyric"] = FilterField.Lyrics,
        ["star"] = FilterField.Stars,
        ["stars"] = FilterField.Stars,
        ["wpm"] = FilterField.Wpm,
        ["cpm"] = FilterField.Cpm,
        ["length"] = FilterField.Length,
        ["len"] = FilterField.Length,
        ["bpm"] = FilterField.Bpm,
        ["date"] = FilterField.Date,
        // Explicit-content marker; "nsfw" reads as the same thing to anyone arriving from osu.
        ["explicit"] = FilterField.Explicit,
        ["nsfw"] = FilterField.Explicit,
    };

    public static FilterField? Lookup(string key) =>
        aliases.TryGetValue(key, out var f) ? f : null;

    public static FieldKind Kind(FilterField field) => field switch
    {
        FilterField.Title or FilterField.Artist or FilterField.Creator
            or FilterField.Source or FilterField.Tag or FilterField.Lyrics => FieldKind.Text,
        FilterField.Date => FieldKind.Date,
        FilterField.Explicit => FieldKind.Bool,
        _ => FieldKind.Numeric,
    };
}

public enum Comparator { Eq, Lt, Lte, Gt, Gte, Range }

/// <summary>A substring (ILIKE) filter on a text column: <c>title:</c>, <c>artist:</c>, etc.</summary>
public sealed record TextFilter(FilterField Field, string Value);

/// <summary>
/// An equality filter on a boolean column (<c>explicit:</c>). Anything that is not a recognised
/// truth word degrades to free text like every other malformed operator value, so
/// <c>explicit:maybe</c> searches for the word rather than erroring.
/// </summary>
public sealed record BoolFilter(FilterField Field, bool Value)
{
    public static BoolFilter? TryParse(FilterField field, string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" or "on" => new BoolFilter(field, true),
        "false" or "no" or "n" or "0" or "off" => new BoolFilter(field, false),
        _ => null,
    };
}

/// <summary>
/// A comparator or inclusive range over a numeric column. For every op except
/// <see cref="Comparator.Range"/> only <see cref="Low"/> carries the operand.
/// </summary>
public sealed record NumericFilter(FilterField Field, Comparator Op, double Low, double High)
{
    /// <summary>
    /// Grammar: an optional comparator prefix (<c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>=</c>) then a number; OR a range <c>a-b</c> / <c>a..b</c>; a bare number means equality.
    /// Numbers may be plain (<c>4</c>, <c>4.5</c>) or <c>mm:ss</c> (<c>1:30</c> → 90); the latter
    /// is meaningful for length but harmless elsewhere. Returns null on anything unparseable.
    /// </summary>
    public static NumericFilter? TryParse(FilterField field, string value)
    {
        var (op, rest) = SplitComparator(value);

        if (op == Comparator.Eq)
        {
            // No explicit comparator: it may still be a range (a-b / a..b).
            if (TrySplitRange(rest, out string lo, out string hi))
            {
                if (TryParseNumber(lo, out double low) && TryParseNumber(hi, out double high))
                    return new NumericFilter(field, Comparator.Range, Math.Min(low, high), Math.Max(low, high));
                return null;
            }
        }

        return TryParseNumber(rest, out double n) ? new NumericFilter(field, op, n, n) : null;
    }

    private static bool TrySplitRange(string value, out string low, out string high)
    {
        low = high = string.Empty;

        int dotdot = value.IndexOf("..", StringComparison.Ordinal);
        if (dotdot > 0)
        {
            low = value[..dotdot];
            high = value[(dotdot + 2)..];
            return low.Length > 0 && high.Length > 0;
        }

        // A single hyphen separates a numeric range (values never contain a hyphen otherwise,
        // negatives are meaningless for these columns, and times use ':').
        int dash = value.IndexOf('-', 1);
        if (dash > 0 && dash < value.Length - 1)
        {
            low = value[..dash];
            high = value[(dash + 1)..];
            return true;
        }

        return false;
    }

    internal static (Comparator Op, string Remainder) SplitComparator(string value)
    {
        if (value.StartsWith(">=", StringComparison.Ordinal)) return (Comparator.Gte, value[2..]);
        if (value.StartsWith("<=", StringComparison.Ordinal)) return (Comparator.Lte, value[2..]);
        if (value.StartsWith(">", StringComparison.Ordinal)) return (Comparator.Gt, value[1..]);
        if (value.StartsWith("<", StringComparison.Ordinal)) return (Comparator.Lt, value[1..]);
        if (value.StartsWith("=", StringComparison.Ordinal)) return (Comparator.Eq, value[1..]);
        return (Comparator.Eq, value);
    }

    internal static bool TryParseNumber(string token, out double value)
    {
        value = 0;
        if (token.Length == 0)
            return false;

        int colon = token.IndexOf(':');
        if (colon > 0)
        {
            // mm:ss → seconds.
            if (int.TryParse(token[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int mm)
                && int.TryParse(token[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int ss)
                && ss < 60)
            {
                value = mm * 60 + ss;
                return true;
            }
            return false;
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// A half-open submission-date window [<see cref="MinInclusive"/>, <see cref="MaxExclusive"/>).
/// Either bound may be null (open-ended). Granularity comes from the value: a year, a month or a
/// day widens to the whole period, and comparators shift the appropriate edge of that period.
/// </summary>
public sealed record DateFilter(DateTime? MinInclusive, DateTime? MaxExclusive)
{
    public static DateFilter? TryParse(string value)
    {
        var (op, rest) = NumericFilter.SplitComparator(value);

        if (op == Comparator.Eq)
        {
            int dotdot = rest.IndexOf("..", StringComparison.Ordinal);
            if (dotdot > 0)
            {
                if (TryParsePeriod(rest[..dotdot], out var lo) && TryParsePeriod(rest[(dotdot + 2)..], out var hi))
                    return new DateFilter(lo.Start, hi.EndExclusive);
                return null;
            }
        }

        if (!TryParsePeriod(rest, out var p))
            return null;

        return op switch
        {
            Comparator.Gt => new DateFilter(p.EndExclusive, null),
            Comparator.Gte => new DateFilter(p.Start, null),
            Comparator.Lt => new DateFilter(null, p.Start),
            Comparator.Lte => new DateFilter(null, p.EndExclusive),
            _ => new DateFilter(p.Start, p.EndExclusive),
        };
    }

    private static bool TryParsePeriod(string token, out (DateTime Start, DateTime EndExclusive) period)
    {
        period = default;
        string[] parts = token.Split('-');

        try
        {
            switch (parts.Length)
            {
                case 1 when parts[0].Length == 4 && int.TryParse(parts[0], out int y):
                    period = (Utc(y, 1, 1), Utc(y + 1, 1, 1));
                    return true;

                case 2 when int.TryParse(parts[0], out int ym) && int.TryParse(parts[1], out int m) && m is >= 1 and <= 12:
                    period = (Utc(ym, m, 1), Utc(ym, m, 1).AddMonths(1));
                    return true;

                case 3 when int.TryParse(parts[0], out int yd) && int.TryParse(parts[1], out int md)
                         && int.TryParse(parts[2], out int d):
                    var day = Utc(yd, md, d);
                    period = (day, day.AddDays(1));
                    return true;

                default:
                    return false;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // e.g. 2024-13-40, month/day out of range.
            return false;
        }
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
}
