using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Packages;

/// <summary>
/// Parses an uploaded beatmap package (a plain zip: one .osu per difficulty + audio/background/
/// video/skin files, no manifest; see the game's exporter, typebeat-osu
/// typebeat.Game/Database/BeatmapExporter.cs) into content-addressed
/// <see cref="PackageFileEntry"/>s plus a <see cref="ParsedDifficulty"/> per .osu.
///
/// The .osu dialect is the "type!beat file format" written by LyricOsuFormat.GenerateOsu
/// (typebeat-osu typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricOsuFormat.cs:69-135): classic
/// .osu sections plus a [Lyrics] section of compact JSON objects. Section parsing here is the
/// minimal server-side subset, only the keys the database stores.
///
/// This is the server's LyricBeatmapDecoder, so it also owns that decoder's FORMAT VERSION gate
/// (see <see cref="ParseFormatVersion"/>): the number on the magic line decides whether a bracket
/// in a stored [Lyrics] line is a backing vocal to strip or a literal lyric mark to keep.
///
/// Structural problems (bad zip, undecodable .osu, hostile entry names) throw
/// <see cref="PackageValidationException"/>; semantic invariants live in <see cref="PackageValidator"/>.
/// </summary>
public static class BeatmapPackageParser
{
    /// <summary>
    /// First line of every difficulty file, up to but not including the version number
    /// (LyricBeatmapDecoder.MAGIC). Matched as a PREFIX, so every version of the format is
    /// accepted here and the number after it is read by <see cref="ParseFormatVersion"/>.
    /// </summary>
    public const string OsuMagic = "type!beat file format v";

    /// <summary>
    /// The version a file with no readable number is treated as (LyricBeatmapDecoder
    /// .FALLBACK_FORMAT_VERSION). It is the ORIGINAL format, so an unparseable magic line falls
    /// back to the historical reading (brackets stripped) rather than to the current one, which is
    /// the direction that cannot invent lyric content for a map that never had it.
    /// </summary>
    public const int FallbackFormatVersion = 1;

    /// <summary>
    /// The first format version whose [Lyrics] brackets are LITERAL lyric marks rather than
    /// backing-vocal spans to strip, backlog 255 (LyricBeatmapDecoder.LITERAL_BRACKETS_FROM_VERSION,
    /// and the version LyricOsuFormat.FORMAT_VERSION now stamps). Below it the parse strips, at or
    /// above it the parse preserves.
    ///
    /// <para>The version is a sound discriminator rather than a guess: no write path that produced
    /// a v1 file could store a literal bracket, because every one of them ran
    /// <see cref="Lyrics.Typeability.StripBackingVocals"/> before the text was written, so a '('
    /// in a v1 file IS a backing vocal by construction. Every map stored before backlog 255 is v1,
    /// so every existing row re-parses byte-identically.</para>
    /// </summary>
    public const int LiteralBracketsFromVersion = 2;

    /// <summary>
    /// The version number off the magic line, or <see cref="FallbackFormatVersion"/> when there is
    /// none to read. Only the digits immediately after <see cref="OsuMagic"/> are taken, so
    /// trailing whitespace or anything else on the line is ignored rather than fatal.
    /// (LyricBeatmapDecoder.ParseFormatVersion, mirrored char for char.)
    /// </summary>
    public static int ParseFormatVersion(string magicLine)
    {
        if (string.IsNullOrEmpty(magicLine) || !magicLine.StartsWith(OsuMagic, StringComparison.Ordinal))
            return FallbackFormatVersion;

        int end = OsuMagic.Length;

        while (end < magicLine.Length && char.IsAsciiDigit(magicLine[end]))
            end++;

        return int.TryParse(magicLine.AsSpan(OsuMagic.Length, end - OsuMagic.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out int version)
            ? version
            : FallbackFormatVersion;
    }

    /// <summary>osu-server-beatmap-submission caps archive path names at 500 (beatmapset_version_file).</summary>
    public const int MaxFilenameLength = 500;

    /// <summary>Decompression-bomb guard: total decompressed bytes across all entries.</summary>
    private const long max_total_uncompressed = 1L << 30; // 1 GiB

    /// <param name="zipStream">The package bytes; must be seekable (buffer uploads to a temp file first).</param>
    public static ParsedPackage Parse(Stream zipStream)
    {
        long packageSize = zipStream.Length;
        zipStream.Position = 0;

        ZipArchive archive;

        try
        {
            archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new PackageValidationException("The uploaded package is not a valid zip archive.");
        }

        using (archive)
        {
            var files = new List<PackageFileEntry>();
            var difficulties = new List<ParsedDifficulty>();
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                // Directory entries carry no content.
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue;

                string filename = NormalizeFilename(entry.FullName);
                validateFilename(filename);

                totalUncompressed += entry.Length;
                if (totalUncompressed > max_total_uncompressed)
                    throw new PackageValidationException("The package decompresses to more than 1 GiB.");

                byte[] content;

                try
                {
                    using var entryStream = entry.Open();
                    using var buffer = new MemoryStream(checked((int)Math.Min(entry.Length, int.MaxValue)));
                    entryStream.CopyTo(buffer);
                    content = buffer.ToArray();
                }
                catch (InvalidDataException)
                {
                    throw new PackageValidationException($"File \"{filename}\" in the package is corrupt.");
                }

                files.Add(new PackageFileEntry(SHA256.HashData(content), content.LongLength, filename));

                if (filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    difficulties.Add(parseOsu(filename, content));
            }

            return new ParsedPackage
            {
                Files = files,
                Difficulties = difficulties,
                PackageSize = packageSize,
            };
        }
    }

    /// <summary>Archive paths are compared and stored with forward slashes (osu's ToStandardisedPath).</summary>
    public static string NormalizeFilename(string archivePath) => archivePath.Replace('\\', '/');

    /// <summary>
    /// Parses a single .osu difficulty outside the zip path, used by <see cref="PaceBackfill"/>
    /// to recompute pace/star numbers from stored blobs without reassembling the package.
    /// </summary>
    public static ParsedDifficulty ParseDifficulty(string filename, byte[] content) => parseOsu(filename, content);

    private static void validateFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new PackageValidationException("The package contains a file with an empty name.");

        if (filename.Length > MaxFilenameLength)
            throw new PackageValidationException($"Filename \"{filename[..64]}...\" exceeds {MaxFilenameLength} characters.");

        // Traversal is a ".." PATH SEGMENT, not any run of dots: legitimate titles routinely end in
        // an ellipsis ("I know youre hurting...mp3"), and a substring check rejected those outright.
        // Backslashes are already folded to '/' by NormalizeFilename before this runs, so splitting
        // on '/' sees every real segment.
        bool traversal = filename.Split('/').Any(segment => segment == "..");

        if (filename.StartsWith('/') || traversal || filename.Contains(':'))
            throw new PackageValidationException($"Filename \"{filename}\" is not a valid relative path.");
    }

    private static ParsedDifficulty parseOsu(string filename, byte[] content)
    {
        string checksumMd5 = Convert.ToHexStringLower(MD5.HashData(content));

        string text;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(stripBom(content));
        }
        catch (DecoderFallbackException)
        {
            throw new PackageValidationException($"\"{filename}\" is not valid UTF-8 text.");
        }

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
            first++;

        if (first == lines.Length || !lines[first].TrimStart().StartsWith(OsuMagic, StringComparison.Ordinal))
            throw new PackageValidationException($"\"{filename}\" is not a type!beat beatmap (missing \"{OsuMagic}\" header).");

        // THE VERSION GATE (backlog 255), read off the magic line exactly as the game's decoder
        // reads it. From v2 on a bracket in a stored [Lyrics] line is a literal lyric mark and
        // stays; a v1 file predates that and its brackets are backing vocals, stripped as they
        // always were.
        int formatVersion = ParseFormatVersion(lines[first].TrimStart());

        string audioFilename = string.Empty;
        double previewTime = -1;
        string title = string.Empty, titleUnicode = string.Empty;
        string artist = string.Empty, artistUnicode = string.Empty;
        string creator = string.Empty, versionName = string.Empty;
        string source = string.Empty, tags = string.Empty, language = string.Empty;
        string lyricFont = string.Empty, lyricFontFile = string.Empty;
        long? beatmapId = null, beatmapSetId = null;
        string? background = null, video = null;
        double? bpm = null;

        string section = string.Empty;
        var lyricLines = new List<string>();

        for (int i = first + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            switch (section)
            {
                case "General":
                case "Metadata":
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0)
                        break;

                    string key = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();

                    switch (key)
                    {
                        case "AudioFilename": audioFilename = value; break;
                        case "PreviewTime": previewTime = parseDouble(value) ?? -1; break;
                        case "Title": title = value; break;
                        case "TitleUnicode": titleUnicode = value; break;
                        case "Artist": artist = value; break;
                        case "ArtistUnicode": artistUnicode = value; break;
                        case "Creator": creator = value; break;
                        case "Version": versionName = value; break;
                        case "Source": source = value; break;
                        case "Tags": tags = value; break;
                        // Song language (019_language.sql): the mapper's editor choice, written by
                        // the game's encoder ONLY when it is a real language, so its absence here
                        // means "not stated" and never "not a language". Kept as the raw text;
                        // PackageIngest folds it onto the canonical vocabulary.
                        case "Language": language = value; break;
                        // The mapper-chosen lyric font (backlog 291): [General] "LyricFont: <family>"
                        // and "LyricFontFile: <filename in the set>", both written by the game's
                        // encoders only when set, so absence means "no font chosen" and never "no
                        // font". The family is free text; the file value is a bare filename
                        // (the editor writes lyricfont.<ext>), normalized like every other archive
                        // path so the version-manifest lookup matches it.
                        case "LyricFont": lyricFont = value; break;
                        case "LyricFontFile": lyricFontFile = NormalizeFilename(value); break;
                        case "BeatmapID": beatmapId = parseLong(value); break;
                        case "BeatmapSetID": beatmapSetId = parseLong(value); break;
                    }

                    break;
                }

                case "Events":
                {
                    // Background: `0,0,"bg.jpg",0,0` (LegacyEventType.Background = 0; also accepts
                    // the name form). Video: `Video,0,"clip.mp4"` (LegacyEventType.Video = 1).
                    string[] fields = line.Split(',');
                    if (fields.Length < 3)
                        break;

                    string type = fields[0].Trim();
                    string eventFilename = fields[2].Trim().Trim('"');

                    if (type is "0" or "Background")
                        background ??= eventFilename;
                    else if (type is "1" or "Video")
                        video ??= eventFilename;

                    break;
                }

                case "TimingPoints":
                {
                    // time,beatLength,... first uninherited (positive beatLength) point wins.
                    // (LyricOsuFormat writes exactly one: "0,500,4,2,0,100,1,0" -> 120 BPM.)
                    if (bpm != null)
                        break;

                    string[] fields = line.Split(',');
                    if (fields.Length < 2)
                        break;

                    double? beatLength = parseDouble(fields[1]);
                    if (beatLength is > 0)
                        bpm = 60000 / beatLength.Value;

                    break;
                }

                case "Lyrics":
                    lyricLines.Add(line);
                    break;
            }
        }

        var (_, parsedLines) = LyricTiming.ParseSection(lyricLines,
            stripBackingVocals: formatVersion < LiteralBracketsFromVersion);

        return new ParsedDifficulty
        {
            Filename = filename,
            ChecksumMd5 = checksumMd5,
            AudioFilename = audioFilename,
            PreviewTime = previewTime,
            Title = title,
            TitleUnicode = titleUnicode,
            Artist = artist,
            ArtistUnicode = artistUnicode,
            Creator = creator,
            VersionName = versionName,
            Source = source,
            Tags = tags,
            Language = language,
            LyricFont = lyricFont,
            LyricFontFile = lyricFontFile,
            BeatmapId = beatmapId,
            BeatmapSetId = beatmapSetId,
            BackgroundFilename = background,
            VideoFilename = video,
            Bpm = bpm,
            LyricSectionLines = lyricLines,
            Lines = parsedLines,
            Pace = LyricPace.Compute(parsedLines),
        };
    }

    private static byte[] stripBom(byte[] content)
        => content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? content[3..]
            : content;

    private static double? parseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    private static long? parseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : null;
}
