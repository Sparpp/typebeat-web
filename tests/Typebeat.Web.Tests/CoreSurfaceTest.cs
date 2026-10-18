using System.Text.Json;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests;

/// <summary>
/// The PUBLIC SURFACE of the browser engine, <c>window.TypeBeatCore</c>, pinned name by name.
///
/// <para><c>typebeat-core.js</c> is assembled from one file per C# counterpart under
/// <c>wwwroot/js/core</c> (W1-016). The lists below were taken from the single-file engine before
/// that split, so they hold the split to the same exports, in the same enumeration order and of the
/// same kinds, with nothing else left on <c>window</c>. Both load routes are held to them: Node,
/// where every fidelity harness requires <c>typebeat-core.js</c> by path, and the /play page,
/// replayed from <see cref="PlayEndpoints.RevisionScripts"/> in a context with no
/// <c>require</c>.</para>
///
/// <para>A deliberate new export updates these lists in the same change.</para>
/// </summary>
public class CoreSurfaceTest
{
    private static readonly string[] exports =
    [
        "isTypeable:function", "isFreestyle:function", "isCell:function", "isPunctuation:function",
        "normalize:function", "spellSpecialLetters:function", "stripBackingVocals:function", "defaultChar:function",
        "projectDefault:function", "toDefaultStream:function", "parseLyricOsu:function", "parseFormatVersion:function",
        "buildBeatmap:function", "syllableCharTarget:function", "parseAudioGain:function", "applyTrackGain:function",
        "parseFreestyleColour:function", "DEFAULT_FREESTYLE_COLOUR:string", "isSyllabifiable:function",
        "countSyllables:function", "splitPoints:function", "buildSyllables:function", "syllableIndexOf:function",
        "buildCharTimedStretch:function", "isCharTimedStretch:function", "isAuthoredValid:function",
        "derivedSplits:function", "splitsFor:function", "cellCuts:function", "segmentOf:function",
        "TypingEngine:function", "computeScore:function", "totalScoreOf:function", "rankFromAccuracy:function",
        "rankFromStatistics:function", "WINDOWS:object", "FREESTYLE_WINDOWS:object", "FREESTYLE_WINDOW_SCALE:number",
        "windowsFor:function", "classify:function", "toHitResult:function", "COMBO_PORTION_MAX:number",
        "ACCURACY_PORTION_MAX:number", "ACCURACY_PORTION_EXPONENT:number", "freestyleTick:function",
        "freestyleGlyph:function", "constants:object", "almostBigger:function", "usableRests:function",
        "pausedWordOf:function", "tokenCellTargets:function", "uncachedMaxComboPortion:function",
        "maxComboPortionOf:function",
    ];

    private static readonly string[] constants =
    [
        "CUE_LEAD_MS:number", "WRONG_KEY_FAIL_STREAK:number", "FREESTYLE_MARKER:string", "SHIMMER_INTERVAL_MS:number",
        "PUNCTUATION:string", "SPECIAL_LETTERS:object", "WORD_BREAK:string", "STRETCH_RUN_LENGTH:number",
        "FORMAT_MAGIC:string", "FALLBACK_FORMAT_VERSION:number", "LITERAL_BRACKETS_FROM_VERSION:number",
        "DEFAULT_AUDIO_GAIN:number", "MAX_AUDIO_GAIN:number", "FLETCHER_DRAG_GRACE_MS:number",
        "FIRST_LINE_LEAD_MS:number", "GREAT_HEALTH_INCREASE:number", "OK_HEALTH_INCREASE:number",
        "MEH_HEALTH_INCREASE:number", "MISS_HEALTH_DRAIN:number", "WRONG_KEY_HP_DRAIN:number", "HEALTH_EPSILON:number",
    ];

    private static readonly Lazy<JsonElement> harness =
        new Lazy<JsonElement>(() => JsHarness.Run("CoreSurfaceHarness.cjs", PlayEndpoints.RevisionScripts.ToArray()));

    private static string[] strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [TestCase("node")]
    [TestCase("browser")]
    public void TheCoreExportsTheSameSurface(string route)
    {
        var root = harness.Value.GetProperty(route);

        Assert.Multiple(() =>
        {
            Assert.That(strings(root.GetProperty("exports")), Is.EqualTo(exports));
            Assert.That(strings(root.GetProperty("constants")), Is.EqualTo(constants));
            Assert.That(strings(root.GetProperty("windowGlobals")), Is.EqualTo(new[] { "TypeBeatCore" }), "the parts object is removed once assembled");
        });
    }

    /// <summary>
    /// The page route stops at <c>typebeat-core.js</c>: everything it ran before that is an engine
    /// part, and every engine part runs before it.
    /// </summary>
    [Test]
    public void ThePageLoadsEveryPartBeforeTheEngine()
    {
        var loaded = strings(harness.Value.GetProperty("browser").GetProperty("loaded"));
        int engine = PlayEndpoints.RevisionScripts.ToList().IndexOf("/js/typebeat-core.js");

        Assert.That(loaded, Is.EqualTo(PlayEndpoints.RevisionScripts.Take(engine + 1)));
        Assert.That(loaded.SkipLast(1), Is.All.StartWith("/js/core/"));
    }
}
