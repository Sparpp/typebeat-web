-- Following + mapper watching (task 64), osu-web parity. TWO social relations, ONE table.
--
-- osu-web keeps these apart: "following" a player (their profile, their activity) is a different
-- act from clicking the bell on a mapper (tell me when they upload). We keep them apart too, but
-- they are the same shape (an ordered pair of users plus a timestamp) and every query over them
-- is "rows for this follower" or "rows for this followee", so one table with a discriminator
-- beats two near-identical tables: one set of FKs, one set of indexes, one insert/delete path,
-- and adding a third relation later (say 'blocked') is a CHECK edit rather than a migration that
-- invents another table.
--
-- kind is text + CHECK rather than a Postgres enum, matching how the schema already stores
-- beatmapsets.status and scores.rank: enums need their own ALTER TYPE dance to extend and read
-- back as an extra type OID Npgsql has to be taught about, and there is no measurable win at
-- this scale.
--
--   'user'   the follow button on a profile. Drives the follower/following counts and the two
--            list pages (/users/{id}/followers, /users/{id}/following).
--   'mapper' the bell on a profile. Drives /watching, the feed of recent uploads by the mappers
--            the signed-in user watches. Deliberately NOT restricted to users who have already
--            uploaded something: watching someone before their first map is exactly when the
--            feed is worth having, and a "must be a mapper" rule would have to be re-evaluated
--            every time a set is deleted.
--
-- The pair is the PRIMARY KEY, so double-follow is a no-op at the storage layer (the toggle
-- handlers insert ON CONFLICT DO NOTHING) rather than a duplicate row that would inflate counts.
-- The column ORDER of that key matters: (follower_id, followee_id, kind) means its index also
-- serves every "who does this user follow / watch" read (the two list pages, the /watching feed's
-- owner filter, and the two EXISTS probes the profile header runs to pick the button state),
-- because they all constrain follower_id first.
--
-- ON DELETE CASCADE on both sides: a follow is pure derived social state with nothing hanging off
-- it, so an account deletion should take its edges with it. (Contrast scores, which are
-- deliberately RESTRICT-shaped: they are content.) Note the site has no account-deletion path
-- today; this is the correct behaviour for when it grows one, not something in use.
--
-- The self-follow rule is enforced BOTH here and in the handlers. The handlers give the user a
-- clean 400 instead of a 500, and the CHECK guarantees no other write path (a future admin tool,
-- a backfill, a psql session) can create the row the follower/following lists would render as
-- "you follow yourself".
CREATE TABLE user_follows
(
    follower_id bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    followee_id bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    kind        text        NOT NULL CHECK (kind IN ('user', 'mapper')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (follower_id, followee_id, kind),
    CONSTRAINT user_follows_no_self CHECK (follower_id <> followee_id)
);

-- The other direction, which the PK cannot serve: "who follows this user" (the follower count on
-- every profile view, and the /users/{id}/followers list). kind is in the key because both
-- surfaces always ask for one relation, never the union.
CREATE INDEX ix_user_follows_followee ON user_follows (followee_id, kind);
