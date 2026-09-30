using System.Globalization;
using System.Reflection;
using Dapper;
using Npgsql;
using Typebeat.Web.Search;
using ClientBeatmap = typebeat.Game.Beatmaps.BeatmapInfo;
using ClientCriteria = typebeat.Game.Screens.Select.FilterCriteria;
using ClientMatcher = typebeat.Game.Screens.Select.BeatmapCarouselFilterMatching;
using ClientParser = typebeat.Game.Screens.Select.FilterQueryParser;
using ClientFormat = typebeat.Game.Utils.FormatUtils;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the search box's EQUALITY (backlog 338): <c>stars=4.07</c> typed on
/// /beatmapsets has to select exactly the difficulties <c>stars=4.07</c> selects in the game's song
/// select.
///
/// <para>
/// The two arms are the real code on both sides and nothing retyped. The game's arm is
/// <c>FilterQueryParser.ApplyQueries</c> (internal, so reached by reflection) into a fresh
/// <c>FilterCriteria</c>, then <c>BeatmapCarouselFilterMatching.CheckCriteriaMatch</c> on a bare
/// <c>BeatmapInfo</c> carrying the value. The site's arm is <see cref="BeatmapSearchQuery.Parse"/>
/// and the very predicate <see cref="BeatmapSearchSql"/> emits, run by POSTGRES over an
/// <c>unnest</c> of the same values aliased as the column it names, so the floor and the
/// tolerance are evaluated by the engine that evaluates them in production (float8 arithmetic,
/// not a C# model of it). Read only: the query touches no table.
/// </para>
///
/// <para>
/// Greater and less are NOT a parity surface, by the owner's decision: they stay on the raw
/// stored rating while the game compares the floored one, so they agree exactly on ratings that
/// already sit on the hundredths and may differ inside a hundredth (a 4.005 is <c>stars&gt;4</c>
/// on the site and not in the game). <see cref="GreaterAndLess_AgreeOnTheHundredths_AndDivergeInsideOne"/>
/// pins both halves of that sentence so neither can change silently.
/// </para>
///
/// <para>
/// Backlog 342: both floors add the same epsilon (<c>FormatUtils.FLOOR_EPSILON</c> in the game,
/// <see cref="BeatmapSearchSql.STAR_FLOOR_EPSILON"/> here) to the scaled rating, because a rating
/// on an exact hundredth is not always one in binary: 4.1 * 100 is 409.99999999999994, so before
/// it both sides floored a 4.10 map to 4.09 and <c>stars=4.1</c> found nothing, identically. The
/// sweep agreed on that bug, which is why the answer on every hundredth is now pinned as well as
/// the agreement (<see cref="StarEquality_EveryExactHundredth_MatchesItsOwnOperand"/>).
/// </para>
/// </summary>
[TestFixture]
public class SearchOperatorParityTest
{
    private static readonly MethodInfo applyQueries = typeof(ClientParser).GetMethod(
        "ApplyQueries", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("FilterQueryParser.ApplyQueries not found");

    /// <summary>Every thousandth from 0 to 10, plus the awkward ones: the listing fixtures, values a
    /// hair either side of a hundredth, and ratings whose rounded display and floored value differ.
    /// Every exact hundredth is already among the thousandths (4100 / 1000.0 is the same double as
    /// 4.1, both being the nearest double to the same rational); the second list is backlog 342's
    /// values written as literals, the doubles one ulp either side of 4.1 (the closest a rating can
    /// sit to that hundredth without being it) and two just under it at 1e-10 and 1e-14.</summary>
    private static readonly double[] ratings = Enumerable.Range(0, 10_001).Select(i => i / 1000.0)
        .Concat([4.0712, 4.068, 4.0799999, 4.0099999, 3.9999999, 0.295, 2.675, 1.005, 6.9999, 4.0750001])
        .Concat([4.1, 4.10, 4.07, 0.29, 0.57, 1.1, 2.3, 8.2, 9.9, 10.0, Math.BitDecrement(4.1), Math.BitIncrement(4.1), 4.0999999999, 4.09999999999999])
        .Distinct()
        .ToArray();

    private static readonly string[] equalityOperators = ["=", ":", "!=", "!:"];

    /// <summary>The backlog 338 operands, plus every one-decimal operand from 0.1 to 9.9 (each is an
    /// x.x0, the shape that exposes backlog 342) and a few two-decimal spellings of them.</summary>
    private static readonly string[] starOperands =
        new[] { "4", "4.0", "4.00", "4.07", "4.1", "0.29", "0.57", "1", "2.67", "6.99", "7", "10", "0", "4.075", "0.3" }
            .Concat(Enumerable.Range(1, 99).Select(i => (i / 10.0).ToString("0.0", CultureInfo.InvariantCulture)))
            .Concat(["4.10", "0.10", "8.20", "9.90", "1.10"])
            .Distinct()
            .ToArray();

    [Test]
    public async Task StarEquality_MatchesTheGameOnEveryRating()
    {
        var mismatches = new List<string>();

        foreach (string op in equalityOperators)
        {
            foreach (string operand in starOperands)
            {
                string query = "stars" + op + operand;
                var site = await SiteMatchesAsync(query, "b", "difficulty_rating", ratings);
                var criteria = GameCriteria(query);
                var game = ratings.Where(r => ClientMatcher.CheckCriteriaMatch(new ClientBeatmap { StarRating = r }, criteria)).ToHashSet();

                Collect(mismatches, query, site, game);
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)));
    }

    /// <summary>
    /// The readable half of the pin, and the answer to what a ONE-DECIMAL (or whole) operand does in
    /// the game: tolerance 0 against the rating floored to two decimals, so <c>stars=4</c> is the
    /// hundredth [4.00, 4.01) and not [4.0, 4.1), and a three-decimal operand matches nothing.
    /// </summary>
    [TestCase("stars=4", new[] { 4.0, 4.005, 4.0099999 }, new[] { 3.9999999, 4.01, 4.05, 4.0712 })]
    [TestCase("stars=4.0", new[] { 4.0, 4.005 }, new[] { 4.01, 4.05 })]
    [TestCase("stars=4.07", new[] { 4.07, 4.0712, 4.0799999 }, new[] { 4.068, 4.08, 4.085 })]
    [TestCase("stars!=4.07", new[] { 4.068, 4.08, 3.99, 4.5 }, new[] { 4.07, 4.0712 })]
    [TestCase("stars=4.075", new double[0], new[] { 4.07, 4.075, 4.0750001, 4.08 })]
    // Backlog 342: a map rated exactly 4.10 (the card prints "4.1") is stars=4.1 on both sides.
    // The epsilon is 1e-9 on the SCALED value, so it reaches 1e-11 stars under a hundredth:
    // 4.09999999999999 (1e-14 under, float-noise distance) is lifted to 4.10, while 4.0999999999
    // (1e-10 under, ten times further than the reach) stays a 4.09.
    [TestCase("stars=4.1", new[] { 4.1, 4.1049, 4.09999999999999 }, new[] { 4.0999999999, 4.09, 4.0999999, 4.11 })]
    [TestCase("stars=4.10", new[] { 4.1 }, new[] { 4.09, 4.11 })]
    [TestCase("stars=4.09", new[] { 4.09, 4.0999999999 }, new[] { 4.1 })]
    [TestCase("stars!=4.1", new[] { 4.09, 4.11 }, new[] { 4.1 })]
    [TestCase("stars=0.29", new[] { 0.29 }, new[] { 0.28, 0.3 })]
    public async Task StarEquality_Table(string query, double[] match, double[] noMatch)
    {
        double[] all = match.Concat(noMatch).ToArray();
        var site = await SiteMatchesAsync(query, "b", "difficulty_rating", all);

        Assert.Multiple(() =>
        {
            foreach (double r in all)
            {
                bool expected = match.Contains(r);
                Assert.That(GameMatches(query, new ClientBeatmap { StarRating = r }), Is.EqualTo(expected), $"game {query} on {r}");
                Assert.That(site.Contains(r), Is.EqualTo(expected), $"site {query} on {r}");
            }
        });
    }

    /// <summary>
    /// BPM IS A SITE-ONLY OPERATOR SINCE PR 3. The game dropped the <c>bpm</c> keyword from song
    /// select (with its BPM sort, group and HUD counter), so <c>bpm=128</c> is free search text there
    /// now and there is no game half left to hold the site's against. The site KEEPS its operator, as
    /// it keeps <c>lang:</c>, <c>lyrics:</c> and <c>explicit:</c> which the game never had: removing a
    /// working website search operator is a product decision, not a mirror, and it is flagged for the
    /// owner rather than taken here. This pins both halves of that: the game really does leave the
    /// keyword unparsed (so if it ever comes back, this fails and the equality sweep this replaced
    /// has to come back with it), and the site still reads it with the 0.5 tolerance the game used.
    /// </summary>
    [Test]
    public async Task BpmIsASiteOnlyOperatorSinceTheGameDroppedIt()
    {
        foreach (string op in equalityOperators)
        {
            string query = "bpm" + op + "128";
            var criteria = new ClientCriteria();
            applyQueries.Invoke(null, [criteria, query]);

            Assert.That(criteria.SearchText?.Trim(), Is.EqualTo(query), $"the game no longer parses {query}; it is search text there");
        }

        // Quarter steps are exact in binary, so the 0.5 edges are hit exactly.
        double[] bpms = Enumerable.Range(400, 400).Select(i => i / 4.0).ToArray();
        var site = await SiteMatchesAsync("bpm=128", "s", "bpm", bpms);

        Assert.That(site.OrderBy(v => v), Is.EqualTo(bpms.Where(v => v > 127.5 && v < 128.5)), "the site's own bpm= keeps the game's old 0.5 tolerance");
    }

    [Test]
    public async Task LengthEquality_MatchesTheGame()
    {
        // Seconds on the site, milliseconds in the game: every quarter second up to ten minutes.
        double[] lengths = Enumerable.Range(0, 2400).Select(i => i / 4.0).ToArray();
        var mismatches = new List<string>();

        foreach (string op in equalityOperators)
        {
            foreach (string operand in new[] { "90", "1:30", "2m", "1.5m", "1m30s", "90s", "4m", "3:59", "0:05" })
            {
                string query = "length" + op + operand;
                var site = await SiteMatchesAsync(query, "b", "total_length_s", lengths);
                var game = lengths.Where(v => GameMatches(query, new ClientBeatmap { Length = v * 1000 })).ToHashSet();

                Collect(mismatches, query, site, game);
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)));
    }

    [Test]
    public async Task GreaterAndLess_AgreeOnTheHundredths_AndDivergeInsideOne()
    {
        // A rating ALREADY on the hundredths by the game's own floor. Since backlog 342 that is
        // every hundredth (0.29 used not to be: 0.29 * 100 is 28.999..., which the game floored to
        // 0.28 and the site's raw comparison did not), and the count pins it.
        double[] grid = Enumerable.Range(0, 1001).Select(i => i / 100.0)
            .Where(r => ClientFormat.FloorToDecimalDigits(r, 2) == r)
            .ToArray();
        Assert.That(grid, Has.Length.EqualTo(1001), "every hundredth floors to itself");
        var mismatches = new List<string>();

        foreach (string op in new[] { ">", ">=", ">:", "<", "<=", "<:" })
        {
            foreach (string operand in new[] { "4", "4.07", "6.5", "0.3" })
            {
                string query = "stars" + op + operand;
                var site = await SiteMatchesAsync(query, "b", "difficulty_rating", grid);
                var game = grid.Where(r => GameMatches(query, new ClientBeatmap { StarRating = r })).ToHashSet();

                Collect(mismatches, query, site, game);
            }
        }

        var offGrid = await SiteMatchesAsync("stars>4", "b", "difficulty_rating", [4.005]);

        Assert.Multiple(() =>
        {
            Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)));

            // The documented divergence: the site keeps greater/less on the raw value.
            Assert.That(offGrid, Does.Contain(4.005), "site stars>4 on 4.005");
            Assert.That(GameMatches("stars>4", new ClientBeatmap { StarRating = 4.005 }), Is.False, "game stars>4 on 4.005");
        });
    }

    /// <summary>
    /// Backlog 342's ANSWER, not only its agreement: for every hundredth from 0.00 to 10.00, the map
    /// rated exactly that (the nearest double, as both sides store it) is found by the operand
    /// written with two decimals, and with one when it ends in 0, and by nothing else in the grid,
    /// through the real game matcher and the real SQL. Without the epsilon both sides missed 4.1,
    /// 0.29, 0.57 and every other hundredth whose scaled double falls a hair under its integer.
    /// </summary>
    [Test]
    public async Task StarEquality_EveryExactHundredth_MatchesItsOwnOperand()
    {
        var misses = new List<string>();
        double[] hundredths = Enumerable.Range(0, 1001).Select(k => k / 100.0).ToArray();

        foreach (int k in Enumerable.Range(0, 1001))
        {
            double rating = hundredths[k];
            var spellings = new List<string> { rating.ToString("0.00", CultureInfo.InvariantCulture) };
            if (k % 10 == 0)
                spellings.Add(rating.ToString("0.0", CultureInfo.InvariantCulture));

            foreach (string operand in spellings)
            {
                string query = "stars=" + operand;
                var site = await SiteMatchesAsync(query, "b", "difficulty_rating", hundredths);
                var criteria = GameCriteria(query);
                var game = hundredths.Where(r => ClientMatcher.CheckCriteriaMatch(new ClientBeatmap { StarRating = r }, criteria)).ToHashSet();

                if (!site.SetEquals([rating]))
                    misses.Add($"site {query}: [{Show(site)}]");
                if (!game.SetEquals([rating]))
                    misses.Add($"game {query}: [{Show(game)}]");
            }
        }

        Assert.That(misses, Is.Empty, $"{misses.Count} misses, first 20:\n" + string.Join("\n", misses.Take(20)));

        static string Show(HashSet<double> set)
            => string.Join(", ", set.OrderBy(r => r).Select(r => r.ToString("R", CultureInfo.InvariantCulture)));
    }

    /// <summary>The readable half of the epsilon pin: one constant, one value, and the SQL spells it.</summary>
    [Test]
    public void TheTwoFloorEpsilonsAreTheSameConstant()
    {
        var (sql, _) = BeatmapSearchSql.NumericPredicate(BeatmapSearchQuery.Parse("stars=4.1").NumericFilters.Single());
        var literal = System.Text.RegularExpressions.Regex.Match(sql, @"\* 100 \+ ([0-9.eE+-]+)::float8\)");

        Assert.Multiple(() =>
        {
            Assert.That(BeatmapSearchSql.STAR_FLOOR_EPSILON, Is.EqualTo(ClientFormat.FLOOR_EPSILON));
            Assert.That(literal.Success, Is.True, sql);
            Assert.That(double.Parse(literal.Groups[1].Value, CultureInfo.InvariantCulture), Is.EqualTo(BeatmapSearchSql.STAR_FLOOR_EPSILON), sql);
        });
    }

    private static bool GameMatches(string query, ClientBeatmap beatmap)
        => ClientMatcher.CheckCriteriaMatch(beatmap, GameCriteria(query));

    /// <summary>The game's own parse of <paramref name="query"/>, built once per query so a sweep
    /// over ten thousand ratings does not re-parse it for each one.</summary>
    private static ClientCriteria GameCriteria(string query)
    {
        var criteria = new ClientCriteria();
        applyQueries.Invoke(null, [criteria, query]);

        // A spelling the game failed to read would be left as search text and silently turn the
        // comparison into a text match; every query here is one it reads.
        Assert.That(criteria.SearchText?.Trim(), Is.Null.Or.Empty, $"the game did not parse {query}");

        return criteria;
    }

    /// <summary>The site's verdicts: the emitted predicate, evaluated by Postgres over the values.</summary>
    private static async Task<HashSet<double>> SiteMatchesAsync(string query, string alias, string column, double[] values)
    {
        var parsed = BeatmapSearchQuery.Parse(query);
        Assert.That(parsed.FreeText, Is.Empty, $"the site did not parse {query}");

        var (predicate, parameters) = BeatmapSearchSql.NumericPredicate(parsed.NumericFilters.Single());

        var args = new DynamicParameters(parameters);
        args.Add("values", values);

        await using var conn = new NpgsqlConnection(ServerFixture.ConnectionString);
        var rows = await conn.QueryAsync<double>(
            $"SELECT {alias}.{column} FROM unnest(@values) AS {alias}({column}) WHERE {predicate}", args);

        return rows.ToHashSet();
    }

    private static void Collect(List<string> mismatches, string query, HashSet<double> site, HashSet<double> game)
    {
        foreach (double r in site.Except(game).OrderBy(r => r))
            mismatches.Add($"{query}: site matches {r.ToString("R", CultureInfo.InvariantCulture)}, the game does not");
        foreach (double r in game.Except(site).OrderBy(r => r))
            mismatches.Add($"{query}: the game matches {r.ToString("R", CultureInfo.InvariantCulture)}, the site does not");
    }
}
