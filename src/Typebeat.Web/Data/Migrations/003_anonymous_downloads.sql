-- typebeat-web migration 003: anonymous download logging (M3).
-- The download endpoint serves signed-out visitors too (spec iron rule 9: anonymous allowed),
-- and every download logs a beatmapset_downloads row; NULL user_id = anonymous.
ALTER TABLE beatmapset_downloads
    ALTER COLUMN user_id DROP NOT NULL;
