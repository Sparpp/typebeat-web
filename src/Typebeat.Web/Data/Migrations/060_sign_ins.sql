-- Sign-in history: where each account is used from (Auth/SignInHistory.cs). One row per
-- (account, address, client instance), refreshed while it stays in use, so an account that plays
-- from one place every day is one row. The client instance is a random id a game install
-- creates once (x-client-instance) or a long-lived website cookie; '' when neither was sent.
CREATE TABLE sign_ins
(
    user_id    bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    ip         text        NOT NULL,
    instance   text        NOT NULL DEFAULT '',
    user_agent text        NOT NULL DEFAULT '',
    first_seen timestamptz NOT NULL DEFAULT now(),
    last_seen  timestamptz NOT NULL DEFAULT now(),
    hits       integer     NOT NULL DEFAULT 1,
    PRIMARY KEY (user_id, ip, instance)
);

CREATE INDEX ix_sign_ins_ip ON sign_ins (ip);
CREATE INDEX ix_sign_ins_instance ON sign_ins (instance) WHERE instance <> '';

-- An account first seen at an address or instance a restricted account also used. Raised for an
-- operator to look at (admin CLI: flags, alts); nothing acts on it automatically.
CREATE TABLE sign_in_reviews
(
    id            bigserial PRIMARY KEY,
    user_id       bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    other_user_id bigint      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    via           text        NOT NULL CHECK (via IN ('ip', 'instance')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    reviewed_at   timestamptz,
    UNIQUE (user_id, other_user_id, via)
);
