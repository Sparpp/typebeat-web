-- Role grants for three production accounts: Noe and Drexion become map reviewers (the
-- "beatmap ranker" role added by 005_ranked_approval.sql), and spuro becomes an administrator.
-- An administrator already implies a reviewer (AuthedUser.CanReviewMaps is IsAdmin ||
-- MapReviewer), and since this migration's sibling change an administrator is also the only
-- role exempt from the no-self-rank rule, which is why spuro's grant is is_admin and not
-- map_reviewer.
--
-- Why user-row data rides a migration, which nothing here has done before (every other users
-- migration is a pure ADD COLUMN): the deploy has no automated grant path at all.
-- deploy/set-reviewer.sh is a manual SSH lever run on the prod box, and there is no
-- set-admin.sh sibling for is_admin, so a migration is the only way for a grant to arrive with
-- a deploy instead of with a human at a psql prompt.
--
-- Deliberately a SILENT NO-OP for a username that is not present: dev, test and CI databases
-- have none of these three accounts, and a misspelling must never brick deploy boot (each
-- migration runs in its own transaction ahead of the first request). A plain UPDATE matching no
-- row is exactly that no-op, so there is no RAISE here on purpose. It is idempotent as well:
-- replaying it, which a fresh database does, sets the same flags to the same values.
-- username is citext UNIQUE, so these literals match whatever casing each account registered
-- with, and no lower() or ::text cast is needed.
--
-- Roles are hydrated per TOKEN rather than per request (TokenService reads is_admin and
-- map_reviewer in the single SELECT behind both the session cookie and the game's bearer), so
-- an account that is already signed in picks its new role up on the next sign in or token
-- refresh. Access tokens live 24h, so that is within a day at the outside, immediately on a
-- fresh login.

UPDATE users SET map_reviewer = true WHERE username IN ('Noe', 'Drexion');

UPDATE users SET is_admin = true WHERE username = 'spuro';
