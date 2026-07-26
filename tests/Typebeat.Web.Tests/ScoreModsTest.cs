namespace Typebeat.Web.Tests;

/// <summary>
/// The score-display badge model (ScoreMods.Parse): reading acronyms AND the track rate out of the
/// stored mods jsonb. Every rate mod must show a rate, including on the historic rows that carry no
/// settings at all, or a "DT" on a board is ambiguous between a 1.01x nudge and a 2.00x sprint.
/// </summary>
public class ScoreModsTest
{
    [Test]
    public void Parse_ReadsTheSubmittedRate()
    {
        var mods = ScoreMods.Parse("""[{"acronym":"DT","settings":{"speed_change":1.75}}]""");

        Assert.That(mods, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(mods[0].Acronym, Is.EqualTo("DT"));
            Assert.That(mods[0].Rate, Is.EqualTo(1.75).Within(1e-12));
            Assert.That(mods[0].RateLabel, Is.EqualTo("1.75x"), "the game renders the rate as {rate:N2}x");
            Assert.That(mods[0].Title, Is.EqualTo("Double Time 1.75x"));
            Assert.That(mods[0].CategoryClass, Is.EqualTo("increase"));
        });
    }

    [Test]
    public void Parse_AbsentRate_ReadsAsTheClientDefault()
    {
        // Historic rows (submitted before the rate was ranked) carry no settings and cannot be
        // backfilled. Under the old rules a ranked bare DT could ONLY have been the default 1.50x,
        // so the default is not a guess; it renders like any other rate, unmarked.
        var mods = ScoreMods.Parse("""[{"acronym":"DT"},{"acronym":"HT"},{"acronym":"NC"}]""");

        Assert.Multiple(() =>
        {
            Assert.That(mods[0].RateLabel, Is.EqualTo("1.50x"));
            Assert.That(mods[1].RateLabel, Is.EqualTo("0.75x"));
            Assert.That(mods[1].Title, Is.EqualTo("Half Time 0.75x"));
            Assert.That(mods[2].RateLabel, Is.EqualTo("1.50x"));
        });
    }

    [Test]
    public void Parse_NonRateMods_CarryNoRate()
    {
        var mods = ScoreMods.Parse("""[{"acronym":"FL"},{"acronym":"LT","settings":{"speed_change":2.0}}]""");

        Assert.Multiple(() =>
        {
            Assert.That(mods[0].RateLabel, Is.Null);
            Assert.That(mods[0].Title, Is.EqualTo("Flashlight"));
            Assert.That(mods[0].RateIntensity, Is.EqualTo(0));

            // speed_change means nothing on a mod with no rate, even if one somehow got stored.
            Assert.That(mods[1].RateLabel, Is.Null);
        });
    }

    [Test]
    public void Parse_SnapsAndClampsTheStoredRate()
    {
        var mods = ScoreMods.Parse(
            """[{"acronym":"DT","settings":{"speed_change":40}},{"acronym":"HT","settings":{"speed_change":0.01}},{"acronym":"NC","settings":{"speed_change":1.756}}]""");

        Assert.Multiple(() =>
        {
            Assert.That(mods[0].RateLabel, Is.EqualTo("2.00x"), "beyond the slider's top");
            Assert.That(mods[1].RateLabel, Is.EqualTo("0.50x"), "below the slider's floor");
            Assert.That(mods[2].RateLabel, Is.EqualTo("1.76x"), "snapped to the slider's 0.01 precision");
        });
    }

    [Test]
    public void RateIntensity_ScalesWithHowFarTheRateIsPushed()
    {
        // 0 at the no-mod end of each slider, 1 at the far end; the badge's rate pill deepens with it.
        Assert.Multiple(() =>
        {
            Assert.That(ScoreMods.Parse("""[{"acronym":"DT","settings":{"speed_change":2.0}}]""")[0].RateIntensity,
                Is.EqualTo(1.0).Within(1e-9));
            Assert.That(ScoreMods.Parse("""[{"acronym":"DT","settings":{"speed_change":1.5}}]""")[0].RateIntensity,
                Is.EqualTo(0.5).Within(1e-9));
            Assert.That(ScoreMods.Parse("""[{"acronym":"DT","settings":{"speed_change":1.01}}]""")[0].RateIntensity,
                Is.EqualTo(0.01).Within(1e-9));
            Assert.That(ScoreMods.Parse("""[{"acronym":"HT","settings":{"speed_change":0.5}}]""")[0].RateIntensity,
                Is.EqualTo(1.0).Within(1e-9));
            Assert.That(ScoreMods.Parse("""[{"acronym":"HT","settings":{"speed_change":0.75}}]""")[0].RateIntensity,
                Is.EqualTo(0.5).Within(1e-9));

            // Emitted as a CSS custom-property value, invariant-formatted.
            Assert.That(ScoreMods.Parse("""[{"acronym":"HT","settings":{"speed_change":0.62}}]""")[0].RateIntensityCss,
                Is.EqualTo("0.76"));
        });
    }

    [Test]
    public void Parse_SurvivesJunk()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScoreMods.Parse(null), Is.Empty);
            Assert.That(ScoreMods.Parse(""), Is.Empty);
            Assert.That(ScoreMods.Parse("[]"), Is.Empty);
            Assert.That(ScoreMods.Parse("not json at all"), Is.Empty, "a score display must never 500 on one bad row");
            Assert.That(ScoreMods.Parse("""[{"acronym":null},{"nothing":1},{"acronym":"  "}]"""), Is.Empty);

            // A non-numeric rate falls back to the default rather than throwing.
            Assert.That(ScoreMods.Parse("""[{"acronym":"DT","settings":{"speed_change":"fast"}}]""")[0].RateLabel,
                Is.EqualTo("1.50x"));

            // An acronym the site does not know still renders a neutral badge.
            var unknown = ScoreMods.Parse("""[{"acronym":"zz"}]""");
            Assert.That(unknown[0].Acronym, Is.EqualTo("ZZ"));
            Assert.That(unknown[0].CategoryClass, Is.EqualTo("other"));
            Assert.That(unknown[0].RateLabel, Is.Null);
        });
    }
}
