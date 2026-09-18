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

/// <summary>A parsed .osu difficulty ("type!beat file format": classic .osu text + [Lyrics]).</summary>
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

    /// <summary>
    /// The RAW text lines of the <c>[Lyrics]</c> section, in file order, each already trimmed and
    /// with blank/comment lines dropped (exactly what the parser fed to
    /// <see cref="LyricTiming.ParseSection"/>). Kept alongside the resolved
    /// <see cref="Lines"/> because <see cref="GameplayFingerprint"/> needs the payload the parse
    /// DROPS as well as the part it keeps: the granularity and seal-grace keys are deliberately not
    /// ported into <see cref="LyricLine"/> (they move gameplay windows, not cell target times), and
    /// a fingerprint blind to them would let a ranked map's judgement change under its own
    /// leaderboard.
    ///
    /// <para>GRANULARITY NO LONGER MOVES A WINDOW: the three-tier ladder it selected was collapsed
    /// to one symmetric set for every cell of every map, so today only the seal grace is a window
    /// key. The argument is unchanged and the key is still hashed, because what the fingerprint
    /// covers is the whole section VERBATIM, including keys this server does not model and keys
    /// whose meaning the game may give back to them.</para>
    /// </summary>
    public required IReadOnlyList<string> LyricSectionLines { get; init; }

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

    private double? srLiterate;
    private double? srLiterateDoubleTime;
    private double? srLiterateHalfTime;

    /// <summary>
    /// The same three ratings for the LITERATE-CONVERTED map, stored as <c>beatmaps.sr_literate</c>
    /// / <c>sr_literate_dt</c> / <c>sr_literate_ht</c> (029_literate_stars.sql). Literate makes
    /// every punctuation mark a typed cell, so it moves the rating and is priced through it exactly
    /// as a rate is (docs/pp.md, backlog 144); it is ORTHOGONAL to the rate, so the two compose into
    /// a cross product rather than a list and all three combinations are stored. Computed lazily and
    /// cached like the plain pair above: each is a full pass over the map's words.
    /// </summary>
    public double SrLiterate => srLiterate ??= LyricDifficulty.Compute(Lines, 1, literate: true);

    /// <summary>The converted map at 1.50x; <c>beatmaps.sr_literate_dt</c>.</summary>
    public double SrLiterateDoubleTime => srLiterateDoubleTime ??= LyricDifficulty.Compute(Lines, RateMods.DoubleTimeBaseRate, literate: true);

    /// <summary>The converted map at 0.75x; <c>beatmaps.sr_literate_ht</c>.</summary>
    public double SrLiterateHalfTime => srLiterateHalfTime ??= LyricDifficulty.Compute(Lines, RateMods.HalfTimeBaseRate, literate: true);

    private LyricWpmCurve? wpmCurve;

    /// <summary>
    /// The map's rolling-window pace (<see cref="LyricWpmCurve"/>): the peak WPM and CPM a perfect
    /// player would ever hit on it, plus the downsampled WPM curve over map time. Stored on the
    /// beatmap row as <c>peak_wpm</c> / <c>peak_cpm</c> / <c>wpm_curve</c> (028_wpm_curve.sql) so
    /// the set page can graph the map without reparsing its blob. Computed lazily and cached like
    /// the rate ratings above: it is a full sweep over every cell of the map.
    /// </summary>
    public LyricWpmCurve WpmCurve => wpmCurve ??= LyricWpmCurve.Compute(Lines);

    /// <summary>
    /// <see cref="WpmCurve"/>'s peak WPM, or null when the map carried too little to measure (under
    /// 30 typeable cells, or no span). NULL rather than 0 because 0 is a value a reader would take
    /// literally; see 028_wpm_curve.sql.
    /// </summary>
    public double? PeakWpm => WpmCurve.IsEmpty ? null : WpmCurve.PeakWpm;

    /// <summary>Peak CPM, on the same null-when-unmeasurable rule as <see cref="PeakWpm"/>.</summary>
    public double? PeakCpm => WpmCurve.IsEmpty ? null : WpmCurve.PeakCpm;

    /// <summary>
    /// The pace to sustain (<see cref="LyricPace.PaceStatistics.TargetWpm"/>, the average WPM
    /// across the fastest fifth of the map's lyric lines of at least three words), stored as
    /// <c>target_wpm</c> (033_target_wpm.sql).
    ///
    /// <para>It comes off <see cref="Pace"/>, NOT off <see cref="WpmCurve"/>, so its null rule is
    /// the one <c>beatmaps.wpm</c> itself would want rather than the curve's: null exactly when the
    /// map has no counted line at all (<see cref="LyricPace.PaceStatistics.TypeableCellCount"/> is
    /// 0), which is the only shape on which a per-line mean is meaningless. A map of three lines is
    /// far too short for the 30-cell curve but its fastest line is a perfectly good target, so
    /// gating this on <c>WpmCurve.IsEmpty</c> would blank a figure the game's own wedge is happily
    /// showing. NULL rather than 0 keeps "no reading" distinguishable from a real one. The
    /// three-word eligibility floor (backlog 274) does not touch this either: a map whose every line
    /// is under three words falls back to selecting from all of them, so the rule stays "no counted
    /// line" and never becomes "no eligible line".</para>
    /// </summary>
    public double? TargetWpm => Pace.TypeableCellCount == 0 ? null : Pace.TargetWpm;

    /// <summary>
    /// The WPM curve as the <c>real[]</c> the column holds (<c>float4</c> is well past what a bar
    /// graph reads), or null when there is no curve to store.
    /// </summary>
    public float[]? WpmCurvePoints => WpmCurve.IsEmpty ? null : WpmCurve.Curve.Select(v => (float)v).ToArray();

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
