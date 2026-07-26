-- Replay storage: the server half of the replay pipeline (backlog 37).
--
-- Until now nothing on the server ever accepted a replay, so every online leaderboard row
-- reported has_replay=false and the client's "watch replay" action had nothing to fetch. The
-- game now uploads the legacy .osr it already encodes locally, right after a successful score
-- submission, via PUT /api/v2/scores/{id}/replay.
--
-- The bytes themselves live in the object store as a NAMED object (replays/{scoreId}.osr), not
-- as a content-addressed blob: a replay is unique to one score (no cross-score dedup to win),
-- and the upload contract is "the owner may overwrite their own", which is exactly named-object
-- (mutable key) semantics. Content-addressed blobs are write-once and never deleted, so a
-- re-upload there would orphan the superseded blob forever.
--
-- These columns are the index over that store:
--   replay_key         the store key, NULL = no replay stored. This column IS the has_replay
--                      flag on the wire, so the leaderboard never has to stat the filesystem.
--                      Stored rather than derived so a future R2/S3 move can rewrite keys per row.
--   replay_bytes       size of the stored object (ops visibility on /data growth).
--   replay_uploaded_at when it landed; a re-upload overwrites both the object and this stamp.
--
-- Existing rows keep NULL, i.e. has_replay=false. There is no backfill: the server never held
-- these bytes, so a pre-feature score can only gain a replay if the client re-uploads one it
-- still has locally.
ALTER TABLE scores
    ADD COLUMN replay_key         text,
    ADD COLUMN replay_bytes       integer,
    ADD COLUMN replay_uploaded_at timestamptz;
