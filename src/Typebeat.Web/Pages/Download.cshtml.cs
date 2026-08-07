using System.Globalization;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages;

/// <summary>
/// Game download page (/download). Presents the per-platform installers: Windows (Velopack
/// Setup.exe, downloads/{TYPEBEAT_GAME_DOWNLOAD}), Linux (AppImage,
/// downloads/{TYPEBEAT_GAME_DOWNLOAD_LINUX}) and macOS (Velopack .pkg,
/// downloads/{TYPEBEAT_GAME_DOWNLOAD_MACOS}), and highlights the visitor's own OS. The actual
/// bytes stream from the /download/game, /download/game-linux and /download/game-macos endpoints
/// (MediaEndpoints); this page only decides which buttons to show and their sizes. A platform
/// with no configured or stored build renders a "coming soon" state instead of a dead link.
/// </summary>
public sealed class DownloadModel(IFileStore store, IConfiguration config) : TypebeatPageModel
{
    public PlatformDownload Windows { get; private set; } = PlatformDownload.Unavailable("windows");
    public PlatformDownload Linux { get; private set; } = PlatformDownload.Unavailable("linux");
    public PlatformDownload Macos { get; private set; } = PlatformDownload.Unavailable("macos");

    /// <summary>The visitor's detected OS ("windows" / "macos" / "linux"), or null when unknown; drives which card is featured.</summary>
    public string? DetectedOs { get; private set; }

    public bool AnyAvailable => Windows.Available || Linux.Available || Macos.Available;

    public async Task OnGetAsync()
    {
        DetectedOs = DetectOs(Request.Headers.UserAgent.ToString());

        Windows = await resolve("windows", GameDownloadKeys.Windows, "/download/game");
        Linux = await resolve("linux", GameDownloadKeys.Linux, "/download/game-linux");
        Macos = await resolve("macos", GameDownloadKeys.Macos, "/download/game-macos");
    }

    private async Task<PlatformDownload> resolve(string os, string configKey, string href)
    {
        string? fileName = config[configKey];

        if (string.IsNullOrEmpty(fileName))
            return PlatformDownload.Unavailable(os);

        // A seekable read stream is the cheapest way to both confirm the object exists and size it.
        await using var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), HttpContext.RequestAborted);

        if (stream is null)
            return PlatformDownload.Unavailable(os);

        return new PlatformDownload(os, true, fileName, stream.Length, href);
    }

    /// <summary>
    /// Coarse OS sniff from the User-Agent, only to feature the matching card; all three stay
    /// available regardless. Public purely so the mobile-exclusion rules below can be unit tested;
    /// it is not a Razor Pages handler (those are the On* methods) and binds to nothing.
    /// </summary>
    public static string? DetectOs(string userAgent)
    {
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return "windows";

        // Desktop Mac UAs say "Macintosh; Intel Mac OS X ..." on every browser. iOS UAs carry
        // "like Mac OS X" too ("iPhone; CPU iPhone OS 17_0 like Mac OS X"), and there is no iOS
        // build, so exclude them the same way the Linux branch excludes Android below. Known
        // unfixable gap: an iPad in desktop mode sends a UA byte-identical to a Mac's, so it is
        // reported as macOS. Nothing server-side can tell those apart.
        if ((userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
            && !userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("iPod", StringComparison.OrdinalIgnoreCase))
            return "macos";

        // Android UAs also contain "Linux"; exclude them (no Android build) so mobile isn't mislabelled.
        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "linux";

        return null;
    }

    public sealed record PlatformDownload(string Os, bool Available, string? FileName, long SizeBytes, string? Href)
    {
        public static PlatformDownload Unavailable(string os) => new(os, false, null, 0, null);

        public string SizeDisplay => SizeBytes >= 1024 * 1024
            ? (SizeBytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
            : (SizeBytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
    }
}
