-- Play history (task 66), osu-web parity: the "Play History" bar chart on a user profile, plays
-- per month. One row per (user, month), incremented as plays land.
--
-- A ROLLUP, NOT AN EVENT LOG. The obvious alternative is to derive the chart on read straight from
-- scores (GROUP BY date_trunc('month', ended_at)), and for today's data volume that would work.
-- It is not what ships, for two reasons. First, scores is the hottest table on the site and the
-- profile page already runs six queries over it; a seventh that scans a user's WHOLE history (no
-- covering index gives grouped monthly counts, ix_scores_user_recent is ordered for the recent-20)
-- gets slower with every play, forever, on a page that is loaded constantly. Second, the counter
-- the graph is meant to visualize is user_stats.play_count, which is NOT "count(*) of scores rows"
-- (see the backfill note below), so a read-time GROUP BY would drift from the number printed in
-- the stats card immediately above the chart. An incremented rollup lets the write path apply
-- exactly the same rule to both, which is the invariant that actually matters here.
--
-- month is a DATE pinned to the first of the month, not a (year int, month int) pair: it sorts,
-- compares, and does interval arithmetic ('2026-08-01'::date - interval '23 months') natively, and
-- the "fill the empty months" pass the chart needs is then plain date stepping rather than
-- carry-the-year modular arithmetic. There is no CHECK forcing day-of-month 1 because both the
-- write path and this backfill produce it through date_trunc, and a CHECK would cost every insert
-- an evaluation to catch a bug no code path can express.
--
-- TIMEZONE: UTC, deliberately and everywhere. scores.ended_at is timestamptz (an instant), so
-- "which month is this play in" has no answer until a zone is chosen. The site has no per-user
-- timezone preference and no concept of local time anywhere else (the profile's dates, the
-- leaderboards and the score rows all render UTC), so honouring one here would put the only
-- zone-aware surface on the site next to five zone-naive ones. UTC also makes the bucket stable:
-- a play never changes month later, which is what lets this be an incremented counter at all.
-- The cost is that a player in UTC+13 sees a late-evening play on the 31st counted in the next
-- month; that is one play at a boundary, on a chart whose bars are months.
CREATE TABLE user_month_playcounts
(
    user_id bigint  NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    month   date    NOT NULL,
    plays   integer NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, month)
);

-- The primary key IS the read index: the chart's only query is "every month for this user, in
-- order", which the PK's (user_id, month) prefix serves directly, so no second index is created.
-- Nothing reads a month across users (there is no site-wide activity graph); when something does,
-- it gets its own index then rather than paying for one now on every insert.
--
-- ON DELETE CASCADE, unlike user_stats (which has no such clause and is never deleted): users are
-- never hard-deleted today, and account erasure (Pages/Settings/Index.cshtml.cs) anonymizes the
-- row and deliberately KEEPS the score aggregates, so these rows survive erasure exactly as
-- user_stats.play_count does. The cascade is only what should happen if a hard delete is ever
-- added, and it is deliberately NOT wired into the erasure statement list: this is derived score
-- data, not personal curation like favourites or pins.

-- ---------------------------------------------------------------------------------------------
-- BACKFILL: existing history, from the stored score rows' own timestamps.
--
-- One scores row is one recorded play, the same definition migration 010 used to reconcile the
-- beatmaps / beatmapsets play counters, and it needs no C# (unlike the 009/016/018/020 backfills,
-- which had to be deferred to startup passes because their inputs live in blobs).
--
-- HONEST DISCREPANCY, one direction only. Going forward, both submission paths bump the month
-- counter inside the SAME guard that bumps user_stats.play_count (Endpoints/ScoreEndpoints.cs,
-- Endpoints/PlayEndpoints.cs: recomputed.StatisticsValid && withinBounds), so a month's plays and
-- the profile's play count move together, fails and unranked plays included. The scores INSERT in
-- those paths is NOT under that guard: a submission that fails the tamper checks is still stored
-- (unranked, with clamped values) while its stats are withheld. So for history predating this
-- migration, a user who ever had such a submission gets a month total slightly ABOVE what their
-- play_count reflects, by exactly the number of rejected submissions they made.
--
-- This is not corrected, and no attempt is made to guess: the guard is ScoringContract.Recompute
-- over each row's statistics jsonb, which is C# (mirrored in JS for the browser player, and
-- deliberately mirrored nowhere else), and reproducing it in SQL would create a third copy of the
-- scoring contract to keep in lockstep, to reclassify a handful of rows written by tampered
-- clients. The opposite error, a month UNDERCOUNTING relative to play_count, cannot happen: every
-- play_count increment runs in the same transaction as its scores INSERT, so there is no
-- play_count increment anywhere in the history without a score row carrying its timestamp.
--
-- Rows for restricted or anonymized users are backfilled like everyone else's: their scores rows
-- exist, and their profiles are simply not reachable (a restricted profile 404s), so filtering
-- here would only make the data wrong for an account that later comes back off restriction.
INSERT INTO user_month_playcounts (user_id, month, plays)
SELECT s.user_id,
       date_trunc('month', s.ended_at AT TIME ZONE 'UTC')::date,
       count(*)
FROM scores s
GROUP BY 1, 2;
