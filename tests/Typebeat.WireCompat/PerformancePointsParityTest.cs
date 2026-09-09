using Newtonsoft.Json;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;

using ClientDifficulty = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPp = typebeat.Game.Rulesets.TypeBeat.Scoring.PerformancePoints;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using ServerDifficulty = Typebeat.Web.Packages.Lyrics.LyricDifficulty;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;
using ServerPp = Typebeat.Web.Scoring.PerformancePoints;
using ServerTypeability = Typebeat.Web.Packages.Lyrics.Typeability;
using ServerUnit = Typebeat.Web.Packages.Lyrics.TimedUnit;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for PERFORMANCE POINTS (backlog 74).
///
/// <para>
/// pp exists twice: the server's <see cref="ServerPp"/> is what writes <c>scores.pp</c>, and the
/// game's <see cref="ClientPp"/> is what the in-game live counter and (later) the results screen
/// price a play with. A player who watches a counter climb to 214 and then sees 198 on their
/// profile has been lied to, so the two must be the SAME arithmetic, not merely similar. This is
/// the only project that compiles both repos, so this is where that is provable.
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
/// rating itself; the server decides which STORED COLUMN prices it. Those are different-looking
/// decisions that must always agree on eligible-or-not, and on which of the three ratings.</item>
/// <item>STAR RATING. The client's whole claim to price a play locally rests on its
/// <c>LyricDifficulty</c> producing the same numbers the server stores as
/// <c>difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c>. That is asserted directly, at all three
/// rates, rather than assumed.</item>
/// </list>
///
/// <para>
/// This file compiles only where the game repo is resolvable (a sibling checkout locally, the
/// pinned submodule in CI), which is the same condition every other test in this project has. It is
/// the first test here to reference the typebeat RULESET project as well as typebeat.Game.
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
    /// What a FULL combo adds to a play at this rating (backlog 270), spelled once because it is
    /// needed on both sides of every ratio in this file.
    ///
    /// <para>THE BONUS DOES NOT CANCEL IN A RATIO. Combo was a FACTOR of the product through v20,
    /// so dividing one play by another left it out; it is ADDED now, so a ratio of two plays that
    /// both carry it is <c>(P1 + B)/(P2 + B)</c> and says nothing about either product. Every pin
    /// below that used to lean on the cancellation subtracts this instead.</para>
    /// </summary>
    private static double FullComboBonus(double starRating)
        => 12.5 * (starRating - 1.0); // pp:const combo_bonus_slope=12.5 combo_bonus_zero=1.0

    #endregion

    #region 1. The formula itself

    [Test]
    public void TheTwoFormulasAgreeExactlyOverASpreadOfPlays()
    {
        double[] stars = [0.5, 1.0, 2.75, 4.0, 6.3, 9.99];
        int[] noteCounts = [1, 5, 47, 100, 500, 2137];
        // 0.78 / 0.80 / 0.82 straddle the accuracy knee backlog 227 put at acc_knee: the term is a
        // logistic there, so a mirror that has the knee and one that does not agree to within a
        // percent at 0.9 and disagree by a factor of two at 0.80. This is the seam that catches a
        // knee landed in only one of the two repos.
        double[] accuracies = [0.0, 0.42, 0.6931, 0.78, 0.80, 0.82, 0.9, 1.0];

        int compared = 0;

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double accuracy in accuracies)
        foreach (int misses in new[] { 0, 1, notes / 3, notes })
        foreach (int maxCombo in new[] { 0, notes / 2, notes })
        foreach (int typos in new[] { 0, 1, 137, notes * 3 })
        {
            double client = ClientPp.Compute(sr, notes, misses, accuracy, maxCombo, no_client_mods, typos);
            double server = ServerPp.Compute(sr, notes, misses, accuracy, maxCombo, no_server_mods, typos);

            Assert.That(client, Is.EqualTo(server),
                $"sr={sr} notes={notes} miss={misses} acc={accuracy} combo={maxCombo} typos={typos}");
            compared++;
        }

        Assert.That(compared, Is.GreaterThan(1000), "the spread must actually be a spread");
    }

    [Test]
    public void TheTwoSplitPenaltyTermsAgreeExactlyIncludingTheDecidedWorkedExamples()
    {
        // Backlog 89 split one penalty term into two, which is a fresh seam: the miss term and the
        // typo term could now drift apart INDEPENDENTLY, and a spread that only ever moves both
        // at once could miss it. Each case below holds one of the two counts fixed while the other
        // moves, and the two headline cases the rebalance was decided on are stated as exact values
        // so this file also pins WHAT the split is worth, not merely that both halves agree.
        //
        // Nothing else in the formula reads either count, so pp divided by the same play with
        // neither is exactly
        // max(0, 1 - miss^1.2/notes)^10 * max(0, 1 - typos^1.2/(notes+typos))^6.
        //
        // Backlog 97 put a CLAMP in both terms, which is a fresh seam of its own: a mirror that
        // clamped and one that did not would agree on every play under the cliff and disagree on
        // every play past it. Backlog 101 then moved both cliffs a long way out, from 23 misses to
        // 178 and from 23 typos to 249, which makes the seam WIDER rather than narrower: a mirror
        // stuck at the old power would clamp on nearly every case a straddle-the-old-cliff spread
        // used, and agree everywhere else. The spread below therefore straddles the NEW cliffs
        // deliberately, and keeps the old thresholds too, where the two powers now disagree the most.
        //
        // A SECOND SEAM THE FRACTIONAL POWER OPENS: Math.Pow(x, 1.2) is not the exactly-rounded
        // product Math.Pow(x, 2) effectively is, so the two mirrors agreeing here is a claim about
        // both calling the same Math.Pow on the same double, which is exactly what EXACT equality
        // (no tolerance) below pins.
        const int notes = 500;

        // EVERY RATIO BELOW IS TAKEN AT NO COMBO AT ALL (backlog 270), which is what keeps the
        // cancellation these figures depend on exact. The combo bonus is ADDED to the product
        // rather than being a factor of it, so it does not cancel in a ratio; subtracting it back
        // off works in the middle of the range and NOT at the ends, because a play one miss below
        // the cliff has a product of about 1e-23 and adding 37.5 pp to that loses it entirely in
        // double. At maxCombo 0 the bonus is exactly 0 and pp IS the product.
        double clientSpotless = ClientPp.Compute(4, notes, 0, 0.9, 0, no_client_mods, 0);
        double serverSpotless = ServerPp.Compute(4, notes, 0, 0.9, 0, no_server_mods, 0);

        Assert.That(clientSpotless, Is.EqualTo(serverSpotless), "the spotless baseline itself must agree");

        foreach ((int misses, int typos) in new[]
                 {
                     (60, 80),   // the first decided example, a live number again since backlog 101
                     (10, 20),   // the second: 0.96830^10 * 0.92998^6, the headline figure
                     (0, 0), (0, 1), (0, 22), (0, 23), (0, 80),              // typos alone,
                     (0, 248), (0, 249), (0, 5000),                          // over the new cliff
                     (1, 0), (22, 0), (23, 0), (60, 0),                      // misses alone,
                     (177, 0), (178, 0), (250, 0), (500, 0),                 // over the new cliff
                     (177, 248), (178, 249),               // either side of both cliffs at once
                     (500, 5000),                          // both at once, at the extreme
                 })
        {
            double client = ClientPp.Compute(4, notes, misses, 0.9, notes, no_client_mods, typos);
            double server = ServerPp.Compute(4, notes, misses, 0.9, notes, no_server_mods, typos);

            Assert.That(client, Is.EqualTo(server), $"miss={misses} typos={typos}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.Compute(4, notes, 60, 0.9, 0, no_client_mods, 80) / clientSpotless,
                Is.EqualTo(0.008341).Within(1e-6)); // pp[f.penalty(500, 60, 80)]
            Assert.That(ClientPp.Compute(4, notes, 10, 0.9, 0, no_client_mods, 20) / clientSpotless,
                Is.EqualTo(0.542001).Within(1e-6)); // pp[f.penalty(500, 10, 20)]

            // Zero typos leaves the typo term at exactly 1.0 on both sides, so the play is
            // priced by its misses alone. Ten misses, not the sixty this used to use: sixty was past
            // the backlog-97 cliff, so both sides would have been asserted to equal zero and the
            // restatement would have stopped saying anything about the arithmetic that produced it.
            Assert.That(ClientPp.Compute(4, notes, 10, 0.9, 0, no_client_mods, 0) / clientSpotless,
                Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.2) / 500.0), 10)).Within(1e-12)); // pp:const count_power=1.2 miss_exponent=10
            Assert.That(ServerPp.Compute(4, notes, 10, 0.9, 0, no_server_mods, 0) / serverSpotless,
                Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.2) / 500.0), 10)).Within(1e-12)); // pp:const count_power=1.2 miss_exponent=10

            // And BOTH sides reach the clamped zero from the same input, which is the seam the
            // clamp itself opens: a mirror missing the Math.Max would produce a non-real result
            // here rather than a zero, and nothing else in this file would catch it. The thresholds
            // are the CURRENT cliffs, so a mirror at the old power would fail the two below the
            // cliff rather than the two at it.
            //
            // AT NO COMBO THE PLAY REALLY IS AN EXACT ZERO on both sides, which is what these
            // pinned before the bonus existed and still the sharpest form of the claim.
            const int missCliff = 178; // pp[math.ceil(f.miss_cliff(500))]
            const int typoCliff = 249; // pp[math.ceil(f.typo_cliff(500))]

            Assert.That(ClientPp.Compute(4, notes, missCliff, 0.9, 0, no_client_mods, 0), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, missCliff, 0.9, 0, no_server_mods, 0), Is.Zero);
            Assert.That(ClientPp.Compute(4, notes, 0, 0.9, 0, no_client_mods, typoCliff), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, 0, 0.9, 0, no_server_mods, typoCliff), Is.Zero);

            Assert.That(ClientPp.Compute(4, notes, missCliff - 1, 0.9, 0, no_client_mods, 0), Is.GreaterThan(0));
            Assert.That(ServerPp.Compute(4, notes, missCliff - 1, 0.9, 0, no_server_mods, 0), Is.GreaterThan(0));
            Assert.That(ClientPp.Compute(4, notes, 0, 0.9, 0, no_client_mods, typoCliff - 1), Is.GreaterThan(0));
            Assert.That(ServerPp.Compute(4, notes, 0, 0.9, 0, no_server_mods, typoCliff - 1), Is.GreaterThan(0));

            // WITH A FULL COMBO the same two inputs are worth EXACTLY the bonus and not a fraction
            // more (backlog 270), which is where the clamped product now goes: it is added to,
            // not multiplied by. Both sides, because a bonus placed inside the product on one
            // mirror only would show here and nowhere else in this region.
            double bonus = FullComboBonus(4);

            Assert.That(ClientPp.Compute(4, notes, missCliff, 0.9, notes, no_client_mods, 0), Is.EqualTo(bonus));
            Assert.That(ServerPp.Compute(4, notes, missCliff, 0.9, notes, no_server_mods, 0), Is.EqualTo(bonus));
            Assert.That(ClientPp.Compute(4, notes, 0, 0.9, notes, no_client_mods, typoCliff), Is.EqualTo(bonus));
            Assert.That(ServerPp.Compute(4, notes, 0, 0.9, notes, no_server_mods, typoCliff), Is.EqualTo(bonus));
        });
    }

    [Test]
    public void TheTwoFormulasAgreeOnDegenerateInput()
    {
        // Neither side may ever produce NaN, Infinity or a negative number, and they must reach the
        // same zero from the same hostile inputs: zero notes, zero/NaN/infinite stars, an accuracy
        // outside [0, 1], negative counts, a combo above the note count.
        double[] stars = [0, -1, 0.0001, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        int[] noteCounts = [-3, 0, 1, 4, 100];
        double[] accuracies = [-1, 0, 2, double.NaN, double.PositiveInfinity];

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double accuracy in accuracies)
        foreach (int misses in new[] { -5, 0, notes + 7 })
        foreach (int maxCombo in new[] { -3, notes, notes + 9 })
        foreach (int typos in new[] { -1, 0, int.MaxValue })
        {
            double client = ClientPp.Compute(sr, notes, misses, accuracy, maxCombo, no_client_mods, typos);
            double server = ServerPp.Compute(sr, notes, misses, accuracy, maxCombo, no_server_mods, typos);

            string context = $"sr={sr} notes={notes} miss={misses} acc={accuracy} combo={maxCombo} typos={typos}";

            Assert.That(client, Is.EqualTo(server), context);
            Assert.That(double.IsFinite(client), Is.True, context);
            Assert.That(client, Is.GreaterThanOrEqualTo(0), context);
        }
    }

    [Test]
    public void TheTwoFlashlightCurvesAgree()
    {
        // The length curve used to be checked here beside this one. Backlog 152 deleted it from
        // both mirrors, so the only note-count-driven curve left in pp is Flashlight's. The spread
        // still runs the whole old range, including the counts that only ever mattered to the
        // length floor, since they cost nothing and cover Flashlight's own clamp.
        foreach (int notes in new[] { -1, 0, 1, 3, 4, 5, 6, 45, 46, 47, 100, 500, 1000, 12345 })
        {
            Assert.That(ClientPp.FlashlightMultiplier(notes), Is.EqualTo(ServerPp.FlashlightMultiplier(notes)), $"flashlight at {notes}");
        }
    }

    // THE HALF TIME MIRROR IS GONE (backlog 265). A test stood here sweeping both mirrors over a
    // 4096-point rating cube, because backlog 90 had given Half Time a multiplier neither side
    // could derive from the one rating it prices with and whose branch turned on a comparison
    // against 1.0. Neither side computes anything of the kind now: an HT play is its 0.75x rating,
    // a DT play its 1.50x one, and the seam that needed a cube to cover no longer exists. What
    // replaces it is the exact re-pin at the bottom of this file, which asserts that a Half Time
    // play prices at the UNPENALISED value and that its rate factor is the plain rating ratio.

    [Test]
    public void TheTwoFormulasAreTheSameGeneration()
    {
        // A client shipped against generation N must not quietly price plays the server stores at
        // generation N+1. Bumping one without the other is exactly what this catches.
        Assert.That(ClientPp.VERSION, Is.EqualTo(ServerPp.VERSION));

        // And the generation itself, so a half-landed cross-repo change that bumped BOTH mirrors
        // but left docs/pp.md and the two per-repo pins behind is caught here too.
        Assert.That(ServerPp.VERSION, Is.EqualTo(21)); // pp:version
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
            // The TYPO stat: priced by its own term, never a note.
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
            Assert.That(ClientPp.Compute(4, 100, 10, 0.9, 90, no_client_mods, ClientPp.CountNotes(old).Typos),
                Is.EqualTo(ServerPp.Compute(4, 100, 10, 0.9, 90, no_server_mods)));
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

    /// <summary>
    /// A STORED RH IS UNPRICED ON BOTH SIDES SINCE BACKLOG 270, and that is what needs pinning now.
    /// The mod shipped (backlog 135) and left the client at backlog 147, so no play can carry the
    /// acronym any more; exactly one stored row still does, and it reprices 10% down at v21, which
    /// is what a VERSION bump is for.
    ///
    /// <para>The removal has to land on BOTH sides or it is worse than not landing at all: pp is
    /// recomputed from a stored row's mods on every <c>PpBackfill</c> sweep and every recalc, so an
    /// arm surviving on one side only is a silent 10% divergence between the in-game counter and
    /// the stored value. That is the same argument the pin made before the deletion, one direction
    /// over, and it is why this test is inverted rather than deleted.</para>
    ///
    /// <para>The <c>ModMultiplier.TotalScoreCeiling</c> table the old note worried about is a
    /// DIFFERENT FILE and is untouched: it still prices <c>"RH"</c> at 1.10, so the stored row's
    /// total stays under its own ceiling and stays ranked.</para>
    /// </summary>
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
        // test whenever the neutral answer happens to be right. FC is 1.02 since backlog 270, so
        // the pair is worth exactly FC and an RH arm left in either mirror would show here.
        var pair = Stack(new RetiredRhythmicMod(), new TypeBeatModFletcher());
        Assert.That(ClientPp.ModMultiplier(pair, 500), Is.EqualTo(ServerPp.ModMultiplier(ServerMods(pair), 500)));
        Assert.That(ClientPp.ModMultiplier(pair, 500), Is.EqualTo(ClientPp.ModMultiplier(Stack(new TypeBeatModFletcher()), 500)));
    }

    /// <summary>
    /// THE COMBO BONUS SITS OUTSIDE THE PRODUCT, MOD MULTIPLIER INCLUDED (backlog 270), and this is
    /// the pin that catches a mirror that put it anywhere else.
    ///
    /// <para>Every other parity pin in this file is blind to the placement. A SPOTLESS FULL COMBO
    /// prices identically whether the bonus multiplies or adds, the no-mod sweep at the top has no
    /// multiplier to distribute over, and a play with no combo at all has no bonus to misplace. It
    /// takes all three at once: a LOSSY play (so the product is not 1), at less than a full combo
    /// (so the bonus is not the whole of it), carrying a MOD STACK (so there is a multiplier to
    /// distribute), on a map rated above <c>combo_bonus_zero</c> (so the bonus is not clamped to
    /// nothing).</para>
    ///
    /// <para>Both mirrors are asserted against each other AND against the arithmetic spelled out,
    /// because the two agreeing on a wrong placement is exactly the failure a cross-repo hand edit
    /// produces: the same mistake gets made twice.</para>
    /// </summary>
    [Test]
    public void ALossyModdedNonFullComboPricesTheBonusOutsideTheProductOnBothSides()
    {
        const double stars = 5.5;
        const int notes = 800;
        const int misses = 30;
        const int typos = 40;
        const int maxCombo = 512;
        const double accuracy = 0.91;

        var clientStack = Stack(new TypeBeatModNoFail(), new TypeBeatModHardRock(), new TypeBeatModFlashlight());
        var serverStack = ServerMods(clientStack);

        double client = ClientPp.Compute(stars, notes, misses, accuracy, maxCombo, clientStack, typos);
        double server = ServerPp.Compute(stars, notes, misses, accuracy, maxCombo, serverStack, typos);

        // The bonus, computed the way both mirrors group it: the combo RATIO times the clamped
        // slope, and nothing about the mods.
        double bonus = (double)maxCombo / notes * Math.Max(0.0, 12.5 * (stars - 1.0)); // pp:const combo_bonus_slope=12.5 combo_bonus_zero=1.0
        double modMultiplier = ClientPp.ModMultiplier(clientStack, notes);

        Assert.Multiple(() =>
        {
            Assert.That(client, Is.EqualTo(server), "the two mirrors must agree on the placement");

            // The bare play, priced with no mods, is the same product plus the same bonus, so the
            // modded play is (bare - bonus) * modMult + bonus. If either mirror multiplied the
            // bonus in, this is off by (modMult - 1) * bonus, which is about 7.5 pp here.
            double bare = ClientPp.Compute(stars, notes, misses, accuracy, maxCombo, no_client_mods, typos);

            Assert.That(client, Is.EqualTo((bare - bonus) * modMultiplier + bonus).Within(1e-9));
            Assert.That(client, Is.Not.EqualTo(bare * modMultiplier).Within(1e-6),
                "the whole play must NOT scale with the mod stack; only its product half does");

            // And the mod multiplier really is above 1 on this stack, so the two spellings above
            // are genuinely different numbers rather than accidentally equal.
            Assert.That(modMultiplier, Is.GreaterThan(1.05));
            Assert.That(bonus, Is.GreaterThan(30));
        });
    }

    [Test]
    public void EveryPairOfRulesetModsPricesIdenticallyOnBothSides()
    {
        // Stacks, not just singles: the multiplier is a product, and a mod that was learned by one
        // side only would hide inside a single-mod test that happens to return 1.0 on both.
        var all = AllRulesetMods();

        for (int i = 0; i < all.Count; i++)
        for (int j = i + 1; j < all.Count; j++)
        {
            var clientStack = Stack(all[i], all[j]);
            var serverStack = ServerMods(clientStack);

            Assert.That(ClientPp.ModMultiplier(clientStack, 500), Is.EqualTo(ServerPp.ModMultiplier(serverStack, 500)),
                $"{all[i].Acronym} + {all[j].Acronym}");
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

    #endregion

    #region 4. Rate eligibility: same verdict, same rating

    [Test]
    public void RateEligibilityAgreesForEveryReachableRate()
    {
        const double base_stars = 4.2;
        const double dt_stars = 6.1;
        const double ht_stars = 3.4;

        // The CONVERTED map's three, deliberately all different from the plain ones so a Literate
        // stack that picked the wrong triple shows up as a wrong number rather than as a pass
        // (backlog 144). They are not the plain ones times any constant, because the real ones are
        // not either: see the storage note on ServerPp.StarsFor.
        const double lt_stars = 4.5;
        const double lt_dt_stars = 6.9;
        const double lt_ht_stars = 3.5;

        var literate = new ServerPp.LiterateStars(lt_stars, lt_dt_stars, lt_ht_stars);

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

        // Literate is ORTHOGONAL to the rate, so every case above is also a Literate case and the
        // pair has to reach the same verdict about the RATE while reading a different triple.
        foreach (var stack in cases.ToList())
            cases.Add(Stack([.. stack, new TypeBeatModLiterate()]));

        foreach (var clientStack in cases)
        {
            double? clientRate = ClientPp.EligibleRate(clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), base_stars, dt_stars, ht_stars, literate);

            string context = string.Join('+', clientStack.Select(m => m.Acronym + (m is ModRateAdjust r ? $"@{r.SpeedChange.Value:0.00}" : "")));

            // The client says WHICH RATE to price at; the server says WHICH STORED RATING prices it.
            // Those must be the same decision, expressed two ways. Since backlog 144 there are two
            // decisions, WHICH MAP and then WHICH RATE, and EligibleRate deliberately still answers
            // only the second: Literate does not touch rate eligibility at all.
            bool converted = ClientPp.IsLiterate(clientStack);

            double? expectedServerStars = clientRate switch
            {
                null => null,
                1.0 => converted ? lt_stars : base_stars,
                1.50 => converted ? lt_dt_stars : dt_stars,
                0.75 => converted ? lt_ht_stars : ht_stars,
                _ => throw new InvalidOperationException($"client returned an unexpected pp-eligible rate {clientRate} for {context}"),
            };

            Assert.That(serverStars.Stars, Is.EqualTo(expectedServerStars), context);
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
        });
    }

    #endregion

    #region 5. Star rating: the client's local rating IS the server's stored one

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
    /// The LITERATE-converted map's three ratings for a twin, i.e. what the server stores in
    /// <c>sr_literate</c> / <c>sr_literate_dt</c> / <c>sr_literate_ht</c> (029_literate_stars.sql).
    /// Every call that could see a Literate stack passes these, because a Literate play whose
    /// converted rating is not supplied is PENDING rather than priced, exactly as a Double Time
    /// play on a map without <c>sr_dt</c> is.
    /// </summary>
    private static ServerPp.LiterateStars ServerLiterate(IReadOnlyList<ServerLine> server)
        => new(ServerDifficulty.Compute(server, 1, literate: true),
               ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, literate: true),
               ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate, literate: true));

    /// <summary>
    /// A DENSE twin, rating past the 10 stars the difficulty model used to clamp at (backlog 118).
    /// <see cref="TwinMaps"/> rates a few stars at every rate, so it cannot tell the two ports apart
    /// anywhere a ceiling would act; this one rates about 19.7 at 1.00x and about 28.6 at 1.50x
    /// under the window/envelope model (backlog 273 at its 12.0 anchor), so both of its figures are
    /// past where the old
    /// ceiling sat, which is where the ports have to be held together for <c>sr_dt</c> to mean
    /// anything. Eight five-letter words to a 1.2 second line is well past what any human sustains,
    /// which is exactly why it reaches the region: the model prices pace against human capability
    /// and this fixture is roughly one and a half times it. It is also SUSTAINED, forty lines of it,
    /// so the envelope fills its range almost completely and the premise below has a wide margin
    /// rather than sitting on the line.
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
    /// A twin carrying FREESTYLE SLOTS, the cells backlog 211 started pricing at a quarter each.
    /// Neither fixture above has a single marker in it, so neither could tell the two ports apart on
    /// the quarter: a map with no slots rates bit-identically to what it rated before 211, which is
    /// the whole point of the weight being a cell COUNT, and it means the existing pins stayed green
    /// through the change on both sides. A divergence here is invisible everywhere except on a
    /// freestyle map's stored rating against the one song select draws for the same map.
    ///
    /// <para>Three shapes, because the weight enters three different ways: a word with slots at its
    /// END, a word with one in the MIDDLE (which must price the same as the first, the markers being
    /// a count and not characters of the stream text), and a token of NOTHING BUT slots, which used
    /// to be dropped from the map outright and is now a word of weight 1. The 72 words put the map
    /// over the 100-cell length pivot on both settings (282 priced cells with the markers, 240
    /// without), so the length accumulator's quarter is exercised as well as the per-word cost.</para>
    ///
    /// <para><paramref name="markers"/> false deletes every marker and IS the pre-211 number rather
    /// than an approximation of it: that code stripped markers before measuring, so a token of
    /// nothing but them became the empty token this builder emits, which both ports skip.</para>
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

    [Test]
    public void TheClientsLocalStarRatingIsTheRatingTheServerStores()
    {
        // This is the load-bearing claim behind pricing a play client-side at all: the game does
        // NOT fetch difficulty_rating / sr_dt / sr_ht, it recomputes them. The three rates below
        // are exactly the three the server stores per beatmap.
        var (client, server) = TwinMaps();

        Assert.Multiple(() =>
        {
            Assert.That(ClientDifficulty.Compute(client), Is.EqualTo(ServerDifficulty.Compute(server)), "difficulty_rating (1.00x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate)), "sr_dt (1.50x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.HALF_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate)), "sr_ht (0.75x)");

            Assert.That(ClientDifficulty.Compute(client), Is.GreaterThan(0), "the fixture must actually rate as something");
        });
    }

    [Test]
    public void TheTwoPortsAgreeAboveTheOldStarCeiling()
    {
        // The test above cannot see this region. Its fixture rates a few stars at every rate, so
        // the two ports would still agree there if one of them kept a ceiling and the other did
        // not, and a ceiling is exactly the thing this pair last disagreed about (backlog 118:
        // both copies of LyricDifficulty used to end in a flat clamp to 10, which never touched a
        // base rating but truncated sr_dt on any map dense enough at 1.50x). A divergence there is
        // invisible on the site and in song select and shows up only as a Double Time play priced
        // differently by the client and the server, which is the whole failure this suite exists
        // to catch. So the region is pinned with a fixture that actually reaches it.
        var (client, server) = DenseTwinMaps();

        Assert.Multiple(() =>
        {
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE), Is.GreaterThan(10),
                "the premise: this fixture must rate past where the old ceiling sat, or it pins nothing");

            Assert.That(ClientDifficulty.Compute(client), Is.EqualTo(ServerDifficulty.Compute(server)), "difficulty_rating (1.00x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate)), "sr_dt (1.50x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.HALF_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate)), "sr_ht (0.75x)");
        });
    }

    [Test]
    public void TheTwoPortsPriceAFreestyleSlotAtTheSameQuarter()
    {
        // Backlog 211. Every other fixture in this file is markerless, and a markerless map rates
        // bit-identically to what it rated before slots were priced at all, so none of them can see
        // this seam: both ports could have shipped a different weight, or one of them no weight at
        // all, without a single assertion above moving.
        var (client, server) = FreestyleTwinMaps();
        var (excludedClient, excludedServer) = FreestyleTwinMaps(markers: false);

        Assert.Multiple(() =>
        {
            // The premise, on both sides independently: the markers have to MOVE the rating, or the
            // fixture is just another markerless map and this test pins nothing. The comparison map
            // is the pre-211 number exactly (see the builder), so this is also the claim that the
            // change reached the client and the server rather than neither.
            Assert.That(ClientDifficulty.Compute(client), Is.GreaterThan(ClientDifficulty.Compute(excludedClient)), "client: the slots must cost something");
            Assert.That(ServerDifficulty.Compute(server), Is.GreaterThan(ServerDifficulty.Compute(excludedServer)), "server: the slots must cost something");

            // And the six ratings a beatmap row stores, on the map that has them.
            Assert.That(ClientDifficulty.Compute(client), Is.EqualTo(ServerDifficulty.Compute(server)), "difficulty_rating (1.00x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate)), "sr_dt (1.50x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.HALF_TIME_BASE_RATE),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate)), "sr_ht (0.75x)");
            Assert.That(ClientDifficulty.Compute(client, 1, literate: true),
                Is.EqualTo(ServerDifficulty.Compute(server, 1, literate: true)), "sr_literate (1.00x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE, literate: true),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, literate: true)), "sr_literate_dt (1.50x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.HALF_TIME_BASE_RATE, literate: true),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate, literate: true)), "sr_literate_ht (0.75x)");

            // The pre-211 map has to agree too, which is the other half of the mirror: the change
            // must have moved the marker map on both sides and left the markerless one alone.
            Assert.That(ClientDifficulty.Compute(excludedClient), Is.EqualTo(ServerDifficulty.Compute(excludedServer)), "and the markerless twin still agrees");
        });
    }

    [Test]
    public void TheTwoPortsAgreeOnWhatAFreestyleSlotIsWorthAndOnHowManyThereAre()
    {
        // The quarter itself, stated as the identity the game's own LyricDifficultyTest states and
        // proved HERE ACROSS THE REPOS: four slots weigh exactly one ordinary cell, so a map of
        // "a&&&&," words rates bit-identically to the same map written "ab,", on both ports. Neither
        // repo's copy of the weight can move without this failing, which is stronger than each
        // repo's own regression pin (both of those would still pass if both copies moved together
        // to, say, a half).
        var (freeClient, freeServer) = QuarterTwinMaps(freestyle: true);
        var (fullClient, fullServer) = QuarterTwinMaps(freestyle: false);

        Assert.Multiple(() =>
        {
            Assert.That(ClientDifficulty.Compute(freeClient), Is.EqualTo(ClientDifficulty.Compute(fullClient)), "client: four slots are one cell");
            Assert.That(ServerDifficulty.Compute(freeServer), Is.EqualTo(ServerDifficulty.Compute(fullServer)), "server: four slots are one cell");
            Assert.That(ClientDifficulty.Compute(freeClient), Is.EqualTo(ServerDifficulty.Compute(freeServer)), "and the two ports agree on the number");
        });
    }

    /// <summary>
    /// Two maps of the same WEIGHT written differently: <c>freestyle</c> true gives "a&amp;&amp;&amp;&amp;," words (one
    /// fixed key plus four quarters), false gives "ab," (two fixed keys). Uniform spans, so both cvs
    /// are 0; no repeated letter, so every run factor is 1; the same word indices, so the repetition
    /// factors match word for word; and 60 words, so both clear the 100-cell length pivot and the
    /// accumulator has to count the quarter as well.
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) QuarterTwinMaps(bool freestyle)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        const int word_count = 60;
        const int words_per_line = 6;
        const double step_ms = 400;
        const double span_ms = 350;

        var source = new List<(string Text, double Start, double End, (string Text, double Start, double End)[] Units)>();
        double t = 0;

        for (int i = 0; i < word_count; i += words_per_line)
        {
            var units = new (string Text, double Start, double End)[words_per_line];

            for (int w = 0; w < words_per_line; w++)
            {
                int index = i + w;
                char first = alphabet[index % alphabet.Length];
                string word = freestyle
                    ? first + new string(ServerTypeability.FREESTYLE_MARKER, 4) + ","
                    : first.ToString() + alphabet[(index + 1) % alphabet.Length] + ",";

                units[w] = (word, t + w * step_ms, t + w * step_ms + span_ms);
            }

            source.Add((string.Join(" ", units.Select(u => u.Text)), t, t + words_per_line * step_ms, units));
            t += words_per_line * step_ms;
        }

        return Twin([.. source]);
    }

    [Test]
    public void TheClientsStarsForIsTheServersStarsForOnTheSameMap()
    {
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double dtStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        foreach (var clientStack in new[]
                 {
                     no_client_mods,
                     Stack(new TypeBeatModLiterate()),
                     Stack(new TypeBeatModDoubleTime()),
                     Stack(new TypeBeatModNightcore()),
                     Stack(new TypeBeatModHalfTime()),
                     Stack(new TypeBeatModLiterate(), new TypeBeatModDoubleTime()),
                     Stack(new TypeBeatModLiterate(), new TypeBeatModHalfTime()),
                     Stack(At(new TypeBeatModDoubleTime(), 1.75)),
                     Stack(At(new TypeBeatModHalfTime(), 0.60)),
                 })
        {
            double? clientStars = ClientPp.StarsFor(client, clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), baseStars, dtStars, htStars, ServerLiterate(server));

            string context = string.Join('+', clientStack.Select(m => m.Acronym));

            // The rating is the WHOLE of what the two sides have to agree on since backlog 265: the
            // rate multiplier that used to ride beside it, derived by the client from the lines and
            // by the server from its stored columns, no longer exists on either side.
            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);
        }
    }

    [Test]
    public void TheClientsLiterateStarRatingIsTheRatingTheServerStores()
    {
        // The backlog-144 half of the claim above: since Literate is priced through the rating of
        // the map it CONVERTS, the client's converted rating has to be the server's sr_literate*
        // for the same three rates, or a Literate play's in-game pp readout disagrees with the
        // number on the leaderboard.
        var (client, server) = TwinMaps();

        double plain = ClientDifficulty.Compute(client);
        double converted = ClientDifficulty.Compute(client, 1, literate: true);

        Assert.Multiple(() =>
        {
            // THE PREMISE. Punctuation and case are what Literate adds, so a fixture where the two
            // streams coincide would pin nothing at all: every assertion below would pass on a port
            // that ignored the flag. Asserted rather than assumed, because the fixture text is a
            // string somebody could "tidy" later without realising what it is for.
            Assert.That(converted, Is.Not.EqualTo(plain), "the fixture must actually be punctuated");

            Assert.That(converted, Is.EqualTo(ServerDifficulty.Compute(server, 1, literate: true)), "sr_literate (1.00x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.DOUBLE_TIME_BASE_RATE, literate: true),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, literate: true)), "sr_literate_dt (1.50x)");
            Assert.That(ClientDifficulty.Compute(client, ClientPp.HALF_TIME_BASE_RATE, literate: true),
                Is.EqualTo(ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate, literate: true)), "sr_literate_ht (0.75x)");
        });
    }

    [Test]
    public void TheLiterateRatingIsNotTheRateRatingTimesAConstant()
    {
        // WHY THERE ARE SIX STORED COLUMNS AND NOT FOUR. The obvious saving is to store sr_literate
        // alone and recover the rate pair as sr_literate * (sr_dt / difficulty_rating), i.e. to
        // assume Literate and the rate compose multiplicatively. They do not, and under the
        // window/envelope model (backlog 273) the reason is that the two act on DIFFERENT AXES. The
        // rate compresses the timeline, which changes which scheduled windows still fit on the map
        // and therefore which window is the PEAK, i.e. the range every rating is a fraction of.
        // Literate adds cells to the words already there, changing every bin's density and so the
        // FILL relative to that peak, without moving a single boundary. Stars are the product of a
        // peak-scaled range and a fill, so neither change is a scalar on the other, and a map where
        // the rate drops a long window off the schedule is a map where the ratio is not even
        // continuous in it.
        //
        // This test is the standing proof of that, so that a future reader who reaches for the
        // saving finds the counter-example already written down rather than having to rediscover
        // it.
        var (_, server) = TwinMaps();

        double plainBase = ServerDifficulty.Compute(server);
        double plainDt = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double literateBase = ServerDifficulty.Compute(server, 1, literate: true);
        double literateDt = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate, literate: true);

        Assert.That(literateDt, Is.Not.EqualTo(literateBase * (plainDt / plainBase)).Within(1e-9),
            "if this ever holds, the rate ratio has become baseline-independent and the three "
            + "sr_literate* columns could collapse to one; until then they cannot");
    }

    [Test]
    public void ALiterateMapMissingItsConvertedRatingIsPendingRatherThanPricedOffThePlainOne()
    {
        // The same deferral rule as the Half Time test below, one level up. difficulty_rating has
        // been NOT NULL since 001, so a plain no-rate play can never be pending; sr_literate is
        // filled by the startup sweep and CAN be missing, and the answer then must be "not yet"
        // rather than "price it off the unconverted map", which would stamp a value the very next
        // sweep has to disagree with.
        var (_, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double dtStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        var full = ServerLiterate(server);

        (ServerPp.RateStars Stars, string Name)[] cases =
        [
            (ServerPp.StarsFor(ServerMods(Stack(new TypeBeatModLiterate())), baseStars, dtStars, htStars, default), "LT with no converted ratings at all"),
            (ServerPp.StarsFor(ServerMods(Stack(new TypeBeatModLiterate(), new TypeBeatModDoubleTime())), baseStars, dtStars, htStars,
                new ServerPp.LiterateStars(full.Base, null, full.HalfTime)), "LT+DT with sr_literate_dt missing"),
        ];

        // The case that FLIPS with backlog 265. LT+HT used to need sr_literate_dt as well, to
        // mirror against, so this triple was pending; the mirror is gone and it prices off
        // sr_literate_ht alone.
        var literateHalfTime = ServerPp.StarsFor(ServerMods(Stack(new TypeBeatModLiterate(), new TypeBeatModHalfTime())), baseStars, dtStars, htStars,
            new ServerPp.LiterateStars(full.Base, null, full.HalfTime));

        Assert.Multiple(() =>
        {
            foreach (var (stars, name) in cases)
            {
                Assert.That(stars.Stars, Is.Null, name);
                Assert.That(stars.Pending, Is.True, name + ": left stale for PpBackfill, not settled at a wrong price");
            }

            Assert.That(literateHalfTime.Stars, Is.EqualTo(full.HalfTime), "LT+HT prices off sr_literate_ht with no up-rate rating stored");
            Assert.That(literateHalfTime.Pending, Is.False);

            // And with the columns filled it prices, off the CONVERTED rating rather than the plain one.
            var priced = ServerPp.StarsFor(ServerMods(Stack(new TypeBeatModLiterate())), baseStars, dtStars, htStars, full);

            Assert.That(priced.Pending, Is.False);
            Assert.That(priced.Stars, Is.EqualTo(full.Base));
            Assert.That(priced.Stars, Is.Not.EqualTo(baseStars));
        });
    }

    [Test]
    public void AHalfTimeMapPricesOffItsDownRateRatingWhetherOrNotTheUpRateOneIsStored()
    {
        // Backlog 90 gave the server a data dependency the client did not have: pricing an HT play
        // needed sr_dt as well as sr_ht, so the server had to DEFER where the client simply computed
        // both, and this test asserted that deferral. Backlog 265 removes the mirror and with it the
        // dependency, so the two halves need exactly the same one rating again, and the rows that
        // were waiting on the other column price on the v20 sweep.
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        var clientStack = Stack(new TypeBeatModHalfTime());
        var halfTime = ServerMods(clientStack);

        var withoutDt = ServerPp.StarsFor(halfTime, baseStars, null, htStars, ServerLiterate(server));
        var withDt = ServerPp.StarsFor(halfTime, baseStars, ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate), htStars, ServerLiterate(server));

        Assert.Multiple(() =>
        {
            Assert.That(withoutDt.Stars, Is.EqualTo(htStars), "sr_dt is not consulted at all");
            Assert.That(withoutDt.Pending, Is.False);

            Assert.That(withDt.Stars, Is.EqualTo(htStars), "and storing it changes nothing");
            Assert.That(withDt.Pending, Is.False);

            // Which is the client's own answer for the same play, computed from the lines.
            Assert.That(ClientPp.StarsFor(client, clientStack), Is.EqualTo(htStars));
        });
    }

    #endregion

    #region 6. The whole pipeline, end to end

    [Test]
    public void TheWholePipelineAgreesForEveryPlayShapeThatMatters()
    {
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double dtStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

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

            var clientCounts = ClientPp.CountNotes(play);
            var serverCounts = ServerCounts(play);

            double? clientStars = ClientPp.StarsFor(client, clientStack);
            var serverStars = ServerPp.StarsFor(serverStack, baseStars, dtStars, htStars, ServerLiterate(server));

            // What the in-game counter would show (nothing at all when the play's rate makes it
            // ineligible, which is the same "no price exists" the server reports as a null). The
            // rating is the whole of the call since backlog 265: there is no second argument a
            // client surface could forget, which is what the extra argument here used to catch.
            double? clientPp = clientStars is double stars
                ? ClientPp.ForPlay(stars, clientCounts, accuracy, maxCombo, clientStack)
                : null;

            // ...against what the server would write to scores.pp for the very same play.
            var (serverPp, settled) = ServerPp.ForScore(
                ranked: true, serverStack, serverCounts, accuracy, maxCombo, baseStars, dtStars, htStars, ServerLiterate(server));

            string context = $"mods=[{string.Join('+', clientStack.Select(m => m.Acronym))}] " +
                             $"play={string.Join(',', play.Select(kv => $"{kv.Key}:{kv.Value}"))} acc={accuracy} combo={maxCombo}";

            Assert.That(clientCounts.Notes, Is.EqualTo(serverCounts.Notes), context);
            Assert.That(clientCounts.Misses, Is.EqualTo(serverCounts.Misses), context);
            Assert.That(clientCounts.Typos, Is.EqualTo(serverCounts.Typos), context);
            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);
            Assert.That(settled, Is.True, context);
            Assert.That(clientPp, Is.EqualTo(serverPp), context);
        }
    }

    [Test]
    public void AHalfTimePlayCarriesNoPenaltyOnEitherSideAndPricesAtItsPlainRatingRatio()
    {
        // The pipeline test above proves the two agree on an HT play; this proves what they agree
        // ON. It used to prove the backlog-90 mirror penalty was actually being applied rather than
        // both sides having dropped it together; since backlog 265 it proves the exact opposite,
        // that BOTH sides have dropped it, which is a claim of the same shape and needs pinning for
        // the same reason: a half-landed removal would leave the two agreeing on nothing.
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double dtStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        // 400 notes, twelve of them missed, fifteen typos. The counts moved down from 20 and 74
        // for backlog 97: on a 400-note map the miss cliff was sqrt(400) = 20 exactly and the
        // typo cliff 20.5, so that fixture priced to zero on every rate at once and the mirror it
        // exists to check could not be read off the ratios at all. Backlog 101 moves the two cliffs
        // out to 147.4 and 209.2, so these counts are now far clear of both; they are left where 97
        // put them because this test is about the RATE factors and any priced play will do.
        var play = new Dictionary<HitResult, int>
        {
            [HitResult.Great] = 308, [HitResult.Ok] = 60, [HitResult.Meh] = 20, [HitResult.Miss] = 12, [HitResult.ComboBreak] = 15,
        };

        double Price(IReadOnlyList<Mod> stack)
        {
            var (pp, settled) = ServerPp.ForScore(true, ServerMods(stack), ServerCounts(play), 0.93, 380, baseStars, dtStars, htStars, ServerLiterate(server));

            Assert.That(settled, Is.True);
            return pp!.Value;
        }

        var halfTime = Stack(new TypeBeatModHalfTime());

        double nomod = Price(no_client_mods);
        double doubleTime = Price(Stack(new TypeBeatModDoubleTime()));
        double halfTimePrice = Price(halfTime);

        // The client's own reading of the very same play.
        double clientHalfTime = ClientPp.ForPlay(
            ClientPp.StarsFor(client, halfTime)!.Value, ClientPp.CountNotes(play), 0.93, 380, halfTime);

        // What Half Time is worth priced off sr_ht and nothing else, which from v3 to v19 was the
        // UNPENALISED value the mirror multiplied down and is now the value itself.
        double unpenalised = ClientPp.ForPlay(ClientPp.StarsFor(client, halfTime)!.Value, ClientPp.CountNotes(play), 0.93, 380, halfTime);

        Assert.Multiple(() =>
        {
            Assert.That(clientHalfTime, Is.EqualTo(halfTimePrice), "the two halves price the play identically");
            Assert.That(halfTimePrice, Is.EqualTo(unpenalised), "and it is the unpenalised value exactly, on both sides");

            // The rate factor of a Half Time play is the PLAIN rating ratio now, not the reciprocal
            // of Double Time's. Both are stated, because the second is what this pinned before and
            // its failure is the whole point: the two are not equal on this fixture map.
            //
            // THE LAW IS ABOUT THE PRODUCT TERM (backlog 270). The combo bonus is ADDED after the
            // product and is itself a function of SR_eff, so it neither cancels in a ratio nor
            // scales like one: the three arms of this test sit at three different ratings, so each
            // carries a DIFFERENT bonus. Each is taken off before the division, which leaves
            // exactly the (SR_rate/SR_base)^sr_exponent this pins.
            double ComboBonus(double stars) => 380.0 / 400.0 * Math.Max(0.0, 12.5 * (stars - 1.0)); // pp:const combo_bonus_slope=12.5 combo_bonus_zero=1.0

            double baseProduct = nomod - ComboBonus(baseStars);
            double up = (doubleTime - ComboBonus(dtStars)) / baseProduct;
            double down = (halfTimePrice - ComboBonus(htStars)) / baseProduct;

            double downRatio = Math.Pow(htStars / baseStars, 2.00); // pp:const sr_exponent=2.00

            Assert.That(down, Is.EqualTo(downRatio).Within(1e-9), "HT's rate factor is (sr_ht/sr_base)^sr_exponent");
            Assert.That(down, Is.Not.EqualTo(1.0 / up).Within(1e-6),
                "and deliberately NOT Double Time's reciprocal any more, which is the asymmetry backlog 265 accepts");
        });
    }

    [Test]
    public void APlayThatEarnsNothingEarnsNothingOnBothSides()
    {
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        var counts = new Dictionary<HitResult, int> { [HitResult.Great] = 400 };
        var customRate = Stack(At(new TypeBeatModDoubleTime(), 1.75));

        var (serverPp, _) = ServerPp.ForScore(true, ServerMods(customRate), ServerCounts(counts), 0.9, 400, baseStars,
            ServerDifficulty.Compute(server, 1.50), ServerDifficulty.Compute(server, 0.75), ServerLiterate(server));

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.StarsFor(client, customRate), Is.Null, "the client shows no number at all");
            Assert.That(serverPp, Is.Null, "and the server prices nothing at all, which is not a price of zero");
        });
    }

    #endregion
}
