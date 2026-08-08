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

    /// <summary>Every mod the type!beat ruleset ships, as the mod panel offers them.</summary>
    private static IReadOnlyList<Mod> AllRulesetMods() =>
    [
        new TypeBeatModNoFail(),
        new TypeBeatModHalfTime(),
        new TypeBeatModSuddenDeath(),
        new TypeBeatModDoubleTime(),
        new TypeBeatModNightcore(),
        new TypeBeatModFlashlight(),
        new TypeBeatModLiterate(),
        new TypeBeatModFletcher(),
        new TypeBeatModMashing(),
        new TypeBeatModMuted(),
    ];

    #endregion

    #region 1. The formula itself

    [Test]
    public void TheTwoFormulasAgreeExactlyOverASpreadOfPlays()
    {
        double[] stars = [0.5, 1.0, 2.75, 4.0, 6.3, 9.99];
        int[] noteCounts = [1, 5, 47, 100, 500, 2137];
        double[] accuracies = [0.0, 0.42, 0.6931, 0.9, 1.0];

        int compared = 0;

        foreach (double sr in stars)
        foreach (int notes in noteCounts)
        foreach (double accuracy in accuracies)
        foreach (int misses in new[] { 0, 1, notes / 3, notes })
        foreach (int maxCombo in new[] { 0, notes / 2, notes })
        foreach (int mistypes in new[] { 0, 1, 137, notes * 3 })
        {
            double client = ClientPp.Compute(sr, notes, misses, accuracy, maxCombo, no_client_mods, mistypes);
            double server = ServerPp.Compute(sr, notes, misses, accuracy, maxCombo, no_server_mods, mistypes);

            Assert.That(client, Is.EqualTo(server),
                $"sr={sr} notes={notes} miss={misses} acc={accuracy} combo={maxCombo} mistypes={mistypes}");
            compared++;
        }

        Assert.That(compared, Is.GreaterThan(1000), "the spread must actually be a spread");
    }

    [Test]
    public void TheTwoSplitPenaltyTermsAgreeExactlyIncludingTheDecidedWorkedExamples()
    {
        // Backlog 89 split one penalty term into two, which is a fresh seam: the miss term and the
        // mistyping term could now drift apart INDEPENDENTLY, and a spread that only ever moves both
        // at once could miss it. Each case below holds one of the two counts fixed while the other
        // moves, and the two headline cases the rebalance was decided on are stated as exact values
        // so this file also pins WHAT the split is worth, not merely that both halves agree.
        //
        // Nothing else in the formula reads either count, so pp divided by the same play with
        // neither is exactly
        // max(0, 1 - miss^1.2/notes)^10 * max(0, 1 - mistypes^1.2/(notes+mistypes))^6.
        //
        // Backlog 97 put a CLAMP in both terms, which is a fresh seam of its own: a mirror that
        // clamped and one that did not would agree on every play under the cliff and disagree on
        // every play past it. Backlog 101 then moved both cliffs a long way out, from 23 misses to
        // 178 and from 23 mistypes to 249, which makes the seam WIDER rather than narrower: a mirror
        // stuck at the old power would clamp on nearly every case a straddle-the-old-cliff spread
        // used, and agree everywhere else. The spread below therefore straddles the NEW cliffs
        // deliberately, and keeps the old thresholds too, where the two powers now disagree the most.
        //
        // A SECOND SEAM THE FRACTIONAL POWER OPENS: Math.Pow(x, 1.2) is not the exactly-rounded
        // product Math.Pow(x, 2) effectively is, so the two mirrors agreeing here is a claim about
        // both calling the same Math.Pow on the same double, which is exactly what EXACT equality
        // (no tolerance) below pins.
        const int notes = 500;

        double clientSpotless = ClientPp.Compute(4, notes, 0, 0.9, notes, no_client_mods, 0);
        double serverSpotless = ServerPp.Compute(4, notes, 0, 0.9, notes, no_server_mods, 0);

        Assert.That(clientSpotless, Is.EqualTo(serverSpotless), "the spotless baseline itself must agree");

        foreach ((int misses, int mistypes) in new[]
                 {
                     (60, 80),   // the first decided example, a live number again since backlog 101
                     (10, 20),   // the second: 0.96830^10 * 0.92998^6, the headline figure
                     (0, 0), (0, 1), (0, 22), (0, 23), (0, 80),              // mistypes alone,
                     (0, 248), (0, 249), (0, 5000),                          // over the new cliff
                     (1, 0), (22, 0), (23, 0), (60, 0),                      // misses alone,
                     (177, 0), (178, 0), (250, 0), (500, 0),                 // over the new cliff
                     (177, 248), (178, 249),               // either side of both cliffs at once
                     (500, 5000),                          // both at once, at the extreme
                 })
        {
            double client = ClientPp.Compute(4, notes, misses, 0.9, notes, no_client_mods, mistypes);
            double server = ServerPp.Compute(4, notes, misses, 0.9, notes, no_server_mods, mistypes);

            Assert.That(client, Is.EqualTo(server), $"miss={misses} mistypes={mistypes}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.Compute(4, notes, 60, 0.9, notes, no_client_mods, 80) / clientSpotless,
                Is.EqualTo(0.000000).Within(1e-6)); // pp[f.penalty(500, 60, 80)]
            Assert.That(ClientPp.Compute(4, notes, 10, 0.9, notes, no_client_mods, 20) / clientSpotless,
                Is.EqualTo(0.052744).Within(1e-6)); // pp[f.penalty(500, 10, 20)]

            // Zero mistypes leaves the mistyping term at exactly 1.0 on both sides, so the play is
            // priced by its misses alone. Ten misses, not the sixty this used to use: sixty was past
            // the backlog-97 cliff, so both sides would have been asserted to equal zero and the
            // restatement would have stopped saying anything about the arithmetic that produced it.
            Assert.That(ClientPp.Compute(4, notes, 10, 0.9, notes, no_client_mods, 0) / clientSpotless,
                Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.6) / 500.0), 10)).Within(1e-12)); // pp:const count_power=1.6 miss_exponent=10
            Assert.That(ServerPp.Compute(4, notes, 10, 0.9, notes, no_server_mods, 0) / serverSpotless,
                Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.6) / 500.0), 10)).Within(1e-12)); // pp:const count_power=1.6 miss_exponent=10

            // And BOTH sides reach the clamped zero from the same input, which is the seam the
            // clamp itself opens: a mirror missing the Math.Max would produce a non-real result
            // here rather than a zero, and nothing else in this file would catch it. The thresholds
            // are the CURRENT cliffs, so a mirror at the old power would fail the two below the
            // cliff rather than the two at it.
            const int missCliff = 49; // pp[math.ceil(f.miss_cliff(500))]
            const int mistypeCliff = 52; // pp[math.ceil(f.mistype_cliff(500))]

            Assert.That(ClientPp.Compute(4, notes, missCliff, 0.9, notes, no_client_mods, 0), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, missCliff, 0.9, notes, no_server_mods, 0), Is.Zero);
            Assert.That(ClientPp.Compute(4, notes, 0, 0.9, notes, no_client_mods, mistypeCliff), Is.Zero);
            Assert.That(ServerPp.Compute(4, notes, 0, 0.9, notes, no_server_mods, mistypeCliff), Is.Zero);

            Assert.That(ClientPp.Compute(4, notes, missCliff - 1, 0.9, notes, no_client_mods, 0), Is.GreaterThan(0));
            Assert.That(ServerPp.Compute(4, notes, missCliff - 1, 0.9, notes, no_server_mods, 0), Is.GreaterThan(0));
            Assert.That(ClientPp.Compute(4, notes, 0, 0.9, notes, no_client_mods, mistypeCliff - 1), Is.GreaterThan(0));
            Assert.That(ServerPp.Compute(4, notes, 0, 0.9, notes, no_server_mods, mistypeCliff - 1), Is.GreaterThan(0));
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
        foreach (int mistypes in new[] { -1, 0, int.MaxValue })
        {
            double client = ClientPp.Compute(sr, notes, misses, accuracy, maxCombo, no_client_mods, mistypes);
            double server = ServerPp.Compute(sr, notes, misses, accuracy, maxCombo, no_server_mods, mistypes);

            string context = $"sr={sr} notes={notes} miss={misses} acc={accuracy} combo={maxCombo} mistypes={mistypes}";

            Assert.That(client, Is.EqualTo(server), context);
            Assert.That(double.IsFinite(client), Is.True, context);
            Assert.That(client, Is.GreaterThanOrEqualTo(0), context);
        }
    }

    [Test]
    public void TheTwoLengthAndFlashlightCurvesAgree()
    {
        foreach (int notes in new[] { -1, 0, 1, 3, 4, 5, 6, 45, 46, 47, 100, 500, 1000, 12345 })
        {
            Assert.That(ClientPp.LengthBonus(notes), Is.EqualTo(ServerPp.LengthBonus(notes)), $"length at {notes}");
            Assert.That(ClientPp.FlashlightMultiplier(notes), Is.EqualTo(ServerPp.FlashlightMultiplier(notes)), $"flashlight at {notes}");
        }
    }

    [Test]
    public void TheTwoHalfTimeMirrorsAgreeExactlyOnBothBranches()
    {
        // Backlog 90 gave Half Time an extra multiplier that neither side can derive from the one
        // rating it prices with: it needs all three. That is a fresh seam, and the branch it takes
        // is decided by a comparison against 1.0, so a spread that only ever lands on one side of
        // that comparison could miss a divergence entirely.
        double[] ratings = [0, -1, 1e-9, 0.5, 1, 2, 3.4, 4.0, 4.2, 4.5, 6.1, 10, 1e9, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

        int compared = 0;

        foreach (double baseStars in ratings)
        foreach (double dt in ratings)
        foreach (double ht in ratings)
        {
            double client = ClientPp.HalfTimeMultiplier(baseStars, dt, ht);
            double server = ServerPp.HalfTimeMultiplier(baseStars, dt, ht);

            string context = $"base={baseStars} dt={dt} ht={ht}";

            Assert.That(client, Is.EqualTo(server), context);
            Assert.That(double.IsFinite(client), Is.True, context);
            Assert.That(client, Is.GreaterThanOrEqualTo(0), context);
            compared++;
        }

        Assert.That(compared, Is.GreaterThan(1000), "the spread must actually be a spread");

        Assert.Multiple(() =>
        {
            // The three decided cases, stated as exact values so this file pins WHAT the mirror is
            // worth and not merely that the two halves agree.

            // The mirror branch, on the fixture spread used further down this file.
            Assert.That(ClientPp.HalfTimeMultiplier(4.2, 6.1, 3.4), Is.EqualTo(0.656438).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4)]
            Assert.That(ServerPp.HalfTimeMultiplier(4.2, 6.1, 3.4), Is.EqualTo(0.656438).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4)]

            // The CLAMPED branch: sr_dt · sr_ht < sr_base², so an unguarded mirror would BUFF Half
            // Time on this map. Both sides must take the flat cut, not the mirror.
            Assert.That(ClientPp.HalfTimeMultiplier(4.2, 4.5, 2.0), Is.EqualTo(0.70).Within(1e-12)); // pp[f.half_time_buff_clamp]
            Assert.That(ServerPp.HalfTimeMultiplier(4.2, 4.5, 2.0), Is.EqualTo(0.70).Within(1e-12)); // pp[f.half_time_buff_clamp]

            // A mild mirror, strictly between the clamp and 1.0: used AS IS on both sides. A
            // Math.Min on either side would return 0.70 here and the two would still agree with
            // each other, which is why the value itself is pinned as well as the parity.
            Assert.That(ClientPp.HalfTimeMultiplier(4.0, 4.5, 3.7), Is.EqualTo(0.901644).Within(1e-6)); // pp[f.half_time_multiplier(4.0, 4.5, 3.7)]
            Assert.That(ServerPp.HalfTimeMultiplier(4.0, 4.5, 3.7), Is.EqualTo(0.901644).Within(1e-6)); // pp[f.half_time_multiplier(4.0, 4.5, 3.7)]
        });
    }

    [Test]
    public void TheTwoFormulasAreTheSameGeneration()
    {
        // A client shipped against generation N must not quietly price plays the server stores at
        // generation N+1. Bumping one without the other is exactly what this catches.
        Assert.That(ClientPp.VERSION, Is.EqualTo(ServerPp.VERSION));
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
            // The MISTYPE stat: priced by its own term, never a note.
            [HitResult.ComboBreak] = 137,
        };

        var client = ClientPp.CountNotes(statistics);
        var server = ServerCounts(statistics);

        Assert.Multiple(() =>
        {
            Assert.That(client.Notes, Is.EqualTo(server.Notes));
            Assert.That(client.Misses, Is.EqualTo(server.Misses));
            Assert.That(client.Mistypes, Is.EqualTo(server.Mistypes));

            Assert.That(client.Notes, Is.EqualTo(400));
            Assert.That(client.Misses, Is.EqualTo(50));
            Assert.That(client.Mistypes, Is.EqualTo(137));
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

        // And the mistype key, which is a note on NEITHER side.
        var mistyped = new Dictionary<HitResult, int> { [ClientPp.MISTYPE_RESULT] = 9 };

        Assert.Multiple(() =>
        {
            Assert.That(ServerCounts(mistyped).Notes, Is.Zero);
            Assert.That(ServerCounts(mistyped).Mistypes, Is.EqualTo(9));
            Assert.That(ClientPp.CountNotes(mistyped).Notes, Is.Zero);
            Assert.That(ClientPp.CountNotes(mistyped).Mistypes, Is.EqualTo(9));

            // The literal the server greps for, spelled by the client's own enum.
            Assert.That(JsonConvert.SerializeObject(mistyped), Does.Contain("combo_break"));
        });
    }

    [Test]
    public void AScoreCarryingNoMistypeKeyReadsAsZeroOnBothSides()
    {
        // Every score submitted before the stat existed omits the key entirely, and must price
        // exactly as it always did.
        var old = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.Miss] = 10 };

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.CountNotes(old).Mistypes, Is.Zero);
            Assert.That(ServerCounts(old).Mistypes, Is.Zero);
            Assert.That(ClientPp.Compute(4, 100, 10, 0.9, 90, no_client_mods, ClientPp.CountNotes(old).Mistypes),
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

        var cases = new List<IReadOnlyList<Mod>>
        {
            no_client_mods,
            Stack(new TypeBeatModLiterate()),
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

        foreach (var clientStack in cases)
        {
            double? clientRate = ClientPp.EligibleRate(clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), base_stars, dt_stars, ht_stars);

            string context = string.Join('+', clientStack.Select(m => m.Acronym + (m is ModRateAdjust r ? $"@{r.SpeedChange.Value:0.00}" : "")));

            // The client says WHICH RATE to price at; the server says WHICH STORED RATING prices it.
            // Those must be the same decision, expressed two ways.
            double? expectedServerStars = clientRate switch
            {
                null => null,
                1.0 => base_stars,
                1.50 => dt_stars,
                0.75 => ht_stars,
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
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) TwinMaps()
    {
        (string Text, double Start, double End, (string Text, double Start, double End)[] Units)[] source =
        [
            ("hello there world", 1000, 4000,
                [("hello", 1000, 2000), ("there", 2000, 3000), ("world", 3000, 4000)]),
            ("typing is a rhythm not a race", 4000, 8000,
                [("typing", 4000, 4800), ("is", 4800, 5100), ("a", 5100, 5300), ("rhythm", 5300, 6400), ("not", 6400, 6900), ("a", 6900, 7100), ("race", 7100, 8000)]),
            ("world world world", 8000, 9000,
                [("world", 8000, 8300), ("world", 8300, 8600), ("world", 8600, 9000)]),
            ("after a long instrumental rest", 30000, 35000,
                [("after", 30000, 31000), ("a", 31000, 31300), ("long", 31300, 32200), ("instrumental", 32200, 34000), ("rest", 34000, 35000)]),
        ];

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
                     Stack(At(new TypeBeatModDoubleTime(), 1.75)),
                     Stack(At(new TypeBeatModHalfTime(), 0.60)),
                 })
        {
            double? clientStars = ClientPp.StarsFor(client, clientStack);
            var serverStars = ServerPp.StarsFor(ServerMods(clientStack), baseStars, dtStars, htStars);

            string context = string.Join('+', clientStack.Select(m => m.Acronym));

            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);

            // And the rate MULTIPLIER that goes with the rating (backlog 90). The client derives it
            // from the lines; the server from its three stored columns. Same number, both ways.
            Assert.That(ClientPp.RateMultiplier(client, clientStack), Is.EqualTo(serverStars.Multiplier), context);
        }
    }

    [Test]
    public void AHalfTimeMapMissingItsUpRateRatingIsPendingRatherThanPricedOffTheDownRateAlone()
    {
        // Backlog 90 gave the server a new data dependency the client does not have: pricing an HT
        // play needs sr_dt as well as sr_ht, and the client simply computes both. The server must
        // therefore DEFER rather than price off sr_ht alone, or every HT play on a map the SR sweep
        // has not fully reached would be stamped at a value the next sweep has to disagree with.
        var (_, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        var halfTime = ServerMods(Stack(new TypeBeatModHalfTime()));

        var withoutDt = ServerPp.StarsFor(halfTime, baseStars, null, htStars);
        var withDt = ServerPp.StarsFor(halfTime, baseStars, ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate), htStars);

        Assert.Multiple(() =>
        {
            Assert.That(withoutDt.Stars, Is.Null);
            Assert.That(withoutDt.Pending, Is.True, "left stale for PpBackfill, not settled at a wrong price");

            Assert.That(withDt.Stars, Is.EqualTo(htStars), "and with both columns filled it prices normally");
            Assert.That(withDt.Pending, Is.False);
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
            // A play from before the mistype stat existed: no combo_break key at all.
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
            var serverStars = ServerPp.StarsFor(serverStack, baseStars, dtStars, htStars);

            // What the in-game counter would show (nothing at all when the play's rate makes it
            // ineligible, which is the same "no price exists" the server reports as a null). The
            // rate multiplier is passed exactly as the client's own surfaces pass it: omitting it
            // here would price every Half Time stack below WITHOUT the backlog-90 penalty, and this
            // assertion is what catches a client surface that forgets to.
            double? clientPp = clientStars is double stars
                ? ClientPp.ForPlay(stars, clientCounts, accuracy, maxCombo, clientStack, ClientPp.RateMultiplier(client, clientStack))
                : null;

            // ...against what the server would write to scores.pp for the very same play.
            var (serverPp, settled) = ServerPp.ForScore(
                ranked: true, serverStack, serverCounts, accuracy, maxCombo, baseStars, dtStars, htStars);

            string context = $"mods=[{string.Join('+', clientStack.Select(m => m.Acronym))}] " +
                             $"play={string.Join(',', play.Select(kv => $"{kv.Key}:{kv.Value}"))} acc={accuracy} combo={maxCombo}";

            Assert.That(clientCounts.Notes, Is.EqualTo(serverCounts.Notes), context);
            Assert.That(clientCounts.Misses, Is.EqualTo(serverCounts.Misses), context);
            Assert.That(clientCounts.Mistypes, Is.EqualTo(serverCounts.Mistypes), context);
            Assert.That(clientStars, Is.EqualTo(serverStars.Stars), context);
            Assert.That(settled, Is.True, context);
            Assert.That(clientPp, Is.EqualTo(serverPp), context);
        }
    }

    [Test]
    public void AHalfTimePlayCarriesTheMirrorPenaltyOnBothSidesAndIsDoubleTimesReciprocal()
    {
        // The pipeline test above proves the two agree on an HT play; this proves what they agree
        // ON, i.e. that the penalty is actually being applied rather than both sides having dropped
        // it together.
        var (client, server) = TwinMaps();

        double baseStars = ServerDifficulty.Compute(server);
        double dtStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.DoubleTimeBaseRate);
        double htStars = ServerDifficulty.Compute(server, Typebeat.Web.Scoring.RateMods.HalfTimeBaseRate);

        // 400 notes, twelve of them missed, fifteen mistypes. The counts moved down from 20 and 74
        // for backlog 97: on a 400-note map the miss cliff was sqrt(400) = 20 exactly and the
        // mistype cliff 20.5, so that fixture priced to zero on every rate at once and the mirror it
        // exists to check could not be read off the ratios at all. Backlog 101 moves the two cliffs
        // out to 147.4 and 209.2, so these counts are now far clear of both; they are left where 97
        // put them because this test is about the RATE factors and any priced play will do.
        var play = new Dictionary<HitResult, int>
        {
            [HitResult.Great] = 308, [HitResult.Ok] = 60, [HitResult.Meh] = 20, [HitResult.Miss] = 12, [HitResult.ComboBreak] = 15,
        };

        double Price(IReadOnlyList<Mod> stack)
        {
            var (pp, settled) = ServerPp.ForScore(true, ServerMods(stack), ServerCounts(play), 0.93, 380, baseStars, dtStars, htStars);

            Assert.That(settled, Is.True);
            return pp!.Value;
        }

        var halfTime = Stack(new TypeBeatModHalfTime());

        double nomod = Price(no_client_mods);
        double doubleTime = Price(Stack(new TypeBeatModDoubleTime()));
        double halfTimePrice = Price(halfTime);

        // The client's own reading of the very same play, penalty and all.
        double clientHalfTime = ClientPp.ForPlay(
            ClientPp.StarsFor(client, halfTime)!.Value, ClientPp.CountNotes(play), 0.93, 380, halfTime,
            ClientPp.RateMultiplier(client, halfTime));

        // What Half Time used to be worth: sr_ht alone, with no multiplier.
        double unpenalised = ClientPp.ForPlay(ClientPp.StarsFor(client, halfTime)!.Value, ClientPp.CountNotes(play), 0.93, 380, halfTime);

        Assert.Multiple(() =>
        {
            Assert.That(clientHalfTime, Is.EqualTo(halfTimePrice), "the two halves price the penalised play identically");
            Assert.That(halfTimePrice, Is.LessThan(unpenalised), "and the penalty is genuinely being applied");

            // Equal and opposite: HT's rate factor is exactly the reciprocal of DT's whenever the
            // mirror (rather than the flat clamp) is in force, which is the term's whole purpose.
            double up = doubleTime / nomod;
            double down = halfTimePrice / nomod;

            double mirror = 1.0 / (Math.Pow(dtStars / baseStars, 2.60) * Math.Pow(htStars / baseStars, 2.60)); // pp:const sr_exponent=2.60*2

            Assert.That(mirror, Is.LessThan(1.0), "the premise: this fixture map is not a concave one");
            Assert.That(ClientPp.HalfTimeMultiplier(baseStars, dtStars, htStars), Is.EqualTo(mirror).Within(1e-12),
                "so the mirror is used here, not the flat clamp");
            Assert.That(down, Is.EqualTo(1.0 / up).Within(1e-9));
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
            ServerDifficulty.Compute(server, 1.50), ServerDifficulty.Compute(server, 0.75));

        Assert.Multiple(() =>
        {
            Assert.That(ClientPp.StarsFor(client, customRate), Is.Null, "the client shows no number at all");
            Assert.That(serverPp, Is.Null, "and the server prices nothing at all, which is not a price of zero");
        });
    }

    #endregion
}
