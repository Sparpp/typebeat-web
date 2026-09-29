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
/// <param name="Wpm">The headline pace of the hardest difficulty, null when unknown: its TARGET
/// WPM (033_target_wpm.sql, the average pace of the fastest fifth of its lines of at least three
/// words), falling back to the stored
/// AVERAGE WPM on any row the LyricPace v18 backfill has not reached yet, so a card is never blank
/// while the sweep is still running. A word is five typeable cells (LyricPace.CHARS_PER_WORD), the
/// typing-test convention, so either reading means what a WPM means anywhere else.</param>
/// <param name="HasPackage">False for pre-M3 sets with no uploaded package: the download rail
/// icon becomes an inert "available in-game only" hint instead of a dead 404 link.</param>
/// <param name="Explicit">Creator-declared explicit content (submission wizard toggle): renders
/// the EXPLICIT badge beside the title. Display only, it filters nothing out.</param>
/// <param name="HasPlayableDiff">The set has a live .osu difficulty, i.e. exactly what
/// /play/map/{id}/osu resolves. Combined with the status and the package in
/// <see cref="CanWebplay"/> it decides whether the card offers the browser-play rail.</param>
/// <param name="HasVideo">beatmapsets.has_video, written at ingest from any difficulty's
/// [Events] Video line. Video files dominate a package's size, so this is what turns the card's
/// download button into the two-option "with video" / "audio only" expand
/// (<see cref="OffersDownloadChoice"/>); every other set downloads on the first click.</param>
/// <param name="DiffsJson">The set's live difficulties as a json array of
/// <c>{id, name, stars, wpm}</c>, hardest first (built by json_agg in the same lateral that
/// produces <paramref name="Stars"/> and <paramref name="Wpm"/>, so it costs no extra round trip).
/// Parsed once into <see cref="Diffs"/>; the partial reads that, never this string.</param>
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
    bool HasPlayableDiff,
    bool HasVideo,
    string DiffsJson)
{
    /// <summary>
    /// How many difficulties the card's star stack draws at most. A set with more still cycles
    /// through all of them (the button's data carries every one, and its label says how many); only
    /// the drawn glyphs stop here, so a ten-difficulty set cannot push the chip off the card.
    /// </summary>
    public const int StackCap = 5;

    private static readonly System.Text.Json.JsonSerializerOptions diffsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private IReadOnlyList<CardDifficulty>? diffs;

    /// <summary>The live difficulties, hardest first (ties broken by id, so the order is stable).</summary>
    public IReadOnlyList<CardDifficulty> Diffs => diffs ??= ParseDiffs(DiffsJson);

    /// <summary>
    /// Two or more live difficulties: the chip becomes the cycling star-stack button. A one (or
    /// zero) difficulty set keeps today's plain chip, byte for byte.
    /// </summary>
    public bool HasDiffStack => Diffs.Count > 1;

    /// <summary>The difficulties whose stars are drawn: the <see cref="StackCap"/> hardest.</summary>
    public IReadOnlyList<CardDifficulty> StackedDiffs => Diffs.Count <= StackCap ? Diffs : Diffs.Take(StackCap).ToList();

    /// <summary>
    /// What the stack button carries for card-diffs.js, one entry per live difficulty in stack
    /// order, every display string already formatted here so the script never re-derives a number
    /// or a colour: the rating text ("0.0#", as the chip prints it), the WPM text (null when
    /// unknown), the <see cref="DifficultyColour"/> tint and the button's aria-label.
    /// </summary>
    public string DiffsDataJson => System.Text.Json.JsonSerializer.Serialize(Diffs.Select(d => new
    {
        id = d.Id,
        stars = FormatStars(d.Stars),
        wpm = d.Wpm is double w ? FormatWpm(w) : null,
        colour = DifficultyColour.ForStars(d.Stars),
        label = DiffLabel(d, Diffs.Count),
    }));

    /// <summary>The widest rating text across the difficulties, in characters: the stack button
    /// reserves it so a click never reflows the card.</summary>
    public int StarsTextWidth => Diffs.Count == 0 ? 0 : Diffs.Max(d => FormatStars(d.Stars).Length);

    /// <summary>The widest WPM number across the difficulties, in characters (0 when none has one).</summary>
    public int WpmTextWidth => Diffs.Count == 0 ? 0 : Diffs.Max(d => d.Wpm is double w ? FormatWpm(w).Length : 0);

    /// <summary>The chip's rating text: the same "0.0#" the one-difficulty chip prints.</summary>
    public static string FormatStars(double stars) => stars.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The chip's WPM number, whole words per minute.</summary>
    public static string FormatWpm(double wpm) => wpm.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The stack button's accessible name while <paramref name="d"/> is on top: which difficulty is
    /// showing and its numbers, then how many there are and what a click does.
    /// </summary>
    public static string DiffLabel(CardDifficulty d, int count)
    {
        string pace = d.Wpm is double w ? $", {FormatWpm(w)} WPM" : string.Empty;
        return $"{d.Name}: {FormatStars(d.Stars)} stars{pace}. {count} difficulties, click to cycle";
    }

    private static IReadOnlyList<CardDifficulty> ParseDiffs(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        return System.Text.Json.JsonSerializer.Deserialize<List<CardDifficulty>>(json, diffsJsonOptions) ?? [];
    }

    public string StatusLabel => BeatmapsetDisplay.StatusLabel(Status);

    public string PillClass => BeatmapsetDisplay.PillClass(Status);

    /// <summary>
    /// Does the download button expand into the two-option panel instead of downloading straight
    /// away? Only for a set that has both a package to serve and a video worth leaving out of it.
    /// The server still decides whether an audio-only package is really servable (a map imported
    /// from an mp4 alone has no separate audio file), which the panel learns from
    /// /beatmapsets/{id}/download-sizes when it opens.
    /// </summary>
    public bool OffersDownloadChoice => HasPackage && HasVideo;

    /// <summary>
    /// Can this set be played in the browser right now? Every PUBLISHED set can
    /// (<see cref="Endpoints.BeatmapsetEndpoints.IsPublished"/>: pending, unranked or ranked),
    /// provided the two things /play/map/{id}/* need are present: an assembled package and a live
    /// .osu difficulty. These are exactly the conditions the /play picker filters on, so the card's
    /// play rail can never link a map the player would fail to load.
    ///
    /// <para>Ranked-ONLY until backlog 230, on the argument that an unranked map "has nothing to
    /// play for". It has: /play/submit already re-reads the set's status at submit time and stores
    /// <c>ranked = false</c> for anything not ranked, the play still counts toward play history and
    /// the profile's play count, and pending/unranked sets are world-readable everywhere else on
    /// the site (their package, cover and preview all serve anonymously). This is also what makes
    /// "play every song" scale past the picker's own LIMIT 60: the rail is on every listing card,
    /// so /beatmapsets with its search and paging becomes the way to reach the long tail.</para>
    /// </summary>
    public bool CanWebplay => Endpoints.BeatmapsetEndpoints.IsPublished(Status) && HasPackage && HasPlayableDiff;

    /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

    /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);
}

/// <summary>One live difficulty of a card's set, as <see cref="BeatmapsetCardSql.Select"/>'s
/// json_agg writes it: <paramref name="Wpm"/> is the same coalesce(target_wpm, wpm) the set-level
/// chip reads, per row.</summary>
public sealed record CardDifficulty(long Id, string Name, double Stars, double? Wpm);

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
               EXISTS (SELECT 1 FROM beatmaps b2 WHERE b2.set_id = s.id AND b2.filename LIKE '%.osu') AS HasPlayableDiff,
               s.has_video        AS HasVideo,
               coalesce(d.diffs, '[]')::text AS DiffsJson
        FROM beatmapsets s
        JOIN users u ON u.id = s.owner_id
        LEFT JOIN LATERAL (
            -- filename IS NOT NULL = the diff is live in the current version; dropped diffs and
            -- freshly-allocated blank rows (never deleted, scores FK) must not drive the chips.
            -- The pace chip is the TARGET WPM (033_target_wpm.sql), with the stored average as the
            -- fallback: target_wpm is NULL on every row the v18 backfill has not reached, and a
            -- COALESCE inside the max keeps those cards populated with the figure they showed
            -- before rather than dropping the chip mid-sweep. Both are WPM in the same unit, so the
            -- fallback is a slightly lower number, never a differently-scaled one.
            -- The cast is on the numeric wpm, not on the coalesce, so the two arms are the same
            -- type going in and the aggregate cannot pick one up by implicit resolution.
            -- diffs is the per-difficulty list behind the card's star stack (backlog 327), in stack
            -- order: hardest first, id as the tiebreak so two equal ratings never swap between
            -- renders. Same rows, same coalesce, so its first entry's stars ARE the max above;
            -- its wpm is that difficulty's own, which is the point (the max can come from a
            -- different difficulty). Cast to text in the outer SELECT: json has no equality
            -- operator, and a consumer wrapping this SELECT must stay free to compare rows.
            SELECT max(b.difficulty_rating) AS stars,
                   max(coalesce(b.target_wpm, b.wpm::double precision)) AS wpm,
                   json_agg(json_build_object(
                       'id', b.id,
                       'name', b.version_name,
                       'stars', b.difficulty_rating,
                       'wpm', coalesce(b.target_wpm, b.wpm::double precision))
                       ORDER BY b.difficulty_rating DESC, b.id) AS diffs
            FROM beatmaps b
            WHERE b.set_id = s.id AND b.filename IS NOT NULL
        ) d ON true
        """;
}
