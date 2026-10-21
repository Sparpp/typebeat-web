using System.Text;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using GameGaps = typebeat.Game.Rulesets.TypeBeat.Gameplay.InstrumentalGaps;
using ServerGaps = Typebeat.Web.Packages.Lyrics.InstrumentalGaps;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the DRAIN LENGTH (backlog 423): gameplay time only, the lyric span minus
/// every instrumental stretch the mid-song skip rule recognises. The game computes it for song
/// select and the IPC source (<see cref="TypeBeatRuleset.CalculateDrainLength"/> over the decoded
/// beatmap, which is what the wedge holds); the server stores it as <c>beatmaps.hit_length_s</c>
/// (<see cref="BeatmapPackage.HitLengthS"/>) and serves it as <c>hit_length</c> to the set overlay.
/// Both are built on the mirrored <c>InstrumentalGaps</c>, so this holds the two over the same
/// .osu bytes.
///
/// <para>It also pins the landmine the change sits next to: the play-time anti-cheat gate reads
/// <c>drain_length_s</c> minus <c>skippable_s</c>, and its required seconds must not move for any
/// map. <see cref="ThePlayTimeGateRequiresExactlyWhatItDidBefore"/> holds every fixture's required
/// seconds to the values recorded on the code before <c>hit_length_s</c> existed.</para>
///
/// <para>Fixtures: a synthetic map (always run, so neither pin is vacuous) with two qualifying
/// instrumentals, one of them opening on a punctuation-only token, plus every real map the
/// environment supplies: each <c>.osu</c> under <c>TYPEBEAT_GAP_OSU_DIR</c> and each
/// <c>timing.json</c> under <c>TYPEBEAT_MAPS_DIR</c>, written to a .osu by the production
/// <see cref="LyricOsuFormat"/>.</para>
/// </summary>
[TestFixture]
public class DrainLengthParityTest
{
    /// <summary>
    /// <see cref="PlayTimeGate.RequiredSeconds"/> at rates 1.0, 1.5 and 0.75 for each fixture, as
    /// the server computed them BEFORE backlog 423 (recorded off web main ed763e2 with the R
    /// format). A fixture the environment supplies but this table does not name still gets the
    /// span check; add its row when it joins the fixture set.
    /// </summary>
    private static readonly Dictionary<string, (double Rate1, double Rate15, double Rate075)> required_before =
        new()
        {
            ["synthetic"] = (16.2, 10.799999999999999, 21.599999999999998),
            ["gap:immortal-flame.osu"] = (87.0232339787128, 58.015489319141864, 116.03097863828373),
            ["gap:neon-rain.osu"] = (100.44, 66.96, 133.92),
            ["gap:wii-shop.osu"] = (53.31699984704552, 35.54466656469702, 71.08933312939403),
            ["maps:D4vd - Remember Me (Arcane)"] = (108.1692, 72.11280000000001, 144.22560000000001),
            ["maps:Friday Pilots Club - Spectator"] = (144.378, 96.252, 192.504),
            ["maps:Leap - Waste your Love"] = (145.89, 97.25999999999999, 194.51999999999998),
            ["maps:Siames - The Wolf"] = (127.746, 85.164, 170.328),
            ["maps:South Arcade - How to get away with Murder"] = (126.32310000000001, 84.2154, 168.4308),
        };

    /// <summary>
    /// Four lines singing 1000-2000, 15000-17000, 18000-19000 and 33000-35000. The second opens on
    /// "...", a token with no typeable cell, so its first vocal is the space after it (15400).
    /// Perceived stretches: 13400 ms (qualifies), 1000 ms (does not), 14000 ms (qualifies), so
    /// 27400 ms of instrumental. The span is 1000 to 38000 (the last line's 35000 plus the
    /// decoder's tail), 37000 ms, which leaves a drain of 9600 ms. The skip windows remove 9000 and
    /// 10000 ms, the gate's 19 s.
    /// </summary>
    private static string SyntheticOsu()
    {
        const string timing =
            """
            {"version":2,"song_end_ms":40000,"lines":[
            {"text":"hello world","start_ms":1000,"end_ms":2000,"words":[{"text":"hello","start_ms":1000,"end_ms":1500,"score":1},{"text":"world","start_ms":1500,"end_ms":2000,"score":1}]},
            {"text":"... after the break","start_ms":15000,"end_ms":17000,"words":[{"text":"...","start_ms":15000,"end_ms":15400,"score":1},{"text":"after","start_ms":15400,"end_ms":16000,"score":1},{"text":"the","start_ms":16000,"end_ms":16300,"score":1},{"text":"break","start_ms":16300,"end_ms":17000,"score":1}]},
            {"text":"quick one","start_ms":18000,"end_ms":19000,"words":[{"text":"quick","start_ms":18000,"end_ms":18500,"score":1},{"text":"one","start_ms":18500,"end_ms":19000,"score":1}]},
            {"text":"last line here","start_ms":33000,"end_ms":35000,"words":[{"text":"last","start_ms":33000,"end_ms":33600,"score":1},{"text":"line","start_ms":33600,"end_ms":34200,"score":1},{"text":"here","start_ms":34200,"end_ms":35000,"score":1}]}
            ]}
            """;

        return LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c", timing.ReplaceLineEndings(string.Empty));
    }

    private static List<(string Name, string Osu)> Fixtures()
    {
        var maps = new List<(string, string)> { ("synthetic", SyntheticOsu()) };

        string gapDir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

        if (gapDir.Length > 0 && Directory.Exists(gapDir))
        {
            foreach (string file in Directory.GetFiles(gapDir, "*.osu").OrderBy(f => f, StringComparer.Ordinal))
                maps.Add(("gap:" + Path.GetFileName(file), File.ReadAllText(file)));
        }

        string mapsDir = Environment.GetEnvironmentVariable("TYPEBEAT_MAPS_DIR") ?? string.Empty;

        if (mapsDir.Length > 0 && Directory.Exists(mapsDir))
        {
            foreach (string dir in Directory.GetDirectories(mapsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string timing = Path.Combine(dir, "timing.json");

                if (File.Exists(timing))
                    maps.Add(("maps:" + Path.GetFileName(dir), LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c", File.ReadAllText(timing))));
            }
        }

        return maps;
    }

    private static typebeat.Game.Beatmaps.Beatmap Decode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
    }

    private static List<TypingLine> GameLines(typebeat.Game.Beatmaps.Beatmap decoded)
        => decoded.HitObjects.OfType<TypeBeatHitObject>()
                  .OrderBy(h => h.LineIndex)
                  .Select(h => TypingLine.FromLyricLine(h.Line))
                  .ToList();

    private static ParsedDifficulty Parse(string osu)
        => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    /// <summary>
    /// Every fixture: the server's stored drain is the game's song select drain, and both sides
    /// count the same instrumental milliseconds. The synthetic map is also held to its worked
    /// numbers, and the sweep must find instrumentals somewhere, so it cannot pass on maps that
    /// stopped exercising the subtraction.
    /// </summary>
    [Test]
    public void TheServerDrainIsTheGameDrainOnEveryFixture()
    {
        int withInstrumentals = 0;

        Assert.Multiple(() =>
        {
            foreach (var (name, osu) in Fixtures())
            {
                var decoded = Decode(osu);
                var gameLines = GameLines(decoded);
                var diff = Parse(osu);

                double gameDrainMs = new TypeBeatRuleset().CalculateDrainLength(decoded);
                double gameInstrumentalMs = GameGaps.InstrumentalMs(gameLines);
                double serverInstrumentalMs = ServerGaps.InstrumentalMs(diff.Lines);

                TestContext.Out.WriteLine(
                    $"{name}: span {diff.DrainLengthS:F3} s, drain {diff.HitLengthS:F3} s (game {gameDrainMs / 1000:F3} s), instrumentals {serverInstrumentalMs / 1000:F3} s, skippable {diff.SkippableS:F3} s");

                Assert.That(diff.HitLengthS * 1000, Is.EqualTo(gameDrainMs).Within(1e-6), $"{name}: drain");
                Assert.That(serverInstrumentalMs, Is.EqualTo(gameInstrumentalMs).Within(1e-6), $"{name}: instrumentals");
                Assert.That(diff.HitLengthS, Is.LessThanOrEqualTo(diff.DrainLengthS), $"{name}: drain never exceeds the span");

                if (serverInstrumentalMs > 0)
                    withInstrumentals++;

                if (name == "synthetic")
                {
                    Assert.That(serverInstrumentalMs, Is.EqualTo(27400).Within(1e-9), "synthetic: 13400 + 14000 ms of instrumental");
                    Assert.That(diff.DrainLengthS, Is.EqualTo(37).Within(1e-9), "synthetic: span 1000 to 38000 ms");
                    Assert.That(diff.HitLengthS, Is.EqualTo(9.6).Within(1e-9), "synthetic: 37000 - 27400 ms");
                }
            }
        });

        Assert.That(withInstrumentals, Is.GreaterThan(0), "no fixture has an instrumental to leave out");
    }

    /// <summary>
    /// The play-time gate's required seconds are exactly what they were before backlog 423, on
    /// every fixture and at a plain, a fast and a slow rate: the gate still reads the span
    /// (<c>drain_length_s</c>, which must equal first line start to last line end) minus the skip
    /// allowance. Non-vacuity: on every fixture with an instrumental, the same arithmetic over the
    /// NEW drain asks for strictly less, so a gate quietly pointed at <c>hit_length_s</c> fails
    /// here.
    /// </summary>
    [Test]
    public void ThePlayTimeGateRequiresExactlyWhatItDidBefore()
    {
        int pinned = 0;
        int wouldLoosen = 0;

        Assert.Multiple(() =>
        {
            foreach (var (name, osu) in Fixtures())
            {
                var diff = Parse(osu);
                double span = (diff.Lines[^1].EndTime - diff.Lines[0].StartTime) / 1000;

                Assert.That(diff.DrainLengthS, Is.EqualTo(span), $"{name}: drain_length_s is the lyric span");

                if (required_before.TryGetValue(name, out var before))
                {
                    pinned++;
                    Assert.That(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, diff.SkippableS), Is.EqualTo(before.Rate1).Within(1e-9), $"{name}: required at 1.0x");
                    Assert.That(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, diff.SkippableS, 1.5), Is.EqualTo(before.Rate15).Within(1e-9), $"{name}: required at 1.5x");
                    Assert.That(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, diff.SkippableS, 0.75), Is.EqualTo(before.Rate075).Within(1e-9), $"{name}: required at 0.75x");
                }
                else
                {
                    TestContext.Out.WriteLine($"{name}: no recorded pre-423 requirement; span check only.");
                }

                if (ServerGaps.InstrumentalMs(diff.Lines) > 0)
                {
                    wouldLoosen++;
                    Assert.That(PlayTimeGate.RequiredSeconds(diff.HitLengthS, diff.SkippableS), Is.LessThan(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, diff.SkippableS)),
                        $"{name}: the shown drain would loosen the gate, which is why the gate does not read it");
                }
            }
        });

        Assert.That(pinned, Is.GreaterThan(0), "no fixture was held to its recorded requirement");
        Assert.That(wouldLoosen, Is.GreaterThan(0), "no fixture distinguishes the span from the shown drain");
    }
}
