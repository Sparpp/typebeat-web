namespace Typebeat.Web.Storage;

/// <summary>
/// The config keys naming each platform's game-client release artifact. The value is a file name
/// resolved through <see cref="StoreKeys.Download"/>, so this pairs with that builder and lives
/// beside it.
///
/// One authority on purpose: the same three keys are read by the /download/game* endpoints, the
/// /download page and the landing page's CTA gate, and a platform added to some of those but not
/// all is exactly how the landing page ended up gated on Windows alone. <see cref="All"/> is the
/// full set in presentation order, for callers that only ask "is any build available".
/// </summary>
public static class GameDownloadKeys
{
    public const string Windows = "TYPEBEAT_GAME_DOWNLOAD";
    public const string Linux = "TYPEBEAT_GAME_DOWNLOAD_LINUX";
    public const string Macos = "TYPEBEAT_GAME_DOWNLOAD_MACOS";

    public static readonly string[] All = [Windows, Linux, Macos];
}
