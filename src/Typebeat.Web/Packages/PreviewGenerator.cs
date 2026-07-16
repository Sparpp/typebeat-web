using System.Diagnostics;
using System.Globalization;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Clips a 30-second 128 kbps mp3 preview from a set's audio track by shelling out to ffmpeg
/// (present in the prod runtime image; NOT a package dependency). Start point is the map's
/// PreviewTime, falling back to 40% into the track (duration probed via ffprobe, then via
/// ffmpeg -i output). A missing/broken ffmpeg degrades to "no preview" — never an upload failure.
/// </summary>
public sealed class PreviewGenerator
{
    public const double ClipSeconds = 30;

    public enum Status
    {
        Generated,
        FfmpegMissing,
        Failed,
    }

    private static readonly TimeSpan process_timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True when an ffmpeg binary responds on PATH. Cheap enough to call per upload; also the
    /// skip condition for the ffmpeg-dependent tests.
    /// </summary>
    public static bool IsFfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process == null)
                return false;

            process.WaitForExit((int)process_timeout.TotalMilliseconds);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <c>previews/{setId}.mp3</c> from the given audio bytes.
    /// </summary>
    /// <param name="audio">The full audio track (any format ffmpeg can read).</param>
    /// <param name="previewTimeMs">The map's PreviewTime; negative = unset -> 40% fallback.</param>
    public async Task<Status> GenerateAsync(Stream audio, double previewTimeMs, long setId, IFileStore store, CancellationToken ct = default)
    {
        if (!IsFfmpegAvailable())
            return Status.FfmpegMissing;

        string inputPath = Path.Combine(Path.GetTempPath(), $"typebeat-preview-{Guid.NewGuid():N}.src");
        string outputPath = Path.ChangeExtension(inputPath, ".mp3");

        try
        {
            await using (var input = File.Create(inputPath))
                await audio.CopyToAsync(input, ct);

            double startSeconds;

            if (previewTimeMs >= 0)
            {
                startSeconds = previewTimeMs / 1000;
            }
            else
            {
                // Unset preview point: 40% into the track; 0 when the duration can't be probed.
                double? duration = await probeDurationSecondsAsync(inputPath, ct);
                startSeconds = duration is double d ? d * 0.4 : 0;
            }

            string args = string.Create(CultureInfo.InvariantCulture,
                $"-y -v error -ss {startSeconds:0.###} -i \"{inputPath}\" -t {ClipSeconds:0.###} -map 0:a:0 -codec:a libmp3lame -b:a 128k -f mp3 \"{outputPath}\"");

            var (exitCode, _, _) = await runAsync("ffmpeg", args, ct);

            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return Status.Failed;

            await using (var output = File.OpenRead(outputPath))
                await store.WriteObjectAsync(StoreKeys.Preview(setId), output, ct);

            return Status.Generated;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Status.Failed;
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    private static async Task<double?> probeDurationSecondsAsync(string inputPath, CancellationToken ct)
    {
        // ffprobe ships with every ffmpeg distribution, but tolerate its absence.
        try
        {
            var (exitCode, stdout, _) = await runAsync(
                "ffprobe",
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
                ct);

            if (exitCode == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                return seconds;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No ffprobe — fall through to the ffmpeg -i parse.
        }

        // `ffmpeg -i x` exits non-zero (no output requested) but prints "Duration: hh:mm:ss.cc".
        var (_, _, stderr) = await runAsync("ffmpeg", $"-i \"{inputPath}\"", ct);

        int idx = stderr.IndexOf("Duration:", StringComparison.Ordinal);

        if (idx >= 0)
        {
            string token = stderr[(idx + "Duration:".Length)..].TrimStart();
            int end = token.IndexOf(',');
            if (end > 0 && TimeSpan.TryParse(token[..end].Trim(), CultureInfo.InvariantCulture, out var span))
                return span.TotalSeconds;
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> runAsync(string exe, string args, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {exe}.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(process_timeout);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }
}
