# Hermes Workbench functionality morning review

Date: 2026-08-12 (America/Chicago)
Owner: Architect
Policy: functionality first; security concerns are recorded in `FUNCTIONALITY-FIRST-DEFERRED-SECURITY-LEDGER.md` and are not remediated during this pass unless they directly block operation

## Executive result

The current authoritative source is broadly green. A coherent desktop/runtime publish completed after the original audit snapshot; mounted acceptance is in progress.

- Frontend: **130 test files / 801 tests passed**.
- Strict TypeScript: **passed**.
- Production Vite build: **passed, 3,289 modules**. The existing large-chunk advisory remains non-blocking.
- `HermesDesktop.Smoke` Release build: **0 warnings / 0 errors**.
- Complete native desktop smoke under the real interactive Windows identity: **passed**, including Credential Manager, Browser/Maps, Docker Control Center, Developer Services, Roslyn, ConPTY terminal, and the Codex app-server handshake.
- Photon CAD focused desktop smoke: **passed**.
- Industrial CAD provider: **18/18**.
- Assembly provider: **8/8**.
- STEP conversion: **9/9**.
- Manual CAD provider: **6/6** after restoring the exact receipt-bound image selection and preserving strict byte/semantic pinning.
- Repository whitespace check: **passed** on the current working changes.

The coherent publish/relaunch is complete. The current runtime uses source commit `feb317bc25a595a73a4bfa95a2d6eedbe5ec130c` and immutable image `sha256:d0e9ef433c24004fdee7a0fde6f8b2fff7d11823adf890e51ae08ec60dc0fb40`. The mounted renderer connected with Photon ready, authenticated Mem0 ready, and exact server-published model/reasoning state. Native visual acceptance remains in progress because one launcher session later lost its top-level WPF handle while the process stayed alive; a subsequent exact launcher restart restored a responding titled window. This lifecycle anomaly must remain visible until stability is proven.

## Current mounted runtime snapshot

Read-only observation, with no service or process mutation:

| Runtime | Observed state |
|---|---|
| Hermes desktop bridge | Listening on `127.0.0.1:8972`; health HTTP 200 |
| Assistant conversation bus | Published service listening on `127.0.0.1:9072`; health HTTP 200 |
| Serena | Listening on `127.0.0.1:9121` |
| LM Studio | Listening on `127.0.0.1:1234`; model catalog HTTP 200 |
| Vite Workbench | Listening on `127.0.0.1:4173` |
| Docker Engine | Reachable, version 29.6.2 |
| Hermes container | Running on an immutable image identity |
| Qdrant | Running and healthy on an immutable image identity |
| Docker Model Runner | Running; one embedding and one completion model loaded during observation |
| Published developer assets | Arduino, Roslyn, JDT, NetCoreDbg and CAD runtime roots exist |

The launcher-scoped `HERMES_IMAGE_REFERENCE` is intentionally absent from an arbitrary audit shell; therefore a raw `docker compose` command outside the launcher is not an authoritative lifecycle check.

## Functionality status by priority

| Priority | Surface | Current status | Exact remaining work |
|---|---|---|---|
| P0 | Manual CAD patterns/evidence | **Source gate closed, 6/6** | Source selection now resolves to the exact published receipt/image identity; verifier accepts only the exact pinned selection in CRLF or strict LF-normalized form. Include the complete mounted 0→2→4→6→8→10 proof in the coherent acceptance pass. |
| P1 | Docker Status / Control Center | **Integrated proven** | Polished status, health, resources, approved lifecycle review, logs and Model Runner inventory/unload pass 19 focused tests and native smoke. Publish and visibly prove the current page. Generic Docker command execution remains intentionally absent. |
| P1 | Arduino | **Integrated proven; stale live UI** | Real receipt-bound Blink inspect/compile produced five verified artifacts. Publish the executable-adjacent asset-root fix and confirm `2/2 Host verified` for an `.ino`. Upload remains unavailable until a reviewed opaque port authority exists. |
| P1 | .NET debugger discovery | **Integrated proven; stale live UI** | Bounded reparse-safe traversal now skips inaccessible data/log/metadata/dependency roots and preserves valid targets. Publish, Refresh targets, select a valid Debug target and run one visible launch/stop cycle. |
| P1 | Raspberry Pi | **Native setup + receipt-bound OpenSSH integrated** | A native owned Configure dialog collects host/user/port/host-key and accepts the private key only through a Windows PasswordBox into Credential Manager. The renderer receives only an opaque target ID/result. A new installer copies only Microsoft-signed `ssh.exe`, `scp.exe`, LICENSE and NOTICE from Windows into the private toolchain, pins exact length/SHA-256 in the existing receipt format, replaces repeat installs deterministically and removes rogue files. Provisioning smoke passed and the current published payload is installed. A real Pi target/credential is still required for mounted inspection; no connection was attempted. Deploy remains review-gated and has no renderer handler. |
| P1 | Browser, Help and Google Maps | **Integrated proven** | Source supports strict navigation, race-safe native visibility, retained/deduplicated Hermes Help, Maps search and typed directions. Publish and visibly test Help, Maps, history, panel switching and click interception. No Maps API key or route-verification claim. |
| P1 | Voice input/output | **Provider loop proven; mounted UI pending** | The durable target contains pinned Kokoro/faster-whisper packages and both digest-verified Kokoro assets. Real Kokoro synthesis produced a 95,276-byte WAV and local faster-whisper transcribed it back as `photon local voice acceptance.` A narrow source fix now permits only the fixed application-owned audio cache to bypass the workspace safe-root category; credential/system denials remain authoritative. Rebuild the container, then prove microphone, preview, profile save and read-aloud in the mounted UI. |
| P1 | Coherent desktop acceptance | **Published; mounted proof in progress** | Frontend/desktop/runtime were rebuilt and launched through `Launch-Hermes.ps1`. Mounted web state proves Photon ready, Mem0 ready and model/reasoning metadata. Complete native UI proof remains pending and the top-level-window lifecycle anomaly above is still open. |
| P2 | Manual CAD fillet/chamfer | **Unavailable honestly** | Requires stable topology/edge identity; do not approximate with all-edge operations. |
| P2 | CAD interference verification | **Unavailable honestly** | Add a trusted overlap-analysis provider; preview geometry is not verification evidence. |
| P2 | IPT/IAM conversion | **Conditional / authority absent** | Generic STEP works. Inventor formats require a receipt-bound Autodesk conversion authority. |
| P2 | Java/JDT | **Integrated proven** | Focused lifecycle is green and payload is present; run one mounted Java semantic-operation spot check. |
| P2 | C/C++/GCC | **Integrated proven** | Hardened compiler suite is green. Publish and visibly prove C17/C++20 selection, diagnostics and artifact output. |
| P2 | MCP and Skills | **Source proven** | Typed operational routes are present: MCP list/catalog/create/test/enable-disable/remove/OAuth/catalog install; Skills list/search/preview/scan/toggle/install/uninstall/update. Run mounted reversible spot checks. |
| P2 | Profiles | **Read-only by contract** | Add a bounded reviewed profile-write transaction before enabling edits; current backend rejects mutations intentionally. |
| P2 | Session Administration | **Read/export only by contract** | Current list/stats/descendants/export works. Branch/import/delete/prune/model locks need durable native transactions before UI enablement. |
| P2 | Source Control | **Integrated status/browse only** | Native status/refresh/cancel and Git Extensions browse work. A real internal stage/commit/push authority is still missing. |
| P2 | Usage Intelligence | **Source proven for supported providers** | Run mounted collector spot checks. Consumer subscription allowances and unsupported per-key provider data must remain unavailable unless official data exists. |
| P2 | Conversation bus multi-party room | **Photon path live; full room incomplete** | Launch Ali and Scarlett and complete direct/group/restart/offline acceptance with the sender-first, recipient-second envelope. |

## Environment-specific findings closed during this pass

### Windows Credential Manager error 1312

This is not a product defect. The exact synthetic Save/List/Read/Delete probe failed under the managed sandbox's NTLM token but passed 10/10 under Chris's normal interactive CloudAP token. The complete native desktop smoke also passed Credential Manager under the interactive identity. Production behavior remains strict; error 1312 was not ignored or softened.

### Restricted NuGet configuration access

The Developer Services smoke failed only when the managed sandbox denied access to the user's NuGet configuration. The identical smoke passed under normal user access. No source workaround was added.

## Serialized publish and visible acceptance checklist

1. Preserve the completed frontend 803/803, production build, desktop Release 0/0, native smoke, CAD-focused smoke, manual CAD 6/6 and whitespace gates.
2. Preserve the completed coherent desktop/frontend/runtime publish and immutable image identity recorded above.
3. Continue launching only through `Launch-Hermes.ps1`.
4. Require a responding desktop with a nonzero top-level HWND and confirm ports 8972, 9072 and 9121.
5. In the visible UI, confirm:
   - Docker Status shows engine, Compose, approved services, resources and loaded models.
   - Arduino shows Host verified and compiles a disposable Blink sketch.
   - Raspberry Pi says Setup required rather than installed.
   - Debugger Refresh targets survives inaccessible workspace directories and launches a valid Debug target.
   - Help opens/focuses one retained Hermes Help browser tab.
   - Maps search and one directions request open correctly; switching away never leaves an invisible click-blocking WebView.
   - CAD manual add/cut/hole/pattern, assembly, STEP import/export, preview, save/reopen and verification behave on the mounted desktop.
   - Voice microphone, preview, saved voice and read-aloud work locally if the voice lane is mounted by then.
6. Continue the remaining reversible mounted checks row-by-row from `FUNCTIONALITY-COMPLETION-MATRIX.md`.
7. Only after functionality is proven, validate and remediate `FUNCTIONALITY-FIRST-DEFERRED-SECURITY-LEDGER.md` by severity.

## Deferred security boundary

No secret value is reproduced here. A secret-like Arduino installation metadata field was observed in published inventory and has been added to the deferred ledger as DS-11. It does not invalidate the receipt-bound compile proof, and it will not be remediated until the functionality-first pass reaches the security phase.
