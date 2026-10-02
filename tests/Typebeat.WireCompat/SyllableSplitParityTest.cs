using System.Globalization;
using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the AUTHORED syllable split (backlog 181): the word-level
/// <c>split_chars</c> array that lets a mapper cut "beauti|ful" where the syllabifier would have
/// cut "beau|tiful". The desktop editor writes it; the browser only ever READS it, and has to read
/// it identically, because the split drives the per-char TARGET times and the judgement GROUPS at
/// once and the two clients submit to the same leaderboards.
///
/// <para>Every fixture here is a real .osu, and most of them come out of the GAME'S OWN
/// <see cref="TypeBeatBeatmapEncoder"/> rather than being hand-typed, so the bytes the browser
/// parses are the bytes the editor writes. The C# arm then decodes each one through the production
/// <see cref="LyricBeatmapDecoder"/> and builds its <see cref="TypingLine"/>s; the browser arm does
/// the same through <c>parseLyricOsu</c> and <c>buildBeatmap</c> in
/// <c>CoreSplitCharsHarness.cjs</c>; and the two are held against each other cell by cell, group by
/// group and membership by membership. The hand-typed cases are the ones the encoder cannot
/// produce, because they are MALFORMED on purpose.</para>
///
/// <para>Every case carries TWO variants of the same map, and the pins run along both axes:</para>
/// <list type="number">
/// <item>The two LOADERS agree, on both variants of every case. That is the fidelity pin.</item>
/// <item>The two loaders agree on how much the authored split MOVED, which is the coverage counter:
/// a case that stopped exercising the authored arm would go quietly green otherwise. The cases that
/// must move assert a non-zero count, and, just as importantly, the cases that must NOT move (an
/// invalid split, a stale one, a split equal to the derived cut, a cosmetic text change) assert
/// exactly zero, which is the fallback pin and the reason the feature cannot retime an existing
/// map.</item>
/// </list>
/// </summary>
[TestFixture]
public class SyllableSplitParityTest
{
    #region The fixtures

    /// <summary>
    /// One case: the same map twice. <paramref name="Authored"/> is the map as written, and
    /// <paramref name="Comparison"/> is what it must (or must not) differ from, in almost every
    /// case the identical map with <c>split_chars</c> taken away.
    /// </summary>
    private sealed record Case(string Name, bool Literate, string Authored, string Comparison, bool MustMoveTargets, bool MustMoveGroups);

    /// <summary>
    /// "beautiful" over [1000, 1900] with one subdivision at 1450, plus the authored split.
    /// The demonstrator: the syllabifier's own answer for a two-segment "beautiful" IS [4], so an
    /// authored [4] leaves the GROUPS alone. Before PR 5 it moved the targets, which was the gap
    /// the feature existed to close (the even spread timed the 't' before the boundary it is
    /// grouped after); since PR 5 the live targets follow the derived cut too
    /// (<see cref="TypingEngine.AlignSubdivisionTargets"/>), so an authored [4] equals the derived
    /// reading outright and moves nothing. An authored [6] moves both.
    /// </summary>
    private static string Beautiful(int[]? splits)
        => Encoded(Line("beautiful", 1000, 2500, 1900, Unit("beautiful", 1000, 1900, splits, 1450)));

    /// <summary>"banana" over [1000, 1600] with two subdivisions; derived cuts ba|na|na ([2, 4]).</summary>
    private static string Banana(int[]? splits)
        => Encoded(Line("banana", 1000, 2000, 1600, Unit("banana", 1000, 1600, splits, 1200, 1400)));

    /// <summary>
    /// The clamp case. Word 2 starts BEFORE word 1 ends, so the loader pushes its start to 1600 and
    /// its 1250 subdivision dies with it. The surviving boundary count then matches the authored
    /// split's length, which is precisely the trap: pairing the one survivor with a split written
    /// for two would re-cut the word. The split must be discarded outright.
    /// </summary>
    private static string ClampedPair(int[]? splits)
        => Encoded(Line("banana orange", 1000, 2400, 1900,
            Unit("banana", 1000, 1600, null, 1300),
            Unit("orange", 1200, 1800, splits, 1250, 1700)));

    /// <summary>"singin'" over [1000, 1800], one subdivision. Derived cuts sin|gin' ([3]); [4] is sing|in'.</summary>
    private static string Punctuated(int[]? splits)
        => Encoded(Line("singin'", 1000, 2200, 1800, Unit("singin'", 1000, 1800, splits, 1350)));

    /// <summary>
    /// The hand-typed half. <paramref name="splitCharsField"/> is spliced in verbatim (including
    /// the leading comma, or empty for none) so the case can carry JSON the encoder would never
    /// write: a string where an array belongs, a fractional index, a non-numeric element.
    /// </summary>
    private static string RawBeautiful(string syllableTexts, string splitCharsField)
        => HandAuthored(
            "{\"text\":\"beautiful\",\"start_ms\":1000,\"end_ms\":1900,\"words\":[{\"text\":\"beautiful\","
            + "\"start_ms\":1000,\"end_ms\":1900,\"score\":1,\"syllables\":" + syllableTexts + splitCharsField + "}]}",
            2500);

    /// <summary>Two subdivisions on "tonight", whose derived three-segment cut is to|ni|ght ([2, 4]).</summary>
    private static string RawTonight(string splitCharsField)
        => HandAuthored(
            "{\"text\":\"tonight\",\"start_ms\":1000,\"end_ms\":1900,\"words\":[{\"text\":\"tonight\","
            + "\"start_ms\":1000,\"end_ms\":1900,\"score\":1,\"syllables\":[{\"start_ms\":1000},{\"start_ms\":1300},{\"start_ms\":1600}]"
            + splitCharsField + "}]}",
            2500);

    private const string honest_texts = "[{\"text\":\"beau\",\"start_ms\":1000},{\"text\":\"tiful\",\"start_ms\":1450}]";

    /// <summary>Cosmetic syllable texts that do NOT concatenate back to the word, and are never read.</summary>
    private const string lying_texts = "[{\"text\":\"XX\",\"start_ms\":1000},{\"text\":\"zzzzzzz\",\"start_ms\":1450}]";

    private static Case[] BuildCases() =>
    [
        // The demonstrator, both halves of it. The first moved the targets alone until PR 5 aligned
        // the derived arm's targets with its groups; authored [4] IS the derived cut, so it is now
        // a no-op like bananaEvenCutIsANoOp (the name is kept so the history reads).
        new Case("beautifulSameGroupsMovedTargets", false, Beautiful([4]), Beautiful(null), false, false),
        new Case("beautifulMovedBoth", false, Beautiful([6]), Beautiful(null), true, true),

        // Two subdivisions: ban|a|na against the derived ba|na|na.
        new Case("bananaMovedBoth", false, Banana([3, 4]), Banana(null), true, true),

        // Authoring the cut the syllabifier would have picked anyway is a no-op, which is what lets
        // the editor canonicalise such a split away without moving the map.
        new Case("bananaEvenCutIsANoOp", false, Banana([2, 4]), Banana(null), false, false),

        // The validation matrix, every one of which must fall back to derived rather than throw.
        new Case("invalidTooFew", false, Banana([3]), Banana(null), false, false),
        new Case("invalidTooMany", false, Banana([1, 3, 4]), Banana(null), false, false),
        new Case("invalidNotAscending", false, Banana([4, 2]), Banana(null), false, false),
        new Case("invalidDuplicate", false, Banana([2, 2]), Banana(null), false, false),
        new Case("invalidEmptiesFirstSegment", false, Banana([0, 4]), Banana(null), false, false),
        new Case("invalidEmptiesLastSegment", false, Banana([2, 6]), Banana(null), false, false),
        new Case("invalidOutOfRange", false, Banana([2, 99]), Banana(null), false, false),
        new Case("invalidNegative", false, Banana([-1, 4]), Banana(null), false, false),

        // The clamp that drops a boundary discards the split even though the count still fits.
        new Case("clampDroppedABoundary", false, ClampedPair([3]), ClampedPair(null), false, false),

        // Punctuation: the split indexes the TOKEN, so the apostrophe has to ride inside a segment
        // the cell cut never counted. Asserted under both streams, because Literate turns the
        // apostrophe into a cell of its own and it must still land in the right group.
        new Case("punctuatedDefaultStream", false, Punctuated([4]), Punctuated(null), true, true),
        new Case("punctuatedLiterate", true, Punctuated([4]), Punctuated(null), true, true),

        // The cosmetic syllable texts are NEVER read: two maps differing only in them are the same
        // map. This is what keeps every pre-181 subtimed map splitting as it always did, since their
        // texts are the encoder's even halves and honouring them would re-cut all of them.
        new Case("cosmeticTextsAreNotTheSplit", false, RawBeautiful(lying_texts, ""), RawBeautiful(honest_texts, ""), false, false),
        new Case("splitCharsWinsOverCosmeticTexts", false, RawBeautiful(lying_texts, ",\"split_chars\":[6]"), RawBeautiful(lying_texts, ""), true, true),

        // Malformed JSON in the field itself: ignored, never fatal.
        new Case("malformedNotAnArray", false, RawBeautiful(honest_texts, ",\"split_chars\":\"nope\""), RawBeautiful(honest_texts, ""), false, false),
        new Case("malformedStringElement", false, RawBeautiful(honest_texts, ",\"split_chars\":[\"a\"]"), RawBeautiful(honest_texts, ""), false, false),
        new Case("malformedEmptyArray", false, RawBeautiful(honest_texts, ",\"split_chars\":[]"), RawBeautiful(honest_texts, ""), false, false),
        new Case("malformedNull", false, RawBeautiful(honest_texts, ",\"split_chars\":null"), RawBeautiful(honest_texts, ""), false, false),

        // JSON does not distinguish 6 from 6.0, and the reader accepts a whole-number float as the
        // index it plainly is; a fractional one is not an index and is dropped.
        new Case("wholeNumberFloatIsTheSameSplit", false, RawBeautiful(honest_texts, ",\"split_chars\":[6.0]"), RawBeautiful(honest_texts, ",\"split_chars\":[6]"), false, false),
        new Case("fractionalIndexFallsBack", false, RawTonight(",\"split_chars\":[2.5,5]"), RawTonight(""), false, false),

        // A non-numeric element is SKIPPED rather than aborting the array, so the survivors can
        // still form a valid split. Pinned as equal to the map that spells those survivors out,
        // because whatever the rule is, the two clients have to apply it the same way.
        new Case("nonNumericElementIsSkipped", false, RawTonight(",\"split_chars\":[2,\"x\",5]"), RawTonight(",\"split_chars\":[2,5]"), false, false),
        new Case("tonightAuthoredMovesBoth", false, RawTonight(",\"split_chars\":[2,5]"), RawTonight(""), true, true),

        // ---- AUTHORED PAUSES (PR 2, the Map Editor's Insert Pause) --------------------------------
        //
        // A rest inside a word is a subdivision with no characters in it: it cuts the word into
        // stretches timed in their own right and gives the characters past it their own judgement
        // span. Pinned here because it rides the same two axes an authored split does (targets and
        // groups), through the same production encoder and both production loaders.

        // One rest, written by the encoder, cutting "tonight" at "toni|ght" where the syllabifier
        // would say "to|night", so both the ramp and the membership move.
        new Case("pauseMovesBoth", false, PausedTonight(new WordPause(1300, 1500, 4)), PausedTonight(), true, true),

        // Several rests on one word beside authored boundaries and an authored split, plus a second
        // word with a rest of its own: the stretch routing, the per-stretch clamp of the authored
        // cut ("fo|re" and "v|e") and the run spans all at once.
        new Case("severalPausesBesideAuthoredSplits", false, PausedForever(true), PausedForever(false), true, true),

        // A boundary that falls INSIDE a rest is routed to the nearer stretch at its relative
        // position, which moves it: the one arm of the routing nothing above reaches.
        new Case("boundaryInsideARestIsRerouted", false, PausedBreathe(true), PausedBreathe(false), true, true),

        // The legacy single `pause` object reads exactly as the one-element array a newer build
        // writes, and an array that is not an array falls back to it exactly as the C# does.
        new Case("legacySinglePauseIsTheArray", false, RawBreathe(",\"pause\":" + breathe_rest), RawBreathe(",\"pauses\":[" + breathe_rest + "]"), false, false),
        new Case("pausesNotAnArrayFallsBackToPause", false, RawBreathe(",\"pauses\":\"x\",\"pause\":" + breathe_rest), RawBreathe(",\"pauses\":[" + breathe_rest + "]"), false, false),
        new Case("rawPauseMovesBoth", false, RawBreathe(",\"pauses\":[" + breathe_rest + "]"), RawBreathe(""), true, true),

        // Every rest PausedWord.UsableRests refuses: an edge on or outside the word, an inverted or
        // empty rest, a split leaving every cell on one side, a rest overlapping the one before it and
        // one on an earlier character than the one before it. The survivors must read exactly as the
        // map that spells only them out, and a word with none must read as if it had none.
        new Case("unusableRestsAreIgnored", false,
            RawBreathe(",\"pauses\":[{\"start_ms\":1000,\"end_ms\":1200,\"split\":3},{\"start_ms\":1800,\"end_ms\":2000,\"split\":3},"
                       + "{\"start_ms\":1500,\"end_ms\":1500,\"split\":3},{\"start_ms\":1300,\"end_ms\":1200,\"split\":3},"
                       + "{\"start_ms\":1300,\"end_ms\":1400,\"split\":0},{\"start_ms\":1300,\"end_ms\":1400,\"split\":7}]"),
            RawBreathe(""), false, false),
        new Case("overlappingAndBackwardRestsAreDropped", false,
            RawBreathe(",\"pauses\":[{\"start_ms\":1300,\"end_ms\":1500,\"split\":3},{\"start_ms\":1400,\"end_ms\":1600,\"split\":5},{\"start_ms\":1700,\"end_ms\":1800,\"split\":2}]"),
            RawBreathe(",\"pauses\":[{\"start_ms\":1300,\"end_ms\":1500,\"split\":3}]"), false, false),

        // Malformed fields: a time that is not a JSON number and a fractional split are skipped; a
        // whole-number float split is the index it plainly is.
        new Case("malformedPauseFieldsAreSkipped", false,
            RawBreathe(",\"pauses\":[{\"start_ms\":\"1400\",\"end_ms\":1600,\"split\":3},{\"start_ms\":1400,\"end_ms\":1600,\"split\":3.5},{\"start_ms\":1400,\"end_ms\":1600}]"),
            RawBreathe(""), false, false),
        new Case("wholeNumberFloatSplitIsTheSameRest", false,
            RawBreathe(",\"pauses\":[{\"start_ms\":1400,\"end_ms\":1600,\"split\":3.0}]"), RawBreathe(",\"pauses\":[" + breathe_rest + "]"), false, false),
    ];

    /// <summary>A word unit carrying authored pauses (and optionally boundaries and an authored split).</summary>
    private static TimedUnit PausedUnit(string text, double start, double end, int[]? splits, WordPause[] pauses, params double[] boundaries)
        => new TimedUnit
        {
            Text = text,
            StartTime = start,
            EndTime = end,
            Source = TimingSource.Explicit,
            SyllableBoundaries = boundaries,
            SyllableSplits = splits ?? [],
            Pauses = pauses,
        };

    /// <summary>"tonight" over [1000, 1900], with whatever rests are given.</summary>
    private static string PausedTonight(params WordPause[] pauses)
        => Encoded(Line("tonight", 1000, 2500, 1900, PausedUnit("tonight", 1000, 1900, null, pauses)));

    /// <summary>
    /// "forever young": "forever" over [1000, 2600] with boundaries 1500 and 2150, authored "fo|re|ver"
    /// and two rests, [1650, 1850] after "fore" and [2250, 2350] after "foreve"; "young" over
    /// [2600, 4000] with one rest after "yo". Stretch 0 "fore" keeps the 1500 boundary and the "fo|re"
    /// cut, stretch 1 "ve" keeps 2150 with the authored 4 clamped to 5, stretch 2 is "r".
    /// </summary>
    private static string PausedForever(bool withPauses)
        => Encoded(Line("forever young", 1000, 5000, 4000,
            PausedUnit("forever", 1000, 2600, [2, 4], withPauses ? [new WordPause(1650, 1850, 4), new WordPause(2250, 2350, 6)] : [], 1500, 2150),
            PausedUnit("young", 2600, 4000, null, withPauses ? [new WordPause(3000, 3300, 2)] : [])));

    /// <summary>"breathe" over [1000, 2000] with a boundary at 1450, which a [1400, 1600] rest swallows.</summary>
    private static string PausedBreathe(bool withPause)
        => Encoded(Line("breathe", 1000, 2500, 2000,
            PausedUnit("breathe", 1000, 2000, null, withPause ? [new WordPause(1400, 1600, 3)] : [], 1450)));

    /// <summary>The one usable rest the hand-typed "breathe" cases are written against: "bre" | "athe".</summary>
    private const string breathe_rest = "{\"start_ms\":1400,\"end_ms\":1600,\"split\":3}";

    /// <summary>"breathe" over [1000, 2000] with no boundaries, and the word's pause fields spliced in verbatim.</summary>
    private static string RawBreathe(string pauseFields)
        => HandAuthored(
            "{\"text\":\"breathe\",\"start_ms\":1000,\"end_ms\":2000,\"words\":[{\"text\":\"breathe\","
            + "\"start_ms\":1000,\"end_ms\":2000,\"score\":1" + pauseFields + "}]}",
            2500);

    /// <summary>
    /// Direct probes on the shared derivation itself. The readings above prove the whole pipeline
    /// agrees; these say WHICH part moved when it does not.
    /// </summary>
    private static (string Token, int Segments, int[] Authored)[] SegmentProbes() =>
    [
        ("apple", 2, [2]),
        ("apple", 2, [3]),
        ("apple", 2, []),
        ("apple", 2, [2, 3]),
        ("apple", 3, [2]),
        ("apple", 2, [0]),
        ("apple", 2, [5]),
        ("apple", 2, [6]),
        ("apple", 2, [-1]),
        ("banana", 3, [4, 2]),
        ("banana", 3, [2, 2]),
        ("banana", 3, [2, 4]),
        ("banana", 3, [3, 4]),
        ("", 2, [1]),
        ("ab", 1, [1]),
        ("beautiful", 2, [6]),
        ("don't", 2, [3]),
        ("singin'", 2, [4]),
        ("a-b", 3, [1, 2]),
        ("b2b", 2, [1]),
        ("tonight", 3, [2, 5]),
    ];

    #endregion

    #region Driving the two sides

    private static readonly Case[] all_cases = BuildCases();

    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        var payload = new StringBuilder();
        payload.Append("{\"cases\":[");

        for (int i = 0; i < all_cases.Length; i++)
        {
            var one = all_cases[i];

            if (i > 0)
                payload.Append(',');

            payload.Append('{');
            payload.Append("\"name\":").Append(JsonSerializer.Serialize(one.Name)).Append(',');
            payload.Append("\"literate\":").Append(one.Literate ? "true" : "false").Append(',');
            payload.Append("\"a\":").Append(JsonSerializer.Serialize(one.Authored)).Append(',');
            payload.Append("\"b\":").Append(JsonSerializer.Serialize(one.Comparison));
            payload.Append('}');
        }

        payload.Append("],\"words\":[");

        var probes = SegmentProbes();

        for (int i = 0; i < probes.Length; i++)
        {
            if (i > 0)
                payload.Append(',');

            payload.Append("{\"token\":").Append(JsonSerializer.Serialize(probes[i].Token));
            payload.Append(",\"segments\":").Append(probes[i].Segments.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"authored\":[").Append(string.Join(",", probes[i].Authored)).Append("]}");
        }

        payload.Append("]}");

        // The harness takes its fixtures from here rather than carrying its own, so the two sides
        // cannot drift onto separately maintained copies of the same map.
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-splitchars-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, payload.ToString(), new UTF8Encoding(false));

        try
        {
            return NodeHarness.Run("CoreSplitCharsHarness.cjs", path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a fidelity test over.
            }
        }
    });

    private static TimedUnit Unit(string text, double start, double end, int[]? splits, params double[] boundaries)
        => new TimedUnit
        {
            Text = text,
            StartTime = start,
            EndTime = end,
            Source = TimingSource.Explicit,
            SyllableBoundaries = boundaries,
            SyllableSplits = splits ?? [],
        };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>The .osu the editor's encoder writes for this map, which is what the browser parses.</summary>
    private static string Encoded(params LyricLine[] lines)
    {
        var beatmap = new Beatmap();
        beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        beatmap.Metadata.Artist = "a";
        beatmap.Metadata.Title = "t";
        beatmap.Metadata.AudioFile = "a.mp3";

        for (int i = 0; i < lines.Length; i++)
        {
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = lines[i].StartTime,
                LineIndex = i,
                Line = lines[i],
                Granularity = TimingGranularity.Syllable,
            });
        }

        var sb = new StringBuilder();

        using (var writer = new StringWriter(sb))
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

        return sb.ToString();
    }

    /// <summary>
    /// A .osu around HAND-TYPED line JSON, for the cases the encoder cannot produce because they
    /// are malformed. The surrounding file is still written by the production
    /// <see cref="LyricOsuFormat"/>, so only the part under test is hand-made.
    /// </summary>
    private static string HandAuthored(string linesJson, double songEndMs)
        => LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c",
            $"{{\"version\":2,\"song_end_ms\":{songEndMs.ToString(CultureInfo.InvariantCulture)},\"lines\":[{linesJson}]}}");

    /// <summary>
    /// One decoded map as the game reads it: the granularity plus a built line per lyric line. The
    /// granularity is METADATA now (it stopped selecting a judgement ladder when the three tiers
    /// became one symmetric set of windows), and it is still held against the browser's reading
    /// because the decoder still stamps it on every hit object and a one-sided parse would drift.
    /// </summary>
    private static (TimingGranularity Granularity, TypingLine[] Lines) Decode(string osu, bool literate)
    {
        var hitObjects = DecodeObjects(osu);
        var granularity = hitObjects.Count > 0 ? hitObjects[0].Granularity : TimingGranularity.Line;

        return (granularity, hitObjects.Select(h => TypingLine.FromLyricLine(h.Line, literate, alignSubdivisionTargets: true)).ToArray());
    }

    /// <summary>The map as the production decoder reads it, units and all.</summary>
    private static List<TypeBeatHitObject> DecodeObjects(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

        return decoded.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();
    }

    /// <summary>
    /// Cells whose target time, and cells whose syllable membership, differ between two readings of
    /// the same map. The mirror of the harness's own <c>movement</c>, and the coverage counter.
    /// </summary>
    private static (int Targets, int Membership) Movement(TypingLine[] a, TypingLine[] b)
    {
        if (a.Length != b.Length)
            return (-1, -1);

        int targets = 0;
        int membership = 0;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Cells.Count != b[i].Cells.Count)
                return (-1, -1);

            for (int c = 0; c < a[i].Cells.Count; c++)
            {
                if (a[i].Cells[c].TargetTime != b[i].Cells[c].TargetTime)
                    targets++;

                if (a[i].SyllableIndexOf(c) != b[i].SyllableIndexOf(c))
                    membership++;
            }
        }

        return (targets, membership);
    }

    private static JsonElement BrowserCase(string name)
    {
        foreach (var one in browser.Value.GetProperty("cases").EnumerateArray())
        {
            if (one.GetProperty("name").GetString() == name)
                return one;
        }

        throw new AssertionException($"the harness emitted no case named {name}");
    }

    #endregion

    /// <summary>
    /// The two loaders resolve every variant of every case to the same cells, the same syllable
    /// groups and the same per-cell membership. This is the fidelity pin: a browser that read
    /// <c>split_chars</c> differently would judge the same performance differently.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryAuthoredMap()
    {
        var harness = browser.Value;

        Assert.Multiple(() =>
        {
            foreach (var one in all_cases)
            {
                var browserCase = BrowserCase(one.Name);

                foreach (string variant in new[] { "a", "b" })
                {
                    string osu = variant == "a" ? one.Authored : one.Comparison;
                    var (granularity, lines) = Decode(osu, one.Literate);
                    var browserReading = browserCase.GetProperty(variant);

                    string what = $"{one.Name}.{variant}";

                    Assert.That(browserReading.GetProperty("granularity").GetString(), Is.EqualTo(granularity.ToString()), $"{what}: granularity");

                    var browserLines = browserReading.GetProperty("lines");
                    Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Length), $"{what}: line count");

                    for (int i = 0; i < lines.Length && i < browserLines.GetArrayLength(); i++)
                    {
                        var line = lines[i];
                        var browserLine = browserLines[i];

                        Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{what}[{i}]: endTime");
                        Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{what}[{i}]: activationTime");
                        Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{what}[{i}]: sealGraceMs");

                        var browserCells = browserLine.GetProperty("cells");
                        Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{what}[{i}]: cell count");

                        for (int c = 0; c < line.Cells.Count && c < browserCells.GetArrayLength(); c++)
                        {
                            Assert.That(browserCells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"{what}[{i}][{c}]: expected");
                            Assert.That(browserCells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"{what}[{i}][{c}]: target");
                        }

                        // The LIVE grouping (backlog 363), selected explicitly: the browser plays live
                        // only, and the line also carries the stored-era natural one.
                        var grouping = line.AuthoredGrouping;
                        var browserGroups = browserLine.GetProperty("syllables");
                        Assert.That(browserGroups.GetArrayLength(), Is.EqualTo(grouping.Groups.Count), $"{what}[{i}]: syllable count");

                        for (int g = 0; g < grouping.Groups.Count && g < browserGroups.GetArrayLength(); g++)
                        {
                            var group = grouping.Groups[g];
                            var browserGroup = browserGroups[g];

                            Assert.That(browserGroup.GetProperty("startCell").GetInt32(), Is.EqualTo(group.StartCell), $"{what}[{i}] syllable {g}: startCell");
                            Assert.That(browserGroup.GetProperty("endCellExclusive").GetInt32(), Is.EqualTo(group.EndCellExclusive), $"{what}[{i}] syllable {g}: endCellExclusive");
                            Assert.That(browserGroup.GetProperty("startTime").GetDouble(), Is.EqualTo(group.StartTime), $"{what}[{i}] syllable {g}: startTime");
                            Assert.That(browserGroup.GetProperty("endTime").GetDouble(), Is.EqualTo(group.EndTime), $"{what}[{i}] syllable {g}: endTime");
                        }

                        var browserMembership = browserLine.GetProperty("cellSyllable");
                        Assert.That(browserMembership.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{what}[{i}]: cellSyllable length");

                        for (int c = 0; c < line.Cells.Count && c < browserMembership.GetArrayLength(); c++)
                            Assert.That(browserMembership[c].GetInt32(), Is.EqualTo(grouping.IndexOf(c)), $"{what}[{i}][{c}]: syllable membership");
                    }
                }
            }
        });

        Assert.That(harness.GetProperty("cases").GetArrayLength(), Is.EqualTo(all_cases.Length), "the harness read every case");
    }

    /// <summary>
    /// The display's mid-word subdivision marks (backlog 317): the browser's
    /// <c>syllableMarkerCells</c> equals <see cref="TypingLine.SyllableMarkerCells"/> on every
    /// variant of every authored case, so /play marks exactly the splits it judges on. The total is
    /// asserted non-zero so the sweep cannot pass vacuously on fixtures that stopped carrying any
    /// subtimed word. Since PR 3 an automatic (syllabifier) split is marked exactly as an authored
    /// one is, so the non-subtimed words of these fixtures are marked too.
    /// </summary>
    [Test]
    public void TheTwoLoadersPlaceTheSameSyllableMarkers()
    {
        int totalMarkers = 0;

        Assert.Multiple(() =>
        {
            foreach (var one in all_cases)
            {
                var browserCase = BrowserCase(one.Name);

                foreach (string variant in new[] { "a", "b" })
                {
                    var (_, lines) = Decode(variant == "a" ? one.Authored : one.Comparison, one.Literate);
                    var browserLines = browserCase.GetProperty(variant).GetProperty("lines");

                    for (int i = 0; i < lines.Length && i < browserLines.GetArrayLength(); i++)
                    {
                        int[] browserMarkers = browserLines[i].GetProperty("syllableMarkerCells").EnumerateArray().Select(e => e.GetInt32()).ToArray();

                        Assert.That(browserMarkers, Is.EqualTo(lines[i].AuthoredGrouping.MarkerCells.ToArray()), $"{one.Name}.{variant}[{i}]: syllable marker cells");
                        totalMarkers += lines[i].AuthoredGrouping.MarkerCells.Count;
                    }
                }
            }
        });

        Assert.That(totalMarkers, Is.GreaterThan(0), "the authored fixtures exercise at least one syllable marker");
    }

    /// <summary>
    /// COVERAGE, and the fallback pin in one: how far the authored split MOVED the map, counted
    /// identically on both sides.
    ///
    /// <para>The cases that must move assert a non-zero count, so the sweep can never silently stop
    /// exercising the authored arm (an authored case that quietly started deriving would still
    /// agree with a C# arm doing the same, and pass). The cases that must NOT move assert exactly
    /// zero, which is the HARD INVARIANT the whole feature rests on: an invalid, stale or
    /// equal-to-derived split, and a cosmetic text change, leave the map exactly as a pre-181
    /// client read it.</para>
    /// </summary>
    [Test]
    public void TheAuthoredArmMovesExactlyTheCasesItShould()
    {
        int totalMovedTargets = 0;
        int totalMovedGroups = 0;

        Assert.Multiple(() =>
        {
            foreach (var one in all_cases)
            {
                var browserCase = BrowserCase(one.Name);

                var (_, authoredLines) = Decode(one.Authored, one.Literate);
                var (_, comparisonLines) = Decode(one.Comparison, one.Literate);
                var moved = Movement(authoredLines, comparisonLines);

                Assert.That(browserCase.GetProperty("movedTargets").GetInt32(), Is.EqualTo(moved.Targets), $"{one.Name}: moved target count");
                Assert.That(browserCase.GetProperty("movedMembership").GetInt32(), Is.EqualTo(moved.Membership), $"{one.Name}: moved membership count");

                if (one.MustMoveTargets)
                    Assert.That(moved.Targets, Is.GreaterThan(0), $"{one.Name}: expected the authored split to move a target");
                else
                    Assert.That(moved.Targets, Is.Zero, $"{one.Name}: expected the map to read exactly as the derived one");

                if (one.MustMoveGroups)
                    Assert.That(moved.Membership, Is.GreaterThan(0), $"{one.Name}: expected the authored split to move a cell into another group");
                else
                    Assert.That(moved.Membership, Is.Zero, $"{one.Name}: expected the grouping to read exactly as the derived one");

                totalMovedTargets += Math.Max(0, moved.Targets);
                totalMovedGroups += Math.Max(0, moved.Membership);
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(totalMovedTargets, Is.GreaterThan(0), "no case anywhere exercised the authored target spread");
            Assert.That(totalMovedGroups, Is.GreaterThan(0), "no case anywhere exercised the authored group fill");
        });
    }

    /// <summary>
    /// The property the whole feature exists for, asserted on the BROWSER'S OWN readings: on a word
    /// whose authored split SURVIVED validation, every cell's target time lies inside the span of
    /// the group that judges it, because the one cut fed both. Before PR 5 the derived arm promised
    /// no such thing (its index-even spread timed the 't' of a derived "beau|tiful" before the
    /// boundary it is grouped after); since PR 5 the live targets follow the derived cut as well,
    /// but this pin still reads only the authored maps (a map whose split the loader rejected is
    /// skipped), so it stays the browser holding its two readings against EACH OTHER rather than
    /// against the C# arm: a port that honoured split_chars for the targets and not for the groups
    /// would agree with nothing and fail here first.
    /// </summary>
    [Test]
    public void EveryAuthoredCellIsJudgedByAGroupThatContainsItsTarget()
    {
        int checkedCases = 0;

        Assert.Multiple(() =>
        {
            foreach (var one in all_cases)
            {
                // Ask the production loader whether it kept the split at all; a rejected one is
                // back on the derived arm, which is not what this pin reads.
                bool authoredSurvived = DecodeObjects(one.Authored)
                    .Any(h => h.Line.Units.Any(u => u.SyllableSplits.Count > 0));

                if (!authoredSurvived)
                    continue;

                checkedCases++;
                var lines = BrowserCase(one.Name).GetProperty("a").GetProperty("lines");

                for (int i = 0; i < lines.GetArrayLength(); i++)
                {
                    var cells = lines[i].GetProperty("cells");
                    var groups = lines[i].GetProperty("syllables");
                    var membership = lines[i].GetProperty("cellSyllable");

                    for (int c = 0; c < cells.GetArrayLength(); c++)
                    {
                        int g = membership[c].GetInt32();

                        if (g < 0)
                            continue;

                        double target = cells[c].GetProperty("target").GetDouble();

                        Assert.That(target, Is.InRange(groups[g].GetProperty("startTime").GetDouble(), groups[g].GetProperty("endTime").GetDouble()),
                            $"{one.Name}[{i}][{c}]: target outside the span of the group judging it");
                    }
                }
            }
        });

        Assert.That(checkedCases, Is.GreaterThan(0), "no case anywhere kept an authored split, so this pin checked nothing");
    }

    /// <summary>
    /// The shared derivation itself, function for function: validity, the derived split, the
    /// effective split and the cell-space cut. The pipeline comparison above would catch a
    /// divergence here too, but only as a moved target somewhere; this says which rule moved.
    /// </summary>
    [Test]
    public void TheTwoSyllableSegmentDerivationsAgree()
    {
        var probes = browser.Value.GetProperty("segments");
        var expected = SegmentProbes();

        Assert.That(probes.GetArrayLength(), Is.EqualTo(expected.Length), "probe count");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < expected.Length; i++)
            {
                var probe = probes[i];
                string token = expected[i].Token;
                int segments = expected[i].Segments;
                int[] authored = expected[i].Authored;
                string what = $"\"{token}\"/{segments}/[{string.Join(",", authored)}]";

                Assert.That(probe.GetProperty("token").GetString(), Is.EqualTo(token), $"{what}: probe order");
                Assert.That(probe.GetProperty("valid").GetBoolean(), Is.EqualTo(SyllableSegments.IsAuthoredValid(token, segments, authored)), $"{what}: IsAuthoredValid");
                Assert.That(Ints(probe, "derived"), Is.EqualTo(SyllableSegments.Derived(token, segments).ToArray()), $"{what}: Derived");
                Assert.That(Ints(probe, "effective"), Is.EqualTo(SyllableSegments.SplitsFor(token, segments, authored).ToArray()), $"{what}: SplitsFor");
                Assert.That(Ints(probe, "cuts"), Is.EqualTo(SyllableSegments.CellCuts(token, SyllableSegments.SplitsFor(token, segments, authored))), $"{what}: CellCuts");
            }
        });
    }

    private static int[] Ints(JsonElement element, string key)
    {
        var list = new List<int>();

        foreach (var entry in element.GetProperty(key).EnumerateArray())
            list.Add(entry.GetInt32());

        return list.ToArray();
    }
}
