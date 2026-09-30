using System.Globalization;
using System.Text.RegularExpressions;

namespace Typebeat.Web.Search;

/// <summary>
/// The ONE free-text rule for beatmapset search (backlog 351): whatever is left of the search box
/// once <see cref="BeatmapSearchQuery"/> has pulled the <c>key:value</c> operators out.
///
/// <para>A query of <see cref="MinFuzzyLength"/> or more characters matches LITERALLY through three
/// layers, each a superset of the one before it, and ranks by the first layer that caught the row;
/// a fourth, typo layer is a FALLBACK the caller applies only when nothing matches literally:</para>
/// <list type="number">
/// <item><b>Exact</b> (tier 0): every word is a whole lexeme of the weighted <c>beatmapsets.search</c>
/// vector, every quoted phrase matches as a phrase.</item>
/// <item><b>Prefix</b> (tier 1): the same, but every word of two or more characters may also be the
/// START of a lexeme (<c>drac</c> finds Dracula and Dragonfly). Every word, not only the last: the
/// listing is submitted, not search-as-you-type, so an abbreviated earlier word is as likely as an
/// unfinished last one, and the AND across the words keeps it narrow. One-character words stay
/// whole-lexeme only, since <c>a:*</c> would match most of the catalogue.</item>
/// <item><b>Substring</b> (tier 2): the text as a case-insensitive fragment of title, artist (both
/// scripts), tags, source or mapper name, so mid-word fragments (<c>cula</c>) and original-script
/// titles the <c>simple</c> parser splits oddly still match.</item>
/// <item><b>Typo</b> (tier 3, <see cref="FreeTextPredicate.TypoWhere"/>, fallback only): every word
/// of three or more characters has a <c>pg_trgm</c> <c>word_similarity</c> above
/// <see cref="TrigramThreshold"/> to the title or the artist (<c>dracla</c> finds Dracula).</item>
/// </list>
///
/// <para>WHY A FALLBACK and not a fourth union layer: no threshold separates a typo from a word that
/// merely CONTAINS a title word. <c>dracla</c> against Dracula scores 0.57, while the scoping tag
/// <c>operatorset</c> against "Star Operator 1" scores 0.67 and <c>operator alpha</c> against
/// "Operator Bravo Ballad" 0.60, so a union would flood ordinary searches with look-alikes. As a
/// fallback it only answers a query that would otherwise show the empty state, which is exactly
/// when a typo is the likely story. It is per WORD for the same reason: whole-query similarity lets
/// one strong word carry a mistyped or unrelated second one.</para>
///
/// <para>A query shorter than <see cref="MinFuzzyLength"/> keeps the substring match alone (what it
/// has always done: <c>dr</c> is too short for a meaningful lexeme or trigram) and carries no
/// tier.</para>
///
/// <para>SAFETY: the SQL this emits is a fixed shape; user text only ever reaches it as parameter
/// VALUES. The tsquery is composed IN SQL from per-word parameters with <c>&amp;&amp;</c>, and a
/// word is a run of letters, digits and combining marks only, so no tsquery operator
/// (<c>&amp; | ! ( ) : * &lt; &gt; '</c>) can ever reach <c>to_tsquery</c>'s own grammar.</para>
/// </summary>
public static class FreeTextSearch
{
    /// <summary>Below this many characters the text is matched as a substring only, and a word
    /// shorter than this takes no part in the typo layer (too few trigrams to mean anything).</summary>
    public const int MinFuzzyLength = 3;

    /// <summary><c>word_similarity</c> must be strictly ABOVE this for the typo layer. 0.4 lets a
    /// dropped or swapped letter through (<c>dracla</c> against Dracula is 0.57, <c>drakula</c>
    /// 0.45) while a four-letter prefix of an unrelated title sits on it (<c>drac</c> against
    /// "Fresh Drop" is exactly 0.4, and is refused).</summary>
    public const double TrigramThreshold = 0.4;

    public const int TierExact = 0;
    public const int TierPrefix = 1;
    public const int TierSubstring = 2;
    public const int TierTrigram = 3;

    private const string param_prefix = "ft_";

    private static readonly Regex lexeme_run = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The predicate for <paramref name="freeText"/>, or null when there is no free text.
    /// Aliases in play: <c>s</c> = beatmapsets, <c>u</c> = the owning user (the listing's
    /// <see cref="Pages.BeatmapsetCardSql"/> shape).
    /// </summary>
    public static FreeTextPredicate? Build(string? freeText)
    {
        string text = (freeText ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        var parameters = new Dictionary<string, object>();

        if (text.Length < MinFuzzyLength)
        {
            parameters[param_prefix + "like"] = "%" + EscapeLike(text) + "%";
            return new FreeTextPredicate(Substring(), null, null, parameters);
        }

        var (words, phrases) = Terms(text);

        // Quotes are query syntax, not title text: the fragment reads the words the visitor typed
        // with the quote marks taken out.
        string bare = Regex.Replace(text.Replace("\"", " "), @"\s+", " ").Trim();
        parameters[param_prefix + "like"] = "%" + EscapeLike(bare) + "%";

        string? exact = null;
        string? prefix = null;

        if (words.Count + phrases.Count > 0)
        {
            var exactParts = new List<string>();
            var prefixParts = new List<string>();

            for (int i = 0; i < words.Count; i++)
            {
                string name = $"{param_prefix}w{i}";
                parameters[name] = words[i];

                // A single run is one lexeme to the 'simple' parser, so plainto_tsquery is its
                // whole-word match and to_tsquery(run || ':*') its prefix match.
                exactParts.Add($"plainto_tsquery('simple', @{name})");
                prefixParts.Add(words[i].Length >= 2
                    ? $"to_tsquery('simple', @{name} || ':*')"
                    : $"plainto_tsquery('simple', @{name})");
            }

            for (int i = 0; i < phrases.Count; i++)
            {
                string name = $"{param_prefix}p{i}";
                parameters[name] = phrases[i];

                // A phrase stays a phrase in both layers: quoting is the visitor asking for exactly it.
                exactParts.Add($"phraseto_tsquery('simple', @{name})");
                prefixParts.Add($"phraseto_tsquery('simple', @{name})");
            }

            exact = "s.search @@ (" + string.Join(" && ", exactParts) + ")";
            prefix = "s.search @@ (" + string.Join(" && ", prefixParts) + ")";
        }

        // The prefix query matches everything the exact one does (a lexeme is a prefix of itself),
        // so the filter needs only the widest FTS layer.
        string where = prefix is null
            ? Substring()
            : $"({prefix}\n OR {Substring()})";

        // Text with no lexeme at all (punctuation only) has the substring layer alone, so no tier.
        string? tier = exact is null
            ? null
            : $"(CASE WHEN {exact} THEN {TierExact} WHEN {prefix} THEN {TierPrefix} ELSE {TierSubstring} END)";

        return new FreeTextPredicate(where, tier, Typo(words, phrases, parameters), parameters);
    }

    /// <summary>
    /// Splits free text into bare words and quoted phrases, each reduced to its letter, digit and
    /// mark runs (lower-cased, since the vector is). A word with punctuation inside it
    /// (<c>AC/DC</c>, <c>don't</c>) becomes its runs, which is how the 'simple' parser indexed it;
    /// an unclosed quote runs to the end of the text; a term with no runs at all (punctuation,
    /// operator characters) drops out.
    /// </summary>
    public static (IReadOnlyList<string> Words, IReadOnlyList<string> Phrases) Terms(string text)
    {
        var words = new List<string>();
        var phrases = new List<string>();

        string[] segments = text.Split('"');
        for (int i = 0; i < segments.Length; i++)
        {
            var runs = lexeme_run.Matches(segments[i])
                .Select(m => m.Value.ToLowerInvariant())
                .ToList();

            if (runs.Count == 0)
                continue;

            // Odd segments sit between a pair of quote marks (or after an unclosed one).
            if (i % 2 == 1)
                phrases.Add(string.Join(' ', runs));
            else
                words.AddRange(runs);
        }

        return (words, phrases);
    }

    public static string EscapeLike(string raw)
        => raw.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Every word long enough to carry trigrams must resemble a title or artist word. The threshold
    /// is compared as <c>real</c>, the type <c>word_similarity</c> returns: against a numeric literal
    /// a real 0.4 reads as 0.40000000596 and slips over its own threshold.
    /// </summary>
    private static string? Typo(IReadOnlyList<string> words, IReadOnlyList<string> phrases, Dictionary<string, object> parameters)
    {
        var typoWords = words.Concat(phrases.SelectMany(ph => ph.Split(' ')))
            .Where(w => w.Length >= MinFuzzyLength)
            .Distinct()
            .ToList();

        if (typoWords.Count == 0)
            return null;

        string threshold = TrigramThreshold.ToString(CultureInfo.InvariantCulture) + "::real";
        var clauses = new List<string>();

        for (int i = 0; i < typoWords.Count; i++)
        {
            string name = $"{param_prefix}t{i}";
            parameters[name] = typoWords[i];
            clauses.Add($"(word_similarity(@{name}, s.title) > {threshold} OR word_similarity(@{name}, s.artist) > {threshold})");
        }

        return "(" + string.Join("\n AND ", clauses) + ")";
    }

    private static string Substring()
        => $"""
            (s.title ILIKE @{param_prefix}like OR s.title_unicode ILIKE @{param_prefix}like
             OR s.artist ILIKE @{param_prefix}like OR s.artist_unicode ILIKE @{param_prefix}like
             OR s.tags ILIKE @{param_prefix}like OR s.source ILIKE @{param_prefix}like OR u.username::text ILIKE @{param_prefix}like)
            """;
}

/// <param name="Where">The literal match: a parenthesised boolean expression to AND into the WHERE
/// clause.</param>
/// <param name="TierSql">A parenthesised integer expression, the literal match tier (0 exact, 1
/// prefix, 2 substring), or null when the text has the substring layer alone (shorter than
/// <see cref="FreeTextSearch.MinFuzzyLength"/>, or no letter or digit in it).</param>
/// <param name="TypoWhere">The typo fallback, to use IN PLACE of <paramref name="Where"/> when no row
/// matches that under the page's other filters (every such row then ranks as
/// <see cref="FreeTextSearch.TierTrigram"/>), or null when the text has no word long enough.</param>
/// <param name="Parameters">Every value the expressions reference, all named <c>ft_*</c>.</param>
public sealed record FreeTextPredicate(string Where, string? TierSql, string? TypoWhere, IReadOnlyDictionary<string, object> Parameters);
