-- typebeat-web migration 056: the cells a SHORTHAND play judges, per stream, beside judged_cells /
-- judged_cells_literate (048) and stamped by the same judged_cells_version. NULL means not counted:
-- a submission is then accepted unchecked. JudgedCells.VERSION 2 makes the backfill recount every row.
ALTER TABLE beatmaps
    ADD COLUMN judged_cells_shorthand integer,
    ADD COLUMN judged_cells_shorthand_literate integer;
