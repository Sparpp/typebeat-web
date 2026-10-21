-- typebeat-web migration 051: the drain length the client is shown, gameplay time only (backlog 423).
--
-- The set overlay's length tooltip read "Drain length" equal to the map length, because the wire's
-- hit_length came straight from drain_length_s, which is the whole lyric SPAN (first line start to
-- last line end) and so counts every instrumental stretch as play. hit_length_s is that span minus
-- each stretch the mid-song skip rule recognises as instrumental, the whole perceived stretch
-- (Packages/Lyrics/InstrumentalGaps.DrainLengthMs, mirrored by the game's song select and pinned
-- against it in WireCompat's DrainLengthParityTest). It is DISPLAY ONLY.
--
-- drain_length_s KEEPS its meaning on purpose. It is the play-time anti-cheat gate's input
-- (Scoring/PlayTimeGate: 0.9 x (drain_length_s - skippable_s) / rate), and skippable_s is already
-- carved out of those same instrumentals, so pointing the gate at hit_length_s would subtract them
-- twice and let impossibly fast plays rank. Leaving the column alone keeps every map's required
-- seconds identical; it also stays the cap on a score's stored play time, which is wall clock the
-- player really spent, instrumentals included.
--
-- NULL, no DEFAULT: NULL means "not computed yet", and every reader falls back to drain_length_s,
-- i.e. exactly what it showed before this migration. Ingest always writes both columns, and
-- Packages/HitLengthBackfill.cs fills every live difficulty at startup from its stored .osu,
-- writing nothing else. hit_length_version stamps the InstrumentalGaps.DRAIN_VERSION the value was
-- computed under, so a rule change recomputes the column without touching ratings or scores.
ALTER TABLE beatmaps
    ADD COLUMN hit_length_s double precision,
    ADD COLUMN hit_length_version smallint;
