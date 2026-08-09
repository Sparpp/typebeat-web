#!/usr/bin/env bash
# Prod-box backups: Postgres dump NIGHTLY, /data uploads tar WEEKLY, local retention, and a
# free-space guard. LOCAL ONLY for now; an offsite copy (R2) is a follow-up needing creds.
#
# Nothing schedules this automatically (the box's root crontab starts EMPTY). Install EXACTLY
# these two lines with `crontab -e` as root, after the M3 compose is up (the appdata job needs
# the app container to mount /data, before that, its docker-exec tar step fails):
#
#   17 3 * * *  /opt/typebeat-web/deploy/backup.sh db      >> /var/log/typebeat-backup.log 2>&1
#   47 3 * * 0  /opt/typebeat-web/deploy/backup.sh appdata >> /var/log/typebeat-backup.log 2>&1
#
# Disk math (75 GB disk shared by pgdata + appdata + backups): nightly FULL tars of /data with
# 14-day retention amplified user-controlled upload data ~14x; a few GB of uploads could fill
# the filesystem Postgres lives on. Weekly tars with a 2-copy retention cap that at ~2x /data,
# the guard below refuses to start any backup with <10 GB free (a skipped backup beats a
# wedged database), and the server itself prunes assembled per-version download packages
# beyond the latest two per set (PackageIngest, they are reassemblable from the
# content-addressed blobs).
#
# MIN_FREE_GB IS A FLOOR, NOT THE REAL GUARD, and on 2026-08-09 that distinction took the site
# down. /data had grown until an appdata tar was ~16 GB, so 10 GB free passed the check and the
# write then filled the disk anyway: a fixed threshold cannot protect against an archive that
# outgrows it. The appdata branch therefore sizes its own preflight from the PREVIOUS archive
# plus 20%, which tracks the data as it grows, and prunes before writing rather than after.
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
  echo "backup SKIPPED ($MODE): ${FREE_GB:-?} GB free < ${MIN_FREE_GB} GB minimum, free space, then re-run"
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
    #
    # ORDER MATTERS, AND GETTING IT WRONG TOOK THE SITE DOWN (2026-08-09). This used to write the
    # new tar and prune afterwards, so the peak requirement was THREE archives (two old plus the
    # one being written) even though the retention cap is two. At ~16 GB per archive on a 75 GB
    # disk shared with pgdata and the docker images, that peak filled the filesystem, the tar died
    # mid-write, Postgres went unhealthy and every request 500'd. Pruning FIRST caps the peak at
    # two, which is what the disk math in the header assumed all along.
    #
    # Keep the newest 1 before writing, so this run's tar makes 2. Count-based, not mtime-based:
    # a few skipped weeks must never age out the only copies we have.
    ls -1t "$DIR"/appdata_*.tar.gz 2>/dev/null | tail -n +2 | xargs -r rm -f

    # Refuse to start if the free space cannot hold an archive about the size of the last one
    # (plus 20%). Failing loudly on a Sunday morning is enormously better than filling the disk
    # out from under Postgres, which is what happens when tar is allowed to run out of room.
    LAST_SIZE=$(stat -c %s "$(ls -1t "$DIR"/appdata_*.tar.gz 2>/dev/null | head -1)" 2>/dev/null || echo 0)
    NEED_KB=$(( (LAST_SIZE / 1024) * 12 / 10 ))
    FREE_KB=$(df -Pk "$DIR" | awk 'NR==2 {print $4}')
    if [ "$NEED_KB" -gt 0 ] && [ "$FREE_KB" -lt "$NEED_KB" ]; then
      echo "backup ABORTED: appdata needs ~$((NEED_KB/1024/1024)) GB free, have $((FREE_KB/1024/1024)) GB" >&2
      exit 1
    fi

    # Write to a .partial name and only publish it on success, so a truncated archive can never
    # be mistaken for a backup, and can never occupy one of the two retention slots. That second
    # failure is the quiet one: the corrupt tar sorts NEWEST, so the next prune would have kept it
    # and deleted a good copy.
    PARTIAL="$DIR/appdata_$TS.tar.gz.partial"
    trap 'rm -f "$PARTIAL"' EXIT

    docker exec typebeat-web-app-1 tar czf - -C / data > "$PARTIAL"
    gzip -t "$PARTIAL" || { echo "backup FAILED: appdata archive is corrupt, discarding" >&2; exit 1; }
    mv "$PARTIAL" "$DIR/appdata_$TS.tar.gz"
    trap - EXIT

    echo "backup ok: appdata_$TS.tar.gz"
    ;;

  *)
    echo "usage: backup.sh {db|appdata}" >&2
    exit 2
    ;;
esac
