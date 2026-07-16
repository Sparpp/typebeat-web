namespace Typebeat.Web.Packages;

/// <summary>
/// Semantic upload invariants (all 422-able via <see cref="PackageValidationException"/>), mirroring
/// osu-server-beatmap-submission's rules per the M3 recon (result.bss.metadata_rules): every diff
/// decodes (parser-side), cross-diff metadata consistency, creator == uploader, diff count bounds,
/// embedded ids match the target set + allocated beatmap ids, and at least one audio file.
/// </summary>
public static class PackageValidator
{
    public const int MaxDifficulties = 128;

    /// <summary>~95 MB: Cloudflare-proxied request bodies cap at 100 MB; leave multipart headroom.</summary>
    public const long MaxPackageBytes = 95L * 1024 * 1024;

    /// <param name="package">The parsed upload.</param>
    /// <param name="targetSetId">The set being submitted to (route id).</param>
    /// <param name="allocatedBeatmapIds">Beatmap ids the server has allocated for this set.</param>
    /// <param name="uploaderUsername">Current username of the authenticated uploader.</param>
    public static void Validate(
        ParsedPackage package,
        long targetSetId,
        IReadOnlyCollection<long> allocatedBeatmapIds,
        string uploaderUsername)
    {
        if (package.PackageSize > MaxPackageBytes)
            throw new PackageValidationException($"The package exceeds the {MaxPackageBytes / (1024 * 1024)} MB size limit.");

        int diffCount = package.Difficulties.Count;

        if (diffCount == 0)
            throw new PackageValidationException("The package contains no difficulty (.osu) files.");

        if (diffCount > MaxDifficulties)
            throw new PackageValidationException($"The package contains {diffCount} difficulties; the maximum is {MaxDifficulties}.");

        // Duplicate archive paths would collide in the version manifest (and on case-insensitive
        // filesystems); the game's own file stores are case-insensitive, so compare that way.
        var seenFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in package.Files)
        {
            if (!seenFilenames.Add(file.Filename))
                throw new PackageValidationException($"The package contains \"{file.Filename}\" more than once.");
        }

        // Cross-difficulty metadata must be a single value (recon: BeatmapPackageParser
        // getSingleValueFrom throws for artist/title/source/tags/creator mismatches).
        requireSingle(package, d => d.Title, "Title");
        requireSingle(package, d => d.Artist, "Artist");
        requireSingle(package, d => d.Source, "Source");
        requireSingle(package, d => d.Tags, "Tags");
        requireSingle(package, d => d.Creator, "Creator");

        // Creator is force-set to the uploader; a package authored under another name is rejected
        // (case-insensitive: users.username is citext).
        string creator = package.Difficulties[0].Creator;

        if (!string.Equals(creator, uploaderUsername, StringComparison.OrdinalIgnoreCase))
            throw new PackageValidationException($"Beatmap creator \"{creator}\" does not match the uploader \"{uploaderUsername}\".");

        var usedBeatmapIds = new HashSet<long>();

        foreach (var diff in package.Difficulties)
        {
            if (diff.BeatmapSetId != targetSetId)
            {
                throw new PackageValidationException(
                    $"\"{diff.Filename}\" declares BeatmapSetID {diff.BeatmapSetId?.ToString() ?? "(none)"} but is being submitted to set {targetSetId}.");
            }

            if (diff.BeatmapId is not long beatmapId || !allocatedBeatmapIds.Contains(beatmapId))
            {
                throw new PackageValidationException(
                    $"\"{diff.Filename}\" declares BeatmapID {diff.BeatmapId?.ToString() ?? "(none)"}, which was not allocated for this set.");
            }

            if (!usedBeatmapIds.Add(beatmapId))
                throw new PackageValidationException($"BeatmapID {beatmapId} is declared by more than one difficulty.");

            if (string.IsNullOrEmpty(diff.AudioFilename))
                throw new PackageValidationException($"\"{diff.Filename}\" declares no AudioFilename.");

            if (!seenFilenames.Contains(BeatmapPackageParser.NormalizeFilename(diff.AudioFilename)))
                throw new PackageValidationException($"Audio file \"{diff.AudioFilename}\" referenced by \"{diff.Filename}\" is missing from the package.");
        }
    }

    private static void requireSingle(ParsedPackage package, Func<ParsedDifficulty, string> selector, string what)
    {
        string expected = selector(package.Difficulties[0]);

        foreach (var diff in package.Difficulties)
        {
            if (!string.Equals(selector(diff), expected, StringComparison.Ordinal))
                throw new PackageValidationException($"{what} differs between difficulties; it must be identical across the whole set.");
        }
    }
}
