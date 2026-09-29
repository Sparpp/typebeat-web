namespace Typebeat.Web.Auth;

/// <summary>
/// Which country a request comes from, the way osu! does it: read ONCE, from the request's IP,
/// when an account is created (and once more by <see cref="CountryBackfill"/> for an account made
/// before this existed), stored as the ISO 3166-1 alpha-2 code, and never re-detected afterwards.
///
/// <para>The IP is never looked up here. Production sits behind Cloudflare, which already resolves
/// every request's IP and forwards the answer as the <c>CF-IPCountry</c> header, so no GeoIP
/// database ships with the app and the IP itself is never stored.</para>
///
/// <para><b>THE HEADER IS TRUSTED ONLY BEHIND THE PROXY</b> (<c>TYPEBEAT_BEHIND_PROXY</c>, the same
/// flag that makes Program.cs honour <c>X-Forwarded-*</c>). A direct connection to the origin can
/// send any header it likes, so without the flag every request reads as <see cref="Countries.Unknown"/>.
/// Behind the proxy, a value is accepted only when it is a country the game client's enum knows
/// (<see cref="Countries.IsCountry"/>); Cloudflare's non-country answers (<c>XX</c> for unknown,
/// <c>T1</c> for Tor) and anything malformed also resolve to <see cref="Countries.Unknown"/>.</para>
/// </summary>
public static class CountryResolver
{
    public const string HeaderName = "CF-IPCountry";

    public const string ProxyFlag = "TYPEBEAT_BEHIND_PROXY";

    /// <summary>The request's country, or <see cref="Countries.Unknown"/>.</summary>
    public static string Resolve(HttpContext ctx)
    {
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        return Resolve(Flags.IsEnabled(config, ProxyFlag), ctx.Request.Headers[HeaderName].ToString());
    }

    /// <summary>
    /// The pure rule: <paramref name="header"/> is honoured only when <paramref name="behindProxy"/>,
    /// and only when it names a storable country. Case is normalised (Cloudflare sends upper case,
    /// but the header is not ours to rely on), surrounding whitespace is ignored.
    /// </summary>
    public static string Resolve(bool behindProxy, string? header)
    {
        if (!behindProxy || string.IsNullOrWhiteSpace(header))
            return Countries.Unknown;

        string code = header.Trim().ToUpperInvariant();
        return Countries.IsCountry(code) ? code : Countries.Unknown;
    }
}
