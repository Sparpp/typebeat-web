using System.Linq;
using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the CORRECTION CAP across the browser/server seam (backlog 210). A cell that
/// held a wrong character before it was ever judged resolves at min(the retype's own tier, Ok), so
/// perfect play strictly beats corrected play per cell. Without the browser half, a fixed typo goes
/// on scoring the full 300 in <c>/play</c> while the desktop scores it 100, on the SAME
/// leaderboards.
///
/// <para><b>Why the cap exists at all.</b> Before it, a word typed wrong and fixed could score
/// bit-identically to a word typed right: the mistype travels as an accuracy-inert statistic
/// (<c>combo_break</c>), and backlog 109 DEFERS the spoiled cell's result so the retype was graded
/// purely on its own timing. A player quick enough to fix inside the Great window paid nothing.
/// <see cref="TheCapMovesAccuracyAndTotalScoreAndNothingElse"/> is that claim inverted on a whole
/// submitted account, and the ordering it produces is clean 300 > corrected 100 > unfixed typo 50
/// > miss 0.</para>
///
/// <para><b>Shaped like <see cref="ComboRestoreParityTest"/>, not like the transcribed-literal
/// guards</b>, and for the reason that file records: the divergence this protects against is a
/// BEHAVIOUR one, so a golden literal transcribed from a game that did not cap yet would keep
/// passing while the browser paid the full tier. The harness therefore drives the browser through
/// the wrong/fix SEQUENCES themselves and reports what the engine announced, counted and
/// submitted.</para>
///
/// <para><b>There is only one arm here.</b> The game pins each shape twice, once under
/// <c>CorrectionCreditRule.Capped</c> and once under <c>Full</c> (the rule every row stored before
/// backlog 210 was played under). The browser has no counterpart to <c>Full</c>: it plays live,
/// writes no replay frames and re-derives no stored row, which is the same reason it has no
/// <c>ComboRestoreRule.Never</c> and no <c>OffTimeRule.BreaksCombo</c>. The contrast is drawn
/// against a CLEAN cell struck at the identical moment instead, which is the comparison the game's
/// two arms draw.</para>
///
/// <para>The sequences and the tier expectations are the game's own pins, transcribed from
/// <c>NonVisual/CorrectionCreditTest.cs</c>. The press TIMES are not, and cannot be: that fixture
/// drives a bare <c>TypingEngine</c> judged on each cell's own point target, while the browser
/// judges a cell against its SYLLABLE's sung span, so every offset below is measured from the end
/// of cell 3's span (5000) rather than from its target. The harness header records the spans.</para>
/// </summary>
public class CorrectionCreditParityTest
{
    private const string mistype_key = "combo_break";

    private static JsonElement Harness() => JsHarness.Run("CoreCorrectionCreditHarness.cjs");

    private static string Str(JsonElement run, string key) => run.GetProperty(key).GetString()!;

    private static int Int(JsonElement run, string key) => run.GetProperty(key).GetInt32();

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static ScoringContract.Recomputed Recompute(JsonElement run)
        => ScoringContract.Recompute(Dict(run, "statistics"), Dict(run, "maximumStatistics"), Int(run, "maxCombo"));

    /// <summary>
    /// The rule, as the MINIMUM it is written as: min(the retype's own tier, Ok). A fix inside the
    /// Great window comes down to Ok, one already inside Ok stays exactly where it is, and one that
    /// only made the Meh window is left alone, because it is already below the cap and demoting it
    /// further would price a slow fix as though it were something else.
    ///
    /// <para>Each offset is checked against a CLEAN cell struck at the same instant, which is what
    /// makes this a cap rather than a table of literals: the uncapped column is the ladder the
    /// browser would award with no typo in the way, and the capped column is what the same press is
    /// worth once the cell has held a wrong character.</para>
    ///
    /// <para><b>Only Great moves</b>, and that is the whole of the min(): of the tiers a correct
    /// press can be classified as, Great is the only one above Ok. Premature and Lagging are off the
    /// ladder entirely, so <c>ladderOffTime</c> is left to the off-time rule (which already answers
    /// below the cap, mapping to a <c>meh</c>) and the two rules compose without either knowing
    /// about the other.</para>
    /// </summary>
    [Test]
    public void TheCapIsAMinimumOverTheLadder()
    {
        var root = Harness();

        (string Case, int Offset, string Uncapped, string Capped)[] expected =
        [
            ("ladderDeadOn", 0, "Great", "Ok"),
            ("ladderGreat", 200, "Great", "Ok"),
            ("ladderOk", 700, "Ok", "Ok"),
            ("ladderMeh", 1500, "Meh", "Meh"),
            ("ladderOffTime", 2500, "Lagging", "Lagging"),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (name, offset, uncapped, capped) in expected)
            {
                var run = root.GetProperty(name);

                Assert.That(Int(run, "offsetMs"), Is.EqualTo(offset), name);
                Assert.That(Str(run, "uncapped"), Is.EqualTo(uncapped), $"{name}: the ladder with no typo in the way");
                Assert.That(Str(run, "capped"), Is.EqualTo(capped), $"{name}: the same press once the cell has held wrong");

                // The DELTA is untouched, which is why the cap costs accuracy and nothing else: the
                // sync tint and the live sync percent read this field back, and a fix struck dead on
                // target really was struck dead on target. Only what the cell is WORTH moved.
                Assert.That(Int(run, "cappedDelta"), Is.EqualTo(offset), $"{name}: the cap does not move the delta");
                Assert.That(Int(run, "cappedDelta"), Is.EqualTo(Int(run, "uncappedDelta")), name);
            }
        });
    }

    /// <summary>
    /// The cap moves the TIER, so what the stage announces and what the cell stores are the same
    /// thing by construction rather than by two places agreeing. Capping the RESULT instead would
    /// show the player a Great and submit an Ok, and would reach nothing at all on the inert-retype
    /// path, which announces a tier without applying any result.
    /// </summary>
    [Test]
    public void TheAnnouncedJudgementIsTheStoredOne()
    {
        var run = Harness().GetProperty("announcedIsStored");
        var counts = Dict(run, "counts");

        Assert.Multiple(() =>
        {
            Assert.That(Str(run, "announced"), Is.EqualTo("Ok"), "the fix was struck dead inside the span and is still an Ok");
            Assert.That(Str(run, "cellJudgeType"), Is.EqualTo("Ok"), "and the cell stores what was announced");
            Assert.That(Str(run, "cellState"), Is.EqualTo("correct"), "a capped cell is still a correct cell on screen");
            Assert.That(Int(run, "judgedDelta"), Is.Zero);

            Assert.That(counts["Great"], Is.EqualTo(3), "the three clean cells, and not the fixed one");
            Assert.That(counts["Ok"], Is.EqualTo(1));
            Assert.That(counts["WrongChar"], Is.EqualTo(1), "the keypress is still counted, as it always was");
        });
    }

    /// <summary>
    /// The cap is a STATE and not a counter: one flag on the cell, so a wrong/fix/wrong/fix cycle
    /// caps exactly once and can never demote below the min() however many times the player goes
    /// round. The second fix is a scoring-inert retype (the cell was already judged), and the tier it
    /// ANNOUNCES has to be the capped Ok the cell actually stored, or the player is shown a judgement
    /// their score does not carry.
    /// </summary>
    [Test]
    public void RepeatedWrongFixCyclesCapExactlyOnce()
    {
        var root = Harness();

        var cycles = root.GetProperty("repeatedCyclesCapOnce");
        var twoWrong = root.GetProperty("twoWrongCharactersOnOneCell");

        Assert.Multiple(() =>
        {
            Assert.That(Str(cycles, "announcedAfterFirstFix"), Is.EqualTo("Ok"));
            Assert.That(Str(cycles, "announced"), Is.EqualTo("Ok"), "the inert retype re-derives the capped award");
            Assert.That(Dict(cycles, "counts")["Ok"], Is.EqualTo(1), "one cap, one count, however many cycles");
            Assert.That(Dict(cycles, "counts")["Great"], Is.EqualTo(3));
            Assert.That(Int(cycles, "mistypes"), Is.EqualTo(2), "the keypresses are counted twice, as they always were");

            // Wrong, backspace, wrong AGAIN, then fix: still one cap. There is nothing here for a
            // counter to double, which is the point of holding the rule as a flag on the cell.
            Assert.That(Str(twoWrong, "announced"), Is.EqualTo("Ok"));
            Assert.That(Dict(twoWrong, "counts")["Ok"], Is.EqualTo(1));
            Assert.That(Dict(twoWrong, "counts")["Great"], Is.EqualTo(3));
            Assert.That(Int(twoWrong, "mistypes"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The residue the cap's framing leaves, pinned as the documented behaviour it is. A cell typed
    /// CORRECTLY, backspaced into, spoiled and then retyped keeps its ORIGINAL clean judgement: the
    /// flag is set only while the cell is still unjudged, the retype is inert, and a cell takes only
    /// its first result. It is the right answer and not merely the reachable one: the player did type
    /// that character right, first time, at that timing.
    /// </summary>
    [Test]
    public void ACellJudgedCleanBeforeItEverHeldWrongKeepsItsCleanJudgement()
    {
        var run = Harness().GetProperty("cleanBeforeWrongKeepsItsCleanJudgement");
        var counts = Dict(run, "counts");

        Assert.Multiple(() =>
        {
            Assert.That(Str(run, "announcedWhenClean"), Is.EqualTo("Great"), "the fixture only means anything if the cell was clean first");
            Assert.That(Str(run, "announced"), Is.EqualTo("Great"), "the clean judgement stands");
            Assert.That(counts["Great"], Is.EqualTo(4));
            Assert.That(counts.GetValueOrDefault("Ok"), Is.Zero);
        });
    }

    /// <summary>
    /// Backlog 140 is untouched: the streak the wrong keypress broke is still resumed by the fix, and
    /// the resume still lands BEFORE the retype is judged, so the capped press is priced at the
    /// restored run. The cap costs accuracy; it does not quietly cost combo as well. Both accounts
    /// are checked, because the class of bug lives in them disagreeing.
    /// </summary>
    [Test]
    public void TheCapLeavesTheComboRestoreAlone()
    {
        var run = Harness().GetProperty("comboRestoreIsUntouched");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("restored").EnumerateArray().Select(e => e.GetInt32()), Is.EqualTo(new[] { 3 }));
            Assert.That(Int(run, "combo"), Is.EqualTo(6), "3 restored + 2 earned since + the fix itself");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(6));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(6), "and the SUBMITTED account agrees");
            Assert.That(Int(run, "processorCombo"), Is.EqualTo(6));
            Assert.That(Str(run, "announced"), Is.EqualTo("Ok"), "the capped tier is announced at the restored streak, not instead of it");
        });
    }

    /// <summary>
    /// The reach of the cap, on three whole SUBMITTED accounts over the same eight cells with every
    /// press dead inside its own span, so the only thing separating them is what happens to cell 3.
    /// <c>max_combo</c>, the miss count, the mistype count, completion and rank are identical between
    /// the clean run and the fixed one, because a capped cell is still a hit that extends the run and
    /// still counts as typed. Accuracy and total_score are the only two that move.
    ///
    /// <para>And the ordering the cap is chosen to produce, read off a real run rather than off the
    /// weights: clean (300 a cell) beats corrected (100) beats an unfixed typo (0 since backlog 213,
    /// where it was 50 when the cap landed, and which also costs completion, rank and the streak).
    /// That is "perfect play beats corrected play beats an unfixed
    /// typo" as three accounts, and backlog 213 widened the last gap rather than narrowing it: the
    /// cell the player never fixed now pays its whole 300, so going back for a typo recovers a full
    /// miss's worth of accuracy where it used to recover 250 of 300.</para>
    ///
    /// <para>The literals are the arithmetic of the combo portion and the accuracy portion, which is
    /// worth stating because the fixed run's combo multiset is EXACTLY the clean run's (the restore
    /// puts back the streak of 3, so the fix lands at 4 and the line runs out 5..8, against the clean
    /// 1..8). So <c>comboProgress</c> is 1 for both and the whole difference is accuracy:
    /// (7·300 + 100)/2400 = 0.916667 against a flat 1, and
    /// round(500000·0.916667·1 + 500000·0.916667^5) = 781947 against 1000000.</para>
    /// </summary>
    [Test]
    public void TheCapMovesAccuracyAndTotalScoreAndNothingElse()
    {
        var root = Harness();

        var clean = root.GetProperty("accountClean");
        var fixedRun = root.GetProperty("accountFixed");
        var unfixed = root.GetProperty("accountUnfixed");

        Assert.Multiple(() =>
        {
            // The clean run: eight cells struck inside their spans, so a perfect account.
            Assert.That(Dict(clean, "statistics"), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 8 }));
            Assert.That(clean.GetProperty("accuracy").GetDouble(), Is.EqualTo(1).Within(1e-12));
            Assert.That(clean.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000));

            // The fixed run: one Great becomes an Ok, and that is the whole of the difference.
            Assert.That(Dict(fixedRun, "statistics"), Is.EquivalentTo(new Dictionary<string, int>
            {
                ["great"] = 7, ["ok"] = 1, [mistype_key] = 1
            }));
            Assert.That(fixedRun.GetProperty("accuracy").GetDouble(), Is.EqualTo((7 * 300 + 100) / 2400.0).Within(1e-12));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(), Is.EqualTo(781_947));

            // What the cap does NOT move, stated as equalities rather than as a claim.
            Assert.That(Int(fixedRun, "maxCombo"), Is.EqualTo(Int(clean, "maxCombo")));
            Assert.That(Int(fixedRun, "maxCombo"), Is.EqualTo(8), "the fix restores the run, so the combo multiset is the clean one's");
            Assert.That(Int(fixedRun, "engineMaxCombo"), Is.EqualTo(Int(clean, "engineMaxCombo")), "and the HUD combo agrees");
            Assert.That(Dict(fixedRun, "statistics"), Does.Not.ContainKey("miss"));
            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(clean.GetProperty("completion").GetDouble()));
            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(1).Within(1e-12), "an Ok counts as typed exactly as a Great does");
            Assert.That(Str(fixedRun, "rank"), Is.EqualTo(Str(clean, "rank")));
            Assert.That(Str(fixedRun, "rank"), Is.EqualTo("X"));
            Assert.That(Dict(fixedRun, "maximumStatistics"), Is.EquivalentTo(Dict(clean, "maximumStatistics")));

            // ...and the two that do.
            Assert.That(fixedRun.GetProperty("accuracy").GetDouble(), Is.LessThan(clean.GetProperty("accuracy").GetDouble()));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(), Is.LessThan(clean.GetProperty("totalScore").GetInt64()));

            // The ordering, on the run that leaves the typo standing: it is worth less than the fix
            // on accuracy AND loses completion, rank and the streak, which the fix keeps. So going
            // back for a typo is still strictly the right play, by a narrower margin than before.
            Assert.That(Dict(unfixed, "statistics")["good"], Is.EqualTo(1), "the unfixed typo takes its own key, which backlog 213 did not move");
            Assert.That(unfixed.GetProperty("accuracy").GetDouble(), Is.EqualTo((7 * 300 + 0) / 2400.0).Within(1e-12));
            Assert.That(unfixed.GetProperty("accuracy").GetDouble(), Is.LessThan(fixedRun.GetProperty("accuracy").GetDouble()));

            // BACKLOG 213 MOVED THIS ACCOUNT AND ONLY THIS ACCOUNT. Its cell used to pay a Meh's 50
            // of 300; it now pays a miss's 0, so the run scores strictly below its backlog-210-era
            // value by exactly that 50, and the clean and fixed runs (which carry no `good` cell)
            // are priced bit-identically to what they were.
            Assert.That(unfixed.GetProperty("accuracy").GetDouble(), Is.LessThan((7 * 300 + 50) / 2400.0),
                "an unfixed typo is worth strictly less than it was under the backlog-124 weight");
            Assert.That((7 * 300 + 50) / 2400.0 - unfixed.GetProperty("accuracy").GetDouble(),
                Is.EqualTo(50 / 2400.0).Within(1e-12), "and less by exactly the Meh credit the fold took away");
            Assert.That(unfixed.GetProperty("totalScore").GetInt64(), Is.LessThan(fixedRun.GetProperty("totalScore").GetInt64()));
            Assert.That(unfixed.GetProperty("completion").GetDouble(), Is.EqualTo(7 / 8.0).Within(1e-12));
            Assert.That(Str(unfixed, "rank"), Is.EqualTo("B"));

            // WHAT BACKLOG 213 LEAVES ALONE, on the same account. THE WIRE DOES NOT MOVE: the
            // browser still seals the cell under its own `good` key and never as a `miss`, which is
            // what keeps old rows comparable with new ones and lets every consumer do the folding.
            Assert.That(Dict(unfixed, "statistics"), Does.Not.ContainKey("miss"),
                "the fold is on the READING, not on what the browser submits");

            // COMPLETION and RANK are the values backlog 126 gave them and are untouched here (both
            // asserted above), and so is max_combo: the typo broke the streak when its keypress
            // landed, and leaving the cell wrong restores nothing, exactly as before the fold.
            Assert.That(Int(unfixed, "maxCombo"), Is.LessThan(Int(clean, "maxCombo")));
            Assert.That(Int(unfixed, "maxCombo"), Is.LessThan(Int(fixedRun, "maxCombo")),
                "which is the streak the fix recovers and this run does not");

            // ...and the server recomputes each of the three off the browser's own dictionaries, so a
            // capped cell is not something the submission path has to be taught about.
            foreach (var run in new[] { clean, fixedRun, unfixed })
            {
                var recomputed = Recompute(run);

                Assert.That(recomputed.StatisticsValid, Is.True);
                Assert.That(recomputed.Accuracy, Is.EqualTo(run.GetProperty("accuracy").GetDouble()).Within(1e-12));
                Assert.That(recomputed.Completion, Is.EqualTo(run.GetProperty("completion").GetDouble()).Within(1e-12));
                Assert.That(recomputed.Rank, Is.EqualTo(Str(run, "rank")));
            }
        });
    }
}
