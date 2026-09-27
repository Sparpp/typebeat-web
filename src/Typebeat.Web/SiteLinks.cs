namespace Typebeat.Web;

/// <summary>
/// Off-site links the site renders in more than one place, so a change is one edit rather than a
/// hunt through Razor and JavaScript.
/// </summary>
public static class SiteLinks
{
    /// <summary>
    /// The type!beat Discord server's permanent (non-expiring) invite. Rendered by the footer
    /// (_Layout), the landing hero's third CTA, and handed to the browser player as the
    /// <c>data-discord-url</c> of its stage root so the player script carries no URL of its own.
    /// </summary>
    public const string DISCORD_INVITE = "https://discord.gg/yAR2PDPgBB";
}
