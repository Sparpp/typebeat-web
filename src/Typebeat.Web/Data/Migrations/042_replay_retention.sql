-- typebeat-web migration 042: replay retention (backlog 365).
--
-- The housekeeping sweep may delete the stored bytes of a replay
-- that is not worth keeping (not among the player's top plays on that map and board, not pinned,
-- not recently uploaded, not recently watched by someone else). The score row itself is never
-- touched beyond its replay columns: the sweep nulls replay_key and stamps replay_pruned_at, so
-- every reader that already treats "no replay_key" as "no replay" keeps working, and
-- tools/score-recalc can still tell a replay that was PRUNED (it existed, the server dropped it)
-- from one that was never uploaded (UnreplayableCase.Pruned against NoReplay).
--
-- A re-upload of the same score clears replay_pruned_at again (ReplayEndpoints), so the column
-- only ever describes the replay the row does not currently have.
ALTER TABLE scores
    ADD COLUMN replay_pruned_at timestamptz;

-- The sweep's candidate scan reads stored replays by age; partial so the index costs nothing for
-- the (majority of) rows that hold no replay at all.
CREATE INDEX ix_scores_replay_age ON scores (replay_uploaded_at) WHERE replay_key IS NOT NULL;
