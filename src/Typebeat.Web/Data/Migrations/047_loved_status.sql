-- Loved (backlog: loved status): a published set a reviewer has marked as loved, as on osu!.
-- Browsable, downloadable and playable like 'ranked', with a leaderboard (its plays are stored
-- ranked and sit on the ranked board), but it awards no pp and never counts toward the global
-- rankings or first places, which stay confined to 'ranked'. Reached only by the reviewer's Love
-- button from 'pending' or 'unranked'; Unlove returns the set to its intended_status.

ALTER TABLE beatmapsets DROP CONSTRAINT beatmapsets_status_check;

ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_status_check
    CHECK (status IN ('hidden', 'pending', 'unranked', 'ranked', 'loved', 'removed'));
