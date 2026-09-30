using ClientCurve = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricWpmCurve;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPace = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricPaceStatistics;
using ClientTypeability = typebeat.Game.Rulesets.TypeBeat.Beatmaps.Typeability;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using ClientWordPause = typebeat.Game.Rulesets.TypeBeat.Beatmaps.WordPause;
using ServerCurve = Typebeat.Web.Packages.Lyrics.LyricWpmCurve;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;
using ServerPace = Typebeat.Web.Packages.Lyrics.LyricPace;
using ServerTypeability = Typebeat.Web.Packages.Lyrics.Typeability;
using ServerUnit = Typebeat.Web.Packages.Lyrics.TimedUnit;
using ServerWordPause = Typebeat.Web.Packages.Lyrics.WordPause;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for a map's TYPING PACE (backlog 169/170).
///
/// <para>
/// Pace exists twice: the game's <see cref="ClientPace"/> and <see cref="ClientCurve"/> feed song
/// select's metadata wedge, and the server's <see cref="ServerPace"/> and <see cref="ServerCurve"/>
/// write <c>beatmaps.wpm</c> and <c>target_wpm</c> (the pace pair) plus <c>peak_wpm</c>,
/// <c>peak_cpm</c> and <c>wpm_curve</c> (the curve), which is what
/// the set page prints and graphs. Unlike a score, NONE OF THIS IS EVER ON THE WIRE: the client
/// computes its figures locally and the server computes its own at ingest, so the mirror is the only
/// thing keeping the two readouts equal, and a player comparing the wedge with the website is the
/// one who finds out when it is not. This is the only project that compiles both repos, so this is
/// where that is provable, and until this file existed it was not pinned anywhere.
/// </para>
///
/// <para>
/// Everything below asserts EXACT equality (no tolerance): the two sides are the same sequence of
/// double operations over the same inputs, so any difference at all is a divergence rather than
/// rounding. The fixture is deliberately awkward, because the arithmetic only diverges where the
/// text is interesting: mixed word lengths (so a chars-per-word figure is not trivially the same
/// number as anything else), punctuation and a hyphen (which the default stream turns into a word
/// break, changing the counts but not the curve), a repeated word, and a long instrumental gap
/// (which the whole-map rate charges only up to a second since LyricPace v23, the per-line mean
/// charges whole, and the curve's map span must span).
/// </para>
/// </summary>
[TestFixture]
public class LyricPaceParityTest
{
    [Test]
    public void TheFiveCharacterWordIsTheSameFiveEverywhere()
    {
        // Four copies of the typing-test constant exist by design: each pace file carries its own so
        // that it depends on nothing and stays mirrorable (see the constants' docs). Nothing but
        // this holds them together, and if they drift the map's advertised WPM stops meaning what
        // the HUD shows. The fifth copy, the browser core's literal in its live counter, is not
        // reachable from here without standing up a whole run.
        Assert.Multiple(() =>
        {
            Assert.That(ServerPace.CHARS_PER_WORD, Is.EqualTo(ClientPace.CHARS_PER_WORD), "map averages");
            Assert.That(ServerCurve.CHARS_PER_WORD, Is.EqualTo(ClientCurve.CHARS_PER_WORD), "rolling window");
            Assert.That(ServerCurve.CHARS_PER_WORD, Is.EqualTo(ServerPace.CHARS_PER_WORD), "the two server copies");
            Assert.That(ServerPace.CHARS_PER_WORD, Is.EqualTo(5.0), "and it is 5, the typing-test convention");
        });
    }

    [Test]
    public void TheSupportedPunctuationIsTheSameSetInBothRepos()
    {
        // Typeability is the text authority every pace figure is measured THROUGH: a map stores the
        // author's punctuated line, and the counts (and therefore the ratings and pp) come off the
        // DEFAULT stream derived from it. A whitelist that differs by one mark makes the two sides
        // normalize the same blob into different lines and count a different map. None of it is on
        // the wire, so, as with CHARS_PER_WORD above, this is the only place the mirror is provable,
        // and until backlog 202 it was not pinned anywhere.
        const string authored = "a,b.c'd-e?f!g;h:i(j)k[l]m\"n$o%p^q*r<s>t/u";

        Assert.Multiple(() =>
        {
            Assert.That(ServerTypeability.PUNCTUATION, Is.EqualTo(ClientTypeability.PUNCTUATION), "the supported set");
            Assert.That(ServerTypeability.WORD_BREAK, Is.EqualTo(ClientTypeability.WORD_BREAK), "the one mark that is a word break");
            Assert.That(ServerTypeability.FREESTYLE_MARKER, Is.EqualTo(ClientTypeability.FREESTYLE_MARKER), "and the marker that is not a mark at all");

            foreach (char c in ServerTypeability.PUNCTUATION)
            {
                Assert.That(ClientTypeability.IsPunctuation(c), Is.True, $"'{c}' is a mark on the client too");
                Assert.That(ClientTypeability.IsTypeable(c), Is.False, $"'{c}' is not a plain typeable char");
                Assert.That(ClientTypeability.IsCell(c), Is.False, $"'{c}' is not a cell");
            }

            // The derivation the set feeds, on a line carrying every mark once.
            Assert.That(ServerTypeability.Normalize(authored), Is.EqualTo(ClientTypeability.Normalize(authored)), "the author's form");
            Assert.That(ServerTypeability.ToDefaultStream(authored), Is.EqualTo(ClientTypeability.ToDefaultStream(authored)), "the played stream");
        });
    }

    [Test]
    public void TheClientsMapAveragesAreTheAveragesTheServerStores()
    {
        var (client, server) = TwinMaps();

        var c = ClientPace.Compute(client);
        var s = ServerPace.Compute(server);

        Assert.Multiple(() =>
        {
            // The counts first: these are what the wpm figure is derived FROM, and they are also
            // stored in their own right (beatmaps.word_count / char_count).
            Assert.That(s.TypeableCellCount, Is.EqualTo(c.TypeableCellCount), "char_count");
            Assert.That(s.WordCount, Is.EqualTo(c.WordCount), "word_count");

            Assert.That(s.AverageCpm, Is.EqualTo(c.AverageCpm), "average CPM");
            Assert.That(s.AverageWpm, Is.EqualTo(c.AverageWpm), "beatmaps.wpm");
            Assert.That(s.AverageCharsPerWord, Is.EqualTo(c.AverageCharsPerWord), "the set page's Chars/word row");
            Assert.That(s.TargetWpm, Is.EqualTo(c.TargetWpm), "beatmaps.target_wpm");

            // THE THIRD FIGURE, which the rework added and which has no column of its own: the
            // unweighted per-line mean that AverageWpm used to be. It is mirrored because the game
            // carries it, and an unmirrored figure is exactly the kind that drifts unnoticed.
            Assert.That(s.LineAverageCpm, Is.EqualTo(c.LineAverageCpm), "the per-line mean");
            Assert.That(s.LineAverageWpm, Is.EqualTo(c.LineAverageWpm), "and in words");

            // Non-vacuity for the chars-per-word row: a fixture whose words happened to average
            // exactly 5 cells would satisfy every assertion here even if one side had kept the
            // real-word convention, because that is precisely the length at which the two agree.
            Assert.That(s.AverageCharsPerWord, Is.Not.EqualTo(ServerPace.CHARS_PER_WORD),
                "the fixture has to sit off 5 cells per word for this to test anything");
            Assert.That(s.AverageWpm, Is.EqualTo(s.AverageCpm / ServerPace.CHARS_PER_WORD), "and WPM is CPM/5 on both sides");

            // NON-VACUITY FOR THE THREE PACE FIGURES, which is what this fixture is shaped for: they
            // have to be THREE DIFFERENT NUMBERS, or a port that returned one of them under another's
            // name would satisfy every equality above on both sides at once and the pin would prove
            // nothing. The whole-map rate divides by the time the map is actually SUNG, the line mean
            // gives every line one vote however long it is, and the target is the hardest window by
            // raw speed read off the difficulty model. The fixture's long instrumental gap and its
            // two-word burst are what pull the three apart.
            Assert.That(s.AverageWpm, Is.Not.EqualTo(s.LineAverageWpm).Within(1e-9),
                "the whole-map rate is not the per-line mean");
            Assert.That(s.TargetWpm, Is.Not.EqualTo(s.AverageWpm).Within(1e-9),
                "and the target is neither");
            Assert.That(s.TargetWpm, Is.GreaterThan(0));

            // THE FLOOR, which is the target's one presentation rule and is a real branch rather
            // than an identity: a map whose hardest window is slower than its own whole-map pace
            // reports the average instead of a target below it. So the target can never read below
            // the average on either side, whatever the model says.
            Assert.That(s.TargetWpm, Is.GreaterThanOrEqualTo(s.AverageWpm), "floored at the whole-map rate");
            Assert.That(c.TargetWpm, Is.GreaterThanOrEqualTo(c.AverageWpm), "on the client too");
        });
    }

    [Test]
    public void EveryPaceFigureAgreesOnEveryStreamAndRateAndOnAPausedMap()
    {
        // PR 2 gave the game's pace a STREAM and a CLOCK (song select reads the map converted with
        // the selected mods), and the server carries both parameters so the mirror can be held at
        // every one of them even though it stores only the default. It also made authored pauses a
        // rating input. PR 3 (LyricPace v23) then made a rest a PACE input as well, but only past
        // the one-second cap every pause is charged at: the paused fixtures' rests are all shorter
        // than that, so they still prove the figures did not follow there, and the long-rest fixture
        // is the one whose rests the whole-map rate has to trim. The target reads the envelope arm's
        // DENSITY, which spreads a word's cells over its whole span (only the chunked axis's rhythm
        // arm reads a word's dividers), on every one of them.
        var fixtures = new[]
        {
            ("awkward", TwinMaps()),
            ("paused", PerformancePointsParityTest.PausedTwinMaps()),
            ("paused, ignored", PerformancePointsParityTest.PausedTwinMaps(PerformancePointsParityTest.PauseShape.InvalidOnly)),
            ("long rests", LongRestTwinMaps(withRests: true)),
        };

        Assert.Multiple(() =>
        {
            foreach ((string name, (IReadOnlyList<ClientLine> client, IReadOnlyList<ServerLine> server)) in fixtures)
            foreach (bool literate in new[] { false, true })
            foreach (double rate in new[] { 1.0, 1.5, 0.75 })
            {
                var c = ClientPace.Compute(client, literate, rate);
                var s = ServerPace.Compute(server, literate, rate);
                string context = $"{name} literate={literate} rate={rate}";

                Assert.That(s.TypeableCellCount, Is.EqualTo(c.TypeableCellCount), $"{context}: cells");
                Assert.That(s.WordCount, Is.EqualTo(c.WordCount), $"{context}: words");
                Assert.That(s.AverageCpm, Is.EqualTo(c.AverageCpm), $"{context}: average CPM");
                Assert.That(s.LineAverageCpm, Is.EqualTo(c.LineAverageCpm), $"{context}: per-line mean");
                Assert.That(s.TargetWpm, Is.EqualTo(c.TargetWpm), $"{context}: target WPM");
            }

            // The default arguments ARE the stored figures: calling with them explicitly changes
            // nothing, so no stored column can have moved for the parameters' sake.
            var (awkwardClient, awkwardServer) = TwinMaps();
            Assert.That(ServerPace.Compute(awkwardServer, false, 1), Is.EqualTo(ServerPace.Compute(awkwardServer)), "the defaults");

            // RESTS UNDER A SECOND DO NOT REACH THE PACE, on either side: the paused fixture and its
            // bare twin share every figure, because a rest inside the sung span is charged whole up
            // to the cap. (That the same rests DO move the rating is pinned in
            // PerformancePointsParityTest, on the same fixture.)
            var (pausedClient, pausedServer) = PerformancePointsParityTest.PausedTwinMaps();
            var (bareClient, bareServer) = PerformancePointsParityTest.PausedTwinMaps(PerformancePointsParityTest.PauseShape.None);

            Assert.That(ServerPace.Compute(pausedServer).AverageCpm, Is.EqualTo(ServerPace.Compute(bareServer).AverageCpm),
                "a rest is not a break for the whole-map rate");
            Assert.That(ClientPace.Compute(pausedClient).AverageCpm, Is.EqualTo(ClientPace.Compute(bareClient).AverageCpm),
                "on the client either");
            Assert.That(ServerPace.Compute(pausedServer).TargetWpm, Is.EqualTo(ServerPace.Compute(bareServer).TargetWpm),
                "nor for the target, whose envelope density spreads a word over its whole span");
            Assert.That(ClientPace.Compute(pausedClient).TargetWpm, Is.EqualTo(ClientPace.Compute(bareClient).TargetWpm),
                "on the client either");

            // A REST OVER A SECOND DOES (v23): the part past the cap leaves the denominator, so the
            // whole-map rate rises on both sides by the same amount, and at every clock (the cap is
            // one PLAYBACK second, so a rate moves it). The per-line mean reads the boundary window
            // and cannot see a rest at all.
            var (longClient, longServer) = LongRestTwinMaps(withRests: true);
            var (plainClient, plainServer) = LongRestTwinMaps(withRests: false);

            foreach (double rate in new[] { 1.0, 1.5, 0.75 })
            {
                Assert.That(ServerPace.Compute(longServer, false, rate).AverageCpm, Is.GreaterThan(ServerPace.Compute(plainServer, false, rate).AverageCpm),
                    $"rate={rate}: a rest past the cap is trimmed from the denominator");
                Assert.That(ServerPace.Compute(longServer, false, rate).AverageCpm, Is.EqualTo(ClientPace.Compute(longClient, false, rate).AverageCpm),
                    $"rate={rate}: identically on both sides");
                Assert.That(ServerPace.Compute(longServer, false, rate).LineAverageCpm, Is.EqualTo(ServerPace.Compute(plainServer, false, rate).LineAverageCpm),
                    $"rate={rate}: and the per-line mean does not see it");
            }

            // Hand-computed at rate 1, so the pin is not only "the two sides agree": each line is
            // two 2000 ms words (the first holding a 3000 ms rest inside a 5000 ms span) with no
            // gap, 7000 ms of spans charged 7000 - (3000 - 1000) = 5000, plus 1000 ms for the
            // 4000 ms rest between the two lines. 2 * (20 letters + 1 space) = 42 cells over 11000 ms.
            Assert.That(ServerPace.Compute(longServer).AverageCpm, Is.EqualTo(42 * 60000.0 / 11000).Within(1e-9), "the hand figure");
        });
    }

    [Test]
    public void TheCurveAgreesAtEveryClock()
    {
        // LyricPace v23 made the curve's window a PLAYBACK-time window (at least WINDOW_SECONDS and
        // MIN_WINDOW_CELLS), so song select recomputes it at the selected rate rather than scaling
        // it. The server stores only the rate-1 reading, and carries the parameter so the mirror is
        // held at every clock anyway.
        var fixtures = new[]
        {
            ("awkward", TwinMaps()),
            ("paused", PerformancePointsParityTest.PausedTwinMaps()),
            ("long rests", LongRestTwinMaps(withRests: true)),
        };

        Assert.Multiple(() =>
        {
            foreach ((string name, (IReadOnlyList<ClientLine> client, IReadOnlyList<ServerLine> server)) in fixtures)
            foreach (double rate in new[] { 1.0, 1.5, 0.75 })
            {
                var c = ClientCurve.Compute(client, rate: rate);
                var s = ServerCurve.Compute(server, rate: rate);
                string context = $"{name} rate={rate}";

                Assert.That(s.IsEmpty, Is.False, $"{context}: the fixture has to be long enough to measure");
                Assert.That(s.PeakWpm, Is.EqualTo(c.PeakWpm), $"{context}: peak");
                Assert.That(s.PeakCpm, Is.EqualTo(c.PeakCpm), $"{context}: peak CPM");
                Assert.That(s.Curve, Is.EqualTo(c.Curve).AsCollection, $"{context}: curve");
            }

            // Non-vacuity for the parameter: a rate that only scaled would leave the normalised shape
            // alone, and this fixture's does not.
            var (_, awkwardServer) = TwinMaps();
            var slow = ServerCurve.Compute(awkwardServer, rate: 1);
            var fast = ServerCurve.Compute(awkwardServer, rate: 1.5);

            Assert.That(fast.Curve.Select(w => w / fast.PeakWpm), Is.Not.EqualTo(slow.Curve.Select(w => w / slow.PeakWpm)).AsCollection,
                "a rate recomputes the curve rather than scaling it");
            Assert.That(ServerCurve.Compute(awkwardServer).Curve, Is.EqualTo(slow.Curve).AsCollection, "the default is rate 1, the stored figure");
        });
    }

    [Test]
    public void TheClientsRollingWindowIsTheCurveTheServerStores()
    {
        var (client, server) = TwinMaps();

        var c = ClientCurve.Compute(client);
        var s = ServerCurve.Compute(server);

        Assert.Multiple(() =>
        {
            Assert.That(s.IsEmpty, Is.False, "the fixture has to be long enough to measure");
            Assert.That(s.PeakWpm, Is.EqualTo(c.PeakWpm), "beatmaps.peak_wpm");
            Assert.That(s.PeakCpm, Is.EqualTo(c.PeakCpm), "beatmaps.peak_cpm");
            Assert.That(s.StartTime, Is.EqualTo(c.StartTime), "curve start");
            Assert.That(s.EndTime, Is.EqualTo(c.EndTime), "curve end");

            // Point by point, not just the peak: the peak is one bar of a hundred, and the set page
            // graphs all of them.
            Assert.That(s.Curve, Is.EqualTo(c.Curve).AsCollection, "beatmaps.wpm_curve");
            Assert.That(s.Curve.Count, Is.EqualTo(ServerCurve.DEFAULT_CURVE_POINTS));

            // The redefinition itself, held on both sides: a window's WPM is its CPM over five, so
            // the two peaks now always sit in the same window.
            Assert.That(s.PeakWpm, Is.EqualTo(s.PeakCpm / ServerCurve.CHARS_PER_WORD));
            Assert.That(c.PeakWpm, Is.EqualTo(c.PeakCpm / ClientCurve.CHARS_PER_WORD));

            // The peak, the target and the map average are three different statistics over the same
            // fixture, and they have to read as three different numbers or a surface that showed
            // one where it means another would go unnoticed. The target itself lives on the pace
            // pair, not here (it is a per-line figure), and is pinned in the test above.
            Assert.That(s.PeakWpm, Is.Not.EqualTo(ServerPace.Compute(server).AverageWpm), "peak is not the map average");
            Assert.That(s.PeakWpm, Is.Not.EqualTo(ServerPace.Compute(server).TargetWpm), "nor the map target");
        });
    }

    [Test]
    public void BothPortsDegenerateTheSameWay()
    {
        var (client, server) = Twin([("hi there", 0, 1000, [("hi", 0, 500), ("there", 500, 1000)])]);

        var clientCurve = ClientCurve.Compute(client);
        var serverCurve = ServerCurve.Compute(server);

        Assert.Multiple(() =>
        {
            // Under MIN_WINDOW_CELLS: no curve, no peaks, on both sides rather than one throwing.
            Assert.That(serverCurve.IsEmpty, Is.EqualTo(clientCurve.IsEmpty));
            Assert.That(serverCurve.PeakWpm, Is.EqualTo(clientCurve.PeakWpm));
            Assert.That(serverCurve.PeakCpm, Is.EqualTo(clientCurve.PeakCpm));

            // THE TARGET DOES NOT DEGENERATE WITH THE CURVE, and both ports have to agree on that
            // too. A one-second map is far too short for the difficulty model to find any window on,
            // so the model's own target is 0; the FLOOR then reports the whole-map average instead,
            // which is why the figure survives at all rather than reading as a map that asks for
            // nothing. That is the floor's second job (its first is the map whose hardest stretch is
            // slower than its own average) and this is where a mirror missing it shows.
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.EqualTo(ClientPace.Compute(client).TargetWpm));
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.EqualTo(ServerPace.Compute(server).AverageWpm));
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.Not.Zero, "the floor keeps a figure on a map with no window");

            // And a map with no words divides by no zero: 0, not NaN, on both sides.
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.EqualTo(ClientPace.Compute([]).AverageCharsPerWord));
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.Zero);
            Assert.That(ServerPace.Compute([]).TargetWpm, Is.EqualTo(ClientPace.Compute([]).TargetWpm));
            Assert.That(ServerPace.Compute([]).TargetWpm, Is.Zero, "no counted line, so nothing to floor against either");
            Assert.That(ServerPace.Compute([]).LineAverageWpm, Is.EqualTo(ClientPace.Compute([]).LineAverageWpm));
        });
    }

    /// <summary>
    /// The awkward fixture described on the class, projected into each repo's own line type. Long
    /// enough to fill several rolling windows (141 typeable cells against a 16-cell floor), so the
    /// curve has real bars rather than the single window a minimal fixture would give it.
    ///
    /// <para>ITS SHAPE IS WHAT PULLS THE THREE PACE FIGURES APART, which is the whole reason a
    /// deliberately awkward fixture is worth having. The long instrumental gap separates the
    /// WHOLE-MAP rate (which divides by the time the map is actually sung, so the gap is in its
    /// denominator only up to a second) from the PER-LINE mean (which gives the line before the gap
    /// one vote like any other). The two-word burst at the end is the fastest thing on the map by a distance, so it
    /// pulls the whole-map rate up while being far too short to be anyone's hardest window, which
    /// separates both of them from the TARGET. Three figures, three numbers, and the test asserts
    /// that outright rather than trusting it.</para>
    ///
    /// <para>The six lines were shaped for the per-line selection the target used to be (a fastest
    /// fifth over the lines of at least three words). That selection is gone, and the fixture is
    /// kept exactly as it was: every property it was built for still separates the figures the
    /// rework left behind, and a fixture nobody had to re-tune is one nobody can have tuned to make
    /// a test pass.</para>
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) TwinMaps()
        => Twin(
        [
            ("Hello there, world!", 1000, 4000,
                [("Hello", 1000, 2000), ("there,", 2000, 3000), ("world!", 3000, 4000)]),
            ("Typing is a rhythm, not a race.", 4000, 8000,
                [("Typing", 4000, 4800), ("is", 4800, 5100), ("a", 5100, 5300), ("rhythm,", 5300, 6400), ("not", 6400, 6900), ("a", 6900, 7100), ("race.", 7100, 8000)]),
            ("world world world", 8000, 9000,
                [("world", 8000, 8300), ("world", 8300, 8600), ("world", 8600, 9000)]),
            ("After a long-drawn instrumental rest...", 30000, 35000,
                [("After", 30000, 31000), ("a", 31000, 31300), ("long-drawn", 31300, 32200), ("instrumental", 32200, 34000), ("rest...", 34000, 35000)]),
            ("Twin maps need one more line", 36000, 38000,
                [("Twin", 36000, 36400), ("maps", 36400, 36700), ("need", 36700, 37000), ("one", 37000, 37300), ("more", 37300, 37600), ("line", 37600, 38000)]),
            // The two-word burst: 14 cells over a 600 ms window is 1400 CPM, the fastest line here
            // by a distance, and the three-word floor keeps it out of the target pool.
            ("screaming fast", 39000, 39600,
                [("screaming", 39000, 39400), ("fast", 39400, 39600)]),
        ]);

    [Test]
    public void TheClientsStarRatingIsTheRatingTheServerStores()
    {
        // THE PACE PAIR AND THE RATING COME OUT OF ONE PARSE and are written by one statement, so
        // they are pinned together here: PaceStatistics.DifficultyRating is what the server lands in
        // beatmaps.difficulty_rating. The client's pace struct carries no such field (the game has
        // no column to fill and asks the model directly), so the equality is against the model
        // itself, which is exactly the claim: the number the server STORES is the number the client
        // COMPUTES for the same map, through the same default reading (the CHUNKED axis).
        //
        // The eighteen-cell matrix is pinned in PerformancePointsParityTest, which is where a price
        // reads it; this is the one rating this file's own Compute produces.
        var (client, server) = TwinMaps();

        Assert.Multiple(() =>
        {
            Assert.That(ServerPace.Compute(server).DifficultyRating,
                Is.EqualTo(typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricDifficulty.Compute(client)), "beatmaps.difficulty_rating");

            // And the server's stored figure really is the model's DEFAULT reading rather than one
            // the pace file computed for itself, which is what keeps the pair and the rating one
            // computation rather than two that have to be kept in step.
            Assert.That(ServerPace.Compute(server).DifficultyRating,
                Is.EqualTo(Typebeat.Web.Packages.Lyrics.LyricDifficulty.Compute(server)));

            Assert.That(ServerPace.Compute(server).DifficultyRating, Is.GreaterThan(0),
                "the fixture has to rate as something, or the equality above is vacuous");
        });
    }

    [Test]
    public void TheVersionsThatGateTheSweepsAreTheOnesTheChangeLandedAt()
    {
        // The pace VERSION is what makes PaceBackfill revisit every stored row, and the pp VERSION
        // is what makes PpBackfill reprice every stored score. Both moved in this change, and a
        // half-landed bump is a catalogue that never re-rates or a score table that never reprices.
        // Pinned here rather than only in each repo's own suite, because the pp one is shared with
        // the game and the pace one gates the column the pp one now depends on.
        Assert.Multiple(() =>
        {
            // 23 since PR 3: the whole-map average's capped pauses and the curve's playback-time
            // window re-derive beatmaps.wpm, target_wpm and the curve columns. No rating moves and
            // the pp formula does not move, so its VERSION stays.
            Assert.That(ServerPace.VERSION, Is.EqualTo(23));
            Assert.That(Typebeat.Web.Scoring.PerformancePoints.VERSION, Is.EqualTo(24)); // pp:version
        });
    }

    /// <summary>
    /// Two lines of two 10-character words each, sung back to back, with a 4000 ms rest between the
    /// lines. With <paramref name="withRests"/> each line's first word runs 5000 ms and holds a
    /// 3000 ms authored rest (split after its fifth character), so the whole-map rate has a rest past
    /// the one-second cap to trim. Without, the same words carry no rest. Long enough (42 cells) to
    /// fill the curve's 16-cell floor.
    /// </summary>
    private static (IReadOnlyList<ClientLine> Client, IReadOnlyList<ServerLine> Server) LongRestTwinMaps(bool withRests)
    {
        var client = new List<ClientLine>();
        var server = new List<ServerLine>();

        foreach (double lineStart in new[] { 1000.0, 12000.0 })
        {
            double first = lineStart, firstEnd = lineStart + 5000, second = firstEnd, secondEnd = second + 2000;
            (double Start, double End, int Split)[] rests = withRests ? [(lineStart + 1000, lineStart + 4000, 5)] : [];

            client.Add(new ClientLine
            {
                RawText = "abcdefghij klmnopqrst",
                StartTime = lineStart,
                EndTime = secondEnd,
                SingEndTime = secondEnd,
                Units =
                [
                    new ClientUnit { Text = "abcdefghij", StartTime = first, EndTime = firstEnd, Pauses = rests.Select(r => new ClientWordPause(r.Start, r.End, r.Split)).ToArray() },
                    new ClientUnit { Text = "klmnopqrst", StartTime = second, EndTime = secondEnd },
                ],
            });
            server.Add(new ServerLine
            {
                RawText = "abcdefghij klmnopqrst",
                StartTime = lineStart,
                EndTime = secondEnd,
                SingEndTime = secondEnd,
                Units =
                [
                    new ServerUnit { Text = "abcdefghij", StartTime = first, EndTime = firstEnd, Pauses = rests.Select(r => new ServerWordPause(r.Start, r.End, r.Split)).ToArray() },
                    new ServerUnit { Text = "klmnopqrst", StartTime = second, EndTime = secondEnd },
                ],
            });
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
}
