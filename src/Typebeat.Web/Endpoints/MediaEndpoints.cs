using Dapper;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Serves the upload pipeline's stored media and packages:
///
///  - GET /covers/{setId}/{version}/{name}.jpg — the CoverGenerator buckets (whitelisted names).
///    Version-keyed but NOT status-keyed: the key never changes when a set is taken down, so
///    covers get the same bounded one-day TTL as previews (never immutable/1y — an edge- or
///    browser-cached cover of a DMCA'd set must age out within a day, see the takedown runbook
///    in deploy/README.md).
///  - GET /previews/{setId}.mp3 — the 30 s preview clip, range-request capable (audio seeking).
///    NOT version-keyed (the key is stable across re-uploads), so cached one day only.
///  - GET /beatmapsets/{id}/download — streams the latest version's assembled package with a
///    "{artist} - {title}.olz" filename, logs to beatmapset_downloads (user_id NULL when
///    anonymous) and bumps the denormalized counter. Anonymous allowed (spec iron rule 9).
///  - GET /img/default-cover.jpg — the self-hosted fallback the beatmap DTOs reference when a
///    set has no generated covers. Generated in-process (neon violet→magenta gradient, the
///    design system's --grad-primary) so no binary asset lives in the repo; cached lazily.
///
/// Hidden/removed sets serve media only to their owner (cookie session or bearer) — covers of
/// a DMCA'd set must not remain fetchable, and unpublished shells are nobody's business.
/// </summary>
public static class MediaEndpoints
{
    private static readonly HashSet<string> cover_names = new(StringComparer.Ordinal)
    {
        "card", "card@2x", "cover", "cover@2x", "list", "list@2x", "slimcover", "slimcover@2x",
    };

    // One day for BOTH buckets. Covers must never be immutable/max-age=1y: the cover URL is
    // version-keyed only and a takedown never bumps the version (BSS blocks re-upload to
    // 'removed' sets), so a long-lived entry would keep serving infringing artwork from the
    // Cloudflare edge and from browser caches for up to a year after the origin starts 404ing.
    // With max-age=86400 a status flip becomes globally visible within a day (plus an immediate
    // manual Cloudflare purge per the deploy/README.md takedown runbook).
    private const string covers_cache_control = "public, max-age=86400";
    private const string previews_cache_control = "public, max-age=86400";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/covers/{setId:long}/{version:int}/{file}", GetCoverAsync);
        app.MapGet("/previews/{setId:long}.mp3", GetPreviewAsync);
        app.MapGet("/beatmapsets/{setId:long}/download", DownloadAsync);

        // Like the default avatar (StubEndpoints): unauthenticated, image loaders carry no token.
        app.MapGet("/img/default-cover.jpg", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = previews_cache_control;
            return Results.Bytes(default_cover.Value, "image/jpeg");
        });
    }

    private static async Task<IResult> GetCoverAsync(long setId, int version, string file, HttpContext ctx, Db db, IFileStore store)
    {
        if (!file.EndsWith(".jpg", StringComparison.Ordinal) || !cover_names.Contains(file[..^4]))
            return Results.NotFound();

        if (!await canSeeSetMediaAsync(ctx, db, setId))
            return Results.NotFound();

        var stream = await store.OpenObjectReadAsync(StoreKeys.Cover(setId, version, file[..^4]), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        ctx.Response.Headers.CacheControl = covers_cache_control;
        return Results.Stream(stream, "image/jpeg");
    }

    private static async Task<IResult> GetPreviewAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        if (!await canSeeSetMediaAsync(ctx, db, setId))
            return Results.NotFound();

        var stream = await store.OpenObjectReadAsync(StoreKeys.Preview(setId), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        ctx.Response.Headers.CacheControl = previews_cache_control;

        // Range processing lets <audio> seek without re-downloading the whole clip.
        return Results.Stream(stream, "audio/mpeg", enableRangeProcessing: true);
    }

    private static async Task<IResult> DownloadAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var row = await conn.QuerySingleOrDefaultAsync<DownloadRow>(
            """
            SELECT s.status     AS status,
                   s.owner_id   AS ownerId,
                   s.artist     AS artist,
                   s.title      AS title,
                   v.package_key AS packageKey
            FROM beatmapsets s
            LEFT JOIN set_versions v
                   ON v.set_id = s.id
                  AND v.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = s.id)
            WHERE s.id = @setId
            """,
            new { setId });

        if (row is null || row.PackageKey is null || (row.Status != "public" && requester?.Id != row.OwnerId))
            return Results.NotFound();

        var package = await store.OpenObjectReadAsync(row.PackageKey, ctx.RequestAborted);

        if (package == null)
            return Results.NotFound();

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmapset_downloads (user_id, set_id) VALUES (@userId, @setId);
            UPDATE beatmapsets SET download_count = download_count + 1 WHERE id = @setId
            """,
            new { userId = requester?.Id, setId });

        // .olz = the game's lazer-style package extension (registered by the client installer).
        string filename = SanitizeFilename($"{row.Artist} - {row.Title}.olz");

        return Results.Stream(package, "application/octet-stream", fileDownloadName: filename, enableRangeProcessing: true);
    }

    /// <summary>Public sets are world-readable; hidden/removed media only for the owner.</summary>
    private static async Task<bool> canSeeSetMediaAsync(HttpContext ctx, Db db, long setId)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return false;

        if (row.Status == "public")
            return true;

        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();
        return requester?.Id == row.OwnerId;
    }

    /// <summary>Strips characters that are invalid in filenames (and CR/LF for header safety).</summary>
    public static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        char[] result = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        return new string(result).Trim(' ', '.', '_') is { Length: > 0 } sane ? sane : "beatmapset.olz";
    }

    // ---------------------------------------------------------------------------------------------
    // The default cover: 400x140 (the card bucket), deep-indigo base swept by the design
    // system's violet→magenta primary gradient — violet leads, magenta only enters at the tail
    // (style-guide rule; never flat pink-on-black). Rendered once, ~4 KB of JPEG.
    // ---------------------------------------------------------------------------------------------
    private static readonly Lazy<byte[]> default_cover = new(() =>
    {
        const int width = 400, height = 140;

        // --violet-deep #7a3ff2 → --magenta #e84baf over --surface #181430.
        var violet = (R: 0x7a, G: 0x3f, B: 0xf2);
        var magenta = (R: 0xe8, G: 0x4b, B: 0xaf);
        var surface = (R: 0x18, G: 0x14, B: 0x30);

        using var image = new Image<Rgba32>(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Diagonal sweep; magenta-weighting is squared so the left 2/3 stays violet.
                double t = (x / (double)(width - 1)) * 0.85 + (y / (double)(height - 1)) * 0.15;
                double m = t * t;

                double r = violet.R + (magenta.R - violet.R) * m;
                double g = violet.G + (magenta.G - violet.G) * m;
                double b = violet.B + (magenta.B - violet.B) * m;

                // Blended onto the dark surface (60% gradient) so it reads as a backdrop, not a button.
                const double strength = 0.6;
                image[x, y] = new Rgba32(
                    (byte)(surface.R + (r - surface.R) * strength),
                    (byte)(surface.G + (g - surface.G) * strength),
                    (byte)(surface.B + (b - surface.B) * strength));
            }
        }

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer, new JpegEncoder { Quality = CoverGenerator.JpegQuality });
        return buffer.ToArray();
    });

    private sealed record DownloadRow(string Status, long OwnerId, string Artist, string Title, string? PackageKey);
}
