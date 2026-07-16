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
        using var source = await Image.LoadAsync(backgroundImage, ct);

        var encoder = new JpegEncoder { Quality = JpegQuality };

        foreach (var (name, width, height) in Sizes)
        {
            await writeAsync(source, StoreKeys.Cover(setId, versionNo, name), width, height, encoder, store, ct);
            await writeAsync(source, StoreKeys.Cover(setId, versionNo, name + "@2x"), width * 2, height * 2, encoder, store, ct);
        }

        return StoreKeys.CoverPrefix(setId, versionNo);
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
