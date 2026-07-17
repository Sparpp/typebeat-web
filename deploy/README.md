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

## Monitoring

- Uptime: UptimeRobot keyword monitor on `https://typebeat.mingda.sh/health` (keyword `ok`,
  5-min interval). Public status page: <https://stats.uptimerobot.com/E7XRJ7vfer>.

## Notes

- The app auto-migrates (`Data/Migrations/*.sql`) and auto-creates the `citext` extension on
  startup, so a fresh Postgres volume needs no manual bootstrapping.
- `TYPEBEAT_BEHIND_PROXY=true` makes the app trust Caddy's `X-Forwarded-*` headers (correct
  client IPs for rate limits, `https`/`wss` scheme in emitted URLs).
