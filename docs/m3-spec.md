# M3 — Real website + lazer-style uploads

Decisions and interop constraints for the M3 build. Recon ground truth (file:line-cited) lives in
per-section files under the session scratchpad
(`C:\Users\Mingda\AppData\Local\Temp\claude\C--Users-Mingda-Documents-type-beat\5563b427-f40d-4c1d-9d0f-f1902c0815bb\tasks\m3recon\`)
— builders should read the sections relevant to their module.

## Scope (user-pinned)

- Website stops being a stub: **landing page**, **beatmap browsing + downloading**, **user profile
  pages** at minimum. Server-rendered Razor Pages; no SPA.
- In-game **links out to the website** wherever lazer links to osu.ppy.sh (no in-game web overlays
  this milestone).
- **In-editor beatmap submission** compatible with lazer's BSS flow (three endpoints), implemented
  by this monolith at the `/bss` path prefix.
- Out of scope: in-game beatmap listing (osu!direct), protocol deep links ("open in type!beat"
  buttons), comments/discussions, email verification (M2), R2 migration.

## User decisions (2026-07-16)

- **Visual identity: "neon karaoke"** — deep indigo/near-black base, **violet primary accent with
  hot-magenta gradient partner** (violet-dominant; never flat pink-on-black — osu trade dress),
  rounded sans display font (OFL, e.g. Baloo 2 / Nunito), subtle glow treatments. Litmus test: a
  screenshot with the logo cropped must not be mistakable for osu.ppy.sh. No triangles motif, no
  Torus-like display face, no osu slogans/mascots.
- **Download CTA: direct from server.** The site serves a Release win-x64 zip from disk
  (`{FileStore}/downloads/`); the download page 404-falls-back to "coming soon" copy when the file
  is absent. Config key `TYPEBEAT_GAME_DOWNLOAD` names the file.

## Policy defaults (user offered veto; none given)

- **Upload gate:** `users.verified_at IS NOT NULL` required to submit. Manual lever until M2:
  `deploy/verify-user.sh <username>` (docker-exec psql UPDATE).
- **Storage:** local disk behind an `IFileStore` abstraction (`/data` volume in prod), content
  paths `files/{sha256hex}`, covers `covers/{setId}/{version}/{size}.jpg`, previews
  `previews/{setId}.mp3`, downloads `downloads/`. R2 swap later = new IFileStore impl.
- **Upload cap ~95 MB** (Cloudflare proxied request bodies cap at 100 MB on our plan). Kestrel
  default is ~28.6 MB — must be raised per-endpoint on the BSS routes only. Since backlog 189, BSS
  uploads arrive via the direct-origin host (`bss.typebeat.mingda.sh`, DNS-only, not proxied
  through Cloudflare), so this Cloudflare figure motivated the original cap but no longer bounds
  the live request path; the cap number itself is unchanged.
- **DMCA minimum:** `/legal/dmca` static page + per-set report form writing to the existing
  `reports` table; takedown = flip `beatmapsets.status` to `removed` (admin SQL for now).

## Interop constraints (iron rules)

1. **Every APIv2/BSS JSON response goes through `WireJson`** (Newtonsoft, NullValueHandling.Include)
   — never System.Text.Json. Website Razor pages are exempt (HTML).
2. **BSS wire contract** (from ppy/osu-server-beatmap-submission; full detail in recon
   `result.bss.*`):
   - `PUT /bss/beatmapsets` (JSON): `{beatmapset_id?, beatmaps_to_create, beatmaps_to_keep[],
     target, notify_on_discussion_replies}` → 200 `{beatmapset_id, beatmap_ids[], files[]}` where
     `files` = latest version's `[{filename, sha2_hash(hex)}]`, **empty array for a fresh set**
     (drives the client's replace-vs-patch branch).
   - `PUT /bss/beatmapsets/{id}` (multipart, single file part `beatmapArchive`) → 204.
   - `PATCH /bss/beatmapsets/{id}` (multipart, repeated file parts `filesChanged` + repeated form
     fields `filesDeleted`) → rebuild latest version with overlay/deletes → 204.
   - Errors: 422 `{"error": "..."}` for invariants, 403 ownership, 404 missing. Bearer auth via the
     existing TokenService (same monolith — client attaches the same token automatically).
   - Version semantics: content-addressed `files` (sha256, dedup on insert), immutable
     `set_versions` snapshots, `version_files` = per-version filename→hash manifest. **If the
     incoming file set (sha+size+filename) equals the latest version's, do NOT cut a new version**
     — just touch `updated_at`.
3. **Metadata is package-driven**: title/artist/tags/source parsed server-side from the uploaded
   `.osu` files (our native format = classic .osu text + `[Lyrics]` section). Creator is force-set
   to the uploader's username. Cross-difficulty metadata must be consistent (422 otherwise).
4. **`GET /api/v2/beatmapsets/{id}`** must exist (client's post-submit result card + status
   preselect). Shape: APIBeatmapSet with nested beatmaps — extend `BeatmapWire` and the WireCompat
   test, don't guess (see recon `result.server.dto_infra`).
5. **Covers**: generate all four osu bucket sizes at upload — `card` 400×140, `cover` 900×250,
   `list` 150×150, `slimcover` 1920×360, each with @2x — because the forked lazer client already
   consumes exactly these keys. ImageSharp; JPEG quality ~80; source = the map's background image.
6. **Preview audio**: 30 s mp3 clipped at `PreviewTime` (fallback 40% into the track) via ffmpeg
   (present in the runtime image). Missing/failed → no preview (graceful null).
7. **Difficulty on upload**: compute chars/words + effective typing time from the `[Lyrics]`
   section → CPM/WPM → `difficulty_rating` matching the client's song-select formula (port the
   arithmetic from `typebeat.Game.Rulesets.TypeBeat`'s difficulty calculation; cite where found).
8. **Cookie auth**: HttpOnly Secure SameSite=Lax cookie carrying an opaque token issued by the
   existing `TokenService` (option (a) in recon `result.server.auth_gap`); resolve via
   `ResolveAsync`. **Antiforgery required** on every state-changing website form. API bearer paths
   stay untouched.
9. Downloads: `GET /beatmapsets/{id}/download` streams the latest version package (assembled zip),
   logs to `beatmapset_downloads`, bumps the denormalized counter. Anonymous allowed.

## Chunked upload sessions (backlog 193)

Some users sit behind a middlebox that black-holes any single request to this host once its body
passes roughly 20 KB: every attempt read the same 20099 bytes of a 37714-byte PATCH and then died
on a read timeout, identically, on both the Cloudflare-proxied and the direct-origin path. No
monolithic upload above that ceiling can ever complete for them, so the two package routes get a
chunked alternative. Four bearer-authed routes, all errors in the usual `{"error": "..."}`
envelope:

- `POST /bss/beatmapsets/{id}/upload-sessions` with `{kind, content_type, total_bytes, sha256}`
  → 200 `{session_id, chunk_bytes, total_chunks, received[], expires_at}`. `kind` is `full` or
  `patch`, naming which of the two handlers will run. Gated on a verified account owning a
  submittable set, but NOT on the hourly upload limit. An identical declaration returns the
  existing session (with whatever it already holds) rather than forking one, which is how a client
  that lost its session id resumes. A NON-identical declaration for the same set and kind
  supersedes (deletes) the stale session for them, and a user already holding three live sessions
  has their oldest evicted rather than being refused: sessions can leak when a client dies between
  its last chunk and complete, and a hard cap turned three leaks into a day-long lockout. Three
  per user is thus a disk bound, and create never answers 429.
- `PUT /bss/upload-sessions/{id}/chunks/{n}`: the raw slice as the body, with its own
  `X-Chunk-Sha256`. 204 on store, and re-sending an index overwrites it.
- `GET /bss/upload-sessions/{id}`: the same shape as create, for resuming.
- `POST /bss/upload-sessions/{id}/complete`: assemble, verify the whole-payload sha256, then run
  the real upload. Answers exactly what the direct route would (204, or its 422/413).

Details that are load-bearing rather than incidental:

- **The payload is the VERBATIM multipart body** the client would otherwise have PUT or PATCHed,
  `content_type` is that body's own Content-Type header (boundary included), and complete replays
  it through the same handler the direct route uses. There is no second parser to drift.
- **`chunk_bytes` is 8192** and the chunk response always carries `Connection: close`. The ceiling
  is per TCP connection, not per request, so pooled keep-alive reuse would accumulate chunks on
  one connection and cross the limit partway through the third; the server closing bounds every
  connection at a single chunk whatever the client's pool does.
- **The hourly upload limit is consumed at complete, exactly once**, not by opening a session and
  not by any chunk: transport retries are free, submissions cost what they always did.
- Sessions live under `{FileStore}/upload-sessions/{id}/` (manifest plus chunk files, not through
  IFileStore, which has no enumeration), expire 24 hours after creation and are swept whenever a
  session is created. A session survives everything a retry can still fix (missing chunks, the
  rate limit, a verification or ownership failure) and is dropped once the payload is either
  proven corrupt or actually handed to the ingest.

## Schema migration 002 (single owner)

`users`: + `last_visit timestamptz` (touched by /me and website page loads, throttled).
`beatmapsets`: + `title_unicode text default ''`, `artist_unicode text default ''`,
  `description text default ''`, `bpm numeric`, `download_count int default 0`.
`beatmaps`: + `filename text`, `word_count int`, `char_count int`, `wpm numeric`.
`favourites`: + `created_at timestamptz default now()`.
`set_versions`: + `uploader_id bigint references users(id)`.
Search: populate `beatmapsets.search` (title/artist/unicode variants/tags/creator, weighted) in the
upload write path + a backfill UPDATE in the migration. Keep hand-SQL Dapper conventions;
migrations are embedded resources applied at startup in filename order.

## Website pages (specs in recon `result.web.*` — follow them)

- `/` landing: neon-karaoke hero, live DB stats line, Download CTA (server zip), Sign up link,
  "newest maps" card strip, footer (source/status/DMCA/terms).
- `/beatmapsets` listing: search box (tsvector + ILIKE fallback), status filter row, sort row
  (Newest · Most played · Most favourited), 2-col card grid, "show more" cursor paging. Cards per
  the recon card spec (list-cover + preview play button, title/artist/mapper, stats, status pill,
  WPM chip, favourite + download rail).
- `/beatmapsets/{id}`: cover header + scrim, stats box (Length · Words · Chars · WPM bar),
  description, tags, global leaderboard (podium + table, judgement names, no PP), download +
  favourite buttons, report link.
- `/users/{id}`: cover band + avatar, joined/last-seen, Global Rank (by total score) + stats grid
  (Total Score · Play Count · Play Time · Accuracy), grade-count row, sections: Best scores /
  Recent scores / Most played / Uploaded maps + Favourites (listing card partial reuse).
- `/download`: the game zip + "how to start" 3 steps.
- Auth pages: `/login`, `/register`, logout POST. `/legal/dmca`, report form.
- Layout: shared `_Layout` with nav (wordmark, Beatmaps, Download, Sign in/avatar chip) and footer;
  status page link https://stats.uptimerobot.com/E7XRJ7vfer stays in the footer.

## Client work (typebeat-osu — separate phase; NO commits, standing rule)

- Link-out: `OsuGame.HandleLink` stubbed cases → `OpenUrlExternally` against WebsiteUrl;
  wire ClickableAvatar/ClickableUsername; fix Settings quick-action ppy URLs.
- Submission port (~14 upstream files, trimmed wizard) with **native-encoder exporter** (never
  LegacyBeatmapExporter — it drops `[Lyrics]`); `BeatmapSubmissionServiceUrl = {root}/bss` in dev
  (derived from the local root). In production, since backlog 189, this points at the distinct
  direct-origin host `https://bss.typebeat.mingda.sh` instead, with the `/bss` path prefix
  unchanged: uploads bypass the Cloudflare-proxied main hostname because sustained upload bodies
  were dying mid-flight between the client and the CF edge (see `deploy/Caddyfile`).
- Registry identity rename: `Software\typebeat\Capabilities`, progIds `typebeat.File/.Uri`,
  scheme `typebeat://` (package extension renamed to `.typb` server-side — 2026-07-17; `.osz`
  stays importable).

## Conventions & environment

- dotnet on this machine: use the full path `"C:/Program Files/dotnet/dotnet.exe"` (bare `dotnet`
  hits a broken Store stub).
- Local DB: native PostgreSQL 17, `Host=localhost;Database=typebeat;Username=typebeat;Password=typebeat`.
- Run server: `dotnet run --project src/Typebeat.Web` (port 5089). Tests: `dotnet test
  tests/Typebeat.Web.Tests`, `dotnet test tests/Typebeat.WireCompat` (needs local Postgres).
- Config flags via `Flags.IsEnabled` (empty-string-safe). Errors to APIv2 clients use the existing
  envelope helpers. Rate limiting: in-memory speed bumps + Cloudflare, per existing pattern. Since
  backlog 189, BSS uploads go direct-origin (not proxied through Cloudflare), so on that path the
  in-memory limiter and the request body cap above are the only layers; unaffected routes are
  unchanged.
- Pushing typebeat-web `main` triggers CI **deploy to production** — commit locally during the
  build; push only at deployable checkpoints.
