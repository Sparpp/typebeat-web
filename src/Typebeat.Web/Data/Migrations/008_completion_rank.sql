-- Completion-based ranking: rank (X/S/A/B/C/D) is graded on "completion" — the fraction of the
-- map's typeable cells the player actually typed (any non-miss judgement) — instead of accuracy.
-- Accuracy stays stored and displayed; score and combo are untouched. Mirrors the client's
-- TypeBeatScoreProcessor and ScoringContract.RankFromCompletion — keep cutoffs in sync.
--
-- Adds scores.completion, backfills it from the stored statistics jsonb, and re-grades every
-- PASSED score under the new rule (failed scores keep rank 'F'). The key sets below mirror
-- ScoringContract's classifiers: "typed" = accuracy-affecting hits; "total" = all
-- accuracy-affecting counts in maximum_statistics (the map's cell count).

ALTER TABLE scores ADD COLUMN completion double precision NOT NULL DEFAULT 0;

WITH counts AS (
    SELECT s.id,
           COALESCE((SELECT sum(v.value::bigint)
                     FROM jsonb_each_text(s.statistics) v
                     WHERE v.key IN ('great', 'perfect', 'good', 'ok', 'meh',
                                     'small_tick_hit', 'large_tick_hit', 'slider_tail_hit')), 0) AS typed,
           COALESCE((SELECT sum(v.value::bigint)
                     FROM jsonb_each_text(s.maximum_statistics) v
                     WHERE v.key IN ('great', 'perfect', 'good', 'ok', 'meh', 'miss',
                                     'small_tick_hit', 'small_tick_miss',
                                     'large_tick_hit', 'large_tick_miss', 'slider_tail_hit')), 0) AS total
    FROM scores s
)
UPDATE scores s
SET completion = LEAST(1.0, GREATEST(0.0, counts.typed::double precision / counts.total))
FROM counts
WHERE counts.id = s.id AND counts.total > 0;

-- Re-grade passed scores on the new metric ('F' for fails is untouched). Bands mirror
-- RankFromCompletion: X at 100%, then S/A/B/C at 95/90/80/70.
UPDATE scores
SET rank = CASE
    WHEN completion >= 1.0  THEN 'X'
    WHEN completion >= 0.95 THEN 'S'
    WHEN completion >= 0.9  THEN 'A'
    WHEN completion >= 0.8  THEN 'B'
    WHEN completion >= 0.7  THEN 'C'
    ELSE 'D'
END
WHERE passed;
