# Photon CAD industrial container verification

Run `2e740804b0044a61839e7867d1484e03` passed 23/23 hardened scenarios in 332963 ms.
Window: `2026-08-15T22:02:09.5206146+00:00` through `2026-08-15T22:07:42.4838254+00:00`.

## Immutable identities and evidence

- Exact base image: `sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703` (lookup tag `photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb`)
- Exact derived image: `sha256:1f5b532d241cc7139e22e03ba7d3a2773acd58b5fcaacef93e7f0e2d741371e0`; all scenarios ran this ID, never a friendly tag
- Friendly tag promoted only after the receipt was durable: `photon-cad-industrial:0.1.0-b123dmcp-0.3.80-bdw0.2.0`
- Immutable receipt: `runs/receipt-2e740804b0044a61839e7867d1484e03.json` (`sha256:fd831217e23baf638c78a29326ccd570b1e9e67037b4e1f840da1ae47761d65b`)
- Catalog: `sha256:aae5554ce9e57133f508e3343663704d35c82e6a31e5201f6224a9b299ddf6b6`; bd_warehouse package content: `sha256:2e1b8d0a41f0562071c3fdc5cbe803871e3c94fdf8927b0238550b6844aea5b9`

## Sealed artifacts

- Box STEP: `sha256:9c21a5e0dc3f47e17ae61a8900403d7ea8b104e0fbb049002341905ea980a6c8`
- Cylinder STEP: `sha256:4d2c3487052a2172b76390a7b2995b53e2835a31c3c19093b8f8c0d08ffd0171`
- Discovered bearing STEP: `sha256:4ea8b134dc70661d29c0b0797976fb1e532a00f7d9365b42c1e79ecfd946da63`
- Spur gear STEP: `sha256:3cd437e4710ba7a040d764b6d24b54ae7ca8812c45181705d372ff4ba5e9759b`
- Self-contained tagged GLB: `sha256:986bda476bca2a812b24068ebc86e7c11a2c223b5db37394d0793b17d3a176bc`

## Security result

The verified runner used network-none, a read-only root, non-root UID/GID 65532, all capabilities dropped, no-new-privileges, bounded CPU/memory/PIDs, separate read-only input and read-write output mounts, host deadlines/output caps, and exact container kill/remove/absence checks. Strict JSON, hostile STEP, path/ADS/traversal, DAG, 15-value transform, atomic output race, hard-link and supported reparse-point, timeout, and stdout-flood cases failed closed.

Redistribution remains **blocked pending an independent dependency/license review**. This report makes no aggregate license, compliance, or SBOM claim.

## Source hashes

- `sha256:97c02fec729c2a306470b8ea73807ca05ddbdb0d0a05d44a3ee777cfafb5ccc4`  `src/Host/PhotonCadIndustrial.ContainerSmoke/HardenedDockerHarness.cs`
- `sha256:5076da3ac7d88d021a193ba1e2557a3c934f57e0f161cbc45d87ea9c5920e401`  `src/Host/PhotonCadIndustrial.ContainerSmoke/PhotonCadIndustrial.ContainerSmoke.csproj`
- `sha256:f8cc2b163cfdd38f3ccb8b74ce4317df9833fba3b53716dfee1a1cb652366f87`  `src/Host/PhotonCadIndustrial.ContainerSmoke/Program.cs`
- `sha256:fae78fb2fc6bb033c33f19ec8571a5cc2ae4c619d38050a969cf4d0323353c7f`  `src/Host/PhotonCadRuntime.Container/industrial/Dockerfile`
- `sha256:154321a9447066fe35fc454f49dd89a0176ccd8ae148504d13aa109ff89f30bd`  `src/Host/PhotonCadRuntime.Container/industrial/README.md`
- `sha256:3ebcc7a2f425157442db35d6bd0915ecfd9d99b6658dad59c494cb0952a503d9`  `src/Host/PhotonCadRuntime.Container/industrial/fixtures/catalog.request.json`
- `sha256:a3e2551b78c641c462145e9a1d53f36bc2d3ed1184bdf2df6a2c74d49c11ad1a`  `src/Host/PhotonCadRuntime.Container/industrial/fixtures/protocol-golden-v1.json`
- `sha256:f62b85f7a42778eaf7c66f48341f2196362923b3f389575f5694596de42d1f04`  `src/Host/PhotonCadRuntime.Container/industrial/photon_industrial_adapter.py`
- `sha256:2bfab3a6e0f78969a2a7d266228f56cced5d01869bf487f12329574674d187c3`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/catalog-v1.schema.json`
- `sha256:93b0e36cd8310651ae530c77aae994a11a2d3fbab6b03d9ef1a30193a0f392f9`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/request-v1.schema.json`
- `sha256:740bf73ee0961efc07e31c290966124af78fddb0f352009c5f48571d9581ab9a`  `src/Host/PhotonCadRuntime.Container/industrial/schemas/response-v1.schema.json`

## Scenarios

- PASS `exact-base-image-identity` (160 ms)
- PASS `immutable-base-staging-binding` (136 ms)
- PASS `offline-derived-image-build` (9715 ms)
- PASS `strict-json-hostile-inputs` (59353 ms)
- PASS `mutable-tag-drift-detection-simulation` (0 ms)
- PASS `host-deadline-kills-exact-container` (1366 ms)
- PASS `stdout-cap-kills-exact-container` (578 ms)
- PASS `deterministic-catalog-bytes-and-identity` (12333 ms)
- PASS `box-6000-and-new-only-output` (11882 ms)
- PASS `cylinder-formula` (5953 ms)
- PASS `manual-rectangle-circle-extrude-cut-hole-and-preview` (73329 ms)
- PASS `manual-protocol-hostiles-and-unimplemented-edit-operations` (30737 ms)
- PASS `named-transformed-step-assembly-inspection-and-preview` (12465 ms)
- PASS `dynamic-bearing-creation` (6876 ms)
- PASS `dynamic-spur-gear-creation` (6210 ms)
- PASS `catalog-digest-and-parameter-fail-closed` (12095 ms)
- PASS `two-part-hierarchical-self-contained-glb` (6307 ms)
- PASS `sealed-input-digest-and-rigid-transform` (18253 ms)
- PASS `path-ads-traversal-and-sealed-step-fail-closed` (47250 ms)
- PASS `dag-cycle-and-depth-bounds` (11913 ms)
- PASS `atomic-output-race-has-one-winner` (5934 ms)
- PASS `host-reparse-and-hardlink-rejection` (13 ms)
- PASS `no-residual-smoke-containers` (40 ms)
