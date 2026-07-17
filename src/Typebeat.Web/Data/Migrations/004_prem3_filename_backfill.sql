-- typebeat-web migration 004: backfill beatmaps.filename for pre-M3 sets.
--
-- Migration 002 added beatmaps.filename with no backfill, and the M3 codebase treats
-- "filename IS NOT NULL" as "this difficulty is live in the current version" (the cards'
-- stats rollup, the set page's stats box, APIv2 beatmapsets.beatmaps[]). Sets created before
-- the upload pipeline existed (the M1 seed tool wrote beatmapsets+beatmaps rows only) have no
-- set_versions rows, so their diffs stayed NULL forever: the live prod set rendered with
-- 0.00 stars / no WPM / "No difficulty data yet" and an empty API beatmaps list.
--
-- Scope guard: ONLY sets with no set_versions at all. On BSS-era sets a NULL filename means
-- "dropped from the current version" (rows are kept for the scores FK) and MUST stay NULL.
--
-- The synthetic name derives from version_name (path separators and reserved filename
-- characters become '_', mirroring MediaEndpoints.SanitizeFilename). These sets have no
-- package the name could point into — it only needs to be non-NULL, stable and recognizable.
-- Their set pages hide the Download button separately (no package ⇒ "available in-game only").
UPDATE beatmaps b
SET filename = regexp_replace(coalesce(nullif(trim(b.version_name), ''), 'beatmap'),
                              '[\\/:*?"<>|]', '_', 'g') || '.osu'
WHERE b.filename IS NULL
  AND NOT EXISTS (SELECT 1 FROM set_versions v WHERE v.set_id = b.set_id);
