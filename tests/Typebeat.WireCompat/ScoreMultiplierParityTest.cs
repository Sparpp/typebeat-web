using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

using ServerContract = Typebeat.Web.Scoring.ScoringContract;
using ServerLocalOnly = Typebeat.Web.Scoring.LocalOnlyMods;
using ServerMultiplier = Typebeat.Web.Scoring.ModMultiplier;
using ServerRateMods = Typebeat.Web.Scoring.RateMods;
using ServerSubmission = Typebeat.Web.Scoring.ScoreSubmission;
using ServerUnranked = Typebeat.Web.Scoring.UnrankedMods;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for the SCORE-SIDE mod multiplier, the ranked verdicts and the statistics allowlist.
///
/// <para>
/// The server's <see cref="ServerMultiplier"/> is a hand transcription of the game's
/// <see cref="TypeBeatScoreMultiplierCalculator"/>, and it decides whether an honest submitted total
/// is in bounds: a value bumped on the client alone would clamp and unrank every honest play with
/// that mod. The pp-side multiplier has its own pin (<c>PerformancePointsParityTest</c>); this is
/// the score side's. It also holds the server's ranked verdict per acronym
/// (<see cref="ServerSubmission.ModRanked"/>, which reads <see cref="ServerUnranked"/> and requires
/// the acronym to be one <see cref="ServerMultiplier.IsKnown"/> answers for) against each mod's own
/// <c>Ranked</c>, and the statistics allowlist against the game's <see cref="HitResult"/> names.
/// </para>
///
/// <para>
/// Every mod is read off <see cref="TypeBeatRuleset"/>'s own <c>CreateAllMods</c>, the System column
/// included, so a mod added to the ruleset is swept here without anyone remembering to list it.
/// </para>
///
/// <para>
/// Three deliberate differences are stated rather than swept, each with its reason:
/// <list type="bullet">
/// <item>Autoplay (<c>AT</c>) is never submitted by the stock client (an autoplay run takes no
/// score token), so the server does not price it and does not know it; a mod local only on every
/// map would be refused outright by <see cref="ServerLocalOnly"/> and sit beside it.
/// Polyglot (<c>PG</c>) is local only on a Chinese or Korean map alone since PR 43
/// (<c>Mod.IsLocalOnlyFor</c>) and ranked at 1.0x everywhere else, so it IS priced and known;
/// its per-language verdict has a pin of its own.</item>
/// <item>Classic (<c>CL</c>) is a mark the SERVER appends to a stored score; the client never
/// prices a live score with it, so its calculator leaves it at 1.0 while the server bounds a
/// marked row at 0.95.</item>
/// <item>Wind Up and Wind Down are priced on a rate ramp whose endpoints the server does not
/// store, so the server allows the most any ramp could pay, which is at or above the client's
/// price. Both are unranked on both sides.</item>
/// </list>
/// </para>
/// </summary>
[TestFixture]
public class ScoreMultiplierParityTest
{
    private static TypeBeatScoreMultiplierCalculator calculator() => new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

    private static IReadOnlyList<Mod> allMods() => new TypeBeatRuleset().CreateAllMods().ToList();

    /// <summary>The two mods the stock client never submits a score with (see the class summary).</summary>
    private static bool neverSubmitted(Mod mod) => mod.LocalOnly || mod is ModAutoplay;

    [Test]
    public void EveryAcronymTheGameCanSubmitIsKnownServerSide()
    {
        var mods = allMods();

        Assert.That(mods, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var mod in mods.Where(m => !neverSubmitted(m)))
                Assert.That(ServerMultiplier.IsKnown(mod.Acronym), Is.True, $"{mod.Acronym} ({mod.Name}) must be priced by the server, or its plays store unranked");

            foreach (var mod in mods.Where(neverSubmitted))
                Assert.That(ServerMultiplier.IsKnown(mod.Acronym), Is.False, $"{mod.Acronym} is never submitted and is not priced server-side");
        });
    }

    [Test]
    public void TheRankedVerdictsAgree()
    {
        var mods = allMods();

        Assert.Multiple(() =>
        {
            foreach (var mod in mods)
            {
                Assert.That(ServerSubmission.ModRanked(mod.Acronym), Is.EqualTo(mod.Ranked), $"{mod.Acronym} ({mod.Name}) at its default configuration");
                Assert.That(ServerLocalOnly.IsLocalOnly(mod.Acronym), Is.EqualTo(mod.LocalOnly), $"{mod.Acronym} local-only");
            }

            // The deny list names only real, unranked mods of this ruleset.
            foreach (string acronym in ServerUnranked.ACRONYMS)
            {
                var mod = mods.SingleOrDefault(m => string.Equals(m.Acronym, acronym, StringComparison.OrdinalIgnoreCase));

                Assert.That(mod, Is.Not.Null, $"{acronym} is on the server's unranked list but not in the ruleset");
                Assert.That(mod?.Ranked, Is.False, $"{acronym} is on the server's unranked list but ranks in the game");
            }
        });
    }

    [Test]
    public void PolyglotIsLocalOnlyAndRankedOnTheSameLanguagesOnBothSides()
    {
        // PR 43: the verdict is per MAP now (Mod.IsLocalOnlyFor / IsRankedFor read the beatmap's
        // metadata language), so the per-acronym pins above cannot see it. Every language the game
        // knows, unspecified included, against the server's rule on the stored canonical name.
        var polyglot = new TypeBeatModPolyglot();
        int localOnly = 0;

        Assert.Multiple(() =>
        {
            foreach (var language in Enum.GetValues<BeatmapLanguage>())
            {
                var beatmap = new BeatmapInfo(metadata: new BeatmapMetadata { Language = language });
                bool game = polyglot.IsLocalOnlyFor(beatmap);
                string stored = language.ToCanonicalName();

                Assert.That(ServerLocalOnly.IsLocalOnlyOn(polyglot.Acronym, stored), Is.EqualTo(game), $"{language}: local only");
                Assert.That(Typebeat.Web.Packages.Lyrics.PolyglotSummary.IsLocalOnlyLanguage(stored), Is.EqualTo(game), $"{language}: the pricing rule");
                Assert.That(polyglot.IsRankedFor(beatmap), Is.EqualTo(!game), $"{language}: ranked wherever it is not local only");

                if (game)
                    localOnly++;
            }

            Assert.That(ServerSubmission.ModRanked(polyglot.Acronym), Is.True, "ranked at its default configuration");
            Assert.That(ServerMultiplier.For(polyglot.Acronym, null), Is.EqualTo(calculator().CalculateFor(new Mod[] { polyglot })).Within(1e-12));
        });

        Assert.That(localOnly, Is.EqualTo(2), "Chinese and Korean, and nothing else");
    }

    [Test]
    public void TheFlatMultipliersAgree()
    {
        var calc = calculator();

        Assert.Multiple(() =>
        {
            foreach (var mod in allMods())
            {
                if (mod is ModRateAdjust || mod is ModTimeRamp || neverSubmitted(mod) || mod.Acronym == "CL")
                    continue;

                Assert.That(ServerMultiplier.For(mod.Acronym, null), Is.EqualTo(calc.CalculateFor(new[] { mod })).Within(1e-12),
                    $"{mod.Acronym} ({mod.Name})");
            }

            // The Classic mark: unpriced by the client's calculator, 0.95 on a stored row server-side.
            var classic = allMods().Single(m => m.Acronym == "CL");
            Assert.That(calc.CalculateFor(new[] { classic }), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(ServerMultiplier.For("CL", null), Is.EqualTo(0.95).Within(1e-12));
        });
    }

    [Test]
    public void TheRateMultipliersAgreeAcrossTheWholeSlider()
    {
        var calc = calculator();
        var rateMods = allMods().OfType<ModRateAdjust>().ToList();

        Assert.That(rateMods, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var mod in rateMods)
            {
                Assert.That(ServerRateMods.TryGetRange(mod.Acronym, out var range), Is.True, $"{mod.Acronym} is a rate mod on the server too");
                Assert.That(range.Min, Is.EqualTo(mod.SpeedChange.MinValue).Within(1e-12), $"{mod.Acronym} slider minimum");
                Assert.That(range.Max, Is.EqualTo(mod.SpeedChange.MaxValue).Within(1e-12), $"{mod.Acronym} slider maximum");
                Assert.That(range.Default, Is.EqualTo(mod.SpeedChange.Default).Within(1e-12), $"{mod.Acronym} default");

                Assert.That(ServerMultiplier.For(mod.Acronym, null), Is.EqualTo(calc.CalculateFor(new[] { mod })).Within(1e-12),
                    $"{mod.Acronym} with no rate submitted reads as the default");

                for (int step = (int)Math.Round(range.Min * 100); step <= (int)Math.Round(range.Max * 100); step++)
                {
                    double rate = step / 100.0;
                    mod.SpeedChange.Value = rate;

                    Assert.That(ServerMultiplier.For(mod.Acronym, rate), Is.EqualTo(calc.CalculateFor(new[] { mod })).Within(1e-12),
                        $"{mod.Acronym} at {rate}");
                }
            }
        });
    }

    [Test]
    public void TheRampsAreBoundedByTheServersPrice()
    {
        var calc = calculator();
        var ramps = allMods().OfType<ModTimeRamp>().ToList();

        Assert.That(ramps, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var mod in ramps)
            {
                Assert.That(ServerMultiplier.For(mod.Acronym, null), Is.GreaterThanOrEqualTo(calc.CalculateFor(new[] { mod })), mod.Acronym);
                Assert.That(mod.Ranked, Is.False, $"{mod.Acronym} is unranked in the game");
                Assert.That(ServerSubmission.ModRanked(mod.Acronym), Is.False, $"{mod.Acronym} is unranked on the server");
            }
        });
    }

    /// <summary>
    /// The statistics allowlist is the game's <see cref="HitResult"/> vocabulary exactly as
    /// a submitted score spells it: every member, serialised as the client serialises its
    /// statistics dictionary, is a key the server keeps, and nothing outside it is.
    /// </summary>
    [Test]
    public void EveryHitResultNameIsAKnownStatisticsKey()
    {
        var all = Enum.GetValues<HitResult>().ToDictionary(r => r, _ => 1);
        var names = JObject.Parse(JsonConvert.SerializeObject(all)).Properties().Select(p => p.Name).ToList();
        var asServerCounts = names.ToDictionary(n => n, _ => 1);

        Assert.Multiple(() =>
        {
            Assert.That(names, Has.Count.EqualTo(all.Count));

            foreach (string name in names)
                Assert.That(ServerContract.IsHitResultKey(name), Is.True, name);

            Assert.That(ServerContract.KnownKeysOnly(asServerCounts).Keys, Is.EquivalentTo(names));
            Assert.That(ServerContract.IsHitResultKey("zz"), Is.False);
            Assert.That(ServerContract.IsHitResultKey("Great"), Is.False, "the wire spells members in lower snake case");
        });
    }
}
