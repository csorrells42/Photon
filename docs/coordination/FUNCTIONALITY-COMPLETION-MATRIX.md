# Hermes Workbench functionality completion matrix

Date: 2026-08-12 (America/Chicago)

This is the root completion ledger for the functionality-first program pass. Security observations belong in the existing deferred-security reports and do not change a functional status unless they directly prevent the feature from operating.

Status meanings:

- **Live proven**: exercised through the published desktop or its authoritative launcher-owned runtime.
- **Integrated proven**: current authoritative source passed an end-to-end native or exact-runtime gate, but the current published desktop has not yet been refreshed with all current bytes.
- **Source proven**: focused and broad source gates pass, but live integration remains unproven.
- **Conditional**: works only when an optional external dependency is installed or configured.
- **Incomplete**: a required user-facing operation or provider is still missing.

## Priority completion order

1. CAD functionality and visible 3D.
2. Compiler.
3. Debugger.
4. Remaining program functionality.
5. Lowest-priority tooling, in this exact order: Arduino, Java, GCC.

## Current matrix

| Surface | Status | Authoritative evidence | Remaining proof or work |
|---|---|---|---|
| CAD project New/Open/Save/Save As/Refresh/Close/Reopen | Integrated proven | `HermesDesktop.Smoke --photon-cad-only`; DesktopAdapter 8/8; Windows real NTFS 26/26 | Final published multi-project/manual regression after the CAD UI freeze. |
| CAD Box/Cylinder persistence | Live proven | Published `test5` accepted Box at revision 2; canonical file readback; exact-image bridge smoke | Final coherent publish must preserve this behavior. |
| CAD bearing and Spur Gear generation | Integrated proven | Exact-image live-industrial gate generated both, persisted 0-to-2-to-4, resolved preview bytes, reopened, verified; published `test5` visibly rendered a real bearing | Published Spur Gear click-through remains to be recorded. |
| CAD 3D preview | Live proven | Published Box and 984,252-byte bearing GLB rendered; one-use same-origin resource returned HTTP 200 | Final publish/manual rotation, Fit, Reset, isolate and tab-switch proof. |
| CAD occurrence hierarchy, transforms and BOM | Integrated proven | Industrial provider 16/16; DesktopAdapter occurrence-transform parity 8/8; canonical `test5` retained two source-bound occurrences and BOM | Published reopen hierarchy/transform presentation and repeated-source quantity proof. |
| CAD manual modeling | Integrated proven | Exact immutable-image gate persisted Sketch/Extrude Add 0-to-2, rectangular Sketch Cut 2-to-4 and Hole Cut 4-to-6; exact STEP and GLB bytes, preview resolution, save and reopen all passed | Fillet, chamfer and linear/circular feature patterns remain unavailable until the canonical project stores stable topology and feature identities; do not fake these as all-edge or body-copy operations. |
| CAD verification | Integrated proven | Exact-image committed verification passed valid-solids, dimensions, assembly-structure and export-readiness after save/reopen | Visible verification UI and unsupported interference-check wording after final publish. |
| CAD generic STEP import | Integrated proven | Real industrial-image STEP bytes passed strict Part-21 intake, byte-exact canonical revision-two save, complete GLB preview resolution and clean reopen; invalid STEP, unsupported IPT authority and cancellation also fail closed | Final mounted native source/destination picker click-through. IPT/IAM remain unavailable without an installed Autodesk conversion authority. |
| CAD generic STEP export | Integrated proven | Current bridge performs selected-entity create-only native export; focused smoke verifies exact committed bytes/receipt, occurrence rejection and cancellation | Final mounted picker/export/readback click-through; no AP214/AP242/Inventor claim. |
| CAD panel layout | Integrated proven | Frozen browser/layout transaction passed 17 files / 166 tests, strict TypeScript and Vite 3,276 modules; current published bundle contains bounded collapse/resize controls for Explorer, Photon, Parts and Inspector | Final mounted collapse/resize/persistence and tab-isolation click-through. |
| C# build/analyze/test | Live proven | Mounted panel visibly reports .NET SDK, compiler/build and typed .NET Tests available; Desktop developer-services smoke and the fresh exact-authority real integrated host execute typed tests | Final workspace-with-project build/test spot check; the current dedicated workspace honestly has no `.csproj`. The earlier apparent failure was reproduced as restricted NuGet.Config access in the test sandbox; the exact `dotnet test` and complete real host sequence pass outside that restriction. |
| Roslyn C# intelligence | Live proven | Mounted panel visibly reports Language service and `.NET / Roslyn` Host verified; real solution open/change/completion/hover/definition/references/rename/code-actions; Roslyn smoke 13/13 | Final Monaco edit/completion/navigation spot check in a selected C# solution. |
| .NET debugging | Live proven | Mounted panel visibly reports Debugger provider Host verified; real NetCoreDbg launch/breakpoint/threads/stack/scopes/variables/evaluate/step/disconnect/relaunch; debugger smoke 10/10 | Final Run/Debug spot check after selecting a workspace target; `No .NET target discovered` is honest for the current empty workspace. |
| Python | Live proven | Mounted integrated host reports 4/4 and executes project inspection, syntax compilation, unittest and a real Serena Python session; C# remained frozen 4/4 during the same gate | Final workspace-with-project editor/navigation spot check only. |
| C++ | Integrated proven | Fixed immutable-container GCC/G++ authority is integrated through Developer Services with C17/C++20 selection, bounded typed compile results, cancellation and deterministic fake-runner coverage. The combined real desktop-host gate compiled both `.c` and `.cpp` from the exact launcher-mounted workspace and returned bounded artifacts without regressing C#/Python 4/4. | Coherently publish and visibly prove GNU C/C++ `1/1 Host verified`, file-sensitive C17/C++20 selection, diagnostics and artifact production. |
| Runtime/model configuration | Integrated proven | Mounted Settings -> Runtime renders the local/LAN OpenAI-compatible endpoint workflow; the live `gpt-oss-20b` composer exposes exactly the server-advertised Low/Medium/High reasoning levels with Medium selected. The LM Studio native v1 API reports downloaded models, loaded instances, exact loaded configuration and model reasoning capabilities. | Add truthful server/model discovery and named profiles. Server-owned values remain authoritative unless an explicit profile field overrides them; unknown values must render as LM Studio-managed, never guessed. Prove Test & discover -> Save profile -> Use for new conversations without sending unset context/output/top-p/temperature fields. |
| Assistant conversation bus | Live proven | Launcher-owned published service on 9072 reports protocol v1 ready; `participants` shows Chris, Codex and Photon online with Photon bound to a visible-turn conversation/generation, while Ali and Scarlett are accurately offline; source smoke is 14/14 and the visible sender then recipient contract is enforced | Mount shared-room status/disable UI; launch Ali and Scarlett and complete the five-party direct/group/restart/offline acceptance matrix. |
| Assistant chat, composer and ordinary sessions | Live proven | The mounted Photon agent is connected and interactive; current UI visibly opens/retains sessions and accepts text/image attachments; focused Agent Registry, Composer and Sessions tests are part of the 221/221 platform batch | Final send/cancel/reconnect/new-session/reopen spot check after the coordinated restart. |
| Account and native provider connections | Integrated proven | Native connection/vault/relay smokes pass; mounted Settings exposes the authenticated account and model selector; focused Connections/System tests are green | Live add/change/remove one nonproduction credential and confirm the runtime lease after the coordinated runtime deployment. |
| MCP, Skills and advanced extensions | Source proven | Focused MCP, MCP editor, Skills, Extension Settings and live-extension controller tests pass in the 221/221 platform batch; System workspace mounts each production surface | Mounted list/refresh plus one reversible MCP/skill/extension operation after publish; keep unsupported upstream actions explicitly unavailable. |
| Profiles and agent runtime | Source proven | Focused Profile Runtime, integration and live-controller tests pass in the 221/221 platform batch; production Settings mounts the verified profile runtime workspace | Mounted read/select plus one supported reversible profile operation; no automatic replay across runtime replacement. |
| Advanced session administration | Source proven | Focused Session Admin and Sessions tests pass in the 221/221 platform batch; production Settings mounts branches/import/export/statistics/model-lock UI | Mounted branch/export/import/reopen and cancellation spot check with disposable data. |
| Vision and image attachments | Integrated proven | Clipboard alternate-representation dedupe landed; AgentDock/vision-focused tests are green in the platform batch; native screenshot/file attachment boundary is present | Final mounted single-paste screenshot proof and one model response grounded in that image. |
| Agent dock and panel docking | Live proven | Current mounted Workbench visibly renders the Photon dock and model selector; WorkbenchDocking focused tests pass in the 221/221 platform batch | Final collapse/reorder/persist/restore spot check; CAD-specific explorer/agent column collapse and resize remain tracked separately. |
| Workspace files and atomic document save | Integrated proven | Full Desktop smoke covers secret-file/junction guards and identity-bound save/conflict behavior | Final mounted open/edit/save spot check. |
| Workspace literal and Serena semantic search | Live proven | Launcher-owned Serena serves Python and C#; Desktop smoke covers literal, native Serena, request binding and cancellation | Final mounted selection styling and navigation spot check. |
| Terminal | Integrated proven | ConPTY PowerShell round trip passed in the full Desktop smoke | Final mounted cwd and global Show/Hide Terminal spot check. |
| Browser | Integrated proven | Desktop smoke covers native tab history, navigation identity, placement dedupe and URL/title projection | Final Maps route/tab/manual navigation spot check. |
| Docker Control Center | Integrated proven | Full Desktop smoke covers typed review, one-use commit, redaction and unavailable-runtime behavior | Final mounted snapshot/log/restart spot check. Update remains unavailable unless a real updater is mounted. |
| Credentials and direct-provider connections | Integrated proven | Credential relay, vault and native collector smokes pass | Coherent runtime-generation deployment and live provider lease proof. |
| Source control | Integrated proven | Native Git service smoke 10/10 and renderer 9/9; status parsing, opaque repository identities, confinement, timeout/cancel and fixed Git Extensions launch mapping pass; this machine has `C:\Program Files\GitExtensions\GitExtensions.exe` | Final mounted status/navigation/launch spot check. Portable installs still need an explicit bundled-asset decision or must label Git Extensions as an optional external integration. |
| Usage Intelligence | Source proven | Focused renderer suite 20/20; production dashboard defaults to `DesktopUsageAdapter`; native host routes named OpenRouter keys plus OpenAI/Anthropic organization collectors and Google project aggregate, with synthetic dollars excluded from live totals; Desktop smoke covers OpenRouter sanitization/error/no-redirect behavior | After the coherent publish, verify the mounted dashboard against each actually configured restricted credential profile; unsupported consumer subscription and per-key Gemini usage must remain explicitly unavailable. |
| Memory/Mem0/Qdrant | Live proven | Verified generation `74871e7e3b436ec3-8e85d46fd71e` adopted on exact image `sha256:8e85d46fd71ebaa399908ff39c8ded2154e30c347b551be6ba4002b9bb784a33`; Qdrant retained the same healthy container/volume and exact green `hermes_workbench_mem0_v3` collection with 13 points after gateway replacement; live OSS search passed; transient initialization retry and the no-`mem0_read` truth contract are loaded | Retain a redacted authenticated `/api/memory` UI readback and one user-visible add/search persistence spot check. |
| Arduino | Integrated proven | Official Arduino CLI 1.5.1 is archive/executable/license hash-pinned; the app-owned AVR core 1.8.8 and `arduino:avr:uno` board are verified at startup. Provisioning smoke passes clean/repeat/corrupt-preservation cases. Embedded/provider gates are green, and the combined real desktop-host gate inspected and compiled Blink into bounded host-owned artifacts. | Coherently publish and visibly prove Arduino `2/2 Host verified` plus a selected `.ino` Check action. Upload remains unavailable until an opaque native port catalog and reviewed one-use upload authorization exist. |
| Java | Integrated proven | Published receipt-bound Eclipse JDT LS and app-owned JRE/payload manifest are present; fresh current-byte focused smoke passed 4/4 including the real JDT lifecycle | Final mounted Java provider and editor semantic-operation spot check. |
| GCC | Integrated proven | The shared immutable-container C/C++ compiler authority, typed GCC handler, frontend gate and combined real desktop-host C17/C++20 compiles are green against the exact launcher image and workspace mount. | Mounted GNU C/C++ UI proof remains pending. Record that cancellation does not yet prove remote `docker exec` child death and that the legacy `artifact.exe` contract can carry a Linux ELF; do not misrepresent either. |

## Gates already green on current bytes

- Full frontend: 127 test files, 781 tests at the latest provider-reasoning freeze; current CAD-focused frontend remains 17 files / 174 tests.
- Production TypeScript and Vite build: 3,276 modules.
- Full `HermesDesktop.Smoke` native suite.
- Fresh current-tree strict TypeScript, `HermesDesktop.Smoke` Release build (0 warnings/0 errors), `--photon-cad-only`, and `--developer-services-only` gates.
- Focused Photon CAD frontend: 17 files, 174 tests.
- Photon CAD IndustrialProvider: 16/16.
- Photon CAD DesktopAdapter: 8/8.
- Photon CAD exact-image live-industrial bridge path: bearing + Spur Gear + preview + save/reopen + verification; byte-exact generic STEP import + preview + reopen; manual sketch/extrude add + sketch cut + hole cut through clean revision six.
- Runtime configuration frontend: 12/12.
- Hermes custom-endpoint backend: 5/5.
- Fresh combined Mem0/Qdrant plus custom-provider backend gate: 109 passed, 1 platform skip.
- Fresh exact-authority real Developer Services gate: .NET 4/4 with typed tests; Python 4/4 with inspect/syntax/unittest/Serena session; real C17/C++20 GCC compiles; real Arduino Uno inspect/compile/artifacts; Roslyn completion/hover/definition/references/rename/code-actions; NetCoreDbg launch/breakpoint/inspect/evaluate/step/disconnect/relaunch.
- C++/GCC source integration: HermesDeveloperServices Release 0/0; trusted language-tooling registry smoke including container GCC; DeveloperServices 13/13; pinned GCC and frozen Python smokes; HermesDesktop Release 0/0; focused renderer 2 files/13 tests; strict TypeScript.
- Fresh current-byte real developer-services sequence: .NET 4/4 + typed tests; Python 4/4; C17/C++20 GCC; Arduino Uno inspect/compile; Roslyn completion/hover/definition/references/rename/code-actions; NetCoreDbg launch/breakpoint/inspect/evaluate/step/disconnect/relaunch.
- Java/JDT: focused smoke 4/4 including the published receipt-bound real JDT lifecycle.
- Assistant conversation bus: current source 14/14; launcher-owned published service protocol v1 is healthy with Chris/Codex/Photon online and Ali/Scarlett explicitly offline.
- Authenticated Mem0/Qdrant: focused source suite green except one unrelated Windows legacy-file encoding fixture; immutable generation verified and adopted; live private search, collection and volume identity verified after gateway replacement.
- Usage Intelligence renderer/native boundary: 20/20 focused tests; native OpenRouter collector contract remains covered by Desktop smoke.
- Source control: native Git service 10/10 and renderer 9/9; supported Git Extensions executable is present on this machine.
- Platform UI batch for Agent Registry, Composer, Connections, MCP, Skills, Extensions, Profiles, Sessions, Vision and Docking: 37 files/221 tests.

## Immediate serialized next actions

1. Publish the frozen CAD source coherently, then manually prove STEP import/export, manual add/cut/hole, bearing/gear, rotation, panel collapse/resize, save/reopen and verification in the mounted desktop.
2. Coherently publish the already-integrated Arduino and C/C++ transactions, then visibly prove Arduino 2/2 and GNU C/C++ 1/1 without regressing C#/Python 4/4.
3. Expand and prove truthful Runtime configuration plus the conversation bus lifecycle, then continue this matrix row by row through the remaining core program surfaces.
4. Provision, integrate and mount Java/JDT functionality.
5. Run the complete requirement-by-requirement final audit.
