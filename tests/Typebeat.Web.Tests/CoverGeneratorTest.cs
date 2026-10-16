using SixLabors.ImageSharp;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// Cover-bucket rendering from a tiny in-memory PNG through a temp-rooted LocalFileStore.
/// </summary>
public class CoverGeneratorTest
{
    private string root = null!;
    private LocalFileStore store = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "typebeat-covertest-" + Guid.NewGuid().ToString("N"));
        store = new LocalFileStore(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task Generate_ProducesAllEightBucketsWithExactDimensions()
    {
        using var background = new MemoryStream(SyntheticPackage.TinyPng());

        string prefix = await new CoverGenerator().GenerateAsync(background, setId: 42, versionNo: 3, store);

        Assert.That(prefix, Is.EqualTo("covers/42/3"));

        foreach (var (name, width, height) in CoverGenerator.Sizes)
        {
            await assertJpegAsync(StoreKeys.Cover(42, 3, name), width, height);
            await assertJpegAsync(StoreKeys.Cover(42, 3, name + "@2x"), width * 2, height * 2);
        }
    }

    [Test]
    public void Generate_UndecodableBackground_Throws()
    {
        // The ingest catches this and records CoverStatus = "failed" (graceful skip).
        using var junk = new MemoryStream(SyntheticPackage.Utf8("not an image"));

        Assert.ThrowsAsync<UnknownImageFormatException>(
            () => new CoverGenerator().GenerateAsync(junk, 1, 1, store));
    }

    [Test]
    public void LoadWithinBudget_OverBudget_ThrowsBeforeDecoding()
    {
        // The 64x48 fixture is 3072 pixels. The refusal must be an ImageFormatException, the type
        // the settings page already turns into its "not a readable image" form error.
        using var background = new MemoryStream(SyntheticPackage.TinyPng());

        var e = Assert.ThrowsAsync<InvalidImageContentException>(
            () => CoverGenerator.LoadWithinBudgetAsync(background, maxPixels: 64 * 48 - 1));

        Assert.That(e, Is.InstanceOf<ImageFormatException>());
    }

    [Test]
    public async Task LoadWithinBudget_AtBudget_Decodes()
    {
        using var background = new MemoryStream(SyntheticPackage.TinyPng());

        using var image = await CoverGenerator.LoadWithinBudgetAsync(background, maxPixels: 64 * 48);

        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(64));
            Assert.That(image.Height, Is.EqualTo(48));
        });
    }

    private async Task assertJpegAsync(string key, int width, int height)
    {
        await using var stream = await store.OpenObjectReadAsync(key);
        Assert.That(stream, Is.Not.Null, key);

        using var image = await Image.LoadAsync(stream!);

        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(width), key);
            Assert.That(image.Height, Is.EqualTo(height), key);
            Assert.That(image.Metadata.DecodedImageFormat?.Name, Is.EqualTo("JPEG"), key);
        });
    }
}
