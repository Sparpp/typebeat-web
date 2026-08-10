using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Tools.ScoreRecalc;

// typebeat-web score recalculation (backlog 114).
//
// Backlog 109 changed what a typo does to a score: a typed-through wrong char used to spend its
// cell's judgement on a Miss the instant it landed and could never be recovered; now the cell's
// result is DEFERRED, so correcting it earns the cell back and only an uncorrected typo misses, at
// the seal. Every stored score was judged under the old rule, so its statistics, max_combo,
// accuracy, completion and rank are priced against a model the client no longer uses. The pp
// backfill cannot fix that, it reprices a given set of statistics rather than re-deriving them.
//
// This replays each score's stored .osr through the game's own TypingEngine and
// TypeBeatScoreProcessor, TWICE: once under the OLD rule, which must reproduce the stored
// statistics exactly or the row is refused, and once under the new one. Everything derived from the
// new statistics comes from the server's own ScoringContract and PerformancePoints.
//
// IT WRITES NOTHING unless the apply command is used AND --i-understand-this-writes-to-the-database
// is passed. Reading is the default and is the whole tool for every other purpose.

/// <summary>
/// The entry point, spelled out rather than left to top-level statements: those generate a type
/// called <c>Program</c>, and <c>tests/Typebeat.WireCompat</c> (which references this project so it
/// can test the recalculation) also boots the SERVER through <c>WebApplicationFactory&lt;Program&gt;</c>.
/// Two <c>Program</c>s in scope is an ambiguity the test project cannot resolve.
/// </summary>
internal static class ScoreRecalcTool
{
    private static Task<int> Main(string[] args) => Cli.RunAsync(args);
}

internal static class Cli
{
    private const string default_site = "https://typebeat.mingda.sh";
    private const string write_flag = "--i-understand-this-writes-to-the-database";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];

        if (command is not ("report" and not null) && command != "apply")
        {
            Console.Error.WriteLine($"error: unknown command '{command}'.");
            Usage();
            return 1;
        }

        var options = Options.Parse(args);

        if (command == "apply" && !options.Confirmed)
        {
            Console.Error.WriteLine($"error: 'apply' writes to the database. Re-run it with {write_flag}.");
            Console.Error.WriteLine("       Run 'report' first, and read the report.");
            return 1;
        }

        using var source = new ReplayArchive(options.Site, options.CacheDir);
        using var cts = new CancellationTokenSource();

        List<StoredScore> scores;
        NpgsqlConnection? conn = null;

        if (options.OfflineDir is string offlineDir)
        {
            source.IndexPackageDirectory(Path.Combine(offlineDir, "sets"));
            scores = OfflineScores.Load(offlineDir, source, options.StarsFile);
            Console.WriteLine($"offline: {scores.Count} replay(s) from {offlineDir}, {source.IndexedHashes.Count} beatmap(s) indexed");
        }
        else
        {
            conn = new NpgsqlConnection(options.ConnectionString);
            await conn.OpenAsync(cts.Token);
            scores = await LoadScoresAsync(conn, options, cts.Token);
            Console.WriteLine($"database: {scores.Count} score row(s) selected");

            foreach (long setId in scores.Select(s => s.SetId).Distinct())
                await source.IndexSetAsync(setId, cts.Token);
        }

        var results = new List<RecalcResult>();

        foreach (var stored in scores)
        {
            ReplayArchive.DecodedReplay? decoded = null;

            if (stored.HasReplay)
            {
                byte[]? osr = options.OfflineDir is string dir
                    ? ReadOfflineReplay(dir, stored.ScoreId)
                    : await source.ReplayBytesAsync(stored.ScoreId, cts.Token);

                decoded = osr is null ? null : source.Decode(osr);
            }

            RecalcResult result;

            try
            {
                result = Recalculation.Run(stored, decoded, options.BackfillMistypes);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"score {stored.ScoreId}: {e.GetType().Name}: {e.Message}");
                continue;
            }

            results.Add(result);
        }

        Report.Print(results, Console.Out);

        if (options.OutFile is string outFile)
        {
            await File.WriteAllTextAsync(outFile, JsonConvert.SerializeObject(results, Formatting.Indented), cts.Token);
            Console.WriteLine($"\nfull per-score detail written to {outFile}");
        }

        if (command == "apply")
        {
            if (conn is null)
            {
                Console.Error.WriteLine("error: 'apply' needs the database; it cannot run with --offline.");
                return 1;
            }

            int written = await ApplyAsync(conn, results, cts.Token);
            Console.WriteLine($"\napplied: {written} score row(s) updated.");
        }
        else
        {
            Console.WriteLine("\nDRY RUN. Nothing was written. Use 'apply' to write these values.");
        }

        conn?.Dispose();
        return 0;
    }

    // -----------------------------------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------------------------------

    private static async Task<List<StoredScore>> LoadScoresAsync(NpgsqlConnection conn, Options options, CancellationToken ct)
    {
        string filter = options.ScoreIds.Count > 0 ? "AND s.id = ANY(@ids)" : string.Empty;
        string limit = options.Limit is int n ? $"LIMIT {n.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var rows = await conn.QueryAsync<StoredScore>(
            $"""
             SELECT s.id                         AS ScoreId,
                    s.beatmap_id                 AS BeatmapId,
                    b.set_id                     AS SetId,
                    s.total_score                AS TotalScore,
                    s.accuracy                   AS Accuracy,
                    s.completion                 AS Completion,
                    s.max_combo                  AS MaxCombo,
                    s.rank                       AS Rank,
                    s.passed                     AS Passed,
                    s.ranked                     AS Ranked,
                    s.replay_key IS NOT NULL     AS HasReplay,
                    s.statistics::text           AS StatisticsJson,
                    s.maximum_statistics::text   AS MaximumStatisticsJson,
                    s.mods::text                 AS ModsJson,
                    s.pp                         AS Pp,
                    true                         AS PpKnown,
                    b.difficulty_rating          AS BaseStars,
                    b.sr_dt                      AS SrDt,
                    b.sr_ht                      AS SrHt
             FROM scores s
             JOIN beatmaps b ON b.id = s.beatmap_id
             WHERE s.ruleset_id = 0 {filter}
             ORDER BY s.id
             {limit}
             """,
            new { ids = options.ScoreIds.ToArray() });

        return rows.ToList();
    }

    private static byte[]? ReadOfflineReplay(string dir, long scoreId)
    {
        string path = Path.Combine(dir, "replays", $"{scoreId}.osr");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // -----------------------------------------------------------------------------------------
    // Writing (the only mutating path in the tool)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the recalculated values for every score that actually MOVES, one transaction for the
    /// lot so a partial sweep can never leave half the leaderboard on one rule and half on the
    /// other. Skipped rows and unchanged rows are not touched at all.
    ///
    /// <para><c>pp_version</c> is stamped only when the price is settled, exactly as
    /// <c>PpBackfill</c> does: an unsettled row keeps version 0 so the next boot reprices it once
    /// its map has a rate star rating.</para>
    /// </summary>
    private static async Task<int> ApplyAsync(NpgsqlConnection conn, IReadOnlyList<RecalcResult> results, CancellationToken ct)
    {
        var moving = results.Where(r => r.Moves).ToList();

        if (moving.Count == 0)
            return 0;

        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var r in moving)
        {
            await conn.ExecuteAsync(
                """
                UPDATE scores
                SET statistics         = CAST(@statistics AS jsonb),
                    maximum_statistics = CAST(@maximumStatistics AS jsonb),
                    max_combo          = @maxCombo,
                    total_score        = @totalScore,
                    accuracy           = @accuracy,
                    completion         = @completion,
                    rank               = @rank,
                    ranked             = @ranked,
                    pp                 = @pp,
                    pp_version         = @ppVersion
                WHERE id = @id
                """,
                new
                {
                    id = r.Stored.ScoreId,
                    statistics = WireCounts.Serialize(r.NewStatistics!),
                    maximumStatistics = WireCounts.Serialize(r.NewMaximumStatistics!),
                    maxCombo = r.NewMaxCombo,
                    totalScore = r.NewTotalScore,
                    accuracy = r.NewAccuracy,
                    completion = r.NewCompletion,
                    rank = r.NewRank,
                    ranked = r.NewRanked,
                    pp = r.NewPp ?? 0,
                    ppVersion = r.PpSettled ? Typebeat.Web.Scoring.PerformancePoints.VERSION : 0,
                },
                tx);
        }

        await tx.CommitAsync(ct);
        return moving.Count;
    }

    // -----------------------------------------------------------------------------------------

    private static void Usage()
    {
        Console.WriteLine("""
            typebeat score recalculation (backlog 114)

            Re-derives every stored score's statistics from its replay under the current judgement
            rule, then reprices everything the server derives from them. Reads by default.

              report   Recalculate and print the before/after report. Writes NOTHING. (the dry run)
              apply    Same, then write the moved values back. Needs the confirmation flag below.

            Options:
              --db <conn>        Postgres connection string (default: $TYPEBEAT_DB, else the dev default)
              --site <url>       typebeat instance to fetch replays and beatmap packages from
                                 (default: https://typebeat.mingda.sh). Read-only, public routes.
              --cache <dir>      where fetched .osr/.osz are kept (default: ./.score-recalc-cache)
              --score <id>       recalculate only this score; repeatable
              --limit <n>        only the first n rows
              --out <file.json>  write the full per-score detail as JSON
              --offline <dir>    no database at all: recalculate every .osr in <dir>/replays against
                                 the packages in <dir>/sets, using each replay's own embedded score
                                 info as the stored account. Analysis only; 'apply' refuses it.
              --stars <file>     offline only: {"<beatmap md5>": <star rating>} so pp can be priced
              --backfill-mistypes
                                 also write a mistype count into rows that predate the stat
                                 (backlog 72). Off by default: introducing it reprices those rows
                                 on a dimension this sweep is not about.

              --i-understand-this-writes-to-the-database
                                 required by 'apply', and by nothing else

            Examples:
              dotnet run --project tools/score-recalc -- report --out recalc.json
              dotnet run --project tools/score-recalc -- apply --i-understand-this-writes-to-the-database
            """);
    }

    private sealed class Options
    {
        public string ConnectionString { get; private init; } = string.Empty;
        public string Site { get; private init; } = default_site;
        public string CacheDir { get; private init; } = ".score-recalc-cache";
        public string? OfflineDir { get; private init; }
        public string? StarsFile { get; private init; }
        public string? OutFile { get; private init; }
        public int? Limit { get; private init; }
        public List<long> ScoreIds { get; } = new();
        public bool Confirmed { get; private init; }
        public bool BackfillMistypes { get; private init; }

        public static Options Parse(string[] args)
        {
            string? db = null, site = null, cache = null, offline = null, stars = null, outFile = null;
            int? limit = null;
            bool confirmed = false, backfillMistypes = false;
            var ids = new List<long>();

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (arg)
                {
                    case "--db": db = Next(); break;
                    case "--site": site = Next(); break;
                    case "--cache": cache = Next(); break;
                    case "--offline": offline = Next(); break;
                    case "--stars": stars = Next(); break;
                    case "--out": outFile = Next(); break;
                    case "--limit": limit = int.TryParse(Next(), out int n) ? n : null; break;
                    case "--score":
                        if (long.TryParse(Next(), out long id))
                            ids.Add(id);
                        break;
                    case "--backfill-mistypes": backfillMistypes = true; break;
                    case write_flag: confirmed = true; break;
                    default:
                        Console.Error.WriteLine($"warning: ignoring unknown option '{arg}'.");
                        break;
                }
            }

            var options = new Options
            {
                // Mirrors Db.ResolveConnectionString.
                ConnectionString = db
                                   ?? Environment.GetEnvironmentVariable("TYPEBEAT_DB")
                                   ?? "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat",
                Site = site ?? default_site,
                CacheDir = cache ?? ".score-recalc-cache",
                OfflineDir = offline,
                StarsFile = stars,
                OutFile = outFile,
                Limit = limit,
                Confirmed = confirmed,
                BackfillMistypes = backfillMistypes,
            };

            options.ScoreIds.AddRange(ids);
            return options;
        }
    }
}
