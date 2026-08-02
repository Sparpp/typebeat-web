using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

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

    /// <summary>
    /// Raw <c>[Metadata] Language:</c> text, empty when the file states none (which is what every
    /// pre-task-58 client writes). Fold it with <see cref="BeatmapLanguages.Normalize"/> before
    /// storing; nothing else should interpret it.
    /// </summary>
    public string Language { get; init; } = string.Empty;

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

    private double? srDoubleTime;
    private double? srHalfTime;

    /// <summary>
    /// Star rating at Double Time's BASE clock rate (1.50x), stored on the beatmap row as
    /// <c>beatmaps.sr_dt</c>. pp prices a rate play exclusively through the rating recomputed at its
    /// rate (docs/pp.md), and only the base rates are pp-eligible, so the server needs exactly this
    /// one figure per direction and never does rate maths at query time. Computed lazily and cached:
    /// <see cref="LyricDifficulty.Compute"/> is a full pass over the map's words.
    /// </summary>
    public double SrDoubleTime => srDoubleTime ??= LyricDifficulty.Compute(Lines, RateMods.DoubleTimeBaseRate);

    /// <summary>Star rating at Half Time's base clock rate (0.75x); <c>beatmaps.sr_ht</c>.</summary>
    public double SrHalfTime => srHalfTime ??= LyricDifficulty.Compute(Lines, RateMods.HalfTimeBaseRate);

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

    /// <summary>
    /// The map's lyric text: every line's normalized words with the author's casing kept, one
    /// lyric line per '\n'-separated line. Stored on the beatmap row (<c>beatmaps.lyrics</c>,
    /// 018_lyrics_search.sql) where it serves double duty: the haystack for the site's
    /// <c>lyrics:</c> search operator (per-word ILIKE is case-insensitive and its wildcards
    /// cross newlines, so this display-friendly form searches identically to a folded one) and
    /// the set page's lyrics section (rendered one stored line per line).
    /// </summary>
    public string LyricsText => string.Join('\n', Lines.Select(l => l.RawText));
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
