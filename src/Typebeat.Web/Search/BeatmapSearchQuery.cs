using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Typebeat.Web.Packages;

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
            // A token is an operator only when it opens with a recognised KEY followed directly by
            // one of the operator spellings; a bare "ratio:2", "ratio>2" or "http://x" (unknown
            // key) falls through to free text untouched, and so does a token opening with a quote.
            if (TrySplitOperator(token, out var field, out var op, out string value)
                && TryAddOperator(field, op, value, text, numeric, date, boolean))
                continue;

            freeTokens.Add(token);
        }

        return new BeatmapSearchQuery(string.Join(' ', freeTokens).Trim(), text, numeric, date, boolean);
    }

    /// <summary>
    /// Splits <c>key</c>, operator and value, accepting every spelling the game's song select
    /// accepts (<c>FilterQueryParser</c>): <c>:</c> and <c>=</c> (equal), <c>!=</c> and <c>!:</c>
    /// (not equal), <c>&lt;</c>, <c>&lt;=</c>, <c>&lt;:</c>, <c>&gt;</c>, <c>&gt;=</c>,
    /// <c>&gt;:</c>. The key is the leading run of ASCII letters and must be on the
    /// <see cref="Fields"/> whitelist. The bare colon keeps its osu-web meaning (a comparator may
    /// PREFIX the value: <c>star:&gt;4</c>), so every saved search and shared URL reads as before.
    /// </summary>
    private static bool TrySplitOperator(string token, out FilterField field, out SearchOperator op, out string value)
    {
        field = default;
        op = default;
        value = string.Empty;

        int keyEnd = 0;
        while (keyEnd < token.Length && char.IsAsciiLetter(token[keyEnd]))
            keyEnd++;

        if (keyEnd == 0 || keyEnd == token.Length || Fields.Lookup(token[..keyEnd]) is not { } found)
            return false;

        char c = token[keyEnd];
        char next = keyEnd + 1 < token.Length ? token[keyEnd + 1] : '\0';
        bool doubled = next is '=' or ':';
        int width = 1;

        switch (c)
        {
            case ':':
                op = SearchOperator.Colon;
                break;

            case '=':
                op = SearchOperator.Eq;
                break;

            case '!' when doubled:
                op = SearchOperator.Neq;
                width = 2;
                break;

            case '<':
                op = doubled ? SearchOperator.Lte : SearchOperator.Lt;
                width = doubled ? 2 : 1;
                break;

            case '>':
                op = doubled ? SearchOperator.Gte : SearchOperator.Gt;
                width = doubled ? 2 : 1;
                break;

            default:
                return false;
        }

        field = found;
        value = Unquote(token[(keyEnd + width)..]);
        return value.Length > 0;
    }

    /// <summary>A value that itself opens with an operator character (<c>stars&gt;&gt;4</c>,
    /// <c>stars=&gt;4</c>) belongs to no spelling the game accepts either, so it stays free text.</summary>
    private static bool StartsWithOperator(string value) => value[0] is '<' or '>' or '=' or '!' or ':';

    private static bool TryAddOperator(FilterField field, SearchOperator op, string value,
        List<TextFilter> text, List<NumericFilter> numeric, List<DateFilter> date, List<BoolFilter> boolean)
    {
        if (op == SearchOperator.Colon)
            return TryAddOperator(field, value, text, numeric, date, boolean);

        switch (Fields.Kind(field))
        {
            case FieldKind.Text:
                // The game's text keys take = and != only (TryUpdateCriteriaText). A lyrics:
                // exclusion has no per-difficulty meaning worth guessing at, so it stays free text.
                if (op == SearchOperator.Eq)
                    return TryAddOperator(field, value, text, numeric, date, boolean);
                if (op == SearchOperator.Neq && field != FilterField.Lyrics)
                {
                    text.Add(new TextFilter(field, value, Exclude: true));
                    return true;
                }
                return false;

            case FieldKind.Language:
                return op == SearchOperator.Eq && TryAddOperator(field, value, text, numeric, date, boolean);

            case FieldKind.Numeric:
            {
                if (StartsWithOperator(value))
                    return false;

                // = is the colon's bare-value form exactly, so it keeps the a-b range too.
                if (op == SearchOperator.Eq)
                    return TryAddOperator(field, value, text, numeric, date, boolean);

                if (!NumericFilter.TryParseOperand(field, value, out double n, out double tolerance))
                    return false;

                var comparator = op switch
                {
                    SearchOperator.Neq => Comparator.Neq,
                    SearchOperator.Lt => Comparator.Lt,
                    SearchOperator.Lte => Comparator.Lte,
                    SearchOperator.Gt => Comparator.Gt,
                    _ => Comparator.Gte,
                };

                numeric.Add(new NumericFilter(field, comparator, n, n) { Tolerance = tolerance });
                return true;
            }

            case FieldKind.Date:
            {
                // Re-expressed as the colon form's comparator prefix, which DateFilter already
                // reads; a date inequality has no period meaning and stays free text.
                if (StartsWithOperator(value) || op == SearchOperator.Neq)
                    return false;

                string prefix = op switch
                {
                    SearchOperator.Lt => "<",
                    SearchOperator.Lte => "<=",
                    SearchOperator.Gt => ">",
                    SearchOperator.Gte => ">=",
                    _ => string.Empty,
                };

                return TryAddOperator(field, prefix + value, text, numeric, date, boolean);
            }

            case FieldKind.Bool:
                if (BoolFilter.TryParse(field, value) is not { } bf)
                    return false;
                if (op == SearchOperator.Eq)
                {
                    boolean.Add(bf);
                    return true;
                }
                if (op == SearchOperator.Neq)
                {
                    boolean.Add(bf with { Value = !bf.Value });
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private static bool TryAddOperator(FilterField field, string value,
        List<TextFilter> text, List<NumericFilter> numeric, List<DateFilter> date, List<BoolFilter> boolean)
    {
        switch (Fields.Kind(field))
        {
            case FieldKind.Text:
                text.Add(new TextFilter(field, value));
                return true;

            case FieldKind.Language:
                // The only closed-vocabulary text field: the value is folded onto a canonical
                // language name here (so "lang:JP" and "language:Japanese" are the same filter,
                // and the SQL builder receives something it can compare for equality), and an
                // unrecognised one degrades to free text like every other malformed operator
                // value, so "lang:klingon" searches for the word instead of matching nothing.
                string canonical = BeatmapLanguages.Normalize(value);

                if (canonical.Length == 0)
                    return false;

                text.Add(new TextFilter(field, canonical));
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
    Title, Artist, Creator, Source, Tag, Lyrics, Language,
    Stars, Wpm, Cpm, TargetWpm, Length, Bpm,
    Date,
    Explicit,
}

internal enum FieldKind { Text, Numeric, Date, Bool, Language }

/// <summary>The operator spelling between a key and its value. <see cref="Colon"/> is the
/// osu-web form whose value may carry its own comparator prefix; the rest are the game's.</summary>
internal enum SearchOperator { Colon, Eq, Neq, Lt, Lte, Gt, Gte }

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
        // Song language (019_language.sql). "lang" is the short form everyone actually types.
        ["language"] = FilterField.Language,
        ["lang"] = FilterField.Language,
        ["star"] = FilterField.Stars,
        ["stars"] = FilterField.Stars,
        // The game's song select reads "sr" (star rating) too, so the same query works in both.
        ["sr"] = FilterField.Stars,
        ["wpm"] = FilterField.Wpm,
        ["cpm"] = FilterField.Cpm,
        // The map's TARGET pace (033_target_wpm.sql): the average WPM across the fastest fifth of
        // its lyric lines of at least three words, the figure the cards and the set page headline.
        // wpm: and cpm: are as they
        // were, on the LyricPace v15 precedent: a saved search or a shared URL carrying either keeps
        // working and keeps meaning the stored average.
        ["target"] = FilterField.TargetWpm,
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
        FilterField.Language => FieldKind.Language,
        FilterField.Date => FieldKind.Date,
        FilterField.Explicit => FieldKind.Bool,
        _ => FieldKind.Numeric,
    };
}

public enum Comparator { Eq, Lt, Lte, Gt, Gte, Range, Neq }

/// <summary>
/// A substring (ILIKE) filter on a text column: <c>title:</c>, <c>artist:</c>, etc. The one
/// exception is <see cref="FilterField.Language"/>, whose <see cref="Value"/> is already folded to
/// a canonical language name at parse time and is compared for EQUALITY by the SQL builder.
/// <see cref="Exclude"/> is the game's <c>title!=foo</c>: the set must NOT match the substring.
/// </summary>
public sealed record TextFilter(FilterField Field, string Value, bool Exclude = false);

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
    /// Half-width, in seconds, of the window a LENGTH equality or inequality matches: the game's
    /// rule (<c>FilterQueryParser.tryUpdateLengthRange</c>), half the smallest unit the value was
    /// written in, so <c>length=90</c> and <c>length=1:30</c> mean 89.5 to 90.5 exclusive and
    /// <c>length=2m</c> means 90 to 150. Zero for every other field.
    /// </summary>
    public double Tolerance { get; init; }

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
                if (TryParseOperand(field, lo, out double low, out _) && TryParseOperand(field, hi, out double high, out _))
                    return new NumericFilter(field, Comparator.Range, Math.Min(low, high), Math.Max(low, high));
                return null;
            }
        }

        return TryParseOperand(field, rest, out double n, out double tolerance)
            ? new NumericFilter(field, op, n, n) { Tolerance = tolerance }
            : null;
    }

    /// <summary>
    /// One operand. Length additionally reads the game's unit forms (<c>2m</c>, <c>1m30s</c>,
    /// <c>1h</c>, <c>1:02:03</c>) after the plain seconds and <c>mm:ss</c> it always read, and
    /// reports the game's equality tolerance for what was written.
    /// </summary>
    internal static bool TryParseOperand(FilterField field, string token, out double value, out double tolerance)
    {
        tolerance = 0;

        if (field != FilterField.Length)
            return TryParseNumber(token, out value);

        // Plain seconds and mm:ss both end in a seconds unit: half a second, as the game.
        tolerance = 0.5;
        return TryParseNumber(token, out value) || TryParseLengthUnits(token, out value, out tolerance);
    }

    private static readonly Regex clockLength = new(@"^((?<hours>\d+):)?(?<minutes>\d+):(?<seconds>\d+)$", RegexOptions.CultureInvariant);

    private static readonly Regex unitLength = new(
        @"^((?<hours>\d+(\.\d+)?)h)?((?<minutes>\d+(\.\d+)?)m)?((?<seconds>\d+(\.\d+)?)s)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A port of the game's <c>tryUpdateLengthRange</c> value grammar: every unit but the largest
    /// written must be under 60, only the smallest may carry a fraction, and the tolerance is half
    /// the smallest unit written.
    /// </summary>
    private static bool TryParseLengthUnits(string token, out double seconds, out double tolerance)
    {
        seconds = 0;
        tolerance = 0;

        var match = clockLength.Match(token);
        if (!match.Success)
            match = unitLength.Match(token);
        if (!match.Success)
            return false;

        // Smallest unit first, as the game walks them.
        var parts = new List<(string Text, double Scale)>();
        if (match.Groups["seconds"].Success) parts.Add((match.Groups["seconds"].Value, 1));
        if (match.Groups["minutes"].Success) parts.Add((match.Groups["minutes"].Value, 60));
        if (match.Groups["hours"].Success) parts.Add((match.Groups["hours"].Value, 3600));

        if (parts.Count == 0)
            return false;

        for (int i = 0; i < parts.Count; i++)
        {
            if (!double.TryParse(parts[i].Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount))
                return false;
            if (i != parts.Count - 1 && amount >= 60)
                return false;
            if (i != 0 && parts[i].Text.Contains('.'))
                return false;

            seconds += amount * parts[i].Scale;
        }

        tolerance = parts[0].Scale / 2;
        return true;
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
