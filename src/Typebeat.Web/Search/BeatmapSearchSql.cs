using System.Globalization;
using System.Text;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Search;

/// <summary>
/// Translates a parsed <see cref="BeatmapSearchQuery"/> into SQL WHERE fragments for the
/// /beatmapsets listing. Every produced clause is <c>AND (...)</c> ready to append after the
/// listing's base predicate, and every value is a Dapper parameter; column references come
/// only from the parser's whitelist, values are NEVER interpolated. Aliases assumed by the
/// generated SQL match <see cref="Pages.BeatmapsetCardSql"/>: <c>s</c> = beatmapsets,
/// <c>u</c> = owner. Per-difficulty filters (the stars/wpm/cpm/target/length numerics and the
/// lyrics: words) resolve through an EXISTS over <c>beatmaps</c>, so a set matches when ONE of its live
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
        // are persisted). Reconstruct it from the stored WPM, which since LyricPace v15 IS
        // cpm / LyricPace.CHARS_PER_WORD, so the reconstruction is exact and needs no counts.
        //
        // It used to read wpm * char_count / word_count, which was the OLD identity exactly (an old
        // WPM was real words per minute, so WPM times chars-per-word was the CPM). Against a v15
        // wpm that expression evaluates to cpm * charsPerWord / 5, wrong for every map whose average
        // word is not exactly 5 cells long, which is every map: it silently over-reports the long
        // worded ones and under-reports the short worded ones, so cpm: would hand back a different
        // set than it claims rather than fail loudly.
        //
        // The 5 is interpolated from the constant rather than typed out, which is safe (a compile
        // time double, never user input) and is the whole lesson of the bug above: an identity
        // written out twice is an identity waiting to drift.
        FilterField.Cpm => $"(b.wpm::double precision * {LyricPace.CHARS_PER_WORD.ToString(CultureInfo.InvariantCulture)})",
        // Stored outright (033_target_wpm.sql), so no derivation and no fallback: unlike the card
        // chip and the /play pill, a filter has to mean exactly what it says. A row the LyricPace
        // v18 backfill has not reached holds NULL here, and NULL fails every comparison, so such a
        // difficulty is simply invisible to target: rather than being matched on a stand-in.
        FilterField.TargetWpm => "b.target_wpm",
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
            // An exclusion (the game's title!=foo) is the negation of the same match. COALESCE
            // because a NULL column (a set without a title_unicode, say) makes the ILIKE NULL, and
            // NOT NULL would drop a set that plainly does not contain the word.
            sql.Append("\nAND ").Append(f.Exclude ? $"NOT COALESCE({clause}, false)" : clause);
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
                inner.Append(" AND ").Append(Comparison(n, Next));

            foreach (string word in lyricWords)
                inner.Append(" AND b.lyrics ILIKE @").Append(Next("%" + EscapeLike(word) + "%"));

            sql.Append("\nAND EXISTS (SELECT 1 FROM beatmaps b WHERE b.set_id = s.id AND b.filename IS NOT NULL")
               .Append(inner)
               .Append(')');
        }

        foreach (var n in query.NumericFilters.Where(n => n.Field == FilterField.Bpm))
            sql.Append("\nAND ").Append(Comparison(n, Next));

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

    /// <summary>The column expression a numeric field compares: <c>b.</c> per difficulty, and
    /// <c>s.bpm</c> for the one set-scoped numeric.</summary>
    private static string NumericExpr(FilterField field) => field == FilterField.Bpm ? "s.bpm" : BeatmapExpr(field);

    /// <summary>
    /// One numeric predicate, exposed so a parity test can evaluate the very SQL the listing runs
    /// against a table of its own (<c>tests/Typebeat.WireCompat/SearchOperatorParityTest.cs</c>
    /// runs it over a VALUES list aliased <c>b</c>). Parameters are named <paramref name="prefix"/>0,
    /// 1, and so on; the SQL carries no user text.
    /// </summary>
    public static (string Sql, IReadOnlyDictionary<string, object> Parameters) NumericPredicate(NumericFilter n, string prefix = "op")
    {
        var param = new Dictionary<string, object>();
        int seq = 0;

        string Next(object value)
        {
            string name = prefix + seq++;
            param[name] = value;
            return name;
        }

        return (Comparison(n, Next), param);
    }

    /// <summary>
    /// Greater and less compare the RAW stored value; equality and inequality compare what the
    /// player is shown (and what the game's song select compares), per field:
    /// <list type="bullet">
    /// <item>stars: the rating FLOORED to two decimals against the operand as typed, the game's
    /// <c>FloorToDecimalDigits(2)</c> with tolerance 0 (<c>BeatmapCarouselFilterMatching</c>), so
    /// <c>stars=4.07</c> is [4.07, 4.08) and <c>stars=4</c> is [4.00, 4.01). Postgres float8
    /// arithmetic is the same IEEE double arithmetic as <c>Math.Floor(v * 100) / 100</c>.</item>
    /// <item>wpm, cpm, target: the value rounded to a whole number, as every page prints it
    /// (<c>ToString("0")</c>, half away from zero, which is <c>round(numeric)</c>).</item>
    /// <item>bpm: within 0.5 either side, exclusive, the game's tolerance.</item>
    /// <item>length: within half the smallest unit the operand was written in, exclusive
    /// (<see cref="NumericFilter.Tolerance"/>), the game's rule.</item>
    /// </list>
    /// Inequality is the SQL negation of the equality clause, so a NULL stat (a target the backfill
    /// has not reached) stays invisible to both, as it is to every other comparison.
    /// </summary>
    private static string Comparison(NumericFilter n, Func<object, string> next)
    {
        string expr = NumericExpr(n.Field);

        return n.Op switch
        {
            Comparator.Gt => $"{expr} > @{next(n.Low)}",
            Comparator.Gte => $"{expr} >= @{next(n.Low)}",
            Comparator.Lt => $"{expr} < @{next(n.Low)}",
            Comparator.Lte => $"{expr} <= @{next(n.Low)}",
            Comparator.Range => $"{expr} BETWEEN @{next(n.Low)} AND @{next(n.High)}",
            Comparator.Neq => $"NOT ({Equality(expr, n, next)})",
            _ => Equality(expr, n, next),
        };
    }

    /// <summary>The game's tolerance for a BPM equality (<c>FilterQueryParser</c>, <c>bpm</c> case).</summary>
    public const double BPM_TOLERANCE = 0.5;

    private static string Equality(string expr, NumericFilter n, Func<object, string> next)
    {
        switch (n.Field)
        {
            case FilterField.Stars:
                return $"floor({expr} * 100) / 100 = @{next(n.Low)}";

            case FilterField.Wpm or FilterField.Cpm or FilterField.TargetWpm:
                return $"round(({expr})::numeric) = @{next(n.Low)}";

            case FilterField.Bpm:
            case FilterField.Length:
                double tolerance = n.Field == FilterField.Bpm ? BPM_TOLERANCE : n.Tolerance;
                return $"({expr} > @{next(n.Low - tolerance)} AND {expr} < @{next(n.Low + tolerance)})";

            default:
                return $"{expr} = @{next(n.Low)}";
        }
    }

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
            Comparator.Neq => "!= " + lo,
            _ => "= " + lo,
        };
    }
}
