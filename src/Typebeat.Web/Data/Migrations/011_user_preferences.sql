-- User-facing website preferences (Settings > Preferences). First one: show a beatmapset's
-- original (title_unicode/artist_unicode) text instead of the romanized title/artist wherever
-- the site renders a map's title/artist. Defaults to the existing (romanized) behaviour.
ALTER TABLE users ADD COLUMN prefer_original_metadata boolean NOT NULL DEFAULT false;
