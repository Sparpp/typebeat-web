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
                WHERE b.pace_version < @version
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

                await conn.ExecuteAsync(
                    """
                    UPDATE beatmaps
                    SET difficulty_rating = @difficultyRating,
                        word_count = @wordCount,
                        char_count = @charCount,
                        wpm = @wpm,
                        skippable_s = @skippableS,
                        lyrics = @lyrics,
                        pace_version = @paceVersion
                    WHERE id = @id
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
