using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// Preview clipping via the real ffmpeg binary; every ffmpeg-dependent test self-skips when the
/// binary is absent (the pipeline itself degrades to "no preview" in that case).
/// </summary>
public class PreviewGeneratorTest
{
    private string root = null!;
    private LocalFileStore store = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "typebeat-previewtest-" + Guid.NewGuid().ToString("N"));
        store = new LocalFileStore(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task Generate_FromWav_AtExplicitPreviewTime()
    {
        requireFfmpeg();

        using var audio = new MemoryStream(makeWav(seconds: 3));

        var status = await new PreviewGenerator().GenerateAsync(audio, previewTimeMs: 500, setId: 7, store);

        Assert.That(status, Is.EqualTo(PreviewGenerator.Status.Generated));

        await using var preview = await store.OpenObjectReadAsync(StoreKeys.Preview(7));
        Assert.That(preview, Is.Not.Null);
        Assert.That(preview!.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task Generate_UnsetPreviewTime_FallsBackToFortyPercent()
    {
        requireFfmpeg();

        using var audio = new MemoryStream(makeWav(seconds: 3));

        var status = await new PreviewGenerator().GenerateAsync(audio, previewTimeMs: -1, setId: 8, store);

        Assert.That(status, Is.EqualTo(PreviewGenerator.Status.Generated));
        Assert.That(await store.ObjectExistsAsync(StoreKeys.Preview(8)), Is.True);
    }

    [Test]
    public async Task Generate_UndecodableAudio_ReportsFailure()
    {
        requireFfmpeg();

        using var junk = new MemoryStream(SyntheticPackage.Utf8("not audio"));

        var status = await new PreviewGenerator().GenerateAsync(junk, previewTimeMs: 0, setId: 9, store);

        Assert.That(status, Is.EqualTo(PreviewGenerator.Status.Failed));
        Assert.That(await store.ObjectExistsAsync(StoreKeys.Preview(9)), Is.False);
    }

    private static void requireFfmpeg()
    {
        if (!PreviewGenerator.IsFfmpegAvailable())
            Assert.Ignore("ffmpeg not on PATH — the pipeline records ffmpeg_missing and skips previews.");
    }

    /// <summary>A minimal PCM16 mono WAV: a 440 Hz tone, small enough to build inline.</summary>
    private static byte[] makeWav(int seconds)
    {
        const int sample_rate = 8000;
        int samples = sample_rate * seconds;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);              // fmt chunk size
        writer.Write((short)1);        // PCM
        writer.Write((short)1);        // mono
        writer.Write(sample_rate);
        writer.Write(sample_rate * 2); // byte rate
        writer.Write((short)2);        // block align
        writer.Write((short)16);       // bits per sample
        writer.Write("data"u8);
        writer.Write(samples * 2);

        for (int i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sample_rate) * 8000));

        writer.Flush();
        return buffer.ToArray();
    }
}
