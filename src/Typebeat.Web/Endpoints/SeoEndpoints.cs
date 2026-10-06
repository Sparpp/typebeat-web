using System.Globalization;
using System.Text;
using System.Xml;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// /robots.txt and /sitemap.xml (backlog 369). Both are anonymous GETs allow-listed as wire
/// routes in Program.IsWireRoute, so the styled error pages never wrap them.
/// </summary>
public static class SeoEndpoints
{
    /// <summary>The sitemap protocol's per-file cap; above it /sitemap.xml becomes an index.</summary>
    public const int MAX_URLS_PER_FILE = 50_000;

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Public pages with no row behind them, listed first and without a lastmod.</summary>
    public static readonly string[] StaticPaths = ["/", "/download", "/beatmapsets", "/rankings", "/legal/dmca", "/legal/privacy"];

    public sealed record SitemapUrl(string Location, DateTime? LastModified);

    private sealed record Snapshot(DateTime BuiltAt, IReadOnlyList<SitemapUrl> Urls);

    private static volatile Snapshot? cached;

    /// <summary>Drops the cached URL list, so the next request reads the database again.</summary>
    public static void ResetCache() => cached = null;

    public static void Map(WebApplication app)
    {
        app.MapGet("/robots.txt", () => Results.Text(Seo.ROBOTS_TXT, "text/plain; charset=utf-8"));
        app.MapGet("/sitemap.xml", GetSitemapAsync);
    }

    private static async Task<IResult> GetSitemapAsync(Db db, HttpContext ctx, int? page)
    {
        var urls = await getUrlsAsync(db, ctx.RequestAborted);

        int files = Math.Max(1, (urls.Count + MAX_URLS_PER_FILE - 1) / MAX_URLS_PER_FILE);

        string xml;
        if (files == 1)
            xml = WriteUrlSet(urls);
        else if (page is null)
            xml = WriteIndex(files);
        else if (page < 1 || page > files)
            return Results.NotFound();
        else
            xml = WriteUrlSet(urls.Skip((page.Value - 1) * MAX_URLS_PER_FILE).Take(MAX_URLS_PER_FILE).ToList());

        return Results.Text(xml, "application/xml; charset=utf-8");
    }

    private static async Task<IReadOnlyList<SitemapUrl>> getUrlsAsync(Db db, CancellationToken ct)
    {
        var snapshot = cached;
        if (snapshot is not null && DateTime.UtcNow - snapshot.BuiltAt < CacheLifetime)
            return snapshot.Urls;

        await using var conn = await db.OpenAsync(ct);

        // Published is pending, unranked, ranked or loved (BeatmapsetEndpoints.IsPublished); a
        // restricted owner's sets are delisted everywhere else, so they are left out here too.
        // lastmod is the latest uploaded version, falling back to the submit time for a set that
        // predates set_versions.
        var sets = await conn.QueryAsync<(long Id, DateTime LastModified)>(new CommandDefinition(
            """
            SELECT s.id, COALESCE(MAX(v.created_at), s.submitted_at)
              FROM beatmapsets s
              JOIN users u ON u.id = s.owner_id
              LEFT JOIN set_versions v ON v.set_id = s.id
             WHERE s.status IN ('pending', 'unranked', 'ranked', 'loved') AND NOT u.restricted
             GROUP BY s.id
             ORDER BY s.id
            """, cancellationToken: ct));

        // Every unrestricted profile with at least one score, lastmod its latest score.
        var users = await conn.QueryAsync<(long Id, DateTime LastModified)>(new CommandDefinition(
            """
            SELECT u.id, MAX(sc.ended_at)
              FROM users u
              JOIN scores sc ON sc.user_id = u.id
             WHERE NOT u.restricted
             GROUP BY u.id
             ORDER BY u.id
            """, cancellationToken: ct));

        var urls = new List<SitemapUrl>();
        urls.AddRange(StaticPaths.Select(p => new SitemapUrl(Seo.CanonicalUrl(p), null)));
        urls.AddRange(sets.Select(s => new SitemapUrl($"{Seo.CANONICAL_ORIGIN}/beatmapsets/{s.Id}", s.LastModified)));
        urls.AddRange(users.Select(u => new SitemapUrl($"{Seo.CANONICAL_ORIGIN}/users/{u.Id}", u.LastModified)));

        cached = new Snapshot(DateTime.UtcNow, urls);
        return urls;
    }

    private const string sitemap_ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public static string WriteUrlSet(IReadOnlyList<SitemapUrl> urls)
        => write(w =>
        {
            w.WriteStartElement("urlset", sitemap_ns);
            foreach (var url in urls)
            {
                w.WriteStartElement("url", sitemap_ns);
                w.WriteElementString("loc", sitemap_ns, url.Location);
                if (url.LastModified is DateTime lastModified)
                    w.WriteElementString("lastmod", sitemap_ns, formatDate(lastModified));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });

    public static string WriteIndex(int files)
        => write(w =>
        {
            w.WriteStartElement("sitemapindex", sitemap_ns);
            for (int i = 1; i <= files; i++)
            {
                w.WriteStartElement("sitemap", sitemap_ns);
                w.WriteElementString("loc", sitemap_ns, $"{Seo.CANONICAL_ORIGIN}/sitemap.xml?page={i}");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });

    private static string formatDate(DateTime value)
        => DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
                   .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string write(Action<XmlWriter> body)
    {
        var buffer = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriterUtf8(buffer), new XmlWriterSettings { Indent = false }))
        {
            writer.WriteStartDocument();
            body(writer);
            writer.WriteEndDocument();
        }
        return buffer.ToString();
    }

    /// <summary>A StringWriter that declares UTF-8, so the XML prolog matches the response charset.</summary>
    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
