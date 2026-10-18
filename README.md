# typebeat-web

o/

The website and server side of [type!beat](https://github.com/Sparpp/typebeat)

Accounts, score submission, leaderboards, beatmap hosting and a browser version of the game, all behind [typebeat.sh](https://typebeat.sh/)

[![Website](https://img.shields.io/badge/website-typebeat.sh-blue)](https://typebeat.sh/)
[![Discord](https://img.shields.io/badge/discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/yAR2PDPgBB)
[![YouTube](https://img.shields.io/badge/youtube-@typebeatgame-FF0000?logo=youtube&logoColor=white)](https://www.youtube.com/@typebeatgame)

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (.NET 10) and a
PostgreSQL database.

```
git clone https://github.com/Sparpp/typebeat-web
cd typebeat-web
docker compose -f compose.dev.yml up -d
dotnet run --project src/Typebeat.Web
```

The site then answers on `http://localhost:5089`, which is the endpoint a debug
build of the game client talks to. Point the app at a different database with
`TYPEBEAT_DB`; the schema is applied on startup from `src/Typebeat.Web/Data/Migrations`.

Or open `typebeat-web.slnx` in your IDE.

The tests need more than the SDK:

- A local PostgreSQL superuser `postgres` / `postgres` on `localhost:5432`, as in
  `.github/workflows/ci.yml`. The website and wire-compat tests create their own
  databases there.
- The tests that prove the website and the game client still agree on scoring
  compile the game as well, so they need a checkout of
  [`typebeat`](https://github.com/Sparpp/typebeat) beside this one (or the
  `external/typebeat-osu` submodule).
- The game in turn needs the `typebeat.Game.Resources` package. It is too large
  for git, so CI packs it from the private `external/type-beat-assets` submodule
  into the local feed in `nuget.config` (the "Pack typebeat.Game.Resources" step
  of the workflow); without that package, or access to that submodule, the game
  and so the wire-compat project will not restore.
- Node, for the JS harnesses (the tests that use one are skipped without it).
- ffmpeg is optional: the preview tests degrade without it.

```
git submodule update --init external/typebeat-osu
dotnet test
```

## Layout

| Path | What |
|---|---|
| `src/Typebeat.Web` | The app: the API the game client speaks, the website (Razor Pages), the browser player, scoring and difficulty rating |
| `tests/Typebeat.Web.Tests` | The test suite |
| `tests/Typebeat.WireCompat` | Compiles the game client against the server and pins that the two agree on the wire and on the mirrored formulas its tests name (not every mirrored formula has a pin there yet) |
| `tools/` | Operator tools: seeding maps, admin actions, score recalculation and reprice reports |
| `deploy/` | Production deployment: Docker Compose, Caddy, backups, runbook |
| `docs/` | The performance points spec and other design notes |

## Licence

typebeat-web is MIT-licensed; see [LICENCE](LICENCE).
