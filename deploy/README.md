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

Flipping `beatmapsets.status` alone is NOT the whole takedown — stored artifacts and edge
caches keep serving until you finish all four steps:

1. Flip the status (origin pages/API/media start 404ing immediately):
   ```
   docker exec -it typebeat-web-postgres-1 psql -U typebeat -d typebeat \
     -c "UPDATE beatmapsets SET status = 'removed' WHERE id = <SET_ID>;"
   ```
2. Delete the set's served artifacts from the appdata volume (covers, preview, assembled
   packages — the content-addressed `files/` blobs stay; they are not directly addressable):
   ```
   docker exec typebeat-web-app-1 sh -c \
     "rm -rf /data/covers/<SET_ID> /data/previews/<SET_ID>.mp3 /data/packages/<SET_ID>"
   ```
3. Purge the Cloudflare cache for the set's media URLs (dashboard → Caching → Purge by URL:
   `https://typebeat.mingda.sh/covers/<SET_ID>/*` and `/previews/<SET_ID>.mp3`), or purge
   everything for a single-set site. Covers/previews are served with `max-age=86400`, so even
   without a purge every cache ages out within a day — the purge closes that window.
4. If the takedown was a DMCA notice, note the set id + notice reference in the report row
   (`reports` table) so repeat-infringer tracking works.

Browser caches cannot be purged remotely; the one-day `max-age` bounds them.

## Backups

`deploy/backup.sh` — LOCAL only (offsite/R2 copy is a follow-up):

- `backup.sh db`: compressed `pg_dump`, meant nightly, 14-day retention.
- `backup.sh appdata`: tar of the `/data` uploads volume, meant WEEKLY, newest 2 tars kept
  (count-based, so skipped weeks never age out the only copies).
- Both modes skip (and log) instead of running when the backup filesystem has <10 GB free —
  the 75 GB disk is shared with Postgres, and a skipped backup beats a wedged database.
- Assembled per-version download packages are pruned by the server itself (PackageIngest
  keeps the latest 2 per set; older ones stay reconstructible from the content-addressed
  blobs), so `/data` does not grow ~2x per re-submission forever.

**Nothing installs the schedule automatically — the box's root crontab starts EMPTY.**
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

## Notes

- The app auto-migrates (`Data/Migrations/*.sql`) and auto-creates the `citext` extension on
  startup, so a fresh Postgres volume needs no manual bootstrapping.
- `TYPEBEAT_BEHIND_PROXY=true` makes the app trust Caddy's `X-Forwarded-*` headers (correct
  client IPs for rate limits, `https`/`wss` scheme in emitted URLs).
