using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the LINE SKIP (backlog 241): Enter gives up the rest of the active line and
/// moves the player onto the next one, in the browser exactly as on the desktop.
///
/// <para>The skip is CARET MOVEMENT AND NOTHING ELSE, which is the claim that lets it exist without
/// an era bit of its own on the desktop side: the cells left behind stay untyped and are judged by
/// the SEAL, all at once, with the abandoned line's one combo break, at that line's own deadline,
/// precisely as they would be for a player who stopped typing and sat there. Nothing is decided at
/// press time, so nothing judged changes value or timing.</para>
///
/// <para>That claim rests on ONE piece of state, <c>lineAbandoned</c> in both engines:
/// <c>sealPermitted</c> defers a line's seal by <c>FLETCHER_DRAG_GRACE_MS</c> while the player is
/// still on it, and a skip moves the caret off, so without the flag the abandoned line seals up to
/// 1500 ms early. That is a scoring difference, not a cosmetic one: the seal is where the misses
/// land and where the line's one combo break is taken, so an early seal re-prices every keypress
/// made on the NEXT line in between at a combo the player had not lost yet. Both clients submit to
/// the SAME leaderboards, so one engine holding the grace and the other not means the identical
/// performance scores differently depending on where it was played.</para>
///
/// <para>ONE COPY OF THE KEYSTROKES, the shape <see cref="WpmClockArmLiveParityTest"/> established:
/// the script, and the readings the browser made of it, come out of
/// <c>CoreFlexibleLinesHarness.cjs</c>'s <c>lineSkip</c> section; this side replays the emitted steps
/// through the game's real <see cref="TypingEngine"/> and compares after every one, with no
/// tolerance. The section introduces a third step op, <c>enter</c>, which is the only thing the two
/// replay loops do not already share, and it lands here as <see cref="TypingEngine.ProcessEnter"/>.
/// The browser has no replay frame to carry it (it writes none at all), so the desktop's ENTER
/// sentinel has no counterpart on this axis and is pinned in the game suite instead.</para>
/// </summary>
[TestFixture]
public class LineSkipLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("CoreFlexibleLinesHarness.cjs"));

    private static JsonElement LineSkip() => harness.Value.GetProperty("lineSkip");

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's three-line map, in the game's terms. Every window is what the browser's loader
    /// derived (contiguous lines, a final line stretched past its vocals), written out here so the
    /// two sides hold the same map rather than two nearly equal ones.
    ///
    /// <para>L0 "ab cd" [1000, 4000) sung to 3000: a = 1000, b = 1500, ' ' = 2000, c = 2000,
    /// d = 2500. L1 "ef" [4000, 8000) sung to 5000: e = 4000, f = 4500, activating at its own 4000
    /// start, so entry into it opens at 2500. L2 "gh" [8000, 12000) sung to 9000: g = 8000,
    /// h = 8500, activating at 8000, so entry opens at 6500. Every seal grace is 0.</para>
    ///
    /// <para>Those two entry instants are what make the script reach both arms of the skip on one
    /// map: an Enter at 2500 hands the caret over ON THE PRESS, and one at 5600 is refused and parks
    /// it for the line-start snap to collect at 6500.</para>
    /// </summary>
    private static LyricBeatmap Map() => new LyricBeatmap
    {
        Metadata = new LyricBeatmapMetadata
        {
            Artist = "a",
            Title = "t",
            FolderPath = @"X:\nowhere",
            AudioFileName = "a.mp3",
        },
        Lines =
        [
            Line("ab cd", 1000, 4000, 3000, Unit("ab", 1000, 2000), Unit("cd", 2000, 3000)),
            Line("ef", 4000, 8000, 5000, Unit("ef", 4000, 5000)),
            Line("gh", 8000, 12000, 9000, Unit("gh", 8000, 9000)),
        ],
        Granularity = TimingGranularity.Line,
    };

    /// <summary>
    /// A started engine under every LIVE rule, which is the only arm the browser can be compared
    /// against: it has no mods payload, writes no replay frames and re-derives no stored row, so the
    /// era flags the C# defaults OFF for replay decoding have to be set by hand here.
    /// </summary>
    private static TypingEngine LiveEngine() => new TypingEngine(Map())
    {
        SyllableTiming = true,
        CharTimedStretch = true,
        FirstCharTiming = true,
        WrongInputOnWordGaps = true,
        StrictSpaces = true,
        SpaceSkipsWord = true,
        FletcherEnabled = true,
        FlexibleLineSnap = true,
        BoundedRush = true,
        BackDatedSealBreak = true,
        LosslessSkipReclaim = true,
        FoldsDisplacedClaim = true,
        FirstLineLeadIn = true,
    };

    /// <summary>The cell states as the JS mirror spells them (its own vocabulary, one for one).</summary>
    private static string StateName(CellState state) => state switch
    {
        CellState.Untyped => "untyped",
        CellState.Correct => "correct",
        CellState.Wrong => "wrong",
        CellState.Missed => "missed",
        CellState.AutoSkipped => "autoskip",
        CellState.Abandoned => "abandoned",
        _ => state.ToString(),
    };

    #endregion

    /// <summary>
    /// THE FIXTURES BEFORE THE ACCOUNTS, the discipline every guard in this project follows: both
    /// sides build their cells through their own loader (the browser's <c>buildBeatmap</c>, the
    /// game's <c>TypingLine.FromLyricLine</c>), so a fixture that drifted would show up below as an
    /// engine divergence and be blamed on the skip.
    ///
    /// <para>The two numbers the whole script hangs on are the entry instants, 2500 and 6500. If
    /// either moved, the two Enters would stop landing on opposite sides of their bound and the
    /// comparison would quietly stop covering one of the arms.</para>
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheLineSkipFixture()
    {
        var engine = LiveEngine();
        var lines = LineSkip().GetProperty("lines");
        var entryOpensAt = LineSkip().GetProperty("entryOpensAt");

        Assert.Multiple(() =>
        {
            Assert.That(LineSkip().GetProperty("dragGraceMs").GetDouble(), Is.EqualTo(TypingEngine.FLETCHER_DRAG_GRACE_MS));

            for (int i = 1; i < engine.Lines.Count; i++)
            {
                Assert.That(entryOpensAt[i - 1].GetDouble(),
                    Is.EqualTo(engine.Lines[i].ActivationTime - TypingEngine.FLETCHER_DRAG_GRACE_MS),
                    $"entry into line {i} opens a grace before its cue");
            }

            Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), "line count");

            for (int i = 0; i < engine.Lines.Count; i++)
            {
                var line = engine.Lines[i];
                var browserLine = lines[i];

                Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"[{i}]: activationTime");
                Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"[{i}]: endTime");
                Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"[{i}]: sealGraceMs");

                var browserCells = browserLine.GetProperty("cells");
                Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"[{i}]: cell count");

                for (int c = 0; c < line.Cells.Count; c++)
                {
                    Assert.That(browserCells[c].GetProperty("expected").GetString(), Is.EqualTo(line.Cells[c].Expected.ToString()), $"[{i}][{c}]: expected");
                    Assert.That(browserCells[c].GetProperty("target").GetDouble(), Is.EqualTo(line.Cells[c].TargetTime), $"[{i}][{c}]: target");
                }
            }
        });
    }

    /// <summary>
    /// The script, replayed through the game's engine and compared to the browser's readings step
    /// for step: where the caret is, whether every press did anything, WHICH LINE THE SEAL HAS
    /// REACHED, every cell's state, and the whole combo account.
    ///
    /// <para>The seal readings are the point. A skip that dropped the abandoned line's grace would
    /// still put the caret in the same places, so caret parity alone would pass: what moves is WHEN
    /// the line behind the player seals, and everything that seal decides (three cells missed on L0,
    /// one on L1, one combo break each) moves with it. The 'e' at 4000 and the 'g' at 8000 are
    /// deliberately inside the held graces, so an early seal is priced by them rather than merely
    /// being visible in a state array.</para>
    ///
    /// <para>Both Enters are handled and both later ones are not, which is the desktop's swallow
    /// rule travelling with the engine: the key handler records and swallows the press only when
    /// <see cref="TypingEngine.ProcessEnter"/> reports it did something, and lets it fall through to
    /// its global binding otherwise. The browser reads the same boolean for the same decision, minus
    /// the recording it has no replay to do.</para>
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOfTheBrowsersLineSkips()
    {
        var engine = LiveEngine();
        var script = LineSkip().GetProperty("script");
        var readings = LineSkip().GetProperty("readings");

        int breaks = 0;
        engine.ComboBroken += () => breaks++;

        Assert.Multiple(() =>
        {
            Assert.That(readings.GetArrayLength(), Is.EqualTo(script.GetArrayLength()), "one reading per step");

            for (int i = 0; i < script.GetArrayLength(); i++)
            {
                var step = script[i];
                var reading = readings[i];
                double t = step.GetProperty("t").GetDouble();
                string op = step.GetProperty("op").GetString()!;
                string where = $"[{i}] {op} {t}";

                switch (op)
                {
                    case "update":
                        engine.Update(t);
                        break;

                    case "enter":
                        bool skipped = engine.ProcessEnter(t);

                        Assert.That(skipped, Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where}: handled");
                        break;

                    case "key":
                        char c = step.GetProperty("c").GetString()![0];
                        bool handled = engine.ProcessKey(c, t);

                        Assert.That(handled, Is.EqualTo(reading.GetProperty("handled").GetBoolean()), $"{where} '{c}': handled");
                        break;

                    default:
                        throw new ArgumentException($"the harness emitted an op this side does not have: {op}");
                }

                var at = reading.GetProperty("at");
                Assert.That(engine.ActiveLineIndex, Is.EqualTo(at.GetProperty("line").GetInt32()), $"{where}: active line");
                Assert.That(engine.CaretIndex, Is.EqualTo(at.GetProperty("cell").GetInt32()), $"{where}: caret");
                Assert.That(engine.NextUnsealedLineIndex, Is.EqualTo(reading.GetProperty("nextUnsealedLineIndex").GetInt32()), $"{where}: the seal's position");
                Assert.That(engine.Combo, Is.EqualTo(reading.GetProperty("combo").GetInt32()), $"{where}: combo");
                Assert.That(engine.MaxCombo, Is.EqualTo(reading.GetProperty("maxCombo").GetInt32()), $"{where}: max combo");
                Assert.That(breaks, Is.EqualTo(reading.GetProperty("comboBreaks").GetInt32()), $"{where}: combo breaks");
                Assert.That(engine.Mistypes, Is.EqualTo(reading.GetProperty("mistypes").GetInt32()), $"{where}: mistypes");
                Assert.That(engine.IsFinished, Is.EqualTo(reading.GetProperty("finished").GetBoolean()), $"{where}: finished");

                var states = reading.GetProperty("states");
                Assert.That(states.GetArrayLength(), Is.EqualTo(engine.Lines.Count), $"{where}: one state row per line");

                for (int l = 0; l < engine.Lines.Count; l++)
                {
                    var cells = engine.Lines[l].Cells;
                    Assert.That(states[l].GetArrayLength(), Is.EqualTo(cells.Count), $"{where}: line {l} cell count");

                    for (int c = 0; c < cells.Count; c++)
                        Assert.That(StateName(cells[c].State), Is.EqualTo(states[l][c].GetString()), $"{where}: line {l} cell {c} state");
                }
            }

            Assert.That(engine.MaxCombo, Is.EqualTo(LineSkip().GetProperty("maxCombo").GetInt32()), "max combo at the end");
            Assert.That(breaks, Is.EqualTo(LineSkip().GetProperty("comboBreaks").GetInt32()), "combo breaks at the end");
            Assert.That(engine.Mistypes, Is.EqualTo(LineSkip().GetProperty("mistypes").GetInt32()), "mistypes at the end");
        });
    }

    /// <summary>
    /// NON-VACUITY, on the harness's own output rather than on either engine, because a comparison
    /// of two engines that both did nothing passes. Four things have to be true of the browser's run
    /// or the test above is not about the line skip at all:
    ///
    /// <list type="bullet">
    /// <item>An Enter was EFFECTIVE while the line it left still had untyped typeable cells, and it
    /// moved the caret onto the next line on the press (the immediate hand-over).</item>
    /// <item>An Enter was effective and did NOT move the caret off its line (the refused roll), and
    /// a later step with no press of its own carried it across (the deferred snap).</item>
    /// <item>An Enter was INEFFECTIVE on a caret with nothing left to give up, which is the state
    /// that keeps the desktop's key falling through to its global binding.</item>
    /// <item>The abandoned lines sealed LATE: at the instant each one's own deadline passed its
    /// cells were still untyped, and they became misses only a full grace later. This is the
    /// assertion that fails if either engine stops holding the grace for a line the caret walked out
    /// of, and it is the whole reason the skip needs no era bit.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheScriptReallyAbandonsLinesAndSealsThemLate()
    {
        var readings = LineSkip().GetProperty("readings");
        double grace = TypingEngine.FLETCHER_DRAG_GRACE_MS;

        int handedOverOnThePress = 0;
        int parkedByTheBound = 0;
        int carriedByASnap = 0;
        int ineffective = 0;

        for (int i = 0; i < readings.GetArrayLength(); i++)
        {
            var reading = readings[i];

            if (reading.GetProperty("op").GetString() != "enter")
                continue;

            var before = readings[i - 1];
            bool handled = reading.GetProperty("handled").GetBoolean();
            int lineLeft = before.GetProperty("at").GetProperty("line").GetInt32();

            if (!handled)
            {
                Assert.That(reading.GetProperty("at").GetProperty("cell").GetInt32(),
                    Is.EqualTo(before.GetProperty("at").GetProperty("cell").GetInt32()),
                    $"[{i}]: an Enter that reported nothing must have changed nothing");
                ineffective++;
                continue;
            }

            Assert.That(before.GetProperty("states")[lineLeft].EnumerateArray().Any(s => s.GetString() == "untyped"), Is.True,
                $"[{i}]: the line was already typed out, so this Enter gave nothing up");

            if (reading.GetProperty("at").GetProperty("line").GetInt32() > lineLeft)
            {
                handedOverOnThePress++;
                continue;
            }

            parkedByTheBound++;

            for (int j = i + 1; j < readings.GetArrayLength(); j++)
            {
                if (readings[j].GetProperty("at").GetProperty("line").GetInt32() <= lineLeft)
                    continue;

                Assert.That(readings[j].GetProperty("op").GetString(), Is.EqualTo("update"),
                    $"[{i}]: the parked caret was carried by a press rather than by the snap");
                carriedByASnap++;
                break;
            }
        }

        var lines = LineSkip().GetProperty("lines");
        int sealedLate = 0;

        for (int l = 0; l < lines.GetArrayLength(); l++)
        {
            double deadline = lines[l].GetProperty("endTime").GetDouble() + lines[l].GetProperty("sealGraceMs").GetDouble();
            string? atTheDeadline = null;
            string? afterTheGrace = null;

            foreach (var reading in readings.EnumerateArray())
            {
                double t = reading.GetProperty("t").GetDouble();
                string? state = reading.GetProperty("states")[l].EnumerateArray().Last().GetString();

                if (t >= deadline && atTheDeadline is null) atTheDeadline = state;
                if (t >= deadline + grace && afterTheGrace is null) afterTheGrace = state;
            }

            if (atTheDeadline == "untyped" && afterTheGrace == "missed")
                sealedLate++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(handedOverOnThePress, Is.GreaterThan(0), "no Enter landed inside the next line's entry window");
            Assert.That(parkedByTheBound, Is.GreaterThan(0), "no Enter landed outside it, so the deferred park was never reached");
            Assert.That(carriedByASnap, Is.EqualTo(parkedByTheBound), "a parked caret was never collected by the line-start snap");
            Assert.That(ineffective, Is.GreaterThan(0), "no Enter was ever refused, so the fall-through state is untested");
            Assert.That(sealedLate, Is.EqualTo(2), "the abandoned lines did not sit untyped at their own deadlines and seal a grace later");
        });
    }
}
