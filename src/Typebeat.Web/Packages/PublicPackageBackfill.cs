using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// The one-time copy of every PUBLISHED set's latest package into the public bucket (backlog 364,
/// D8): the sets uploaded before the bucket existed have <c>set_versions.public_key</c> NULL, and
/// until they get one their downloads keep streaming from the box.
///
/// <para>A hosted service, started after the startup sweeps and NEVER awaited by them, because a
/// catalogue of packages over the network takes far longer than a deploy's health window. It
/// returns at once when the store is disabled, so an unconfigured box does nothing at all.
/// Idempotent and resumable: it only ever picks versions whose key is still NULL, one at a time,
/// through <see cref="PackageIngest.PublishPackageAsync"/> (the same upload the ingest does), so a
/// restart halfway simply carries on. A version that fails is logged and left NULL for the next
/// boot, never retried in a loop.</para>
///
/// <para>Published sets only: a hidden or removed set's download never redirects, so there is no
/// reason to hand its bytes to a CDN, and a takedown must never be undone by a backfill.</para>
/// </summary>
public sealed class PublicPackageBackfill(
    Db db,
    IPublicObjectStore publicStore,
    PackageIngest ingest,
    ILogger<PublicPackageBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!publicStore.Enabled)
            return;

        // Let the host finish starting before any network traffic begins.
        await Task.Yield();

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown mid-run: the next boot resumes from the versions still NULL.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Public package backfill stopped early; the next boot resumes it.");
        }
    }

    /// <summary>
    /// One pass: uploads the latest version of every published set that has no public copy yet.
    /// Returns how many versions it uploaded. Public so tests can drive a pass directly.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        if (!publicStore.Enabled)
            return 0;

        List<long> pending;

        await using (var conn = await db.OpenAsync(ct))
        {
            pending = (await conn.QueryAsync<long>(
                """
                SELECT v.id
                FROM set_versions v
                JOIN beatmapsets s ON s.id = v.set_id
                WHERE v.public_key IS NULL
                  AND v.package_key IS NOT NULL
                  AND s.status IN ('pending', 'unranked', 'ranked')
                  AND v.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = v.set_id)
                ORDER BY v.id
                """)).ToList();
        }

        if (pending.Count == 0)
            return 0;

        logger.LogInformation("Public package backfill: {Count} published set(s) have no public copy yet.", pending.Count);

        int uploaded = 0;

        foreach (long versionId in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (await ingest.PublishPackageAsync(versionId, onlyIfMissing: true, ct))
                uploaded++;
        }

        logger.LogInformation("Public package backfill: {Uploaded} of {Count} uploaded.", uploaded, pending.Count);
        return uploaded;
    }
}
