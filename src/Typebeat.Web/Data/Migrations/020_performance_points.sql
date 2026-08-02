-- typebeat-web migration 020: performance points (pp), task 61. pp replaces cumulative score as
-- the MAIN global ranking; the score board survives as the second tab on /rankings. The formula's
-- canonical home is docs/pp.md, implemented in Scoring/PerformancePoints.cs.
--
-- WHAT IS STORED AND WHAT IS NOT.
--   * PER-PLAY pp is stored (scores.pp). It is a pure function of the map's star rating, the play's
--     statistics blob, its accuracy/combo and its mods, none of which change once written, so it is
--     computed once at submission and then only recomputed when one of its INPUTS moves (see the
--     version stamp below). Computing it on read instead would mean parsing every score's
--     statistics jsonb on every rankings page load.
--   * The PER-PLAYER TOTAL is deliberately NOT stored (Scoring/PpRanking.cs computes it on read:
--     best play per ranked map, sorted, summed with a 0.85 decay). The decay is expected to rise
--     towards osu's 0.95 as the ranked pool grows, and keeping the total out of the schema makes
--     that a one-line change with no migration and no recompute job. Eligibility (score ranked, set
--     ranked, account not restricted/deleted) is likewise re-checked on read, so an admin un-rank
--     lands on the board immediately.
--
-- SR AT RATE. pp prices Double Time / Half Time exclusively through the star rating recomputed at
-- the play's clock rate, with no flat rate multiplier anywhere, so nothing double-counts. Only the
-- BASE rates earn pp (DT/NC 1.50x, HT 0.75x; a custom rate still ranks on the score leaderboards
-- and simply earns 0 pp), which means the server only ever needs three ratings per map. Two of them
-- are new columns here; the third is the existing rate-1.0 beatmaps.difficulty_rating. Nothing
-- computes a rate-adjusted rating at query time.
--
-- NULL, not 0, for sr_dt / sr_ht: "not computed yet" has to be distinguishable from "genuinely
-- zero stars" (an empty map rates 0.0), because a play whose rating is merely missing must earn no
-- pp YET and be revisited, whereas a play on a genuinely 0-star map earns no pp ever. The values
-- cannot be derived in SQL (LyricDifficulty is a C# strain model over the stored .osu lyric data,
-- exactly as 009/016/018 could not do their backfills here either), so Packages/PaceBackfill.cs
-- fills them from the stored blobs at startup.
--
-- WHY THE SR SWEEP DOES NOT BUMP LyricPace.VERSION. PaceBackfill's staleness predicate gains a
-- second arm (sr_dt IS NULL) instead. A VERSION bump to 9 would ALSO re-derive every stored map
-- against the punctuated text (see the doc comment on LyricPace.VERSION, task 59), changing the
-- word/cell counts of .osz-conversion-tool blobs. That re-derivation is a deliberate, separate
-- decision; bundling it into a pp deploy would silently change published pace numbers. The extra
-- arm keeps the two decoupled: this sweep fills the new columns once and touches nothing else,
-- and a later v9 bump still recomputes sr_dt/sr_ht for free because they ride the same UPDATE.
ALTER TABLE beatmaps ADD COLUMN sr_dt double precision;
ALTER TABLE beatmaps ADD COLUMN sr_ht double precision;

-- 0, not NULL, for scores.pp: a play that earns nothing (unranked, failed, custom rate) genuinely
-- has zero performance, and pp_version below is what carries "computed or not", so the ranking
-- query never needs a NULL special case.
ALTER TABLE scores ADD COLUMN pp double precision NOT NULL DEFAULT 0;

-- Scoring/PerformancePoints.VERSION at the time this row's pp was written; 0 means "never computed
-- under any version", which is every pre-migration row and every row whose beatmap has since had
-- its star ratings rewritten. Packages/PpBackfill.cs recomputes everything below the current
-- VERSION at startup, in the same self-healing shape as PaceBackfill: a row it cannot resolve (its
-- map's rate SR not filled in yet) is left stale and retried on the next boot rather than being
-- stamped with a wrong value. That is what stops a pp computed before its SR existed from
-- silently freezing at zero.
ALTER TABLE scores ADD COLUMN pp_version smallint NOT NULL DEFAULT 0;

-- The ranking query's fold: best pp per (user, beatmap) over ranked, passed, pp-earning rows.
-- Partial, so the index carries only rows that can ever reach the board.
CREATE INDEX ix_scores_pp ON scores (user_id, beatmap_id, pp DESC) WHERE ranked AND passed AND pp > 0;

-- No index for the backfill's "pp_version < VERSION" scan on purpose: the predicate moves with
-- every version bump so a partial index could not be pinned to it, and at this table's size the
-- one sequential scan per boot costs nothing.
