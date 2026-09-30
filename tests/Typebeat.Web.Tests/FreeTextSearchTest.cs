using Typebeat.Web.Search;

namespace Typebeat.Web.Tests;

/// <summary>
/// The shared free-text predicate builder (backlog 351), driven as a pure static: the SQL shape it
/// emits, the terms it composes the tsquery from, and that user text only ever travels as a
/// parameter value. The listing behaviour itself is pinned end to end in ListingPageTest.
/// </summary>
public class FreeTextSearchTest
{
    [Test]
    public void Empty_HasNoPredicate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FreeTextSearch.Build(null), Is.Null);
            Assert.That(FreeTextSearch.Build("   "), Is.Null);
        });
    }

    [Test]
    public void ShortText_IsSubstringOnly_WithNoTier()
    {
        var p = FreeTextSearch.Build("dr")!;

        Assert.Multiple(() =>
        {
            Assert.That(p.TierSql, Is.Null);
            Assert.That(p.TypoWhere, Is.Null);
            Assert.That(p.Where, Does.Not.Contain("tsquery"));
            Assert.That(p.Where, Does.Not.Contain("word_similarity"));
            Assert.That(p.Parameters["ft_like"], Is.EqualTo("%dr%"));
        });
    }

    [Test]
    public void EveryWordOfTwoOrMoreCharacters_IsAPrefix_OneCharacterWordsStayWhole()
    {
        var p = FreeTextSearch.Build("washing mach a")!;

        Assert.Multiple(() =>
        {
            Assert.That(p.Parameters["ft_w0"], Is.EqualTo("washing"));
            Assert.That(p.Parameters["ft_w1"], Is.EqualTo("mach"));
            Assert.That(p.Parameters["ft_w2"], Is.EqualTo("a"));
            Assert.That(p.Where, Does.Contain("to_tsquery('simple', @ft_w0 || ':*')"));
            Assert.That(p.Where, Does.Contain("to_tsquery('simple', @ft_w1 || ':*')"));
            Assert.That(p.Where, Does.Contain("plainto_tsquery('simple', @ft_w2)"));
            Assert.That(p.Where, Does.Not.Contain("@ft_w2 || ':*'"));
        });
    }

    [Test]
    public void QuotedText_IsOnePhrase_InBothLayers()
    {
        var (words, phrases) = FreeTextSearch.Terms("\"Washing Machine\" heart");
        var p = FreeTextSearch.Build("\"Washing Machine\" heart")!;

        Assert.Multiple(() =>
        {
            Assert.That(phrases, Is.EqualTo(new[] { "washing machine" }));
            Assert.That(words, Is.EqualTo(new[] { "heart" }));
            Assert.That(p.TierSql, Does.Contain("phraseto_tsquery('simple', @ft_p0)"));
            Assert.That(p.Parameters["ft_p0"], Is.EqualTo("washing machine"));
            // Quote marks are syntax, so the fragment drops them; the phrase's words still get a
            // typo probe each.
            Assert.That(p.Parameters["ft_like"], Is.EqualTo("%Washing Machine heart%"));
            Assert.That(p.Parameters.Where(kv => kv.Key.StartsWith("ft_t")).Select(kv => kv.Value),
                Is.EquivalentTo(new[] { "heart", "washing", "machine" }));
        });
    }

    [Test]
    public void Terms_KeepLetterDigitAndMarkRunsOnly()
    {
        var (words, phrases) = FreeTextSearch.Terms("AC/DC don't &|!():*<> 日本語 \"unclosed tail");

        Assert.Multiple(() =>
        {
            Assert.That(words, Is.EqualTo(new[] { "ac", "dc", "don", "t", "日本語" }));
            Assert.That(phrases, Is.EqualTo(new[] { "unclosed tail" }));
        });
    }

    [Test]
    public void PunctuationOnly_KeepsTheSubstringLayerAlone()
    {
        var p = FreeTextSearch.Build("!!!&|")!;

        Assert.Multiple(() =>
        {
            Assert.That(p.Where, Does.Not.Contain("tsquery"));
            Assert.That(p.Where, Does.Contain("ILIKE @ft_like"));
            Assert.That(p.TypoWhere, Is.Null);
            Assert.That(p.TierSql, Is.Null, "one layer, no tier (and no CASE without a WHEN)");
        });
    }

    [Test]
    public void Typo_IsPerWord_OverTitleOrArtist_ForWordsOfThreeOrMore()
    {
        var p = FreeTextSearch.Build("washng machin a")!;

        Assert.Multiple(() =>
        {
            Assert.That(p.Parameters["ft_t0"], Is.EqualTo("washng"));
            Assert.That(p.Parameters["ft_t1"], Is.EqualTo("machin"));
            Assert.That(p.Parameters.ContainsKey("ft_t2"), Is.False, "a one-letter word has no trigrams worth matching");
            Assert.That(p.TypoWhere, Is.EqualTo(
                "((word_similarity(@ft_t0, s.title) > 0.4::real OR word_similarity(@ft_t0, s.artist) > 0.4::real)\n"
                + " AND (word_similarity(@ft_t1, s.title) > 0.4::real OR word_similarity(@ft_t1, s.artist) > 0.4::real))"));
            // The literal predicate never carries the typo layer: it is a fallback, not a union.
            Assert.That(p.Where, Does.Not.Contain("word_similarity"));
        });
    }

    [Test]
    public void TierOrder_IsExactThenPrefixThenSubstring()
    {
        string tier = FreeTextSearch.Build("dracula")!.TierSql!;

        Assert.That(tier, Is.EqualTo(
            "(CASE WHEN s.search @@ (plainto_tsquery('simple', @ft_w0)) THEN 0"
            + " WHEN s.search @@ (to_tsquery('simple', @ft_w0 || ':*')) THEN 1 ELSE 2 END)"));
    }

    [Test]
    public void Sql_NeverCarriesUserText()
    {
        // Same pin as BeatmapSearchQueryTest.Sql_NeverCarriesUserText, for the free-text half: a
        // hostile string reaches SQL as parameter values only, whatever shape it takes.
        const string hostile = "x');DROP_TABLE_users;-- \"select pg_sleep\" ':*|!";
        var p = FreeTextSearch.Build(hostile)!;

        Assert.Multiple(() =>
        {
            foreach (string sql in new[] { p.Where, p.TierSql!, p.TypoWhere! })
            {
                Assert.That(sql, Does.Not.Contain("DROP"));
                Assert.That(sql, Does.Not.Contain("drop"));
                Assert.That(sql, Does.Not.Contain("pg_sleep"));
                Assert.That(sql, Does.Not.Contain("x'"));
            }

            Assert.That(p.Parameters.Keys, Is.All.StartWith("ft_"));
            Assert.That(p.Parameters.Values, Has.Some.EqualTo("%x');DROP\\_TABLE\\_users;-- select pg\\_sleep ':*|!%"));
            // Every tsquery word is a bare run, so no tsquery operator can reach to_tsquery.
            Assert.That(p.Parameters.Where(kv => kv.Key.StartsWith("ft_w") || kv.Key.StartsWith("ft_p") || kv.Key.StartsWith("ft_t")).Select(kv => (string)kv.Value),
                Is.All.Matches<string>(v => v.All(ch => char.IsLetterOrDigit(ch) || ch == ' ')));
        });
    }
}
