ARG PHOTON_CAD_GEOMETRY_BASE=photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb
FROM ${PHOTON_CAD_GEOMETRY_BASE}

ARG PHOTON_CAD_GEOMETRY_BASE_ID=sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703

LABEL io.photon.cad.role="industrial-adapter" \
      io.photon.cad.base-image-id="${PHOTON_CAD_GEOMETRY_BASE_ID}" \
      io.photon.cad.protocol="photon.cad.industrial.request/v1" \
      io.photon.cad.engineering-status="local-evaluation-only" \
      io.photon.cad.redistribution="blocked-pending-license-review" \
      org.opencontainers.image.description="Offline Photon CAD industrial adapter over the pinned geometry image" \
      org.opencontainers.image.licenses="" \
      org.opencontainers.image.version="0.1.0"

ENV PHOTON_CAD_INDUSTRIAL_PROTOCOL=photon.cad.industrial.request/v1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONHASHSEED=0 \
    PYTHONNOUSERSITE=1

COPY --chown=65532:65532 photon_industrial_adapter.py /opt/photon/bin/photon_industrial_adapter.py
COPY --chown=65532:65532 schemas /opt/photon/contracts
COPY --chown=65532:65532 fixtures /opt/photon/golden

USER 65532:65532
WORKDIR /tmp
ENTRYPOINT ["/opt/photon/venv/bin/python", "/opt/photon/bin/photon_industrial_adapter.py"]
