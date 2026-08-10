using System.IO.Compression;
using LineBufferedReader = typebeat.Game.IO.LineBufferedReader;
using System.Security.Cryptography;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;
using typebeat.Game.Tests.Beatmaps;

namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// Everything the recalculation needs that is not in the database: the replay bytes, and the exact
/// beatmap the run was played on.
///
/// <para>Both are fetched over PUBLIC HTTP from a running typebeat instance and cached on disk,
/// deliberately. <c>GET /api/v2/scores/{id}/replay</c> is public by design, and
/// <c>GET /beatmapsets/{id}/download</c> serves the .osz; going through them means the tool needs
/// no access to the server's file-store volume and can be run from anywhere the database is
/// reachable. Reads only, and every byte is cached, so a re-run costs nothing.</para>
///
/// <para>The beatmap is matched by the replay's own MD5 hash, never by beatmap id: the hash is the
/// identity contract (<c>beatmaps.checksum_md5</c>, 001_init.sql), so a set whose package was
/// re-uploaded still resolves the exact .osu the run was judged against, or resolves nothing at all
/// rather than the wrong one.</para>
/// </summary>
public sealed class ReplayArchive : IDisposable
{
    private readonly HttpClient http;
    private readonly string cacheDir;
    private readonly string siteUrl;

    /// <summary>MD5 (lowercase hex) of a final .osu -> its decoded beatmap, built lazily per package.</summary>
    private readonly Dictionary<string, IBeatmap> beatmapsByHash = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<long> scannedSets = new();

    private static readonly TypeBeatRuleset ruleset = new();

    public ReplayArchive(string siteUrl, string cacheDir)
    {
        this.siteUrl = siteUrl.TrimEnd('/');
        this.cacheDir = cacheDir;

        Directory.CreateDirectory(Path.Combine(cacheDir, "replays"));
        Directory.CreateDirectory(Path.Combine(cacheDir, "sets"));

        http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "typebeat-score-recalc/1");

        LyricBeatmapDecoder.Register();
    }

    /// <summary>Cached .osr bytes for one score, or null when the server holds no replay for it.</summary>
    public async Task<byte[]?> ReplayBytesAsync(long scoreId, CancellationToken ct)
    {
        string path = Path.Combine(cacheDir, "replays", $"{scoreId}.osr");

        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return await File.ReadAllBytesAsync(path, ct);

        var response = await http.GetAsync($"{siteUrl}/api/v2/scores/{scoreId}/replay", ct);

        if (!response.IsSuccessStatusCode)
            return null;

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return bytes;
    }

    /// <summary>
    /// Pulls one set's package into the beatmap index. Cheap and idempotent; a set already scanned
    /// (or one with no downloadable package) is a no-op.
    /// </summary>
    public async Task IndexSetAsync(long setId, CancellationToken ct)
    {
        if (!scannedSets.Add(setId))
            return;

        string path = Path.Combine(cacheDir, "sets", $"{setId}.osz");

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            var response = await http.GetAsync($"{siteUrl}/beatmapsets/{setId}/download", ct);

            if (!response.IsSuccessStatusCode)
                return;

            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
        }

        IndexPackage(path);
    }

    /// <summary>Indexes every .osu inside one package file (.osz or .typb; both are zips).</summary>
    public void IndexPackage(string packagePath)
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
                string hash = Convert.ToHexStringLower(MD5.HashData(bytes));

                if (beatmapsByHash.ContainsKey(hash))
                    continue;

                if (DecodeOsu(bytes) is not IBeatmap decoded)
                    continue;

                // The .osu decoder has no idea what file it came from, so it leaves MD5Hash empty.
                // Stamp it here: the hash IS the beatmap's identity (beatmaps.checksum_md5), and it
                // is what a replay names its map by.
                decoded.BeatmapInfo.MD5Hash = hash;
                beatmapsByHash[hash] = decoded;
            }
        }
        catch (InvalidDataException)
        {
            // Not a readable package (a truncated download, or an HTML error page saved as .osz).
        }
    }

    /// <summary>Indexes every package in a directory, for a run against pre-downloaded files.</summary>
    public void IndexPackageDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (string file in Directory.EnumerateFiles(dir).OrderBy(f => f))
        {
            if (file.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".typb", StringComparison.OrdinalIgnoreCase))
                IndexPackage(file);
        }
    }

    private static IBeatmap? DecodeOsu(byte[] osu)
    {
        try
        {
            using var stream = new MemoryStream(osu);
            using var reader = new LineBufferedReader(stream);
            return Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The source beatmap whose final .osu hashes to <paramref name="md5"/>, or null.</summary>
    public IBeatmap? BeatmapForHash(string md5) => beatmapsByHash.GetValueOrDefault(md5);

    public IReadOnlyCollection<string> IndexedHashes => beatmapsByHash.Keys;

    /// <summary>
    /// Decodes an .osr into the score it describes plus the PLAYABLE beatmap it was played on.
    ///
    /// <para>The .osr is self-describing: its trailing <c>LegacyReplaySoloScoreInfo</c> blob carries
    /// the mods, the statistics and maximum_statistics, the rank and the base total score exactly as
    /// the client computed them, which is byte-for-byte what the server stored
    /// (<c>ScoreEndpoints.SubmitScore</c> serializes the submitted dictionaries verbatim). That is
    /// what makes an old-rule reproduction check possible per replay, without trusting anything the
    /// tool derived.</para>
    /// </summary>
    public DecodedReplay? Decode(byte[] osr)
    {
        var decoder = new SourceScoreDecoder(this);

        Score score;

        try
        {
            using var stream = new MemoryStream(osr);
            score = decoder.Parse(stream);
        }
        catch (LegacyScoreDecoder.BeatmapNotFoundException e)
        {
            return new DecodedReplay(null, null, null, e.Hash);
        }
        catch (Exception)
        {
            return null;
        }

        string hash = score.ScoreInfo.BeatmapInfo?.MD5Hash ?? string.Empty;

        if (BeatmapForHash(hash) is not IBeatmap source)
            return new DecodedReplay(null, null, null, hash);

        // Re-derive the playable beatmap with the FINAL mod list. The decoder builds one early, off
        // the legacy mod bitfield, before the score-info blob (which is where a type!beat mod like
        // Literate actually travels) has been read; Literate changes the CELL LIST, so a playable
        // beatmap built without it would judge a different map.
        var working = new TestWorkingBeatmap(source);
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, score.ScoreInfo.Mods);

        return new DecodedReplay(score, playable, hash, null);
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// <paramref name="Score"/> and <paramref name="Playable"/> are null when the beatmap could not
    /// be resolved, in which case <paramref name="MissingBeatmapHash"/> says which one was wanted.
    /// </summary>
    public sealed record DecodedReplay(Score? Score, IBeatmap? Playable, string? BeatmapHash, string? MissingBeatmapHash);

    /// <summary>
    /// The one abstract seam <see cref="LegacyScoreDecoder"/> leaves open: which ruleset, and where
    /// beatmaps come from. There is exactly one ruleset here, and beatmaps come from the index built
    /// out of the downloaded packages.
    /// </summary>
    private sealed class SourceScoreDecoder(ReplayArchive source) : LegacyScoreDecoder
    {
        protected override Ruleset GetRuleset(int rulesetId) => ruleset;

        protected override WorkingBeatmap GetBeatmap(string md5Hash)
        {
            if (source.BeatmapForHash(md5Hash) is not IBeatmap beatmap)
                throw new BeatmapNotFoundException(md5Hash);

            return new TestWorkingBeatmap(beatmap);
        }
    }
}
