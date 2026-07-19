#!/usr/bin/env python3
"""Aligner worker: processes /data/align-jobs one at a time.

Protocol (see AlignJobStore.cs, the C# side of this contract):
  {job}/job.json       inputs manifest — its presence means inputs are complete
  {job}/input.<ext>    audio (or video whose audio track aligns)
  {job}/lyrics.txt     raw lyrics
  {job}/.running       claim stamp written by this worker
  {job}/progress.log   appended aligner output (last line = live progress)
  {job}/timing.json    terminal success payload
  {job}/error.json     terminal failure: {"error": "..."}

One job at a time by construction (torch/demucs peak at multiple GB on this box — the loop IS
the capacity plan). Jobs older than the GC window are deleted wholesale. A stale .running claim
(worker crashed/restarted mid-job) is failed permanently rather than re-run: alignment costs
minutes of CPU and the client has long since given up on the abandonment window anyway.
"""

import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

JOBS_ROOT = Path(os.environ.get("ALIGN_JOBS_ROOT", "/data/align-jobs"))
ALIGNER = Path(__file__).parent / "align_lyrics.py"
JOB_TIMEOUT_S = 15 * 60
GC_AGE_S = 6 * 3600
POLL_S = 2.0


def log(msg: str) -> None:
    print(f"[worker] {msg}", flush=True)


def write_error(job: Path, message: str) -> None:
    (job / "error.json").write_text(json.dumps({"error": message}), encoding="utf-8")


def is_terminal(job: Path) -> bool:
    return (job / "timing.json").exists() or (job / "error.json").exists()


def is_cancelled(job: Path) -> bool:
    # The web app drops this marker on DELETE /api/v2/typebeat/align/{id} (see AlignJobStore.cs).
    return (job / "cancel").exists()


def gc_old_jobs() -> None:
    now = time.time()
    if not JOBS_ROOT.exists():
        return
    for job in JOBS_ROOT.iterdir():
        try:
            if job.is_dir() and now - job.stat().st_mtime > GC_AGE_S:
                shutil.rmtree(job, ignore_errors=True)
                log(f"gc: removed {job.name}")
        except OSError:
            pass


def next_job() -> Path | None:
    """Oldest claimable job (manifest present, not terminal, not claimed)."""
    candidates = []
    if not JOBS_ROOT.exists():
        return None
    for job in JOBS_ROOT.iterdir():
        try:
            if not job.is_dir() or not (job / "job.json").exists() or is_terminal(job):
                continue
            if (job / ".running").exists():
                # A claim can only be ours-from-a-past-life: fail it permanently (see module doc).
                write_error(job, "the aligner restarted while processing this job")
                continue
            if is_cancelled(job):
                # Cancelled before we ever claimed it: terminate without spending any CPU.
                write_error(job, "alignment cancelled")
                continue
            candidates.append(job)
        except OSError:
            continue
    candidates.sort(key=lambda p: p.stat().st_mtime)
    return candidates[0] if candidates else None


def run_job(job: Path) -> None:
    manifest = json.loads((job / "job.json").read_text(encoding="utf-8"))
    audio = job / manifest["audio_file"]
    lyrics = job / "lyrics.txt"
    out_dir = job / "out"

    if not audio.exists() or not lyrics.exists():
        write_error(job, "job inputs are missing")
        return

    (job / ".running").touch()

    if is_cancelled(job):
        # Cancel landed between claim and start — don't launch the aligner at all.
        write_error(job, "alignment cancelled")
        return

    log(f"running {job.name} ({manifest.get('artist')} - {manifest.get('title')}, anchors={manifest.get('anchors')})")

    cmd = [sys.executable, str(ALIGNER), str(audio), str(lyrics), "-o", str(out_dir)]
    if manifest.get("anchors") == "auto":
        cmd += ["--anchors", "auto"]

    start = time.time()

    try:
        with open(job / "progress.log", "a", encoding="utf-8") as progress:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                cwd=str(ALIGNER.parent),
            )
            assert proc.stdout is not None

            for line in proc.stdout:
                if line.strip():
                    progress.write(line if line.endswith("\n") else line + "\n")
                    progress.flush()
                if is_cancelled(job):
                    # The client left the import screen; stop burning CPU on a result no one will collect.
                    proc.kill()
                    write_error(job, "alignment cancelled")
                    log(f"cancelled {job.name}")
                    return
                if time.time() - start > JOB_TIMEOUT_S:
                    proc.kill()
                    write_error(job, "alignment timed out")
                    return

            code = proc.wait(timeout=60)
    except Exception as e:  # noqa: BLE001 — any failure must terminate the job, not the worker
        write_error(job, f"aligner crashed: {e}")
        return

    if code != 0:
        tail = ""
        try:
            tail = " | ".join((job / "progress.log").read_text(encoding="utf-8").splitlines()[-4:])
        except OSError:
            pass
        write_error(job, f"aligner exited with code {code}: {tail}"[:2000])
        return

    produced = sorted(out_dir.glob("*.timing.json")) if out_dir.exists() else []
    if not produced:
        write_error(job, "the aligner produced no timing.json")
        return

    # timing.json LAST — its presence is the terminal success signal.
    shutil.copyfile(produced[0], job / "timing.json")
    log(f"done {job.name} in {time.time() - start:.0f}s")


def main() -> None:
    log(f"aligner worker up; jobs root {JOBS_ROOT}, aligner {ALIGNER}")
    JOBS_ROOT.mkdir(parents=True, exist_ok=True)
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
        except Exception as e:  # noqa: BLE001 — the loop must survive anything
            log(f"loop error: {e}")

        time.sleep(POLL_S)


if __name__ == "__main__":
    main()
