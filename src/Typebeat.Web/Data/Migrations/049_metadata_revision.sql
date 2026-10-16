-- Descriptive edits have their own revision: they never change package identity or listing age.
ALTER TABLE beatmapsets ADD COLUMN metadata_revision bigint NOT NULL DEFAULT 0;
