namespace Typebeat.Web.Storage;

/// <summary>
/// <see cref="IFileStore"/> on the local filesystem, rooted at the TYPEBEAT_FILE_ROOT config key
/// (prod: the /data volume; dev default: ./data). Keys map 1:1 to relative paths, so the on-disk
/// layout mirrors the future R2 bucket exactly (files/{sha256hex}, covers/..., previews/...,
/// packages/..., downloads/...). Writes go through a temp file + atomic move so a crashed upload
/// never leaves a half-written object at a live key.
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    public const string RootConfigKey = "TYPEBEAT_FILE_ROOT";
    public const string DefaultRoot = "./data";

    private readonly string root;

    public LocalFileStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public static LocalFileStore FromConfiguration(IConfiguration config)
    {
        string? configured = config[RootConfigKey];
        return new LocalFileStore(string.IsNullOrEmpty(configured) ? DefaultRoot : configured);
    }

    public Task<bool> WriteBlobIfAbsentAsync(byte[] sha256, Stream content, CancellationToken ct = default)
        => writeIfAbsentAsync(StoreKeys.Blob(sha256), content, ct);

    public Task<bool> BlobExistsAsync(byte[] sha256, CancellationToken ct = default)
        => Task.FromResult(File.Exists(resolve(StoreKeys.Blob(sha256))));

    public Task<Stream> OpenBlobReadAsync(byte[] sha256, CancellationToken ct = default)
    {
        string path = resolve(StoreKeys.Blob(sha256));

        if (!File.Exists(path))
            throw new FileNotFoundException($"Blob {Convert.ToHexStringLower(sha256)} is not stored.", path);

        return Task.FromResult<Stream>(openRead(path));
    }

    public async Task WriteObjectAsync(string key, Stream content, CancellationToken ct = default)
    {
        string path = resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public Task<bool> ObjectExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(resolve(key)));

    public Task<Stream?> OpenObjectReadAsync(string key, CancellationToken ct = default)
    {
        string path = resolve(key);
        return Task.FromResult(File.Exists(path) ? (Stream?)openRead(path) : null);
    }

    public Task DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        try
        {
            File.Delete(resolve(key));
        }
        catch (DirectoryNotFoundException)
        {
            // Contract: no-op when the object is absent. File.Delete already no-ops on a missing
            // file when its parent directory exists, but throws DirectoryNotFoundException when
            // the directory itself is missing — that's "absent" too, not an error.
        }

        return Task.CompletedTask;
    }

    private async Task<bool> writeIfAbsentAsync(string key, Stream content, CancellationToken ct)
    {
        string path = resolve(key);

        // Blobs are immutable: same key == same bytes, so an existing file is already correct.
        if (File.Exists(path))
            return false;

        await WriteObjectAsync(key, content, ct);
        return true;
    }

    private FileStream openRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>
    /// Maps a store key to an absolute path under the root, rejecting anything that would
    /// escape it (defense in depth — keys are server-generated, never user input).
    /// </summary>
    private string resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\\') || Path.IsPathRooted(key))
            throw new ArgumentException($"Invalid store key '{key}'.", nameof(key));

        string full = Path.GetFullPath(Path.Combine(root, key));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Store key '{key}' escapes the file root.", nameof(key));

        return full;
    }
}
