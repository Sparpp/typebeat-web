#!/usr/bin/env bash
set -euo pipefail
cd /opt/typebeat-web
docker compose --project-directory /opt/typebeat-web \
  -f deploy/compose.prod.yml --env-file deploy/.env "$@"
