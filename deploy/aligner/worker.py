#!/usr/bin/env python3
"""Aligner worker (backlog 413): processes /data/align-jobs one at a time.

Protocol (see src/Typebeat.Web/Align/AlignJobStore.cs, the C# side of this contract):
  worker.json          heartbeat written by this worker every HEARTBEAT_S: aligner_version and
                       heartbeat_at (UTC); the app refuses new jobs when it is stale
  {job}/job.json       inputs manifest, its presence means inputs are complete
  {job}/input.<ext>    audio (or video whose audio track aligns)
  {job}/lyrics.txt     raw lyrics
  {job}/.running       claim stamp written by this worker
  {job}/progress.log   appended aligner output (last line = live progress)
  {job}/cancel         the owner's cancel request
  {job}/timing.json    terminal success payload
  {job}/error.json     terminal failure: {"error": "..."}

The aligner script is NOT a copy kept here: CI stages the game pin's lyriclab/ (the web repo's
external/typebeat-osu submodule) into the image, so the server always runs exactly the aligner the
pinned game ships. Its command line is the game's (LyricMapImporter.AlignerArguments): --anchors
from the manifest, --vocal-mode estimated when asked and the script has it (version 7+), and
--evidence fused plus --lyrics-language on version 10+. The quality tier stays the default (fast):
the server is sized for capacity, not for the full tier's accuracy.

One job at a time by construction (torch/demucs peak at several GB on this box; the loop IS the
capacity plan). A job's heavy files (the audio, its work and out dirs) are deleted the moment it
ends; whole job directories go after RETENTION_S, which outlives a UTC day on purpose, because the
app counts a player's manifests for its daily cap. A stale .running claim (worker crashed or was
restarted mid-job) is failed permanently rather than re-run, and so is a job that waited longer
than PENDING_MAX_AGE_S: alignment costs minutes of CPU and the client has long given up by then.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

JOBS_ROOT = Path(os.environ.get("ALIGN_JOBS_ROOT", "/data/align-jobs"))
ALIGNER = Path(__file__).parent / "align_lyrics.py"
THREADS = int(os.environ.get("ALIGN_THREADS", "3"))

# Keep these equal to AlignJobStore's JobTimeout, PendingMaxAge and RetentionHours.
JOB_TIMEOUT_S = 20 * 60
PENDING_MAX_AGE_S = 3 * 3600
RETENTION_S = 48 * 3600
# Heavy files of a job that never reached the worker (it was down, or the job was abandoned).
HEAVY_AGE_S = 6 * 3600
HEARTBEAT_S = 15
POLL_S = 2.0

# The first script versions with each flag (the game's LyricMapImporter constants).
VOCAL_MODE_ALIGNER_VERSION = 7
FUSED_EVIDENCE_ALIGNER_VERSION = 10

ALLOWED_ANCHORS = ("ref", "auto")


def log(msg: str) -> None:
    print(f"[worker] {msg}", flush=True)


def read_aligner_version() -> str | None:
    """ALIGNER_VERSION off the script's source, the way the game reads it (no Python import)."""
    try:
        m = re.search(r'^ALIGNER_VERSION\s*=\s*"([^"]+)"', ALIGNER.read_text(encoding="utf-8"), re.MULTILINE)
        return m.group(1) if m else None
    except OSError:
        return None


ALIGNER_VERSION = read_aligner_version()


def version_at_least(minimum: int) -> bool:
    try:
        return ALIGNER_VERSION is not None and int(ALIGNER_VERSION) >= minimum
    except ValueError:
        return False


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_heartbeat() -> None:
    JOBS_ROOT.mkdir(parents=True, exist_ok=True)
    tmp = JOBS_ROOT / f".worker.{os.getpid()}.tmp"
    tmp.write_text(json.dumps({"aligner_version": ALIGNER_VERSION, "heartbeat_at": utc_now_iso()}), encoding="utf-8")
    os.replace(tmp, JOBS_ROOT / "worker.json")


def heartbeat_loop() -> None:
    # Its own thread, so a long silent aligner stage never makes the worker look dead.
    while True:
        try:
            write_heartbeat()
        except Exception as e:  # noqa: BLE001 - the heartbeat must outlive any one failure
            log(f"heartbeat error: {e}")
        time.sleep(HEARTBEAT_S)


def write_error(job: Path, message: str) -> None:
    (job / "error.json").write_text(json.dumps({"error": message}), encoding="utf-8")


def is_terminal(job: Path) -> bool:
    return (job / "timing.json").exists() or (job / "error.json").exists()


def is_cancelled(job: Path) -> bool:
    # The web app drops this marker on DELETE /api/v2/typebeat/server-align/{id}.
    return (job / "cancel").exists()


def read_manifest(job: Path) -> dict:
    return json.loads((job / "job.json").read_text(encoding="utf-8"))


def created_at(job: Path, manifest: dict | None = None) -> float:
    """The manifest's created_at as a unix time, falling back to job.json's mtime."""
    try:
        manifest = manifest if manifest is not None else read_manifest(job)
        stamp = str(manifest["created_at"]).replace("Z", "+00:00")
        # .NET writes seven fractional digits; Python 3.11's fromisoformat wants at most six.
        stamp = re.sub(r"(\.\d{6})\d+", r"\1", stamp)
        return datetime.fromisoformat(stamp).timestamp()
    except Exception:  # noqa: BLE001 - any unreadable stamp falls back to the file time
        return (job / "job.json").stat().st_mtime


def drop_heavy_files(job: Path) -> None:
    """The audio, the aligner's work dir (separated stems, caches) and its out dir. The small
    files (manifest, lyrics, progress, the terminal file) stay until the job dir is collected."""
    for child in job.iterdir():
        try:
            if child.name.startswith("input."):
                child.unlink(missing_ok=True)
            elif child.name in ("work", "out") and child.is_dir():
                shutil.rmtree(child, ignore_errors=True)
        except OSError:
            pass


def gc_old_jobs() -> None:
    now = time.time()
    if not JOBS_ROOT.exists():
        return
    for job in JOBS_ROOT.iterdir():
        try:
            if not job.is_dir() or job.name.startswith("."):
                continue
            manifest = job / "job.json"
            age = now - (manifest.stat().st_mtime if manifest.exists() else job.stat().st_mtime)
            if age > RETENTION_S:
                shutil.rmtree(job, ignore_errors=True)
                log(f"gc: removed {job.name}")
            elif age > HEAVY_AGE_S:
                drop_heavy_files(job)
        except OSError:
            pass


def next_job() -> Path | None:
    """Oldest claimable job (manifest present, not terminal, not claimed), by created_at."""
    candidates = []
    if not JOBS_ROOT.exists():
        return None
    now = time.time()
    for job in JOBS_ROOT.iterdir():
        try:
            if not job.is_dir() or not (job / "job.json").exists() or is_terminal(job):
                continue
            if (job / ".running").exists():
                # A claim can only be ours-from-a-past-life: fail it permanently (see module doc).
                write_error(job, "the server aligner restarted while processing this job; try again")
                drop_heavy_files(job)
                continue
            if is_cancelled(job):
                # Cancelled before we ever claimed it: terminate without spending any CPU.
                write_error(job, "alignment cancelled")
                drop_heavy_files(job)
                continue
            stamp = created_at(job)
            if now - stamp > PENDING_MAX_AGE_S:
                write_error(job, "this job waited too long in the server aligner queue; try again later, or use the local aligner")
                drop_heavy_files(job)
                continue
            candidates.append((stamp, job.name, job))
        except (OSError, ValueError):
            continue
    candidates.sort()
    return candidates[0][2] if candidates else None


def build_command(job: Path, manifest: dict, audio: Path, lyrics: Path, out_dir: Path) -> list[str]:
    """The game's LyricMapImporter.AlignerArguments, minus the device (CPU only here) and the
    quality tier (the default, fast)."""
    anchors = manifest.get("anchors")
    if anchors not in ALLOWED_ANCHORS:
        anchors = "auto"

    cmd = [sys.executable, str(ALIGNER), str(audio), str(lyrics), "-o", str(out_dir),
           "--work-dir", str(job / "work"), "--threads", str(THREADS), "--anchors", anchors]

    # The app only asks for estimated vocals on stamped lyrics (the mode paces lines from them).
    if manifest.get("vocal_mode") == "estimated" and anchors == "ref" and version_at_least(VOCAL_MODE_ALIGNER_VERSION):
        cmd += ["--vocal-mode", "estimated"]

    if version_at_least(FUSED_EVIDENCE_ALIGNER_VERSION):
        cmd += ["--evidence", "fused"]
        language = str(manifest.get("language") or "").strip().lower()
        # Canonical names only ever reach here (the app normalises), but argv is no place for doubt.
        if re.fullmatch(r"[a-z]{2,32}", language):
            cmd += ["--lyrics-language", language]

    return cmd


def pump(stream, progress_path: Path) -> None:
    """Copies the aligner's output into progress.log as it arrives (a thread of its own, so the
    main loop can enforce the timeout and the cancel even while the aligner prints nothing)."""
    with open(progress_path, "a", encoding="utf-8") as progress:
        for line in stream:
            if line.strip():
                progress.write(line if line.endswith("\n") else line + "\n")
                progress.flush()


def run_job(job: Path) -> None:
    try:
        manifest = read_manifest(job)
    except (OSError, ValueError) as e:
        write_error(job, f"the job manifest is unreadable: {e}")
        drop_heavy_files(job)
        return

    audio = job / str(manifest.get("audio_file", ""))
    lyrics = job / "lyrics.txt"
    out_dir = job / "out"

    if not manifest.get("audio_file") or not audio.exists() or not lyrics.exists():
        write_error(job, "job inputs are missing")
        drop_heavy_files(job)
        return

    (job / ".running").touch()

    if is_cancelled(job):
        # Cancel landed between claim and start, don't launch the aligner at all.
        write_error(job, "alignment cancelled")
        drop_heavy_files(job)
        return

    cmd = build_command(job, manifest, audio, lyrics, out_dir)
    log(f"running {job.name} ({manifest.get('artist')} - {manifest.get('title')}): {' '.join(cmd[2:])}")

    start = time.time()
    env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")

    try:
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            cwd=str(ALIGNER.parent),
            env=env,
        )
        assert proc.stdout is not None
        reader = threading.Thread(target=pump, args=(proc.stdout, job / "progress.log"), daemon=True)
        reader.start()

        while proc.poll() is None:
            if is_cancelled(job):
                # The client left the import screen; stop burning CPU on a result no one will collect.
                proc.kill()
                proc.wait()
                write_error(job, "alignment cancelled")
                log(f"cancelled {job.name}")
                return
            if time.time() - start > JOB_TIMEOUT_S:
                proc.kill()
                proc.wait()
                write_error(job, "alignment timed out on the server")
                log(f"timed out {job.name}")
                return
            time.sleep(1.0)

        reader.join(timeout=10)
        code = proc.returncode
    except Exception as e:  # noqa: BLE001 - any failure must terminate the job, not the worker
        write_error(job, f"the server aligner crashed: {e}")
        return
    finally:
        if is_terminal(job):
            drop_heavy_files(job)

    if code != 0:
        tail = ""
        try:
            tail = " | ".join((job / "progress.log").read_text(encoding="utf-8").splitlines()[-4:])
        except OSError:
            pass
        write_error(job, f"the aligner exited with code {code}: {tail}"[:2000])
        drop_heavy_files(job)
        return

    produced = sorted(out_dir.glob("*.timing.json")) if out_dir.exists() else []
    if not produced:
        write_error(job, "the aligner produced no timing.json")
        drop_heavy_files(job)
        return

    # timing.json LAST, its presence is the terminal success signal.
    tmp = job / "timing.json.tmp"
    shutil.copyfile(produced[0], tmp)
    os.replace(tmp, job / "timing.json")
    drop_heavy_files(job)
    log(f"done {job.name} in {time.time() - start:.0f}s")


def warm_weights() -> None:
    """Fetches the fused path's QMUL weights into TORCH_HOME before the first job needs them (best
    effort: the first fused run fetches them itself when this fails)."""
    if not version_at_least(FUSED_EVIDENCE_ALIGNER_VERSION):
        return
    try:
        r = subprocess.run([sys.executable, "-c", "import align_lyrics; print(align_lyrics.qmul_weights_path())"],
                           cwd=str(ALIGNER.parent), capture_output=True, text=True, timeout=600)
        detail = (r.stdout.strip() or r.stderr.strip()).splitlines()[-1:] or [""]
        log(f"fused-evidence weights: {'ready' if r.returncode == 0 else 'not fetched'} {detail[0]}")
    except Exception as e:  # noqa: BLE001
        log(f"fused-evidence weights not fetched: {e}")


def main() -> None:
    log(f"aligner worker up; jobs root {JOBS_ROOT}, aligner {ALIGNER} version {ALIGNER_VERSION}, threads {THREADS}")
    if ALIGNER_VERSION is None:
        log("WARNING: no ALIGNER_VERSION in the aligner script; was lyriclab staged into the image?")

    JOBS_ROOT.mkdir(parents=True, exist_ok=True)
    threading.Thread(target=heartbeat_loop, daemon=True).start()
    warm_weights()

    last_gc = 0.0

    while True:
        try:
            if time.time() - last_gc > 3600:
                gc_old_jobs()
                last_gc = time.time()

            job = next_job()
            if job is not None:
                run_job(job)
                continue  # immediately look for the next queued job
        except Exception as e:  # noqa: BLE001 - the loop must survive anything
            log(f"loop error: {e}")

        time.sleep(POLL_S)


if __name__ == "__main__":
    main()
