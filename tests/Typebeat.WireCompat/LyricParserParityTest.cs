using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps.Timing;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Typebeat.Web.Packages;
using ClientArm = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty.JudgementArm;
using ClientDifficulty = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPace = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricPaceStatistics;
using ClientPp = typebeat.Game.Rulesets.TypeBeat.Scoring.PerformancePoints;
using ServerArm = Typebeat.Web.Packages.Lyrics.LyricDifficulty.JudgementArm;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the STORED MAP'S PARSE: the same .osu read by the game's production
/// decoder (<see cref="LyricBeatmapDecoder"/>, i.e. <c>TimingJsonLoader.TryParseRawLine</c> and
/// <c>BuildLines</c>) and by the server's ingest (<see cref="BeatmapPackageParser"/>, i.e.
/// <c>LyricTiming</c>), unit by unit, and then rated by each side's own model.
///
/// <para>
/// Every other rating pin in this project hands both models a HAND-BUILT map, so it proves the
/// models agree on a unit they were given and says nothing about whether the two parsers GIVE them
/// the same unit. That gap is where PR 2's authored pauses live (the editor writes a per-word
/// <c>pauses</c> array, and a map from the build before that wrote a single <c>pause</c> object;
/// both loaders keep only the rests <c>PausedWord.UsableRests</c> accepts against the CLAMPED word),
/// and it is where the server's syllable parse had silently diverged: it read a word's
/// <c>syllables</c> as bare numbers while every writer produces <c>{text, start_ms, end_ms}</c>
/// objects, so every subdivided map rated here as unsubdivided. Nothing could see it, because no
/// test fed both parsers the same bytes. This one does.
/// </para>
///
/// <para>
/// The .osu is produced by the game's own <c>LyricOsuFormat.GenerateOsu</c>, the write path the
/// .osz conversion and the editor's export share, which passes each line object through verbatim;
/// so the pause shapes below reach both parsers exactly as a mapper's file would carry them.
/// </para>
/// </summary>
[TestFixture]
public class LyricParserParityTest
{
    private const double word_ms = 900;

    /// <summary>
    /// One word of the fixture: its text, its syllable starts (offsets from the word's own start; the
    /// first syllable at 0 is added for it, as every writer does), and the raw JSON members a word
    /// may carry for its rests, written verbatim.
    /// </summary>
    private readonly record struct Word(string Text, double[] Syllables, Action<JsonObject, double>? Rests = null, double? EndOverride = null);

    private static JsonObject Rest(double start, double end, JsonNode? split)
    {
        var rest = new JsonObject { ["start_ms"] = start, ["end_ms"] = end };

        if (split != null)
            rest["split"] = split;

        return rest;
    }

    /// <summary>
    /// The words, cycled line after line. Each reaches a different branch of the pause contract:
    /// <list type="bullet">
    /// <item><description>"instrumental": the <c>pauses</c> ARRAY with one valid rest;</description></item>
    /// <item><description>"together": the array with TWO valid rests, beside an entry missing its
    /// split and an entry that is not an object, both dropped at parse;</description></item>
    /// <item><description>"wonderful": the LEGACY single <c>pause</c> object, over authored syllables;</description></item>
    /// <item><description>"remember": rests that must be IGNORED (no typeable cell before the split,
    /// an end outside the word, an inverted pair, a text order contradicting the time order);</description></item>
    /// <item><description>"beautiful": both keys at once (the array wins and the object is never
    /// read), a whole-number float split (kept) and a fractional one (dropped at parse);</description></item>
    /// <item><description>"rhythm": a <c>pauses</c> that is not an ARRAY (so it is not read at all,
    /// even though it holds a well-formed rest) falling through to a <c>pause</c> that is not an
    /// OBJECT, so the word carries nothing;</description></item>
    /// <item><description>"forever": a rest valid against the word's RAW span but outside its CLAMPED
    /// one, because the word overruns its line and is clamped back to the next line's start (the
    /// line's last word, which is why the fixture puts it there).</description></item>
    /// </list>
    /// </summary>
    private static readonly Word[] words =
    [
        new("instrumental", [300, 600], (w, t) => w["pauses"] = new JsonArray(Rest(t + 350, t + 500, 5))),
        new("together", [], (w, t) => w["pauses"] = new JsonArray(Rest(t + 250, t + 350, 2), Rest(t + 550, t + 650, 5), Rest(t + 750, t + 800, null), JsonValue.Create(4))),
        new("wonderful", [300, 600], (w, t) => w["pause"] = Rest(t + 400, t + 500, 5)),
        new("remember", [450], (w, t) => w["pauses"] = new JsonArray(
            Rest(t + 200, t + 300, 0), Rest(t + 700, t + 950, 4), Rest(t + 600, t + 500, 3),
            Rest(t + 100, t + 150, 5), Rest(t + 250, t + 350, 3))),
        new("beautiful", [], (w, t) =>
        {
            w["pauses"] = new JsonArray(Rest(t + 200, t + 300, JsonNode.Parse("3.0")), Rest(t + 500, t + 600, JsonNode.Parse("6.5")));
            w["pause"] = Rest(t + 700, t + 800, 7);
        }),
        new("rhythm", [], (w, t) =>
        {
            w["pauses"] = Rest(t + 200, t + 300, 3);
            w["pause"] = 5;
        }),
        new("forever", [], (w, t) => w["pauses"] = new JsonArray(Rest(t + 300, t + 400, 3), Rest(t + 1250, t + 1350, 5)), EndOverride: 1400),
    ];

    /// <summary>The fixture as a timing.json v2 document, eight lines of every word above.</summary>
    private static string TimingJson(bool withRests = true)
    {
        var lines = new JsonArray();
        double t = 2000;

        for (int l = 0; l < 8; l++)
        {
            var wordArray = new JsonArray();
            double lineStart = t;

            foreach (Word word in words)
            {
                double end = t + (word.EndOverride ?? word_ms);
                var w = new JsonObject
                {
                    ["text"] = word.Text,
                    ["start_ms"] = t,
                    ["end_ms"] = end,
                    ["score"] = 1,
                };

                if (word.Syllables.Length > 0)
                {
                    var syllables = new JsonArray();
                    double[] starts = [0, .. word.Syllables];

                    for (int s = 0; s < starts.Length; s++)
                    {
                        syllables.Add(new JsonObject
                        {
                            ["text"] = "x",
                            ["start_ms"] = t + starts[s],
                            ["end_ms"] = s + 1 < starts.Length ? t + starts[s + 1] : end,
                        });
                    }

                    w["syllables"] = syllables;
                }

                if (withRests)
                    word.Rests?.Invoke(w, t);

                wordArray.Add(w);
                t += word_ms;
            }

            lines.Add(new JsonObject
            {
                ["text"] = string.Join(' ', words.Select(w => w.Text)),
                ["start_ms"] = lineStart,
                ["end_ms"] = t,
                ["words"] = wordArray,
            });

            // A gap after each line, so the overrunning last word really is clamped to the next
            // line's start rather than merely reaching it.
            t += 300;
        }

        return new JsonObject { ["version"] = 2, ["song_end_ms"] = t + 5000, ["lines"] = lines }.ToJsonString();
    }

    private static string Osu(bool withRests = true)
        => LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(withRests));

    /// <summary>The whole beatmap as the game's production decoder reads it.</summary>
    private static Beatmap ClientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    /// <summary>The map's lines as the game's production decoder reads them.</summary>
    private static IReadOnlyList<ClientLine> ClientParse(string osu)
        => ClientDecode(osu).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    /// <summary>The map as the server's ingest reads it.</summary>
    private static ParsedDifficulty ServerParse(string osu)
        => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    /// <summary>
    /// The map-font keys (backlog 291) through the same one-file-both-parsers lens: the .osu the
    /// game's writer produces with a LyricFont/LyricFontFile carries them to BOTH production
    /// readers, and a file without them reads as fontless on both sides. The keys are plain
    /// [General] text, so what this pins is the SPELLING and placement: a renamed or moved key on
    /// either side reads as absent on the other, which would silently strip every map's font in
    /// one client while the other keeps showing it.
    /// </summary>
    [Test]
    public void BothParsersReadTheMapFontKeysFromTheSameFile()
    {
        string withFont = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(),
            lyricFont: "Blocky Pixels", lyricFontFile: "lyricfont.woff2");

        var clientMeta = ClientDecode(withFont).Metadata;
        var server = ServerParse(withFont);

        var clientMetaWithout = ClientDecode(Osu()).Metadata;
        var serverWithout = ServerParse(Osu());

        Assert.Multiple(() =>
        {
            Assert.That(clientMeta.LyricFont, Is.EqualTo("Blocky Pixels"), "client: family");
            Assert.That(server.LyricFont, Is.EqualTo("Blocky Pixels"), "server: family");
            Assert.That(clientMeta.LyricFontFile, Is.EqualTo("lyricfont.woff2"), "client: file");
            Assert.That(server.LyricFontFile, Is.EqualTo("lyricfont.woff2"), "server: file");

            Assert.That(clientMetaWithout.LyricFont, Is.Empty, "client: a fontless map stays fontless");
            Assert.That(clientMetaWithout.LyricFontFile, Is.Empty);
            Assert.That(serverWithout.LyricFont, Is.Empty, "server: no key, no font");
            Assert.That(serverWithout.LyricFontFile, Is.Empty);
        });
    }

    /// <summary>
    /// The map's track gain (backlog 315) through the same lens, but between the game's production
    /// decoder and the BROWSER's <c>parseLyricOsu</c>: the server never reads the key, /play is
    /// served the stored .osu verbatim and applies the gain itself. The files are the game writer's
    /// own output, either with a gain passed to it (so the value is spelled by
    /// <c>BeatmapMetadata.EncodeAudioGain</c>) or with a raw <c>AudioGain:</c> line spliced into its
    /// [Metadata] section, which is how the clamp and the TryParse edges reach both readers.
    /// </summary>
    [Test]
    public void TheBrowserReadsTheSameAudioGainAsTheGamesDecoder()
    {
        string plain = Osu();
        const string metadata_header = "[Metadata]";
        int at = plain.IndexOf(metadata_header, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "the writer emits a [Metadata] section");

        string spliced(params string[] values)
        {
            var lines = new StringBuilder();
            foreach (string v in values)
                lines.Append("\nAudioGain:").Append(v);
            return plain.Insert(at + metadata_header.Length, lines.ToString());
        }

        var cases = new List<(string Name, string Osu)>
        {
            ("absent", plain),
            ("written 2.5", LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(), audioGain: 2.5)),
            ("written 1/3", LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(), audioGain: 1.0 / 3)),
            ("written 0", LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(), audioGain: 0)),
            ("written 4", LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(), audioGain: 4)),
            ("good then junk", spliced("2", "junk")),
            ("junk then good", spliced("junk", "3")),
        };

        foreach (string raw in new[]
                 {
                     "2", "0.5", "4", "4.5", "1e1", "-1", "-0.25", "0", ".5", "1.", "+2", "2.5e-1", "1E+0",
                     "2x", "1.5.2", "abc", "", "1,5", "0x2", "1 2", "Infinity", "-Infinity", "infinity", "1e400", " 3 ",
                     // NaN used to be the one value the two readers disagreed on: double.TryParse
                     // accepts it and Math.Clamp passed it through, while the browser has always
                     // refused it. The game's decoder now guards it the same way (backlog 324), so
                     // this is an ordinary agreeing case rather than a divergence pinned on its own.
                     "NaN",
                 })
            cases.Add(($"raw '{raw}'", spliced(raw)));

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-audiogain-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(cases.Select(c => c.Osu).ToArray()), new UTF8Encoding(false));

        System.Text.Json.JsonElement browser;

        try
        {
            browser = NodeHarness.Run("CoreAudioGainHarness.cjs", path);
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

        double[] gains = browser.GetProperty("gains").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        double[] built = browser.GetProperty("built").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        int nonDefault = 0;

        Assert.Multiple(() =>
        {
            Assert.That(gains, Has.Length.EqualTo(cases.Count));

            for (int i = 0; i < cases.Count; i++)
            {
                double game = ClientDecode(cases[i].Osu).Metadata.AudioGain;

                if (game != BeatmapMetadata.DEFAULT_AUDIO_GAIN)
                    nonDefault++;

                Assert.That(gains[i], Is.EqualTo(game), $"{cases[i].Name}: parse");
                Assert.That(built[i], Is.EqualTo(game), $"{cases[i].Name}: carried by buildBeatmap");
            }
        });

        // Coverage: most cases must move the gain, or the agreement is mostly on the default.
        Assert.That(nonDefault, Is.GreaterThanOrEqualTo(20), "cases whose gain is not the default");
    }

    /// <summary>
    /// The Latin SPECIAL LETTERS (backlog 329, step a) through all three decoders of one file. An
    /// import's .osu re-emits the aligner's RAW line, so "Straße" reaches every client as written
    /// and each one's normalizer decides what the player types: the game's production decoder,
    /// the server's ingest (whose parse is what the stored rating counts) and the browser's
    /// <c>parseLyricOsu</c> + <c>buildBeatmap</c> (what a /play run types). Held text for text, unit
    /// for unit and cell for cell, on both the default and the Literate flattening, since Literate
    /// types the spelled capitals case-sensitively.
    /// </summary>
    [Test]
    public void AllThreeDecodersSpellTheSpecialLettersIdentically()
    {
        (string Text, double Start, double End)[][] fixture =
        [
            [("Straße", 1000, 1800), ("STRAẞE", 1800, 2600), ("Grüße", 2600, 3400)],
            [("Øresund", 4000, 4800), ("Ærø", 4800, 5600), ("cœur", 5600, 6200), ("Œuvre", 6200, 7000)],
            [("łódź", 8000, 8800), ("Łódź", 8800, 9600), ("þú", 9600, 10200), ("Þór", 10200, 11000)],
            [("Đorđe", 12000, 12800), ("Ðað", 12800, 13400), ("kalı", 13400, 14000), ("Ŋaŋ", 14000, 14600), ("ĸ", 14600, 15000)],
            [("Ǿ", 16000, 16400), ("ǽ", 16400, 16800), ("Ǣ", 16800, 17200), ("ĸ", 17200, 17600)],
        ];

        var lines = new JsonArray();

        foreach (var line in fixture)
        {
            var wordArray = new JsonArray();

            foreach ((string text, double start, double end) in line)
                wordArray.Add(new JsonObject { ["text"] = text, ["start_ms"] = start, ["end_ms"] = end, ["score"] = 1 });

            lines.Add(new JsonObject
            {
                ["text"] = string.Join(' ', line.Select(w => w.Text)),
                ["start_ms"] = line[0].Start,
                ["end_ms"] = line[^1].End,
                ["words"] = wordArray,
            });
        }

        string timingJson = new JsonObject { ["version"] = 2, ["song_end_ms"] = 20000, ["lines"] = lines }.ToJsonString();
        string osu = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", timingJson);

        // The premise: the stored file carries the letters RAW (the writer spells them as JSON
        // escapes, which every decoder reads back as the letter), so the decoders really are what
        // decide.
        Assert.That(osu, Does.Contain("Stra\\u00DFe").And.Contain("\\u0141\\u00F3d\\u017A").And.Contain("\\u00DE\\u00F3r"));

        var client = ClientParse(osu);
        var server = ServerParse(osu).Lines;

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-lyricdecode-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { osu }), new UTF8Encoding(false));

        System.Text.Json.JsonElement browser;

        try
        {
            browser = NodeHarness.Run("CoreLyricDecodeHarness.cjs", path)[0];
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

        Assert.Multiple(() =>
        {
            // The game and server TABLES are one table, entry for entry.
            Assert.That(Web.Packages.Lyrics.Typeability.SPECIAL_LETTERS, Is.EquivalentTo(Typeability.SPECIAL_LETTERS), "the two C# tables");

            Assert.That(client.Select(l => l.RawText), Is.EqualTo(new[]
            {
                "Strasse STRASSE Grusse",
                "Oresund AEro coeur OEuvre",
                "lodz Lodz thu Thor",
                "Dorde Dad kali Ngang k",
                "O ae AE k",
            }), "the game's decode, stated so a failure names the rule");

            Assert.That(server.Select(l => l.RawText), Is.EqualTo(client.Select(l => l.RawText)), "server: text");

            for (int l = 0; l < Math.Min(client.Count, server.Count); l++)
            {
                Assert.That(server[l].Units.Select(u => (u.Text, u.StartTime, u.EndTime)),
                    Is.EqualTo(client[l].Units.Select(u => (u.Text, u.StartTime, u.EndTime))), $"line {l}: server units");
            }

            foreach (bool literate in new[] { false, true })
            {
                var js = browser.GetProperty(literate ? "literate" : "plain").EnumerateArray().ToArray();
                string arm = literate ? "literate" : "plain";

                Assert.That(js, Has.Length.EqualTo(client.Count), $"{arm}: browser line count");

                for (int l = 0; l < Math.Min(js.Length, client.Count); l++)
                {
                    var cells = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine.FromLyricLine(client[l], literate).Cells;

                    Assert.That(js[l].GetProperty("text").GetString(), Is.EqualTo(client[l].RawText), $"{arm} line {l}: browser text");
                    Assert.That(js[l].GetProperty("stream").GetString(), Is.EqualTo(new string(cells.Select(c => c.Expected).ToArray())),
                        $"{arm} line {l}: browser cells");
                    Assert.That(js[l].GetProperty("targets").EnumerateArray().Select(e => e.GetDouble()),
                        Is.EqualTo(cells.Select(c => c.TargetTime)).AsCollection, $"{arm} line {l}: browser cell targets");
                }
            }
        });
    }

    /// <summary>
    /// PR 3's EDITOR TIMING in the stored file. The game's writer now emits an <c>[Editor]</c> section
    /// (<c>BeatDivisor:</c>) and as many <c>[TimingPoints]</c> rows as the editor's BPM tools authored,
    /// kiai effect points folded in, where it used to write the one placeholder row
    /// <c>0,500,4,2,0,100,1,0</c>. None of it is gameplay: every cell target comes from
    /// <c>[Lyrics]</c>. So the same map written with and without editor timing has to parse, rate,
    /// pace, validate and FINGERPRINT identically on the server, and decode to the same cells in
    /// the browser; only the bytes (checksum_md5) and the set's BPM (the first uninherited row, as
    /// it always was) may differ.
    ///
    /// <para>The fingerprint decision, made here and documented on <see cref="GameplayFingerprint"/>:
    /// timing points and the beat divisor stay OUT of it, as BPM always was, so a BPM-only editor
    /// save keeps a ranked map's rank. The game's own local status check compares the whole
    /// encoding and is stricter; the server's rule has always been "what the player types against",
    /// and editor timing is not that.</para>
    /// </summary>
    [Test]
    public void AFileWithEditorTimingParsesRatesAndFingerprintsLikeTheSameMapWithout()
    {
        TimingControlPoint[] timing =
        [
            new TimingControlPoint { Time = -123.456789, BeatLength = 60000 / 128.0 },
            new TimingControlPoint { Time = 8012.345678, BeatLength = 60000 / 89.987654321, TimeSignature = new TimeSignature(7), OmitFirstBarLine = true },
        ];
        EffectControlPoint[] effects = [new EffectControlPoint { Time = 9000.25, KiaiMode = true }];

        string edited = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(),
            beatmapId: 11, beatmapSetId: 7, editorTimingPoints: timing, editorEffectPoints: effects, beatDivisor: 3);
        string plain = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson(), beatmapId: 11, beatmapSetId: 7);

        var clientEdited = ClientDecode(edited);
        var serverEdited = ServerParse(edited);
        var serverPlain = ServerParse(plain);

        byte[] audio = Encoding.UTF8.GetBytes("audio");
        var files = new List<PackageFileEntry> { new PackageFileEntry(SHA256.HashData(audio), audio.Length, "audio.mp3") };

        using var zip = new MemoryStream();

        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in new[] { ("map.osu", Encoding.UTF8.GetBytes(edited)), ("audio.mp3", audio) })
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        zip.Position = 0;
        var package = BeatmapPackageParser.Parse(zip);

        string[] browser = LyricDecodeInBrowser(plain, edited);

        Assert.Multiple(() =>
        {
            Assert.That(edited, Does.Contain("[Editor]\r\nBeatDivisor: 3").Or.Contain("[Editor]\nBeatDivisor: 3"), "the premise: the new writer emits the section");
            Assert.That(clientEdited.BeatmapInfo.BeatDivisor, Is.EqualTo(3), "the game reads it back");
            Assert.That(clientEdited.ControlPointInfo.TimingPoints.Count, Is.EqualTo(2), "and both authored timing rows");

            Assert.That(serverEdited.LyricSectionLines, Is.EqualTo(serverPlain.LyricSectionLines), "the [Lyrics] section is untouched");
            Assert.That(serverEdited.Pace, Is.EqualTo(serverPlain.Pace), "pace and difficulty_rating");
            Assert.That(serverEdited.RatingsJson, Is.EqualTo(serverPlain.RatingsJson), "the eighteen-cell matrix");
            Assert.That(GameplayFingerprint.Compute(serverEdited, files), Is.EqualTo(GameplayFingerprint.Compute(serverPlain, files)),
                "editor timing is not gameplay, so the fingerprint does not move");
            Assert.That(serverEdited.ChecksumMd5, Is.Not.EqualTo(serverPlain.ChecksumMd5), "while the bytes do");

            Assert.That(serverPlain.Bpm, Is.EqualTo(120), "the placeholder row");
            Assert.That(serverEdited.Bpm, Is.EqualTo(128).Within(1e-9), "the first uninherited row, the set's BPM as it always was");

            Assert.That(() => PackageValidator.Validate(package, 7, [11], "mapper"), Throws.Nothing, "the package validates");

            Assert.That(browser[1], Is.EqualTo(browser[0]), "the browser decodes the same cells from both files");
        });
    }

    /// <summary>The browser's decode of each file (its lines, both streams), as raw JSON text.</summary>
    private static string[] LyricDecodeInBrowser(params string[] osus)
    {
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-lyricdecode-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(osus), new UTF8Encoding(false));

        try
        {
            var result = NodeHarness.Run("CoreLyricDecodeHarness.cjs", path);
            return Enumerable.Range(0, osus.Length).Select(i => result[i].GetRawText()).ToArray();
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
    }

    [Test]
    public void BothParsersReadTheSameUnitsPausesAndSyllablesFromTheSameFile()
    {
        string osu = Osu();
        var client = ClientParse(osu);
        IReadOnlyList<ServerLine> server = ServerParse(osu).Lines;

        Assert.Multiple(() =>
        {
            Assert.That(server.Count, Is.EqualTo(client.Count), "line count");

            for (int l = 0; l < Math.Min(client.Count, server.Count); l++)
            {
                Assert.That(server[l].RawText, Is.EqualTo(client[l].RawText), $"line {l}: text");
                Assert.That(server[l].StartTime, Is.EqualTo(client[l].StartTime), $"line {l}: start");
                Assert.That(server[l].EndTime, Is.EqualTo(client[l].EndTime), $"line {l}: end");
                Assert.That(server[l].SingEndTime, Is.EqualTo(client[l].SingEndTime), $"line {l}: sing end");
                Assert.That(server[l].Units.Count, Is.EqualTo(client[l].Units.Count), $"line {l}: units");

                for (int u = 0; u < Math.Min(client[l].Units.Count, server[l].Units.Count); u++)
                {
                    var c = client[l].Units[u];
                    var s = server[l].Units[u];
                    string at = $"line {l} word {u} ({c.Text})";

                    Assert.That(s.Text, Is.EqualTo(c.Text), $"{at}: text");
                    Assert.That(s.StartTime, Is.EqualTo(c.StartTime), $"{at}: start");
                    Assert.That(s.EndTime, Is.EqualTo(c.EndTime), $"{at}: end");
                    Assert.That(s.SyllableBoundaries, Is.EqualTo(c.SyllableBoundaries).AsCollection, $"{at}: syllable boundaries");
                    Assert.That(s.Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar)),
                        Is.EqualTo(c.Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar))).AsCollection, $"{at}: pauses");
                }
            }

            // THE CONTRACT, stated on the first line against the server alone so a failure names the
            // rule rather than only a disagreement. Offsets are from each word's start.
            var first = server[0].Units;
            double start(int u) => first[u].StartTime;

            Assert.That(first[0].Pauses, Is.EqualTo(new[] { new Web.Packages.Lyrics.WordPause(start(0) + 350, start(0) + 500, 5) }), "one rest from the array");
            Assert.That(first[1].Pauses.Count, Is.EqualTo(2), "two rests from the array");
            Assert.That(first[2].Pauses, Is.EqualTo(new[] { new Web.Packages.Lyrics.WordPause(start(2) + 400, start(2) + 500, 5) }), "the legacy single object");
            Assert.That(first[2].SyllableBoundaries, Is.EqualTo(new[] { start(2) + 300, start(2) + 600 }), "syllable OBJECTS become boundaries");
            Assert.That(first[3].Pauses, Is.EqualTo(new[] { new Web.Packages.Lyrics.WordPause(start(3) + 100, start(3) + 150, 5) }),
                "every other rest on the word is ignored: no cell before it, outside the word, inverted, or out of text order");
            Assert.That(first[4].Pauses, Is.EqualTo(new[] { new Web.Packages.Lyrics.WordPause(start(4) + 200, start(4) + 300, 3) }),
                "a whole-number float split is kept, a fractional one dropped, and the array wins over the object");
            Assert.That(first[1].Pauses.Select(p => p.SplitChar), Is.EqualTo(new[] { 2, 5 }), "and the malformed entries beside them are dropped");
            Assert.That(first[5].Pauses, Is.Empty, "a non-array pauses and a non-object pause carry nothing");
            Assert.That(first[6].EndTime, Is.LessThan(first[6].StartTime + 1400), "the premise: the overrunning word IS clamped");
            Assert.That(first[6].Pauses, Is.EqualTo(new[] { new Web.Packages.Lyrics.WordPause(start(6) + 300, start(6) + 400, 3) }),
                "a rest outside the CLAMPED word is dropped even though it sat inside the raw one");
        });
    }

    [Test]
    public void TheParsedMapRatesTheSameInAllEighteenCellsAndPacesTheSame()
    {
        // THE END-TO-END CLAIM: the stored matrix the server computes from the bytes it was
        // uploaded is the matrix the client computes from the same bytes. Stars and difficult
        // characters, all eighteen cells, plus the pace figures that read the same parse.
        string osu = Osu();
        var client = ClientParse(osu);
        var parsed = ServerParse(osu);
        var matrix = Typebeat.Web.Scoring.BeatmapRatings.Parse(parsed.RatingsJson)!;

        (ClientArm Client, ServerArm Server, string Name)[] arms =
        [
            (ClientArm.None, ServerArm.None, "none"),
            (ClientArm.Easy, ServerArm.Easy, "ez"),
            (ClientArm.HardRock, ServerArm.HardRock, "hr"),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(matrix.Count, Is.EqualTo(18));

            foreach ((ClientArm clientArm, ServerArm serverArm, string armName) in arms)
            foreach (bool literate in new[] { false, true })
            foreach (double rate in new[] { 1.0, ClientPp.DOUBLE_TIME_BASE_RATE, ClientPp.HALF_TIME_BASE_RATE })
            {
                string name = $"{armName}/{(literate ? "literate" : "plain")}/{rate:0.00}";
                var clientDetail = ClientDifficulty.ComputeDetail(client, rate, literate, ClientDifficulty.Live, clientArm);
                var cell = matrix.TryGet(serverArm, literate, rate);

                Assert.That(cell, Is.Not.Null, name);
                Assert.That(cell!.Value.Stars, Is.EqualTo(clientDetail.Stars), $"{name}: stars");
                Assert.That(cell.Value.DifficultCharacters, Is.EqualTo(clientDetail.DifficultCharacters), $"{name}: difficult characters");
            }

            var clientPace = ClientPace.Compute(client);

            Assert.That(parsed.Pace.TypeableCellCount, Is.EqualTo(clientPace.TypeableCellCount), "char_count");
            Assert.That(parsed.Pace.WordCount, Is.EqualTo(clientPace.WordCount), "word_count");
            Assert.That(parsed.Pace.AverageCpm, Is.EqualTo(clientPace.AverageCpm), "wpm");
            Assert.That(parsed.Pace.TargetWpm, Is.EqualTo(clientPace.TargetWpm), "target_wpm");
            Assert.That(parsed.Pace.DifficultyRating, Is.EqualTo(ClientDifficulty.Compute(client)), "difficulty_rating");

            // Non-vacuity: the rests in the FILE have to reach the stored rating. The same file with
            // every rest removed (syllables kept) must rate differently, or the equalities above
            // would hold for a server that parsed no pause at all.
            var bare = ServerParse(Osu(withRests: false));

            Assert.That(bare.Lines.Sum(l => l.Units.Sum(u => u.Pauses.Count)), Is.Zero);
            Assert.That(parsed.Lines.Sum(l => l.Units.Sum(u => u.Pauses.Count)), Is.GreaterThan(0));
            Assert.That(parsed.Pace.DifficultyRating, Is.Not.EqualTo(bare.Pace.DifficultyRating),
                "the parsed rests have to move the stored rating");
        });
    }
}
