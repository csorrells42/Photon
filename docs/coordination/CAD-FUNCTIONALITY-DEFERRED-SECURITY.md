# Photon CAD Functionality — Deferred Security Notes

Date: 2026-08-11  
Scope: Photon CAD industrial runtime, project persistence, preview custody, verification, assembly, and generic STEP export  
Disposition: recorded for Architect and deferred until the functionality acceptance is complete

## Policy for this pass

The active objective is complete, observable CAD behavior. These items are not being used to redefine or reduce the functionality target. Existing fail-closed identity, bounds, pathless renderer, digest, exact-image, and atomic-persistence controls remain in force; this ledger records security or release work that is deliberately outside the current functionality pass.

## CAD-DS-01 — Industrial container evidence is local-engineering-only

Observed current state:

- The accepted industrial runtime is bound to an immutable local image and receipt and executes the exact image ID rather than a friendly tag.
- The receipt states that redistribution is blocked.
- Current live acceptance proves the local exact-image workflow; it is not a public installer or redistribution claim.

Deferred direction: establish redistributable licensing/provenance, installer-owned immutable image deployment, and production update/rollback evidence before a public release claim.

## CAD-DS-02 — Local Docker daemon trust remains an external dependency

Observed current state:

- The desktop invokes a fixed local Docker executable through the hardened industrial runner.
- The renderer receives no Docker socket, executable path, arguments, environment, container ID, or filesystem path.
- Exact-image, network-none, non-root, capability-drop, timeout, output-bound, and cleanup checks are covered by the industrial acceptance suite.

Deferred direction: independently harden and attest the local daemon and image-store boundary. Do not mount the Docker socket into Photon or broaden the renderer contract.

## CAD-DS-03 — Preview delivery is intentionally one-use and in-memory

Observed current state:

- GLB bytes are sealed only from the committed canonical project readback.
- Preview URLs are same-origin, opaque, bounded, generation/session/project/revision-bound, and one-use.
- A renderer restart or navigation retires the old preview generation.

Deferred direction: complete an independent abuse/variance review of resource exhaustion, cache interaction, and WebView request concurrency after the live functionality proof. Preserve one-use custody and never fall back to filesystem paths.

## CAD-DS-04 — Generic STEP export is create-only in the functionality tranche

Observed current state:

- The renderer protocol is pathless and selects only a canonical Body or Part at the exact clean project revision and content digest.
- The native destination authority is intended to create a generic Part-21 copy without renderer-visible paths.
- Overwrite and release-package claims are intentionally excluded from this tranche.

Deferred direction: security-review destination replacement/overwrite authorization, junction/reparse behavior, and durable recovery before enabling overwrite or batch export.

## CAD-DS-05 — CAD runtime workspace cleanup is best-effort after custody release

Observed current state:

- Runtime and preview capabilities are revoked before navigation/reset generation replacement.
- A prior review found that recursive deletion failure of the bridge-owned temporary runtime workspace is logged/best-effort rather than represented as retryable retained cleanup custody.

Deferred direction: add a durable, bounded cleanup journal for bridge-owned temporary workspaces without exposing paths or weakening shutdown. This is cleanup debt, not authority to delete arbitrary directories.

## Architect notification

Architect should treat this file as the canonical CAD security-deferment ledger for the functionality-first pass. Any additional security issue found during final publish/live acceptance must be appended here with its observed evidence and future acceptance direction.

