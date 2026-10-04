-- typebeat-web migration 046: the SCORE REWEIGHT (owner, 2026-10-04).
--
-- total_score's two halves were osu's 500000/500000: the first scaled by cumulative COMBO POSITION
-- (a single dropped key costs the whole rest of the run), the second by map PROGRESS at accuracy to
-- the fifth power. The owner moved the weight onto the accuracy half - 300000/700000 - so a score
-- leans on how well the player typed rather than how long their streak ran. A perfect play still
-- totals exactly 1000000.
--
-- WHY EVERY STORED ROW MOVES. A stored total is round(base x multiplier) where base is the two-term
-- sum above. The first term shrinks by 0.6 and the second grows by 1.4, so
--     new_base = 0.6 x old_base + 0.8 x (500000 x acc^5 x accuracyProgress)
-- (the cross-multiply is exact: 0.3/0.5 = 0.6 and 0.7/0.5 = 1.4, and 0.6 + 0.8 = 1.4 - 0.6 = 0.8).
-- Every OTHER mod factor in the stack cancels out of the ratio, exactly as 015's Half Time rescale
-- found, so the row is RE-BASED rather than recomputed from a replay.
--
-- WHY THIS IS A C# SWEEP AND NOT A PURE-SQL ONE. The second term needs accuracyProgress (judged
-- cells over the map's cells), which IS recoverable from the stored statistics - but the derived
-- mod multiplier the stored total was priced with, and the rounding, are computed by the same C#
-- the submit path uses (Scoring/ModMultiplier.cs). Doing it in C# keeps ONE definition of the
-- multiplier rather than a SQL copy that could drift, exactly as Scoring/SetRankClassicMark.cs
-- (043) chose for its own reprice. This file therefore only creates the audit table; the sweep
-- Scoring/ScoreReweightBackfill.cs fills it.
--
-- THIS TABLE IS THE AUDIT TRAIL. Nothing reads it: it exists so "which scores the 2026-10-04
-- reweight moved, and by how much" is answerable afterwards, and so a mistaken re-base can be found
-- and reversed (each row records the old and new total). Idempotence does NOT depend on it for
-- correctness of a single value - the transform is a pure function of the stored row - but the
-- candidate filter EXCLUDES recorded rows so a second boot does not re-apply the shrink (which WOULD
-- be wrong: this is not idempotent, since 0.6 x (0.6 x x) is not 0.6 x x). The primary key makes
-- the insert safe to repeat.
CREATE TABLE IF NOT EXISTS score_reweights
(
    score_id   bigint      PRIMARY KEY REFERENCES scores (id) ON DELETE CASCADE,
    old_total  bigint      NOT NULL,
    new_total  bigint      NOT NULL,
    reweighted_at timestamptz NOT NULL DEFAULT now()
);
