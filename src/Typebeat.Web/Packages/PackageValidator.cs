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

    /// <summary>
    /// Per-file cap for a bundled lyric font (backlog 291), mirroring the game editor's own
    /// bundling rule (TypeBeatSetupSection.MAX_BUNDLED_FONT_BYTES): the client refuses to bundle
    /// a font over 5 MiB, so a bigger one in a package never came from the editor.
    /// </summary>
    public const long MaxFontBytes = 5 * 1024 * 1024;

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

        // The bundled lyric font (backlog 291), on the game editor's own bundling rules
        // (TypeBeatSetupSection): single-face .ttf/.otf/.woff2 only, at most ONE per set, each
        // under MaxFontBytes. A .ttc/.otc collection never leaves the editor (one file carrying
        // several faces has no single family to register), so one here is hand-made and refused
        // outright rather than admitted as an ordinary set file.
        string? fontFilename = null;

        foreach (var file in package.Files)
        {
            string extension = Path.GetExtension(file.Filename).ToLowerInvariant();

            if (extension is ".ttc" or ".otc")
                throw new PackageValidationException($"\"{file.Filename}\" is a font collection (.ttc/.otc); bundle a single-face .ttf, .otf or .woff2 instead.");

            if (extension is not (".ttf" or ".otf" or ".woff2"))
                continue;

            if (file.Size > MaxFontBytes)
                throw new PackageValidationException($"Font file \"{file.Filename}\" exceeds the {MaxFontBytes / (1024 * 1024)} MiB font size limit.");

            if (fontFilename != null)
                throw new PackageValidationException($"The package contains more than one font file (\"{fontFilename}\" and \"{file.Filename}\"); a set may bundle at most one.");

            fontFilename = file.Filename;
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

            // THE ORIGINAL TEXT (backlog 330): a word the game's romaniser could not spell is stored
            // with no typed text until the mapper gives it one. The game's editor refuses to submit
            // such a draft; this is the same refusal for any client that does not.
            if (diff.UnromanisedWords.Count > 0)
            {
                throw new PackageValidationException(
                    $"\"{diff.Filename}\" has {diff.UnromanisedWords.Count} word(s) with no romanisation yet "
                    + $"({string.Join(", ", diff.UnromanisedWords.Distinct().Take(5))}); romanise them in the lyric editor before submitting.");
            }
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
