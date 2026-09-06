using Typebeat.Web.Storage;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Private operational readouts for the Discord bot ("Buddy", discord-buddybot), on the same
/// footing as <see cref="BuddyEndpoints"/>: not part of the osu-compatible /api/v2 wire surface
/// the game client speaks, guarded by the same single static key (<c>TYPEBEAT_BUDDY_KEY</c>) via
/// <see cref="BuddyEndpoints.Authorised"/>, and 404 (not 401) when the key is unset so a deploy
/// that has not opted in exposes nothing.
///
/// <para>Why this exists: on 2026-09-04 the production box filled its 75 GB disk, Postgres went
/// unhealthy (57P03, "the database system is in recovery mode") and every page 500'd with no
/// warning anywhere. Nothing was watching free space. The bot now polls this and shouts in
/// Discord before the disk is full.</para>
///
/// <para>GET /api/v2/ops/disk returns <c>{ totalBytes, freeBytes, usedPercent }</c> for the
/// filesystem holding <c>TYPEBEAT_FILE_ROOT</c> (the /data appdata volume in prod). That volume
/// is a plain directory on the host filesystem, not a separate device, so statvfs of /data
/// reports the REAL host disk: the same number `df -h /` shows, which is the number that
/// actually matters, since the release packages, the docker build cache and the container logs
/// that filled the box all live outside /data.</para>
///
/// <para>EDGE NOTE: this endpoint is a dumb readout. It holds no threshold, no state and no
/// memory of what it last answered; it does not know an alert exists. The 80 percent alert is
/// EDGE-TRIGGERED CLIENT-SIDE by the bot, which remembers whether it was already over the line
/// and posts only on the crossing. Do not add hysteresis, a "should I alert" flag or a
/// last-alerted timestamp here: two consumers polling the same server-side edge would each eat
/// the other's transition.</para>
/// </summary>
public static class OpsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/ops/disk", Disk);
    }

    private static IResult Disk(HttpContext ctx, IConfiguration config)
    {
        // Same gate as the score feed: unset key = the whole feature is off and 404s.
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        // The file root the app itself writes to, resolved exactly as LocalFileStore resolves it,
        // so the readout can never describe a different filesystem than the uploads land on.
        string root = config[LocalFileStore.RootConfigKey] is { Length: > 0 } configured
            ? configured
            : LocalFileStore.DefaultRoot;

        DiskReadout readout;

        try
        {
            readout = Measure(root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A readout that cannot be taken must not answer 200 with zeroes: a "0 percent used"
            // is indistinguishable from a healthy disk and would silence the alert permanently.
            return Results.Problem(
                $"could not read disk usage for '{root}': {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new
        {
            totalBytes = readout.TotalBytes,
            freeBytes = readout.FreeBytes,
            usedPercent = readout.UsedPercent,
        });
    }

    /// <summary>
    /// Reads the filesystem holding <paramref name="path"/>. The probe is injectable so the
    /// numbers can be driven from a test without a real disk in a given state; the default
    /// probe is <see cref="ProbeFilesystem"/>.
    /// </summary>
    public static DiskReadout Measure(string path, Func<string, (long TotalBytes, long FreeBytes)>? probe = null)
    {
        (long total, long free) = (probe ?? ProbeFilesystem)(path);
        return Describe(total, free);
    }

    /// <summary>
    /// The whole computation, pure, so it is testable anywhere (per CLAUDE.md: no new
    /// WebApplicationFactory host to prove arithmetic).
    ///
    /// <para><c>usedPercent</c> is measured against the TOTAL size while <c>freeBytes</c> is what
    /// is available to a non-root writer, so on a filesystem with root-reserved blocks (ext4
    /// reserves 5 percent by default) this reads a little HIGHER than `df`'s Use%, which divides
    /// by used+available instead. That direction is the safe one for an alert: it fires slightly
    /// early rather than slightly late.</para>
    ///
    /// <para>Degenerate inputs are clamped rather than thrown on, because the caller is an alert
    /// path: a probe that answers nonsense should still produce a number the bot can compare.</para>
    /// </summary>
    public static DiskReadout Describe(long totalBytes, long freeBytes)
    {
        long total = Math.Max(0, totalBytes);
        long free = Math.Clamp(freeBytes, 0, total);

        double usedPercent = total == 0 ? 0 : Math.Round((total - free) * 100.0 / total, 2);

        return new DiskReadout(total, free, usedPercent);
    }

    /// <summary>
    /// The real probe.
    ///
    /// <para>On Unix <see cref="DriveInfo"/> statvfs's the name it is given, so it is handed the
    /// path itself and reports the filesystem that path actually sits on (a bind-mounted volume
    /// included). On Windows the constructor only accepts a drive root, so the path is mapped
    /// onto its root first; that is the dev/test shape, where the file root is a local
    /// directory.</para>
    /// </summary>
    public static (long TotalBytes, long FreeBytes) ProbeFilesystem(string path)
    {
        var drive = new DriveInfo(driveNameFor(path));
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    private static string driveNameFor(string path)
    {
        string full = Path.GetFullPath(path);

        return OperatingSystem.IsWindows()
            ? Path.GetPathRoot(full) ?? full
            : full;
    }

    /// <summary>One filesystem's occupancy. <paramref name="UsedPercent"/> is 0-100, two decimals.</summary>
    public readonly record struct DiskReadout(long TotalBytes, long FreeBytes, double UsedPercent);
}
