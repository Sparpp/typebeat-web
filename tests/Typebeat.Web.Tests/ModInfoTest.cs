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

    [Test]
    public void UnknownAcronym_StillFallsBackGracefully()
    {
        Assert.That(ModInfo.CategoryClass("ZZ"), Is.EqualTo("other"));
        Assert.That(ModInfo.Name("ZZ"), Is.EqualTo("ZZ"));
    }
}
