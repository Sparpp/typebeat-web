namespace Typebeat.Web.Pages;

/// <summary>
/// View model for the shared beatmapset card partial (Pages/Shared/_BeatmapsetCard.cshtml):
/// THE reusable set-card component (landing strip, /beatmapsets listing, profile sections).
/// Self-contained: everything the partial renders is on this record; hydrate it with
/// <see cref="BeatmapsetCardSql.Select"/>.
/// </summary>
/// <param name="CoverUrl">Site-relative list-cover URL (150×150 bucket), or null → gradient placeholder (never a broken img).</param>
/// <param name="PreviewUrl">Site-relative 30s preview mp3, or null → no play button.</param>
/// <param name="Date">submitted_at; timestamptz arrives from Npgsql as UTC DateTime.</param>
/// <param name="Wpm">Perfect-play words per minute of the hardest difficulty, null when unknown. A
/// word is five typeable cells (LyricPace.CHARS_PER_WORD), the typing-test convention, so this is
/// exactly that difficulty's CPM over five and means what a WPM means anywhere else.</param>
/// <param name="HasPackage">False for pre-M3 sets with no uploaded package: the download rail
/// icon becomes an inert "available in-game only" hint instead of a dead 404 link.</param>
/// <param name="Explicit">Creator-declared explicit content (submission wizard toggle): renders
/// the EXPLICIT badge beside the title. Display only, it filters nothing out.</param>
/// <param name="HasPlayableDiff">The set has a live .osu difficulty, i.e. exactly what
/// /play/map/{id}/osu resolves. Combined with the status and the package in
/// <see cref="CanWebplay"/> it decides whether the card offers the browser-play rail.</param>
public sealed record BeatmapsetCardModel(
    long Id,
    string Title,
    string Artist,
    string? TitleUnicode,
    string? ArtistUnicode,
    string Creator,
    long OwnerId,
    string? CoverUrl,
    string? PreviewUrl,
    string Status,
    int PlayCount,
    int FavouriteCount,
    int DownloadCount,
    DateTime Date,
    double Stars,
    double? Wpm,
    bool IsFavourited,
    bool HasPackage,
    bool Explicit,
    bool HasPlayableDiff)
{
    public string StatusLabel => BeatmapsetDisplay.StatusLabel(Status);

    public string PillClass => BeatmapsetDisplay.PillClass(Status);

    /// <summary>
    /// Can this set be played in the browser right now? Ranked only (browser scores land on the
    /// live leaderboards, so an unranked map has nothing to play for), and only when the two
    /// things /play/map/{id}/* need are present: an assembled package and a live .osu difficulty.
    /// These are exactly the conditions the /play picker filters on, so the card's play rail can
    /// never link a map the player would fail to load.
    /// </summary>
    public bool CanWebplay => Status == "ranked" && HasPackage && HasPlayableDiff;

    /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

    /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);
}

/// <summary>Status wording shared by the card partial and the set page.</summary>
public static class BeatmapsetDisplay
{
    /// <summary>DB status → user-facing word ("Ranked" = live leaderboards; "Pending" = published, awaiting review).</summary>
    public static string StatusLabel(string status) => status switch
    {
        "ranked" => "Ranked",
        "pending" => "Pending",
        "unranked" => "Unranked",
        "hidden" => "Hidden",
        "removed" => "Removed",
        _ => status,
    };

    public static string PillClass(string status) => status switch
    {
        "ranked" or "pending" or "unranked" or "hidden" or "removed" => $"pill--{status}",
        _ => string.Empty,
    };
}

/// <summary>
/// Canonical SQL for hydrating <see cref="BeatmapsetCardModel"/> rows with Dapper.
/// </summary>
public static class BeatmapsetCardSql
{
    /// <summary>
    /// SELECT … FROM fragment producing exactly the card record's columns. Callers append
    /// WHERE / ORDER BY / LIMIT and must supply <c>@viewerId</c> (the signed-in user id, or 0
    /// for anonymous; user ids start at 1, so 0 never matches a favourite).
    /// Aliases in play: <c>s</c> = beatmapsets, <c>u</c> = owner, <c>d</c> = difficulty rollup.
    /// The column ORDER here is load-bearing: Dapper matches a positional record's constructor
    /// parameters against the reader's columns POSITIONALLY, so a new column must be added at the
    /// same index in both this SELECT and <see cref="BeatmapsetCardModel"/>.
    /// </summary>
    public const string Select =
        """
        SELECT s.id               AS Id,
               s.title            AS Title,
               s.artist           AS Artist,
               s.title_unicode    AS TitleUnicode,
               s.artist_unicode   AS ArtistUnicode,
               u.username::text   AS Creator,
               s.owner_id         AS OwnerId,
               CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
               CASE WHEN s.preview_key IS NOT NULL THEN '/' || s.preview_key END            AS PreviewUrl,
               s.status           AS Status,
               s.play_count       AS PlayCount,
               s.favourite_count  AS FavouriteCount,
               s.download_count   AS DownloadCount,
               s.submitted_at     AS Date,
               coalesce(d.stars, 0)      AS Stars,
               d.wpm::double precision   AS Wpm,
               EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId) AS IsFavourited,
               EXISTS (SELECT 1 FROM set_versions v WHERE v.set_id = s.id AND v.package_key IS NOT NULL) AS HasPackage,
               s.explicit         AS Explicit,
               -- Same predicate as PlayEndpoints.ResolveOsuFilenameAsync: if this is false the
               -- browser player has nothing to load, so the card must not offer webplay.
               EXISTS (SELECT 1 FROM beatmaps b2 WHERE b2.set_id = s.id AND b2.filename LIKE '%.osu') AS HasPlayableDiff
        FROM beatmapsets s
        JOIN users u ON u.id = s.owner_id
        LEFT JOIN LATERAL (
            -- filename IS NOT NULL = the diff is live in the current version; dropped diffs and
            -- freshly-allocated blank rows (never deleted, scores FK) must not drive the chips.
            SELECT max(b.difficulty_rating) AS stars, max(b.wpm) AS wpm
            FROM beatmaps b
            WHERE b.set_id = s.id AND b.filename IS NOT NULL
        ) d ON true
        """;
}
