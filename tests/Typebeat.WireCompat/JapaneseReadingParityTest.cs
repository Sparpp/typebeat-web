using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using typebeat.Game.Rulesets.TypeBeat;

namespace Typebeat.WireCompat;

/// <summary>
/// PR 43's pin for the JAPANESE READING PATH, the one piece of the Polyglot mirror that reads a
/// dictionary: a ranked Japanese Polyglot play is judged on each character's minimum accepted romaji,
/// and that minimum comes from the game's reading path (Kawazu over the LibNMeCab IPAdic dictionary,
/// then the romaji grammar), so the server runs the SAME two packages at the SAME versions and a copy
/// of the same code. This holds the two copies to each other over the game's own Japanese fixtures:
/// every Japanese literal of its Japanese, Polyglot and Original tests, the romaniser's Japanese
/// fixture, and the Mozc romaji table its wapuro test reads.
///
/// <para>Every type compared here is internal on both sides (the mirrors are verbatim, see
/// <see cref="PolyglotMirrorSourceTest"/>), so the calls go through reflection, which is what lets the
/// copies stay byte-identical rather than growing a visibility change for the test's sake.</para>
/// </summary>
[TestFixture]
public class JapaneseReadingParityTest
{
    private static readonly Assembly game = typeof(TypeBeatRuleset).Assembly;
    private static readonly Assembly server = typeof(Typebeat.Web.Packages.Lyrics.PolyglotSummary).Assembly;

    private static Type gameType(string name) => game.GetType(name, throwOnError: true)!;
    private static Type serverType(string name) => server.GetType("Typebeat.Web.Packages.Lyrics." + name, throwOnError: true)!;

    private static object? call(Type type, string method, params object?[] args)
    {
        var candidates = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();

        Assert.That(candidates, Has.Length.EqualTo(1), $"{type.Name}.{method}/{args.Length}");
        return candidates[0].Invoke(null, args);
    }

    /// <summary>A comparable rendering of a result: records print themselves, sequences join, other objects list their public properties.</summary>
    private static string describe(object? value)
    {
        switch (value)
        {
            case null:
                return "null";

            case string s:
                return s;

            case IEnumerable sequence:
                return "[" + string.Join("|", sequence.Cast<object?>().Select(describe)) + "]";
        }

        var type = value.GetType();

        if (type.IsEnum)
            return Convert.ToInt32(value).ToString();

        if (type.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType == type)
            return value.ToString()!;

        return "{" + string.Join(",", type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name)
                                           .Select(p => p.Name + "=" + describe(p.GetValue(value)))) + "}";
    }

    private static string fixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "JapaneseFixtures", name);

    private static bool isJapanese(char c) => c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or '々';

    /// <summary>Every Japanese string literal of the game's Japanese, Polyglot and Original tests, distinct, plus the romaniser fixture's sources.</summary>
    public static IEnumerable<string> Corpus()
    {
        var literal = new Regex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"");
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "JapaneseFixtures"), "*.txt"))
        {
            foreach (Match m in literal.Matches(File.ReadAllText(path)))
            {
                string raw = m.Groups[1].Value;

                // Japanese literals in these tests carry no escapes; anything that does is skipped
                // rather than half-decoded (C# escapes are not all regex escapes).
                if (raw.Contains('\\') || !raw.Any(isJapanese) || raw.Length > 80)
                    continue;

                found.Add(raw);
            }
        }

        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "RomaniserFixtures", "japanese.tsv")))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            found.Add(line.Split('\t')[1]);
        }

        return found;
    }

    [Test]
    public void TheCorpusIsTheGamesJapaneseFixtures()
    {
        var corpus = Corpus().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(corpus, Has.Count.GreaterThan(100), "the game's Japanese tests carry a real corpus");
            Assert.That(corpus.Count(t => t.Any(c => c is >= '一' and <= '鿿')), Is.GreaterThan(50), "and it reaches kanji, which only the dictionary reads");
        });
    }

    [Test]
    public void BothSidesReadEveryFixtureIdentically()
    {
        Type gameReading = gameType("typebeat.Game.Rulesets.TypeBeat.Beatmaps.JapaneseReading");
        Type serverReading = serverType("JapaneseReading");
        int kanji = 0;

        Assert.Multiple(() =>
        {
            foreach (string text in Corpus())
            {
                Assert.That(describe(call(serverReading, "Read", text)), Is.EqualTo(describe(call(gameReading, "Read", text))), $"Read({text})");
                Assert.That(describe(call(serverReading, "Romanise", text, "japanese")), Is.EqualTo(describe(call(gameReading, "Romanise", text, "japanese"))), $"Romanise({text})");
                Assert.That(describe(call(serverReading, "Segment", text, "japanese")), Is.EqualTo(describe(call(gameReading, "Segment", text, "japanese"))), $"Segment({text})");

                if (text.Any(c => c is >= '一' and <= '鿿') && ((IEnumerable)call(gameReading, "Read", text)!).Cast<object>().Any())
                    kanji++;
            }
        });

        // The premise: the dictionary really answered, on both sides, for kanji the corpus carries.
        Assert.That(kanji, Is.GreaterThan(50), "the dictionary read the kanji fixtures");
    }

    [Test]
    public void BothSidesPlanEveryFixtureIdentically()
    {
        Type gamePlan = gameType("typebeat.Game.Rulesets.TypeBeat.Gameplay.JapaneseInputPlan");
        Type serverPlan = serverType("JapaneseInputPlan");
        Type gameRomaji = gameType("typebeat.Game.Rulesets.TypeBeat.Gameplay.JapaneseRomaji");
        Type serverRomaji = serverType("JapaneseRomaji");

        Assert.Multiple(() =>
        {
            foreach (string text in Corpus())
            foreach (bool characterTiming in new[] { false, true })
            {
                object gameResult = call(gamePlan, "Create", text, null, true, true, characterTiming)!;
                object serverResult = call(serverPlan, "Create", text, null, true, true, characterTiming)!;
                string arm = $"{text} (character timing {characterTiming})";

                Assert.That(describe(groups(serverResult)), Is.EqualTo(describe(groups(gameResult))), $"{arm}: groups");

                for (int i = 0; i <= text.Length; i++)
                {
                    Assert.That(readingAt(serverResult, i), Is.EqualTo(readingAt(gameResult, i)), $"{arm}: reading at {i}");
                }

                foreach (object group in ((IEnumerable)groups(gameResult)).Cast<object>())
                {
                    string reading = (string)group.GetType().GetProperty("Reading")!.GetValue(group)!;

                    Assert.That(call(serverRomaji, "MinimumInputs", reading, ""), Is.EqualTo(call(gameRomaji, "MinimumInputs", reading, "")), $"{arm}: minimum inputs of {reading}");
                    Assert.That(call(serverRomaji, "ShortestInput", reading, ""), Is.EqualTo(call(gameRomaji, "ShortestInput", reading, "")), $"{arm}: shortest input of {reading}");
                }
            }
        });

        static object groups(object plan) => plan.GetType().GetProperty("Groups", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(plan)!;

        static string? readingAt(object plan, int cell)
            => (string?)plan.GetType().GetMethod("CharacterReadingAt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(plan, [cell]);
    }

    [Test]
    public void BothGrammarsAgreeOnTheMozcTable()
    {
        Type gameRomaji = gameType("typebeat.Game.Rulesets.TypeBeat.Gameplay.JapaneseRomaji");
        Type serverRomaji = serverType("JapaneseRomaji");
        int rows = 0;

        Assert.Multiple(() =>
        {
            foreach (string line in File.ReadAllLines(fixturePath("mozc-romaji.tsv")))
            {
                string[] cols = line.Split('\t');

                if (cols.Length < 2 || cols[0].Length == 0)
                    continue;

                string romaji = cols[0], kana = cols[1];

                Assert.That(describe(call(serverRomaji, "Matches", kana, romaji, "", false)), Is.EqualTo(describe(call(gameRomaji, "Matches", kana, romaji, "", false))), $"Matches({kana}, {romaji})");
                Assert.That(describe(call(serverRomaji, "Matches", kana, romaji, "", true)), Is.EqualTo(describe(call(gameRomaji, "Matches", kana, romaji, "", true))), $"Matches({kana}, {romaji}, kana)");
                Assert.That(call(serverRomaji, "MinimumInputs", kana, ""), Is.EqualTo(call(gameRomaji, "MinimumInputs", kana, "")), $"MinimumInputs({kana})");
                Assert.That(call(serverRomaji, "ShortestInput", kana, ""), Is.EqualTo(call(gameRomaji, "ShortestInput", kana, "")), $"ShortestInput({kana})");
                Assert.That(call(serverRomaji, "ReadAuthored", romaji), Is.EqualTo(call(gameRomaji, "ReadAuthored", romaji)), $"ReadAuthored({romaji})");
                Assert.That(describe(call(serverRomaji, "InputEnds", kana, romaji, "", false)), Is.EqualTo(describe(call(gameRomaji, "InputEnds", kana, romaji, "", false))), $"InputEnds({kana}, {romaji})");
                rows++;
            }
        });

        Assert.That(rows, Is.GreaterThan(200), "the table really is the Mozc table");
    }
}
