using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Deletes the cover jpegs of set versions that can no longer be shown (backlog 365). Every ingest
/// renders all eight buckets (four sizes, @1x and @2x) under <c>covers/{set}/{version}/</c> and
/// <c>beatmapsets.cover_key</c> only ever points at one version, so the older versions' covers were
/// pure dead weight on the shared disk, never reclaimed.
///
/// <para>The rule is the package prune's (<c>prunePackagesBeyondLatestTwoAsync</c>): keep the latest
/// version and the one before it, as a safety margin for a request still serving the previous
/// cover, and ALSO keep whichever version <c>cover_key</c> names, because a version whose cover
/// generation failed or which had no background leaves cover_key pointing at an older one, and that
/// older one is then the cover the site is actually showing.</para>
///
/// <para>The keys come from <see cref="CoverGenerator.Sizes"/>, since <see cref="IFileStore"/>
/// cannot enumerate a prefix. Shared by the ingest (which prunes the one version that just fell
/// out of the window) and the housekeeping sweep (the one-time catch-up over every older version).</para>
/// </summary>
public static class CoverPrune
{
    /// <summary>Every key one version's cover generation writes.</summary>
    public static IEnumerable<string> KeysFor(long setId, int versionNo)
    {
        foreach (var (name, _, _) in CoverGenerator.Sizes)
        {
            yield return StoreKeys.Cover(setId, versionNo, name);
            yield return StoreKeys.Cover(setId, versionNo, name + "@2x");
        }
    }

    /// <summary>
    /// The versions whose covers are prunable for a set whose latest version is
    /// <paramref name="latestVersionNo"/> and whose cover_key is <paramref name="coverKey"/>: every
    /// version below latest - 1, except the one cover_key names. Pure.
    /// </summary>
    public static IEnumerable<int> PrunableVersions(long setId, int latestVersionNo, string? coverKey)
    {
        for (int versionNo = latestVersionNo - 2; versionNo >= 1; versionNo--)
        {
            if (!string.Equals(StoreKeys.CoverPrefix(setId, versionNo), coverKey, StringComparison.Ordinal))
                yield return versionNo;
        }
    }

    /// <summary>
    /// Deletes one version's covers unless it is the one <paramref name="coverKey"/> names. Returns
    /// how many objects actually existed and were removed.
    /// </summary>
    public static async Task<int> PruneVersionAsync(IFileStore store, long setId, int versionNo, string? coverKey, CancellationToken ct = default)
    {
        if (string.Equals(StoreKeys.CoverPrefix(setId, versionNo), coverKey, StringComparison.Ordinal))
            return 0;

        int deleted = 0;

        foreach (string key in KeysFor(setId, versionNo))
        {
            if (!await store.ObjectExistsAsync(key, ct))
                continue;

            await store.DeleteObjectAsync(key, ct);
            deleted++;
        }

        return deleted;
    }
}
