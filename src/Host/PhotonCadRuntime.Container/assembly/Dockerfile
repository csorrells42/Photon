# syntax=docker/dockerfile:1.7
FROM --platform=linux/amd64 python:3.11.13-slim-bookworm@sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1 AS builder

ARG POETRY_VERSION=2.4.1
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

ADD --checksum=sha256:ec4be69fe9b5a864ee599e9939e91524398b4cc5af59cae081a253c5f59f5560 \
    https://codeload.github.com/partcad/partcad/tar.gz/b77c2c08f4a63b97070fcaf9e26bc09f34f95037 \
    /tmp/partcad.tar.gz

COPY runtime-requirements.lock /tmp/runtime-requirements.lock
COPY ocp-reassert.lock /tmp/ocp-reassert.lock
COPY photon_partcad_offline_patch.py /tmp/photon_partcad_offline_patch.py

RUN python -m venv /opt/photon/build-venv \
    && /opt/photon/build-venv/bin/python -m pip install "poetry==${POETRY_VERSION}" \
    && python -m venv /opt/photon/venv \
    && mkdir -p /src \
    && tar -xzf /tmp/partcad.tar.gz --strip-components=1 -C /src \
    && python /tmp/photon_partcad_offline_patch.py \
       /src/partcad/src/partcad/runtime_python_none.py \
    && cd /src \
    && VIRTUAL_ENV=/opt/photon/venv /opt/photon/build-venv/bin/poetry install \
       --only partcad --no-root --no-interaction --no-ansi \
    && /opt/photon/venv/bin/python -m pip install --no-deps --require-hashes --only-binary=:all: \
       --requirement /tmp/runtime-requirements.lock \
    && /opt/photon/venv/bin/python -m pip install --force-reinstall --no-deps \
       --require-hashes --only-binary=:all: --requirement /tmp/ocp-reassert.lock \
    && /opt/photon/venv/bin/python -m pip install --no-deps --no-build-isolation \
       /src/partcad-utils /src/partcad /src/partcad-service-json-rpc \
    && /opt/photon/venv/bin/python -m pip check \
    && PC_TELEMETRY_TYPE=none /opt/photon/venv/bin/python -c \
       "import partcad; assert partcad.__version__ == '0.7.158'" \
    && rm -rf /tmp/partcad.tar.gz /root/.cache

FROM --platform=linux/amd64 python:3.11.13-slim-bookworm@sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1

ARG PHOTON_BUNDLE_VERSION=0.1.1
LABEL org.opencontainers.image.title="Photon CAD assembly runtime" \
      org.opencontainers.image.description="Pinned, offline-at-runtime PartCAD assembly and BOM worker for Photon CAD" \
      org.opencontainers.image.version="${PHOTON_BUNDLE_VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.source="https://github.com/partcad/partcad" \
      org.opencontainers.image.base.name="python:3.11.13-slim-bookworm" \
      org.opencontainers.image.base.digest="sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1" \
      io.photon.cad.role="assembly" \
      io.photon.cad.source-revision="b77c2c08f4a63b97070fcaf9e26bc09f34f95037"

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgl1 libgomp1 libsm6 libx11-6 libxext6 libxrender1 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 65532 photon \
    && useradd --uid 65532 --gid 65532 --home-dir /session/home --no-create-home --shell /usr/sbin/nologin photon \
    && mkdir -p /opt/photon/bin /workspace \
    && chown 65532:65532 /workspace

COPY --from=builder /opt/photon/venv /opt/photon/venv
COPY --chown=65532:65532 photon_partcad_entrypoint.py /opt/photon/bin/photon_partcad_entrypoint.py

# Prove the native CAD stack only after the final image has the same shared
# libraries it will have in production.  Running this in the builder stage
# would reject a healthy runtime merely because that stage omits libGL/X11.
RUN /opt/photon/venv/bin/python -sOOIu -c \
    "import pyexpat, backports.zstd, OCP; from OCP.STEPControl import STEPControl_Reader, STEPControl_Writer"

ENV HOME=/session/home \
    XDG_CACHE_HOME=/session/cache \
    PATH=/opt/photon/venv/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    PYTHONPATH=/opt/photon/bin \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONHASHSEED=0 \
    PYTHONNOUSERSITE=1 \
    PC_PYTHON_SANDBOX=none \
    PC_TELEMETRY_TYPE=none \
    PC_TELEMETRY_SENTRY_DSN="" \
    PC_TELEMETRY_PERFORMANCE=false \
    PC_TELEMETRY_FAILURES=false \
    PC_TELEMETRY_DEBUG=false

USER 65532:65532
WORKDIR /workspace
ENTRYPOINT ["/opt/photon/venv/bin/python", "/opt/photon/bin/photon_partcad_entrypoint.py"]
