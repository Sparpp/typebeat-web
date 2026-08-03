using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the MISTYPE stat (backlog 72) across the browser/server seam.
///
/// <para>A wrong keypress used to leave no trace in a submitted score: the desktop client rejected
/// it without raising a judgement, so <c>statistics</c> carried great/ok/meh/miss only and a play
/// full of stumbles recomputed to a spotless accuracy. It is now persisted as its own
/// <c>combo_break</c> key. Because /play scores land on the SAME leaderboards as desktop ones, the
/// hand-written <c>wwwroot/js/typebeat-core.js</c> has to account it identically, or a browser play
/// and a desktop play of the same performance submit different dictionaries.</para>
///
/// <para>The strong assertion here is that nothing is hardcoded twice: the Node harness runs real
/// plays (with and without wrong keys) through the SHIPPED JS, and this test feeds the dictionaries
/// it emits straight into the server's own <see cref="ScoringContract"/> and
/// <see cref="PerformancePoints"/>, which are the code that will judge them in production.</para>
/// </summary>
public class MistypeParityTest
{
    private const string mistype_key = "combo_break";

    private static JsonElement Harness() => JsHarness.Run("CoreMistypeHarness.cjs");

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static ScoringContract.Recomputed Recompute(JsonElement run)
        => ScoringContract.Recompute(Dict(run, "statistics"), Dict(run, "maximumStatistics"), run.GetProperty("maxCombo").GetInt32());

    [Test]
    public void TheBrowserSubmitsWrongKeypressesUnderTheKeyTheServerReads()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // A clean play carries NO key at all, exactly like a score from before the stat existed
            // (both the desktop client and this engine omit zero-valued entries).
            Assert.That(Dict(root.GetProperty("clean"), "statistics"), Does.Not.ContainKey(mistype_key));

            Assert.That(Dict(root.GetProperty("mistyped"), "statistics")[mistype_key], Is.EqualTo(7));

            // Per KEYPRESS, not per cell: two wrong keys on the same cell are two mistypes.
            Assert.That(Dict(root.GetProperty("doubleMistyped"), "statistics")[mistype_key], Is.EqualTo(14));

            // Mistypes and misses are independent; neither absorbs the other.
            var both = Dict(root.GetProperty("mistypedAndMissed"), "statistics");
            Assert.That(both[mistype_key], Is.EqualTo(7));
            Assert.That(both["miss"], Is.EqualTo(3));

            // maximum_statistics stays one great per cell in every run: mistypes must never inflate
            // the denominator of accuracy, completion or pp.
            foreach (string name in new[] { "clean", "mistyped", "doubleMistyped", "mistypedAndMissed" })
            {
                Assert.That(Dict(root.GetProperty(name), "maximumStatistics"),
                    Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 15 }), name);
            }
        });
    }

    [Test]
    public void AHeavilyMistypedPlayStillRanksAndRecomputesToTheCleanValues()
    {
        // LANDMINE 1: StatisticsValid fails when accuracy-affecting judged counts exceed
        // maximum_statistics. combo_break has no counterpart there, so treating it as a judgement
        // would unrank every mistyped play. It must stay outside that comparison.
        var root = Harness();

        var clean = Recompute(root.GetProperty("clean"));
        var mistyped = Recompute(root.GetProperty("mistyped"));
        var doubleMistyped = Recompute(root.GetProperty("doubleMistyped"));

        Assert.Multiple(() =>
        {
            Assert.That(mistyped.StatisticsValid, Is.True, "a mistyped play must still be rankable");
            Assert.That(doubleMistyped.StatisticsValid, Is.True);

            // The headline guarantee: same cells typed, same numbers. Accuracy stays the timing
            // quality of the cells that were typed, completion/rank stay cells over cells, so an
            // old score and a new one still mean the same thing and SS survives a stumble.
            Assert.That(mistyped.Accuracy, Is.EqualTo(clean.Accuracy));
            Assert.That(mistyped.Completion, Is.EqualTo(clean.Completion));
            Assert.That(mistyped.Rank, Is.EqualTo("X"));
            Assert.That(doubleMistyped.Accuracy, Is.EqualTo(clean.Accuracy));
            Assert.That(doubleMistyped.Rank, Is.EqualTo("X"));
        });
    }

    [Test]
    public void StrippingTheMistypeKeyChangesNothingTheContractComputes()
    {
        // The same proof stated as an equivalence, and simultaneously the OLD-CLIENT guarantee: a
        // submission that omits the key entirely (every client shipped before backlog 72) must
        // recompute to exactly what one carrying it does.
        var root = Harness();

        foreach (string name in new[] { "mistyped", "doubleMistyped", "mistypedAndMissed" })
        {
            var run = root.GetProperty(name);
            var withKey = Dict(run, "statistics");
            var withoutKey = new Dictionary<string, int>(withKey, StringComparer.Ordinal);
            withoutKey.Remove(mistype_key);

            var maximums = Dict(run, "maximumStatistics");
            int maxCombo = run.GetProperty("maxCombo").GetInt32();

            var carried = ScoringContract.Recompute(withKey, maximums, maxCombo);
            var omitted = ScoringContract.Recompute(withoutKey, maximums, maxCombo);

            Assert.That(carried, Is.EqualTo(omitted), name);
        }
    }

    [Test]
    public void PpReadsTheBrowsersMistypeCountAndPricesItOnlyThroughCleanliness()
    {
        var root = Harness();

        var clean = PerformancePoints.CountNotes(Dict(root.GetProperty("clean"), "statistics"));
        var mistyped = PerformancePoints.CountNotes(Dict(root.GetProperty("mistyped"), "statistics"));

        Assert.Multiple(() =>
        {
            Assert.That(clean.Notes, Is.EqualTo(15));
            Assert.That(clean.Mistypes, Is.Zero, "no key means no mistypes, never a guess");

            // notes is the map's CELL count and must not move: letting keypresses into it would
            // hand a masher a bigger length bonus and a smaller combo denominator.
            Assert.That(mistyped.Notes, Is.EqualTo(15));
            Assert.That(mistyped.Misses, Is.Zero);
            Assert.That(mistyped.Mistypes, Is.EqualTo(7));
        });

        double cleanPp = PerformancePoints.Compute(5, clean.Notes, clean.Misses, 1.0, 15, [], clean.Mistypes);
        double mistypedPp = PerformancePoints.Compute(5, mistyped.Notes, mistyped.Misses, 1.0, 15, [], mistyped.Mistypes);

        Assert.That(mistypedPp, Is.LessThan(cleanPp), "mistyping must cost pp; that is the point of the stat");
    }

    /// <summary>
    /// KNOWN DIVERGENCE, pre-dating backlog 72 and deliberately left alone by it.
    ///
    /// <para>The desktop client breaks the score processor's combo on every rejected key, so both
    /// <c>max_combo</c> and the combo portion of <c>total_score</c> fall. The browser's
    /// <c>computeScore</c> instead RECONSTRUCTS combo at the end of the play from the per-cell
    /// result stream, in which a rejected key leaves nothing, so it breaks combo only on a missed
    /// CELL. The two therefore submit different <c>max_combo</c> and <c>total_score</c> for the same
    /// mistyped performance, on shared leaderboards, in the browser's favour.</para>
    ///
    /// <para>Fixing it changes what browser plays score, which is a leaderboard decision of the
    /// same weight as the mistype decision itself, so it is surfaced rather than taken here. This
    /// test pins the CURRENT behaviour so a future fix fails loudly instead of quietly: when it
    /// does, delete it and rewrite this note.</para>
    /// </summary>
    [Test]
    public void KnownDivergenceBrowserComboIgnoresWrongKeyBreaks()
    {
        var root = Harness();
        var mistyped = root.GetProperty("mistyped");

        Assert.Multiple(() =>
        {
            // The engine's own live combo DOES break on a wrong key, matching the desktop: seven
            // wrong keys spread one per cell over the first seven cells leave a longest clean run
            // of the remaining eight.
            Assert.That(mistyped.GetProperty("engineMistypes").GetInt32(), Is.EqualTo(7));

            // ...but what is SUBMITTED is the reconstruction, which sees a flawless 15.
            Assert.That(mistyped.GetProperty("maxCombo").GetInt32(), Is.EqualTo(15));
            Assert.That(mistyped.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000));
        });
    }
}
