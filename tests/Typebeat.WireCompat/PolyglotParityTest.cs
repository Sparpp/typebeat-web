using System.Text;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using Typebeat.Tools.ScoreRecalc;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPp = typebeat.Game.Rulesets.TypeBeat.Scoring.PerformancePoints;
using ClientTypingLine = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using ClientWordPause = typebeat.Game.Rulesets.TypeBeat.Beatmaps.WordPause;
using ServerArm = Typebeat.Web.Packages.Lyrics.LyricDifficulty.JudgementArm;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;
using ServerSummary = Typebeat.Web.Packages.Lyrics.PolyglotSummary;
using ServerTypingLine = Typebeat.Web.Packages.Lyrics.TypingLine;

namespace Typebeat.WireCompat;

/// <summary>
/// PR 43's cross-repo pin for POLYGLOT, which became a RANKED mod (1.0x, local only on Chinese and
/// Korean maps): one .osu, written by the game's own encoder, read by the game's decoder and by
/// the server's Polyglot parse, then held through every layer the server mirrors.
///
/// <list type="bullet">
/// <item>THE PARSE: the server's <c>ParsedDifficulty.PolyglotLines</c> reads the same originals,
/// cuts, rests and cut links as the game's TimingJsonLoader.</item>
/// <item>THE FLATTENING: the server's reduced <c>TypingLine.ForMods</c> yields the game's cells
/// (char, judged, target, Japanese end target, minimum romaji inputs) on both streams and both target
/// eras, which is where the Japanese dictionary reading (Kawazu over IPAdic, the same package on both
/// sides) reaches a count.</item>
/// <item>THE JUDGEMENT STREAM: the server's per-map counts are what the game's score processor
/// reports for a Polyglot play (its maximum, its full combo).</item>
/// <item>THE RATING: the Polyglot cells of the stored matrix are the game's own
/// <c>PerformancePoints.StarsFor</c> / <c>DifficultCharactersFor</c> with the mod in the stack, in all
/// three arms, both streams and all three rates, and the stored played duration is the difficulty
/// calculator's.</item>
/// </list>
/// </summary>
[TestFixture]
public class PolyglotParityTest
{
    private static ClientUnit unit(string text, double start, double end, string? original = null, double[]? boundaries = null,
                                   int[]? splits = null, ClientWordPause[]? pauses = null)
        => new ClientUnit
        {
            Text = text, StartTime = start, EndTime = end, Source = TimingSource.Explicit, Original = original,
            SyllableBoundaries = boundaries ?? [], SyllableSplits = splits ?? [], Pauses = pauses ?? [],
        };

    private static ClientLine line(string raw, double start, double end, string? original, params ClientUnit[] units)
        => new ClientLine { RawText = raw, Original = original, StartTime = start, EndTime = end, SingEndTime = units[^1].EndTime, Units = units };

    /// <summary>A Russian map: Cyrillic originals per word, capitals and punctuation, a subdivided and a paused word.</summary>
    private static ClientLine[] russian() =>
    [
        line("Privet, mir!", 1000, 3000, "Привет, мир!",
            unit("Privet,", 1000, 1900, "Привет,"), unit("mir!", 1900, 2800, "мир!")),
        line("Rastsvetali yabloni i grushi", 3000, 7000, null,
            unit("Rastsvetali", 3000, 4400, "Расцветали", boundaries: [3500, 4000], splits: [4, 7]),
            unit("yabloni", 4400, 5400, "яблони", pauses: [new ClientWordPause(4700, 4900, 3)]),
            unit("i", 5400, 5600, "и"),
            unit("grushi", 5600, 6800, "груши")),
        line("hello world", 7000, 9000, null, unit("hello", 7000, 7900), unit("world", 7900, 8800)),
        line("Shchuka, Yozh!", 9000, 11000, null, unit("Shchuka,", 9000, 9900, "Щука,"), unit("Yozh!", 9900, 10800, "Ёж!")),
    ];

    /// <summary>A Latin map with national letters (French, German, Polish forms) and a typographic apostrophe.</summary>
    private static ClientLine[] latin() =>
    [
        line("Ca va bien", 1000, 3000, null, unit("Ca", 1000, 1500, "Ça"), unit("va", 1500, 2000), unit("bien", 2000, 2800)),
        line("Grusse aus Koln", 3000, 5000, "Grüße aus Köln",
            unit("Grusse", 3000, 3800, "Grüße"), unit("aus", 3800, 4200), unit("Koln", 4200, 4800, "Köln")),
        line("Zolw ma'te", 5000, 7000, null, unit("Zolw", 5000, 5800, "Żółw", boundaries: [5400], splits: [2]), unit("ma'te", 5800, 6800, "ma’té")),
        line("plain line here", 7000, 9000, null, unit("plain", 7000, 7600), unit("line", 7600, 8200), unit("here", 8200, 8800)),
    ];

    /// <summary>
    /// A Japanese map: word originals with kanji and kana, a line whose original lives on the LINE only
    /// (the dictionary's own segmentation pairs it with the typed words), a small tsu, a long vowel,
    /// a subdivided word, a paused word, a mapper reading that differs from the dictionary, a
    /// spelling only the dictionary can read and a Latin word inside a Japanese line.
    /// </summary>
    private static ClientLine[] japanese() =>
    [
        line("kimi ga suki", 1000, 3200, "君がすき",
            unit("kimi", 1000, 1700, "君"), unit("ga", 1700, 2100, "が"), unit("suki", 2100, 3000, "すき")),
        line("toukyou no sora wa aoi", 3200, 7000, "東京の空は青い",
            unit("toukyou", 3200, 4300), unit("no", 4300, 4600), unit("sora", 4600, 5300), unit("wa", 5300, 5600), unit("aoi", 5600, 6600)),
        line("kitto ashita mo", 7000, 10000, null,
            unit("kitto", 7000, 7900, "きっと", boundaries: [7400], splits: [2]),
            unit("ashita", 7900, 9000, "明日", pauses: [new ClientWordPause(8300, 8500, 2)]),
            unit("mo", 9000, 9500, "も")),
        line("ra-men love jigoku", 10000, 13000, null,
            unit("ra-men", 10000, 11000, "ラーメン"), unit("love", 11000, 11800), unit("jigoku", 11800, 12800, "地獄")),
        line("hitori de aruku", 13000, 16000, "一人で歩く",
            unit("hitori", 13000, 13900, "一人"), unit("de", 13900, 14300, "で"), unit("aruku", 14300, 15500, "歩く")),
        // A mapper spelling the input grammar cannot decode ("kiss" for 愛): only the DICTIONARY can
        // read this character, so the minimum inputs here come from Kawazu alone.
        line("kiss wa yume", 16000, 19000, null,
            unit("kiss", 16000, 17000, "愛"), unit("wa", 17000, 17400, "は"), unit("yume", 17400, 18500, "夢")),
    ];

    private static (string Name, ClientLine[] Lines, BeatmapLanguage Language)[] fixtures() =>
    [
        ("russian", russian(), BeatmapLanguage.Russian),
        ("latin", latin(), BeatmapLanguage.Other),
        ("japanese", japanese(), BeatmapLanguage.Japanese),
    ];

    private static string encode(ClientLine[] lines, BeatmapLanguage language)
    {
        var beatmap = new Beatmap();
        beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        beatmap.Metadata.Artist = "Artist";
        beatmap.Metadata.Title = "Title";
        beatmap.Metadata.AudioFile = "audio.mp3";
        beatmap.Metadata.Author.Username = "mapper";
        beatmap.Metadata.Language = language;

        for (int i = 0; i < lines.Length; i++)
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

        return sb.ToString();
    }

    private static Beatmap clientDecode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    private static IReadOnlyList<ClientLine> clientLines(Beatmap beatmap)
        => beatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToArray();

    private static ParsedDifficulty serverParse(string osu) => BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

    private static IBeatmap playable(Beatmap decoded, IReadOnlyList<Mod> mods)
        => new FlatWorkingBeatmap(decoded).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, mods);

    private static string? clientLanguage(Beatmap decoded) => TypeBeatModPolyglot.LanguageFor(playable(decoded, []));

    [TestCaseSource(nameof(names))]
    public void TheServersPolyglotParseIsTheGamesParse(string name)
    {
        var (_, lines, language) = fixtures().Single(f => f.Name == name);
        string osu = encode(lines, language);
        var client = clientLines(clientDecode(osu));
        var server = serverParse(osu).PolyglotLines;

        Assert.That(server, Has.Count.EqualTo(client.Count), "line count");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < client.Count; i++)
            {
                Assert.That(server[i].RawText, Is.EqualTo(client[i].RawText), $"line {i} text");
                Assert.That(server[i].Original, Is.EqualTo(client[i].Original), $"line {i} original");
                Assert.That(server[i].UnromanisedWords.Select(w => (w.Position, w.Original, w.StartTime, w.EndTime)),
                    Is.EqualTo(client[i].UnromanisedWords.Select(w => (w.Position, w.Original, w.StartTime, w.EndTime))), $"line {i} unromanised");
                Assert.That(server[i].Units.Select(describe), Is.EqualTo(client[i].Units.Select(describe)), $"line {i} units");
            }
        });
    }

    private static string describe(ClientUnit u)
        => $"{u.Text}|{u.StartTime}|{u.EndTime}|{u.Original}|{string.Join(",", u.SyllableBoundaries)}|{string.Join(",", u.SyllableSplits)}|"
           + $"{string.Join(",", u.Pauses.Select(p => $"{p.StartTime}-{p.EndTime}@{p.SplitChar}"))}|{string.Join(",", u.OriginalSplits.Select(o => $"{o.TextIndex}>{o.OriginalIndex}"))}|{u.Source}|{u.Confidence}";

    private static string describe(Typebeat.Web.Packages.Lyrics.TimedUnit u)
        => $"{u.Text}|{u.StartTime}|{u.EndTime}|{u.Original}|{string.Join(",", u.SyllableBoundaries)}|{string.Join(",", u.SyllableSplits)}|"
           + $"{string.Join(",", u.Pauses.Select(p => $"{p.StartTime}-{p.EndTime}@{p.SplitChar}"))}|{string.Join(",", u.OriginalSplits.Select(o => $"{o.TextIndex}>{o.OriginalIndex}"))}|{u.Source}|{u.Confidence}";

    [TestCaseSource(nameof(names))]
    public void TheServersPolyglotCellsAreTheGamesCells(string name)
    {
        var (_, lines, language) = fixtures().Single(f => f.Name == name);
        string osu = encode(lines, language);
        var decoded = clientDecode(osu);
        var client = clientLines(decoded);
        var server = serverParse(osu);
        string? lang = clientLanguage(decoded);

        Assert.That(server.Ratings.Polyglot?.Language, Is.EqualTo(lang), "both sides romanise under the same language");

        int japaneseCells = 0;

        Assert.Multiple(() =>
        {
            foreach (bool literate in new[] { false, true })
            foreach (bool aligned in new[] { false, true })
            {
                for (int i = 0; i < client.Count; i++)
                {
                    var c = ClientTypingLine.ForMods(client[i], literate, true, lang, alignSubdivisionTargets: aligned, japaneseCharacterTiming: true);
                    var s = ServerTypingLine.ForMods(server.PolyglotLines[i], literate, true, lang, alignSubdivisionTargets: aligned, japaneseCharacterTiming: true);
                    string arm = $"{name} line {i} {(literate ? "literate" : "plain")} {(aligned ? "aligned" : "legacy")}";

                    Assert.That(s.DisplayText, Is.EqualTo(c.DisplayText), $"{arm}: display text");
                    Assert.That(s.Cells.Select(x => (x.Expected, x.IsTypeable, x.TargetTime, x.EndTargetTime, x.MinimumRomajiInputs)),
                        Is.EqualTo(c.Cells.Select(x => (x.Expected, x.IsTypeable, x.TargetTime, x.EndTargetTime, x.MinimumRomajiInputs))), $"{arm}: cells");

                    japaneseCells += c.Cells.Count(x => x.MinimumRomajiInputs > 0);
                }
            }
        });

        // The premise: the Japanese fixture really reaches the input-scored path, and the others do not.
        if (name == "japanese")
            Assert.That(japaneseCells, Is.GreaterThan(0), "the Japanese fixture must carry input-scored characters");
        else
            Assert.That(japaneseCells, Is.Zero);
    }

    private static readonly string[] names = ["russian", "latin", "japanese"];

    [TestCaseSource(nameof(names))]
    public void TheStoredJudgementStreamIsWhatTheGamesProcessorCounts(string name)
    {
        var (_, lines, language) = fixtures().Single(f => f.Name == name);
        string osu = encode(lines, language);
        var decoded = clientDecode(osu);
        var summary = ServerSummary.Parse(Newtonsoft.Json.Linq.JObject.Parse(serverParse(osu).RatingsJson)["polyglot"])!;

        Assert.That(summary, Is.Not.Null, "the stored matrix carries the Polyglot summary");

        Assert.Multiple(() =>
        {
            foreach (bool literate in new[] { false, true })
            {
                Mod[] mods = literate ? [new TypeBeatModPolyglot(), new TypeBeatModLiterate()] : [new TypeBeatModPolyglot()];
                var map = playable(decoded, mods);
                var stream = summary.For(literate);
                string arm = $"{name} {(literate ? "literate" : "plain")}";

                Assert.That(stream.Combo, Is.EqualTo(TypeBeatScoreProcessor.MaximumComboFor(map)), $"{arm}: full combo");
                Assert.That(stream.Cells, Is.EqualTo(map.HitObjects.OfType<TypeBeatHitObject>().SelectMany(h => h.NestedHitObjects.OfType<TypeBeatCharObject>()).Count(c => c.Phase == 0)),
                    $"{arm}: scoring objects");

                int maximum = gameMaximum(map, mods);

                if (summary.Japanese)
                {
                    // An untouched play submits its simulated maximum: every character at its minimum inputs.
                    Assert.That(maximum, Is.EqualTo(stream.Combo), $"{arm}: the simulated maximum is the minimum-input count");
                    Assert.That(stream.MaximumInputs, Is.GreaterThanOrEqualTo(maximum), $"{arm}: the ceiling holds the honest maximum");
                    Assert.That(stream.Cells, Is.LessThan(stream.Combo), $"{arm}: input scoring really is per input");
                }
                else
                {
                    Assert.That(maximum, Is.EqualTo(stream.Cells), $"{arm}: one judgement per object");
                    Assert.That(stream.Combo, Is.EqualTo(stream.Cells), $"{arm}: combo is the object count");
                }
            }
        });
    }

    private static int gameMaximum(IBeatmap map, IReadOnlyList<Mod> mods)
    {
        var ruleset = new TypeBeatRuleset();
        var processor = new TypeBeatScoreProcessor(ruleset);
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(map);

        var info = new ScoreInfo { Ruleset = ruleset.RulesetInfo, Passed = true };
        processor.PopulateScore(info);

        return ScoringContract.CountAccuracyAffecting(WireCounts.From(info.MaximumStatistics));
    }

    [TestCaseSource(nameof(names))]
    public void ThePolyglotRatingCellsAreTheGamesOwn(string name)
    {
        var (_, lines, language) = fixtures().Single(f => f.Name == name);
        string osu = encode(lines, language);
        var decoded = clientDecode(osu);
        var client = clientLines(decoded);
        string? lang = clientLanguage(decoded);
        var matrix = BeatmapRatings.Parse(serverParse(osu).RatingsJson)!;

        Assert.That(matrix.Polyglot!.Supported, Is.True, "every fixture is a script the game rates under the mod");

        (Mod? Mod, ServerArm Arm, string Name)[] arms = [(null, ServerArm.None, "none"), (new TypeBeatModEasy(), ServerArm.Easy, "ez"), (new TypeBeatModHardRock(), ServerArm.HardRock, "hr")];
        (Mod? Mod, double Rate)[] rates = [(null, 1.0), (new TypeBeatModDoubleTime(), ClientPp.DOUBLE_TIME_BASE_RATE), (new TypeBeatModHalfTime(), ClientPp.HALF_TIME_BASE_RATE)];

        Assert.Multiple(() =>
        {
            foreach (var (armMod, serverArm, armName) in arms)
            foreach (bool literate in new[] { false, true })
            foreach (var (rateMod, rate) in rates)
            {
                var mods = new List<Mod> { new TypeBeatModPolyglot() };
                if (literate)
                    mods.Add(new TypeBeatModLiterate());
                if (armMod != null)
                    mods.Add(armMod);
                if (rateMod != null)
                    mods.Add(rateMod);

                string arm = $"{name} {armName}/{(literate ? "literate" : "plain")}/{rate:0.00}";
                var cell = matrix.TryGet(serverArm, literate, rate, polyglot: true);

                Assert.That(cell, Is.Not.Null, arm);
                Assert.That(cell!.Value.Stars, Is.EqualTo(ClientPp.StarsFor(client, mods, lang)), $"{arm}: stars");
                Assert.That(cell.Value.DifficultCharacters, Is.EqualTo(ClientPp.DifficultCharactersFor(client, mods, lang)), $"{arm}: difficult characters");
            }

            // The premise: the projection moves the rating at all (or the cells could be the base ones).
            Assert.That(matrix.TryGet(ServerArm.None, false, 1.0, polyglot: true)!.Value.Stars,
                Is.Not.EqualTo(matrix.TryGet(ServerArm.None, false, 1.0)!.Value.Stars), $"{name}: the Polyglot stream is its own rating");
        });
    }

    [TestCaseSource(nameof(names))]
    public void ThePolyglotPlayedDurationIsTheDifficultyCalculatorsOwn(string name)
    {
        var (_, lines, language) = fixtures().Single(f => f.Name == name);
        string osu = encode(lines, language);
        var decoded = clientDecode(osu);
        var summary = BeatmapRatings.Parse(serverParse(osu).RatingsJson)!.Polyglot!;

        Assert.Multiple(() =>
        {
            foreach (bool literate in new[] { false, true })
            {
                Mod[] mods = literate ? [new TypeBeatModPolyglot(), new TypeBeatModLiterate()] : [new TypeBeatModPolyglot()];
                var attributes = (TypeBeatDifficultyAttributes)new TypeBeatRuleset().CreateDifficultyCalculator(new FlatWorkingBeatmap(decoded)).Calculate(mods);

                Assert.That(summary.For(literate).PlayedDurationS, Is.EqualTo(attributes.PlayedDurationSeconds), $"{name} {(literate ? "literate" : "plain")}");
                Assert.That(summary.For(literate).Combo, Is.EqualTo(attributes.MaxCombo), $"{name} {(literate ? "literate" : "plain")}: the calculator's combo");
            }
        });
    }
}
