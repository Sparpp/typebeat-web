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

## Monitoring

- Uptime: UptimeRobot keyword monitor on `https://typebeat.mingda.sh/health` (keyword `ok`,
  5-min interval). Public status page: <https://stats.uptimerobot.com/E7XRJ7vfer>.

## Notes

- The app auto-migrates (`Data/Migrations/*.sql`) and auto-creates the `citext` extension on
  startup, so a fresh Postgres volume needs no manual bootstrapping.
- `TYPEBEAT_BEHIND_PROXY=true` makes the app trust Caddy's `X-Forwarded-*` headers (correct
  client IPs for rate limits, `https`/`wss` scheme in emitted URLs).
