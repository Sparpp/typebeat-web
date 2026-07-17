#!/usr/bin/env bash
# Grant or revoke the map_reviewer role (migration 005): reviewers rank/unrank pending sets
# from the website's set pages. No reviewer UI for user management exists yet, so until then
# this is the lever.
# Usage, on the prod box:  /opt/typebeat-web/deploy/set-reviewer.sh '<username>' on|off
set -euo pipefail

[ $# -eq 2 ] && { [ "$2" = on ] || [ "$2" = off ]; } || { echo "usage: $0 '<username>' on|off" >&2; exit 1; }

WANT=$([ "$2" = on ] && echo true || echo false)

PW=$(grep POSTGRES_PASSWORD /opt/typebeat-web/deploy/.env | cut -d= -f2)

# psql's :'u' variable quoting keeps arbitrary usernames (spaces, quotes) safe in the SQL.
# The query is fed on STDIN, not via -c: psql only performs :'var' interpolation for input
# read from stdin/-f, never for a -c command string (that path sends the literal ":'u'" to
# the server and errors). docker exec -i wires our stdin through to psql.
psql_exec() {
  printf '%s\n' "$2" | docker exec -e PGPASSWORD="$PW" -i typebeat-web-postgres-1 \
    psql -U typebeat -d typebeat -v ON_ERROR_STOP=1 -v u="$1" -tA
}

MATCHED=$(psql_exec "$1" "UPDATE users SET map_reviewer = $WANT WHERE username = :'u' AND map_reviewer <> $WANT RETURNING username")

if [ -n "$MATCHED" ]; then
  echo "map_reviewer $2: $MATCHED"
elif [ "$(psql_exec "$1" "SELECT 1 FROM users WHERE username = :'u'")" = "1" ]; then
  echo "already $2: $1"
else
  echo "no such user: $1" >&2
  exit 1
fi
