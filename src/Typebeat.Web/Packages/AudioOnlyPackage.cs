using System.IO.Compression;

namespace Typebeat.Web.Packages;

/// <summary>
/// Decides which entries of an assembled package the AUDIO-ONLY download variant
/// (<c>?noVideo=1</c>, MediaEndpoints.DownloadAsync) leaves out, and whether that variant may be
/// offered for this package at all.
///
/// <para><b>Which entry is "the video" is read off the .osu files, never guessed from the
/// extension.</b> The video is whatever a difficulty's <c>[Events] Video</c> line names
/// (<see cref="ParsedDifficulty.VideoFilename"/>), which is exactly what the game treats as the
/// video, and different difficulties of one set may name different files. The alternative, a list
/// of video extensions, exists only in the game repo (typebeat.Game/Utils/SupportedExtensions.cs)
/// and copying it here would create an unwritten cross-repo mirror that drifts silently.</para>
///
/// <para><b>The .osu bytes are never touched.</b> The variant omits the video FILE; it does not
/// strip the <c>Video,</c> event line. <c>beatmaps.checksum_md5</c> is the MD5 of the .osu content
/// and IS the beatmap's leaderboard identity, so a rewritten .osu would mint a different beatmap
/// and orphan every score on it. The game tolerates a referenced-but-absent video (the storyboard
/// drawable's texture lookup returns null and it simply loads nothing; the client's own
/// "delete videos" maintenance action leaves maps in precisely this state), which is what makes
/// omit-the-file the correct mechanism.</para>
/// </summary>
public static class AudioOnlyPackage
{
    /// <summary>Difficulty files, the only entries this class reads.</summary>
    public static bool IsDifficulty(string filename)
        => filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Works out the video entries of a package from its difficulty files.
    /// </summary>
    /// <param name="difficulties">Every .osu entry of the package, as (archive path, raw bytes).</param>
    public static AudioOnlyPlan Plan(IEnumerable<(string Filename, byte[] Content)> difficulties)
    {
        var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filename, content) in difficulties)
        {
            ParsedDifficulty parsed;

            try
            {
                parsed = BeatmapPackageParser.ParseDifficulty(filename, content);
            }
            catch (PackageValidationException)
            {
                // Ingest parsed every one of these, so this is unreachable in practice. If it ever
                // happens, we cannot tell what this difficulty plays or shows, and another
                // difficulty's video could be this one's audio, so refuse the variant outright
                // rather than risk shipping a map with no sound.
                return AudioOnlyPlan.Unavailable;
            }

            if (parsed.VideoFilename is { Length: > 0 } video)
                videos.Add(BeatmapPackageParser.NormalizeFilename(video));

            if (parsed.AudioFilename is { Length: > 0 } track)
                audio.Add(BeatmapPackageParser.NormalizeFilename(track));
        }

        // THE PRE-234 GUARD. A map imported from an mp4 alone names the SAME file as both its
        // audio and its video (backlog 234 is the producer-side half that splits a standalone mp3
        // out at import time). Dropping that entry would ship a silent map, so the whole variant is
        // withdrawn for this package: the download falls back to the full one and the card never
        // offers the choice. Cross-difficulty on purpose: one difficulty's video may be another's
        // audio track.
        if (videos.Overlaps(audio))
            return AudioOnlyPlan.Unavailable;

        // No difficulty names a video, so there is nothing to leave out and the stored package
        // already IS the audio-only one. Saying so here means the download route hands over the
        // stored bytes (ranges and all) instead of re-zipping an identical archive, and the card
        // never offers a choice between two identical downloads.
        if (videos.Count == 0)
            return AudioOnlyPlan.Unavailable;

        return new AudioOnlyPlan(videos, available: true);
    }

    /// <summary>
    /// <see cref="Plan(IEnumerable{ValueTuple{string, byte[]}})"/> over an already-open package
    /// archive: reads the .osu entries out of it (they are small; everything else in the archive is
    /// left alone) and hands back the plan for the same archive's entries.
    /// </summary>
    public static async Task<AudioOnlyPlan> PlanAsync(ZipArchive package, CancellationToken ct = default)
    {
        var difficulties = new List<(string, byte[])>();

        foreach (var entry in package.Entries)
        {
            if (!IsDifficulty(entry.FullName))
                continue;

            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            difficulties.Add((BeatmapPackageParser.NormalizeFilename(entry.FullName), buffer.ToArray()));
        }

        return Plan(difficulties);
    }
}

/// <summary>
/// The outcome of <see cref="AudioOnlyPackage.Plan(IEnumerable{ValueTuple{string, byte[]}})"/>:
/// the set of entries an audio-only package omits, or <see cref="Unavailable"/> when the variant
/// must not be served for this package at all.
/// </summary>
public sealed class AudioOnlyPlan
{
    /// <summary>No audio-only variant for this package: serve (and offer) the full one.</summary>
    public static readonly AudioOnlyPlan Unavailable =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), available: false);

    private readonly HashSet<string> videoFilenames;

    internal AudioOnlyPlan(HashSet<string> videoFilenames, bool available)
    {
        this.videoFilenames = videoFilenames;
        Available = available;
    }

    /// <summary>
    /// False when an audio-only package would be a broken package (see the mp4-as-audio guard in
    /// <see cref="AudioOnlyPackage"/>). Callers must serve the full package instead of 404ing: the
    /// game client sends <c>?noVideo=1</c> on its own whenever the player's "prefer no video"
    /// setting is on, and that setting must never turn a download into a failure.
    /// </summary>
    public bool Available { get; }

    /// <summary>The video entries, normalized archive paths. Empty when <see cref="Available"/> is false.</summary>
    public IReadOnlyCollection<string> VideoFilenames => videoFilenames;

    /// <summary>Does the audio-only package leave this archive entry out?</summary>
    public bool Omits(string archivePath)
        => Available && videoFilenames.Contains(BeatmapPackageParser.NormalizeFilename(archivePath));
}
