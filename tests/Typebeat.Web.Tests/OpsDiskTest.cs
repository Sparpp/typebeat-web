using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests;

/// <summary>
/// The disk readout behind GET /api/v2/ops/disk (backlog 268), the number the Discord bot alerts
/// on after the 2026-09-04 disk-full outage.
///
/// Driven through the pure <see cref="OpsEndpoints.Describe"/> and the probe seam on
/// <see cref="OpsEndpoints.Measure"/> rather than over HTTP: per CLAUDE.md a new
/// WebApplicationFactory host would destabilise the shared fixture, and none of this arithmetic
/// needs one. The key gate itself is exercised on the EXISTING website host in
/// Website/ApiRegressionGuardTest.
/// </summary>
public class OpsDiskTest
{
    [TestCase(100L, 100L, 0d)]
    [TestCase(100L, 0L, 100d)]
    [TestCase(100L, 20L, 80d)]
    [TestCase(100L, 21L, 79d)]
    public void Describe_PercentIsUsedOverTotal(long total, long free, double expected)
    {
        Assert.That(OpsEndpoints.Describe(total, free).UsedPercent, Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void Describe_MatchesTheOutageBox()
    {
        // 75 GB disk with 6 GB left, the shape the box was in on the way down.
        const long total = 75L * 1024 * 1024 * 1024;
        const long free = 6L * 1024 * 1024 * 1024;

        var readout = OpsEndpoints.Describe(total, free);

        Assert.Multiple(() =>
        {
            Assert.That(readout.TotalBytes, Is.EqualTo(total));
            Assert.That(readout.FreeBytes, Is.EqualTo(free));
            // 69/75 = 92%, comfortably past the bot's 80 percent line.
            Assert.That(readout.UsedPercent, Is.EqualTo(92d).Within(0.01));
        });
    }

    [Test]
    public void Describe_ClampsNonsenseRatherThanThrowing()
    {
        // The caller is an alert path: a probe answering rubbish must still yield a comparable
        // number instead of a 503 that reads as "no problem" to a careless consumer.
        Assert.Multiple(() =>
        {
            Assert.That(OpsEndpoints.Describe(0, 0).UsedPercent, Is.EqualTo(0d));
            Assert.That(OpsEndpoints.Describe(-5, -5).TotalBytes, Is.EqualTo(0L));
            Assert.That(OpsEndpoints.Describe(100, -5).UsedPercent, Is.EqualTo(100d));
            // More free than exists: clamped to the total, so the percent stays inside 0-100.
            Assert.That(OpsEndpoints.Describe(100, 500).FreeBytes, Is.EqualTo(100L));
            Assert.That(OpsEndpoints.Describe(100, 500).UsedPercent, Is.EqualTo(0d));
        });
    }

    [Test]
    public void Measure_ReadsTheInjectedProbe()
    {
        string? asked = null;

        var readout = OpsEndpoints.Measure("/data", path =>
        {
            asked = path;
            return (TotalBytes: 1000, FreeBytes: 250);
        });

        Assert.Multiple(() =>
        {
            // The path reaches the probe untouched: on Unix DriveInfo statvfs's exactly this, so a
            // rewrite here would silently report a different filesystem than /data.
            Assert.That(asked, Is.EqualTo("/data"));
            Assert.That(readout.UsedPercent, Is.EqualTo(75d));
        });
    }

    [Test]
    public void Measure_DefaultProbe_ReadsARealFilesystem()
    {
        // Non-vacuity for the default probe: it has to work on the dev/test box too, where the
        // file root is an ordinary directory rather than a mount point.
        var readout = OpsEndpoints.Measure(TestContext.CurrentContext.WorkDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(readout.TotalBytes, Is.GreaterThan(0L));
            Assert.That(readout.FreeBytes, Is.LessThanOrEqualTo(readout.TotalBytes));
            Assert.That(readout.UsedPercent, Is.InRange(0d, 100d));
        });
    }
}
