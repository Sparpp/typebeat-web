-- typebeat-web migration 001: initial schema (M1 scope + forward slots for M3 uploads).
-- Conventions: bigserial ids, timestamptz everywhere, snake_case, no ORM-generated DDL.

CREATE EXTENSION IF NOT EXISTS citext;

CREATE TABLE users
(
    id            bigserial PRIMARY KEY,
    username      citext      NOT NULL UNIQUE,
    email         citext      NOT NULL UNIQUE,
    password_hash text        NOT NULL,
    country_code  char(2)     NOT NULL DEFAULT 'XX',
    avatar_key    text,
    verified_at   timestamptz,
    restricted    boolean     NOT NULL DEFAULT false,
    is_admin      boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE oauth_tokens
(
    id                 bigserial PRIMARY KEY,
    user_id            bigint      NOT NULL REFERENCES users (id),
    access_hash        bytea       NOT NULL UNIQUE,
    refresh_hash       bytea       NOT NULL UNIQUE,
    access_expires_at  timestamptz NOT NULL,
    refresh_expires_at timestamptz NOT NULL,
    -- Refresh consumption: each use of a refresh token INSERTS a new row (the old access
    -- token lives out its natural expiry). consumed_at marks first use; within a short grace
    -- window after it, the same refresh token may be used again (each use minting a fresh
    -- pair), so a client retry racing a successful rotation never gets logged out.
    consumed_at        timestamptz,
    revoked_at         timestamptz,
    created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_oauth_tokens_user ON oauth_tokens (user_id);

CREATE TABLE email_tokens
(
    id         bigserial PRIMARY KEY,
    user_id    bigint      NOT NULL REFERENCES users (id),
    token_hash bytea       NOT NULL UNIQUE,
    purpose    text        NOT NULL, -- 'verify' | 'reset'
    expires_at timestamptz NOT NULL,
    used_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Client builds observed at score-token creation. Record-don't-reject: unknown builds are
-- registered on sight; blocking a build makes token creation fail with "outdated client"
-- and is the retroactive-invalidation lever (admin CLI bulk-unranks its scores).
CREATE TABLE builds
(
    id            bigserial PRIMARY KEY,
    version_hash  text        NOT NULL UNIQUE,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    blocked       boolean     NOT NULL DEFAULT false
);

CREATE TABLE beatmapsets
(
    id              bigserial PRIMARY KEY,
    owner_id        bigint      NOT NULL REFERENCES users (id),
    title           text        NOT NULL DEFAULT '',
    artist          text        NOT NULL DEFAULT '',
    source          text        NOT NULL DEFAULT '',
    tags            text        NOT NULL DEFAULT '',
    status          text        NOT NULL DEFAULT 'public' CHECK (status IN ('public', 'hidden', 'removed')),
    has_video       boolean     NOT NULL DEFAULT false,
    cover_key       text,
    preview_key     text,
    current_version integer     NOT NULL DEFAULT 1,
    play_count      integer     NOT NULL DEFAULT 0,
    favourite_count integer     NOT NULL DEFAULT 0,
    search          tsvector,
    submitted_at    timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_beatmapsets_search ON beatmapsets USING gin (search);

CREATE TABLE beatmaps
(
    id                bigserial PRIMARY KEY,
    set_id            bigint           NOT NULL REFERENCES beatmapsets (id),
    version_name      text             NOT NULL DEFAULT 'type!beat',
    ruleset_id        smallint         NOT NULL DEFAULT 0,
    -- MD5 of the FINAL .osu bytes (after BeatmapID/BeatmapSetID injection); byte-identical
    -- to what the client computes on import. The beatmap_hash identity contract.
    checksum_md5      char(32)         NOT NULL UNIQUE,
    total_length_s    double precision NOT NULL DEFAULT 0,
    drain_length_s    double precision NOT NULL DEFAULT 0,
    difficulty_rating double precision NOT NULL DEFAULT 0,
    play_count        integer          NOT NULL DEFAULT 0
);

CREATE INDEX ix_beatmaps_set ON beatmaps (set_id);

-- Content-addressed, write-once file store (R2 objects keyed files/{sha256}). M3.
CREATE TABLE files
(
    sha256        bytea       PRIMARY KEY,
    size          bigint      NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE set_versions
(
    id          bigserial PRIMARY KEY,
    set_id      bigint      NOT NULL REFERENCES beatmapsets (id),
    version_no  integer     NOT NULL,
    package_key text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (set_id, version_no)
);

CREATE TABLE version_files
(
    version_id bigint NOT NULL REFERENCES set_versions (id),
    sha256     bytea  NOT NULL REFERENCES files (sha256),
    filename   text   NOT NULL,
    PRIMARY KEY (version_id, filename)
);

-- Two-phase score submission (osu's shape): POST creates a token, PUT completes it.
-- created_at is the server wall-clock anchor for the minimum-play-time check.
CREATE TABLE score_tokens
(
    id           bigserial PRIMARY KEY,
    user_id      bigint      NOT NULL REFERENCES users (id),
    beatmap_id   bigint      NOT NULL REFERENCES beatmaps (id),
    ruleset_id   smallint    NOT NULL DEFAULT 0,
    beatmap_hash char(32)    NOT NULL,
    build_id     bigint      NOT NULL REFERENCES builds (id),
    score_id     bigint,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_score_tokens_user ON score_tokens (user_id);

CREATE TABLE scores
(
    id                 bigserial PRIMARY KEY,
    user_id            bigint           NOT NULL REFERENCES users (id),
    beatmap_id         bigint           NOT NULL REFERENCES beatmaps (id),
    ruleset_id         smallint         NOT NULL DEFAULT 0,
    total_score        bigint           NOT NULL,
    accuracy           double precision NOT NULL,
    max_combo          integer          NOT NULL,
    rank               text             NOT NULL,
    passed             boolean          NOT NULL,
    -- ranked=false excludes from leaderboards (failed validation, blocked build, unranked by admin).
    ranked             boolean          NOT NULL DEFAULT true,
    -- preserve=false rows are prunable (osu's model: non-passed, non-personal-best).
    preserve           boolean          NOT NULL DEFAULT true,
    processed_version  smallint         NOT NULL DEFAULT 1,
    mods               jsonb            NOT NULL DEFAULT '[]',
    statistics         jsonb            NOT NULL DEFAULT '{}',
    maximum_statistics jsonb            NOT NULL DEFAULT '{}',
    build_id           bigint           REFERENCES builds (id),
    started_at         timestamptz,
    ended_at           timestamptz      NOT NULL DEFAULT now()
);

-- Leaderboard query: best-per-user via DISTINCT ON over this index.
CREATE INDEX ix_scores_leaderboard ON scores (beatmap_id, ranked, total_score DESC);
CREATE INDEX ix_scores_user_recent ON scores (user_id, ended_at DESC);

CREATE TABLE user_stats
(
    user_id     bigint PRIMARY KEY REFERENCES users (id),
    play_count  integer NOT NULL DEFAULT 0,
    total_score bigint  NOT NULL DEFAULT 0,
    play_time_s bigint  NOT NULL DEFAULT 0,
    hit_counts  jsonb   NOT NULL DEFAULT '{}'
);

CREATE TABLE favourites
(
    user_id bigint NOT NULL REFERENCES users (id),
    set_id  bigint NOT NULL REFERENCES beatmapsets (id),
    PRIMARY KEY (user_id, set_id)
);

CREATE TABLE reports
(
    id          bigserial PRIMARY KEY,
    set_id      bigint      NOT NULL REFERENCES beatmapsets (id),
    reporter_id bigint      REFERENCES users (id),
    reason      text        NOT NULL,
    kind        text        NOT NULL DEFAULT 'user_report' CHECK (kind IN ('user_report', 'dmca')),
    status      text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'resolved', 'dismissed')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz
);

-- Audit trail for every moderation act; also the repeat-infringer strike source.
CREATE TABLE moderation_actions
(
    id         bigserial PRIMARY KEY,
    actor_id   bigint      NOT NULL REFERENCES users (id),
    set_id     bigint      REFERENCES beatmapsets (id),
    action     text        NOT NULL,
    note       text        NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE beatmapset_downloads
(
    user_id bigint      NOT NULL REFERENCES users (id),
    set_id  bigint      NOT NULL REFERENCES beatmapsets (id),
    at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_downloads_user_at ON beatmapset_downloads (user_id, at);
