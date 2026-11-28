using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// /play on a phone (backlog 501). Js/PlayerSoftKeysHarness.cjs mounts the real player over a fake
/// DOM and drives the hidden text field it owns on a touch device, logging every engine call. The
/// stakes are the shared leaderboards: a soft key must reach the engine exactly as the physical key
/// would (same router, same quantised instant), once, and a physical key whose text also lands in
/// the field must never be typed twice. On a desktop with a mouse the field must not exist at all.
/// </summary>
[TestFixture]
public class PlayerSoftKeysTest
{
    private const string Sentinel = "\u200B";

    private static JsonElement root;

    [OneTimeSetUp]
    public void RunHarness() => root = JsHarness.Run("PlayerSoftKeysHarness.cjs");

    private static JsonElement at(params string[] path)
    {
        var e = root;
        foreach (string p in path)
            e = e.GetProperty(p);
        return e;
    }

    private static bool flag(params string[] path) => at(path).GetBoolean();
    private static int num(params string[] path) => at(path).GetInt32();
    private static string? str(params string[] path) => at(path).GetString();

    private static string[] keys(params string[] path) => at(path).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // Each logged engine call as "fn:char@time" (char and time only where the call carries them).
    private static string[] calls(JsonElement list) => list.EnumerateArray().Select(c =>
    {
        string fn = c.GetProperty("fn").GetString()!;
        string ch = c.TryGetProperty("c", out var cv) ? cv.GetString()! : "";
        string t = c.TryGetProperty("t", out var tv) ? "@" + tv.GetDouble() : "";
        return fn + ":" + ch + t;
    }).ToArray();

    [Test]
    public void TheFieldDiff_IsOneBackspacePerRemovedCharThenOneKeyPerAddedChar()
    {
        Assert.Multiple(() =>
        {
            Assert.That(keys("diff", "plain"), Is.EqualTo(new[] { "a" }));
            Assert.That(keys("diff", "predictive"), Is.EqualTo(new[] { "h", "e", "l", "l", "o", " " }));
            Assert.That(keys("diff", "backspaceOnSentinel"), Is.EqualTo(new[] { "Backspace" }));
            Assert.That(keys("diff", "autocorrect"), Is.EqualTo(new[] { "Backspace", "Backspace", "h", "e", " " }));
            Assert.That(keys("diff", "composingStep"), Is.EqualTo(new[] { "l" }));
            Assert.That(keys("diff", "enter"), Is.EqualTo(new[] { "Enter" }));
            // A surrogate pair is never split: the emoji goes and comes back whole.
            Assert.That(keys("diff", "emoji"), Is.EqualTo(new[] { "Backspace", "\U0001F603" }));
            Assert.That(keys("diff", "unchanged"), Is.Empty);
        });
    }

    [Test]
    public void OnlyAnImeOrNamelessKeydown_IsLeftToTheInputPath()
    {
        Assert.Multiple(() =>
        {
            foreach (string k in new[] { "keyCode229", "unidentified", "process", "composing", "noKey" })
                Assert.That(flag("ime", k), Is.True, k);
            foreach (string k in new[] { "letter", "backspace", "enter" })
                Assert.That(flag("ime", k), Is.False, k);
        });
    }

    /// <summary>
    /// An on-screen key has no meaningful key-up, so a hold a soft key opens stays DOWN until the next
    /// key press of any kind ends it at that press's quantised instant (x.5 rounds half to even): the
    /// same letter typed again, the first key of a later multi-key change, or a physical key going
    /// down. A physical key-up never ends it, even the one for the same letter.
    /// </summary>
    [Test]
    public void ASoftKeysHold_StaysDownUntilTheNextKeyPress()
    {
        string softKey = str("softHold", "softHoldKey")!;

        Assert.Multiple(() =>
        {
            Assert.That(calls(at("softHold", "opened", "calls")), Is.EqualTo(new[] { "key:l@1300", "key:o@1300" }));
            Assert.That(flag("softHold", "opened", "holdOpen"), Is.True);
            Assert.That(str("softHold", "opened", "heldKey"), Is.EqualTo(softKey));

            Assert.That(calls(at("softHold", "sameLetter", "calls")), Is.EqualTo(new[] { "release:@1450", "key:o@1450" }));
            Assert.That(flag("softHold", "sameLetter", "holdOpen"), Is.True, "the new 'o' opens its own");

            Assert.That(calls(at("softHold", "inOneChange", "calls")), Is.EqualTo(new[] { "release:@1600", "key:v@1600", "key:o@1600" }));
            Assert.That(flag("softHold", "inOneChange", "holdOpen"), Is.True);

            Assert.That(at("softHold", "physicalKeyUp", "calls").GetArrayLength(), Is.Zero);
            Assert.That(flag("softHold", "physicalKeyUp", "holdOpen"), Is.True);

            Assert.That(calls(at("softHold", "physicalDown", "calls")), Is.EqualTo(new[] { "release:@1750", "key:v@1750" }));
            Assert.That(at("softHold", "physicalDown", "heldKey").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    /// <summary>
    /// The same rule on a real map through the mounted player ('s' held [1000, 1500]): with no further
    /// press the hold completes as a Great at its end with no release call; otherwise exactly one
    /// release lands at the instant of whatever ends it (the next soft key, an IME's 229 keydown, a
    /// pause, the field losing focus), graded on how much was held (40 percent here, a Miss).
    /// </summary>
    [Test]
    public void ASoftHold_CompletesAtItsEnd_OrEndsOnceAtWhatEndsIt()
    {
        Assert.Multiple(() =>
        {
            var end = at("holds", "completesAtEnd");
            double tPress = end.GetProperty("tPress").GetDouble();
            Assert.That(end.GetProperty("heldAfterPress").GetBoolean(), Is.True);
            Assert.That(calls(end.GetProperty("calls")), Is.EqualTo(new[] { "processKey:s@" + tPress }));
            Assert.That(end.GetProperty("tails")[0].GetProperty("type").GetString(), Is.EqualTo("Great"));
            Assert.That(end.GetProperty("tails")[0].GetProperty("releaseTime").GetDouble(), Is.EqualTo(1500));
            Assert.That(end.GetProperty("holdOpenAfter").GetBoolean(), Is.False);

            foreach (string how in new[] { "nextSoftKey", "imeKeydown", "pause", "fieldBlur" })
            {
                var r = at("holds", how);
                double t0 = r.GetProperty("tPress").GetDouble();
                double t1 = r.GetProperty("tEnd").GetDouble();
                var expected = new List<string> { "processKey:s@" + t0, "processRelease:@" + t1 };
                if (how == "nextSoftKey") expected.Add("processKey:o@" + t1);
                Assert.That(calls(r.GetProperty("calls")), Is.EqualTo(expected), how);
                Assert.That(r.GetProperty("tails").GetArrayLength(), Is.EqualTo(1), how);
                Assert.That(r.GetProperty("tails")[0].GetProperty("releaseTime").GetDouble(), Is.EqualTo(t1), how);
                Assert.That(r.GetProperty("tails")[0].GetProperty("type").GetString(), Is.EqualTo("Miss"), how);
                Assert.That(r.GetProperty("holdOpenAfter").GetBoolean(), Is.False, how);
            }

            Assert.That(flag("holds", "pause", "pausedWithHold"), Is.False, "released before the clock froze");
            Assert.That(flag("holds", "pause", "paused"), Is.False, "resumed");
        });
    }

    [Test]
    public void OnADesktop_NoFieldIsMade_AndTheKeydownPathTypesAsBefore()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("desktop", "field"), Is.False);
            Assert.That(flag("desktop", "prevented"), Is.True);
            Assert.That(keys("desktop", "calls"), Is.EqualTo(new[] { "processKey:a" }));
            Assert.That(num("desktop", "caret"), Is.EqualTo(1));
        });
    }

    [Test]
    public void OnACoarsePointer_TheFieldIsMadeFocusedAndQuiet()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("setup", "field"), Is.True);
            Assert.That(str("setup", "tag"), Is.EqualTo("INPUT"));
            Assert.That(str("setup", "type"), Is.EqualTo("text"));
            Assert.That(flag("setup", "focused"), Is.True);
            Assert.That(str("setup", "value"), Is.EqualTo(Sentinel));
            Assert.That(at("setup", "selection")[0].GetInt32(), Is.EqualTo(1));
            Assert.That(str("setup", "attributes", "autocomplete"), Is.EqualTo("off"));
            Assert.That(str("setup", "attributes", "autocorrect"), Is.EqualTo("off"));
            Assert.That(str("setup", "attributes", "autocapitalize"), Is.EqualTo("off"));
            Assert.That(str("setup", "attributes", "spellcheck"), Is.EqualTo("false"));
        });
    }

    /// <summary>
    /// Plain soft keys, an IME's 229 keydown and its input, a spacebar the keydown path already typed
    /// under an IME (its committed space is swallowed), and a two-letter predictive insert, each
    /// judged at the event's own quantised clock and each exactly once.
    /// </summary>
    [Test]
    public void SoftKeys_ReachTheEngineOnce_AtTheQuantisedInstant()
    {
        double tA = at("soft", "tA").GetDouble();
        double tB = at("soft", "tB").GetDouble();
        double tSpace = at("soft", "tSpace").GetDouble();
        double tCd = at("soft", "tCd").GetDouble();

        Assert.Multiple(() =>
        {
            Assert.That(calls(at("soft", "calls")), Is.EqualTo(new[]
            {
                "processKey:a@" + tA,
                "processKey:b@" + tB,
                "processKey: @" + tSpace,
                "processKey:c@" + tCd,
                "processKey:d@" + tCd,
            }));
            Assert.That(tA, Is.EqualTo(Math.Floor(tA)), "a whole millisecond");
            Assert.That(flag("soft", "imeDownPrevented"), Is.False, "the 229 keydown is left to the input path");
            Assert.That(flag("soft", "spaceDownPrevented"), Is.True, "the keydown path typed this one itself");
            Assert.That(num("soft", "caret"), Is.EqualTo(5), "ab cd typed out");
            Assert.That(str("soft", "valueAfterA"), Is.EqualTo(Sentinel), "reset after each read");
            Assert.That(str("soft", "valueAtEnd"), Is.EqualTo(Sentinel));
        });
    }

    [Test]
    public void BackspaceOnTheSentinel_IsOneEngineBackspace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(num("edits", "caretAfterTypo"), Is.EqualTo(2));
            Assert.That(calls(at("edits", "backspace", "calls")), Is.EqualTo(new[] { "processBackspace:" }));
            Assert.That(num("edits", "backspace", "caret"), Is.EqualTo(1));
            Assert.That(str("edits", "backspace", "value"), Is.EqualTo(Sentinel), "the sentinel is put back");
            Assert.That(at("edits", "backspace", "selection")[0].GetInt32(), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The no-double-count guard: a physical keydown is routed once by the keydown path, a cancellable
    /// beforeinput behind it is cancelled, and text that reaches the field anyway (an OS repeat the
    /// keydown path ignores) is dropped. After the key-up the field reads input again.
    /// </summary>
    [Test]
    public void APhysicalKeydownAndItsInput_TypeOnce()
    {
        double t = at("edits", "physical", "tPhysB").GetDouble();

        Assert.Multiple(() =>
        {
            Assert.That(flag("edits", "physical", "prevented"), Is.True);
            Assert.That(flag("edits", "physical", "repeatPrevented"), Is.False);
            Assert.That(flag("edits", "physical", "cancelled"), Is.True);
            Assert.That(str("edits", "physical", "valueAfterCancelled"), Is.EqualTo(Sentinel));
            Assert.That(str("edits", "physical", "valueAfterRepeat"), Is.EqualTo(Sentinel));
            Assert.That(calls(at("edits", "physical", "calls")), Is.EqualTo(new[] { "processKey:b@" + t }));
            Assert.That(calls(at("edits", "afterKeyUp")), Is.EqualTo(new[] { "processKey:c@" + t }));
        });
    }

    [Test]
    public void AnOpenComposition_TypesEachStep_AndIsResetOnlyAfterItEnds()
    {
        string[] c = calls(at("edits", "composition", "calls"));

        Assert.Multiple(() =>
        {
            Assert.That(c.Length, Is.EqualTo(2));
            Assert.That(c[0], Does.StartWith("processKey:d@"));
            Assert.That(c[1], Does.StartWith("processKey:e@"));
            Assert.That(str("edits", "composition", "valueMidComposition"), Is.EqualTo(Sentinel + "d"));
            Assert.That(str("edits", "composition", "valueAtCompositionEnd"), Is.EqualTo(Sentinel + "de"));
            Assert.That(str("edits", "composition", "valueAfter"), Is.EqualTo(Sentinel));
        });
    }

    [Test]
    public void APaste_IsNeverTyping_AndAPausedPlayIgnoresSoftKeys()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("edits", "pasted", "cancelled"), Is.True);
            Assert.That(str("edits", "pasted", "valueAfterCancel"), Is.EqualTo(Sentinel));
            Assert.That(at("edits", "pasted", "calls").GetArrayLength(), Is.Zero);
            Assert.That(str("edits", "pasted", "value"), Is.EqualTo(Sentinel));

            Assert.That(at("edits", "pause", "whilePaused").GetArrayLength(), Is.Zero);
            Assert.That(flag("edits", "pause", "pausedFocus"), Is.False, "the pause card has focus");
            Assert.That(flag("edits", "pause", "refocused"), Is.True, "resume gives it back");
            Assert.That(flag("edits", "fieldRemovedOnDestroy"), Is.True);
        });
    }

    [Test]
    public void ATouch_TurnsItOn_AndFocusIsKeptAndTheLyricKeptInView()
    {
        Assert.Multiple(() =>
        {
            Assert.That(flag("touch", "made"), Is.True);
            Assert.That(flag("touch", "focused"), Is.True);
            Assert.That(flag("touch", "stolen"), Is.True);
            Assert.That(flag("touch", "refocused"), Is.True, "taken back on the next task");
            Assert.That(flag("touch", "tapRefocus"), Is.True);
            // Row centre 530 against a visible band of [0, 300]: scrolled by 530 - 150.
            var scrolled = at("touch", "scrolled");
            Assert.That(scrolled.GetArrayLength(), Is.EqualTo(1));
            Assert.That(scrolled[0][1].GetDouble(), Is.EqualTo(380));
            Assert.That(num("touch", "viewportListenersAfterDestroy"), Is.Zero);
        });
    }

    /// <summary>
    /// Backlog 503: a key routed through the soft path marks the run, which reaches onFinish as
    /// <c>results.mobile</c> (play.js sends it, the server stores the "MB" Mobile mark) and draws the
    /// badge on the results card. A run typed on physical keys alone is not marked, on a touch
    /// device or a desktop, and a retry (from the results card, or mid-play from the pause card)
    /// starts unmarked. Every run here lands all five cells, so none is a vacuous empty run.
    /// </summary>
    [Test]
    public void ASoftKey_MarksTheRunMobile_AndARetryStartsUnmarked()
    {
        Assert.Multiple(() =>
        {
            // The router itself: marked by a key, never by an empty change.
            Assert.That(flag("softHold", "markedBefore"), Is.False);
            Assert.That(flag("softHold", "markedByEmpty"), Is.False);
            Assert.That(flag("softHold", "markedAfter"), Is.True);

            foreach (string run in new[] { "softThenPhysical", "retryFromResults", "touchPhysicalOnly", "desktop", "retryMidPlay" })
            {
                Assert.That(flag("mark", run, "finished"), Is.True, run + " concludes");
                Assert.That(num("mark", run, "landed"), Is.EqualTo(5), run + " is really typed");
            }

            Assert.That(flag("mark", "softThenPhysical", "mobile"), Is.True, "one soft key marks the run");
            Assert.That(flag("mark", "softThenPhysical", "modsRow"), Is.True);
            Assert.That(str("mark", "softThenPhysical", "modsHtml"),
                Is.EqualTo("<span class=\"mod-icons\"><span class=\"mod-icon mod-icon--other\" title=\"Mobile\">MB</span></span>"),
                "the site's own badge markup (_ModIcons.cshtml) with ModInfo's name and category");
            Assert.That(ModInfo.Name("MB"), Is.EqualTo("Mobile"), "the card and the stored row's badge agree");
            Assert.That(ModInfo.CategoryClass("MB"), Is.EqualTo("other"));

            Assert.That(flag("mark", "retryFromResults", "mobile"), Is.False, "play again starts unmarked");
            Assert.That(flag("mark", "touchPhysicalOnly", "mobile"), Is.False, "a touch device typed on physical keys is not mobile");
            Assert.That(flag("mark", "desktop", "mobile"), Is.False);
            Assert.That(flag("mark", "retryMidPlay", "mobile"), Is.False, "a mid-play retry drops the discarded run's mark");
            Assert.That(num("mark", "retryMidPlayFinishes"), Is.EqualTo(1), "the discarded run never reached onFinish");

            foreach (string run in new[] { "retryFromResults", "touchPhysicalOnly", "desktop", "retryMidPlay" })
                Assert.That(flag("mark", run, "modsRow"), Is.False, run + " draws no mods row");
        });
    }
}
