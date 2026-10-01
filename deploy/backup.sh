#!/usr/bin/env bash
# Prod-box backups: Postgres dump NIGHTLY, /data uploads tar WEEKLY, local retention, a free-space
# guard, and an OFFSITE copy of every verified archive to a Cloudflare R2 bucket.
#
#   backup.sh db              nightly dump: verified, kept 14 days locally, then copied offsite
#   backup.sh appdata         weekly /data tar: verified, newest 2 kept locally, then copied offsite
#   backup.sh offsite <file>  copy ONE existing local archive offsite again (after an
#                             "offsite FAILED" line, or to test the R2 setup). With DRY_RUN=1 it
#                             prints the rclone commands and the stamp instead of running them.
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
#
# OFFSITE (backlog 367). Credentials come from deploy/.env (R2_ACCOUNT_ID, R2_ACCESS_KEY_ID,
# R2_SECRET_ACCESS_KEY, R2_BUCKET, optional R2_ENABLED=0 to switch it off); with any of them
# missing the copy is SKIPPED with a log line and the local backup still counts as done. A failed
# or size-mismatched upload exits non-zero and NEVER deletes the local copy. Remote retention is
# NOT this script's job: it lives on the bucket as age-based lifecycle rules per prefix (db/,
# appdata/). Those rules are age-only, so unlike the count-based local rule above they CAN expire
# every copy after enough missed weeks; that is why each verified upload writes a freshness stamp
# into /data/ops/offsite-<kind>.json, which the app serves at GET /api/v2/ops/backups for an
# external poller (the Discord bot, once it grows one) to alert on when it goes stale.
set -euo pipefail

MODE=${1:-db}
# Both paths are overridable ONLY so `DRY_RUN=1 backup.sh offsite <file>` can be exercised off the
# box; cron never sets them.
DIR=${BACKUP_DIR:-/mnt/typebeat-backups}
ENV_FILE=${BACKUP_ENV_FILE:-/opt/typebeat-web/deploy/.env}
TS=$(date -u +%Y%m%dT%H%M%SZ)
MIN_FREE_GB=10
DRY_RUN=${DRY_RUN:-0}

# rclone runs from its pinned docker image rather than an apt package: docker is already on the
# box, and distro rclone builds can predate the Cloudflare provider. The backup directory is mounted
# at the SAME path inside the container, read-only, so file paths need no translation. Set
# RCLONE_BIN to a host binary (e.g. RCLONE_BIN=rclone) to use one instead.
RCLONE_IMAGE=${RCLONE_IMAGE:-rclone/rclone:1.68.2}
RCLONE_BIN=${RCLONE_BIN:-}

# One value from deploy/.env: the LAST `NAME=value` line, anchored so R2_BUCKET cannot match a
# longer name, with CR and one layer of surrounding quotes stripped. Prints nothing when absent.
env_get() {
  [ -r "$ENV_FILE" ] || return 0
  sed -n "s/^$1=//p" "$ENV_FILE" | tail -n 1 | tr -d '\r' | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'\$/\1/"
}

rclone_run() {
  if [ -n "$RCLONE_BIN" ]; then
    "$RCLONE_BIN" "$@"
  else
    docker run --rm -v "$DIR:$DIR:ro" \
      -e RCLONE_CONFIG_R2_TYPE -e RCLONE_CONFIG_R2_PROVIDER -e RCLONE_CONFIG_R2_ENDPOINT \
      -e RCLONE_CONFIG_R2_ACCESS_KEY_ID -e RCLONE_CONFIG_R2_SECRET_ACCESS_KEY \
      -e RCLONE_CONFIG_R2_REGION -e RCLONE_CONFIG_R2_ACL -e RCLONE_CONFIG_R2_NO_CHECK_BUCKET \
      "$RCLONE_IMAGE" "$@"
  fi
}

stamp_json() {
  printf '{"kind":"%s","file":"%s","bytes":%s,"uploadedAt":"%s"}\n' "$1" "$2" "$3" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}

offsite_fail() {
  echo "offsite FAILED ($1): $2. The local copy is KEPT; retry with: $0 offsite $3" >&2
  exit 1
}

# Copy one verified local archive to R2, prove the remote copy has the same size, and only then
# write the freshness stamp. Every step is checked explicitly rather than left to `set -e`, because
# bash runs a function called from a conditional with `set -e` OFF, and a failure would then fall
# straight through to the stamp.
#
# Never deletes anything, local or remote.
offsite() {
  local file=$1 name prefix
  name=$(basename "$file")

  case "$name" in
    typebeat_*.dump.gz) prefix=db ;;
    appdata_*.tar.gz)   prefix=appdata ;;
    *) echo "offsite: '$name' is neither typebeat_*.dump.gz nor appdata_*.tar.gz" >&2; exit 2 ;;
  esac

  [ -f "$file" ] || { echo "offsite: '$file' does not exist" >&2; exit 2; }
  if [ "$(cd "$(dirname "$file")" && pwd)" != "$(cd "$DIR" && pwd)" ]; then
    echo "offsite: '$file' is not directly inside $DIR (the rclone container only sees that directory)" >&2
    exit 2
  fi

  local enabled account key secret bucket
  enabled=$(env_get R2_ENABLED)
  account=$(env_get R2_ACCOUNT_ID)
  key=$(env_get R2_ACCESS_KEY_ID)
  secret=$(env_get R2_SECRET_ACCESS_KEY)
  bucket=$(env_get R2_BUCKET)

  case "$enabled" in
    0|false|no|off)
      echo "offsite SKIPPED ($prefix): R2_ENABLED=$enabled in deploy/.env"
      return 0 ;;
  esac
  if [ -z "$account" ] || [ -z "$key" ] || [ -z "$secret" ] || [ -z "$bucket" ]; then
    # Not an error: a box without R2 credentials still takes its local backups. Saying that there
    # is no offsite copy is the freshness alert's job, not this exit code's.
    echo "offsite SKIPPED ($prefix): not configured (set R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET in deploy/.env)"
    return 0
  fi

  # rclone is configured purely from the environment: no config file on the box to drift or leak.
  # NO_CHECK_BUCKET is required for a bucket-scoped token, which may not list or create buckets.
  export RCLONE_CONFIG_R2_TYPE=s3
  export RCLONE_CONFIG_R2_PROVIDER=Cloudflare
  export RCLONE_CONFIG_R2_ENDPOINT="https://$account.r2.cloudflarestorage.com"
  export RCLONE_CONFIG_R2_ACCESS_KEY_ID="$key"
  export RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$secret"
  export RCLONE_CONFIG_R2_REGION=auto
  export RCLONE_CONFIG_R2_ACL=private
  export RCLONE_CONFIG_R2_NO_CHECK_BUCKET=true

  local remote="r2:$bucket/$prefix/$name" local_bytes remote_bytes
  local_bytes=$(stat -c %s "$file")

  if [ "$DRY_RUN" = 1 ]; then
    echo "DRY_RUN: endpoint $RCLONE_CONFIG_R2_ENDPOINT, bucket $bucket"
    echo "DRY_RUN: rclone copyto --retries 3 $file $remote"
    echo "DRY_RUN: rclone lsjson --files-only $remote   (must report Size $local_bytes)"
    echo "DRY_RUN: rclone lsf r2:$bucket/$prefix/ | tail -n 5"
    echo "DRY_RUN: typebeat-web-app-1:/data/ops/offsite-$prefix.json <- $(stamp_json "$prefix" "$name" "$local_bytes")"
    return 0
  fi

  rclone_run copyto --retries 3 "$file" "$remote" \
    || offsite_fail "$prefix" "upload of $name to $remote failed" "$file"

  # Checked against what the bucket now holds, not against what rclone reports having sent.
  remote_bytes=$(rclone_run lsjson --files-only "$remote" | grep -o '"Size":[0-9]*' | head -n 1 | cut -d: -f2) \
    || offsite_fail "$prefix" "could not read $remote back after the upload" "$file"
  if [ "${remote_bytes:-}" != "$local_bytes" ]; then
    offsite_fail "$prefix" "$remote is ${remote_bytes:-missing} bytes, the local file is $local_bytes" "$file"
  fi

  echo "offsite ok: $prefix/$name $local_bytes"
  echo "offsite newest in r2:$bucket/$prefix/:"
  rclone_run lsf "r2:$bucket/$prefix/" | tail -n 5 || true

  # The stamp lands in the app's /data (the appdata volume) through the app container, written to
  # a temp name and renamed so the endpoint never reads half a file. Written ONLY after the size
  # check, so "fresh" can only ever mean "a verified copy exists offsite".
  stamp_json "$prefix" "$name" "$local_bytes" \
    | docker exec -i typebeat-web-app-1 sh -c "mkdir -p /data/ops && cat > /data/ops/offsite-$prefix.json.tmp && mv /data/ops/offsite-$prefix.json.tmp /data/ops/offsite-$prefix.json" \
    || offsite_fail "$prefix" "uploaded and verified, but the freshness stamp could not be written into typebeat-web-app-1, so GET /api/v2/ops/backups will report this backup as stale" "$file"
}

# Re-push one archive. Handled BEFORE the guards below: it writes nothing locally, so neither the
# mount check nor the free-space floor applies.
if [ "$MODE" = offsite ]; then
  [ -n "${2:-}" ] || { echo "usage: backup.sh offsite <archive in $DIR>" >&2; exit 2; }
  offsite "$2"
  exit 0
fi

# The backups live on a SEPARATE Hetzner volume (scsi-0HC_Volume_106574543, 100 GB, mounted by
# fstab with nofail), not on the root disk. Refuse to run if it is not actually mounted: without
# this check a detached or unmounted volume turns every backup into a write onto the 75 GB root
# disk, which is precisely the failure that took the site down on 2026-08-09, just wearing a
# different hat. Deliberately NOT mkdir -p: creating the mount point on the root disk is the bug,
# not the fix.
if ! mountpoint -q "$DIR"; then
  echo "backup ABORTED ($MODE): $DIR is not a mount point, so the backup volume is not mounted." >&2
  echo "  check: lsblk; mount -a; df -h $DIR" >&2
  exit 1
fi

# Free-space guard: skip + log instead of filling the disk Postgres runs on.
FREE_GB=$(df -BG --output=avail "$DIR" | tail -n 1 | tr -dc '0-9')
if [ "${FREE_GB:-0}" -lt "$MIN_FREE_GB" ]; then
  echo "backup SKIPPED ($MODE): ${FREE_GB:-?} GB free < ${MIN_FREE_GB} GB minimum, free space, then re-run"
  exit 1
fi

case "$MODE" in
  db)
    # Nightly database dump, 14-day retention.
    PW=$(grep POSTGRES_PASSWORD "$ENV_FILE" | cut -d= -f2)

    # Same publish rule as appdata below: write a .partial, prove it, and only then give it the
    # real name. Before this the dump went straight to its final name, so a pg_dump that died
    # halfway left a truncated file that looked exactly like a good backup, and would now be
    # uploaded offsite as one. Two checks, cheap to dear: gzip -t proves the compressed stream is
    # whole; pg_restore --list proves the custom-format archive inside has a readable table of
    # contents, i.e. pg_restore could actually start on it.
    PARTIAL="$DIR/typebeat_$TS.dump.gz.partial"
    trap 'rm -f "$PARTIAL"' EXIT

    # A run killed hard (SIGKILL, power loss) skips the trap; sweep what such a run left behind.
    find "$DIR" -maxdepth 1 -name 'typebeat_*.dump.gz.partial' -mmin +720 -delete

    docker exec -e PGPASSWORD="$PW" typebeat-web-postgres-1 \
      pg_dump -U typebeat -d typebeat -Fc | gzip > "$PARTIAL"
    gzip -t "$PARTIAL" || { echo "backup FAILED: the db dump is not a whole gzip stream, discarding" >&2; exit 1; }
    gunzip -c "$PARTIAL" | docker exec -i typebeat-web-postgres-1 pg_restore --list > /dev/null \
      || { echo "backup FAILED: pg_restore cannot read the dump's table of contents, discarding" >&2; exit 1; }
    mv "$PARTIAL" "$DIR/typebeat_$TS.dump.gz"
    trap - EXIT

    find "$DIR" -name 'typebeat_*.dump.gz' -mtime +14 -delete

    echo "backup ok: typebeat_$TS.dump.gz"
    offsite "$DIR/typebeat_$TS.dump.gz"
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
    offsite "$DIR/appdata_$TS.tar.gz"
    ;;

  *)
    echo "usage: backup.sh {db|appdata|offsite <archive>}" >&2
    exit 2
    ;;
esac
