#!/usr/bin/env bash
# Nightly backups on the prod box: Postgres dump + uploaded-files snapshot, 14-day retention.
# This is the box's existing pg_dump script (previously living only in /opt/typebeat-web),
# committed to the repo and extended to also capture the /data volume (M3 uploads).
# LOCAL ONLY for now — an offsite copy (R2) is a follow-up needing creds.
#
# Run via root's crontab on the box, e.g.:
#   17 3 * * * /opt/typebeat-web/deploy/backup.sh >> /var/log/typebeat-backup.log 2>&1
set -euo pipefail

DIR=/opt/typebeat-web/backups
PW=$(grep POSTGRES_PASSWORD /opt/typebeat-web/deploy/.env | cut -d= -f2)
TS=$(date -u +%Y%m%dT%H%M%SZ)

mkdir -p "$DIR"

# Database.
docker exec -e PGPASSWORD="$PW" typebeat-web-postgres-1 \
  pg_dump -U typebeat -d typebeat -Fc | gzip > "$DIR/typebeat_$TS.dump.gz"

# Uploaded beatmap packages/covers/previews: the appdata volume, mounted at /data in the app
# container (TYPEBEAT_FILE_ROOT). tar ships with the aspnet base image (Debian).
docker exec typebeat-web-app-1 tar czf - -C / data > "$DIR/appdata_$TS.tar.gz"

# keep 14 days
find "$DIR" -name 'typebeat_*.dump.gz' -mtime +14 -delete
find "$DIR" -name 'appdata_*.tar.gz' -mtime +14 -delete

echo "backup ok: typebeat_$TS.dump.gz appdata_$TS.tar.gz"
