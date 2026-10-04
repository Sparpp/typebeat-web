using System.Text;
using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Search;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/beatmapsets/search: the game's in-client beatmap listing overlay
/// (SearchBeatmapSetsRequest). Same matching as the /beatmapsets page (key:value operators via
/// <see cref="BeatmapSearchQuery"/>, free text via <see cref="FreeTextSearch"/>), but the response
/// is osu-web's <c>{ beatmapsets, cursor, total }</c> shape.
///
/// <para>Paging is offset-based: the client echoes the cursor back as <c>cursor[offset]</c>, and the
/// response omits <c>cursor</c> (null) on the last page. Offsets are fine here because the overlay
/// only pages forward a few screens, unlike the website's keyset "show more".</para>
///
/// <para>
/// Every listing filter whose data this server STORES is honoured: status category (including
/// favourites and the viewer's own maps), the subscribed-mappers general filter (mapper follows),
/// song language, has-video, achieved ranks, played/unplayed and explicit content. Parameters for
/// data it does not keep (<c>m</c>: one ruleset; <c>g</c>: no genre; the storyboard extra; the
/// recommended/converts/spotlights/featured-artists general filters) are accepted and ignored, and
/// the overlay does not offer them. Optional auth: a bearer unlocks the viewer-relative filters
/// (favourites, mine, follows, ranks, played).
/// </para>
/// </summary>
public static class BeatmapsetSearchEndpoints
{
    public const int PageSize = 50;

    /// <summary>
    /// The client's <c>SearchLanguage</c> ids (osu-web's language ids, its declaration order) to the
    /// stored canonical names (<see cref="Packages.BeatmapLanguages"/>). 1 is osu's "Unspecified",
    /// which is the stored empty string.
    /// </summary>
    private static readonly Dictionary<int, string> language_ids = new()
    {
        [1] = "", [2] = "english", [3] = "japanese", [4] = "chinese", [5] = "instrumental", [6] = "korean",
        [7] = "french", [8] = "german", [9] = "swedish", [10] = "spanish", [11] = "italian", [12] = "russian",
        [13] = "polish", [14] = "other",
    };

    /// <summary>The grades a score can be stored with, as the client's <c>ScoreRank</c> names them.</summary>
    private static readonly HashSet<string> score_ranks = ["XH", "X", "SH", "S", "A", "B", "C", "D"];

    private const string published = "s.status IN ('pending', 'unranked', 'ranked')";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/beatmapsets/search", SearchAsync);
    }

    private static async Task<IResult> SearchAsync(HttpContext ctx, Db db)
    {
        var q = ctx.Request.Query;
        var requester = await ctx.ResolveBearerAsync();
        long viewerId = requester?.Id ?? 0;
        bool signedIn = viewerId != 0;

        string category = q["s"].ToString().ToLowerInvariant();

        string statusPredicate = category switch
        {
            "leaderboard" or "ranked" => "s.status = 'ranked'",
            "pending" or "wip" => "s.status = 'pending'",
            "graveyard" => "s.status = 'unranked'",
            "favourites" when signedIn => $"{published} AND EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId)",
            // The viewer's own maps, including the hidden ones only they can open.
            "mine" when signedIn => "s.owner_id = @viewerId AND s.status <> 'removed'",
            // There is no qualification step and no loved status; nothing is either.
            "qualified" or "loved" => "false",
            _ => published,
        };

        var where = new StringBuilder($"WHERE {statusPredicate} AND (NOT u.restricted OR s.owner_id = @viewerId)");
        var param = new DynamicParameters();
        param.Add("viewerId", viewerId);

        var search = BeatmapSearchQuery.Parse(q["q"].ToString().Trim());

        var textMatch = FreeTextSearch.Build(search.FreeText);
        if (textMatch is not null)
        {
            foreach (var (name, value) in textMatch.Parameters)
                param.Add(name, value);
        }

        var (opSql, opParams) = BeatmapSearchSql.Build(search);
        if (opSql.Length > 0)
        {
            where.Append(opSql);
            foreach (var (name, value) in opParams)
                param.Add(name, value);
        }

        // General filters, dot-separated. Only "follows" (sets by mappers the viewer watches) is backed by stored data.
        if (signedIn && q["c"].ToString().Split('.').Contains("follows"))
        {
            where.Append(
                "\nAND EXISTS (SELECT 1 FROM user_follows uf"
                + " WHERE uf.follower_id = @viewerId AND uf.followee_id = s.owner_id AND uf.kind = 'mapper')");
        }

        if (int.TryParse(q["l"], out int languageId) && language_ids.TryGetValue(languageId, out string? language))
        {
            where.Append("\nAND s.language = @language");
            param.Add("language", language);
        }

        if (q["e"].ToString().Split('.').Contains("video"))
            where.Append("\nAND s.has_video");

        // Sets where the viewer holds a passed play of one of these grades.
        string[] ranks = q["r"].ToString().Split('.').Where(score_ranks.Contains).ToArray();
        if (signedIn && ranks.Length > 0)
        {
            where.Append(
                "\nAND EXISTS (SELECT 1 FROM scores sc JOIN beatmaps b ON b.id = sc.beatmap_id"
                + " WHERE b.set_id = s.id AND sc.user_id = @viewerId AND sc.passed AND sc.rank = ANY(@ranks))");
            param.Add("ranks", ranks);
        }

        string played = q["played"].ToString().ToLowerInvariant();
        if (signedIn && played is "played" or "unplayed")
        {
            where.Append(played == "unplayed" ? "\nAND NOT EXISTS" : "\nAND EXISTS")
                 .Append(" (SELECT 1 FROM scores sc JOIN beatmaps b ON b.id = sc.beatmap_id WHERE b.set_id = s.id AND sc.user_id = @viewerId)");
        }

        // The client sends nsfw=false when the player has turned explicit content off.
        if (q["nsfw"].ToString().Equals("false", StringComparison.OrdinalIgnoreCase))
            where.Append("\nAND NOT s.explicit");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // Same typo-fallback rule as the website listing: the typo layer only answers a search that
        // nothing matches literally under the other filters.
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
                tier = null;
            }

            where.Append("\nAND ").Append(textWhere);
        }

        // sort=<criteria>_<asc|desc>.
        string[] sortParts = q["sort"].ToString().ToLowerInvariant().Split('_', 2);
        string dir = sortParts.Length > 1 && sortParts[1] == "asc" ? "ASC" : "DESC";
        string orderBy = sortParts[0] switch
        {
            "title" => $"ORDER BY s.title {dir}, s.id DESC",
            "artist" => $"ORDER BY s.artist {dir}, s.id DESC",
            "difficulty" => $"ORDER BY (SELECT max(b.difficulty_rating) FROM beatmaps b WHERE b.set_id = s.id AND b.filename IS NOT NULL) {dir} NULLS LAST, s.id DESC",
            "plays" => $"ORDER BY s.play_count {dir}, s.id DESC",
            "favourites" => $"ORDER BY s.favourite_count {dir}, s.id DESC",
            "updated" => $"ORDER BY s.updated_at {dir}, s.id DESC",
            // Free text on relevance or the default (newest) sort: best match tier first, newest within a
            // tier, like the website listing. Explicit sorts keep their own order.
            "relevance" or "ranked" or "" when tier is not null && dir == "DESC" => $"ORDER BY {tier}, s.submitted_at DESC, s.id DESC",
            _ => $"ORDER BY s.submitted_at {dir}, s.id DESC",
        };

        int offset = int.TryParse(q["cursor[offset]"], out int parsed) && parsed > 0 ? parsed : 0;
        param.Add("offset", offset);

        int total = await conn.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM beatmapsets s JOIN users u ON u.id = s.owner_id\n{where}", param);

        var sets = await BeatmapsetCards.LoadAsync(conn,
            $"""
             FROM beatmapsets s JOIN users u ON u.id = s.owner_id
             {where}
             {orderBy}
             LIMIT {PageSize} OFFSET @offset
             """,
            param, $"{ctx.Request.Scheme}://{ctx.Request.Host}", ctx.RequestAborted);

        int next = offset + sets.Count;

        return WireJson.Ok(new
        {
            beatmapsets = sets,
            // osu-web shape: a cursor object while more pages exist, null on the last page.
            cursor = next < total ? new Dictionary<string, object> { ["offset"] = next } : null,
            total,
        });
    }
}
