using Typebeat.Web;

namespace Typebeat.Web.Tests;

/// <summary>
/// Presentation metadata for the mod-icon badges (ModInfo). The ranked Literate mod ("LT") must
/// render as a Difficulty-Increase badge named "Literate", consistent with the other increase mods.
/// </summary>
public class ModInfoTest
{
    [Test]
    public void Literate_RendersAsRankedDifficultyIncreaseBadge()
    {
        Assert.That(ModInfo.CategoryClass("LT"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("LT"), Is.EqualTo("Literate"));

        // Case-insensitive, like the other acronyms.
        Assert.That(ModInfo.CategoryClass("lt"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("lt"), Is.EqualTo("Literate"));
    }

    /// <summary>
    /// Gatekeeper (backlog 107), the strict wrong-key model that used to be the client's default.
    /// Ranked and a difficulty increase, so its badge must read like the other increase mods rather
    /// than falling through to the neutral "other" one.
    /// </summary>
    [Test]
    public void Gatekeeper_RendersAsRankedDifficultyIncreaseBadge()
    {
        Assert.That(ModInfo.CategoryClass("GK"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("GK"), Is.EqualTo("Gatekeeper"));

        Assert.That(ModInfo.CategoryClass("gk"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("gk"), Is.EqualTo("Gatekeeper"));
    }

    /// <summary>
    /// Rhythmic (backlog 135), the millisecond judgement ladder. Ranked and a difficulty increase,
    /// so it badges like the other increase mods rather than falling through to "other".
    /// </summary>
    [Test]
    public void Rhythmic_RendersAsRankedDifficultyIncreaseBadge()
    {
        Assert.That(ModInfo.CategoryClass("RH"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("RH"), Is.EqualTo("Rhythmic"));

        Assert.That(ModInfo.CategoryClass("rh"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("rh"), Is.EqualTo("Rhythmic"));
    }

    /// <summary>
    /// Easy (backlog 149), the doubled judgement windows. A difficulty REDUCTION, so it badges green
    /// beside Half Time and No Fail rather than falling through to "other".
    /// </summary>
    [Test]
    public void Easy_RendersAsRankedDifficultyReductionBadge()
    {
        Assert.That(ModInfo.CategoryClass("EZ"), Is.EqualTo("reduction"));
        Assert.That(ModInfo.Name("EZ"), Is.EqualTo("Easy"));

        Assert.That(ModInfo.CategoryClass("ez"), Is.EqualTo("reduction"));
        Assert.That(ModInfo.Name("ez"), Is.EqualTo("Easy"));
    }

    /// <summary>
    /// Daycore (backlog 395), Half Time's pitch-PRESERVING twin. A difficulty REDUCTION like HT, so
    /// it badges green beside Half Time rather than falling through to "other".
    /// </summary>
    [Test]
    public void Daycore_RendersAsRankedDifficultyReductionBadge()
    {
        Assert.That(ModInfo.CategoryClass("DC"), Is.EqualTo("reduction"));
        Assert.That(ModInfo.Name("DC"), Is.EqualTo("Daycore"));

        Assert.That(ModInfo.CategoryClass("dc"), Is.EqualTo("reduction"));
        Assert.That(ModInfo.Name("dc"), Is.EqualTo("Daycore"));
    }

    /// <summary>
    /// Hard Rock (backlog 150), the halved judgement windows. A difficulty INCREASE, so it badges
    /// red beside Double Time and Flashlight rather than falling through to "other".
    /// </summary>
    [Test]
    public void HardRock_RendersAsRankedDifficultyIncreaseBadge()
    {
        Assert.That(ModInfo.CategoryClass("HR"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("HR"), Is.EqualTo("Hard Rock"));

        Assert.That(ModInfo.CategoryClass("hr"), Is.EqualTo("increase"));
        Assert.That(ModInfo.Name("hr"), Is.EqualTo("Hard Rock"));
    }

    /// <summary>
    /// Fletcher's two acronyms (backlog 208 reversed the mod and moved it to a new one). The badge
    /// has to tell them apart, because a stored row carrying "FT" was played under the OPPOSITE
    /// rule to a row carrying "FC": FT unpinned the caret from the playhead, which is what every
    /// play does now, and FC pins it back. Both names mirror the game's own mod classes, so the
    /// tooltip on an old row says what that row was played under rather than what the name means
    /// today.
    ///
    /// <para>Both stay on the neutral "other" badge, which is not an oversight: they are CONVERSION
    /// mods, and the four buckets this file has (increase, reduction, automation, other) hold no
    /// conversion colour. Categorising one as an increase and the other as a reduction would say
    /// they sit on a difficulty axis they are deliberately off.</para>
    /// </summary>
    [Test]
    public void Fletcher_KeepsTheLiveAndRetiredAcronymsApart()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModInfo.Name("FC"), Is.EqualTo("Fletcher"));
            Assert.That(ModInfo.Name("fc"), Is.EqualTo("Fletcher"));

            Assert.That(ModInfo.Name("FT"), Is.EqualTo("Fletcher (retired)"));
            Assert.That(ModInfo.Name("ft"), Is.EqualTo("Fletcher (retired)"));

            Assert.That(ModInfo.Name("FT"), Is.Not.EqualTo(ModInfo.Name("FC")),
                "an FT row and an FC row were played under opposite rules and must not share a tooltip");

            Assert.That(ModInfo.CategoryClass("FC"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("FT"), Is.EqualTo("other"));
        });
    }

    /// <summary>
    /// Recite (backlog 229), which hides every character until it is typed. Ranked and typed
    /// ModType.DifficultyIncrease by the game, modelled on Flashlight, so it badges red beside it
    /// rather than falling through to "other".
    /// </summary>
    [Test]
    public void Recite_RendersAsRankedDifficultyIncreaseBadge()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModInfo.CategoryClass("RE"), Is.EqualTo("increase"));
            Assert.That(ModInfo.Name("RE"), Is.EqualTo("Recite"));

            Assert.That(ModInfo.CategoryClass("re"), Is.EqualTo("increase"));
            Assert.That(ModInfo.Name("re"), Is.EqualTo("Recite"));

            Assert.That(ModInfo.CategoryClass("RE"), Is.EqualTo(ModInfo.CategoryClass("FL")),
                "the mod it is modelled on badges the same way");
        });
    }

    /// <summary>
    /// Dyslexia (backlog 231) and Puppeteer (backlog 256), the unranked follower/conversion pair
    /// still current, plus the older Conductor rate-follower (backlog 226) that backlog 257 retired
    /// and moved off its own name. All are NAMED, because an unnamed acronym renders as the bare
    /// acronym and a leaderboard row would say "CT" with no tooltip at all.
    ///
    /// <para>All three stay on the neutral "other" badge, and that is a decision rather than an
    /// oversight. The retired Conductor and Puppeteer are both ModType.Fun, the category Wind Up,
    /// Wind Down and Muted are in, none of which this table has ever coloured: a rate the player
    /// steers is not a difficulty adjustment in either direction, and Puppeteer takes that a step
    /// further by forgiving timing judgement outright rather than merely steering it. Dyslexia is
    /// ModType.Conversion, the bucket the four colours here do not cover (the Fletcher pair above
    /// records the same reasoning), and it is not "automation" either: Mashing earns violet by
    /// making every key the right key, while Dyslexia still makes the player type every character of
    /// the word.</para>
    /// </summary>
    [Test]
    public void ConductorDyslexiaAndPuppeteer_AreNamed_AndKeepTheNeutralBadge()
    {
        Assert.Multiple(() =>
        {
            // Backlog 257: "PT" (still TypeBeatModPuppeteer) is now the live mod named Conductor,
            // and "CT" (still TypeBeatModConductor) is the retired one, on the FT "Fletcher
            // (retired)" precedent above.
            Assert.That(ModInfo.Name("PT"), Is.EqualTo("Conductor"));
            Assert.That(ModInfo.Name("pt"), Is.EqualTo("Conductor"));
            Assert.That(ModInfo.Name("CT"), Is.EqualTo("Conductor (retired)"));
            Assert.That(ModInfo.Name("ct"), Is.EqualTo("Conductor (retired)"));

            Assert.That(ModInfo.Name("CT"), Is.Not.EqualTo(ModInfo.Name("PT")),
                "a CT row and a PT row were played under opposite rules and must not share a tooltip");

            Assert.That(ModInfo.Name("DX"), Is.EqualTo("Dyslexia"));
            Assert.That(ModInfo.Name("dx"), Is.EqualTo("Dyslexia"));

            Assert.That(ModInfo.CategoryClass("CT"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("ct"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("DX"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("dx"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("PT"), Is.EqualTo("other"));
            Assert.That(ModInfo.CategoryClass("pt"), Is.EqualTo("other"));

            // The retired Conductor and Puppeteer sit with the ramps they share a mod type with, not
            // with the rate mods they share a knob with.
            Assert.That(ModInfo.CategoryClass("CT"), Is.EqualTo(ModInfo.CategoryClass("WU")));
            Assert.That(ModInfo.CategoryClass("CT"), Is.Not.EqualTo(ModInfo.CategoryClass("DT")));
            Assert.That(ModInfo.CategoryClass("PT"), Is.EqualTo(ModInfo.CategoryClass("WU")));
            Assert.That(ModInfo.CategoryClass("PT"), Is.Not.EqualTo(ModInfo.CategoryClass("DT")));

            // Dyslexia is not Mashing: it relaxes which cell a key lands in, it does not play the
            // map for the player.
            Assert.That(ModInfo.CategoryClass("DX"), Is.Not.EqualTo(ModInfo.CategoryClass("RX")));
        });
    }

    [Test]
    public void UnknownAcronym_StillFallsBackGracefully()
    {
        Assert.That(ModInfo.CategoryClass("ZZ"), Is.EqualTo("other"));
        Assert.That(ModInfo.Name("ZZ"), Is.EqualTo("ZZ"));
    }
}
