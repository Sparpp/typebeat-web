using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup fill for <c>beatmaps.judged_cells</c> / <c>judged_cells_literate</c>
/// (048_judged_cells.sql), the same shape as <see cref="PaceBackfill"/>: each live difficulty whose
/// pair is missing or was counted under another <see cref="JudgedCells.VERSION"/> has its .osu
/// looked up through its set's current version manifest, reparsed and recounted. A row that fails
/// (missing blob, parse error) is logged and left as it was, so the next boot retries it.
/// A map the game would decode differently is stamped with the current version and NULL counts
/// (<see cref="JudgedCells.ForMap"/>), which reads as unknown and is not retried.
///
/// <para>
/// Until a row is filled, a submission on it is accepted UNCHECKED, as every submission was before
/// 048 (<see cref="Scoring.ScoreSubmission.MaximumMatchesMap"/>), so running this late, or failing
/// on a row, never refuses an honest play. It writes nothing but its own three columns: the count
/// moves no rating and no stored score, so unlike <see cref="PaceBackfill"/> it hands nothing to
/// <see cref="PpBackfill"/>.
/// </para>
/// </summary>
public static class JudgedCellBackfill
{
    public static async Task RunAsync(Db db, IFileStore fileStore, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var stale = (await conn.QueryAsync<(long BeatmapId, string Filename, byte[] Sha256, string ChecksumMd5)>(
                """
                SELECT b.id AS BeatmapId, b.filename AS Filename, vf.sha256 AS Sha256, b.checksum_md5 AS ChecksumMd5
                FROM beatmaps b
                JOIN beatmapsets s ON s.id = b.set_id
                JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
                JOIN version_files vf ON vf.version_id = sv.id AND vf.filename = b.filename
                WHERE b.judged_cells_version IS DISTINCT FROM @version
                """,
                new { version = JudgedCells.VERSION }))
            .ToList();

        if (stale.Count == 0)
            return;

        int counted = 0;

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
                var judged = JudgedCells.ForMap(diff.LyricSectionLines, diff.Lines);

                // Only onto the .osu that was read: a re-upload landing in between has written its
                // own counts already, and must not be overwritten with the old map's.
                await conn.ExecuteAsync(
                    """
                    UPDATE beatmaps
                    SET judged_cells = @judgedCells,
                        judged_cells_literate = @judgedCellsLiterate,
                        judged_cells_version = @version
                    WHERE id = @id AND checksum_md5 = @checksumMd5
                    """,
                    new
                    {
                        id = row.BeatmapId,
                        judgedCells = judged.Cells,
                        judgedCellsLiterate = judged.Literate,
                        checksumMd5 = row.ChecksumMd5,
                        version = JudgedCells.VERSION,
                    });

                counted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Judged cell backfill failed for beatmap {BeatmapId} ({Filename}); will retry next startup.", row.BeatmapId, row.Filename);
            }
        }

        logger.LogInformation("Judged cell backfill: {Counted}/{Stale} beatmaps counted at v{Version}.", counted, stale.Count, JudgedCells.VERSION);
    }
}
