using Newtonsoft.Json;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// The database-free score list: every <c>.osr</c> in <c>&lt;dir&gt;/replays</c>, with its stored
/// account read out of the replay itself.
///
/// <para>That is sound because an .osr's trailing <c>LegacyReplaySoloScoreInfo</c> blob carries the
/// statistics, maximum_statistics, mods and rank the client computed, and
/// <c>ScoreEndpoints.SubmitScore</c> stores the submitted dictionaries VERBATIM
/// (<c>JsonConvert.SerializeObject(statistics)</c>). So the replay is a faithful copy of the row's
/// statistics without any database access, which is exactly what an offline reproduction check
/// needs. <c>max_combo</c> rides in the legacy header, clamped to a ushort by the format, and
/// <c>total_score</c> is the client's own value before the server's bounds clamp.</para>
///
/// <para>What the .osr cannot know: <c>ranked</c>, <c>passed</c> and <c>pp</c>, none of which are in
/// the file, and the map's rate star ratings. Without help an offline run therefore assumes a
/// passed, ranked play (which is what a replay-carrying row on a leaderboard is) and prices pp only
/// for maps whose base star rating is supplied via <c>--stars</c>.</para>
///
/// <para><b>--scores closes that gap</b>, which is what makes an offline SUPERSEDE report complete
/// enough to decide on. It is a plain JSON object keyed by score id, and every field is optional:
/// <c>pp</c> (so the report can show pp before as well as after), <c>ranked</c> and <c>passed</c>
/// (so a failed or already-unranked row is not silently treated as a live leaderboard entry),
/// <c>beatmap_id</c> and <c>user_id</c> (so the leaderboard-impact section can group), and
/// <c>sr_dt</c> / <c>sr_ht</c> / <c>sr_literate</c> / <c>sr_literate_dt</c> / <c>sr_literate_ht</c>
/// (so a rate or Literate play prices at all rather than at nothing). Nothing in it is a secret and
/// none of it is a replay, so exporting it is the least-privilege way to hand a sweep over.</para>
///
/// <para>It stays an ANALYSIS mode either way: both applies refuse <c>--offline</c>, and an
/// <c>--expect-superseded</c> count must come from a DATABASE supersede-report, never from an
/// offline one, since the offline row list is whatever .osr files were exported.</para>
/// </summary>
public static class OfflineScores
{
    public static List<StoredScore> Load(string dir, ReplayArchive source, string? starsFile, string? scoresFile = null)
    {
        var stars = LoadStars(starsFile);
        var supplied = LoadSuppliedRows(scoresFile);
        var scores = new List<StoredScore>();

        string replayDir = Path.Combine(dir, "replays");

        if (!Directory.Exists(replayDir))
            return scores;

        foreach (string path in Directory.EnumerateFiles(replayDir, "*.osr").OrderBy(p => p))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(path), out long scoreId))
                continue;

            var decoded = source.Decode(File.ReadAllBytes(path));
            var row = supplied.GetValueOrDefault(scoreId);

            if (decoded?.Score is null)
            {
                // Still listed, so the report accounts for it rather than silently losing it.
                scores.Add(Placeholder(scoreId, decoded?.MissingBeatmapHash));
                continue;
            }

            var info = decoded.Score.ScoreInfo;
            string hash = decoded.BeatmapHash ?? string.Empty;

            scores.Add(new StoredScore(
                ScoreId: scoreId,
                BeatmapId: row?.BeatmapId ?? 0,
                SetId: 0,
                TotalScore: info.TotalScore,
                Accuracy: info.Accuracy,
                Completion: TypeBeatScoreProcessor.ComputeCompletion(info),
                MaxCombo: info.MaxCombo,
                // The server never stores the silver ranks: ScoringContract.RankFromCompletion
                // emits X/S/A/B/C/D only, so an SH/XH out of the client reads as its base grade.
                Rank: info.Rank.ToString() switch { "XH" => "X", "SH" => "S", var r => r },
                Passed: row?.Passed ?? true,
                Ranked: row?.Ranked ?? true,
                HasReplay: true,
                StatisticsJson: WireCounts.Serialize(WireCounts.From(info.Statistics)),
                MaximumStatisticsJson: WireCounts.Serialize(WireCounts.From(info.MaximumStatistics)),
                ModsJson: SerializeMods(info),
                Pp: row?.Pp ?? 0,
                PpKnown: row?.Pp is not null,
                BaseStars: row?.Stars ?? stars.GetValueOrDefault(hash),
                SrDt: row?.SrDt,
                SrHt: row?.SrHt,
                SrLiterate: row?.SrLiterate,
                SrLiterateDt: row?.SrLiterateDt,
                SrLiterateHt: row?.SrLiterateHt,
                // An offline run has no beatmap row, so it cannot tell a package it failed to index
                // from one that was re-uploaded. Every miss reads as beatmap-missing, which is the
                // conservative direction: it is the case supersede-apply refuses to write around.
                CurrentChecksumMd5: string.Empty,
                UserId: row?.UserId ?? 0));
        }

        return scores;
    }

    /// <summary>The optional per-score columns an .osr does not carry. Every field is nullable.</summary>
    private sealed class SuppliedRow
    {
        [JsonProperty("pp")] public double? Pp { get; set; }
        [JsonProperty("ranked")] public bool? Ranked { get; set; }
        [JsonProperty("passed")] public bool? Passed { get; set; }
        [JsonProperty("beatmap_id")] public long? BeatmapId { get; set; }
        [JsonProperty("user_id")] public long? UserId { get; set; }
        [JsonProperty("stars")] public double? Stars { get; set; }
        [JsonProperty("sr_dt")] public double? SrDt { get; set; }
        [JsonProperty("sr_ht")] public double? SrHt { get; set; }
        [JsonProperty("sr_literate")] public double? SrLiterate { get; set; }
        [JsonProperty("sr_literate_dt")] public double? SrLiterateDt { get; set; }
        [JsonProperty("sr_literate_ht")] public double? SrLiterateHt { get; set; }
    }

    private static Dictionary<long, SuppliedRow> LoadSuppliedRows(string? file)
    {
        if (file is null || !File.Exists(file))
            return new Dictionary<long, SuppliedRow>();

        var parsed = JsonConvert.DeserializeObject<Dictionary<string, SuppliedRow>>(File.ReadAllText(file));
        var rows = new Dictionary<long, SuppliedRow>();

        foreach (var (key, value) in parsed ?? new Dictionary<string, SuppliedRow>())
        {
            if (long.TryParse(key, out long id) && value is not null)
                rows[id] = value;
        }

        return rows;
    }

    /// <summary>A row the tool could not decode, kept so the report can count it.</summary>
    private static StoredScore Placeholder(long scoreId, string? missingHash) => new(
        scoreId, 0, 0, 0, 0, 0, 0, "?", true, true, true, "{}", "{}", "[]", 0, false, 0, null, null, null, null, null, string.Empty, 0);

    /// <summary>The stored mods jsonb shape ([{acronym, settings}]), rebuilt from the replay's APIMods.</summary>
    private static string SerializeMods(typebeat.Game.Scoring.ScoreInfo info)
    {
        var mods = info.APIMods.Select(m => m.Settings.TryGetValue("speed_change", out object? speed)
            ? (object)new { acronym = m.Acronym, settings = new { speed_change = Convert.ToDouble(speed) } }
            : new { acronym = m.Acronym }).ToList();

        return mods.Count == 0 ? "[]" : JsonConvert.SerializeObject(mods);
    }

    private static Dictionary<string, double> LoadStars(string? file)
    {
        if (file is null || !File.Exists(file))
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var parsed = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(file));
        return new Dictionary<string, double>(parsed ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
    }
}
