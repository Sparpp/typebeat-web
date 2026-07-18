using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Turns an uploaded avatar/banner into a fixed-size JPEG in the file store. Center-crop
/// cover-fit + quality-80, mirroring <see cref="CoverGenerator"/>. Keys are version-stamped
/// (<see cref="StoreKeys.Avatar"/>/<see cref="StoreKeys.UserCover"/>) so each replacement gets a
/// fresh, immutable URL — no cache-busting query string, and a stale avatar never lingers in a
/// browser or edge cache after the user changes it.
/// </summary>
public static class ProfileMedia
{
    public const int AvatarSize = 256;
    public const int BannerWidth = 1500;
    public const int BannerHeight = 500;

    /// <summary>Writes a square avatar JPEG; returns the key to store in <c>users.avatar_key</c>.</summary>
    /// <exception cref="UnknownImageFormatException">The upload is not a decodable image.</exception>
    public static Task<string> WriteAvatarAsync(Stream upload, long userId, long version, IFileStore store, CancellationToken ct = default)
        => writeAsync(upload, StoreKeys.Avatar(userId, version), AvatarSize, AvatarSize, store, ct);

    /// <summary>Writes a 3:1 banner JPEG; returns the key to store in <c>users.cover_key</c>.</summary>
    /// <exception cref="UnknownImageFormatException">The upload is not a decodable image.</exception>
    public static Task<string> WriteBannerAsync(Stream upload, long userId, long version, IFileStore store, CancellationToken ct = default)
        => writeAsync(upload, StoreKeys.UserCover(userId, version), BannerWidth, BannerHeight, store, ct);

    private static async Task<string> writeAsync(Stream upload, string key, int width, int height, IFileStore store, CancellationToken ct)
    {
        // Throws UnknownImageFormatException on a non-image upload; the page turns that into a
        // friendly form error rather than a 500.
        using var image = await Image.LoadAsync(upload, ct);

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop, // scale to fill, crop the overflow around the centre.
        }));

        using var buffer = new MemoryStream();
        await image.SaveAsJpegAsync(buffer, new JpegEncoder { Quality = CoverGenerator.JpegQuality }, ct);
        buffer.Position = 0;

        await store.WriteObjectAsync(key, buffer, ct);
        return key;
    }
}
