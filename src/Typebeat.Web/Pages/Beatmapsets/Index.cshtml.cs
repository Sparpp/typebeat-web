using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Search;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// Public beatmap listing (/beatmapsets): full-text search (websearch_to_tsquery over the
/// weighted beatmapsets.search vector, ILIKE fallback for short or lexeme-free queries),
/// a status filter row, three sorts, and keyset "show more" paging — 50 per page, cursor on
/// (sort key, id) carried in plain querystring links so the page needs no JavaScript.
/// </summary>
public sealed class ListingModel(Db db) : TypebeatPageModel
{
    public const int PageSize = 50;

    // Full-text search needs a word-ish token; anything shorter goes substring (ILIKE).
    private const int min_fts_query_length = 3;

    public string Query { get; private set; } = string.Empty;
    public string Sort { get; private set; } = "newest";
    public string StatusFilter { get; private set; } = "any";

    /// <summary>When set (and the viewer is signed in), hides sets the viewer already has a score on.</summary>
    public bool Unplayed { get; private set; }

    /// <summary>Whether the "Unplayed" filter is offered (only meaningful for a signed-in viewer).</summary>
    public bool CanFilterUnplayed => CurrentUser is not null;

    public IReadOnlyList<BeatmapsetCardModel> Sets { get; private set; } = [];

    /// <summary>Querystring link for the next page, or null when this page is the last.</summary>
    public string? NextPageUrl { get; private set; }

    public async Task OnGetAsync(string? q, string? s, string? status, bool? unplayed,
        long? after, [FromQuery(Name = "after_id")] long? afterId)
    {
        Query = (q ?? string.Empty).Trim();
        Sort = s is "plays" or "favs" ? s : "newest";
        StatusFilter = status is "ranked" or "pending" or "unranked" ? status : "any";
        Unplayed = unplayed == true && CurrentUser is not null;

        // Any = every publicly browsable status ('pending', 'unranked' and 'ranked' all are; hidden
        // and removed never list). The Ranked/Pending/Unranked pills narrow to one.
        string statusPredicate = StatusFilter switch
        {
            "ranked" => "s.status = 'ranked'",
            "pending" => "s.status = 'pending'",
            "unranked" => "s.status = 'unranked'",
            _ => "s.status IN ('pending', 'unranked', 'ranked')",
        };

        // Restricted mappers' sets are delisted site-wide (their profiles 404, so every
        // "mapped by" link would be dead); the owner still finds their own.
        var where = new StringBuilder($"WHERE {statusPredicate} AND (NOT u.restricted OR s.owner_id = @viewerId)");
        var param = new Dapper.DynamicParameters();
        param.Add("viewerId", CurrentUser?.Id ?? 0);

        // Pull osu-web-style key:value operators (title:, star:>4, date:2026, …) out of the box;
        // whatever's left is the free text the site has always matched on. Unknown keys and
        // malformed operator values stay in the free text, so the query never errors.
        var search = BeatmapSearchQuery.Parse(Query);
        string freeText = search.FreeText;

        if (freeText.Length > 0)
        {
            const string ilike =
                """
                (s.title ILIKE @like OR s.title_unicode ILIKE @like
                 OR s.artist ILIKE @like OR s.artist_unicode ILIKE @like
                 OR s.tags ILIKE @like OR s.source ILIKE @like OR u.username::text ILIKE @like)
                """;

            param.Add("like", "%" + EscapeLike(freeText) + "%");

            if (freeText.Length >= min_fts_query_length)
            {
                // websearch_to_tsquery('simple') matches how the vector is built (PackageIngest.
                // SearchVectorSql, 'simple' config). A query that yields no lexemes at all
                // (punctuation-only) falls back to substring matching instead of matching nothing.
                param.Add("q", freeText);
                where.Append(
                    $"""

                     AND (CASE WHEN numnode(websearch_to_tsquery('simple', @q)) > 0
                               THEN s.search @@ websearch_to_tsquery('simple', @q)
                               ELSE {ilike} END)
                     """);
            }
            else
            {
                where.Append("\nAND ").Append(ilike);
            }
        }

        // Typed operators translate to strictly-parameterised WHERE clauses (columns come from the
        // parser whitelist only). Composes with the free-text predicate above and the sort/paging
        // cursor below.
        var (opSql, opParams) = BeatmapSearchSql.Build(search);
        if (opSql.Length > 0)
        {
            where.Append(opSql);
            foreach (var (name, value) in opParams)
                param.Add(name, value);
        }

        // "Unplayed" = the signed-in viewer has no score on any beatmap in the set. Gated on
        // Unplayed already being false when signed out, so @viewerId is always a real user here.
        if (Unplayed)
        {
            where.Append(
                "\nAND NOT EXISTS (SELECT 1 FROM scores sc JOIN beatmaps b ON b.id = sc.beatmap_id"
                + " WHERE b.set_id = s.id AND sc.user_id = @viewerId)");
        }

        string orderBy;
        string? cursor = null;

        switch (Sort)
        {
            case "plays":
                orderBy = "ORDER BY s.play_count DESC, s.id DESC";
                if (after is not null && afterId is not null)
                {
                    cursor = "AND (s.play_count, s.id) < (@afterNum, @afterId)";
                    param.Add("afterNum", (int)Math.Clamp(after.Value, int.MinValue, int.MaxValue));
                }
                break;

            case "favs":
                orderBy = "ORDER BY s.favourite_count DESC, s.id DESC";
                if (after is not null && afterId is not null)
                {
                    cursor = "AND (s.favourite_count, s.id) < (@afterNum, @afterId)";
                    param.Add("afterNum", (int)Math.Clamp(after.Value, int.MinValue, int.MaxValue));
                }
                break;

            default:
                orderBy = "ORDER BY s.submitted_at DESC, s.id DESC";
                if (after is not null && afterId is not null)
                {
                    // Cursor carries unix MICROseconds — timestamptz is microsecond-precise, so
                    // millisecond truncation could skip rows sharing the boundary instant.
                    cursor = "AND (s.submitted_at, s.id) < (@afterTs, @afterId)";
                    param.Add("afterTs", DateTime.UnixEpoch.AddTicks(after.Value * 10));
                }
                break;
        }

        if (cursor is not null)
        {
            where.Append('\n').Append(cursor);
            param.Add("afterId", afterId!.Value);
        }

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // One extra row tells us whether a next page exists.
        var rows = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"{BeatmapsetCardSql.Select}\n{where}\n{orderBy}\nLIMIT {PageSize + 1}",
            param)).ToList();

        bool hasMore = rows.Count > PageSize;
        if (hasMore)
            rows.RemoveAt(PageSize);

        Sets = rows;

        if (hasMore)
        {
            var last = rows[^1];
            long nextAfter = Sort switch
            {
                "plays" => last.PlayCount,
                "favs" => last.FavouriteCount,
                _ => (last.Date.Ticks - DateTime.UnixEpoch.Ticks) / 10,
            };
            NextPageUrl = BuildUrl(nextAfter, last.Id);
        }
    }

    /// <summary>Listing URL preserving query/status/sort; cursor params only when paging.</summary>
    public string BuildUrl(long? after = null, long? afterId = null, string? sort = null, string? status = null, bool? unplayed = null)
    {
        var parts = new List<string>();

        if (Query.Length > 0)
            parts.Add("q=" + Uri.EscapeDataString(Query));

        string effectiveStatus = status ?? StatusFilter;
        if (effectiveStatus != "any")
            parts.Add("status=" + effectiveStatus);

        string effectiveSort = sort ?? Sort;
        if (effectiveSort != "newest")
            parts.Add("s=" + effectiveSort);

        if (unplayed ?? Unplayed)
            parts.Add("unplayed=true");

        if (after is not null && afterId is not null)
        {
            parts.Add($"after={after}");
            parts.Add($"after_id={afterId}");
        }

        return parts.Count == 0 ? "/beatmapsets" : "/beatmapsets?" + string.Join("&", parts);
    }

    private static string EscapeLike(string raw)
        => raw.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
