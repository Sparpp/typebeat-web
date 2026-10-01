using System.IO.Compression;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
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
///  - GET /covers/{setId}/{version}/{name}.jpg: the CoverGenerator buckets (whitelisted names).
///    Version-keyed but NOT status-keyed: the key never changes when a set is taken down, so
///    covers get the same bounded one-day TTL as previews (never immutable/1y; an edge- or
///    browser-cached cover of a DMCA'd set must age out within a day, see the takedown runbook
///    in deploy/README.md).
///  - GET /previews/{setId}.mp3: the 30 s preview clip, range-request capable (audio seeking).
///    NOT version-keyed (the key is stable across re-uploads), so cached one day only.
///  - GET /beatmapsets/{id}/download: streams the latest version's assembled package with a
///    "{artist} - {title}.typb" filename, logs to beatmapset_downloads (user_id NULL when
///    anonymous) and bumps the denormalized counter, once per LOGICAL download (ranged
///    continuations/resumes are not re-counted). Anonymous allowed (spec iron rule 9).
///    <c>?noVideo=1</c> serves the same package with the video files left out (AudioOnlyPackage).
///  - GET /beatmapsets/{id}/download-sizes: the approximate byte totals of those two variants,
///    for the card's two-option download panel. Same visibility gate.
///  - GET /img/default-cover.jpg: the self-hosted fallback the beatmap DTOs reference when a
///    set has no generated covers. Generated in-process (neon violet→magenta gradient, the
///    design system's --grad-primary) so no binary asset lives in the repo; cached lazily.
///
/// Hidden/removed sets serve media only to their owner (cookie session or bearer); covers of
/// a DMCA'd set must not remain fetchable, and unpublished shells are nobody's business.
///
/// DUAL-HOME (backlog 364): with the public bucket configured (<see cref="IPublicObjectStore"/>),
/// the three big-byte routes (installers, feed nupkgs, the full package download) answer a request
/// on a Cloudflare-proxied host with a 302 to the bucket's custom domain, AFTER every gate and
/// counter, while the direct-origin bss.* hosts keep streaming from the box. With it unconfigured
/// wantsRedirect always declines and every route is exactly what it was.
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

        // The card's download panel asks for these once, the first time it is opened.
        app.MapGet("/beatmapsets/{setId:long}/download-sizes", DownloadSizesAsync);

        // The game client (DownloadBeatmapSetRequest, used by the in-client "update" button) builds
        // its download URL under the /api/v2 namespace, so alias the same handler there. Without
        // this the client 404s and shows "Beatmap download failed!". DownloadAsync allows anonymous
        // and accepts an optional Bearer, so it serves the client unchanged; /api/v2/* is already a
        // wire route (bare 404s, no styled error page). The website's non-api route above is untouched.
        //
        // This alias is also where the ?noVideo=1 flag arrives from the GAME: the client's
        // DownloadBeatmapSetRequest has always appended it when the player has "prefer downloads
        // without video" on, and the server used to drop it. Honouring it here makes that in-client
        // setting work with no game-side change.
        app.MapGet("/api/v2/beatmapsets/{setId:long}/download", DownloadAsync);

        // Profile avatar/banner: version-stamped keys (avatars/{id}/{v}.jpg, user-covers/{id}/{v}.jpg)
        // so each upload has a distinct, immutable URL. World-readable (public profiles; the image
        // loader carries no auth). A stale-key request just 404s once the user re-uploads.
        app.MapGet("/avatars/{userId:long}/{version:long}.jpg", (long userId, long version, HttpContext ctx, IFileStore store)
            => ServeImmutableImageAsync(StoreKeys.Avatar(userId, version), ctx, store));
        app.MapGet("/user-covers/{userId:long}/{version:long}.jpg", (long userId, long version, HttpContext ctx, IFileStore store)
            => ServeImmutableImageAsync(StoreKeys.UserCover(userId, version), ctx, store));

        // The game-client release artifacts (downloads/{TYPEBEAT_GAME_DOWNLOAD}, the Velopack
        // Windows Setup.exe; downloads/{TYPEBEAT_GAME_DOWNLOAD_LINUX}, the Linux AppImage;
        // downloads/{TYPEBEAT_GAME_DOWNLOAD_MACOS}, the macOS .pkg installer). The /download page
        // links here per-platform. 404 when the platform's build is unconfigured or unstored.
        // Anonymous: anyone can grab the game.
        app.MapGet("/download/game", (HttpContext ctx, IConfiguration config, IFileStore store, IPublicObjectStore publicStore, GameInstallers installers)
            => DownloadArtifactAsync(ctx, config, store, publicStore, installers, GameDownloadKeys.Windows));
        app.MapGet("/download/game-linux", (HttpContext ctx, IConfiguration config, IFileStore store, IPublicObjectStore publicStore, GameInstallers installers)
            => DownloadArtifactAsync(ctx, config, store, publicStore, installers, GameDownloadKeys.Linux));
        app.MapGet("/download/game-macos", (HttpContext ctx, IConfiguration config, IFileStore store, IPublicObjectStore publicStore, GameInstallers installers)
            => DownloadArtifactAsync(ctx, config, store, publicStore, installers, GameDownloadKeys.Macos));

        // The Velopack update feed (downloads/releases/{file}): the release manifest + full
        // package the installed client's VelopackUpdateManager polls (SimpleWebSource at
        // https://typebeat.sh/releases). Anonymous. Manifests must never be cached (a
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

    private static async Task<IResult> DownloadArtifactAsync(
        HttpContext ctx, IConfiguration config, IFileStore store, IPublicObjectStore publicStore, GameInstallers installers, string configKey)
    {
        string? fileName = config[configKey];

        if (string.IsNullOrEmpty(fileName))
            return Results.NotFound();

        // The bucket copy, when there is one (a ship uploads it beside the box's copy). An
        // installer the bucket does not hold still streams from the box rather than 404ing.
        if (wantsRedirect(ctx, publicStore) && await installers.PublicSizeAsync(fileName, ctx.RequestAborted) != null)
            return redirectTo(ctx, publicStore, StoreKeys.Download(fileName));

        var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        // Attachment disposition + Content-Length + range (seekable FileStream); a download
        // manager's segmented/resumed fetch works. Content type follows the artifact (installer
        // exe / AppImage / pkg today, zip historically).
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
    ///
    /// <para>Backlog 364: a *.nupkg on a Cloudflare host redirects to the bucket's
    /// downloads/releases/{file} without consulting the box (both copies are uploaded and pruned
    /// by the same ship scripts, and every current nupkg is backfilled before the bucket is
    /// switched on). The MANIFESTS never redirect: they are KB-sized, Velopack appends per-client
    /// query parameters to them, their no-cache is what makes a release visible at once, and both
    /// prunes and the ship verify read them here. bundled-*.typb is not a nupkg and stays here
    /// too.</para>
    /// </summary>
    private static async Task<IResult> ServeReleaseAssetAsync(string file, HttpContext ctx, IFileStore store, IPublicObjectStore publicStore)
    {
        if (string.IsNullOrEmpty(file) || file.Contains(".."))
            return Results.NotFound();

        if (file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && wantsRedirect(ctx, publicStore))
            return redirectTo(ctx, publicStore, StoreKeys.Release(file));

        var stream = await store.OpenObjectReadAsync(StoreKeys.Release(file), ctx.RequestAborted);

        if (stream == null)
            return Results.NotFound();

        bool isManifest = !file.Contains('.') || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        ctx.Response.Headers.CacheControl = isManifest ? "no-cache" : "public, max-age=86400";

        return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>
    /// Streams the latest assembled package. <c>?noVideo=1</c> streams the AUDIO-ONLY variant of
    /// the same package: every entry except the video files, filtered live (see
    /// <see cref="StreamAudioOnlyAsync"/>), never a second stored object.
    ///
    /// <para>The spelling is the game client's: DownloadBeatmapSetRequest has always appended
    /// exactly <c>?noVideo=1</c> to the /api/v2 alias when the player's "prefer downloads without
    /// video" setting is on, and the server dropped it. So implementing this flag is also a live
    /// behaviour change for the in-client download and update buttons of every player who has that
    /// setting on, not only for the website card.</para>
    ///
    /// <para>The visibility gate and the counter bump both sit ABOVE the variant branch: the two
    /// options are one download route with one gate, and neither can become a path around it.</para>
    /// </summary>
    private static async Task<IResult> DownloadAsync(long setId, HttpContext ctx, Db db, IFileStore store, IPublicObjectStore publicStore)
    {
        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var row = await conn.QuerySingleOrDefaultAsync<DownloadRow>(
            """
            SELECT s.status     AS status,
                   s.owner_id   AS ownerId,
                   s.artist     AS artist,
                   s.title      AS title,
                   v.package_key AS packageKey,
                   v.public_key  AS publicKey
            FROM beatmapsets s
            LEFT JOIN set_versions v
                   ON v.set_id = s.id
                  AND v.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = s.id)
            WHERE s.id = @setId
            """,
            new { setId });

        if (row is null || row.PackageKey is null
            || (!BeatmapsetEndpoints.IsPublished(row.Status) && requester?.Id != row.OwnerId))
            return Results.NotFound();

        var package = await store.OpenObjectReadAsync(row.PackageKey, ctx.RequestAborted);

        if (package == null)
            return Results.NotFound();

        bool noVideo = wantsNoVideo(ctx);

        // One logical download = one log row + one counter bump, for BOTH options.
        //
        // On the full arm range processing is enabled below, so a download manager's 8-way
        // segmented fetch or a browser resume issues many GETs for ONE download; only the request
        // that covers the start of the file counts (no Range header, or a range starting at byte
        // 0); continuations and resumes don't.
        //
        // The audio-only arm carries no ranges at all (a live-filtered zip has no length and cannot
        // seek, and its full-package fallback is served the same way so the two answers stay
        // protocol-identical), so every request on it is a whole download and always counts. Two
        // different routes to the same counter, deliberately: do not "simplify" this to one.
        if (noVideo || countsAsDownload(ctx))
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

        if (noVideo)
            return await StreamAudioOnlyAsync(package, row, filename, ctx);

        // Backlog 364: the full package of a PUBLISHED set with a bucket copy is handed off, AFTER
        // the gate and the counter above (which is why the 302 carries no-store: an edge-cached
        // redirect would skip both). The audio-only arm above is a live filter of the LOCAL
        // package and never redirects; a hidden set's owner keeps the local stream; a NULL key (no
        // copy yet, or its upload failed) streams locally as before.
        if (row.PublicKey != null && BeatmapsetEndpoints.IsPublished(row.Status) && wantsRedirect(ctx, publicStore))
        {
            await package.DisposeAsync();
            return redirectTo(ctx, publicStore, row.PublicKey);
        }

        return Results.Stream(package, "application/octet-stream", fileDownloadName: filename, enableRangeProcessing: true);
    }

    /// <summary>
    /// The audio-only arm: re-streams the stored package minus its video entries, straight from the
    /// store's (seekable) stream into the response body. Nothing is re-stored, so nothing has to
    /// join the package pruning, and the .osu entries are copied byte for byte, which is what keeps
    /// <c>beatmaps.checksum_md5</c>, and therefore every leaderboard on this set, intact.
    ///
    /// <para>No range processing here: a live-filtered zip has no Content-Length and cannot seek
    /// backwards, so a Range header on this arm is simply ignored and the whole variant is returned
    /// with a 200 (which is also why <see cref="DownloadAsync"/> counts every request on it). The
    /// visible cost is no progress percentage and no resume for the small download; the big one
    /// keeps both.</para>
    ///
    /// <para>Every way out leads back to the FULL package rather than to an error, because the game
    /// client sets this flag from a preference and a preference must never break a download: a
    /// package whose audio file IS its video file (see the guard in <see cref="AudioOnlyPackage"/>),
    /// a package with no video to leave out (the client sends the flag on every download when the
    /// setting is on, and most sets have no video), and a stored object that cannot be read as a zip
    /// (corrupt, or a stream a future non-local store hands back forward-only). All of them are
    /// served rangeless like the filtered variant, so one noVideo request is one counted download
    /// whichever body it gets.</para>
    /// </summary>
    private static async Task<IResult> StreamAudioOnlyAsync(Stream package, DownloadRow row, string fullFilename, HttpContext ctx)
    {
        // Reading the archive's central directory needs to seek, which the local store's FileStream
        // does. Anything else gets the package it would have got before this variant existed.
        if (!package.CanSeek)
            return Results.Stream(package, "application/octet-stream", fileDownloadName: fullFilename);

        ZipArchive? source = null;

        try
        {
            source = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);

            var plan = await AudioOnlyPackage.PlanAsync(source, ctx.RequestAborted);

            if (!plan.Available)
            {
                source.Dispose();
                source = null;
                package.Position = 0;

                return Results.Stream(package, "application/octet-stream", fileDownloadName: fullFilename);
            }

            string filename = SanitizeFilename($"{row.Artist} - {row.Title} [no video].typb");

            // ZipArchive writes the entry headers, the data descriptors and the whole central
            // directory with SYNCHRONOUS Write calls on the stream it was handed (only the entry
            // BODIES go through the async copy below), and the response body refuses synchronous
            // writes by default. This one response opts in rather than buffering the variant into
            // memory or onto disk first, which is what "filter it live" means.
            var bodyControl = ctx.Features.Get<IHttpBodyControlFeature>();

            if (bodyControl != null)
                bodyControl.AllowSynchronousIO = true;

            var archive = source;
            source = null; // ownership moves to the streaming callback below.

            return Results.Stream(async output =>
            {
                await using (package)
                using (archive)
                using (var filtered = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (plan.Omits(entry.FullName))
                            continue;

                        var copy = filtered.CreateEntry(entry.FullName, CompressionLevel.Optimal);

                        await using var input = entry.Open();
                        await using var target = copy.Open();
                        await input.CopyToAsync(target, ctx.RequestAborted);
                    }
                }
            }, "application/octet-stream", fileDownloadName: filename);
        }
        catch (InvalidDataException)
        {
            // Not a readable zip. Nothing to filter, but the stored bytes are still what the full
            // download would hand over, so hand those over rather than failing this one request.
            source?.Dispose();
            source = null;
            package.Position = 0;

            return Results.Stream(package, "application/octet-stream", fileDownloadName: fullFilename);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>
    /// Reads the audio-only flag. Deliberately not a bound <c>bool</c> parameter: the game client
    /// sends <c>?noVideo=1</c>, and "1" does not parse as a bool, so a bound parameter would 400
    /// every in-client download from a player who prefers no video.
    /// </summary>
    private static bool wantsNoVideo(HttpContext ctx)
    {
        string? value = ctx.Request.Query["noVideo"];

        return value == "1"
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// GET /beatmapsets/{setId}/download-sizes: what the card's two-option download panel labels
    /// its choices with, fetched once when the panel is first opened (nothing stores a package
    /// size, and a per-card subquery on a full listing grid would pay for a number most visitors
    /// never look at).
    ///
    /// <para><c>{ full, audioOnly, audioOnlyAvailable }</c>. The byte totals are the manifest's
    /// UNCOMPRESSED sizes (files.size), so they are approximate and the UI must say so: mp3 and mp4
    /// entries, which dominate, barely compress, while the .osu and .txt entries are overstated.
    /// <c>audioOnlyAvailable</c> is false when the set has no video entry at all, and when the
    /// mp4-as-audio guard withdraws the variant, which is exactly when the card must not offer the
    /// choice.</para>
    ///
    /// <para>Same visibility gate as the download itself, so an unpublished set does not leak the
    /// existence or the size of its package to a non-owner.</para>
    /// </summary>
    private static async Task<IResult> DownloadSizesAsync(long setId, HttpContext ctx, Db db, IFileStore store, PackageIngest ingest)
    {
        if (!await canSeeSetMediaAsync(ctx, db, setId))
            return Results.NotFound();

        var manifest = await ingest.GetLatestVersionFilesAsync(setId, ctx.RequestAborted);

        if (manifest.Count == 0)
            return Results.NotFound();

        long full = manifest.Sum(f => f.Size);
        var plan = AudioOnlyPlan.Unavailable;

        try
        {
            var difficulties = new List<(string, byte[])>();

            foreach (var file in manifest.Where(f => AudioOnlyPackage.IsDifficulty(f.Filename)))
            {
                await using var blob = await store.OpenBlobReadAsync(file.Sha256, ctx.RequestAborted);
                using var buffer = new MemoryStream();
                await blob.CopyToAsync(buffer, ctx.RequestAborted);
                difficulties.Add((file.Filename, buffer.ToArray()));
            }

            plan = AudioOnlyPackage.Plan(difficulties);
        }
        catch (FileNotFoundException)
        {
            // A manifest row whose blob is missing: we cannot say what the video is, so we do not
            // offer the variant. The download route makes the same call from the package itself.
        }

        long audioOnly = manifest.Where(f => !plan.Omits(f.Filename)).Sum(f => f.Size);

        return Results.Json(new
        {
            full,
            audioOnly,
            // "There is a smaller package to choose": a set whose difficulties name no video at all
            // has nothing to leave out, so the choice would be two identical downloads.
            audioOnlyAvailable = plan.Available && audioOnly < full,
        });
    }

    /// <summary>
    /// True when this request represents the start of a logical download: no Range header, an
    /// unparseable one (ignored by range processing, served as a full 200), or a range starting
    /// at byte 0. Mid-file continuations (non-zero start) and suffix ranges don't count; they
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

    /// <summary>
    /// True when this request should be handed to the public bucket: the bucket is configured and
    /// the request did NOT arrive on a direct-origin host. The direct hosts (bss.typebeat.sh and
    /// its legacy twin) exist for the players whose sustained transfers stall on the Cloudflare
    /// path, and the bucket's custom domain is on that path, so they always stream from the box.
    /// </summary>
    private static bool wantsRedirect(HttpContext ctx, IPublicObjectStore publicStore)
        => publicStore.Enabled && !publicStore.IsDirectHost(ctx.Request.Host.Host);

    /// <summary>
    /// The 302 itself. <c>no-store</c> because the redirect is issued AFTER the visibility gate and
    /// the download counter: a cached 302 would let the edge or a browser skip both.
    /// </summary>
    private static IResult redirectTo(HttpContext ctx, IPublicObjectStore publicStore, string key)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(publicStore.PublicUrl(key), permanent: false);
    }

    /// <summary>Published sets (BeatmapsetEndpoints.IsPublished) are world-readable; hidden/removed media only for the owner.</summary>
    private static async Task<bool> canSeeSetMediaAsync(HttpContext ctx, Db db, long setId)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return false;

        if (BeatmapsetEndpoints.IsPublished(row.Status))
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
    // system's violet→magenta primary gradient, violet leads, magenta only enters at the tail
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

    private sealed record DownloadRow(string Status, long OwnerId, string Artist, string Title, string? PackageKey, string? PublicKey);
}
