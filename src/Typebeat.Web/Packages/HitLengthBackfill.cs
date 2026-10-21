using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup fill for <c>beatmaps.hit_length_s</c> (051_hit_length.sql), the drain the client is
/// shown, in the shape of <see cref="JudgedCellBackfill"/>: each live difficulty whose value is
/// missing or was computed under another <see cref="InstrumentalGaps.DRAIN_VERSION"/> has its .osu
/// looked up through its set's current version manifest, reparsed and remeasured
/// (<see cref="BeatmapPackage.HitLengthS"/>). A row that fails (missing blob, parse error) is logged
/// and left as it was, so the next boot retries it.
///
/// <para>
/// A DEDICATED sweep rather than a <see cref="LyricPace.VERSION"/> bump, deliberately. A pace bump
/// makes <see cref="PaceBackfill"/> re-rate the whole catalogue and stamp every score back to
/// pp version 0 for <see cref="PpBackfill"/> to reprice, all to fill one display column that moves
/// no rating and no score. This writes its own two columns and nothing else: not
/// <c>drain_length_s</c> (the play-time gate's input, which must not move), not
/// <c>skippable_s</c>, not a rating. Until a row is filled every reader serves
/// <c>drain_length_s</c> in its place, which is exactly what it served before 051, so running late
/// or failing on a row only delays the corrected figure.
/// </para>
/// </summary>
public static class HitLengthBackfill
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
                WHERE b.hit_length_version IS DISTINCT FROM @version
                """,
                new { version = InstrumentalGaps.DRAIN_VERSION }))
            .ToList();

        if (stale.Count == 0)
            return;

        int measured = 0;

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

                // Only onto the .osu that was read: a re-upload landing in between has written its
                // own value already, and must not be overwritten with the old map's.
                await conn.ExecuteAsync(
                    """
                    UPDATE beatmaps
                    SET hit_length_s = @hitLengthS,
                        hit_length_version = @version
                    WHERE id = @id AND checksum_md5 = @checksumMd5
                    """,
                    new
                    {
                        id = row.BeatmapId,
                        hitLengthS = diff.HitLengthS,
                        checksumMd5 = row.ChecksumMd5,
                        version = InstrumentalGaps.DRAIN_VERSION,
                    });

                measured++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hit length backfill failed for beatmap {BeatmapId} ({Filename}); will retry next startup.", row.BeatmapId, row.Filename);
            }
        }

        logger.LogInformation("Hit length backfill: {Measured}/{Stale} beatmaps measured at v{Version}.", measured, stale.Count, InstrumentalGaps.DRAIN_VERSION);
    }
}
