-- typebeat-web migration 040: typo-tolerant search (backlog 351).
--
-- The /beatmapsets free text now matches in layers (Search/FreeTextSearch.cs): whole lexemes,
-- lexeme prefixes, substrings, and last a trigram word_similarity against the title and the
-- artist, so a dropped or swapped letter ('dracla') still finds the map. That last layer needs
-- pg_trgm. It is a TRUSTED extension (Postgres 13 and up), so the database owner creates it
-- without superuser, exactly as 001 creates citext.
--
-- The two indexes are gin_trgm_ops over the two columns the typo layer reads. Postgres uses them
-- for the trigram operators (<%, %) and for ILIKE on those columns; the catalogue is small enough
-- that building them is instant.

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS ix_beatmapsets_title_trgm ON beatmapsets USING gin (title gin_trgm_ops);
CREATE INDEX IF NOT EXISTS ix_beatmapsets_artist_trgm ON beatmapsets USING gin (artist gin_trgm_ops);
