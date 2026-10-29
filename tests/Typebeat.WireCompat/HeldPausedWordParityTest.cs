using System.Text;
using System.Text.Json.Nodes;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using Typebeat.Tools.ScoreRecalc;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;
using ClientArm = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty.JudgementArm;
using ClientDifficulty = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPp = typebeat.Game.Rulesets.TypeBeat.Scoring.PerformancePoints;
using ServerArm = Typebeat.Web.Packages.Lyrics.LyricDifficulty.JudgementArm;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 448: a word may carry rests and holds at once, as long as no hold spans or overlaps a rest
/// (the game's <c>HeldWord.ClearOfRest</c>, mirrored in the server's <c>HeldWord</c>). The same .osu,
/// written by the game's own <c>LyricOsuFormat.GenerateOsu</c>, is read by both production parsers and
/// held to agree on which holds play (through the judged cell count, which counts each usable hold's
/// tail and ticks), on the rating, and on whether the map may be submitted.
///
/// <para>"please" is sung over [1000, 4000] with a rest over [2000, 2500] cut before the 'a'. The 'e' is
/// held to the rest's start and the 'a' from its end (one tick): both play. The second line is the
/// same word with the 'e' held THROUGH the rest, which no play honours.</para>
/// </summary>
[TestFixture]
public class HeldPausedWordParityTest
{
    private static JsonObject hold(int c, double start, double end, params double[] ticks)
    {
        var json = new JsonObject { ["char"] = c, ["start_ms"] = start, ["end_ms"] = end };

        if (ticks.Length > 0)
            json["ticks"] = new JsonArray(ticks.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());

        return json;
    }

    /// <summary>One line: "please go" with the word's rest, the given holds on "please", from <paramref name="t"/>.</summary>
    private static JsonObject line(double t, bool holds, params JsonObject[] onPlease)
    {
        var please = new JsonObject
        {
            ["text"] = "please",
            ["start_ms"] = t,
            ["end_ms"] = t + 3000,
            ["score"] = 1,
            ["pauses"] = new JsonArray(new JsonObject { ["start_ms"] = t + 1000, ["end_ms"] = t + 1500, ["split"] = 3 }),
        };

        if (holds)
            please["holds"] = new JsonArray(onPlease.Select(h => (JsonNode)h).ToArray());

        return new JsonObject
        {
            ["text"] = "please go",
            ["start_ms"] = t,
            ["end_ms"] = t + 4000,
            ["words"] = new JsonArray(please, new JsonObject { ["text"] = "go", ["start_ms"] = t + 3000, ["end_ms"] = t + 4000, ["score"] = 1 }),
        };
    }

    private static string osu(bool holds = true, bool crossing = true)
    {
        var lines = new JsonArray(line(1000, holds, hold(2, 1700, 2000), hold(3, 2500, 2900, 2700)));

        if (crossing)
            lines.Add(line(11000, holds, hold(2, 11700, 12700)));

        var json = new JsonObject { ["version"] = 2, ["song_end_ms"] = 30000, ["lines"] = lines }.ToJsonString();
        return LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", json);
    }

    private static Beatmap clientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    private static IReadOnlyList<ClientLine> clientLines(string osu)
        => clientDecode(osu).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    /// <summary>What the game submits as maximum_statistics under <paramref name="mods"/>, as the server counts it.</summary>
    private static int gameMaximum(string osu, IReadOnlyList<Mod> mods)
    {
        var ruleset = new TypeBeatRuleset();
        var processor = new TypeBeatScoreProcessor(ruleset);
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(new FlatWorkingBeatmap(clientDecode(osu)).GetPlayableBeatmap(ruleset.RulesetInfo, mods));

        var info = new ScoreInfo { Ruleset = ruleset.RulesetInfo, Passed = true };
        processor.PopulateScore(info);

        return ScoringContract.CountAccuracyAffecting(WireCounts.From(info.MaximumStatistics));
    }

    private static ParsedDifficulty serverParse(string osu)
        => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    [Test]
    public void BothSidesCountTheSameHoldsOnAPausedWord()
    {
        string map = osu();
        var server = serverParse(map).Lines;

        Assert.That(server.SelectMany(l => l.Units).Count(u => u.Pauses.Count > 0 && u.Objects.Holds.Count > 0), Is.EqualTo(2), "the premise: both parse rests and holds on one word");

        int plain = JudgedCells.Count(server, literate: false);
        int literate = JudgedCells.Count(server, literate: true);
        int shorthand = JudgedCells.Count(server, literate: false, shorthand: true);

        Assert.Multiple(() =>
        {
            // Two usable tails and one tick, on the first line only: the hold through the rest plays as a plain press.
            Assert.That(plain - server.Sum(l => JudgedCells.CountDefault(l.RawText)), Is.EqualTo(3));

            Assert.That(gameMaximum(map, []), Is.EqualTo(plain), "no mods");
            Assert.That(gameMaximum(map, [new TypeBeatModLiterate()]), Is.EqualTo(literate), "LT");
            Assert.That(gameMaximum(map, [new TypeBeatModShorthand()]), Is.EqualTo(shorthand), "SH");
        });
    }

    [Test]
    public void BothSidesRateAPausedHeldWordTheSame()
    {
        string map = osu();
        var client = clientLines(map);
        var matrix = BeatmapRatings.Parse(serverParse(map).RatingsJson)!;

        (ClientArm Client, ServerArm Server, string Name)[] arms =
        [
            (ClientArm.None, ServerArm.None, "none"),
            (ClientArm.Easy, ServerArm.Easy, "ez"),
            (ClientArm.HardRock, ServerArm.HardRock, "hr"),
        ];

        Assert.Multiple(() =>
        {
            foreach ((ClientArm clientArm, ServerArm serverArm, string armName) in arms)
            foreach (bool literate in new[] { false, true })
            foreach (double rate in new[] { 1.0, ClientPp.DOUBLE_TIME_BASE_RATE })
            {
                string name = $"{armName}/{(literate ? "literate" : "plain")}/{rate:0.00}";
                var clientDetail = ClientDifficulty.ComputeDetail(client, rate, literate, ClientDifficulty.Live, clientArm);
                var cell = matrix.TryGet(serverArm, literate, rate);

                Assert.That(cell, Is.Not.Null, name);
                Assert.That(cell!.Value.Stars, Is.EqualTo(clientDetail.Stars), $"{name}: stars");
            }

            // Non-vacuity: the holds on the paused word reach the stored rating.
            var bare = BeatmapRatings.Parse(serverParse(osu(holds: false)).RatingsJson)!;
            Assert.That(matrix.TryGet(ServerArm.None, false, 1.0)!.Value.Stars,
                Is.Not.EqualTo(bare.TryGet(ServerArm.None, false, 1.0)!.Value.Stars), "the holds move the rating");
        });
    }

    [Test]
    public void BothSidesSubmitOnlyHoldsThatStayInsideTheirStretch()
    {
        string usable = osu(crossing: false);
        string crossing = osu();

        Assert.Multiple(() =>
        {
            Assert.That(WordObjectsSubmission.Refusal(clientDecode(usable)), Is.Null, "game: usable");
            Assert.That(WordObjectsValidation.FirstProblem(serverParse(usable).Lines), Is.Null, "server: usable");

            Assert.That(WordObjectsSubmission.Refusal(clientDecode(crossing)), Is.Not.Null, "game: a hold through a rest");
            Assert.That(WordObjectsValidation.FirstProblem(serverParse(crossing).Lines), Does.Contain("crosses a pause"), "server: a hold through a rest");
        });
    }
}
