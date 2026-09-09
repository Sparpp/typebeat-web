using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Tools.RepriceReport;
using Typebeat.Web.Scoring;

// typebeat-web reprice report (backlog 152).
//
// 152 moved LENGTH pricing out of pp and into the star rating: pp's
// max(0.1, 1 + 0.50*log10(notes/100)) factor was DELETED, and stars gained an additive
// 0.12*max(0, log10(cells/100)) bonus. Its acceptance clause asks for a before/after diff over prod
// data, and nothing in the tree could produce one. PaceBackfill.RunAsync and PpBackfill.RunAsync are
// the only code that reprices a catalogue and both of them WRITE, at boot, with no dry-run path.
//
// This is that dry run. It reads a database and a site, recomputes both halves through the SERVER'S
// OWN code (BeatmapPackageParser.ParseDifficulty for the ratings, PerformancePoints.ForScore for the
// prices, i.e. exactly what the two backfills call), and prints what a reprice would do. It has no
// apply command, no --i-understand flag, and opens no write path: every SQL statement it issues is a
// SELECT. That is deliberate and is the difference between this tool and tools/score-recalc, which
// it is otherwise modelled on down to the cache layout.
//
// WHAT IT CHECKS, which is exactly what 152's acceptance clause names:
//
//   SR   every stored rating against a recomputation: delta in [0, +0.17], monotone in cells, with
//        the map's cell count printed because the bonus is a function of it.
//   pp   every stored price against a repricing under today's formula and the NEW ratings:
//        deflation graded by map length, no sign flips, and order changes only where length was the
//        differentiator, reported as per-board placement changes.
//
// Anything failing a predicate is listed BY ID. A summary that says "all within range" while hiding
// an outlier is the failure mode the whole thing exists to avoid.
//
// THE SR HALF IS SPENT. Backlog 269 replaced the star model wholesale and backlog 273 DELETED the
// length term this tool was written around, so a run against today's catalogue re-rates through the
// current model (it always calls the server's own code) and then measures the move against a bonus
// that no longer exists. Every SR predicate here is 152's and stays 152's, which means they now
// describe what a row stored between 152 and 273 was MADE OF rather than what a reprice would do.
// Kept, not deleted: the pp half is unaffected, and a forensic tool that can still decompose a
// legacy rating is worth more than one silently retuned to a model it was never about. See
// Length.cs for the same note next to the constants.

/// <summary>
/// The entry point, spelled out rather than left to top-level statements: those generate a type
/// called <c>Program</c>, and <c>tests/Typebeat.Web.Tests</c> (which references this project so it
/// can test the analysis) also boots the SERVER through <c>WebApplicationFactory&lt;Program&gt;</c>.
/// Two <c>Program</c>s in scope is an ambiguity the test project cannot resolve.
/// </summary>
internal static class RepriceReportTool
{
    private static Task<int> Main(string[] args) => RepriceCli.RunAsync(args);
}

/// <summary>Everything one run produced, which is what the report renders and what --out serializes.</summary>
internal sealed class RunContext
{
    public required string Site { get; init; }
    public required IReadOnlyList<SrRow> Maps { get; init; }
    public required IReadOnlyList<PpRow> Scores { get; init; }
    public required SrFindings Sr { get; init; }
    public required PpFindings Pp { get; init; }
    public required BoardFindings Boards { get; init; }
    public required Tolerances Tolerances { get; init; }
    public IReadOnlyCollection<long> UnavailableSets { get; init; } = Array.Empty<long>();
    public bool FullTable { get; init; }

    public int MapsReRated => Maps.Count(m => m.Resolved);

    public int ScoresRepriced => Scores.Count(s => s.Repriced);

    /// <summary>
    /// Nothing was re-rated, so every predicate below held over an empty set. A run like this must
    /// not read as a pass: it is the exact shape of the failure the report is written to avoid, one
    /// level up.
    /// </summary>
    public bool Vacuous => MapsReRated == 0;

    /// <summary>Every predicate 152's acceptance clause names, over the rows this run could read.</summary>
    public bool Passed => Sr.Passed && Pp.Passed && (Boards.Filtered || Boards.Passed);
}

internal static class RepriceCli
{
    private const string default_site = "https://typebeat.mingda.sh";

    /// <summary>Clean run, every predicate held.</summary>
    private const int exit_ok = 0;

    /// <summary>The tool could not run: bad options, no database, nothing to read.</summary>
    private const int exit_error = 1;

    /// <summary>The run completed and at least one predicate FAILED. The report says which.</summary>
    private const int exit_predicate_failed = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? exit_error : exit_ok;
        }

        if (args[0] != "report")
        {
            Console.Error.WriteLine($"error: unknown command '{args[0]}'. There is only 'report': this tool never writes.");
            Usage();
            return exit_error;
        }

        if (!RepriceOptions.TryParse(args, out var options, out string parseError))
        {
            Console.Error.WriteLine($"error: {parseError}");
            return exit_error;
        }

        using var cts = new CancellationTokenSource();
        using var packages = new PackageSource(options.Site, options.CacheDir);

        if (options.PackageDir is string dir)
            Console.WriteLine($"indexed {packages.IndexPackageDirectory(dir)} local package(s) from {dir}");

        await using var conn = new NpgsqlConnection(options.ConnectionString);

        try
        {
            await conn.OpenAsync(cts.Token);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: could not open the database ({e.GetType().Name}: {e.Message}).");
            Console.Error.WriteLine("       Pass --db, or set TYPEBEAT_DB. The tool only ever SELECTs.");
            return exit_error;
        }

        List<StoredBeatmap> beatmaps;
        List<StoredScoreRow> stored;
        List<SrRow> maps;
        List<PpRow> scores;
        HashSet<long> boardEligible;
        bool filtered = options.Limit is not null || options.BeatmapIds.Count > 0;

        // Every read is inside one handler, because they all fail the same interesting way: a
        // database older than the columns this report needs (sr_dt arrived in 020, the three
        // sr_literate ones in 029). An unhandled Npgsql stack trace tells its reader nothing about
        // that, and this tool is run by an operator against a snapshot, not by a developer.
        try
        {
            beatmaps = await LoadBeatmapsAsync(conn, options, cts.Token);

            if (beatmaps.Count == 0)
            {
                Console.Error.WriteLine("error: no beatmap rows matched. Nothing to report.");
                return exit_error;
            }

            Console.WriteLine($"database: {beatmaps.Count} beatmap row(s) selected");

            foreach (long setId in beatmaps.Select(b => b.SetId).Distinct())
                await packages.IndexSetAsync(setId, cts.Token);

            Console.WriteLine($"packages: {packages.IndexedCount} .osu indexed, {packages.UnavailableSets.Count} set(s) unavailable");

            maps = beatmaps.Select(m => Resolve(m, packages)).ToList();
            var byBeatmap = maps.ToDictionary(m => m.Stored.BeatmapId);

            stored = await LoadScoresAsync(conn, byBeatmap.Keys.ToArray(), cts.Token);
            Console.WriteLine($"database: {stored.Count} score row(s) selected");

            scores = stored.Select(s => PpRow.Price(s, byBeatmap[s.BeatmapId])).ToList();
            boardEligible = filtered ? new HashSet<long>() : await LoadBoardEligibleAsync(conn, scores, cts.Token);
        }
        catch (PostgresException e)
        {
            Console.Error.WriteLine($"error: the database rejected a read ({e.SqlState}: {e.MessageText}).");

            // 42703, undefined_column, is the interesting one and deserves the specific answer.
            if (e.SqlState == "42703")
            {
                Console.Error.WriteLine("       This report needs a schema at 029_literate_stars or later: it reads sr_dt, sr_ht");
                Console.Error.WriteLine("       and the three sr_literate columns. Point --db at a current snapshot. Migrating one");
                Console.Error.WriteLine("       is not this tool's job, and it will not do it.");
            }
            else
            {
                Console.Error.WriteLine("       The tool only ever SELECTs, so the account it connects as needs read access and");
                Console.Error.WriteLine("       nothing more. Check --db, or $TYPEBEAT_DB.");
            }

            return exit_error;
        }

        var run = new RunContext
        {
            Site = options.Site,
            Maps = maps,
            Scores = scores,
            Sr = SrAnalysis.Run(maps, options.Tolerances),
            Pp = PpAnalysis.Run(scores, options.Tolerances),
            Boards = BoardAnalysis.Analyse(scores, boardEligible, filtered),
            Tolerances = options.Tolerances,
            UnavailableSets = packages.UnavailableSets,
            FullTable = options.FullTable,
        };

        Report.Print(run, Console.Out);

        if (options.OutFile is string outFile)
        {
            await File.WriteAllTextAsync(outFile, Serialize(run), cts.Token);
            Console.WriteLine($"\nfull per-row detail written to {outFile}");
        }

        if (run.Vacuous)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("error: NOT ONE map was re-rated, so nothing above was actually checked. The predicates");
            Console.Error.WriteLine("       'held' over an empty set, which is not the same thing as holding. Usually this is");
            Console.Error.WriteLine("       an unreachable --site or a cold cache: see the unavailable set list above.");
            return exit_error;
        }

        return run.Passed ? exit_ok : exit_predicate_failed;
    }

    /// <summary>
    /// The .osu a beatmap row was rated from, matched BY CHECKSUM so the recomputation runs over the
    /// exact bytes the stored rating came from. A miss is reported by id rather than fudged onto a
    /// same-named file, which would mix a re-upload's content change into the diff.
    /// </summary>
    private static SrRow Resolve(StoredBeatmap stored, PackageSource packages)
    {
        if (packages.OsuForHash(stored.ChecksumMd5) is byte[] osu)
            return SrRow.Recompute(stored, osu);

        return packages.UnavailableSets.Contains(stored.SetId)
            ? SrRow.Unresolved(stored, MapResolution.PackageUnavailable, $"set {stored.SetId} package could not be fetched")
            : SrRow.Unresolved(stored, MapResolution.HashNotInPackage, $"no .osu in set {stored.SetId} hashes to {stored.ChecksumMd5}");
    }

    // -----------------------------------------------------------------------------------------
    // Reading. Every statement below is a SELECT, and there is no other kind in this tool.
    // -----------------------------------------------------------------------------------------

    private static async Task<List<StoredBeatmap>> LoadBeatmapsAsync(NpgsqlConnection conn, RepriceOptions options, CancellationToken ct)
    {
        string filter = options.BeatmapIds.Count > 0 ? "AND b.id = ANY(@ids)" : string.Empty;
        string limit = options.Limit is int n ? $"LIMIT {n.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var rows = await conn.QueryAsync<StoredBeatmap>(
            new CommandDefinition(
                $"""
                 SELECT b.id                AS BeatmapId,
                        b.set_id            AS SetId,
                        b.filename          AS Filename,
                        b.checksum_md5      AS ChecksumMd5,
                        b.version_name      AS VersionName,
                        bs.title            AS Title,
                        bs.artist           AS Artist,
                        bs.status           AS SetStatus,
                        b.pace_version      AS PaceVersion,
                        b.char_count        AS CharCount,
                        b.difficulty_rating AS DifficultyRating,
                        b.sr_dt             AS SrDt,
                        b.sr_ht             AS SrHt,
                        b.sr_literate       AS SrLiterate,
                        b.sr_literate_dt    AS SrLiterateDt,
                        b.sr_literate_ht    AS SrLiterateHt,
                        b.freestyle_cell_count AS FreestyleCellCount
                 FROM beatmaps b
                 JOIN beatmapsets bs ON bs.id = b.set_id
                 WHERE b.ruleset_id = 0 {filter}
                 ORDER BY b.id
                 {limit}
                 """,
                new { ids = options.BeatmapIds.ToArray() },
                cancellationToken: ct));

        return rows.ToList();
    }

    private static async Task<List<StoredScoreRow>> LoadScoresAsync(NpgsqlConnection conn, long[] beatmapIds, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<StoredScoreRow>(
            new CommandDefinition(
                """
                SELECT s.id               AS ScoreId,
                       s.user_id          AS UserId,
                       s.beatmap_id       AS BeatmapId,
                       s.pp               AS Pp,
                       -- smallint on the column (020_performance_points.sql), int here: Dapper
                       -- materializes a record by CONSTRUCTOR, and an Int16 will not bind to an int
                       -- parameter, so the cast belongs in the query rather than in the record.
                       s.pp_version::int  AS PpVersion,
                       s.ranked           AS Ranked,
                       s.passed           AS Passed,
                       s.accuracy         AS Accuracy,
                       s.max_combo        AS MaxCombo,
                       s.mods::text       AS ModsJson,
                       s.statistics::text AS StatisticsJson
                FROM scores s
                WHERE s.ruleset_id = 0 AND s.beatmap_id = ANY(@beatmapIds)
                ORDER BY s.id
                """,
                new { beatmapIds },
                cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Which rows are on a ranked board at all, i.e. <see cref="PpRanking.EligiblePlaysSql"/> WITHOUT
    /// its <c>pp &gt; 0</c> clause: the price predicate has to be applied twice, once per side, or a
    /// row that stops earning pp could never be seen leaving a board.
    ///
    /// <para>THE REST OF THE DEFINITION IS NOT RESTATED, IT IS CHECKED. Dropping one clause out of a
    /// shared SQL string means writing the other clauses again, so the query below is run alongside
    /// the real <see cref="PpRanking.EligiblePlaysSql"/> and the two are held against each other:
    /// this set, filtered to a stored price above zero, must be exactly the set the server would
    /// serve. A mismatch is printed rather than swallowed, because it means this tool's idea of a
    /// board has drifted from the site's.</para>
    /// </summary>
    private static async Task<HashSet<long>> LoadBoardEligibleAsync(NpgsqlConnection conn, IReadOnlyList<PpRow> scores, CancellationToken ct)
    {
        var ignoringPrice = (await conn.QueryAsync<long>(
            new CommandDefinition(
                $"""
                 SELECT s.id
                 FROM scores s
                 JOIN beatmaps b ON b.id = s.beatmap_id
                 JOIN beatmapsets bs ON bs.id = b.set_id
                 JOIN users u ON u.id = s.user_id
                 WHERE {BeatmapLeaderboard.OnBoard("s", "true")}
                   AND bs.status = 'ranked'
                   AND NOT u.restricted AND u.deleted_at IS NULL
                 """,
                cancellationToken: ct))).ToHashSet();

        var server = (await conn.QueryAsync<long>(
            new CommandDefinition($"SELECT e.id FROM ({PpRanking.EligiblePlaysSql}) e", cancellationToken: ct))).ToHashSet();

        var stored = scores.Where(s => s.Stored.Pp > 0).Select(s => s.Stored.ScoreId).ToHashSet();
        var mine = ignoringPrice.Where(stored.Contains).ToHashSet();

        // Only rows this run actually loaded can be compared, so the server's set is narrowed to them
        // first; a --limit run has already turned the boards off anyway.
        var loaded = scores.Select(s => s.Stored.ScoreId).ToHashSet();
        server.IntersectWith(loaded);

        if (!server.SetEquals(mine))
        {
            Console.Error.WriteLine($"warning: board eligibility drifted from PpRanking.EligiblePlaysSql ({server.Count} rows there, {mine.Count} here).");
            Console.Error.WriteLine("         The board sections below are computed from this tool's copy of the predicate, which");
            Console.Error.WriteLine("         no longer matches the server's. Treat the placement numbers as unreliable.");
        }

        return ignoringPrice;
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The full per-row detail, enums BY NAME. This file is read by a human deciding whether 152's
    /// acceptance clause is satisfied, and "Resolution": 2 tells them nothing about why a map was
    /// left out.
    /// </summary>
    private static string Serialize(RunContext run)
    {
        var document = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            run.Site,
            paceVersion = Typebeat.Web.Packages.Lyrics.LyricPace.VERSION,
            ppVersion = PerformancePoints.VERSION,
            assumed = new
            {
                lengthStars = Length.LENGTH_STARS,
                referenceCells = Length.REFERENCE_CELLS,
                deletedPpWeight = Length.LENGTH_WEIGHT,
                deletedPpFloor = Length.LENGTH_FLOOR,
            },
            passed = run.Passed,
            // Next to the verdict, always: "passed" over an empty set is not a pass, and a reader
            // who greps this file for it has to see that in the same object.
            vacuous = run.Vacuous,
            mapsReRated = run.MapsReRated,
            mapsRead = run.Maps.Count,
            scoresRepriced = run.ScoresRepriced,
            scoresRead = run.Scores.Count,
            maps = run.Maps.Select(m => new
            {
                m.Stored.BeatmapId,
                m.Stored.SetId,
                m.Stored.Name,
                m.Stored.PaceVersion,
                m.Resolution,
                m.Detail,
                m.Cells,
                m.LiterateCells,
                m.PaceCells,
                storedCharCount = m.Stored.CharCount,
                // Both halves of the row's length as STORED, so a rating this run cannot explain
                // can be read against the map's freestyle content without refetching its blob:
                // cells above are PRICED (a slot counts a quarter since backlog 211), while
                // char_count counts a slot whole and this says how many of them there are.
                storedFreestyleCellCount = m.Stored.FreestyleCellCount,
                variants = SrAnalysis.Variants.Select(v => new
                {
                    variant = v,
                    stored = m.Stored.Rating(v),
                    recomputed = m.New(v),
                    delta = m.Delta(v),
                    expectedBonus = m.ExpectedBonus(v),
                    residual = m.Residual(v),
                    impliedCells = m.ImpliedCells(v),
                }),
            }),
            scores = run.Scores.Select(s => new
            {
                s.Stored.ScoreId,
                s.Stored.UserId,
                s.Stored.BeatmapId,
                s.Stored.PpVersion,
                s.Stored.Ranked,
                s.Stored.Passed,
                notes = s.Notes.Notes,
                misses = s.Notes.Misses,
                typos = s.Notes.Typos,
                s.Literate,
                s.Cells,
                s.StarBefore,
                s.StarAfter,
                storedPp = s.Stored.Pp,
                ppAtStoredStars = s.PpAtStoredStars,
                newPp = s.NewPp,
                s.Settled,
                s.Refused,
                s.Ratio,
                s.ExpectedDeletedFactor,
                s.ImpliedDeletedFactor,
                s.FactorResidual,
                s.StarBonusRatio,
            }),
            findings = new
            {
                sr = new
                {
                    run.Sr.OutOfRange,
                    run.Sr.MonotonicityBreaks,
                    run.Sr.BonusMismatch,
                    run.Sr.WholeStarCrossing,
                    run.Sr.StaleCharCount,
                    fitted = run.Sr.FittedBonus,
                    unresolved = run.Sr.Unresolved.Select(u => new { u.Stored.BeatmapId, u.Resolution, u.Detail }),
                },
                pp = new
                {
                    run.Pp.SignFlips,
                    run.Pp.UnexplainedMoves,
                    run.Pp.Unpriceable,
                    run.Pp.Buckets,
                    run.Pp.GradingBreaks,
                    run.Pp.Anchors,
                    fitted = run.Pp.FittedWeight,
                    run.Pp.Refused,
                    run.Pp.Pending,
                },
                boards = new
                {
                    run.Boards.Filtered,
                    run.Boards.Boards,
                    run.Boards.BoardsReordered,
                    run.Boards.BoardsTopChanged,
                    run.Boards.RowsLeavingBoards,
                    run.Boards.RowsJoiningBoards,
                    run.Boards.RepresentativeChanges,
                    run.Boards.BoardOrderChanges,
                    run.Boards.GlobalOrderChanges,
                    run.Boards.RankMoves,
                    violations = run.Boards.Violations,
                },
            },
        };

        return JsonConvert.SerializeObject(document, new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            // One casing for the whole file, including the finding records, whose property names
            // come from C# and would otherwise read as a different document than the rest.
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        });
    }

    private static void Usage()
    {
        Console.WriteLine("""
            typebeat reprice report (backlog 152)

            What a post-152 reprice would do to the live catalogue, computed from a database and a
            site, WITHOUT WRITING ANYTHING. There is one command and it reads: this tool has no
            apply, no confirmation flag and no UPDATE statement anywhere in it. The sweeps it models
            (Packages/PaceBackfill and Packages/PpBackfill) only exist as writers, which is why the
            acceptance diff 152 asks for could not be produced before this.

              report             Recompute every beatmap's six star ratings from the exact .osu its
                                 checksum names, reprice every score under today's formula and those
                                 new ratings, and check the predicates 152's acceptance clause names:

                                   SR   every delta in [0, +0.17], monotone in cells, with the map's
                                        typeable cell count printed (the bonus is
                                        0.12*max(0, log10(cells/100)), so the count is what makes it
                                        checkable), and the residual against that bonus per map.
                                   pp   deflation graded by map length, no sign flips, and order
                                        changes only where length was the differentiator, reported as
                                        per-board placement changes.

                                 Anything failing a predicate is listed BY ID.

            Options:
              --db <conn>        Postgres connection string (default: $TYPEBEAT_DB, else the dev default)
              --site <url>       typebeat instance the beatmap packages are fetched from
                                 (default: https://typebeat.mingda.sh). Read-only, public routes.
              --cache <dir>      where fetched .osz are kept (default: ./.reprice-report-cache). The
                                 layout matches tools/score-recalc's, so pointing this at that tool's
                                 cache reuses every package it has already downloaded.
              --packages <dir>   index local .osz/.typb files before fetching anything, for a run
                                 against packages that are already on disk
              --beatmap <id>     only this beatmap; repeatable. Turns the board sections OFF, since a
                                 slice of a board cannot tell a place change from a missing row.
              --limit <n>        only the first n beatmaps. Turns the board sections off too.
              --out <file.json>  write the full per-row detail as JSON (every variant of every map,
                                 every score's three prices, and every finding)
              --full-table       print every row of every table and every finding, instead of the
                                 first 200 / 25
              --star-tolerance <x>
                                 how far a rating may sit from its predicted length bonus before the
                                 map is reported as having moved for another reason (default 1e-6)
              --pp-tolerance <x> the same, relative, on the pp side (default 1e-6)

            Exit codes:
              0  the run completed and every predicate held
              1  the run could not start (bad options, no database, nothing selected)
              2  the run completed and at least one predicate FAILED

            Examples:
              dotnet run --project tools/reprice-report -- report --out reprice.json
              dotnet run --project tools/reprice-report -- report --cache .score-recalc-cache --full-table
            """);
    }

    private sealed class RepriceOptions
    {
        public string ConnectionString { get; private init; } = string.Empty;
        public string Site { get; private init; } = default_site;
        public string CacheDir { get; private init; } = ".reprice-report-cache";
        public string? PackageDir { get; private init; }
        public string? OutFile { get; private init; }
        public int? Limit { get; private init; }
        public bool FullTable { get; private init; }
        public Tolerances Tolerances { get; private init; } = new();
        public List<long> BeatmapIds { get; } = new();

        /// <summary>
        /// An unknown or malformed option is a HARD ERROR. The tool writes nothing, so the stakes are
        /// lower than score-recalc's, but a silently dropped <c>--beatmap</c> would produce a report
        /// over a different population than the one its reader typed, which is its own kind of lie.
        /// </summary>
        public static bool TryParse(string[] args, out RepriceOptions options, out string error)
        {
            string? db = null, site = null, cache = null, packages = null, outFile = null;
            int? limit = null;
            bool fullTable = false;
            double star = 1e-6, pp = 1e-6;
            var ids = new List<long>();

            options = new RepriceOptions();
            error = string.Empty;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (arg)
                {
                    case "--db": db = Next(); break;
                    case "--site": site = Next(); break;
                    case "--cache": cache = Next(); break;
                    case "--packages": packages = Next(); break;
                    case "--out": outFile = Next(); break;
                    case "--full-table": fullTable = true; break;

                    case "--limit":
                        if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
                        {
                            error = "--limit needs a positive number.";
                            return false;
                        }

                        limit = n;
                        break;

                    case "--beatmap":
                        if (!long.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                        {
                            error = "--beatmap needs a beatmap id.";
                            return false;
                        }

                        ids.Add(id);
                        break;

                    case "--star-tolerance":
                        if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out star) || star < 0)
                        {
                            error = "--star-tolerance needs a non-negative number.";
                            return false;
                        }

                        break;

                    case "--pp-tolerance":
                        if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out pp) || pp < 0)
                        {
                            error = "--pp-tolerance needs a non-negative number.";
                            return false;
                        }

                        break;

                    default:
                        error = $"unknown option '{arg}'. Run with --help.";
                        return false;
                }
            }

            options = new RepriceOptions
            {
                // Mirrors Db.ResolveConnectionString, exactly as tools/score-recalc does.
                ConnectionString = db
                                   ?? Environment.GetEnvironmentVariable("TYPEBEAT_DB")
                                   ?? "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat",
                Site = site ?? default_site,
                CacheDir = cache ?? ".reprice-report-cache",
                PackageDir = packages,
                OutFile = outFile,
                Limit = limit,
                FullTable = fullTable,
                Tolerances = new Tolerances { Star = star, PpRelative = pp },
            };

            options.BeatmapIds.AddRange(ids);
            return true;
        }
    }
}
