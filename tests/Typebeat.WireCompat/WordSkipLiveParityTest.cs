using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
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
using Typebeat.Tools.ScoreRecalc;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the RECLAIMABLE WORD SKIP (backlog 167, web half 168): the browser's
/// <c>typebeat-core.js</c> against the GAME's live <see cref="TypingEngine"/>, over the same
/// keystrokes on the same map.
///
/// <para>The two clients submit to the SAME leaderboards, so a skip that leaves a word re-typeable
/// on the desktop and misses it on the spot in the browser is not a cosmetic difference: the
/// identical performance scores lower in one client than in the other. Until backlog 168 that is
/// exactly what the browser did, so the guard has to be a comparison against the live C# rather
/// than against a literal transcribed from it: a literal copied out of a game that already
/// reclaimed would have been just as easy to write for a browser that does not.</para>
///
/// <para>This is the only project that compiles both repos, which is why the comparison lives here
/// rather than beside the other JS fidelity guards in <c>Typebeat.Web.Tests</c>. Those pin the
/// browser's ENGINE state (cell states, caret, combo, points) against the game suite's own
/// assertions (<c>WordSkipParityTest</c> there); this one pins the SUBMITTED ACCOUNT (statistics,
/// max_combo, total score, accuracy, completion, rank) against the numbers the game's own scorer
/// produces. The node harness is shared with them,
/// <c>tests/Typebeat.Web.Tests/Js/CoreWordSkipHarness.cjs</c>, so both sides read one description
/// of what was typed.</para>
///
/// <para>The C# side goes through <see cref="TypeBeatReplayScorer"/>, which is the headless
/// assembly of exactly the seams <c>TypeBeatPlayfield</c> wires up in a live play (the per-cell
/// results, the seal, the hand-mirrored mistype break, the combo restore, and since backlog 167
/// <c>WordAbandoned</c>'s break and <c>AbandonSealed</c>'s combo-neutral marks). It is fed under
/// the LIVE rules, because the browser has no era to select: it never re-derives a stored score.</para>
/// </summary>
[TestFixture]
public class WordSkipLiveParityTest
{
    #region The game suite's own fixtures, transcribed (NonVisual/SpaceSkipWordTest.cs)

    private static TimedUnit Unit(string text, double start, double end)
        => new TimedUnit { Text = text, StartTime = start, EndTime = end };

    private static LyricLine Line(string text, double start, double end, double singEnd, params TimedUnit[] units)
        => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

    private static TypeBeatBeatmap Map(params LyricLine[] lines)
    {
        var map = new TypeBeatBeatmap();

        for (int i = 0; i < lines.Length; i++)
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Line });

        map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
        map.BeatmapInfo.Metadata.Artist = "Test";
        map.BeatmapInfo.Metadata.Title = "Song";

        foreach (var hitObject in map.HitObjects)
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

        return map;
    }

    /// <summary>
    /// "cat dog", active [1000, 6000), SingEnd 5000. Cells c = 1000, a = 1666.67, t = 2333.33,
    /// ' ' = 3000, d = 3000, o = 3666.67, g = 4333.33. A three-letter word is the shortest one that
    /// can lose MORE than one cell to a skip.
    /// </summary>
    private static TypeBeatBeatmap CatDog() => Map(Line("cat dog", 1000, 6000, 5000,
        Unit("cat", 1000, 3000), Unit("dog", 3000, 5000)));

    /// <summary>
    /// "cat dog" with a SECOND line after it, "hi" on [6000, 10000): h = 6000, i = 6500. The only
    /// fixture here in which a line seals on abandoned cells while the play CARRIES ON, and so the
    /// only one where the seal's combo-neutral marks are observable at all: in a single-line run the
    /// seal is the last thing that happens, so a Miss zeroing the submitted combo there costs
    /// nothing (max_combo is banked and no judgement follows to be weighted by the wreckage).
    /// </summary>
    private static TypeBeatBeatmap CatDogThenHi() => Map(
        Line("cat dog", 1000, 6000, 5000, Unit("cat", 1000, 3000), Unit("dog", 3000, 5000)),
        Line("hi", 6000, 10000, 7000, Unit("hi", 6000, 7000)));

    /// <summary>"ab cd": a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.</summary>
    private static TypeBeatBeatmap AbCd() => Map(Line("ab cd", 1000, 4000, 3000,
        Unit("ab", 1000, 2000), Unit("cd", 2000, 3000)));

    private const double a_target = 1000 + 2000 / 3.0;
    private const double t_target = 1000 + 2 * 2000 / 3.0;
    private const double o_target = 3000 + 2000 / 3.0;
    private const double g_target = 3000 + 2 * 2000 / 3.0;

    #endregion

    #region Driving the two sides

    /// <summary>
    /// A replay of the given keystrokes, headed by the CONFIG frame the settings travel in: bit 0
    /// allow-wrong-input (the default model, which is all the browser has), bit 1 space-skips-word
    /// (on, or there is nothing to test), bit 3 wrong-input-on-word-gaps (on, the live model since
    /// backlog 181) and bit 4 strict-spaces (on, the live model since backlog 184), both of which
    /// are likewise all the browser has.
    ///
    /// <para>Neither bit changes a run below, because none of them presses a wrong LETTER on a word
    /// gap (which is what bit 4's park is scoped to, and what bit 3 decides at all) and every space
    /// they do press is either on a gap or a deliberate skip. They are set because this arm is
    /// supposed to be the live one on every axis, so a case added later cannot silently be scored
    /// under a stored era the browser can never select. Both are pinned on their own where they do
    /// move a run, in <c>EngineFuzzLiveParityTest</c>.</para>
    /// </summary>
    private static Replay Keystrokes(params (double time, char character)[] keys)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, wrongInputOnWordGaps: true, strictSpaces: true, backDatedSealBreak: true));

        foreach ((double time, char character) in keys)
            replay.Frames.Add(new TypeBeatReplayFrame(time, character));

        return replay;
    }

    private const char backspace = TypeBeatReplayFrame.BACKSPACE;

    /// <summary>
    /// The game's own account for one run, judged under every LIVE rule. The browser selects no era
    /// on any of these axes (it has no mods payload, no replay input and never re-scores a stored
    /// row), so the live arm is the only one it can be compared against.
    /// </summary>
    private static TypeBeatReplayAccount Play(IBeatmap map, Replay replay)
        => TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

    /// <summary>
    /// The game's counts as the wire spells them, minus the LINE containers'
    /// <see cref="TypeBeatResultMapping.LINE_RESULT"/>. One <c>ignore_hit</c> per line is the one
    /// key the browser has never carried, deliberately and from long before this change: the line
    /// object is scoring-inert (not scorable, no combo, no accuracy), so <c>typebeat-core.js</c>
    /// does not model it at all, and the server's <c>ScoringContract</c> ignores non-accuracy
    /// -affecting keys when it recomputes. Dropping it here is what keeps this comparison about the
    /// CELLS, which is what the skip moves; every other key, including the equally
    /// non-accuracy-affecting <c>combo_break</c>, is compared as it stands.
    /// </summary>
    private static Dictionary<string, int> Wire(IReadOnlyDictionary<HitResult, int> counts)
        => WireCounts.From(counts.Where(entry => entry.Key != TypeBeatResultMapping.LINE_RESULT).ToDictionary(entry => entry.Key, entry => entry.Value));

    /// <summary>
    /// Asserts the browser's submitted account for one run against the game's, field for field. No
    /// tolerance on the doubles: the two are the same sequence of operations on the same inputs, so
    /// any difference at all is a divergence rather than rounding.
    /// </summary>
    private static void AssertSameAccount(string scenario, TypeBeatReplayAccount game, JsonElement browser)
    {
        var submitted = browser.GetProperty("submitted");

        Assert.Multiple(() =>
        {
            Assert.That(Dict(submitted, "statistics"), Is.EquivalentTo(Wire(game.Statistics)), $"{scenario}: statistics");
            Assert.That(Dict(submitted, "maximumStatistics"), Is.EquivalentTo(Wire(game.MaximumStatistics)), $"{scenario}: maximum_statistics");
            Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(game.MaxCombo), $"{scenario}: max_combo");
            Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(game.TotalScore), $"{scenario}: total_score");
            Assert.That(submitted.GetProperty("accuracy").GetDouble(), Is.EqualTo(game.Accuracy), $"{scenario}: accuracy");
            Assert.That(submitted.GetProperty("completion").GetDouble(), Is.EqualTo(game.Completion), $"{scenario}: completion");
            Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo(game.Rank.ToString()), $"{scenario}: rank");
        });
    }

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    #endregion

    #region The browser side: the shared node harness

    private static readonly Lazy<JsonElement> browser_runs = new Lazy<JsonElement>(RunHarness);

    /// <summary>
    /// Runs the SAME harness the <c>Typebeat.Web.Tests</c> word-skip guard runs, against the served
    /// <c>wwwroot/js/typebeat-core.js</c>, and parses its stdout as JSON. Node is optional on a dev
    /// box, so a missing node ignores the test rather than failing it (CI has node), which is the
    /// rule <c>JsHarness</c> already applies for the other guards.
    /// </summary>
    private static JsonElement RunHarness()
    {
        string root = RepoRoot();
        string core = Path.Combine(root, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js");
        string harness = Path.Combine(root, "tests", "Typebeat.Web.Tests", "Js", "CoreWordSkipHarness.cjs");

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add(core);

        Process process;

        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Assert.Ignore($"node is not available to run the JS fidelity harness: {ex.Message}");
            throw; // unreachable
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), $"harness failed: {stderr}");
        return JsonDocument.Parse(stdout).RootElement;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Typebeat.Web", "wwwroot", "js", "typebeat-core.js")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("could not locate the repo root");
    }

    private static JsonElement Browser(string scenario) => browser_runs.Value.GetProperty(scenario);

    #endregion

    /// <summary>
    /// The reference the other two runs are read against: "cat dog" typed straight through, nothing
    /// skipped. A perfect play is the easy case, and it is here so that a divergence in the skip runs
    /// cannot be explained away as the two clients disagreeing about the map.
    /// </summary>
    [Test]
    public void ACleanRunAgrees()
    {
        var game = Play(CatDog(), Keystrokes(
            (1000, 'c'), (a_target, 'a'), (t_target, 't'), (3000, ' '),
            (3000, 'd'), (o_target, 'o'), (g_target, 'g')));

        Assert.That(game.TotalScore, Is.EqualTo(1_000_000), "every cell typed on its target");
        AssertSameAccount("cleanRun", game, Browser("cleanRun"));
    }

    /// <summary>
    /// THE PIN THE DESIGN RESTS ON, and the one the browser used to fail: a skip the player never
    /// returns to costs exactly what it always cost. The cells are given up at the keypress, resolve
    /// as ordinary misses at the seal, and take exactly ONE combo break between them.
    ///
    /// <para>Both halves of the seal settlement are visible in this one number. The abandoned cells'
    /// Misses are applied COMBO-NEUTRAL, so they do not break the run the player rebuilt through the
    /// rest of the line a second time; and the combo-neutral path is gated on the result INCREASING
    /// combo, so those same Misses are still weighted at zero rather than being paid a full
    /// combo-weighted portion for characters nobody typed. Get the gate wrong and the browser pays
    /// for the skipped word twice over, which is a higher total score than the desktop for the
    /// identical performance.</para>
    /// </summary>
    [Test]
    public void ASkipNeverReturnedToAgrees()
    {
        var game = Play(CatDog(), Keystrokes(
            (1000, 'c'),
            (2600, ' '), // abandon "at", never come back
            (3000, 'd'), (o_target, 'o'), (g_target, 'g')));

        Assert.Multiple(() =>
        {
            Assert.That(game.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(2), "the two cells nobody came back for");
            Assert.That(game.MaxCombo, Is.EqualTo(4), "the run rebuilt after the skip is not broken again at the seal");
        });

        AssertSameAccount("skipNeverReturnedTo", game, Browser("skipNeverReturnedTo"));
    }

    /// <summary>
    /// The other half of the pair: a skip the player DOES come back for costs nothing beyond the
    /// detour. One backspace re-enters the word, the cells earn their ordinary judgements, and the
    /// streak the skip broke resumes on the first cell it abandoned, so the map still ends on a
    /// perfect X with every cell typed.
    ///
    /// <para>The total score is deliberately NOT the clean run's, and the difference is real rather
    /// than a rounding artefact: the word gap was typed once, at combo 0 immediately after the break,
    /// and the retype of it on the way back through is scoring-inert. Both clients have to agree on
    /// that too, which is what asserting the exact total here says.</para>
    /// </summary>
    [Test]
    public void AFullyReclaimedSkipAgrees()
    {
        var game = Play(CatDog(), Keystrokes(
            (1000, 'c'),
            (2600, ' '),          // abandon "at"
            (2600, backspace),    // off the typed gap
            (2600, backspace),    // through the whole abandoned run, onto 'c'
            (1000, 'c'),          // inert retype
            (a_target, 'a'),      // the snapshot cell: the run resumes here
            (t_target, 't'), (3000, ' '), (3000, 'd'), (o_target, 'o'), (g_target, 'g')));

        Assert.Multiple(() =>
        {
            Assert.That(game.Statistics.ContainsKey(HitResult.Miss), Is.False, "every cell was typed in the end");
            Assert.That(game.MaxCombo, Is.EqualTo(7), "the streak the skip broke came back on the cell it was taken against");
            Assert.That(game.Rank, Is.EqualTo(typebeat.Game.Scoring.ScoreRank.X));
        });

        AssertSameAccount("fullyReclaimedRun", game, Browser("fullyReclaimedRun"));
    }

    /// <summary>
    /// The last word of a line has no gap after it, so the skip lands the caret at the end of the
    /// line and the cells sit phantom until the line's own deadline. They resolve there, as the
    /// misses they turned out to be, and WITHOUT a second combo break.
    /// </summary>
    [Test]
    public void SkippingTheLastWordOfALineAgrees()
    {
        var game = Play(AbCd(), Keystrokes(
            (1000, 'a'), (1500, 'b'), (2000, ' '),
            (2100, ' '))); // the whole last word goes

        Assert.That(game.MaxCombo, Is.EqualTo(3), "the three cells typed before the skip");
        AssertSameAccount("skippingTheLastWordOfALine", game, Browser("skippingTheLastWordOfALine"));
    }

    /// <summary>
    /// The seal's COMBO-NEUTRAL marks, isolated: the same never-reclaimed skip, but on a line the
    /// play carries on past. The abandoned cells resolve as Misses while the player is still holding
    /// the run they rebuilt after the skip, so those Misses must leave combo exactly where they find
    /// it, or the run is wiped a second time for a break already taken and every judgement on the
    /// next line is weighted by the wreckage.
    ///
    /// <para>This is the ONE shape in which the marks are observable, which is why it is here rather
    /// than folded into the single-line pins above: when the seal is the last thing that happens,
    /// max_combo is already banked and no judgement follows, so a second break there costs nothing
    /// and a browser that never marked anything would still agree with the desktop.</para>
    /// </summary>
    [Test]
    public void ASkipOnALineThePlayCarriesOnPastAgrees()
    {
        var game = Play(CatDogThenHi(), Keystrokes(
            (1000, 'c'),
            (2600, ' '), // abandon "at", never come back
            (3000, 'd'), (o_target, 'o'), (g_target, 'g'),
            (6000, 'h'), (6500, 'i')));

        Assert.Multiple(() =>
        {
            Assert.That(game.MaxCombo, Is.EqualTo(6),
                "the four cells rebuilt after the skip plus the whole of the next line, uninterrupted by the seal");
            Assert.That(game.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(2));
        });

        AssertSameAccount("skipThenTheNextLine", game, Browser("skipThenTheNextLine"));
    }

    /// <summary>
    /// The whole feature as one run, on the game suite's own
    /// <c>EveryAbandonedCellLeavesThePhantomStateExactlyOnce</c> sequence: skip "cat", come back for
    /// it, then skip "dog" and never return. Both exits from the phantom state happen in one play,
    /// which is what makes this the pin that they do not overlap or leave a cell behind.
    /// </summary>
    [Test]
    public void OneRunThroughBothExitsAgrees()
    {
        var game = Play(CatDog(), Keystrokes(
            (1000, 'c'),
            (2600, ' '),
            (2600, backspace),
            (2600, backspace),
            (1000, 'c'), (a_target, 'a'), (t_target, 't'), (3000, ' '), (3000, 'd'),
            (3800, ' '))); // abandon the rest of "dog"

        Assert.That(game.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(2), "only the word nobody came back for");
        AssertSameAccount("everyAbandonedCellLeavesExactlyOnce", game, Browser("everyAbandonedCellLeavesExactlyOnce"));
    }
}
