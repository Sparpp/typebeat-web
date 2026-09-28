using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// MANUAL NEWLINES (backlog 307), held against the game's own semantics. The desktop's setting has
/// defaulted ON since PR 2 and the browser has no settings surface, so <c>typebeat-core.js</c> takes
/// it at that shipped default: a FINISHED line is the player's to close (Space, Enter, or under
/// <c>NewlineOnTypedLetter</c> the next line's own letter), neither time-driven arm hands the caret
/// on, a typed-out line is HELD to its drag cutoff, and a line handed over before its entry window
/// opens WAITS, every key swallowed, until it does.
///
/// <para>The golden values are the asserts of typebeat-osu's
/// <c>NonVisual/ManualNewlinesTest.cs</c>, case for case and in order, on the same three-line map
/// (<c>CoreFlexibleLinesHarness.cjs</c>'s <c>manualNewlines</c> section records each case as an
/// ordered trace). That is why this guard runs without a game checkout. The cross-repo half, the
/// same rules played through the game's real engine, is <c>Typebeat.WireCompat</c>'s
/// <c>EngineFuzzLiveParityTest</c> (CONFIG bits 14 and 15), <c>KeyHandlerOrderLiveParityTest</c>
/// (the router) and the other live-parity scripts, which all run this arm now.</para>
///
/// <para>Two game cases are not transcribed: <c>TheSettingIsOffOnAFreshEngine</c> is the C# property's
/// replay default, which the browser deliberately does not share (see
/// <see cref="FlexibleLinesParityTest.TheBrowserRunsTheFlexibleCaretUnconditionally"/>), and
/// <c>TheFrameBitSetsTheEngineFlag</c> is about the replay CONFIG frame, which the browser does not
/// write.</para>
/// </summary>
public class ManualNewlinesParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => JsHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement Section() => harness.Value.GetProperty("manualNewlines");

    /// <summary>
    /// The game's expected trace for every case, in the order its asserts read them. Numbers are
    /// line and caret indices, instants, or counts; strings are cell states in the browser's
    /// vocabulary.
    /// </summary>
    private static readonly Dictionary<string, (string Label, object? Value)[]> golden = new()
    {
        ["OffKeepsTheAutomaticHandOver"] =
        [
            ("line", 0), ("complete", true), ("line", 0), ("line", 1), ("caret", 0),
        ],
        ["OnParksAFinishedLineUntilTheCutoff"] =
        [
            ("line", 0), ("complete", true), ("awaiting", false), ("letter", false), ("line", 0),
            ("line", 0), ("line", 0), ("line", 0), ("nextUnsealed", 0), ("line", 0),
        ],
        ["AnEarlyNewlineLandsAndTheNextLineWaits"] =
        [
            ("space", true), ("line", 1), ("caret", 0), ("awaiting", true), ("c", false), ("x", false), ("caret", 0),
            ("awaiting", true), ("awaiting", true), ("awaiting", false), ("c", true), ("caret", 1),
        ],
        ["ATypedLetterHandsTheLineOnOnceTheWindowOpens"] =
        [
            ("c", true), ("line", 1), ("caret", 0), ("c", true), ("line", 1), ("caret", 1),
        ],
        ["ATypedLetterIsInertWithoutItsEraBit"] =
        [
            ("c", false), ("line", 0), ("caret", 2),
        ],
        ["BackspaceStepsBackUpWhileTheOldLineIsStillOpen"] =
        [
            ("space", true), ("line", 1), ("backspace", true), ("line", 0), ("caret", 2),
            ("backspace", true), ("caret", 1), ("complete", false),
        ],
        ["EnterClosesAFinishedLine"] =
        [
            ("enter", true), ("line", 1), ("awaiting", true), ("enter", true), ("line", 1), ("awaiting", false),
        ],
        ["EnterMidLineSkipsAndStillMovesOnInOnePress"] =
        [
            ("enter", true), ("line", 1), ("awaiting", true), ("enter", true), ("line", 1), ("caret", 0),
        ],
        ["TheSongHandsTheCaretOnAtThePushCutoff"] =
        [
            ("line", 0), ("line", 0), ("line", 1), ("caret", 0), ("awaiting", false), ("c", true), ("caret", 1), ("nextUnsealed", 1),
        ],
        ["ThePushWarningCountsDownToTheManualCutoff"] =
        [
            ("cutoff", 8500), ("cutoff", 8500), ("line", 1), ("cutoff", 13500),
        ],
        ["TheStepBackClosesWithTheLine"] =
        [
            ("space", true), ("line", 1), ("backspace", true), ("line", 0), ("line", 1), ("backspace", false), ("line", 1), ("caret", 0),
        ],
        ["BackspaceIntoASkippedLineLandsOnTheLastTypedCharacter"] =
        [
            ("a", true), ("enter", true), ("line", 1), ("backspace", true), ("line", 0), ("caret", 1), ("complete", false),
            ("b", true), ("complete", true), ("line", 1), ("misses", 0),
        ],
        ["TheStepBackReopensAWordTheSkipLeftPhantom"] =
        [
            ("skip", true), ("cell3", "abandoned"), ("enter", true), ("line", 1), ("backspace", true), ("line", 0), ("caret", 3),
            ("cell3", "untyped"), ("cell4", "untyped"), ("refunded", 1), ("c", true),
        ],
        ["APinnedCaretIgnoresTheSetting"] =
        [
            ("line", 0), ("space", false), ("awaiting", false), ("line", 0), ("line", 1),
        ],
    };

    /// <summary>
    /// THE FIXTURE BEFORE THE TRACES: the game's <c>threeLines()</c> map, as the browser's loader
    /// derives it. The instants the whole file hangs on are the entry window into L1 (5500) and the
    /// two drag cutoffs (8500, 13500), which need every seal grace to be 0.
    /// </summary>
    [Test]
    public void TheFixtureIsTheGamesThreeLineMap()
    {
        var fixture = Section().GetProperty("fixture");
        var lines = fixture.GetProperty("lines");

        Assert.Multiple(() =>
        {
            Assert.That(fixture.GetProperty("entryOpensAt").GetDouble(), Is.EqualTo(5500), "L1's 7000 activation less the grace");
            Assert.That(lines.GetArrayLength(), Is.EqualTo(3));

            double[][] windows = [[1000, 7000], [7000, 12000], [12000, 17000]];
            double[][] targets = [[1000, 1500], [8000, 8500], [13000, 13500]];

            for (int i = 0; i < 3; i++)
            {
                Assert.That(lines[i].GetProperty("activationTime").GetDouble(), Is.EqualTo(windows[i][0]), $"[{i}]: activation");
                Assert.That(lines[i].GetProperty("endTime").GetDouble(), Is.EqualTo(windows[i][1]), $"[{i}]: end");
                Assert.That(lines[i].GetProperty("sealGraceMs").GetDouble(), Is.Zero, $"[{i}]: seal grace");
                Assert.That(lines[i].GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("target").GetDouble()),
                    Is.EqualTo(targets[i]), $"[{i}]: targets");
            }
        });
    }

    /// <summary>
    /// Every transcribed case, trace for trace. One test per case so a divergence names the rule it
    /// broke.
    /// </summary>
    [TestCaseSource(nameof(CaseNames))]
    public void TheBrowserAgreesWithTheGamesCase(string name)
    {
        var trace = Section().GetProperty("cases").GetProperty(name);
        var expected = golden[name];

        Assert.That(trace.GetArrayLength(), Is.EqualTo(expected.Length), $"{name}: the trace has a different number of observations");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < expected.Length; i++)
            {
                var (label, value) = expected[i];
                var entry = trace[i];
                string where = $"{name}[{i}] {label}";

                Assert.That(entry[0].GetString(), Is.EqualTo(label), $"{where}: label");

                switch (value)
                {
                    case bool b:
                        Assert.That(entry[1].GetBoolean(), Is.EqualTo(b), where);
                        break;

                    case int n:
                        Assert.That(entry[1].GetDouble(), Is.EqualTo(n), where);
                        break;

                    case string s:
                        Assert.That(entry[1].GetString(), Is.EqualTo(s), where);
                        break;

                    default:
                        Assert.That(entry[1].ValueKind, Is.EqualTo(JsonValueKind.Null), where);
                        break;
                }
            }
        });
    }

    public static IEnumerable<string> CaseNames() => golden.Keys;
}
