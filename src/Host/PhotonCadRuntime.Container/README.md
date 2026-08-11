# Photon CAD runtime bundles

This directory builds the two Linux/amd64 runtime images used by Photon CAD. They are intentionally separate:

- `photon-cad-geometry` contains build123d-mcp, build123d, bd_warehouse, OCP, and the bounded stdio geometry process.
- `photon-cad-assembly` contains PartCAD's foreground JSON-RPC service with a Photon-owned method allowlist.

The images are build-time network consumers and runtime network-denied artifacts. A release must ship verified image archives plus `bundle-receipt.json`; the installed application must never resolve a floating registry tag.

## Pinned upstream identity

| Bundle | Upstream | Revision | Source archive SHA-256 |
| --- | --- | --- | --- |
| Geometry | `pzfreo/build123d-mcp` 0.3.80 source | `8fb7cefb28daa7f9f68b7e653244256f09b22b1f` | `d307c94e1fc94d2c634736a34ffbabc037b9186351915cf16aa60e7db8dad97f` |
| Assembly | `partcad/partcad` 0.7.158 development source | `b77c2c08f4a63b97070fcaf9e26bc09f34f95037` | `ec4be69fe9b5a864ee599e9939e91524398b4cc5af59cae081a253c5f59f5560` |
| Base | Python 3.12.11 slim Bookworm, linux/amd64 | Docker manifest | `sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49` |

The upstream source archives are fetched only by BuildKit and are accepted only when their checksums match. The build123d-mcp `uv.lock` and PartCAD `poetry.lock` supply the dependency closure. The produced image IDs and saved-archive hashes are captured in the release receipt.

## Runtime boundary

The desktop broker must launch exactly one disposable container per open CAD session with the policy in `runtime-policy.json`:

- stdio only; never publish a port or mount the Docker socket;
- `--network none`, read-only root, all Linux capabilities dropped, and `no-new-privileges`;
- fixed non-root UID/GID 65532;
- bounded CPU, memory, PID count, request time, and temporary filesystems;
- exactly one host-created project directory mounted at `/workspace`;
- no API keys, host home, credential stores, Docker configuration, or arbitrary paths mounted;
- geometry and assembly images never share a Python environment or writable session directory.

build123d-mcp's Python sandbox is defense in depth, not the security boundary. The disposable no-network container is the boundary. PartCAD runs offline with telemetry disabled, Python runtime `none` inside the already isolated container, and an explicit operation allowlist that omits install, update, daemon, telemetry, provider, HTTP, and package-refresh methods.

## Build and verify

From this directory in PowerShell:

```powershell
.\Build-PhotonCadRuntimeBundles.ps1 -OutputDirectory C:\path\to\new-output-directory
.\Test-PhotonCadRuntimeBundles.ps1
```

The output directory must not already exist. The build script loads deterministic local tags for testing, saves both images as transportable tar archives, and writes `bundle-receipt.json`. Installer/update/rollback code is a shared integration seam and is intentionally not implemented here.

The test script verifies image identity, non-root execution, import/version health, entrypoint immutability, and the runtime-policy flags. It does not claim mounted Photon UI, end-to-end CAD operations, or Inventor acceptance.

