using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the GRADE rule (PR 17) across the browser/server seam.
///
/// <para>Grades moved from completion to timing accuracy with a missed-cell condition on SS and S.
/// The rule lives in three places that must agree: the game's <c>TypeBeatScoreProcessor</c>, the
/// server's <see cref="ScoringContract"/>, and the hand-written <c>wwwroot/js/typebeat-core.js</c>.
/// A browser /play score lands on the SAME leaderboards as a desktop one, so a one-sided edit
/// would grade the same performance differently depending on where it was played.</para>
///
/// <para>Nothing is hardcoded twice: the Node harness runs the SHIPPED JS, and this test holds its
/// answers against the server's own <see cref="ScoringContract"/>, which is the code that will
/// grade a submitted score in production.</para>
/// </summary>
public class RankParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreRankHarness.cjs");

    [Test]
    public void TheBrowserGradesMatchTheServerRule()
    {
        var root = Harness();

        foreach (var pair in root.GetProperty("pairs").EnumerateArray())
        {
            double accuracy = pair.GetProperty("accuracy").GetDouble();
            double missed = pair.GetProperty("missed").GetDouble();
            string jsRank = pair.GetProperty("rank").GetString();

            Assert.That(jsRank, Is.EqualTo(ScoringContract.RankFromAccuracy(accuracy, missed)),
                $"accuracy {accuracy}, missedFraction {missed}");
        }
    }

    [Test]
    public void TheBrowserGradesFromCountsMatchTheServerRule()
    {
        var root = Harness();

        foreach (var entry in root.GetProperty("stats").EnumerateArray())
        {
            double accuracy = entry.GetProperty("accuracy").GetDouble();
            string jsRank = entry.GetProperty("rank").GetString();

            var statistics = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var property in entry.GetProperty("counts").EnumerateObject())
                statistics[property.Name] = property.Value.GetInt32();

            Assert.That(jsRank, Is.EqualTo(ScoringContract.RankFromStatistics(accuracy, statistics)),
                $"accuracy {accuracy} over {string.Join(",", statistics.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
    }
}
