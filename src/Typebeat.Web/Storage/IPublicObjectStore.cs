namespace Typebeat.Web.Storage;

/// <summary>
/// A PUBLIC object bucket (Cloudflare R2 behind a custom domain) that the large download bytes
/// can be handed off to with a 302, so the box's uplink stops carrying them (backlog 364).
///
/// <para>Deliberately PARALLEL to <see cref="IFileStore"/>, not a replacement for it. The local
/// store keeps every byte it holds today: the audio-only download needs a seekable local package,
/// the startup sweeps read every blob, the /download page and the landing CTA ask it about
/// installers, and the direct-origin bss.* hosts keep streaming from it (DUAL-HOME). This store
/// only ever holds COPIES, and only of what is safe to hand to an unauthenticated CDN: release
/// feed packages, the installers and the latest package of each set under a content-unique key.
/// It is not a backup.</para>
///
/// <para>Off unless every TYPEBEAT_R2_* key is set (<see cref="PublicObjectStoreOptions"/>). Off is
/// <see cref="DisabledPublicObjectStore"/>, and every caller checks <see cref="Enabled"/> first,
/// so an unconfigured deployment behaves byte for byte as it did before this existed.</para>
/// </summary>
public interface IPublicObjectStore
{
    /// <summary>True when a bucket is configured. Every caller gates on this first.</summary>
    bool Enabled { get; }

    /// <summary>One line for the startup log: which mode is live, never a secret.</summary>
    string Description { get; }

    /// <summary>
    /// Uploads <paramref name="content"/> (seekable, read from its current position) to
    /// <paramref name="key"/>, replacing any object already there. The HTTP metadata is frozen at
    /// PUT time: the edge serves exactly these Cache-Control and Content-Disposition values.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, string cacheControl, string? contentDisposition, CancellationToken ct = default);

    /// <summary>Deletes <paramref name="key"/>; deleting an absent key is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>The stored object's size in bytes, or null when it is absent.</summary>
    Task<long?> StatAsync(string key, CancellationToken ct = default);

    /// <summary>The absolute https URL the public custom domain serves <paramref name="key"/> at.</summary>
    string PublicUrl(string key);

    /// <summary>
    /// True for the direct-origin hosts (bss.typebeat.sh, bss.typebeat.mingda.sh by default) that
    /// must keep streaming from the box rather than redirecting: they exist precisely for the
    /// players whose sustained transfers stall on the Cloudflare path (backlog 191), and the R2
    /// custom domain is on that same path.
    /// </summary>
    bool IsDirectHost(string host);
}

/// <summary>
/// The configuration of the public bucket. All five of TYPEBEAT_R2_ENDPOINT, TYPEBEAT_R2_BUCKET,
/// TYPEBEAT_R2_ACCESS_KEY_ID, TYPEBEAT_R2_SECRET_ACCESS_KEY and TYPEBEAT_R2_PUBLIC_BASE_URL are
/// required; TYPEBEAT_R2_DIRECT_HOSTS (comma separated) is optional.
/// </summary>
public sealed record PublicObjectStoreOptions(
    string Endpoint,
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    string PublicBaseUrl,
    IReadOnlySet<string> DirectHosts)
{
    public const string EndpointKey = "TYPEBEAT_R2_ENDPOINT";
    public const string BucketKey = "TYPEBEAT_R2_BUCKET";
    public const string AccessKeyIdKey = "TYPEBEAT_R2_ACCESS_KEY_ID";
    public const string SecretAccessKeyKey = "TYPEBEAT_R2_SECRET_ACCESS_KEY";
    public const string PublicBaseUrlKey = "TYPEBEAT_R2_PUBLIC_BASE_URL";
    public const string DirectHostsKey = "TYPEBEAT_R2_DIRECT_HOSTS";

    /// <summary>The direct-origin hosts Caddy serves without the Cloudflare proxy (Caddyfile, backlog 191).</summary>
    public static readonly string[] DefaultDirectHosts = ["bss.typebeat.sh", "bss.typebeat.mingda.sh"];

    /// <summary>
    /// The options, or null (with the reason) when the bucket is not fully configured. The public
    /// base URL must be an absolute https URL: .NET's HttpClient refuses an https-to-http redirect,
    /// so an http base would silently break every installed client's update.
    /// </summary>
    public static PublicObjectStoreOptions? FromConfiguration(IConfiguration config, out string reason)
    {
        string? endpoint = config[EndpointKey];
        string? bucket = config[BucketKey];
        string? accessKeyId = config[AccessKeyIdKey];
        string? secret = config[SecretAccessKeyKey];
        string? publicBase = config[PublicBaseUrlKey];

        string[] missing = new[]
            {
                (EndpointKey, endpoint), (BucketKey, bucket), (AccessKeyIdKey, accessKeyId),
                (SecretAccessKeyKey, secret), (PublicBaseUrlKey, publicBase),
            }
            .Where(p => string.IsNullOrWhiteSpace(p.Item2))
            .Select(p => p.Item1)
            .ToArray();

        if (missing.Length == 5)
        {
            reason = "not configured";
            return null;
        }

        if (missing.Length > 0)
        {
            reason = "incomplete, missing " + string.Join(", ", missing);
            return null;
        }

        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            reason = PublicBaseUrlKey + " is not an absolute https URL";
            return null;
        }

        string? hosts = config[DirectHostsKey];
        var directHosts = new HashSet<string>(
            string.IsNullOrWhiteSpace(hosts)
                ? DefaultDirectHosts
                : hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        reason = "";
        return new PublicObjectStoreOptions(endpoint!.Trim(), bucket!.Trim(), accessKeyId!.Trim(), secret!.Trim(),
            publicBase!.Trim().TrimEnd('/'), directHosts);
    }

    /// <summary>{base}/{key} with each key segment percent-encoded (the slashes stay paths).</summary>
    public static string BuildPublicUrl(string publicBaseUrl, string key)
        => publicBaseUrl.TrimEnd('/') + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>The off state: nothing is configured, nothing is redirected, nothing is uploaded.</summary>
public sealed class DisabledPublicObjectStore(string reason = "not configured") : IPublicObjectStore
{
    public bool Enabled => false;

    public string Description => "disabled (" + reason + "), large downloads stream from the box";

    public Task PutAsync(string key, Stream content, string contentType, string cacheControl, string? contentDisposition, CancellationToken ct = default)
        => throw new InvalidOperationException("The public object store is disabled.");

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => throw new InvalidOperationException("The public object store is disabled.");

    public Task<long?> StatAsync(string key, CancellationToken ct = default) => Task.FromResult<long?>(null);

    public string PublicUrl(string key) => throw new InvalidOperationException("The public object store is disabled.");

    public bool IsDirectHost(string host) => true;
}

/// <summary>
/// The HTTP metadata each kind of public object is stored with, in one place so the ingest, the
/// backfill and the docs cannot disagree.
/// </summary>
public static class PublicObjectMetadata
{
    /// <summary>
    /// Packages and feed nupkgs: one day, the takedown doctrine every other set medium follows
    /// (MediaEndpoints' covers and previews). Package keys are content-unique, so a re-upload never
    /// needs the old entry to expire; a takedown does, and is also purged by hand.
    /// </summary>
    public const string PackageCacheControl = "public, max-age=86400";

    /// <summary>Installers keep STABLE names across releases, so the edge must revalidate by ETag.</summary>
    public const string InstallerCacheControl = "no-cache";

    /// <summary>
    /// An attachment disposition carrying <paramref name="fileName"/> both as an ASCII fallback and
    /// RFC 5987 encoded, since S3 metadata headers must be ASCII to sign.
    /// </summary>
    public static string AttachmentDisposition(string fileName)
    {
        string ascii = new(fileName.Select(c => c is < ' ' or > '~' or '"' or '\\' ? '_' : c).ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
