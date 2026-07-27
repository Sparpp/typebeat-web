using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Packages;

/// <summary>
/// One file inside a beatmap package: the content-addressed identity the whole versioning
/// scheme runs on. Two versions share a file iff (sha256, size, filename) all match
/// (osu-server-beatmap-submission's PackageFileEqualityComparer).
/// </summary>
public sealed record PackageFileEntry(byte[] Sha256, long Size, string Filename)
{
    public string Sha256Hex => Convert.ToHexStringLower(Sha256);
}

/// <summary>A parsed .osu difficulty ("type!beat file format v1": classic .osu text + [Lyrics]).</summary>
public sealed class ParsedDifficulty
{
    /// <summary>Archive path of the .osu file (normalized to forward slashes).</summary>
    public required string Filename { get; init; }

    /// <summary>MD5 of the exact .osu bytes: the beatmap_hash identity contract (beatmaps.checksum_md5).</summary>
    public required string ChecksumMd5 { get; init; }

    // [General]
    public string AudioFilename { get; init; } = string.Empty;

    /// <summary>Menu/website preview start in ms; -1 = unset (osu default).</summary>
    public double PreviewTime { get; init; } = -1;

    // [Metadata]
    public string Title { get; init; } = string.Empty;
    public string TitleUnicode { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string ArtistUnicode { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public long? BeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }

    // [Events]
    public string? BackgroundFilename { get; init; }
    public string? VideoFilename { get; init; }

    /// <summary>From the first uninherited [TimingPoints] entry (60000 / beatLength); null when absent.</summary>
    public double? Bpm { get; init; }

    // [Lyrics]-derived.
    public required IReadOnlyList<LyricLine> Lines { get; init; }
    public required LyricPace.PaceStatistics Pace { get; init; }

    /// <summary>Last lyric line's hard end, seconds (0 for an empty map).</summary>
    public double TotalLengthS => Lines.Count > 0 ? Lines[^1].EndTime / 1000 : 0;

    /// <summary>First lyric line start -> last line end, seconds.</summary>
    public double DrainLengthS => Lines.Count > 0 ? (Lines[^1].EndTime - Lines[0].StartTime) / 1000 : 0;

    /// <summary>
    /// Seconds of <see cref="DrainLengthS"/> the in-game skip button can legally remove, seconds
    /// (<see cref="InstrumentalGaps.SkippableSeconds"/>). Stored on the beatmap row so the
    /// play-time anti-cheat gate can price an honest skip-using play without re-reading the blob.
    /// </summary>
    public double SkippableS => InstrumentalGaps.SkippableSeconds(Lines);
}

/// <summary>The fully parsed contents of an uploaded beatmap package zip.</summary>
public sealed class ParsedPackage
{
    /// <summary>Every file in the archive (directories excluded), in archive order.</summary>
    public required IReadOnlyList<PackageFileEntry> Files { get; init; }

    public required IReadOnlyList<ParsedDifficulty> Difficulties { get; init; }

    /// <summary>Compressed package size in bytes (the upload body).</summary>
    public required long PackageSize { get; init; }

    public bool HasVideo => Difficulties.Any(d => d.VideoFilename != null);
}
