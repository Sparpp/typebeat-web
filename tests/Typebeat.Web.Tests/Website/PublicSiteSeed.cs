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

    /// <summary>"Bohemian Keyboard Rhapsody" by "Queen of Types" — search assertions.</summary>
    public static long SearchSetId { get; private set; }

    /// <summary>"Hammered Keys", play_count 999999 — most-played sort.</summary>
    public static long MostPlayedId { get; private set; }

    /// <summary>"Favourite Fingers", favourite_count 999999 — most-favourited sort.</summary>
    public static long MostFavedId { get; private set; }

    /// <summary>"Fresh Drop", newest public set site-wide.</summary>
    public static long FreshId { get; private set; }

    /// <summary>Hidden set, submitted "in the future" so it would top every list if leaked.</summary>
    public static long HiddenId { get; private set; }

    public static long RemovedId { get; private set; }

    /// <summary>"Leaderboard Anthem" — set page + leaderboard + report/favourite tests.</summary>
    public static long LeaderboardSetId { get; private set; }

    public static long LeaderboardBeatmapId { get; private set; }

    /// <summary>Set with cover_key/preview_key populated — cover img + preview button markup.</summary>
    public static long CoveredSetId { get; private set; }

    /// <summary>
    /// Public set with live diffs but NO set_versions row — the pre-M3 shape migration 004
    /// backfills. Its pages must hide the Download actions ("available in-game only").
    /// </summary>
    public static long PackagelessId { get; private set; }

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

            await conn.ExecuteAsync(
                """
                UPDATE beatmapsets
                SET cover_key = 'covers/' || id || '/1', preview_key = 'previews/' || id || '.mp3'
                WHERE id = @id
                """,
                new { id = CoveredSetId });

            // Leaderboard: typist one twice (only the 900k best may surface — DISTINCT ON),
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
        string status = "public", string tags = "", string source = "", string description = "",
        int playCount = 0, int favouriteCount = 0, double? bpm = null)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets
                (owner_id, title, artist, source, tags, description, status, bpm,
                 play_count, favourite_count, submitted_at, updated_at)
            VALUES
                (@ownerId, @title, @artist, @source, @tags, @description, @status, @bpm,
                 @playCount, @favouriteCount, now() + @submittedOffset, now() + @submittedOffset)
            RETURNING id
            """,
            new
            {
                ownerId = MapperId, title, artist, source, tags, description, status, bpm,
                playCount, favouriteCount, submittedOffset,
            });

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId,
        double totalLengthS, double stars, double wpm, int wordCount, int charCount)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES
                (@setId, 'type!beat', @checksum, @totalLengthS, @drainLengthS,
                 @stars, 'map.osu', @wordCount, @charCount, @wpm)
            RETURNING id
            """,
            new
            {
                setId, checksum = Guid.NewGuid().ToString("N"), totalLengthS,
                drainLengthS = totalLengthS * 0.9, stars, wordCount, charCount, wpm,
            });

    private static async Task InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double accuracy, int maxCombo, string rank, bool ranked = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, @accuracy, @maxCombo, @rank, true, @ranked,
                 '[]'::jsonb, '{"great":100,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":110}'::jsonb)
            """,
            new { userId, beatmapId, totalScore, accuracy, maxCombo, rank, ranked });
}
