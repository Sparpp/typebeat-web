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
/// </summary>
[TestFixture]
public class SearchOperatorParityTest
{
    private static readonly MethodInfo applyQueries = typeof(ClientParser).GetMethod(
        "ApplyQueries", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("FilterQueryParser.ApplyQueries not found");

    /// <summary>Every thousandth from 0 to 10, plus the awkward ones: the listing fixtures, values a
    /// hair either side of a hundredth, and ratings whose rounded display and floored value differ.</summary>
    private static readonly double[] ratings = Enumerable.Range(0, 10_001).Select(i => i / 1000.0)
        .Concat([4.0712, 4.068, 4.0799999, 4.0099999, 3.9999999, 0.295, 2.675, 1.005, 6.9999, 4.0750001])
        .Distinct()
        .ToArray();

    private static readonly string[] equalityOperators = ["=", ":", "!=", "!:"];

    private static readonly string[] starOperands =
        ["4", "4.0", "4.00", "4.07", "4.1", "0.29", "0.57", "1", "2.67", "6.99", "7", "10", "0", "4.075", "0.3"];

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
                var game = ratings.Where(r => GameMatches(query, new ClientBeatmap { StarRating = r })).ToHashSet();

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

    [Test]
    public async Task BpmEquality_MatchesTheGame()
    {
        // Quarter steps are exact in binary, so the 0.5 edges are hit exactly on both sides.
        double[] bpms = Enumerable.Range(400, 400).Select(i => i / 4.0).ToArray();
        var mismatches = new List<string>();

        foreach (string op in equalityOperators)
        {
            foreach (string operand in new[] { "128", "128.5", "150", "99.75" })
            {
                string query = "bpm" + op + operand;
                var site = await SiteMatchesAsync(query, "s", "bpm", bpms);
                var game = bpms.Where(v => GameMatches(query, new ClientBeatmap { BPM = v })).ToHashSet();

                Collect(mismatches, query, site, game);
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)));
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
        // A rating ALREADY on the hundredths by the game's own floor (0.29 is not: 0.29 * 100 is
        // 28.999..., which the game floors to 0.28, and the site's raw comparison does not).
        double[] grid = Enumerable.Range(0, 1001).Select(i => i / 100.0)
            .Where(r => ClientFormat.FloorToDecimalDigits(r, 2) == r)
            .ToArray();
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

    private static bool GameMatches(string query, ClientBeatmap beatmap)
    {
        var criteria = new ClientCriteria();
        applyQueries.Invoke(null, [criteria, query]);

        // A spelling the game failed to read would be left as search text and silently turn the
        // comparison into a text match; every query here is one it reads.
        Assert.That(criteria.SearchText?.Trim(), Is.Null.Or.Empty, $"the game did not parse {query}");

        return ClientMatcher.CheckCriteriaMatch(beatmap, criteria);
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
