using Typebeat.Web.Data;
using Typebeat.Web.Ops;
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
/// <para>WHAT A CONTAINER CAN AND CANNOT SEE, stated honestly because backlog 286 asked whether
/// this readout had been lying: a process inside the app container cannot run the host's `df` and
/// has no view of the host mount table. What it CAN see is the filesystem BACKING the path it
/// statvfs's, and that is a real filesystem, not a container illusion. `appdata` is an ordinary
/// named docker volume, so its data lives under <c>/var/lib/docker/volumes/…/_data</c> on the
/// host, and /data inside the container is a bind of that directory: same device, same free
/// space, so the answer equals the host's `df /`. THE PINNED ASSUMPTION IS THAT THE DOCKER DATA
/// ROOT AND THE ROOT FILESYSTEM ARE ONE DEVICE. Give `appdata` a driver option or a bind onto a
/// separate disk (the box already has one: the 100 GB backup volume at /mnt/typebeat-backups) and
/// this endpoint silently starts watching THAT device while the root filesystem, which is where
/// the docker images, the build cache, the journal and /root staging all live, goes unwatched.
/// Nothing in the code can detect that swap, so it is a deploy-time invariant: see the Disk
/// section of deploy/README.md.</para>
///
/// <para>EDGE NOTE: this endpoint is a dumb readout. It holds no threshold, no state and no
/// memory of what it last answered; it does not know an alert exists. The 80 percent alert is
/// EDGE-TRIGGERED CLIENT-SIDE by the bot, which remembers whether it was already over the line
/// and posts only on the crossing. Do not add hysteresis, a "should I alert" flag or a
/// last-alerted timestamp here: two consumers polling the same server-side edge would each eat
/// the other's transition.</para>
///
/// <para>Backlog 365 added four fields, all additive (the bot reads with .get, so it ignores them
/// until it wants them): <c>level</c>, the <see cref="DiskGuard"/>'s classification of THIS reading
/// (<see cref="DiskGuard.Classify"/>, a pure function of the numbers, not an edge: the endpoint
/// still remembers nothing), <c>pgFreeBytes</c> when Postgres is probed on its own filesystem
/// (<c>TYPEBEAT_PG_PROBE_PATH</c>, null otherwise), <c>replayBytes</c> (SUM of the stored replays'
/// sizes; null when the database cannot answer, since a disk alert must not fail because the
/// database is the thing in trouble) and <c>lastSweepAt</c> (the last completed housekeeping pass,
/// null when it has not run).</para>
/// </summary>
public static class OpsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/ops/disk", Disk);
    }

    private static async Task<IResult> Disk(HttpContext ctx, IConfiguration config, DiskGuard disk, Db db, HousekeepingStatus housekeeping)
    {
        // Same gate as the score feed: unset key = the whole feature is off and 404s.
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        string root = disk.Options.FileRoot;

        // A fresh reading, never the guard's cache: the bot polls every few minutes and deserves
        // the number as of now. Same probe, same Measure, so this IS the guard's view too.
        var status = disk.Refresh();

        if (status.Files is not { } readout)
        {
            // A readout that cannot be taken must not answer 200 with zeroes: a "0 percent used"
            // is indistinguishable from a healthy disk and would silence the alert permanently.
            return Results.Problem(
                $"could not read disk usage for '{root}': {status.FilesError}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // The same rule one level down, and the hole backlog 286 found in it: a probe can SUCCEED
        // and still answer nonsense. statvfs on a filesystem it does not understand reports zero
        // blocks, and Describe then clamps that to "0.00 percent used", which is the single most
        // reassuring number this endpoint can emit. The bot compares it against 80 and stays quiet
        // forever. A zero-size filesystem does not exist, so treat it as the failed probe it is.
        if (!Usable(readout))
        {
            return Results.Problem(
                $"disk usage for '{root}' read back as a zero-size filesystem, which cannot be true: treating it as a failed probe rather than an empty disk",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        long? replayBytes;

        try
        {
            await using var conn = await db.OpenAsync(ctx.RequestAborted);
            replayBytes = await Housekeeping.StoredReplayBytesAsync(conn, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            replayBytes = null;
        }

        return Results.Json(new
        {
            totalBytes = readout.TotalBytes,
            freeBytes = readout.FreeBytes,
            usedPercent = readout.UsedPercent,
            level = LevelName(status.Level),
            pgFreeBytes = status.PgProbeDistinct ? status.Postgres?.FreeBytes : null,
            replayBytes,
            lastSweepAt = housekeeping.LastSweepAt?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>The wire spelling of a level: snake_case, stable, what a bot edge would compare.</summary>
    public static string LevelName(DiskLevel level) => level switch
    {
        DiskLevel.Normal => "normal",
        DiskLevel.Low => "low",
        DiskLevel.UploadsRefused => "uploads_refused",
        DiskLevel.Critical => "critical",
        _ => "unknown",
    };

    /// <summary>
    /// The file root the app itself writes to, resolved exactly as <see cref="LocalFileStore"/>
    /// resolves it, so the readout can never describe a different filesystem than the uploads land
    /// on. Public because the boot-time log line in Program.cs measures the same root, and two
    /// copies of this fallback would be two answers to "which disk is being watched".
    /// </summary>
    public static string FileRoot(IConfiguration config)
        => config[LocalFileStore.RootConfigKey] is { Length: > 0 } configured ? configured : LocalFileStore.DefaultRoot;

    /// <summary>
    /// Whether a readout describes a real filesystem. Only a zero total fails: every other
    /// degenerate input <see cref="Describe"/> clamps stays comparable, but a zero total makes the
    /// percentage meaningless AND maximally reassuring, which is the one direction an alert path
    /// must never fail in.
    /// </summary>
    public static bool Usable(DiskReadout readout) => readout.TotalBytes > 0;

    /// <summary>
    /// The one line the app logs about free space at boot.
    ///
    /// <para>Exists because of the second fill (backlog 286): the alert chain has four separate
    /// ways to be silent (key unset, bot container down, alert channel unset, poller never
    /// deployed) and in every one of them NOTHING anywhere states a number. A deploy always writes
    /// its own log, so this puts the disk in it for free: `docker logs typebeat-web-app-1 | grep
    /// Disk:` answers "how full was the box at the last deploy" with no bot, no key and no
    /// network.</para>
    ///
    /// <para>Deliberately carries no threshold. The 80 percent line lives in the bot
    /// (<c>BUDDY_DISK_THRESHOLD_PCT</c>) and is configurable there; a second copy here to colour a
    /// log level would be a number that could drift from the one that actually alerts. The only
    /// judgement it makes is the unusable case, which is not a threshold but a broken instrument.</para>
    /// </summary>
    public static string StartupLine(string root, DiskReadout readout)
    {
        if (!Usable(readout))
        {
            return FormattableString.Invariant(
                $"Disk: the readout for '{root}' came back as a zero-size filesystem, so GET /api/v2/ops/disk will 503 and the Discord disk alert is BLIND. Check TYPEBEAT_FILE_ROOT and its mount.");
        }

        return FormattableString.Invariant(
            $"Disk: {readout.UsedPercent:0.0} percent used on the filesystem holding '{root}' ({gib(readout.FreeBytes)} GiB free of {gib(readout.TotalBytes)} GiB). On the prod box that filesystem is the root disk, because the appdata volume lives on it.");
    }

    private static string gib(long bytes)
        => (bytes / (double)(1024L * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

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

        if (OperatingSystem.IsWindows())
            return Path.GetPathRoot(full) ?? full;

        // On Unix DriveInfo throws for a path that does not exist yet, and the file root is only
        // created on its first write (a fresh test host, a CI runner, a box before the first
        // upload). The filesystem it WILL sit on is the one its nearest existing ancestor sits on,
        // so walk up to that; the probe then answers the same whether or not the directory exists,
        // instead of reading as Unknown until the first file lands.
        string probe = full;

        while (!Directory.Exists(probe))
        {
            string? parent = Path.GetDirectoryName(probe);

            if (string.IsNullOrEmpty(parent) || parent == probe)
                break;

            probe = parent;
        }

        return probe;
    }

    /// <summary>One filesystem's occupancy. <paramref name="UsedPercent"/> is 0-100, two decimals.</summary>
    public readonly record struct DiskReadout(long TotalBytes, long FreeBytes, double UsedPercent);
}
