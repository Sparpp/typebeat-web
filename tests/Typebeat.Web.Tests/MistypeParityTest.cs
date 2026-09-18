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
            foreach (string name in new[] { "clean", "mistyped", "doubleMistyped", "mistypedAndMissed", "firstCellMistyped", "lastCellMistyped", "clusteredMistyped" })
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
    public void PpReadsTheBrowsersMistypeCountAndNoLongerPricesIt()
    {
        var root = Harness();

        var clean = PerformancePoints.CountNotes(Dict(root.GetProperty("clean"), "statistics"));
        var mistyped = PerformancePoints.CountNotes(Dict(root.GetProperty("mistyped"), "statistics"));

        Assert.Multiple(() =>
        {
            Assert.That(clean.Notes, Is.EqualTo(15));
            Assert.That(clean.Typos, Is.Zero, "no key means no mistypes, never a guess");

            // notes is the map's CELL count and must not move: letting keypresses into it would
            // hand a masher a bigger length bonus and a smaller combo denominator.
            Assert.That(mistyped.Notes, Is.EqualTo(15));
            Assert.That(mistyped.Misses, Is.Zero);
            Assert.That(mistyped.Typos, Is.EqualTo(7));
        });

        // THE PRICE NO LONGER MOVES, and that is the change rather than a weakened test:
        // PerformancePoints v22 deleted the typo term outright, so a wrong keypress the player
        // recovered from costs nothing. The COUNT still has to cross the wire and still has to be
        // read identically on both sides (everything above this line), because the surfaces that
        // display it read it and because an UNCORRECTED typo is still folded into the miss count,
        // which is priced as harshly as ever.
        double cleanPp = PerformancePoints.Compute(5, clean.Notes, clean.Notes, clean.Misses, 1.0, 15, [], clean.Typos);
        double mistypedPp = PerformancePoints.Compute(5, mistyped.Notes, mistyped.Notes, mistyped.Misses, 1.0, 15, [], mistyped.Typos);

        Assert.That(mistypedPp, Is.EqualTo(cleanPp), "the stat is carried and displayed; since v22 it is not priced");
    }

    /// <summary>
    /// A rejected wrong key breaks the SUBMITTED combo, exactly as the desktop client's score
    /// processor breaks it (backlog 73). Until then the browser reconstructed combo at the end of
    /// the play from the per-cell result stream, in which a rejected key leaves nothing behind, so
    /// it broke combo only on a missed CELL and a mistyped browser play out-scored the identical
    /// desktop performance on the shared leaderboard. The test that pinned that divergence is what
    /// this replaces.
    ///
    /// <para>The web test project cannot reference the ruleset assembly, so the expected values
    /// below are DERIVED BY HAND from the C# model and stated as literals. The model, all of it in
    /// <c>ScoreProcessor.ApplyResultInternal</c> / <c>updateScore</c> unless noted:</para>
    ///
    /// <list type="bullet">
    /// <item>A cell judged Perfect becomes <c>HitResult.Great</c>: <c>Combo++</c>, then
    /// <c>currentComboPortion += 300 * ComboAfterJudgement^0.5</c> (<c>GetComboScoreChange</c>
    /// weights by the judgement's MAX result, always Great = 300, and by the combo as it stands
    /// AFTER the increment).</item>
    /// <item>A rejected wrong key raises no judgement at all:
    /// <c>TypeBeatPlayfield.onWrongKeyRejected</c> sets <c>Combo.Value = 0</c> by hand and moves
    /// nothing else. It is only the LATER contributions, now weighted by a restarted combo, that
    /// make it cost anything.</item>
    /// <item>A cell that seals untyped becomes a Miss: <c>Combo = 0</c> and a contribution of
    /// <c>300 * 0^0.5 = 0</c>.</item>
    /// <item><c>max_combo</c> is <c>HighestCombo</c>, the running maximum of that combo.</item>
    /// <item>The denominator, <c>maximumComboPortion</c>, comes from the autoplay simulation: a
    /// Great on every one of the map's 15 cells, so combo 1..15, so
    /// <c>300 * Σ(i=1..15) √i = 300 * 40.469197 = 12140.758980</c>.</item>
    /// <item><c>total_score = round(500000 * acc * comboProgress + 500000 * acc^5 *
    /// accuracyProgress)</c> with <c>acc</c> the judged-only accuracy and <c>accuracyProgress</c>
    /// the judged cells over the map's 15. Every typed cell here is judged at delta 0, i.e. Perfect,
    /// so <c>acc</c> is 1 wherever nothing was missed.</item>
    /// </list>
    ///
    /// <para>Per run, "combo after each judgement" then the portion it sums to:</para>
    ///
    /// <list type="bullet">
    /// <item><c>clean</c>: 1..15. Portion 300*Σ(1..15)√i = 12140.758980, comboProgress 1, so the
    /// full 1000000.</item>
    /// <item><c>firstCellMistyped</c>: the one break lands on an already-zero combo, so the run is
    /// still 1..15 and the score is still exactly 1000000. A wrong key on the first cell costs
    /// combo nothing (it still costs pp, through the mistype count).</item>
    /// <item><c>mistyped</c> (and <c>doubleMistyped</c>, whose second key on each cell breaks an
    /// already-broken combo): a break before each of cells 1..7 leaves each of them at combo 1,
    /// then cells 8..15 run 2..9. Portion 300*(7 + Σ(2..9)√i) = 300*25.306001 = 7591.800158,
    /// comboProgress 0.625315120, total = round(500000*0.625315120 + 500000) = 812658.</item>
    /// <item><c>lastCellMistyped</c>: 1..14, break, then the last cell at combo 1. Portion
    /// 300*(Σ(1..14)√i + 1) = 300*37.596213 = 11278.863976, comboProgress 0.929008145,
    /// total = round(500000*0.929008145 + 500000) = 964504.</item>
    /// <item><c>clusteredMistyped</c>: 1..7, then five wrong keys in a row (ONE break, five
    /// mistypes), then cells 8..15 at 1..8. Portion 300*(Σ(1..7)√i + Σ(1..8)√i) = 300*29.783574 =
    /// 8935.072178, comboProgress 0.735956639, total = round(500000*0.735956639 + 500000) =
    /// 867978.</item>
    /// <item><c>mistypedAndMissed</c>: a break before each of cells 1..7, then cells 8..12 at 2..6,
    /// then three sealed misses contributing 0. Portion 300*(7 + Σ(2..6)√i) = 300*16.831822 =
    /// 5049.546627, comboProgress 0.415916883. Here acc = 12*300/(15*300) = 0.8 and
    /// accuracyProgress = 15/15 = 1 (a miss is still a judged cell), so
    /// total = round(500000*0.8*0.415916883 + 500000*0.8^5) = round(166366.75 + 163840) =
    /// 330207.</item>
    /// </list>
    /// </summary>
    [Test]
    public void WrongKeypressesBreakTheSubmittedComboLikeTheDesktopProcessor()
    {
        var root = Harness();

        (string Run, int MaxCombo, long TotalScore)[] expected =
        [
            ("clean", 15, 1_000_000),
            ("firstCellMistyped", 15, 1_000_000),
            ("mistyped", 9, 812_658),
            ("doubleMistyped", 9, 812_658),
            ("lastCellMistyped", 14, 964_504),
            ("clusteredMistyped", 8, 867_978),
            ("mistypedAndMissed", 6, 330_207),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (run, maxCombo, totalScore) in expected)
            {
                var play = root.GetProperty(run);

                Assert.That(play.GetProperty("maxCombo").GetInt32(), Is.EqualTo(maxCombo), $"{run} max_combo");
                Assert.That(play.GetProperty("totalScore").GetInt64(), Is.EqualTo(totalScore), $"{run} total_score");

                // Cross-check on a second, independently maintained account: the engine's own live
                // combo (what the /play HUD counts up) breaks in the same places, so the number the
                // player watched and the number submitted for them cannot drift apart.
                Assert.That(play.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(maxCombo), $"{run} live combo");
            }
        });
    }

    [Test]
    public void TheComboCostOfAWrongKeyIsItsPositionNotItsCount()
    {
        // Same statement as the table above, in the form that says why the old reconstruction could
        // never have been patched up from the mistype COUNT: seven single wrong keys and fourteen
        // doubled ones cost exactly the same combo, while five keys clustered on one cell cost less
        // than five spread over five cells, and one on the opening cell costs nothing.
        var root = Harness();

        long Score(string run) => root.GetProperty(run).GetProperty("totalScore").GetInt64();
        int Mistypes(string run) => Dict(root.GetProperty(run), "statistics").GetValueOrDefault(mistype_key);

        Assert.Multiple(() =>
        {
            Assert.That(Mistypes("mistyped"), Is.EqualTo(7));
            Assert.That(Mistypes("doubleMistyped"), Is.EqualTo(14));
            Assert.That(Score("doubleMistyped"), Is.EqualTo(Score("mistyped")), "twice the keys, the same breaks");

            Assert.That(Mistypes("clusteredMistyped"), Is.EqualTo(5));
            Assert.That(Score("clusteredMistyped"), Is.GreaterThan(Score("mistyped")), "five keys, one break");

            Assert.That(Mistypes("firstCellMistyped"), Is.EqualTo(1));
            Assert.That(Score("firstCellMistyped"), Is.EqualTo(Score("clean")), "nothing to break on cell one");

            Assert.That(Score("lastCellMistyped"), Is.LessThan(Score("clean")), "one key, a 14 combo gone");
        });
    }

    [Test]
    public void TheLoweredValuesStillPassTheServersBounds()
    {
        // Server side needs no change for any of this, and this is the proof: max_combo is bounded
        // ABOVE by the theoretical maximum and total_score ABOVE by the provable ceiling, so
        // submitting less of both is always inside the contract. Kept as a test because "lower is
        // safer" is an argument, not a guarantee.
        var root = Harness();

        Assert.Multiple(() =>
        {
            foreach (string name in new[] { "clean", "mistyped", "doubleMistyped", "mistypedAndMissed", "firstCellMistyped", "lastCellMistyped", "clusteredMistyped" })
            {
                var run = root.GetProperty(name);
                var recomputed = Recompute(run);

                Assert.That(recomputed.StatisticsValid, Is.True, name);
                Assert.That(ScoringContract.TotalScoreWithinBounds(run.GetProperty("totalScore").GetInt64(), recomputed), Is.True, name);
            }

            // The ceiling is comboProgress = 1, which only a break-free run reaches; every run with
            // a break now sits strictly under it, where before the fix it sat exactly on it.
            var mistyped = root.GetProperty("mistyped");
            Assert.That(mistyped.GetProperty("totalScore").GetInt64(), Is.LessThan(Recompute(mistyped).TotalScoreCeiling));
            Assert.That(root.GetProperty("clean").GetProperty("totalScore").GetInt64(), Is.EqualTo(Recompute(root.GetProperty("clean")).TotalScoreCeiling));
        });
    }
}
