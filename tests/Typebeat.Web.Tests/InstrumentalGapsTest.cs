using System.Text;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// The skip allowance the play-time gate subtracts from a map's drain length
/// (<see cref="InstrumentalGaps"/>), and the gate arithmetic itself
/// (<see cref="PlayTimeGate"/>). Task 47.
///
/// <para>
/// The numbers pinned here are the game's own: its InstrumentalGapsTest fixes a 10 000 ms perceived
/// gap at gapStart 3000 / skipTarget 9000 (6000 ms removable), and its qualification rule covers
/// neither the intro before the first line nor the outro after the last. This file is the server
/// mirror's regression pin against exactly those, so a divergence between the two repos shows up
/// here rather than as honest players being unranked again.
/// </para>
/// </summary>
[TestFixture]
public class InstrumentalGapsTest
{
    /// <summary>
    /// Two lines whose perceived instrumental is exactly <see cref="InstrumentalGaps.MIN_GAP_MS"/>:
    /// line 0 sings 1000-2000, line 1's first vocal is at 12000.
    /// </summary>
    private const string exactly_ten_second_gap =
        """
        {"version":2,"song_end_ms":40000,"granularity":"Word"}
        {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
        {"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}
        """;

    /// <summary>The same map with the second line pulled one millisecond earlier.</summary>
    private const string one_millisecond_short =
        """
        {"version":2,"song_end_ms":40000,"granularity":"Word"}
        {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
        {"text":"cd","start_ms":11999,"end_ms":13000,"words":[{"text":"cd","start_ms":11999,"end_ms":13000,"score":1}]}
        """;

    // ---- the threshold ----

    [Test]
    public void ExactlyTenSeconds_Qualifies_AndYieldsTheGamesWindow()
    {
        var gaps = InstrumentalGaps.Compute(Lines(exactly_ten_second_gap));

        Assert.That(gaps, Has.Count.EqualTo(1));

        Assert.Multiple(() =>
        {
            // sungEnd 2000 + GAP_START_SETTLE_MS.
            Assert.That(gaps[0].GapStartTime, Is.EqualTo(3000).Within(1e-9));
            // The next line's activation clamps to its own start (its vocals begin there).
            Assert.That(gaps[0].ActivationTime, Is.EqualTo(12000).Within(1e-9));
            // activation - SKIP_LEAD_MS.
            Assert.That(gaps[0].SkipTarget, Is.EqualTo(9000).Within(1e-9));
            Assert.That(gaps[0].SkippableMs, Is.EqualTo(6000).Within(1e-9));
        });
    }

    [Test]
    public void OneMillisecondShort_DoesNotQualify()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InstrumentalGaps.Compute(Lines(one_millisecond_short)), Is.Empty);
            Assert.That(InstrumentalGaps.SkippableSeconds(Lines(one_millisecond_short)), Is.EqualTo(0));
        });
    }

    [Test]
    public void QualificationIsMeasuredVocalToVocal_NotBoundaryToBoundary()
    {
        // Line 0's window runs to 12000 (line windows are contiguous), so a boundary-to-boundary
        // reading would see no gap at all. The perceived stretch is what counts, and it is 10 s.
        var lines = Lines(exactly_ten_second_gap);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].EndTime, Is.EqualTo(lines[1].StartTime), "line windows are contiguous");
            Assert.That(InstrumentalGaps.FirstVocalTime(lines[1]) - lines[0].SingEndTime,
                Is.EqualTo(InstrumentalGaps.MIN_GAP_MS).Within(1e-9));
            Assert.That(InstrumentalGaps.Compute(lines), Has.Count.EqualTo(1));
        });
    }

    // ---- intro / outro, matching what the game does ----

    [Test]
    public void IntroBeforeTheFirstLine_IsNotSkippable()
    {
        // 30 s of silence before anything is sung, then two lines back to back. The game's intro
        // SkipOverlay covers the run-up, not InstrumentalGaps, and drain_length_s starts at the
        // first line anyway, so the allowance is zero.
        var lines = Lines(
            """
            {"version":2,"song_end_ms":60000,"granularity":"Word"}
            {"text":"ab","start_ms":30000,"end_ms":31000,"words":[{"text":"ab","start_ms":30000,"end_ms":31000,"score":1}]}
            {"text":"cd","start_ms":32000,"end_ms":33000,"words":[{"text":"cd","start_ms":32000,"end_ms":33000,"score":1}]}
            """);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].StartTime, Is.EqualTo(30000), "the intro really is 30 s long");
            Assert.That(InstrumentalGaps.SkippableSeconds(lines), Is.EqualTo(0));
        });
    }

    [Test]
    public void OutroAfterTheLastLine_IsNotSkippable()
    {
        // A song that runs another 45 s past the final word. There is no next line, so there is no
        // gap; the game's outro overlay ends the play rather than seeking.
        var lines = Lines(
            """
            {"version":2,"song_end_ms":60000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            {"text":"cd","start_ms":3000,"end_ms":4000,"words":[{"text":"cd","start_ms":3000,"end_ms":4000,"score":1}]}
            """);

        Assert.That(InstrumentalGaps.SkippableSeconds(lines), Is.EqualTo(0));
    }

    // ---- the usability filter ----

    [Test]
    public void AQualifyingGapWithNoUsableWindow_IsDropped()
    {
        // The perceived stretch is still exactly 10 s (SingEndTime is 2000), but line 0's word
        // overruns to the boundary, so its last typeable cell is targeted at 9250 and the skip
        // period would open after the skip target. The game drops such a gap rather than flashing
        // an unusable overlay, so nothing is skippable.
        var lines = Lines(
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"abcd","start_ms":1000,"end_ms":2000,"words":[{"text":"abcd","start_ms":1000,"end_ms":15000,"score":1}]}
            {"text":"cd","start_ms":12000,"end_ms":13000,"words":[{"text":"cd","start_ms":12000,"end_ms":13000,"score":1}]}
            """);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].SingEndTime, Is.EqualTo(2000));
            Assert.That(InstrumentalGaps.LastTypeableTarget(lines[0]), Is.EqualTo(9250).Within(1e-9));
            Assert.That(InstrumentalGaps.Compute(lines), Is.Empty);
        });
    }

    // ---- degenerate maps ----

    [Test]
    public void MapsWithoutTwoLines_HaveNoAllowance()
    {
        // A blank map (task 45 made those importable; they cannot be played or submitted) and a
        // one-line map both yield zero rather than throwing.
        var blank = Lines("""{"version":2,"song_end_ms":40000,"granularity":"Word"}""");

        var single = Lines(
            """
            {"version":2,"song_end_ms":40000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            """);

        Assert.Multiple(() =>
        {
            Assert.That(blank, Is.Empty);
            Assert.That(InstrumentalGaps.SkippableSeconds(blank), Is.EqualTo(0));
            Assert.That(InstrumentalGaps.SkippableSeconds(single), Is.EqualTo(0));
            Assert.That(InstrumentalGaps.SkippableSeconds(null), Is.EqualTo(0));
        });
    }

    // ---- the whole path: a real-shaped .osu blob ----

    [Test]
    public void ParsedFromAnOsuBlob_TwoGapsSum_AndRideOnTheDifficulty()
    {
        // Four lines with two long instrumentals between them, in the shape "Immortal Flame" has
        // (gaps at roughly a minute and a minute and a half in).
        string osu = SyntheticPackage.OsuText(lyrics:
            """
            {"version":2,"song_end_ms":140000,"granularity":"Word"}
            {"text":"ab","start_ms":1000,"end_ms":2000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1}]}
            {"text":"cd","start_ms":58000,"end_ms":59000,"words":[{"text":"cd","start_ms":58000,"end_ms":59000,"score":1}]}
            {"text":"ef","start_ms":93000,"end_ms":94000,"words":[{"text":"ef","start_ms":93000,"end_ms":94000,"score":1}]}
            {"text":"gh","start_ms":95000,"end_ms":96000,"words":[{"text":"gh","start_ms":95000,"end_ms":96000,"score":1}]}
            """);

        var diff = BeatmapPackageParser.ParseDifficulty("map.osu", Encoding.UTF8.GetBytes(osu));

        // Gap 1: sung end 2000 -> first vocal 58000. gapStart 3000, skipTarget 55000: 52 s.
        // Gap 2: sung end 59000 -> first vocal 93000. gapStart 60000, skipTarget 90000: 30 s.
        // The 95000 line is only 1 s behind its predecessor, so it opens no gap.
        Assert.Multiple(() =>
        {
            Assert.That(diff.SkippableS, Is.EqualTo(82).Within(1e-9));
            Assert.That(diff.DrainLengthS, Is.EqualTo(98).Within(1e-9), "1000 ms to 99000 ms");

            // The whole point of the fix: an honest play of this map cannot reach 0.9 x drain,
            // but it clears the corrected bound comfortably.
            Assert.That(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, 0), Is.EqualTo(88.2).Within(1e-9));
            Assert.That(PlayTimeGate.RequiredSeconds(diff.DrainLengthS, diff.SkippableS), Is.EqualTo(14.4).Within(1e-9));
        });
    }

    // ---- the gate ----

    [Test]
    public void UnknownAllowance_FallsBackToTheOldBound()
    {
        Assert.Multiple(() =>
        {
            // skippable_s = 0 is what an unparseable or unreached blob leaves behind; the gate must
            // then be exactly as strict as it was before task 47, never more lenient.
            Assert.That(PlayTimeGate.RequiredSeconds(100, 0), Is.EqualTo(90).Within(1e-9));
            Assert.That(PlayTimeGate.Passes(89.9, 100, 0), Is.False);
            Assert.That(PlayTimeGate.Passes(90, 100, 0), Is.True);
        });
    }

    [Test]
    public void TheBoundIsClamped_NotSignedOrUnbounded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlayTimeGate.RequiredSeconds(100, 40), Is.EqualTo(54).Within(1e-9));
            Assert.That(PlayTimeGate.RequiredSeconds(100, 500), Is.EqualTo(0), "an oversized allowance cannot go negative");
            Assert.That(PlayTimeGate.RequiredSeconds(100, -5), Is.EqualTo(90).Within(1e-9), "nor can a negative one loosen it");
            Assert.That(PlayTimeGate.RequiredSeconds(0, 0), Is.EqualTo(0), "a zero-drain map gates nothing");
        });
    }

    private static IReadOnlyList<LyricLine> Lines(string lyrics)
        => LyricTiming.ParseSection(lyrics.ReplaceLineEndings("\n").Split('\n')).Lines;
}
