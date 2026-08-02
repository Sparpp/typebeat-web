namespace Typebeat.Web.Pages.Users;

/// <summary>
/// The profile's stacked sections, as a list of stable slugs, plus the two rules that turn a
/// stored order (026_profile_order.sql) into a render order and a submitted order into a storable
/// one. This is the ONLY authority for which sections exist and what their default order is.
///
/// The slugs are the page's anchors too (<c>#pinned</c>, <c>#best-scores</c>, ...), so they are
/// part of the site's URL surface and must not be renamed casually: an old link and an old stored
/// array both point at them. <see cref="Default"/> is the order every profile renders in until its
/// owner drags something, and it must stay in step with the section blocks in Profile.cshtml,
/// which are keyed by these same constants.
///
/// Adding a section later means: a constant, an entry in <see cref="Default"/> where it belongs,
/// and a block in the view. Nothing has to be migrated, because of the two rules below.
/// </summary>
public static class ProfileSections
{
    public const string Pinned = "pinned";
    public const string BestScores = "best-scores";
    public const string FirstPlaces = "first-places";
    public const string RecentScores = "recent-scores";
    public const string MostPlayed = "most-played";
    public const string MostViewedReplays = "most-viewed-replays";
    public const string ReplayViews = "replay-views";
    public const string PlayHistory = "play-history";
    public const string Maps = "maps";
    public const string Favourites = "favourites";

    /// <summary>Every section, in the order a profile renders in until its owner reorders it.</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        Pinned,
        BestScores,
        FirstPlaces,
        RecentScores,
        MostPlayed,
        MostViewedReplays,
        ReplayViews,
        PlayHistory,
        Maps,
        Favourites,
    ];

    private static readonly HashSet<string> known = new(Default, StringComparer.Ordinal);

    public static bool IsKnown(string? id) => id is not null && known.Contains(id);

    /// <summary>
    /// The order to render in, from whatever is stored (null for a user who has never reordered).
    ///
    /// Two rules, and they are the whole forward-compatibility story:
    ///  - ids that are not <see cref="IsKnown"/> are IGNORED, so a section that gets removed later
    ///    does not break the arrays that name it;
    ///  - known ids MISSING from the stored array are appended in <see cref="Default"/> order, so a
    ///    section added later still appears for the users who reordered before it existed, rather
    ///    than vanishing from their profile.
    ///
    /// Duplicates keep their first position, which is the only reading that preserves an order.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? stored)
    {
        if (stored is null || stored.Count == 0)
            return Default;

        var order = new List<string>(Default.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string? id in stored)
        {
            if (IsKnown(id) && seen.Add(id!))
                order.Add(id!);
        }

        foreach (string id in Default)
        {
            if (seen.Add(id))
                order.Add(id);
        }

        return order;
    }

    /// <summary>
    /// Validates a submitted order for storage. Returns false when the submission is garbage and
    /// must be REFUSED (nothing is written); returns true with the array to store in
    /// <paramref name="order"/>, which is null when the submission means "back to the default".
    ///
    /// The client only ever submits the full list it just read out of the DOM, so every refusal
    /// here means a forged or corrupted request: an unknown id, nothing at all, or more entries
    /// than there are sections. Duplicates are the one thing that is repaired rather than refused
    /// (first position wins, exactly as <see cref="Resolve"/> reads them), since a duplicate
    /// carries an unambiguous intent and a browser quirk could in principle produce one.
    ///
    /// An order that comes out equal to <see cref="Default"/> stores null rather than a literal
    /// copy of the default: see 026_profile_order.sql for why that distinction matters.
    /// </summary>
    public static bool TrySanitize(IReadOnlyList<string?>? submitted, out string[]? order)
    {
        order = null;

        if (submitted is null || submitted.Count == 0 || submitted.Count > Default.Count)
            return false;

        var clean = new List<string>(submitted.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string? id in submitted)
        {
            if (!IsKnown(id))
                return false;

            if (seen.Add(id!))
                clean.Add(id!);
        }

        order = clean.SequenceEqual(Default) ? null : clean.ToArray();
        return true;
    }
}

/// <summary>
/// Model for the per-section reorder control (_SectionHandle.cshtml): whether to render it at all
/// (owner only), and the section's heading, which is what its buttons announce.
/// </summary>
public sealed record SectionHandle(bool Owner, string Label);
