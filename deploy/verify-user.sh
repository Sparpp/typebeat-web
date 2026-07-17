#!/usr/bin/env bash
# Manually verify a user account. Uploading beatmaps requires users.verified_at (BSS gate);
# email verification is an M2 follow-up, so until then this is the lever.
# Usage, on the prod box:  /opt/typebeat-web/deploy/verify-user.sh '<username>'
set -euo pipefail

[ $# -eq 1 ] || { echo "usage: $0 '<username>'" >&2; exit 1; }

PW=$(grep POSTGRES_PASSWORD /opt/typebeat-web/deploy/.env | cut -d= -f2)

# psql's :'u' variable quoting keeps arbitrary usernames (spaces, quotes) safe in the SQL.
# The query is fed on STDIN, not via -c: psql only performs :'var' interpolation for input
# read from stdin/-f, never for a -c command string (that path sends the literal ":'u'" to
# the server and errors). docker exec -i wires our stdin through to psql.
psql_exec() {
  printf '%s\n' "$2" | docker exec -e PGPASSWORD="$PW" -i typebeat-web-postgres-1 \
    psql -U typebeat -d typebeat -v ON_ERROR_STOP=1 -v u="$1" -tA
}

MATCHED=$(psql_exec "$1" "UPDATE users SET verified_at = now() WHERE username = :'u' AND verified_at IS NULL RETURNING username")

if [ -n "$MATCHED" ]; then
  echo "verified: $MATCHED"
elif [ "$(psql_exec "$1" "SELECT 1 FROM users WHERE username = :'u'")" = "1" ]; then
  echo "already verified: $1"
else
  echo "no such user: $1" >&2
  exit 1
fi
