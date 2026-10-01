using System.Globalization;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Storage;

/// <summary>
/// How much room is left, as a decision rather than a number (backlog 365).
///
/// <para>Ordered by severity, except <see cref="Unknown"/>, which is not a severity at all: a probe
/// that failed or read back as a zero-size filesystem says nothing about the disk, so it never
/// refuses a write. It is still reported (on /health and the ops endpoint) so a blind instrument
/// is visible.</para>
/// </summary>
public enum DiskLevel
{
    Unknown,

    /// <summary>Above every threshold.</summary>
    Normal,

    /// <summary>Under <see cref="DiskGuardOptions.LowBytes"/>. Report only: nothing is refused.</summary>
    Low,

    /// <summary>
    /// The FILE ROOT is under <see cref="DiskGuardOptions.UploadFloorBytes"/>: replay uploads, every
    /// BSS package write and avatar/banner uploads are refused, so user uploads cannot eat the last
    /// gigabytes Postgres needs. Score submission is NEVER refused.
    /// </summary>
    UploadsRefused,

    /// <summary>
    /// The POSTGRES filesystem is under <see cref="DiskGuardOptions.CriticalBytes"/> (WAL headroom:
    /// the default max_wal_size is 1 GB). Everything <see cref="UploadsRefused"/> refuses, plus new
    /// score TOKENS, so a player is told before playing rather than after. A submission for a token
    /// already issued is still accepted: it is one row.
    /// </summary>
    Critical,
}

/// <summary>The thresholds and probe paths, env-configured with tolerant parsing.</summary>
public sealed record DiskGuardOptions(
    long LowBytes,
    long UploadFloorBytes,
    long CriticalBytes,
    TimeSpan CacheWindow,
    string FileRoot,
    string? PgProbePath)
{
    public const string LowKey = "TYPEBEAT_DISK_LOW_GB";
    public const string UploadFloorKey = "TYPEBEAT_DISK_UPLOAD_FLOOR_GB";
    public const string CriticalKey = "TYPEBEAT_DISK_CRITICAL_GB";
    public const string ProbeSecondsKey = "TYPEBEAT_DISK_PROBE_SECONDS";
    public const string PgProbePathKey = "TYPEBEAT_PG_PROBE_PATH";

    public const double DefaultLowGb = 10;
    public const double DefaultUploadFloorGb = 5;
    public const double DefaultCriticalGb = 2;
    public const double DefaultProbeSeconds = 15;

    private const long gib = 1024L * 1024 * 1024;

    /// <summary>Whether Postgres is watched on its own filesystem rather than through the file root.</summary>
    public bool PgProbeDistinct => PgProbePath is { Length: > 0 };

    public static DiskGuardOptions FromConfiguration(IConfiguration config)
    {
        string? pgPath = config[PgProbePathKey];

        return new DiskGuardOptions(
            LowBytes: GigabytesToBytes(ParseNonNegative(config[LowKey], DefaultLowGb)),
            UploadFloorBytes: GigabytesToBytes(ParseNonNegative(config[UploadFloorKey], DefaultUploadFloorGb)),
            CriticalBytes: GigabytesToBytes(ParseNonNegative(config[CriticalKey], DefaultCriticalGb)),
            CacheWindow: TimeSpan.FromSeconds(ParseNonNegative(config[ProbeSecondsKey], DefaultProbeSeconds)),
            FileRoot: OpsEndpoints.FileRoot(config),
            PgProbePath: string.IsNullOrWhiteSpace(pgPath) ? null : pgPath);
    }

    /// <summary>
    /// Tolerant number parsing, for the same reason as <see cref="Flags"/>: compose passes an unset
    /// variable through as an EMPTY STRING, and a threshold that crashed the boot (or silently read
    /// as zero, which disables the guard) over that would be worse than the default.
    /// Anything that is not a finite, non-negative invariant-culture number is the fallback.
    /// </summary>
    public static double ParseNonNegative(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
           && double.IsFinite(parsed) && parsed >= 0
            ? parsed
            : fallback;

    /// <summary>GiB, not GB: the same unit the boot line and `df -h` print.</summary>
    public static long GigabytesToBytes(double gigabytes) => (long)Math.Round(gigabytes * gib);
}

/// <summary>
/// One classified reading. <see cref="Files"/> is the file root's filesystem; <see cref="Postgres"/>
/// is the filesystem the critical level watches, which is the same reading as <see cref="Files"/>
/// when no separate probe path is configured. Either is null when its probe failed, with the reason
/// in the matching error.
/// </summary>
public sealed record DiskStatus(
    DiskLevel Level,
    OpsEndpoints.DiskReadout? Files,
    OpsEndpoints.DiskReadout? Postgres,
    bool PgProbeDistinct,
    string? FilesError,
    string? PostgresError,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Replay, BSS and avatar/banner writes are refused.</summary>
    public bool UploadsRefused => Level is DiskLevel.UploadsRefused or DiskLevel.Critical;

    /// <summary>New score tokens are refused (and only then: submission never is).</summary>
    public bool TokensRefused => Level is DiskLevel.Critical;

    /// <summary>
    /// The free space that decided the level, for messages: the Postgres filesystem at
    /// <see cref="DiskLevel.Critical"/>, the file root otherwise, and the smaller of the two at
    /// <see cref="DiskLevel.Low"/>.
    /// </summary>
    public long? DecidingFreeBytes => Level switch
    {
        DiskLevel.Critical => Postgres?.FreeBytes,
        DiskLevel.Low => minFree(Files, Postgres),
        _ => Files?.FreeBytes,
    };

    private static long? minFree(OpsEndpoints.DiskReadout? a, OpsEndpoints.DiskReadout? b)
    {
        long? x = a is { } ra && OpsEndpoints.Usable(ra) ? ra.FreeBytes : null;
        long? y = b is { } rb && OpsEndpoints.Usable(rb) ? rb.FreeBytes : null;

        if (x is null)
            return y;

        return y is null ? x : Math.Min(x.Value, y.Value);
    }
}

/// <summary>
/// The app-side disk guard (backlog 365): refuses the writes that can be refused before the disk
/// fills to the point where Postgres dies, which is what took the whole site down twice
/// (2026-09-04 and 2026-09-26) while every upload kept succeeding right up to the end.
///
/// <para>ONE measurement implementation: both probes go through <see cref="OpsEndpoints.Measure"/>
/// and its <see cref="OpsEndpoints.ProbeFilesystem"/>, so the guard, GET /api/v2/ops/disk and the
/// boot line can never disagree about which filesystem they read or how.</para>
///
/// <para>Probe A is the file root (<see cref="OpsEndpoints.FileRoot"/>): uploads land there, so the
/// upload floor reads it. Probe B is Postgres' own filesystem when <c>TYPEBEAT_PG_PROBE_PATH</c>
/// names one (prod: a read-only mount of the pgdata volume) and falls back to probe A otherwise,
/// which is true on today's box, where pgdata and appdata share the root disk.</para>
///
/// <para>The reading is cached for <see cref="DiskGuardOptions.CacheWindow"/> (15 s by default), so
/// /health and every guarded write share one statvfs per window. The level itself is a STATELESS
/// classification of the reading (<see cref="Classify"/>): no hysteresis, no memory of past
/// answers, which keeps the ops endpoint's "no server-side edge" rule intact. The only state is the
/// last level, kept to log a Warning on each transition.</para>
/// </summary>
public sealed class DiskGuard
{
    private readonly Func<string, (long TotalBytes, long FreeBytes)> probe;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;
    private readonly object gate = new();

    private DiskStatus? cached;
    private DiskLevel? lastLogged;
    private Func<string, (long TotalBytes, long FreeBytes)>? probeOverride;

    public DiskGuard(DiskGuardOptions options, Func<string, (long TotalBytes, long FreeBytes)>? probe = null, TimeProvider? clock = null, ILogger? logger = null)
    {
        Options = options;
        this.probe = probe ?? OpsEndpoints.ProbeFilesystem;
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger;
    }

    public DiskGuardOptions Options { get; }

    /// <summary>
    /// TEST SEAM: replaces the probe for both paths and drops the cached reading, so a test on an
    /// existing host can put the server "below the floor" without filling a disk. Set back to null
    /// in a finally. Never set in production code.
    /// </summary>
    public Func<string, (long TotalBytes, long FreeBytes)>? ProbeOverride
    {
        get => probeOverride;
        set
        {
            lock (gate)
            {
                probeOverride = value;
                cached = null;
            }
        }
    }

    /// <summary>The cached reading, re-measured once the cache window has passed.</summary>
    public DiskStatus Current
    {
        get
        {
            lock (gate)
            {
                if (cached is { } c && clock.GetUtcNow() - c.MeasuredAt < Options.CacheWindow)
                    return c;
            }

            return Refresh();
        }
    }

    /// <summary>Measures now, regardless of the cache, and caches the result.</summary>
    public DiskStatus Refresh()
    {
        var active = probeOverride ?? probe;
        var now = clock.GetUtcNow();

        var (files, filesError) = measure(Options.FileRoot, active);

        OpsEndpoints.DiskReadout? pg = files;
        string? pgError = filesError;

        if (Options.PgProbeDistinct)
            (pg, pgError) = measure(Options.PgProbePath!, active);

        var status = new DiskStatus(Classify(files, pg, Options), files, pg, Options.PgProbeDistinct, filesError, pgError, now);

        DiskLevel? previous;

        lock (gate)
        {
            cached = status;
            previous = lastLogged;
            lastLogged = status.Level;
        }

        // A Warning on every transition (including recovery), so `docker logs | grep Disk` shows
        // when writes started and stopped being refused. The first reading only logs when it is
        // already off Normal: a healthy boot says so in the boot line instead.
        if (previous != status.Level && (previous is not null || status.Level != DiskLevel.Normal))
        {
            logger?.LogWarning("Disk: level {Previous} -> {Level} ({Description}).",
                previous?.ToString() ?? "(boot)", status.Level, Describe(status));
        }

        return status;
    }

    private static (OpsEndpoints.DiskReadout? Readout, string? Error) measure(string path, Func<string, (long TotalBytes, long FreeBytes)> probe)
    {
        try
        {
            return (OpsEndpoints.Measure(path, probe), null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The whole decision, pure. Critical reads Postgres' filesystem, the upload floor reads the
    /// file root, Low reads whichever is smaller. A probe that failed or read back as zero size
    /// contributes nothing: it can never cause a refusal, and when nothing ELSE decided a level the
    /// answer is <see cref="DiskLevel.Unknown"/> rather than a reassuring Normal.
    /// </summary>
    public static DiskLevel Classify(OpsEndpoints.DiskReadout? files, OpsEndpoints.DiskReadout? postgres, DiskGuardOptions options)
    {
        long? filesFree = files is { } f && OpsEndpoints.Usable(f) ? f.FreeBytes : null;
        long? pgFree = postgres is { } p && OpsEndpoints.Usable(p) ? p.FreeBytes : null;

        if (pgFree < options.CriticalBytes)
            return DiskLevel.Critical;

        if (filesFree < options.UploadFloorBytes)
            return DiskLevel.UploadsRefused;

        if (filesFree < options.LowBytes || pgFree < options.LowBytes)
            return DiskLevel.Low;

        if (filesFree is null || pgFree is null)
            return DiskLevel.Unknown;

        return DiskLevel.Normal;
    }

    /// <summary>
    /// The /health body. <c>ok</c> while nothing is refused (with a note when low or blind, which
    /// still matches UptimeRobot's "ok" keyword and CI's <c>^(ok|degraded)</c> grep); once writes are
    /// refused it is a body that does NOT contain the substring "ok" anywhere, so the keyword monitor
    /// misses and alerts on its own, independently of the Discord bot. Mind the vocabulary: "token"
    /// and "book" both contain it, which is why the critical note says "scores paused".
    /// </summary>
    public static string HealthBody(DiskStatus status) => status.Level switch
    {
        DiskLevel.Normal => "ok",
        DiskLevel.Low => FormattableString.Invariant($"ok disk-low {Gib(status.DecidingFreeBytes ?? 0)} GiB free"),
        DiskLevel.Unknown => "ok disk-unknown",
        DiskLevel.UploadsRefused => FormattableString.Invariant($"degraded: disk {Gib(status.DecidingFreeBytes ?? 0)} GiB free, writes refused"),
        _ => FormattableString.Invariant($"degraded: disk {Gib(status.DecidingFreeBytes ?? 0)} GiB free, writes refused, scores paused"),
    };

    /// <summary>The message a refused write carries (BSS, replay, settings).</summary>
    public static string UploadRefusal(DiskStatus status)
        => FormattableString.Invariant($"The server is low on storage ({Gib(status.DecidingFreeBytes ?? 0)} GiB free), so uploads are paused until space is freed. Please try again later.");

    /// <summary>The message a refused score token carries.</summary>
    public const string TokenRefusal = "server storage is full, scores are paused";

    /// <summary>One line for logs: level and both readings.</summary>
    public static string Describe(DiskStatus status)
    {
        string files = status.Files is { } f
            ? FormattableString.Invariant($"file root {Gib(f.FreeBytes)} GiB free of {Gib(f.TotalBytes)} GiB")
            : $"file root unreadable ({status.FilesError})";

        if (!status.PgProbeDistinct)
            return files + ", postgres shares it";

        string pg = status.Postgres is { } p
            ? FormattableString.Invariant($"postgres {Gib(p.FreeBytes)} GiB free of {Gib(p.TotalBytes)} GiB")
            : $"postgres unreadable ({status.PostgresError})";

        return files + ", " + pg;
    }

    /// <summary>
    /// The boot line's second half: level, thresholds and which filesystem the critical level
    /// watches, so `docker logs | grep Disk:` also answers "what would the guard do right now".
    /// Never throws (the probe failure is part of the status).
    /// </summary>
    public string StartupLine()
    {
        var status = Refresh();

        string pgProbe = Options.PgProbeDistinct
            ? $"postgres probed at '{Options.PgProbePath}'"
            : "postgres probed through the file root (TYPEBEAT_PG_PROBE_PATH unset)";

        return FormattableString.Invariant(
            $"Disk: guard level {status.Level}; low under {Gib(Options.LowBytes)} GiB, uploads refused under {Gib(Options.UploadFloorBytes)} GiB, score tokens refused under {Gib(Options.CriticalBytes)} GiB; {pgProbe}; {Describe(status)}.");
    }

    public static string Gib(long bytes)
        => (bytes / (double)(1024L * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
}
