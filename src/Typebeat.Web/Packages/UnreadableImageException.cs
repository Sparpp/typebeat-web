namespace Typebeat.Web.Packages;

/// <summary>
/// An uploaded image that is not turned into a cover, avatar or banner: not an image at all, an
/// unsupported format, corrupt bytes, or a header declaring more pixels than
/// <see cref="CoverGenerator.MaxSourcePixels"/>. Thrown by
/// <see cref="CoverGenerator.LoadWithinBudgetAsync"/>, so every caller handles all four the same
/// way (the settings page shows its "not a readable image" form error, the ingest records the
/// cover as failed).
/// </summary>
public sealed class UnreadableImageException(string message) : Exception(message);
