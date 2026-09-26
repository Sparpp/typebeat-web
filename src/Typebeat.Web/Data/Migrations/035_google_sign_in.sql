-- typebeat-web migration 035: "Continue with Google" (OpenID Connect sign-in) and the accounts it
-- can create, which have NO PASSWORD.
--
-- WHY password_hash BECOMES NULLABLE. Until now every account came into existence through a
-- password: the website /register form and the game's POST /users both hash one on the way in, and
-- 001 declared the column NOT NULL on that basis. An account created by signing in with Google has
-- no password to hash, and inventing one (a random string nobody knows) would be a password in
-- every sense the code checks, just an unguessable one, which hides the real state of the account
-- from every reader. NULL says exactly what is true: this account cannot sign in with a password.
--
-- NULL NEVER AUTHENTICATES. Auth/PasswordService.Verify answers false for a null (or empty) hash
-- before the hasher is ever consulted, and every password check in the app goes through it: the
-- website /login form, the game client's POST /oauth/token password grant, and the Settings page.
-- The empty string is the older "no usable password" marker: account deletion (Settings,
-- anonymize_sql) has written '' since 007 and still does, and it is treated exactly like NULL.
--
-- THE GAME CLIENT ONLY KNOWS PASSWORDS. It signs in with a username or email and a password over
-- the password grant and has no browser flow, so a Google-only account cannot play until it sets a
-- password. Settings offers that ("Set a password"), gated on an emailed code of the new purpose
-- 'password' below, the same code machinery /verify and /reset-password use.
--
-- user_external_logins is the link between a local account and an identity at an outside
-- provider. Only 'google' exists today; the table is shaped for more so a second provider is a
-- code change, not a schema change.
--
--   subject     the provider's stable, never-reassigned account id (the ID token's `sub`). This,
--               and never the email, is what identifies a returning Google user: a Google account's
--               address can change, and an address can be recycled to a different person.
--   email       the address Google reported at link time (refreshed on every sign-in). Display
--               only, for the Settings page's "linked to ..." line; nothing matches against it.
--
--   UNIQUE (provider, subject)   one Google account signs in to at most one type!beat account.
--   UNIQUE (user_id, provider)   one type!beat account links at most one Google account; linking a
--                                different one means unlinking first.
--
-- Both constraints are named so Auth/ExternalLogins.cs can tell which one a racing INSERT lost on.
-- Users are anonymized rather than deleted (007), so the FK never cascades in practice; the
-- anonymizing delete in Settings removes the link rows itself, because which Google account a
-- person used is personal data. ON DELETE CASCADE is only there so a hard delete, should one ever
-- be run by hand, cannot be blocked by a row nobody needs.
ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;

CREATE TABLE user_external_logins
(
    user_id    bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    provider   text        NOT NULL,
    subject    text        NOT NULL,
    email      text,
    created_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_user_external_logins_subject UNIQUE (provider, subject),
    CONSTRAINT uq_user_external_logins_user    UNIQUE (user_id, provider)
);

-- A fourth code purpose joins the three from 006: 'password', the code a password-less account
-- confirms before Settings lets it set its first password. Free text, no CHECK, as 006 noted.
COMMENT ON COLUMN email_tokens.purpose IS 'verify | login | reset | password';
