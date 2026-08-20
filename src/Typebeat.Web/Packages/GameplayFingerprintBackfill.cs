using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup fill for <c>beatmaps.gameplay_fingerprint</c> (030_gameplay_fingerprint.sql), the same
/// shape as <see cref="PaceBackfill"/> / <see cref="PpBackfill"/> / <see cref="LanguageBackfill"/>:
/// recompute from the stored artifacts, log and skip whatever fails, retry it next boot.
///
/// <para>
/// WHY IT IS NOT OPTIONAL. Every map that is ranked TODAY has no stored fingerprint, and the
/// ingest's demotion check compares against exactly that column. Without this sweep the FIRST
/// re-upload after deploy is the one case that would fail to demote, which is precisely the case
/// backlog 173 exists to catch. (The check's unknown-reads-as-changed rule means such a row would
/// actually demote rather than silently keep its rank, so the failure mode is a false demote, not
/// a miss, but a catalogue-wide wave of false demotes on deploy day is not an acceptable
/// alternative to just filling the column.)
/// </para>
///
/// <para>
/// IT ALSO HANDLES A RECIPE CHANGE. Staleness is "NULL, or not written by the current
/// <see cref="GameplayFingerprint.VERSION"/>", so bumping that constant hands the whole catalogue
/// back here. This runs before the app serves a request (Program.cs), so the rewrite always beats
/// the first upload and a recipe change can never be mistaken for a mapper's gameplay edit.
/// </para>
///
/// <para>
/// WHAT IT DELIBERATELY DOES NOT REACH, stated so the gap is documented rather than silent:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Difficulties that are not live</b> (<c>filename IS NULL</c>): dropped diffs and the blank
///     rows a BSS id allocation creates before its first upload. NULL is their correct value, not a
///     hole: the ingest reads "has a fingerprint" as "was in the previous current version", so
///     filling a dead row would make the next upload think a difficulty was removed.
///   </description></item>
///   <item><description>
///     <b>Sets with no current version</b>, i.e. a set created by the BSS PUT whose first package
///     upload never landed. Those are still 'hidden', can never have been ranked, and their first
///     successful upload writes the fingerprint anyway.
///   </description></item>
///   <item><description>
///     <b>Rows whose blob is missing or whose .osu no longer parses.</b> Logged and left NULL, so
///     the next boot retries. If such a row is on a ranked set, the ingest's unknown-reads-as-
///     changed rule means its next upload demotes even for a metadata-only edit. That is the
///     conservative direction, and one reviewer click undoes it.
///   </description></item>
/// </list>
/// </summary>
public static class GameplayFingerprintBackfill
{
    public static async Task RunAsync(Db db, IFileStore fileStore, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // The current version's manifest is the only honest source: it is what the download path
        // serves and what the next upload will be diffed against. Joining beatmaps -> the set's
        // current set_versions row gives every live difficulty its version, and the manifest is
        // then read once per version rather than once per difficulty (a two-diff set shares one).
        var stale = (await conn.QueryAsync<(long BeatmapId, string Filename, long VersionId)>(
                $"""
                 SELECT b.id AS BeatmapId, b.filename AS Filename, sv.id AS VersionId
                 FROM beatmaps b
                 JOIN beatmapsets s ON s.id = b.set_id
                 JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
                 WHERE b.filename IS NOT NULL
                   AND (b.gameplay_fingerprint IS NULL OR b.gameplay_fingerprint NOT LIKE @currentVersion)
                 ORDER BY sv.id
                 """,
                new { currentVersion = GameplayFingerprint.CurrentVersionLikePattern }))
            .ToList();

        if (stale.Count == 0)
            return;

        int filled = 0;
        var manifests = new Dictionary<long, IReadOnlyList<PackageFileEntry>>();

        foreach (var row in stale)
        {
            try
            {
                if (!manifests.TryGetValue(row.VersionId, out var manifest))
                {
                    manifest = (await conn.QueryAsync<(byte[] Sha256, long Size, string Filename)>(
                            """
                            SELECT vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
                            FROM version_files vf
                            JOIN files f ON f.sha256 = vf.sha256
                            WHERE vf.version_id = @versionId
                            """,
                            new { versionId = row.VersionId }))
                        .Select(f => new PackageFileEntry(f.Sha256, f.Size, f.Filename))
                        .ToList();

                    manifests[row.VersionId] = manifest;
                }

                var osu = GameplayFingerprint.Resolve(manifest, row.Filename);

                if (osu == null)
                {
                    // The row claims a filename the current version does not contain: a set whose
                    // liveness and manifest disagree. Nothing to compute from, so leave it NULL.
                    logger.LogWarning(
                        "Gameplay fingerprint backfill: beatmap {BeatmapId} claims \"{Filename}\", which is not in its set's current version.",
                        row.BeatmapId, row.Filename);
                    continue;
                }

                byte[] content;

                await using (var blob = await fileStore.OpenBlobReadAsync(osu.Sha256, ct))
                using (var buffer = new MemoryStream())
                {
                    await blob.CopyToAsync(buffer, ct);
                    content = buffer.ToArray();
                }

                var diff = BeatmapPackageParser.ParseDifficulty(row.Filename, content);

                await conn.ExecuteAsync(
                    "UPDATE beatmaps SET gameplay_fingerprint = @fingerprint WHERE id = @id",
                    new { id = row.BeatmapId, fingerprint = GameplayFingerprint.Compute(diff, manifest) });

                filled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Gameplay fingerprint backfill failed for beatmap {BeatmapId} ({Filename}); will retry next startup.",
                    row.BeatmapId, row.Filename);
            }
        }

        logger.LogInformation(
            "Gameplay fingerprint backfill: {Filled}/{Stale} beatmaps fingerprinted at v{Version}.",
            filled, stale.Count, GameplayFingerprint.VERSION);
    }
}
