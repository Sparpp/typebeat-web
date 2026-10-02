using System.Text;
using System.Text.Json;
using osuTK.Input;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Input;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on KEYSTROKE TO CHARACTER (backlog 309). The desktop maps a PHYSICAL key
/// position through <see cref="KeyCharMap.TryMap(Key, KeyboardLayout, bool, bool, bool, out char)"/>
/// under the player's <see cref="KeyboardLayout"/> setting; the browser reads the OS layout's own
/// <c>KeyboardEvent.key</c> and borrows the positional model only where that has nothing typeable to
/// say (<c>keyToChar</c> in typebeat-player.js). The two meet wherever the OS layout IS the layout
/// the desktop setting describes, and that is what this holds: for US QWERTY, German QWERTZ and
/// French AZERTY (each against its own setting) and Russian ЙЦУКЕН (against the default QWERTY
/// setting, the non-Latin case), every key position, both Shift states, and both "the previous
/// keydown was a dead key" states, with the composed vowel a dead key really produces.
///
/// <para>The expected side is GENERATED from <see cref="KeyCharMap"/> with Literate off (the only
/// surface /play plays), Caps Lock off. The input side is a model of what each OS layout reports
/// as <c>e.key</c> per position, which is the one hand-written part: it is layout data, not
/// behaviour. Every row runs through <c>KeyToCharHarness.cjs</c> in one node call.</para>
///
/// <para>Deliberately NOT covered: a Latin OS layout the desktop has no setting for (Dvorak,
/// Colemak), and an OS layout that disagrees with the setting. There the browser follows the OS
/// layout on purpose, and the desktop is the one typing letters the keycaps do not show.</para>
/// </summary>
[TestFixture]
public class KeyToCharParityTest
{
    private sealed record Position(string Code, Key Key, string Plain, string Shifted);

    private sealed record Layout(string Name, KeyboardLayout Setting, Position[] Positions, char[] DeadMarks);

    private sealed record Row(string Name, string KeyValue, string Code, bool Shift, bool Dead, char? Expected);

    #region The OS layouts, as e.key reports them

    private const string dead = "Dead";

    private static IEnumerable<Position> Letters(Func<char, string> legend)
    {
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string plain = legend(c);
            yield return new Position("Key" + c, Key.A + (c - 'A'), plain, plain.ToUpperInvariant());
        }
    }

    private static IEnumerable<Position> DigitRow(string shifted)
    {
        for (int d = 0; d <= 9; d++)
        {
            // shifted is listed 1..9 then 0, the way the keycaps run.
            int index = d == 0 ? 9 : d - 1;
            string s = shifted.Substring(index, 1);
            yield return new Position("Digit" + d, Key.Number0 + d, d.ToString(), s);
        }
    }

    private static readonly string[] numpadNavigation = ["Insert", "End", "ArrowDown", "PageDown", "ArrowLeft", "Clear", "ArrowRight", "Home", "ArrowUp", "PageUp"];

    private static IEnumerable<Position> Numpad()
    {
        // Shift on a keypad digit is Num Lock off on Windows: the navigation key.
        for (int d = 0; d <= 9; d++)
            yield return new Position("Numpad" + d, Key.Keypad0 + d, d.ToString(), numpadNavigation[d]);
    }

    private static Layout Us() => new Layout("US", KeyboardLayout.Qwerty,
    [
        .. Letters(c => char.ToLowerInvariant(c).ToString()),
        .. DigitRow("!@#$%^&*()"),
        .. Numpad(),
        new Position("Backquote", Key.Tilde, "`", "~"),
        new Position("Minus", Key.Minus, "-", "_"),
        new Position("Equal", Key.Plus, "=", "+"),
        new Position("BracketLeft", Key.BracketLeft, "[", "{"),
        new Position("BracketRight", Key.BracketRight, "]", "}"),
        new Position("Backslash", Key.BackSlash, "\\", "|"),
        new Position("Semicolon", Key.Semicolon, ";", ":"),
        new Position("Quote", Key.Quote, "'", "\""),
        new Position("Comma", Key.Comma, ",", "<"),
        new Position("Period", Key.Period, ".", ">"),
        new Position("Slash", Key.Slash, "/", "?"),
    ], []);

    private static Layout German() => new Layout("DE", KeyboardLayout.Qwertz,
    [
        .. Letters(c => c switch { 'Y' => "z", 'Z' => "y", _ => char.ToLowerInvariant(c).ToString() }),
        .. DigitRow("!\"§$%&/()="),
        .. Numpad(),
        new Position("Backquote", Key.Tilde, dead, "°"),
        new Position("Minus", Key.Minus, "ß", "?"),
        new Position("Equal", Key.Plus, dead, dead),
        new Position("BracketLeft", Key.BracketLeft, "ü", "Ü"),
        new Position("BracketRight", Key.BracketRight, "+", "*"),
        new Position("Backslash", Key.BackSlash, "#", "'"),
        new Position("Semicolon", Key.Semicolon, "ö", "Ö"),
        new Position("Quote", Key.Quote, "ä", "Ä"),
        new Position("Comma", Key.Comma, ",", ";"),
        new Position("Period", Key.Period, ".", ":"),
        new Position("Slash", Key.Slash, "-", "_"),
        new Position("IntlBackslash", Key.NonUSBackSlash, "<", ">"),
    ], ['̂', '́', '̀']); // ^ on Backquote, acute and grave on Equal

    private static Layout French() => new Layout("FR", KeyboardLayout.Azerty,
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
        new Position("Digit1", Key.Number1, "&", "1"),
        new Position("Digit2", Key.Number2, "é", "2"),
        new Position("Digit3", Key.Number3, "\"", "3"),
        new Position("Digit4", Key.Number4, "'", "4"),
        new Position("Digit5", Key.Number5, "(", "5"),
        new Position("Digit6", Key.Number6, "-", "6"),
        new Position("Digit7", Key.Number7, "è", "7"),
        new Position("Digit8", Key.Number8, "_", "8"),
        new Position("Digit9", Key.Number9, "ç", "9"),
        new Position("Digit0", Key.Number0, "à", "0"),
        .. Numpad(),
        new Position("Backquote", Key.Tilde, "²", "Unidentified"),
        new Position("Minus", Key.Minus, ")", "°"),
        new Position("Equal", Key.Plus, "=", "+"),
        new Position("BracketLeft", Key.BracketLeft, dead, dead),
        new Position("BracketRight", Key.BracketRight, "$", "£"),
        new Position("Backslash", Key.BackSlash, "*", "µ"),
        new Position("Semicolon", Key.Semicolon, "m", "M"),
        new Position("Quote", Key.Quote, "ù", "%"),
        new Position("Comma", Key.Comma, ";", "."),
        new Position("Period", Key.Period, ":", "/"),
        new Position("Slash", Key.Slash, "!", "§"),
        new Position("IntlBackslash", Key.NonUSBackSlash, "<", ">"),
    ], ['̂', '̈']); // circumflex and diaeresis, both on BracketLeft

    private static Layout Russian() => new Layout("RU", KeyboardLayout.Qwerty,
    [
        .. Letters(c => "фисвуапршолдьтщзйкыегмцчня".Substring(c - 'A', 1)),
        .. DigitRow("!\"№;%:?*()"),
        .. Numpad(),
        new Position("Backquote", Key.Tilde, "ё", "Ё"),
        new Position("Minus", Key.Minus, "-", "_"),
        new Position("Equal", Key.Plus, "=", "+"),
        new Position("BracketLeft", Key.BracketLeft, "х", "Х"),
        new Position("BracketRight", Key.BracketRight, "ъ", "Ъ"),
        new Position("Backslash", Key.BackSlash, "\\", "/"),
        new Position("Semicolon", Key.Semicolon, "ж", "Ж"),
        new Position("Quote", Key.Quote, "э", "Э"),
        new Position("Comma", Key.Comma, "б", "Б"),
        new Position("Period", Key.Period, "ю", "Ю"),
        new Position("Slash", Key.Slash, ".", ","),
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

    private static List<Row> Generate()
    {
        var rows = new List<Row>();

        foreach (var layout in new[] { Us(), German(), French(), Russian() })
        {
            foreach (var pos in layout.Positions)
            {
                foreach (bool shift in new[] { false, true })
                {
                    char? expected = KeyCharMap.TryMap(pos.Key, layout.Setting, shift, false, false, out char c) ? c : null;
                    string legend = shift ? pos.Shifted : pos.Plain;

                    rows.Add(new Row(layout.Name, legend, pos.Code, shift, false, expected));

                    foreach (string afterDead in AfterDead(legend, layout.DeadMarks))
                        rows.Add(new Row(layout.Name, afterDead, pos.Code, shift, true, expected));
                }
            }
        }

        return rows;
    }

    [Test]
    public void KeyToChar_AgreesWithKeyCharMapWhereverTheOsLayoutIsTheDesktopSetting()
    {
        var rows = Generate();

        string path = Path.Combine(Path.GetTempPath(), $"keytochar-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rows.Select(r => new { key = r.KeyValue, code = r.Code, shift = r.Shift, dead = r.Dead })),
            new UTF8Encoding(false));

        JsonElement answers;

        try
        {
            answers = NodeHarness.Run("KeyToCharHarness.cjs", path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.That(answers.GetArrayLength(), Is.EqualTo(rows.Count));

        var mismatches = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            var a = answers[i];
            char? got = a.ValueKind == JsonValueKind.Null ? null : a.GetString()!.Single();

            if (got != rows[i].Expected)
            {
                var r = rows[i];
                mismatches.Add($"{r.Name} {r.Code} key='{r.KeyValue}' shift={r.Shift} prevDead={r.Dead}: desktop '{r.Expected}', browser '{got}'");
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));

        // Non-vacuity: the table really reaches every rule arm, including the ones the old KEY_RE
        // dropped (Shift+digit, the Azerty digit row, a composed vowel, a Cyrillic letter).
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.GreaterThan(600));
            Assert.That(rows.Any(r => r.Name == "US" && r.Code == "Digit1" && r.Shift && r.KeyValue == "!" && r.Expected == '1'));
            Assert.That(rows.Any(r => r.Name == "FR" && r.Code == "Digit2" && !r.Shift && r.KeyValue == "é" && r.Expected == '2'));
            Assert.That(rows.Any(r => r.Name == "FR" && r.Code == "KeyQ" && r.Dead && r.KeyValue == "â" && r.Expected == 'a'));
            Assert.That(rows.Any(r => r.Name == "DE" && r.Code == "KeyE" && r.Dead && r.KeyValue == "é" && r.Expected == 'e'));
            Assert.That(rows.Any(r => r.Name == "FR" && r.Code == "KeyM" && r.KeyValue == "," && r.Expected == null));
            Assert.That(rows.Any(r => r.Name == "RU" && r.Code == "KeyA" && r.Shift && r.KeyValue == "Ф" && r.Expected == 'A'));
            Assert.That(rows.Any(r => r.Name == "DE" && r.Code == "Semicolon" && r.Dead && r.KeyValue == "ö" && r.Expected == null));
        });
    }
}
