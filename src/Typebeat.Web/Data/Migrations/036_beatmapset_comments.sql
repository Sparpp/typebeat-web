-- Set-page social surface (backlog 295): the description gets a writer, and sets get comments.
--
-- beatmapsets.description has existed since 002_website_uploads.sql and the set page has rendered
-- it (plain-text-encoded) all along, but NOTHING wrote it: every row still carries the '' default.
-- The page now lets the set owner (or a map reviewer) edit it, so the column gets the same budget
-- the profile bio has (Settings.IndexModel.MaxDescriptionLength = 2000). Every existing row is '',
-- so this CHECK cannot fail on apply. The description is deliberately NOT folded into
-- beatmapsets.search: the search vector is title/artist/tags/creator by design, and a mapper
-- padding a description with popular words must not hijack the listing.
ALTER TABLE beatmapsets
    ADD CONSTRAINT beatmapsets_description_length CHECK (length(description) <= 2000);

-- User comments on a beatmapset, the 'comment' future that 027_notifications.sql anticipated
-- three times in its own comments. Typed FK columns, not a jsonb payload, per the house style
-- (023/026/027).
--
-- user_id CASCADE is safe: account deletion is GDPR erasure (Settings' Delete handler), which
-- ANONYMIZES the users row rather than deleting it, so a comment outlives its author as
-- 'deleted_{id}' the way scores and moderation records already do. The cascade only fires for a
-- hard DELETE, which no production path performs.
--
-- Deletion of a comment is SOFT (deleted_at/deleted_by), for two reasons: the comment list pages
-- by keyset cursor over id, and a hard delete would shift what "the 50 after id N" means under a
-- reader's feet; and the deletion audit reads off the row itself (who removed it, and what the
-- body said), so a reviewer removal stays reviewable.
CREATE TABLE beatmapset_comments
(
    id         bigserial   PRIMARY KEY,
    set_id     bigint      NOT NULL REFERENCES beatmapsets (id) ON DELETE CASCADE,
    user_id    bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    body       text        NOT NULL CHECK (length(body) BETWEEN 1 AND 2000),
    created_at timestamptz NOT NULL DEFAULT now(),

    -- NULL = live. Set together by the one soft-delete write; deleted_by is the remover (the
    -- author, the set owner, or a reviewer), kept plain like moderation_actions.actor_id.
    deleted_at timestamptz,
    deleted_by bigint      REFERENCES users (id)
);

-- The set page's read: live comments of one set, oldest first, keyset-paged on id. PARTIAL so
-- deleted rows leave the index the way read notifications leave ix_user_notifications_unread.
CREATE INDEX ix_beatmapset_comments_live ON beatmapset_comments (set_id, id)
    WHERE deleted_at IS NULL;

-- A user's own comment history (moderation lookups, and any future profile surface).
CREATE INDEX ix_beatmapset_comments_user ON beatmapset_comments (user_id, created_at DESC);

-- The second notification kind, exactly the CHECK-edit-plus-nullable-column 027 said it would be.
-- 'map_comment': someone commented on your set. actor_id is the commenter, set_id is the set,
-- comment_id is the comment. Fires once per comment, so it does NOT join
-- ux_user_notifications_mapper_upload (that index is kind-scoped for precisely this reason).
ALTER TABLE user_notifications
    DROP CONSTRAINT user_notifications_kind_check;
ALTER TABLE user_notifications
    ADD CONSTRAINT user_notifications_kind_check CHECK (kind IN ('mapper_upload', 'map_comment'));
ALTER TABLE user_notifications
    ADD COLUMN comment_id bigint REFERENCES beatmapset_comments (id) ON DELETE CASCADE;
