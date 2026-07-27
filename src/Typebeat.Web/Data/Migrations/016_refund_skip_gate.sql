-- Skip-gate refund (task 47): the minimum-play-time anti-cheat gate has been wrongly unranking
-- honest players since the game gained its instrumental-skip button (task 5).
--
-- THE BUG. Both submission paths required a play's elapsed wall clock (score-token creation to
-- submission) to be at least 90% of the map's drain length. The skip button lets a player jump any
-- purely instrumental stretch of 10 s or more, so an honest play of a gap-heavy map finishes well
-- under that. On "Immortal Flame" (two qualifying gaps) the old 109.12 s requirement was not
-- reachable by ANY skip-using play, so every one of them was stored unranked. The corrected bound
-- is 0.9 x (drain - skippable), where skippable is what the skip button may legally remove; see
-- Scoring/PlayTimeGate.cs and Packages/Lyrics/InstrumentalGaps.cs (the mirror of the game's
-- typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs).
--
-- WHAT THIS FILE DOES, AND WHAT IT DELIBERATELY DOES NOT
-- The refund itself is NOT expressible here. Two of its inputs live outside SQL:
--   * skippable_s is derived from the map's .osu blob, and blobs live in the content-addressed
--     file store (files/{sha256}), not in any column, so no query can compute it;
--   * "was this row unranked BY THE GATE and by nothing else" needs the submit path's own
--     recompute (ScoringContract: statistics validity, full judgement, the score ceiling), which
--     already exists in C# and must not be mirrored a third time in SQL.
-- So this migration lays the ground and Scoring/SkipGateRefund.cs performs the refund at startup,
-- right after PaceBackfill has filled skippable_s in (Program.cs). It is guarded by, and audited
-- in, the score_refunds table below, exactly the way 015_ht_nerf_rescore.sql guards itself with
-- score_rescales: the (migration, score_id) key is both the idempotency guard and the trail.
--
-- WHY skippable_s DEFAULTS TO 0. Zero means "no skip allowance known", which makes the corrected
-- bound identical to the old one. A row the pace backfill has not reached, or cannot reach (blob
-- missing, unparseable, set version manifest gone), therefore keeps the pre-task-47 gate rather
-- than silently becoming more lenient. The refund pass reads the same column, so it too refunds
-- nothing on a map whose allowance is unknown.

-- The skip allowance in map-time seconds, subtracted from drain_length_s by the play-time gate.
-- Written at ingest (PackageIngest) and by the pace backfill (LyricPace.VERSION 7).
ALTER TABLE beatmaps ADD COLUMN IF NOT EXISTS skippable_s double precision NOT NULL DEFAULT 0;

-- Guard + audit trail for score rows whose `ranked` flag this repo has restored. Keyed by
-- migration like score_rescales, so a future refund gets its own key and its own guard; the
-- recorded elapsed/required pair is what the decision was made on, which is the only way to review
-- it afterwards (nothing else persists a score's elapsed time as a number).
CREATE TABLE IF NOT EXISTS score_refunds
(
    migration   text        NOT NULL,
    score_id    bigint      NOT NULL REFERENCES scores (id) ON DELETE CASCADE,
    -- Seconds between the score token's creation and the submission landing: the exact quantity
    -- the gate compared, reconstructed from scores.started_at / scores.ended_at.
    elapsed_s   double precision NOT NULL,
    -- What the OLD gate demanded, 0.9 x drain.
    old_required_s double precision NOT NULL,
    -- What the corrected gate demands, 0.9 x (drain - skippable).
    new_required_s double precision NOT NULL,
    refunded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (migration, score_id)
);
