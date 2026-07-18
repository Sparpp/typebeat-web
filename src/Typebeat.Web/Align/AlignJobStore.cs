using System.Text.Json;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Align;

/// <summary>
/// File-based job exchange between the web app and the aligner worker container, rooted at
/// {TYPEBEAT_FILE_ROOT}/align-jobs (the shared /data volume in prod). The app owns job creation
/// and status reads; the worker (deploy/aligner/worker.py) claims the oldest unprocessed job,
/// streams progress, and terminates it with timing.json or error.json. No database involvement —
/// jobs are transient scratch (the worker garbage-collects dirs older than a few hours), and the
/// protocol is plain files so the worker needs no credentials:
///
///   {root}/align-jobs/{id}/job.json       inputs manifest (owner, artist/title, anchors mode)
///                          input.&lt;ext&gt;    uploaded audio (or video whose audio track aligns)
///                          lyrics.txt     raw lyrics text
///                          .running       worker's claim stamp (state: running)
///                          progress.log   appended aligner output lines
///                          timing.json    terminal: success payload
///                          error.json     terminal: {"error": "..."}
/// </summary>
public sealed class AlignJobStore(IConfiguration config)
{
    public const int MAX_AUDIO_BYTES = 64 * 1024 * 1024;
    public const int MAX_LYRICS_BYTES = 64 * 1024;

    /// <summary>A pending/running job older than this is considered abandoned (worker died or was
    /// never up) and stops blocking its owner from submitting a new one.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(20);

    private static readonly HashSet<string> allowed_audio_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac", ".opus", ".mp4", ".m4v", ".mov", ".webm",
    };

    private readonly string root = Path.Combine(
        Path.GetFullPath(string.IsNullOrEmpty(config[LocalFileStore.RootConfigKey]) ? LocalFileStore.DefaultRoot : config[LocalFileStore.RootConfigKey]!),
        "align-jobs");

    public sealed record JobStatus(string Id, string State, string? Progress, string? TimingJson, string? Error);

    public static bool IsAllowedAudioExtension(string extension) => allowed_audio_extensions.Contains(extension);

    /// <summary>
    /// True when every non-empty content line starts with a [mm:ss.xx] stamp (metadata tags
    /// neutral) — the aligner's high-accuracy "ref" mode. Mirrors the game's
    /// LyricMapImporter.HasLineStamps so the worker runs the same mode the client would locally.
    /// </summary>
    public static bool HasLineStamps(string lyricsContent)
    {
        if (string.IsNullOrWhiteSpace(lyricsContent))
            return false;

        bool anyStamp = false;

        foreach (string raw in lyricsContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
                continue;

            if (line.StartsWith('['))
            {
                int close = line.IndexOf(']');

                if (close > 1)
                {
                    string stamp = line[1..close];
                    int colon = stamp.IndexOf(':');

                    if (colon > 0 && int.TryParse(stamp[..colon], out _) && double.TryParse(stamp[(colon + 1)..], out _))
                        anyStamp = true;

                    // Timestamped or metadata tag line — either way not a bare content line.
                    continue;
                }
            }

            return false; // a content line without a stamp -> not fully stamped
        }

        return anyStamp;
    }

    /// <summary>The caller's most recent job still considered active (unfinished + inside the
    /// abandonment window), or null.</summary>
    public string? FindActiveJob(long userId)
    {
        if (!Directory.Exists(root))
            return null;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            var manifest = readManifest(dir);

            if (manifest == null || manifest.Value.UserId != userId)
                continue;

            if (File.Exists(Path.Combine(dir, "timing.json")) || File.Exists(Path.Combine(dir, "error.json")))
                continue;

            if (DateTimeOffset.UtcNow - manifest.Value.CreatedAt < ActiveWindow)
                return Path.GetFileName(dir);
        }

        return null;
    }

    /// <summary>Creates the job directory + inputs; the worker picks it up on its next poll.</summary>
    public async Task<string> CreateJobAsync(long userId, string artist, string title, string audioExtension, Stream audio, string lyricsContent, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);

        await using (var file = File.Create(Path.Combine(dir, "input" + audioExtension.ToLowerInvariant())))
            await audio.CopyToAsync(file, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "lyrics.txt"), lyricsContent, ct);

        var manifest = new
        {
            id,
            user_id = userId,
            artist,
            title,
            audio_file = "input" + audioExtension.ToLowerInvariant(),
            // "ref" = fully line-stamped lyrics anchor the pass; otherwise fully automatic.
            anchors = HasLineStamps(lyricsContent) ? "ref" : "auto",
            created_at = DateTimeOffset.UtcNow,
        };

        // job.json LAST — its presence is the worker's "inputs are complete" signal.
        await File.WriteAllTextAsync(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(manifest), ct);

        return id;
    }

    /// <summary>Reads a job's current state; null when the job doesn't exist or isn't the caller's.</summary>
    public async Task<JobStatus?> ReadStatusAsync(string id, long userId, CancellationToken ct)
    {
        // Ids are self-generated GUID strings; reject anything path-like outright.
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return null;

        string dir = Path.Combine(root, id);
        var manifest = readManifest(dir);

        if (manifest == null || manifest.Value.UserId != userId)
            return null;

        string errorPath = Path.Combine(dir, "error.json");

        if (File.Exists(errorPath))
        {
            string? error = null;

            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(errorPath, ct));
                if (doc.RootElement.TryGetProperty("error", out var e))
                    error = e.GetString();
            }
            catch (JsonException)
            {
            }

            return new JobStatus(id, "failed", null, null, error ?? "alignment failed");
        }

        string timingPath = Path.Combine(dir, "timing.json");

        if (File.Exists(timingPath))
            return new JobStatus(id, "done", null, await File.ReadAllTextAsync(timingPath, ct), null);

        string state = File.Exists(Path.Combine(dir, ".running")) ? "running" : "pending";

        // Stale-abandoned pending/running (worker down, crashed mid-job): surface as failed so
        // the client stops polling instead of spinning forever.
        if (DateTimeOffset.UtcNow - manifest.Value.CreatedAt >= ActiveWindow)
            return new JobStatus(id, "failed", null, null, "alignment timed out on the server");

        return new JobStatus(id, state, readLastProgressLine(dir), null, null);
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

    private static (long UserId, DateTimeOffset CreatedAt)? readManifest(string dir)
    {
        string path = Path.Combine(dir, "job.json");

        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootEl = doc.RootElement;

            long userId = rootEl.GetProperty("user_id").GetInt64();
            var createdAt = rootEl.GetProperty("created_at").GetDateTimeOffset();
            return (userId, createdAt);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
