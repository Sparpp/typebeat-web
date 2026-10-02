using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Typebeat.Web.Storage;

/// <summary>
/// <see cref="IPublicObjectStore"/> on Cloudflare R2 through its S3 API (AWSSDK.S3). Uploads go to
/// the account's S3 endpoint directly (never through a proxied zone: the 100 MB proxied-body cap
/// would truncate every package), reads come from the custom domain in
/// <see cref="PublicObjectStoreOptions.PublicBaseUrl"/>.
///
/// <para>Two R2-specific settings matter. The payload is signed whole rather than aws-chunked
/// (<c>UseChunkEncoding = false</c>; every caller hands over a seekable stream), and the SDK's
/// CRC32 checksum trailers are set to WHEN_REQUIRED on both directions, because R2 rejects the
/// trailer the 2025 SDKs started sending by default.</para>
/// </summary>
public sealed class R2PublicObjectStore : IPublicObjectStore, IDisposable
{
    private readonly PublicObjectStoreOptions options;
    private readonly IAmazonS3 client;

    public R2PublicObjectStore(PublicObjectStoreOptions options)
        : this(options, new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "auto",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            }))
    {
    }

    /// <summary>For a caller that wants to hand in its own client (nothing in the app does today).</summary>
    public R2PublicObjectStore(PublicObjectStoreOptions options, IAmazonS3 client)
    {
        this.options = options;
        this.client = client;
    }

    public bool Enabled => true;

    public string Description
        => $"Cloudflare R2 bucket '{options.Bucket}' at {options.Endpoint}, public at {options.PublicBaseUrl}, direct hosts {string.Join(", ", options.DirectHosts.Order(StringComparer.OrdinalIgnoreCase))}";

    public async Task PutAsync(string key, Stream content, string contentType, string cacheControl, string? contentDisposition, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            ContentType = contentType,
            UseChunkEncoding = false,
        };

        request.Headers.CacheControl = cacheControl;

        if (contentDisposition != null)
            request.Headers.ContentDisposition = contentDisposition;

        await client.PutObjectAsync(request, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
        => await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Bucket, Key = key }, ct);

    public async Task<long?> StatAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = options.Bucket, Key = key }, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PublicObjectInfo>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var objects = new List<PublicObjectInfo>();
        var request = new ListObjectsV2Request { BucketName = options.Bucket, Prefix = prefix };

        while (true)
        {
            var page = await client.ListObjectsV2Async(request, ct);

            foreach (var o in page.S3Objects ?? [])
                objects.Add(new PublicObjectInfo(o.Key, o.Size ?? 0, o.ETag?.Trim('"')));

            if (page.IsTruncated != true || string.IsNullOrEmpty(page.NextContinuationToken))
                return objects;

            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    public string PublicUrl(string key) => PublicObjectStoreOptions.BuildPublicUrl(options.PublicBaseUrl, key);

    public bool IsDirectHost(string host) => options.DirectHosts.Contains(host);

    public void Dispose() => client.Dispose();
}
