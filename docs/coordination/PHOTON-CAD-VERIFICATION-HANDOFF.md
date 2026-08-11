# Photon CAD canonical verification handoff

Date: 2026-08-11  
Status: isolated source complete; shared mount intentionally not performed

## Owned scope

This lane added only:

- `src/Host/PhotonCadVerification/**`
- `src/Host/PhotonCadVerification.Smoke/**`
- this handoff

It did not modify the desktop bridge, MainWindow, HermesDesktop project, renderer, container/image, IndustrialProvider, ManualProvider, Assembly provider, file conversion, or any existing smoke.

## Verification authority

`PhotonCadCanonicalVerifier` accepts either a bounded seekable stream or canonical bytes plus an exact custody request containing:

- expected byte length;
- expected physical file SHA-256;
- expected canonical logical content digest;
- expected canonical BOM digest;
- clean-project requirement.

The verifier preflights the 128 MiB codec ceiling before allocation, exact-reads a stream without taking ownership, and isolates caller-owned memory before hashing and decoding. Canonical decode remains the authority for framing, Unicode/numeric constraints, graph semantics, operation/provenance binding, embedded digest/length checks, and the codec's strict STEP/GLB media checks.

Above that baseline, this module independently verifies:

1. exact canonical custody and clean-state binding;
2. one-root occurrence DAG and complete parent reachability;
3. finite, right-handed rigid 4x4 transforms;
4. equality between authoritative STEP owners and occurrence sources;
5. BOM recomputation from occurrences, including one row per source, exact `Each` quantity, and part-number correspondence;
6. BOM canonical digest binding;
7. exact geometry-entity to authoritative-STEP ownership correspondence;
8. artifact operation, capability, and source-provenance binding;
9. artifact byte length, SHA-256, and media envelope;
10. exact GLB `photonEntityId` coverage and GLB/occurrence transform correspondence;
11. generic STEP source readiness for every visible, unsuppressed geometry entity.

The public report type rejects contradictory `Verified`/`Complete` flags. Failed, unavailable, and passed states cannot be silently relabeled by a caller.

## Honest unavailable checks

The provider-neutral verifier does **not** claim any of these without the exact geometry container:

- B-rep solid validity;
- assembly interference;
- dimensional conformance.

They are returned as `Unavailable` with reason `geometry_container_required`. A structurally verified project therefore has `Verified=true` and `Complete=false` until a trusted geometry authority supplies those checks. This is deliberate; no mesh heuristic is promoted to a CAD verification result.

## Evidence

- Release build: 0 warnings, 0 errors.
- Isolated hostile smoke: 14/14.
- Production-identical repeat: 10 consecutive 14/14 runs.
- `dotnet format --verify-no-changes`: clean for production and smoke projects.
- Hostiles cover physical-byte tamper, custody mismatch, stale BOM, ambiguous BOM source, missing occurrence coverage, preview tag drift, occurrence cycle, non-rigid transform, pre-cancellation, oversize preallocation rejection, absent preview, and contradictory report construction.

## Narrow later mount seam

After current shared writers freeze, mount only through these explicit seams:

1. Add a `PhotonCadVerification` project reference to `HermesDesktop.csproj`.
2. Add a typed `photonCad.verify` route in `PhotonCadBridge.cs`; the host must source expected byte length and digest from atomic storage/readback evidence rather than recomputing an "expected" value from the same untrusted buffer.
3. Add the exact case to `MainWindow.xaml.cs` message dispatch.
4. Project the bounded check list into the existing Verify phase in `PhotonCadDesktopWorkspace.tsx` and focused tests.
5. Keep runtime paths, storage targets, artifact handles, exception text, and container identities out of renderer frames.

The geometry-container verifier may later append solid/interference/dimension results, but it must consume the same project identity, revision, canonical digest, and artifact digests. It must not replace or weaken this canonical verification report.
