using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Tools.ScoreRecalc;

// typebeat-web score recalculation (backlog 114, then 136 and 142).
//
// Backlog 109 changed what a typo does to a score: a typed-through wrong char used to spend its
// cell's judgement on a Miss the instant it landed and could never be recovered; now the cell's
// result is DEFERRED, so correcting it earns the cell back and only an uncorrected typo misses, at
// the seal. Every stored score was judged under the old rule, so its statistics, max_combo,
// accuracy, completion and rank are priced against a model the client no longer uses. The pp
// backfill cannot fix that, it reprices a given set of statistics rather than re-deriving them.
//
// This replays each score's stored .osr through the game's own TypingEngine and
// TypeBeatScoreProcessor, TWICE, and everything derived from the resulting statistics comes from
// the server's own ScoringContract and PerformancePoints.
//
// THERE ARE TWO SWEEPS, and they are separate commands rather than one command with a looser gate.
//
//   report / apply                     REPRODUCE. Re-derive under the rules the row was priced
//                                      under, refuse anything that does not come back exactly, then
//                                      report what today's TYPO rule alone makes of it. This is the
//                                      verification sweep, and it is how the tool proves it
//                                      understands a row at all.
//
//   supersede-report / supersede-apply SUPERSEDE. Re-judge under ALL of today's rules and REPLACE
//                                      the stored numbers (backlog 136, decided by the user
//                                      2026-08-13). Reproduction is not the gate here: the sweep
//                                      deliberately re-judges on rules the row was not played
//                                      under, and for a row stored in the backlog 133-to-147 window
//                                      reproduction is impossible outright, since backlog 147
//                                      deleted the ladder that graded it. So it becomes a
//                                      diagnostic and a DIFFERENT predicate takes over as the gate:
//                                      the judgement of the run may move, the run may not.
//
// THE ERAS THE HARNESS CAN EXPRESS (backlog 151). A stored row is re-derived on the ladder it was
// actually played on, through four switches on TypeBeatReplayScorer: the typo rule (backlog 109),
// combo restore (140), the untimed spacebar (148) and the rate-scaled judgement windows (150). The
// last two reach the widest: every map has spaces, and every DT/NC/HT row was graded on unscaled
// windows. The one era NO switch can express is the backlog 133-to-147 window.
//
// WHICH ERA A GIVEN ROW IS IN is decided three ways, none of them a date (backlog 155 and 156).
// Combo restore is a constant, because it moved after the last row that could care was stored. The
// typo rule is READ OFF THE ROW, because an uncorrected typo takes a key only one of the two rules
// can produce. The spacebar and the rate windows leave no key at all, so they are PROVED BY
// RECONSTRUCTION: a row that does not come back under the pre-release pair is re-derived under each
// remaining combination and pinned to the one that reproduces it exactly, and a row that no
// combination reproduces is reported as unexplained rather than given an era. A submission time
// would be the easy alternative and the wrong one, since the server deployed at a known instant but
// the client updates whenever its player updates it.
//
// Those rows are superseded like any other, because their stored numbers describe a game no client
// can play, which is what superseding is for. What they get instead of a gate is VISIBILITY: they
// are counted as their own population in the report rather than folded into a reproduction
// percentage (they are not the same thing as a row that disagrees with the harness for an unknown
// reason), and supersede-apply will not start until --expect-unreproducible names the count. They
// are not an --unreplayable case: every arm of that means nothing can be derived from the row, and
// one of these derives perfectly well, it is only the CHECK that is unavailable.
//
// Why not one command with a threshold: a threshold loose enough to pass a sweep in which nothing
// reproduces is loose enough to pass genuine corruption, and that gate is the only thing standing
// between a bad sweep and the live score table (backlog 142).
//
// IT WRITES NOTHING unless an apply command is used AND --i-understand-this-writes-to-the-database
// is passed. Superseding needs that flag and two more. Reading is the default and is the whole tool
// for every other purpose.

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

    /// <summary>
    /// The SECOND confirmation, required by <c>supersede-apply</c> and by nothing else. It is not
    /// decoration on top of <see cref="write_flag"/>: that flag says "this touches the database",
    /// which an operator who has run the reproduce sweep has already internalised, and this one says
    /// the different and much larger thing, that the rows being written are ones the tool CANNOT
    /// reproduce and whose stored numbers are being discarded on purpose.
    /// </summary>
    private const string supersede_flag = "--i-understand-this-discards-the-stored-numbers";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];

        if (command is not ("report" or "apply" or "supersede-report" or "supersede-apply"))
        {
            Console.Error.WriteLine($"error: unknown command '{command}'.");
            Usage();
            return 1;
        }

        // The mode is carried by the COMMAND NAME, so there is no flag that turns a reproduce run
        // into a superseding one and no way to reach the superseding rules by mistyping an option.
        var mode = command.StartsWith("supersede-", StringComparison.Ordinal) ? RecalcMode.Supersede : RecalcMode.Reproduce;
        bool writing = command is "apply" or "supersede-apply";

        if (!Options.TryParse(args, out var options, out string parseError))
        {
            Console.Error.WriteLine($"error: {parseError}");
            return 1;
        }

        if (writing && !options.Confirmed)
        {
            Console.Error.WriteLine($"error: '{command}' writes to the database. Re-run it with {write_flag}.");
            Console.Error.WriteLine($"       Run '{(mode == RecalcMode.Supersede ? "supersede-report" : "report")}' first, and read the report.");
            return 1;
        }

        if (command == "supersede-apply")
        {
            if (!options.SupersedeConfirmed)
            {
                Console.Error.WriteLine("error: 'supersede-apply' DISCARDS the stored numbers of rows it cannot reproduce.");
                Console.Error.WriteLine($"       Re-run it with {supersede_flag} as well.");
                return 1;
            }

            // The third guard, and the only one that cannot be satisfied without having read a
            // report: name the number of rows the sweep is expected to write. A blind supersede run
            // is the failure mode worth engineering against, so the tool refuses to start one.
            if (options.ExpectSuperseded is null)
            {
                Console.Error.WriteLine("error: 'supersede-apply' needs --expect-superseded <n>, the row count from the");
                Console.Error.WriteLine("       supersede-report you are applying. It is checked against what this run would");
                Console.Error.WriteLine("       actually write, so a stale or unread report stops the sweep instead of");
                Console.Error.WriteLine("       silently applying a different one.");
                return 1;
            }

            // The fourth guard, and the second that cannot be satisfied without having read a
            // report: name how many rows come from the backlog 133-to-147 window. Those are
            // superseded on numbers NOTHING can check, since the ladder that judged them is deleted,
            // and a sweep should not be able to write them without the operator having seen how many
            // there are. It changes no behaviour beyond that, deliberately.
            if (options.ExpectUnreproducible is null)
            {
                Console.Error.WriteLine("error: 'supersede-apply' needs --expect-unreproducible <n>, the count of rows from the");
                Console.Error.WriteLine("       backlog 133-to-147 window printed by the supersede-report you are applying. Those");
                Console.Error.WriteLine("       rows were judged on a ladder that no longer exists, so nothing can verify what");
                Console.Error.WriteLine("       they are being replaced with. Read the number, then pass it.");
                return 1;
            }
        }

        using var source = new ReplayArchive(options.Site, options.CacheDir);
        using var cts = new CancellationTokenSource();

        List<StoredScore> scores;
        NpgsqlConnection? conn = null;

        if (options.OfflineDir is string offlineDir)
        {
            source.IndexPackageDirectory(Path.Combine(offlineDir, "sets"));
            scores = OfflineScores.Load(offlineDir, source, options.StarsFile, options.ScoresFile);
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
                result = Recalculation.Run(stored, decoded, options.BackfillMistypes, mode);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"score {stored.ScoreId}: {e.GetType().Name}: {e.Message}");
                continue;
            }

            results.Add(result);
        }

        var plan = WritePlan.Build(results, mode, options.Unreplayable, options.ScoreIds.Count > 0 || options.Limit is not null);

        Report.Print(results, plan, options.WholeTable, Console.Out);

        if (options.OutFile is string outFile)
        {
            // Enums by NAME. This file is read by a human deciding whether to apply the sweep, and
            // "Skip": 6 tells them nothing about why a row was left alone.
            var json = JsonConvert.SerializeObject(results, Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter());
            await File.WriteAllTextAsync(outFile, json, cts.Token);
            Console.WriteLine($"\nfull per-score detail written to {outFile}");
        }

        if (!writing)
        {
            Console.WriteLine($"\nDRY RUN. Nothing was written. Use '{(mode == RecalcMode.Supersede ? "supersede-apply" : "apply")}' to write these values.");
            conn?.Dispose();
            return 0;
        }

        if (conn is null)
        {
            Console.Error.WriteLine($"error: '{command}' needs the database; it cannot run with --offline.");
            return 1;
        }

        // Everything below is a REFUSAL TO WRITE that could only be decided once the sweep had run.
        // The flag guards above stop an accidental invocation; these stop a deliberate one that is
        // about to do something other than what its operator read in the report.
        if (mode == RecalcMode.Supersede && !ConfirmSupersede(options, plan, results))
            return 1;

        int written = await ApplyAsync(conn, plan, cts.Token);
        Console.WriteLine($"\napplied: {written} score row(s) updated.");

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
                    b.sr_ht                      AS SrHt,
                    b.sr_literate                AS SrLiterate,
                    b.sr_literate_dt             AS SrLiterateDt,
                    b.sr_literate_ht             AS SrLiterateHt,
                    b.checksum_md5               AS CurrentChecksumMd5,
                    s.user_id                    AS UserId
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
    /// The last refusals, the ones that need the sweep's own result to decide. Each is a distinct
    /// failure of "the operator is applying the run they read about", and none of them is a
    /// tolerance: they either hold or they do not.
    /// </summary>
    private static bool ConfirmSupersede(Options options, WritePlan plan, IReadOnlyList<RecalcResult> results)
    {
        if (plan.Undecided.Count > 0)
        {
            Console.Error.WriteLine("error: this sweep hit rows with no usable replay and no policy was given for them.");
            Console.Error.WriteLine("       Backlog 136 asks for an explicit decision on these rather than a default, so");
            Console.Error.WriteLine("       there is no default. Add --unreplayable <case>=<keep|unrank> for each of:");

            foreach (var undecided in plan.Undecided)
                Console.Error.WriteLine($"         {WritePlan.Name(undecided),-18} {plan.Unreplayable[undecided].Count,6} row(s)");

            return false;
        }

        if (plan.Unreplayable.TryGetValue(UnreplayableCase.BeatmapMissing, out var missing) && !options.AllowUnavailableBeatmaps)
        {
            Console.Error.WriteLine($"error: {missing.Count} row(s) name a beatmap this run could not fetch, though the row's map");
            Console.Error.WriteLine("       still hashes to it. That is a fetch failure (cold cache, unreachable site), not a");
            Console.Error.WriteLine("       fact about the data, and applying around it leaves a board where some rows are");
            Console.Error.WriteLine("       superseded and some are not for a reason nobody can see later. Re-run somewhere the");
            Console.Error.WriteLine("       packages are reachable, or pass --allow-unavailable-beatmaps to accept the gap.");
            return false;
        }

        var notTheSameRun = results.Where(r => r.Skip == SkipReason.NotTheSameRun).ToList();

        if (notTheSameRun.Count > 0 && !options.AllowRefusedRows)
        {
            Console.Error.WriteLine($"error: {notTheSameRun.Count} row(s) were REFUSED: the replay does not describe the same run over");
            Console.Error.WriteLine("       the same map as the stored row. That is the corruption this gate exists to catch, and");
            Console.Error.WriteLine("       it is not the expected pre-133 reproduction failure, which this sweep already");
            Console.Error.WriteLine("       tolerates. Investigate them before writing anything, or pass --allow-refused-rows.");

            foreach (var r in notTheSameRun.Take(20))
                Console.Error.WriteLine($"         score {r.Stored.ScoreId,-10} {r.Detail}");

            return false;
        }

        if (options.ExpectUnreproducible != plan.DeletedLadderWindow.Count)
        {
            Console.Error.WriteLine($"error: --expect-unreproducible says {options.ExpectUnreproducible}, this run holds {plan.DeletedLadderWindow.Count} row(s) from the");
            Console.Error.WriteLine("       backlog 133-to-147 window. The report you are applying is not this sweep. Those are");
            Console.Error.WriteLine("       the rows no reproduction check can vouch for, so the count is read before the sweep");
            Console.Error.WriteLine("       runs rather than discovered after it. Re-run supersede-report with the SAME options.");
            return false;
        }

        if (options.ExpectSuperseded != plan.RowsWritten)
        {
            Console.Error.WriteLine($"error: --expect-superseded says {options.ExpectSuperseded}, this run would write {plan.RowsWritten}.");
            Console.Error.WriteLine("       The report you are applying is not this sweep. Re-run supersede-report with the");
            Console.Error.WriteLine("       SAME options and apply the number it prints. A drift here usually means a score was");
            Console.Error.WriteLine("       submitted since the report, or that the two runs used different options.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the plan, one transaction for the lot so a partial sweep can never leave half the
    /// leaderboard on one rule and half on the other. Rows the plan does not name are not touched.
    ///
    /// <para><c>pp_version</c> is stamped only when the price is settled, exactly as
    /// <c>PpBackfill</c> does: an unsettled row keeps version 0 so the next boot reprices it once
    /// its map has a rate star rating.</para>
    /// </summary>
    private static async Task<int> ApplyAsync(NpgsqlConnection conn, WritePlan plan, CancellationToken ct)
    {
        if (plan.RowsWritten == 0)
            return 0;

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Rows the policy unranks. ONLY ranked and pp move: their statistics were never re-derived,
        // so rewriting them would be inventing numbers, and backlog 142 keeps
        // ScoringContract.JudgedUnderTheFourthTier alive precisely so their untouched keys keep
        // reading correctly. pp_version is stamped because an unranked row's price is settled at
        // null/0 whatever its map's ratings do.
        foreach (var r in plan.Unranked)
        {
            await conn.ExecuteAsync(
                """
                UPDATE scores
                SET ranked     = false,
                    pp         = 0,
                    pp_version = @ppVersion
                WHERE id = @id
                """,
                new { id = r.Stored.ScoreId, ppVersion = Typebeat.Web.Scoring.PerformancePoints.VERSION },
                tx);
        }

        foreach (var r in plan.Rejudged)
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
        return plan.RowsWritten;
    }

    // -----------------------------------------------------------------------------------------

    private static void Usage()
    {
        Console.WriteLine("""
            typebeat score recalculation (backlog 114, 136, 142)

            Re-derives a stored score's statistics from its REPLAY, then reprices everything the
            server derives from them. Reads by default. There are two sweeps, and they are separate
            commands because they check themselves with opposite predicates.

              report             REPRODUCE (backlog 114). Re-derive under the rules the row was
                                 priced under (ComboRestoreRule.Never + SpaceTimingRule.Timed +
                                 RateWindowRule.Unscaled, plus the typo rule that judged that ROW:
                                 TypoRule.Deferred where its statistics carry an uncorrected typo,
                                 TypoRule.ImmediateMiss otherwise), REFUSE anything that does not
                                 come back exactly, then report what today's typo rule alone makes
                                 of it. total_score keeps the row's own mod multiplier.
                                 Writes NOTHING.
              apply              Same, then write the moved values back.

              supersede-report   SUPERSEDE (backlog 136, decided 2026-08-13). Re-judge under ALL of
                                 today's rules (TypoRule.Deferred + ComboRestoreRule.OnFix +
                                 SpaceTimingRule.Untimed + RateWindowRule.ScaledByRate) and report
                                 the stored numbers being REPLACED. total_score is priced with
                                 today's multipliers, because a superseded row has to be a score
                                 today's client could produce. Reproduction is a diagnostic here,
                                 not a gate: the sweep re-judges on rules the row was not played
                                 under, and a row from the backlog 133-to-147 window cannot
                                 reproduce at all. What gates instead is that the replay must
                                 describe the SAME RUN over the SAME MAP (cell counts, frames
                                 consumed). Writes NOTHING.
              supersede-apply    Same, then write. Needs three confirmations, see below.

            Options:
              --db <conn>        Postgres connection string (default: $TYPEBEAT_DB, else the dev default)
              --site <url>       typebeat instance to fetch replays and beatmap packages from
                                 (default: https://typebeat.mingda.sh). Read-only, public routes.
              --cache <dir>      where fetched .osr/.osz are kept (default: ./.score-recalc-cache)
              --score <id>       recalculate only this score; repeatable
              --limit <n>        only the first n rows
              --out <file.json>  write the full per-score detail as JSON
              --full-table       print every row of the before/after table instead of the first 200
              --offline <dir>    no database at all: recalculate every .osr in <dir>/replays against
                                 the packages in <dir>/sets, using each replay's own embedded score
                                 info as the stored account. Analysis only; both applies refuse it.
              --stars <file>     offline only: {"<beatmap md5>": <star rating>} so pp can be priced
              --scores <file>    offline only: {"<score id>": {...}} carrying the score columns an
                                 .osr does not (pp, ranked, passed, and the rate star ratings), so
                                 an offline supersede report can show pp before/after and can tell a
                                 failed run from a passed one. See docs/score-recalc-export.md.
              --backfill-mistypes
                                 also write a mistype count into rows that predate the stat
                                 (backlog 72). Off by default: introducing it reprices those rows
                                 on a dimension neither sweep is about.

            Supersede only:
              --unreplayable <case>=<keep|unrank>
                                 what to do with rows that have no usable replay. Repeatable, and
                                 a bare <keep|unrank> sets every case at once. Cases:
                                   no-replay        no replay was ever stored
                                   unreadable       stored bytes do not decode
                                   empty-replay     decodes but holds no typing frames
                                   beatmap-missing  the .osu could not be fetched (a FETCH failure)
                                   beatmap-changed  the set was re-uploaded; that .osu is gone
                                   failed-run       passed = false, so health is not re-derivable
                                 There is no default. supersede-apply refuses to run until every
                                 case the sweep ACTUALLY HIT has a policy, and never asks about one
                                 it did not hit.
              --expect-superseded <n>
                                 the row count printed by the supersede-report you are applying.
                                 Checked against what this run would write, so a stale or unread
                                 report stops the sweep.
              --expect-unreproducible <n>
                                 how many of them come from the backlog 133-to-147 window, the count
                                 the report prints as FROM THE 133-TO-147 WINDOW. Those rows were
                                 judged on a four-tier character ladder backlog 147 deleted, so they
                                 cannot be reproduced and nothing can verify the numbers replacing
                                 them. This changes no behaviour: it exists so the sweep cannot be
                                 started by anyone who has not read how many there are.
              --allow-unavailable-beatmaps
                                 proceed even though some packages could not be fetched, accepting a
                                 partly superseded leaderboard
              --allow-refused-rows
                                 proceed even though some rows failed the same-run gate

              --i-understand-this-writes-to-the-database
                                 required by 'apply' and 'supersede-apply'
              --i-understand-this-discards-the-stored-numbers
                                 required by 'supersede-apply', and by nothing else

            Examples:
              dotnet run --project tools/score-recalc -- report --out recalc.json
              dotnet run --project tools/score-recalc -- supersede-report --out supersede.json
              dotnet run --project tools/score-recalc -- supersede-apply \
                  --unreplayable keep --expect-superseded 412 --expect-unreproducible 7 \
                  --i-understand-this-writes-to-the-database \
                  --i-understand-this-discards-the-stored-numbers
            """);
    }

    private sealed class Options
    {
        public string ConnectionString { get; private init; } = string.Empty;
        public string Site { get; private init; } = default_site;
        public string CacheDir { get; private init; } = ".score-recalc-cache";
        public string? OfflineDir { get; private init; }
        public string? StarsFile { get; private init; }
        public string? ScoresFile { get; private init; }
        public string? OutFile { get; private init; }
        public int? Limit { get; private init; }
        public int? ExpectSuperseded { get; private init; }

        /// <summary>
        /// How many rows the operator read as coming from the backlog 133-to-147 window. Required by
        /// <c>supersede-apply</c> for the same reason <see cref="ExpectSuperseded"/> is, and it is a
        /// separate number because it answers a separate question: that one says how many rows are
        /// being written, this one says how many of them are being written on a re-derivation nothing
        /// can check against.
        /// </summary>
        public int? ExpectUnreproducible { get; private init; }
        public List<long> ScoreIds { get; } = new();
        public bool Confirmed { get; private init; }
        public bool SupersedeConfirmed { get; private init; }
        public bool BackfillMistypes { get; private init; }
        public bool AllowUnavailableBeatmaps { get; private init; }
        public bool AllowRefusedRows { get; private init; }
        public bool WholeTable { get; private init; }
        public Dictionary<UnreplayableCase, UnreplayablePolicy> Unreplayable { get; } = new();

        /// <summary>
        /// An unknown or malformed option is a HARD ERROR, not a warning. It used to be ignored, and
        /// with a superseding sweep in the tool that is no longer acceptable: a mistyped
        /// <c>--expect-superseded</c> or <c>--unreplayable</c> would be dropped, and the run would
        /// then either refuse for a confusing reason or, worse, proceed under a policy nobody typed.
        /// </summary>
        public static bool TryParse(string[] args, out Options options, out string error)
        {
            string? db = null, site = null, cache = null, offline = null, stars = null, scoresFile = null, outFile = null;
            int? limit = null, expect = null, expectUnreproducible = null;
            bool confirmed = false, supersedeConfirmed = false, backfillMistypes = false;
            bool allowUnavailable = false, allowRefused = false, wholeTable = false;
            var ids = new List<long>();
            var unreplayable = new Dictionary<UnreplayableCase, UnreplayablePolicy>();

            options = new Options();
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
                    case "--offline": offline = Next(); break;
                    case "--stars": stars = Next(); break;
                    case "--scores": scoresFile = Next(); break;
                    case "--out": outFile = Next(); break;
                    case "--full-table": wholeTable = true; break;

                    case "--limit":
                        if (!int.TryParse(Next(), out int n))
                        {
                            error = "--limit needs a number.";
                            return false;
                        }

                        limit = n;
                        break;

                    case "--expect-superseded":
                        if (!int.TryParse(Next(), out int e) || e < 0)
                        {
                            error = "--expect-superseded needs a non-negative number, the row count from the report.";
                            return false;
                        }

                        expect = e;
                        break;

                    case "--expect-unreproducible":
                        if (!int.TryParse(Next(), out int u) || u < 0)
                        {
                            error = "--expect-unreproducible needs a non-negative number, the 133-to-147 window count from the report.";
                            return false;
                        }

                        expectUnreproducible = u;
                        break;

                    case "--score":
                        if (!long.TryParse(Next(), out long id))
                        {
                            error = "--score needs a score id.";
                            return false;
                        }

                        ids.Add(id);
                        break;

                    case "--unreplayable":
                        if (Next() is not string spec || !TryApplyPolicy(spec, unreplayable, out error))
                        {
                            if (error.Length == 0)
                                error = "--unreplayable needs <keep|unrank> or <case>=<keep|unrank>.";

                            return false;
                        }

                        break;

                    case "--backfill-mistypes": backfillMistypes = true; break;
                    case "--allow-unavailable-beatmaps": allowUnavailable = true; break;
                    case "--allow-refused-rows": allowRefused = true; break;
                    case write_flag: confirmed = true; break;
                    case supersede_flag: supersedeConfirmed = true; break;

                    default:
                        error = $"unknown option '{arg}'. Run with --help.";
                        return false;
                }
            }

            options = new Options
            {
                // Mirrors Db.ResolveConnectionString.
                ConnectionString = db
                                   ?? Environment.GetEnvironmentVariable("TYPEBEAT_DB")
                                   ?? "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat",
                Site = site ?? default_site,
                CacheDir = cache ?? ".score-recalc-cache",
                OfflineDir = offline,
                StarsFile = stars,
                ScoresFile = scoresFile,
                OutFile = outFile,
                Limit = limit,
                ExpectSuperseded = expect,
                ExpectUnreproducible = expectUnreproducible,
                Confirmed = confirmed,
                SupersedeConfirmed = supersedeConfirmed,
                BackfillMistypes = backfillMistypes,
                AllowUnavailableBeatmaps = allowUnavailable,
                AllowRefusedRows = allowRefused,
                WholeTable = wholeTable,
            };

            options.ScoreIds.AddRange(ids);

            foreach (var (key, value) in unreplayable)
                options.Unreplayable[key] = value;

            return true;
        }

        private static bool TryApplyPolicy(string spec, Dictionary<UnreplayableCase, UnreplayablePolicy> into, out string error)
        {
            error = string.Empty;

            int split = spec.IndexOf('=');

            if (split < 0)
            {
                if (!WritePlan.TryParsePolicy(spec, out var all))
                {
                    error = $"--unreplayable '{spec}' is neither a policy (keep, unrank) nor <case>=<policy>.";
                    return false;
                }

                foreach (var value in Enum.GetValues<UnreplayableCase>())
                    into[value] = all;

                return true;
            }

            string caseName = spec[..split];
            string policyName = spec[(split + 1)..];

            if (!WritePlan.TryParseCase(caseName, out var parsedCase))
            {
                error = $"--unreplayable: '{caseName}' is not a case. See --help for the list.";
                return false;
            }

            if (!WritePlan.TryParsePolicy(policyName, out var parsedPolicy))
            {
                error = $"--unreplayable: '{policyName}' is not a policy (keep, unrank).";
                return false;
            }

            into[parsedCase] = parsedPolicy;
            return true;
        }
    }
}
