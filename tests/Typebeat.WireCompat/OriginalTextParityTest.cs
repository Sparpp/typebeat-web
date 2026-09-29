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
    private static string editorSave()
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
                UnromanisedWords = [new UnromanisedWord(0, "君", 5000, 5600)],
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
