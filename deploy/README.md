# Deploy

Single Hetzner box, Docker Compose. Caddy terminates TLS for `typebeat.mingda.sh` and
reverse-proxies to the ASP.NET app; Postgres lives on the private compose network.

## First deploy

1. DNS: an `A` record `typebeat.mingda.sh -> <server ip>` must resolve (DNS-only at first, so
   Caddy's ACME HTTP challenge reaches the box; switch Cloudflare to proxied afterwards).
2. On the box, in the repo root (`/opt/typebeat-web`):
   ```
   docker compose -f deploy/compose.prod.yml up -d --build
   ```
   `deploy/.env` (git-ignored) must define `POSTGRES_PASSWORD`. Compose reads it automatically
   when named `.env` in the compose file's directory, or pass `--env-file deploy/.env`.
3. Watch Caddy obtain the certificate:
   ```
   docker compose -f deploy/compose.prod.yml logs -f caddy
   ```
4. Verify: `curl https://typebeat.mingda.sh/health` returns `ok`.

## Update

```
git pull   # once the repo is under version control
docker compose -f deploy/compose.prod.yml up -d --build
```

## Takedown runbook (DMCA / removed sets)

Flipping `beatmapsets.status` alone is NOT the whole takedown; stored artifacts and edge
caches keep serving until you finish all four steps:

1. Flip the status (origin pages/API/media start 404ing immediately):
   ```
   docker exec -it typebeat-web-postgres-1 psql -U typebeat -d typebeat \
     -c "UPDATE beatmapsets SET status = 'removed' WHERE id = <SET_ID>;"
   ```
2. Delete the set's served artifacts from the appdata volume (covers, preview, assembled
   packages; the content-addressed `files/` blobs stay, they are not directly addressable):
   ```
   docker exec typebeat-web-app-1 sh -c \
     "rm -rf /data/covers/<SET_ID> /data/previews/<SET_ID>.mp3 /data/packages/<SET_ID>"
   ```
3. Purge the Cloudflare cache for the set's media URLs (dashboard → Caching → Purge by URL:
   `https://typebeat.mingda.sh/covers/<SET_ID>/*` and `/previews/<SET_ID>.mp3`), or purge
   everything for a single-set site. Covers/previews are served with `max-age=86400`, so even
   without a purge every cache ages out within a day; the purge closes that window.
4. If the takedown was a DMCA notice, note the set id + notice reference in the report row
   (`reports` table) so repeat-infringer tracking works.

Browser caches cannot be purged remotely; the one-day `max-age` bounds them.

## Origin-side ingest (upload dies at the edge)

When a submission never reaches Kestrel (the Cloudflare edge or the user's own network path
drops the body mid-flight), the user cannot retry their way out of it: nothing on our side ever
saw the request. The workaround is to run the upload from the box itself, straight at the app
container over the compose network, holding a short-lived token that acts AS that user.

**First confirm the request really never arrived.** Every server-side rejection logs under the
`BssUpload` category:

```
docker compose -f deploy/compose.prod.yml logs --since 24h app | grep BssUpload
```

Lines naming the user's set (422 / 413 / missing `beatmapArchive`) mean the app DID receive the
upload and rejected it: that is a normal rejection, fix the package with the user rather than
ingesting around it. **NOTHING** for the window in which the user says they tried, while they
report a transport failure client-side, is this runbook's case.

1. Get the package. Ask the user for the exact `.osz` their client tried to submit, over any
   channel that is not our origin (DM, drive link). Land it on the box, then verify it before
   touching the API:
   ```
   ls -l /root/ingest/package.osz                # must be under 95 MiB (PackageValidator cap)
   sha256sum /root/ingest/package.osz            # cross-check against the user's own hash
   unzip -t /root/ingest/package.osz             # archive must be intact
   unzip -p /root/ingest/package.osz '*.osu' | grep -iE 'BeatmapID|BeatmapSetID'
   ```
   The ids from the last line are what the server validates the package against, so they tell
   you which set and difficulty slots this package expects to land in.

2. Check whether those slots already exist. In an edge failure they almost always do: the
   client's JSON step is tiny and succeeds, only the multipart body dies.
   ```
   docker exec -it typebeat-web-postgres-1 psql -U typebeat -d typebeat -c \
     "SELECT bs.id AS set_id, bs.status, b.id AS beatmap_id, b.filename
        FROM beatmapsets bs LEFT JOIN beatmaps b ON b.set_id = bs.id
       WHERE bs.owner_id = (SELECT id FROM users WHERE username = '<USERNAME>')
       ORDER BY bs.id, b.id;"
   ```
   Ids matching the package's embedded ids = slots are allocated, **skip step 4**.

3. Issue a short-lived token for the user. The admin CLI is not in the published app image, so
   run it in a throwaway SDK container (that image is already on the box as the app's build
   stage) joined to the compose network. `mkdir` first: NuGet hard-fails restore when the local
   feed declared in `nuget.config` is absent, and `external/packages` is not in the checkout.
   ```
   mkdir -p /opt/typebeat-web/external/packages
   set -a; . /opt/typebeat-web/deploy/.env; set +a
   docker run --rm --network typebeat-web_default -v /opt/typebeat-web:/repo -w /repo \
     -e TYPEBEAT_DB="Host=postgres;Port=5432;Database=typebeat;Username=typebeat;Password=$POSTGRES_PASSWORD" \
     mcr.microsoft.com/dotnet/sdk:10.0 \
     dotnet run --project tools/admin -- issue-token <USERNAME> 30
   ```
   It prints the bearer token on its own line plus the token id to revoke in step 6. 30 minutes
   is the default and 240 the maximum; take the shortest window that fits the op. Confirm the
   network name with `docker network ls --filter name=typebeat` if the run cannot resolve
   `postgres`. Export the token for the curls below: `TOKEN=<paste>`.

4. **Only if step 2 showed no allocated slots**, create or re-target the set. `app:8080` is the
   app's container-internal address (`ASPNETCORE_URLS` in `compose.prod.yml`, the same target
   `deploy/Caddyfile` proxies to), so this bypasses Cloudflare entirely.
   ```
   docker run --rm --network typebeat-web_default curlimages/curl:latest \
     -sS -i -X PUT http://app:8080/bss/beatmapsets \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"beatmapset_id":null,"beatmaps_to_create":1,"beatmaps_to_keep":[],"target":"Pending","explicit":false,"notify_on_discussion_replies":false}'
   ```
   200 returns `{beatmapset_id, beatmap_ids, files}`. Semantics worth getting right:
   - `beatmapset_id`: `null` creates a fresh set (status `hidden` until the first successful
     upload publishes it); an id re-targets that existing owned set.
   - `beatmaps_to_create`: how many new difficulty ids to allocate.
   - `beatmaps_to_keep`: the existing difficulty ids that stay in the set. Any live difficulty
     NOT listed is dropped from the current version (the row survives for the scores FK, its
     `filename` goes NULL), so an omission silently unpublishes a difficulty. List every id the
     package contains. `keep + create` must land in 1..128.
   - The returned ids must match the `BeatmapID`/`BeatmapSetID` baked into the package's `.osu`
     files or step 5 will 422. That is exactly why this step is normally skipped: the user's
     client already ran it and built the zip around the ids it got back.

5. Upload the full package (`beatmapArchive` is the field name the server looks for):
   ```
   docker run --rm --network typebeat-web_default -v /root/ingest:/pkg:ro curlimages/curl:latest \
     -sS -i -X PUT http://app:8080/bss/beatmapsets/<SET_ID> \
     -H "Authorization: Bearer $TOKEN" \
     -F "beatmapArchive=@/pkg/package.osz"
   ```
   **204 No Content** is success: the version was cut and covers/preview/package artifacts are
   published. A 422 body carries the same message the in-editor wizard would have shown (take it
   back to the user), 403 means the token's user does not own the set, 429 is the rate limit
   below. Then verify:
   ```
   docker exec -it typebeat-web-postgres-1 psql -U typebeat -d typebeat -c \
     "SELECT id, status, updated_at FROM beatmapsets WHERE id = <SET_ID>;"
   curl -sS -o /dev/null -w '%{http_code}\n' https://typebeat.mingda.sh/beatmapsets/<SET_ID>
   ```

6. Revoke the token. Do this even if it is about to expire on its own:
   ```
   docker run --rm --network typebeat-web_default -v /opt/typebeat-web:/repo -w /repo \
     -e TYPEBEAT_DB="Host=postgres;Port=5432;Database=typebeat;Username=typebeat;Password=$POSTGRES_PASSWORD" \
     mcr.microsoft.com/dotnet/sdk:10.0 \
     dotnet run --project tools/admin -- revoke-token <TOKEN_ID>
   ```

Caveats:

- **This acts AS the user.** The set is owned by them, the creator field is force-set to their
  username, and nothing records that a maintainer pushed the bytes. Only ever run it on the
  package they sent you, with their say-so.
- **Rate limit: 12 uploads per hour per user** on the two upload routes, in-memory per app
  process. The user's failed attempts never reached the app so they did not count, but ask them
  to stop retrying while you work, and note the window resets when the app container restarts.
- **Body cap: 100 MB** on the upload routes, with the package itself capped at 95 MiB. Going
  through `app:8080` bypasses Cloudflare's 100 MB proxied-body limit but neither of these.
- The normal gates still apply: an unverified account 422s, a non-owner 403s, and a set already
  taken down (`status = 'removed'`) 422s. The token grants no extra authority.
- The wire contract for these endpoints lives in `docs/m3-spec.md` (iron rule 2, lines 44-56).
  If anything above disagrees with it, that file wins. `PATCH /bss/beatmapsets/{id}` exists for
  delta uploads and is deliberately not used here: for this op the full package is what you have.

## Backups

`deploy/backup.sh`: LOCAL only (offsite/R2 copy is a follow-up):

- `backup.sh db`: compressed `pg_dump`, meant nightly, 14-day retention.
- `backup.sh appdata`: tar of the `/data` uploads volume, meant WEEKLY, newest 2 tars kept
  (count-based, so skipped weeks never age out the only copies).
- Both modes skip (and log) instead of running when the backup filesystem has <10 GB free;
  the 75 GB disk is shared with Postgres, and a skipped backup beats a wedged database.
- Assembled per-version download packages are pruned by the server itself (PackageIngest
  keeps the latest 2 per set; older ones stay reconstructible from the content-addressed
  blobs), so `/data` does not grow ~2x per re-submission forever.

**Nothing installs the schedule automatically; the box's root crontab starts EMPTY.**
After the first M3 deploy is up (the appdata job docker-execs into the app container, which
must mount `/data`), run `crontab -e` as root and add exactly:

```
17 3 * * *  /opt/typebeat-web/deploy/backup.sh db      >> /var/log/typebeat-backup.log 2>&1
47 3 * * 0  /opt/typebeat-web/deploy/backup.sh appdata >> /var/log/typebeat-backup.log 2>&1
```

Verify with `crontab -l`, and after the first scheduled night check
`/var/log/typebeat-backup.log` for `backup ok:` lines.

## Monitoring

- Uptime: UptimeRobot keyword monitor on `https://typebeat.mingda.sh/health` (keyword `ok`,
  5-min interval). Public status page: <https://stats.uptimerobot.com/E7XRJ7vfer>.

## Enabling the game-download CTA

The `/download` page shows "coming soon" until both of these are true:

1. The release zip exists in the uploads volume:
   ```
   docker cp typebeat-win-x64.zip typebeat-web-app-1:/data/downloads/typebeat-win-x64.zip
   ```
   (create the `downloads/` directory first if needed).
2. `deploy/.env` on the box sets `TYPEBEAT_GAME_DOWNLOAD=typebeat-win-x64.zip`, then
   `docker compose -f deploy/compose.prod.yml up -d` recreates the app container so the
   variable (forwarded by compose.prod.yml's environment block) reaches the app.

Each platform card is independent and has its own key; a platform with no key set (or no file
stored) just shows "coming soon" rather than a dead link. The Linux and macOS artifacts are
published by the game repo's `build-linux.yml` / `build-macos.yml` workflows under stable,
un-versioned names, so their keys are set once and never change:

| Key | Card | Value the workflow publishes |
|-----|------|------------------------------|
| `TYPEBEAT_GAME_DOWNLOAD` | Windows | (whatever the Windows installer is named) |
| `TYPEBEAT_GAME_DOWNLOAD_LINUX` | Linux | `typebeat-linux.AppImage` |
| `TYPEBEAT_GAME_DOWNLOAD_MACOS` | macOS | `typebeat-macos.pkg` |

All three must be declared in `compose.prod.yml`'s `environment:` block: compose does not forward
undeclared host env vars, so an `.env` entry with no matching declaration is silently ignored.

## Notes

- The app auto-migrates (`Data/Migrations/*.sql`) and auto-creates the `citext` extension on
  startup, so a fresh Postgres volume needs no manual bootstrapping.
- `TYPEBEAT_BEHIND_PROXY=true` makes the app trust Caddy's `X-Forwarded-*` headers (correct
  client IPs for rate limits, `https`/`wss` scheme in emitted URLs).
