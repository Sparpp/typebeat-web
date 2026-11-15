using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Typebeat.Web.Ops;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// <see cref="WebsiteFixture.CreateSecondaryHost"/>, the one way a test in this namespace boots a second
/// host on the shared database: it runs none of the startup sweeps, whose re-derivation of the shared
/// seed made the suite order-dependent after backlog 472 (see the helper), yet its gate reads finished,
/// so ingest and the rank button are as open on it as on the shared host.
///
/// NonParallelizable: it boots its own host, like every other user of the helper.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SecondaryHostTest
{
    [Test]
    public async Task ASecondHost_RunsNoStartupSweep_AndLeavesTheSeedAsSeeded()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        using var factory = WebsiteFixture.CreateSecondaryHost(_ => { });
        using var client = factory.CreateDefaultClient(WebsiteFixture.BaseAddress);

        using (var probe = await client.GetAsync("/health"))
            probe.EnsureSuccessStatusCode();

        var gate = factory.Services.GetRequiredService<StartupSweepGate>();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Services.GetServices<IHostedService>().OfType<StartupSweeps>(), Is.Empty,
                "the shared host swept this database before the seeds; a second sweep re-derives their hand-set rows");
            Assert.That(gate.State, Is.EqualTo(StartupSweepState.Done));
            Assert.That(gate.IngestOpen, Is.True);
            Assert.That(gate.RankOpen, Is.True);
        });

        // The rows a sweep rewrote in 472's failures: Twin Peaks' stored figures disagree with its own
        // .osu blobs on purpose (PublicSiteSeed), so they survive only while nothing re-derives them.
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var rows = (await conn.QueryAsync<(double Stars, double Wpm, int? JudgedCells)>(
                "SELECT difficulty_rating, wpm::float8, judged_cells FROM beatmaps WHERE id = ANY(@ids) ORDER BY difficulty_rating",
                new { ids = new[] { PublicSiteSeed.MultiDiffEasyId, PublicSiteSeed.MultiDiffHardId } }))
            .ToList();

        Assert.That(rows, Is.EqualTo(new (double, double, int?)[] { (2.0, 60, null), (6.0, 180, null) }));
    }
}
