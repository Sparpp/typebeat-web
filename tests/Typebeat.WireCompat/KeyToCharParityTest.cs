using System.Text;
using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using GameRomaniser = typebeat.Game.Rulesets.TypeBeat.Beatmaps.Romaniser;
using GameTypeability = typebeat.Game.Rulesets.TypeBeat.Beatmaps.Typeability;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on KEYSTROKE TO CHARACTERS (backlogs 309, 383). Since 383 the desktop types
/// what the OS COMMITTED for a press, folded onto the typing surface by
/// <see cref="TextInputFold.Fold"/>; the browser reads the same committed character as
/// <c>KeyboardEvent.key</c> and folds it with <c>foldTyped</c> in typebeat-player.js. So the pin is
/// the plain one: the same character in types the same presses out. Before 383 this test held the
/// browser's positional fallback against the desktop's three hand-written layout tables (KeyCharMap,
/// now deleted), which is why the per-layout <c>e.key</c> models below survive as input data.
///
/// <para>Two arms, both through <c>KeyToCharHarness.cjs</c> in one node call:</para>
/// <list type="bullet">
/// <item>THE FOLD: <c>foldTyped(s, punctuation)</c> against <c>TextInputFold.Fold(s, punctuation)</c>
/// for every char U+0020..U+024F (the range <c>TextInputFoldTest</c> pins against the lyric
/// normalizer) and U+1E00..U+1EFF, every special letter and Latin extra, the typographic marks, an
/// inert set, a few decomposed and multi-char commits, each under BOTH punctuation arms.</item>
/// <item>THE KEYSTROKE: <c>keyToChars</c> for what US, German, French and Russian layouts report per
/// position (both Shift states, and the composed vowel after a dead key), against the desktop's rule
/// for the same press: the digit row and keypad positional without Literate
/// (<c>TypingKeys.TryPositionalDigit</c>, which is internal to the game, so restated here), else
/// the fold of the committed character, else for the spacebar its own ' '. /play is never Literate,
/// so this arm folds without punctuation.</item>
/// </list>
/// </summary>
[TestFixture]
public class KeyToCharParityTest
{
    private sealed record Position(string Code, string Plain, string Shifted);

    private sealed record Layout(string Name, Position[] Positions, char[] DeadMarks);

    private sealed record KeyRow(string Name, string KeyValue, string Code, bool Shift, bool Dead, string Expected);

    #region The OS layouts, as e.key reports them

    private const string dead = "Dead";

    private static IEnumerable<Position> Letters(Func<char, string> legend)
    {
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string plain = legend(c);
            yield return new Position("Key" + c, plain, plain.ToUpperInvariant());
        }
    }

    private static IEnumerable<Position> DigitRow(string shifted)
    {
        for (int d = 0; d <= 9; d++)
        {
            // shifted is listed 1..9 then 0, the way the keycaps run.
            int index = d == 0 ? 9 : d - 1;
            string s = shifted.Substring(index, 1);
            yield return new Position("Digit" + d, d.ToString(), s);
        }
    }

    private static readonly string[] numpadNavigation = ["Insert", "End", "ArrowDown", "PageDown", "ArrowLeft", "Clear", "ArrowRight", "Home", "ArrowUp", "PageUp"];

    private static IEnumerable<Position> Numpad()
    {
        // Shift on a keypad digit is Num Lock off on Windows: the navigation key.
        for (int d = 0; d <= 9; d++)
            yield return new Position("Numpad" + d, d.ToString(), numpadNavigation[d]);
    }

    private static readonly Position space = new Position("Space", " ", " ");

    private static Layout Us() => new Layout("US",
    [
        .. Letters(c => char.ToLowerInvariant(c).ToString()),
        .. DigitRow("!@#$%^&*()"),
        .. Numpad(),
        new Position("Backquote", "`", "~"),
        new Position("Minus", "-", "_"),
        new Position("Equal", "=", "+"),
        new Position("BracketLeft", "[", "{"),
        new Position("BracketRight", "]", "}"),
        new Position("Backslash", "\\", "|"),
        new Position("Semicolon", ";", ":"),
        new Position("Quote", "'", "\""),
        new Position("Comma", ",", "<"),
        new Position("Period", ".", ">"),
        new Position("Slash", "/", "?"),
        space,
    ], []);

    private static Layout German() => new Layout("DE",
    [
        .. Letters(c => c switch { 'Y' => "z", 'Z' => "y", _ => char.ToLowerInvariant(c).ToString() }),
        .. DigitRow("!\"§$%&/()="),
        .. Numpad(),
        new Position("Backquote", dead, "°"),
        new Position("Minus", "ß", "?"),
        new Position("Equal", dead, dead),
        new Position("BracketLeft", "ü", "Ü"),
        new Position("BracketRight", "+", "*"),
        new Position("Backslash", "#", "'"),
        new Position("Semicolon", "ö", "Ö"),
        new Position("Quote", "ä", "Ä"),
        new Position("Comma", ",", ";"),
        new Position("Period", ".", ":"),
        new Position("Slash", "-", "_"),
        new Position("IntlBackslash", "<", ">"),
        space,
    ], ['̂', '́', '̀']); // ^ on Backquote, acute and grave on Equal

    private static Layout French() => new Layout("FR",
    [
        .. Letters(c => c switch
        {
            'Q' => "a",
            'A' => "q",
            'W' => "z",
            'Z' => "w",
            'M' => ",",
            _ => char.ToLowerInvariant(c).ToString(),
        }).Select(p => p.Code == "KeyM" ? p with { Shifted = "?" } : p),
        // The digit row is reversed: the marks and accented letters unshifted, the digits shifted.
        new Position("Digit1", "&", "1"),
        new Position("Digit2", "é", "2"),
        new Position("Digit3", "\"", "3"),
        new Position("Digit4", "'", "4"),
        new Position("Digit5", "(", "5"),
        new Position("Digit6", "-", "6"),
        new Position("Digit7", "è", "7"),
        new Position("Digit8", "_", "8"),
        new Position("Digit9", "ç", "9"),
        new Position("Digit0", "à", "0"),
        .. Numpad(),
        new Position("Backquote", "²", "Unidentified"),
        new Position("Minus", ")", "°"),
        new Position("Equal", "=", "+"),
        new Position("BracketLeft", dead, dead),
        new Position("BracketRight", "$", "£"),
        new Position("Backslash", "*", "µ"),
        new Position("Semicolon", "m", "M"),
        new Position("Quote", "ù", "%"),
        new Position("Comma", ";", "."),
        new Position("Period", ":", "/"),
        new Position("Slash", "!", "§"),
        new Position("IntlBackslash", "<", ">"),
        space,
    ], ['̂', '̈']); // circumflex and diaeresis, both on BracketLeft

    private static Layout Russian() => new Layout("RU",
    [
        .. Letters(c => "фисвуапршолдьтщзйкыегмцчня".Substring(c - 'A', 1)),
        .. DigitRow("!\"№;%:?*()"),
        .. Numpad(),
        new Position("Backquote", "ё", "Ё"),
        new Position("Minus", "-", "_"),
        new Position("Equal", "=", "+"),
        new Position("BracketLeft", "х", "Х"),
        new Position("BracketRight", "ъ", "Ъ"),
        new Position("Backslash", "\\", "/"),
        new Position("Semicolon", "ж", "Ж"),
        new Position("Quote", "э", "Э"),
        new Position("Comma", "б", "Б"),
        new Position("Period", "ю", "Ю"),
        new Position("Slash", ".", ","),
        space,
    ], []);

    #endregion

    /// <summary>
    /// What a keydown reports right after a dead key: the composed character when the mark
    /// composes with this legend, otherwise the legend itself (a dead key followed by a key it
    /// cannot compose with reports that key's own character and emits both).
    /// </summary>
    private static IEnumerable<string> AfterDead(string legend, char[] marks)
    {
        if (legend.Length != 1 || legend == dead)
        {
            yield return legend;
            yield break;
        }

        bool composedAny = false;

        foreach (char mark in marks)
        {
            string composed = (legend + mark).Normalize(NormalizationForm.FormC);

            if (composed.Length == 1)
            {
                composedAny = true;
                yield return composed;
            }
        }

        if (!composedAny)
            yield return legend;
    }

    /// <summary>
    /// The desktop's answer for one press on the non-Literate surface. A legend longer than one
    /// char is a NAMED key value ('Dead', 'End', 'Unidentified'): the OS commits nothing for it.
    /// </summary>
    private static string DesktopTypes(string code, string legend)
    {
        // TypingKeys.TryPositionalDigit (typebeat.Game.Rulesets.TypeBeat/UI/TypingKeys.cs).
        if (code.StartsWith("Digit", StringComparison.Ordinal) || code.StartsWith("Numpad", StringComparison.Ordinal))
            return code[^1].ToString();

        string? committed = legend.Length == 1 ? legend : null;

        if (committed != null)
            return string.Concat(TextInputFold.Fold(committed, false));

        // PressPlan.Space with no commit: the spacebar types its space.
        return code == "Space" ? " " : "";
    }

    private static List<KeyRow> KeyRows()
    {
        var rows = new List<KeyRow>();

        foreach (var layout in new[] { Us(), German(), French(), Russian() })
        {
            foreach (var pos in layout.Positions)
            {
                foreach (bool shift in new[] { false, true })
                {
                    string legend = shift ? pos.Shifted : pos.Plain;

                    rows.Add(new KeyRow(layout.Name, legend, pos.Code, shift, false, DesktopTypes(pos.Code, legend)));

                    foreach (string afterDead in AfterDead(legend, layout.DeadMarks))
                        rows.Add(new KeyRow(layout.Name, afterDead, pos.Code, shift, true, DesktopTypes(pos.Code, afterDead)));
                }
            }
        }

        return rows;
    }

    /// <summary>The committed strings the fold arm holds, each folded under both punctuation arms.</summary>
    private static List<string> FoldInputs()
    {
        var inputs = new List<string>();

        for (int c = 0x20; c <= 0x24F; c++)
            inputs.Add(((char)c).ToString());

        for (int c = 0x1E00; c <= 0x1EFF; c++)
            inputs.Add(((char)c).ToString());

        inputs.AddRange(GameTypeability.SPECIAL_LETTERS.Keys.Select(c => c.ToString()));
        inputs.AddRange(GameRomaniser.LATIN_EXTRAS.Keys.Select(c => c.ToString()));

        // The typographic marks Typeability.FoldTypographic folds.
        inputs.AddRange(new[] { "‘", "’", "‚", "′", "“", "”", "„", "″", "–", "—", "―", "−", " ", " ", " " });

        // The inert set: symbols, a non-Latin script, controls, whitespace that is not a space,
        // surrogate halves and a whole pair, a dead key's bare marks.
        inputs.AddRange(new[]
        {
            "€", "£", "¤", "&", "@", "`", "§", "°", "²", "µ", "ф", "Ф", "ё", "ς", "Σ", "ب", "あ", "中",
            "\u0000", "\u001F", "\u007F", "\u0085", "\t", "\n", "\r", " ", "　",
            "\uD83D", "\uDE00", "😀", "́", "̈", "^", "~", "ˆ", "¨", "´",
        });

        // Multi-char commits: decomposed letters, a dead key's uncomposed pair, words.
        inputs.AddRange(new[]
        {
            "", "é", "É", "ä́", "^q", "ße", "naïve", "Straße", "œuvre", "Ǿ", "ǽ", "ĲSSEL",
            "it’s", "rock–n–roll", "a b", "€5", "q\uD83D",
        });

        return inputs;
    }

    [Test]
    public void FoldAndKeystrokes_AgreeWithTextInputFold()
    {
        var foldInputs = FoldInputs();
        var foldRows = foldInputs.SelectMany(s => new[] { (s, false), (s, true) }).ToList();
        var keyRows = KeyRows();

        string path = Path.Combine(Path.GetTempPath(), $"keytochar-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            fold = foldRows.Select(r => new { units = r.s.Select(c => (int)c).ToArray(), punctuation = r.Item2 }),
            keys = keyRows.Select(r => new { key = r.KeyValue, code = r.Code }),
        }), new UTF8Encoding(false));

        JsonElement answers;

        try
        {
            answers = NodeHarness.Run("KeyToCharHarness.cjs", path);
        }
        finally
        {
            File.Delete(path);
        }

        var fold = answers.GetProperty("fold");
        var keys = answers.GetProperty("keys");

        Assert.That(fold.GetArrayLength(), Is.EqualTo(foldRows.Count));
        Assert.That(keys.GetArrayLength(), Is.EqualTo(keyRows.Count));

        var mismatches = new List<string>();

        for (int i = 0; i < foldRows.Count; i++)
        {
            var (s, punctuation) = foldRows[i];
            string expected = string.Concat(TextInputFold.Fold(s, punctuation));
            string got = fold[i].GetString()!;

            if (got != expected)
                mismatches.Add($"fold [{string.Join(' ', s.Select(c => $"U+{(int)c:X4}"))}] punctuation={punctuation}: desktop \"{expected}\", browser \"{got}\"");
        }

        for (int i = 0; i < keyRows.Count; i++)
        {
            var r = keyRows[i];
            string got = keys[i].GetString()!;

            if (got != r.Expected)
                mismatches.Add($"{r.Name} {r.Code} key='{r.KeyValue}' shift={r.Shift} afterDead={r.Dead}: desktop \"{r.Expected}\", browser \"{got}\"");
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));

        // Non-vacuity: the tables really reach every rule arm of the fold and of the keystroke, on
        // the DESKTOP side (so a fold that quietly went inert would fail here, not pass both sides).
        string Desktop(string s, bool punctuation) => string.Concat(TextInputFold.Fold(s, punctuation));

        Assert.Multiple(() =>
        {
            Assert.That(foldRows, Has.Count.GreaterThan(1800));
            Assert.That(Desktop("é", false), Is.EqualTo("e"));
            Assert.That(Desktop("Ç", false), Is.EqualTo("C"));
            Assert.That(Desktop("é", false), Is.EqualTo("e"));
            Assert.That(Desktop("ß", false), Is.EqualTo("ss"));
            Assert.That(Desktop("þ", false), Is.EqualTo("th"));
            Assert.That(Desktop("ĳ", false), Is.EqualTo("ij"));
            Assert.That(Desktop("’", true), Is.EqualTo("'"));
            Assert.That(Desktop("’", false), Is.Empty);
            Assert.That(Desktop(" ", false), Is.EqualTo(" "));
            Assert.That(Desktop(",", true), Is.EqualTo(","));
            Assert.That(Desktop(",", false), Is.Empty);
            Assert.That(Desktop("€", true), Is.Empty);
            Assert.That(Desktop("ф", true), Is.Empty);

            Assert.That(keyRows, Has.Count.GreaterThan(600));
            Assert.That(keyRows.Any(r => r.Name == "US" && r.Code == "Digit1" && r.Shift && r.KeyValue == "!" && r.Expected == "1"));
            Assert.That(keyRows.Any(r => r.Name == "FR" && r.Code == "Digit2" && !r.Shift && r.KeyValue == "é" && r.Expected == "2"));
            Assert.That(keyRows.Any(r => r.Name == "FR" && r.Code == "KeyQ" && r.Dead && r.KeyValue == "â" && r.Expected == "a"));
            Assert.That(keyRows.Any(r => r.Name == "DE" && r.Code == "KeyE" && r.Dead && r.KeyValue == "é" && r.Expected == "e"));
            Assert.That(keyRows.Any(r => r.Name == "FR" && r.Code == "KeyM" && r.KeyValue == "," && r.Expected == ""));
            Assert.That(keyRows.Any(r => r.Name == "RU" && r.Code == "KeyA" && r.Shift && r.KeyValue == "Ф" && r.Expected == ""));
            Assert.That(keyRows.Any(r => r.Name == "DE" && r.Code == "Semicolon" && r.KeyValue == "ö" && r.Expected == "o"));
            Assert.That(keyRows.Any(r => r.Name == "DE" && r.Code == "Minus" && r.KeyValue == "ß" && r.Expected == "ss"));
            Assert.That(keyRows.Any(r => r.Name == "FR" && r.Code == "BracketLeft" && r.KeyValue == dead && r.Expected == ""));
            Assert.That(keyRows.Any(r => r.Code == "Space" && r.Expected == " "));
        });
    }
}
