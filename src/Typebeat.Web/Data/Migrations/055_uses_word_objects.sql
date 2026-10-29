-- Word objects (holds). The browser player has none, so it declines a
-- difficulty that carries any; ingest records which ones do. Every existing row is correctly false:
-- the game refused to submit such a map until this column existed.
ALTER TABLE beatmaps ADD COLUMN uses_word_objects boolean NOT NULL DEFAULT false;
