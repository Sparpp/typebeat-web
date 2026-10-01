using Microsoft.Extensions.DependencyInjection;
using Typebeat.Web.Ops;

namespace Typebeat.Web.Tests;

/// <summary>
/// The fixtures' wait for the startup sweeps (backlog 368). Until then the chain ran before the host
/// started, so every <c>WebApplicationFactory</c> host's first request already saw it finished and a
/// failing sweep failed the boot. It now runs in a hosted service after the host listens, so each
/// host awaits it here before seeding (no seeded row may race a sweep) and asserts it finished
/// cleanly (a broken sweep stays loud).
/// </summary>
public static class StartupSweepHosts
{
    public static async Task AwaitAsync(IServiceProvider services)
    {
        var gate = services.GetRequiredService<StartupSweepGate>();
        await gate.Completed.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.That(gate.State, Is.EqualTo(StartupSweepState.Done), $"startup sweeps failed at {gate.FailedStage}");
    }
}

/// <summary>The gate's own state machine, driven directly on fresh instances (no host).</summary>
public class StartupSweepGateTest
{
    [Test]
    public void Fresh_IsRunning_AndRefusesBothPaths()
    {
        var gate = new StartupSweepGate();

        Assert.Multiple(() =>
        {
            Assert.That(gate.State, Is.EqualTo(StartupSweepState.Running));
            Assert.That(gate.Completed.IsCompleted, Is.False);
            Assert.That(gate.IngestOpen, Is.False);
            Assert.That(gate.RankOpen, Is.False);
            Assert.That(gate.ReadinessBody(), Is.EqualTo("sweeping"));
        });
    }

    [Test]
    public void FingerprintsAloneDoNotOpenIngest_WhileTheChainIsStillRunning()
    {
        var gate = new StartupSweepGate();
        gate.MarkFingerprintsCurrent();

        Assert.That(gate.IngestOpen, Is.False, "ingest waits for the whole chain, not just the fingerprint stage");
    }

    [Test]
    public void Done_OpensEverything_AndCompletes()
    {
        var gate = new StartupSweepGate();
        gate.MarkFingerprintsCurrent();
        gate.Finish(failedStage: null);

        Assert.Multiple(() =>
        {
            Assert.That(gate.State, Is.EqualTo(StartupSweepState.Done));
            Assert.That(gate.Completed.IsCompletedSuccessfully, Is.True);
            Assert.That(gate.IngestOpen, Is.True);
            Assert.That(gate.RankOpen, Is.True);
            Assert.That(gate.ReadinessBody(), Is.EqualTo("ready"));
        });
    }

    [Test]
    public void FailedBeforeFingerprints_KeepsIngestRefused_ButOpensRank()
    {
        var gate = new StartupSweepGate();
        gate.Finish("PaceBackfill");

        Assert.Multiple(() =>
        {
            Assert.That(gate.State, Is.EqualTo(StartupSweepState.Failed));
            Assert.That(gate.Completed.IsCompletedSuccessfully, Is.True, "Completed never faults");
            Assert.That(gate.IngestOpen, Is.False, "a re-upload would read the missing fingerprints as changed");
            Assert.That(gate.RankOpen, Is.True);
            Assert.That(gate.ReadinessBody(), Is.EqualTo("failed: PaceBackfill"));
        });
    }

    [Test]
    public void FailedAfterFingerprints_OpensIngest()
    {
        var gate = new StartupSweepGate();
        gate.MarkFingerprintsCurrent();
        gate.Finish("PpBackfill");

        Assert.Multiple(() =>
        {
            Assert.That(gate.State, Is.EqualTo(StartupSweepState.Failed));
            Assert.That(gate.IngestOpen, Is.True);
            Assert.That(gate.ReadinessBody(), Is.EqualTo("failed: PpBackfill"));
        });
    }

    [Test]
    public void Override_ClosesARealDoneGate_AndClearingItRestoresIt()
    {
        var gate = new StartupSweepGate();
        gate.MarkFingerprintsCurrent();
        gate.Finish(null);

        gate.StateOverride = StartupSweepState.Running;
        bool closed = !gate.IngestOpen && !gate.RankOpen;
        gate.StateOverride = null;

        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.True);
            Assert.That(gate.IngestOpen, Is.True);
        });
    }
}
