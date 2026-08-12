# Functionality-First Deferred Security Ledger

Date: 2026-08-12
Owner: Architect
Disposition: documentation-only consolidation; remediation begins only after the full functionality matrix is proven

## Purpose

This index consolidates the security concerns already recorded by SPAT, SuperMax, and Architect without reopening or remediating them during the active functionality pass. The source ledgers remain authoritative for their detailed evidence and acceptance directions.

No secret value is copied into this document.

## Source ledgers

1. `docs/coordination/DEVELOPER-FUNCTIONALITY-DEFERRED-SECURITY.md`
   - SPAT developer-services findings DS-01 through DS-10.
2. `docs/coordination/CAD-FUNCTIONALITY-DEFERRED-SECURITY.md`
   - SuperMax CAD findings CAD-DS-01 through CAD-DS-05.
3. `C:\Users\clsor\AppData\Local\hermes\handoffs\CAD\ARCHITECT-CAD-SECURITY-LEDGER-NOTICE.md`
   - Architect intake notice pointing to the canonical CAD ledger; no additional finding beyond CAD-DS-01 through CAD-DS-05.

## Consolidated queue

| ID | Area | Concern | Functional status | Security-pass disposition |
|---|---|---|---|---|
| DS-01 | Serena | Workspace identity can drift from operator intent | Functionality proven; identity hardening deferred | Bind semantic requests to host-owned canonical workspace identity |
| DS-02 | Serena | Standalone loopback MCP exposes more tools than Workspace Search consumes | Narrow adapter works | Evaluate Windows-user binding or a narrow host proxy |
| DS-03 | Debugger | Attach authorization lacks an independent native confirmation | Typed attach works | Add short-lived host-owned confirmation if selected |
| DS-04 | Debugger | DAP evaluation can have target-process side effects | Evaluation works | Define read-like versus side-effecting policy |
| DS-05 | Developer tooling | Validation payloads are user-writable test installations | Validation evidence remains usable | Prove production installer provenance and ACLs |
| DS-06 | Credentials | Ignored runtime state contains a live-shaped plaintext OpenRouter credential | Functionality not invalidated; value never copied here | Revoke/rotate and migrate to Windows-bound credential custody |
| DS-07 | .NET | Compiler/test authority is PATH-discovered rather than receipt-bound | Build/test works | Bind execution to one revalidated absolute identity |
| DS-08 | .NET tests | Explicit project tests execute workspace code | Intended functionality | Add host-owned trust/confirmation policy |
| DS-09 | Python tests | Unittest executes workspace code in a read-write mounted container | Intended functionality | Evaluate read-only disposable source snapshot and bounded output |
| DS-10 | Python/Docker | Tooling depends on local Docker daemon authority | Functionality proven | Attest Windows Docker Desktop ownership/provenance |
| DS-11 | Arduino toolchain | Published Arduino inventory contains a secret-like installation metadata field | Receipt-bound inspect/compile works; no value copied or exposed | Review whether the field is required, rotate if sensitive, and remove it from distributable inventory before public release |
| CAD-DS-01 | CAD runtime | Industrial evidence is local-engineering-only; redistribution blocked | Local functionality works | Resolve licensing/provenance before public distribution |
| CAD-DS-02 | CAD/Docker | Local Docker daemon remains an external trust dependency | Exact-image runner works | Independently attest daemon/image-store boundary |
| CAD-DS-03 | CAD preview | One-use in-memory preview delivery still needs abuse/variance review | Preview functionality works | Review exhaustion/cache/request concurrency without weakening custody |
| CAD-DS-04 | CAD export | Generic STEP export is create-only | Current tranche works | Review overwrite, reparse, and recovery authority before expansion |
| CAD-DS-05 | CAD cleanup | Temporary workspace cleanup is best-effort after custody release | Does not block CAD use | Add bounded durable cleanup journal for bridge-owned paths only |

## Execution order

1. Complete Docker Status.
2. Complete Arduino, Raspberry Pi, and debugger functionality.
3. Complete browser, Help, and Google Maps functionality.
4. Run the full-system functionality matrix and keep closing missing features.
5. Reconcile any new security notes into this index without exposing secrets.
6. Only then triage and remediate the queue by validated severity.

## Current non-security functionality observation

An earlier broad-smoke Credential Manager error (`Win32Exception 1312`) was isolated to the managed sandbox's NTLM logon token. The exact round trip and complete desktop smoke pass under Chris's interactive CloudAP identity, so no product bypass or softened denial was introduced.
