# SuperMax CAD functionality handoff

Date: 2026-08-11  
Owner after this handoff: SuperMax  
Previous owner: root/Codex  
Repository: `C:\Users\clsor\Documents\Codex\HermesAgent`  
Branch: `agent/hermes-functionality-checkpoint`  
Last pushed checkpoint: `b079a38ef4b2bb7c0e7eb6ae71b6c6b1504d64e3`

## User objective

Finish Photon CAD functionality before moving to the rest of the product. The required order is CAD, compiler, debugger, then the remaining program functionality. Security findings should be documented and deferred unless they directly block functionality. Arduino, Java, and GCC are lowest priority and are enabled later in that order.

SuperMax owns CAD functionality end to end after this handoff: provider, host bridge, renderer, persistence, preview, verification, generic STEP export, publish, and visible acceptance.

## Current real-image acceptance — 2026-08-11 09:49 CDT

The frozen current tree passed the production-path exact-image industrial acceptance:

```text
dotnet run --project src\Host\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release -- --photon-cad-live-industrial
```

Result: exit 0 in 46.8 seconds. The run used the live pinned industrial provider and proved:

- exact host catalog discovery returned `available / ready`;
- a new canonical millimeter project was created;
- one source-exact bearing committed at revision 2 and its sealed GLB resolved and was read;
- one exact Spur Gear committed at revision 4 and its complete-project GLB replaced the bearing-only preview;
- refresh, clean close, and reopen returned the exact saved revision 4 project;
- committed verification passed for valid solids, dimensions, assembly structure, and export readiness;
- canonical decode proved two source entities, two occurrences, two derived BOM rows, four ordered applied operations, two independently digested authoritative STEP artifacts, and exactly one final ProjectPreview GLB;
- the bearing and Spur Gear retained their exact capability, entity, occurrence, BOM, provenance, receipt, image, digest, length, and Part-21 bindings.

The broader native bridge gate also passed on the same bytes:

```text
dotnet run --project src\Host\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release -- --photon-cad-only
```

This evidence closes the real provider/save/reopen/verification/BOM/STEP/GLB engine path. It does not replace the remaining mounted human-visible checks for two-tab switching, orbit/zoom/fit, non-identity occurrence placement and removal, native STEP destination selection, and visible pane collapse/resize persistence.

## Current source freeze — 2026-08-11 09:26 CDT

This section supersedes the browser/layout gaps and older renderer hashes below while retaining their historical evidence.

The reusable parts and work-area transaction is source-complete and independently accepted:

- `Parts library` replaces the misleading flat `Operation library` presentation.
- Source-backed capabilities are searchable and filterable as Primitives, Gears, Bearings, Other parts, or Assembly tools.
- The library shows reusable Body/Part definitions already sealed in the current project with their occurrence counts; these remain canonical project definitions rather than loose untrusted files.
- Explorer and Photon side panes have bounded pointer and keyboard separators, visible collapse/restore controls, and a versioned bounded local layout preference.
- Parts/context and parameter-inspector panes have bounded pointer and keyboard separators, visible collapse/restore controls, and their own versioned bounded layout preference; resizing those side panes flexes the still-mounted 3D preview.
- The compact `<=900px` CAD layout resolves to rail + CAD editor only; Workspace Explorer and Photon remain hidden there, and the wide-layout controls/resizers remain unchanged.
- Persistent per-project CAD workspace instances remain mounted across tab switching; this transaction does not replace or remount their 3D/session state.
- The stale `HERMESAGENT` source label is removed.

Frozen seven-file hashes:

| File | SHA-256 |
|---|---|
| `src/Modules/PhotonCad/PhotonCadWorkspace.tsx` | `56DA8DCC05E9595077DF85A10FF7D84E4AAD0EA00F3B7B4E12DF5EAD3C22F503` |
| `src/Modules/PhotonCad/PhotonCadWorkspace.css` | `7FC65D3231DFC6C94CB34A0085C431B55FEC7D87E6D1EFE12EB09CA2ABFB0742` |
| `src/Modules/PhotonCad/PhotonCadWorkspace.test.tsx` | `C3AE8C7BC04A883CCF14063C32B4DE0EDF4D8A4B4B9A58D92D693F2CBC2320E9` |
| `src/app/App.tsx` | `F0F211B8F73376F7CB04EAC5AF955C6365A5A89C09E3E1F07E116E84ABE1EE3F` |
| `src/app/styles.css` | `E7FD8F85876697C0A385B243F80A3781B98E872F4C0A105BF72E1D085974ACFC` |
| `src/app/CadWorkbenchLayout.ts` | `4DB568C89F0401E3EE49328F3818756F63A696D40B919D0280CF65A336591897` |
| `src/app/CadWorkbenchLayout.test.ts` | `C523F1B00428B276BF01E498A8ECE954AA95C4DE414A7D43A198BDF24EF74CAB` |

Independent gates on these exact bytes:

- Focused browser/layout tests: 25/25 passed.
- Photon CAD frontend plus layout helper: 17 files / 166 tests passed.
- Production `tsc -b` and Vite build: passed, 3,276 modules.
- Scoped diff/whitespace check: clean.

No new deferred security issue was introduced by this renderer-only transaction. The security ledger remains `docs/coordination/CAD-FUNCTIONALITY-DEFERRED-SECURITY.md`; Architect was notified through `C:\Users\clsor\AppData\Local\hermes\handoffs\CAD\ARCHITECT-CAD-SECURITY-LEDGER-NOTICE.md`.

Still required before the CAD-complete claim: publish these exact current bytes, then perform mounted acceptance for rotatable/zoomable/fit 3D, fresh two-project isolation, Spur Gear, explicit non-identity assembly placement, Remove occurrence, verification, selected-part generic STEP export, close/reopen canonical projection and preview hydration, plus visible layout collapse/resize restoration. Source tests are not a substitute for these live gates.

## Current integrated checkpoint — 2026-08-11 08:30 CDT

This section supersedes the older 04:58 checkpoint retained below for provenance.

Live functionality already proven in the mounted desktop:

- A genuinely new millimeter project persisted a Box at revision 2 and rendered its sealed GLB through a successful one-use resource GET.
- The same project added a real BD Warehouse capped deep-groove bearing at revision 4. Canonical readback proved two source entities, two occurrences, two BOM rows, four applied operations, two independently digested STEP artifacts, and one complete replacement GLB. The user confirmed the 3D model is visible.
- The automatic post-mutation refresh race is closed: exact internal refresh preserves only the newly committed same-project/same-revision/same-content preview; manual or mismatched refresh revokes it.
- Persisted GLB hydration for clean reopened projects, truthful committed verification, and generic selected-entity STEP Part-21 export are implemented in current source and pass focused host/renderer smokes. These newer source bytes still require the next coordinated publish for mounted acceptance.
- Two fresh New projects retain independent runtime attachments and mounted per-tab CAD workspace state in source gates.

The latest functionality correction adds an honest `Remove occurrence` operation. It removes one selected occurrence plus descendants from the assembly scene, replaces occurrences and derived BOM, CAS-replaces the complete sealed GLB at revision +1, and preserves source part definitions and authoritative STEP bytes for reuse. Removing the last/root occurrence fails closed; this is not destructive project-file deletion.

Current removal-path hashes:

| File | SHA-256 |
|---|---|
| `src/Host/PhotonCadRuntime.IndustrialProvider/AssemblyContracts.cs` | `6472F298669C30D0D42E900461F2B36FD3091FC60B280ACB929D91BBA4920413` |
| `src/Host/PhotonCadRuntime.IndustrialProvider/AssemblyMutationProvider.cs` | `317B3AD2986269A09ABBC3E10F49251993A342765DAF5F633C93BBA890354DE9` |
| `src/Host/PhotonCadRuntime.IndustrialProvider/SealedMutationProvider.cs` | `66DF7F6C9861EE7DABE9254C966CDB07113CF1FEBA203570623FA40123B66CEE` |
| `src/Host/PhotonCadRuntime.IndustrialProvider.Smoke/Program.cs` | `52FD4781F825B5C2B63DE1671A87330490D45BA69DE904429A0544E6B3D4FDD3` |
| `src/Host/HermesDesktop/PhotonCadBridge.cs` | `DC166698CC66DCB0EDC7523B3371E7CED5A4357E003E961867E6EC4233DB899D` |
| `src/Host/HermesDesktop.Smoke/PhotonCadBridgeSmoke.cs` | `55A08EEBFC2FF899B6257AC65236D334532B841924B9227967B1003AE94BDA5A` |
| `src/Modules/PhotonCad/PhotonCadWorkspace.tsx` | `9790E462DF83EA7A0A852EFF4B4608901C57EBF027583AA1543DAA47112D6F18` |
| `src/Modules/PhotonCad/PhotonCadWorkspace.test.tsx` | `47D8893D947111EE1262D8E9702E88DA7F70607D9D6C11D99086041F3F342F24` |

Current gates:

- Industrial provider Release build: 0 warnings / 0 errors; smoke 16/16.
- HermesDesktop + focused smoke Release build: 0 warnings / 0 errors; `--photon-cad-only` passed with durable remove, preview refresh/hydration, STEP export, close/reopen, and verification coverage.
- Photon CAD frontend: 16 files / 160 tests passed; strict TypeScript passed.
- Production frontend build: passed, 3,273 modules.
- Exact C# formatting verification: passed.

Still required before the CAD-complete claim:

- Coordinated publish/relaunch, then live `Remove occurrence` proof on a disposable fresh project (do not modify the user's accepted `test5` evidence project).
- Live Spur Gear, explicit non-identity placement transform, verification, generic STEP export, close/reopen preview hydration, and two-project independence acceptance.
- A real categorized/searchable reusable parts-browser surface. The current left pane is a verified operation catalog backed by `bd-warehouse 0.2.0`, not a full folder-style part library.
- Collapsible/resizable Explorer/CAD/Photon and CAD library/preview/inspector panes. Current source has the grid-height fixes, but no complete keyboard-accessible splitter/collapse implementation yet.

CAD source ownership is frozen at the hashes above for coordinated publish. Do not relaunch over a partial copy. SPAT may take the shared desktop only after the publisher confirms these exact current bytes were included or explicitly defers the CAD live gate.

## Current proven functionality

- Windows New/Open/Save/Save As/Refresh/Close/Reopen project lifecycle is mounted.
- The New-project name is prefilled into the native file dialog; the user confirmed the duplicate-name entry problem is closed.
- Exact-image industrial Box and Cylinder creation persists through canonical RuntimeSync.
- Box followed by Cylinder saves and reopens as clean revision 4 with two entities, two occurrences, four operations, two sealed STEP artifacts, one replacement GLB preview, and two canonical BOM rows.
- Preview custody/responder is mounted using opaque one-use fixed-origin resources.
- Exact production-path Docker smoke passed before the current uncommitted catalog work:
  - `Desktop Photon CAD LIVE industrial Docker 0-to-2-to-4 persistence and preview responder passed.`
  - provider containers after completion: zero.
- Pushed backup `b079a38` contains the canonical BOM work and its passing live smoke.

Local engineering runtime evidence remains intentionally untracked under `runtime-assets/photon-cad-industrial/`. Do not stage or publish it. The exact local files are required for live engineering operation but remain redistribution-blocked.

## Current uncommitted dynamic-catalog work

Source writes are frozen at this handoff. Seven CAD files are intentionally dirty/new:

| File | SHA-256 |
|---|---|
| `src/Host/PhotonCadRuntime.IndustrialProvider/CatalogContracts.cs` | `B33E880D7BD78F6719BB2AD7AF4DD91E5A2FD5C60AF8475A8B5B52D0519CE2CE` |
| `src/Host/PhotonCadRuntime.IndustrialProvider/ProtocolV1.cs` | `50798009BEACFB310B61A4244D406887BA078F095799E38B10BE477D291A58B6` |
| `src/Host/PhotonCadRuntime.IndustrialProvider/SealedMutationProvider.cs` | `222EA663DBB2CDF8412651649580E2A5351116998DB944D49C49BC0D0236FCB2` |
| `src/Host/PhotonCadRuntime.IndustrialProvider/MutationMapperV1.cs` | `A1DD43AC2BBEB3BFCB2DCBB2DB9FE9405FBCD8859D40CB69C1E10A03B1E23DFA` |
| `src/Host/PhotonCadRuntime.IndustrialProvider.Smoke/Program.cs` | `713CF4E05E53E883433BF56E19A6ED248421EA728175D3B2106B81280914FEC4` |
| `src/Host/HermesDesktop/PhotonCadBridge.cs` | `0D9B32A87E623A7341E9653D4CAD82F2451B02DDC5BC71A902D152FB63DC7F4B` |
| `src/Host/HermesDesktop.Smoke/PhotonCadBridgeSmoke.cs` | `AD6585383DDC81D5C5AF9E2DAFA2BF75A8BE661F0B6F8D88BF8FA16786E8C89D` |

What is implemented in these bytes:

- A digest-bound provider catalog projection exposing supported bearings and the exact `Spur Gear` item.
- Renderer-safe opaque choice tokens; raw library choice values stay provider-side.
- Typed number/integer/boolean/choice catalog inputs.
- `catalog` and `createCatalogItem` protocol serialization and strict response/provenance validation.
- Provider `GetCatalogAsync` and `BindCatalogItemAsync` paths.
- Catalog parts map to canonical `Part` entities, one occurrence, a sealed STEP, complete-project GLB replacement, and an authoritative BOM row.
- Bridge describe projects primitive plus dynamic catalog capabilities.
- Bridge execute parses dynamic item inputs and binds them to the production provider.

Current gates on these uncommitted bytes:

- `PhotonCadRuntime.IndustrialProvider` Release build: 0 warnings, 0 errors.
- Provider smoke: 14/14, including a fake digest-bound Spur Gear persistence path.
- `HermesDesktop.Smoke` Release build: 0 warnings, 0 errors.
- `--photon-cad-only`: pass.
- Direct inspection of the exact installed image proved catalog digest `sha256:aae5554ce9e57133f508e3343663704d35c82e6a31e5201f6224a9b299ddf6b6`, a supported bearing, and supported `Spur Gear` with typed parameters.

Not yet proven on these bytes:

- The dynamic bearing/Spur Gear provider path has not yet been run through the real exact image, bridge, canonical save, preview responder, and reopen.
- `RunLiveIndustrialAsync` still asserts exactly two capabilities. It must be changed to locate the dynamic bearing and Spur Gear by capability/title rather than count `2`, then execute both and verify the resulting revisions, BOM, STEP set, preview CAS, and reopen.
- The current bridge work is uncommitted. Review before amending it; do not discard the new catalog file.

## Required CAD completion sequence

1. Finish the real dynamic catalog acceptance:
   - Describe must return Box, Cylinder, at least one supported bearing, and exact `Spur Gear`.
   - Run a real bearing and a real Spur Gear through the exact installed image.
   - Persist/reopen their STEP, occurrences, complete GLB, provenance, and BOM.
   - Add hostile catalog digest/item/choice/parameter tests.
2. Finish assembly placement and transforms:
   - Add explicit occurrence place/update operations.
   - Use one exact root, bounded parent DAG, rigid 16-value transforms, and complete preview replacement.
   - Recompute BOM quantities from the resulting trusted occurrence set; never accept caller totals.
3. Fix lifecycle projection:
   - `PhotonCadProjects.DesktopAdapter/PhotonCadProjectWireProjection.cs` currently drops canonical occurrences/transforms on Refresh/Open/Reopen.
   - Extend the typed project contract and renderer normalization so reopened projects show the same occurrence hierarchy and transforms as the accepted operation result.
4. Verification:
   - First mount host-side canonical assembly/DAG/transform/BOM/export-readiness checks.
   - Then add containerized valid-solid/interference/dimension verification and issue fresh immutable runtime evidence if the container protocol changes.
5. Release/export:
   - Mount one truthful canonical generic STEP Part-21 export from embedded sealed project artifacts.
   - Do not claim AP214, AP242, Inventor, STL, DXF, SVG, or multi-file release formats until separately implemented and proven.
6. Publish and visible acceptance:
   - Build/publish/relaunch the exact desktop.
   - User-visible manual proof: New, Box, Cylinder, bearing, Spur Gear, preview after each, Save, Close, Reopen, hierarchy/BOM retained, generic STEP export.
   - Verify zero residual owned containers/jobs after each failure and final shutdown.

## Collision boundaries

- SPAT owns Serena/Roslyn/compiler/debugger and DeveloperServices/Monaco language/debug seams. Avoid those paths.
- CAD may modify `PhotonCadBridge.cs` and CAD-only smoke/renderer/provider/project files.
- Coordinate before touching `HermesDesktop.csproj` or `MainWindow.xaml.cs`; SPAT may need those for developer-services registration.
- Do not stage `runtime-assets/` local engineering evidence.

## Bidirectional Codex to Photon bridge status

Do not build a second conversation bridge. The Ali-style core conduit already exists and is live:

- `HermesConversationBridge` exposes an authenticated loopback service on `127.0.0.1:8972`.
- `HermesConversationBridgeAdapter` submits through the same `useHermesChat.send` action used by the visible Photon dock and returns the same committed transcript/tool activity.
- The self-contained `hermes-bridge.exe` client supports `health`, `status`, `send --text`, and `interrupt`.
- On 2026-08-11, root verified the live health endpoint and read the current visible Photon session successfully through `status`.
- Root then sent one live bridge handshake. The user message appeared exactly once in the visible Photon transcript and Photon replied exactly once with acknowledgment, proving the core no-mediation path end to end.

This is enough for Codex and Photon to communicate without the user relaying text. Root retains ownership of the remaining relay-product refinements documented in `CAD-FUNCTIONALITY-REQUIREMENTS.md`; they are not part of SuperMax's CAD source ownership. SuperMax should use the existing bridge for live error coordination and must not create a parallel hidden conversation or alternate transcript authority.

The live handshake exposed one concrete functional defect for the bridge-owner backlog: `BridgeClientPolicy.Default` times out after 15 seconds while `HermesConversationBridge.SubmitTurnAsync` intentionally permits a turn to run for 30 minutes. The CLI therefore returned `The bridge did not respond before the client timeout.` even though Photon completed the turn and the exact reply was available immediately afterward through `status`. Align the send/wait protocol rather than merely increasing every command timeout: health/status should remain short, while a submitted turn needs an acknowledged turn ID plus bounded wait/poll semantics.
