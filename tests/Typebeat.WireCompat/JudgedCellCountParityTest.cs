using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using Typebeat.Web.Scoring;
using ServerJudgedCells = Typebeat.Web.Packages.Lyrics.JudgedCells;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on <see cref="ServerJudgedCells"/>, the server's count of the cells a map
/// judges (048_judged_cells.sql), which both submit routes hold a claimed <c>maximum_statistics</c>
/// against and REFUSE on a mismatch. A drift here does not move a number, it turns honest
/// plays away, so the count is held against what each client actually submits, on the same .osu
/// bytes the server stores:
///
/// <list type="bullet">
/// <item>the GAME: the production decoder, then <see cref="WorkingBeatmap.GetPlayableBeatmap(typebeat.Game.Rulesets.IRulesetInfo, IReadOnlyList{Mod})"/>
/// (the converter, every mod's beatmap hooks, the nested per-cell objects), then the score
/// processor's own <c>MaximumStatistics</c>, reduced the way the server reduces it
/// (<see cref="ScoringContract.CountAccuracyAffecting"/>). Every mod the game can submit is played
/// alone, so a mod that starts changing the cell list fails here rather than in production; Literate
/// is the one that does today.</item>
/// <item>the BROWSER: <c>parseLyricOsu</c> + <c>buildBeatmap</c>, whose cell list is what
/// <c>computeScore</c> reports as <c>maximumStatistics.great</c>. It plays the default stream only.</item>
/// </list>
///
/// <para>The fixture reaches every rule the count reads: marks inside, around and between words, a
/// hyphen as a word break (and leading or trailing one, which breaks nothing), brackets on both sides
/// of the format version gate, freestyle slots and a stripped ampersand, special letters, digits,
/// the newer marks, split markers, standalone "-" and "|" tokens, a <c>words[]</c> shorter than
/// the line's tokens, freestyle slots beside marks, and a line with nothing to type (dropped on
/// both sides; at v1 a wholly bracketed line is one too). A line
/// that carries only an original is left out: the game keeps it with no cells, but the server
/// refuses to store a map that has one (<c>PackageValidator</c>, unromanised words).</para>
/// </summary>
[TestFixture]
public class JudgedCellCountParityTest
{
    /// <summary>
    /// One line of the fixture: its text, whether it declares freestyle, whether it carries word
    /// timing, and (when set) how many of its tokens get a word object, for a <c>words[]</c> shorter
    /// than the line.
    /// </summary>
    private readonly record struct FixtureLine(string Text, bool Freestyle = false, bool Words = true, int? WordLimit = null);

    private static readonly FixtureLine[] fixture =
    [
        new("Don't stop, believing!"),
        new("The bad-cat sat."),
        new("(oh oh) yeah [ah]"),
        new("- leading and trailing -"),
        new("a , b ... c"),
        new("Straße 99 Grüße"),
        new("la && la", Freestyle: true),
        new("rock & roll"),
        new("beau|ti|ful day", Words: false),
        new("$5 50% *star* <tag> a/b ~x_y^z;"),
        new("wide  spaced   words", Words: false),
        new("!!!"),
        new("x - y"),
        new("a | b"),
        new("c |b d| e"),
        new("a || b", Words: false),
        new("one two three", WordLimit: 1),
        new("a&, &b. -&", Freestyle: true),
        new("(a) [b] c"),
        new("(whole line)"),
        new("a - - b -- c", Words: false),
        new("Ending line: \"quoted\" (and done)."),
    ];

    private static string TimingJson()
    {
        var lines = new JsonArray();
        double t = 1000;

        foreach (var line in fixture)
        {
            double start = t;
            var json = new JsonObject { ["text"] = line.Text, ["start_ms"] = start };

            if (line.Freestyle)
                json["freestyle"] = true;

            if (line.Words)
            {
                var words = new JsonArray();

                foreach (string token in line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(line.WordLimit ?? int.MaxValue))
                {
                    words.Add(new JsonObject { ["text"] = token, ["start_ms"] = t, ["end_ms"] = t + 400, ["score"] = 1 });
                    t += 400;
                }

                json["words"] = words;
            }

            if (!line.Words || line.WordLimit != null)
                t += 1600;

            json["end_ms"] = t;
            lines.Add(json);
            t += 500;
        }

        return new JsonObject { ["version"] = 2, ["song_end_ms"] = t + 5000, ["lines"] = lines }.ToJsonString();
    }

    /// <summary>The fixture as the game's writer stores it, at the current format version or at v1.</summary>
    private static string Osu(bool v1)
    {
        string osu = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", TimingJson());

        return v1 ? Regex.Replace(osu, @"\A" + Regex.Escape(LyricBeatmapDecoder.MAGIC) + @"\d+", LyricBeatmapDecoder.MAGIC + "1") : osu;
    }

    private static Beatmap ClientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    /// <summary>The game's playable beatmap under <paramref name="mods"/>, as a Player builds it.</summary>
    private static IBeatmap Playable(string osu, IReadOnlyList<Mod> mods)
        => new FlatWorkingBeatmap(ClientDecode(osu)).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, mods);

    /// <summary>What the game submits as maximum_statistics under <paramref name="mods"/>, as the server counts it.</summary>
    private static int GameMaximum(string osu, IReadOnlyList<Mod> mods)
    {
        var ruleset = new TypeBeatRuleset();
        var processor = new TypeBeatScoreProcessor(ruleset);
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(Playable(osu, mods));

        var info = new ScoreInfo { Ruleset = ruleset.RulesetInfo, Passed = true };
        processor.PopulateScore(info);

        return ScoringContract.CountAccuracyAffecting(WireCounts.From(info.MaximumStatistics));
    }

    /// <summary>The browser's cell count of each file, the <c>maximumStatistics.great</c> its <c>computeScore</c> reports.</summary>
    private static int[] BrowserMaximum(params string[] osus)
    {
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-judgedcells-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(osus), new UTF8Encoding(false));

        try
        {
            var result = NodeHarness.Run("CoreLyricDecodeHarness.cjs", path);
            return Enumerable.Range(0, osus.Length)
                             .Select(i => result[i].GetProperty("plain").EnumerateArray().Sum(l => l.GetProperty("stream").GetString()!.Length))
                             .ToArray();
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

    private static ParsedDifficulty ServerParse(string osu)
        => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    [TestCase(false)]
    [TestCase(true)]
    public void TheServerCountIsWhatTheGameSubmits_LineForLine_OnBothStreams(bool v1)
    {
        string osu = Osu(v1);
        var server = ServerParse(osu).Lines;

        foreach (bool literate in new[] { false, true })
        {
            Mod[] mods = literate ? [new TypeBeatModLiterate()] : [];
            var lines = Playable(osu, mods).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToArray();
            string arm = literate ? "literate" : "default";

            Assert.That(server, Has.Count.EqualTo(lines.Length), $"{arm}: line count");

            Assert.Multiple(() =>
            {
                for (int l = 0; l < Math.Min(server.Count, lines.Length); l++)
                {
                    int expected = lines[l].NestedHitObjects.OfType<TypeBeatCharObject>().Count();
                    int actual = literate ? ServerJudgedCells.CountLiterate(server[l].RawText) : ServerJudgedCells.CountDefault(server[l].RawText);

                    Assert.That(actual, Is.EqualTo(expected), $"{arm} line {l} \"{lines[l].Line.RawText}\"");
                }
            });
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EverySubmittableModSubmitsTheCountOfItsStream(bool v1)
    {
        string osu = Osu(v1);
        var server = ServerParse(osu).Lines;
        int plain = ServerJudgedCells.Count(server, literate: false);
        int literate = ServerJudgedCells.Count(server, literate: true);

        // The premise: the fixture's marks make the two streams differ, so a route that read the
        // wrong column would be caught.
        Assert.That(literate, Is.GreaterThan(plain));

        var submittable = new TypeBeatRuleset().CreateAllMods().Where(m => !m.LocalOnly).ToArray();

        Assert.That(submittable.Select(m => m.Acronym), Does.Contain("LT").And.Contain("DT"), "the enumeration reaches the mods");

        Assert.Multiple(() =>
        {
            Assert.That(GameMaximum(osu, []), Is.EqualTo(plain), "no mods");

            foreach (var mod in submittable)
            {
                // What the submit route reads: Literate in the stack selects the Literate column.
                bool literateStack = mod.Acronym == Typebeat.Web.Scoring.PerformancePoints.LITERATE_ACRONYM;
                Assert.That(GameMaximum(osu, [mod]), Is.EqualTo(literateStack ? literate : plain), mod.Acronym);
            }

            Assert.That(GameMaximum(osu, [new TypeBeatModLiterate(), new TypeBeatModDoubleTime()]), Is.EqualTo(literate), "LT+DT");
        });
    }

    [Test]
    public void TheBrowserSubmitsTheDefaultCount()
    {
        string[] osus = [Osu(v1: false), Osu(v1: true)];
        int[] browser = BrowserMaximum(osus);

        Assert.Multiple(() =>
        {
            for (int i = 0; i < osus.Length; i++)
                Assert.That(browser[i], Is.EqualTo(ServerJudgedCells.Count(ServerParse(osus[i]).Lines, literate: false)), i == 0 ? "current format" : "v1");
        });
    }

    /// <summary>
    /// A <c>[Lyrics]</c> line with a <c>//</c> past its first character is cut there by the game's
    /// legacy decoder (<c>LegacyDecoder.StripComments</c>), so the game drops the line while the
    /// server parse keeps it. Such a map is stored with NO count (<see cref="ServerJudgedCells.ForMap"/>),
    /// so its honest plays are accepted unchecked rather than refused against a count the game does
    /// not submit. The premise is checked first: the game really does submit fewer cells.
    /// </summary>
    [TestCase("see http://x.com now", null)]
    [TestCase("a//b c", null)]
    [TestCase("hi there", "x//y")]
    public void ALyricsLineTheGameReadsAsACommentLeavesTheMapUncounted(string text, string? original)
    {
        var line = new JsonObject { ["text"] = text, ["start_ms"] = 1000, ["end_ms"] = 2000 };

        if (original != null)
            line["original"] = original;

        var lines = new JsonArray(line, new JsonObject { ["text"] = "hello world", ["start_ms"] = 5000, ["end_ms"] = 6000 });
        string osu = LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper",
            new JsonObject { ["version"] = 2, ["song_end_ms"] = 20000, ["lines"] = lines }.ToJsonString());

        var server = ServerParse(osu);

        Assert.Multiple(() =>
        {
            Assert.That(GameMaximum(osu, []), Is.LessThan(ServerJudgedCells.Count(server.Lines, literate: false)),
                "the premise: the game drops the line the server keeps");
            Assert.That(ServerJudgedCells.ForMap(server.LyricSectionLines, server.Lines), Is.EqualTo(((int?)null, (int?)null)));
        });

        // The control: the same map without the comment marker is counted, and agrees with the game.
        string plain = osu.Replace("//", "/");
        var plainServer = ServerParse(plain);
        Assert.That(ServerJudgedCells.ForMap(plainServer.LyricSectionLines, plainServer.Lines).Cells, Is.EqualTo(GameMaximum(plain, [])));
    }
}
