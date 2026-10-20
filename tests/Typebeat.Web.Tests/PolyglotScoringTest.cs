using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// PR 43's server rules for a POLYGLOT play, driven as pure statics (no host): where it is local only,
/// how its claimed maximum is held against the map's Polyglot stream, how its combo is bounded, and how
/// it is priced. The cross-repo half (that the stored stream and cells are the game's own) is
/// WireCompat's <c>PolyglotParityTest</c>.
/// </summary>
[TestFixture]
public class PolyglotScoringTest
{
    private const string russian_lyrics =
        """{"version":2,"song_end_ms":12000,"lines":[{"text":"Privet mir","original":"Привет мир","start_ms":1000,"end_ms":3000,"words":[{"text":"Privet","original":"Привет","start_ms":1000,"end_ms":1900},{"text":"mir","original":"мир","start_ms":1900,"end_ms":2800}]},{"text":"Rastsvetali yabloni","start_ms":3000,"end_ms":6000,"words":[{"text":"Rastsvetali","original":"Расцветали","start_ms":3000,"end_ms":4500},{"text":"yabloni","original":"яблони","start_ms":4500,"end_ms":5800}]}]}""";

    private const string japanese_lyrics =
        """{"version":2,"song_end_ms":12000,"lines":[{"text":"kimi ga suki","original":"君がすき","start_ms":1000,"end_ms":3200,"words":[{"text":"kimi","original":"君","start_ms":1000,"end_ms":1700},{"text":"ga","original":"が","start_ms":1700,"end_ms":2100},{"text":"suki","original":"すき","start_ms":2100,"end_ms":3000}]},{"text":"kiss wa yume","start_ms":3200,"end_ms":6000,"words":[{"text":"kiss","original":"愛","start_ms":3200,"end_ms":4200},{"text":"wa","original":"は","start_ms":4200,"end_ms":4600},{"text":"yume","original":"夢","start_ms":4600,"end_ms":5800}]}]}""";

    private static ParsedDifficulty parse(string timing, string language)
        => BeatmapPackageParser.ParseDifficulty("m.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: lyricsSection(timing), language: language)));

    // The synthetic package takes the [Lyrics] section as its compact lines: a header object, then
    // one object per line, as the game's writer emits them.
    private static string lyricsSection(string timing)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(timing)!.AsObject();
        var lines = document["lines"]!.AsArray().Select(l => l!.ToJsonString());
        return string.Join('\n', new[] { $"{{\"version\":2,\"song_end_ms\":{document["song_end_ms"]}}}" }.Concat(lines));
    }

    [Test]
    public void PolyglotIsLocalOnlyOnChineseAndKoreanMapsAlone()
    {
        Assert.Multiple(() =>
        {
            foreach (string language in BeatmapLanguages.All.Append(BeatmapLanguages.Unset))
            {
                bool expected = language is "chinese" or "korean";
                Assert.That(LocalOnlyMods.IsLocalOnlyOn("PG", language), Is.EqualTo(expected), language);
                Assert.That(LocalOnlyMods.IsLocalOnlyOn("pg", language), Is.EqualTo(expected), language);
                Assert.That(LocalOnlyMods.IsLocalOnlyOn("HD", language), Is.False, $"an ordinary mod on {language}");
            }

            Assert.That(LocalOnlyMods.IsLocalOnly("PG"), Is.False, "Polyglot is no longer local only by acronym");
            Assert.That(LocalOnlyMods.AnyLocalOnly(["""[{"acronym":"PG"}]"""], "korean"), Is.True);
            Assert.That(LocalOnlyMods.AnyLocalOnly(["""[{"acronym":"PG"}]"""], "japanese"), Is.False);
            Assert.That(ScoreSubmission.ModRanked("PG"), Is.True, "ranked at 1.0x");
            Assert.That(ModMultiplier.For("PG", null), Is.EqualTo(1.0));
        });
    }

    [Test]
    public void TheEffectiveLanguageIsTheDifficultysOwn_ElseTheSets()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PolyglotSummary.EffectiveDeclared("russian", "korean"), Is.EqualTo("russian"));
            Assert.That(PolyglotSummary.EffectiveDeclared("", "korean"), Is.EqualTo("korean"));
            Assert.That(PolyglotSummary.EffectiveDeclared(null, null), Is.EqualTo(""));
        });
    }

    [Test]
    public void ALatinOrCyrillicMapsPolyglotClaimMustBeItsExactCellCount()
    {
        var summary = parse(russian_lyrics, "russian").Ratings.Polyglot!;
        int cells = summary.Plain.Cells;

        Assert.Multiple(() =>
        {
            Assert.That(summary.Japanese, Is.False);
            Assert.That(summary.Supported, Is.True);
            Assert.That(summary.Plain.Combo, Is.EqualTo(cells), "one judgement per object");
            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = cells }, summary, literate: false), Is.True);
            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = cells + 1 }, summary, literate: false), Is.False);
            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = cells - 1 }, summary, literate: false), Is.False);
            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = 1 }, null, literate: false), Is.True,
                "a matrix without its Polyglot half is accepted unchecked, as an unknown judged-cell count is");
        });
    }

    [Test]
    public void AJapaneseMapsPolyglotClaimMayFollowTheSpellingTyped_WithinItsBounds()
    {
        var summary = parse(japanese_lyrics, "japanese").Ratings.Polyglot!;
        var stream = summary.Plain;

        Assert.Multiple(() =>
        {
            Assert.That(summary.Japanese, Is.True);
            Assert.That(stream.Combo, Is.GreaterThan(stream.Cells), "characters are scored per minimum input");
            Assert.That(stream.MaximumInputs, Is.GreaterThan(stream.Combo));

            foreach (int claimed in new[] { stream.Cells, stream.Combo, stream.MaximumInputs })
                Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = claimed }, summary, literate: false), Is.True, $"{claimed}");

            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = stream.Cells - 1 }, summary, literate: false), Is.False, "below one input per object");
            Assert.That(ScoreSubmission.PolyglotMaximumMatchesMap(new Dictionary<string, int> { ["great"] = stream.MaximumInputs + 1 }, summary, literate: false), Is.False, "above the longest spelling");
        });
    }

    [Test]
    public void AJapanesePlaysComboCeilingIsItsMinimumInputCombo()
    {
        // A kana-input play records fewer judgements than the minimum romaji, but its combo still grows
        // by each character's minimum inputs, so its honest max combo is above its own maximum count.
        var maximum = new Dictionary<string, int> { ["great"] = 10 };
        var statistics = new Dictionary<string, int> { ["great"] = 10 };

        Assert.Multiple(() =>
        {
            Assert.That(ScoringContract.Recompute(statistics, maximum, 14).StatisticsValid, Is.False, "the ordinary ceiling refuses it");
            Assert.That(ScoringContract.Recompute(statistics, maximum, 14, comboMaximum: 14).StatisticsValid, Is.True, "the minimum-input combo admits it");
            Assert.That(ScoringContract.Recompute(statistics, maximum, 15, comboMaximum: 14).StatisticsValid, Is.False, "and nothing above it");
            Assert.That(ScoringContract.Recompute(statistics, maximum, 14, comboMaximum: 14).TheoreticalMaxCombo, Is.EqualTo(14));
        });
    }

    [Test]
    public void APolyglotPlayIsPricedOnItsOwnStream()
    {
        var map = parse(russian_lyrics, "russian");
        var ratings = BeatmapRatings.Parse(map.RatingsJson)!;
        var notes = new PerformancePoints.NoteCounts(ratings.Polyglot!.Plain.Cells, 0, 0);
        ScoreMod[] polyglot = [new("PG", null)];

        var (plainPp, _) = PerformancePoints.ForScore(true, null, notes, 1.0, notes.Notes, ratings, map.Ratings.Polyglot!.Plain.PlayedDurationS);
        var (pgPp, settled) = PerformancePoints.ForScore(true, polyglot, notes, 1.0, notes.Notes, ratings);
        var cell = ratings.TryGet(LyricDifficulty.JudgementArm.None, false, 1.0, polyglot: true)!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(settled, Is.True);
            Assert.That(pgPp, Is.EqualTo(PerformancePoints.Compute(cell.Stars, notes.Notes, cell.DifficultCharacters, 0, 1.0, notes.Notes, polyglot,
                playedDurationSeconds: ratings.Polyglot.Plain.PlayedDurationS)), "the Polyglot cell and the Polyglot stream's duration");
            Assert.That(pgPp, Is.Not.EqualTo(plainPp), "and not the base reading");
        });
    }

    [Test]
    public void APolyglotPlayWithoutItsMatrixHalfIsPending_AndOneOnAnUnratableMapIsNeverPriced()
    {
        ScoreMod[] polyglot = [new("PG", null)];
        var notes = new PerformancePoints.NoteCounts(20, 0, 0);
        var baseOnly = BeatmapRatings.Parse(BeatmapRatings.Compute(parse(russian_lyrics, "russian").Lines).ToJson())!;
        var korean = BeatmapRatings.Parse(parse(russian_lyrics, "korean").RatingsJson)!;

        Assert.Multiple(() =>
        {
            Assert.That(PerformancePoints.ForScore(true, polyglot, notes, 1.0, 20, baseOnly), Is.EqualTo(((double?)null, false)), "pending, retried by the backfill");
            Assert.That(PerformancePoints.ForScore(true, polyglot, notes, 1.0, 20, korean), Is.EqualTo(((double?)null, true)), "refused for good");
        });
    }

    [Test]
    public void AJapanesePlaysComboIsClampedToItsMinimumRomajiFullCombo()
    {
        var map = parse(japanese_lyrics, "japanese");
        var ratings = BeatmapRatings.Parse(map.RatingsJson)!;
        var stream = ratings.Polyglot!.Plain;
        var cell = ratings.TryGet(LyricDifficulty.JudgementArm.None, false, 1.0, polyglot: true)!.Value;
        ScoreMod[] polyglot = [new("PG", null)];

        // A play that typed some longer spellings: more judgements (notes) than the combo can reach.
        var notes = new PerformancePoints.NoteCounts(stream.Combo + 3, 0, 0);

        var (pp, _) = PerformancePoints.ForScore(true, polyglot, notes, 1.0, stream.Combo, ratings);
        var (undeclared, _) = PerformancePoints.ForScore(true, polyglot, notes, 1.0, stream.Combo,
            BeatmapRatings.Parse(parse(japanese_lyrics, "").RatingsJson), setLanguage: "");

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.EqualTo(PerformancePoints.Compute(cell.Stars, notes.Notes, cell.DifficultCharacters, 0, 1.0, stream.Combo, polyglot,
                playedDurationSeconds: stream.PlayedDurationS, comboNotes: stream.Combo)), "a full minimum-input combo is a full combo");
            Assert.That(undeclared, Is.Not.EqualTo(pp), "the clamp follows the DECLARED language, as the game's calculator does");
        });
    }

    [Test]
    public void TheMatrixRoundTripsItsPolyglotHalf()
    {
        var map = parse(japanese_lyrics, "japanese");
        var back = BeatmapRatings.Parse(map.RatingsJson)!;

        Assert.Multiple(() =>
        {
            Assert.That(back.Count, Is.EqualTo(36), "eighteen base cells and eighteen Polyglot ones");
            Assert.That(back.Polyglot!.Plain, Is.EqualTo(map.Ratings.Polyglot!.Plain));
            Assert.That(back.Polyglot.Literate, Is.EqualTo(map.Ratings.Polyglot.Literate));
            Assert.That(back.Polyglot.Language, Is.EqualTo("japanese"));
            Assert.That(back.Polyglot.Declared, Is.EqualTo("japanese"));
            Assert.That(back.ToJson(), Is.EqualTo(map.RatingsJson), "byte for byte");
        });
    }
}
