-- Notifications (task 70): the bell in the site header, and the rows behind its badge.
--
-- Task 64 shipped mapper watching with NO notification system: "tell me when they upload" was
-- answered by a page you had to remember to visit (/watching). This table is the other half, the
-- thing that finds you: one row per (recipient, event), counted for the badge and listed in the
-- dropdown.
--
-- ---------------------------------------------------------------------------------------------
-- FAN-OUT ON WRITE, not fan-out on read. A row per watcher is written at the moment the event
-- happens, rather than every page load re-deriving "what has happened since I last looked" from
-- user_follows x beatmapsets. Two reasons, and the second is the real one:
--
--   1. The badge is on EVERY page of the site for every signed-in user, so its query has to be a
--      single indexed count, not a join over a watchlist. See ix_user_notifications_unread.
--   2. Read state is per notification, and there is nothing to hang it off in a derived world. A
--      "last seen" watermark cannot express "I read this one and not that one", and the moment a
--      second kind of notification exists (a comment, a rank change) a watermark has to become a
--      watermark PER KIND, which is this table with a worse shape.
--
-- The cost of fan-out is the write amplification of a popular mapper (one upload, N rows). That
-- is bounded by the watcher count, happens once per publish, and rides inside the ingest
-- transaction that was already open, so it is one extra INSERT ... SELECT.
--
-- ---------------------------------------------------------------------------------------------
-- TYPED NULLABLE REFERENCE COLUMNS, NOT A jsonb PAYLOAD. What a notification points at is stored
-- as real FK columns (set_id, actor_id), so the schema stays queryable and self-cleaning:
-- ON DELETE CASCADE means deleting a set or an account takes its notifications with it, with no
-- background sweep hunting for dangling ids inside a JSON blob, and no possibility of a row whose
-- payload names a set that no longer exists. This is the same call 023_follows.sql and
-- 026_profile_order.sql made (columns over documents) and it is the house style: a jsonb payload
-- would move the shape out of the database and into whatever C# happened to write it.
--
-- The price is that a future kind needing a new reference (a score, a comment) needs a migration
-- adding a nullable column. That is a two-line ALTER, it is visible in review, and it is exactly
-- the moment you want to be thinking about the delete behaviour of the thing you are pointing at.
--
-- kind is text + CHECK, matching beatmapsets.status, scores.rank and user_follows.kind: adding a
-- kind is a CHECK edit rather than the ALTER TYPE dance (and the extra type OID) an enum costs.
--
--   'mapper_upload'  a mapper you watch published a new set. actor_id is the mapper, set_id is
--                    the set. Fired exactly once per (watcher, set): see the unique index below.
--
-- ---------------------------------------------------------------------------------------------
-- NOTHING IS BACKFILLED. Every watched-mapper upload that exists today predates this table, and
-- inventing rows for them would hand every watcher a badge full of maps they have already seen
-- (or already decided not to care about) on the first page load after the deploy. Notifications
-- start at zero for everybody and fill up from the next publish onward. The /watching feed still
-- lists the historical uploads, which is where "what did I miss" belongs.
CREATE TABLE user_notifications
(
    id         bigserial PRIMARY KEY,

    -- The RECIPIENT. Every read path filters on this column and only this column, which is also
    -- the authorization check: a handler updates WHERE user_id = <session user>, so a forged id
    -- belonging to somebody else matches no row rather than being detected and refused.
    user_id    bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,

    kind       text        NOT NULL CHECK (kind IN ('mapper_upload')),

    -- What it points at. Nullable because a future kind may point at neither.
    set_id     bigint      REFERENCES beatmapsets (id) ON DELETE CASCADE,

    -- Who caused it (the mapper, for 'mapper_upload'). CASCADE for the same reason as set_id: a
    -- notification is derived social state and has no meaning without its actor.
    actor_id   bigint      REFERENCES users (id) ON DELETE CASCADE,

    created_at timestamptz NOT NULL DEFAULT now(),

    -- NULL = unread. A timestamp rather than a boolean because it costs the same 8 bytes as the
    -- alignment padding a bool would sit in, and "when did they see this" is the question any
    -- future digest email or retention sweep asks.
    read_at    timestamptz
);

-- The dropdown and the /watching list: this user's notifications, newest first. id DESC breaks
-- created_at ties, which a fan-out INSERT produces by the dozen (every row of one publish shares
-- the statement's now()), so paging cannot skip or repeat a row.
CREATE INDEX ix_user_notifications_feed ON user_notifications (user_id, created_at DESC, id DESC);

-- The badge, on every page load of the whole site. PARTIAL: read notifications are the
-- overwhelming majority over time and the count never looks at them, so keeping them out of this
-- index makes it roughly "one entry per unread row site-wide" and lets a row leave the index
-- (rather than move within it) when it is read.
CREATE INDEX ix_user_notifications_unread ON user_notifications (user_id, created_at DESC)
    WHERE read_at IS NULL;

-- At most ONE 'mapper_upload' per (watcher, set), enforced here rather than trusted to the
-- caller. The fan-out already fires only on the hidden -> published transition, which happens
-- once per set, so this index is belt and braces: it makes a re-notify unrepresentable no matter
-- what a future admin republish tool, a manual UPDATE, or a retried transaction does.
--
-- Scoped to the one kind with WHERE, deliberately: at-most-once is a property of THIS kind, not
-- of notifications in general. A future 'comment' kind fires many times for one set and must not
-- inherit a uniqueness rule it would violate on its second row.
CREATE UNIQUE INDEX ux_user_notifications_mapper_upload
    ON user_notifications (user_id, set_id)
    WHERE kind = 'mapper_upload';
