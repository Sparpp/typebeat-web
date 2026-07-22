using Dapper;
using Microsoft.Extensions.Primitives;
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
///    "{artist} - {title}.typb" filename, logs to beatmapset_downloads (user_id NULL when
///    anonymous) and bumps the denormalized counter, once per LOGICAL download (ranged
///    continuations/resumes are not re-counted). Anonymous allowed (spec iron rule 9).
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

        // The game client (DownloadBeatmapSetRequest, used by the in-client "update" button) builds
        // its download URL under the /api/v2 namespace, so alias the same handler there. Without
        // this the client 404s and shows "Beatmap download failed!". DownloadAsync allows anonymous
        // and accepts an optional Bearer, so it serves the client unchanged; /api/v2/* is already a
        // wire route (bare 404s, no styled error page). The website's non-api route above is untouched.
        app.MapGet("/api/v2/beatmapsets/{setId:long}/download", DownloadAsync);

        // Profile avatar/banner: version-stamped keys (avatars/{id}/{v}.jpg, user-covers/{id}/{v}.jpg)
        // so each upload has a distinct, immutable URL. World-readable (public profiles; the image
        // loader carries no auth). A stale-key request just 404s once the user re-uploads.
        app.MapGet("/avatars/{userId:long}/{version:long}.jpg", (long userId, long version, HttpContext ctx, IFileStore store)
            => ServeImmutableImageAsync(StoreKeys.Avatar(userId, version), ctx, store));
        app.MapGet("/user-covers/{userId:long}/{version:long}.jpg", (long userId, long version, HttpContext ctx, IFileStore store)
            => ServeImmutableImageAsync(StoreKeys.UserCover(userId, version), ctx, store));

        // The game-client release artifacts (downloads/{TYPEBEAT_GAME_DOWNLOAD} — the Velopack
        // Windows Setup.exe; downloads/{TYPEBEAT_GAME_DOWNLOAD_LINUX} — the Linux AppImage). The
        // /download page links here per-platform. 404 when the platform's build is unconfigured or
        // unstored. Anonymous — anyone can grab the game.
        app.MapGet("/download/game", (HttpContext ctx, IConfiguration config, IFileStore store)
            => DownloadArtifactAsync(ctx, config, store, "TYPEBEAT_GAME_DOWNLOAD"));
        app.MapGet("/download/game-linux", (HttpContext ctx, IConfiguration config, IFileStore store)
            => DownloadArtifactAsync(ctx, config, store, "TYPEBEAT_GAME_DOWNLOAD_LINUX"));

        // The Velopack update feed (downloads/releases/{file}): the release manifest + full
        // package the installed client's VelopackUpdateManager polls (SimpleWebSource at
        // https://typebeat.mingda.sh/releases). Anonymous. Manifests must never be cached (a
        // stale one hides a new release); packages are immutable by name.
        app.MapGet("/releases/{file}", ServeReleaseAssetAsync);

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

    /// <summary>Serves a version-stamped profile image. The key changes on every upload, so the
    /// URL is safe to cache forever; a request for a superseded version simply 404s.</summary>
    private static async Task<IResult> ServeImmutableImageAsync(string key, HttpContext ctx, IFileStore store)
    {
        var stream = await store.OpenObjectReadAsync(key, ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.Stream(stream, "image/jpeg");
    }

    private static async Task<IResult> DownloadArtifactAsync(HttpContext ctx, IConfiguration config, IFileStore store, string configKey)
    {
        string? fileName = config[configKey];

        if (string.IsNullOrEmpty(fileName))
            return Results.NotFound();

        var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        // Attachment disposition + Content-Length + range (seekable FileStream) — a download
        // manager's segmented/resumed fetch works. Content type follows the artifact (installer
        // exe / AppImage today, zip historically).
        string contentType = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip"
            : "application/octet-stream";

        return Results.Stream(stream, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Serves one Velopack feed asset from downloads/releases/. The path segment route constraint
    /// already excludes slashes; ".." is rejected explicitly so a store key can never escape the
    /// releases prefix. Manifests (extensionless RELEASES / *.json) get no-cache so a new release
    /// is visible immediately; packages (*.nupkg, *.exe) are content-named and effectively
    /// immutable, but a bounded day keeps takedown behavior consistent with other media.
    /// </summary>
    private static async Task<IResult> ServeReleaseAssetAsync(string file, HttpContext ctx, IFileStore store)
    {
        if (string.IsNullOrEmpty(file) || file.Contains(".."))
            return Results.NotFound();

        var stream = await store.OpenObjectReadAsync(StoreKeys.Release(file), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        bool isManifest = !file.Contains('.') || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        ctx.Response.Headers.CacheControl = isManifest ? "no-cache" : "public, max-age=86400";

        return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
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

        if (row is null || row.PackageKey is null
            || (row.Status is not ("pending" or "ranked") && requester?.Id != row.OwnerId))
            return Results.NotFound();

        var package = await store.OpenObjectReadAsync(row.PackageKey, ctx.RequestAborted);

        if (package == null)
            return Results.NotFound();

        // One logical download = one log row + one counter bump. Range processing is enabled
        // below, so a download manager's 8-way segmented fetch or a browser resume issues many
        // GETs for ONE download — only the request that covers the start of the file counts
        // (no Range header, or a range starting at byte 0); continuations and resumes don't.
        if (countsAsDownload(ctx))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO beatmapset_downloads (user_id, set_id) VALUES (@userId, @setId);
                UPDATE beatmapsets SET download_count = download_count + 1 WHERE id = @setId
                """,
                new { userId = requester?.Id, setId });
        }

        // .typb = the game's native package extension (registered by the client installer).
        string filename = SanitizeFilename($"{row.Artist} - {row.Title}.typb");

        return Results.Stream(package, "application/octet-stream", fileDownloadName: filename, enableRangeProcessing: true);
    }

    /// <summary>
    /// True when this request represents the start of a logical download: no Range header, an
    /// unparseable one (ignored by range processing, served as a full 200), or a range starting
    /// at byte 0. Mid-file continuations (non-zero start) and suffix ranges don't count — they
    /// are resumes/segments of a download that was already counted.
    /// </summary>
    private static bool countsAsDownload(HttpContext ctx)
    {
        if (StringValues.IsNullOrEmpty(ctx.Request.Headers.Range))
            return true;

        var range = ctx.Request.GetTypedHeaders().Range;

        if (range == null)
            return true; // malformed header: range processing ignores it and streams the full file.

        return range.Ranges.Any(r => r.From == 0);
    }

    /// <summary>Published sets ('pending'/'ranked') are world-readable; hidden/removed media only for the owner.</summary>
    private static async Task<bool> canSeeSetMediaAsync(HttpContext ctx, Db db, long setId)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return false;

        if (row.Status is "pending" or "ranked")
            return true;

        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();
        return requester?.Id == row.OwnerId;
    }

    /// <summary>Strips characters that are invalid in filenames (and CR/LF for header safety).</summary>
    public static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        char[] result = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        return new string(result).Trim(' ', '.', '_') is { Length: > 0 } sane ? sane : "beatmapset.typb";
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
