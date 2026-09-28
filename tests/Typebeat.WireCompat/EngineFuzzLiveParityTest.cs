using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using Typebeat.Tools.ScoreRecalc;

namespace Typebeat.WireCompat;

/// <summary>
/// The DIFFERENTIAL cross-repo pin on the browser scoring core (backlog 172): long, mixed,
/// generated keystroke streams replayed through the browser's <c>typebeat-core.js</c> and through
/// the GAME's own <see cref="TypeBeatReplayScorer"/>, compared field for field with no tolerance.
///
/// <para>Every other JS fidelity guard, this project's <see cref="WordSkipLiveParityTest"/>
/// included, pins ONE named rule through a hand-written sequence. That is the right shape for a
/// rule somebody thought about, and it is why each of them exists; what none of them can do is
/// catch a rule nobody wrote a scenario for, or an interaction between two of them. The two
/// clients submit to the SAME leaderboards, so any such gap is a browser play that scores
/// differently from the identical desktop performance. This test is the sweep: the harness
/// generates streams that mix correct presses at every judgement tier, typed-through typos,
/// backspaces, rejected keys, word skips and idle stretches that seal lines, and emits both what
/// it played and what the browser made of it. The C# side plays the identical script.</para>
///
/// <para>The FIXTURES are pinned before the accounts are. Both sides build their cells through
/// their own loader (the browser's <c>buildBeatmap</c>, the game's
/// <see cref="TypingLine.FromLyricLine"/>), so a fixture that drifted would show up as an account
/// divergence and be blamed on the engine. <see cref="TheTwoLoadersAgreeOnEveryFixture"/> runs
/// first in intent: it holds every cell's expected char and target time, plus the line's deadline,
/// cue and seal grace, against each other. There is no per-cell judge tier left to hold: one
/// symmetric ladder (Great 150 / Ok 300 / Meh 600) now grades every cell of every map, the beatmap
/// granularity selects nothing, and <c>TypingCell.JudgeGranularity</c> and the browser's matching
/// <c>tier</c> field went with it.</para>
///
/// <para>The C# side is fed under every LIVE rule, because the browser selects no era on any axis:
/// it has no mods payload, no replay input, and nothing anywhere re-scores a stored row through
/// that file. Since backlog 179 that includes SYLLABLE-SPAN judgement, since backlog 181 the
/// WORD-GAP input model and since backlog 209 the STRETCH narrowing of the span rule, all of which
/// the game reads off each replay's own CONFIG frame and defaults OFF, so the generated frames here
/// have to set bits 2, 3 and 6 or the C# arm would re-derive on point targets while the browser
/// judges spans, reject the wrong keys the browser types into word gaps, and pay a mashed freestyle
/// section the delta of zero the browser charges for (see <see cref="Keystrokes"/>). The places
/// those rules are asserted APART rather than together are
/// <see cref="ClearingTheConfigFrameSyllableBitReDerivesTheClassicRule"/>,
/// <see cref="ClearingTheConfigFrameWordGapBitRejectsTheTypoInstead"/> and
/// <see cref="ClearingTheConfigFrameStretchBitPaysTheMashedRunInFull"/>.</para>
///
/// <para>A handful of the cases are SCRIPTED rather than generated, and they are played and
/// compared identically. The generator reaches what it happens to roll, and backlog 176 found a
/// rule it never once rolled the deciding shape for: two redeemable breaks in a row with nothing
/// earned between them, and a walk back into the older one. The scripted cases are written out in
/// the harness beside the generator, so a shape that matters is not left to a seed.</para>
/// </summary>
[TestFixture]
public class EngineFuzzLiveParityTest
{
    #region Fixtures, mirroring CoreFuzzHarness.cjs

    private static TimedUnit Unit(string text, double start, double end, double confidence = 1, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, Confidence = confidence, SyllableBoundaries = syllables };

    /// <summary>
    /// A subdivided word carrying an AUTHORED character split (backlog 181), the word-level
    /// <c>split_chars</c> the harness writes into the same fixture's JSON.
    /// </summary>
    private static TimedUnit Authored(string text, double start, double end, int[] splits, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables, SyllableSplits = splits };

    /// <summary>
    /// A word carrying AUTHORED PAUSES (PR 2, the word-level <c>pauses</c> array, or the legacy single
    /// <c>pause</c> object) and optionally boundaries and an authored split. The C# fixture carries
    /// every rest the harness writes, usable or not: <see cref="PausedWord"/> is where both sides
    /// decide which ones count.
    /// </summary>
    private static TimedUnit Paused(string text, double start, double end, int[] splits, WordPause[] pauses, params double[] syllables)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables, SyllableSplits = splits, Pauses = pauses };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    private static LyricLine Estimated(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units, Estimated = true };

    private static TypeBeatBeatmap Map(TimingGranularity granularity, params LyricLine[] lines)
    {
        var map = new TypeBeatBeatmap();

        for (int i = 0; i < lines.Length; i++)
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = granularity });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Song";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    /// <summary>
    /// The same maps the harness writes as map JSON, in the LyricLine shape the game's own tests
    /// use. The line deadlines are what the browser's loader derives: a line ends where the NEXT one
    /// starts, and the last one at min(song end, vocal end + 3000).
    /// </summary>
    private static LyricLine[] Fixture(string name)
    {
        switch (name)
        {
            case "catDog":
                return [Line("cat dog", 1000, 6000, 5000, Unit("cat", 1000, 3000), Unit("dog", 3000, 5000))];

            case "abCd":
                return [Line("ab cd", 1000, 4000, 3000, Unit("ab", 1000, 2000), Unit("cd", 2000, 3000))];

            case "catDogThenHi":
                return
                [
                    Line("cat dog", 1000, 6000, 5000, Unit("cat", 1000, 3000), Unit("dog", 3000, 5000)),
                    Line("hi", 6000, 10000, 7000, Unit("hi", 6000, 7000)),
                ];

            case "quickBrownFox":
                return
                [
                    Line("the quick brown", 1000, 5000, 4000, Unit("the", 1000, 1600), Unit("quick", 1600, 2800), Unit("brown", 2800, 4000)),
                    Line("fox jumps", 5000, 10000, 7000, Unit("fox", 5000, 5800), Unit("jumps", 5800, 7000)),
                ];

            case "mixedTiers":
                return
                [
                    Line("The bad-cat sat.", 1000, 5000, 4000,
                        Unit("The", 1000, 1600),
                        Unit("bad-cat", 1600, 2800, 1, 2200),
                        Unit("sat.", 2800, 4000, 0.1)),
                    Estimated("Oh no", 5000, 9500, 6500, Unit("Oh", 5000, 5700), Unit("no", 5700, 6500)),
                ];

            case "syllabic":
                return [Line("one two", 1000, 6000, 3000, Unit("one", 1000, 2000, 1, 1500), Unit("two", 2000, 3000))];

            case "syllableWords":
                return
                [
                    Line("cake tonight", 1000, 5000, 4000, Unit("cake", 1000, 2400), Unit("tonight", 2400, 4000)),
                    Line("little people", 5000, 11000, 8000, Unit("little", 5000, 6500), Unit("people", 6500, 8000)),
                ];

            case "stylised":
                return [Line("ohhh little", 1000, 6500, 3500, Unit("ohhh", 1000, 2000), Unit("little", 2000, 3500))];

            case "subtimed":
                return
                [
                    Line("cake tonight", 1000, 7000, 4000,
                        Unit("cake", 1000, 2400, 1, 1700),
                        Unit("tonight", 2400, 4000, 1, 2800, 3300)),
                ];

            case "authoredSplit":
                return
                [
                    Line("beautiful tonight", 1000, 7000, 4000,
                        Authored("beautiful", 1000, 1900, [6], 1450),
                        Authored("tonight", 1900, 4000, [2, 5], 2600, 3200)),
                ];

            case "freestyleStretch":
                return
                [
                    Line("&&&& aaaaa", 1000, 20000, 17000,
                        Unit("&&&&", 1000, 5000),
                        Unit("aaaaa", 5000, 17000, 1, 9000)),
                ];

            // Backlog 208's LINE-START SNAP fixture, the game's own
            // FletcherEngineTest.parkedLineMap. THE ONE FIXTURE HERE WHOSE DEADLINES ARE NOT WHAT
            // THE BROWSER'S LOADER DERIVES, and deliberately so: the snap can only decide anything
            // while a FINISHED caret is parked on a line the seal has not reached and the next line
            // has ALREADY started, which needs a line with no cells at all (L1's text is pure
            // punctuation, which the default stream strips) AND a window that outlives the next
            // line's cue (L1 runs to 20000; L2's first vocal is at 12000, so it activates at
            // 10500). The loader makes windows strictly contiguous and drops a cell-less line
            // outright, so the browser harness splices the same three lines in by hand
            // (withParkedMiddleLine) and TheTwoLoadersAgreeOnEveryFixture holds its numbers against
            // the TypingLine built here.
            case "parkedLine":
                return
                [
                    Line("ab", 1000, 3000, 2000, Unit("ab", 1000, 2000)),
                    Line("...", 3000, 20000, 19000, Unit("...", 3000, 19000)),
                    Line("cd", 10000, 30000, 13000, Unit("cd", 12000, 13000)),
                ];

            // Backlog 218's RUSH BOUND fixture, the game's own FletcherEngineTest.instrumentalGapMap,
            // and unlike "parkedLine" above every number in it IS what the browser's loader derives:
            // line windows are contiguous, so the twelve-second instrumental lives inside line 0's own
            // window rather than in a hole between the lines. Line 1's activation is clamped to its
            // own 14000 start, so entry into it opens at 14000 - FLETCHER_DRAG_GRACE_MS = 12500, a
            // second and a half before line 0 could seal at all: a player who types "ab" out on time
            // is refused and parked for eleven seconds. Eight cells in the second line so the rush cap
            // has room to bite on the far side of the deferred roll.
            // PR 2's AUTHORED PAUSES and its stretch exclusion, CoreFuzzHarness.cjs's pausedWords (see
            // the comment there for every shape it holds). Line 0 starts 200 ms before its first vocal,
            // so its activation (800) is the earlier edge of the first line's head start.
            case "pausedWords":
                return
                [
                    Line("tonight forever", 800, 5000, 4000,
                        Paused("tonight", 1000, 2400, [], [new WordPause(1300, 1700, 4)]),
                        Paused("forever", 2400, 4000, [2, 4], [new WordPause(2950, 3150, 4), new WordPause(3600, 3700, 6)], 2900, 3500)),
                    Line("breathe in", 5000, 8000, 7000,
                        Paused("breathe", 5000, 6500, [], [new WordPause(5600, 6000, 3)], 5700),
                        Paused("in", 6500, 7000, [], [new WordPause(6600, 6700, 0)])),
                    Line("yooooooooou heyyyyy", 8000, 16000, 13000,
                        Authored("yooooooooou", 8000, 11000, [6, 10], 9500, 10500),
                        Authored("heyyyyy", 11000, 13000, [3], 12000)),
                ];

            case "instrumentalGap":
                return
                [
                    Line("ab", 1000, 14000, 2000, Unit("ab", 1000, 2000)),
                    Line("cdefghij", 14000, 18000, 15000, Unit("cdefghij", 14000, 15000)),
                ];

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture");
        }
    }

    /// <summary>
    /// The beatmap granularity each fixture's header declares. METADATA ONLY since the judgement
    /// ladder was collapsed to one symmetric set of windows: it no longer decides what any cell is
    /// graded against, and neither does an estimated line or a low-confidence word. It is still set
    /// here because <see cref="LyricBeatmap.Granularity"/> is a required member every map carries,
    /// and because the fixtures that declare something other than Line earn their keep on the CELL
    /// side (a subdivided word, a low-confidence unit, an estimated line) rather than on the ladder.
    /// </summary>
    private static TimingGranularity GranularityOf(string name)
    {
        switch (name)
        {
            case "mixedTiers":
                return TimingGranularity.Word;

            case "syllabic":
                return TimingGranularity.Syllable;

            default:
                return TimingGranularity.Line;
        }
    }

    #endregion

    #region Driving the two sides

    /// <summary>
    /// The generated stream as a replay, headed by the CONFIG frame the nine judgement-relevant
    /// settings travel in: bit 0 allow-wrong-input (on, the default model and all the browser has),
    /// bit 1 space-skips-word (whichever half of the case matrix this run is), bit 2 syllable-span
    /// judgement, bit 3 wrong-input-on-word-gaps, bit 4 strict-spaces, bit 5 the FLEXIBLE LINES
    /// default, bit 6 the char-timed stretch narrowing, bit 7 the BOUNDED RUSH and bit 8 the
    /// FIRST-CHAR hybrid.
    ///
    /// <para>Bits 2, 3, 4, 5, 6, 7 and 8 are ON here, and getting any of them wrong would quietly
    /// gut the whole sweep rather than fail loudly in one place. <see cref="TypeBeatReplayScorer"/>
    /// follows the CONFIG frame for all seven (<c>ReplayEngineFeed.Apply</c>), and the engine's
    /// DEFAULTS are the classic point rule, the strict word gap, the classic space rules, the PINNED
    /// caret, the unnarrowed span, the UNBOUNDED roll and the span-wide first character, because a
    /// replay recorded before backlog 179, 181, 184, 208, 209, 218 or 247 must re-derive under the
    /// rules its fingers were graded on. The browser has no era axis at all: it only plays live, so
    /// it judges on spans, types wrong letters into word gaps, applies the space discipline, runs the
    /// flexible caret, char-times a stretch, bounds the rush and opens a syllable on its start time
    /// unconditionally. A config frame without bit 2 would put the C# arm on point deltas while the
    /// JS arm is on spans, and every case that ever pressed inside a span would part; one without
    /// bit 3 would have the C# arm REJECT every wrong key the script lands on a gap, holding a caret
    /// the browser moved; one without bit 4 would have it reject every mid-word space and advance
    /// past every gap typo, which is the same failure again; one without bit 5 would PIN the C# arm's
    /// caret to the playhead while the browser's finishes lines early, drags past deadlines and
    /// refuses combo out past the rush cap; one without bit 7 would let the C# arm roll onto a line
    /// the browser's bound refuses, so every press the browser makes inert in the park lands on a
    /// cell there; one without bit 6 would pay every mashed freestyle slot and stretched run a delta
    /// of zero the browser charges for; one without bit 8 would pay every syllable's OPENING press a
    /// delta of zero anywhere inside the span while the browser charges it for how late into the
    /// syllable it landed. The first five end the same way: every keystroke after the first such
    /// press lands on a different cell. The last two part the accounts without moving the caret at
    /// all, which is why the sweep also counts the presses each of them decides (see
    /// <see cref="TheSweepReachesTheRulesItIsMeantTo"/>).</para>
    ///
    /// <para>Since backlog 259 the frames also set BIT 10, the back-dated seal break: a line sealing
    /// on cells nobody typed destroys only the run earned up to the last of them and leaves what the
    /// player built past it standing. The browser takes that rule unconditionally, being live-only,
    /// and the C# defaults it OFF so that every stored replay re-derives the whole-run wipe it was
    /// submitted under. Without the bit here the two arms would part at the first seal a flexible
    /// caret ran ahead of, on combo and therefore on every combo-weighted portion after it, with no
    /// keystroke moving at all.</para>
    ///
    /// <para>Since PR 2 they set BIT 16 as well, the first line's head start
    /// (<see cref="TypingEngine.FirstLineLeadIn"/>): a press up to
    /// <see cref="TypingEngine.FIRST_LINE_LEAD_MS"/> before the map's first vocal opens the first line.
    /// The live playfield sets it for every stack and the browser takes it unconditionally; without it
    /// the C# arm would refuse every head-start press the generator rolls, the browser would open the
    /// line on it, and every keystroke after it would land on a different cell.</para>
    ///
    /// <para>Since backlog 307 they set BITS 14 AND 15 too, the manual newline and the typed-through
    /// newline riding on it (<see cref="TypingEngine.ManualNewlines"/>,
    /// <see cref="TypingEngine.NewlineOnTypedLetter"/>). Both are a desktop SETTING that has defaulted
    /// ON since PR 2, and the browser takes that shipped default unconditionally, having no settings
    /// surface; the C# engine property stays off so a run stored with the automatic hand-over
    /// re-derives on it. Without the bits the C# arm would roll a finished caret on the press that
    /// finished its line while the browser parks it for the player's own newline, and every
    /// keystroke after it would land on a different line.</para>
    ///
    /// <para>Bit 5 is the one that cannot be read as a single fact, which is why it is a parameter
    /// here rather than a constant: bit 5 CLEAR means a PINNED caret for a plain old replay, but an
    /// unpinned caret WITHOUT the line-start snap for one carrying the retired "FT" acronym, so
    /// <c>ReplayEngineFeed.Apply</c> takes the snap from the bit outright and the caret from
    /// <c>bit 5 || TypingEngine.FlexibleCaretFromMod</c>. The sweep passes no mods, so the frame is
    /// the whole of the answer here.</para>
    /// </summary>
    private static Replay Keystrokes(JsonElement keys, bool spaceSkipsWord, bool syllableTiming = true, bool wrongInputOnWordGaps = true, bool strictSpaces = true, bool charTimedStretch = true, bool flexibleLines = true, bool boundedRush = true, bool firstCharTiming = true, bool backDatedSealBreak = true, bool losslessSkipReclaim = true, bool foldsDisplacedClaim = true, bool firstLineLeadIn = true, bool manualNewlines = true)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: spaceSkipsWord, syllableTiming: syllableTiming, wrongInputOnWordGaps: wrongInputOnWordGaps, strictSpaces: strictSpaces, charTimedStretch: charTimedStretch, flexibleLines: flexibleLines, boundedRush: boundedRush, firstCharTiming: firstCharTiming, backDatedSealBreak: backDatedSealBreak, losslessSkipReclaim: losslessSkipReclaim, foldsDisplacedClaim: foldsDisplacedClaim, manualNewlines: manualNewlines, newlineOnTypedLetter: manualNewlines, firstLineLeadIn: firstLineLeadIn));

        foreach (var key in keys.EnumerateArray())
        {
            double time = key[0].GetDouble();
            string character = key[1].GetString()!;

            replay.Frames.Add(new TypeBeatReplayFrame(time, character[0]));
        }

        return replay;
    }

    /// <summary>
    /// The game's counts as the wire spells them, minus the LINE containers' <c>ignore_hit</c>: the
    /// line object is scoring-inert, so <c>typebeat-core.js</c> does not model it and the server's
    /// <c>ScoringContract</c> ignores it when it recomputes. Identical to
    /// <see cref="WordSkipLiveParityTest"/>'s filter, and for the same reason.
    /// </summary>
    private static Dictionary<string, int> Wire(IReadOnlyDictionary<HitResult, int> counts)
        => WireCounts.From(counts.Where(entry => entry.Key != TypeBeatResultMapping.LINE_RESULT).ToDictionary(entry => entry.Key, entry => entry.Value));

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    #endregion

    #region The browser side: the generated harness

    private static readonly Lazy<JsonElement> browser_runs = new Lazy<JsonElement>(() => RunHarness("CoreFuzzHarness.cjs"));

    /// <summary>
    /// The browser's own answers for the syllabifier corpus, from the harness that also self-checks
    /// them against the game's pinned splits. Held here so the SAME words can be put to the game's
    /// real <see cref="Syllabifier"/>, which only this project can reference.
    /// </summary>
    private static readonly Lazy<JsonElement> browser_syllabifier = new Lazy<JsonElement>(() => RunHarness("CoreSyllabifierHarness.cjs"));

    /// <summary>
    /// Runs one of the Node harnesses against the served <c>wwwroot/js/typebeat-core.js</c> and
    /// parses its stdout as JSON. Node is optional on a dev box, so a missing node ignores the test
    /// rather than failing it (CI has node), which is the rule every other JS guard already applies.
    /// </summary>
    private static JsonElement RunHarness(string harnessFileName) => NodeHarness.Run(harnessFileName);

    #endregion

    /// <summary>
    /// The two loaders resolve every fixture to the same cells. Asserted separately, and before any
    /// account is compared, because a fixture divergence would otherwise reach the account
    /// comparison wearing the engine's clothes.
    /// </summary>
    [Test]
    public void TheTwoLoadersAgreeOnEveryFixture()
    {
        var fixtures = browser_runs.Value.GetProperty("fixtures");

        Assert.Multiple(() =>
        {
            foreach (var fixture in fixtures.EnumerateObject())
            {
                var lines = Fixture(fixture.Name);
                var browserLines = fixture.Value;

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Length), $"{fixture.Name}: line count");

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = TypingLine.FromLyricLine(lines[i]);
                    var browserLine = browserLines[i];

                    Assert.That(browserLine.GetProperty("endTime").GetDouble(), Is.EqualTo(line.EndTime), $"{fixture.Name}[{i}]: endTime");
                    Assert.That(browserLine.GetProperty("activationTime").GetDouble(), Is.EqualTo(line.ActivationTime), $"{fixture.Name}[{i}]: activationTime");
                    Assert.That(browserLine.GetProperty("firstVocalTime").GetDouble(), Is.EqualTo(line.FirstVocalTime), $"{fixture.Name}[{i}]: firstVocalTime");
                    Assert.That(browserLine.GetProperty("sealGraceMs").GetDouble(), Is.EqualTo(line.SealGraceMs), $"{fixture.Name}[{i}]: sealGraceMs");

                    var browserCells = browserLine.GetProperty("cells");
                    Assert.That(browserCells.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{fixture.Name}[{i}]: cell count");

                    for (int c = 0; c < line.Cells.Count && c < browserCells.GetArrayLength(); c++)
                    {
                        var cell = line.Cells[c];
                        var browserCell = browserCells[c];

                        Assert.That(browserCell.GetProperty("expected").GetString(), Is.EqualTo(cell.Expected.ToString()), $"{fixture.Name}[{i}][{c}]: expected");
                        Assert.That(browserCell.GetProperty("target").GetDouble(), Is.EqualTo(cell.TargetTime), $"{fixture.Name}[{i}][{c}]: target");
                    }

                    // The SYLLABLE groups (backlog 179), pinned here for the same reason the cells
                    // are: they are what a press on a grouped cell is judged against, so a group
                    // that drifted would surface as an account divergence and be blamed on the
                    // engine. This holds the whole derivation at once, the syllabifier's splits,
                    // the forced count on a subtimed word, the stylised gate that leaves
                    // "ohhh" ungrouped, the dropped groups and the monotonic clamp.
                    var browserGroups = browserLine.GetProperty("syllables");
                    Assert.That(browserGroups.GetArrayLength(), Is.EqualTo(line.Syllables.Count), $"{fixture.Name}[{i}]: syllable count");

                    for (int g = 0; g < line.Syllables.Count && g < browserGroups.GetArrayLength(); g++)
                    {
                        var group = line.Syllables[g];
                        var browserGroup = browserGroups[g];

                        Assert.That(browserGroup.GetProperty("startCell").GetInt32(), Is.EqualTo(group.StartCell), $"{fixture.Name}[{i}] syllable {g}: startCell");
                        Assert.That(browserGroup.GetProperty("endCellExclusive").GetInt32(), Is.EqualTo(group.EndCellExclusive), $"{fixture.Name}[{i}] syllable {g}: endCellExclusive");
                        Assert.That(browserGroup.GetProperty("startTime").GetDouble(), Is.EqualTo(group.StartTime), $"{fixture.Name}[{i}] syllable {g}: startTime");
                        Assert.That(browserGroup.GetProperty("endTime").GetDouble(), Is.EqualTo(group.EndTime), $"{fixture.Name}[{i}] syllable {g}: endTime");
                    }

                    // Membership is read through SyllableIndexOf and never by range, so it is pinned
                    // cell by cell: an ungrouped cell can sit positionally inside a group's range.
                    var browserMembership = browserLine.GetProperty("cellSyllable");
                    Assert.That(browserMembership.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{fixture.Name}[{i}]: cellSyllable length");

                    for (int c = 0; c < line.Cells.Count && c < browserMembership.GetArrayLength(); c++)
                        Assert.That(browserMembership[c].GetInt32(), Is.EqualTo(line.SyllableIndexOf(c)), $"{fixture.Name}[{i}][{c}]: syllable membership");

                    // The STRETCH flags (backlog 209), the second half of what decides WHICH rule
                    // judges a press: a freestyle slot, or a cell of a run of three or more identical
                    // characters inside one syllable, is graded on its own character target while
                    // everything else keeps the span. Derived on both sides from the cells and the
                    // membership above, so this holds the derivation itself: the run threshold, the
                    // case fold, the cut at the syllable boundary and the break at an ungrouped cell.
                    var browserStretch = browserLine.GetProperty("charTimedStretch");
                    Assert.That(browserStretch.GetArrayLength(), Is.EqualTo(line.Cells.Count), $"{fixture.Name}[{i}]: charTimedStretch length");

                    for (int c = 0; c < line.Cells.Count && c < browserStretch.GetArrayLength(); c++)
                        Assert.That(browserStretch[c].GetBoolean(), Is.EqualTo(line.IsCharTimedStretch(c)), $"{fixture.Name}[{i}][{c}]: char-timed stretch");
                }
            }
        });
    }

    /// <summary>
    /// COVERAGE, not behaviour: the sweep's authored-split fixture has to actually exercise the
    /// authored arm, or every case above would keep passing while both sides quietly derived.
    ///
    /// <para>The counter is the number of cells whose target time or syllable membership MOVES when
    /// the same fixture's <c>split_chars</c> are taken away, and it is asserted greater than zero.
    /// Both halves of the split are counted, because the whole point of the feature is that one cut
    /// drives the two together: a port that honoured the field for the targets and not for the
    /// groups (or the reverse) would show a smaller count here and part from the browser above.</para>
    /// </summary>
    [Test]
    public void TheAuthoredSplitFixtureReallyExercisesTheAuthoredArm()
    {
        var authored = TypingLine.FromLyricLine(Fixture("authoredSplit")[0]);
        var derived = TypingLine.FromLyricLine(WithoutSplits(Fixture("authoredSplit")[0]));

        Assert.That(derived.Cells.Count, Is.EqualTo(authored.Cells.Count), "stripping the split must not change the cells themselves");

        int movedTargets = 0;
        int movedMembership = 0;

        for (int c = 0; c < authored.Cells.Count; c++)
        {
            if (authored.Cells[c].TargetTime != derived.Cells[c].TargetTime)
                movedTargets++;

            if (authored.SyllableIndexOf(c) != derived.SyllableIndexOf(c))
                movedMembership++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(movedTargets, Is.GreaterThan(0), "the authored split moved no target: the sweep stopped exercising it");
            Assert.That(movedMembership, Is.GreaterThan(0), "the authored split moved no cell into another group: the sweep stopped exercising it");
        });
    }

    /// <summary>
    /// The same line with every authored split taken away, i.e. as a pre-181 map carries it, and
    /// with every authored PAUSE taken away too (PR 2). <paramref name="keepSplits"/> keeps the
    /// splits, which is the pre-PR-2 reading of a map that has both.
    /// </summary>
    private static LyricLine WithoutSplits(LyricLine line, bool keepSplits = false)
    {
        var units = new List<TimedUnit>(line.Units.Count);

        foreach (var unit in line.Units)
        {
            units.Add(new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = unit.SyllableBoundaries,
                SyllableSplits = keepSplits ? unit.SyllableSplits : Array.Empty<int>(),
            });
        }

        return new LyricLine
        {
            RawText = line.RawText,
            StartTime = line.StartTime,
            EndTime = line.EndTime,
            SingEndTime = line.SingEndTime,
            Units = units,
            SealGraceMs = line.SealGraceMs,
            Estimated = line.Estimated,
        };
    }

    /// <summary>
    /// The browser's ported syllabifier answers exactly what the game's does, word for word, for
    /// every word of the game's own pinned corpus plus the stylised, junk and forced-count probes.
    ///
    /// <para><c>Typebeat.Web.Tests.SyllabifierParityTest</c> already holds the JS against the
    /// corpus TRANSCRIBED into the harness, which is what makes that guard runnable without the
    /// game repo. This is the other half, and the one the transcription cannot do: it calls the
    /// REAL <see cref="Syllabifier"/>, so a rule that changes on the C# side without the JS moving
    /// fails here rather than being copied into a stale transcription and passing.</para>
    /// </summary>
    [Test]
    public void TheTwoSyllabifiersAgreeWordForWord()
    {
        var root = browser_syllabifier.Value;
        var words = root.GetProperty("words");

        Assert.That(words.GetArrayLength(), Is.GreaterThan(100), "the corpus should be the whole pinned word list");

        Assert.Multiple(() =>
        {
            foreach (var entry in words.EnumerateArray())
            {
                string word = entry.GetProperty("word").GetString()!;

                Assert.That(Ints(entry, "splits"), Is.EqualTo(Syllabifier.SplitPoints(word).ToArray()), $"{word}: splits");
                Assert.That(entry.GetProperty("count").GetInt32(), Is.EqualTo(Syllabifier.CountSyllables(word)), $"{word}: count");
                Assert.That(entry.GetProperty("syllabifiable").GetBoolean(), Is.EqualTo(Syllabifier.IsSyllabifiable(word)), $"{word}: gate");
            }

            // The forced-count reconciliation too, across the whole 0..length+2 sweep the game's own
            // fixture runs: merge-weakest, add-best-split and the graceful over-forcing degrade.
            foreach (var entry in root.GetProperty("forced").EnumerateArray())
            {
                string word = entry.GetProperty("word").GetString()!;
                int forced = entry.GetProperty("forced").GetInt32();

                Assert.That(Ints(entry, "splits"), Is.EqualTo(Syllabifier.SplitPoints(word, forced).ToArray()), $"{word} forced {forced}");
            }
        });
    }

    /// <summary>
    /// The rank the GAME's own ladder gives for the run the game just derived, which is what the
    /// browser's rank is compared against instead of <see cref="TypeBeatReplayAccount.Rank"/>
    /// itself.
    ///
    /// <para>THE DIFFERENCE IS AN OSU STALENESS QUIRK, not a scoring rule. <c>ScoreProcessor</c>
    /// recomputes its live rank only when ACCURACY CHANGES (<c>updateRank</c> is wired to
    /// <c>Accuracy.ValueChanged</c> and to nothing else), so a run whose judged cells ALL carry the
    /// same accuracy weight never moves accuracy after its first judgement and its live rank stays
    /// where that first judgement left it. A run made entirely of Mehs and unfixed typos is exactly
    /// that: both are worth 50 of 300 (<c>TypeBeatScoreProcessor.GetBaseScoreForResult</c>
    /// re-weights the typo), so accuracy sits at 1/6 from the first press to the last and the
    /// client's own readout still says X. Backlog 199 is what made such runs reachable in the
    /// sweep: an off-time press is a Meh now rather than a weight-zero Miss.</para>
    ///
    /// <para>It reaches no leaderboard, which is why this is a comparison basis rather than a bug
    /// the browser has to copy: <c>ScoreEndpoints</c> recomputes rank from the SUBMITTED statistics
    /// through <c>ScoringContract.RankFromCompletion</c> and stores that, so the stored rank is the
    /// value computed here whichever client played the run. Comparing against it keeps the parity
    /// this test is for (the two clients' ladders, over the same completion) without pinning the
    /// desktop HUD's staleness into the browser.</para>
    /// </summary>
    private static ScoreRank RankOf(TypeBeatReplayAccount game)
        => game.Rank == ScoreRank.F ? ScoreRank.F : TypeBeatScoreProcessor.RankFromCompletion(game.Completion);

    private static int[] Ints(JsonElement element, string key)
    {
        var values = new List<int>();

        foreach (var value in element.GetProperty(key).EnumerateArray())
            values.Add(value.GetInt32());

        return values.ToArray();
    }

    /// <summary>
    /// Every generated run's SUBMITTED account, browser against game, field for field and with no
    /// tolerance on the doubles: the two are the same sequence of operations on the same inputs, so
    /// any difference at all is a divergence rather than rounding.
    ///
    /// <para>One test rather than one per case, because the cases are generated: naming them
    /// individually would pin a case count that the next widening of the harness would have to
    /// churn, and <see cref="Assert.Multiple"/> already reports every failing case by name.</para>
    /// </summary>
    [Test]
    public void EveryGeneratedRunAgrees()
    {
        var cases = browser_runs.Value.GetProperty("cases");

        Assert.That(cases.GetArrayLength(), Is.GreaterThan(50), "the harness should be generating a real sweep");

        Assert.Multiple(() =>
        {
            foreach (var browserCase in cases.EnumerateArray())
            {
                string scenario = browserCase.GetProperty("name").GetString()!;
                bool spaceSkipsWord = browserCase.GetProperty("spaceSkipsWord").GetBoolean();
                string fixture = browserCase.GetProperty("fixture").GetString()!;
                var map = Map(GranularityOf(fixture), Fixture(fixture));
                var replay = Keystrokes(browserCase.GetProperty("keys"), spaceSkipsWord);

                var game = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
                var submitted = browserCase.GetProperty("submitted");

                Assert.That(game.UnconsumedFrames, Is.Zero, $"{scenario}: the game never reached some of the recorded keystrokes");
                Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Statistics)), $"{scenario}: statistics");
                Assert.That(Dict(submitted, "maximumStatistics"), Is.EquivalentTo(Wire(game.MaximumStatistics)), $"{scenario}: maximum_statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.MaxCombo), $"{scenario}: max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.TotalScore), $"{scenario}: total_score");
                Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Accuracy), $"{scenario}: accuracy");
                Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(game.Completion), $"{scenario}: completion");
                Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(RankOf(game).ToString()), $"{scenario}: rank");
            }
        });
    }

    /// <summary>
    /// The sweep is only worth what it exercises, so this pins the SHAPE of what was generated
    /// rather than any one run: the cases have to reach every scoring tier, both exits from a typo,
    /// real misses and a spread of ranks, or a green run above means nothing. It reads the browser
    /// side alone, which is the side the harness measures.
    /// </summary>
    [Test]
    public void TheSweepReachesTheRulesItIsMeantTo()
    {
        var cases = browser_runs.Value.GetProperty("cases");

        int withTypos = 0, withMisses = 0, withOk = 0, withMeh = 0, perfect = 0;
        int skipPresses = 0, comboRestores = 0, backspaces = 0, passiveBreaks = 0, spanJudgements = 0, gapTypos = 0;
        int parkedGapTypos = 0, stepOvers = 0, midWordSpaceTypos = 0, stretchPointJudgements = 0, firstCharJudgements = 0;
        int rollForwards = 0, lineSnaps = 0, dragHolds = 0, rushCapBreaks = 0, refusedRolls = 0;
        int lineStepBacks = 0;
        int ownCreditBreaks = 0;
        int leadInOpens = 0, pauseJudgements = 0;
        int manualHandOvers = 0, typedLetterNewlines = 0, awaitingSwallows = 0, holdExtensions = 0, enters = 0;
        int cellLessLetterNewlines = 0;

        foreach (var browserCase in cases.EnumerateArray())
        {
            var statistics = Dict(browserCase.GetProperty("submitted"), "statistics");

            if (statistics.ContainsKey("good")) withTypos++;
            if (statistics.ContainsKey("miss")) withMisses++;
            if (statistics.ContainsKey("ok")) withOk++;
            if (statistics.ContainsKey("meh")) withMeh++;
            if (browserCase.GetProperty("submitted").GetProperty("rank").GetString() == "X") perfect++;

            skipPresses += browserCase.GetProperty("skipPresses").GetInt32();
            comboRestores += browserCase.GetProperty("restores").GetInt32();
            passiveBreaks += browserCase.GetProperty("passiveBreaks").GetInt32();
            ownCreditBreaks += browserCase.GetProperty("ownCreditBreaks").GetInt32();
            spanJudgements += browserCase.GetProperty("spanJudgements").GetInt32();
            stretchPointJudgements += browserCase.GetProperty("stretchPointJudgements").GetInt32();
            firstCharJudgements += browserCase.GetProperty("firstCharJudgements").GetInt32();
            gapTypos += browserCase.GetProperty("gapTypos").GetInt32();
            parkedGapTypos += browserCase.GetProperty("parkedGapTypos").GetInt32();
            stepOvers += browserCase.GetProperty("stepOvers").GetInt32();
            midWordSpaceTypos += browserCase.GetProperty("midWordSpaceTypos").GetInt32();
            rollForwards += browserCase.GetProperty("rollForwards").GetInt32();
            lineSnaps += browserCase.GetProperty("lineSnaps").GetInt32();
            dragHolds += browserCase.GetProperty("dragHolds").GetInt32();
            rushCapBreaks += browserCase.GetProperty("rushCapBreaks").GetInt32();
            refusedRolls += browserCase.GetProperty("refusedRolls").GetInt32();
            lineStepBacks += browserCase.GetProperty("lineStepBacks").GetInt32();
            leadInOpens += browserCase.GetProperty("leadInOpens").GetInt32();
            pauseJudgements += browserCase.GetProperty("pauseJudgements").GetInt32();
            manualHandOvers += browserCase.GetProperty("manualHandOvers").GetInt32();
            typedLetterNewlines += browserCase.GetProperty("typedLetterNewlines").GetInt32();
            cellLessLetterNewlines += browserCase.GetProperty("cellLessLetterNewlines").GetInt32();
            awaitingSwallows += browserCase.GetProperty("awaitingSwallows").GetInt32();
            holdExtensions += browserCase.GetProperty("holdExtensions").GetInt32();
            enters += browserCase.GetProperty("keys").EnumerateArray().Count(key => key[1].GetString() == "\n");
            backspaces += browserCase.GetProperty("keys").EnumerateArray().Count(key => key[1].GetString() == "");
        }

        Assert.Multiple(() =>
        {
            Assert.That(withTypos, Is.GreaterThan(0), "no run left an uncorrected typo");
            Assert.That(withMisses, Is.GreaterThan(0), "no run missed a cell");
            Assert.That(withOk, Is.GreaterThan(0), "no run landed a press in the Ok band");
            Assert.That(withMeh, Is.GreaterThan(0), "no run landed a press in the Meh band");
            Assert.That(perfect, Is.GreaterThan(0), "no run typed the map out clean");
            Assert.That(backspaces, Is.GreaterThan(0), "no run ever erased anything");
            Assert.That(skipPresses, Is.GreaterThan(0), "no run abandoned a word");
            Assert.That(comboRestores, Is.GreaterThan(0), "no run walked back into a break and resumed the streak");

            // Backlog 176's own rule: a redeemable break landing at a streak of ZERO with a claim
            // already outstanding, which is the only case where whose claim it is gets decided. The
            // scripted cases guarantee this one, because thirty seeds per fixture never reached the
            // full shape (they reach the break, but not the walk back into the older cell that makes
            // the two rules differ), and a sweep that never reaches it cannot pin it.
            Assert.That(passiveBreaks, Is.GreaterThan(0), "no run took a redeemable break that had no streak to claim with");

            // Backlog 243's refinement of that rule, counted only where the two DISAGREE: a break
            // standing on a real streak of 1 that was credited by the claim's own press (the space
            // that skipped the word, judged on the gap it landed on) and is spared anyway. Under 176
            // alone that break took the claim, so this counter going to zero means the sweep stopped
            // reaching the shape a real play lost 430 combo to, and both arms would agree on the old
            // answer with nothing failing.
            Assert.That(ownCreditBreaks, Is.GreaterThan(0), "no run broke on nothing but the combo a word skip's own space credited");

            // Backlog 179's own rule: a press the SPAN decided rather than the point. Counted only
            // when the cell was in a group AND the span answer differed from the point answer, so a
            // fixture edit that ungrouped every token (or a port that quietly stopped grouping)
            // leaves both sides agreeing on point deltas, green, and no longer covering the rule.
            // That is the failure mode this counter exists for: it cannot be caught by comparing
            // the two arms, because both would be wrong in the same direction.
            Assert.That(spanJudgements, Is.GreaterThan(0), "no run judged a press against its syllable's span");

            // Backlog 209's own rule, the OTHER arm of the same seam and counted the same way: a
            // press on a STRETCH cell (a freestyle slot, or a cell of a run of three identical
            // characters in one syllable) that the narrowing put back on its character target while
            // the span would have answered something else. Both counters have to be positive, because
            // a cell in a group now reaches one of two rules and a sweep that reached only one of
            // them would leave the other uncovered while every case still passed.
            Assert.That(stretchPointJudgements, Is.GreaterThan(0), "no run judged a press on a stretch cell's own character target");

            // Backlog 247's own rule, the THIRD arm of that seam and counted on the same principle:
            // a press on the cell that OPENS a syllable, paid the distance from the span's start
            // where the span would have paid it something else. Only a press that landed LATE into
            // its syllable can be counted, because the early side of the two rules is the same
            // number, so this says the sweep reached the half of the rule 247 actually moved. Like
            // the stretch counter it cannot be replaced by the arms disagreeing: both engines set
            // the bit, so a port that lost the hybrid would leave them agreeing on the span answer,
            // green, and covering nothing.
            Assert.That(firstCharJudgements, Is.GreaterThan(0), "no run judged a press on the start of the syllable it opened");

            // Backlog 181's own rule: a wrong letter landing IN a word gap. Counted on the CELLS
            // (a gap that became Wrong), which is exactly the set of presses whose outcome differs
            // from the strict rule, because the strict arm rejects them and writes nothing. Same
            // failure mode as the counter above: a generator that stopped rolling letters at gaps,
            // or a port that quietly went back to rejecting them, would leave both arms agreeing on
            // rejections, green, and no longer covering the rule.
            Assert.That(gapTypos, Is.GreaterThan(0), "no run typed a wrong letter into a word gap");

            // Backlog 184's own three rules, counted the same way and for the same reason. The park
            // and the step-over only exist on the skip arm of the matrix and the mid-word typo only
            // on the other one, so all three together say the sweep is exercising both halves of the
            // space discipline. Each is measured on the ENGINE's state around the press: a port that
            // went back to advancing past a gap typo, or to rejecting a mid-word space, would leave
            // both arms agreeing (the C# arm follows the same CONFIG bit), green, and covering
            // nothing.
            Assert.That(parkedGapTypos, Is.GreaterThan(0), "no run parked the caret on a gap it spoiled");
            Assert.That(stepOvers, Is.GreaterThan(0), "no run pressed the space that steps over a parked typo");
            Assert.That(midWordSpaceTypos, Is.GreaterThan(0), "no run typed a space into a lyric character");

            // Backlog 208's four freedoms, one counter each and for the identical reason: both arms
            // now run the flexible caret (the CONFIG frame here sets bit 5), so a port that quietly
            // lost one of these would leave the two sides agreeing on the PINNED answer, green, and
            // covering nothing. Each is measured on the engine's own seam rather than on a script.
            //
            // The snap's counter is the one that could not be left to the generator at all: it only
            // fires while a FINISHED caret is parked on a line the seal has not reached and the next
            // line has already started, which needs the overlapping-window fixture (see
            // "parkedLine"), because on a contiguous map the seal's own hand-over always gets there
            // first. Every other fixture would read zero forever.
            //
            // Since backlog 307 the sweep runs the MANUAL NEWLINE (CONFIG bits 14 and 15, the browser's
            // only arm), under which those two time-driven hand-overs are dead code on both sides: the
            // press that finishes a line parks the caret, and the snap never fires. So their counters
            // are pinned at ZERO rather than above it, which is the non-vacuity check for the arm
            // itself: a port that quietly fell back to the automatic hand-over reads non-zero here.
            Assert.That(rollForwards, Is.Zero, "a finished line rolled the caret on by itself: the sweep is not on the manual arm");
            Assert.That(lineSnaps, Is.Zero, "a parked caret was snapped on by the next line starting: the sweep is not on the manual arm");
            Assert.That(dragHolds, Is.GreaterThan(0), "no run held a line open past its deadline for a player still typing it");
            Assert.That(rushCapBreaks, Is.GreaterThan(0), "no run put the caret out past the rush cap");

            // Backlog 218's own rule, counted on entryPermitted, the engine's own predicate, and only
            // where it answered FALSE: a press finished a line more than FLETCHER_DRAG_GRACE_MS
            // before the next line's cue, so the roll was refused and the caret parked past the last
            // cell it had. This is the sharpest of the set, because losing the bound is the one
            // change that would leave BOTH arms agreeing (on the pre-218 unbounded roll), green, and
            // covering nothing at all: the CONFIG frame here sets bit 7, so the C# side follows the
            // browser onto the bound rather than deciding it independently. The generated streams do
            // reach it on the contiguous fixtures (finishing "cat dog" on time is already 167 ms too
            // early for "hi"), and scripted/rushBoundPark reaches it on purpose so it is not left to
            // a seed.
            Assert.That(refusedRolls, Is.GreaterThan(0), "no run finished a line early enough for the rush bound to refuse the roll");

            // THE STEP BACK, the newest rule on this seam and the one with NO era bit of its own: a
            // backspace at the head of a line hands the caret back into the line behind it while the
            // engine has not sealed that line yet, landing it on the last character the line has an
            // answer for rather than past its end. The C# gates it on FletcherEnabled, which bit 5
            // already sets here, so both arms run it and a port that lost it would have one side
            // erasing nothing where the other moved a caret and every later keystroke on a different
            // cell. The sweep reaches it on its own (a rush or a snap leaves the caret at index 0 of
            // the next line often enough that a generated backspace lands there), which is what this
            // counter is asserting stays true.
            Assert.That(lineStepBacks, Is.GreaterThan(0), "no run ever backspaced off the head of a line into the one behind it");

            // PR 2, THE FIRST LINE'S HEAD START: a press inside [min(activation, first vocal -
            // FIRST_LINE_LEAD_MS), activation) that OPENED the first line, where every earlier build
            // refused it. The CONFIG frame here sets bit 16, so both arms take it; a port that lost it
            // would leave the browser refusing a press the C# arm opens the line on and every
            // keystroke after it on a different cell. The generator rolls it for half of every
            // fixture's runs on a stream of its own, and scripted/firstLineHeadStart writes it out.
            Assert.That(leadInOpens, Is.GreaterThan(0), "no run opened the first line inside its head start");

            // PR 2, AUTHORED PAUSES: a press whose judged delta the pauses MOVED, counted against the
            // same press on the same map with every pause taken away. Only the pausedWords fixture can
            // reach it, and scripted/pausedSpans presses inside every stretch on purpose.
            Assert.That(pauseJudgements, Is.GreaterThan(0), "no run was judged differently for a word's authored pauses");

            // Backlog 307, THE MANUAL NEWLINE, one counter per rule it brings, each measured on the
            // browser engine's own seam: a hand-over the player's own space, Enter or letter made
            // (rollForwardManually moved the caret); of those, one a LETTER made
            // (newlineOnTypedLetter); a press the engine SWALLOWED because the caret was waiting on a
            // line whose entry window had not opened (awaitingEntry); and a finished line held past
            // its own deadline toward the drag cutoff (manualNewlineHoldsLineOpen). The CONFIG frame
            // here sets bits 14 and 15, so a port that lost any of them would leave the two sides
            // agreeing on the automatic hand-over, green, and covering nothing. The generator rolls
            // Enter as well, so the frame kind the replay feed hands to ProcessEnter is exercised too.
            Assert.That(manualHandOvers, Is.GreaterThan(0), "no run handed a finished line over by its own press");
            Assert.That(typedLetterNewlines, Is.GreaterThan(0), "no run handed a finished line over by typing the next one");

            // Backlog 326: of those, a letter that landed the caret on a line with NO cells (only
            // parkedLine's hand-built middle line has none), where the move stands and the letter is
            // dropped. The C# threw there until 326, so the generator used to steer clear of it.
            Assert.That(cellLessLetterNewlines, Is.GreaterThan(0), "no run typed a letter onto a line with nothing to type");
            Assert.That(awaitingSwallows, Is.GreaterThan(0), "no run pressed into a line whose entry window had not opened");
            Assert.That(holdExtensions, Is.GreaterThan(0), "no run held a finished line open past its deadline");
            Assert.That(enters, Is.GreaterThan(0), "no run pressed Enter");
        });
    }

    /// <summary>
    /// COVERAGE for PR 2's cell side, the way <see cref="TheAuthoredSplitFixtureReallyExercisesTheAuthoredArm"/>
    /// is for backlog 181's: the pausedWords fixture has to move targets AND membership when its
    /// pauses are taken away (or the loader agreement above would pass on two sides that both
    /// ignore them), and its last line has to hold one run the stretch exclusion SPARES
    /// ("yooooo|oooo|u", paced into spans) beside one it still char-times ("hey|yyyy").
    /// </summary>
    [Test]
    public void ThePausedFixtureReallyExercisesThePauseArm()
    {
        var lines = Fixture("pausedWords");
        int movedTargets = 0;
        int movedMembership = 0;

        foreach (var original in lines)
        {
            var paused = TypingLine.FromLyricLine(original);
            var plain = TypingLine.FromLyricLine(WithoutSplits(original, keepSplits: true));

            Assert.That(plain.Cells.Count, Is.EqualTo(paused.Cells.Count), "stripping the pauses must not change the cells themselves");

            for (int c = 0; c < paused.Cells.Count; c++)
            {
                if (paused.Cells[c].TargetTime != plain.Cells[c].TargetTime)
                    movedTargets++;

                if (paused.SyllableIndexOf(c) != plain.SyllableIndexOf(c))
                    movedMembership++;
            }
        }

        var stretchLine = TypingLine.FromLyricLine(lines[2]);

        Assert.Multiple(() =>
        {
            Assert.That(movedTargets, Is.GreaterThan(0), "the pauses moved no target: the sweep stopped exercising them");
            Assert.That(movedMembership, Is.GreaterThan(0), "the pauses moved no cell into another group: the sweep stopped exercising them");

            // "yooooooooou": cells 1..9 are the nine 'o's, split 5 | 4 by the authored cut.
            for (int c = 1; c <= 9; c++)
                Assert.That(stretchLine.IsCharTimedStretch(c), Is.False, $"'o' cell {c}: a run a divider paces into spans is not char-timed");

            // "heyyyyy" starts at cell 12; its trailing four 'y's (cells 15..18) are one run the divider merely starts.
            for (int c = 15; c <= 18; c++)
                Assert.That(stretchLine.IsCharTimedStretch(c), Is.True, $"'y' cell {c}: a run a divider only starts stays char-timed");
        });
    }

    /// <summary>
    /// The ERA arm, which the browser does not have and therefore cannot prove: a replay whose
    /// CONFIG frame leaves bit 2 CLEAR re-derives under the classic POINT rule, and the identical
    /// keystrokes re-derive as spans when it is set. Backlog 247 makes it three arms rather than
    /// two, because bit 8 sits inside bit 2: with both set the syllable's OPENING character is paid
    /// the distance from the span's start, which is the live rule since 247 and the one the browser
    /// runs, while a row stored between 179 and 247 keeps the span-wide zero.
    ///
    /// <para>Everything else in this fixture asserts the two clients AGREE; this one asserts the
    /// stored era still parts them, which is what keeps a pre-179 score reproducing the judgement
    /// its fingers actually earned. It is also the guard on the reconciliation the sweep above
    /// depends on: if the scorer ever stopped following the flag, every generated case would still
    /// pass (both arms on spans) while every old replay silently re-scored under the new rule.</para>
    ///
    /// <para>The keystrokes are the harness's <c>scripted/insideTheSpan</c> case, written out here
    /// rather than read from it so this test needs no node: every character is pressed well ahead
    /// of its own point target but inside the sung span of its syllable, which is exactly the shape
    /// the two rules disagree about.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameSyllableBitReDerivesTheClassicRule()
    {
        (double time, char key)[] keys =
        [
            (1000, 'c'), (1100, 'a'), (1200, 'k'), (1300, 'e'), (2400, ' '),
            (2500, 't'), (2600, 'o'), (3900, 'n'), (3910, 'i'), (3920, 'g'), (3930, 'h'), (3940, 't'),
            (5000, 'l'), (5100, 'i'), (5200, 't'), (6400, 't'), (6410, 'l'), (6420, 'e'), (6500, ' '),
            (6600, 'p'), (6700, 'e'), (6800, 'o'), (7900, 'p'), (7910, 'l'), (7920, 'e'),
        ];

        var spans = ScoreWithEra(keys, syllableTiming: true);
        var classic = ScoreWithEra(keys, syllableTiming: false);
        var hybrid = ScoreWithEra(keys, syllableTiming: true, firstCharTiming: true);

        Assert.Multiple(() =>
        {
            // Under the 179 rule every press sits inside its syllable's span, so every one of them
            // is delta 0 and the map is typed clean.
            Assert.That(Wire(spans.Statistics), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 25 }), "syllable era: statistics");
            Assert.That(spans.Rank.ToString(), Is.EqualTo("X"), "syllable era: rank");

            // Under the classic rule the same fingers are graded on the distance to each CHARACTER,
            // which those presses are nowhere near.
            Assert.That(Wire(classic.Statistics).GetValueOrDefault("great"), Is.LessThan(25), "classic era: some presses must fall out of Great");
            Assert.That(classic.TotalScore, Is.LessThan(spans.TotalScore), "classic era: total score");
            Assert.That(classic.Accuracy, Is.LessThan(spans.Accuracy), "classic era: accuracy");

            // Under the LIVE rule (both bits) the eighteen presses that open nothing keep their
            // zero, and the seven that open a syllable are paid how late into it they landed: 'c' at
            // 1000 and 'l' at 5000 open [1000, 2400] and [5000, 5750] dead on their starts, 't' at
            // 2500 opens [2400, 2857.14] 100 late and 'p' at 6600 opens [6500, 7250] 100 late, all
            // four inside the 150 ms Great window. The other three are off the ladder entirely: 'n'
            // at 3900 opens the "night" span at 2857.14, 1042.86 late, and the 't' at 6400 and the
            // 'p' at 7900 open [5750, 6500] and [7250, 8000] 650 late, which is past the 600 ms Meh
            // bound. So 22 Greats and the three Mehs backlog 199 grades an off-ladder hit as.
            //
            // The middle rung emptied when the ladder was retuned: those two 650s were Oks under the
            // old [-600, 1000] late window. Nothing about the run moved, only what it is worth.
            Assert.That(Wire(hybrid.Statistics), Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 22, ["meh"] = 3 }), "hybrid era: statistics");

            // Nothing about the run changes but what the presses were WORTH: the caret lands on the
            // same cells and the map is still typed clean, so the hybrid sits strictly between the
            // two older eras rather than replacing either.
            Assert.That(hybrid.Completion, Is.EqualTo(spans.Completion), "hybrid era: the play completes exactly as the span era does");
            Assert.That(hybrid.MaxCombo, Is.EqualTo(spans.MaxCombo), "hybrid era: max combo");
            Assert.That(hybrid.Accuracy, Is.LessThan(spans.Accuracy), "hybrid era: accuracy is under the span era's");
            Assert.That(hybrid.Accuracy, Is.GreaterThan(classic.Accuracy), "hybrid era: accuracy is over the classic era's");
            Assert.That(hybrid.TotalScore, Is.LessThan(spans.TotalScore), "hybrid era: total score is under the span era's");
            Assert.That(hybrid.TotalScore, Is.GreaterThan(classic.TotalScore), "hybrid era: total score is over the classic era's");
        });
    }

    /// <summary>
    /// The other ERA arm the browser does not have and cannot prove: a replay whose CONFIG frame
    /// leaves bit 3 CLEAR REJECTS a wrong letter pressed on a word gap, exactly as the run was
    /// played before backlog 181, while the identical keystrokes type it through when the bit is
    /// set.
    ///
    /// <para>Judgement relevant in the strongest sense there is: the two arms disagree about whether
    /// the CARET MOVED, so a single such frame decoded under the wrong arm desynchronises every
    /// keystroke after it. That is what the numbers below say. Under the live arm the typo consumes
    /// the gap and "cd" lands on its own two cells, leaving one unfixed typo and a completion of
    /// 4/5; under the stored arm the caret never leaves the gap, so the 'c' and the 'd' are rejected
    /// there in turn and the line seals with three characters nobody typed.</para>
    ///
    /// <para>It is also the guard on the reconciliation the sweep above depends on: if the scorer
    /// ever stopped following the flag, every generated case would still pass (both arms typing gap
    /// typos through) while every old replay silently re-scored under a model its player never
    /// touched.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameWordGapBitRejectsTheTypoInstead()
    {
        // The harness's scripted/gapTypoUnfixed case, written out here rather than read from it so
        // this test needs no node: "ab cd" typed clean up to the word gap, a wrong letter on the
        // gap, then the second word pressed on its own targets.
        (double time, char key)[] keys = [(1000, 'a'), (1500, 'b'), (2000, 'x'), (2000, 'c'), (2500, 'd')];

        var through = ScoreGapTypo(keys, wrongInputOnWordGaps: true);
        var strict = ScoreGapTypo(keys, wrongInputOnWordGaps: false);

        Assert.Multiple(() =>
        {
            var live = Wire(through.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(4), "live era: the four lyric characters");
            Assert.That(live.GetValueOrDefault(WireCounts.Key(TypeBeatResultMapping.UNFIXED_TYPO)), Is.EqualTo(1), "live era: the gap is an unfixed typo");
            Assert.That(live.GetValueOrDefault("miss"), Is.Zero, "live era: a typo is not a miss");
            Assert.That(through.Completion, Is.EqualTo(4 / 5.0).Within(1e-9), "live era: completion");

            var stored = Wire(strict.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(2), "stored era: only the cells before the gap");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(3), "stored era: the caret never left the gap, so nothing after it was typed");
            Assert.That(strict.Completion, Is.EqualTo(2 / 5.0).Within(1e-9), "stored era: completion");
            Assert.That(strict.TotalScore, Is.LessThan(through.TotalScore), "stored era: total score");
        });
    }

    /// <summary>
    /// The THIRD era arm the browser does not have and cannot prove (backlog 184): a replay whose
    /// CONFIG frame leaves bit 4 CLEAR REJECTS a space pressed inside a word, exactly as the run was
    /// played before that task, while the identical keystrokes type it through when the bit is set.
    ///
    /// <para>Judgement relevant in the same strongest sense as the word-gap bit above: the two arms
    /// disagree about whether the CARET MOVED. Under the stored arm the space is refused, the 'b'
    /// then lands on its own cell and the line is typed out but for its last character, which seals
    /// as a miss (4/5). Under the live arm the space takes the 'b' cell, so everything after it is
    /// one cell out and every remaining press is a typo of its own (1/5).</para>
    ///
    /// <para>With word skipping OFF, which is what the browser hardcodes, the bit's other half (the
    /// gap-typo park) is inert by construction: that half is scoped to the skip setting, because the
    /// skip gate is what a spoiled gap used to be fed to. It is pinned on the game side, where the
    /// setting exists (<c>SpaceDisciplineTest</c>).</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameStrictSpaceBitRejectsTheMidWordSpaceInstead()
    {
        // "ab cd" with a space fumbled onto the 'b': then the 'b' itself, the real word gap, and the
        // 'c', each pressed on its own target, so the only thing that moves between the two arms is
        // where that first space left the caret.
        (double time, char key)[] keys = [(1000, 'a'), (1500, ' '), (1500, 'b'), (2000, ' '), (2000, 'c')];

        var through = ScoreSpaceDiscipline(keys, strictSpaces: true);
        var strict = ScoreSpaceDiscipline(keys, strictSpaces: false);

        Assert.Multiple(() =>
        {
            var live = Wire(through.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(1), "live era: only the 'a', pressed before the fumble");
            Assert.That(live.GetValueOrDefault(WireCounts.Key(TypeBeatResultMapping.UNFIXED_TYPO)), Is.EqualTo(4), "live era: every later press landed one cell early");
            Assert.That(live.GetValueOrDefault("miss"), Is.Zero, "live era: the line was typed out, wrongly");
            Assert.That(through.Completion, Is.EqualTo(1 / 5.0).Within(1e-9), "live era: completion");

            var stored = Wire(strict.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(4), "stored era: the space was refused, so nothing after it moved");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(1), "stored era: the 'd' the run never reached");
            Assert.That(strict.Completion, Is.EqualTo(4 / 5.0).Within(1e-9), "stored era: completion");
            Assert.That(through.TotalScore, Is.LessThan(strict.TotalScore), "live era: a typed-through space costs more than a refused one");
        });
    }

    /// <summary>
    /// The FOURTH era arm the browser does not have and cannot prove (backlog 209): a replay whose
    /// CONFIG frame leaves bit 6 CLEAR pays a mashed freestyle section and a mashed stretched run a
    /// delta of zero, exactly as the runs stored before that task were graded, while the identical
    /// keystrokes are put back on the characters' own targets when the bit is set.
    ///
    /// <para>Unlike bits 3 and 4 this one moves no caret: every press lands on the same cell under
    /// both arms and the play completes either way, and what parts is only what the presses were
    /// WORTH. That is why the sweep counts the presses this rule decides rather than relying on a
    /// divergence to surface it, and why the era needs pinning here: if the scorer stopped following
    /// the flag, every generated case would still pass (both arms narrowed) while every pre-209
    /// replay silently re-scored under a rule its player never touched.</para>
    ///
    /// <para>The keystrokes are the harness's <c>scripted/mashedStretch</c> case, written out here
    /// rather than read from it so this test needs no node.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameStretchBitPaysTheMashedRunInFull()
    {
        (double time, char key)[] keys =
        [
            (1000, 'q'), (1000, 'q'), (1000, 'q'), (1000, 'q'), (5000, ' '),
            (5000, 'a'), (6000, 'a'), (9100, 'a'), (9200, 'a'), (9300, 'a'),
        ];

        var live = ScoreStretch(keys, charTimedStretch: true);
        var stored = ScoreStretch(keys, charTimedStretch: false);

        Assert.Multiple(() =>
        {
            // Live: the four markers are judged on 1000 / 2000 / 3000 / 4000 against presses all at
            // 1000 (deltas 0, -1000, -2000, -3000) and the run's three cells on 8200 / 10600 / 13800
            // against 9100 / 9200 / 9300 (900, -1400, -4500). On the one ladder (Great 150, Ok 300,
            // Meh 600) that is one Great and six off the ladder, which backlog 199 grades as Meh
            // hits. The other three Greats are the untimed word gap and the two cells of the first
            // "aa" syllable, both pressed inside its [5000, 9000] span.
            var liveCounts = Wire(live.Statistics);
            Assert.That(liveCounts.GetValueOrDefault("great"), Is.EqualTo(4), "live era: the first marker, the gap and the two span-judged cells");
            Assert.That(liveCounts.GetValueOrDefault("ok"), Is.Zero, "live era: nothing this mashed lands on the middle rung");
            Assert.That(liveCounts.GetValueOrDefault("meh"), Is.EqualTo(6), "live era: the mashed remainder, off the ladder either side");

            // Stored: the three stretch cells are inside their syllable's span, so they are paid 0,
            // and so are all four markers, whose whole token is one span from 1000 to 5000.
            var storedCounts = Wire(stored.Statistics);
            Assert.That(storedCounts.GetValueOrDefault("great"), Is.EqualTo(10), "stored era: the span paid every press");
            Assert.That(stored.Accuracy, Is.EqualTo(1).Within(1e-9));

            // The mash fills every cell either way: what moved is what the presses were worth.
            Assert.That(live.Completion, Is.EqualTo(stored.Completion), "the play completes under both arms");
            Assert.That(live.MaxCombo, Is.EqualTo(stored.MaxCombo));
            Assert.That(live.Accuracy, Is.LessThan(stored.Accuracy), "live era: accuracy");
            Assert.That(live.TotalScore, Is.LessThan(stored.TotalScore), "live era: total score");
        });
    }

    /// <summary>
    /// The FIFTH era arm the browser does not have and cannot prove (backlog 208): a replay whose
    /// CONFIG frame leaves bit 5 CLEAR re-derives on a caret PINNED to the playhead, exactly as
    /// every run stored before that task was played, while the identical keystrokes run the
    /// flexible caret when the bit is set.
    ///
    /// <para>Judgement relevant in the strongest sense there is, the one bits 3 and 4 share: the two
    /// arms disagree about WHERE THE CARET IS, so a single such frame decoded under the wrong arm
    /// lands every keystroke after it on a different cell. The fixture is the sharpest shape for it
    /// (see <c>parkedLine</c>): under the live arm "ab" is typed out, the caret rolls straight on to
    /// the cell-less middle line, and the SNAP hands it to "cd" at that line's 10500 cue, so the map
    /// is typed clean. Under the stored arm the caret waits to be walked along by the seal, which
    /// does not reach the middle line until 20000, so both presses land on a line with no cells at
    /// all and "cd" seals with two characters nobody typed.</para>
    ///
    /// <para>It is also the guard on the reconciliation the sweep above depends on: if the scorer
    /// stopped following the flag, every generated case would still pass (both arms flexible) while
    /// every pre-208 replay silently re-scored with a caret its player never had.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameFlexibleLinesBitPinsTheCaret()
    {
        // The harness's scripted/parkedSnap case, written out here rather than read from it so this
        // test needs no node.
        (double time, char key)[] keys = [(1000, 'a'), (1500, 'b'), (12000, 'c'), (12500, 'd')];

        var flexible = ScoreParked(keys, flexibleLines: true);
        var pinned = ScoreParked(keys, flexibleLines: false);

        Assert.Multiple(() =>
        {
            var live = Wire(flexible.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(4), "live era: the snap carried the caret onto the last line");
            Assert.That(live.GetValueOrDefault("miss"), Is.Zero, "live era: nothing was left untyped");
            Assert.That(flexible.Completion, Is.EqualTo(1).Within(1e-9), "live era: completion");

            var stored = Wire(pinned.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(2), "stored era: only the first line");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(2), "stored era: both presses were eaten by a line with no cells");
            Assert.That(pinned.Completion, Is.EqualTo(2 / 4.0).Within(1e-9), "stored era: completion");
            Assert.That(pinned.TotalScore, Is.LessThan(flexible.TotalScore), "stored era: total score");
        });
    }

    /// <summary>
    /// The SIXTH era arm the browser does not have and cannot prove (backlog 218): a replay whose
    /// CONFIG frame leaves bit 7 CLEAR re-derives with the UNBOUNDED roll, exactly as every run
    /// stored between backlog 208 and 218 was played, while the identical keystrokes are bounded
    /// when the bit is set.
    ///
    /// <para>Judgement relevant in the same strong sense bit 5 is, and sharper in one way: the two
    /// arms disagree about WHERE THE CARET IS, and under the bound the presses an unbounded run made
    /// are refused OUTRIGHT rather than landed somewhere else, so re-deriving an old row under the
    /// new rule would cost it whole lines' worth of judgements. The fixture is the shape that says so
    /// (see <c>instrumentalGap</c>): "ab" is typed out on time at 1500, eleven seconds before entry
    /// into "cdefghij" opens at 12500. Under the STORED arm the caret goes straight on, so the 6000
    /// and 9000 presses land on that line's first two cells (correct characters, graded Meh for how
    /// early they are, which is what rushing always read as) and six cells are missed. Under the LIVE
    /// arm the caret parks past "ab"'s last cell, both presses are inert, and all eight cells of the
    /// second line are missed.</para>
    ///
    /// <para>It is also the guard on the reconciliation the sweep above depends on: if the scorer
    /// stopped following the flag, every generated case would still pass (both arms bounded) while
    /// every 208-to-218 replay silently re-scored with a caret its player never had.</para>
    /// </summary>
    [Test]
    public void ClearingTheConfigFrameBoundedRushBitReDerivesTheUnboundedRoll()
    {
        (double time, char key)[] keys = [(1000, 'a'), (1500, 'b'), (6000, 'c'), (9000, 'd')];

        var bounded = ScoreInstrumentalGap(keys, boundedRush: true);
        var unbounded = ScoreInstrumentalGap(keys, boundedRush: false);

        Assert.Multiple(() =>
        {
            var live = Wire(bounded.Statistics);
            Assert.That(live.GetValueOrDefault("great"), Is.EqualTo(2), "live era: only the line the player actually reached");
            Assert.That(live.GetValueOrDefault("meh"), Is.Zero, "live era: the two rushed presses were refused outright, so they are worth nothing at all");
            Assert.That(live.GetValueOrDefault("miss"), Is.EqualTo(8), "live era: every cell of the second line");
            Assert.That(bounded.Completion, Is.EqualTo(2 / 10.0).Within(1e-9), "live era: completion");

            var stored = Wire(unbounded.Statistics);
            Assert.That(stored.GetValueOrDefault("great"), Is.EqualTo(2), "stored era: the first line, typed the same way");
            Assert.That(stored.GetValueOrDefault("meh"), Is.EqualTo(2),
                "stored era: the roll was unbounded, so both rushed presses LANDED, graded on how early they were");
            Assert.That(stored.GetValueOrDefault("miss"), Is.EqualTo(6), "stored era: the six cells of the second line nobody reached");
            Assert.That(unbounded.Completion, Is.EqualTo(4 / 10.0).Within(1e-9), "stored era: completion");

            Assert.That(bounded.TotalScore, Is.LessThan(unbounded.TotalScore),
                "the bound costs this script two cells, which is exactly why an old row must keep its own bit");
        });
    }

    /// <summary>
    /// THE ONE COMBINATION NO CONFIG BIT CAN EXPRESS, and the reason the retired "FT" mod class must
    /// not be deleted: a run stored under it was played with the caret UNPINNED and WITHOUT the
    /// line-start snap, which no frame can say for itself, because bit 5 did not exist when those
    /// rows were recorded and so is CLEAR on every one of them. <c>ReplayEngineFeed.Apply</c>
    /// therefore takes the snap from the bit outright and the caret from
    /// <c>bit 5 || TypingEngine.FlexibleCaretFromMod</c>, which <see cref="TypeBeatReplayScorer"/>
    /// sets off the score's own mod list.
    ///
    /// <para>Pinned HERE rather than only in the game's own suite because this is the path the
    /// server-side recalc walks: it re-derives a stored row through the same scorer with the mods
    /// read off that row, so dropping the mod class, or failing to resolve the acronym, would
    /// re-derive every FT row on a pinned caret and report it as a corrupt score. The mod is
    /// resolved through the ruleset rather than constructed, which is what makes that the thing
    /// being tested.</para>
    ///
    /// <para>The keystrokes are the harness's <c>scripted/dragPastTheDeadline</c> case: "cat dog"
    /// with its final 'g' pressed 500 ms past the line's 6000 deadline and well inside the drag
    /// grace. An unpinned caret still has the line, so the press lands late on its own cell and the
    /// map completes; a pinned one lost the line at 6000, so the 'g' is a miss and the two presses
    /// after it are typos on "hi".</para>
    /// </summary>
    [Test]
    public void TheRetiredFletcherAcronymStillReDerivesAnUnpinnedCaret()
    {
        var legacyFletcher = new TypeBeatRuleset().CreateAllMods().SingleOrDefault(mod => mod.Acronym == "FT");

        Assert.That(legacyFletcher, Is.Not.Null, "the retired FT acronym must still resolve, or every stored FT row re-derives pinned");

        (double time, char key)[] keys =
        [
            (1000, 'c'), (1667, 'a'), (2333, 't'), (3000, ' '), (3000, 'd'), (3667, 'o'),
            (6500, 'g'), (7000, 'h'), (7500, 'i'),
        ];

        // Both arms leave bit 5 CLEAR, exactly as a stored row does. The ONLY difference is the mod.
        var storedFt = ScoreDrag(keys, [legacyFletcher!]);
        var storedPlain = ScoreDrag(keys, []);

        Assert.Multiple(() =>
        {
            var ft = Wire(storedFt.Statistics);
            Assert.That(ft.GetValueOrDefault("great"), Is.EqualTo(7), "FT: the drag grace kept the line, so the whole of it was typed");
            Assert.That(ft.GetValueOrDefault("miss"), Is.Zero, "FT: nothing was snatched away");
            Assert.That(storedFt.Completion, Is.EqualTo(1).Within(1e-9), "FT: completion");

            var plain = Wire(storedPlain.Statistics);
            Assert.That(plain.GetValueOrDefault("great"), Is.EqualTo(6), "pinned: the line went without the 'g'");
            Assert.That(plain.GetValueOrDefault("miss"), Is.EqualTo(1), "pinned: the 'g' the boundary snatched");
            Assert.That(plain.GetValueOrDefault(WireCounts.Key(TypeBeatResultMapping.UNFIXED_TYPO)), Is.EqualTo(2),
                "pinned: the caret was on 'hi', so the last two presses spoiled its cells");
            Assert.That(storedPlain.Completion, Is.EqualTo(6 / 9.0).Within(1e-9), "pinned: completion");

            // And the mod does NOT bring the snap with it, which is the whole point of separating
            // the two: an FT run re-derived with the snap would move its caret onto a line its
            // player was still parked behind.
            var parkedUnderFt = TypeBeatReplayScorer.Score(
                Map(GranularityOf("parkedLine"), Fixture("parkedLine")),
                [legacyFletcher!],
                Keystrokes([(1000, 'a'), (1500, 'b'), (12000, 'c'), (12500, 'd')], flexibleLines: false),
                TypoRule.Deferred,
                ComboRestoreRule.OnFix);

            Assert.That(Wire(parkedUnderFt.Statistics).GetValueOrDefault("miss"), Is.EqualTo(2),
                "an FT run never had the snap, so its caret stays parked and the last line is never reached");
        });
    }

    /// <summary>
    /// The scripted-key form of <see cref="Keystrokes"/>, for the era tests that need no node.
    /// <paramref name="boundedRush"/> defaults CLEAR, which is what the two bit-5 tests below want:
    /// they compare a pinned caret against the flexible one as backlog 208 shipped it, and both of
    /// their arms are stored eras that never had the bound.
    /// </summary>
    private static Replay Keystrokes((double time, char key)[] keys, bool flexibleLines, bool boundedRush = false)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: flexibleLines, boundedRush: boundedRush));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return replay;
    }

    private static TypeBeatReplayAccount ScoreParked((double time, char key)[] keys, bool flexibleLines)
        => TypeBeatReplayScorer.Score(Map(GranularityOf("parkedLine"), Fixture("parkedLine")), Array.Empty<Mod>(), Keystrokes(keys, flexibleLines), TypoRule.Deferred, ComboRestoreRule.OnFix);

    private static TypeBeatReplayAccount ScoreInstrumentalGap((double time, char key)[] keys, bool boundedRush)
        => TypeBeatReplayScorer.Score(Map(GranularityOf("instrumentalGap"), Fixture("instrumentalGap")), Array.Empty<Mod>(), Keystrokes(keys, flexibleLines: true, boundedRush: boundedRush), TypoRule.Deferred, ComboRestoreRule.OnFix);

    private static TypeBeatReplayAccount ScoreDrag((double time, char key)[] keys, Mod[] mods)
        => TypeBeatReplayScorer.Score(Map(GranularityOf("catDogThenHi"), Fixture("catDogThenHi")), mods, Keystrokes(keys, flexibleLines: false), TypoRule.Deferred, ComboRestoreRule.OnFix);

    private static TypeBeatReplayAccount ScoreStretch((double time, char key)[] keys, bool charTimedStretch)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: charTimedStretch));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("freestyleStretch"), Fixture("freestyleStretch")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    private static TypeBeatReplayAccount ScoreGapTypo((double time, char key)[] keys, bool wrongInputOnWordGaps)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: wrongInputOnWordGaps, strictSpaces: true));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("abCd"), Fixture("abCd")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    private static TypeBeatReplayAccount ScoreSpaceDiscipline((double time, char key)[] keys, bool strictSpaces)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: strictSpaces));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("abCd"), Fixture("abCd")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    private static TypeBeatReplayAccount ScoreWithEra((double time, char key)[] keys, bool syllableTiming, bool firstCharTiming = false)
    {
        var replay = new Replay();
        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: syllableTiming, firstCharTiming: firstCharTiming));

        foreach (var (time, key) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, key));

        return TypeBeatReplayScorer.Score(Map(GranularityOf("syllableWords"), Fixture("syllableWords")), Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
    }

    #region The health arm (backlog 306)

    /// <summary>
    /// One case played through <see cref="LiveHealthArm"/> under NO FAIL, fed exactly as
    /// <see cref="TypeBeatReplayScorer"/> feeds its engine (the display-cadence loop, each due key as
    /// <c>Update(time)</c> then the key), which is also exactly how the harness plays the browser's.
    /// </summary>
    private sealed record HealthRun(List<double> AfterKeys, double AtEnd, double Min, int FailRequests, double? FirstFailRequestAt, ScoreInfo Account);

    private static HealthRun PlayHealth(string fixture, JsonElement keys, bool spaceSkipsWord)
    {
        var lines = Fixture(fixture);
        var engine = HealthLiveParityTest.LiveEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = GranularityOf(fixture),
        }, spaceSkipsWord);

        using var arm = new LiveHealthArm(Map(GranularityOf(fixture), lines), engine, noFail: true);

        double clock = 0;
        double? firstFailRequestAt = null;
        arm.FailRequested += () => firstFailRequestAt ??= clock;

        var frames = keys.EnumerateArray().Select(k => (time: k[0].GetDouble(), key: k[1].GetString()![0])).ToList();
        var afterKeys = new List<double>();
        double min = 1;

        double end = engine.Lines.Count > 0 ? engine.Lines[^1].EndTime + engine.Lines[^1].SealGraceMs + 10000 : 10000;
        if (frames.Count > 0)
            end = Math.Max(end, frames[^1].time + 10000);

        void apply((double time, char key) frame)
        {
            clock = frame.time;
            engine.Update(frame.time);

            if (frame.key == TypeBeatReplayFrame.BACKSPACE)
                engine.ProcessBackspace();
            else if (frame.key == TypeBeatReplayFrame.ENTER)
                engine.ProcessEnter(frame.time);
            else
                engine.ProcessKey(frame.key, frame.time);

            afterKeys.Add(arm.Health.Health.Value);
            min = Math.Min(min, arm.Health.Health.Value);
        }

        int next = 0;

        for (double now = 0; now <= end; now += ReplayEngineFeed.FRAME_MS)
        {
            while (next < frames.Count && frames[next].time <= now)
                apply(frames[next++]);

            clock = now;
            engine.Update(now);
            min = Math.Min(min, arm.Health.Health.Value);
        }

        while (next < frames.Count)
            apply(frames[next++]);

        clock = end;
        engine.Update(end);
        min = Math.Min(min, arm.Health.Health.Value);

        return new HealthRun(afterKeys, arm.Health.Health.Value, min, arm.FailRequests, firstFailRequestAt, arm.Conclude());
    }

    /// <summary>
    /// THE HEALTH ARM of the sweep (backlog 306): every generated and scripted run's HP bar, browser
    /// against the desktop's real <see cref="TypeBeatHealthProcessor"/> behind
    /// <see cref="TypeBeatHealthFeed"/>, after every keystroke and at the end, to the last bit, plus
    /// the lowest it went, how many fails the No Fail veto refused and the instant of the first.
    ///
    /// <para>Both sides play under the veto (osu's No Fail: <c>HealthProcessor.Failed</c> answering
    /// false), because the account half of this sweep is compared against
    /// <see cref="TypeBeatReplayScorer"/>, which simulates no health and plays every run out in full.
    /// The browser no longer stops at the 13th rejection for the same reason, which is what retired
    /// the harness's old cap on rejected keys. The arm's own account is held against the browser's
    /// too, so the score half of <see cref="LiveHealthArm"/> is proven against every case in the
    /// sweep and not only against the handful of named health scripts.</para>
    /// </summary>
    [Test]
    public void EveryGeneratedRunDrainsTheSameBar()
    {
        var cases = browser_runs.Value.GetProperty("cases");

        Assert.Multiple(() =>
        {
            foreach (var browserCase in cases.EnumerateArray())
            {
                string scenario = browserCase.GetProperty("name").GetString()!;
                var game = PlayHealth(browserCase.GetProperty("fixture").GetString()!, browserCase.GetProperty("keys"), browserCase.GetProperty("spaceSkipsWord").GetBoolean());
                var health = browserCase.GetProperty("health");
                var afterKeys = health.GetProperty("afterKeys").EnumerateArray().Select(v => v.GetDouble()).ToList();

                Assert.That(afterKeys, Is.EqualTo(game.AfterKeys), $"{scenario}: the bar after every keystroke");
                Assert.That(health.GetProperty("atEnd").GetDouble(), Is.EqualTo(game.AtEnd), $"{scenario}: the bar at the end");
                Assert.That(health.GetProperty("min").GetDouble(), Is.EqualTo(game.Min), $"{scenario}: the lowest the bar went");
                Assert.That(health.GetProperty("failRequests").GetInt32(), Is.EqualTo(game.FailRequests), $"{scenario}: fails the veto refused");

                var first = health.GetProperty("firstFailRequestAt");
                Assert.That(first.ValueKind == JsonValueKind.Null ? (double?)null : first.GetDouble(), Is.EqualTo(game.FirstFailRequestAt), $"{scenario}: the first refused fail");

                var submitted = browserCase.GetProperty("submitted");
                Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Account.Statistics)), $"{scenario}: the arm's statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.Account.MaxCombo), $"{scenario}: the arm's max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.Account.TotalScore), $"{scenario}: the arm's total_score");
            }
        });
    }

    /// <summary>
    /// The health arm is only worth what it exercises: every one of the account's moves has to be
    /// reached somewhere in the sweep (a one-cell deferred drain, which is a typo or a one-cell skip;
    /// a multi-cell one, which is a word skip; a refund, and a refund the clamp at full cut short; a
    /// rejection's drain), and at least one run has to have the veto refuse a fail, with the bar at 0.
    /// Read off the browser side, which is the side the harness measures.
    /// </summary>
    [Test]
    public void TheHealthArmReachesEveryMoveOfTheAccount()
    {
        int oneCell = 0, multiCell = 0, refunds = 0, clamped = 0, rejections = 0, refused = 0, empty = 0;

        foreach (var browserCase in browser_runs.Value.GetProperty("cases").EnumerateArray())
        {
            var health = browserCase.GetProperty("health");

            if (health.GetProperty("oneCellDrains").GetInt32() > 0) oneCell++;
            if (health.GetProperty("multiCellDrains").GetInt32() > 0) multiCell++;
            if (health.GetProperty("refunds").GetInt32() > 0) refunds++;
            if (health.GetProperty("clampedRefunds").GetInt32() > 0) clamped++;
            if (health.GetProperty("rejectionDrains").GetInt32() > 0) rejections++;
            if (health.GetProperty("failRequests").GetInt32() > 0) refused++;
            if (health.GetProperty("min").GetDouble() == 0) empty++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(oneCell, Is.GreaterThan(0), "no run took a one-cell drain (a typo)");
            Assert.That(multiCell, Is.GreaterThan(0), "no run took a multi-cell drain (a word skip)");
            Assert.That(refunds, Is.GreaterThan(0), "no run refunded a drain");
            Assert.That(clamped, Is.GreaterThan(0), "no run refunded into a bar the clamp at full cut short");
            Assert.That(rejections, Is.GreaterThan(0), "no run drained on a rejected key");
            Assert.That(refused, Is.GreaterThan(0), "no run had the No Fail veto refuse a fail");
            Assert.That(empty, Is.GreaterThan(0), "no run emptied the bar");
        });
    }

    #endregion
}
