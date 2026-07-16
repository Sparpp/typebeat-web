using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages;

/// <summary>
/// Game download page (/download). The release zip is a named object in the file store
/// (downloads/{TYPEBEAT_GAME_DOWNLOAD}); when the config key is unset or the file is missing
/// the page shows a "coming soon" state instead. /download/game (the {handler} route) streams
/// the zip with a proper attachment disposition.
/// </summary>
public sealed class DownloadModel(IFileStore store, IConfiguration config) : TypebeatPageModel
{
    public bool Available { get; private set; }
    public string? FileName { get; private set; }
    public long SizeBytes { get; private set; }

    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? (SizeBytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : (SizeBytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";

    public async Task OnGetAsync()
    {
        string? fileName = config["TYPEBEAT_GAME_DOWNLOAD"];

        if (string.IsNullOrEmpty(fileName))
            return;

        // Size for the button caption; a seekable read stream is the cheapest stat we have.
        await using var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), HttpContext.RequestAborted);

        if (stream is null)
            return;

        Available = true;
        FileName = fileName;
        SizeBytes = stream.Length;
    }

    public async Task<IActionResult> OnGetGameAsync()
    {
        string? fileName = config["TYPEBEAT_GAME_DOWNLOAD"];

        if (string.IsNullOrEmpty(fileName))
            return NotFound();

        var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), HttpContext.RequestAborted);

        if (stream is null)
            return NotFound();

        // FileStreamResult disposes the stream and emits Content-Length (seekable) +
        // Content-Disposition: attachment.
        return new FileStreamResult(stream, "application/zip")
        {
            FileDownloadName = fileName,
            EnableRangeProcessing = true,
        };
    }
}
