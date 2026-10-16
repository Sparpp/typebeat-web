using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// DB-free tests for the pure halves of <see cref="ScoreSubmission"/>, the pieces both submit routes
/// share: the ranked verdict of a submitted acronym, the claimed maximum held against the map's own
/// judged cells, and the <c>user_stats.hit_counts</c> merge.
/// </summary>
public class ScoreSubmissionTest
{
    [Test]
    public void ModRanked_RefusesUnknownAndAlwaysUnrankedAcronyms()
    {
        Assert.Multiple(() =>
        {
            // Every ranked acronym the client can send, the retired FT and the server's CL mark too.
            foreach (string acronym in new[] { "EZ", "NF", "HT", "DC", "HR", "SD", "DT", "NC", "FL", "LT", "RE", "FC", "GK", "MU", "FT", "CL", "RH" })
            {
                Assert.That(ScoreSubmission.ModRanked(acronym), Is.True, acronym);
                Assert.That(ScoreSubmission.ModRanked(acronym.ToLowerInvariant()), Is.True, acronym.ToLowerInvariant());
            }

            foreach (string acronym in UnrankedMods.ACRONYMS)
                Assert.That(ScoreSubmission.ModRanked(acronym), Is.False, acronym);

            // Unknown: an invented acronym, an osu mod this ruleset does not have, and Autoplay,
            // which the game never submits.
            foreach (string acronym in new[] { "ZZ", "HD", "AT" })
                Assert.That(ScoreSubmission.ModRanked(acronym), Is.False, acronym);

            // A blank acronym names no mod (the stored stack drops it), as before.
            Assert.That(ScoreSubmission.ModRanked(null), Is.True);
            Assert.That(ScoreSubmission.ModRanked("  "), Is.True);

            Assert.That(ScoreSubmission.StackRanked(["DT", "FL", "HR"]), Is.True);
            Assert.That(ScoreSubmission.StackRanked(["DT", "ZZ"]), Is.False, "one unknown acronym unranks the stack");
            Assert.That(ScoreSubmission.StackRanked([]), Is.True);
        });
    }

    [Test]
    public void KnownStatistics_DropsUnknownKeys_AndBoundsBonusByTheMaximum()
    {
        var (statistics, maximum, dropped) = ScoreSubmission.KnownStatistics(
            new Dictionary<string, int> { ["great"] = 10, ["large_bonus"] = 20_000, ["zz"] = 7 },
            new Dictionary<string, int> { ["great"] = 10, ["Great"] = 3 });

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Keys, Is.EquivalentTo(new[] { "great", "large_bonus" }));
            Assert.That(statistics["large_bonus"], Is.Zero, "no bonus in the map's maximum, so none may be claimed");
            Assert.That(maximum.Keys, Is.EquivalentTo(new[] { "great" }));
            Assert.That(dropped, Is.EqualTo(2), "zz from the statistics, Great from the maximum");
            Assert.That(ScoreSubmission.KnownStatistics(null, null).DroppedKeys, Is.Zero);
        });
    }

    [Test]
    public void KnownStatistics_IgnoresABonusTheClientAlsoClaimsAsMaximum()
    {
        var (statistics, maximum, dropped) = ScoreSubmission.KnownStatistics(
            new Dictionary<string, int> { ["great"] = 10, ["large_bonus"] = 20_000 },
            new Dictionary<string, int> { ["great"] = 10, ["large_bonus"] = 20_000 });

        Assert.Multiple(() =>
        {
            Assert.That(statistics["large_bonus"], Is.Zero, "a type!beat map has no bonus judgement");
            Assert.That(maximum.Keys, Is.EquivalentTo(new[] { "great" }));
            Assert.That(dropped, Is.EqualTo(1));
        });
    }

    [Test]
    public void MaximumMatchesMap_RefusesAClaimThatIsNotTheMapsCellCount()
    {
        Assert.Multiple(() =>
        {
            // The audit's case: ten Greats against a claimed ten-cell map, on a map of 240 cells.
            Assert.That(ScoreSubmission.MaximumMatchesMap(new Dictionary<string, int> { ["great"] = 10 }, 240), Is.False);
            Assert.That(ScoreSubmission.MaximumMatchesMap(new Dictionary<string, int> { ["great"] = 240 }, 240), Is.True);

            // Over-claiming is refused too: it would deflate completion, but it is not the map.
            Assert.That(ScoreSubmission.MaximumMatchesMap(new Dictionary<string, int> { ["great"] = 241 }, 240), Is.False);

            // Only accuracy-affecting results count, as the recompute reads them: the line objects'
            // ignore_hit and a combo-only key leave the count where it is.
            Assert.That(ScoreSubmission.MaximumMatchesMap(
                new Dictionary<string, int> { ["great"] = 240, ["ignore_hit"] = 12, ["combo_break"] = 3 }, 240), Is.True);

            // An empty or absent claim against a map with cells is not the map.
            Assert.That(ScoreSubmission.MaximumMatchesMap(new Dictionary<string, int>(), 240), Is.False);
            Assert.That(ScoreSubmission.MaximumMatchesMap(null, 240), Is.False);

            // Not counted (yet, or for this version of the map): accepted unchecked, as before.
            Assert.That(ScoreSubmission.MaximumMatchesMap(new Dictionary<string, int> { ["great"] = 10 }, null), Is.True);
        });
    }

    [Test]
    public void JudgedCells_CountsTheDefaultAndTheLiterateStreams()
    {
        Assert.Multiple(() =>
        {
            // Default: lower-cased, a hyphen becomes one word break, every other mark is gone.
            // "the bad cat sat" is 15 cells, spaces included.
            Assert.That(JudgedCells.CountDefault("The bad-cat sat."), Is.EqualTo(15));
            // Literate: every authored char, the hyphen and the full stop included.
            Assert.That(JudgedCells.CountLiterate("The bad-cat sat."), Is.EqualTo(16));

            // A hyphen run collapses to one space, and to none at either end of the line.
            Assert.That(JudgedCells.CountDefault("-a - b-"), Is.EqualTo(3));
            Assert.That(JudgedCells.CountLiterate("-a - b-"), Is.EqualTo(7));

            // A freestyle slot is a judged cell on both streams; a mark standing alone keeps both
            // authored spaces on the default stream.
            Assert.That(JudgedCells.CountDefault("go && go"), Is.EqualTo(8));
            Assert.That(JudgedCells.CountDefault("oh , no"), Is.EqualTo(6));
            Assert.That(JudgedCells.CountLiterate("oh , no"), Is.EqualTo(7));

            Assert.That(JudgedCells.CountDefault(""), Is.Zero);
            Assert.That(JudgedCells.CountLiterate(""), Is.Zero);
        });
    }

    [Test]
    public void MergeHitCounts_SumsKnownKeys_AndIgnoresTheRest()
    {
        string merged = ScoreSubmission.MergeHitCounts(
            """{"great":10,"miss":2}""",
            new Dictionary<string, int> { ["great"] = 5, ["ok"] = 3, ["miss"] = 0, ["meh"] = -4, ["zz"] = 1000 });

        var parsed = JObject.Parse(merged);

        Assert.Multiple(() =>
        {
            Assert.That((long)parsed["great"]!, Is.EqualTo(15));
            Assert.That((long)parsed["ok"]!, Is.EqualTo(3));
            Assert.That((long)parsed["miss"]!, Is.EqualTo(2), "a zero count adds nothing");
            Assert.That(parsed["meh"], Is.Null, "a negative count is tamper-shaped and ignored");
            Assert.That(parsed["zz"], Is.Null, "an unknown key never reaches the profile totals");
        });
    }

    [Test]
    public void MergeHitCounts_HoldsAtIntMax_InsteadOfWrappingNegative()
    {
        string merged = ScoreSubmission.MergeHitCounts(
            "{\"great\":" + (int.MaxValue - 1) + "}",
            new Dictionary<string, int> { ["great"] = int.MaxValue });

        Assert.That((long)JObject.Parse(merged)["great"]!, Is.EqualTo(int.MaxValue));
    }
}
