-- Account settings (M3.2): the profile fields the /settings page writes. avatar_key already
-- exists (001); this adds the profile description, the uploadable banner/cover, and a deletion
-- tombstone marker.
--
-- deleted_at: account deletion ANONYMIZES rather than hard-deletes (GDPR erasure via
-- anonymization). The row is kept so every foreign key into it stays valid; the user's
-- uploaded maps, their (now-anonymous) scores, and any moderation records they authored all
-- survive. The delete path scrubs every personal column, drops auth/session rows and personal
-- activity (tokens, favourites, download logs), renames to 'deleted_{id}', and stamps this.

ALTER TABLE users ADD COLUMN description text        NOT NULL DEFAULT '';
ALTER TABLE users ADD COLUMN cover_key   text;
ALTER TABLE users ADD COLUMN deleted_at  timestamptz;
