using ClientCurve = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricWpmCurve;
using ClientLine = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricLine;
using ClientPace = typebeat.Game.Rulesets.TypeBeat.Beatmaps.LyricPaceStatistics;
using ClientTypeability = typebeat.Game.Rulesets.TypeBeat.Beatmaps.Typeability;
using ClientUnit = typebeat.Game.Rulesets.TypeBeat.Beatmaps.TimedUnit;
using ServerCurve = Typebeat.Web.Packages.Lyrics.LyricWpmCurve;
using ServerLine = Typebeat.Web.Packages.Lyrics.LyricLine;
using ServerPace = Typebeat.Web.Packages.Lyrics.LyricPace;
using ServerTypeability = Typebeat.Web.Packages.Lyrics.Typeability;
using ServerUnit = Typebeat.Web.Packages.Lyrics.TimedUnit;

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
/// (which the per-line pace mean must not dilute but the curve's map span must span).
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

            // Non-vacuity for the row above: a fixture whose words happened to average exactly 5
            // cells would satisfy every assertion here even if one side had kept the real-word
            // convention, because that is precisely the length at which the two agree.
            Assert.That(s.AverageCharsPerWord, Is.Not.EqualTo(ServerPace.CHARS_PER_WORD),
                "the fixture has to sit off 5 cells per word for this to test anything");
            Assert.That(s.AverageWpm, Is.EqualTo(s.AverageCpm / ServerPace.CHARS_PER_WORD), "and WPM is CPM/5 on both sides");

            // Non-vacuity for the target row, which needs three separate things of the fixture.
            //
            // First, the two figures must be DIFFERENT numbers: a port that returned the map
            // average under the name TargetWpm would satisfy the equality above on both sides at
            // once and the pin would prove nothing. The fixture's six lines run 340, 435, 1020,
            // 432, 840 and 1400 CPM, so the average is 4467/6 = 744.5 CPM = 148.9 WPM while the
            // target is the fastest ceil(0.20 * 5) = 1 ELIGIBLE line, 1020 CPM = 204 WPM.
            //
            // Second, and this is why the fixture has five ELIGIBLE lines rather than the four it
            // had before the target existed: at n = 5 the selected count steps with the fraction
            // (ceil(0.20 * 5) = 1 against ceil(0.25 * 5) = 2), so a one-sided retune of
            // target_line_fraction in either mirror lands here. At n = 4 both fractions select one
            // line and this pin would sleep through the drift.
            //
            // Third, and this is the SIXTH line (backlog 274): "screaming fast" is two words, so the
            // eligibility floor refuses it, and it is the fastest line on the map at 1400 CPM. Drop
            // the floor in either mirror alone and that side selects the fastest ceil(0.20 * 6) = 2
            // of all six, (1400 + 1020) / 2 = 1210 CPM = 242 WPM, and this pin goes red. Without a
            // short line the fixture cannot see a one-sided floor at all, exactly as it could not
            // see a one-sided fraction at four lines.
            Assert.That(s.TargetWpm, Is.EqualTo(204.0).Within(1e-9), "the fastest ELIGIBLE line, 1020 CPM");
            Assert.That(s.AverageWpm, Is.EqualTo(148.9).Within(1e-9), "against a 744.5 CPM map average");
            Assert.That(s.TargetWpm, Is.GreaterThan(s.AverageWpm), "target is not the average");
            Assert.That(s.TargetWpm, Is.Not.EqualTo(242.0).Within(1e-9), "nor the unfiltered fastest fifth");
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
            // Under WINDOW_CELLS: no curve, no peaks, on both sides rather than one throwing.
            Assert.That(serverCurve.IsEmpty, Is.EqualTo(clientCurve.IsEmpty));
            Assert.That(serverCurve.PeakWpm, Is.EqualTo(clientCurve.PeakWpm));
            Assert.That(serverCurve.PeakCpm, Is.EqualTo(clientCurve.PeakCpm));

            // The TARGET does not degenerate with the curve, and both ports have to agree on that
            // too: one line is already a fifth of one line, so this two-word map has a target where
            // it has no peak, and with one counted line the target is that line, i.e. the average.
            // It is also the ALL-SHORT FALLBACK on both sides (backlog 274): "hi there" is two
            // words, so nothing here clears the eligibility floor and the pool is every counted line,
            // which is why the figure survives at all rather than dividing by an empty selection.
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.EqualTo(ClientPace.Compute(client).TargetWpm));
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.EqualTo(ServerPace.Compute(server).AverageWpm));
            Assert.That(ServerPace.Compute(server).TargetWpm, Is.Not.Zero, "a per-line figure survives a map with no window");

            // And a map with no words divides by no zero: 0, not NaN, on both sides.
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.EqualTo(ClientPace.Compute([]).AverageCharsPerWord));
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.Zero);
            Assert.That(ServerPace.Compute([]).TargetWpm, Is.EqualTo(ClientPace.Compute([]).TargetWpm));
            Assert.That(ServerPace.Compute([]).TargetWpm, Is.Zero, "no counted line, so no fifth of one");
        });
    }

    /// <summary>
    /// The awkward fixture described on the class, projected into each repo's own line type. Long
    /// enough to fill several rolling windows (141 typeable cells against a 30-cell window), so the
    /// curve has real bars rather than the single window a minimal fixture would give it.
    ///
    /// <para>FIVE ELIGIBLE lines, and the count is load bearing: the target pace selects
    /// ceil(target_line_fraction * poolCount) of them, which at five lines takes one line at 0.20
    /// and two at 0.25. So a one-sided retune of that constant in either mirror shows up here,
    /// where at four lines both fractions would have selected the same one line and the pin would
    /// have slept through it.</para>
    ///
    /// <para>Plus a SIXTH line that is deliberately INELIGIBLE (backlog 274): "screaming fast" is two
    /// words, under the three-word floor, and it is the fastest line on the map. The target must
    /// therefore ignore it, and a mirror that dropped the floor would select it and read a different
    /// number. That is the same reason the fifth line exists, one constant along: a pin can only see
    /// a rule the fixture actually reaches.</para>
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
