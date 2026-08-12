# Photon CAD industrial container verification

Run `8dbb4721528345a18188f141ce3df0db` passed 22/22 hardened scenarios in 273652 ms.
Window: `2026-08-11T23:37:01.6933371+00:00` through `2026-08-11T23:41:35.3457543+00:00`.

## Immutable identities and evidence

- Exact base image: `sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703` (lookup tag `photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb`)
- Exact derived image: `sha256:11225c611b86551574636a1331adb6320217c62d11b7a6992ed15d4b0cc37760`; all scenarios ran this ID, never a friendly tag
- Friendly tag promoted only after the receipt was durable: `photon-cad-industrial:0.1.0-b123dmcp-0.3.80-bdw0.2.0`
- Immutable receipt: `runs/receipt-8dbb4721528345a18188f141ce3df0db.json` (`sha256:8efb4c11381c5a5daef860d88e0b0ebfc876a608d503181cbbf0045bb3833884`)
- Catalog: `sha256:aae5554ce9e57133f508e3343663704d35c82e6a31e5201f6224a9b299ddf6b6`; bd_warehouse package content: `sha256:2e1b8d0a41f0562071c3fdc5cbe803871e3c94fdf8927b0238550b6844aea5b9`

## Sealed artifacts

- Box STEP: `sha256:9c21a5e0dc3f47e17ae61a8900403d7ea8b104e0fbb049002341905ea980a6c8`
- Cylinder STEP: `sha256:4d2c3487052a2172b76390a7b2995b53e2835a31c3c19093b8f8c0d08ffd0171`
- Discovered bearing STEP: `sha256:df0f171fd8142dfaf549584ee71036a1376fff52160a5c7655f650515d9f0624`
- Spur gear STEP: `sha256:3cd437e4710ba7a040d764b6d24b54ae7ca8812c45181705d372ff4ba5e9759b`
- Self-contained tagged GLB: `sha256:986bda476bca2a812b24068ebc86e7c11a2c223b5db37394d0793b17d3a176bc`

## Security result

The verified runner used network-none, a read-only root, non-root UID/GID 65532, all capabilities dropped, no-new-privileges, bounded CPU/memory/PIDs, separate read-only input and read-write output mounts, host deadlines/output caps, and exact container kill/remove/absence checks. Strict JSON, hostile STEP, path/ADS/traversal, DAG, 15-value transform, atomic output race, hard-link and supported reparse-point, timeout, and stdout-flood cases failed closed.

Redistribution remains **blocked pending an independent dependency/license review**. This report makes no aggregate license, compliance, or SBOM claim.

## Source hashes

- `sha256:97c02fec729c2a306470b8ea73807ca05ddbdb0d0a05d44a3ee777cfafb5ccc4`  `src/Host/PhotonCadIndustrial.ContainerSmoke/HardenedDockerHarness.cs`
- `sha256:5076da3ac7d88d021a193ba1e2557a3c934f57e0f161cbc45d87ea9c5920e401`  `src/Host/PhotonCadIndustrial.ContainerSmoke/PhotonCadIndustrial.ContainerSmoke.csproj`
- `sha256:f27e1f9eaf561e863b619f603f1ec734c185d97a5c3ed7dd16c2673a46dc8556`  `src/Host/PhotonCadIndustrial.ContainerSmoke/Program.cs`
- `sha256:fae78fb2fc6bb033c33f19ec8571a5cc2ae4c619d38050a969cf4d0323353c7f`  `src/Host/PhotonCadRuntime.Container/industrial/Dockerfile`
- `sha256:154321a9447066fe35fc454f49dd89a0176ccd8ae148504d13aa109ff89f30bd`  `src/Host/PhotonCadRuntime.Container/industrial/README.md`
- `sha256:3ebcc7a2f425157442db35d6bd0915ecfd9d99b6658dad59c494cb0952a503d9`  `src/Host/PhotonCadRuntime.Container/industrial/fixtures/catalog.request.json`
- `sha256:a3e2551b78c641c462145e9a1d53f36bc2d3ed1184bdf2df6a2c74d49c11ad1a`  `src/Host/PhotonCadRuntime.Container/industrial/fixtures/protocol-golden-v1.json`
- `sha256:b0594ef6364b8dde1983df8a5f0f364c54ac3d124fd3ef922f236dc954a32dc8`  `src/Host/PhotonCadRuntime.Container/industrial/photon_industrial_adapter.py`
- `sha256:2bfab3a6e0f78969a2a7d266228f56cced5d01869bf487f12329574674d187c3`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/catalog-v1.schema.json`
- `sha256:b94c5da7099e524c8596a4201b6f045f2561bae18e235883b81161e0d05f1a72`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/request-v1.schema.json`
- `sha256:eb1250be329a51bb5dd94b0697232b746983404a7c594832c5d02dab55099497`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/response-v1.schema.json`

## Scenarios

- PASS `exact-base-image-identity` (138 ms)
- PASS `immutable-base-staging-binding` (148 ms)
- PASS `offline-derived-image-build` (1166 ms)
- PASS `strict-json-hostile-inputs` (64235 ms)
- PASS `mutable-tag-drift-detection-simulation` (0 ms)
- PASS `host-deadline-kills-exact-container` (1417 ms)
- PASS `stdout-cap-kills-exact-container` (639 ms)
- PASS `deterministic-catalog-bytes-and-identity` (12369 ms)
- PASS `box-6000-and-new-only-output` (12413 ms)
- PASS `cylinder-formula` (6169 ms)
- PASS `manual-rectangle-circle-extrude-cut-hole-and-preview` (30957 ms)
- PASS `manual-protocol-hostiles-and-unimplemented-edit-operations` (24892 ms)
- PASS `dynamic-bearing-creation` (7207 ms)
- PASS `dynamic-spur-gear-creation` (6532 ms)
- PASS `catalog-digest-and-parameter-fail-closed` (12182 ms)
- PASS `two-part-hierarchical-self-contained-glb` (6170 ms)
- PASS `sealed-input-digest-and-rigid-transform` (18562 ms)
- PASS `path-ads-traversal-and-sealed-step-fail-closed` (49746 ms)
- PASS `dag-cycle-and-depth-bounds` (12442 ms)
- PASS `atomic-output-race-has-one-winner` (6187 ms)
- PASS `host-reparse-and-hardlink-rejection` (10 ms)
- PASS `no-residual-smoke-containers` (45 ms)
