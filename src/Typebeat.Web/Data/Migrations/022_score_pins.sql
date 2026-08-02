-- Pinned scores (task 63): a player can pin their own ranked plays so a "Pinned" section shows at
-- the top of their profile, above Best/Recent. osu-web parity, minus the drag-reordering (a later
-- task adds profile-section reordering; per-pin ordering can ride that pattern then). Until then
-- the section is strictly newest-pin-first, which is what pinned_at exists for.
--
-- WHY score_id IS THE PRIMARY KEY, not (user_id, score_id). A score belongs to exactly one user
-- (scores.user_id), and only that user may pin it, so "this score is pinned" is a property OF THE
-- SCORE: at most one pin row can ever exist for it. Keying on score_id alone makes that a schema
-- fact instead of an invariant the app has to keep, and it makes the double-pin case a plain
-- ON CONFLICT rather than a read-then-write race.
--
-- user_id is then redundant with scores.user_id, and is stored anyway for two reasons: the profile
-- section and the 10-pin cap both ask "which pins does user X have", and neither should have to
-- join scores to find out. The write path derives the value FROM the score row (it never trusts a
-- client-supplied owner), so the two cannot drift.
--
-- ON DELETE CASCADE on score_id: nothing deletes score rows today (scores are kept even through
-- account erasure, which anonymizes the user instead), but a pin is meaningless without its score,
-- and a dangling pin row would make the profile query silently short a row. The FK on user_id is
-- deliberately NOT cascading: users are never hard-deleted, and the settings-page erasure removes
-- pins explicitly (Pages/Settings/Index.cshtml.cs, anonymize_sql) since they are personal curation.
--
-- The cap is app-side (ScorePins.MaxPins), not a constraint here: a CHECK cannot count sibling
-- rows, and the write path already serializes per user with pg_advisory_xact_lock so the count it
-- reads cannot be stale (same pattern as the BSS ingest lock in Packages/PackageIngest.cs).
CREATE TABLE score_pins
(
    score_id  bigint PRIMARY KEY REFERENCES scores (id) ON DELETE CASCADE,
    user_id   bigint      NOT NULL REFERENCES users (id),
    pinned_at timestamptz NOT NULL DEFAULT now()
);

-- The profile section query: this user's pins, newest first. Covers the cap count too.
CREATE INDEX ix_score_pins_user ON score_pins (user_id, pinned_at DESC);
