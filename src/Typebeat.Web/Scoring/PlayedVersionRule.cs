using Dapper;
using Npgsql;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Scoring;

/// <summary>
/// THE VERSION RULE of <see cref="SetRankRefund"/> (backlog 352): a play stored unranked on a
/// pending set is carried onto the ranked board only when the map it was played on is, as far as
/// gameplay goes, the map that was ranked. Without it a play on an earlier upload (different
/// timing, a different recording) was re-ranked onto the new version's board, which is the one
/// thing the backlog 173 demotion exists to stop.
///
/// <para>
/// WHICH VERSION A PLAY WAS ON. The score row does not say; its TOKEN does.
/// <c>score_tokens.beatmap_hash</c> (001_init.sql) is the MD5 of the exact .osu the client held
/// when it asked for the token, and both token paths refuse any hash that is not the difficulty's
/// checksum at that moment (<c>ScoreEndpoints</c> for desktop, <c>PlayEndpoints</c> stamps the
/// served checksum for the browser). So the hash names the .osu bytes of the version that was
/// current when the play began.
/// </para>
///
/// <para>
/// THE RULE. A row is carried when its token's hash equals the difficulty's CURRENT checksum
/// (<see cref="Verdict.SameBytes"/>, no blob is read), or when the two versions share a
/// <see cref="GameplayFingerprint"/> (<see cref="Verdict.SameGameplay"/>): a title fix, a new
/// background, a beatdrop or an original-script edit moves the hash but not the gameplay and must
/// not strand the plays made before it, which is exactly the line backlog 173 draws for demotion.
/// </para>
///
/// <para>
/// THE OLD FINGERPRINT IS RECOVERABLE, and that is what makes the second arm possible. Every
/// <c>set_versions</c> row is an immutable snapshot and <c>version_files</c> keeps its whole
/// manifest (filename to sha256) for ever, and the content-addressed blobs are never deleted. So
/// for any earlier version this reads each .osu blob in the manifest, finds the one whose MD5 is
/// the token's hash, and computes <see cref="GameplayFingerprint.Compute"/> over it against THAT
/// version's own manifest (so the audio hashed is the recording that version shipped). It is the
/// same function the ingest's demotion check uses, fed the same shape of input.
/// </para>
///
/// <para>
/// WHAT IT DECLINES, all as <see cref="Verdict.Unknown"/> or <see cref="Verdict.Changed"/>, and
/// both mean "stays unranked" (precision over recall, as every refund pass has it):
/// </para>
/// <list type="bullet">
///   <item><description>A score with no token row at all (nothing names its version).</description></item>
///   <item><description>A hash no stored version's .osu produces: a manifest edited in place
///   outside the ingest (the 2026-07-19 beatdrop prod op repointed <c>version_files</c> and the
///   checksum by hand), or a blob that is missing or no longer parses.</description></item>
///   <item><description>A hash whose .osu appears in more than one version with DIFFERENT
///   fingerprints (the same .osu shipped against two recordings): which one the play heard is
///   ambiguous, so it is carried only if every candidate agrees with the current one.</description></item>
/// </list>
///
/// <para>
/// WHAT THE FIRST ARM ACCEPTS, stated rather than hidden. Exact-hash equality is taken as proof
/// of sameness without looking at the audio, as the owner's rule specifies. A set that swapped
/// only its recording under a byte-identical .osu (possible since backlog 171) keeps its checksum,
/// so a play made on the old recording is carried. The ingest demotes such a set, so this only
/// reaches a row after a reviewer has looked at the new recording and ranked it again.
/// </para>
///
/// <para>
/// ONE INSTANCE PER PASS: the per-set history (every version's parsed .osu files) is read once
/// and cached, and only for a set some candidate actually needs it for.
/// </para>
/// </summary>
public sealed class PlayedVersionRule(IFileStore fileStore, ILogger logger)
{
    public enum Verdict
    {
        /// <summary>The token's hash is the difficulty's current checksum: the very same .osu.</summary>
        SameBytes,

        /// <summary>A different .osu, whose version has the current version's gameplay fingerprint.</summary>
        SameGameplay,

        /// <summary>The version the play was on has different gameplay. Stays unranked.</summary>
        Changed,

        /// <summary>The played version cannot be established or fingerprinted. Stays unranked.</summary>
        Unknown,
    }

    /// <summary>One .osu in one stored version, as the rule needs it.</summary>
    private sealed record OsuInVersion(int VersionNo, string Md5, long? BeatmapId, string Fingerprint);

    private readonly Dictionary<long, IReadOnlyList<OsuInVersion>> histories = new();

    public static bool IsCarried(Verdict verdict) => verdict is Verdict.SameBytes or Verdict.SameGameplay;

    public async Task<Verdict> JudgeAsync(NpgsqlConnection conn, GateRefund.CandidateRow row, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(row.PlayedHash))
            return Verdict.Unknown;

        // MD5 hex is compared case-blind: the parser writes lowercase, a client could send either.
        string current = row.CurrentChecksum.Trim().ToLowerInvariant();
        string played = row.PlayedHash.Trim().ToLowerInvariant();

        if (played == current)
            return Verdict.SameBytes;

        var history = await historyOfAsync(conn, row.SetId, ct);

        var currentPrints = history
                            .Where(o => o.VersionNo == row.CurrentVersion && o.Md5 == current)
                            .Select(o => o.Fingerprint)
                            .Distinct()
                            .ToList();

        // The .osu bytes carry the embedded BeatmapID, so an MD5 match is already this
        // difficulty; the id test is a belt for a file that states none.
        var playedPrints = history
                           .Where(o => o.Md5 == played && (o.BeatmapId is null || o.BeatmapId == row.BeatmapId))
                           .Select(o => o.Fingerprint)
                           .Distinct()
                           .ToList();

        if (currentPrints.Count != 1 || playedPrints.Count == 0)
            return Verdict.Unknown;

        return playedPrints.All(p => p == currentPrints[0]) ? Verdict.SameGameplay : Verdict.Changed;
    }

    private async Task<IReadOnlyList<OsuInVersion>> historyOfAsync(NpgsqlConnection conn, long setId, CancellationToken ct)
    {
        if (histories.TryGetValue(setId, out var cached))
            return cached;

        var files = (await conn.QueryAsync<(int VersionNo, byte[] Sha256, long Size, string Filename)>(
                """
                SELECT sv.version_no AS VersionNo, vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
                FROM set_versions sv
                JOIN version_files vf ON vf.version_id = sv.id
                JOIN files f ON f.sha256 = vf.sha256
                WHERE sv.set_id = @setId
                """,
                new { setId }))
            .ToList();

        var history = new List<OsuInVersion>();

        // A .osu shared by several versions is read and parsed once.
        var parsed = new Dictionary<string, ParsedDifficulty?>();

        foreach (var version in files.GroupBy(f => f.VersionNo))
        {
            var manifest = version.Select(f => new PackageFileEntry(f.Sha256, f.Size, f.Filename)).ToList();

            foreach (var osu in manifest.Where(f => f.Filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                if (!parsed.TryGetValue(osu.Sha256Hex, out var difficulty))
                {
                    difficulty = await parseAsync(osu, setId, ct);
                    parsed[osu.Sha256Hex] = difficulty;
                }

                if (difficulty == null)
                    continue;

                history.Add(new OsuInVersion(
                    version.Key, difficulty.ChecksumMd5.ToLowerInvariant(), difficulty.BeatmapId, GameplayFingerprint.Compute(difficulty, manifest)));
            }
        }

        histories[setId] = history;
        return history;
    }

    private async Task<ParsedDifficulty?> parseAsync(PackageFileEntry osu, long setId, CancellationToken ct)
    {
        try
        {
            await using var blob = await fileStore.OpenBlobReadAsync(osu.Sha256, ct);
            using var buffer = new MemoryStream();
            await blob.CopyToAsync(buffer, ct);

            return BeatmapPackageParser.ParseDifficulty(osu.Filename, buffer.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing or unparseable blob fingerprints nothing: any play that needed it is
            // declined as Unknown, and the next pass tries again.
            logger.LogWarning(ex, "Version rule: set {SetId}'s \"{Filename}\" ({Sha256}) could not be read.", setId, osu.Filename, osu.Sha256Hex);
            return null;
        }
    }
}
