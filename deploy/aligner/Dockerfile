# Aligner worker: python + CPU torch + demucs around the vendored lyriclab aligner
# (align_lyrics.py is a copy of typebeat-osu/lyriclab/align_lyrics.py, keep them in sync).
# Python 3.11 + torch 2.5.x pinned deliberately, mirroring lyriclab/setup.ps1 (torch 2.6
# breaks Demucs checkpoint loading; 3.11 for wheel coverage). ffmpeg decodes mp3/mp4 inputs.
FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu \
        torch==2.5.1 torchaudio==2.5.1 \
    && pip install --no-cache-dir demucs==4.0.1 soundfile pyphen num2words tqdm

WORKDIR /aligner
COPY deploy/aligner/align_lyrics.py deploy/aligner/worker.py ./

# Demucs model checkpoints land under TORCH_HOME; pointing it at the shared volume means the
# ~few-hundred-MB download survives container rebuilds.
ENV TORCH_HOME=/data/align-cache \
    PYTHONUNBUFFERED=1

CMD ["python", "worker.py"]
