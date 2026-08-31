using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests;

/// <summary>
/// Builders for synthetic "type!beat file format" beatmap packages, modeled line-for-line on
/// the game's writer (typebeat-osu typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricOsuFormat.cs:69-135)
/// plus the BeatmapID/BeatmapSetID lines the submission flow injects (LegacyBeatmapEncoder.cs:147-148
/// shape; LyricOsuFormat itself does not emit ids yet).
///
/// <para>The magic line carries <see cref="CurrentFormatVersion"/> by default, because that is what
/// the game's writer stamps now (LyricOsuFormat.FORMAT_VERSION) and therefore what an upload looks
/// like today. A test that wants the LEGACY reading of a bracket passes
/// <c>formatVersion: BeatmapPackageParser.FallbackFormatVersion</c>.</para>
/// </summary>
public static class SyntheticPackage
{
    /// <summary>
    /// The format version <see cref="OsuText"/> stamps unless told otherwise: what the game's
    /// writer emits today, so the default fixture is a CURRENT upload and its brackets are literal.
    /// </summary>
    public const int CurrentFormatVersion = BeatmapPackageParser.LiteralBracketsFromVersion;

    /// <summary>
    /// The [Lyrics] payload of the game's own pace regression test
    /// (LyricPaceStatisticsTest.ComputesBoundaryWindowPace): "ab cd", 2 words, 5 typeable cells,
    /// boundary window 3000 ms (start 1000 -> line end 4000) -> CPM 100, WPM 20 (CPM/5 since
    /// LyricPace v15), 2.5 cells per word.
    /// </summary>
    public const string PaceRegressionLyrics =
        """
        {"version":2,"song_end_ms":4000,"granularity":"Word"}
        {"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}
        """;

    public static string OsuText(
        string title = "Neon Nights",
        string titleUnicode = "Neon Nights",
        string artist = "Synth Rider",
        string artistUnicode = "Synth Rider",
        string creator = "uploader",
        string version = "type!beat",
        string source = "",
        string tags = "typebeat lyrics typing",
        // Empty = the file states no language, which is exactly what every pre-task-58 client
        // wrote and what the game's encoder still writes for an "unspecified" map.
        string language = "",
        long? beatmapId = 1001,
        long? beatmapSetId = 1,
        string audioFilename = "audio.mp3",
        double previewTime = -1,
        string? background = "bg.jpg",
        string? video = null,
        string lyrics = PaceRegressionLyrics,
        int formatVersion = CurrentFormatVersion)
    {
        var sb = new StringBuilder();

        sb.Append($"{BeatmapPackageParser.OsuMagic}{formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        sb.Append('\n');
        sb.Append("[General]\n");
        sb.Append($"AudioFilename: {audioFilename}\n");
        sb.Append("AudioLeadIn: 0\n");
        sb.Append($"PreviewTime: {previewTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        sb.Append("Countdown: 0\n");
        sb.Append("SampleSet: None\n");
        sb.Append('\n');
        sb.Append("[Metadata]\n");
        sb.Append($"Title:{title}\n");
        sb.Append($"TitleUnicode:{titleUnicode}\n");
        sb.Append($"Artist:{artist}\n");
        sb.Append($"ArtistUnicode:{artistUnicode}\n");
        sb.Append($"Creator:{creator}\n");
        sb.Append($"Version:{version}\n");

        if (source.Length > 0)
            sb.Append($"Source:{source}\n");

        sb.Append($"Tags:{tags}\n");

        // Written only when set, mirroring the game's encoder: an unspecified map emits no line at
        // all, which keeps existing maps' encodings byte-identical.
        if (language.Length > 0)
            sb.Append($"Language:{language}\n");

        if (beatmapId != null)
            sb.Append($"BeatmapID: {beatmapId}\n");
        if (beatmapSetId != null)
            sb.Append($"BeatmapSetID: {beatmapSetId}\n");

        sb.Append('\n');
        sb.Append("[Difficulty]\n");
        sb.Append("HPDrainRate:5\n");
        sb.Append("CircleSize:5\n");
        sb.Append("OverallDifficulty:5\n");
        sb.Append("ApproachRate:5\n");
        sb.Append("SliderMultiplier:1.4\n");
        sb.Append("SliderTickRate:1\n");

        if (background != null || video != null)
        {
            sb.Append('\n');
            sb.Append("[Events]\n");
            sb.Append("//Background and Video events\n");

            if (background != null)
                sb.Append($"0,0,\"{background}\",0,0\n");
            if (video != null)
                sb.Append($"Video,0,\"{video}\"\n");
        }

        sb.Append('\n');
        sb.Append("[TimingPoints]\n");
        sb.Append("0,500,4,2,0,100,1,0\n");
        sb.Append('\n');
        sb.Append("[Lyrics]\n");
        sb.Append(lyrics.ReplaceLineEndings("\n"));
        sb.Append('\n');

        return sb.ToString();
    }

    /// <summary>Builds an in-memory package zip from (path, content) pairs.</summary>
    public static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A decodable 64x48 PNG for cover-generation paths.</summary>
    public static byte[] TinyPng()
    {
        using var image = new Image<Rgba32>(64, 48);

        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image[x, y] = new Rgba32((byte)(x * 4), (byte)(y * 5), 128);

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
