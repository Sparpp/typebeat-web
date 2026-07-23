-- Reconcile the denormalized play counters. play_count was only ever incremented on user_stats
-- (the player's profile total), never on the beatmaps / beatmapsets rows the website displays,
-- so every map showed 0 plays despite having submitted scores on its leaderboard. ScoreEndpoints
-- now bumps both on each submission; this backfills the history from the scores table (every
-- score row is one recorded play). SET (not +=) is a safe authoritative reconciliation because
-- these columns were untouched until now.
UPDATE beatmaps b
SET play_count = c.n
FROM (SELECT beatmap_id, count(*) AS n FROM scores GROUP BY beatmap_id) c
WHERE c.beatmap_id = b.id;

UPDATE beatmapsets s
SET play_count = c.n
FROM (
    SELECT b.set_id, count(*) AS n
    FROM scores sc
    JOIN beatmaps b ON b.id = sc.beatmap_id
    GROUP BY b.set_id
) c
WHERE c.set_id = s.id;
