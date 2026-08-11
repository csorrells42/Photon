# Photon full-source Hermes runtime

This directory defines the first release boundary that can put the patched Hermes backend into the running container. It builds the complete `source` checkout with its own `Dockerfile`; it does not copy a hand-maintained overlay and never selects `latest`.

## Modes

- `Development` accepts a dirty checkout. Every normal, safe source file is copied to a temporary immutable build context, recorded by path, byte length, and SHA-256, and incorporated into the content-derived image tag. The source is inventoried again after staging, so a concurrent edit aborts preparation.
- `Release` additionally requires a clean Git checkout, an exact 40-character commit, an explicitly configured expected upstream remote, and a release-lock path. A new lock is created once; an existing lock must match every bound field exactly. The current checkout is intentionally dirty, so no release lock is committed here.

Both modes reject reparse points, symlinks/junctions, and credential-file names. Runtime data, caches, dependencies, build-output directories, and Git metadata are excluded from both the manifest and staged context. The temporary context is deleted after preparation/build and is never a second editable source tree.

Example development preparation, without Docker:

```powershell
.\runtime\Build-HermesRuntime.ps1 -Mode Development -PrepareOnly
```

Example release build after the nested source has been committed:

```powershell
.\runtime\Build-HermesRuntime.ps1 `
  -Mode Release `
  -ExpectedSourceCommit 0123456789abcdef0123456789abcdef01234567 `
  -ExpectedUpstreamRemote https://github.com/csorrells42/Photon `
  -ReleaseLockPath .\release-inputs\hermes-source.lock.json
```

`Build-HermesRuntime.ps1` passes the exact source commit into the upstream Dockerfile and applies labels binding the source digest, Dockerfile digest, runtime protocol, authenticated Workbench mode, and locked Mem0/Qdrant versions.

After a real image build, verify and record an immutable generation:

```powershell
.\runtime\Verify-HermesRuntime.ps1 `
  -BuildRequestPath .\logs\runtime-builds\<source-digest>\runtime-build-request.json
```

Verification resolves the content-derived tag to an exact image ID, re-inspects that ID, compares every required label, then runs a network-disabled Python probe. The probe imports `mem0ai` and `qdrant-client`, checks their locked versions, enables the explicit Workbench mode, verifies the product identity marker, and requires the authenticated-memory and MCP patch files. Only then is a read-only generation lock written under `logs\runtime-generations`.

The checked-in `runtime-image.env.example` is a shape example only. It is not a runnable release identity.

Before adopting a verified generation, run the read-only preflight:

```powershell
.\runtime\Adopt-HermesRuntime.ps1 `
  -GenerationId <source-prefix>-<image-prefix> `
  -PreflightOnly
```

Adoption is a separate explicit operation. It revalidates the read-only generation files, exact image ID and labels, Compose resolution, and the currently running image retained for rollback. The generation commit binds a non-secret deployment fingerprint made from the exact `docker-compose.yml` bytes and the required Workbench/authenticated-Mem0 environment, including the private Qdrant endpoint `http://memory-vector:6333`. A matching image ID is not sufficient: adoption force-recreates `gateway` whenever either the committed fingerprint or the running required environment differs. It checks the post-recreate image and environment, checks health from inside that container, and atomically commits `logs\runtime-generations\runtime-generation.current.json` only after success.

Authenticated memory uses a separate receipt-pinned `memory-vector` Qdrant service. It runs non-root with a read-only root filesystem, has no published host port, stores only in the Docker-managed `memory-vector-data` volume, and must pass `/readyz` before the gateway starts. The Qdrant image is not folded into the Hermes gateway generation; `runtime\memory-vector.lock.json` binds its exact version, image index, and platform manifests.

A same-generation adoption preserves the last genuinely different rollback image. Legacy commits affected by the old self-referential `previous` bug fail closed unless `-RepairPreviousFromRuntimeIdentity` is explicitly supplied. That repair accepts only the local pre-commit `logs\runtime-identity.json`, validates its timestamp and immutable repository digest against an installed Docker image, and never invents a generation ID or rollback target.

```powershell
.\runtime\Adopt-HermesRuntime.ps1 `
  -GenerationId <source-prefix>-<image-prefix> `
  -Yes
```

Launch validates the committed lock, environment file, hashes, exact image ID, and all image labels before Compose. If no generation has ever been adopted, the launcher may use the installer-pinned bootstrap image. Once a current generation exists, the legacy upstream updater refuses to replace it.
