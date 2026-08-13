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

    [Test]
    public void UnknownAcronym_StillFallsBackGracefully()
    {
        Assert.That(ModInfo.CategoryClass("ZZ"), Is.EqualTo("other"));
        Assert.That(ModInfo.Name("ZZ"), Is.EqualTo("ZZ"));
    }
}
