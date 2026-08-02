using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SixLabors.ImageSharp;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages.Settings;

/// <summary>
/// Account settings (/settings), for the signed-in user only (anonymous → /login). Four
/// self-contained POST handlers, each re-rendering this page on error and using POST-redirect-GET
/// on success:
///  - Profile: the plain-text description (bio).
///  - Avatar / Banner: an uploaded image, resized to a fixed JPEG by <see cref="ProfileMedia"/>
///    and stored under a version-stamped key; the superseded object is deleted so blobs don't leak.
///  - Delete: GDPR erasure. ANONYMIZES rather than hard-deletes: scrubs every personal column,
///    drops the user's auth/session rows and personal activity (tokens, favourites, score pins,
///    download logs), and renames to 'deleted_{id}'. The row is KEPT so the maps they uploaded and the
///    (now-anonymous) scores/moderation records referencing them stay valid. Requires typing the
///    exact username to confirm, then signs the browser out.
/// </summary>
public sealed class IndexModel(Db db, IFileStore store, TokenService tokens) : TypebeatPageModel
{
    public const int MaxDescriptionLength = 2000;

    // Kestrel's global cap is higher; this is the friendly per-upload limit. An avatar/banner is
    // a few hundred KB after re-encode, so 8 MB of source is generous.
    private const long max_image_bytes = 8L * 1024 * 1024;

    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? ConfirmUsername { get; set; }

    /// <summary>Preferences &gt; show a map's original (non-romanized) title/artist instead of romanized.</summary>
    [BindProperty] public bool PreferOriginalMetadata { get; set; }

    /// <summary>Non-null after a failed POST; shown as an inline error.</summary>
    public string? Error { get; private set; }

    /// <summary>Set from the ?saved= redirect after a successful POST (PRG).</summary>
    public string? Notice { get; private set; }

    /// <summary>Current media URLs (version-stamped keys served by MediaEndpoints), or null.</summary>
    public string? AvatarUrl { get; private set; }
    public string? BannerUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? saved = null)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        Notice = saved switch
        {
            "profile" => "Profile saved.",
            "avatar" => "Avatar updated.",
            "banner" => "Banner updated.",
            "preferences" => "Preferences saved.",
            _ => null,
        };

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
        await loadCurrentAsync(conn, CurrentUser.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        if (CurrentUser is null)
            return Redirect("/login");

        string description = (Description ?? string.Empty).Trim();

        if (description.Length > MaxDescriptionLength)
            return await failAsync(CurrentUser.Id, $"Description is too long (max {MaxDescriptionLength} characters).");

        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            await conn.ExecuteAsync(
                "UPDATE users SET description = @description WHERE id = @id",
                new { description, id = CurrentUser.Id });
        }

        return RedirectToPage(new { saved = "profile" });
    }

    public async Task<IActionResult> OnPostPreferencesAsync()
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            await conn.ExecuteAsync(
                "UPDATE users SET prefer_original_metadata = @pref WHERE id = @id",
                new { pref = PreferOriginalMetadata, id = CurrentUser.Id });
        }

        return RedirectToPage(new { saved = "preferences" });
    }

    public Task<IActionResult> OnPostAvatarAsync(IFormFile? avatar) => handleImageUploadAsync(avatar, isBanner: false);

    public Task<IActionResult> OnPostBannerAsync(IFormFile? banner) => handleImageUploadAsync(banner, isBanner: true);

    private async Task<IActionResult> handleImageUploadAsync(IFormFile? file, bool isBanner)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        long id = CurrentUser.Id;
        string what = isBanner ? "banner" : "avatar";

        if (file is null || file.Length == 0)
            return await failAsync(id, $"Choose an image to use as your {what}.");

        if (file.Length > max_image_bytes)
            return await failAsync(id, $"That {what} is too large (max {max_image_bytes / (1024 * 1024)} MB).");

        // A version stamp makes the stored key (and its URL) unique per upload, so replacements
        // are cache-safe. Milliseconds avoids a collision on rapid re-uploads.
        long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string key;
        try
        {
            await using var upload = file.OpenReadStream();
            key = isBanner
                ? await ProfileMedia.WriteBannerAsync(upload, id, version, store, HttpContext.RequestAborted)
                : await ProfileMedia.WriteAvatarAsync(upload, id, version, store, HttpContext.RequestAborted);
        }
        catch (ImageFormatException)
        {
            // Covers UnknownImageFormatException (not an image) and InvalidImageContentException
            // (declared format, corrupt bytes); both derive from ImageFormatException.
            return await failAsync(id, "That file isn't a readable image. Try a PNG or JPEG.");
        }

        // The column name is a fixed internal literal, never user input; safe to interpolate.
        string column = isBanner ? "cover_key" : "avatar_key";
        string? previousKey;

        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            previousKey = await conn.ExecuteScalarAsync<string?>(
                $"SELECT {column} FROM users WHERE id = @id", new { id });

            await conn.ExecuteAsync(
                $"UPDATE users SET {column} = @key WHERE id = @id", new { key, id });
        }

        // Remove the superseded blob (best-effort). Keys are unique per upload, so this is never
        // the object we just wrote.
        if (previousKey is not null && previousKey != key)
            await store.DeleteObjectAsync(previousKey, HttpContext.RequestAborted);

        return RedirectToPage(new { saved = what });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (CurrentUser is null)
            return Redirect("/login");

        long id = CurrentUser.Id;

        if (!string.Equals(ConfirmUsername?.Trim(), CurrentUser.Username, StringComparison.OrdinalIgnoreCase))
            return await failAsync(id, "Type your username exactly to confirm account deletion.");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Grab the stored image keys first so their blobs can be removed after the row is scrubbed.
        var media = await conn.QuerySingleOrDefaultAsync<(string? AvatarKey, string? CoverKey)>(
            "SELECT avatar_key AS AvatarKey, cover_key AS CoverKey FROM users WHERE id = @id",
            new { id });

        await using (var tx = await conn.BeginTransactionAsync(HttpContext.RequestAborted))
        {
            await conn.ExecuteAsync(anonymize_sql, new { id }, tx);
            await tx.CommitAsync(HttpContext.RequestAborted);
        }

        // The person's uploaded images are personal data; remove the blobs too. Best-effort: the
        // account is already anonymized (committed above), so a storage hiccup must not fail the
        // request or leave the browser session un-cleared. Orphaned blobs can be swept later.
        try
        {
            if (media.AvatarKey is not null)
                await store.DeleteObjectAsync(media.AvatarKey, HttpContext.RequestAborted);
            if (media.CoverKey is not null)
                await store.DeleteObjectAsync(media.CoverKey, HttpContext.RequestAborted);
        }
        catch
        {
            // Ignore: the erasure of personal DATA already happened in the committed transaction.
        }

        // Drop the session cookie (its token row is already gone, so the revoke is a no-op).
        await SessionCookieAuth.SignOutAsync(HttpContext, tokens);

        return Redirect("/?deleted=1");
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<IActionResult> failAsync(long id, string error)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
        await loadCurrentAsync(conn, id);
        Error = error;
        return Page();
    }

    private async Task loadCurrentAsync(NpgsqlConnection conn, long id)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string Description, string? AvatarKey, string? CoverKey, bool PreferOriginalMetadata)>(
            "SELECT description AS Description, avatar_key AS AvatarKey, cover_key AS CoverKey, prefer_original_metadata AS PreferOriginalMetadata FROM users WHERE id = @id",
            new { id });

        // Preserve a rejected edit (Description already bound) but fill the rest from the row.
        Description ??= row.Description;
        AvatarUrl = row.AvatarKey is null ? null : $"/{row.AvatarKey}";
        BannerUrl = row.CoverKey is null ? null : $"/{row.CoverKey}";
        PreferOriginalMetadata = row.PreferOriginalMetadata;
    }

    /// <summary>
    /// Anonymizing account deletion. Multi-statement, run inside one transaction. Deletes the
    /// user's auth/session rows and personal activity; keeps their maps, scores and any
    /// moderation records (the row itself survives, so those FKs stay valid); scrubs every
    /// personal column and stamps <c>deleted_at</c>.
    /// </summary>
    private const string anonymize_sql =
        """
        UPDATE beatmapsets SET favourite_count = GREATEST(favourite_count - 1, 0)
         WHERE id IN (SELECT set_id FROM favourites WHERE user_id = @id);

        DELETE FROM favourites            WHERE user_id = @id;
        -- Pins are personal curation, not score data: the scores themselves survive erasure
        -- (anonymized), but the profile section they were arranged for does not.
        DELETE FROM score_pins            WHERE user_id = @id;
        DELETE FROM beatmapset_downloads  WHERE user_id = @id;
        -- Which replays this person WATCHED is personal activity, like their downloads, so the
        -- ledger rows go. The view counters they contributed to belong to the players who were
        -- watched (scores.replay_views, user_month_replay_views) and are deliberately left alone:
        -- somebody else's profile must not lose numbers because a viewer erased their account.
        DELETE FROM replay_views          WHERE viewer_id = @id;
        DELETE FROM score_tokens          WHERE user_id = @id;
        DELETE FROM email_tokens          WHERE user_id = @id;
        DELETE FROM oauth_tokens          WHERE user_id = @id;

        UPDATE reports SET reporter_id = NULL WHERE reporter_id = @id;

        UPDATE users SET
            username      = 'deleted_' || id::text,
            email         = 'deleted_' || id::text || '@deleted.invalid',
            password_hash = '',
            country_code  = 'XX',
            avatar_key    = NULL,
            cover_key     = NULL,
            description   = '',
            -- The section order (026_profile_order.sql) is personal curation, same reasoning as
            -- the pins above: an erased account's profile goes back to the default layout.
            profile_order = NULL,
            is_admin      = false,
            map_reviewer  = false,
            verified_at   = NULL,
            deleted_at    = now()
        WHERE id = @id;
        """;
}
