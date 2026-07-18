-- Boundary-window pace rework (LyricPace v2): a line's typing window is now
-- EndTime - StartTime (the boundary-to-boundary time the player actually gets), and
-- WPM/CPM use real word/cell counts instead of the "1 word = 5 chars" estimate, averaged
-- per line. Stored wpm / difficulty_rating / word_count / char_count values written under
-- v1 arithmetic cannot be recomputed in SQL (they derive from the .osu lyric data), so this
-- migration only adds the version stamp; PaceBackfill reparses stored blobs at startup and
-- upgrades any row still below LyricPace.VERSION.
ALTER TABLE beatmaps ADD COLUMN pace_version int NOT NULL DEFAULT 1;
