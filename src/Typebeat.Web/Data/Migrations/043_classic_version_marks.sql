-- typebeat-web migration 043: the DOWNWARD half of the version rule, as a Classic mark (backlog 398).
--
-- 030_gameplay_fingerprint.sql demotes a RANKED SET back to 'pending' when a mapper re-uploads it
-- with gameplay-affecting changes, and deliberately leaves every stored score alone, because at the
-- time `scores.beatmap_id` carried no version link and there was no honest subset to invalidate.
-- Backlog 352 then recovered the version a play was made on from its token
-- (`score_tokens.beatmap_hash`) and used it in the CARRY-UP direction: a play stored unranked on a
-- pending set is re-ranked onto the board only when the map it was played on is, as far as gameplay
-- goes, the map that was ranked (Scoring/PlayedVersionRule.cs).
--
-- WHAT WAS STILL MISSING. A board re-ranked after a re-upload mixed plays from every version it had
-- ever shipped, with no record that an older one was even a different map. Backlog 398 now runs the
-- same predicate DOWNWARD (Scoring/SetRankClassicMark.cs): a score whose played version is not
-- provably the current gameplay is MARKED, not unranked (owner decision 2026-10-04, which supersedes
-- an earlier drop-to-unranked plan). The mark is the synthetic acronym "CL" appended to the score's
-- `scores.mods`, and it costs the play 5 percent: `total_score` is repriced to 0.95x and pp
-- recomputes at 0.95x (Scoring/ModMultiplier.cs and Scoring/PerformancePoints.cs both price "CL").
-- The owner's strict arm marks the unprovable too: the only keeps are SameBytes and SameGameplay.
--
-- THIS TABLE IS THE AUDIT TRAIL. Nothing reads it: it exists so "which scores the 2026-10-04 batch
-- marked, and why" is answerable afterwards, and so a mistaken mark can be found and reverted (the
-- mark is removed by deleting the "CL" entry from `scores.mods` and repricing the row, exactly as
-- reversing any other edit). Idempotence does NOT depend on it: the mark's own presence in `mods` is
-- the sweep's candidate filter, so a marked row is skipped. The primary key only makes the insert
-- safe to repeat.
CREATE TABLE IF NOT EXISTS score_classic_marks
(
    score_id   bigint      PRIMARY KEY REFERENCES scores (id) ON DELETE CASCADE,
    -- Why the played version was judged not to be the ranked one: 'missing_token',
    -- 'no_matching_version' or 'changed_gameplay' (Scoring/SetRankClassicMark.cs MarkReason).
    reason     text        NOT NULL,
    -- The set the score sat on, denormalized so the batch is readable without a join back through
    -- a beatmap row that a later upload may have re-pointed.
    set_id     bigint      NOT NULL REFERENCES beatmapsets (id) ON DELETE CASCADE,
    marked_at  timestamptz NOT NULL DEFAULT now()
);
