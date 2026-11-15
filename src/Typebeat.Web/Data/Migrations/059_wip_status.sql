-- WIP (backlog 476): a published set its mapper has marked as still being worked on, chosen in the
-- in-game submission wizard (target "WIP"). Until now WIP and Pending both published to 'pending',
-- so the website labelled every WIP map Pending. As on osu!: browsable, downloadable and playable
-- like 'pending', but it serves no leaderboard (the game client already shows none for WIP) and a
-- reviewer cannot rank or love it until the mapper re-submits it as Pending.
--
-- Existing rows are left exactly as they are. Nothing recorded which 'pending' sets were submitted
-- as WIP (the PUT's target was folded into intended_status = 'pending'), so there is nothing to
-- move them by; a mapper who wants one shown as WIP re-submits it with that target.

ALTER TABLE beatmapsets DROP CONSTRAINT beatmapsets_status_check;

ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_status_check
    CHECK (status IN ('hidden', 'wip', 'pending', 'unranked', 'ranked', 'loved', 'removed'));

-- The wizard's choice is carried to the publish flip in intended_status (migration 012), so WIP
-- has to be a value it can hold too.
ALTER TABLE beatmapsets DROP CONSTRAINT beatmapsets_intended_status_check;

ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_intended_status_check
    CHECK (intended_status IN ('wip', 'pending', 'unranked'));
