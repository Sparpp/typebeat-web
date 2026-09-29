using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Gives an account made before country detection existed its country, once. Every account was
/// created with <c>country_code = 'XX'</c> from 001_init.sql until <see cref="CountryResolver"/>
/// landed, and nothing ever wrote the column, so on a signed-in request (website cookie or game
/// bearer) from an account that still reads <c>XX</c> and has never chosen for itself, the request's
/// country is resolved and stored.
///
/// <para>It never overwrites anything. The in-memory check on the already-resolved user skips the
/// whole thing for every account that has a country or has chosen (so for everyone, once it has
/// fired), a request that resolves to no country writes nothing, and the one UPDATE is guarded by
/// <c>country_code = 'XX' AND NOT country_chosen</c>, so it cannot race a Settings save or run twice
/// on two concurrent requests. <c>country_chosen</c> (038_country_chosen.sql) is what keeps a
/// deliberate "No country" (also stored as <c>XX</c>) from being re-detected here.</para>
/// </summary>
public static class CountryBackfill
{
    /// <summary>
    /// Stores the request's country for <paramref name="user"/> when it has none and never chose,
    /// returning the user as it now stands (so the same request already renders the new flag).
    /// </summary>
    public static async Task<AuthedUser> ApplyAsync(HttpContext ctx, AuthedUser user)
    {
        if (user.CountryCode != Countries.Unknown || user.CountryChosen)
            return user;

        string code = CountryResolver.Resolve(ctx);
        if (code == Countries.Unknown)
            return user;

        var db = ctx.RequestServices.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        int updated = await conn.ExecuteAsync(
            """
            UPDATE users SET country_code = @code
            WHERE id = @id AND country_code = 'XX' AND NOT country_chosen
            """,
            new { code, id = user.Id });

        return updated == 1 ? user with { CountryCode = code } : user;
    }
}
