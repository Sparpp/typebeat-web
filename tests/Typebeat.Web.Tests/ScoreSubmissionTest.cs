using Newtonsoft.Json.Linq;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// DB-free tests for the pure halves of <see cref="ScoreSubmission"/>, the pieces both submit routes
/// share: the ranked verdict of a submitted acronym and the <c>user_stats.hit_counts</c> merge.
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
