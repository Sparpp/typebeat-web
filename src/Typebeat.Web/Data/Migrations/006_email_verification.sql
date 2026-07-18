-- typebeat-web migration 006: email verification codes (M2).
--
-- email_tokens now also carries short-lived 6-digit numeric CODES (not just the opaque
-- reset/verify links slotted in 001). token_hash holds SHA-256 of the plaintext code; a code
-- is single-use (used_at) and time-boxed (expires_at). The new `attempts` column is the
-- per-code wrong-guess counter that lets VerifyAsync burn a code after too many misses
-- (brute-force ceiling: 6 digits = 1e6 space, <=5 guesses per code, one active code per
-- (user,purpose), 60s resend cooldown + hourly cap — see EmailCodeService).
--
-- `purpose` gains a third value 'login' alongside the original 'verify' | 'reset'. The column
-- is free text (no CHECK constraint), so no constraint change is needed — only the intent comment.

COMMENT ON COLUMN email_tokens.purpose IS 'verify | login | reset';

ALTER TABLE email_tokens
    ADD COLUMN attempts smallint NOT NULL DEFAULT 0;

-- One active (unused) code per (user, purpose) is the invariant IssueAsync maintains; this
-- partial index makes "find/invalidate the user's live code of this purpose" a point lookup.
CREATE INDEX ix_email_tokens_user_purpose
    ON email_tokens (user_id, purpose)
    WHERE used_at IS NULL;
