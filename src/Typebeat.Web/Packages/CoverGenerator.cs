using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Renders the four osu cover buckets (each @1x and @2x) from a set's background image at upload
/// time. The forked lazer client consumes exactly these keys (card/cover/list/slimcover), so the
/// sizes are fixed. Center-crop cover-fit, JPEG quality ~80.
/// </summary>
public sealed class CoverGenerator
{
    public const int JpegQuality = 80;

    /// <summary>
    /// Largest source image, in pixels (width x height), that is decoded at all, here and in
    /// <see cref="ProfileMedia"/>. Real backgrounds top out around 8K UHD (7680x4320, about 33 MP)
    /// and the largest output is the 3840x720 slimcover@2x, so this sits well above any honest
    /// upload; what it stops is a small file whose header declares dimensions that would decode
    /// into gigabytes.
    /// </summary>
    public const long MaxSourcePixels = 8192L * 8192;

    /// <summary>The @1x bucket sizes; @2x doubles both dimensions.</summary>
    public static readonly IReadOnlyList<(string Name, int Width, int Height)> Sizes =
    [
        ("card", 400, 140),
        ("cover", 900, 250),
        ("list", 150, 150),
        ("slimcover", 1920, 360),
    ];

    /// <summary>
    /// Generates all eight cover jpegs under <c>covers/{setId}/{versionNo}/</c>.
    /// </summary>
    /// <returns>The cover key prefix to store in <c>beatmapsets.cover_key</c>.</returns>
    /// <exception cref="UnknownImageFormatException">The background is not a decodable image.</exception>
    public async Task<string> GenerateAsync(Stream backgroundImage, long setId, int versionNo, IFileStore store, CancellationToken ct = default)
    {
        using var source = await LoadWithinBudgetAsync(backgroundImage, MaxSourcePixels, ct);

        var encoder = new JpegEncoder { Quality = JpegQuality };

        foreach (var (name, width, height) in Sizes)
        {
            await writeAsync(source, StoreKeys.Cover(setId, versionNo, name), width, height, encoder, store, ct);
            await writeAsync(source, StoreKeys.Cover(setId, versionNo, name + "@2x"), width * 2, height * 2, encoder, store, ct);
        }

        return StoreKeys.CoverPrefix(setId, versionNo);
    }

    /// <summary>
    /// Reads only the image header first and refuses a source above <paramref name="maxPixels"/>
    /// before anything is decoded. The refusal is an <c>InvalidImageContentException</c>, an
    /// <c>ImageFormatException</c> like every other unreadable upload, so callers that already
    /// handle a corrupt image handle an oversized one the same way.
    /// </summary>
    /// <exception cref="UnknownImageFormatException">The source is not a decodable image.</exception>
    public static async Task<Image> LoadWithinBudgetAsync(Stream source, long maxPixels, CancellationToken ct = default)
    {
        // Identify and decode both read from the start, so a stream that cannot rewind is
        // buffered once (every current caller passes a seekable one).
        if (!source.CanSeek)
        {
            var copy = new MemoryStream();
            await source.CopyToAsync(copy, ct);
            copy.Position = 0;
            source = copy;
        }

        long start = source.Position;
        var info = await Image.IdentifyAsync(source, ct);

        if ((long)info.Width * info.Height > maxPixels)
            throw new InvalidImageContentException($"The image is {info.Width}x{info.Height}, above the {maxPixels} pixel budget.");

        source.Position = start;
        return await Image.LoadAsync(source, ct);
    }

    private static async Task writeAsync(Image source, string key, int width, int height, JpegEncoder encoder, IFileStore store, CancellationToken ct)
    {
        // ResizeMode.Crop = scale to fill, crop the overflow around the center anchor (cover-fit).
        using var sized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
        }));

        using var buffer = new MemoryStream();
        await sized.SaveAsJpegAsync(buffer, encoder, ct);
        buffer.Position = 0;

        await store.WriteObjectAsync(key, buffer, ct);
    }
}
