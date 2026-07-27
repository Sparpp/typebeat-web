-- Half Time nerf (task 44): the shared rate curve's DECREASE_SLOPE goes 1.80 -> 3.00, so every
-- down-rate play is now worth strictly less than it was. Default Half Time (0.75x) pays 0.25
-- instead of 0.55; the 0.10 floor is reached at 0.70x instead of 0.50x, so every rate in
-- [0.50, 0.70] pays 0.10; 0.80x pays 0.40, 0.90x pays 0.70, 0.99x pays 0.97. The INCREASE side
-- (DT/NC slope 0.46) is untouched, so no DT/NC score moves. See Scoring/RateMultiplier.cs, which
-- is the byte-for-byte mirror of the game's TypeBeatRateMultiplier.
--
-- Scores already in the table were computed by the client as round(base x old_multiplier) and
-- stored verbatim, so a stored Half Time total is priced on the OLD curve and would sit above
-- every post-nerf Half Time play on the same board forever. This migration re-bases them.
--
-- WHAT IS AND IS NOT TOUCHED
--   * scores.total_score, for rows whose mods carry a rate strictly below 1.00 (in practice HT;
--     DT/NC are clamped to [1.01, 2.00] at submit so they can never qualify, but the rule below is
--     written from the rate, not from the acronym). Every other row is byte-identical afterwards.
--   * user_stats.total_score, the one DENORMALIZED aggregate that embeds score totals (the
--     profile "total score" and the rankings page's cumulative column). Every other scoring
--     surface (leaderboards, GlobalRanking.PerUserCumulativeSql, the set page, the profile's best
--     scores) is a live query over scores.total_score and needs nothing.
--   * NOT touched: rank, completion, accuracy, max_combo, ranked, passed. Ranks are graded on
--     completion percent (X/S/A/B/C at 100/95/90/80/70), which has nothing to do with the
--     multiplier, so a re-based score keeps its grade.
--   * NOT touched: Wind Up / Wind Down rows. Their multiplier is paid on a rate RAMP whose
--     endpoints are not persisted, so the old total is not reconstructible; they are unranked at
--     every configuration and reach no board.
--
-- HOW THE NEW TOTAL IS DERIVED, AND WHY IT IS EXACT
-- The pre-multiplier base (SoloScoreInfo.total_score_without_mods) is NOT a column: the server
-- bounds it at submit and then discards it. It is also not recoverable from the stored total,
-- because round(base x 0.55) maps a run of ~1.8 consecutive integer bases onto one stored value.
-- So the row is RESCALED rather than recomputed:
--
--     new_total = round_half_away(old_total x new_multiplier / old_multiplier)
--
-- Both multipliers are exact 4-decimal decimals of the stored 2-decimal rate, so they are held
-- here as integer basis points (multiplier x 10000) and the whole expression is evaluated in
-- BIGINT arithmetic:
--
--     new_total = (2 x old_total x new_bp + old_bp) / (2 x old_bp)          -- integer division
--
-- which is floor(old_total x new_bp / old_bp + 1/2), i.e. exactly half-away-from-zero rounding of
-- the true rational value for a non-negative total. No double precision anywhere, no numeric
-- division-scale rounding, so the result is bit-reproducible and cannot drift by a point the way a
-- float rescale can. Every other mod in the stack (FL, LT, NF, FT, DT) cancels out of the ratio,
-- so the whole stack does not need to be re-priced, only its down-rate factor. (The client's own
-- Math.Round is half-to-EVEN; half-away is used here because it is what Postgres rounds natively
-- and what RateMultiplier already uses. The two disagree only on an exact .5, which costs at most
-- the same single point the paragraph below already accounts for.)
--
-- The one thing this cannot do is recover the exact value a NEW client would have submitted for
-- the same play, round(base x new_multiplier): the original rounding error (at most half a point,
-- scaled by new/old) survives the rescale, so a re-based total can sit one point off what a replay
-- of the identical performance would score today. That is the information-theoretic best available
-- from the persisted data (the recoverable base interval is wider than one integer), it is applied
-- uniformly, and rounding is monotonic so relative order on every board is preserved.
--
-- IDEMPOTENCY
-- Every row this migration considers is recorded in score_rescales, keyed (migration, score_id),
-- and the candidate set excludes anything already recorded. Re-running the file (by hand, or on a
-- database where schema_migrations was lost) therefore selects nothing and changes nothing: no
-- score is shrunk twice and no user_stats delta is applied twice. The table doubles as the audit
-- trail for the re-base.

CREATE TABLE IF NOT EXISTS score_rescales
(
    -- Which migration re-based the row; a future curve change gets its own key and its own guard.
    migration   text        NOT NULL,
    score_id    bigint      NOT NULL REFERENCES scores (id) ON DELETE CASCADE,
    old_total   bigint      NOT NULL,
    new_total   bigint      NOT NULL,
    rescaled_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (migration, score_id)
);

WITH
-- One row per (score, rate mod) for every mod entry carrying a rate. The rate is read exactly as
-- RateMods.ReadSpeedChange does it: snapped to the slider's 0.01 precision, then clamped into that
-- mod's range. A rate mod with no stored speed_change reads as the client default, which is what
-- RateMods.DefaultSpeed assumes and the only rate a pre-task-27 client could have omitted it at.
mod_rate AS (
    SELECT s.id                                          AS score_id,
           s.user_id                                     AS user_id,
           s.total_score                                 AS old_total,
           CASE entry.acronym
               WHEN 'HT' THEN least(0.99, greatest(0.50, round(coalesce(entry.raw_rate, 0.75), 2)))
               WHEN 'DT' THEN least(2.00, greatest(1.01, round(coalesce(entry.raw_rate, 1.50), 2)))
               WHEN 'NC' THEN least(2.00, greatest(1.01, round(coalesce(entry.raw_rate, 1.50), 2)))
           END                                           AS rate
    FROM scores s
    CROSS JOIN LATERAL (
        SELECT upper(btrim(e.value ->> 'acronym')) AS acronym,
               CASE
                   WHEN jsonb_typeof(e.value -> 'settings' -> 'speed_change') = 'number'
                       THEN (e.value -> 'settings' ->> 'speed_change')::numeric
               END                                 AS raw_rate
        -- A non-array mods blob describes no mod stack; treat it as no mods rather than erroring.
        FROM jsonb_array_elements(
                 CASE WHEN jsonb_typeof(s.mods) = 'array' THEN s.mods ELSE '[]'::jsonb END) AS e(value)
    ) entry
    WHERE entry.acronym IN ('HT', 'DT', 'NC')
      AND NOT EXISTS (SELECT 1
                      FROM score_rescales r
                      WHERE r.migration = '015_ht_nerf_rescore' AND r.score_id = s.id)
),
-- Down-rates only. A stack cannot really hold two of one rate mod (the client keys mods by type),
-- but if a tampered row does, collapse to the DEAREST instance, exactly as ModMultiplier.MaxForStack
-- prices it; the curve is non-decreasing, so dearest means highest rate.
down_rate AS (
    SELECT score_id, user_id, old_total, max(rate) AS rate
    FROM mod_rate
    WHERE rate < 1.00
    GROUP BY score_id, user_id, old_total
),
-- RateMultiplier.For below 1.0x, in integer basis points: round(max(0.10, 1 - slope x (1 - r)), 4)
-- x 10000. r carries 2 decimals, so 18000 x (1 - r) and 30000 x (1 - r) are whole numbers and the
-- round() is exact, matching the client's MidpointRounding.AwayFromZero to 4 places.
priced AS (
    SELECT d.score_id,
           d.user_id,
           d.old_total,
           round(greatest(1000, 10000 - 18000 * (1 - d.rate)))::bigint AS old_bp,
           round(greatest(1000, 10000 - 30000 * (1 - d.rate)))::bigint AS new_bp
    FROM down_rate d
),
candidate AS (
    SELECT p.score_id,
           p.user_id,
           p.old_total,
           -- floor(old_total x new_bp / old_bp + 1/2) in exact bigint arithmetic. old_total is
           -- non-negative by construction (the submit path stores either a bounds-checked total or
           -- a clamped ceiling, both >= 0), so integer division truncates the same way floor does.
           (2 * greatest(p.old_total, 0) * p.new_bp + p.old_bp) / (2 * p.old_bp) AS new_total
    FROM priced p
),
logged AS (
    INSERT INTO score_rescales (migration, score_id, old_total, new_total)
    SELECT '015_ht_nerf_rescore', c.score_id, c.old_total, c.new_total
    FROM candidate c
    RETURNING score_id
),
updated AS (
    UPDATE scores s
    SET total_score = c.new_total
    FROM candidate c
    WHERE c.score_id = s.id AND c.new_total <> c.old_total
    RETURNING s.id
)
-- The player's cumulative total. It is an accumulator (each accepted submission added its stored
-- total), so it is corrected by the same signed delta the score rows just took, not recomputed.
-- greatest(0, ...) is a floor for the one drift case this cannot see: a submission that failed the
-- tamper checks was stored with a clamped total and never accrued here, so subtracting its delta
-- over-corrects. Such rows are hostile by definition, hold no ranked standing, and every row this
-- migration touched is listed in score_rescales if one ever needs reconciling by hand.
UPDATE user_stats us
SET total_score = greatest(0, us.total_score + d.delta)
FROM (
    SELECT user_id, sum(new_total - old_total) AS delta
    FROM candidate
    GROUP BY user_id
) d
WHERE d.user_id = us.user_id AND d.delta <> 0;
