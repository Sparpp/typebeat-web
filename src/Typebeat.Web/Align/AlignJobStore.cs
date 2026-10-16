using System.Globalization;
using System.Text.Json;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Align;

/// <summary>
/// File-based job exchange between the web app and the aligner worker container (backlog 413,
/// bringing back what backlog 287 retired), rooted at {TYPEBEAT_FILE_ROOT}/align-jobs (the shared
/// /data volume in prod). The app owns job creation, status reads and cancellation; the worker
/// (deploy/aligner/worker.py) claims the oldest pending job, streams progress, and terminates it
/// with timing.json or error.json. No database involvement: the protocol is plain files, so the
/// worker needs no credentials.
///
///   {root}/align-jobs/worker.json          worker heartbeat: aligner_version, heartbeat_at (UTC)
///   {root}/align-jobs/{id}/job.json        inputs manifest (owner, artist/title, anchors,
///                                          vocal_mode, language, aligner_version, created_at);
///                                          written LAST, its presence means the inputs are complete
///                          input.&lt;ext&gt;     uploaded audio (or video whose audio track aligns)
///                          lyrics.txt      raw lyrics text
///                          .running        the worker's claim stamp (state: running; its mtime is
///                                          the claim time)
///                          progress.log    appended aligner output lines
///                          cancel          the owner's cancel request (state: cancelled)
///                          timing.json     terminal: success payload
///                          error.json      terminal: {"error": "..."}
///
/// The worker deletes a job's heavy files (the audio, its work and out dirs) the moment the job
/// ends, and whole job directories after <see cref="RetentionHours"/>, so a manifest outlives its
/// UTC day: that is what <see cref="JobsCreatedToday"/> counts for the daily cap.
///
/// One job at a time BY CONSTRUCTION on the worker side (torch/demucs peak at several GB); the
/// queue is bounded here instead: one active job per player, <see cref="MaxPendingJobs"/> pending
/// jobs in all, <see cref="JobsPerDay"/> jobs per player per UTC day.
/// </summary>
public sealed class AlignJobStore(IConfiguration config)
{
    public const int MAX_AUDIO_BYTES = 64 * 1024 * 1024;
    public const int MAX_LYRICS_BYTES = 64 * 1024;

    /// <summary>Jobs one player may create per UTC day (cancelled ones count: a submission is the cost).</summary>
    public const int JobsPerDay = 10;

    /// <summary>A new job is refused (503) while this many jobs are already waiting to start.</summary>
    public const int MaxPendingJobs = 20;

    /// <summary>
    /// The worker kills a run that takes longer (worker.py JOB_TIMEOUT_S, keep equal). Twenty
    /// minutes, up from version 1's fifteen, for the fused evidence pass (aligner version 10+),
    /// which adds a second acoustic model over the same stem.
    /// </summary>
    public static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A running claim older than the job timeout plus this grace means the worker died or hung
    /// mid-job without terminating it: the job is failed so its owner is not stuck.
    /// </summary>
    public static readonly TimeSpan RunningGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A job still pending after this long has waited too long in the queue (worker.py
    /// PENDING_MAX_AGE_S, keep equal): failed on both sides, never run late.
    /// </summary>
    public static readonly TimeSpan PendingMaxAge = TimeSpan.FromHours(3);

    /// <summary>The worker refreshes its heartbeat every 15 s; older than this, it is not running.</summary>
    public static readonly TimeSpan WorkerStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>How long the worker keeps a job directory (worker.py RETENTION_S, keep equal).</summary>
    public const int RetentionHours = 48;

    public const string RefusalWorkerDown = "the server aligner is not running right now; try again later, or use the local aligner";
    public const string RefusalQueueFull = "the server aligner queue is full right now; try again later, or use the local aligner";
    public const string ErrorWorkerStopped = "the server aligner stopped before this job finished; try again later, or use the local aligner";
    public const string ErrorRunTooLong = "alignment timed out on the server";
    public const string ErrorWaitedTooLong = "this job waited too long in the server aligner queue; try again later, or use the local aligner";

    public const string WorkerFileName = "worker.json";

    private static readonly HashSet<string> allowed_audio_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac", ".opus", ".mp4", ".m4v", ".mov", ".webm",
    };

    /// <summary>Serialises admission (the 409 / 429 / 503 checks) with job creation, so two
    /// concurrent submissions cannot both pass a check only one of them should.</summary>
    private readonly SemaphoreSlim admission = new(1, 1);

    private readonly string root = Path.Combine(
        Path.GetFullPath(string.IsNullOrEmpty(config[LocalFileStore.RootConfigKey]) ? LocalFileStore.DefaultRoot : config[LocalFileStore.RootConfigKey]!),
        "align-jobs");

    /// <summary>The directory jobs live under (tests play the worker's half of the protocol there).</summary>
    public string Root => root;

    public sealed record JobStatus(string Id, string State, int? QueuePosition, string? Progress, string? TimingJson, string? Error, string? AlignerVersion);

    public sealed record WorkerInfo(string? AlignerVersion, DateTimeOffset HeartbeatAt);

    /// <summary>What a submission carries besides the audio stream.</summary>
    public sealed record JobRequest(string Artist, string Title, string AudioExtension, string Lyrics, string Language, string VocalMode);

    /// <summary>Why <see cref="TryCreateJobAsync"/> refused, or the created job.</summary>
    public sealed record Admission(string? Id, int QueuePosition, string? AlignerVersion, AdmissionRefusal Refusal, string? ActiveJobId);

    public enum AdmissionRefusal
    {
        None,
        WorkerDown,
        ActiveJob,
        DailyCap,
        QueueFull,
    }

    public static bool IsAllowedAudioExtension(string extension) => allowed_audio_extensions.Contains(extension);

    // ---- anchors (mirror of the game's LyricMapImporter.AlignerAnchorMode) ----

    /// <summary>
    /// The <c>--anchors</c> mode the worker runs with: "ref" whenever at least one line carries a
    /// leading [mm:ss.xx] stamp followed by text, "auto" for bare text. Mirrors the game's
    /// <c>LyricMapImporter.AlignerAnchorMode</c> (via <c>HasAnyLineStamp</c> / <c>countStampedLines</c>)
    /// so the server runs the mode the client would run locally.
    /// </summary>
    public static string AnchorMode(string lyricsContent) => HasAnyLineStamp(lyricsContent) ? "ref" : "auto";

    /// <summary>True when at least one line is stamped (sparse anchors, aligner version 3+).</summary>
    public static bool HasAnyLineStamp(string lyricsContent)
    {
        if (string.IsNullOrWhiteSpace(lyricsContent))
            return false;

        foreach (string raw in lyricsContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || !line.StartsWith('['))
                continue;

            int close = line.IndexOf(']');

            // A bare stamp (the end marker) and a metadata tag ([ar:...]) are neither stamped nor content.
            if (close > 1 && TryParseTimestamp(line.Substring(1, close - 1), out _) && line.Substring(close + 1).Trim().Length > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// "mm:ss.xx" / "mm:ss.xxx" / "m:ss.x", bare or bracketed. A port of the game's
    /// <c>LrcParser.TryParseTimestamp</c>, which decides what counts as a stamp.
    /// </summary>
    public static bool TryParseTimestamp(string token, out double milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        token = token.Trim();

        if (token.Length >= 2 && ((token[0] == '[' && token[^1] == ']') || (token[0] == '<' && token[^1] == '>')))
            token = token.Substring(1, token.Length - 2).Trim();

        int colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;

        string minutesPart = token.Substring(0, colon);
        string rest = token.Substring(colon + 1);

        if (!int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            return false;

        int dot = rest.IndexOf('.');
        string secondsPart = dot < 0 ? rest : rest.Substring(0, dot);
        string fractionPart = dot < 0 ? string.Empty : rest.Substring(dot + 1);

        if (!int.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            return false;

        double fractionMs = 0;

        if (fractionPart.Length > 0)
        {
            if (!int.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out int frac))
                return false;

            fractionMs = frac / Math.Pow(10, fractionPart.Length) * 1000.0;
        }

        milliseconds = minutes * 60000.0 + seconds * 1000.0 + fractionMs;
        return true;
    }

    // ---- worker ----

    /// <summary>The worker's last heartbeat, or null when it never wrote one (or it is unreadable).</summary>
    public WorkerInfo? ReadWorker()
    {
        string path = Path.Combine(root, WorkerFileName);

        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var el = doc.RootElement;

            string? version = el.TryGetProperty("aligner_version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var heartbeat = el.GetProperty("heartbeat_at").GetDateTimeOffset();
            return new WorkerInfo(version, heartbeat);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The worker's heartbeat when it is recent enough to call the worker running, else null.</summary>
    public WorkerInfo? LiveWorker()
        => ReadWorker() is { } w && DateTimeOffset.UtcNow - w.HeartbeatAt < WorkerStaleAfter ? w : null;

    // ---- admission ----

    /// <summary>
    /// The checks a submission must pass, in the order the caller is told about them: the worker is
    /// running (503), the player has no active job (409), the daily cap (429), the queue has room
    /// (503). Cheap (a directory scan), so the endpoint runs it BEFORE reading a 64 MB body, and
    /// <see cref="TryCreateJobAsync"/> runs it again under the admission lock.
    /// </summary>
    public Admission Check(long userId)
    {
        var worker = LiveWorker();
        if (worker == null)
            return new Admission(null, 0, null, AdmissionRefusal.WorkerDown, null);

        var jobs = scan(workerLive: true);
        var now = DateTimeOffset.UtcNow;

        var mine = jobs.Where(j => j.UserId == userId).ToList();

        if (mine.FirstOrDefault(j => j.Active) is { } active)
            return new Admission(null, 0, worker.AlignerVersion, AdmissionRefusal.ActiveJob, active.Id);

        if (mine.Count(j => j.CreatedAt >= utcDayStart(now)) >= JobsPerDay)
            return new Admission(null, 0, worker.AlignerVersion, AdmissionRefusal.DailyCap, null);

        if (jobs.Count(j => j.Active && !j.Running) >= MaxPendingJobs)
            return new Admission(null, 0, worker.AlignerVersion, AdmissionRefusal.QueueFull, null);

        return new Admission(null, jobs.Count(j => j.Active) + 1, worker.AlignerVersion, AdmissionRefusal.None, null);
    }

    /// <summary>Jobs <paramref name="userId"/> created since 00:00 UTC today (the daily cap's count).</summary>
    public int JobsCreatedToday(long userId)
    {
        var start = utcDayStart(DateTimeOffset.UtcNow);
        // workerLive: true, so a count never fails anything as a side effect.
        return scan(workerLive: true).Count(j => j.UserId == userId && j.CreatedAt >= start);
    }

    /// <summary>When the daily cap next resets (the coming 00:00 UTC).</summary>
    public static DateTimeOffset NextDailyReset(DateTimeOffset now) => utcDayStart(now).AddDays(1);

    /// <summary>
    /// Re-checks admission and, when it passes, creates the job directory and its inputs (the
    /// worker picks it up on its next poll). Both happen under one lock.
    /// </summary>
    public async Task<Admission> TryCreateJobAsync(long userId, JobRequest request, Stream audio, CancellationToken ct)
    {
        await admission.WaitAsync(ct);

        try
        {
            var check = Check(userId);
            if (check.Refusal != AdmissionRefusal.None)
                return check;

            string id = Guid.NewGuid().ToString("N");
            string dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);

            string audioFile = "input" + request.AudioExtension.ToLowerInvariant();

            try
            {
                await using (var file = File.Create(Path.Combine(dir, audioFile)))
                    await audio.CopyToAsync(file, ct);

                await File.WriteAllTextAsync(Path.Combine(dir, "lyrics.txt"), request.Lyrics, ct);

                string anchors = AnchorMode(request.Lyrics);

                var manifest = new
                {
                    id,
                    user_id = userId,
                    artist = request.Artist,
                    title = request.Title,
                    audio_file = audioFile,
                    // "ref" as soon as one line is stamped, else fully automatic (the game's rule).
                    anchors,
                    // Estimated vocals pace lines from their stamps, so without one the run is aligned
                    // as usual (the game's EstimatedVocalsApply); the worker adds the script-version gate.
                    vocal_mode = request.VocalMode == "estimated" && anchors == "ref" ? "estimated" : "aligned",
                    language = request.Language,
                    aligner_version = check.AlignerVersion,
                    created_at = DateTimeOffset.UtcNow,
                };

                // job.json LAST: its presence is the worker's "inputs are complete" signal.
                await File.WriteAllTextAsync(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(manifest), ct);
            }
            catch
            {
                // A half-written job must not linger as a manifest-less directory holding the audio.
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            return check with { Id = id };
        }
        finally
        {
            admission.Release();
        }
    }

    // ---- status ----

    /// <summary>Reads a job's current state; null when the job doesn't exist or isn't the caller's.</summary>
    public async Task<JobStatus?> ReadStatusAsync(string id, long userId, CancellationToken ct)
    {
        if (!isJobId(id))
            return null;

        string dir = Path.Combine(root, id);
        bool workerLive = LiveWorker() != null;
        var job = readJob(dir, workerLive);

        if (job == null || job.UserId != userId)
            return null;

        switch (job.State)
        {
            case "cancelled":
                return new JobStatus(id, "cancelled", null, null, null, null, job.AlignerVersion);

            case "failed":
                return new JobStatus(id, "failed", null, null, null, job.Error ?? await readErrorAsync(dir, ct), job.AlignerVersion);

            case "done":
                return new JobStatus(id, "done", null, null, await File.ReadAllTextAsync(Path.Combine(dir, "timing.json"), ct), null, job.AlignerVersion);
        }

        int? position = null;

        if (job.State == "pending")
        {
            // 1-based: one more than the active jobs (running included) created before this one.
            position = 1 + scan(workerLive).Count(j => j.Active && j.Id != job.Id && isAhead(j, job));
        }

        return new JobStatus(id, job.State, position, readLastProgressLine(dir), null, null, job.AlignerVersion);
    }

    /// <summary>
    /// Requests cancellation of a job (owner-only): drops a <c>cancel</c> marker the worker honours
    /// by killing its aligner subprocess, and which immediately frees the owner's one-active-job slot
    /// and makes the job report as cancelled. Idempotent: an already-finished job reports success with
    /// nothing to do; returns false only when the job doesn't exist or isn't the caller's.
    /// </summary>
    public async Task<bool> RequestCancelAsync(string id, long userId, CancellationToken ct)
    {
        if (!isJobId(id))
            return false;

        string dir = Path.Combine(root, id);
        var manifest = readManifest(dir);

        if (manifest == null || manifest.UserId != userId)
            return false;

        // Raced the worker to completion; nothing to stop, but report success (idempotent).
        if (File.Exists(Path.Combine(dir, "timing.json")) || File.Exists(Path.Combine(dir, "error.json")))
            return true;

        // An empty marker file (like .running): its presence is the worker's cancel signal.
        await File.WriteAllTextAsync(Path.Combine(dir, "cancel"), string.Empty, ct);
        return true;
    }

    // ---- internals ----

    private sealed record Manifest(string Id, long UserId, DateTimeOffset CreatedAt, string? AlignerVersion);

    /// <summary>A job as the store sees it; <see cref="Active"/> = pending or running.</summary>
    private sealed record Job(string Id, long UserId, DateTimeOffset CreatedAt, string? AlignerVersion, string State, string? Error)
    {
        public bool Active => State is "pending" or "running";

        public bool Running => State == "running";
    }

    private static DateTimeOffset utcDayStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private static bool isAhead(Job other, Job job)
        => other.CreatedAt < job.CreatedAt || (other.CreatedAt == job.CreatedAt && string.CompareOrdinal(other.Id, job.Id) < 0);

    // Ids are self-generated GUID strings; reject anything path-like outright.
    private static bool isJobId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private List<Job> scan(bool workerLive)
    {
        var jobs = new List<Job>();

        if (!Directory.Exists(root))
            return jobs;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (readJob(dir, workerLive) is { } job)
                jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>
    /// Classifies one job directory. The rules, in order: the owner's cancel marker wins
    /// (cancelled), then the worker's error.json (failed), then timing.json (done); an unfinished
    /// job is failed here, and error.json written for the worker's sake, when its run outlived the
    /// timeout, it waited past <see cref="PendingMaxAge"/>, or the worker stopped beating.
    /// </summary>
    private static Job? readJob(string dir, bool workerLive)
    {
        var m = readManifest(dir);
        if (m == null)
            return null;

        Job make(string state, string? error = null) => new(m.Id, m.UserId, m.CreatedAt, m.AlignerVersion, state, error);

        if (File.Exists(Path.Combine(dir, "cancel")))
            return make("cancelled");

        if (File.Exists(Path.Combine(dir, "error.json")))
            return make("failed");

        if (File.Exists(Path.Combine(dir, "timing.json")))
            return make("done");

        var now = DateTimeOffset.UtcNow;
        string claim = Path.Combine(dir, ".running");
        bool running = File.Exists(claim);

        string? stale = null;

        if (running && now - new DateTimeOffset(File.GetLastWriteTimeUtc(claim), TimeSpan.Zero) > JobTimeout + RunningGrace)
            stale = ErrorRunTooLong;
        else if (!running && now - m.CreatedAt > PendingMaxAge)
            stale = ErrorWaitedTooLong;
        else if (!workerLive)
            stale = ErrorWorkerStopped;

        if (stale != null)
        {
            failJob(dir, stale);
            return make("failed", stale);
        }

        return make(running ? "running" : "pending");
    }

    /// <summary>Terminates a job the worker can no longer be trusted to (best effort), so it does
    /// not run late for an owner who was already told it failed.</summary>
    private static void failJob(string dir, string message)
    {
        try
        {
            string path = Path.Combine(dir, "error.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(new { error = message }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string> readErrorAsync(string dir, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "error.json"), ct));
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
        }

        return "alignment failed";
    }

    private static string? readLastProgressLine(string dir)
    {
        string path = Path.Combine(dir, "progress.log");

        try
        {
            if (!File.Exists(path))
                return null;

            // Shared with a concurrently-appending worker: tolerate the write lock.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? last = null;

            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    last = line.Trim();
            }

            return last;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Manifest? readManifest(string dir)
    {
        string path = Path.Combine(dir, "job.json");

        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var el = doc.RootElement;

            long userId = el.GetProperty("user_id").GetInt64();
            var createdAt = el.GetProperty("created_at").GetDateTimeOffset();
            string? version = el.TryGetProperty("aligner_version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Manifest(Path.GetFileName(dir), userId, createdAt, version);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
