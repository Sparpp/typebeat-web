-- typebeat-web migration 002: website + lazer-style uploads (M3).
-- New columns only — no table renames, no data-destructive changes.

-- Touched by /me and website page loads (throttled by the caller).
ALTER TABLE users
    ADD COLUMN last_visit timestamptz;

ALTER TABLE beatmapsets
    ADD COLUMN title_unicode  text    NOT NULL DEFAULT '',
    ADD COLUMN artist_unicode text    NOT NULL DEFAULT '',
    ADD COLUMN description    text    NOT NULL DEFAULT '',
    ADD COLUMN bpm            numeric,
    ADD COLUMN download_count integer NOT NULL DEFAULT 0;

-- filename: the archive path of this difficulty's .osu inside the current version
-- (the missing beatmap-row -> version_files query path flagged in the M3 recon).
-- word/char/wpm: perfect-play typing-pace stats computed at upload from [Lyrics].
ALTER TABLE beatmaps
    ADD COLUMN filename   text,
    ADD COLUMN word_count integer,
    ADD COLUMN char_count integer,
    ADD COLUMN wpm        numeric;

ALTER TABLE favourites
    ADD COLUMN created_at timestamptz NOT NULL DEFAULT now();

-- Who submitted this version (collab-safe; distinct from beatmapsets.owner_id).
ALTER TABLE set_versions
    ADD COLUMN uploader_id bigint REFERENCES users (id);

-- Backfill the search tsvector (declared + GIN-indexed in 001 but never populated).
-- MUST stay in sync with the expression the upload write path uses (PackageIngest.SearchVectorSql):
-- title/unicode weighted A, artist/unicode B, creator username C, tags/source D.
-- 'simple' config: titles/artists are multilingual proper nouns — no English stemming.
UPDATE beatmapsets s
SET search = setweight(to_tsvector('simple', coalesce(s.title, '')), 'A')
          || setweight(to_tsvector('simple', coalesce(s.title_unicode, '')), 'A')
          || setweight(to_tsvector('simple', coalesce(s.artist, '')), 'B')
          || setweight(to_tsvector('simple', coalesce(s.artist_unicode, '')), 'B')
          || setweight(to_tsvector('simple', coalesce(u.username::text, '')), 'C')
          || setweight(to_tsvector('simple', coalesce(s.tags, '')), 'D')
          || setweight(to_tsvector('simple', coalesce(s.source, '')), 'D')
FROM users u
WHERE u.id = s.owner_id;
