-- Replay views (task 65), osu-web parity: the "Replays Watched by Others" number in the profile
-- stats card, the "Most viewed replays" list, and the views-per-month chart beside Play history.
--
-- ---------------------------------------------------------------------------------------------
-- WHAT ONE VIEW IS. The whole definition lives here and in Scoring/ReplayViews.cs; nothing else
-- may invent its own. A view is counted when ALL FOUR of these hold:
--
--   1. A STORED REPLAY WAS ACTUALLY SERVED. GET /api/v2/scores/{id}/replay (ReplayEndpoints, the
--      only path that serves replay bytes anywhere: the game client's watch-replay action and the
--      website's "replay" / "Download" links all hit it) got as far as opening the object. A 404,
--      for an unknown score, a score with no replay, or a row whose object is missing, counts
--      nothing: there was nothing to watch.
--   2. THE REQUESTER IS NOT THE SCORE'S OWNER. The heading is "watched by OTHERS", so an owner
--      re-downloading their own .osr never moves their own number. (The game client barely even
--      asks: ReplayAvailabilityResolver prefers the bit-exact local replay for your own scores.)
--   3. THE REQUESTER IS IDENTIFIED, by bearer token (the game client sends one on every API
--      request) or by the website session cookie. An anonymous serve counts NOTHING. There is no
--      honest per-person key for an anonymous requester: behind Cloudflare a whole ISP shares one
--      edge IP, CF-Connecting-IP is spoofable by anything reaching the origin directly, and
--      storing (even hashed) visitor IPs would create a new personal-data store to hold a
--      counter. So this number UNDERCOUNTS, deliberately and in one direction only, exactly like
--      osu-web, where watching a replay requires being signed in at all. Signed-out watching
--      keeps working; it is simply not counted.
--   4. IT IS THE FIRST SUCH SERVE FOR THAT (SCORE, VIEWER) PAIR TODAY, UTC. That is what the
--      replay_views ledger below exists to decide. Without it, a refreshed browser tab, a client
--      that retries a failed import, or somebody sitting on F5 would each be worth unbounded
--      "views", and the most-viewed list would rank persistence rather than interest.
--
-- UTC for the same reason 024_play_history.sql spells out: the day (and therefore the month) a
-- view lands in must never change afterwards, or an incremented counter is the wrong structure
-- for it. The site renders every other date in UTC too.
--
-- ---------------------------------------------------------------------------------------------
-- THREE OBJECTS, ONE WRITE PATH (Scoring/ReplayViews.RecordAsync writes all three in one
-- statement, so they cannot drift):
--
--   replay_views             the dedup ledger, one row per (score, viewer, UTC day).
--   scores.replay_views      per-score running total, for the "Most viewed replays" list.
--   user_month_replay_views  per-OWNER monthly rollup, for the stats-card total and the chart.
--
-- The ledger alone could answer all three questions with a GROUP BY, and is deliberately not
-- asked to: it is the widest-growing table of the three (one row per person per score per day),
-- while both reads are on the profile page, which is loaded constantly. Counters keep those reads
-- index-served and constant-cost, and keep the write a single round trip.
--
-- NO BACKFILL, and none is possible. 024 could reconstruct months from scores.ended_at because
-- every past play left a row; a past replay SERVE left nothing anywhere in the database (the
-- endpoint has never written anything, and there is no request log in Postgres), so every counter
-- honestly starts at zero on deploy day and the chart starts at the first watch after it. A
-- backfill from, say, replay_uploaded_at would be inventing data.
CREATE TABLE replay_views
(
    score_id  bigint NOT NULL REFERENCES scores (id) ON DELETE CASCADE,
    viewer_id bigint NOT NULL REFERENCES users (id) ON DELETE CASCADE,

    -- The UTC day of the serve, a date rather than a timestamp: it IS the dedup bucket, and
    -- storing the instant would invite a second, subtly different notion of "same day".
    viewed_on date   NOT NULL,

    -- The dedup rule as a schema fact rather than an app invariant: the write is an INSERT
    -- ... ON CONFLICT DO NOTHING whose rowcount says whether this serve counted, so two
    -- simultaneous requests from the same viewer serialize on the key instead of both reading
    -- "not viewed yet". score_id leads because the only reads are per score.
    PRIMARY KEY (score_id, viewer_id, viewed_on)
);

-- Ledger rows older than "today" can never be hit again (the key includes the day), so pruning
-- them is safe whenever the table's size starts to matter. No pruner ships now: at this scale the
-- rows are worth more than the space, they are the only per-view record we keep, and a cron that
-- deletes data is not something to add before there is data.
--
-- ON DELETE CASCADE on both FKs: a ledger row is meaningless without its score, and viewer rows
-- follow the same rule the rest of the social tables use. Account ERASURE
-- (Pages/Settings/Index.cshtml.cs) deletes this user's rows as VIEWER explicitly, next to
-- beatmapset_downloads, because "which replays this person watched" is personal activity. The
-- counters below are not touched by that: an owner's view total must not shrink because one of
-- their viewers closed their account, exactly as their scores and play counts survive it.

-- Per-score total. NOT NULL DEFAULT 0 is a metadata-only ALTER on Postgres 11+, so this does not
-- rewrite the scores table.
ALTER TABLE scores
    ADD COLUMN replay_views integer NOT NULL DEFAULT 0;

-- The "Most viewed replays" section: this user's scores, most-watched first. PARTIAL, on
-- replay_views > 0, because the overwhelming majority of scores will never be watched at all and
-- have no business in an index that exists to answer "the top ten of the ones that were". id is
-- the tie-break, so the section's order matches the query's ORDER BY exactly.
CREATE INDEX ix_scores_user_replay_views ON scores (user_id, replay_views DESC, id)
    WHERE replay_views > 0;

-- Views of THIS user's replays, per month: the chart, and (summed) the stats-card total. Same
-- shape and same reasoning as user_month_playcounts (024): month is a date pinned to the first of
-- the month so the chart's gap filling is plain date stepping, and the primary key is the read
-- index, since the only query is "every month for this user, in order".
CREATE TABLE user_month_replay_views
(
    user_id bigint  NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    month   date    NOT NULL,
    views   integer NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, month)
);
