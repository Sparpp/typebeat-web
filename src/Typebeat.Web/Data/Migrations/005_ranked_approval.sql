-- Ranked-by-approval: uploads land as 'pending' (browsable, downloadable, NO leaderboards);
-- a user with the map_reviewer role (or an admin) promotes them to 'ranked' from the website.
-- 'public', the pre-approval catch-all state, splits into those two meanings; existing rows
-- were all live with leaderboards, so they grandfather as 'ranked'.

ALTER TABLE users ADD COLUMN map_reviewer boolean NOT NULL DEFAULT false;

ALTER TABLE beatmapsets DROP CONSTRAINT beatmapsets_status_check;

UPDATE beatmapsets SET status = 'ranked' WHERE status = 'public';

ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_status_check
    CHECK (status IN ('hidden', 'pending', 'ranked', 'removed'));

-- New sets are invisible shells until their first successful upload publishes them (as
-- 'pending' from now on); the old default of 'public' predates the BSS create flow.
ALTER TABLE beatmapsets ALTER COLUMN status SET DEFAULT 'hidden';
