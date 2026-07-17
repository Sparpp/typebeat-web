#!/usr/bin/env bash
# Prod-box backups: Postgres dump NIGHTLY, /data uploads tar WEEKLY, local retention, and a
# free-space guard. LOCAL ONLY for now — an offsite copy (R2) is a follow-up needing creds.
#
# Nothing schedules this automatically (the box's root crontab starts EMPTY). Install EXACTLY
# these two lines with `crontab -e` as root, after the M3 compose is up (the appdata job needs
# the app container to mount /data — before that, its docker-exec tar step fails):
#
#   17 3 * * *  /opt/typebeat-web/deploy/backup.sh db      >> /var/log/typebeat-backup.log 2>&1
#   47 3 * * 0  /opt/typebeat-web/deploy/backup.sh appdata >> /var/log/typebeat-backup.log 2>&1
#
# Disk math (75 GB disk shared by pgdata + appdata + backups): nightly FULL tars of /data with
# 14-day retention amplified user-controlled upload data ~14x — a few GB of uploads could fill
# the filesystem Postgres lives on. Weekly tars with a 2-copy retention cap that at ~2x /data,
# the guard below refuses to start any backup with <10 GB free (a skipped backup beats a
# wedged database), and the server itself prunes assembled per-version download packages
# beyond the latest two per set (PackageIngest — they are reassemblable from the
# content-addressed blobs).
set -euo pipefail

MODE=${1:-db}
DIR=/opt/typebeat-web/backups
PW=$(grep POSTGRES_PASSWORD /opt/typebeat-web/deploy/.env | cut -d= -f2)
TS=$(date -u +%Y%m%dT%H%M%SZ)
MIN_FREE_GB=10

mkdir -p "$DIR"

# Free-space guard: skip + log instead of filling the disk Postgres runs on.
FREE_GB=$(df -BG --output=avail "$DIR" | tail -n 1 | tr -dc '0-9')
if [ "${FREE_GB:-0}" -lt "$MIN_FREE_GB" ]; then
  echo "backup SKIPPED ($MODE): ${FREE_GB:-?} GB free < ${MIN_FREE_GB} GB minimum — free space, then re-run"
  exit 1
fi

case "$MODE" in
  db)
    # Nightly database dump, 14-day retention.
    docker exec -e PGPASSWORD="$PW" typebeat-web-postgres-1 \
      pg_dump -U typebeat -d typebeat -Fc | gzip > "$DIR/typebeat_$TS.dump.gz"

    find "$DIR" -name 'typebeat_*.dump.gz' -mtime +14 -delete

    echo "backup ok: typebeat_$TS.dump.gz"
    ;;

  appdata)
    # Weekly snapshot of uploaded beatmap blobs/covers/previews/packages: the appdata volume,
    # mounted at /data in the app container (TYPEBEAT_FILE_ROOT). tar ships with the aspnet
    # base image (Debian).
    docker exec typebeat-web-app-1 tar czf - -C / data > "$DIR/appdata_$TS.tar.gz"

    # Keep the newest 2 tars. Count-based, not mtime-based: a few skipped weeks must never
    # age out the only copies we have.
    ls -1t "$DIR"/appdata_*.tar.gz 2>/dev/null | tail -n +3 | xargs -r rm -f

    echo "backup ok: appdata_$TS.tar.gz"
    ;;

  *)
    echo "usage: backup.sh {db|appdata}" >&2
    exit 2
    ;;
esac
