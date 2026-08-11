# Photon CAD container compliance

This directory defines an offline, fail-closed release-compliance companion for
the Photon CAD geometry and assembly containers. It does not build, load, run,
or download a container. CAD/model/library code remains inside the containers;
these scripts only validate already-supplied release evidence.

The current images are **not redistribution-ready**. The pinned maps intentionally
contain unresolved entries. `Export-PhotonCadCompliance.ps1` refuses to create a
receipt until every entry is resolved with exact legal artifacts and every
package that requires corresponding source has a complete source-offer record.

## Supplied input

Pass a new evidence directory containing `compliance-input.v1.json`. Paths in
the manifest must be relative, remain beneath that directory, and traverse no
reparse point. The manifest has this shape:

```json
{
  "schema": "photon.cad.compliance-input/v1",
  "buildxVersion": "the exact policy buildx version",
  "roles": [
    {
      "role": "geometry",
      "tag": "the exact policy tag",
      "artifacts": {
        "archive": { "path": "geometry/photon-cad-geometry.tar" },
        "imageIndex": { "path": "geometry/image-index.oci.json" },
        "imageManifest": { "path": "geometry/image-manifest.oci.json" },
        "attestationManifest": { "path": "geometry/attestation-manifest.oci.json" },
        "sbom": { "path": "geometry/sbom.spdx.intoto.json" },
        "provenance": { "path": "geometry/provenance.intoto.json" },
        "pythonInventory": { "path": "geometry/python-packages.json" },
        "debianInventory": { "path": "geometry/debian-packages.json" },
        "sourceOffer": { "path": "geometry/debian-source-offer.json" },
        "legalManifest": { "path": "geometry/legal-files.manifest.json" }
      }
    },
    { "role": "assembly", "tag": "the exact policy tag", "artifacts": {} }
  ]
}
```

The assembly entry has the same ten artifact keys. The five JSON evidence files
below use these schemas:

- Python/Debian inventory: `photon.cad.package-inventory/v1`, exact `role`, and
  `packages` containing unique `{ ecosystem, name, version, classification }`.
  Classifications are `direct`, `transitive`, or `unlinked` for Python and
  `direct`, `transitive`, or `base` for Debian. `unresolved` must be empty.
- Legal manifest: `photon.cad.legal-manifest/v1`, exact `role`, and one package
  record for every inventory package. Each record contains `ecosystem`, `name`,
  `version`, a nonempty SPDX `licenseExpression`, `resolutionStatus: resolved`,
  `sourceOfferRequired`, and one or more legal files. Legal-file paths must be
  below `<role>/licenses/`; each file carries `kind`, lowercase SHA-256, and byte
  length. `unresolved` must be empty.
- Source offer: `photon.cad.source-offer/v1`, exact `role`, and resolved entries
  `{ packageKey, sourceUri, sourceSha256, status }` for every package whose legal
  record sets `sourceOfferRequired`. `sourceUri` must be HTTPS. `unresolved` must
  be empty. `packageKey` is the canonical lowercase ecosystem and package name
  plus the exact version: `ecosystem|name|version`.

The exporter validates raw OCI JSON digests, attestation subjects, SPDX 2.3,
the exact package/legal/source-offer closure, all legal-file bytes, required
NOTICE hashes, the pinned manual-review maps, and archive hashes. It then copies
only compliance evidence (not the large runtime archives), writes
`compliance-receipt.v1.json`, and writes its non-circular detached digest to
`compliance-receipt.v1.json.sha256`.

```powershell
.\Export-PhotonCadCompliance.ps1 `
  -InputDirectory C:\release-evidence `
  -OutputDirectory C:\release\compliance

.\Verify-PhotonCadCompliance.ps1 `
  -ComplianceDirectory C:\release\compliance `
  -RuntimeArchiveDirectory C:\release-evidence
```

The output directory must not exist. A failed export may leave a partial new
directory for diagnosis; the scripts never delete or overwrite evidence.
Detached SHA-256 proves transport integrity, not publisher identity. Signing is
a separate release concern.

## Remaining required integration

Root still owns the existing Dockerfiles and build/release scripts. Before a
redistribution claim, those seams must correct the aggregate Apache-only image
labels, add the PartCAD modification notice, export attestations before BuildKit
history can be pruned, and make this verifier a mandatory release gate.
