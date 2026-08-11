using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup recompute for beatmap rows whose stored pace numbers predate the current
/// <see cref="LyricPace.VERSION"/> arithmetic (see 009_boundary_pace.sql). Each stale row's
/// .osu blob is looked up through its set's current version manifest, reparsed, and its
/// wpm / difficulty_rating / word_count / char_count rewritten. Idempotent and self-healing:
/// a row that fails (missing blob, parse error) is logged and left at its old version, so the
/// next boot retries it; everything else proceeds.
///
/// <para>
/// STALENESS HAS TWO ARMS (020_performance_points.sql). Besides an out-of-date
/// <see cref="LyricPace.VERSION"/>, a row is also stale when its RATE-ADJUSTED star ratings
/// (<c>sr_dt</c> / <c>sr_ht</c>, what pp prices a Double Time / Half Time play at) have never been
/// filled in. The second arm exists precisely so filling those columns did NOT need a VERSION bump:
/// bumping to 9 would additionally re-derive every stored map against the punctuated text and move
/// the word/cell counts of .osz-conversion blobs (see the VERSION doc comment, task 59), which was
/// held back as a deliberate separate decision rather than riding along with a pp deploy. THAT
/// DECISION HAS SINCE BEEN TAKEN: the backlog-115/119 star change needed the whole catalogue
/// re-rated, VERSION is now 9, and the punctuated re-derive travelled with it knowingly. The rate
/// ratings recompute for free either way, because they ride the same UPDATE.
/// </para>
/// </summary>
public static class PaceBackfill
{
    public static async Task RunAsync(Db db, IFileStore fileStore, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var stale = (await conn.QueryAsync<(long BeatmapId, string Filename, byte[] Sha256)>(
                """
                SELECT b.id AS BeatmapId, b.filename AS Filename, vf.sha256 AS Sha256
                FROM beatmaps b
                JOIN beatmapsets s ON s.id = b.set_id
                JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
                JOIN version_files vf ON vf.version_id = sv.id AND vf.filename = b.filename
                WHERE b.pace_version < @version OR b.sr_dt IS NULL OR b.sr_ht IS NULL
                """,
                new { version = LyricPace.VERSION }))
            .ToList();

        if (stale.Count == 0)
            return;

        int upgraded = 0;

        foreach (var row in stale)
        {
            try
            {
                byte[] content;

                await using (var blob = await fileStore.OpenBlobReadAsync(row.Sha256, ct))
                using (var buffer = new MemoryStream())
                {
                    await blob.CopyToAsync(buffer, ct);
                    content = buffer.ToArray();
                }

                var diff = BeatmapPackageParser.ParseDifficulty(row.Filename, content);

                // One statement: rewrite the row, then invalidate the pp of every score set on it.
                // The stored per-score pp is a function of the map's star ratings, so a rewritten
                // rating must never leave a stale pp behind; stamping pp_version back to 0 hands
                // those rows to PpBackfill, which runs later in the same startup (Program.cs).
                await conn.ExecuteAsync(
                    """
                    UPDATE beatmaps
                    SET difficulty_rating = @difficultyRating,
                        word_count = @wordCount,
                        char_count = @charCount,
                        wpm = @wpm,
                        skippable_s = @skippableS,
                        lyrics = @lyrics,
                        sr_dt = @srDt,
                        sr_ht = @srHt,
                        peak_wpm = @peakWpm,
                        peak_cpm = @peakCpm,
                        wpm_curve = @wpmCurve,
                        pace_version = @paceVersion
                    WHERE id = @id;

                    UPDATE scores SET pp_version = 0 WHERE beatmap_id = @id AND pp_version <> 0;
                    """,
                    new
                    {
                        id = row.BeatmapId,
                        difficultyRating = diff.Pace.DifficultyRating,
                        wordCount = diff.Pace.WordCount,
                        charCount = diff.Pace.TypeableCellCount,
                        wpm = diff.Pace.AverageWpm,
                        // v7: the skip allowance the play-time gate subtracts from drain. Rows the
                        // backfill cannot reach keep skippable_s = 0, i.e. the pre-task-47 bound.
                        skippableS = diff.SkippableS,
                        // v8: the lyrics: search haystack. Rows the backfill cannot reach keep '',
                        // i.e. invisible to lyrics: searches (018_lyrics_search.sql).
                        lyrics = diff.LyricsText,
                        // 020: the two rate-adjusted star ratings pp prices DT/HT plays at. Rows
                        // the backfill cannot reach keep NULL, i.e. their rate plays earn no pp
                        // yet and are retried rather than frozen at zero (PpBackfill).
                        srDt = diff.SrDoubleTime,
                        srHt = diff.SrHalfTime,
                        // v11: the rolling-window pace the set page graphs (028_wpm_curve.sql).
                        // Rows the backfill cannot reach keep NULL, i.e. no graph, which is also
                        // what a map too short to measure stores.
                        peakWpm = diff.PeakWpm,
                        peakCpm = diff.PeakCpm,
                        wpmCurve = diff.WpmCurvePoints,
                        paceVersion = LyricPace.VERSION,
                    });

                upgraded++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pace backfill failed for beatmap {BeatmapId} ({Filename}); will retry next startup.", row.BeatmapId, row.Filename);
            }
        }

        logger.LogInformation("Pace backfill: {Upgraded}/{Stale} beatmaps recomputed to pace v{Version}.", upgraded, stale.Count, LyricPace.VERSION);
    }
}
