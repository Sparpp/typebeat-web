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
/// write <c>beatmaps.wpm</c>, <c>peak_wpm</c>, <c>peak_cpm</c> and <c>wpm_curve</c>, which is what
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

            // Non-vacuity for the row above: a fixture whose words happened to average exactly 5
            // cells would satisfy every assertion here even if one side had kept the real-word
            // convention, because that is precisely the length at which the two agree.
            Assert.That(s.AverageCharsPerWord, Is.Not.EqualTo(ServerPace.CHARS_PER_WORD),
                "the fixture has to sit off 5 cells per word for this to test anything");
            Assert.That(s.AverageWpm, Is.EqualTo(s.AverageCpm / ServerPace.CHARS_PER_WORD), "and WPM is CPM/5 on both sides");
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

            // And a map with no words divides by no zero: 0, not NaN, on both sides.
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.EqualTo(ClientPace.Compute([]).AverageCharsPerWord));
            Assert.That(ServerPace.Compute([]).AverageCharsPerWord, Is.Zero);
        });
    }

    /// <summary>
    /// The awkward fixture described on the class, projected into each repo's own line type. Long
    /// enough to fill several rolling windows (about 98 typeable cells against a 30-cell window), so
    /// the curve has real bars rather than the single window a minimal fixture would give it.
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
