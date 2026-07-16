using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/me/ and /api/v2/me/{ruleset} — the logged-in user fetch APIAccess issues at the
/// end of its connect sequence (GetMeRequest, Target "me/{ruleset?.ShortName}"). The ruleset
/// segment is ignored: there is only one ruleset. Returns the APIMe payload built by UserWire.
/// </summary>
public static class MeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GetMeRequest with a null ruleset targets "me/" (trailing slash); with a ruleset it
        // targets "me/typebeat". Both resolve to the same authed user.
        app.MapGet("/api/v2/me/", Handle).RequireBearer();
        app.MapGet("/api/v2/me/{ruleset}", Handle).RequireBearer();
    }

    private static async Task<IResult> Handle(HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();

        // The game client fetches /me at every connect, which makes it the natural "last seen
        // in game" signal — same throttled touch the website's cookie auth applies.
        await LastVisit.TouchAsync(db, user.Id, ctx.RequestAborted);

        return WireJson.Ok(UserWire.Me(user, ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty));
    }
}
