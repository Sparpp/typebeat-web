using System.Text.Json;

namespace Typebeat.Web.Packages.Lyrics;

/// <summary>One whitespace token of a lyric line with resolved timing (game's TimedUnit).</summary>
public sealed class TimedUnit
{
    public required string Text { get; init; }
    public required double StartTime { get; init; }
    public required double EndTime { get; init; }

    /// <summary>
    /// The word's AUTHORED syllable starts, strictly inside (<see cref="StartTime"/>,
    /// <see cref="EndTime"/>) and ascending, or empty when the word carries none (game's
    /// TimedUnit.SyllableBoundaries, LyricBeatmap.cs:445).
    ///
    /// <para>The server ignored these until the difficulty rework, because nothing it computed read
    /// them: the pace figures do not, and the envelope model spreads a word's cells uniformly across
    /// its whole span. The rhythm arm of the chunked axis DOES (see
    /// <c>LyricDifficulty.PressIntervals</c>): a cell is judged inside its own syllable's sung span,
    /// so a subdivided word offers a different set of judgement intervals from the one-segment
    /// fallback (since LyricPace v24, backlog 363: the engine no longer syllabifies at gameplay), and
    /// a mirror that dropped the boundaries would rate every subdivided map differently from the
    /// client. The client's import pass now writes its syllabifier's cut here, as ordinary
    /// <c>syllables</c> objects (with <c>split_chars</c>, which only the engines read), so these are
    /// the only route by which that cut reaches a stored rating. Empty by default, so every caller that builds a unit by hand
    /// (the tests, the interpolation fallback) reads exactly as it did.</para>
    ///
    /// <para>NOT PORTED, and the one place the two sides can still disagree: the '|' SPLIT MARKER
    /// path. The game keeps pipes through normalization, strips them into per-word character
    /// positions and lets them AUTHOR even boundaries over a word the aligner did not subdivide
    /// (Beatmaps/SplitMarkers.cs, Gameplay/SyllableSegments.IsAuthoredValid). The server's
    /// <see cref="Typeability"/> has no split marker at all, so a pipe is simply dropped from the
    /// text as any unsupported character is: the stored TEXT agrees with the game's, and only a
    /// piped word with no <c>syllables</c> array of its own rates here as unsubdivided.</para>
    /// </summary>
    public IReadOnlyList<double> SyllableBoundaries { get; init; } = Array.Empty<double>();

    /// <summary>
    /// The authored pauses inside this word, in TIME order: empty for every word in every map written
    /// before the feature existed, and for every word that never needed one. See
    /// <see cref="WordPause"/> (game's TimedUnit.Pauses, LyricBeatmap.cs).
    ///
    /// <para>N rests cut the word into N + 1 sung stretches, each timed in its own right by
    /// <see cref="PausedWord"/>, which is what <see cref="LyricDifficulty"/> reads them through. The
    /// rests never overlap each other and never share a character, and each one's cut stands in the
    /// same order as its time. The parser keeps only the rests <see cref="PausedWord.UsableRests"/>
    /// accepts against the CLAMPED word, exactly as the game's loader does, so a rest the play would
    /// ignore is never stored here. Empty by default, so every caller that builds a unit by hand
    /// reads exactly as it did.</para>
    /// </summary>
    public IReadOnlyList<WordPause> Pauses { get; init; } = Array.Empty<WordPause>();

    /// <summary>
    /// The word as the song WRITES it, in its own script (backlog 330; game's
    /// <c>TimedUnit.Original</c>), read off the word object's optional <c>original</c> key and null
    /// when it has none. PRESERVED, and never read by a BASE figure: nothing the server computes for a
    /// play without Polyglot reads it, which is the contract that lets a map gain originals without a
    /// single base rating or fingerprint moving (see <see cref="GameplayFingerprint"/>). Since PR 43 the
    /// Polyglot half of the rating matrix reads it (<see cref="PolyglotSummary"/>), through the Polyglot
    /// parse, which keeps it exactly as written.
    /// </summary>
    public string? Original { get; init; }

    /// <summary>
    /// Where the word's timing came from (game's <c>TimedUnit.Source</c>): explicit word timing or
    /// the interpolation fallback. Read only by the Polyglot mirror, which carries it through.
    /// </summary>
    public TimingSource Source { get; init; } = TimingSource.Interpolated;

    /// <summary>The aligner's score for the word (game's <c>TimedUnit.Confidence</c>), 1 when absent.</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>
    /// The word's AUTHORED character splits, one per kept boundary (game's
    /// <c>TimedUnit.SyllableSplits</c>, the <c>split_chars</c> key), or empty for a derived split.
    /// Kept only under the game's rule (nothing clamped away, a valid split of the token). No base
    /// rating reads it; the Polyglot mirror carries romanised cuts onto the original through it.
    /// </summary>
    public IReadOnlyList<int> SyllableSplits { get; init; } = Array.Empty<int>();

    /// <summary>The mapper's explicit cut links between the two spellings (game's <c>TimedUnit.OriginalSplits</c>).</summary>
    public IReadOnlyList<OriginalLyricSplit> OriginalSplits { get; init; } = Array.Empty<OriginalLyricSplit>();

    /// <summary>The word's objects (game's <c>TimedUnit.Objects</c>); <see cref="WordObjects.None"/> for every word without any.</summary>
    public WordObjects Objects { get; init; } = WordObjects.None;

    /// <summary>
    /// The syllables the game's '|' split markers give this word (<see cref="SplitMarkers"/>), or null
    /// when no pipe cuts it. No rating reads it; only <see cref="JudgedCells"/> does, via <see cref="AsPlayed"/>.
    /// </summary>
    public (IReadOnlyList<double> Boundaries, IReadOnlyList<int> Splits)? SplitMarkerSyllables { get; init; }

    /// <summary>This word as the game plays it: its <see cref="SplitMarkerSyllables"/>, when it has any, in place of the rated syllables.</summary>
    public TimedUnit AsPlayed()
        => SplitMarkerSyllables is { } played ? copy(Objects, played.Boundaries, played.Splits, null) : this;

    /// <summary>This word with <paramref name="objects"/> in place of its own (game's <c>TimedUnit.WithObjects</c>).</summary>
    public TimedUnit WithObjects(WordObjects objects) => copy(objects, SyllableBoundaries, SyllableSplits, SplitMarkerSyllables);

    internal TimedUnit WithSplitMarkerSyllables((IReadOnlyList<double> Boundaries, IReadOnlyList<int> Splits) played)
        => copy(Objects, SyllableBoundaries, SyllableSplits, played);

    private TimedUnit copy(WordObjects objects, IReadOnlyList<double> boundaries, IReadOnlyList<int> splits,
                           (IReadOnlyList<double> Boundaries, IReadOnlyList<int> Splits)? splitMarkerSyllables) => new TimedUnit
    {
        Text = Text,
        StartTime = StartTime,
        EndTime = EndTime,
        Source = Source,
        Confidence = Confidence,
        SyllableBoundaries = boundaries,
        Pauses = Pauses,
        SyllableSplits = splits,
        Original = Original,
        OriginalSplits = OriginalSplits,
        Objects = objects,
        SplitMarkerSyllables = splitMarkerSyllables,
    };
}

/// <summary>
/// A HOLD (game's <c>WordHold</c>, Beatmaps/WordObjects.cs): the character at <see cref="Char"/>
/// (index into <see cref="TimedUnit.Text"/>) is held from <see cref="StartTime"/> to
/// <see cref="EndTime"/>, absolute ms, with authored <see cref="Ticks"/>.
/// </summary>
public sealed record WordHold(int Char, double StartTime, double EndTime, IReadOnlyList<double> Ticks);

/// <summary>
/// The mapper-authored objects a word carries beyond its timing (game's <c>WordObjects</c>): auto-fill
/// (only the onsets, or the picked <see cref="TypedLetters"/>, are typed) and holds. Rated by
/// <see cref="LyricDifficulty"/>; a map with any is desktop-only (<c>beatmaps.uses_word_objects</c>).
/// </summary>
public sealed class WordObjects
{
    public static WordObjects None { get; } = new WordObjects();

    public bool AutoFill { get; init; }

    public IReadOnlyList<int> TypedLetters { get; init; } = Array.Empty<int>();

    public IReadOnlyList<WordHold> Holds { get; init; } = Array.Empty<WordHold>();

    public bool IsEmpty => !AutoFill && Holds.Count == 0 && TypedLetters.Count == 0;

    /// <summary>These objects for a retyped word (game's <c>WordObjects.ForText</c>): a new spelling drops holds and picks.</summary>
    public WordObjects ForText(string oldText, string newText)
        => (Holds.Count == 0 && TypedLetters.Count == 0) || string.Equals(oldText, newText, StringComparison.Ordinal)
            ? this
            : new WordObjects { AutoFill = AutoFill };
}

/// <summary>Where a unit's timing came from (game's <c>TimingSource</c>).</summary>
public enum TimingSource
{
    Interpolated,
    Explicit
}

/// <summary>A mapper-authored correspondence between cuts in the two spellings (game's <c>OriginalLyricSplit</c>).</summary>
public readonly record struct OriginalLyricSplit(int TextIndex, int OriginalIndex);

/// <summary>
/// A word the romaniser could not spell (game's <c>UnromanisedWord</c>): no token in the line text,
/// its original at <paramref name="Position"/> (the number of typed words before it) over its own span.
/// </summary>
public readonly record struct UnromanisedWord(int Position, string Original, double StartTime, double EndTime);

/// <summary>
/// AN AUTHORED PAUSE INSIDE ONE WORD, the Map Editor's <b>Insert Pause</b> (game's
/// <c>WordPause</c>, LyricBeatmap.cs).
///
/// <para>A rest the singer takes between two of the word's characters, for the multisyllabic words
/// where the breath falls mid-word: nothing is typed while it lasts, the caret waits where it is,
/// and the characters after it are timed from its END. It is deliberately NOT a rest for the pace
/// figures: the word still reads as uninterrupted singing, so the WPM denominators keep counting it
/// as silence inside the word instead of resetting on a breath.</para>
///
/// <para><see cref="SplitChar"/> is the character index the pause sits AFTER (the character before
/// it is the last one typed before the wait), so it is strictly inside the token, like the syllable
/// splits it sits beside. The times are absolute milliseconds, strictly inside the unit's own span,
/// with <see cref="StartTime"/> before <see cref="EndTime"/>. An old map carries none of this at
/// all.</para>
///
/// <para>A word may take SEVERAL rests (see <see cref="TimedUnit.Pauses"/>), one per breath: each is
/// a divider of its own, so the word is sung in as many stretches as it has rests plus one, and every
/// pair must agree with the text: a rest later in TIME sits on a later character than the ones
/// before it, and no two rests share a character or a moment.</para>
/// </summary>
public readonly record struct WordPause(double StartTime, double EndTime, int SplitChar);

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

    /// <summary>
    /// The line as the song writes it (backlog 330; game's <c>LyricLine.Original</c>), or null.
    /// Preserved, never rated, like <see cref="TimedUnit.Original"/>.
    /// </summary>
    public string? Original { get; init; }

    /// <summary>The line's seal grace (game's <c>LyricLine.SealGraceMs</c>); read only by the Polyglot mirror.</summary>
    public double SealGraceMs { get; init; }

    /// <summary>The aligner's estimated flag (game's <c>LyricLine.Estimated</c>); carried, never read.</summary>
    public bool Estimated { get; init; }

    /// <summary>
    /// The line's UNROMANISED words (game's <c>LyricLine.UnromanisedWords</c>). Filled only by the
    /// Polyglot parse (<see cref="LyricTiming.ParseSection"/> with <c>polyglot: true</c>); the base parse
    /// leaves it empty, as it always has.
    /// </summary>
    public IReadOnlyList<UnromanisedWord> UnromanisedWords { get; init; } = Array.Empty<UnromanisedWord>();
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

    /// <summary>TimingJsonLoader.MAX_SEAL_GRACE_MS: the cap on a line's seal grace.</summary>
    public const double MAX_SEAL_GRACE_MS = 700;

    /// <summary>
    /// One parsed [Lyrics] line. <c>Words</c>'s fourth member is the word's AUTHORED SYLLABLE
    /// STARTS as they sit on the wire (the <c>start_ms</c> of each <c>"syllables"</c> object the
    /// game's encoder writes and TimingJsonLoader reads back), filtered to the word's RAW span
    /// exactly as the game's parse filters them: <see cref="buildExplicitUnits"/> applies the
    /// game's remaining rules once the word's clamped span is known. Empty for the words of every
    /// map the aligner did not subdivide, which is what makes this addition inert for them.
    ///
    /// <para><paramref name="WordPauses"/> is PARALLEL to <paramref name="Words"/> (entry i holds
    /// word i's authored pauses exactly as they sat on the wire, unvalidated) and is null when the
    /// line authored none, which is every line of every map written before the feature existed:
    /// the game's <c>TimingJsonLoader.RawLine.WordPauses</c>. It rides beside the word tuple rather
    /// than inside it so every existing construction of this struct is unchanged.</para>
    ///
    /// <para><paramref name="WordObjects"/> is parallel to <paramref name="Words"/> like the pauses, and null when no word carries an object.</para>
    ///
    /// <para><paramref name="Pipes"/> is parallel to the tokens of <paramref name="Text"/>: where each one's
    /// '|' split markers sat (<see cref="SplitMarkers.Strip"/>), null when the line has none.</para>
    /// </summary>
    public readonly record struct RawLine(
        string Text,
        double StartMs,
        double EndMs,
        List<(string Text, double Start, double End, double[] Syllables)> Words,
        List<List<(double Start, double End, int Split)>>? WordPauses = null,
        string? Original = null,
        List<string?>? WordOriginals = null,
        bool Estimated = false,
        double? SealGraceMs = null,
        List<List<int>>? WordSplits = null,
        List<double>? WordScores = null,
        List<List<OriginalLyricSplit>>? WordOriginalSplits = null,
        List<WordObjects>? WordObjects = null,
        IReadOnlyList<IReadOnlyList<int>>? Pipes = null);

    /// <summary>Header fields of a [Lyrics] section (all optional on the wire).</summary>
    public sealed class Header
    {
        public double? SongEndMs { get; set; }
        public double? BeatdropMs { get; set; }

        /// <summary>
        /// The ORIGINALS of the section's UNROMANISED words (backlog 330), in order: every word
        /// written with an empty <c>text</c> beside an <c>original</c>, and every line that has an
        /// original but nothing to type. A map carrying any is a draft the game refuses to submit,
        /// and <see cref="PackageValidator"/> refuses it too.
        /// </summary>
        public List<string> Unromanised { get; } = new List<string>();
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
    public static (Header Header, IReadOnlyList<LyricLine> Lines) ParseSection(IEnumerable<string> sectionLines, bool stripBackingVocals = false,
                                                                               bool polyglot = false)
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

                header.Unromanised.AddRange(UnromanisedOf(root, stripBackingVocals));

                if (TryParseRawLine(root, out var rawLine, stripBackingVocals, polyglot))
                    raw.Add(rawLine);
            }
            catch (JsonException)
            {
                // Client logs and skips (LyricBeatmapDecoder.cs:112-115); same tolerance here.
            }
        }

        return (header, BuildLines(raw, header.SongEndMs, polyglot));
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
    public static bool TryParseRawLine(JsonElement lineElement, out RawLine rawLine, bool stripBackingVocals = false, bool polyglot = false)
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

        // The split markers, as TimingJsonLoader reads them; kept only when stripping them gives the stored text.
        IReadOnlyList<IReadOnlyList<int>>? pipes = null;

        if (SplitMarkers.Carries(raw))
        {
            string piped = Typeability.Normalize(stripBackingVocals ? Typeability.StripBackingVocals(raw) : raw,
                keepFreestyleMarkers: freestyle, keepSplitMarkers: true);

            if (SplitMarkers.Carries(piped) && SplitMarkers.Strip(piped) is var (stripped, positions) && stripped == normalized)
                pipes = positions;
        }

        // A line with nothing to TYPE is dropped, and the previous line extends over its span.
        // Tested on the DEFAULT stream, not on the normalized text, because a line that is nothing
        // but punctuation ("...") now normalizes non-empty yet still gives the player no cell at
        // all. The two conditions coincide exactly for every other input, so this is the same rule
        // the parser has always applied (and the same one the browser core applies).
        //
        // THE POLYGLOT PARSE keeps the game's own rule instead (TimingJsonLoader.TryParseRawLine): a
        // line with no cell survives when it carries an ORIGINAL or an unromanised word, because the
        // original is what a Polyglot play types. The base parse keeps the stricter drop it has
        // always had, so no base rating, pace figure or count moves for it.
        if (Typeability.ToDefaultStream(normalized).Length == 0
            && (!polyglot || readOriginal(lineElement, polyglot) == null && !carriesUnromanisedWord(lineElement, polyglot)))
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

        bool estimated = lineElement.TryGetProperty("estimated", out JsonElement estimatedElement)
                         && estimatedElement.ValueKind == JsonValueKind.True;

        var words = new List<(string Text, double Start, double End, double[] Syllables)>();

        // Parallel to words[]: word i's authored char splits (split_chars), its aligner score and its
        // explicit original cut links, read exactly as TimingJsonLoader reads them. No base figure
        // reads any of the three; the Polyglot mirror carries them onto the original.
        var wordSplits = new List<List<int>>();
        var wordScores = new List<double>();
        var wordOriginalSplits = new List<List<OriginalLyricSplit>>();

        // Parallel to words[]: word i's authored pauses, raw (TimingJsonLoader's wordPauses).
        var wordPauses = new List<List<(double Start, double End, int Split)>>();

        // Parallel to words[]: word i's ORIGINAL (backlog 330), TimingJsonLoader's wordOriginals.
        var wordOriginals = new List<string?>();

        // Parallel to words[]: word i's objects, TimingJsonLoader's wordObjects.
        var wordObjects = new List<WordObjects>();

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

                // The word's authored syllable STARTS, read exactly as TimingJsonLoader reads them
                // (its words[].syllables): each entry is an OBJECT, the one the game's encoder and
                // SynthesizedTimingJson write ({text, start_ms, end_ms}), and its start_ms becomes
                // a boundary when it sits strictly inside the word's RAW span. The first syllable
                // starts at the word's own start, so it contributes none. Anything else in the
                // array (a bare number, an object without a numeric start_ms) is skipped, as the
                // game skips it. The remaining rule ("strictly inside the CLAMPED word") needs the
                // word's resolved span, which BuildLines has and this parse does not.
                //
                // Before PR 2's port this read BARE NUMBERS, a shape no writer produces, so every
                // real subdivided map rated here as unsubdivided while the client read its
                // boundaries. The parser parity test in WireCompat now pins this against the game's
                // own loader.
                double[] syllables = Array.Empty<double>();

                if (wordElement.TryGetProperty("syllables", out JsonElement sylEl) && sylEl.ValueKind == JsonValueKind.Array)
                {
                    var parsed = new List<double>(sylEl.GetArrayLength());

                    foreach (JsonElement entry in sylEl.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.Object
                            && entry.TryGetProperty("start_ms", out JsonElement sylStart)
                            && tryGetDouble(sylStart, out double at)
                            && at > ws && at < we)
                        {
                            parsed.Add(at);
                        }
                    }

                    syllables = parsed.Count == 0 ? Array.Empty<double>() : parsed.ToArray();
                }

                // Missing score = trusted (1).
                double score = wordElement.TryGetProperty("score", out JsonElement scEl) && tryGetDouble(scEl, out double scv) ? scv : 1;

                var splitChars = new List<int>();

                if (wordElement.TryGetProperty("split_chars", out JsonElement splitsEl) && splitsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement splitEl in splitsEl.EnumerateArray())
                    {
                        if (tryGetInt(splitEl, out int split))
                            splitChars.Add(split);
                    }
                }

                words.Add((wordText, ws, we, syllables));
                wordSplits.Add(splitChars);
                wordScores.Add(score);
                wordOriginals.Add(readOriginal(wordElement, polyglot));

                var originalSplits = new List<OriginalLyricSplit>();

                if (wordElement.TryGetProperty("original_splits", out JsonElement links) && links.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement link in links.EnumerateArray())
                    {
                        if (link.ValueKind == JsonValueKind.Array && link.GetArrayLength() == 2
                            && tryGetInt(link[0], out int textIndex) && tryGetInt(link[1], out int originalIndex))
                            originalSplits.Add(new OriginalLyricSplit(textIndex, originalIndex));
                    }
                }

                wordOriginalSplits.Add(originalSplits);

                // THE AUTHORED PAUSES (type!beat editor extension): the rests inside the word, read
                // RAW here and validated against the CLAMPED word in buildExplicitUnits, exactly as
                // the game's TimingJsonLoader does. `pauses` is the array a word writes when it takes
                // more than one breath; the single `pause` object is the shape the feature had before
                // a word could hold several, and reads the same way. An entry missing any of its
                // three members, or carrying a non-number (or a fractional split), is dropped. When
                // `pauses` is an array the single object is not read at all.
                var pauses = new List<(double Start, double End, int Split)>();

                void readPause(JsonElement element)
                {
                    if (element.ValueKind != JsonValueKind.Object
                        || !element.TryGetProperty("start_ms", out JsonElement pauseStart) || !tryGetDouble(pauseStart, out double pauseMsA)
                        || !element.TryGetProperty("end_ms", out JsonElement pauseEnd) || !tryGetDouble(pauseEnd, out double pauseMsB)
                        || !element.TryGetProperty("split", out JsonElement pauseSplit) || !tryGetInt(pauseSplit, out int pauseChar))
                    {
                        return;
                    }

                    pauses.Add((pauseMsA, pauseMsB, pauseChar));
                }

                if (wordElement.TryGetProperty("pauses", out JsonElement pausesEl) && pausesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement pauseEl in pausesEl.EnumerateArray())
                        readPause(pauseEl);
                }
                else if (wordElement.TryGetProperty("pause", out JsonElement singlePauseEl))
                {
                    readPause(singlePauseEl);
                }

                wordPauses.Add(pauses);

                // WORD OBJECTS, read as written (TimingJsonLoader's word loop): picks only on an
                // auto-fill word, a hold only with numeric char/start_ms/end_ms, ticks only numbers.
                bool autoFill = wordElement.TryGetProperty("autofill", out JsonElement autoFillEl) && autoFillEl.ValueKind == JsonValueKind.True;
                var typedLetters = new List<int>();

                if (autoFill && wordElement.TryGetProperty("autofill_letters", out JsonElement lettersEl) && lettersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement letterEl in lettersEl.EnumerateArray())
                    {
                        if (tryGetInt(letterEl, out int letter))
                            typedLetters.Add(letter);
                    }
                }

                var holds = new List<WordHold>();

                if (wordElement.TryGetProperty("holds", out JsonElement holdsEl) && holdsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement holdEl in holdsEl.EnumerateArray())
                    {
                        if (holdEl.ValueKind == JsonValueKind.Object
                            && holdEl.TryGetProperty("char", out JsonElement hc) && tryGetInt(hc, out int holdChar)
                            && holdEl.TryGetProperty("start_ms", out JsonElement hs) && tryGetDouble(hs, out double holdStart)
                            && holdEl.TryGetProperty("end_ms", out JsonElement he) && tryGetDouble(he, out double holdEnd))
                        {
                            var ticks = new List<double>();

                            if (holdEl.TryGetProperty("ticks", out JsonElement ticksEl) && ticksEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement tickEl in ticksEl.EnumerateArray())
                                {
                                    if (tryGetDouble(tickEl, out double tick))
                                        ticks.Add(tick);
                                }
                            }

                            holds.Add(new WordHold(holdChar, holdStart, holdEnd, ticks.ToArray()));
                        }
                    }
                }

                wordObjects.Add(!autoFill && holds.Count == 0 && typedLetters.Count == 0
                    ? WordObjects.None
                    : new WordObjects { AutoFill = autoFill, Holds = holds.ToArray(), TypedLetters = typedLetters.ToArray() });
            }
        }

        double? sealGraceMs = null;

        if (lineElement.TryGetProperty("seal_grace_ms", out JsonElement sealGraceElement)
            && tryGetDouble(sealGraceElement, out double parsedSealGrace))
        {
            sealGraceMs = parsedSealGrace;
        }

        rawLine = new RawLine(normalized, startMs, endMs, words,
            wordPauses.Exists(p => p.Count > 0) ? wordPauses : null,
            readOriginal(lineElement, polyglot),
            wordOriginals.Exists(o => o != null) ? wordOriginals : null,
            estimated,
            sealGraceMs,
            wordSplits.Exists(w => w.Count > 0) ? wordSplits : null,
            wordScores,
            wordOriginalSplits.Exists(o => o.Count > 0) ? wordOriginalSplits : null,
            wordObjects.Exists(o => !o.IsEmpty) ? wordObjects : null,
            pipes);
        return true;
    }

    /// <summary>
    /// The object's optional <c>original</c> string, or null. The base parse reads a non-empty string
    /// only, as it always has; the Polyglot parse also keeps the editor's EMPTY placeholder, which is
    /// the game's rule since PR 43 (TimingJsonLoader.readOriginal).
    /// </summary>
    private static string? readOriginal(JsonElement element, bool polyglot = false)
        => element.TryGetProperty("original", out JsonElement originalElement)
           && originalElement.ValueKind == JsonValueKind.String
           && originalElement.GetString() is string original
           && (polyglot || original.Length > 0)
            ? original
            : null;

    /// <summary>Whether any of the line's words[] is an unromanised word (TimingJsonLoader.carriesUnromanisedWord).</summary>
    private static bool carriesUnromanisedWord(JsonElement lineElement, bool polyglot)
    {
        if (!lineElement.TryGetProperty("words", out JsonElement wordsElement) || wordsElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement word in wordsElement.EnumerateArray())
        {
            if (word.ValueKind == JsonValueKind.Object
                && readOriginal(word, polyglot) != null
                && word.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                && text.GetString()?.Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether word <paramref name="index"/> of <paramref name="line"/> is UNROMANISED (backlog
    /// 330): an EMPTY text beside an original, the shape the game's importer writes for a word its
    /// romaniser could not spell. It has no token in the line text, so it is taken out of the
    /// words[]/token pairing, exactly as TimingJsonLoader.IsUnromanisedWord takes it out.
    /// </summary>
    public static bool IsUnromanisedWord(RawLine line, int index)
        => line.WordOriginals != null && index < line.WordOriginals.Count && line.WordOriginals[index] != null
           && line.Words[index].Text.Length == 0;

    /// <summary>
    /// The unromanised originals one [Lyrics] line carries (see <see cref="Header.Unromanised"/>):
    /// each word with an empty text and an original, and, for a line with an original but nothing
    /// to type, the line's original when no word of it already said so.
    ///
    /// <para>THE ONE PLACE THE TWO PARSERS KNOWINGLY DIFFER. The game keeps such a no-cell line
    /// (so its editor can romanise it) where this parse drops it, as it drops every line with no
    /// cell. A map carrying one can never be stored here (<see cref="PackageValidator"/> refuses
    /// it, and the game refuses to submit it), so no rating is ever computed on the difference.</para>
    /// </summary>
    public static IEnumerable<string> UnromanisedOf(JsonElement lineElement, bool stripBackingVocals = false)
    {
        if (lineElement.ValueKind != JsonValueKind.Object
            || !lineElement.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        int found = 0;

        if (lineElement.TryGetProperty("words", out JsonElement wordsElement) && wordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement word in wordsElement.EnumerateArray())
            {
                if (word.ValueKind == JsonValueKind.Object
                    && readOriginal(word) is string original
                    && word.TryGetProperty("text", out JsonElement wordText) && wordText.ValueKind == JsonValueKind.String
                    && wordText.GetString()?.Length == 0)
                {
                    found++;
                    yield return original;
                }
            }
        }

        string raw = textElement.GetString() ?? string.Empty;
        bool freestyle = lineElement.TryGetProperty("freestyle", out JsonElement f) && f.ValueKind == JsonValueKind.True;
        string normalized = Typeability.Normalize(stripBackingVocals ? Typeability.StripBackingVocals(raw) : raw, keepFreestyleMarkers: freestyle);

        if (found == 0 && Typeability.ToDefaultStream(normalized).Length == 0 && readOriginal(lineElement) is string lineOriginal)
            yield return lineOriginal;
    }

    /// <summary>
    /// Resolves per-line End/SingEnd and word units for an ordered set of raw lines: a non-last
    /// line's hard seal is the next line's start; the last line gets vocal end + 3 s tail capped
    /// at song_end_ms. (TimingJsonLoader.BuildLines, TimingJsonLoader.cs:191-255.)
    /// </summary>
    public static IReadOnlyList<LyricLine> BuildLines(IReadOnlyList<RawLine> raw, double? songEndMs, bool polyglot = false)
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

            // TimingJsonLoader.BuildLines' seal grace: an explicit one wins, else the raw overrun.
            double rawWordsEnd = start;

            foreach (var w in line.Words)
                rawWordsEnd = Math.Max(rawWordsEnd, Math.Max(w.Start, w.End));

            double sealGrace = line.SealGraceMs is double explicitGrace
                ? Math.Clamp(explicitGrace, 0, MAX_SEAL_GRACE_MS)
                : Math.Min(Math.Max(0, rawWordsEnd - end), MAX_SEAL_GRACE_MS);

            string[] tokens = line.Text.Split(' ');
            IReadOnlyList<TimedUnit> units;

            // UNROMANISED WORDS (backlog 330) have no token, so they are taken out of the pairing,
            // exactly as TimingJsonLoader.pairedWords takes them out. A line without one pairs
            // exactly as it always has.
            var unromanised = new List<(int Before, int Index)>();
            var paired = pairedWords(line, unromanised);

            if (tokens.Length == paired.Words.Count && tokens.Length > 0)
            {
                units = buildExplicitUnits(tokens, paired.Words, paired.WordPauses, start, end, paired.WordOriginals,
                    paired.WordSplits, paired.WordScores, polyglot, paired.WordObjects, line.Pipes);

                if (polyglot && line.WordOriginalSplits != null)
                {
                    var links = line.WordOriginalSplits.Where((_, index) => !IsUnromanisedWord(line, index)).ToArray();
                    units = units.Select((unit, index) => index < links.Length
                        ? OriginalLyricSubdivisions.WithExplicit(unit, links[index]) : unit).ToArray();
                }
            }
            else
                units = withSplitMarkers(withDerivedOriginals(InterpolateUnits(line.Text, start, singEnd), line.Original), line.Pipes);

            result.Add(new LyricLine
            {
                RawText = line.Text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
                Original = line.Original,
                SealGraceMs = sealGrace,
                Estimated = line.Estimated,
                UnromanisedWords = !polyglot || unromanised.Count == 0
                    ? Array.Empty<UnromanisedWord>()
                    : unromanised.Select(u => new UnromanisedWord(
                        Math.Min(u.Before, units.Count),
                        line.WordOriginals![u.Index]!,
                        Math.Clamp(line.Words[u.Index].Start, start, end),
                        Math.Clamp(line.Words[u.Index].End, Math.Clamp(line.Words[u.Index].Start, start, end), end))).ToArray(),
            });
        }

        return result;
    }

    /// <summary>The interpolated units with the syllables their pipes author (game's <c>LrcParser.InterpolateUnits</c>).</summary>
    private static IReadOnlyList<TimedUnit> withSplitMarkers(IReadOnlyList<TimedUnit> units, IReadOnlyList<IReadOnlyList<int>>? pipes)
    {
        if (pipes == null)
            return units;

        return units.Select((u, i) => i < pipes.Count && SplitMarkers.Authored(u.Text, u.StartTime, u.EndTime, pipes[i]) is { } played
            ? u.WithSplitMarkerSyllables(played)
            : u).ToArray();
    }

    /// <summary>
    /// Interpolated units given their originals from the LINE's original, when the line has no
    /// words[] to carry them (a line-granularity map): the original's whitespace tokens pair with the
    /// units one for one when, and only when, the two counts agree (TimingJsonLoader.withDerivedOriginals).
    /// </summary>
    private static IReadOnlyList<TimedUnit> withDerivedOriginals(IReadOnlyList<TimedUnit> units, string? lineOriginal)
    {
        if (lineOriginal == null || units.Count == 0)
            return units;

        string[] originals = lineOriginal.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (originals.Length != units.Count)
            return units;

        return units.Select((u, i) => carriesOriginal(originals[i]) && originals[i] != u.Text
            ? new TimedUnit
            {
                Text = u.Text,
                StartTime = u.StartTime,
                EndTime = u.EndTime,
                SyllableBoundaries = u.SyllableBoundaries,
                SyllableSplits = u.SyllableSplits,
                Pauses = u.Pauses,
                Source = u.Source,
                Confidence = u.Confidence,
                Objects = u.Objects,
                Original = originals[i],
            }
            : u).ToArray();
    }

    /// <summary>
    /// Whether a source word records an original at all: it carries a letter or a combining mark
    /// outside ASCII (the game's <c>LyricOriginals.CarriesOriginal</c>, which decides the same thing
    /// for the importer).
    /// </summary>
    private static bool carriesOriginal(string source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] <= 0x7F)
                continue;

            switch (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(source, i))
            {
                case System.Globalization.UnicodeCategory.UppercaseLetter:
                case System.Globalization.UnicodeCategory.LowercaseLetter:
                case System.Globalization.UnicodeCategory.TitlecaseLetter:
                case System.Globalization.UnicodeCategory.ModifierLetter:
                case System.Globalization.UnicodeCategory.OtherLetter:
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                case System.Globalization.UnicodeCategory.SpacingCombiningMark:
                case System.Globalization.UnicodeCategory.EnclosingMark:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The line's words[] with its unromanised words taken out, the parallel lists filtered in step
    /// (TimingJsonLoader.pairedWords). A line with none comes back with its own lists.
    /// </summary>
    private static (List<(string Text, double Start, double End, double[] Syllables)> Words,
        List<List<(double Start, double End, int Split)>>? WordPauses, List<string?>? WordOriginals,
        List<List<int>>? WordSplits, List<double>? WordScores, List<WordObjects>? WordObjects) pairedWords(RawLine line, List<(int Before, int Index)> unromanised)
    {
        bool any = false;

        for (int i = 0; i < line.Words.Count && !any; i++)
            any = IsUnromanisedWord(line, i);

        if (!any)
            return (line.Words, line.WordPauses, line.WordOriginals, line.WordSplits, line.WordScores, line.WordObjects);

        var words = new List<(string Text, double Start, double End, double[] Syllables)>();
        var pauses = line.WordPauses == null ? null : new List<List<(double Start, double End, int Split)>>();
        var originals = new List<string?>();
        var splits = line.WordSplits == null ? null : new List<List<int>>();
        var scores = line.WordScores == null ? null : new List<double>();
        var objects = line.WordObjects == null ? null : new List<WordObjects>();

        for (int i = 0; i < line.Words.Count; i++)
        {
            if (IsUnromanisedWord(line, i))
            {
                unromanised.Add((words.Count, i));
                continue;
            }

            words.Add(line.Words[i]);
            pauses?.Add(i < line.WordPauses!.Count ? line.WordPauses[i] : new List<(double, double, int)>());
            originals.Add(line.WordOriginals![i]);
            splits?.Add(i < line.WordSplits!.Count ? line.WordSplits[i] : new List<int>());
            scores?.Add(i < line.WordScores!.Count ? line.WordScores[i] : 1);
            objects?.Add(i < line.WordObjects!.Count ? line.WordObjects[i] : WordObjects.None);
        }

        return (words, pauses, originals, splits, scores, objects);
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
        List<(string Text, double Start, double End, double[] Syllables)> words,
        List<List<(double Start, double End, int Split)>>? wordPauses,
        double lineStart,
        double lineEnd,
        List<string?>? wordOriginals = null,
        List<List<int>>? wordSplits = null,
        List<double>? wordScores = null,
        bool polyglot = false,
        List<WordObjects>? wordObjects = null,
        IReadOnlyList<IReadOnlyList<int>>? pipes = null)
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

            // THE AUTHORED PAUSES, kept only when they survived the same clamping the syllables
            // did and are real rests inside this token: both edges strictly inside the clamped
            // word, start before end, and the split a character index that leaves typeable cells on
            // both sides. Which of a word's rests survive is the DERIVATION's own answer
            // (PausedWord.UsableRests, as TimingJsonLoader.cs asks it), so a map never loads a rest
            // the play would ignore: the per-rest terms above, and then none overlapping an earlier
            // one, no two sharing a character, and every later rest on a later character than the
            // one before it. Anything else is dropped rather than guessed at.
            var rawPauses = wordPauses != null && m < wordPauses.Count ? wordPauses[m] : null;
            var keptPauses = PausedWord.UsableRests(
                tokens[m], ws, we,
                rawPauses?.Select(pause => new WordPause(pause.Start, pause.End, pause.Split)) ?? Enumerable.Empty<WordPause>());

            // The authored split (split_chars), kept only when nothing was clamped away and the
            // indices are a valid split of this token, exactly as TimingJsonLoader keeps it.
            var boundaries = syllableBoundaries(words[m].Syllables, ws, we);
            var splits = wordSplits != null && m < wordSplits.Count ? wordSplits[m] : null;
            int[] keptSplits = splits != null
                               && boundaries.Count == words[m].Syllables.Length
                               && SyllableSegments.IsAuthoredValid(tokens[m], boundaries.Count + 1, splits)
                ? splits.ToArray()
                : Array.Empty<int>();

            // The word's pipes (TimingJsonLoader.buildExplicitUnits): a valid cut keeps the aligner's
            // times when the counts match, else divides the word evenly. Never replaces the rated syllables.
            (IReadOnlyList<double> Boundaries, IReadOnlyList<int> Splits)? splitMarkerSyllables = null;
            var wordPipes = pipes != null && m < pipes.Count ? pipes[m] : Array.Empty<int>();

            if (wordPipes.Count > 0 && SyllableSegments.IsAuthoredValid(tokens[m], wordPipes.Count + 1, wordPipes))
            {
                if (wordPipes.Count == boundaries.Count)
                    splitMarkerSyllables = (boundaries, wordPipes.ToArray());
                else
                    splitMarkerSyllables = SplitMarkers.Authored(tokens[m], ws, we, wordPipes);
            }

            units.Add(new TimedUnit
            {
                Text = tokens[m],
                StartTime = ws,
                EndTime = we,
                Source = TimingSource.Explicit,
                Confidence = Math.Clamp(wordScores != null && m < wordScores.Count ? wordScores[m] : 1, 0, 1),
                SyllableSplits = keptSplits,
                // "Keep only subdivisions that stayed strictly inside the (possibly clamped) word",
                // distinct and ordered, exactly as TimingJsonLoader.cs:406 filters them. A boundary
                // ON either edge would author an empty syllable segment, which is why the
                // comparisons are strict on both sides.
                SyllableBoundaries = boundaries,
                Pauses = keptPauses.Count == 0 ? Array.Empty<WordPause>() : keptPauses,
                // Preserved, never rated by the base figures (backlog 330). The Polyglot parse keeps it
                // exactly as written, the game's rule since PR 43; the base parse keeps dropping an
                // original equal to its token, so lyrics_original and the base figures are untouched.
                Original = wordOriginals != null && m < wordOriginals.Count && (polyglot || wordOriginals[m] != tokens[m]) ? wordOriginals[m] : null,
                Objects = wordObjects != null && m < wordObjects.Count ? wordObjects[m] : WordObjects.None,
                SplitMarkerSyllables = splitMarkerSyllables,
            });

            prevEnd = we;
        }

        return units;
    }

    /// <summary>
    /// The wire's syllable starts reduced to a word's own boundaries:
    /// <c>Where(b => b &gt; start &amp;&amp; b &lt; end).Distinct().OrderBy(b =&gt; b)</c>, which is
    /// TimingJsonLoader.cs:406 verbatim. The order of the three steps is the game's and is
    /// load bearing for the DISTINCT one: two equal boundaries would author a zero-width syllable,
    /// and the game drops the duplicate rather than the pair.
    /// </summary>
    private static IReadOnlyList<double> syllableBoundaries(double[] syllables, double start, double end)
    {
        if (syllables.Length == 0)
            return Array.Empty<double>();

        var kept = new List<double>(syllables.Length);

        foreach (double at in syllables)
        {
            if (at > start && at < end && !kept.Contains(at))
                kept.Add(at);
        }

        if (kept.Count == 0)
            return Array.Empty<double>();

        kept.Sort();
        return kept;
    }

    // TimingJsonLoader.tryGetInt: a JSON number that is a whole value, 2.0 as well as 2.
    private static bool tryGetInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind != JsonValueKind.Number)
            return false;

        if (element.TryGetInt32(out value))
            return true;

        // JSON doesn't distinguish 2 from 2.0; accept whole-number float tokens too.
        if (element.TryGetDouble(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }

        return false;
    }

    private static bool tryGetDouble(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out value);

        return false;
    }
}
