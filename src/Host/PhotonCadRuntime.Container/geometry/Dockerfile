# syntax=docker/dockerfile:1.7
FROM --platform=linux/amd64 python:3.12.11-slim-bookworm@sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49 AS builder

ARG UV_VERSION=0.8.22
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    UV_LINK_MODE=copy \
    VIRTUAL_ENV=/opt/photon/venv

ADD --checksum=sha256:d307c94e1fc94d2c634736a34ffbabc037b9186351915cf16aa60e7db8dad97f \
    https://codeload.github.com/pzfreo/build123d-mcp/tar.gz/8fb7cefb28daa7f9f68b7e653244256f09b22b1f \
    /tmp/build123d-mcp.tar.gz

RUN python -m venv "${VIRTUAL_ENV}" \
    && "${VIRTUAL_ENV}/bin/python" -m pip install "uv==${UV_VERSION}" \
    && mkdir -p /src \
    && tar -xzf /tmp/build123d-mcp.tar.gz --strip-components=1 -C /src \
    && cd /src \
    && "${VIRTUAL_ENV}/bin/uv" sync --active --locked --no-dev --no-editable \
    && "${VIRTUAL_ENV}/bin/python" -m pip uninstall -y uv \
    && rm -rf /tmp/build123d-mcp.tar.gz /root/.cache /src/.venv

FROM --platform=linux/amd64 python:3.12.11-slim-bookworm@sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49

ARG PHOTON_BUNDLE_VERSION=0.1.0
LABEL org.opencontainers.image.title="Photon CAD geometry runtime" \
      org.opencontainers.image.description="Pinned, offline-at-runtime build123d-mcp geometry worker for Photon CAD" \
      org.opencontainers.image.version="${PHOTON_BUNDLE_VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.source="https://github.com/pzfreo/build123d-mcp" \
      org.opencontainers.image.base.name="python:3.12.11-slim-bookworm" \
      org.opencontainers.image.base.digest="sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49" \
      io.photon.cad.role="geometry" \
      io.photon.cad.source-revision="8fb7cefb28daa7f9f68b7e653244256f09b22b1f"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       fontconfig fonts-dejavu-core libgl1 libgomp1 libsm6 libx11-6 libxext6 libxrender1 xvfb \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 65532 photon \
    && useradd --uid 65532 --gid 65532 --home-dir /session/home --no-create-home --shell /usr/sbin/nologin photon \
    && mkdir -p /opt/photon/bin /workspace \
    && chown 65532:65532 /workspace

COPY --from=builder /opt/photon/venv /opt/photon/venv
COPY --chown=65532:65532 photon_geometry_entrypoint.py /opt/photon/bin/photon_geometry_entrypoint.py

ENV HOME=/session/home \
    XDG_CACHE_HOME=/session/cache \
    OMP_NUM_THREADS=2 \
    OPENBLAS_NUM_THREADS=2 \
    MKL_NUM_THREADS=2 \
    NUMEXPR_NUM_THREADS=2 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONHASHSEED=0 \
    PYTHONNOUSERSITE=1 \
    BUILD123D_TRANSPORT=stdio \
    BUILD123D_EXEC_TIMEOUT=120 \
    BUILD123D_CPU_LIMIT_S=300 \
    BUILD123D_ALLOW_ALL_IMPORTS=0 \
    BUILD123D_NO_SANDBOX=0 \
    BUILD123D_EXPERIMENTAL=0

USER 65532:65532
WORKDIR /workspace
ENTRYPOINT ["/opt/photon/venv/bin/python", "/opt/photon/bin/photon_geometry_entrypoint.py"]
