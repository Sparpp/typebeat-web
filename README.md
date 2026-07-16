# typebeat-web

The entire server side of [type!beat](../typebeat-osu): the osu-web APIv2 dialect the game
client speaks (accounts, score submission, per-map leaderboards, beatmap lookup — M1 scope),
plus (later milestones) beatmap hosting/downloads and the public website.

Architecture, wire contract, and milestone plan: `../type!beat/docs/online-architecture.md`.

## Stack

- ASP.NET Core (.NET 10), minimal APIs; Razor Pages for the website from M3.
- **Newtonsoft.Json for every wire response** — the client deserializes with Newtonsoft
  attribute semantics; see `Wire/Wire.cs`. Never serialize an API response with
  System.Text.Json.
- PostgreSQL 16, Dapper + hand-written SQL. Migrations are plain SQL files embedded in the
  binary (`Data/Migrations/*.sql`), applied at startup in filename order.
- Opaque bearer tokens (SHA-256 at rest), refresh rotation with a 60s grace window.

## Dev quickstart

```
docker compose -f compose.dev.yml up -d     # postgres:16 on localhost:5432
dotnet run --project src/Typebeat.Web       # http://localhost:5089 (matches the client's dev endpoint)
```

A debug build of the game (or any build with `TYPEBEAT_API_URL=http://localhost:5089`)
then talks to this instance.

## Layout

- `src/Typebeat.Web` — the app. `Endpoints/` one static module per wire endpoint group;
  `Wire/` response conventions + DTOs; `Auth/` token + password services; `Data/` Db + SQL.
- `tools/seed` — packages a lyriclab `.osz` for online play: assigns IDs, injects them into
  each `.osu`'s `[Metadata]`, hashes the FINAL bytes (the beatmap_hash identity contract),
  inserts DB rows, emits the finalized `.osz` to import into the client.
- `tools/admin` — block/unblock builds, unrank scores, restrict users.
- `tests/Typebeat.Web.Tests` — unit tests (NUnit).

## Iron rules

1. Beatmap bytes are hashed once, after ID injection, and never rewritten afterwards;
   whatever is served for download must be exactly the hashed bytes.
2. Download endpoints 302-redirect to object storage; server code never streams `.osz` bytes.
3. Score-submit error strings (`invalid token`, `expired token`,
   `invalid or missing beatmap_hash`, `outdated client`) are exact-matched by the client —
   never reword.
4. Never branch on the client's `x-api-version` header; accept and log any positive integer.
