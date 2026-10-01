using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests;

/// <summary>
/// The offsite freshness readout behind GET /api/v2/ops/backups (backlog 367): stamp parsing, age
/// arithmetic against an injected clock, and the honesty rule that every bad stamp reads as STALE
/// (null), never fresh. Pure helpers only, like <see cref="OpsDiskTest"/>; the key gate is pinned
/// on the existing website host in Website/ApiRegressionGuardTest.
/// </summary>
public class OpsBackupsTest
{
    private static readonly DateTimeOffset now = new(2026, 10, 3, 3, 17, 42, TimeSpan.Zero);

    // Exactly what deploy/backup.sh's stamp_json prints, trailing newline included.
    private const string db_stamp = "{\"kind\":\"db\",\"file\":\"typebeat_20261001T031700Z.dump.gz\",\"bytes\":12345,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}\n";

    [Test]
    public void Parse_ReadsTheScriptsStampAndAgesIt()
    {
        var stamp = OpsBackupsEndpoints.Parse(db_stamp, "db", now);

        Assert.That(stamp, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stamp!.Value.File, Is.EqualTo("typebeat_20261001T031700Z.dump.gz"));
            Assert.That(stamp.Value.Bytes, Is.EqualTo(12345L));
            Assert.That(stamp.Value.UploadedAt, Is.EqualTo(new DateTimeOffset(2026, 10, 1, 3, 17, 42, TimeSpan.Zero)));
            // Exactly two days: 172800 s, the intended db alert line.
            Assert.That(stamp.Value.AgeSeconds, Is.EqualTo(172800L));
        });
    }

    [Test]
    public void Parse_AgeFloorsToWholeSeconds()
    {
        var stamp = OpsBackupsEndpoints.Parse(db_stamp, "db", new DateTimeOffset(2026, 10, 1, 3, 18, 42, 999, TimeSpan.Zero));
        Assert.That(stamp!.Value.AgeSeconds, Is.EqualTo(60L));
    }

    [Test]
    public void Parse_SmallClockSkewClampsToZero_LargeFutureIsStale()
    {
        var uploaded = new DateTimeOffset(2026, 10, 1, 3, 17, 42, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(OpsBackupsEndpoints.Parse(db_stamp, "db", uploaded.AddMinutes(-4))!.Value.AgeSeconds, Is.EqualTo(0L));
            // A stamp from far in the future would read as fresh for as long as the bad clock lasts.
            Assert.That(OpsBackupsEndpoints.Parse(db_stamp, "db", uploaded.AddMinutes(-6)), Is.Null);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":12")]
    [TestCase("[]")]
    [TestCase("42")]
    [TestCase("{}")]
    [TestCase("{\"kind\":\"appdata\",\"file\":\"x.dump.gz\",\"bytes\":1,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"\",\"bytes\":1,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":0,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":-1,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":\"12\",\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":1.5,\"uploadedAt\":\"2026-10-01T03:17:42Z\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":1}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":1,\"uploadedAt\":\"yesterday\"}")]
    [TestCase("{\"kind\":\"db\",\"file\":\"x\",\"bytes\":1,\"uploadedAt\":1727752662}")]
    public void Parse_AnythingButACompleteStampOfTheRightKindIsStale(string? json)
    {
        Assert.That(OpsBackupsEndpoints.Parse(json, "db", now), Is.Null);
    }

    [Test]
    public void Read_MissingStampsAreBothStale()
    {
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ops-backups-" + Guid.NewGuid().ToString("N"));

        var readout = OpsBackupsEndpoints.Read(root, now);

        Assert.Multiple(() =>
        {
            Assert.That(readout.Db, Is.Null);
            Assert.That(readout.Appdata, Is.Null);
        });
    }

    [Test]
    public void Read_FindsEachKindAtTheScriptsPath()
    {
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ops-backups-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ops"));
            // The paths backup.sh writes: /data/ops/offsite-<kind>.json under the file root.
            File.WriteAllText(Path.Combine(root, "ops", "offsite-db.json"), db_stamp);
            File.WriteAllText(Path.Combine(root, "ops", "offsite-appdata.json"), "garbage");

            var readout = OpsBackupsEndpoints.Read(root, now);

            Assert.Multiple(() =>
            {
                Assert.That(OpsBackupsEndpoints.StampPath(root, "db"), Is.EqualTo(Path.Combine(root, "ops", "offsite-db.json")));
                Assert.That(readout.Db?.Bytes, Is.EqualTo(12345L));
                Assert.That(readout.Appdata, Is.Null, "an unparseable stamp reads as stale, never fresh");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
