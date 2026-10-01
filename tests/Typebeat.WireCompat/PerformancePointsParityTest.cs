using Newtonsoft.Json;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;

using ClientArm = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty.JudgementArm;
using ClientAxis = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty.EnduranceAxis;
using ClientDifficulty = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPp = typebeat.Game.Rulesets.TypeBeat.Scoring.PerformancePoints;
using ClientTypability = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TypabilityIndex;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using ClientWordPause = typebeat.Game.Rulesets.TypeBeat.Beatmaps.WordPause;
using ServerArm = Typebeat.Web.Packages.Lyrics.LyricDifficulty.JudgementArm;
using ServerAxis = Typebeat.Web.Packages.Lyrics.LyricDifficulty.EnduranceAxis;
using ServerDifficulty = Typebeat.Web.Packages.Lyrics.LyricDifficulty;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;
using ServerPp = Typebeat.Web.Scoring.PerformancePoints;
using ServerRates = Typebeat.Web.Scoring.BeatmapRatings;
using ServerTypability = Typebeat.Web.Packages.Lyrics.TypabilityIndex;
using ServerTypeability = Typebeat.Web.Packages.Lyrics.Typeability;
using ServerUnit = Typebeat.Web.Packages.Lyrics.TimedUnit;
using ServerWordPause = Typebeat.Web.Packages.Lyrics.WordPause;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for PERFORMANCE POINTS (backlog 74).
///
/// <para>
/// pp exists twice: the server's <see cref="ServerPp"/> is what writes <c>scores.pp</c>, and the
/// game's <see cref="ClientPp"/> is what the in-game live counter and the results screen price a
/// play with. A player who watches a counter climb to 214 and then sees 198 on their profile has
/// been lied to, so the two must be the SAME arithmetic, not merely similar. This is the only
/// project that compiles both repos, so this is where that is provable.
/// </para>
///
/// <para>
/// Everything below drives the two implementations over the same play and asserts EXACT equality
/// (no tolerance): they are the same sequence of double operations, so any difference at all is a
/// divergence, not rounding. The inputs deliberately cross every seam where the two sides are
/// SHAPED differently and could therefore drift without either repo's own tests noticing:
/// </para>
///
/// <list type="number">
/// <item>STATISTICS. The client counts <see cref="HitResult"/> members; the server counts snake_case
/// JSON keys. The bridge asserted here is the real one: the dictionary is serialised exactly as a
/// submitted score serialises it, and the server reads that text.</item>
/// <item>MODS. The client holds real <see cref="Mod"/> objects with bindable settings; the server
/// holds <c>{acronym, rate}</c> parsed out of the <c>scores.mods</c> jsonb. The bridge is again the
/// real one: <see cref="APIMod"/> to JSON to <c>ScoreMods.Parse</c>.</item>
/// <item>RATE ELIGIBILITY. The client decides which CLOCK RATE a play is priced at and computes the
/// rating itself; the server decides which STORED CELL prices it. Those are different-looking
/// decisions that must always agree on eligible-or-not, and on which reading.</item>
/// <item>THE RATING MATRIX. The client's whole claim to price a play locally rests on its
/// <c>LyricDifficulty</c> producing the same numbers the server stores. Since the difficulty rework
/// that is EIGHTEEN numbers and not three: a judgement arm (none / Easy / Hard Rock) times a stream
/// (plain / Literate) times a rate (1.00 / 1.50 / 0.75). Each of the eighteen is asserted, and so is
/// the DIFFICULT-CHARACTER count each carries, which is the second half of a price since
/// <c>VERSION</c> 22 and which the server stores in <c>beatmaps.ratings</c>
/// (034_ratings_matrix.sql).</item>
/// <item>TYPABILITY. Both sides now read an embedded table of per-line scores and, for a line the
/// table does not carry, compute one from the same model and dictionaries. The server's copy is
/// embedded under a DIFFERENT MANIFEST PREFIX, which is the one sanctioned difference between the
/// two files, so "the same bytes are actually reachable on both sides" is a claim that has to be
/// made here rather than assumed.</item>
/// </list>
///
/// <para>
/// This file compiles only where the game repo is resolvable (a sibling checkout locally, the
/// pinned submodule in CI), which is the same condition every other test in this project has.
/// </para>
/// </summary>
[TestFixture]
public class PerformancePointsParityTest
{
    private static readonly IReadOnlyList<Mod> no_client_mods = Array.Empty<Mod>();
    private static readonly IReadOnlyList<Typebeat.Web.ScoreMod> no_server_mods = Array.Empty<Typebeat.Web.ScoreMod>();

    #region Bridges: the real wire, not a hand-written translation

    /// <summary>
    /// The client's judgement counts as the server receives them: serialised exactly the way a
    /// submitted <c>SoloScoreInfo.Statistics</c> is (Newtonsoft, enum keys via their
    /// <c>EnumMember</c> names), then read back through the server's own jsonb reader.
    /// </summary>
    private static ServerPp.NoteCounts ServerCounts(IReadOnlyDictionary<HitResult, int> statistics)
        => ServerPp.CountNotes(JsonConvert.SerializeObject(statistics));

    /// <summary>The client's mod stack as the server stores and re-reads it.</summary>
    private static IReadOnlyList<Typebeat.Web.ScoreMod> ServerMods(IReadOnlyList<Mod> mods)
        => Typebeat.Web.ScoreMods.Parse(JsonConvert.SerializeObject(mods.Select(m => new APIMod(m)).ToArray()));

    private static IReadOnlyList<Mod> Stack(params Mod[] mods) => mods;

    private static T At<T>(T mod, double rate) where T : ModRateAdjust
    {
        mod.SpeedChange.Value = rate;
        return mod;
    }

    /// <summary>
    /// Every mod the type!beat ruleset ships, read off <c>TypeBeatRuleset.GetModsFor</c>'s five
    /// player-facing columns plus the resolvable System one.
    ///
    /// <para>THIS LIST WENT STALE AND THAT IS EXACTLY THE FAILURE IT EXISTS TO CATCH. It was
    /// missing Easy, Hard Rock, Recite, Dyslexia, Autoplay, Puppeteer and Conductor when backlog
    /// 270 found it, so the two sweeps below (single and pair) had never once reached the EZ, HR or
    /// RE arms of either mirror: a multiplier landed in one repo only would have gone unnoticed
    /// here and shown up as a leaderboard divergence instead. A mod added to the ruleset has to be
    /// added here, and nothing enforces that but reading the ruleset.</para>
    ///
    /// <para><c>ModWindUp</c> and <c>ModWindDown</c> are deliberately absent: they are the
    /// always-unranked pair, so no score carrying one is ever priced at all.</para>
    /// </summary>
    private static IReadOnlyList<Mod> AllRulesetMods() =>
    [
        new TypeBeatModEasy(),
        new TypeBeatModNoFail(),
        new TypeBeatModHalfTime(),
        new TypeBeatModHardRock(),
        new TypeBeatModSuddenDeath(),
        new TypeBeatModDoubleTime(),
        new TypeBeatModNightcore(),
        new TypeBeatModFlashlight(),
        new TypeBeatModLiterate(),
        new TypeBeatModRecite(),
        new TypeBeatModFletcher(),
        new TypeBeatModGatekeeper(),
        new TypeBeatModDyslexia(),
        new TypeBeatModAutoplay(),
        new TypeBeatModMashing(),
        new TypeBeatModMuted(),
        new TypeBeatModPuppeteer(),
        new TypeBeatModConductor(),
    ];

    /// <summary>
    /// THE COMBO FACTOR the price is multiplied by (VERSION 22), spelled once because it is needed
    /// on both sides of several ratios below: a ceiling the map's LENGTH earns (1% per 200 cells on
    /// a line through the origin, capped at 10%), times the share of the map the longest run held,
    /// times a 1.5 kicker when nothing was dropped at all.
    ///
    /// <para>IT IS A FACTOR AND NOT AN AMOUNT, which is the reverse of v21 and is why a ratio of two
    /// plays that differ only in their run is exactly this number: through v21 the bonus was ADDED,
    /// so it did not cancel in a ratio and every pin here had to subtract it back off.</para>
    /// </summary>
    private static double ComboFactor(int notes, int maxCombo, int misses)
    {
        double ceiling = Math.Min(10.0, Math.Max(0, notes) / 200.0 * 1.0) / 100.0; // pp:const combo_bonus_at_200_cells=1.0 combo_bonus_cap=10.0
        double ratio = (double)Math.Clamp(maxCombo, 0, notes) / notes;
        double kicker = misses == 0 && maxCombo >= notes ? 1.5 : 1; // pp:const combo_bonus_perfect=1.5

        return 1 + ceiling * ratio * kicker;
    }

    #endregion

    #region 1. The formula itself

    [Test]
    public void TheTwoFormulasAgreeExactlyOverASpreadOfPlays()
    {
        double[] stars = [0.5, 1.0, 2.75, 4.0, 6.3, 9.99];
        int[] noteCounts = [1, 5, 47, 100, 500, 2137];
        // 0.49 / 0.50 / 0.51 straddle the accuracy FLOOR the exponential curve sits above
        // (DEPARTURE 5): the price is exactly 0 at or below it and climbs from there, so a mirror
        // that has the floor and one that still runs the old power curve agree nowhere near it.
        // 0.78 / 0.80 / 0.82 are kept from the soft-knee generation, where a mirror carrying a LIVE
        // knee would differ by a factor of two.
        double[] accuracies = [0.0, 0.42, 0.49, 0.50, 0.51, 0.6931, 0.78, 0.80, 0.82, 0.9, 1.0];

        int compared = 0;

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double accuracy in accuracies)
        // The DIFFICULT CHARACTERS, which is the new axis of the formula: fewer than the cells (the
        // usual case), equal to them, more than them (which the clamp has to survive), and none at
        // all, which is the map that cannot absorb a miss.
        foreach (double difficult in new double[] { 0, 1, notes / 4.0, notes, notes * 3.0 })
        foreach (int misses in new[] { 0, 1, notes / 3, notes })
        foreach (int maxCombo in new[] { 0, notes / 2, notes })
        {
            double client = ClientPp.Compute(sr, notes, difficult, misses, accuracy, maxCombo, no_client_mods);
            double server = ServerPp.Compute(sr, notes, difficult, misses, accuracy, maxCombo, no_server_mods);

            Assert.That(client, Is.EqualTo(server),
                $"sr={sr} notes={notes} difficult={difficult} miss={misses} acc={accuracy} combo={maxCombo}");
            compared++;
        }

        Assert.That(compared, Is.GreaterThan(1000), "the spread must actually be a spread");
    }

    [Test]
    public void TheCleanlinessTermAgreesExactlyAcrossTheWholeMissFraction()
    {
        // THE ONE PENALTY TERM LEFT, and a fresh seam of its own: v22 moved it off the plain note
        // count and onto the map's DIFFICULT CHARACTERS, and put the power on the missed FRACTION
        // rather than on the raw count. A mirror one generation behind agrees with this one nowhere
        // except at zero misses, and a mirror that kept the count-based shape agrees only where the
        // two happen to cross.
        //
        // Nothing else in the product reads the miss count, so pp divided by the same play with none
        // is exactly max(0, 1 - (miss/difficult)^count_power)^miss_exponent. THE RATIO IS TAKEN AT NO
        // COMBO on both sides, which is what keeps that cancellation exact: the combo factor depends
        // on whether the play was spotless, so a lossy play and a clean one do not carry the same
        // one.
        //
        // A SEAM THE FRACTIONAL POWER OPENS: Math.Pow(x, 1.2) is not the exactly-rounded product
        // Math.Pow(x, 2) effectively is, so the two mirrors agreeing here is a claim about both
        // calling the same Math.Pow on the same double, which is what EXACT equality pins.
        const int notes = 1000;
        const double difficult = 400;

        double clientSpotless = ClientPp.Compute(4, notes, difficult, 0, 0.9, 0, no_client_mods);
        double serverSpotless = ServerPp.Compute(4, notes, difficult, 0, 0.9, 0, no_server_mods);

        Assert.That(clientSpotless, Is.EqualTo(serverSpotless), "the spotless baseline itself must agree");

        Assert.Multiple(() =>
        {
            foreach (int misses in new[] { 0, 1, 4, 20, 40, 100, 200, 399, 400, 401, 700, 1000 })
            {
                double client = ClientPp.Compute(4, notes, difficult, misses, 0.9, 0, no_client_mods);
                double server = ServerPp.Compute(4, notes, difficult, misses, 0.9, 0, no_server_mods);

                Assert.That(client, Is.EqualTo(server), $"miss={misses}");

                // And what they agree ON, spelled out, because the two agreeing on a wrong shape is
                // exactly what a cross-repo hand edit produces: the same mistake made twice.
                double expected = Math.Pow(Math.Max(0, 1 - Math.Pow(misses / difficult, 1.2)), 13.5134); // pp:const count_power=1.2 miss_exponent=13.5134

                Assert.That(client / clientSpotless, Is.EqualTo(misses == 0 ? 1 : expected).Within(1e-12), $"miss={misses}");
            }

            // THE CLAMP, which is load-bearing rather than defensive and is its own seam: misses are
            // cells and a map has FEWER difficult characters than cells, so the fraction really does
            // exceed 1, and a mirror missing the Math.Max would raise a negative base to a fractional
            // power and produce a non-real result rather than a zero.
            Assert.That(ClientPp.Compute(4, notes, difficult, 401, 0.9, 0, no_client_mods), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, difficult, 401, 0.9, 0, no_server_mods), Is.Zero);
            Assert.That(ClientPp.Compute(4, notes, difficult, 399, 0.9, 0, no_client_mods), Is.GreaterThan(0));
            Assert.That(ServerPp.Compute(4, notes, difficult, 399, 0.9, 0, no_server_mods), Is.GreaterThan(0));

            // A MAP WITH NO DIFFICULT CHARACTERS cannot absorb a miss on either side, and is exactly
            // 1.0 with none: the miss == 0 branch comes first, which is the 0/0 case both mirrors
            // have to take the same way.
            Assert.That(ClientPp.Compute(4, notes, 0, 1, 0.9, 0, no_client_mods), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, 0, 1, 0.9, 0, no_server_mods), Is.Zero);
            Assert.That(ClientPp.Compute(4, notes, 0, 0, 0.9, 0, no_client_mods),
                Is.EqualTo(ServerPp.Compute(4, notes, 0, 0, 0.9, 0, no_server_mods)));
            Assert.That(ClientPp.Compute(4, notes, 0, 0, 0.9, 0, no_client_mods), Is.GreaterThan(0));
        });
    }

    [Test]
    public void TheDeletedTypoTermIsDeletedOnBothSides()
    {
        // v22 removed the typo term outright. A HALF-LANDED removal is the failure this catches, and
        // it is the worst kind: pp is recomputed from a stored row's counts on every PpBackfill sweep
        // and every recalc, so a term surviving in the client only is a silent divergence between the
        // in-game counter and the stored value on every play that ever mistyped.
        //
        // The parameter stays on both signatures, so the assertion is that it does NOTHING rather
        // than that it is gone.
        const int notes = 500;
        const double difficult = 300;

        double clientClean = ClientPp.Compute(4, notes, difficult, 10, 0.9, 400, no_client_mods, 0);
        double serverClean = ServerPp.Compute(4, notes, difficult, 10, 0.9, 400, no_server_mods, 0);

        Assert.Multiple(() =>
        {
            Assert.That(clientClean, Is.EqualTo(serverClean));

            foreach (int typos in new[] { 1, 22, 23, 80, 248, 249, 5000, int.MaxValue, -1 })
            {
                Assert.That(ClientPp.Compute(4, notes, difficult, 10, 0.9, 400, no_client_mods, typos),
                    Is.EqualTo(clientClean), $"client, typos={typos}");
                Assert.That(ServerPp.Compute(4, notes, difficult, 10, 0.9, 400, no_server_mods, typos),
                    Is.EqualTo(serverClean), $"server, typos={typos}");
            }
        });
    }

    [Test]
    public void TheComboFactorMultipliesThePriceOnBothSides()
    {
        // THE PLACEMENT, which is the one thing this shape invites getting wrong, and it is the
        // opposite of v21's. Every other parity pin here is blind to it: a spotless full combo
        // prices identically whether the bonus multiplies or adds, and a play with no run at all has
        // no bonus to misplace. It takes a LOSSY play (so the product is not 1), at less than a full
        // combo (so the factor is not the whole ceiling), carrying a MOD STACK (so there is a
        // multiplier for the bonus to be inside or outside of), on a map long enough to earn a real
        // ceiling.
        //
        // Both mirrors are asserted against each other AND against the arithmetic spelled out,
        // because the two agreeing on a wrong placement is exactly the failure a cross-repo hand
        // edit produces.
        const double stars = 5.5;
        const int notes = 1600;
        const double difficult = 900;
        const int misses = 30;
        const int maxCombo = 1024;
        const double accuracy = 0.91;

        // FLASHLIGHT AND RECITE, not the Hard Rock stack this used to carry: HR's flat term is
        // NEUTRAL since v24 (its judgement arm prices it instead), so a stack leaning on it
        // multiplies to less than 1 and the "the multiplier really is above 1" premise below would
        // be false. These two are the length-scaled pair, so the product is comfortably above it and
        // also moves with the note count, which a flat stack would not.
        var clientStack = Stack(new TypeBeatModNoFail(), new TypeBeatModRecite(), new TypeBeatModFlashlight());
        var serverStack = ServerMods(clientStack);

        double client = ClientPp.Compute(stars, notes, difficult, misses, accuracy, maxCombo, clientStack);
        double server = ServerPp.Compute(stars, notes, difficult, misses, accuracy, maxCombo, serverStack);

        double modMultiplier = ClientPp.ModMultiplier(clientStack, notes);
        double noRun = ClientPp.Compute(stars, notes, difficult, misses, accuracy, 0, clientStack);

        Assert.Multiple(() =>
        {
            Assert.That(client, Is.EqualTo(server), "the two mirrors must agree on the placement");

            // The factor is a factor: the same play with no run at all, scaled.
            Assert.That(client, Is.EqualTo(noRun * ComboFactor(notes, maxCombo, misses)).Within(1e-9));

            // And the mod multiplier scales the WHOLE thing, bonus included, which is what makes it
            // a percentage rather than an amount. Under v21 the bonus sat outside the multiplier and
            // this identity was false by (modMult - 1) times the bonus.
            double bare = ClientPp.Compute(stars, notes, difficult, misses, accuracy, maxCombo, no_client_mods);

            Assert.That(client, Is.EqualTo(bare * modMultiplier).Within(1e-9));
            Assert.That(modMultiplier, Is.GreaterThan(1.05), "the stack really does multiply, so the identity is not trivial");
            Assert.That(ComboFactor(notes, maxCombo, misses), Is.GreaterThan(1.05), "and the run really does earn something");
        });
    }

    [Test]
    public void TheSpotlessKickerLandsOnBothSidesOrNeither()
    {
        // The kicker needs BOTH halves, every cell in one run AND nothing dropped, so it is a CLIFF
        // rather than a curve and a mirror that tested only one of the two conditions would agree
        // everywhere except on exactly this pair of plays.
        const int notes = 2000;
        const double difficult = 2000;

        foreach ((int misses, int maxCombo, double expected) in new (int, int, double)[]
                 {
                     (0, notes, 1.15),          // spotless full combo: the ceiling times the kicker
                     (0, notes - 1, 1.09995),   // one cell short of the run: no kicker
                     (1, notes, 1.10),          // the whole map in one run but a cell dropped: no kicker
                 })
        {
            double clientNoRun = ClientPp.Compute(4, notes, difficult, misses, 0.9, 0, no_client_mods);
            double client = ClientPp.Compute(4, notes, difficult, misses, 0.9, maxCombo, no_client_mods);
            double server = ServerPp.Compute(4, notes, difficult, misses, 0.9, maxCombo, no_server_mods);

            string context = $"miss={misses} combo={maxCombo}";

            Assert.That(client, Is.EqualTo(server), context);
            Assert.That(client / clientNoRun, Is.EqualTo(expected).Within(1e-9), context);
        }
    }

    [Test]
    public void TheTwoFormulasAgreeOnDegenerateInput()
    {
        // Neither side may ever produce NaN, Infinity or a negative number, and they must reach the
        // same zero from the same hostile inputs: zero notes, zero/NaN/infinite stars, an accuracy
        // outside [0, 1], negative counts, a combo above the note count, and a DIFFICULT-CHARACTER
        // count that is any of those things too (it arrives off a stored jsonb document, so it can
        // be anything a double can be).
        double[] stars = [0, -1, 0.0001, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        int[] noteCounts = [-3, 0, 1, 4, 100];
        double[] accuracies = [-1, 0, 2, double.NaN, double.PositiveInfinity];
        double[] difficulties = [0, -3, 0.5, 100, 1e9, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double accuracy in accuracies)
        foreach (double difficult in difficulties)
        foreach (int misses in new[] { -5, 0, notes + 7 })
        foreach (int maxCombo in new[] { -3, notes, notes + 9 })
        {
            double client = ClientPp.Compute(sr, notes, difficult, misses, accuracy, maxCombo, no_client_mods);
            double server = ServerPp.Compute(sr, notes, difficult, misses, accuracy, maxCombo, no_server_mods);

            string context = $"sr={sr} notes={notes} difficult={difficult} miss={misses} acc={accuracy} combo={maxCombo}";

            Assert.That(client, Is.EqualTo(server), context);
            Assert.That(double.IsFinite(client), Is.True, context);
            Assert.That(client, Is.GreaterThanOrEqualTo(0), context);
        }
    }

    [Test]
    public void TheTwoFlashlightCurvesAgreeAndSoDoesTheReciteScaleOnTopOfThem()
    {
        // Flashlight is the only note-count-driven curve in pp, and since DEPARTURE 6 (v23) RECITE
        // is a SCALE on it rather than a flat term, so the two have to be pinned together: a mirror
        // that kept the flat 1.07 would agree with this one at exactly one note count and nowhere
        // else.
        foreach (int notes in new[] { -1, 0, 1, 3, 4, 5, 6, 45, 46, 47, 100, 500, 1000, 12345 })
        {
            Assert.That(ClientPp.FlashlightMultiplier(notes), Is.EqualTo(ServerPp.FlashlightMultiplier(notes)), $"flashlight at {notes}");
            Assert.That(ClientPp.ReciteMultiplierFor(notes), Is.EqualTo(ServerPp.ReciteMultiplierFor(notes)), $"recite at {notes}");
        }

        Assert.Multiple(() =>
        {
            // The definition itself, on both sides: under Flashlight's floor there is no bonus to
            // scale and Recite is worth exactly nothing either.
            Assert.That(ClientPp.ReciteMultiplierFor(46), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(ServerPp.ReciteMultiplierFor(46), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(ServerPp.ReciteMultiplierFor(500),
                Is.EqualTo(1 + 2.0 * (ServerPp.FlashlightMultiplier(500) - 1)).Within(1e-12)); // pp:const recite_multiplier=2.0
        });
    }

    [Test]
    public void TheTwoFormulasAreTheSameGeneration()
    {
        // A client shipped against generation N must not quietly price plays the server stores at
        // generation N+1. Bumping one without the other is exactly what this catches.
        Assert.That(ClientPp.VERSION, Is.EqualTo(ServerPp.VERSION));

        // And the generation itself, so a half-landed cross-repo change that bumped BOTH mirrors
        // but left docs/pp.md and the two per-repo pins behind is caught here too.
        Assert.That(ServerPp.VERSION, Is.EqualTo(24)); // pp:version
    }

    #endregion

    #region 2. Statistics: HitResult members versus snake_case jsonb keys

    [Test]
    public void CountNotesAgreesThroughTheRealStatisticsWire()
    {
        var statistics = new Dictionary<HitResult, int>
        {
            [HitResult.Great] = 300,
            [HitResult.Ok] = 40,
            [HitResult.Meh] = 10,
            [HitResult.Miss] = 50,
            // The line containers, and a result no typing map emits. Counting any of them would
            // inflate `notes` and dilute every factor.
            [HitResult.IgnoreHit] = 12,
            [HitResult.IgnoreMiss] = 3,
            [HitResult.LargeBonus] = 7,
            // The TYPO stat: carried and displayed, never a note, and since v22 never priced.
            [HitResult.ComboBreak] = 137,
        };

        var client = ClientPp.CountNotes(statistics);
        var server = ServerCounts(statistics);

        Assert.Multiple(() =>
        {
            Assert.That(client.Notes, Is.EqualTo(server.Notes));
            Assert.That(client.Misses, Is.EqualTo(server.Misses));
            Assert.That(client.Typos, Is.EqualTo(server.Typos));

            Assert.That(client.Notes, Is.EqualTo(400));
            Assert.That(client.Misses, Is.EqualTo(50));
            Assert.That(client.Typos, Is.EqualTo(137));

            // NEITHER SIDE DERIVES THE DIFFICULT CHARACTERS FROM A PLAY, which is the whole point of
            // the figure being a property of the MAP: the counts carry a 0 until a caller that can
            // see the map fills it in (the client from the lines, the server from the stored matrix).
            Assert.That(client.DifficultCharacters, Is.Zero);
            Assert.That(server.DifficultCharacters, Is.Zero);
        });
    }

    [Test]
    public void EveryNoteResultTravelsUnderTheKeyTheServerCounts()
    {
        // One result at a time, so a single renamed key cannot hide inside a total that happens to
        // still add up.
        foreach (var result in new[] { HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss })
        {
            var statistics = new Dictionary<HitResult, int> { [result] = 9 };

            Assert.That(ServerCounts(statistics).Notes, Is.EqualTo(9), $"{result} must reach the server as a note");
            Assert.That(ClientPp.CountNotes(statistics).Notes, Is.EqualTo(9), $"{result} must count as a note client-side");
        }

        // And the typo key, which is a note on NEITHER side.
        var mistyped = new Dictionary<HitResult, int> { [ClientPp.MISTYPE_RESULT] = 9 };

        Assert.Multiple(() =>
        {
            Assert.That(ServerCounts(mistyped).Notes, Is.Zero);
            Assert.That(ServerCounts(mistyped).Typos, Is.EqualTo(9));
            Assert.That(ClientPp.CountNotes(mistyped).Notes, Is.Zero);
            Assert.That(ClientPp.CountNotes(mistyped).Typos, Is.EqualTo(9));

            // The literal the server greps for, spelled by the client's own enum.
            Assert.That(JsonConvert.SerializeObject(mistyped), Does.Contain("combo_break"));
        });
    }

    [Test]
    public void AScoreCarryingNoTypoKeyReadsAsZeroOnBothSides()
    {
        // Every score submitted before the stat existed omits the key entirely, and must price
        // exactly as it always did.
        var old = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.Miss] = 10 };

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.CountNotes(old).Typos, Is.Zero);
            Assert.That(ServerCounts(old).Typos, Is.Zero);
            Assert.That(ClientPp.Compute(4, 100, 60, 10, 0.9, 90, no_client_mods, ClientPp.CountNotes(old).Typos),
                Is.EqualTo(ServerPp.Compute(4, 100, 60, 10, 0.9, 90, no_server_mods)));
        });
    }

    #endregion

    #region 3. Mods: real Mod objects versus parsed acronyms

    [Test]
    public void EveryRulesetModPricesIdenticallyOnBothSides()
    {
        foreach (var mod in AllRulesetMods())
        {
            var clientStack = Stack(mod);
            var serverStack = ServerMods(clientStack);

            Assert.That(serverStack, Has.Count.EqualTo(1), $"{mod.Acronym} must survive the wire");
            Assert.That(serverStack[0].Acronym, Is.EqualTo(mod.Acronym.ToUpperInvariant()));

            foreach (int notes in new[] { 1, 46, 100, 500, 3000 })
            {
                Assert.That(ClientPp.ModMultiplier(clientStack, notes), Is.EqualTo(ServerPp.ModMultiplier(serverStack, notes)),
                    $"{mod.Acronym} at {notes} notes");
            }
        }
    }

    /// <summary>
    /// THE FOUR MODS THE FORK RETUNED, each pinned at its own value as well as across the repos.
    /// A cross-repo hand edit makes the same mistake twice, so "the two agree" is necessary and not
    /// sufficient: the numbers themselves are stated, and the two that are NOT flat any more (Easy
    /// is priced partly through its judgement arm, Recite entirely through Flashlight's curve) say
    /// so where they are asserted.
    /// </summary>
    [Test]
    public void TheRetunedModMultipliersAgreeAndAreTheValuesTheSandboxHolds()
    {
        Assert.Multiple(() =>
        {
            foreach ((Mod mod, double expected, string why) in new (Mod, double, string)[]
                     {
                         // The values are carried in the table rather than each on its own
                         // assertion line, so the retune tool cannot rewrite them from a
                         // marker: they are pinned by hand and by the sweep above, which is
                         // what a CROSS-REPO pin wants anyway. A tool that recomputed both
                         // sides from its own constant would agree with itself.
                         (new TypeBeatModEasy(), 0.85, "Easy, the rest of what its doubled windows are worth after the arm"),
                         (new TypeBeatModHardRock(), 1.00, "Hard Rock, NEUTRAL: the arm prices it, and paying twice is the mistake"),
                         (new TypeBeatModFletcher(), 1.02, "Fletcher (FC), the pinned caret"),
                         (new TypeBeatModNoFail(), 0.90, "No Fail"),
                     })
            {
                var clientStack = Stack(mod);
                var serverStack = ServerMods(clientStack);

                Assert.That(ClientPp.ModMultiplier(clientStack, 500), Is.EqualTo(expected).Within(1e-12), why);
                Assert.That(ServerPp.ModMultiplier(serverStack, 500), Is.EqualTo(expected).Within(1e-12), why);
            }

            // Recite is length-scaled now, so it has no single value: it is Flashlight's bonus times
            // its own scale, pinned in the Flashlight test above and held across the repos here.
            var recite = Stack(new TypeBeatModRecite());

            Assert.That(ClientPp.ModMultiplier(recite, 500), Is.EqualTo(ServerPp.ModMultiplier(ServerMods(recite), 500)));
            Assert.That(ServerPp.ModMultiplier(ServerMods(recite), 500),
                Is.EqualTo(ServerPp.ReciteMultiplierFor(500)).Within(1e-12));
            Assert.That(ServerPp.ModMultiplier(recite is var _ ? ServerMods(recite) : null, 100),
                Is.Not.EqualTo(ServerPp.ModMultiplier(ServerMods(recite), 500)).Within(1e-9),
                "and it genuinely moves with the note count, which a flat term could not");
        });
    }

    /// <summary>
    /// A mod carrying the RETIRED Rhythmic acronym. Backlog 147 deleted <c>TypeBeatModRhythmic</c>
    /// from the ruleset, so it cannot appear in <see cref="AllRulesetMods"/> and the pair sweeps
    /// above can never reach RH; this stands in for the only thing that still can, a score row
    /// submitted while the mod was live.
    /// </summary>
    private sealed class RetiredRhythmicMod : Mod
    {
        public override string Name => "Rhythmic";
        public override string Acronym => "RH";
        public override osu.Framework.Localisation.LocalisableString Description => "A stored acronym no client can select any more.";
    }

    [Test]
    public void AStoredRhythmicIsUnpricedOnBothSides()
    {
        var clientStack = Stack(new RetiredRhythmicMod());
        var serverStack = ServerMods(clientStack);

        Assert.That(serverStack, Has.Count.EqualTo(1), "RH must survive the wire");
        Assert.That(serverStack[0].Acronym, Is.EqualTo("RH"));

        foreach (int notes in new[] { 1, 46, 100, 500, 3000 })
        {
            Assert.That(ClientPp.ModMultiplier(clientStack, notes), Is.EqualTo(ServerPp.ModMultiplier(serverStack, notes)),
                $"RH at {notes} notes");
            Assert.That(ServerPp.ModMultiplier(serverStack, notes), Is.EqualTo(1.0).Within(1e-12),
                $"and it is unpriced on both sides, at {notes} notes");
        }

        // Stacked, because the multiplier is a product and a stray arm hides inside a single-mod
        // test whenever the neutral answer happens to be right. FC is 1.02, so the pair is worth
        // exactly FC and an RH arm left in either mirror would show here.
        var pair = Stack(new RetiredRhythmicMod(), new TypeBeatModFletcher());
        Assert.That(ClientPp.ModMultiplier(pair, 500), Is.EqualTo(ServerPp.ModMultiplier(ServerMods(pair), 500)));
        Assert.That(ClientPp.ModMultiplier(pair, 500), Is.EqualTo(ClientPp.ModMultiplier(Stack(new TypeBeatModFletcher()), 500)));
    }

    [Test]
    public void EveryPairOfRulesetModsPricesIdenticallyOnBothSides()
    {
        // Stacks, not just singles: the multiplier is a product, and a mod that was learned by one
        // side only would hide inside a single-mod test that happens to return 1.0 on both. Two note
        // counts, because Recite and Flashlight are length-scaled and a pair carrying both is a
        // different number at each.
        var all = AllRulesetMods();

        for (int i = 0; i < all.Count; i++)
        for (int j = i + 1; j < all.Count; j++)
        foreach (int notes in new[] { 100, 500, 3000 })
        {
            var clientStack = Stack(all[i], all[j]);
            var serverStack = ServerMods(clientStack);

            Assert.That(ClientPp.ModMultiplier(clientStack, notes), Is.EqualTo(ServerPp.ModMultiplier(serverStack, notes)),
                $"{all[i].Acronym} + {all[j].Acronym} at {notes} notes");
        }
    }

    [Test]
    public void ARateModContributesNothingToTheMultiplierOnEitherSide()
    {
        // The rate is priced through the star rating alone; a flat term on either side would
        // double-count it.
        foreach (double rate in new[] { 1.01, 1.50, 2.00 })
        {
            var clientStack = Stack(At(new TypeBeatModDoubleTime(), rate));

            Assert.That(ClientPp.ModMultiplier(clientStack, 500), Is.EqualTo(1.0));
            Assert.That(ServerPp.ModMultiplier(ServerMods(clientStack), 500), Is.EqualTo(1.0));
        }
    }

    [Test]
    public void TheJudgementArmIsReadTheSameWayFromTypesAndFromAcronyms()
    {
        // THE ONE SANCTIONED DIFFERENCE between the two JudgementArmFor implementations: the client
        // keys on the mod TYPES, which exist only in the client, and the server on the ACRONYMS that
        // travel on the wire. They have to reach the same verdict for every stack, or a play is
        // RATED in one arm and priced in another.
        IReadOnlyList<Mod>[] stacks =
        [
            no_client_mods,
            Stack(new TypeBeatModNoFail()),
            Stack(new TypeBeatModEasy()),
            Stack(new TypeBeatModHardRock()),
            Stack(new TypeBeatModEasy(), new TypeBeatModLiterate()),
            Stack(new TypeBeatModHardRock(), new TypeBeatModDoubleTime(), new TypeBeatModLiterate()),
            Stack(new TypeBeatModFlashlight(), new TypeBeatModRecite()),
            // Tamper-shaped: the client makes the two mutually exclusive, so a row carrying both can
            // only come from a hand-written mods blob. Both sides take whichever comes first, which
            // is the same loop written twice.
            Stack(new TypeBeatModEasy(), new TypeBeatModHardRock()),
            Stack(new TypeBeatModHardRock(), new TypeBeatModEasy()),
        ];

        foreach (var clientStack in stacks)
        {
            ClientArm client = ClientPp.JudgementArmFor(clientStack);
            ServerArm server = ServerPp.JudgementArmFor(ServerMods(clientStack));

            string context = string.Join('+', clientStack.Select(m => m.Acronym));

            Assert.That(server.ToString(), Is.EqualTo(client.ToString()), context);
        }
    }

    #endregion

    #region 4. Rate eligibility: same verdict, same reading

    [Test]
    public void RateEligibilityAgreesForEveryReachableRate()
    {
        var (client, server) = TwinMaps();
        var matrix = ServerMatrix(server);

        var cases = new List<IReadOnlyList<Mod>>
        {
            no_client_mods,
            Stack(new TypeBeatModNoFail(), new TypeBeatModFlashlight()),
            Stack(new TypeBeatModDoubleTime()),
            Stack(new TypeBeatModNightcore()),
            Stack(new TypeBeatModHalfTime()),
            Stack(new TypeBeatModDoubleTime(), new TypeBeatModHalfTime()), // tamper-shaped
        };

        // Every reachable slider position of both rate mods, at the sliders' own 0.01 step.
        for (int step = 101; step <= 200; step++)
            cases.Add(Stack(At(new TypeBeatModDoubleTime(), step / 100.0)));

        for (int step = 50; step <= 99; step++)
            cases.Add(Stack(At(new TypeBeatModHalfTime(), step / 100.0)));

        // Literate and the two judgement arms are ORTHOGONAL to the rate, so every case above is
        // also a Literate case, an Easy case and a Hard Rock case, and each has to reach the same
        // verdict about the RATE while reading a different cell.
        foreach (var stack in cases.ToList())
        {
            cases.Add(Stack([.. stack, new TypeBeatModLiterate()]));
            cases.Add(Stack([.. stack, new TypeBeatModEasy()]));
            cases.Add(Stack([.. stack, new TypeBeatModHardRock()]));
        }

        foreach (var clientStack in cases)
        {
            double? clientRate = ClientPp.EligibleRate(clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), matrix);

            string context = string.Join('+', clientStack.Select(m => m.Acronym + (m is ModRateAdjust r ? $"@{r.SpeedChange.Value:0.00}" : "")));

            // The client says WHICH RATE to price at; the server says WHICH STORED CELL prices it.
            // Those must be the same decision, expressed two ways. EligibleRate deliberately answers
            // the RATE question alone: neither Literate nor a judgement arm touches rate eligibility.
            double? expected = clientRate is double rate
                ? ClientDifficulty.Compute(client, rate, ClientPp.IsLiterate(clientStack), ClientDifficulty.Live, ClientPp.JudgementArmFor(clientStack))
                : null;

            Assert.That(serverStars.Stars, Is.EqualTo(expected), context);
            Assert.That(serverStars.Pending, Is.False, context);
        }
    }

    [Test]
    public void OnlyTheSliderDefaultsAreEligibleOnEitherSide()
    {
        Assert.Multiple(() =>
        {
            // Server side first: its values are the mods' slider ranges read at runtime, the
            // client's are compile-time constants, and NUnit wants the constant to be the expected.
            Assert.That(Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, Is.EqualTo(ClientPp.DOUBLE_TIME_BASE_RATE));
            Assert.That(Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate, Is.EqualTo(ClientPp.HALF_TIME_BASE_RATE));

            // And those are the mods' own slider defaults, not a third copy of the numbers.
            Assert.That(new TypeBeatModDoubleTime().SpeedChange.Default, Is.EqualTo(ClientPp.DOUBLE_TIME_BASE_RATE));
            Assert.That(new TypeBeatModNightcore().SpeedChange.Default, Is.EqualTo(ClientPp.DOUBLE_TIME_BASE_RATE));
            Assert.That(new TypeBeatModHalfTime().SpeedChange.Default, Is.EqualTo(ClientPp.HALF_TIME_BASE_RATE));

            // And the matrix is keyed on those same three rates, not on a fourth copy.
            Assert.That(ServerRates.Rates, Is.EqualTo(new[]
            {
                1.0, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate,
            }).AsCollection);
        });
    }

    #endregion

    #region 5. The rating matrix: the client's local rating IS the server's stored one

    /// <summary>
    /// The same lyric map, built once per repo's own <c>LyricLine</c> type. Deliberately a shape
    /// with things that exercise the difficulty model: repeated words, a dense line, a long rest.
    ///
    /// <para>IT IS PUNCTUATED AND CAPITALISED ON PURPOSE (backlog 144). The text is the AUTHOR'S
    /// form, which is what a map stores, and the Literate mod types it verbatim where every other
    /// stack types the stripped lower-case stream. An unpunctuated fixture would make the two
    /// streams the same string, so every Literate assertion in this file would pass even if one of
    /// the two ports had forgotten to apply the mod at all. Marks are drawn from
    /// <c>Typeability.PUNCTUATION</c>, since anything outside that set is stripped by Normalize
    /// before a map is ever written.</para>
    ///
    /// <para>IT IS ALSO REAL ENGLISH, which matters since the rework: the TYPABILITY index scores a
    /// line's own text against four dictionaries and leaves a line whose characters mostly sit
    /// outside them UNSCORED. A fixture of nonsense words would clear neither gate, so the whole
    /// typability arm of both models would read as off and this file would pin the wrong thing.</para>
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) TwinMaps()
    {
        (string Text, double Start, double End, (string Text, double Start, double End)[] Units)[] source =
        [
            ("Hello there, world!", 1000, 4000,
                [("Hello", 1000, 2000), ("there,", 2000, 3000), ("world!", 3000, 4000)]),
            ("Typing is a rhythm, not a race.", 4000, 8000,
                [("Typing", 4000, 4800), ("is", 4800, 5100), ("a", 5100, 5300), ("rhythm,", 5300, 6400), ("not", 6400, 6900), ("a", 6900, 7100), ("race.", 7100, 8000)]),
            ("world world world", 8000, 9000,
                [("world", 8000, 8300), ("world", 8300, 8600), ("world", 8600, 9000)]),
            ("After a long-drawn instrumental rest...", 30000, 35000,
                [("After", 30000, 31000), ("a", 31000, 31300), ("long-drawn", 31300, 32200), ("instrumental", 32200, 34000), ("rest...", 34000, 35000)]),
        ];

        return Twin(source);
    }

    /// <summary>
    /// The server's RATING MATRIX for a twin, i.e. exactly what the ingest writes to
    /// <c>beatmaps.ratings</c> (034_ratings_matrix.sql), round-tripped through the stored document
    /// so every pin below reads the cell a real row would hand it rather than an in-memory one.
    /// </summary>
    private static ServerRates ServerMatrix(IReadOnlyList<ServerLine> server)
        => ServerRates.Parse(ServerRates.Compute(server).ToJson())!;

    /// <summary>
    /// The eighteen combinations the matrix stores, as the pair of enums each side spells them with.
    /// One list, walked by every parity pin here, so a combination cannot be covered on one axis and
    /// missed on another.
    /// </summary>
    private static IEnumerable<(ClientArm Client, ServerArm Server, bool Literate, double Rate, string Name)> Combinations()
    {
        (ClientArm Client, ServerArm Server, string Name)[] arms =
        [
            (ClientArm.None, ServerArm.None, "none"),
            (ClientArm.Easy, ServerArm.Easy, "ez"),
            (ClientArm.HardRock, ServerArm.HardRock, "hr"),
        ];

        foreach ((ClientArm clientArm, ServerArm serverArm, string name) in arms)
        foreach (bool literate in new[] { false, true })
        foreach (double rate in new[] { 1.0, ClientPp.DOUBLE_TIME_BASE_RATE, ClientPp.HALF_TIME_BASE_RATE })
            yield return (clientArm, serverArm, literate, rate, $"{name}/{(literate ? "literate" : "plain")}/{rate:0.00}");
    }

    /// <summary>
    /// A DENSE twin, rating far above what any ordinary map reaches, so the two ports are held
    /// together in the region where a truncation or a clamp landed on one side only would show.
    /// <see cref="TwinMaps"/> rates a few stars at every rate, so it cannot tell the ports apart
    /// anywhere such a thing would act. Eight five-letter words to a 1.2 second line is well past
    /// what any human sustains, which is exactly why it reaches the region: the model prices pace
    /// against human capability and this fixture is roughly one and a half times it. It is also
    /// SUSTAINED, forty lines of it, so the chunked axis sees the same demand in every chunk.
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) DenseTwinMaps()
    {
        string[] pool = ["flame", "river", "cider", "amber", "otter", "nudge", "vivid", "query", "zebra", "month", "proxy", "blitz"];
        const int line_count = 40;
        const int words_per_line = 8;
        const double line_ms = 1200;
        const double word_ms = line_ms / words_per_line;

        var source = new List<(string Text, double Start, double End, (string Text, double Start, double End)[] Units)>();
        int wordIndex = 0;

        for (int l = 0; l < line_count; l++)
        {
            double lineStart = l * line_ms;
            var units = new (string Text, double Start, double End)[words_per_line];

            for (int w = 0; w < words_per_line; w++)
            {
                double wordStart = lineStart + w * word_ms;
                units[w] = (pool[wordIndex++ % pool.Length], wordStart, wordStart + word_ms);
            }

            source.Add((string.Join(" ", units.Select(u => u.Text)), lineStart, lineStart + line_ms, units));
        }

        return Twin([.. source]);
    }

    /// <summary>
    /// A twin carrying FREESTYLE SLOTS, the cells backlog 211 prices at a quarter each. Neither
    /// fixture above has a single marker in it, so neither could tell the two ports apart on the
    /// quarter, and since the rework a marker is ALSO a stretch cell in the rhythm arm's press
    /// intervals, which is a second thing only a marker-bearing fixture can see. A divergence here
    /// is invisible everywhere except on a freestyle map's stored rating against the one song select
    /// draws for the same map.
    ///
    /// <para>Three shapes, because the weight enters three different ways: a word with slots at its
    /// END, a word with one in the MIDDLE (which must price the same as the first, the markers being
    /// a count and not characters of the stream text), and a token of NOTHING BUT slots, which used
    /// to be dropped from the map outright and is now a word of weight 1.</para>
    ///
    /// <para><paramref name="markers"/> false deletes every marker, which is the same map without
    /// them: the comparison that proves the slots cost something at all.</para>
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) FreestyleTwinMaps(bool markers = true)
    {
        string[] pool = ["flame", "river", "cider", "amber", "otter", "nudge"];
        const int line_count = 12;
        const int words_per_line = 6;
        const double line_ms = 1800;
        const double word_ms = line_ms / words_per_line;

        string slots(int n) => markers ? new string(ServerTypeability.FREESTYLE_MARKER, n) : string.Empty;

        var source = new List<(string Text, double Start, double End, (string Text, double Start, double End)[] Units)>();
        int wordIndex = 0;

        for (int l = 0; l < line_count; l++)
        {
            double lineStart = l * line_ms;
            var units = new (string Text, double Start, double End)[words_per_line];

            for (int w = 0; w < words_per_line; w++)
            {
                double wordStart = lineStart + w * word_ms;
                string word = pool[wordIndex % pool.Length];

                string text = (wordIndex % 3) switch
                {
                    0 => word + slots(2),
                    1 => word.Insert(2, slots(1)),
                    _ => slots(4),
                };

                wordIndex++;
                units[w] = (text, wordStart, wordStart + word_ms);
            }

            source.Add((string.Join(" ", units.Select(u => u.Text)), lineStart, lineStart + line_ms, units));
        }

        return Twin([.. source]);
    }

    /// <summary>
    /// A twin whose words carry AUTHORED SYLLABLE BOUNDARIES, which nothing else here does.
    ///
    /// <para>THE SEAM THIS OPENS IS NEW AND IS THE SHARPEST ONE IN THIS FILE. Until the rework
    /// nothing in either rating read a word's subdivisions, so the server's own lyric parser dropped
    /// them on the floor and no test could tell. The rhythm arm reads them now
    /// (<c>LyricDifficulty.PressIntervals</c> judges a cell inside its own syllable's sung span, and
    /// syllabifies a word the mapper did not subdivide), so a subdivided word offers a DIFFERENT set
    /// of judgement intervals from the syllabified fallback and a mirror that ignored the boundaries
    /// would rate every subdivided map differently from the client.</para>
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) SubdividedTwinMaps()
    {
        // Words the engine's own syllabifier would cut somewhere ELSE, so the authored boundaries
        // are doing real work rather than agreeing with the fallback by luck.
        (string Text, double[] Boundaries)[] words =
        [
            ("instrumental", [30300, 30700, 31100]),
            ("rhythm", [31600]),
            ("together", [32400, 32900]),
            ("wonderful", [33600, 34100]),
            ("remember", [34800, 35300]),
            ("beautiful", [36000, 36500]),
        ];

        var client = new List<ClientLine>();
        var server = new List<ServerLine>();
        double t = 30000;
        const double word_ms = 900;

        for (int line = 0; line < 8; line++)
        {
            double lineStart = t;
            var clientUnits = new List<ClientUnit>();
            var serverUnits = new List<ServerUnit>();
            var texts = new List<string>();

            foreach ((string text, double[] boundaries) in words)
            {
                // The boundaries are authored against the FIRST line's clock, so every later line
                // shifts them by the same offset its own words shift by.
                double offset = t - (30000 + Array.IndexOf(words, (text, boundaries)) * word_ms);
                double[] shifted = boundaries.Select(b => b + offset).Where(b => b > t && b < t + word_ms).ToArray();

                clientUnits.Add(new ClientUnit { Text = text, StartTime = t, EndTime = t + word_ms, SyllableBoundaries = shifted });
                serverUnits.Add(new ServerUnit { Text = text, StartTime = t, EndTime = t + word_ms, SyllableBoundaries = shifted });
                texts.Add(text);
                t += word_ms;
            }

            string raw = string.Join(' ', texts);

            client.Add(new ClientLine { RawText = raw, StartTime = lineStart, EndTime = t, SingEndTime = t, Units = clientUnits });
            server.Add(new ServerLine { RawText = raw, StartTime = lineStart, EndTime = t, SingEndTime = t, Units = serverUnits });
        }

        return (client, server);
    }

    /// <summary>Which of a paused word's rests <see cref="PausedTwinMaps"/> keeps.</summary>
    internal enum PauseShape
    {
        /// <summary>Every authored rest, valid and invalid alike, exactly as a map would carry them.</summary>
        All,

        /// <summary>Only the rests the derivation must IGNORE, which has to rate as the bare map.</summary>
        InvalidOnly,

        /// <summary>No rest at all: the same words, times and syllables as a map written before the feature.</summary>
        None,
    }

    /// <summary>
    /// A twin whose words carry AUTHORED PAUSES (the editor's Insert Pause, PR 2), which nothing else
    /// here does.
    ///
    /// <para>A rest is a DIVIDER for the rating: the word is judged in the stretches it is sung in
    /// (<c>LyricDifficulty.BuildWords</c> reads them through <c>PausedWord.Of</c>), so a mirror that
    /// dropped them would rate every paused map as one long word with the rests folded in as free
    /// time. Each word below reaches a different branch of <c>PausedWord.UsableRests</c>, the rule
    /// both loaders and both ratings share:</para>
    /// <list type="bullet">
    /// <item><description>one valid rest (two stretches);</description></item>
    /// <item><description>two valid rests (three stretches);</description></item>
    /// <item><description>a valid rest OVER authored syllable boundaries, where the rest's stretches
    /// replace the syllable groups in the rating;</description></item>
    /// <item><description>rests that must be IGNORED: a split with no typeable cell before it, one
    /// whose end leaves the word, and an inverted one;</description></item>
    /// <item><description>two rests whose TEXT order contradicts their TIME order, of which only the
    /// first survives;</description></item>
    /// <item><description>a plain word, the control.</description></item>
    /// </list>
    ///
    /// <para>The fixture feeds the rests UNVALIDATED (a hand-built unit, not a parsed one), so the
    /// validation inside <c>PausedWord.Of</c> is what is under test here; the parser's copy of the
    /// same rule is pinned in <see cref="LyricParserParityTest"/>.</para>
    /// </summary>
    internal static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) PausedTwinMaps(PauseShape shape = PauseShape.All)
    {
        const double word_ms = 900;

        // Offsets are from each word's own start, so every line lays out identically. CutsAlone says
        // whether a rest would cut its word ON ITS OWN: the last one on "beautiful" would, and is
        // dropped only because the rest before it already holds a later character.
        (string Text, double[] Boundaries, (double Start, double End, int Split, bool CutsAlone)[] Rests)[] words =
        [
            ("instrumental", [], [(350, 500, 5, true)]),
            ("together", [], [(250, 350, 2, true), (550, 650, 5, true)]),
            ("wonderful", [300, 600], [(400, 500, 5, true)]),
            ("remember", [450], [(200, 300, 0, false), (700, 950, 4, false), (600, 500, 3, false)]),
            ("beautiful", [], [(200, 300, 6, true), (500, 600, 3, true)]),
            ("rhythm", [], []),
        ];

        var client = new List<ClientLine>();
        var server = new List<ServerLine>();
        double t = 2000;

        for (int line = 0; line < 10; line++)
        {
            double lineStart = t;
            var clientUnits = new List<ClientUnit>();
            var serverUnits = new List<ServerUnit>();
            var texts = new List<string>();

            foreach ((string text, double[] boundaries, var rests) in words)
            {
                var kept = rests.Where(r => shape switch
                {
                    PauseShape.All => true,
                    PauseShape.InvalidOnly => !r.CutsAlone,
                    _ => false,
                }).ToArray();

                double[] shifted = boundaries.Select(b => b + t).ToArray();
                double start = t;

                clientUnits.Add(new ClientUnit
                {
                    Text = text, StartTime = t, EndTime = t + word_ms, SyllableBoundaries = shifted,
                    Pauses = kept.Select(r => new ClientWordPause(start + r.Start, start + r.End, r.Split)).ToArray(),
                });
                serverUnits.Add(new ServerUnit
                {
                    Text = text, StartTime = t, EndTime = t + word_ms, SyllableBoundaries = shifted,
                    Pauses = kept.Select(r => new ServerWordPause(start + r.Start, start + r.End, r.Split)).ToArray(),
                });

                texts.Add(text);
                t += word_ms;
            }

            string raw = string.Join(' ', texts);

            client.Add(new ClientLine { RawText = raw, StartTime = lineStart, EndTime = t, SingEndTime = t, Units = clientUnits });
            server.Add(new ServerLine { RawText = raw, StartTime = lineStart, EndTime = t, SingEndTime = t, Units = serverUnits });
        }

        return (client, server);
    }

    /// <summary>The one lyric shape, projected into each repo's own <c>LyricLine</c> type.</summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) Twin(
        (string Text, double Start, double End, (string Text, double Start, double End)[] Units)[] source)
    {
        var client = source.Select(l => new ClientLine
        {
            RawText = l.Text,
            StartTime = l.Start,
            EndTime = l.End,
            SingEndTime = l.End,
            Units = l.Units.Select(u => new ClientUnit { Text = u.Text, StartTime = u.Start, EndTime = u.End }).ToArray(),
        }).ToArray();

        var server = source.Select(l => new ServerLine
        {
            RawText = l.Text,
            StartTime = l.Start,
            EndTime = l.End,
            SingEndTime = l.End,
            Units = l.Units.Select(u => new ServerUnit { Text = u.Text, StartTime = u.Start, EndTime = u.End }).ToArray(),
        }).ToArray();

        return (client, server);
    }

    [TestCase("plain", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(the awkward fixture)")]
    [TestCase("dense", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(dense, far above an ordinary map)")]
    [TestCase("freestyle", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(freestyle slots)")]
    [TestCase("subdivided", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(authored syllable boundaries)")]
    [TestCase("paused", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(authored pauses, valid and ignored)")]
    [TestCase("paused-ignored", TestName = "TheEighteenStoredRatingsAreTheClientsOwn(authored pauses, every one ignored)")]
    public void TheEighteenStoredRatingsAreTheClientsOwn(string fixtureName)
    {
        // THE LOAD-BEARING CLAIM behind pricing a play client-side at all: the game does NOT fetch
        // the map's ratings, it recomputes them. Since the rework that is eighteen numbers rather
        // than six, because the JUDGEMENT ARM is a rating input, and each carries a
        // DIFFICULT-CHARACTER count as well, which is the other half of a price since VERSION 22.
        // Both halves of all eighteen, on four fixtures that reach different parts of the model.
        var (client, server) = Fixture(fixtureName);
        var matrix = ServerMatrix(server);

        Assert.Multiple(() =>
        {
            Assert.That(matrix.Count, Is.EqualTo(18), "the ingest has to write all eighteen or the pin is partial");

            foreach ((ClientArm clientArm, ServerArm serverArm, bool literate, double rate, string name) in Combinations())
            {
                var clientDetail = ClientDifficulty.ComputeDetail(client, rate, literate, ClientDifficulty.Live, clientArm);
                var cell = matrix.TryGet(serverArm, literate, rate);

                Assert.That(cell, Is.Not.Null, name);
                Assert.That(cell!.Value.Stars, Is.EqualTo(clientDetail.Stars), $"{name}: stars");
                Assert.That(cell.Value.DifficultCharacters, Is.EqualTo(clientDetail.DifficultCharacters), $"{name}: difficult characters");
            }

            // THE PREMISE, without which every equality above could hold on a model that returned a
            // constant. The fixture has to rate as something, the three arms have to be three
            // DIFFERENT numbers (or the arm axis is not being read at all), and the same for the
            // rates.
            double none = matrix.TryGet(ServerArm.None, false, 1.0)!.Value.Stars;
            double easy = matrix.TryGet(ServerArm.Easy, false, 1.0)!.Value.Stars;
            double hardRock = matrix.TryGet(ServerArm.HardRock, false, 1.0)!.Value.Stars;

            Assert.That(none, Is.GreaterThan(0), "the fixture must actually rate as something");

            // Except on the dense fixture since backlog 363: every one of its words is syllabifiable
            // and unsubdivided, so the live arm reads each as ONE judgement window, which is exactly
            // Easy's word shelter, and the two arms rate identically. That is the change working (it
            // used to differ only through the gameplay syllabifier's invented cuts); the other five
            // fixtures still hold the arm axis apart.
            if (fixtureName == "dense")
                Assert.That(easy, Is.EqualTo(none), "an unsubdivided map's live windows are its words, which is Easy's shelter");
            else
                Assert.That(easy, Is.Not.EqualTo(none), "Easy must be a different rating, or the arm is not a rating input");
            Assert.That(hardRock, Is.Not.EqualTo(none), "and so must Hard Rock");
            Assert.That(matrix.TryGet(ServerArm.None, false, ClientPp.DOUBLE_TIME_BASE_RATE)!.Value.Stars, Is.GreaterThan(none),
                "and a faster clock must rate harder");
        });
    }

    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) Fixture(string name) => name switch
    {
        "dense" => DenseTwinMaps(),
        "freestyle" => FreestyleTwinMaps(),
        "subdivided" => SubdividedTwinMaps(),
        "paused" => PausedTwinMaps(),
        "paused-ignored" => PausedTwinMaps(PauseShape.InvalidOnly),
        _ => TwinMaps(),
    };

    [Test]
    public void TheEnvelopeArmAgreesToo()
    {
        // The chunked axis is what ships and what the eighteen cells hold, but the ENVELOPE arm is
        // still read by both sides for the pace figures' target, so a divergence there shows up on
        // the set page against song select's wedge rather than on a leaderboard. Same fixtures, same
        // exactness, one axis over.
        foreach (string name in new[] { "plain", "dense", "freestyle", "subdivided", "paused", "paused-ignored" })
        {
            var (client, server) = Fixture(name);

            foreach (double rate in new[] { 1.0, ClientPp.DOUBLE_TIME_BASE_RATE, ClientPp.HALF_TIME_BASE_RATE })
            foreach (bool literate in new[] { false, true })
            {
                var c = ClientDifficulty.ComputeDetail(client, rate, literate, ClientAxis.Envelope);
                var s = ServerDifficulty.ComputeDetail(server, rate, literate, ServerAxis.Envelope);

                string context = $"{name} rate={rate} literate={literate}";

                Assert.That(s.Stars, Is.EqualTo(c.Stars), $"{context}: stars");
                Assert.That(s.DifficultCharacters, Is.EqualTo(c.DifficultCharacters), $"{context}: difficult characters");
                Assert.That(s.TargetWpm, Is.EqualTo(c.TargetWpm), $"{context}: target WPM");
                Assert.That(s.Peak, Is.EqualTo(c.Peak), $"{context}: peak");
                Assert.That(s.Cells, Is.EqualTo(c.Cells), $"{context}: cells");
            }
        }
    }

    [Test]
    public void TheTwoPortsAgreeOnAFreestyleSlotAndOnWhetherItCostsAnything()
    {
        // Backlog 211, and since the rework a second thing: a marker is a STRETCH cell in the rhythm
        // arm as well as a quarter of a cell in the density. Every other fixture here is markerless,
        // and a markerless map rates bit-identically whatever the weight is, so none of them can see
        // either seam.
        var (client, server) = FreestyleTwinMaps();
        var (excludedClient, excludedServer) = FreestyleTwinMaps(markers: false);

        Assert.Multiple(() =>
        {
            // The premise, on both sides independently: the markers have to MOVE the rating, or the
            // fixture is just another markerless map and this pins nothing.
            Assert.That(ClientDifficulty.Compute(client), Is.Not.EqualTo(ClientDifficulty.Compute(excludedClient)), "client: the slots must cost something");
            Assert.That(ServerDifficulty.Compute(server), Is.Not.EqualTo(ServerDifficulty.Compute(excludedServer)), "server: the slots must cost something");

            // And the markerless twin has to agree too, which is the other half of the mirror: the
            // weight must have moved the marker map on both sides and left the markerless one alone.
            Assert.That(ServerDifficulty.Compute(excludedServer), Is.EqualTo(ClientDifficulty.Compute(excludedClient)), "and the markerless twin still agrees");
        });
    }

    [Test]
    public void AuthoredSyllableBoundariesReachTheRatingOnBothSides()
    {
        // The premise of the subdivided fixture, stated as its own pin: the boundaries have to MOVE
        // the rating, or the parity assertions on that fixture would pass on a server that still
        // dropped them. The comparison map is the same words at the same times with no subdivisions
        // at all, which is what the server computed before its TimedUnit carried them.
        var (client, server) = SubdividedTwinMaps();

        var bareServer = server.Select(l => new ServerLine
        {
            RawText = l.RawText,
            StartTime = l.StartTime,
            EndTime = l.EndTime,
            SingEndTime = l.SingEndTime,
            Units = l.Units.Select(u => new ServerUnit { Text = u.Text, StartTime = u.StartTime, EndTime = u.EndTime }).ToArray(),
        }).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(server.Any(l => l.Units.Any(u => u.SyllableBoundaries.Count > 0)), Is.True,
                "the fixture has to actually carry boundaries");
            Assert.That(ServerDifficulty.Compute(server), Is.Not.EqualTo(ServerDifficulty.Compute(bareServer)),
                "and they have to reach the rating, or a mirror that dropped them would pass every pin above");
            Assert.That(ServerDifficulty.Compute(server), Is.EqualTo(ClientDifficulty.Compute(client)),
                "and the two sides read the same subdivisions");
        });
    }

    [Test]
    public void AuthoredPausesReachTheRatingOnBothSidesAndIgnoredOnesDoNot()
    {
        // The premise of the paused fixtures, stated as its own pin, in both directions. A VALID rest
        // has to MOVE the rating, or the eighteen-cell parity on that fixture would pass on a server
        // that still read every paused word as one span. And a rest the derivation must IGNORE has to
        // move NOTHING, on both sides: the map rates exactly as the same map with no rest at all,
        // which is what a map written before the feature is.
        var (client, server) = PausedTwinMaps();
        var (ignoredClient, ignoredServer) = PausedTwinMaps(PauseShape.InvalidOnly);
        var (bareClient, bareServer) = PausedTwinMaps(PauseShape.None);

        Assert.Multiple(() =>
        {
            foreach ((ClientArm clientArm, ServerArm serverArm, bool literate, double rate, string name) in Combinations())
            {
                double paused = ServerDifficulty.ComputeDetail(server, rate, literate, ServerDifficulty.Live, serverArm).Stars;
                double ignored = ServerDifficulty.ComputeDetail(ignoredServer, rate, literate, ServerDifficulty.Live, serverArm).Stars;
                double bare = ServerDifficulty.ComputeDetail(bareServer, rate, literate, ServerDifficulty.Live, serverArm).Stars;

                // EASY IS THE EXCEPTION, on both sides and by design: its arm shelters a press by
                // the WORD rather than the syllable (LyricDifficulty's Shelter.Word, the model's
                // mirror of the engine's word shelter), so a divider inside a word, a rest as much
                // as a syllable boundary, moves nothing it reads. The parity pins above still hold
                // its cells to the client's; only the premise skips it.
                if (serverArm != ServerArm.Easy)
                {
                    Assert.That(paused, Is.Not.EqualTo(bare), $"{name}: the rests have to reach the server's rating");
                    Assert.That(ClientDifficulty.ComputeDetail(client, rate, literate, ClientDifficulty.Live, clientArm).Stars,
                        Is.Not.EqualTo(ClientDifficulty.ComputeDetail(bareClient, rate, literate, ClientDifficulty.Live, clientArm).Stars),
                        $"{name}: the client's premise too");
                }

                Assert.That(ignored, Is.EqualTo(bare), $"{name}: and an ignored rest has to cost nothing on the server");
                Assert.That(ClientDifficulty.ComputeDetail(ignoredClient, rate, literate, ClientDifficulty.Live, clientArm).Stars,
                    Is.EqualTo(bare), $"{name}: and the client's ignored rests read as the server's bare map");
            }

            // The fixture must hand the server rests at all, or the inequalities above test the
            // syllable boundaries rather than the pauses.
            Assert.That(server.Sum(l => l.Units.Sum(u => u.Pauses.Count)), Is.GreaterThan(0));
            Assert.That(ignoredServer.Sum(l => l.Units.Sum(u => u.Pauses.Count)), Is.GreaterThan(0));
        });
    }

    [Test]
    public void TheClientsStarsForIsTheServersStarsForOnTheSameMap()
    {
        var (client, server) = TwinMaps();
        var matrix = ServerMatrix(server);

        foreach (var clientStack in new[]
                 {
                     no_client_mods,
                     Stack(new TypeBeatModLiterate()),
                     Stack(new TypeBeatModDoubleTime()),
                     Stack(new TypeBeatModNightcore()),
                     Stack(new TypeBeatModHalfTime()),
                     Stack(new TypeBeatModEasy()),
                     Stack(new TypeBeatModHardRock()),
                     Stack(new TypeBeatModEasy(), new TypeBeatModLiterate(), new TypeBeatModDoubleTime()),
                     Stack(new TypeBeatModHardRock(), new TypeBeatModHalfTime()),
                     Stack(new TypeBeatModLiterate(), new TypeBeatModDoubleTime()),
                     Stack(new TypeBeatModLiterate(), new TypeBeatModHalfTime()),
                     Stack(At(new TypeBeatModDoubleTime(), 1.75)),
                     Stack(At(new TypeBeatModHalfTime(), 0.60)),
                 })
        {
            double? clientStars = ClientPp.StarsFor(client, clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), matrix);

            string context = string.Join('+', clientStack.Select(m => m.Acronym));

            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);

            // And the OTHER half, which nothing before the rework had to agree on: the client
            // derives the difficult characters from the lines, the server reads them off the stored
            // cell, and a price needs both.
            Assert.That(serverStars.DifficultCharacters,
                Is.EqualTo(clientStars is null ? 0 : ClientPp.DifficultCharactersFor(client, clientStack)), context);
        }
    }

    [Test]
    public void TheLiterateRatingIsNotTheRateRatingTimesAConstant()
    {
        // WHY THE MATRIX STORES A CROSS PRODUCT AND NOT A FEW SCALARS. The obvious saving is to
        // store one rating per axis and recover the rest by multiplying, i.e. to assume the rate,
        // the stream and the arm compose. They do not: the rate compresses the timeline, which moves
        // which chunk boundaries and which judgement intervals the model sees; Literate adds cells
        // to the words already there, changing every chunk's density without moving a boundary; and
        // the arm changes the INTERVALS a press may land in without touching either. No two of the
        // three are a scalar on each other.
        //
        // This test is the standing proof of that, so a future reader who reaches for the saving
        // finds the counter-example already written down rather than having to rediscover it.
        var (_, server) = TwinMaps();

        double plainBase = ServerDifficulty.Compute(server);
        double plainDt = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double literateBase = ServerDifficulty.Compute(server, 1, literate: true);
        double literateDt = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, literate: true);
        double easyBase = ServerDifficulty.Compute(server, 1, false, ServerDifficulty.Live, ServerArm.Easy);
        double easyDt = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, false, ServerDifficulty.Live, ServerArm.Easy);

        Assert.Multiple(() =>
        {
            Assert.That(literateDt, Is.Not.EqualTo(literateBase * (plainDt / plainBase)).Within(1e-9),
                "if this ever holds, the rate ratio has become stream-independent and the literate cells could collapse");
            Assert.That(easyDt, Is.Not.EqualTo(easyBase * (plainDt / plainBase)).Within(1e-9),
                "and the same for the arm, which is the axis the rework added");
        });
    }

    [Test]
    public void AMapWhoseMatrixIsMissingItsCellIsPendingRatherThanPricedOffAnotherOne()
    {
        // The deferral rule, which the matrix WIDENS: through v21 only a rate or a Literate play
        // could be pending, because difficulty_rating has been NOT NULL since 001. beatmaps.ratings
        // is a new nullable column, so on a row the sweep has not reached EVERY play is pending,
        // including a bare no-mod one. The answer has to be "not yet" rather than a price the very
        // next sweep would disagree with.
        var (_, server) = TwinMaps();
        var full = ServerMatrix(server);

        Assert.Multiple(() =>
        {
            foreach (var clientStack in new[]
                     {
                         no_client_mods,
                         Stack(new TypeBeatModLiterate()),
                         Stack(new TypeBeatModDoubleTime()),
                         Stack(new TypeBeatModEasy()),
                     })
            {
                string context = string.Join('+', clientStack.Select(m => m.Acronym));
                var missing = ServerPp.StarsFor(ServerMods(clientStack), null);

                Assert.That(missing.Stars, Is.Null, context);
                Assert.That(missing.Pending, Is.True, context + ": left stale for PpBackfill, not settled at a wrong price");

                // And with the matrix filled it prices.
                Assert.That(ServerPp.StarsFor(ServerMods(clientStack), full).Pending, Is.False, context);
                Assert.That(ServerPp.StarsFor(ServerMods(clientStack), full).Stars, Is.Not.Null, context);
            }

            // A CUSTOM RATE IS STILL A REFUSAL and not a deferral, matrix or no matrix: nothing about
            // filling the column can ever make that play eligible, and the client agrees by showing
            // no number at all.
            var custom = Stack(At(new TypeBeatModDoubleTime(), 1.75));

            Assert.That(ServerPp.StarsFor(ServerMods(custom), null).Pending, Is.False);
            Assert.That(ServerPp.StarsFor(ServerMods(custom), full).Pending, Is.False);
            Assert.That(ServerPp.StarsFor(ServerMods(custom), full).Stars, Is.Null);
        });
    }

    #endregion

    #region 6. Typability: the same table, reachable under two different manifest names

    [Test]
    public void TheTypabilityTableIsTheSameTableOnBothSides()
    {
        // THE ONE SANCTIONED DIFFERENCE between the two TypabilityIndex files is the manifest
        // RESOURCE NAME the table is embedded under, so "the same bytes are reachable" is a claim
        // that cannot be made in either repo alone. A server whose csproj forgot to glob the
        // resources compiles, runs, and rates every map as if typability were off: the model reads a
        // line as unscored, the map fails the scored-fraction gate, and every one of its eighteen
        // ratings comes out at the no-typability value. That is a silent, whole-catalogue divergence
        // and this is the only place it can be caught.
        Assert.Multiple(() =>
        {
            Assert.That(ServerTypability.KnownLines, Is.EqualTo(ClientTypability.KnownLines));
            Assert.That(ServerTypability.KnownLines, Is.GreaterThan(1000),
                "the table has to have actually loaded; an empty one would make every assertion below vacuous");

            // The dials, which are constants of the model rather than of the table.
            Assert.That(ServerTypability.Strength, Is.EqualTo(ClientTypability.Strength));
            Assert.That(ServerTypability.Cv, Is.EqualTo(ClientTypability.Cv));
            Assert.That(ServerTypability.MinCoverage, Is.EqualTo(ClientTypability.MinCoverage));
            Assert.That(ServerTypability.MinScoredFraction, Is.EqualTo(ClientTypability.MinScoredFraction));
            Assert.That(ServerTypability.MinMultiplier, Is.EqualTo(ClientTypability.MinMultiplier));
            Assert.That(ServerTypability.MaxMultiplier, Is.EqualTo(ClientTypability.MaxMultiplier));
        });
    }

    [Test]
    public void TheTwoIndexesScoreTheSameLinesTheSameWay()
    {
        // Lines of both kinds, because they take different paths through the index: one the shipped
        // TABLE carries (which must win outright) and one it does not (which is computed in-process
        // from the model tables and the four Hunspell dictionaries, a far longer path and the one
        // that reads the resources this file exists to check).
        //
        // The catalogue line is discovered rather than hard-coded: the table is regenerated by a
        // tool, so a line spelled here would go stale silently and quietly turn this into a
        // two-non-catalogue-line test.
        string[] lines =
        [
            // Plainly not in the bundled catalogue: real English, so it clears the coverage gate and
            // is scored by the computed index rather than refused.
            "the quick brown fox jumps over the lazy dog",
            "typing is a rhythm not a race",
            "remember the beautiful instrumental together",
            // Marks and capitals, i.e. the Literate stream's form of a line, which the index scores
            // as a different sentence from its stripped form.
            "Remember, the beautiful instrumental. Together!",
            // Under the coverage gate on both sides: mostly outside every dictionary, so both must
            // refuse it rather than scoring words the index never saw.
            "zzxq wvbk mjqt phgz",
            // Degenerate.
            "",
            "a",
            "   ",
        ];

        Assert.Multiple(() =>
        {
            foreach (string line in lines)
            {
                bool clientScored = ClientTypability.TryScore(line, out var clientScore);
                bool serverScored = ServerTypability.TryScore(line, out var serverScore);

                Assert.That(serverScored, Is.EqualTo(clientScored), $"scored? '{line}'");

                if (!clientScored)
                    continue;

                Assert.That(serverScore.Z, Is.EqualTo(clientScore.Z), $"z of '{line}'");
                Assert.That(serverScore.Coverage, Is.EqualTo(clientScore.Coverage), $"coverage of '{line}'");

                // And the multiplier the rating actually reads, which is what a z becomes.
                Assert.That(ServerTypability.Multiplier(serverScore.Z), Is.EqualTo(ClientTypability.Multiplier(clientScore.Z)), $"multiplier of '{line}'");
            }

            // The premise: at least one of those lines has to have been SCORED, or the loop above
            // only ever asserted that both sides refuse everything, which an empty table would also
            // satisfy.
            Assert.That(lines.Count(l => ServerTypability.TryScore(l, out _)), Is.GreaterThan(0),
                "the computed index has to actually score something, or this test is vacuous");

            // The clamp and the multiplier curve, swept rather than sampled, since they are pure
            // functions of a z and a cheap way to catch a constant landed on one side only.
            for (int step = -40; step <= 40; step++)
            {
                double z = step / 10.0;

                Assert.That(ServerTypability.ClampZ(z), Is.EqualTo(ClientTypability.ClampZ(z)), $"clamp at z={z}");
                Assert.That(ServerTypability.Multiplier(z), Is.EqualTo(ClientTypability.Multiplier(z)), $"multiplier at z={z}");
            }
        });
    }

    #endregion

    #region 7. The whole pipeline, end to end

    [Test]
    public void TheWholePipelineAgreesForEveryPlayShapeThatMatters()
    {
        var (client, server) = TwinMaps();
        var matrix = ServerMatrix(server);

        IReadOnlyList<Mod>[] stacks =
        [
            no_client_mods,
            Stack(new TypeBeatModNoFail()),
            Stack(new TypeBeatModLiterate(), new TypeBeatModFlashlight()),
            Stack(new TypeBeatModFletcher()),
            Stack(new TypeBeatModSuddenDeath(), new TypeBeatModMuted()),
            Stack(new TypeBeatModDoubleTime()),
            Stack(new TypeBeatModNightcore(), new TypeBeatModLiterate()),
            Stack(new TypeBeatModHalfTime(), new TypeBeatModNoFail()),
            Stack(new TypeBeatModEasy()),
            Stack(new TypeBeatModHardRock(), new TypeBeatModRecite()),
            Stack(new TypeBeatModEasy(), new TypeBeatModLiterate(), new TypeBeatModDoubleTime()),
            Stack(At(new TypeBeatModDoubleTime(), 1.99)), // custom rate: worth nothing on both sides
            Stack(At(new TypeBeatModHalfTime(), 0.51)),
        ];

        Dictionary<HitResult, int>[] plays =
        [
            // A flawless run.
            new() { [HitResult.Great] = 400, [HitResult.IgnoreHit] = 60 },
            // A realistic one: sloppy timing, a few dropped cells, stumbles.
            new() { [HitResult.Great] = 300, [HitResult.Ok] = 60, [HitResult.Meh] = 20, [HitResult.Miss] = 20, [HitResult.IgnoreHit] = 60, [HitResult.ComboBreak] = 74 },
            // A masher: everything typed, but a wrong key for every right one.
            new() { [HitResult.Great] = 400, [HitResult.ComboBreak] = 400 },
            // A give-up run.
            new() { [HitResult.Great] = 40, [HitResult.Miss] = 360, [HitResult.ComboBreak] = 12 },
            // A play from before the typo stat existed: no combo_break key at all.
            new() { [HitResult.Great] = 380, [HitResult.Miss] = 20 },
            // A one-note map.
            new() { [HitResult.Great] = 1 },
        ];

        foreach (var clientStack in stacks)
        foreach (var play in plays)
        foreach (double accuracy in new[] { 0.0, 0.55, 0.93, 1.0 })
        foreach (int maxCombo in new[] { 0, 137, 400 })
        {
            var serverStack = ServerMods(clientStack);

            // THE CLIENT FILLS THE DIFFICULT CHARACTERS FROM THE MAP IT HAS LOADED; the server reads
            // them off the stored cell. That is the seam the whole matrix exists to close, and it is
            // crossed here on every play rather than only in the StarsFor pin above.
            var clientCounts = ClientPp.CountNotes(play) with { DifficultCharacters = ClientPp.DifficultCharactersFor(client, clientStack) };
            var serverCounts = ServerCounts(play);

            double? clientStars = ClientPp.StarsFor(client, clientStack);
            var serverStars = ServerPp.StarsFor(serverStack, matrix);

            // What the in-game counter would show (nothing at all when the play's rate makes it
            // ineligible, which is the same "no price exists" the server reports as a null).
            double? clientPp = clientStars is double stars
                ? ClientPp.ForPlay(stars, clientCounts, accuracy, maxCombo, clientStack)
                : null;

            // ...against what the server would write to scores.pp for the very same play.
            var (serverPp, settled) = ServerPp.ForScore(
                ranked: true, serverStack, serverCounts, accuracy, maxCombo, matrix);

            string context = $"mods=[{string.Join('+', clientStack.Select(m => m.Acronym))}] " +
                             $"play={string.Join(',', play.Select(kv => $"{kv.Key}:{kv.Value}"))} acc={accuracy} combo={maxCombo}";

            Assert.That(clientCounts.Notes, Is.EqualTo(serverCounts.Notes), context);
            Assert.That(clientCounts.Misses, Is.EqualTo(serverCounts.Misses), context);
            Assert.That(clientCounts.Typos, Is.EqualTo(serverCounts.Typos), context);
            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);
            Assert.That(clientCounts.DifficultCharacters,
                Is.EqualTo(clientStars is null ? 0 : serverStars.DifficultCharacters), context);
            Assert.That(settled, Is.True, context);
            Assert.That(clientPp, Is.EqualTo(serverPp), context);
        }
    }

    [Test]
    public void EachRateAndEachArmIsExactlyItsOwnRatingOnBothSides()
    {
        // The pipeline test proves the two agree on a rate or an arm play; this proves what they
        // agree ON. Every non-rating factor of the price is shared across the arms of this test, so
        // a ratio of two of them is exactly the two ratings raised to sr_exponent, times whatever
        // the two cleanliness terms differ by (their difficult-character counts are not the same
        // number, which is the point of storing them per cell).
        var (client, server) = TwinMaps();
        var matrix = ServerMatrix(server);

        var play = new Dictionary<HitResult, int>
        {
            [HitResult.Great] = 308, [HitResult.Ok] = 60, [HitResult.Meh] = 20, [HitResult.Miss] = 12, [HitResult.ComboBreak] = 15,
        };

        double Price(IReadOnlyList<Mod> stack)
        {
            var (pp, settled) = ServerPp.ForScore(true, ServerMods(stack), ServerCounts(play), 0.93, 380, matrix);

            Assert.That(settled, Is.True);
            return pp!.Value;
        }

        double ClientPrice(IReadOnlyList<Mod> stack)
        {
            var counts = ClientPp.CountNotes(play) with { DifficultCharacters = ClientPp.DifficultCharactersFor(client, stack) };
            return ClientPp.ForPlay(ClientPp.StarsFor(client, stack)!.Value, counts, 0.93, 380, stack);
        }

        Assert.Multiple(() =>
        {
            foreach (var stack in new[]
                     {
                         no_client_mods,
                         Stack(new TypeBeatModDoubleTime()),
                         Stack(new TypeBeatModHalfTime()),
                         Stack(new TypeBeatModEasy()),
                         Stack(new TypeBeatModHardRock()),
                         Stack(new TypeBeatModLiterate()),
                     })
            {
                string context = string.Join('+', stack.Select(m => m.Acronym));

                Assert.That(ClientPrice(stack), Is.EqualTo(Price(stack)), context);

                // And the price is the one the cell alone implies, which is the claim that neither
                // side is applying a second, hidden term for the same mod.
                var cell = ServerPp.StarsFor(ServerMods(stack), matrix);

                Assert.That(Price(stack), Is.EqualTo(ServerPp.Compute(
                    cell.Stars!.Value, ServerCounts(play).Notes, cell.DifficultCharacters,
                    ServerCounts(play).Misses, 0.93, 380, ServerMods(stack), ServerCounts(play).Typos)).Within(1e-12), context);
            }

            // HARD ROCK'S FLAT TERM IS NEUTRAL, so the whole of what it is worth comes through its
            // cell. That is the double-count this shape exists to avoid, and a flat term reinstated
            // on either side would show here and nowhere else.
            Assert.That(ServerPp.ModMultiplier(ServerMods(Stack(new TypeBeatModHardRock())), 400), Is.EqualTo(1.0));
            Assert.That(Price(Stack(new TypeBeatModHardRock())), Is.Not.EqualTo(Price(no_client_mods)),
                "and it still moves the price, through the rating");
        });
    }

    [Test]
    public void APlayThatEarnsNothingEarnsNothingOnBothSides()
    {
        var (client, server) = TwinMaps();
        var matrix = ServerMatrix(server);

        var counts = new Dictionary<HitResult, int> { [HitResult.Great] = 400 };
        var customRate = Stack(At(new TypeBeatModDoubleTime(), 1.75));

        var (serverPp, _) = ServerPp.ForScore(true, ServerMods(customRate), ServerCounts(counts), 0.9, 400, matrix);

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.StarsFor(client, customRate), Is.Null, "the client shows no number at all");
            Assert.That(serverPp, Is.Null, "and the server prices nothing at all, which is not a price of zero");
        });
    }

    #endregion
}
