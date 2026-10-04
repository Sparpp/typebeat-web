using System.Text;
using System.Text.Json;
using osu.Framework.Graphics;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using Typebeat.Web.Packages;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using GameSyncWindows = typebeat.Game.Rulesets.TypeBeat.Gameplay.SyncWindows;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 384's cross-repo pin: the mapper's FREESTYLE colour, one <c>[General] FreestyleColour:</c>
/// key written by the game's editor save. The game's decoder and the browser's (parseLyricOsu +
/// buildBeatmap, then the colour the player paints .tb-c-free with) must read the same colour off
/// the same bytes, absent, chosen, default and malformed alike, with the same default; and the
/// server's ingest must take the key without it moving anything it stores or ranks on.
/// </summary>
[TestFixture]
public class FreestyleColourParityTest
{
    private static readonly Colour4 teal = new Colour4((byte)0x12, (byte)0xab, (byte)0xef, (byte)255);

    /// <summary>The editor's save of a one-line freestyle map carrying <paramref name="colour"/>.</summary>
    private static string editorSave(Colour4? colour)
    {
        var line = new ClientLine
        {
            RawText = "ab&cd", StartTime = 1000, EndTime = 3000, SingEndTime = 2800,
            Units = [new ClientUnit { Text = "ab&cd", StartTime = 1000, EndTime = 2800, Source = TimingSource.Explicit }],
        };

        var beatmap = new Beatmap { FreestyleColour = colour };
        beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        beatmap.Metadata.Artist = "Artist";
        beatmap.Metadata.Title = "Title";
        beatmap.Metadata.AudioFile = "audio.mp3";
        beatmap.Metadata.Author.Username = "mapper";
        beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

        return sb.ToString();
    }

    /// <summary>The untouched save with <paramref name="lines"/> hand-inserted at the end of [General].</summary>
    private static string handEdited(params string[] lines)
        => editorSave(null).Replace("[Metadata]", string.Join("\n", lines) + "\n\n[Metadata]");

    private static Beatmap clientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    private static ParsedDifficulty serverParse(string osu) => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    private static (string Name, string Osu)[] cases() =>
    [
        ("untouched", editorSave(null)),
        ("chosen", editorSave(teal)),
        ("default picked", editorSave((Colour4)TypeBeatStyle.FreestyleChar)),
        ("uppercase by hand", handEdited("FreestyleColour: #12ABEF")),
        ("no hash", handEdited("FreestyleColour: 12abef")),
        ("short form", handEdited("FreestyleColour: #1ae")),
        ("with alpha", handEdited("FreestyleColour: #12abefff")),
        ("last line wins, malformed", handEdited("FreestyleColour: #12abef", "FreestyleColour: purple")),
        ("last line wins, valid", handEdited("FreestyleColour: purple", "FreestyleColour: #12abef")),
    ];

    [Test]
    public void TheWriterEmitsTheKeyOnlyForAChosenColour()
    {
        Assert.That(editorSave(null), Does.Not.Contain("FreestyleColour"));
        Assert.That(editorSave((Colour4)TypeBeatStyle.FreestyleChar), Is.EqualTo(editorSave(null)), "the default writes nothing");
        Assert.That(editorSave(teal), Does.Contain("\nFreestyleColour: #12abef"));
    }

    [Test]
    public void TheBrowserReadsAndPaintsWhatTheGameDecodes()
    {
        var all = cases();

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-freestyle-colour-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(all.Select(c => c.Osu).ToArray()), new UTF8Encoding(false));

        JsonElement browser;

        try
        {
            browser = NodeHarness.Run("FreestyleColourHarness.cjs", path);
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

        string defaultSpelling = FreestyleColourKey.Format((Colour4)FreestyleColourKey.Default);

        Assert.Multiple(() =>
        {
            Assert.That(browser.GetProperty("defaultColour").GetString(), Is.EqualTo(defaultSpelling), "one default on both clients");

            var results = browser.GetProperty("cases").EnumerateArray().ToArray();
            Assert.That(results, Has.Length.EqualTo(all.Length));

            for (int i = 0; i < Math.Min(results.Length, all.Length); i++)
            {
                Colour4? game = clientDecode(all[i].Osu).FreestyleColour;
                string? gameSpelling = game is Colour4 c ? FreestyleColourKey.Format(c) : null;

                Assert.That(results[i].GetProperty("decoded").ValueKind == JsonValueKind.Null ? null : results[i].GetProperty("decoded").GetString(),
                    Is.EqualTo(gameSpelling), $"{all[i].Name}: decoded colour");
                Assert.That(results[i].GetProperty("painted").GetString(),
                    Is.EqualTo(FreestyleColourKey.Format((Colour4)FreestyleColourKey.Resolve(game))), $"{all[i].Name}: painted colour");
                Assert.That(results[i].GetProperty("lines").GetInt32(), Is.EqualTo(1), $"{all[i].Name}: the key moves no lyric");
            }

            // The premise of the table: it really does exercise both outcomes on both sides.
            Assert.That(all.Count(c => clientDecode(c.Osu).FreestyleColour == teal), Is.EqualTo(3));
            Assert.That(all.Count(c => clientDecode(c.Osu).FreestyleColour == null), Is.EqualTo(6));
        });
    }

    /// <summary>
    /// The stylesheet's fallback for <c>.tb-c-free</c> is the same default, so a page that never
    /// sets <c>--tb-free</c> still paints the desktop's violet.
    /// </summary>
    [Test]
    public void TheStylesheetFallsBackToTheSameDefault()
    {
        string css = File.ReadAllText(Path.Combine(NodeHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css"));
        string defaultSpelling = FreestyleColourKey.Format((Colour4)FreestyleColourKey.Default);

        Assert.That(css, Does.Contain($".tb-c-free, .tb-line-cur .tb-c-free {{ color: var(--tb-free, {defaultSpelling}); }}"));
    }

    /// <summary>
    /// The server takes the key without storing or ranking on it: the ingest parse accepts every
    /// case (its [General] switch has no arm for the key and no default), and the gameplay
    /// fingerprint is unchanged by it, so a colour-only resubmission keeps a ranked map's rank. The
    /// game's LOCAL status check does count it, as it counts the lyric font: a recoloured map reads
    /// as locally modified until it is submitted, which is what gets the colour to the site.
    /// </summary>
    [Test]
    public void TheServerIngestsTheKeyAndTheFingerprintIgnoresIt()
    {
        var files = new List<PackageFileEntry> { new PackageFileEntry(new byte[32], 5, "audio.mp3") };
        string plain = editorSave(null);
        string fingerprint = GameplayFingerprint.Compute(serverParse(plain), files);

        foreach (var (name, osu) in cases())
        {
            var parsed = serverParse(osu);
            Assert.That(parsed.Lines, Has.Count.EqualTo(1), $"{name}: parsed");
            Assert.That(GameplayFingerprint.Compute(parsed, files), Is.EqualTo(fingerprint), $"{name}: fingerprint");
        }

        Assert.That(new TypeBeatRuleset().NativeEncodingsEquivalentForStatus(editorSave(teal), plain), Is.False);
    }

    /// <summary>
    /// PR 16's freestyle JUDGEMENT ladder is a second cross-repo pin in the same file family: a
    /// freestyle slot is graded on a ladder SCALED wider than an ordinary cell's (the game's
    /// <c>SyncWindows.FREESTYLE_WINDOW_SCALE</c>, read through <c>TypingEngine.WindowsFor</c>), and
    /// the browser must widen it by the same factor or a browser freestyle run is judged tighter
    /// than a desktop one on the same map and submits different statistics. Pins the scale and every
    /// bound of the browser's widened ladder against the game's own numbers.
    /// </summary>
    [Test]
    public void TheBrowserWidensFreestyleJudgementByTheGamesScale()
    {
        var browser = NodeHarness.Run("CoreFreestyleHarness.cjs");

        double scale = browser.GetProperty("freestyleWindowScale").GetDouble();
        Assert.That(scale, Is.EqualTo(GameSyncWindows.FREESTYLE_WINDOW_SCALE),
            "the browser's freestyle window scale must equal the game's");

        var windows = browser.GetProperty("freestyleWindows");
        var game = GameSyncWindows.Default;

        Assert.Multiple(() =>
        {
            Assert.That(windows.GetProperty("ge").GetDouble(), Is.EqualTo(game.GreatEarly * scale).Within(1e-9), "great early");
            Assert.That(windows.GetProperty("gl").GetDouble(), Is.EqualTo(game.GreatLate * scale).Within(1e-9), "great late");
            Assert.That(windows.GetProperty("oe").GetDouble(), Is.EqualTo(game.OkEarly * scale).Within(1e-9), "ok early");
            Assert.That(windows.GetProperty("ol").GetDouble(), Is.EqualTo(game.OkLate * scale).Within(1e-9), "ok late");
            Assert.That(windows.GetProperty("me").GetDouble(), Is.EqualTo(game.MehEarly * scale).Within(1e-9), "meh early");
            Assert.That(windows.GetProperty("ml").GetDouble(), Is.EqualTo(game.MehLate * scale).Within(1e-9), "meh late");
        });
    }
}
