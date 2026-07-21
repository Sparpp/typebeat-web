-- Unranked: a published set the creator deliberately marks as not for ranking (chosen in the
-- in-game submission wizard). Browsable, downloadable and playable like 'pending', but never
-- leaderboard-eligible and never promoted by a reviewer. Distinct from 'pending' (awaiting review,
-- seeking ranked) and 'hidden' (pre-publish shell).

ALTER TABLE beatmapsets DROP CONSTRAINT beatmapsets_status_check;

ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_status_check
    CHECK (status IN ('hidden', 'pending', 'unranked', 'ranked', 'removed'));

-- The submission wizard's target ('pending' vs 'unranked') arrives on the PUT /bss/beatmapsets
-- metadata call, but the publish flip (hidden → published) only happens later, on the first
-- package upload. Persist the creator's choice here so the flip lands on the right status.
ALTER TABLE beatmapsets
    ADD COLUMN intended_status text NOT NULL DEFAULT 'pending'
    CHECK (intended_status IN ('pending', 'unranked'));
