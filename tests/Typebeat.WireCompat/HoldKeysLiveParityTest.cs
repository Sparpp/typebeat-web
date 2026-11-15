using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the HOLD half of the browser's key handler: which key's release ends a hold,
/// and when. The browser arm is <c>PlayerHoldKeysHarness.cjs</c>, which drives the SHIPPED router
/// (<c>TypeBeatCore.display.routeKeyDown</c> / <c>routeKeyUp</c> / <c>releaseHeldKey</c>) through
/// keydowns, keyups and a focus loss between the 60 Hz ticks of a fake clock. This side feeds the
/// game's real <see cref="TypingEngine"/> the same ticks and the same events through
/// <c>TypeBeatPlayfield.KeyHandler</c>'s hold rules, transcribed line for line below (the handler is
/// a Drawable over a live input manager, so it cannot be driven headlessly): <c>OnKeyDown</c>'s
/// release of an open hold by any other non-modifier, non-repeat key, <c>rememberHeldKey</c> after a
/// press that opened a hold, <c>OnKeyUp</c>'s release by the held key, and <c>releaseHold</c>'s
/// engine advance before <see cref="TypingEngine.ProcessRelease"/>. A focus loss is the input
/// manager releasing every key, which reaches <c>OnKeyUp</c> for the held one.
///
/// <para>Compared with no tolerance: every engine call each side made (the op, its character, its
/// quantised time and its answer), and the account the game's <see cref="TypeBeatReplayScorer"/>
/// derives from this side's calls against the browser's own submitted account.</para>
/// </summary>
[TestFixture]
public class HoldKeysLiveParityTest
{
    /// <summary>The map the harness plays (its scenarios are written against these spans).</summary>
    private const string timing = """
        {"version": 2, "song_end_ms": 12000, "lines": [
          {"text": "love me", "start_ms": 1000, "end_ms": 3000, "words": [
            {"text": "love", "start_ms": 1000, "end_ms": 2200, "score": 1, "holds": [{"char": 1, "start_ms": 1300, "end_ms": 1900, "ticks": [1450, 1600, 1750]}]},
            {"text": "me", "start_ms": 2200, "end_ms": 3000, "score": 1, "holds": [{"char": 1, "start_ms": 2600, "end_ms": 3000}]}]},
          {"text": "Oh go", "start_ms": 6000, "end_ms": 8000, "words": [
            {"text": "Oh", "start_ms": 6000, "end_ms": 6800, "score": 1, "holds": [{"char": 0, "start_ms": 6000, "end_ms": 6500}]},
            {"text": "go", "start_ms": 6800, "end_ms": 8000, "score": 1}]}
        ]}
        """;

    private const double tick_ms = 1000.0 / 60;
    private const double tick_phase = 0.37;
    private const double end = 14000;

    private static readonly Lazy<string> osu = new Lazy<string>(() => LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", timing));

    private static readonly Lazy<JsonElement> harness = new Lazy<JsonElement>(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-hold-keys-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { osu = osu.Value }));

        try
        {
            return NodeHarness.Run("PlayerHoldKeysHarness.cjs", path);
        }
        finally
        {
            File.Delete(path);
        }
    });

    private static IBeatmap playable()
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu.Value));
        using var reader = new LineBufferedReader(stream);
        var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        return new FlatWorkingBeatmap(decoded).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, Array.Empty<Mod>());
    }

    private sealed record Call(string Fn, string? Char, double? Time, bool Result, double FrameTime);

    /// <summary><c>KeyHandler.isModifier</c>, on the browser's codes: Shift, Ctrl, Alt and Win, either side.</summary>
    private static bool isModifier(string? code)
        => code is "ShiftLeft" or "ShiftRight" or "ControlLeft" or "ControlRight" or "AltLeft" or "AltRight" or "MetaLeft" or "MetaRight";

    /// <summary>The desktop key handler's hold rules over the game's engine, for one scenario's events.</summary>
    private static List<Call> desktop(IBeatmap map, JsonElement events)
    {
        var engine = HealthLiveParityTest.LiveEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToList(),
            Granularity = map.HitObjects.OfType<TypeBeatHitObject>().First().Granularity,
        }, spaceSkipsWord: true);

        var calls = new List<Call>();
        string? heldKey = null;

        // KeyHandler.releaseHold.
        void releaseHold(double time)
        {
            heldKey = null;

            if (engine.ActiveHold == null)
                return;

            engine.Update(time);
            calls.Add(new Call("release", null, time, engine.ProcessRelease(time), time));
        }

        void apply(JsonElement e)
        {
            string type = e.GetProperty("type").GetString()!;

            // Millisecond-quantised as OnKeyDown / OnKeyUp quantise Time.Current (Math.Round, to even).
            double time = Math.Round(e.GetProperty("t").GetDouble());
            string? key = e.GetProperty("key").ValueKind == JsonValueKind.Null ? null : e.GetProperty("key").GetString();
            string? code = e.GetProperty("code").ValueKind == JsonValueKind.Null ? null : e.GetProperty("code").GetString();
            bool repeat = e.GetProperty("repeat").GetBoolean();

            switch (type)
            {
                case "blur":
                    // Focus loss: the input manager releases every key, and OnKeyUp sees the held one.
                    if (heldKey != null)
                        releaseHold(time);
                    return;

                case "up":
                    // OnKeyUp.
                    if (heldKey != null && code == heldKey)
                        releaseHold(time);
                    return;
            }

            // OnKeyDown: another key going down ends a hold first, as its own frame.
            if (engine.ActiveHold != null && !repeat && code != heldKey && !isModifier(code))
                releaseHold(time);

            // A modifier types nothing, and a held character key's repeat produces nothing.
            if (isModifier(code) || repeat)
                return;

            engine.Update(time);

            if (key == "Backspace")
            {
                calls.Add(new Call("backspace", null, null, engine.ProcessBackspace(), time));
                return;
            }

            if (key == "Enter")
            {
                calls.Add(new Call("enter", null, time, engine.ProcessEnter(time), time));
                return;
            }

            // typeCharacters + rememberHeldKey: a press that opened a NEW hold remembers its key.
            var holdBefore = engine.ActiveHold;
            bool typed = engine.ProcessKey(key![0], time);
            calls.Add(new Call("key", key, time, typed, time));

            if (typed && engine.ActiveHold is TypingEngine.ActiveHoldInfo hold && hold != holdBefore)
                heldKey = code;
        }

        var list = events.EnumerateArray().ToList();
        int next = 0;

        for (int k = 0; ; k++)
        {
            double tick = tick_phase + k * tick_ms;

            while (next < list.Count && list[next].GetProperty("t").GetDouble() < tick)
                apply(list[next++]);

            if (tick > end)
                break;

            engine.Update(tick);
        }

        return calls;
    }

    private static Replay replayOf(List<Call> calls)
    {
        var replay = new Replay();

        replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true,
            charTimedStretch: true, flexibleLines: true, boundedRush: true, firstCharTiming: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true,
            manualNewlines: true, newlineOnTypedLetter: true, firstLineLeadIn: true));
        replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true,
            earlyFinish: true, keyTimeJudgement: true, bestRetypeJudgement: true, stacksDisplacedClaims: true));

        // Only the calls the desktop would record: a press or release the engine took, and every backspace.
        foreach (var call in calls)
        {
            if (call.Fn == "key" && call.Result)
                replay.Frames.Add(new TypeBeatReplayFrame(call.FrameTime, call.Char![0]));
            else if (call.Fn == "release" && call.Result)
                replay.Frames.Add(new TypeBeatReplayFrame(call.FrameTime, TypeBeatReplayFrame.RELEASE));
            else if (call.Fn == "backspace")
                replay.Frames.Add(new TypeBeatReplayFrame(call.FrameTime, TypeBeatReplayFrame.BACKSPACE));
            else if (call.Fn == "enter" && call.Result)
                replay.Frames.Add(new TypeBeatReplayFrame(call.FrameTime, TypeBeatReplayFrame.ENTER));
        }

        return replay;
    }

    [Test]
    public void EveryHoldKeyScenarioMakesTheDesktopsCalls()
    {
        var scenarios = harness.Value;

        Assert.That(scenarios.EnumerateObject().Count(), Is.GreaterThanOrEqualTo(9), "the harness should carry every scenario");

        Assert.Multiple(() =>
        {
            foreach (var scenario in scenarios.EnumerateObject())
            {
                var map = playable();
                var game = desktop(map, scenario.Value.GetProperty("events"));
                var browser = scenario.Value.GetProperty("calls").EnumerateArray().ToList();

                Assert.That(browser.Count, Is.EqualTo(game.Count), $"{scenario.Name}: engine call count");

                for (int i = 0; i < browser.Count && i < game.Count; i++)
                {
                    var b = browser[i];
                    string at = $"{scenario.Name} call {i}";

                    Assert.That(b.GetProperty("fn").GetString(), Is.EqualTo(game[i].Fn), $"{at}: op");
                    Assert.That(b.GetProperty("result").GetBoolean(), Is.EqualTo(game[i].Result), $"{at}: answer");

                    if (game[i].Char != null)
                        Assert.That(b.GetProperty("c").GetString(), Is.EqualTo(game[i].Char), $"{at}: character");

                    if (game[i].Time is double time)
                        Assert.That(b.GetProperty("t").GetDouble(), Is.EqualTo(time), $"{at}: time");
                }

                var account = TypeBeatReplayScorer.Score(playable(), Array.Empty<Mod>(), replayOf(game), TypoRule.Deferred, ComboRestoreRule.OnFix);
                var submitted = scenario.Value.GetProperty("submitted");

                Assert.That(HealthLiveParityTest.Dict(submitted, "statistics"), Is.EquivalentTo(HealthLiveParityTest.Wire(account.Statistics)), $"{scenario.Name}: statistics");
                Assert.That(submitted.GetProperty("maxCombo").GetInt32(), Is.EqualTo(account.MaxCombo), $"{scenario.Name}: max_combo");
                Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(account.TotalScore), $"{scenario.Name}: total_score");
            }
        });
    }

    /// <summary>
    /// Non-vacuity: the scenarios reach what they are named for, read off the browser's own calls. A
    /// release judged at a key-up, one at another key's key-down and one at a focus loss; none for a
    /// modifier, a repeat, or a key-up past the hold's end.
    /// </summary>
    [Test]
    public void TheScenariosReachWhatTheyAreNamedFor()
    {
        var s = harness.Value;

        int releases(string name) => s.GetProperty(name).GetProperty("calls").EnumerateArray().Count(c => c.GetProperty("fn").GetString() == "release" && c.GetProperty("result").GetBoolean());

        double? firstReleaseAt(string name) => s.GetProperty(name).GetProperty("calls").EnumerateArray()
            .Where(c => c.GetProperty("fn").GetString() == "release").Select(c => (double?)c.GetProperty("t").GetDouble()).FirstOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(firstReleaseAt("keyUpReleases"), Is.EqualTo(1651), "the held key's key-up, quantised");
            Assert.That(firstReleaseAt("anotherKeyReleasesFirst"), Is.EqualTo(1700), "another key's key-down, at its own instant");
            Assert.That(releases("anotherKeyReleasesFirst"), Is.EqualTo(1), "the held key's later key-up ends nothing");
            Assert.That(firstReleaseAt("modifiersDoNotRelease"), Is.EqualTo(1850), "only the held key's key-up, past every modifier");
            Assert.That(firstReleaseAt("repeatDoesNotRelease"), Is.EqualTo(1880), "only the key-up, past every repeat");
            Assert.That(firstReleaseAt("focusLossReleases"), Is.EqualTo(1500), "the focus loss, quantised to even");
            Assert.That(releases("keyUpPastTheEnd"), Is.Zero, "a key-up past the end finds the hold completed");
            Assert.That(firstReleaseAt("backspaceReleasesFirst"), Is.EqualTo(1500), "the backspace ends the hold first");
            Assert.That(firstReleaseAt("shiftedCapital"), Is.EqualTo(6400), "the letter's key-up, not the Shift's");
        });
    }
}
