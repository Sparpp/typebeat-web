using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the KEYSTROKE PROTOCOL (backlog 305): a keypress is quantised to whole
/// milliseconds with C# <see cref="Math.Round(double)"/> (banker's rounding), and the engine is
/// advanced to that instant BEFORE any gate or judgement reads it. That is backlog 20's replay
/// determinism contract, which <c>TypeBeatPlayfield</c>'s key handler has always followed, and the
/// order <c>ReplayEngineFeed</c> and every parity harness use.
///
/// <para>The browser used to judge every press against engine state last advanced by the render
/// tick, with the raw fractional audio time. Anything that fell due between that frame and the
/// press was therefore applied AFTER the key: a press just past a drag cutoff was judged on the old
/// line, a press on a rush-parked caret whose entry opened between frames was lost against the
/// "line fully typed" guard, a backspace just past a seal stepped back up into a line the desktop
/// had already sealed, and a midpoint press was judged a millisecond away from the desktop's.</para>
///
/// <para>ONE COPY OF THE KEYSTROKES. The browser arm is <c>PlayerDisplayHarness.cjs</c>'s
/// <c>keyOrder</c> section, which runs the SHIPPED router (<c>TypeBeatCore.display.routeKeyDown</c>,
/// the whole of the player's keydown listener) on a 60 Hz tick whose phase keeps every tick off a
/// whole millisecond, with every press landing between two ticks. This side feeds the game's real
/// <see cref="TypingEngine"/> the same ticks, and each press the way the desktop key handler does:
/// <c>Update(Math.Round(t))</c>, then the op, gated exactly as <c>TypeBeatPlayfield</c> gates it
/// (including the parked-untouched-head Space drop). Every step is compared with zero tolerance on
/// cells, judged deltas, combo, score, statistics and the WPM clock.</para>
/// </summary>
[TestFixture]
public class KeyHandlerOrderLiveParityTest
{
    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() => NodeHarness.Run("PlayerDisplayHarness.cjs"));

    private static JsonElement KeyOrder() => harness.Value.GetProperty("keyOrder");

    #region The fixture, declared in the game's own terms

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    /// <summary>
    /// The harness's five-line map, in the game's terms (windows as the browser's loader derived
    /// them: contiguous, the last one stretched past its vocals). L4's window opens at 16000 but its
    /// first vocal is 20000, so it activates at 18500 and entry into it opens at 17000.
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
            Line("ij", 12000, 16000, 13000, Unit("ij", 12000, 13000)),
            Line("kl", 16000, 24000, 21000, Unit("kl", 20000, 21000)),
        ],
        Granularity = TimingGranularity.Line,
    };

    /// <summary>A started engine under every LIVE rule, the only arm the browser can be held against.</summary>
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
        ManualNewlines = true,
        NewlineOnTypedLetter = true,
        AllowWrongInput = true,
    };

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
    /// The desktop key handler's routing for one press (TypeBeatPlayfield's OnKeyDown, default
    /// bindings, no modifiers), AFTER its update: which engine op it makes, if any. Returns the op's
    /// result, or null when the handler makes no EFFECTIVE engine call and lets the key fall through.
    ///
    /// <para>The line-complete arm is the MANUAL NEWLINE's (backlog 307, the setting the browser
    /// takes at the desktop's shipped default): with no selection live, a Space goes to
    /// <see cref="TypingEngine.ProcessKey"/> as the newline, and a letter goes there as the
    /// typed-through newline, each consumed only if the engine reports it did something. A refused
    /// one falls through, which the desktop reaches after calls that changed nothing; the comparison
    /// tolerates the browser's matching refused calls for exactly that reason. Enter never reaches
    /// that arm: the SkipLine binding above it hands a finished line to
    /// <see cref="TypingEngine.ProcessEnter"/>, which is where its newline lives.</para>
    /// </summary>
    private static bool? DesktopPress(TypingEngine engine, string key, double time)
    {
        if (!engine.LineIsActive && !engine.FirstLineTypingOpensAt(time))
            return null;

        if (engine.FletcherEnabled && key == " " && !engine.SongIsOnTheCaretsLine && engine.ActiveLineUntouched)
            return null;

        switch (key)
        {
            case "Backspace":
                return engine.AllowWrongInput ? engine.ProcessBackspace() : null;

            case "Enter":
                return engine.ProcessEnter(time);
        }

        if (engine.IsLineComplete)
        {
            if (engine.ManualNewlines && key == " " && engine.ProcessKey(' ', time))
                return true;

            if (engine.NewlineOnTypedLetter && key.Length == 1 && engine.ProcessKey(key[0], time))
                return true;

            return null;
        }

        return engine.ProcessKey(key[0], time);
    }

    /// <summary>
    /// THE FIXTURES BEFORE THE ACCOUNTS: both sides build the cells through their own loader, so a
    /// fixture that drifted would otherwise be read below as an engine divergence.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnTheKeyOrderFixture()
    {
        var engine = LiveEngine();
        var lines = KeyOrder().GetProperty("lines");
        var entryOpensAt = KeyOrder().GetProperty("entryOpensAt");

        Assert.Multiple(() =>
        {
            Assert.That(lines.GetArrayLength(), Is.EqualTo(engine.Lines.Count), "line count");

            for (int i = 1; i < engine.Lines.Count; i++)
            {
                Assert.That(entryOpensAt[i - 1].GetDouble(),
                    Is.EqualTo(engine.Lines[i].ActivationTime - TypingEngine.FLETCHER_DRAG_GRACE_MS), $"entry into line {i}");
            }

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
    /// The run, step for step. Ticks go in at their fractional times, exactly as the browser's render
    /// loop feeds them; presses go in as the desktop takes them. The browser's own record of what its
    /// router did is checked too: the instant it advanced the engine to (which must be the desktop's
    /// rounded time, and must have happened before the op), and the op it made with the time it
    /// passed.
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOfTheBrowsersKeystrokes() => ReplayTheBrowsersRun(KeyOrder());

    /// <summary>
    /// The MANUAL NEWLINE's keystrokes (backlog 307) through the same router on the same map and
    /// tick, held against the desktop the same way: the newline Space before the skip gate, the
    /// second Space dropped at the parked head, the typed-letter newline landing on a line that
    /// awaits its window, presses swallowed by that wait, the window opening between a tick and a
    /// press, the hold cutoff, and the Enter newline.
    /// </summary>
    [Test]
    public void TheGameEngineMakesTheSameRunOfTheBrowsersManualNewlines() => ReplayTheBrowsersRun(ManualKeyOrder());

    private static JsonElement ManualKeyOrder() => harness.Value.GetProperty("keyOrderManual");

    private static void ReplayTheBrowsersRun(JsonElement section)
    {
        var engine = LiveEngine();
        var steps = section.GetProperty("steps");

        int breaks = 0;
        engine.ComboBroken += () => breaks++;

        for (int i = 0; i < steps.GetArrayLength(); i++)
        {
            var step = steps[i];
            double t = step.GetProperty("t").GetDouble();
            string op = step.GetProperty("op").GetString()!;

            // Every divergence at a step is collected, and the run stops at the FIRST diverging
            // step: once the two engines part, every later step differs too and says nothing new.
            var diverged = new List<string>();

            void Eq<T>(T game, T browser, string what)
            {
                if (!EqualityComparer<T>.Default.Equals(game, browser))
                    diverged.Add($"{what}: game {game?.ToString() ?? "null"}, browser {browser?.ToString() ?? "null"}");
            }

            if (op == "update")
            {
                engine.Update(t);
            }
            else
            {
                string key = step.GetProperty("key").GetString()!;
                double rounded = Math.Round(t);

                engine.Update(rounded);

                var judged = step.GetProperty("judgedAgainst");

                if (judged.ValueKind != JsonValueKind.Object)
                {
                    diverged.Add("the router never advanced the engine before the press");
                }
                else
                {
                    Eq(rounded, judged.GetProperty("t").GetDouble(), "the instant the engine was advanced to");
                    Eq(engine.ActiveLineIndex, judged.GetProperty("line").GetInt32(), "line judged against");
                    Eq(engine.CaretIndex, judged.GetProperty("cell").GetInt32(), "caret judged against");
                    Eq(engine.NextUnsealedLineIndex < 0 ? engine.Lines.Count : engine.NextUnsealedLineIndex, judged.GetProperty("seal").GetInt32(), "seal judged against");
                    Eq(engine.ActiveLineUntouched, judged.GetProperty("untouched").GetBoolean(), "ActiveLineUntouched at the press");
                    Eq(engine.SongIsOnTheCaretsLine, judged.GetProperty("songOnIt").GetBoolean(), "SongIsOnTheCaretsLine at the press");
                    Eq(engine.AwaitingEntry, judged.GetProperty("awaiting").GetBoolean(), "AwaitingEntry at the press");
                }

                bool? result = DesktopPress(engine, key, rounded);

                // A browser processKey the desktop would not have made is tolerated only when it
                // reported doing nothing (the desktop lets a letter on a finished line fall through
                // without calling the engine; the browser calls it and it refuses). Any state it
                // might still have touched is caught by the comparisons below.
                var ops = step.GetProperty("calls").EnumerateArray()
                              .Where(c => !(result is null && c.GetProperty("fn").GetString() == "key" && !c.GetProperty("result").GetBoolean()))
                              .ToArray();

                if (result is null)
                {
                    Eq(0, ops.Length, "engine ops where the desktop makes none");
                }
                else
                {
                    Eq(1, ops.Length, "engine ops");

                    if (ops.Length == 1)
                    {
                        Eq(result.Value, ops[0].GetProperty("result").GetBoolean(), "op result");

                        if (ops[0].TryGetProperty("t", out var opTime))
                            Eq(rounded, opTime.GetDouble(), "the time the op was judged at");
                    }
                }
            }

            Eq(engine.ActiveLineIndex, step.GetProperty("line").GetInt32(), "active line");
            Eq(engine.CaretIndex, step.GetProperty("cell").GetInt32(), "caret");
            Eq(engine.IsFinished, step.GetProperty("finished").GetBoolean(), "finished");
            Eq(engine.Combo, step.GetProperty("combo").GetInt32(), "combo");
            Eq(engine.MaxCombo, step.GetProperty("maxCombo").GetInt32(), "max combo");
            Eq(breaks, step.GetProperty("comboBreaks").GetInt32(), "combo breaks");
            Eq(engine.Mistypes, step.GetProperty("mistypes").GetInt32(), "mistypes");
            Eq(engine.Score, step.GetProperty("score").GetInt64(), "score");
            Eq(engine.LiveWpm, step.GetProperty("liveWpm").GetDouble(), "live WPM (the WPM clock)");
            Eq(engine.ActiveLineUntouched, step.GetProperty("activeLineUntouched").GetBoolean(), "ActiveLineUntouched");
            Eq(engine.SongIsOnTheCaretsLine, step.GetProperty("songIsOnTheCaretsLine").GetBoolean(), "SongIsOnTheCaretsLine");
            Eq(engine.AwaitingEntry, step.GetProperty("awaiting").GetBoolean(), "AwaitingEntry");

            // The keypress statistics. The browser's engine counts only what a PRESS is judged
            // (a seal's misses live on the cells, compared below), so Miss is left out here.
            string counts = string.Join(",", engine.BuildResults().Counts
                                                   .Where(kv => kv.Value != 0 && kv.Key != JudgementType.Miss)
                                                   .Select(kv => $"{kv.Key}={kv.Value}").OrderBy(x => x, StringComparer.Ordinal));
            string browserCounts = string.Join(",", step.GetProperty("counts").EnumerateObject()
                                                        .Where(p => p.Value.GetInt32() != 0 && p.Name != "Miss")
                                                        .Select(p => $"{p.Name}={p.Value.GetInt32()}").OrderBy(x => x, StringComparer.Ordinal));
            Eq(counts, browserCounts, "statistics");

            var states = step.GetProperty("states");
            var deltas = step.GetProperty("deltas");

            for (int l = 0; l < engine.Lines.Count; l++)
            {
                var cells = engine.Lines[l].Cells;

                for (int c = 0; c < cells.Count; c++)
                {
                    Eq(StateName(cells[c].State), states[l][c].GetString(), $"line {l} cell {c} state");

                    var delta = deltas[l][c];
                    double? browserDelta = delta.ValueKind == JsonValueKind.Null ? null : delta.GetDouble();
                    Eq(cells[c].JudgedDelta, browserDelta, $"line {l} cell {c} judged delta");
                }
            }

            if (diverged.Count > 0)
            {
                string what = op == "update" ? $"tick at {t}" : $"press '{step.GetProperty("key").GetString()}' at {t}";
                Assert.Fail($"step [{i}], {what}, is the first to diverge:\n  " + string.Join("\n  ", diverged));
            }
        }
    }

    /// <summary>
    /// NON-VACUITY, on the browser's own record: a comparison of two engines that never met the
    /// shapes this task is about passes trivially. Each of the transitions the old order applied
    /// AFTER the key has to have fallen due between a tick and a press, which is visible as the
    /// router's own update moving the state the press was judged against away from the tick's:
    ///
    /// <list type="bullet">
    /// <item>A SEAL (the seal cursor advanced with the caret staying put): the backspace after an
    /// abandoned line's held deadline.</item>
    /// <item>A DRAG CUTOFF (the seal advanced AND carried the caret onto the next line).</item>
    /// <item>NO RUSH SNAP: since backlog 307 the browser runs the manual newline, under which the
    /// line-start snap is dead code, so the press at 10500.5 that used to land just past one is now
    /// the typed-through newline, made BY the press rather than before it. Pinned at zero, so a
    /// router or engine that fell back to the automatic hand-over reads non-zero; the manual
    /// section's own transition (a window opening between a tick and a press) is counted in
    /// <see cref="TheManualNewlineKeystrokesReachEveryArm"/>.</item>
    /// <item>An ACTIVATION (no line active at the tick, one active at the press).</item>
    /// <item>The parked-untouched-head Space DROP, swallowed with no engine op.</item>
    /// <item>A MIDPOINT press, where banker's rounding and Math.round disagree.</item>
    /// </list>
    /// </summary>
    [Test]
    public void EveryPressLandsJustAfterATransitionTheTickHadNotApplied()
    {
        var steps = KeyOrder().GetProperty("steps");

        int seals = 0, cutoffs = 0, snaps = 0, activations = 0, drops = 0, midpoints = 0;

        foreach (var step in steps.EnumerateArray())
        {
            if (step.GetProperty("op").GetString() != "press")
                continue;

            double t = step.GetProperty("t").GetDouble();

            if (t - Math.Floor(t) == 0.5 && Math.Round(t) != Math.Floor(t + 0.5))
                midpoints++;

            if (step.GetProperty("key").GetString() == " " && step.GetProperty("prevented").GetBoolean() && step.GetProperty("calls").GetArrayLength() == 0)
                drops++;

            var before = step.GetProperty("before");
            var judged = step.GetProperty("judgedAgainst");

            if (judged.ValueKind != JsonValueKind.Object)
                continue;

            int lineBefore = before.GetProperty("line").GetInt32(), lineAt = judged.GetProperty("line").GetInt32();
            int sealBefore = before.GetProperty("seal").GetInt32(), sealAt = judged.GetProperty("seal").GetInt32();

            if (lineBefore == -1 && lineAt >= 0) activations++;
            else if (sealAt > sealBefore && lineAt == lineBefore) seals++;
            else if (sealAt > sealBefore && lineAt > lineBefore) cutoffs++;
            else if (sealAt == sealBefore && lineAt > lineBefore) snaps++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(seals, Is.GreaterThan(0), "no press landed just past a seal");
            Assert.That(cutoffs, Is.GreaterThan(0), "no press landed just past a drag cutoff");
            Assert.That(snaps, Is.Zero, "a press landed just past a rush snap: the run is not on the manual arm");
            Assert.That(activations, Is.GreaterThan(0), "no press landed just past a line's activation");
            Assert.That(drops, Is.GreaterThan(0), "no Space was dropped on a parked untouched head");
            Assert.That(midpoints, Is.GreaterThan(0), "no press sat on a midpoint the two roundings disagree on");
        });
    }

    /// <summary>
    /// NON-VACUITY for the manual section, on the browser's own record: every arm the desktop's
    /// manual key handling has must be reached, or the comparison above passes on a run that never
    /// met them.
    /// </summary>
    [Test]
    public void TheManualNewlineKeystrokesReachEveryArm()
    {
        int spaceNewlines = 0, letterNewlines = 0, enterNewlines = 0, secondSpaceDrops = 0, awaitingDrops = 0;
        int swallowedKeys = 0, swallowedEnters = 0, windowOpenings = 0, holdCutoffs = 0;

        foreach (var step in ManualKeyOrder().GetProperty("steps").EnumerateArray())
        {
            if (step.GetProperty("op").GetString() != "press")
                continue;

            string key = step.GetProperty("key").GetString()!;
            var before = step.GetProperty("before");
            var judged = step.GetProperty("judgedAgainst");
            var calls = step.GetProperty("calls").EnumerateArray().ToArray();
            int lineAt = judged.GetProperty("line").GetInt32();
            bool movedOn = step.GetProperty("line").GetInt32() > lineAt;
            bool effective = calls.Length == 1 && calls[0].GetProperty("result").GetBoolean();

            if (key == " " && movedOn && effective) spaceNewlines++;
            if (key.Length == 1 && key != " " && movedOn && effective) letterNewlines++;
            if (key == "Enter" && movedOn && effective && !judged.GetProperty("untouched").GetBoolean()) enterNewlines++;

            if (key == " " && calls.Length == 0 && step.GetProperty("prevented").GetBoolean())
            {
                if (judged.GetProperty("awaiting").GetBoolean()) awaitingDrops++;
                else secondSpaceDrops++;
            }

            if (judged.GetProperty("awaiting").GetBoolean() && calls.Length == 1 && !calls[0].GetProperty("result").GetBoolean())
            {
                if (key == "Enter") swallowedEnters++;
                else swallowedKeys++;
            }

            if (before.GetProperty("awaiting").GetBoolean() && !judged.GetProperty("awaiting").GetBoolean()) windowOpenings++;
            if (judged.GetProperty("seal").GetInt32() > before.GetProperty("seal").GetInt32() && lineAt > before.GetProperty("line").GetInt32()) holdCutoffs++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(spaceNewlines, Is.GreaterThan(0), "no Space handed a finished line over");
            Assert.That(letterNewlines, Is.GreaterThan(0), "no letter handed a finished line over");
            Assert.That(enterNewlines, Is.GreaterThan(0), "no Enter handed a finished line over");
            Assert.That(secondSpaceDrops, Is.GreaterThan(0), "no second Space was dropped at the head it landed on");
            Assert.That(awaitingDrops, Is.GreaterThan(0), "no Space was dropped at the head of an awaited line");
            Assert.That(swallowedKeys, Is.GreaterThan(0), "no letter was swallowed by the wait");
            Assert.That(swallowedEnters, Is.GreaterThan(0), "no Enter was swallowed by the wait");
            Assert.That(windowOpenings, Is.GreaterThan(0), "no awaited window opened between a tick and a press");
            Assert.That(holdCutoffs, Is.GreaterThan(0), "no press landed just past a held line's cutoff");
        });
    }
}
