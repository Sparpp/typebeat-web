using Dapper;
using Npgsql;

namespace Typebeat.Web.Pages.Settings;

/// <summary>The account settings page's SQL (<see cref="IndexModel"/>), one method per statement it runs.</summary>
public static class SettingsQueries
{
    /// <summary>What the page shows of the account (<see cref="IndexModel"/>'s GET and every failed POST).</summary>
    public static Task<(string Description, string? AvatarKey, string? CoverKey, bool PreferOriginalMetadata, bool HasPassword,
        string CountryCode, bool CountryChosen)> CurrentAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleOrDefaultAsync<(string Description, string? AvatarKey, string? CoverKey, bool PreferOriginalMetadata, bool HasPassword,
            string CountryCode, bool CountryChosen)>(
            """
            SELECT description AS Description, avatar_key AS AvatarKey, cover_key AS CoverKey,
                   prefer_original_metadata AS PreferOriginalMetadata,
                   COALESCE(password_hash, '') <> '' AS HasPassword,
                   country_code::text AS CountryCode, country_chosen AS CountryChosen
            FROM users WHERE id = @id
            """,
            new { id });

    public static Task UpdateDescriptionAsync(NpgsqlConnection conn, long id, string description)
        => conn.ExecuteAsync(
            "UPDATE users SET description = @description WHERE id = @id",
            new { description, id });

    /// <summary>
    /// country_chosen is set on EVERY save, "No country" included: that is the whole point of the
    /// flag, a deliberate XX must never be re-detected by CountryBackfill.
    /// </summary>
    public static Task UpdateCountryAsync(NpgsqlConnection conn, long id, string code)
        => conn.ExecuteAsync(
            "UPDATE users SET country_code = @code, country_chosen = true WHERE id = @id",
            new { code, id });

    public static Task UpdatePreferencesAsync(NpgsqlConnection conn, long id, bool pref)
        => conn.ExecuteAsync(
            "UPDATE users SET prefer_original_metadata = @pref WHERE id = @id",
            new { pref, id });

    /// <summary>The stored password hash (empty or null: none) and the address a set-password code goes to.</summary>
    public static Task<(string? Hash, string Address)> PasswordAccountAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleAsync<(string? Hash, string Address)>(
            "SELECT password_hash AS Hash, email::text AS Address FROM users WHERE id = @id", new { id });

    /// <summary>
    /// Only ever a FIRST password: the WHERE makes this a no-op on an account that has one, even if
    /// a second tab set it after the page was rendered. Returns the rows changed.
    /// </summary>
    public static Task<int> SetFirstPasswordAsync(NpgsqlConnection conn, long id, string hash)
        => conn.ExecuteAsync(
            "UPDATE users SET password_hash = @hash WHERE id = @id AND COALESCE(password_hash, '') = ''",
            new { hash, id });

    /// <summary>Points the avatar (or banner) at <paramref name="key"/> and returns the key it replaced, if any.</summary>
    public static async Task<string?> ReplaceMediaKeyAsync(NpgsqlConnection conn, long id, bool isBanner, string key)
    {
        // The column name is a fixed internal literal, never user input; safe to interpolate.
        string column = isBanner ? "cover_key" : "avatar_key";

        string? previousKey = await conn.ExecuteScalarAsync<string?>(
            $"SELECT {column} FROM users WHERE id = @id", new { id });

        await conn.ExecuteAsync(
            $"UPDATE users SET {column} = @key WHERE id = @id", new { key, id });

        return previousKey;
    }

    public static Task<(string? AvatarKey, string? CoverKey)> MediaKeysAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleOrDefaultAsync<(string? AvatarKey, string? CoverKey)>(
            "SELECT avatar_key AS AvatarKey, cover_key AS CoverKey FROM users WHERE id = @id",
            new { id });

    /// <summary>Runs <see cref="anonymize_sql"/> inside the caller's transaction.</summary>
    public static Task AnonymizeAsync(NpgsqlConnection conn, long id, NpgsqlTransaction tx)
        => conn.ExecuteAsync(anonymize_sql, new { id }, tx);

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
        -- Which Google account this person signed in with is personal data (035_google_sign_in.sql),
        -- and dropping the link also frees that Google account to sign up afresh.
        DELETE FROM user_external_logins  WHERE user_id = @id;

        UPDATE reports SET reporter_id = NULL WHERE reporter_id = @id;

        UPDATE users SET
            username      = 'deleted_' || id::text,
            email         = 'deleted_' || id::text || '@deleted.invalid',
            password_hash = '',
            country_code  = 'XX',
            -- Chosen, so CountryBackfill can never re-detect a country for an erased account (it
            -- has no sessions left to do it from, but the guard should not depend on that).
            country_chosen = true,
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
