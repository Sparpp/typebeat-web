using SkiaSharp;
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
    /// <exception cref="UnreadableImageException">The background is not a decodable image.</exception>
    public async Task<string> GenerateAsync(Stream backgroundImage, long setId, int versionNo, IFileStore store, CancellationToken ct = default)
    {
        using var source = await LoadWithinBudgetAsync(backgroundImage, MaxSourcePixels, ct);

        foreach (var (name, width, height) in Sizes)
        {
            await WriteCoverFitJpegAsync(source, StoreKeys.Cover(setId, versionNo, name), width, height, store, ct);
            await WriteCoverFitJpegAsync(source, StoreKeys.Cover(setId, versionNo, name + "@2x"), width * 2, height * 2, store, ct);
        }

        return StoreKeys.CoverPrefix(setId, versionNo);
    }

    /// <summary>
    /// Reads only the image header first and refuses a source above <paramref name="maxPixels"/>
    /// before anything is decoded. The refusal is an <see cref="UnreadableImageException"/>, like
    /// every other unreadable upload, so callers that already handle a corrupt image handle an
    /// oversized one the same way.
    /// </summary>
    /// <exception cref="UnreadableImageException">The source is not a decodable image, or is above the budget.</exception>
    public static async Task<SKImage> LoadWithinBudgetAsync(Stream source, long maxPixels, CancellationToken ct = default)
    {
        // Skia's codec reads synchronously, and an upload stream may only allow async reads, so
        // the encoded bytes are buffered once (they are already capped by the upload size limits).
        byte[] bytes;

        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        using var data = SKData.CreateCopy(bytes);

        // A null codec covers both "not an image" and "an image format Skia cannot read".
        using var codec = SKCodec.Create(data)
                          ?? throw new UnreadableImageException("The file is not a decodable image.");

        // The codec has parsed the header only; nothing is decoded until the bitmap below.
        var info = codec.Info;

        if ((long)info.Width * info.Height > maxPixels)
            throw new UnreadableImageException($"The image is {info.Width}x{info.Height}, above the {maxPixels} pixel budget.");

        // Null when the pixel data is corrupt past the header.
        using var bitmap = SKBitmap.Decode(codec)
                           ?? throw new UnreadableImageException("The image data is corrupt.");

        // Immutable, so the image shares the decoded pixels rather than copying them.
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap)
               ?? throw new UnreadableImageException("The image data is corrupt.");
    }

    /// <summary>
    /// Scales <paramref name="source"/> to fill <paramref name="width"/> x <paramref name="height"/>,
    /// crops the overflow around the center (cover-fit), and writes it to <paramref name="key"/> as
    /// a quality-<see cref="JpegQuality"/> JPEG.
    /// </summary>
    public static async Task WriteCoverFitJpegAsync(SKImage source, string key, int width, int height, IFileStore store, CancellationToken ct)
    {
        using var jpeg = CoverFitJpeg(source, width, height);
        await using var stream = jpeg.AsStream();

        await store.WriteObjectAsync(key, stream, ct);
    }

    /// <summary>The cover-fit render behind <see cref="WriteCoverFitJpegAsync"/>, as JPEG bytes.</summary>
    public static SKData CoverFitJpeg(SKImage source, int width, int height)
    {
        // Scale to fill: the larger of the two ratios, so the scaled source covers the target on
        // both axes; the source rectangle that lands on the target is then centered in the source.
        double scale = Math.Max(width / (double)source.Width, height / (double)source.Height);
        float cropWidth = (float)(width / scale);
        float cropHeight = (float)(height / scale);
        float left = (source.Width - cropWidth) / 2f;
        float top = (source.Height - cropHeight) / 2f;

        // Downscaling reads from mipmaps so a large background does not alias; upscaling a small
        // one uses a bicubic (Catmull-Rom) filter.
        var sampling = scale < 1
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.CatmullRom);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} surface.");

        surface.Canvas.DrawImage(source, SKRect.Create(left, top, cropWidth, cropHeight), SKRect.Create(width, height), sampling);
        surface.Canvas.Flush();

        using var rendered = surface.PeekPixels();
        return EncodeJpeg(rendered);
    }

    /// <summary>
    /// Encodes <paramref name="pixels"/> as a quality-<see cref="JpegQuality"/>, 4:2:0 JPEG. JPEG
    /// has no alpha, so a translucent pixel keeps its own (unpremultiplied) colour and drops the
    /// alpha, rather than being darkened toward black.
    /// </summary>
    public static SKData EncodeJpeg(SKPixmap pixels)
    {
        var options = new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore);

        if (pixels.AlphaType != SKAlphaType.Premul)
            return pixels.Encode(options) ?? throw new InvalidOperationException("JPEG encoding failed.");

        using var unpremul = new SKBitmap(pixels.Info.WithAlphaType(SKAlphaType.Unpremul));
        using var target = unpremul.PeekPixels();

        if (!pixels.ReadPixels(target))
            throw new InvalidOperationException("Could not unpremultiply the rendered pixels.");

        return target.Encode(options) ?? throw new InvalidOperationException("JPEG encoding failed.");
    }
}
