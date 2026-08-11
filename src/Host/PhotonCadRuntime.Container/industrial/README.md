# Photon CAD industrial container tranche

This directory is a new-only, container-owned adapter over the exact local
geometry image `photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb`
(`sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703`).

The adapter accepts exactly one bounded JSON document on standard input and
returns exactly one bounded JSON document on standard output. It has four
operations: deterministic catalog discovery, box/cylinder creation, typed
catalog-item creation, and GLB preview generation from digest-bound sealed part
inputs plus a rigid occurrence hierarchy.

Security and ownership invariants:

- CAD/model/library code executes only inside the non-root, network-disabled,
  read-only container. Host code may coordinate I/O and policy only.
- The request cannot contain Python, module, class, file path, command, package,
  import, or executable names. Catalog IDs are opaque and bound to the catalog
  digest and installed package content identity.
- Only fixed bd_warehouse modules are imported. Constructor parameters are
  limited to scalar, enum, and bounded choices discovered from that fixed code.
- Input artifacts are addressed by bounded slots, verified by SHA-256, copied
  into private container storage, then parsed. Output files are new-only and
  published atomically; an existing destination fails closed.
- Sealed STEP inputs are mounted separately at `/photon-input` read-only.
  New-only STEP/GLB results are written under `/photon-output`; the host must
  independently reopen, bound, and hash each result before accepting it.
- The GLB is self-contained, uses one tagged node per occurrence, has one
  embedded buffer, and emits no URIs, textures, images, animation, or skins.
- O-rings, retaining rings, and shaft keys remain visible but unsupported in
  bd_warehouse 0.2.0. No substitute geometry is invented.

The Dockerfile intentionally installs nothing. The independent smoke project
must bind an unguessable staging reference to the exact base image ID, verify
its root-filesystem ancestry/configuration before and after the build, and run
every scenario by the exact derived image ID. Building this Dockerfile directly
or running a friendly tag is not release evidence.

This tranche is for local engineering evaluation only. Redistribution is
blocked pending an independent license/dependency review. The image deliberately
makes no aggregate license or compliance claim: its Dockerfile explicitly
clears the inherited aggregate-license label instead of repeating it. This
repository does not invent an SBOM for inherited contents it has not
independently verified.

## Canonical host handoff

Successful part creation returns an authoritative sealed artifact as exactly
`{ format: "step", contentDigest, byteLength }`. It contains no filesystem path,
temporary slot, handle, Python name, or executable name. The host must store the
exact returned bytes under that digest before treating the mutation as
persistent. `createPrimitive` also returns normalized primitive provenance;
`createCatalogItem` returns the catalog digest, opaque item ID, and validated
scalar parameters. Re-running a constructor is not a substitute for retaining
the sealed bytes.

Successful preview creation returns the GLB artifact as exactly
`{ format: "glb", contentDigest, byteLength }`, bounds, and canonical provenance.
The provenance contains digest-and-length-bound `sourcePartId` entries and
sorted occurrences with `entityId`, `sourcePartId`, nullable `parentEntityId`,
and a 16-value row-major rigid local transform. The response deliberately omits
the request's temporary `inputSlot`; that slot is transport state, never project
state. Preview data remains derived and non-authoritative even though its bytes
are sealed.
