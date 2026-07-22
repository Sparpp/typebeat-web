using System.Globalization;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages;

/// <summary>
/// Game download page (/download). Presents the per-platform installers — Windows (Velopack
/// Setup.exe, downloads/{TYPEBEAT_GAME_DOWNLOAD}) and Linux (AppImage,
/// downloads/{TYPEBEAT_GAME_DOWNLOAD_LINUX}) — and highlights the visitor's own OS. The actual
/// bytes stream from the /download/game and /download/game-linux endpoints (MediaEndpoints);
/// this page only decides which buttons to show and their sizes. A platform with no configured
/// or stored build renders a "coming soon" state instead of a dead link.
/// </summary>
public sealed class DownloadModel(IFileStore store, IConfiguration config) : TypebeatPageModel
{
    public PlatformDownload Windows { get; private set; } = PlatformDownload.Unavailable("windows");
    public PlatformDownload Linux { get; private set; } = PlatformDownload.Unavailable("linux");

    /// <summary>The visitor's detected OS ("windows" / "linux"), or null when unknown — drives which card is featured.</summary>
    public string? DetectedOs { get; private set; }

    public bool AnyAvailable => Windows.Available || Linux.Available;

    public async Task OnGetAsync()
    {
        DetectedOs = DetectOs(Request.Headers.UserAgent.ToString());

        Windows = await resolve("windows", "TYPEBEAT_GAME_DOWNLOAD", "/download/game");
        Linux = await resolve("linux", "TYPEBEAT_GAME_DOWNLOAD_LINUX", "/download/game-linux");
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

    /// <summary>Coarse OS sniff from the User-Agent — only to feature the matching card; both stay available.</summary>
    private static string? DetectOs(string userAgent)
    {
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return "windows";

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
