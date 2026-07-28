using Dapper;
using Npgsql;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Deterministic content seed for the public-site page tests (landing / listing / set page):
/// a mapper, 60 filler sets for paging, named specials for search/sort/status assertions, and a
/// small leaderboard. Idempotent via a gate so every test class can call
/// <see cref="EnsureSeededAsync"/> from its OneTimeSetUp regardless of execution order.
/// Inserted directly with SQL (the upload pipeline has its own tests), which means the search
/// vector must be populated here the same way the ingest write path does it.
/// </summary>
public static class PublicSiteSeed
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static bool seeded;

    public static long MapperId { get; private set; }
    public static long TypistOneId { get; private set; }
    public static long TypistTwoId { get; private set; }
    public static long TypistThreeId { get; private set; }
    public static long CheatSuspectId { get; private set; }

    /// <summary>"Bohemian Keyboard Rhapsody" by "Queen of Types": search assertions.</summary>
    public static long SearchSetId { get; private set; }

    /// <summary>"Hammered Keys", play_count 999999: most-played sort.</summary>
    public static long MostPlayedId { get; private set; }

    /// <summary>"Favourite Fingers", favourite_count 999999: most-favourited sort.</summary>
    public static long MostFavedId { get; private set; }

    /// <summary>"Fresh Drop", newest published set site-wide.</summary>
    public static long FreshId { get; private set; }

    /// <summary>"Waiting Room", the one seeded 'pending' set, browsable but not ranked, so
    /// listing filters, profile sections and the set page's locked leaderboard cover both
    /// published statuses.</summary>
    public static long PendingId { get; private set; }

    /// <summary>Hidden set, submitted "in the future" so it would top every list if leaked.</summary>
    public static long HiddenId { get; private set; }

    public static long RemovedId { get; private set; }

    /// <summary>"Leaderboard Anthem": set page + leaderboard + report/favourite tests.</summary>
    public static long LeaderboardSetId { get; private set; }

    public static long LeaderboardBeatmapId { get; private set; }

    /// <summary>Set with cover_key/preview_key populated: cover img + preview button markup.</summary>
    public static long CoveredSetId { get; private set; }

    /// <summary>"Parental Advisory Anthem", the one set flagged explicit: the EXPLICIT badge and
    /// the <c>explicit:</c> search operator assert against it (every other seeded set is clean).</summary>
    public static long ExplicitSetId { get; private set; }

    /// <summary>"Radio Edit Anthem", the non-explicit twin sharing the "advisoryset" tag.</summary>
    public static long CleanTwinSetId { get; private set; }

    /// <summary>
    /// Public set with live diffs but NO set_versions row; the pre-M3 shape migration 004
    /// backfills. Its pages must hide the Download actions ("available in-game only").
    /// </summary>
    public static long PackagelessId { get; private set; }

    // Two fixed-fingerprint sets for the typed search-operator tests. Both tagged "operatorset"
    // so a test can scope free text to just this pair, then narrow with an operator. Fixed past
    // submit dates make the date: assertions deterministic.
    //
    // Alpha: submitted 2024-03-15, stars 4.5, wpm 100, length 90s (1:30), cpm 100*500/100 = 500.
    /// <summary>"Operator Alpha Synthwave": the low-stat operator fixture.</summary>
    public static long OpAlphaId { get; private set; }

    // Bravo: submitted 2022-11-01, stars 7.0, wpm 200, length 240s (4:00), cpm 200*700/100 = 1400.
    /// <summary>"Operator Bravo Ballad": the high-stat operator fixture.</summary>
    public static long OpBravoId { get; private set; }

    public static async Task EnsureSeededAsync()
    {
        await gate.WaitAsync();

        try
        {
            if (seeded)
                return;

            await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
            await conn.OpenAsync();

            MapperId = await InsertUserAsync(conn, "neon mapper");
            TypistOneId = await InsertUserAsync(conn, "typist one");
            TypistTwoId = await InsertUserAsync(conn, "typist two");
            TypistThreeId = await InsertUserAsync(conn, "typist three");
            CheatSuspectId = await InsertUserAsync(conn, "cheat suspect");

            // 60 filler sets: paging (50/page) needs more than one page of public sets.
            for (int i = 0; i < 60; i++)
            {
                await InsertSetAsync(conn,
                    title: $"Filler Song {i:00}", artist: "Filler Artist",
                    submittedOffset: TimeSpan.FromHours(-(i + 1)),
                    playCount: i, favouriteCount: i % 7);
            }

            SearchSetId = await InsertSetAsync(conn,
                title: "Bohemian Keyboard Rhapsody", artist: "Queen of Types",
                submittedOffset: TimeSpan.FromMinutes(-30),
                tags: "classic rock typing");

            MostPlayedId = await InsertSetAsync(conn,
                title: "Hammered Keys", artist: "The Plays",
                submittedOffset: TimeSpan.FromDays(-2), playCount: 999_999);

            MostFavedId = await InsertSetAsync(conn,
                title: "Favourite Fingers", artist: "The Hearts",
                submittedOffset: TimeSpan.FromDays(-2), favouriteCount: 999_999);

            FreshId = await InsertSetAsync(conn,
                title: "Fresh Drop", artist: "The Newest",
                submittedOffset: TimeSpan.FromHours(1));

            PendingId = await InsertSetAsync(conn,
                title: "Waiting Room", artist: "The Unreviewed",
                submittedOffset: TimeSpan.FromMinutes(-20), status: "pending");

            await InsertBeatmapAsync(conn, PendingId,
                totalLengthS: 80, stars: 4.2, wpm: 100, wordCount: 130, charCount: 640);

            HiddenId = await InsertSetAsync(conn,
                title: "Hidden Gem Nobody", artist: "Should Not Appear",
                submittedOffset: TimeSpan.FromHours(2), status: "hidden");

            RemovedId = await InsertSetAsync(conn,
                title: "Removed For Reasons", artist: "Gone",
                submittedOffset: TimeSpan.FromHours(2), status: "removed");

            LeaderboardSetId = await InsertSetAsync(conn,
                title: "Leaderboard Anthem", artist: "The Score Settlers",
                submittedOffset: TimeSpan.FromMinutes(-40),
                tags: "anthem leaderboard", source: "Type Hero", bpm: 128,
                description: "Line one\nLine two <script>alert(1)</script>");

            LeaderboardBeatmapId = await InsertBeatmapAsync(conn, LeaderboardSetId,
                totalLengthS: 95.5, stars: 3.2, wpm: 80, wordCount: 120, charCount: 600);

            CoveredSetId = await InsertSetAsync(conn,
                title: "Covered In Neon", artist: "The Artwork",
                submittedOffset: TimeSpan.FromMinutes(30));

            await InsertBeatmapAsync(conn, CoveredSetId,
                totalLengthS: 120, stars: 5.0, wpm: 125, wordCount: 200, charCount: 950);

            // The only explicit-flagged set. Tagged "advisoryset" so a search can scope to it (and
            // to its clean twin below) without dragging in the filler wall.
            ExplicitSetId = await InsertSetAsync(conn,
                title: "Parental Advisory Anthem", artist: "The Unfiltered",
                submittedOffset: TimeSpan.FromMinutes(-25), tags: "advisoryset",
                isExplicit: true);

            await InsertBeatmapAsync(conn, ExplicitSetId,
                totalLengthS: 110, stars: 4.0, wpm: 110, wordCount: 150, charCount: 700);

            // Clean twin of the above, same tag: proves the badge and the explicit: operator
            // discriminate rather than matching everything in scope.
            CleanTwinSetId = await InsertSetAsync(conn,
                title: "Radio Edit Anthem", artist: "The Bleeped",
                submittedOffset: TimeSpan.FromMinutes(-24), tags: "advisoryset");

            await InsertBeatmapAsync(conn, CleanTwinSetId,
                totalLengthS: 110, stars: 4.0, wpm: 110, wordCount: 150, charCount: 700);

            await conn.ExecuteAsync(
                """
                UPDATE beatmapsets
                SET cover_key = 'covers/' || id || '/1', preview_key = 'previews/' || id || '.mp3'
                WHERE id = @id
                """,
                new { id = CoveredSetId });

            // Leaderboard: typist one twice (only the 900k best may surface, DISTINCT ON),
            // the others once, plus an unranked row that must never appear.
            await InsertScoreAsync(conn, TypistOneId, LeaderboardBeatmapId, 900_000, 0.9846, 87, "S");
            await InsertScoreAsync(conn, TypistOneId, LeaderboardBeatmapId, 600_000, 0.9012, 40, "A");
            await InsertScoreAsync(conn, TypistTwoId, LeaderboardBeatmapId, 700_000, 0.9311, 61, "A");
            await InsertScoreAsync(conn, TypistThreeId, LeaderboardBeatmapId, 500_000, 0.8523, 30, "B");
            await InsertScoreAsync(conn, CheatSuspectId, LeaderboardBeatmapId, 999_999_999, 1, 110, "X", ranked: false);

            PackagelessId = await InsertSetAsync(conn,
                title: "Editor Era Classic", artist: "The Backfilled",
                submittedOffset: TimeSpan.FromDays(-1));

            await InsertBeatmapAsync(conn, PackagelessId,
                totalLengthS: 100, stars: 2.5, wpm: 60, wordCount: 90, charCount: 420);

            OpAlphaId = await InsertSetAtAsync(conn,
                title: "Operator Alpha Synthwave", artist: "Synth Operator",
                tags: "operatorset", submittedAt: new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc));
            // Disjoint lyric haystacks (only "night" shared) so the lyrics: tests can prove
            // single-word narrowing and multi-word AND semantics within the pair.
            await InsertBeatmapAsync(conn, OpAlphaId,
                totalLengthS: 90, stars: 4.5, wpm: 100, wordCount: 100, charCount: 500,
                lyrics: "neon skyline glowing all night");

            OpBravoId = await InsertSetAtAsync(conn,
                title: "Operator Bravo Ballad", artist: "Piano Operator",
                tags: "operatorset", submittedAt: new DateTime(2022, 11, 1, 0, 0, 0, DateTimeKind.Utc));
            await InsertBeatmapAsync(conn, OpBravoId,
                totalLengthS: 240, stars: 7.0, wpm: 200, wordCount: 100, charCount: 700,
                lyrics: "quiet rain falls on the piano at night");

            // Every seeded set EXCEPT the packageless one gets a version row, mirroring sets
            // that went through the upload pipeline: the set page / card Download actions key
            // on package existence (the object itself is never streamed by these tests).
            await conn.ExecuteAsync(
                """
                INSERT INTO set_versions (set_id, version_no, package_key)
                SELECT id, 1, 'packages/' || id || '/1.typb'
                FROM beatmapsets
                WHERE owner_id = @mapperId AND id <> @packagelessId
                """,
                new { mapperId = MapperId, packagelessId = PackagelessId });

            // Same expression the upload write path uses (and migration 002's backfill).
            await conn.ExecuteAsync(
                $"UPDATE beatmapsets s SET search = {PackageIngest.SearchVectorSql} FROM users u WHERE u.id = s.owner_id");

            seeded = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<long> InsertUserAsync(NpgsqlConnection conn, string username)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'not-a-real-hash', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn,
        string title, string artist, TimeSpan submittedOffset,
        string status = "ranked", string tags = "", string source = "", string description = "",
        int playCount = 0, int favouriteCount = 0, double? bpm = null, bool isExplicit = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets
                (owner_id, title, artist, source, tags, description, status, bpm, explicit,
                 play_count, favourite_count, submitted_at, updated_at)
            VALUES
                (@ownerId, @title, @artist, @source, @tags, @description, @status, @bpm, @isExplicit,
                 @playCount, @favouriteCount, now() + @submittedOffset, now() + @submittedOffset)
            RETURNING id
            """,
            new
            {
                ownerId = MapperId, title, artist, source, tags, description, status, bpm, isExplicit,
                playCount, favouriteCount, submittedOffset,
            });

    /// <summary>As <see cref="InsertSetAsync"/> but pins an ABSOLUTE submitted_at (the operator
    /// date: fixtures need stable calendar dates, not now()-relative offsets).</summary>
    private static async Task<long> InsertSetAtAsync(NpgsqlConnection conn,
        string title, string artist, string tags, DateTime submittedAt, string status = "ranked")
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets
                (owner_id, title, artist, tags, status, submitted_at, updated_at)
            VALUES
                (@ownerId, @title, @artist, @tags, @status, @submittedAt, @submittedAt)
            RETURNING id
            """,
            new { ownerId = MapperId, title, artist, tags, status, submittedAt });

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId,
        double totalLengthS, double stars, double wpm, int wordCount, int charCount,
        string lyrics = "")
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm, lyrics)
            VALUES
                (@setId, 'type!beat', @checksum, @totalLengthS, @drainLengthS,
                 @stars, 'map.osu', @wordCount, @charCount, @wpm, @lyrics)
            RETURNING id
            """,
            new
            {
                setId, checksum = Guid.NewGuid().ToString("N"), totalLengthS,
                drainLengthS = totalLengthS * 0.9, stars, wordCount, charCount, wpm, lyrics,
            });

    private static async Task InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double accuracy, int maxCombo, string rank, bool ranked = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, @accuracy, @completion, @maxCombo, @rank, true, @ranked,
                 '[]'::jsonb, '{"great":100,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":110}'::jsonb)
            """,
            // completion matches the fixed statistics blob: 107 typed of 110 cells. (Ranks stay
            // whatever the caller seeds; these are display fixtures, not grading fixtures.)
            new { userId, beatmapId, totalScore, accuracy, completion = 107.0 / 110.0, maxCombo, rank, ranked });
}
