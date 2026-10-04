using System.Diagnostics;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Ops;

/// <summary>Where the boot-time backfill chain (<see cref="StartupSweeps"/>) has got to.</summary>
public enum StartupSweepState
{
    /// <summary>The chain has not finished yet (or the process is shutting down part way through it).</summary>
    Running,

    /// <summary>Every sweep ran to completion.</summary>
    Done,

    /// <summary>One sweep threw; the rest of the chain did not run. See <see cref="StartupSweepGate.FailedStage"/>.</summary>
    Failed,
}

/// <summary>
/// The gate the requests that must not race the boot-time backfill chain wait on (backlog 368).
/// Until backlog 368 the chain ran in Program.cs before Kestrel bound, so nothing could race it, and
/// a deploy was down for the whole of it. Now the app listens as soon as its migrations are applied,
/// pages and score submission are served at once (anything they write mid-sweep heals: a stale pace
/// stamps its scores back to pp version 0, and <see cref="PpBackfill"/> runs last), and only two
/// paths are held back:
/// <list type="bullet">
///   <item>BSS package ingest (the full upload, the patch and the upload-session complete), because
///   the ingest reads a missing or out-of-date gameplay fingerprint as "changed", so a re-upload
///   before <see cref="GameplayFingerprintBackfill"/> has filled them would demote a ranked map for a
///   metadata-only edit. The transport-only session routes (create, chunk, status) stay open, so a
///   client can keep sending chunks and only its complete call waits.</item>
///   <item>The website's rank button, whose in-request carry runs the same set-scoped SetRankRefund
///   and PpBackfill passes as the chain (idempotent if concurrent, but kept apart all the same).</item>
/// </list>
/// Both answer 503 with Retry-After while the gate is closed. The game's submission flow already
/// retries 502/503/504 as a gateway blip, so an upload sent during a deploy simply lands a little
/// later. /health is NOT gated: it stays liveness (process up, database reachable), and /health/ready
/// reports this gate for anyone who wants to know when the sweeps are through.
/// </summary>
public sealed class StartupSweepGate
{
    /// <summary>What a refused request is told to wait before trying again, in seconds.</summary>
    public const int RetryAfterSeconds = 30;

    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile StartupSweepState state = StartupSweepState.Running;
    private volatile bool fingerprintsCurrent;

    /// <summary>Completes once the chain has finished, successfully or not. Never faults.</summary>
    public Task Completed => finished.Task;

    /// <summary>The sweep that threw, when <see cref="State"/> is <see cref="StartupSweepState.Failed"/>.</summary>
    public string? FailedStage { get; private set; }

    /// <summary>
    /// True once <see cref="GameplayFingerprintBackfill"/> has completed in this process. A chain that
    /// fails AFTER it still opens package ingest; one that fails at or before it keeps ingest refused
    /// until a restart, because opening it would let a re-upload demote a ranked map.
    /// </summary>
    public bool FingerprintsCurrent => fingerprintsCurrent;

    /// <summary>Test seam: forces <see cref="State"/> (null means the real state). Never set in production.</summary>
    public StartupSweepState? StateOverride { get; set; }

    public StartupSweepState State => StateOverride ?? state;

    /// <summary>Whether BSS package ingest may run now.</summary>
    public bool IngestOpen => State switch
    {
        StartupSweepState.Done => true,
        StartupSweepState.Failed => FingerprintsCurrent,
        _ => false,
    };

    /// <summary>Whether the rank button may run now (any finished chain: its carry is its own pass).</summary>
    public bool RankOpen => State != StartupSweepState.Running;

    /// <summary>Called by <see cref="StartupSweeps"/> once the fingerprint backfill has completed.</summary>
    public void MarkFingerprintsCurrent() => fingerprintsCurrent = true;

    /// <summary>Called by <see cref="StartupSweeps"/> when the chain ends: null for success, else the sweep that threw.</summary>
    public void Finish(string? failedStage)
    {
        FailedStage = failedStage;
        state = failedStage == null ? StartupSweepState.Done : StartupSweepState.Failed;
        finished.TrySetResult();
    }

    /// <summary>The wire refusal for package ingest while the gate is closed: the JSON error envelope, 503, Retry-After.</summary>
    public IResult IngestRefusal()
        => WireJson.Unavailable(
            State == StartupSweepState.Failed
                ? "Beatmap submission is unavailable while the server repairs its catalogue. Please try again later."
                : "The server is finishing its startup maintenance. Please submit again in a minute.",
            RetryAfterSeconds);

    /// <summary>The plain body /health/ready answers: "ready", "sweeping" or "failed: Stage".</summary>
    public string ReadinessBody() => State switch
    {
        StartupSweepState.Done => "ready",
        StartupSweepState.Failed => $"failed: {FailedStage}",
        _ => "sweeping",
    };
}

/// <summary>
/// The boot-time backfill chain (backlog 368), moved off the listen path into a hosted service:
/// Program.cs still applies the extensions and the migrations synchronously before the app listens,
/// then this runs every sweep in the original order, in the background, and opens
/// <see cref="StartupSweepGate"/> when it is through. The order is load-bearing and unchanged:
/// Pace, then Fingerprint, then Language (reads the lyric text Pace fills), SkipGate (reads Pace's
/// skippable_s), RateGate, SetRank, SetRankClassicMark (backlog 398: the version rule pointed down,
/// after the carry-up so the two cannot fight over a row), and Pp LAST (reads sr_dt / sr_ht and the
/// ranked flags the refunds may have just flipped). The whole chain is caught: a failing sweep is logged as an error
/// and recorded on the gate, and never takes the host down (it used to crash the boot, which on a
/// deploy meant a crash loop re-running every sweep on each restart while the health check went red).
/// A shutdown part way through cancels the current sweep; each one is idempotent and the next boot
/// resumes it.
/// </summary>
public sealed class StartupSweeps(Db db, IFileStore fileStore, StartupSweepGate gate, CacheEviction eviction, ILogger<StartupSweeps> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return from StartAsync at once, so the server binds without waiting for any sweep.
        await Task.Yield();

        var clock = Stopwatch.StartNew();
        string stage = nameof(PaceBackfill);

        logger.LogInformation("Startup sweeps: started; BSS package ingest and the rank button answer 503 until they finish.");

        try
        {
            // Recompute stored wpm/star/word/char numbers for rows written under an older pace
            // arithmetic (LyricPace.VERSION bumps). No-op when everything is current.
            await PaceBackfill.RunAsync(db, fileStore, logger, stoppingToken);

            // Fill beatmaps.gameplay_fingerprint for every live difficulty that has none, or whose
            // stored value predates GameplayFingerprint.VERSION (030_gameplay_fingerprint.sql). The
            // ingest reads a missing one as "changed", which is why package ingest waits on the gate.
            stage = nameof(GameplayFingerprintBackfill);
            await GameplayFingerprintBackfill.RunAsync(db, fileStore, logger, stoppingToken);
            gate.MarkFingerprintsCurrent();

            // Detect the song language of every set that still has none (019_language.sql) from the
            // lyric text, so after Pace. Only writes rows that are still unset.
            stage = nameof(LanguageBackfill);
            await LanguageBackfill.RunAsync(db, logger, stoppingToken);

            // Re-rank scores the pre-task-47 play-time gate unranked for using the skip button
            // (016_refund_skip_gate.sql); reads beatmaps.skippable_s, which Pace fills.
            stage = nameof(SkipGateRefund);
            await SkipGateRefund.RunAsync(db, logger, stoppingToken);

            // Re-rank scores the rate-blind play-time gate unranked for an up-rate
            // (017_rate_gate_refund.sql); after the skip refund, so a row only that one needed is no
            // longer a candidate here.
            stage = nameof(RateGateRefund);
            await RateGateRefund.RunAsync(db, logger, stoppingToken);

            // Standing sweep (backlog 270): re-rank honest scores stored unranked only because their
            // set was still pending. After both refunds, before PpBackfill, which prices what it
            // flips. The rank button runs the same pass scoped to its set; this is the safety net.
            stage = nameof(SetRankRefund);
            await SetRankRefund.RunAsync(db, fileStore, logger, stoppingToken);

            // Standing sweep (backlog 398): the version rule pointed DOWNWARD. A score whose played
            // version is not the current gameplay of its (re-ranked) map (its token is missing,
            // names a hash no stored version produces, or names a version with different gameplay)
            // gets the synthetic Classic mark: "CL" appended to scores.mods, ranked kept, total_score
            // repriced to 0.95x, pp_version stamped to 0 so the PpBackfill below reprices it at
            // 0.95x. AFTER the carry-up, so the two share the predicate: a stale pending-era play
            // SetRankRefund carried is marked here in the same boot.
            stage = nameof(SetRankClassicMark);
            await SetRankClassicMark.RunAsync(db, fileStore, logger, eviction, stoppingToken);

            // Recompute stored per-score pp below PerformancePoints.VERSION (020_performance_points.sql).
            // LAST: reads sr_dt / sr_ht (Pace) and scores.ranked (the refunds above).
            stage = nameof(PpBackfill);
            await PpBackfill.RunAsync(db, logger, stoppingToken);

            // Re-base stored total_score onto the 300000/700000 split (046_score_reweight_marks.sql,
            // owner 2026-10-04). Independent of everything above: pp prices from accuracy/misses/SR,
            // not from total_score, and the transform reads only the stored row. LAST so the 0.95x
            // Classic reprice above is already in the total this compounds.
            stage = nameof(ScoreReweightBackfill);
            await ScoreReweightBackfill.RunAsync(db, logger, eviction, stoppingToken);

            gate.Finish(failedStage: null);
            logger.LogInformation("Startup sweeps: done in {Seconds:0.0}s; package ingest and the rank button are open.",
                clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Startup sweeps: stopped by shutdown during {Stage} after {Seconds:0.0}s; the next boot resumes them.",
                stage, clock.Elapsed.TotalSeconds);
        }
        catch (Exception e)
        {
            gate.Finish(stage);
            logger.LogError(e,
                "Startup sweeps: {Stage} FAILED after {Seconds:0.0}s and the sweeps after it did not run. Package ingest is {Ingest} until a restart; the rank button is open.",
                stage, clock.Elapsed.TotalSeconds, gate.IngestOpen ? "open" : "REFUSED (503)");
        }
    }
}
