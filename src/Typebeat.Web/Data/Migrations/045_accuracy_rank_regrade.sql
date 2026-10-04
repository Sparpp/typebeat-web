-- typebeat-web migration 045: RE-GRADE every stored score under the accuracy rule (PR 17).
--
-- Grades moved from completion (the fraction of cells typed) to timing accuracy with a missed-cell
-- condition on SS (no cell missed at all) and S (under 3% of the map's cells missed). The client
-- derives the DISPLAYED grade from the stored statistics under the current rule
-- (TypeBeatScoreProcessor.RankFromStatistics), so an opened results screen and the selector's rows
-- already read correctly without this; this migration rewrites the STORED `scores.rank` column too,
-- so the leaderboard's grade column and its S/A filters agree with what the client shows.
--
-- Rank is a stored, denormalized column graded at submit by ScoringContract (which this change also
-- moves to the accuracy rule), so a rule change leaves every existing row on the old ladder until it
-- is rewritten. Same shape as 008_completion_rank.sql, which re-graded the catalogue when the rule
-- moved to completion in the first place; this is the move back, on the new terms.
--
-- WHAT IS AND IS NOT TOUCHED
--   * scores.rank, for PASSED rows only. A failed row keeps its 'F' (ScoringContract grades a fail
--     F at submit and this mirrors that).
--   * NOT touched: accuracy, completion (still stored, still shown), total_score, pp, ranked,
--     passed. The pp that goes with the new grade is re-priced by PpBackfill off the
--     PerformancePoints.VERSION bump, which is a separate movement with its own stamp.
--
-- HOW THE NEW GRADE IS DERIVED, AND WHY IT MATCHES THE SERVER
-- accuracy is stored directly. The missed fraction is the misses over judged cells, from the stored
-- statistics jsonb: per PerformancePoints.CountNotes, the note set is great/ok/meh/perfect plus the
-- two miss keys `miss` and `good` (THE UNCORRECTED TYPO IS A MISS, backlog 213) - so judged =
-- great+ok+meh+perfect+miss+good and missed = miss+good. Both sums are over the keys that are
-- PRESENT, which is what the C# does (a key absent contributes nothing).
--
-- The bands below mirror TypeBeatScoreProcessor.ACCURACY_CUTOFF_* and S_MISS_LIMIT / RankFromAccuracy
-- EXACTLY: X at 0.98 with no miss, S at 0.92 with under 3% missed, then A/B/C at 0.85/0.75/0.60.
-- Keep them in sync if the ladder moves; Scoring/LyricWpmCurve has no part here.
--
-- IDEMPOTENT BY CONSTRUCTION: the statement is a pure function of (accuracy, statistics), so
-- re-running it writes the same value. No audit table is needed (contrast 015, whose rescale was
-- multiplicative and so was not idempotent and needed one).
--
-- No SQL can size the before/after here; run this query first and read the counts before deploying:
--   SELECT rank, count(*) FROM scores WHERE passed GROUP BY rank ORDER BY 1;

WITH counts AS (
    SELECT s.id,
           s.accuracy AS accuracy,
           COALESCE((SELECT sum(v.value::bigint)
                     FROM jsonb_each_text(s.statistics) v
                     WHERE v.key IN ('great', 'ok', 'meh', 'perfect')), 0) AS judged_hits,
           COALESCE((SELECT sum(v.value::bigint)
                     FROM jsonb_each_text(s.statistics) v
                     WHERE v.key IN ('miss', 'good')), 0) AS missed
    FROM scores s
    WHERE s.passed
)
UPDATE scores s
SET rank = CASE
    WHEN counts.accuracy >= 0.98 AND counts.missed = 0 THEN 'X'
    WHEN counts.accuracy >= 0.92 AND counts.missed < 0.03 * (counts.judged_hits + counts.missed) THEN 'S'
    WHEN counts.accuracy >= 0.85 THEN 'A'
    WHEN counts.accuracy >= 0.75 THEN 'B'
    WHEN counts.accuracy >= 0.60 THEN 'C'
    ELSE 'D'
END
FROM counts
WHERE counts.id = s.id AND s.passed;
