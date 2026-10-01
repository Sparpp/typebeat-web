using System.Globalization;
using System.Text.Json;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/ops/backups: how old the newest VERIFIED offsite copy of each backup kind is, for
/// an external freshness alert to poll (backlog 367; the Discord bot is the intended consumer). Same footing as <see cref="OpsEndpoints"/>:
/// private, gated by <c>TYPEBEAT_BUDDY_KEY</c> through <see cref="BuddyEndpoints.Authorised"/>,
/// 404 when the key is unset. Kept in its own module so it maps independently of the disk readout.
///
/// <para>Where the numbers come from: deploy/backup.sh, after it has uploaded an archive to R2 AND
/// read the remote size back equal to the local one, writes a stamp into the app's own file root
/// (<c>/data/ops/offsite-db.json</c>, <c>/data/ops/offsite-appdata.json</c>) of the shape
/// <c>{"kind":"db","file":"typebeat_X.dump.gz","bytes":N,"uploadedAt":"2026-10-01T03:17:42Z"}</c>.
/// This endpoint only reads those files back.</para>
///
/// <para>Why a stamp the APP serves and not an alert in the script: a cron job that never runs
/// (crontab wiped, box rebuilt, the script erroring before its first echo) cannot announce its own
/// absence. Something that runs anyway has to notice that nothing new has arrived, and the bot
/// already polls this host for the disk readout.</para>
///
/// <para>THE HONESTY RULE, the same one the disk readout follows: anything that is not a well-formed
/// stamp of the right kind reads as <c>null</c>, which a consumer must treat as STALE. Missing file,
/// unreadable file, garbage, a zero-byte or wrong-kind stamp, and a stamp dated more than
/// <see cref="FutureTolerance"/> in the future (which would otherwise read as fresh for as long as
/// the bad clock lasts) all land there. There is no failure mode that answers "fresh".</para>
///
/// <para>Like the disk readout this holds NO threshold and no state: it reports ages, and the poller owns
/// the threshold (48 hours is the intended db line), the edge trigger and the recovery message.</para>
/// </summary>
public static class OpsBackupsEndpoints
{
    /// <summary>Directory under the file root that backup.sh writes its stamps into.</summary>
    public const string StampDirectory = "ops";

    /// <summary>Clock skew between the host (which stamps) and the container (which reads) that is
    /// still accepted, with the age clamped to zero.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/ops/backups", Backups);
    }

    private static IResult Backups(HttpContext ctx, IConfiguration config)
    {
        if (!BuddyEndpoints.Authorised(ctx, config, out IResult? failure))
            return failure!;

        var readout = Read(OpsEndpoints.FileRoot(config), DateTimeOffset.UtcNow);

        return Results.Json(new
        {
            db = project(readout.Db),
            appdata = project(readout.Appdata),
        });
    }

    private static object? project(OffsiteStamp? stamp) => stamp is not { } s
        ? null
        : new
        {
            file = s.File,
            bytes = s.Bytes,
            uploadedAt = s.UploadedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ageSeconds = s.AgeSeconds,
        };

    /// <summary>Both stamps under <paramref name="fileRoot"/>, aged against <paramref name="now"/>.</summary>
    public static BackupsReadout Read(string fileRoot, DateTimeOffset now)
        => new(ReadStamp(StampPath(fileRoot, "db"), "db", now), ReadStamp(StampPath(fileRoot, "appdata"), "appdata", now));

    /// <summary>The path backup.sh writes a kind's stamp to (the root is /data in prod).</summary>
    public static string StampPath(string fileRoot, string kind)
        => Path.Combine(fileRoot, StampDirectory, $"offsite-{kind}.json");

    /// <summary>One stamp file, or null (stale) when it is absent or cannot be read.</summary>
    public static OffsiteStamp? ReadStamp(string path, string kind, DateTimeOffset now)
    {
        string json;

        try
        {
            if (!File.Exists(path))
                return null;

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(json, kind, now);
    }

    /// <summary>
    /// The whole judgement, pure. Returns null for anything that is not a complete stamp of
    /// <paramref name="expectedKind"/>: see the honesty rule on the class.
    /// </summary>
    public static OffsiteStamp? Parse(string? json, string expectedKind, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // A db stamp sitting in the appdata slot (or the reverse) is not evidence of either.
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != expectedKind)
                return null;

            if (!root.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString()))
                return null;

            // An empty archive is not a backup, whatever its timestamp says.
            if (!root.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out long size) || size <= 0)
                return null;

            if (!root.TryGetProperty("uploadedAt", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var uploadedAt))
                return null;

            TimeSpan age = now - uploadedAt;

            if (age < -FutureTolerance)
                return null;

            long ageSeconds = Math.Max(0, (long)Math.Floor(age.TotalSeconds));

            return new OffsiteStamp(file.GetString()!, size, uploadedAt, ageSeconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A verified offsite copy: its file name, size, upload time and age at read time.</summary>
    public readonly record struct OffsiteStamp(string File, long Bytes, DateTimeOffset UploadedAt, long AgeSeconds);

    /// <summary>Both kinds; null means STALE (no trustworthy stamp).</summary>
    public readonly record struct BackupsReadout(OffsiteStamp? Db, OffsiteStamp? Appdata);
}
