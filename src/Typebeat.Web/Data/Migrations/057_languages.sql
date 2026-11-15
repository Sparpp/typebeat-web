-- A set may declare several song languages (backlog 472). The game's editor picks a SET of languages, the
-- .osu carries them on a [Metadata] Languages: line beside the old single Language: line, and the listing's
-- language filter (and the search box's language: operator) matches a set when ANY of its languages is the
-- one asked for.
--
-- languages holds the whole list, canonical names (Packages/BeatmapLanguages.cs) in the mapper's order,
-- normalised by BeatmapLanguages.NormalizeList: no duplicates, no '' entries, and 'instrumental' only ever
-- alone. '{}' (empty) is "not known yet", the list form of language = '' (019_language.sql).
--
-- language STAYS, as the set's PRIMARY language (BeatmapLanguages.Primary: the first entry that is not
-- 'english', else 'english'), and every writer keeps the two in step: language = Primary(languages), and
-- languages = '{}' exactly when language = ''. It stays because a single language is still what several
-- readers need (the ratings and the Polyglot support rules read it, as do the SEO block and the client's
-- song_language, which every client before the list reads), and because a single primary is what the old
-- Language: line carries, so an old client's upload still lands somewhere meaningful.
--
-- Backfilled in SQL from the single value, which is exact: before this migration every set had at most one
-- language, and that one IS its primary.
ALTER TABLE beatmapsets ADD COLUMN languages text[] NOT NULL DEFAULT '{}';

UPDATE beatmapsets SET languages = ARRAY[language] WHERE language <> '';

-- The listing filters on membership (@language = ANY(languages)), which a GIN index over the array serves
-- through the equivalent containment form (languages @> ARRAY[@language]). The search SQL writes the
-- containment form so the planner can use it.
CREATE INDEX idx_beatmapsets_languages ON beatmapsets USING gin (languages);
