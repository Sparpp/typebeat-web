-- typebeat-web migration 043: the DOWNWARD half of the version rule (backlog 398).
--
-- 030_gameplay_fingerprint.sql demotes a RANKED SET back to 'pending' when a mapper re-uploads it
-- with gameplay-affecting changes, and deliberately leaves every stored score's `ranked` flag
-- alone, because at the time `scores.beatmap_id` carried no version link and there was no honest
-- subset to invalidate. Backlog 352 then recovered the version a play was made on from its token
-- (`score_tokens.beatmap_hash`) and used it in the CARRY-UP direction: a play stored unranked on a
-- pending set is re-ranked onto the board only when the map it was played on is, as far as gameplay
-- goes, the map that was ranked (Scoring/PlayedVersionRule.cs).
--
-- WHAT WAS STILL MISSING. Nothing lowered a score that was ALREADY ranked when its set was
-- re-ranked. So a board re-ranked after a re-upload mixed plays from every version it had ever
-- shipped: a v1 play stayed ranked next to the v2 plays. Backlog 398 closes that with the same
-- predicate run DOWNWARD (Scoring/SetRankDemotion.cs): a currently-ranked score whose played
-- version is not provably the current one becomes unranked, both on the rank transition and via a
-- one-time backfill. The owner's strict arm drops the unprovable too: the only keeps are SameBytes
-- and SameGameplay, and a missing token, a hash no stored version produces, or a version with a
-- different fingerprint all demote.
--
-- THIS TABLE IS THE AUDIT TRAIL, and it is a separate table rather than a row in `score_refunds`
-- on purpose. `score_refunds` records the play-time gate's decision (elapsed_s / old_required_s /
-- new_required_s); a demotion is not a gate decision, and writing gate-shaped numbers for it would
-- be a lie in a table whose whole point is "what the decision was made on". Nothing reads this
-- table: it exists so "which scores did the 2026-10-04 batch demote, and why" is answerable
-- afterwards, and so a mistaken demotion can be found and reversed. Idempotence does not depend on
-- it (a demoted row stops matching the sweep's ranked-only candidate query); the primary key only
-- makes the insert safe to repeat.
CREATE TABLE IF NOT EXISTS score_demotions
(
    score_id   bigint      PRIMARY KEY REFERENCES scores (id) ON DELETE CASCADE,
    -- Why the played version was judged not to be the ranked one: 'missing_token',
    -- 'no_matching_version' or 'changed_gameplay' (Scoring/SetRankDemotion.cs DropReason).
    reason     text        NOT NULL,
    -- The set the score sat on, denormalized so the batch is readable without a join back through
    -- a beatmap row that a later upload may have re-pointed.
    set_id     bigint      NOT NULL REFERENCES beatmapsets (id) ON DELETE CASCADE,
    demoted_at timestamptz NOT NULL DEFAULT now()
);
