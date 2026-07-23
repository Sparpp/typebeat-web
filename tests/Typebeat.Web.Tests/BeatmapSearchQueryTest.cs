using Typebeat.Web.Search;

namespace Typebeat.Web.Tests;

/// <summary>
/// The pure query parser for the /beatmapsets search box: each operator, quoting, the numeric
/// comparators/ranges, date granularity, garbage input, and free text mixed with operators.
/// No database; <see cref="BeatmapSearchQuery.Parse"/> and the SQL builder are both pure.
/// </summary>
public class BeatmapSearchQueryTest
{
    // ---- free text ----

    [Test]
    public void PlainText_IsAllFreeText_NoOperators()
    {
        var q = BeatmapSearchQuery.Parse("bohemian rhapsody");

        Assert.Multiple(() =>
        {
            Assert.That(q.FreeText, Is.EqualTo("bohemian rhapsody"));
            Assert.That(q.HasOperators, Is.False);
        });
    }

    [Test]
    public void Empty_And_Null_ParseToEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapSearchQuery.Parse("").FreeText, Is.Empty);
            Assert.That(BeatmapSearchQuery.Parse(null).FreeText, Is.Empty);
            Assert.That(BeatmapSearchQuery.Parse("   ").FreeText, Is.Empty);
        });
    }

    // ---- text operators ----

    [Test]
    public void TextOperators_AreExtracted_LeavingFreeTextBehind()
    {
        var q = BeatmapSearchQuery.Parse("neon title:rhapsody artist:queen");

        Assert.Multiple(() =>
        {
            Assert.That(q.FreeText, Is.EqualTo("neon"));
            Assert.That(q.TextFilters, Has.Count.EqualTo(2));
            Assert.That(q.TextFilters, Has.One.Matches<TextFilter>(f => f.Field == FilterField.Title && f.Value == "rhapsody"));
            Assert.That(q.TextFilters, Has.One.Matches<TextFilter>(f => f.Field == FilterField.Artist && f.Value == "queen"));
        });
    }

    [Test]
    public void QuotedValue_KeepsSpaces()
    {
        var q = BeatmapSearchQuery.Parse("title:\"night drive\" extra");

        Assert.Multiple(() =>
        {
            Assert.That(q.TextFilters.Single().Value, Is.EqualTo("night drive"));
            Assert.That(q.FreeText, Is.EqualTo("extra"));
        });
    }

    [Test]
    public void CreatorAndMapper_AreAliases_TagAndTags_AreAliases()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapSearchQuery.Parse("mapper:neon").TextFilters.Single().Field, Is.EqualTo(FilterField.Creator));
            Assert.That(BeatmapSearchQuery.Parse("creator:neon").TextFilters.Single().Field, Is.EqualTo(FilterField.Creator));
            Assert.That(BeatmapSearchQuery.Parse("tag:rock").TextFilters.Single().Field, Is.EqualTo(FilterField.Tag));
            Assert.That(BeatmapSearchQuery.Parse("tags:rock").TextFilters.Single().Field, Is.EqualTo(FilterField.Tag));
        });
    }

    [Test]
    public void KeysAreCaseInsensitive()
    {
        var q = BeatmapSearchQuery.Parse("TITLE:hi Star:>4");

        Assert.Multiple(() =>
        {
            Assert.That(q.TextFilters.Single().Field, Is.EqualTo(FilterField.Title));
            Assert.That(q.NumericFilters.Single().Field, Is.EqualTo(FilterField.Stars));
        });
    }

    // ---- numeric comparators + ranges ----

    [TestCase("star:>4", Comparator.Gt, 4.0)]
    [TestCase("star:>=4", Comparator.Gte, 4.0)]
    [TestCase("star:<6", Comparator.Lt, 6.0)]
    [TestCase("star:<=6", Comparator.Lte, 6.0)]
    [TestCase("star:=5", Comparator.Eq, 5.0)]
    [TestCase("star:5", Comparator.Eq, 5.0)]
    [TestCase("star:4.5", Comparator.Eq, 4.5)]
    public void NumericComparators(string input, Comparator op, double low)
    {
        var n = BeatmapSearchQuery.Parse(input).NumericFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(n.Field, Is.EqualTo(FilterField.Stars));
            Assert.That(n.Op, Is.EqualTo(op));
            Assert.That(n.Low, Is.EqualTo(low));
        });
    }

    [TestCase("star:4-6", 4.0, 6.0)]
    [TestCase("star:6-4", 4.0, 6.0)]  // normalised low..high
    [TestCase("star:4..6", 4.0, 6.0)]
    public void NumericRanges(string input, double low, double high)
    {
        var n = BeatmapSearchQuery.Parse(input).NumericFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(n.Op, Is.EqualTo(Comparator.Range));
            Assert.That(n.Low, Is.EqualTo(low));
            Assert.That(n.High, Is.EqualTo(high));
        });
    }

    [Test]
    public void Wpm_And_Cpm_And_Bpm_AreNumericFields()
    {
        var q = BeatmapSearchQuery.Parse("wpm:>120 cpm:600-800 bpm:128");

        Assert.Multiple(() =>
        {
            Assert.That(q.NumericFilters, Has.One.Matches<NumericFilter>(n => n.Field == FilterField.Wpm && n.Op == Comparator.Gt && n.Low == 120));
            Assert.That(q.NumericFilters, Has.One.Matches<NumericFilter>(n => n.Field == FilterField.Cpm && n.Op == Comparator.Range && n.Low == 600 && n.High == 800));
            Assert.That(q.NumericFilters, Has.One.Matches<NumericFilter>(n => n.Field == FilterField.Bpm && n.Op == Comparator.Eq && n.Low == 128));
        });
    }

    // ---- length mm:ss ----

    [TestCase("length:90", 90.0)]
    [TestCase("length:1:30", 90.0)]
    public void Length_AcceptsSecondsAndMmSs(string input, double seconds)
    {
        var n = BeatmapSearchQuery.Parse(input).NumericFilters.Single();
        Assert.That(n.Low, Is.EqualTo(seconds));
    }

    [Test]
    public void Length_MmSs_WithComparator_AndRange()
    {
        var gt = BeatmapSearchQuery.Parse("length:>1:30").NumericFilters.Single();
        var range = BeatmapSearchQuery.Parse("length:1:30-2:00").NumericFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(gt.Op, Is.EqualTo(Comparator.Gt));
            Assert.That(gt.Low, Is.EqualTo(90));
            Assert.That(range.Op, Is.EqualTo(Comparator.Range));
            Assert.That(range.Low, Is.EqualTo(90));
            Assert.That(range.High, Is.EqualTo(120));
        });
    }

    [Test]
    public void LenAlias_Works()
        => Assert.That(BeatmapSearchQuery.Parse("len:>60").NumericFilters.Single().Field, Is.EqualTo(FilterField.Length));

    // ---- date granularity ----

    [Test]
    public void Date_Year_IsWholeYearWindow()
    {
        var d = BeatmapSearchQuery.Parse("date:2026").DateFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(d.MinInclusive, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(d.MaxExclusive, Is.EqualTo(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    [Test]
    public void Date_Month_And_Day_Windows()
    {
        var month = BeatmapSearchQuery.Parse("date:2026-02").DateFilters.Single();
        var day = BeatmapSearchQuery.Parse("date:2026-02-15").DateFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(month.MinInclusive, Is.EqualTo(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(month.MaxExclusive, Is.EqualTo(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(day.MinInclusive, Is.EqualTo(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(day.MaxExclusive, Is.EqualTo(new DateTime(2026, 2, 16, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    [Test]
    public void Date_Comparators_ShiftTheEdge()
    {
        var after = BeatmapSearchQuery.Parse("date:>2026-07-01").DateFilters.Single();
        var before = BeatmapSearchQuery.Parse("date:<2026").DateFilters.Single();
        var atLeastYear = BeatmapSearchQuery.Parse("date:>=2026").DateFilters.Single();

        Assert.Multiple(() =>
        {
            // > a single day means strictly after that whole day.
            Assert.That(after.MinInclusive, Is.EqualTo(new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(after.MaxExclusive, Is.Null);
            // < a year means before it begins.
            Assert.That(before.MaxExclusive, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(before.MinInclusive, Is.Null);
            Assert.That(atLeastYear.MinInclusive, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    [Test]
    public void Date_Range_WithDotDot()
    {
        var d = BeatmapSearchQuery.Parse("date:2026-01-01..2026-06-30").DateFilters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(d.MinInclusive, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(d.MaxExclusive, Is.EqualTo(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    // ---- garbage / unknown keys degrade to free text ----

    [Test]
    public void UnknownKey_IsFreeText()
    {
        var q = BeatmapSearchQuery.Parse("colour:blue hello");

        Assert.Multiple(() =>
        {
            Assert.That(q.HasOperators, Is.False);
            Assert.That(q.FreeText, Is.EqualTo("colour:blue hello"));
        });
    }

    [Test]
    public void MalformedNumeric_FallsBackToFreeText()
    {
        var q = BeatmapSearchQuery.Parse("star:abc");

        Assert.Multiple(() =>
        {
            Assert.That(q.NumericFilters, Is.Empty);
            Assert.That(q.FreeText, Is.EqualTo("star:abc"));
        });
    }

    [Test]
    public void MalformedDate_FallsBackToFreeText()
    {
        var q = BeatmapSearchQuery.Parse("date:2026-13-40");

        Assert.Multiple(() =>
        {
            Assert.That(q.DateFilters, Is.Empty);
            Assert.That(q.FreeText, Is.EqualTo("date:2026-13-40"));
        });
    }

    [Test]
    public void EmptyOperatorValue_IsFreeText()
    {
        var q = BeatmapSearchQuery.Parse("title:");
        Assert.That(q.FreeText, Is.EqualTo("title:"));
    }

    [Test]
    public void MixedFreeTextAndOperators_AllSurvive()
    {
        var q = BeatmapSearchQuery.Parse("piano title:\"blue moon\" star:4-6 wpm:>100 date:2026 nightcore");

        Assert.Multiple(() =>
        {
            Assert.That(q.FreeText, Is.EqualTo("piano nightcore"));
            Assert.That(q.TextFilters, Has.Count.EqualTo(1));
            Assert.That(q.NumericFilters, Has.Count.EqualTo(2));
            Assert.That(q.DateFilters, Has.Count.EqualTo(1));
        });
    }

    // ---- SQL builder is parameterised and column-whitelisted ----

    [Test]
    public void Sql_Empty_WhenNoOperators()
    {
        var (sql, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("just words"));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Is.Empty);
            Assert.That(param, Is.Empty);
        });
    }

    [Test]
    public void Sql_TextFilter_UsesIlikeParameterWithWildcards()
    {
        var (sql, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("title:rhap"));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("s.title ILIKE @op0"));
            Assert.That(sql, Does.Contain("s.title_unicode ILIKE @op0"));
            Assert.That(param["op0"], Is.EqualTo("%rhap%"));
        });
    }

    [Test]
    public void Sql_PerDifficultyNumerics_CollapseIntoOneExists()
    {
        var (sql, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("star:>4 wpm:<200"));

        Assert.Multiple(() =>
        {
            Assert.That(System.Text.RegularExpressions.Regex.Matches(sql, "EXISTS").Count, Is.EqualTo(1));
            Assert.That(sql, Does.Contain("b.difficulty_rating > @op0"));
            Assert.That(sql, Does.Contain("b.wpm < @op1"));
            Assert.That(param["op0"], Is.EqualTo(4.0));
            Assert.That(param["op1"], Is.EqualTo(200.0));
        });
    }

    [Test]
    public void Sql_Cpm_IsDerivedFromStoredCounts()
    {
        var (sql, _) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("cpm:>600"));
        Assert.That(sql, Does.Contain("b.wpm * b.char_count").IgnoreCase);
    }

    [Test]
    public void Sql_Range_EmitsBetween()
    {
        var (sql, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("star:4-6"));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("b.difficulty_rating BETWEEN @op0 AND @op1"));
            Assert.That(param["op0"], Is.EqualTo(4.0));
            Assert.That(param["op1"], Is.EqualTo(6.0));
        });
    }

    [Test]
    public void Sql_Bpm_IsSetScoped_NotInExists()
    {
        var (sql, _) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("bpm:>120"));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("s.bpm > @op0"));
            Assert.That(sql, Does.Not.Contain("EXISTS"));
        });
    }

    [Test]
    public void Sql_Date_UsesHalfOpenBounds()
    {
        var (sql, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("date:2026"));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("s.submitted_at >= @op0"));
            Assert.That(sql, Does.Contain("s.submitted_at < @op1"));
            Assert.That(param["op0"], Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(param["op1"], Is.EqualTo(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    [Test]
    public void Sql_EscapesLikeWildcardsInValue()
    {
        var (_, param) = BeatmapSearchSql.Build(BeatmapSearchQuery.Parse("title:50%"));
        Assert.That(param["op0"], Is.EqualTo(@"%50\%%"));
    }
}
