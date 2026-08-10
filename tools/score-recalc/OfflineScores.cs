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
/// <para>What it cannot know: <c>ranked</c>, <c>passed</c> and <c>pp</c>, none of which are in the
/// file. Offline runs therefore assume a passed, ranked play (which is what a replay-carrying row
/// on a leaderboard is), and price pp only for maps whose star rating is supplied via
/// <c>--stars</c>. It is an ANALYSIS mode; 'apply' refuses to run in it.</para>
/// </summary>
public static class OfflineScores
{
    public static List<StoredScore> Load(string dir, ReplayArchive source, string? starsFile)
    {
        var stars = LoadStars(starsFile);
        var scores = new List<StoredScore>();

        string replayDir = Path.Combine(dir, "replays");

        if (!Directory.Exists(replayDir))
            return scores;

        foreach (string path in Directory.EnumerateFiles(replayDir, "*.osr").OrderBy(p => p))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(path), out long scoreId))
                continue;

            var decoded = source.Decode(File.ReadAllBytes(path));

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
                BeatmapId: 0,
                SetId: 0,
                TotalScore: info.TotalScore,
                Accuracy: info.Accuracy,
                Completion: TypeBeatScoreProcessor.ComputeCompletion(info),
                MaxCombo: info.MaxCombo,
                // The server never stores the silver ranks: ScoringContract.RankFromCompletion
                // emits X/S/A/B/C/D only, so an SH/XH out of the client reads as its base grade.
                Rank: info.Rank.ToString() switch { "XH" => "X", "SH" => "S", var r => r },
                Passed: true,
                Ranked: true,
                HasReplay: true,
                StatisticsJson: WireCounts.Serialize(WireCounts.From(info.Statistics)),
                MaximumStatisticsJson: WireCounts.Serialize(WireCounts.From(info.MaximumStatistics)),
                ModsJson: SerializeMods(info),
                Pp: 0,
                PpKnown: false,
                BaseStars: stars.GetValueOrDefault(hash),
                SrDt: null,
                SrHt: null));
        }

        return scores;
    }

    /// <summary>A row the tool could not decode, kept so the report can count it.</summary>
    private static StoredScore Placeholder(long scoreId, string? missingHash) => new(
        scoreId, 0, 0, 0, 0, 0, 0, "?", true, true, true, "{}", "{}", "[]", 0, false, 0, null, null);

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
