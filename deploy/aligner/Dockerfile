# Aligner worker (backlog 413): python + CPU torch + demucs + the fused-evidence pair, around the
# aligner script the game pin ships. There is NO copy of align_lyrics.py in this repo: the CI
# deploy job checks out the external/typebeat-osu submodule at its pinned commit and stages its
# lyriclab/ directory as deploy/aligner/lyriclab/ (git-ignored) before shipping the tree to the box,
# because the box's build context has no submodules (the ship tar excludes external/, and so does
# .dockerignore). A build without that staging fails at the COPY below rather than running an old
# script.
#
# Pins mirror lyriclab/setup.sh: Python 3.11 (torch 2.5.1 has wheels for 3.9 to 3.12 only), CPU
# torch/torchaudio 2.5.1 (2.6 breaks the Demucs checkpoint load), demucs 4.0.1, the rest of its
# ALIGNER_PACKAGES unpinned. Two deliberate differences from setup.sh, both because this is a
# Debian image rather than a player's machine:
#   - ffmpeg comes from apt, not the imageio-ffmpeg wheel (the script shells out to `ffmpeg` on PATH);
#   - the fused path's espeak-ng is the distro package, handed to phonemizer through
#     PHONEMIZER_ESPEAK_LIBRARY (which align_lyrics.py's QmulTokenizer honours), instead of the
#     espeakng-loader wheel. The distro build knows its own data dir, so no data path is passed.
FROM python:3.11-slim-bookworm

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg espeak-ng \
    && rm -rf /var/lib/apt/lists/* \
    && lib="$(find /usr/lib -name 'libespeak-ng.so.1' | head -n 1)" \
    && test -n "$lib" \
    && ln -s "$lib" /usr/local/lib/libespeak-ng.so.1

RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu \
        torch==2.5.1 torchaudio==2.5.1 \
    && pip install --no-cache-dir demucs==4.0.1 soundfile pyphen num2words tqdm phonemizer

# Demucs checkpoints and the fused path's QMUL weights (57 MB) land under TORCH_HOME
# (hub/checkpoints); on the shared volume they survive image rebuilds and are fetched once (the
# worker warms the QMUL weights at start-up), rather than being baked into every image.
ENV PHONEMIZER_ESPEAK_LIBRARY=/usr/local/lib/libespeak-ng.so.1 \
    PYTHONUNBUFFERED=1 \
    PYTHONUTF8=1 \
    TORCH_HOME=/data/align-cache

# The same check setup.sh's sentinel stands for, plus the fused pair: the image is unusable
# without these, so a broken resolve fails the build here and not the first player's job.
RUN python -c "import torch, torchaudio, demucs, soundfile, pyphen, num2words, phonemizer" \
    && python -c "from phonemizer.backend import EspeakBackend; print(EspeakBackend('en-us').phonemize(['hello'], strip=True))"

WORKDIR /aligner
COPY deploy/aligner/lyriclab/ ./
COPY deploy/aligner/worker.py ./
RUN grep -m 1 '^ALIGNER_VERSION' align_lyrics.py

# The hash of deploy/aligner/ (this file, worker.py, the staged lyriclab/) CI built the image from:
# the deploy skips the build when the next one matches (ci.yml, "Build + start aligner worker").
# Last, so a new value never invalidates a cached layer above it.
ARG ALIGNER_INPUTS=unknown
LABEL typebeat.aligner.inputs=$ALIGNER_INPUTS

CMD ["python", "worker.py"]
