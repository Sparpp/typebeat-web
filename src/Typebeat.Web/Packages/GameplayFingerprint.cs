using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Typebeat.Web.Packages;

/// <summary>
/// The identity of a difficulty's GAMEPLAY, as opposed to the identity of its bytes. Stored per
/// beatmap row as <c>beatmaps.gameplay_fingerprint</c> (030_gameplay_fingerprint.sql) and compared
/// at ingest: a ranked set whose fingerprint moves is demoted back to 'pending', because the map a
/// reviewer signed off on and the map its leaderboard is now collecting scores for are no longer
/// the same map (backlog 173).
///
/// <para>
/// WHY THE EXISTING COLUMNS COULD NOT DO THIS JOB. <c>beatmaps.checksum_md5</c> is the MD5 of the
/// whole .osu, so it moves for a tag fix or a background swap and would demote on every cosmetic
/// save. <c>beatmaps.lyrics</c> (018_lyrics_search.sql) is the plain-text search haystack, so it
/// cannot see a timing-only edit at all (same words, shifted times). This is the narrow thing
/// between them: everything the player types against, and nothing else.
/// </para>
///
/// <para>
/// WHAT IS IN IT, and why exactly these two:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>The whole <c>[Lyrics]</c> section</b>, verbatim, minus <c>beatdrop_ms</c>. That section
///     IS the timing schema: per-line start/end, per-word units and their syllable arrays, the
///     freestyle flag, the header's <c>song_end_ms</c> (which caps the last line's seal deadline)
///     and <c>granularity</c>. Taking it raw rather than through the parsed
///     <see cref="Lyrics.LyricLine"/> model is deliberate: the server's port drops the granularity
///     and seal-grace machinery as "gameplay windows only, never cell target times"
///     (Lyrics/LyricTiming.cs), and those windows are exactly the kind of thing a fingerprint must
///     not be blind to. Raw text sees every key, including ones this server does not model yet, and
///     including <c>granularity</c>, which selected a judgement ladder until the three tiers were
///     collapsed into one symmetric set and is metadata today.
///   </description></item>
///   <item><description>
///     <b>The SHA256 of the audio file the difficulty points at.</b> Not optional, and newly
///     urgent since backlog 171: a mapper can now swap a ranked map's mp3 while keeping every
///     timing byte identical, and a fingerprint over lyrics alone would happily keep the rank on a
///     different recording that the stored timings no longer match.
///   </description></item>
/// </list>
///
/// <para>
/// WHAT IS OUT, so these edits keep the rank: title, artist, both unicode variants, creator,
/// difficulty name, source, tags, language, background, video, preview time, BPM/timing points,
/// the .osu filename, and the beatdrop. The beatdrop exclusion is not a judgement call made here:
/// it mirrors the game's own <c>TypeBeatRuleset.NativeEncodingsEquivalentForStatus</c>, which
/// normalises <c>beatdrop_ms</c> out precisely so a beatdrop-only editor save cannot demote a
/// ranked map locally, on the grounds that the beatdrop only soundtracks the main-menu intro and
/// has no bearing on gameplay or scoring. The regex below is a mirror of that ruleset's
/// <c>LyricOsuFormat.StripBeatdrop</c>.
/// </para>
///
/// <para>
/// THE AUDIO IS HASHED BY BYTES, NOT BY NAME, and that is the correct reading of "gameplay
/// affecting": renaming <c>audio.mp3</c> to <c>song.mp3</c> without changing a sample is a
/// cosmetic edit and keeps the rank, while pointing the difficulty at a different file, or
/// replacing the bytes behind the same name, both move the hash and both demote.
/// </para>
///
/// <para>
/// SENSITIVITY TO REFORMATTING is accepted, on the same terms the game accepts it. A hand-editor
/// that re-orders the keys inside a <c>[Lyrics]</c> line without changing a value would move the
/// fingerprint and demote. Every real client writes this section from
/// <c>LyricOsuFormat.GenerateOsu</c>, which re-serialises the timing.json elements compactly and
/// deterministically, and the game's own ranked-status check is a plain string comparison of the
/// whole encoding, so a canonicalising JSON re-serialisation here would be strictly more clever
/// than the thing it mirrors, for a case no client produces. A false demote is also the
/// recoverable direction: a reviewer re-ranks.
/// </para>
/// </summary>
public static class GameplayFingerprint
{
    /// <summary>
    /// Stamped into every stored value as a <c>"v1:"</c> prefix. Bump when the RECIPE below
    /// changes (a new field folded in, a different exclusion), which makes every stored value
    /// stale-shaped and hands the whole catalogue to
    /// <see cref="GameplayFingerprintBackfill"/> to rewrite. That sweep runs at startup BEFORE the
    /// app serves a request (Program.cs), so a recipe change can never be mistaken for a mapper's
    /// gameplay edit: by the time an upload can arrive, every row already speaks the new version.
    /// </summary>
    public const int VERSION = 1;

    private static readonly string version_prefix = $"v{VERSION}:";

    /// <summary>
    /// LIKE pattern matching a stored value written by the CURRENT <see cref="VERSION"/>; anything
    /// else (NULL, or an older version's value) is what the backfill considers stale.
    /// </summary>
    public static string CurrentVersionLikePattern => version_prefix + "%";

    // Mirror of LyricOsuFormat.StripBeatdrop (typebeat-osu
    // typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricOsuFormat.cs): the encoder always writes the
    // field after another header key, so the leading comma is what is stripped.
    private static readonly Regex beatdrop_field =
        new Regex(",?\"beatdrop_ms\":[-0-9.eE+]+", RegexOptions.Compiled);

    /// <summary>
    /// The stored fingerprint for one difficulty: <c>"v1:"</c> + lowercase SHA256 hex over the
    /// canonical form built by <see cref="CanonicalForm"/>.
    /// </summary>
    /// <param name="difficulty">The parsed .osu.</param>
    /// <param name="files">
    /// The file list the difficulty's audio is resolved against: <c>ParsedPackage.Files</c> at
    /// ingest, or the stored version manifest during the backfill. Same shape either way, which is
    /// what lets one function serve both callers.
    /// </param>
    public static string Compute(ParsedDifficulty difficulty, IReadOnlyList<PackageFileEntry> files)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm(difficulty, files)));
        return version_prefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// The exact bytes <see cref="Compute"/> hashes, exposed so a test can assert WHAT moved the
    /// fingerprint rather than only that something did.
    ///
    /// <para>
    /// Field-count and length prefixes are in there so no content can forge a field boundary: a
    /// lyric line cannot contain a newline (the .osu parser splits on them before this ever sees
    /// the section), but the sizes make that structural rather than incidental.
    /// </para>
    /// </summary>
    public static string CanonicalForm(ParsedDifficulty difficulty, IReadOnlyList<PackageFileEntry> files)
    {
        var sb = new StringBuilder();

        sb.Append("typebeat-gameplay-fingerprint/").Append(VERSION).Append('\n');

        // The audio the difficulty actually points at. "absent" for a difficulty whose audio is
        // not in the package: PackageValidator refuses that on the upload path, so this only ever
        // shows up for a historical row the backfill reaches, and it is a stable value rather than
        // a hole (two audio-less versions of the same map compare equal, as they should).
        var audio = Resolve(files, difficulty.AudioFilename);

        sb.Append("audio:").Append(audio?.Sha256Hex ?? "absent").Append('\n');

        var lyricLines = difficulty.LyricSectionLines;

        sb.Append("lyrics:").Append(lyricLines.Count).Append('\n');

        foreach (string line in lyricLines)
        {
            string stripped = beatdrop_field.Replace(line, string.Empty);
            sb.Append(stripped.Length).Append(':').Append(stripped).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds a package file by the name a .osu refers to it as. Same rule
    /// <see cref="PackageIngest"/> resolves the background and audio with: forward-slashed and
    /// case-insensitive, because the archive is written on one OS and read on another.
    /// </summary>
    public static PackageFileEntry? Resolve(IReadOnlyList<PackageFileEntry> files, string? filename)
    {
        if (string.IsNullOrEmpty(filename))
            return null;

        string normalized = BeatmapPackageParser.NormalizeFilename(filename);
        return files.FirstOrDefault(f => string.Equals(f.Filename, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
