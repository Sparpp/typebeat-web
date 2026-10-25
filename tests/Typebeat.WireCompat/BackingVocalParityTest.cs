using System.Text;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 445's cross-repo pin, in the <see cref="OriginalTextParityTest"/> pattern: a map with
/// BACKING VOCALS (the <c>"backing"</c> array ending a <c>[Lyrics]</c> line, words shown above the
/// lyric and never typed), as the game's own writers produce it, read by the game's decoder, the
/// server's ingest and the browser's decoder. The game reads the words back exactly; the server and
/// the browser read the map exactly as they read its backing-free twin; and the game's local
/// ranked-status check and the server's gameplay fingerprint agree that backing vocals are no
/// gameplay change.
/// </summary>
[TestFixture]
public class BackingVocalParityTest
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

    private static ClientUnit unit(string text, double start, double end)
        => new ClientUnit { Text = text, StartTime = start, EndTime = end, Source = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimingSource.Explicit };

    /// <summary>The editor's SAVE of a map with backing vocals: one placed anchor, one default, a bracket kept as text.</summary>
    private static string editorSave()
    {
        var lines = new[]
        {
            new ClientLine
            {
                RawText = "i wanna let my hair", StartTime = 1000, EndTime = 4000, SingEndTime = 3800,
                Units = [unit("i", 1000, 1300), unit("wanna", 1300, 2000), unit("let", 2000, 2500), unit("my", 2500, 3000), unit("hair", 3000, 3800)],
                BackingVocals = [new BackingVocal("down", 2600, 3000, 0.05), new BackingVocal("(down)", 3100, 3500)],
            },
            new ClientLine
            {
                RawText = "down", StartTime = 4000, EndTime = 6000, SingEndTime = 5500,
                Units = [unit("down", 4000, 5500)],
                BackingVocals = [new BackingVocal("ooh", 4200, 5800)],
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

    /// <summary>An IMPORT of an .lrc with a bracketed backing vocal and a backing-only line, through the importer and the .osu writer.</summary>
    private static string lrcImport()
    {
        const string lrc = "[00:01.00]i wanna let my hair down (down)\n[00:05.00](ooh ooh)\n[00:07.00]and dance\n[00:09.00]\n";
        string timing = LyricMapImporter.SynthesizeTimingJsonFromLrc(lrc, null)!;
        return LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", timing);
    }

    private static readonly string[] files = ["editor save", "lrc import"];

    private static string osuOf(string name) => name == "editor save" ? editorSave() : lrcImport();

    [TestCaseSource(nameof(files))]
    public void TheGameReadsItsBackingVocalsBack(string name)
    {
        string osu = osuOf(name);
        var client = clientLines(osu);

        Assert.That(osu, Does.Contain("\"backing\":["), "the fixture carries backing vocals at all");
        Assert.That(client.SelectMany(l => l.BackingVocals).Select(w => w.Text),
            Is.EquivalentTo(name == "editor save" ? new[] { "down", "(down)", "ooh" } : new[] { "down", "ooh", "ooh" }));
        Assert.That(client.All(l => l.BackingVocals.All(w => w.StartTime >= l.StartTime && w.EndTime <= l.EndTime)), "each inside its own line");
    }

    /// <summary>
    /// The server reads nothing off the backing vocals: the same lines, words and times, the same
    /// judged-cell count and the same stored lyric text as the backing-free twin, and the same lines
    /// and words the game's own decoder reads.
    /// </summary>
    [TestCaseSource(nameof(files))]
    public void TheServerParseIsTheMapWithoutItsBackingVocals(string name)
    {
        string osu = osuOf(name);
        string stripped = LyricOsuFormat.StripBacking(osu);
        var client = clientLines(osu);
        var server = serverParse(osu);
        var twin = serverParse(stripped);

        Assert.That(stripped, Does.Not.Contain("\"backing\""));
        Assert.That(server.Lines.Count, Is.EqualTo(client.Count));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < client.Count; i++)
            {
                Assert.That(server.Lines[i].RawText, Is.EqualTo(client[i].RawText), $"line {i} text");
                Assert.That(server.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime)),
                    Is.EqualTo(client[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime))), $"line {i} words");
                Assert.That(server.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime)),
                    Is.EqualTo(twin.Lines[i].Units.Select(u => (u.Text, u.StartTime, u.EndTime))), $"line {i} words against the twin");
            }

            Assert.That(server.LyricsText, Is.EqualTo(twin.LyricsText));
            foreach (bool literate in new[] { false, true })
                Assert.That(JudgedCells.Count(server.Lines, literate), Is.EqualTo(JudgedCells.Count(twin.Lines, literate)), $"judged cells, literate {literate}");
        });
    }

    /// <summary>
    /// The two exclusions are ONE rule: the game's <c>LyricOsuFormat.StripBacking</c> (its local
    /// ranked-status check) and the server's <see cref="GameplayFingerprint"/> both drop exactly the
    /// backing vocals, so a file and its backing-free twin are the same map to both, and a typed word
    /// changed is a different map to both.
    /// </summary>
    [TestCaseSource(nameof(files))]
    public void TheStatusCheckAndTheFingerprintExcludeTheSameBackingVocals(string name)
    {
        string osu = osuOf(name);
        string stripped = LyricOsuFormat.StripBacking(osu);
        var files = new List<PackageFileEntry> { new PackageFileEntry(new byte[32], 5, "audio.mp3") };

        Assert.That(new TypeBeatRuleset().NativeEncodingsEquivalentForStatus(osu, stripped), Is.True);
        Assert.That(GameplayFingerprint.Compute(serverParse(osu), files), Is.EqualTo(GameplayFingerprint.Compute(serverParse(stripped), files)));

        // The server's canonical line IS the game's stripped line, byte for byte.
        var clientStrippedLyrics = stripped[(stripped.IndexOf("[Lyrics]", StringComparison.Ordinal) + "[Lyrics]".Length)..]
                                   .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(serverParse(osu).LyricSectionLines.Select(GameplayFingerprint.CanonicalLyricLine), Is.EqualTo(clientStrippedLyrics));

        string firstWord = clientLines(osu)[0].Units[0].Text;
        string retyped = osu.Replace($"{{\"text\":\"{firstWord}\"", $"{{\"text\":\"{firstWord}x\"");
        Assert.That(retyped, Is.Not.EqualTo(osu));
        Assert.That(new TypeBeatRuleset().NativeEncodingsEquivalentForStatus(osu, retyped), Is.False);
        Assert.That(GameplayFingerprint.Compute(serverParse(retyped), files), Is.Not.EqualTo(GameplayFingerprint.Compute(serverParse(osu), files)));
    }

    /// <summary>
    /// A stored map whose backing arrays need FITTING and CLEANING on the way in: words before and
    /// after their line, entries the reader must skip (no start, a string start, a blank text, not an
    /// object), a messy text, and anchors out of range or of the wrong kind.
    /// </summary>
    private static string messySave()
    {
        string osu = editorSave();
        const string messy = ",\"backing\":[{\"text\":\"early\",\"start_ms\":200,\"end_ms\":1500},{\"text\":\" a  b \",\"start_ms\":2600,\"end_ms\":2500,\"anchor\":7},"
                             + "{\"text\":\"skip\"},{\"text\":\"skip\",\"start_ms\":\"2700\"},{\"text\":\"  \",\"start_ms\":2800},3,"
                             + "{\"text\":\"left\",\"start_ms\":3000,\"anchor\":-2},{\"text\":\"kind\",\"start_ms\":3200,\"anchor\":\"left\"},{\"text\":\"late\",\"start_ms\":3900,\"end_ms\":9000}]}";
        int at = osu.IndexOf(",\"backing\":[", StringComparison.Ordinal);
        int end = osu.IndexOf("]}", at, StringComparison.Ordinal) + 2;
        return osu[..at] + messy + osu[end..];
    }

    /// <summary>
    /// The browser's half of the display (typebeat-player.js and buildBeatmap) against the game's:
    /// the same words read and fitted out of the same bytes, the same resting place across the line,
    /// the same tilt (the hash and its half-to-even rounding ported bit for bit) and the same pop-up
    /// pose at every moment of the throw.
    /// </summary>
    [Test]
    public void TheBrowserReadsPlacesTiltsAndPopsThemAsDesktopDoes()
    {
        string[] osu = [editorSave(), lrcImport(), messySave()];
        (string Text, double Start, int Index)[] tilts =
        [
            ("down", 2600, 0), ("(down)", 3100.5, 1), ("ooh", 4200, 0), ("d\u00eda", 2500.5, 3), ("yeah yeah", 2601.5, 7), ("x", -40, 2), ("\u305a\u3063\u3068", 123456.25, 0),
        ];
        double[] elapsed = [-1, 0, 5, 46.67, 93.33, 120, 140, 200, 250, 279.9, 280, 1000];
        (double?[] Anchors, int Index)[] anchors =
        [
            ([null], 0), ([null, null], 1), ([null, null, null, null], 2), ([0.05, null, 2.5], 0), ([0.05, null, 2.5], 1), ([0.05, null, 2.5], 2), ([-1, null], 0),
        ];

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-backing-display-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            osu,
            tilts = tilts.Select(t => new { text = t.Text, start = t.Start, index = t.Index }),
            elapsed,
            anchors = anchors.Select(a => new { anchors = a.Anchors, index = a.Index }),
        }), new UTF8Encoding(false));

        System.Text.Json.JsonElement browser;

        try
        {
            browser = NodeHarness.Run("CoreBackingVocalsHarness.cjs", path);
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
            var lines = browser.GetProperty("lines").EnumerateArray().ToArray();

            for (int f = 0; f < osu.Length; f++)
            {
                var client = clientLines(osu[f]);
                var browserLines = lines[f].EnumerateArray().ToArray();

                Assert.That(browserLines, Has.Length.EqualTo(client.Count), $"file {f}: line count");

                for (int l = 0; l < Math.Min(browserLines.Length, client.Count); l++)
                {
                    var words = browserLines[l].EnumerateArray().Select(w => new BackingVocal(
                        w.GetProperty("text").GetString()!, w.GetProperty("start").GetDouble(), w.GetProperty("end").GetDouble(),
                        w.GetProperty("anchor").ValueKind == System.Text.Json.JsonValueKind.Null ? null : w.GetProperty("anchor").GetDouble())).ToArray();

                    Assert.That(words, Is.EqualTo(client[l].BackingVocals), $"file {f} line {l}: the words read and fitted");
                }
            }

            Assert.That(clientLines(osu[2]).SelectMany(l => l.BackingVocals).Select(w => w.Text), Is.EqualTo(new[] { "early", "a b", "left", "kind", "late", "ooh" }),
                "the messy fixture really exercises the reader");

            var browserTilts = browser.GetProperty("tilts").EnumerateArray().Select(e => e.GetDouble()).ToArray();

            for (int i = 0; i < tilts.Length; i++)
            {
                float desktop = typebeat.Game.Rulesets.TypeBeat.UI.LyricLineDisplay.BackingTilt(new BackingVocal(tilts[i].Text, tilts[i].Start, tilts[i].Start), tilts[i].Index);
                Assert.That(browserTilts[i], Is.EqualTo(desktop).Within(1e-4), $"tilt of {tilts[i]}");
            }

            var poses = browser.GetProperty("poses").EnumerateArray().ToArray();

            for (int i = 0; i < elapsed.Length; i++)
            {
                var desktop = typebeat.Game.Rulesets.TypeBeat.UI.LyricLineDisplay.BackingPop(elapsed[i]);
                var pose = poses[i];
                string at = $"pose at {elapsed[i]} ms";

                Assert.That(pose.GetProperty("alpha").GetDouble(), Is.EqualTo(desktop.Alpha).Within(1e-5), at + ": alpha");

                // Hidden is hidden: the rest of a hidden pose is never drawn.
                if (desktop.Alpha <= 0)
                    continue;

                Assert.That(pose.GetProperty("lift").GetDouble(), Is.EqualTo(desktop.Lift).Within(1e-5), at + ": lift");
                Assert.That(pose.GetProperty("scale").GetDouble(), Is.EqualTo(desktop.Scale).Within(1e-5), at + ": scale");
                Assert.That(pose.GetProperty("tilt").GetDouble(), Is.EqualTo(desktop.Tilt).Within(1e-5), at + ": tilt");
            }

            var browserAnchors = browser.GetProperty("anchors").EnumerateArray().Select(e => e.GetDouble()).ToArray();

            for (int i = 0; i < anchors.Length; i++)
            {
                var words = anchors[i].Anchors.Select((a, k) => new BackingVocal("w", k, k, a)).ToArray();
                Assert.That(browserAnchors[i], Is.EqualTo(BackingVocals.AnchorOf(words, anchors[i].Index)), $"anchor {i}");
            }
        });
    }

    /// <summary>
    /// The BROWSER reads the file with backing vocals exactly as it reads its backing-free twin, and
    /// both are the game's own text, cells and cell targets, on the default and Literate arms.
    /// </summary>
    [TestCaseSource(nameof(files))]
    public void TheBrowserDecoderIgnoresBackingVocals(string name)
    {
        string osu = osuOf(name);
        string stripped = LyricOsuFormat.StripBacking(osu);
        var client = clientLines(osu);

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-backing-{Guid.NewGuid():N}.json");
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
                var withBacking = browser[0].GetProperty(arm).EnumerateArray().ToArray();
                var withoutBacking = browser[1].GetProperty(arm).EnumerateArray().ToArray();

                Assert.That(withBacking.Select(l => l.GetRawText()), Is.EqualTo(withoutBacking.Select(l => l.GetRawText())),
                    $"{arm}: the backing vocals move nothing the browser decodes");

                Assert.That(withBacking, Has.Length.EqualTo(client.Count), $"{arm}: browser line count");

                for (int l = 0; l < Math.Min(withBacking.Length, client.Count); l++)
                {
                    var cells = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine.FromLyricLine(client[l], literate, alignSubdivisionTargets: true).Cells;

                    Assert.That(withBacking[l].GetProperty("text").GetString(), Is.EqualTo(client[l].RawText), $"{arm} line {l}: browser text");
                    Assert.That(withBacking[l].GetProperty("stream").GetString(), Is.EqualTo(new string(cells.Select(c => c.Expected).ToArray())),
                        $"{arm} line {l}: browser cells");
                    Assert.That(withBacking[l].GetProperty("targets").EnumerateArray().Select(e => e.GetDouble()),
                        Is.EqualTo(cells.Select(c => c.TargetTime)).AsCollection, $"{arm} line {l}: browser cell targets");
                }
            }
        });
    }
}
