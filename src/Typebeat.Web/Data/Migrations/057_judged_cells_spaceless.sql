-- typebeat-web migration 057: the cells a SPACELESS play judges (backlog 470), per stream, beside the
-- Shorthand pair (056) and stamped by the same judged_cells_version. NULL means not counted: a
-- submission is then accepted unchecked. JudgedCells.VERSION 4 makes the backfill recount every row.
ALTER TABLE beatmaps
    ADD COLUMN judged_cells_spaceless integer,
    ADD COLUMN judged_cells_spaceless_literate integer;
