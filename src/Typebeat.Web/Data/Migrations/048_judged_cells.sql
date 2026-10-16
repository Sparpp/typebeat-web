-- typebeat-web migration 048: how many cells of each difficulty the game JUDGES, per stream.
--
-- A submission's maximum_statistics used to come only from the client and was never held against
-- the map, so ten Greats against a claimed ten-cell map recomputed as a full, valid X on a map of a
-- thousand cells. These columns are the server's own answer: the count of nested scoring objects
-- the game's converter builds for the map, one Great each, which is what an honest submission's
-- maximum_statistics adds up to (Packages/Lyrics/JudgedCells.cs, pinned against the game's
-- converter in WireCompat's JudgedCellCountParityTest). Both submit routes refuse a claim that
-- disagrees (Scoring/ScoreSubmission.MaximumMatchesMap).
--
-- NOT char_count, which is a pace figure: it leaves out freestyle slots and knows only the default
-- stream (031_freestyle_cell_count.sql). The Literate mod judges every supported mark as a cell
-- too, so it gets a column of its own.
--
-- NULL, no DEFAULT: NULL means "not counted yet", and a submission on such a row is accepted
-- unchecked, exactly as before this migration. Ingest always writes both, and
-- Packages/JudgedCellBackfill.cs fills every live difficulty at startup from its stored .osu.
-- judged_cells_version stamps the JudgedCells.VERSION the pair was counted under; a row stamped
-- with any other version is read as unknown, so a rule change can never refuse a play against a
-- count the current rules did not produce.
ALTER TABLE beatmaps
    ADD COLUMN judged_cells integer,
    ADD COLUMN judged_cells_literate integer,
    ADD COLUMN judged_cells_version smallint;
