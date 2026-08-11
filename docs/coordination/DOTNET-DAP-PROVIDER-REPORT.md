# Fixed .NET DAP Provider Lane Report

## Completion status

The isolated lane is complete. It adds a standalone `net10.0` debug-only toolchain provider and a standalone smoke executable around the existing `HermesDeveloperServices` DAP contracts. The provider is not connected to the desktop host or renderer in this lane.

No debugger was downloaded, installed, bundled, or executed. No real process was attached to or debugged. Runtime coverage used only a disposable copy of the lane-owned smoke executable acting as a framed stdio fake adapter.

No source outside these assigned locations was edited:

- `src/Host/HermesDotNetDebugger/**`
- `src/Host/HermesDotNetDebugger.Smoke/**`
- `docs/coordination/DOTNET-DAP-PROVIDER-REPORT.md`

`src/Host/HermesDeveloperServices/**`, `docs/coordination/COMPILER-DEBUGGER-LANE-REPORT.md`, and `docs/ROADMAP.md` were inspected read-only. `HermesSessionAdmin` and `HermesSystemWorkspace.tsx` were not touched and must not be touched as part of integrating this lane.

## Provider result

`HermesDotNetDebuggerProvider` implements the existing `IToolchainProvider` seam as a debug-only local sidecar:

- `ProviderId`: `hermes-dotnet-dap`
- provider version: `1.0.0`
- existing toolchain contract version: `1`
- DAP protocol declaration: `1.71.0`, matching the existing developer-services lane
- languages: C#, F#, and Visual Basic for CoreCLR debug targets
- build and LSP capabilities: false
- launch, attach, breakpoints, and evaluation: true

`StartAsync` validates the fixed installation and authorized workspace but starts no child. A child is created only after the required caller-supplied `IDotNetDebugAuthorizationPolicy` returns true for the specific typed launch or attach request. There is no permissive authorization default.

The production API exposes typed operations only:

- launch or attach one session;
- set source breakpoints;
- configuration done;
- threads, stack trace, scopes, variables, and evaluate;
- continue, step over, step in, and step out;
- disconnect and disposal.

There is no generic DAP request, executable, argument, command, shell, listener, server, engine-logging, environment, network, downloader, or process-name surface. The only adapter argument is the compiled constant `--interpreter=vscode`.

## Fixed provisioning and process boundary

The accepted installation is exactly:

`<HermesInstallRoot>\debuggers\netcoredbg\3.1.3-1062\netcoredbg.exe`

The adjacent required receipt is:

`<HermesInstallRoot>\debuggers\netcoredbg\3.1.3-1062\hermes-provisioning.json`

The receipt is bounded to 4 KiB and must match the pinned component, version, source commit, archive name, and archive SHA-256. It must also contain the SHA-256 that the installer computed for the extracted `netcoredbg.exe`; the provider re-hashes the executable and compares it in fixed time before every process start. Missing, malformed, mismatched, unreadable, hash-invalid, or reparse-backed installations are unavailable.

The process transport:

- executes that one fixed path directly with `UseShellExecute=false`;
- supplies only `--interpreter=vscode` through `ProcessStartInfo.ArgumentList`;
- clears the adapter process environment completely;
- redirects stdin, stdout, and stderr and opens no network listener;
- limits DAP headers to 8 KiB, each DAP payload to 1 MiB, and retained stderr to 32 KiB while counting dropped characters;
- applies 15-second operation/negotiation deadlines plus caller cancellation;
- sends best-effort DAP `cancel` through the existing session when negotiated;
- allows one active session per provider;
- closes stdin, allows a two-second graceful adapter exit, then kills only the exact created `Process` child tree;
- never enumerates, selects, or kills by process name.

Launch programs must be existing absolute `.dll` or `.exe` files inside the explicit workspace. Working directories and breakpoint sources must also be existing paths inside that workspace. Reparse points are rejected. Launch arguments, expressions, breakpoint fields/counts, stack/variable pages, and evaluation contexts are bounded. Attach accepts only a positive supplied PID and does not inspect or contact it before explicit authorization and DAP negotiation.

## Pinned implementation and official-source research

The selected candidate is Samsung NetCoreDbg because upstream implements VS Code DAP for CoreCLR and publishes the source under the MIT license. The lane pins, but does not acquire, the following release:

- upstream project: `Samsung/netcoredbg`
- release/tag: `3.1.3-1062`
- published: `2025-12-12T11:33:53Z`
- full source commit: `8b8b22200fecdb1aec5f47af63215462d8c79a4b`
- Windows x64 archive: `netcoredbg-win64.zip`
- archive size: `3,475,639` bytes
- official GitHub asset digest: `sha256:c67ae052e0bcb9ce37000f261e2d397a0d5b6615cafe30c868239a78598dfb37`
- fixed adapter mode: `--interpreter=vscode`

Official sources:

- [release 3.1.3-1062](https://github.com/Samsung/netcoredbg/releases/tag/3.1.3-1062)
- [official release metadata, including asset size and digest](https://api.github.com/repos/Samsung/netcoredbg/releases/tags/3.1.3-1062)
- [pinned source tree](https://github.com/Samsung/netcoredbg/tree/3.1.3-1062)
- [pinned NetCoreDbg MIT license](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/LICENSE)
- [upstream usage and `--interpreter=vscode` documentation](https://github.com/Samsung/netcoredbg/blob/3.1.3-1062/README.md#running-netcoredbg)

The engineering redistribution conclusion is that the upstream source is permissively redistributable when its license and applicable third-party conditions are satisfied. This is not a substitute for the product's final legal approval. Exact notices found in the pinned tag are:

- NetCoreDbg: MIT, copyright 2017 Samsung Electronics Co., LTD; include the copyright and permission notice in copies or substantial portions.
- Catch2: Boost Software License 1.0; retain its required statement where applicable. [Pinned text](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/third_party/catch2/LICENSE.txt)
- nlohmann/json: MIT, copyright 2013-2018 Niels Lohmann; retain the copyright and permission notice. [Pinned text](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/third_party/json/LICENSE.MIT)
- libelfin: MIT, copyright 2013 Austin T. Clements; retain the copyright and permission notice. [Pinned text](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/third_party/libelfin/LICENSE)
- linenoise-ng bundle: BSD-style linenoise notice, Markus Kuhn `wcwidth` permission notice, and Unicode `ConvertUTF` notice whose redistribution grant requires the notice to remain attached. [Pinned text](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/third_party/linenoise-ng/LICENSE)
- Upstream `AUTHORS` should be retained with the component notice material. [Pinned attribution](https://raw.githubusercontent.com/Samsung/netcoredbg/3.1.3-1062/AUTHORS)

Because this lane was expressly prohibited from downloading the release archive, it did not independently inspect the archive's binary inventory or generate an SBOM. That final exact-archive composition review remains a mandatory installer/release gate; the source-tag notice inventory above must not be treated as proof that no additional shipped notice applies.

`vsdbg` was not selected: it is not an open-source, general-redistribution substitute for this lane and was not downloaded or referenced.

## Exact installer requirements

Super's installer lane must do all of the following before enabling this provider in a distributable build:

1. Acquire only `https://github.com/Samsung/netcoredbg/releases/download/3.1.3-1062/netcoredbg-win64.zip`; never resolve “latest” at install or runtime.
2. Require the exact byte length `3,475,639` and SHA-256 `c67ae052e0bcb9ce37000f261e2d397a0d5b6615cafe30c868239a78598dfb37` before extraction.
3. Extract into a private staging directory with traversal, absolute-path, reserved-name, and reparse-point rejection; do not extract over an existing component.
4. Inventory and license-scan every extracted file, generate/retain an SBOM, and obtain the product's legal approval for the exact archive.
5. Ship the complete upstream MIT text, the applicable pinned third-party texts above, and attribution in Hermes NOTICE/license material and with the installed component where required.
6. Provision the complete release payload atomically into `debuggers\netcoredbg\3.1.3-1062`; do not copy only `netcoredbg.exe` if the archive supplies adjacent runtime libraries.
7. Compute the extracted `netcoredbg.exe` SHA-256 and write the schema-1 provisioning receipt only after archive verification, safe extraction, inventory, and notice installation succeed.
8. Apply installer-owned ACLs appropriate to the Hermes application directory. A writable debugger or receipt invalidates the intended provenance boundary even if the provider can detect ordinary corruption.
9. Do not add runtime download/update logic, PATH discovery, registry discovery, alternate versions, fallbacks, server mode, logging arguments, or a separate debugger installer.
10. Treat absent or invalid provisioning as the ordinary provider `Unavailable` state. The product must not silently fall back to another debugger.

## Protocol and smoke coverage

| Required behavior | Evidence |
|---|---|
| initialize and capabilities | Fake adapter negotiates configurationDone, conditional breakpoints, hover evaluation, and cancellation. |
| launch negotiation | Authorized typed launch receives the fixed program/cwd/args envelope and initialized event. |
| attach negotiation | Synthetic PID `424242` is authorized and sent only to the fake; no OS process lookup or real attach occurs. |
| breakpoints and configurationDone | Source breakpoint response is verified before explicit configurationDone. |
| stopped/continued | Both events are observed across continue and every step kind. |
| threads, stack traces, scopes, variables, evaluate | Typed responses are asserted end to end. |
| continue and step | Continue, next, stepIn, and stepOut run as fixed operations and return to stopped. |
| disconnect | DAP disconnect completes before the exact fake child is disposed. |
| adapter exit | Controlled fake EOF moves the existing DAP session to `Exited`. |
| malformed frames | Invalid `Content-Length` faults negotiation; a new explicitly requested session then succeeds. |
| cancellation | A hanging evaluate and a hanging launch are cancelled; later explicit operations/sessions recover. |
| bounded output/environment | Smoke confirms zero inherited environment entries, exactly one fixed adapter argument, capped stderr, and dropped-character accounting. |
| exact child ownership | Two fake adapters run concurrently; stopping provider A exits A's PID while provider B's distinct PID remains alive. |
| bounds | Outside-workspace paths, excessive arguments, invalid attach PID, and oversized frames are rejected before process creation. |

The fake adapter never submits a shell command, starts a debug target, opens a listener, or attaches to a process.

## Final build and test evidence

Command:

`dotnet build .\src\Host\HermesDotNetDebugger\HermesDotNetDebugger.csproj --configuration Release --nologo --tl:off --verbosity:minimal`

Result: passed in 0.79 seconds; 0 warnings, 0 errors.

Command:

`dotnet build .\src\Host\HermesDotNetDebugger.Smoke\HermesDotNetDebugger.Smoke.csproj --configuration Release --nologo --tl:off --verbosity:minimal`

Result: passed in 0.85 seconds; 0 warnings, 0 errors.

Command:

`dotnet run --project .\src\Host\HermesDotNetDebugger.Smoke\HermesDotNetDebugger.Smoke.csproj --configuration Release --no-build`

Result: passed in 5.5 seconds; **9 passed, 0 failed**.

The first pre-final smoke run caught PascalCase launch/attach envelope fields before they reached the existing DAP session. The provider was corrected to serialize those fixed envelopes with `JsonSerializerDefaults.Web`; all final evidence above is from the corrected build.

## Hand-authored files

- `src/Host/HermesDotNetDebugger/HermesDotNetDebugger.csproj`
- `src/Host/HermesDotNetDebugger/Properties/AssemblyInfo.cs`
- `src/Host/HermesDotNetDebugger/DebuggerContracts.cs`
- `src/Host/HermesDotNetDebugger/NetCoreDbgProvisioning.cs`
- `src/Host/HermesDotNetDebugger/NetCoreDbgProcessTransport.cs`
- `src/Host/HermesDotNetDebugger/HermesDotNetDebuggerProvider.cs`
- `src/Host/HermesDotNetDebugger/HermesDotNetDebugSession.cs`
- `src/Host/HermesDotNetDebugger.Smoke/HermesDotNetDebugger.Smoke.csproj`
- `src/Host/HermesDotNetDebugger.Smoke/FakeDapAdapter.cs`
- `src/Host/HermesDotNetDebugger.Smoke/Program.cs`
- `docs/coordination/DOTNET-DAP-PROVIDER-REPORT.md`

Release `bin`/`obj` artifacts were generated only beneath the two lane-owned project folders. No package dependency was added; the provider uses only .NET 10 and a project reference to the existing read-only `HermesDeveloperServices` project.

## Integration seam for Super

This lane stops here. Super may later reference `HermesDotNetDebugger.csproj`, construct one `HermesDotNetDebuggerProvider` with the trusted application install root and a UI/host-backed `IDotNetDebugAuthorizationPolicy`, register the provider in the existing toolchain registry, call `StartAsync` with the explicit workspace, and map only the typed session operations/events across the trusted host boundary.

Integration must preserve these rules:

- do not advertise the provider as available until the fixed receipt and executable hash pass;
- never expose install paths, executable selection, raw DAP requests, adapter stderr, or authorization decisions to untrusted browser input;
- do not automatically launch, attach, reconnect, restart, or replay a debug request;
- do not edit or route this work through `HermesSessionAdmin` or `HermesSystemWorkspace.tsx`;
- do not turn this into a second product, installer, developer-services host, or container.

Exact report path for Super:

`C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\DOTNET-DAP-PROVIDER-REPORT.md`
