using System.Text;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Typebeat.Web.Packages;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 330's cross-repo pin, in the <see cref="LyricParserParityTest"/> pattern: ONE .osu, as
/// the game's own writers produce it, read by the game's production decoder and by the server's
/// ingest parse. Both must read the same ORIGINALS (line and word), the same typed words and times
/// around them, and the same UNROMANISED words; and the game's local ranked-status check and the
/// server's gameplay fingerprint must agree that originals are not a gameplay change.
/// </summary>
[TestFixture]
public class OriginalTextParityTest
{
    private static Beatmap clientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    private static IReadOnlyList<ClientLine> clientLines(string osu)
        => clientDecode(osu).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    private static ParsedDifficulty serverParse(string osu) => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    private static ClientUnit unit(string text, double start, double end, string? original = null)
        => new ClientUnit { Text = text, StartTime = start, EndTime = end, Source = TimingSource.Explicit, Original = original };

    /// <summary>The editor's SAVE of a map with originals: Russian, a plain English line, and a Japanese line holding an unromanised kanji.</summary>
    private static string editorSave(bool withUnromanised = true)
    {
        var lines = new[]
        {
            new ClientLine
            {
                RawText = "Privet mir", Original = "Привет мир", StartTime = 1000, EndTime = 3000, SingEndTime = 2800,
                Units = [unit("Privet", 1000, 1900, "Привет"), unit("mir", 1900, 2800, "мир")],
            },
            new ClientLine
            {
                RawText = "hello world", StartTime = 3000, EndTime = 5000, SingEndTime = 4800,
                Units = [unit("hello", 3000, 3900), unit("world", 3900, 4800)],
            },
            new ClientLine
            {
                RawText = "ga suki", Original = "君 が すき", StartTime = 5000, EndTime = 8000, SingEndTime = 7500,
                Units = [unit("ga", 5600, 6400, "が"), unit("suki", 6400, 7500, "すき")],
                UnromanisedWords = withUnromanised ? [new UnromanisedWord(0, "君", 5000, 5600)] : [],
            },
        };

        var beatmap = new Beatmap();
        beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        beatmap.Metadata.Artist = "Artist";
        beatmap.Metadata.Title = "Title";
        beatmap.Metadata.AudioFile = "audio.mp3";
        beatmap.Metadata.Author.Username = "mapper";

        for (int i = 0; i < lines.Length; i++)
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

        return sb.ToString();
    }

    /// <summary>An IMPORT of a Russian .lrc, through the importer's own romanisation and the .osu writer.</summary>
    private static string russianImport()
    {
        const string lrc = "[00:01.00]Расцветали яблони и груши\n[00:05.00]Поплыли туманы над рекой\n[00:09.00]\n";
        string timing = LyricMapImporter.SynthesizeTimingJsonFromLrc(lrc, LyricOriginals.DetectLanguage([lrc]))!;
        return LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", timing, language: "russian");
    }

    [TestCaseSource(nameof(files))]
    public void BothParsersReadTheSameOriginalsFromOneFile(string name)
    {
        string osu = name == "editor save" ? editorSave() : russianImport();

        var client = clientLines(osu);
        var server = serverParse(osu);

        Assert.That(server.Lines.Count, Is.EqualTo(client.Count));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < client.Count; i++)
            {
                Assert.That(server.Lines[i].RawText, Is.EqualTo(client[i].RawText), $"line {i} text");
                Assert.That(server.Lines[i].Original, Is.EqualTo(client[i].Original), $"line {i} original");
                Assert.That(server.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime, u.Original)),
                    Is.EqualTo(client[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime, u.Original))), $"line {i} words");
            }

            Assert.That(server.UnromanisedWords, Is.EqualTo(LyricOriginals.UnromanisedWords(client)), "the unromanised words");
            Assert.That(client.Any(l => l.Original != null), Is.True, "the fixture carries originals at all");
        });
    }

    private static readonly string[] files = ["editor save", "russian import"];

    /// <summary>
    /// The BROWSER ARM (backlog 332). <c>/play</c> is nomod by decision and plays the ROMANISED text,
    /// so <c>typebeat-core.js</c> needs no Polyglot at all; what it needs is to TOLERATE AND IGNORE
    /// every <c>original</c> key, line and word, because it is served the same stored .osu the
    /// desktop decodes. Held two ways over the same fixtures, through the production decoder on the
    /// C# side and the shipped <c>parseLyricOsu</c> + <c>buildBeatmap</c> on the JS side: the
    /// browser reads the file WITH originals exactly as it reads its originals-stripped twin, and
    /// both are the game's own text, cells and cell targets, on the default and Literate arms.
    /// </summary>
    ///
    /// <para>The editor save is taken WITHOUT its unromanised kanji here, and on purpose: a word
    /// the romaniser could not spell is written with an empty text, the game and the server both
    /// take it out of the word pairing, and the browser does not (its first cell on that line is
    /// timed from 5000, the kanji's start, where desktop times it from 5600). No /play run can meet
    /// that shape, because <c>PackageValidator</c> refuses any package still carrying one, so the
    /// browser is only ever served maps where every original sits beside a typed word, which is
    /// exactly what this pins.</para>
    [TestCaseSource(nameof(files))]
    public void TheBrowserDecoderIgnoresOriginals(string name)
    {
        string osu = name == "editor save" ? editorSave(withUnromanised: false) : russianImport();
        string stripped = LyricOsuFormat.StripOriginals(osu);

        // The premise: the file really carries originals, and the twin really does not.
        Assert.That(osu, Does.Contain("\"original\""));
        Assert.That(stripped, Does.Not.Contain("\"original\""));

        var client = clientLines(osu);

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-originals-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { osu, stripped }), new UTF8Encoding(false));

        System.Text.Json.JsonElement browser;

        try
        {
            browser = NodeHarness.Run("CoreLyricDecodeHarness.cjs", path);
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
            foreach (bool literate in new[] { false, true })
            {
                string arm = literate ? "literate" : "plain";
                var withOriginals = browser[0].GetProperty(arm).EnumerateArray().ToArray();
                var withoutOriginals = browser[1].GetProperty(arm).EnumerateArray().ToArray();

                Assert.That(withOriginals.Select(l => l.GetRawText()), Is.EqualTo(withoutOriginals.Select(l => l.GetRawText())),
                    $"{arm}: the originals move nothing the browser decodes");

                Assert.That(withOriginals, Has.Length.EqualTo(client.Count), $"{arm}: browser line count");

                for (int l = 0; l < Math.Min(withOriginals.Length, client.Count); l++)
                {
                    var cells = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine.FromLyricLine(client[l], literate).Cells;

                    Assert.That(withOriginals[l].GetProperty("text").GetString(), Is.EqualTo(client[l].RawText), $"{arm} line {l}: browser text");
                    Assert.That(withOriginals[l].GetProperty("stream").GetString(), Is.EqualTo(new string(cells.Select(c => c.Expected).ToArray())),
                        $"{arm} line {l}: browser cells");
                    Assert.That(withOriginals[l].GetProperty("targets").EnumerateArray().Select(e => e.GetDouble()),
                        Is.EqualTo(cells.Select(c => c.TargetTime)).AsCollection, $"{arm} line {l}: browser cell targets");
                }
            }
        });
    }

    /// <summary>
    /// The site half of the same file (backlog 332): the server's ingest turns the originals into
    /// <c>beatmaps.lyrics_original</c>, aligned line for line with <c>beatmaps.lyrics</c>, with the
    /// game's own line originals where the map wrote them and an empty line where it wrote none.
    /// </summary>
    [Test]
    public void TheStoredOriginalColumnIsAlignedWithTheStoredLyrics()
    {
        var client = clientLines(editorSave());
        var server = serverParse(editorSave());

        string[] lyrics = server.LyricsText.Split('\n');
        string[] originals = server.OriginalLyricsText.Split('\n');

        Assert.Multiple(() =>
        {
            Assert.That(originals, Has.Length.EqualTo(lyrics.Length));
            Assert.That(originals, Is.EqualTo(client.Select(l => l.Original ?? string.Empty)));
            Assert.That(serverParse(LyricOsuFormat.StripOriginals(russianImport())).OriginalLyricsText, Is.Empty,
                "a map with no originals stores none");
        });
    }

    /// <summary>
    /// The two exclusions are ONE rule: the game's <c>LyricOsuFormat.StripOriginals</c> (its local
    /// ranked-status check) and the server's <see cref="GameplayFingerprint"/> both drop exactly the
    /// originals, so a file and its originals-stripped twin are the same map to both, and a typed
    /// word changed is a different map to both.
    /// </summary>
    [Test]
    public void TheStatusCheckAndTheFingerprintExcludeTheSameOriginals()
    {
        string osu = russianImport();
        string stripped = LyricOsuFormat.StripOriginals(osu);
        var files = new List<PackageFileEntry> { new PackageFileEntry(new byte[32], 5, "audio.mp3") };

        Assert.That(stripped, Does.Not.Contain("\"original\""));
        Assert.That(new TypeBeatRuleset().NativeEncodingsEquivalentForStatus(osu, stripped), Is.True);
        Assert.That(GameplayFingerprint.Compute(serverParse(osu), files), Is.EqualTo(GameplayFingerprint.Compute(serverParse(stripped), files)));

        // The server's canonical line IS the game's stripped line, byte for byte.
        var clientStrippedLyrics = stripped[(stripped.IndexOf("[Lyrics]", StringComparison.Ordinal) + "[Lyrics]".Length)..]
                                   .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(serverParse(osu).LyricSectionLines.Select(GameplayFingerprint.CanonicalLyricLine), Is.EqualTo(clientStrippedLyrics));

        string retyped = osu.Replace("\"text\":\"Poplyli tumany nad rekoy\"", "\"text\":\"Poplyli tumany nad rekoi\"");
        Assert.That(retyped, Is.Not.EqualTo(osu));
        Assert.That(new TypeBeatRuleset().NativeEncodingsEquivalentForStatus(osu, retyped), Is.False);
        Assert.That(GameplayFingerprint.Compute(serverParse(retyped), files), Is.Not.EqualTo(GameplayFingerprint.Compute(serverParse(osu), files)));
    }
}
