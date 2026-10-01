using Microsoft.Extensions.Configuration;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Ops;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// The disk guard (backlog 365): the level classification, the failed-probe rule, the cache
/// window, the postgres probe fallback, the tolerant env parsing, and the /health body. All pure or
/// driven through the injectable probe and clock, so no host is needed (the suite adds no new
/// WebApplicationFactory); the refusals themselves are pinned on the existing hosts in
/// Website/DiskGuardSiteTest and Bss/BssDiskGuardTest.
/// </summary>
public class DiskGuardTest
{
    private const long gib = 1024L * 1024 * 1024;

    private static readonly DiskGuardOptions defaults = new(
        LowBytes: 10 * gib,
        UploadFloorBytes: 5 * gib,
        CriticalBytes: 2 * gib,
        CacheWindow: TimeSpan.FromSeconds(15),
        FileRoot: "/data",
        PgProbePath: null);

    private static OpsEndpoints.DiskReadout disk(long freeBytes, long totalBytes = 75 * gib)
        => OpsEndpoints.Describe(totalBytes, freeBytes);

    // ---- classification at each boundary ----

    [TestCase(20 * gib, DiskLevel.Normal)]
    [TestCase(10 * gib, DiskLevel.Normal)] // exactly at the line is not under it
    [TestCase(10 * gib - 1, DiskLevel.Low)]
    [TestCase(5 * gib, DiskLevel.Low)]
    [TestCase(5 * gib - 1, DiskLevel.UploadsRefused)]
    [TestCase(2 * gib, DiskLevel.UploadsRefused)]
    [TestCase(2 * gib - 1, DiskLevel.Critical)]
    [TestCase(0L, DiskLevel.Critical)]
    public void Classify_SharedDevice_EachBoundary(long free, DiskLevel expected)
    {
        // Today's box: no separate pg probe, so the postgres reading IS the file root's.
        var reading = disk(free);
        Assert.That(DiskGuard.Classify(reading, reading, defaults), Is.EqualTo(expected));
    }

    [Test]
    public void Classify_DistinctDevices_CriticalReadsPostgres_FloorReadsTheFileRoot()
    {
        Assert.Multiple(() =>
        {
            // Plenty of room for uploads, but Postgres' own disk is nearly full: critical.
            Assert.That(DiskGuard.Classify(disk(50 * gib), disk(1 * gib), defaults), Is.EqualTo(DiskLevel.Critical));

            // The reverse: the file root under the floor while Postgres is fine refuses uploads only.
            Assert.That(DiskGuard.Classify(disk(3 * gib), disk(50 * gib), defaults), Is.EqualTo(DiskLevel.UploadsRefused));

            // A file root under the CRITICAL figure is still only the upload floor when Postgres
            // lives elsewhere: tokens are about WAL headroom, not about the uploads volume.
            Assert.That(DiskGuard.Classify(disk(1 * gib), disk(50 * gib), defaults), Is.EqualTo(DiskLevel.UploadsRefused));

            // Low reads whichever is smaller.
            Assert.That(DiskGuard.Classify(disk(50 * gib), disk(8 * gib), defaults), Is.EqualTo(DiskLevel.Low));
        });
    }

    // ---- an unusable or failed probe is Unknown and never refuses ----

    [Test]
    public void Classify_UnusableProbe_IsUnknown_AndNeverRefuses()
    {
        var zeroSize = OpsEndpoints.Describe(0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(DiskGuard.Classify(null, null, defaults), Is.EqualTo(DiskLevel.Unknown));
            Assert.That(DiskGuard.Classify(zeroSize, zeroSize, defaults), Is.EqualTo(DiskLevel.Unknown),
                "a zero-size filesystem reads 0 free, which must not read as 'critical' either");

            // One probe blind, the other healthy: Unknown, not Normal.
            Assert.That(DiskGuard.Classify(disk(50 * gib), null, defaults), Is.EqualTo(DiskLevel.Unknown));
            Assert.That(DiskGuard.Classify(null, disk(50 * gib), defaults), Is.EqualTo(DiskLevel.Unknown));

            // One probe blind, the other a real refusal: the refusal stands (it is knowledge).
            Assert.That(DiskGuard.Classify(null, disk(1 * gib), defaults), Is.EqualTo(DiskLevel.Critical));
            Assert.That(DiskGuard.Classify(disk(3 * gib), null, defaults), Is.EqualTo(DiskLevel.UploadsRefused));
        });
    }

    [Test]
    public void FailedProbe_GivesUnknown_WithTheReason_AndRefusesNothing()
    {
        var guard = new DiskGuard(defaults, probe: _ => throw new IOException("no such device"));

        var status = guard.Current;

        Assert.Multiple(() =>
        {
            Assert.That(status.Level, Is.EqualTo(DiskLevel.Unknown));
            Assert.That(status.Files, Is.Null);
            Assert.That(status.FilesError, Is.EqualTo("no such device"));
            Assert.That(status.UploadsRefused, Is.False);
            Assert.That(status.TokensRefused, Is.False);
            Assert.That(DiskGuard.HealthBody(status), Is.EqualTo("ok disk-unknown"));
        });
    }

    // ---- the cache window ----

    [Test]
    public void Current_IsCachedForTheWindow_ThenReMeasured()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        int probes = 0;
        long free = 50 * gib;

        var guard = new DiskGuard(defaults, probe: _ =>
        {
            probes++;
            return (75 * gib, free);
        }, clock: clock);

        Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.Normal));
        Assert.That(probes, Is.EqualTo(1));

        free = 1 * gib;
        clock.Advance(TimeSpan.FromSeconds(14));

        Assert.Multiple(() =>
        {
            Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.Normal), "inside the window the cached reading answers");
            Assert.That(probes, Is.EqualTo(1));
        });

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.Critical), "at the window's end the disk is read again");
            Assert.That(probes, Is.EqualTo(2));
        });

        // Refresh always measures, whatever the window says.
        guard.Refresh();
        Assert.That(probes, Is.EqualTo(3));
    }

    [Test]
    public void ProbeOverride_DropsTheCache_AndWinsOverTheRealProbe()
    {
        var guard = new DiskGuard(defaults, probe: _ => (75 * gib, 50 * gib));
        Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.Normal));

        guard.ProbeOverride = _ => (75 * gib, 3 * gib);
        Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.UploadsRefused));

        guard.ProbeOverride = null;
        Assert.That(guard.Current.Level, Is.EqualTo(DiskLevel.Normal));
    }

    // ---- the postgres probe ----

    [Test]
    public void PgProbe_FallsBackToTheFileRoot_WhenUnset()
    {
        var probed = new List<string>();
        var guard = new DiskGuard(defaults, probe: path =>
        {
            probed.Add(path);
            return (75 * gib, 50 * gib);
        });

        var status = guard.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(probed, Is.EqualTo(new[] { "/data" }), "one statvfs, of the file root");
            Assert.That(status.PgProbeDistinct, Is.False);
            Assert.That(status.Postgres, Is.EqualTo(status.Files));
        });
    }

    [Test]
    public void PgProbe_WhenSet_IsMeasuredSeparately()
    {
        var options = defaults with { PgProbePath = "/pgdata-probe" };
        var guard = new DiskGuard(options, probe: path => path == "/pgdata-probe" ? (100 * gib, 1 * gib) : (75 * gib, 50 * gib));

        var status = guard.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(status.PgProbeDistinct, Is.True);
            Assert.That(status.Files!.Value.FreeBytes, Is.EqualTo(50 * gib));
            Assert.That(status.Postgres!.Value.FreeBytes, Is.EqualTo(1 * gib));
            Assert.That(status.Level, Is.EqualTo(DiskLevel.Critical));
            Assert.That(status.DecidingFreeBytes, Is.EqualTo(1 * gib), "the critical message names the postgres figure");
        });
    }

    // ---- env parsing ----

    [Test]
    public void FromConfiguration_EmptyStringsAndRubbish_AreTheDefaults()
    {
        // Compose passes an unset variable through as "" (Program.cs, Flags): it must not crash the
        // boot and must not read as zero, which would switch the guard off.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DiskGuardOptions.LowKey] = "",
            [DiskGuardOptions.UploadFloorKey] = "not a number",
            [DiskGuardOptions.CriticalKey] = "-3",
            [DiskGuardOptions.ProbeSecondsKey] = "",
            [DiskGuardOptions.PgProbePathKey] = "",
        }).Build();

        var options = DiskGuardOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.LowBytes, Is.EqualTo(10 * gib));
            Assert.That(options.UploadFloorBytes, Is.EqualTo(5 * gib));
            Assert.That(options.CriticalBytes, Is.EqualTo(2 * gib));
            Assert.That(options.CacheWindow, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(options.PgProbePath, Is.Null, "an empty probe path means 'use the file root'");
            Assert.That(options.PgProbeDistinct, Is.False);
        });
    }

    [Test]
    public void FromConfiguration_ReadsInvariantDecimals()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DiskGuardOptions.LowKey] = "12.5",
            [DiskGuardOptions.UploadFloorKey] = "0",
            [DiskGuardOptions.PgProbePathKey] = "/pgdata-probe",
        }).Build();

        var options = DiskGuardOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.LowBytes, Is.EqualTo(12 * gib + gib / 2));
            Assert.That(options.UploadFloorBytes, Is.Zero, "an explicit 0 is honoured: the floor is off");
            Assert.That(options.PgProbePath, Is.EqualTo("/pgdata-probe"));
        });
    }

    [Test]
    public void ReplaySweepMode_DefaultsToDryRun()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HousekeepingOptions.ParseMode(null), Is.EqualTo(ReplaySweepMode.DryRun));
            Assert.That(HousekeepingOptions.ParseMode(""), Is.EqualTo(ReplaySweepMode.DryRun));
            Assert.That(HousekeepingOptions.ParseMode("yes please"), Is.EqualTo(ReplaySweepMode.DryRun));
            Assert.That(HousekeepingOptions.ParseMode("dryrun"), Is.EqualTo(ReplaySweepMode.DryRun));
            Assert.That(HousekeepingOptions.ParseMode(" ON "), Is.EqualTo(ReplaySweepMode.On));
            Assert.That(HousekeepingOptions.ParseMode("off"), Is.EqualTo(ReplaySweepMode.Off));
            Assert.That(HousekeepingOptions.FromConfiguration(new ConfigurationBuilder().Build()).ReplaySweep, Is.EqualTo(ReplaySweepMode.DryRun));
        });
    }

    // ---- the /health body ----

    [Test]
    public void HealthBody_Healthy_IsExactlyOk()
    {
        var guard = new DiskGuard(defaults, probe: _ => (75 * gib, 50 * gib));
        Assert.That(DiskGuard.HealthBody(guard.Current), Is.EqualTo("ok"));
    }

    [Test]
    public void HealthBody_Low_StillStartsWithOk()
    {
        var guard = new DiskGuard(defaults, probe: _ => (75 * gib, 8 * gib));
        Assert.That(DiskGuard.HealthBody(guard.Current), Is.EqualTo("ok disk-low 8.0 GiB free"));
    }

    [TestCase(3L * 1024 * 1024 * 1024, "degraded: disk 3.0 GiB free, writes refused")]
    [TestCase(1L * 1024 * 1024 * 1024, "degraded: disk 1.0 GiB free, writes refused, scores paused")]
    [TestCase(0L, "degraded: disk 0.0 GiB free, writes refused, scores paused")]
    public void HealthBody_Degraded_NeverContainsOk(long free, string expected)
    {
        var guard = new DiskGuard(defaults, probe: _ => (75 * gib, free));
        string body = DiskGuard.HealthBody(guard.Current);

        Assert.Multiple(() =>
        {
            Assert.That(body, Is.EqualTo(expected));
            // UptimeRobot's keyword monitor matches "ok" anywhere: one stray "token" or "book" in this
            // body and the monitor would stay green through the outage it exists for.
            Assert.That(body, Does.Not.Contain("ok").IgnoreCase);
            // CI's post-deploy grep (ci.yml) accepts it, so a deploy onto a full disk is not red.
            Assert.That(body, Does.Match("^(ok|degraded)"));
        });
    }

    // ---- the ops endpoint's level is a pure function of the reading ----

    [Test]
    public void OpsLevel_IsAStatelessClassification()
    {
        // Same numbers in, same level out, whatever was answered before: no edge, no memory.
        var guard = new DiskGuard(defaults, probe: _ => (75 * gib, 3 * gib));

        var first = guard.Refresh();
        guard.ProbeOverride = _ => (75 * gib, 50 * gib);
        guard.Refresh();
        guard.ProbeOverride = null;
        var again = guard.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(again.Level, Is.EqualTo(first.Level));
            Assert.That(again.Level, Is.EqualTo(DiskGuard.Classify(again.Files, again.Postgres, defaults)));
            Assert.That(OpsEndpoints.LevelName(DiskLevel.Normal), Is.EqualTo("normal"));
            Assert.That(OpsEndpoints.LevelName(DiskLevel.Low), Is.EqualTo("low"));
            Assert.That(OpsEndpoints.LevelName(DiskLevel.UploadsRefused), Is.EqualTo("uploads_refused"));
            Assert.That(OpsEndpoints.LevelName(DiskLevel.Critical), Is.EqualTo("critical"));
            Assert.That(OpsEndpoints.LevelName(DiskLevel.Unknown), Is.EqualTo("unknown"));
        });
    }

    [Test]
    public void StartupLine_NamesLevelThresholdsAndTheProbe_AndNeverThrows()
    {
        var guard = new DiskGuard(defaults, probe: _ => throw new UnauthorizedAccessException("denied"));
        string line = guard.StartupLine();

        Assert.Multiple(() =>
        {
            Assert.That(line, Does.StartWith("Disk: guard level Unknown;"));
            Assert.That(line, Does.Contain("uploads refused under 5.0 GiB"));
            Assert.That(line, Does.Contain("score tokens refused under 2.0 GiB"));
            Assert.That(line, Does.Contain("TYPEBEAT_PG_PROBE_PATH unset"));
            Assert.That(line, Does.Contain("file root unreadable (denied)"));
        });
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
