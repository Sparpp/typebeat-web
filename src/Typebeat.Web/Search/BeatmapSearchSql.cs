using System.Globalization;
using System.Text;

namespace Typebeat.Web.Search;

/// <summary>
/// Translates a parsed <see cref="BeatmapSearchQuery"/> into SQL WHERE fragments for the
/// /beatmapsets listing. Every produced clause is <c>AND (...)</c> ready to append after the
/// listing's base predicate, and every value is a Dapper parameter; column references come
/// only from the parser's whitelist, values are NEVER interpolated. Aliases assumed by the
/// generated SQL match <see cref="Pages.BeatmapsetCardSql"/>: <c>s</c> = beatmapsets,
/// <c>u</c> = owner. Per-difficulty filters (the stars/wpm/cpm/length numerics and the lyrics:
/// words) resolve through an EXISTS over <c>beatmaps</c>, so a set matches when ONE of its live
/// difficulties satisfies all of them together; everything else (including language:) is
/// set-scoped.
/// </summary>
public static class BeatmapSearchSql
{
    /// <summary>Per-difficulty column expressions. cpm is derived; see <see cref="Build"/> notes.</summary>
    private static string BeatmapExpr(FilterField field) => field switch
    {
        FilterField.Stars => "b.difficulty_rating",
        FilterField.Wpm => "b.wpm",
        // No stored CPM column (LyricPace computes AverageCpm at ingest but only wpm/word/char
        // are persisted). Reconstruct it from the stored counts: cpm ≈ wpm × chars-per-word.
        // NULLIF guards word_count 0/NULL (row then never matches, which is correct).
        FilterField.Cpm => "(b.wpm * b.char_count::double precision / NULLIF(b.word_count, 0))",
        FilterField.Length => "b.total_length_s",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a per-difficulty numeric field"),
    };

    /// <summary>
    /// Builds the AND-clause fragment plus its parameters. Returns an empty fragment (and no
    /// params) when the query carries no operators. Parameter names are prefixed to stay clear of
    /// the listing's own (<c>@viewerId</c>, <c>@like</c>, <c>@q</c>, cursor params).
    /// </summary>
    public static (string Sql, IReadOnlyDictionary<string, object> Parameters) Build(
        BeatmapSearchQuery query, string prefix = "op")
    {
        var sql = new StringBuilder();
        var param = new Dictionary<string, object>();
        int seq = 0;

        string Next(object value)
        {
            string name = prefix + seq++;
            param[name] = value;
            return name;
        }

        // Song language is a set property and a closed vocabulary, so it is the one text filter
        // that compares for EQUALITY rather than ILIKE: the parser already folded the user's
        // input onto a canonical name (BeatmapLanguages), so there is nothing left to match
        // loosely, and equality is what the partial index in 019_language.sql serves. Sets whose
        // language is still unset hold '', which no canonical name equals, so they are simply
        // invisible to this filter.
        foreach (var f in query.TextFilters.Where(f => f.Field == FilterField.Language))
            sql.Append("\nAND s.language = @").Append(Next(f.Value));

        // Lyrics text lives per difficulty (beatmaps.lyrics), so lyrics: joins the per-difficulty
        // EXISTS below rather than the set-scoped clauses here.
        foreach (var f in query.TextFilters.Where(f => f.Field is not (FilterField.Lyrics or FilterField.Language)))
        {
            string p = Next("%" + EscapeLike(f.Value) + "%");
            string clause = f.Field switch
            {
                FilterField.Title => $"(s.title ILIKE @{p} OR s.title_unicode ILIKE @{p})",
                FilterField.Artist => $"(s.artist ILIKE @{p} OR s.artist_unicode ILIKE @{p})",
                FilterField.Creator => $"u.username::text ILIKE @{p}",
                FilterField.Source => $"s.source ILIKE @{p}",
                FilterField.Tag => $"s.tags ILIKE @{p}",
                _ => throw new ArgumentOutOfRangeException(nameof(query), f.Field, "not a text field"),
            };
            sql.Append("\nAND ").Append(clause);
        }

        // Per-difficulty predicates collapse into one EXISTS so they apply to the SAME difficulty:
        // the numeric stats, and the lyrics haystack. A lyrics: value is split on whitespace and
        // EVERY word must appear somewhere in that difficulty's lyrics (one ILIKE per word, ANDed),
        // so lyrics:"neon night" finds maps singing both words in any order.
        var perDiff = query.NumericFilters.Where(n => n.Field != FilterField.Bpm).ToList();
        var lyricWords = query.TextFilters
            .Where(f => f.Field == FilterField.Lyrics)
            .SelectMany(f => f.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        if (perDiff.Count > 0 || lyricWords.Count > 0)
        {
            var inner = new StringBuilder();
            foreach (var n in perDiff)
                inner.Append(" AND ").Append(Comparison(BeatmapExpr(n.Field), n, Next));

            foreach (string word in lyricWords)
                inner.Append(" AND b.lyrics ILIKE @").Append(Next("%" + EscapeLike(word) + "%"));

            sql.Append("\nAND EXISTS (SELECT 1 FROM beatmaps b WHERE b.set_id = s.id AND b.filename IS NOT NULL")
               .Append(inner)
               .Append(')');
        }

        foreach (var n in query.NumericFilters.Where(n => n.Field == FilterField.Bpm))
            sql.Append("\nAND ").Append(Comparison("s.bpm", n, Next));

        foreach (var b in query.BoolFilters)
        {
            string column = b.Field switch
            {
                FilterField.Explicit => "s.explicit",
                _ => throw new ArgumentOutOfRangeException(nameof(query), b.Field, "not a boolean field"),
            };
            sql.Append("\nAND ").Append(column).Append(" = @").Append(Next(b.Value));
        }

        foreach (var d in query.DateFilters)
        {
            if (d.MinInclusive is { } min)
                sql.Append("\nAND s.submitted_at >= @").Append(Next(min));
            if (d.MaxExclusive is { } max)
                sql.Append("\nAND s.submitted_at < @").Append(Next(max));
        }

        return (sql.ToString(), param);
    }

    private static string Comparison(string expr, NumericFilter n, Func<object, string> next) => n.Op switch
    {
        Comparator.Gt => $"{expr} > @{next(n.Low)}",
        Comparator.Gte => $"{expr} >= @{next(n.Low)}",
        Comparator.Lt => $"{expr} < @{next(n.Low)}",
        Comparator.Lte => $"{expr} <= @{next(n.Low)}",
        Comparator.Range => $"{expr} BETWEEN @{next(n.Low)} AND @{next(n.High)}",
        _ => $"{expr} = @{next(n.Low)}",
    };

    private static string EscapeLike(string raw)
        => raw.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Human-readable echo of a numeric filter, for the guide / debugging.</summary>
    internal static string Describe(NumericFilter n)
    {
        string lo = n.Low.ToString(CultureInfo.InvariantCulture);
        return n.Op switch
        {
            Comparator.Gt => "> " + lo,
            Comparator.Gte => ">= " + lo,
            Comparator.Lt => "< " + lo,
            Comparator.Lte => "<= " + lo,
            Comparator.Range => lo + "–" + n.High.ToString(CultureInfo.InvariantCulture),
            _ => "= " + lo,
        };
    }
}
