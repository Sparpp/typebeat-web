using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Search;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// Public beatmap listing (/beatmapsets): free-text search through <see cref="FreeTextSearch"/>
/// (exact lexeme, prefix, substring and typo layers), a status filter row, three sorts, and
/// keyset "show more" paging: 50 per page, cursor on (sort key, id) carried in plain querystring
/// links so the page needs no JavaScript. With free text on the default sort the match tier leads
/// the order (and the cursor, as <c>after_tier</c>); an explicit sort keeps its own order.
/// </summary>
public sealed class ListingModel(Db db) : TypebeatPageModel
{
    public const int PageSize = 50;

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
        long? after, [FromQuery(Name = "after_id")] long? afterId,
        [FromQuery(Name = "after_tier")] int? afterTier)
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

        // Exact lexeme, prefix and substring layers, plus the typo fallback: one shared builder
        // (FreeTextSearch). Its predicate joins the WHERE once the fallback is decided, below.
        var textMatch = FreeTextSearch.Build(freeText);
        if (textMatch is not null)
        {
            foreach (var (name, value) in textMatch.Parameters)
                param.Add(name, value);
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

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // The typo layer answers only a search nothing matches literally under every other filter
        // (status, operators, unplayed), and then it replaces the literal predicate outright. The
        // probe carries no cursor, so every page of one search takes the same branch.
        string? tier = textMatch?.TierSql;
        if (textMatch is not null)
        {
            string textWhere = textMatch.Where;

            if (textMatch.TypoWhere is not null
                && !await conn.ExecuteScalarAsync<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM beatmapsets s JOIN users u ON u.id = s.owner_id\n{where}\nAND {textMatch.Where})",
                    param))
            {
                textWhere = textMatch.TypoWhere;
                tier = null; // one tier only: plain newest-first, and no after_tier in the cursor
            }

            where.Append("\nAND ").Append(textWhere);
        }

        string orderBy;
        string? cursor = null;

        // Match tier ranks only the default sort: "most played" and "most favourited" are the
        // visitor asking for that order, so they keep it exactly as before.

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

            case "newest" when tier is not null:
                // Free text on the default sort: best match tier first, newest within a tier. The
                // cursor leads with the tier (ascending) ahead of the (submitted_at, id) pair
                // (descending), so it cannot be one row comparison.
                orderBy = $"ORDER BY {tier}, s.submitted_at DESC, s.id DESC";
                if (after is not null && afterId is not null)
                {
                    cursor = $"AND ({tier} > @afterTier OR ({tier} = @afterTier AND (s.submitted_at, s.id) < (@afterTs, @afterId)))";
                    param.Add("afterTs", DateTime.UnixEpoch.AddTicks(after.Value * 10));
                    param.Add("afterTier", Math.Clamp(afterTier ?? FreeTextSearch.TierExact, FreeTextSearch.TierExact, FreeTextSearch.TierSubstring));
                }
                break;

            default:
                orderBy = "ORDER BY s.submitted_at DESC, s.id DESC";
                if (after is not null && afterId is not null)
                {
                    // Cursor carries unix MICROseconds; timestamptz is microsecond-precise, so
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
            int? nextTier = null;
            if (tier is not null && Sort == "newest")
            {
                param.Add("lastId", last.Id);
                nextTier = await conn.ExecuteScalarAsync<int>(
                    $"SELECT {tier} FROM beatmapsets s JOIN users u ON u.id = s.owner_id WHERE s.id = @lastId",
                    param);
            }

            NextPageUrl = BuildUrl(nextAfter, last.Id, afterTier: nextTier);
        }
    }

    /// <summary>Listing URL preserving query/status/sort; cursor params only when paging.</summary>
    public string BuildUrl(long? after = null, long? afterId = null, string? sort = null, string? status = null, bool? unplayed = null,
        int? afterTier = null)
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
            if (afterTier is not null)
                parts.Add($"after_tier={afterTier}");
        }

        return parts.Count == 0 ? "/beatmapsets" : "/beatmapsets?" + string.Join("&", parts);
    }

}
