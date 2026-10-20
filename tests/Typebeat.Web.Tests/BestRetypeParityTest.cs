using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for PR 43's two engine rules in the browser scoring core, with no game checkout
/// needed: <c>Js/CoreBestRetypeHarness.cjs</c> drives the shipped JS and this asserts golden values.
/// The cross-repo half is <c>Typebeat.WireCompat.BestRetypeJudgementParityTest</c>, which replays the
/// same harness output through the game's own engine and replay scorer; every number below was read
/// off that comparison, so a drift here and a drift there are the same drift.
///
/// <para><b>BEST RETYPE JUDGEMENT</b> (extended CONFIG bit 13): retyping a backspaced cell keeps the
/// better of its grades, amending the cell's submitted result in place with no combo and no HP. The
/// fixture line is "ab cde" ("ab" over [1000, 2000], "cde" over [3000, 4000]); 'c' opens its
/// syllable, so a press at t is graded on t - 3000. <c>live</c> is the browser as /play runs it,
/// <c>legacy</c> the rule stubbed back to "the first award stands".</para>
///
/// <para><b>PAUSED-WORD PUNCTUATION</b>: a mark inside a paused word is timed within the stretch on its
/// own side of the rest, never inside the silence.</para>
/// </summary>
public class BestRetypeParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreBestRetypeHarness.cjs");

    private static JsonElement Arm(JsonElement root, string scenario, string arm) => root.GetProperty("retype").GetProperty("runs").GetProperty(scenario).GetProperty(arm);

    private static JsonElement Last(JsonElement arm)
    {
        var readings = arm.GetProperty("readings");
        return readings[readings.GetArrayLength() - 1];
    }

    private static Dictionary<string, int> Statistics(JsonElement arm)
        => arm.GetProperty("submitted").GetProperty("statistics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());

    private static long Total(JsonElement arm) => arm.GetProperty("submitted").GetProperty("totalScore").GetInt64();

    /// <summary>Cell 3 ('c'): the award and the delta it stands on at the end of the run.</summary>
    private static (string? tier, double delta) Opening(JsonElement arm)
    {
        var cell = Last(arm).GetProperty("judged")[0][3];
        return (cell.GetProperty("tier").GetString(), cell.GetProperty("delta").GetDouble());
    }

    [Test]
    public void AMehRetypedOnTheBeatBecomesAGreat()
    {
        var root = Harness();
        var live = Arm(root, "mehThenGreat", "live");
        var legacy = Arm(root, "mehThenGreat", "legacy");

        Assert.Multiple(() =>
        {
            Assert.That(Opening(live), Is.EqualTo(("Great", -150.0)));
            Assert.That(Statistics(live), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 6 }));
            Assert.That(Total(live), Is.EqualTo(1_000_000));

            Assert.That(Opening(legacy), Is.EqualTo(("Meh", -400.0)));
            Assert.That(Statistics(legacy), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 5, ["meh"] = 1 }));
            Assert.That(Total(legacy), Is.EqualTo(589765));

            // No second combo increment for the retype.
            Assert.That(Last(live).GetProperty("maxCombo").GetInt32(), Is.EqualTo(6));
            Assert.That(live.GetProperty("submitted").GetProperty("maxCombo").GetInt32(), Is.EqualTo(6));
        });
    }

    [Test]
    public void AWorseRetypeNeverTakesTheBetterAwardAway()
    {
        var live = Arm(Harness(), "greatStaysGreat", "live");

        Assert.Multiple(() =>
        {
            Assert.That(Opening(live), Is.EqualTo(("Great", -100.0)), "the Lagging retype at +700 left the Great alone");
            Assert.That(Last(live).GetProperty("counts").GetProperty("Lagging").GetInt32(), Is.Zero);
            Assert.That(Total(live), Is.EqualTo(1_000_000));
        });
    }

    [Test]
    public void RepeatedRetypesKeepTheBestAndAnEqualOneMovesNothing()
    {
        var live = Arm(Harness(), "repeatedRetypes", "live");
        var readings = live.GetProperty("readings");

        // Steps: Meh (-500), Ok (-300), Ok again (-250, equal), Great (-100), Great again (-50, equal).
        double DeltaAfter(double t, string op)
        {
            foreach (var reading in readings.EnumerateArray())
            {
                if (reading.GetProperty("op").GetString() == op && reading.GetProperty("t").GetDouble() == t)
                    return reading.GetProperty("judged")[0][3].GetProperty("delta").GetDouble();
            }

            throw new InvalidOperationException($"no {op} reading at {t}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(DeltaAfter(2500, "key"), Is.EqualTo(-500));
            Assert.That(DeltaAfter(2700, "key"), Is.EqualTo(-300));
            Assert.That(DeltaAfter(2750, "key"), Is.EqualTo(-300), "an equal Ok keeps the delta it was first awarded on");
            Assert.That(DeltaAfter(2900, "key"), Is.EqualTo(-100));
            Assert.That(DeltaAfter(2950, "key"), Is.EqualTo(-100), "an equal Great keeps the delta it was first awarded on");
            Assert.That(Opening(live), Is.EqualTo(("Great", -100.0)));
            Assert.That(Statistics(live), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 6 }));
        });
    }

    [Test]
    public void AnOffTimePressRetypedInsideTheWindowMovesTheEngineCountOnly()
    {
        var root = Harness();
        var live = Arm(root, "offTimeToMeh", "live");
        var legacy = Arm(root, "offTimeToMeh", "legacy");

        Assert.Multiple(() =>
        {
            Assert.That(Opening(live), Is.EqualTo(("Meh", -500.0)));
            Assert.That(Opening(legacy), Is.EqualTo(("Premature", -700.0)));
            Assert.That(Last(live).GetProperty("counts").GetProperty("Premature").GetInt32(), Is.Zero);
            Assert.That(Last(legacy).GetProperty("counts").GetProperty("Premature").GetInt32(), Is.EqualTo(1));

            // An off-time press already submits as meh, so the submitted account cannot move.
            Assert.That(Statistics(live), Is.EquivalentTo(Statistics(legacy)));
            Assert.That(Total(live), Is.EqualTo(Total(legacy)));
        });
    }

    [Test]
    public void ACorrectedCellKeepsItsCorrectionCapOnTheRetype()
    {
        var live = Arm(Harness(), "correctionCap", "live");

        Assert.Multiple(() =>
        {
            Assert.That(Opening(live), Is.EqualTo(("Ok", -50.0)), "a Great-timed retype of a corrected cell is awarded Ok");
            Assert.That(Statistics(live), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 5, ["ok"] = 1, ["combo_break"] = 1 }));
            Assert.That(Total(live), Is.EqualTo(655117));
        });
    }

    [Test]
    public void TwoCellsBackspacedTogetherAreEachUpgradedOnTheirOwn()
    {
        var root = Harness();
        var live = Arm(root, "rewind", "live");
        var cells = Last(live).GetProperty("judged")[0];

        Assert.Multiple(() =>
        {
            Assert.That(cells[3].GetProperty("tier").GetString(), Is.EqualTo("Great"));
            Assert.That(cells[4].GetProperty("tier").GetString(), Is.EqualTo("Great"));
            Assert.That(Total(live), Is.EqualTo(1_000_000));
            Assert.That(Total(Arm(root, "rewind", "legacy")), Is.EqualTo(354214));
        });
    }

    [Test]
    public void AnUpgradeMovesNeitherComboNorHealth()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            foreach (var scenario in root.GetProperty("retype").GetProperty("runs").EnumerateObject())
            {
                var live = scenario.Value.GetProperty("live").GetProperty("readings");
                var legacy = scenario.Value.GetProperty("legacy").GetProperty("readings");

                for (int i = 0; i < live.GetArrayLength(); i++)
                {
                    foreach (string field in new[] { "combo", "maxCombo", "processorCombo", "processorHighestCombo", "health" })
                        Assert.That(live[i].GetProperty(field).GetRawText(), Is.EqualTo(legacy[i].GetProperty(field).GetRawText()), $"{scenario.Name}[{i}]: {field}");
                }
            }
        });
    }

    [Test]
    public void PausedWordPunctuationStaysOutOfTheRest()
    {
        var paused = Harness().GetProperty("paused");
        var literate = paused.GetProperty("literate")[0];
        var plain = paused.GetProperty("default")[0];

        Assert.Multiple(() =>
        {
            // "well-known" rests [1500, 2000] after "well-": the hyphen sits between 'l' (1375) and 1500.
            Assert.That(literate[4].GetProperty("expected").GetString(), Is.EqualTo("-"));
            Assert.That(literate[4].GetProperty("target").GetDouble(), Is.EqualTo(1437.5));
            Assert.That(plain[4].GetProperty("expected").GetString(), Is.EqualTo(" "), "the default stream types the hyphen as a space");
            Assert.That(plain[4].GetProperty("target").GetDouble(), Is.EqualTo(1437.5));

            // "yes,no" rests [3400, 3600] after "yes": the comma opens the second stretch at 3600.
            Assert.That(literate[14].GetProperty("expected").GetString(), Is.EqualTo(","));
            Assert.That(literate[14].GetProperty("target").GetDouble(), Is.EqualTo(3600));

            // The letters are untouched by the rule.
            Assert.That(literate[3].GetProperty("target").GetDouble(), Is.EqualTo(1375));
            Assert.That(literate[5].GetProperty("target").GetDouble(), Is.EqualTo(2000));
            Assert.That(literate[15].GetProperty("target").GetDouble(), Is.EqualTo(3600));
        });
    }
}
