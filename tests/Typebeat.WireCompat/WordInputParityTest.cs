using System.Globalization;
using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the two WORD-LEVEL EDITING GESTURES (backlog 182): Ctrl+Backspace erases
/// the previous word, Ctrl+A offers back the run to the earliest unfixed typo (backlog 184 widened
/// it from the nearest one) so every mistake can be retyped in one go. The desktop got them first
/// (<c>TypeBeatKeyHandler</c>); this is the browser holding its half against the game's live engine.
///
/// <para>Since backlog 184 it also carries the SPACE DISCIPLINE, which belongs here rather than in a
/// fixture of its own because both of its halves are about where the caret ends up: a wrong letter
/// on a word gap PARKS the caret on it (with word skipping on), and a space typed inside a word is a
/// typo rather than a rejection (with word skipping off). A caret that parts between the two clients
/// puts every later keystroke on a different cell.</para>
///
/// <para>The engine's whole share of both gestures is TWO PURE QUERIES,
/// <see cref="TypingEngine.WordBackspaceTarget"/> and <see cref="TypingEngine.RetypeSelectionAnchor"/>,
/// mirrored into <c>typebeat-core.js</c> as plain getters. Everything else is COMPOSED out of engine
/// calls that already exist (a run of <see cref="TypingEngine.ProcessBackspace"/> plus at most one
/// <see cref="TypingEngine.ProcessKey"/>) and lives in the input layer. That is why the gestures
/// needed no new replay frame and no new era bit on the desktop, and it is why the two clients can
/// be compared at all: they run the same engine calls in the same order.</para>
///
/// <para>So the comparison here is not "do the two queries agree on a snapshot": both arms run the
/// SAME step script through the SAME input-layer interpreter (collapse-then-type, the defensive
/// erase loop, the per-frame staleness drop), and a probe is taken after EVERY step carrying both
/// queries, the caret, the selection the input layer is holding, every cell's state and typed char,
/// and the whole running account. A divergence anywhere in the composition, not just in the two
/// queries, fails here.</para>
///
/// <para>The scenarios are declared once, on this side, and handed to
/// <c>tests/Typebeat.Web.Tests/Js/CoreWordInputHarness.cjs</c> as a temp JSON file, so the two arms
/// cannot drift onto separately maintained copies of the same map or the same keystrokes. Every map
/// goes through the production <see cref="LyricOsuFormat"/> and is read by each side's own loader.</para>
///
/// <para>One case in the desktop's own spec has no reachable fixture on either side: a NON-TYPEABLE
/// (auto-skipped) cell. Both loaders build cells the same way (<c>TypingLine.FromLyricLine</c> and
/// <c>buildCells</c>), and in both streams every surviving char is typeable: the default stream drops
/// punctuation outright, and Literate makes every mark a first-class cell. So the auto-skip
/// step-over is pinned through the reachable twin that shares its code path, the ABANDONED cells of
/// a word skip (<c>ProcessBackspace</c> steps transparently over both, or since PR 3 undoes the skip
/// outright when the caret is beside it), and the "a mark rides inside its word rather than opening
/// one" half is pinned under Literate, where an apostrophe IS a cell and must still not read as a
/// word boundary.</para>
///
/// <para>PR 3's SECOND INPUT ERA (<see cref="TypingEngine.InputEra2"/>) is live on both arms: one
/// backspace undoes a word skip and the gap it typed, a gap typo anchors on the word before it, and a
/// word given up whole anchors on its own head. The scenarios that pinned the older answers carry the
/// era's answers now, under the game's own "...UnderInputEra2" test names.</para>
/// </summary>
[TestFixture]
public class WordInputParityTest
{
    #region The step language, shared with the node harness

    /// <summary>
    /// One input-layer action. <c>key</c> is a typeable keypress (which consumes a live selection
    /// first), <c>backspace</c> and <c>ctrlBackspace</c> are the two erase widths, <c>ctrlA</c> takes
    /// a selection, <c>update</c> advances the clock, and <c>churn</c> reads both queries n times to
    /// prove they mutate nothing.
    /// </summary>
    private sealed record Step
    {
        public required string Op { get; init; }
        public string? C { get; init; }
        public double? T { get; init; }
        public int? N { get; init; }
    }

    private static Step Key(char c, double t) => new Step { Op = "key", C = c.ToString(), T = t };
    private static Step Backspace() => new Step { Op = "backspace" };
    private static Step CtrlBackspace() => new Step { Op = "ctrlBackspace" };
    private static Step CtrlA() => new Step { Op = "ctrlA" };
    private static Step Update(double t) => new Step { Op = "update", T = t };
    private static Step Churn(int n = 100) => new Step { Op = "churn", N = n };

    private sealed record Scenario
    {
        public required string Name { get; init; }
        public required string Osu { get; init; }
        public bool Literate { get; init; }
        public bool SpaceSkipsWord { get; init; }
        public double StartTime { get; init; } = 1000;
        public required Step[] Steps { get; init; }
    }

    #endregion

    #region The fixtures

    /// <summary>
    /// The desktop spec's own fixture (<c>NonVisual/WordInputTest.abCdEf</c>), as the .osu both
    /// loaders read: "ab cd ef", cells a(1000) b(1500) ' '(2000) c(2000) d(2500) ' '(3000) e(3000)
    /// f(3500). The word gaps are cells 2 and 5 and the words start at 0, 3 and 6. THREE words, not
    /// two, because the load-bearing property of Ctrl+Backspace is that it takes exactly one of them.
    /// song_end_ms is far out so nothing seals mid-scenario.
    /// </summary>
    private static string AbCdEf() => Osu(
        "{\"text\":\"ab cd ef\",\"start_ms\":1000,\"end_ms\":4000,\"words\":["
        + "{\"text\":\"ab\",\"start_ms\":1000,\"end_ms\":2000,\"score\":1},"
        + "{\"text\":\"cd\",\"start_ms\":2000,\"end_ms\":3000,\"score\":1},"
        + "{\"text\":\"ef\",\"start_ms\":3000,\"end_ms\":4000,\"score\":1}]}");

    /// <summary>
    /// "don't go", read under LITERATE so the apostrophe is a cell of its own: d(1000) o(1250)
    /// n(1500) '(1625) t(1750) ' '(2000) g(2000) o(2500). The one fixture where a cell that is not a
    /// letter sits INSIDE a word, which is the property both queries have to get right (a mark rides
    /// inside the word it is attached to; only a typeable SPACE opens a new one).
    /// </summary>
    private static string DontGo() => Osu(
        "{\"text\":\"don't go\",\"start_ms\":1000,\"end_ms\":3000,\"words\":["
        + "{\"text\":\"don't\",\"start_ms\":1000,\"end_ms\":2000,\"score\":1},"
        + "{\"text\":\"go\",\"start_ms\":2000,\"end_ms\":3000,\"score\":1}]}");

    /// <summary>The .osu the production writer emits for one line, which is what both loaders read.</summary>
    private static string Osu(string lineJson)
        => LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c",
            "{\"version\":2,\"song_end_ms\":60000,\"lines\":[" + lineJson + "]}");

    // AbCdEf cell targets, named for the cells they belong to.
    private const double a_t = 1000;
    private const double b_t = 1500;
    private const double gap1_t = 2000;
    private const double c_t = 2000;
    private const double d_t = 2500;
    private const double gap2_t = 3000;
    private const double e_t = 3000;
    private const double f_t = 3500;

    /// <summary>The whole of "ab cd ef" typed in order, every cell dead on its target.</summary>
    private static Step[] TypeItAll() =>
    [
        Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t),
        Key('d', d_t), Key(' ', gap2_t), Key('e', e_t), Key('f', f_t),
    ];

    private static Scenario[] BuildScenarios() =>
    [
        // ---- Ctrl+Backspace: where the word ends -------------------------------------------------

        // At the head of the line there is nothing behind the caret, so the query answers the caret
        // itself and the composed gesture never calls the engine. Ctrl+A is equally inert with no
        // typo anywhere.
        new Scenario { Name = "headOfLineIsANoOp", Osu = AbCdEf(), Steps = [Churn(), CtrlBackspace(), CtrlA()] },

        // Mid-word: back to the start of the word the caret is inside, and no further.
        new Scenario
        {
            Name = "midWordErasesToThatWordsStart",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t), Churn(), CtrlBackspace()],
        },

        // At a word START (the gap immediately behind the caret) the gesture takes the gap AND the
        // whole word before it, which is the case a naive "walk back to the previous space" gets
        // wrong by leaving the caret on the gap.
        new Scenario
        {
            Name = "atAWordStartItTakesTheGapAndThePreviousWord",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Churn(), CtrlBackspace()],
        },

        // A LINE-COMPLETE caret (caret == cells.Count) still answers: the last word goes.
        new Scenario
        {
            Name = "atTheEndOfTheLineItTakesTheLastWord",
            Osu = AbCdEf(),
            Steps = [.. TypeItAll(), Churn(), CtrlBackspace()],
        },

        // Held down, it walks back word by word and then stops dead at the head of the line rather
        // than spinning or erasing past it.
        new Scenario
        {
            Name = "holdingItWalksBackWordByWordToTheHead",
            Osu = AbCdEf(),
            Steps = [.. TypeItAll(), CtrlBackspace(), CtrlBackspace(), CtrlBackspace(), CtrlBackspace()],
        },

        // A word given up to a word SKIP is reclaimed exactly as the plain key reclaims it. Since
        // PR 3 (ItReclaimsASkippedWordLikeThePlainKeyUnderInputEra2) the first press of the composed
        // loop undoes the skip and its gap, and the second erases the typed 'a', landing on the
        // target.
        new Scenario
        {
            Name = "itReclaimsASkippedWordLikeThePlainKey",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps = [Key('a', a_t), Key(' ', 1200), Churn(), CtrlBackspace()],
        },

        // ---- Ctrl+A: where the retype starts -----------------------------------------------------

        // Nothing wrong behind the caret, nothing to offer. Asserted on a clean run AND on one whose
        // typo has already been backspaced away, because the query reads cell STATE and not the
        // history of the run.
        new Scenario
        {
            Name = "withNoTypoBehindTheCaretThereIsNoAnchor",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t), Key('d', d_t), CtrlA(), Churn()],
        },
        new Scenario
        {
            Name = "anErasedTypoIsNotAnUnfixedOne",
            Osu = AbCdEf(),
            Steps = [Key('x', a_t), Churn(), Backspace(), Churn(), CtrlA()],
        },

        // An ordinary lyric typo anchors on its own WORD's start, so the player retypes the word and
        // not the line.
        new Scenario
        {
            Name = "aTypoAnchorsOnItsOwnWordsStart",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('x', b_t), Key(' ', gap1_t), Key('c', c_t), Churn(), CtrlA()],
        },

        // A typo in the word the player is halfway through anchors on THAT word: it is the only one
        // on the line, and the scan takes the earliest, so the selection is the partial word alone
        // rather than everything typed before it.
        new Scenario
        {
            Name = "aTypoInTheCurrentPartialWordAnchorsOnThatWord",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('z', c_t), Churn(), CtrlA()],
        },

        // Two unfixed typos, one per word, caret in the third word: the EARLIEST one wins (backlog
        // 184), so one gesture offers back everything that has to be retyped rather than the most
        // recent word alone.
        new Scenario
        {
            Name = "theEarliestTypoBehindTheCaretWins",
            Osu = AbCdEf(),
            Steps =
            [
                Key('x', a_t), Key('b', b_t), Key(' ', gap1_t), Key('z', c_t),
                Key('d', d_t), Key(' ', gap2_t), Key('e', e_t), Churn(), CtrlA(),
            ],
        },

        // A typo on the WORD GAP itself (a wrong letter typed into the gap cell, live since backlog
        // 181 and unconditional in the browser). Since PR 3
        // (AGapTypoAnchorsOnThePrecedingWordUnderInputEra2) it anchors on the start of the word
        // BEFORE the gap, so the retype includes its space; before PR 3 it anchored on the gap
        // itself.
        new Scenario
        {
            Name = "aGapTypoAnchorsOnThePrecedingWord",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('b', b_t), Key('x', gap1_t), Key('c', c_t), Churn(), CtrlA()],
        },

        // Backlog 244's guard direction, the reverse of "aSelectionCollapsesThroughAbandonedCells"
        // below (where the typo and the abandoned cell share one word): an unfixed typo in an
        // EARLIER word still outranks a word skip's abandoned cells in a LATER one, purely because
        // the scan takes the earliest mistake on the line regardless of which of the two states it
        // is in.
        new Scenario
        {
            Name = "anEarlierTypoOutranksALaterAbandonedWord",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps = [Key('x', a_t), Key('b', b_t), Key(' ', gap1_t), Key(' ', 2200), Churn(), CtrlA()],
        },

        // A word given up WHOLE, and the collapse that has to land on the anchor it offered. Backlog
        // 260 widened that anchor onto the gap in FRONT of the word, because the old backspace stepped
        // transparently over abandoned cells and could not stop on the head of a word nobody touched.
        // Since PR 3 (CollapsingASelectionOverAWhollyAbandonedWordLandsOnItsAnchorUnderInputEra2) the
        // era's backspace undoes the skip in one press and stops ON the word's head, so that is the
        // anchor, and the gap in front of it is preserved.
        //
        // The whole gesture is composed here rather than stopping at the anchor: the load-bearing
        // reading is the caret AFTER the erases being the anchor the player was shown, and the line
        // then typing out with no mistype at all.
        new Scenario
        {
            Name = "aWhollyAbandonedWordAnchorsOnItsHead",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps =
            [
                Key('a', a_t), Key('b', b_t), Key(' ', gap1_t),
                Key(' ', 2100), // the space at the HEAD of "cd": the whole word goes, untouched
                Churn(), CtrlA(),
                Key('c', c_t), Key('d', d_t), Key(' ', gap2_t), Key('e', e_t), Key('f', f_t),
            ],
        },

        // A LINE-COMPLETE caret, and the composed consume from there: the selection reaches back over
        // the last word, collapses, and the letter lands on the anchor cell.
        new Scenario
        {
            Name = "aLineCompleteCaretStillAnchorsAndConsumes",
            Osu = AbCdEf(),
            Steps =
            [
                Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t),
                Key('d', d_t), Key(' ', gap2_t), Key('x', e_t), Key('f', f_t),
                Churn(), CtrlA(), Key('e', e_t), Key('f', f_t),
            ],
        },

        // ---- Consuming a selection ---------------------------------------------------------------

        // THE COMPOSED COLLAPSE, and the pin the whole gesture rests on: a mass backspace to the
        // anchor and then an ordinary judged keypress there. Both arms must land on identical cell
        // states, identical typed chars and an identical score, or a browser retype is worth a
        // different number of points from a desktop one on the same leaderboard.
        new Scenario
        {
            Name = "consumingASelectionErasesToTheAnchorAndTypesThere",
            Osu = AbCdEf(),
            Steps =
            [
                Key('a', a_t), Key('x', b_t), Key(' ', gap1_t), Key('c', c_t),
                CtrlA(), Churn(),
                Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t), Key('d', d_t),
            ],
        },

        // The selection reaching back over cells a word skip ABANDONED collapses through them in the
        // usual transparent way, so the run never stalls on a phantom cell.
        new Scenario
        {
            Name = "aSelectionCollapsesThroughAbandonedCells",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps =
            [
                Key('x', a_t), Key(' ', 1200), Key('c', c_t),
                CtrlA(), Churn(), Key('a', a_t), Key('b', b_t),
            ],
        },

        // An erase key over a live selection collapses it and types nothing, at BOTH widths: the
        // Ctrl combo does not word-erase on top of the collapse, and the plain key does not erase one
        // extra cell.
        new Scenario
        {
            Name = "ctrlBackspaceOverALiveSelectionCollapsesIt",
            Osu = AbCdEf(),
            Steps =
            [
                Key('a', a_t), Key('x', b_t), Key(' ', gap1_t), Key('c', c_t),
                CtrlA(), CtrlBackspace(), CtrlBackspace(),
            ],
        },
        new Scenario
        {
            Name = "plainBackspaceOverALiveSelectionCollapsesIt",
            Osu = AbCdEf(),
            Steps =
            [
                Key('a', a_t), Key('x', b_t), Key(' ', gap1_t), Key('c', c_t),
                CtrlA(), Backspace(), Backspace(),
            ],
        },

        // Pressing Ctrl+A again with one already open recomputes the same range rather than growing
        // it, and a selection whose line goes out from under it is dropped rather than left stale.
        new Scenario
        {
            Name = "aStaleSelectionIsDroppedWhenTheLineGoes",
            Osu = AbCdEf(),
            Steps = [Key('a', a_t), Key('x', b_t), CtrlA(), CtrlA(), Update(61000), Churn(), CtrlA()],
        },

        // ---- Space discipline (backlog 184) ------------------------------------------------------

        // THE PARK, end to end. With word skipping on, a wrong letter on the word GAP spoils it
        // WITHOUT moving the caret, a second wrong letter overwrites that same cell rather than
        // spoiling the next one, the space then STEPS OVER the typo (crediting accuracy, judging
        // nothing, leaving the cell wrong), and the next word types out clean. Ctrl+A in the middle
        // of it is a deliberate no-op: the parked typo sits AT the caret, outside the scan's
        // [0, caret) range, because it is one backspace away rather than a selection away.
        new Scenario
        {
            Name = "aGapTypoParksTheCaretAndTheSpaceStepsOverIt",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps =
            [
                Key('a', a_t), Key('b', b_t), Key('c', gap1_t), Churn(), CtrlA(),
                Key('z', gap1_t), Churn(), Key(' ', gap1_t),
                Key('c', c_t), Key('d', d_t), Key(' ', gap2_t), Key('e', e_t), Key('f', f_t),
            ],
        },

        // Backspace on a parked gap clears it WHERE IT SITS: one press, one cell, caret unmoved, and
        // the perfectly good word behind it untouched. The corrected space then earns the cell and
        // the streak the typo broke, through the existing combo-restore machinery.
        new Scenario
        {
            Name = "backspaceClearsAParkedGapInPlace",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps =
            [
                Key('a', a_t), Key('b', b_t), Key('x', gap1_t), Churn(), Backspace(), Churn(),
                Key(' ', gap1_t), Key('c', c_t),
            ],
        },

        // The other half of backlog 184, on the arm the browser actually plays: with no word to skip
        // a SPACE inside a word is nothing but a wrong character, so it is typed through into the
        // cell exactly as a wrong letter is. The anchor then treats it as the ordinary lyric typo it
        // is (the head of "ab", not the cell itself, because a lyric typo is retyped with its word),
        // and the erase key over that selection collapses it, after which the word types out clean.
        new Scenario
        {
            Name = "aMidWordSpaceIsTypedThroughAsAnOrdinaryTypo",
            Osu = AbCdEf(),
            Steps =
            [
                Key('a', a_t), Key(' ', b_t), Churn(), CtrlA(), Backspace(),
                Key('a', a_t), Key('b', b_t), Key(' ', gap1_t), Key('c', c_t),
            ],
        },

        // PR 2's erase-run fix. A lyric typo on the 'b' moves the caret onto the gap, and a wrong
        // letter there PARKS it (skip arm). Ctrl+A anchors on the head of "ab", so the selection
        // ENDS on the parked gap. The next letter collapses it: the first erase clears the parked
        // gap IN PLACE (caret unmoved), which the loop used to read as no progress and stop on,
        // leaving "ab" standing and landing the letter on the just-cleared gap as a fresh typo. Now
        // the run carries on through the word and the letter lands on the anchor.
        new Scenario
        {
            Name = "aSelectionEndingOnAParkedGapErasesThroughIt",
            Osu = AbCdEf(),
            SpaceSkipsWord = true,
            Steps =
            [
                Key('a', a_t), Key('x', b_t), Key('z', gap1_t), Churn(), CtrlA(),
                Key('a', gap1_t), Key('b', gap1_t), Key(' ', gap1_t), Key('c', c_t),
            ],
        },

        // ---- A mark inside a word (Literate) -----------------------------------------------------

        // Under Literate the apostrophe is a first-class typeable cell, and it must still NOT read as
        // a word boundary. Two halves, kept apart because an erase key over a live selection
        // collapses it (so a Ctrl+Backspace after a Ctrl+A would be measuring the collapse, not the
        // word width): the ANCHOR half says a typo on the mark anchors on the head of "don't", and
        // the WIDTH half says Ctrl+Backspace walks straight through the mark to take the whole word.
        new Scenario
        {
            Name = "aTypoOnAMarkAnchorsOnItsWholeWord",
            Osu = DontGo(),
            Literate = true,
            Steps = [.. TypeDontGoWithATypoOnTheMark(), Churn(), CtrlA()],
        },
        new Scenario
        {
            Name = "aMarkIsNotAWordBoundary",
            Osu = DontGo(),
            Literate = true,
            Steps = [.. TypeDontGoWithATypoOnTheMark(), Churn(), CtrlBackspace(), Churn(), CtrlBackspace()],
        },
    ];

    /// <summary>"don't g" under Literate, with a wrong letter typed into the apostrophe cell.</summary>
    private static Step[] TypeDontGoWithATypoOnTheMark() =>
    [
        Key('d', 1000), Key('o', 1250), Key('n', 1500), Key('x', 1625), Key('t', 1750),
        Key(' ', 2000), Key('g', 2000),
    ];

    #endregion

    #region Driving the two sides

    private static readonly Scenario[] all_scenarios = BuildScenarios();

    private static readonly JsonSerializerOptions payload_options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The browser arm: the scenarios written to a temp file and run through the shared node
    /// harness against the SERVED <c>wwwroot/js/typebeat-core.js</c>.
    /// </summary>
    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        string json = JsonSerializer.Serialize(new { scenarios = all_scenarios }, payload_options);
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-wordinput-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        try
        {
            return NodeHarness.Run("CoreWordInputHarness.cjs", path);
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
    });

    /// <summary>The map as the GAME's production decoder reads it, so both arms read one file.</summary>
    private static LyricBeatmap Load(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        var objects = decoded.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

        return new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = objects.Select(h => h.Line).ToList(),
            Granularity = objects.Count > 0 ? objects[0].Granularity : TimingGranularity.Line,
        };
    }

    /// <summary>
    /// The engine plus the pure UI state the input layer holds beside it: the live retype selection
    /// (<c>TypeBeatPlayfield.CurrentRetypeSelection</c> on the desktop, a local in the browser
    /// player) and the count of erases the composed gestures have made.
    /// </summary>
    private sealed class Session
    {
        public required TypingEngine Engine { get; init; }
        public (int LineIndex, int StartCell, int EndCell)? Selection { get; set; }
        public int Erases { get; set; }
    }

    /// <summary>
    /// A started engine on the scenario's map, under every LIVE rule, which is the only arm the
    /// browser can be compared against: it has no mods payload, writes no replay frames and
    /// re-derives no stored row. AllowWrongInput is the default; WrongInputOnWordGaps and
    /// StrictSpaces have to be set by hand here because the C# keeps both as era arms (CONFIG flags
    /// bits 3 and 4) and the browser is permanently on their live side (see the notes in
    /// typebeat-core.js's wrong-key path).
    ///
    /// <para>The JUDGEMENT era bits are set here too (2, 5 to 8, 10 to 12 and 16), the full live set
    /// every other parity fixture in this project carries. They were missing until PR 2's parked
    /// erase scenario pressed a retyped cell late inside its syllable: the browser judged it on the
    /// span and the point-rule C# arm did not, and every earlier scenario had pressed dead on target,
    /// where the two rules agree.</para>
    /// </summary>
    private static Session Started(Scenario scenario)
    {
        var engine = new TypingEngine(Load(scenario.Osu), scenario.Literate)
        {
            SyllableTiming = true,
            CharTimedStretch = true,
            FirstCharTiming = true,
            FletcherEnabled = true,
            FlexibleLineSnap = true,
            BoundedRush = true,
            WrongInputOnWordGaps = true,
            StrictSpaces = true,
            SpaceSkipsWord = scenario.SpaceSkipsWord,
            BackDatedSealBreak = true,
            LosslessSkipReclaim = true,
            FoldsDisplacedClaim = true,
            FirstLineLeadIn = true,
            // Backlog 347, the first bit of the SECOND CONFIG flags word: the browser takes it unconditionally.
            RushCapCostsAccuracy = true,
            InputEra2 = true,
            AuthoredSyllablesOnly = true,
            AlignSubdivisionTargets = true,
            // EARLY FINISH (bit 4 of the second CONFIG flags word, the 0x01 CONFIG_EXTENDED carrier):
            // the final line seals the moment it is fully typed, so a bare live engine needs it set.
            EarlyFinish = true,
            ManualNewlines = true,
            NewlineOnTypedLetter = true,
        };

        engine.Update(scenario.StartTime);
        return new Session { Engine = engine };
    }

    /// <summary>
    /// <c>TypeBeatKeyHandler.eraseBackTo</c>, and the same loop the browser player runs: ordinary
    /// backspaces back to a target, with the defensive no-progress break (an erase that reclaimed
    /// abandoned cells at the head of a line can land on 0 and be auto-skipped forward again, and a
    /// gesture must never spin). Since PR 2 the in-place clear of a PARKED typo is let through that
    /// break (<see cref="TypingEngine.CaretOnParkedTypo"/>, read BEFORE the press as the playfield
    /// reads it).
    /// </summary>
    private static int EraseBackTo(TypingEngine engine, int target)
    {
        int erases = 0;

        while (engine.CaretIndex > target)
        {
            int before = engine.CaretIndex;
            bool parked = engine.CaretOnParkedTypo;

            if (!engine.ProcessBackspace())
                break;

            erases++;

            if (engine.CaretIndex >= before && !parked)
                break;
        }

        return erases;
    }

    /// <summary><c>TypeBeatKeyHandler.collapseSelection</c>: the selection is dropped BEFORE the
    /// erases so the staleness check cannot race them.</summary>
    private static bool CollapseSelection(Session session)
    {
        if (session.Selection is null)
            return false;

        int startCell = session.Selection.Value.StartCell;

        session.Selection = null;
        session.Erases += EraseBackTo(session.Engine, startCell);
        return true;
    }

    /// <summary><c>TypeBeatPlayfield.Update</c>'s staleness drop, which both clients run per frame:
    /// a selection whose line or caret no longer match it is stale and goes.</summary>
    private static void DropStaleSelection(Session session)
    {
        if (session.Selection is not { } selection)
            return;

        if (session.Engine.ActiveLineIndex != selection.LineIndex || session.Engine.CaretIndex != selection.EndCell)
            session.Selection = null;
    }

    private static void RunStep(Session session, Step step)
    {
        var engine = session.Engine;

        switch (step.Op)
        {
            case "update":
                engine.Update(step.T!.Value);
                break;

            case "key":
                // A typeable key: the selection is consumed FIRST, so the key lands on the anchor.
                CollapseSelection(session);
                engine.ProcessKey(step.C![0], step.T!.Value);
                break;

            case "backspace":
                if (!CollapseSelection(session) && engine.ProcessBackspace())
                    session.Erases++;
                break;

            case "ctrlBackspace":
                if (!CollapseSelection(session))
                    session.Erases += EraseBackTo(engine, engine.WordBackspaceTarget);
                break;

            case "churn":
                // PURITY. Reading each query n times over a mid-run engine must leave every
                // observable where the probe before this step found it, which the probe after it
                // then asserts on both arms. The sum is accumulated (and read) so the calls cannot
                // be optimised away as dead.
                int sink = 0;

                for (int i = 0; i < step.N!.Value; i++)
                    sink += engine.WordBackspaceTarget + engine.RetypeSelectionAnchor;

                if (sink == int.MinValue)
                    throw new InvalidOperationException("unreachable");

                break;

            case "ctrlA":
                int anchor = engine.RetypeSelectionAnchor;

                // No typo behind the caret: nothing to select and nothing to clear.
                if (anchor >= 0)
                    session.Selection = (engine.ActiveLineIndex, anchor, engine.CaretIndex);

                break;

            default:
                throw new ArgumentException($"unknown op {step.Op}");
        }

        DropStaleSelection(session);
    }

    /// <summary>Everything both arms can see, taken after every step.</summary>
    private sealed record Probe(
        int CaretIndex,
        int ActiveLineIndex,
        bool Finished,
        int WordBackspaceTarget,
        int RetypeSelectionAnchor,
        int SelectionStart,
        int SelectionEnd,
        int Erases,
        string[] States,
        string[] Typed,
        long Score,
        int Combo,
        int MaxCombo,
        int Mistypes,
        double Accuracy);

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

    private static Probe Take(Session session)
    {
        var engine = session.Engine;
        var cells = engine.Lines[0].Cells;

        return new Probe(
            engine.CaretIndex,
            engine.ActiveLineIndex,
            engine.IsFinished,
            engine.WordBackspaceTarget,
            engine.RetypeSelectionAnchor,
            session.Selection?.StartCell ?? -1,
            session.Selection?.EndCell ?? -1,
            session.Erases,
            cells.Select(c => StateName(c.State)).ToArray(),
            cells.Select(c => c.TypedChar?.ToString() ?? string.Empty).ToArray(),
            engine.Score,
            engine.Combo,
            engine.MaxCombo,
            engine.Mistypes,
            engine.LiveAccuracy);
    }

    private static Probe[] GameProbes(Scenario scenario)
    {
        var session = Started(scenario);
        var probes = new List<Probe> { Take(session) };

        foreach (var step in scenario.Steps)
        {
            RunStep(session, step);
            probes.Add(Take(session));
        }

        return probes.ToArray();
    }

    private static JsonElement BrowserScenario(string name)
    {
        foreach (var one in browser.Value.GetProperty("scenarios").EnumerateArray())
        {
            if (one.GetProperty("name").GetString() == name)
                return one;
        }

        throw new AssertionException($"the harness emitted no scenario named {name}");
    }

    private static string[] Strings(JsonElement element, string key)
        => element.GetProperty(key).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

    #endregion

    /// <summary>
    /// Both loaders build the same cells for every fixture. Asserted first and separately, so a
    /// divergence in the step streams below can never be explained away as the two clients reading
    /// the map differently.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryFixture()
    {
        Assert.Multiple(() =>
        {
            foreach (var scenario in all_scenarios)
            {
                var cells = new TypingEngine(Load(scenario.Osu), scenario.Literate) { AlignSubdivisionTargets = true }.Lines[0].Cells;
                var browserScenario = BrowserScenario(scenario.Name);

                var expected = browserScenario.GetProperty("expected");
                var targets = browserScenario.GetProperty("targets");
                var typeable = browserScenario.GetProperty("typeable");

                Assert.That(expected.GetArrayLength(), Is.EqualTo(cells.Count), $"{scenario.Name}: cell count");

                for (int i = 0; i < cells.Count && i < expected.GetArrayLength(); i++)
                {
                    Assert.That(expected[i].GetString(), Is.EqualTo(cells[i].Expected.ToString()), $"{scenario.Name}[{i}]: expected");
                    Assert.That(targets[i].GetDouble(), Is.EqualTo(cells[i].TargetTime), $"{scenario.Name}[{i}]: target");
                    Assert.That(typeable[i].GetBoolean(), Is.EqualTo(cells[i].IsTypeable), $"{scenario.Name}[{i}]: typeable");
                }
            }
        });
    }

    /// <summary>
    /// THE PIN. Step for step, the two engines answer both queries identically and the composed
    /// gestures leave them in identical states: same caret, same selection held by the input layer,
    /// same cell states and typed chars, same score, combo, max combo, mistypes and accuracy.
    ///
    /// <para>Both queries are read on EVERY probe, including the ones after a <c>churn</c> step that
    /// read each of them a hundred times, so the purity the whole design rests on (they are pure, so
    /// they can be plain getters in the JS port and the gesture can live entirely in the input
    /// layer) is pinned on both arms rather than assumed on one.</para>
    /// </summary>
    [Test]
    public void EveryStepOfEveryGestureAgrees()
    {
        int comparedProbes = 0;

        Assert.Multiple(() =>
        {
            foreach (var scenario in all_scenarios)
            {
                var game = GameProbes(scenario);
                var browserProbes = BrowserScenario(scenario.Name).GetProperty("probes");

                Assert.That(browserProbes.GetArrayLength(), Is.EqualTo(game.Length), $"{scenario.Name}: probe count");

                for (int i = 0; i < game.Length && i < browserProbes.GetArrayLength(); i++)
                {
                    var mine = game[i];
                    var theirs = browserProbes[i];
                    string what = $"{scenario.Name} after step {i}";

                    Assert.That(theirs.GetProperty("caretIndex").GetInt32(), Is.EqualTo(mine.CaretIndex), $"{what}: caret");
                    Assert.That(theirs.GetProperty("activeLineIndex").GetInt32(), Is.EqualTo(mine.ActiveLineIndex), $"{what}: active line");
                    Assert.That(theirs.GetProperty("finished").GetBoolean(), Is.EqualTo(mine.Finished), $"{what}: finished");
                    Assert.That(theirs.GetProperty("wordBackspaceTarget").GetInt32(), Is.EqualTo(mine.WordBackspaceTarget), $"{what}: wordBackspaceTarget");
                    Assert.That(theirs.GetProperty("retypeSelectionAnchor").GetInt32(), Is.EqualTo(mine.RetypeSelectionAnchor), $"{what}: retypeSelectionAnchor");
                    Assert.That(theirs.GetProperty("selectionStart").GetInt32(), Is.EqualTo(mine.SelectionStart), $"{what}: selection start");
                    Assert.That(theirs.GetProperty("selectionEnd").GetInt32(), Is.EqualTo(mine.SelectionEnd), $"{what}: selection end");
                    Assert.That(theirs.GetProperty("erases").GetInt32(), Is.EqualTo(mine.Erases), $"{what}: erases made");
                    Assert.That(Strings(theirs, "states"), Is.EqualTo(mine.States), $"{what}: cell states");
                    Assert.That(Strings(theirs, "typed"), Is.EqualTo(mine.Typed), $"{what}: typed chars");
                    Assert.That(theirs.GetProperty("score").GetInt64(), Is.EqualTo(mine.Score), $"{what}: score");
                    Assert.That(theirs.GetProperty("combo").GetInt32(), Is.EqualTo(mine.Combo), $"{what}: combo");
                    Assert.That(theirs.GetProperty("maxCombo").GetInt32(), Is.EqualTo(mine.MaxCombo), $"{what}: max combo");
                    Assert.That(theirs.GetProperty("mistypes").GetInt32(), Is.EqualTo(mine.Mistypes), $"{what}: mistypes");
                    Assert.That(theirs.GetProperty("accuracy").GetDouble(), Is.EqualTo(mine.Accuracy), $"{what}: accuracy");

                    comparedProbes++;
                }
            }
        });

        Assert.That(comparedProbes, Is.GreaterThan(0), "the harness produced nothing to compare");
    }

    /// <summary>
    /// COVERAGE, on the C# arm's own probes: the sweep above would still pass if every scenario had
    /// quietly stopped exercising the thing it was written for (two arms agreeing on the wrong answer
    /// agree just as well). So each scenario's load-bearing number is asserted here, in the
    /// vocabulary of the desktop spec it came from.
    /// </summary>
    [Test]
    public void EachScenarioExercisesTheCaseItWasWrittenFor()
    {
        var probes = all_scenarios.ToDictionary(s => s.Name, GameProbes);

        Assert.Multiple(() =>
        {
            // Ctrl+Backspace, the four widths.
            Assert.That(probes["headOfLineIsANoOp"][0].WordBackspaceTarget, Is.Zero, "the caret itself, so the gesture never calls the engine");
            Assert.That(probes["headOfLineIsANoOp"][^1].Erases, Is.Zero, "and it erased nothing");
            Assert.That(probes["headOfLineIsANoOp"][^1].RetypeSelectionAnchor, Is.EqualTo(-1), "with no typo anywhere there is no anchor");

            Assert.That(probes["midWordErasesToThatWordsStart"][^2].WordBackspaceTarget, Is.EqualTo(3), "the start of \"cd\", the word the caret is inside");
            Assert.That(probes["midWordErasesToThatWordsStart"][^1].CaretIndex, Is.EqualTo(3));

            Assert.That(probes["atAWordStartItTakesTheGapAndThePreviousWord"][^2].WordBackspaceTarget, Is.Zero, "the gap AND the whole of \"ab\"");
            Assert.That(probes["atAWordStartItTakesTheGapAndThePreviousWord"][^1].Erases, Is.EqualTo(3));

            Assert.That(probes["atTheEndOfTheLineItTakesTheLastWord"][^2].CaretIndex, Is.EqualTo(8), "a line-complete caret");
            Assert.That(probes["atTheEndOfTheLineItTakesTheLastWord"][^2].WordBackspaceTarget, Is.EqualTo(6), "the start of \"ef\"");

            Assert.That(probes["holdingItWalksBackWordByWordToTheHead"].Select(p => p.CaretIndex).TakeLast(5),
                Is.EqualTo(new[] { 8, 6, 3, 0, 0 }), "word by word to the head, then a dead stop");

            Assert.That(probes["itReclaimsASkippedWordLikeThePlainKey"][^2].States[1], Is.EqualTo("abandoned"), "the skip left a phantom cell");
            Assert.That(probes["itReclaimsASkippedWordLikeThePlainKey"][^1].Erases, Is.EqualTo(2), "undo the skip and its gap, then erase 'a' (PR 3)");
            Assert.That(probes["itReclaimsASkippedWordLikeThePlainKey"][^1].CaretIndex, Is.Zero);
            Assert.That(probes["itReclaimsASkippedWordLikeThePlainKey"][^1].States[1], Is.EqualTo("untyped"), "the abandoned cell was reclaimed");

            // Ctrl+A, the anchor rules.
            Assert.That(probes["withNoTypoBehindTheCaretThereIsNoAnchor"][^1].RetypeSelectionAnchor, Is.EqualTo(-1));
            Assert.That(probes["withNoTypoBehindTheCaretThereIsNoAnchor"][^1].SelectionStart, Is.EqualTo(-1), "so Ctrl+A selected nothing");

            Assert.That(probes["anErasedTypoIsNotAnUnfixedOne"][1].RetypeSelectionAnchor, Is.Zero, "the typo is there");
            Assert.That(probes["anErasedTypoIsNotAnUnfixedOne"][^1].RetypeSelectionAnchor, Is.EqualTo(-1), "and gone once backspaced away");

            Assert.That(probes["aTypoAnchorsOnItsOwnWordsStart"][^1].SelectionStart, Is.Zero, "the head of \"ab\"");
            Assert.That(probes["aTypoAnchorsOnItsOwnWordsStart"][^1].SelectionEnd, Is.EqualTo(4), "back to the caret");

            Assert.That(probes["aTypoInTheCurrentPartialWordAnchorsOnThatWord"][^1].SelectionStart, Is.EqualTo(3), "the head of \"cd\", the only typo's own word");

            Assert.That(probes["theEarliestTypoBehindTheCaretWins"][^2].States[0], Is.EqualTo("wrong"), "a typo in the first word");
            Assert.That(probes["theEarliestTypoBehindTheCaretWins"][^2].States[3], Is.EqualTo("wrong"), "and another in the second");
            Assert.That(probes["theEarliestTypoBehindTheCaretWins"][^1].SelectionStart, Is.Zero, "the EARLIER typo's word, so one gesture offers both back");
            Assert.That(probes["theEarliestTypoBehindTheCaretWins"][^1].SelectionEnd, Is.EqualTo(7), "back to the caret in the third word");

            Assert.That(probes["aGapTypoAnchorsOnThePrecedingWord"][^2].States[2], Is.EqualTo("wrong"), "a wrong letter landed on the word gap");
            Assert.That(probes["aGapTypoAnchorsOnThePrecedingWord"][^1].SelectionStart, Is.Zero, "the head of \"ab\", selected with its gap (PR 3)");

            Assert.That(probes["anEarlierTypoOutranksALaterAbandonedWord"][^2].States[0], Is.EqualTo("wrong"), "the typo in \"ab\"");
            Assert.That(probes["anEarlierTypoOutranksALaterAbandonedWord"][^2].States[3], Is.EqualTo("abandoned"), "the later word skip's abandoned \"c\"");
            Assert.That(probes["anEarlierTypoOutranksALaterAbandonedWord"][^1].SelectionStart, Is.Zero, "the earlier typo's word wins, not the later abandoned one");

            // PR 3: the anchor is the wholly abandoned word's own head, where the era's backspace stops.
            var whollyAbandoned = probes["aWhollyAbandonedWordAnchorsOnItsHead"];
            Assert.That(whollyAbandoned[4].States.Skip(3).Take(2), Is.All.EqualTo("abandoned"), "the space gave up the whole of \"cd\"");
            Assert.That(whollyAbandoned[4].CaretIndex, Is.EqualTo(6), "and the space was judged on the gap after it");
            Assert.That(whollyAbandoned[4].RetypeSelectionAnchor, Is.EqualTo(3), "the head of the skipped word");
            Assert.That(whollyAbandoned[6].SelectionStart, Is.EqualTo(3), "so that is what Ctrl+A offered");
            Assert.That(whollyAbandoned[6].SelectionEnd, Is.EqualTo(6));
            Assert.That(whollyAbandoned[7].Erases, Is.EqualTo(1), "one press undoes the skip and the gap it typed");
            Assert.That(whollyAbandoned[7].CaretIndex, Is.EqualTo(4), "the collapse landed ON the anchor and the 'c' then typed there");
            Assert.That(whollyAbandoned[7].States[2], Is.EqualTo("correct"), "the gap in front of the word was preserved");
            Assert.That(whollyAbandoned[7].States[3], Is.EqualTo("correct"));
            Assert.That(whollyAbandoned[^1].States, Is.All.EqualTo("correct"), "and the line typed out clean");
            Assert.That(whollyAbandoned[^1].Mistypes, Is.Zero, "the correction manufactured no mistake of its own");

            Assert.That(probes["aLineCompleteCaretStillAnchorsAndConsumes"][^4].CaretIndex, Is.EqualTo(8), "line complete");
            Assert.That(probes["aLineCompleteCaretStillAnchorsAndConsumes"][^3].SelectionStart, Is.EqualTo(6), "the head of \"ef\"");
            Assert.That(probes["aLineCompleteCaretStillAnchorsAndConsumes"][^1].States, Is.All.EqualTo("correct"), "and the word was retyped clean");

            // The composed collapse.
            var consume = probes["consumingASelectionErasesToTheAnchorAndTypesThere"];
            Assert.That(consume[^7].SelectionStart, Is.Zero, "the selection was open on \"ab \" plus the 'c'");
            Assert.That(consume[^7].SelectionEnd, Is.EqualTo(4));
            Assert.That(consume[^6].SelectionStart, Is.Zero, "reading the queries a hundred times did not consume it");
            Assert.That(consume[^5].Erases, Is.EqualTo(4), "the letter collapsed four cells before it landed");
            Assert.That(consume[^5].CaretIndex, Is.EqualTo(1), "and then typed at the anchor");
            Assert.That(consume[^5].SelectionStart, Is.EqualTo(-1), "and the selection is gone with it");
            Assert.That(consume[^1].States.Take(5), Is.All.EqualTo("correct"), "every cell the selection covered was retyped");
            Assert.That(consume[^1].Mistypes, Is.EqualTo(1), "the typo is still counted; the retype fixed the cell, not the history");

            var abandoned = probes["aSelectionCollapsesThroughAbandonedCells"];
            Assert.That(abandoned[2].States[1], Is.EqualTo("abandoned"), "the space gave up the rest of \"ab\"");
            Assert.That(abandoned[^3].SelectionStart, Is.Zero, "the typo is at the head of \"ab\"");
            Assert.That(abandoned[^2].Erases, Is.EqualTo(3), "'c', then the undo of the skip and its gap, then the typo (PR 3)");

            // An erase key over a live selection collapses it and types nothing, at both widths.
            foreach (string name in new[] { "ctrlBackspaceOverALiveSelectionCollapsesIt", "plainBackspaceOverALiveSelectionCollapsesIt" })
            {
                Assert.That(probes[name][^2].CaretIndex, Is.Zero, $"{name}: the collapse alone took the caret home");
                Assert.That(probes[name][^2].Erases, Is.EqualTo(4), $"{name}: exactly the four cells the selection covered");
                Assert.That(probes[name][^2].States, Is.All.EqualTo("untyped"), $"{name}: and nothing was typed");
            }

            var stale = probes["aStaleSelectionIsDroppedWhenTheLineGoes"];
            Assert.That(stale[^4].SelectionStart, Is.Zero, "a second Ctrl+A recomputed the same range");
            Assert.That(stale[^4].SelectionEnd, Is.EqualTo(2));
            Assert.That(stale[^3].ActiveLineIndex, Is.EqualTo(-1), "the line sealed out from under it");
            Assert.That(stale[^3].SelectionStart, Is.EqualTo(-1), "so the selection was dropped");
            Assert.That(stale[^1].WordBackspaceTarget, Is.EqualTo(stale[^1].CaretIndex), "and both queries answer inertly off a finished run");
            Assert.That(stale[^1].RetypeSelectionAnchor, Is.EqualTo(-1));

            // Space discipline (backlog 184): the park, its in-place erase, and the mid-word typo.
            var park = probes["aGapTypoParksTheCaretAndTheSpaceStepsOverIt"];
            Assert.That(park[3].CaretIndex, Is.EqualTo(2), "the caret PARKED on the gap it spoiled");
            Assert.That(park[3].States[2], Is.EqualTo("wrong"));
            Assert.That(park[3].Typed[2], Is.EqualTo("c"));
            Assert.That(park[5].RetypeSelectionAnchor, Is.EqualTo(-1), "a typo AT the caret is outside the scan");
            Assert.That(park[5].SelectionStart, Is.EqualTo(-1), "so Ctrl+A selected nothing");
            Assert.That(park[6].Typed[2], Is.EqualTo("z"), "a second wrong letter overwrote the same cell");
            Assert.That(park[6].CaretIndex, Is.EqualTo(2), "and still did not move the caret");
            Assert.That(park[6].States.Count(s => s == "wrong"), Is.EqualTo(1), "one spoiled cell, not two");
            Assert.That(park[8].CaretIndex, Is.EqualTo(3), "the space stepped over the gap");
            Assert.That(park[8].States[2], Is.EqualTo("wrong"), "leaving the typo standing");
            Assert.That(park[8].Accuracy, Is.EqualTo(3 / 5.0).Within(1e-12), "and counting itself CORRECT: 2 letters + this space, over 5 presses");
            Assert.That(park[^1].States.Count(s => s == "correct"), Is.EqualTo(7), "the rest of the line typed out clean");
            Assert.That(park[^1].Mistypes, Is.EqualTo(2), "both attempts at the gap are still wrong keypresses");

            var parkedErase = probes["backspaceClearsAParkedGapInPlace"];
            Assert.That(parkedErase[3].CaretIndex, Is.EqualTo(2), "parked again");
            Assert.That(parkedErase[5].States[2], Is.EqualTo("untyped"), "the parked cell was cleared");
            Assert.That(parkedErase[5].States[1], Is.EqualTo("correct"), "and the word in front of it was not touched");
            Assert.That(parkedErase[5].CaretIndex, Is.EqualTo(2), "with the caret exactly where it was");
            Assert.That(parkedErase[5].Erases, Is.EqualTo(1), "one press, one cell");
            Assert.That(parkedErase[^2].States[2], Is.EqualTo("correct"), "the space then earns the gap");
            Assert.That(parkedErase[^2].Combo, Is.EqualTo(3), "at the streak the typo broke, put back");

            var midWordSpace = probes["aMidWordSpaceIsTypedThroughAsAnOrdinaryTypo"];
            Assert.That(midWordSpace[2].States[1], Is.EqualTo("wrong"), "the cell took the space");
            Assert.That(midWordSpace[2].Typed[1], Is.EqualTo(" "));
            Assert.That(midWordSpace[2].CaretIndex, Is.EqualTo(2), "and the caret moved on with it");
            Assert.That(midWordSpace[2].Accuracy, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(midWordSpace[4].SelectionStart, Is.Zero, "the anchor is the head of \"ab\", as for any lyric typo");
            Assert.That(midWordSpace[5].CaretIndex, Is.Zero, "the erase key collapsed that selection");
            Assert.That(midWordSpace[5].Erases, Is.EqualTo(2));
            Assert.That(midWordSpace[^1].States.Take(4), Is.All.EqualTo("correct"), "and the word was retyped clean");

            // A mark is a cell but never a boundary.
            var markAnchor = probes["aTypoOnAMarkAnchorsOnItsWholeWord"];
            Assert.That(markAnchor[^2].States[3], Is.EqualTo("wrong"), "the typo landed on the apostrophe cell");
            Assert.That(markAnchor[^1].SelectionStart, Is.Zero, "which anchors on the head of \"don't\", not on the mark");
            Assert.That(markAnchor[^1].SelectionEnd, Is.EqualTo(7));

            var markWidth = probes["aMarkIsNotAWordBoundary"];
            Assert.That(markWidth[^4].WordBackspaceTarget, Is.EqualTo(6), "the caret is inside \"go\"");
            Assert.That(markWidth[^3].CaretIndex, Is.EqualTo(6), "so Ctrl+Backspace took its one letter");
            Assert.That(markWidth[^2].WordBackspaceTarget, Is.Zero, "the gap AND the whole of \"don't\", mark included");
            Assert.That(markWidth[^1].CaretIndex, Is.Zero);
            Assert.That(markWidth[^1].States, Is.All.EqualTo("untyped"), "the mark was erased with the rest of its word");
        });
    }
}
