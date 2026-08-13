using System.IO.Compression;
using System.Security.Cryptography;

namespace Typebeat.Tools.RepriceReport;

/// <summary>
/// The one thing this report needs that the database does not hold: the .osu bytes a beatmap row was
/// rated from, so the rating can be recomputed under today's code.
///
/// <para>Fetched over PUBLIC HTTP from a running typebeat instance and cached on disk, the same way
/// <c>tools/score-recalc</c>'s <c>ReplayArchive</c> fetches packages, and with a BYTE-COMPATIBLE
/// CACHE LAYOUT (<c>&lt;cache&gt;/sets/&lt;set id&gt;.osz</c>): point <c>--cache</c> at a
/// score-recalc cache and every package it has already downloaded is reused rather than fetched
/// again. Deliberately a separate class rather than a shared one, for now: score-recalc's copy also
/// decodes beatmaps through the GAME's decoder and is mid-use for backlog 151, so lifting code out
/// of it would put a refactor of a tool someone is running underneath a report that does not need
/// one. What is here is the HTTP-and-disk half only, roughly thirty lines, and the two should be
/// folded together once 151 is done.</para>
///
/// <para>THE .osu IS FOUND BY MD5, never by filename: <c>beatmaps.checksum_md5</c> is the identity
/// contract (001_init.sql), so a hash match means the recomputation is running over the exact bytes
/// the stored rating came from and the whole delta is the code change. A filename match would
/// silently mix a re-uploaded map's content change into the diff, which is the one thing this report
/// must not do; a row whose hash is not in the package is reported as unresolved instead.</para>
/// </summary>
internal sealed class PackageSource : IDisposable
{
    private readonly HttpClient http;
    private readonly string cacheDir;
    private readonly string siteUrl;

    /// <summary>MD5 (lowercase hex) of a final .osu -> its bytes.</summary>
    private readonly Dictionary<string, byte[]> osuByHash = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<long> scannedSets = new();

    public PackageSource(string siteUrl, string cacheDir)
    {
        this.siteUrl = siteUrl.TrimEnd('/');
        this.cacheDir = cacheDir;

        Directory.CreateDirectory(Path.Combine(cacheDir, "sets"));

        http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "typebeat-reprice-report/1");
    }

    /// <summary>Sets whose package could not be fetched at all, so their maps cannot be re-rated.</summary>
    public HashSet<long> UnavailableSets { get; } = new();

    public int IndexedCount => osuByHash.Count;

    /// <summary>
    /// Pulls one set's package into the index. Cheap and idempotent: a set already scanned, or one
    /// already in the cache, costs no request.
    /// </summary>
    public async Task IndexSetAsync(long setId, CancellationToken ct)
    {
        if (!scannedSets.Add(setId))
            return;

        string path = Path.Combine(cacheDir, "sets", $"{setId}.osz");

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            HttpResponseMessage response;

            try
            {
                response = await http.GetAsync($"{siteUrl}/beatmapsets/{setId}/download", ct);
            }
            catch (HttpRequestException)
            {
                UnavailableSets.Add(setId);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                UnavailableSets.Add(setId);
                return;
            }

            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
        }

        if (!IndexPackage(path))
            UnavailableSets.Add(setId);
    }

    /// <summary>Indexes every .osu inside one package file (.osz or .typb; both are zips).</summary>
    public bool IndexPackage(string packagePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                byte[] bytes = buffer.ToArray();
                osuByHash.TryAdd(Convert.ToHexStringLower(MD5.HashData(bytes)), bytes);
            }

            return true;
        }
        catch (InvalidDataException)
        {
            // Not a readable package: a truncated download, or an HTML error page saved as .osz.
            return false;
        }
    }

    /// <summary>Indexes every package in a directory, for a run against pre-downloaded files.</summary>
    public int IndexPackageDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        int packages = 0;

        foreach (string file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            if ((file.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".typb", StringComparison.OrdinalIgnoreCase))
                && IndexPackage(file))
            {
                packages++;
            }
        }

        return packages;
    }

    /// <summary>The .osu bytes whose MD5 is <paramref name="md5"/>, or null when none was indexed.</summary>
    public byte[]? OsuForHash(string? md5)
        => md5 is null ? null : osuByHash.GetValueOrDefault(md5);

    public void Dispose() => http.Dispose();
}
