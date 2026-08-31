using System.Text.Json;

namespace Typebeat.Web.Packages.Lyrics;

/// <summary>One whitespace token of a lyric line with resolved timing (game's TimedUnit).</summary>
public sealed class TimedUnit
{
    public required string Text { get; init; }
    public required double StartTime { get; init; }
    public required double EndTime { get; init; }
}

/// <summary>A resolved lyric line (game's LyricLine, minus gameplay-only fields).</summary>
public sealed class LyricLine
{
    /// <summary>Already normalized via <see cref="Typeability.Normalize"/>.</summary>
    public required string RawText { get; init; }

    public required double StartTime { get; init; }

    /// <summary>Hard seal deadline == next line's StartTime (last line: vocal end + tail).</summary>
    public required double EndTime { get; init; }

    /// <summary>Vocal end estimate; StartTime &lt;= SingEndTime &lt;= EndTime.</summary>
    public required double SingEndTime { get; init; }

    /// <summary>One per whitespace token of <see cref="RawText"/>, in order.</summary>
    public required IReadOnlyList<TimedUnit> Units { get; init; }
}

/// <summary>
/// Server-side port of the game's [Lyrics]-section timing pipeline, reduced to what the upload
/// stats need (unit/line times; the judge-granularity and seal-grace machinery only affect
/// gameplay windows, never cell target times, so it is deliberately not ported):
///
///  - one compact JSON object per [Lyrics] line: a header object (no "text" key:
///    version/song_end_ms/beatdrop_ms/granularity) followed by one line object each, exactly as
///    written by LyricOsuFormat.GenerateOsu (typebeat-osu
///    typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricOsuFormat.cs:112-133) and read back by
///    LyricBeatmapDecoder.parseLyricLine (Beatmaps/LyricBeatmapDecoder.cs:81-116);
///  - raw-line parsing per TimingJsonLoader.TryParseRawLine (Beatmaps/TimingJsonLoader.cs:114-185);
///  - line resolution per TimingJsonLoader.BuildLines (:191-255), including the char-weighted
///    interpolation fallback LrcParser.InterpolateUnits (Beatmaps/LrcParser.cs:176-215).
/// </summary>
public static class LyricTiming
{
    // TimingJsonLoader.cs:23-24.
    public const int SUPPORTED_VERSION = 2;
    public const double LAST_LINE_TAIL_MS = 3000;

    public readonly record struct RawLine(
        string Text,
        double StartMs,
        double EndMs,
        List<(string Text, double Start, double End)> Words);

    /// <summary>Header fields of a [Lyrics] section (all optional on the wire).</summary>
    public sealed class Header
    {
        public double? SongEndMs { get; set; }
        public double? BeatdropMs { get; set; }
    }

    /// <summary>
    /// Parses the raw text lines of a [Lyrics] section. Mirrors LyricBeatmapDecoder.parseLyricLine:
    /// unparseable JSON lines are skipped, an object without "text" is the header, the rest go
    /// through <see cref="TryParseRawLine"/>.
    /// </summary>
    /// <param name="stripBackingVocals">
    /// Passed straight to <see cref="TryParseRawLine"/>; see the seam described there. The caller
    /// that knows is <c>BeatmapPackageParser</c>, which reads the file's FORMAT VERSION off its
    /// magic line and passes <c>version &lt; LiteralBracketsFromVersion</c>, exactly as the game's
    /// decoder does. Defaults to false (the map-format contract, brackets are literal), so a caller
    /// holding a [Lyrics] section with no file around it gets the CURRENT reading.
    /// </param>
    public static (Header Header, IReadOnlyList<LyricLine> Lines) ParseSection(IEnumerable<string> sectionLines, bool stripBackingVocals = false)
    {
        var header = new Header();
        var raw = new List<RawLine>();

        foreach (string line in sectionLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (!root.TryGetProperty("text", out _))
                {
                    if (root.TryGetProperty("song_end_ms", out JsonElement songEnd) && songEnd.ValueKind == JsonValueKind.Number)
                        header.SongEndMs = songEnd.GetDouble();

                    if (root.TryGetProperty("beatdrop_ms", out JsonElement beatdrop) && beatdrop.ValueKind == JsonValueKind.Number)
                        header.BeatdropMs = beatdrop.GetDouble();

                    continue;
                }

                if (TryParseRawLine(root, out var rawLine, stripBackingVocals))
                    raw.Add(rawLine);
            }
            catch (JsonException)
            {
                // Client logs and skips (LyricBeatmapDecoder.cs:112-115); same tolerance here.
            }
        }

        return (header, BuildLines(raw, header.SongEndMs));
    }

    /// <summary>
    /// One timing.json "lines[]" element -> <see cref="RawLine"/>. False for non-objects, missing
    /// text/start_ms, and lines whose text gives the player no cell at all (whole-line backing
    /// vocals among them). (TimingJsonLoader.TryParseRawLine, TimingJsonLoader.cs:114-185.)
    /// </summary>
    /// <param name="stripBackingVocals">
    /// THE SEAM backlog 255 cut, mirrored from the game's flag of the same name: the difference
    /// between reading an IMPORT and reading a STORED MAP. It defaults to false, the map-format
    /// contract, so a '(' in a saved [Lyrics] line is a literal lyric mark and survives the parse
    /// like any other punctuation. The server only ever reads stored maps, so its one true caller
    /// is the FORMAT VERSION GATE: <c>BeatmapPackageParser</c> passes true for a file below
    /// <c>BeatmapPackageParser.LiteralBracketsFromVersion</c>, where a bracket is a backing vocal
    /// by construction and is stripped exactly as it always was. There a whole bracketed line still
    /// yields no cell and is dropped, and a partial strip still changes the token count so the
    /// words[] pairing in <see cref="BuildLines"/> falls back to interpolation for that line.
    /// </param>
    public static bool TryParseRawLine(JsonElement lineElement, out RawLine rawLine, bool stripBackingVocals = false)
    {
        rawLine = default;

        if (lineElement.ValueKind != JsonValueKind.Object)
            return false;

        if (!lineElement.TryGetProperty("text", out JsonElement textElement)
            || textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // Opt-in freestyle authoring (type!beat editor extension, written by the editor's encoder):
        // "freestyle": true declares that the ampersands in this line's text are FREESTYLE CELL
        // markers rather than lyric punctuation. Without the flag the text normalizes exactly as it
        // always has (ampersands stripped), so every map produced before this feature, and every
        // aligner line whose lyrics genuinely contain "&", ingests unchanged.
        bool freestyle = lineElement.TryGetProperty("freestyle", out JsonElement freestyleElement)
                         && freestyleElement.ValueKind == JsonValueKind.True;

        string raw = textElement.GetString() ?? string.Empty;

        string normalized = Typeability.Normalize(stripBackingVocals ? Typeability.StripBackingVocals(raw) : raw,
            keepFreestyleMarkers: freestyle);

        // A line with nothing to TYPE is dropped, and the previous line extends over its span.
        // Tested on the DEFAULT stream, not on the normalized text, because a line that is nothing
        // but punctuation ("...") now normalizes non-empty yet still gives the player no cell at
        // all. The two conditions coincide exactly for every other input, so this is the same rule
        // the parser has always applied (and the same one the browser core applies).
        if (Typeability.ToDefaultStream(normalized).Length == 0)
            return false;

        if (!lineElement.TryGetProperty("start_ms", out JsonElement startElement)
            || !tryGetDouble(startElement, out double startMs))
        {
            return false;
        }

        double endMs = startMs;

        if (lineElement.TryGetProperty("end_ms", out JsonElement endElement)
            && tryGetDouble(endElement, out double parsedEnd))
        {
            endMs = parsedEnd;
        }

        var words = new List<(string Text, double Start, double End)>();

        if (lineElement.TryGetProperty("words", out JsonElement wordsElement)
            && wordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement wordElement in wordsElement.EnumerateArray())
            {
                if (wordElement.ValueKind != JsonValueKind.Object)
                    continue;

                string wordText = wordElement.TryGetProperty("text", out JsonElement wt) && wt.ValueKind == JsonValueKind.String
                    ? wt.GetString() ?? string.Empty
                    : string.Empty;

                double ws = wordElement.TryGetProperty("start_ms", out JsonElement wsEl) && tryGetDouble(wsEl, out double wsv) ? wsv : startMs;
                double we = wordElement.TryGetProperty("end_ms", out JsonElement weEl) && tryGetDouble(weEl, out double wev) ? wev : ws;

                words.Add((wordText, ws, we));
            }
        }

        rawLine = new RawLine(normalized, startMs, endMs, words);
        return true;
    }

    /// <summary>
    /// Resolves per-line End/SingEnd and word units for an ordered set of raw lines: a non-last
    /// line's hard seal is the next line's start; the last line gets vocal end + 3 s tail capped
    /// at song_end_ms. (TimingJsonLoader.BuildLines, TimingJsonLoader.cs:191-255.)
    /// </summary>
    public static IReadOnlyList<LyricLine> BuildLines(IReadOnlyList<RawLine> raw, double? songEndMs)
    {
        var result = new List<LyricLine>(raw.Count);

        for (int i = 0; i < raw.Count; i++)
        {
            RawLine line = raw[i];
            double start = line.StartMs;

            double end;

            if (i < raw.Count - 1)
            {
                end = raw[i + 1].StartMs;
            }
            else
            {
                double tailEnd = line.EndMs + LAST_LINE_TAIL_MS;
                end = songEndMs.HasValue ? Math.Min(songEndMs.Value, tailEnd) : tailEnd;
            }

            if (end < start)
                end = start;

            double singEnd = Math.Clamp(line.EndMs, start, end);

            string[] tokens = line.Text.Split(' ');
            IReadOnlyList<TimedUnit> units;

            if (tokens.Length == line.Words.Count && tokens.Length > 0)
                units = buildExplicitUnits(tokens, line.Words, start, end);
            else
                units = InterpolateUnits(line.Text, start, singEnd);

            result.Add(new LyricLine
            {
                RawText = line.Text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
            });
        }

        return result;
    }

    /// <summary>
    /// Distributes [start, end] over the whitespace tokens, weighting each by
    /// (typeableCount + 1). (LrcParser.InterpolateUnits, LrcParser.cs:176-215.)
    /// </summary>
    public static IReadOnlyList<TimedUnit> InterpolateUnits(string normalizedText, double start, double end)
    {
        var units = new List<TimedUnit>();
        if (string.IsNullOrEmpty(normalizedText))
            return units;

        string[] tokens = normalizedText.Split(' ');

        double totalWeight = 0;
        double[] weights = new double[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            weights[i] = Typeability.TypeableCount(tokens[i]) + 1;
            totalWeight += weights[i];
        }

        if (totalWeight <= 0)
            totalWeight = tokens.Length;

        double span = end - start;
        double cumulative = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            double unitStart = start + span * (cumulative / totalWeight);
            cumulative += weights[i];
            double unitEnd = start + span * (cumulative / totalWeight);

            units.Add(new TimedUnit
            {
                Text = tokens[i],
                StartTime = unitStart,
                EndTime = unitEnd,
            });
        }

        return units;
    }

    // TimingJsonLoader.buildExplicitUnits (TimingJsonLoader.cs:257-290): clamp into the line
    // window and enforce non-decreasing across units.
    private static IReadOnlyList<TimedUnit> buildExplicitUnits(
        string[] tokens,
        List<(string Text, double Start, double End)> words,
        double lineStart,
        double lineEnd)
    {
        var units = new List<TimedUnit>(tokens.Length);
        double prevEnd = lineStart;

        for (int m = 0; m < tokens.Length; m++)
        {
            double ws = Math.Clamp(words[m].Start, lineStart, lineEnd);
            double we = Math.Clamp(words[m].End, ws, lineEnd);

            if (ws < prevEnd)
                ws = prevEnd;
            if (we < ws)
                we = ws;

            units.Add(new TimedUnit
            {
                Text = tokens[m],
                StartTime = ws,
                EndTime = we,
            });

            prevEnd = we;
        }

        return units;
    }

    private static bool tryGetDouble(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out value);

        return false;
    }
}
