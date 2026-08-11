# Compiler and Debugger Services Lane Report

## Completion status

The isolated developer-services nucleus is complete in the lane-owned folders. It provides a dependency-light `net10.0` library, a no-framework smoke executable, guarded .NET builds and diagnostics, a bounded DAP codec and client state machine, and a versioned provider-neutral toolchain registry.

No desktop host, Workbench UI, installer, launcher, shutdown workflow, upstream Hermes container, remote installer, artifact bundle, or product source outside this lane was modified. No debugger, language server, Java component, proprietary IDE component, or external binary was downloaded or run.

## Product and architecture invariant

Hermes Workbench remains exactly one complete product: one desktop Workbench, one installer/launcher/shutdown workflow, one upstream Hermes Docker container, and one local developer-services host. Toolchain providers are internal modules of that one installation. They are not editions, optional customer-facing product fragments, separate installers, separate developer-services hosts, or separate Docker stacks.

Owned external language servers and debug adapters may later run as on-demand child processes of the single local developer-services host. `ToolchainExecutionKind.ExistingHermesContainerProcess` reserves a future seam for a process in the already-existing upstream Hermes container; it does not authorize creating another container. `ToolchainDeploymentScope.HermesWorkbenchInternalModule` is validated at registration.

The Workbench-facing protocol is language-neutral. UI surfaces consume provider descriptors, build diagnostics, LSP capability state, DAP capability state, availability, and lifecycle results without exposing product-module installation choices.

## Architecture and protocol versions

- `DeveloperServicesProtocol.Version`: **1**
- `DeveloperServicesProtocol.ToolchainProviderVersion`: **1**
- DAP implementation: bounded `Content-Length` framing plus the session subset needed for initialize, launch/attach, configuration, breakpoints, threads, stack frames, scopes, variables, evaluation, continue/step, cancellation, disconnect, and adapter exit. The official DAP site reported specification **1.71.0** when this report was written.
- Contracts are immutable records or read-only interfaces and serialize through `System.Text.Json` without a third-party serialization dependency.
- Lines and columns remain one-based from MSBuild through the public contracts, matching Monaco marker inputs.

### Provider-neutral host

`IToolchainProvider` declares:

- stable provider identity and contract version;
- language IDs and project kinds;
- compiler/build, LSP, and DAP capabilities;
- local-sidecar or existing-Hermes-container process support;
- executable discovery and safe availability/error state;
- explicit workspace root and host-to-adapter path mappings;
- cancellable start/stop lifecycle and owned-resource disposal.

`ToolchainProviderRegistry` rejects duplicate IDs, incompatible contract versions, missing language/execution declarations, invalid execution kinds, and any deployment scope other than the complete Workbench internal-module scope. Selection is exact and capability-based. Zero matches return `NotFound`; multiple matches return `Ambiguous` instead of silently choosing. This is protocol routing, not AI interpretation.

`DotnetToolchainProvider` is adapter 1. It currently declares guarded `.sln`, `.slnx`, and `.csproj` build support for `csharp`. Its LSP and DAP flags deliberately remain false until an approved Roslyn language-service adapter and a provisioned debugger sidecar are integrated. `DotnetBuildRunner`, future Roslyn services, and future NetCoreDbg hosting belong behind this provider; they are not the provider-neutral endpoint itself.

### Guarded build path

`DotnetBuildRunner`:

- accepts only an existing `.sln`, `.slnx`, or `.csproj` under an explicit existing workspace root;
- canonicalizes root and target, rejects traversal/outside targets, rejects unsupported files, and rejects any reparse point in the selected path;
- starts `dotnet` directly with `UseShellExecute=false` and no shell;
- permits only the fixed vocabulary `build`, the validated target, `--nologo`, `--tl:off`, `--verbosity:minimal`, `--configuration`, and the `Debug`/`Release` enum value;
- reads stdout and stderr concurrently;
- bounds retained combined output and diagnostic count;
- stops only the process created by that invocation and its owned child tree when cancelled;
- returns classified, sanitized failures rather than raw exception or filesystem details.

`MsBuildDiagnosticParser` accepts only the canonical form:

`file(line,column[,endLine,endColumn]): error|warning|info CODE: message [project]`

The greedy file capture preserves Windows drive prefixes, spaces, parentheses before the final range, and Unicode. Ordinary restore/build progress is ignored.

### DAP path

`DapFrameDecoder` incrementally handles fragmented and back-to-back frames, validates a single non-negative `Content-Length`, enforces header and payload limits before payload allocation, and rejects malformed JSON/UTF-8/message types. `IDapMessageTransport` keeps the state machine independent from a future owned stdio sidecar process.

`DapSession` correlates requests and responses, negotiates adapter capabilities, waits for the `initialized` event after launch or attach, validates lifecycle states, handles breakpoint and inspection requests, maps stopped/continued/terminated events, sends best-effort DAP `cancel` when supported, fails pending work on adapter exit, and disconnects without opening a listener or selecting/attaching to a real process.

## Files changed

Hand-authored files:

- `src/Host/HermesDeveloperServices/HermesDeveloperServices.csproj`
- `src/Host/HermesDeveloperServices/DeveloperServicesProtocol.cs`
- `src/Host/HermesDeveloperServices/BuildContracts.cs`
- `src/Host/HermesDeveloperServices/BuildTargetResolver.cs`
- `src/Host/HermesDeveloperServices/BoundedTextCapture.cs`
- `src/Host/HermesDeveloperServices/MsBuildDiagnosticParser.cs`
- `src/Host/HermesDeveloperServices/DotnetBuildRunner.cs`
- `src/Host/HermesDeveloperServices/DapContracts.cs`
- `src/Host/HermesDeveloperServices/DapFramingCodec.cs`
- `src/Host/HermesDeveloperServices/IDapMessageTransport.cs`
- `src/Host/HermesDeveloperServices/DapSession.cs`
- `src/Host/HermesDeveloperServices/ToolchainContracts.cs`
- `src/Host/HermesDeveloperServices/ToolchainProviderRegistry.cs`
- `src/Host/HermesDeveloperServices/DotnetToolchainProvider.cs`
- `src/Host/HermesDeveloperServices.Smoke/HermesDeveloperServices.Smoke.csproj`
- `src/Host/HermesDeveloperServices.Smoke/Program.cs`
- `src/Host/HermesDeveloperServices.Smoke/FakeDapAdapter.cs`
- `docs/coordination/COMPILER-DEBUGGER-LANE-REPORT.md`

Release `bin`/`obj` outputs were produced only inside the two lane-owned project folders by verification builds.

## Dependencies added

None. Both projects use only the .NET 10 shared framework and a project reference from the smoke executable to the library. No NuGet package was added. NetCoreDbg, Roslyn Workspaces/Features, JDT LS, `java-debug`, `vsdbg`, Visual Studio, VS Code, C# Dev Kit, or any proprietary IDE binary is not bundled or referenced.

## Tests and builds run

1. `dotnet build .\src\Host\HermesDeveloperServices\HermesDeveloperServices.csproj --configuration Release --nologo --tl:off --verbosity:minimal`
   - Result: passed
   - Final total: 0 warnings, 0 errors

2. `dotnet build .\src\Host\HermesDeveloperServices.Smoke\HermesDeveloperServices.Smoke.csproj --configuration Release --nologo --tl:off --verbosity:minimal`
   - Result: passed
   - Final total: 0 warnings, 0 errors

3. `dotnet run --project .\src\Host\HermesDeveloperServices.Smoke\HermesDeveloperServices.Smoke.csproj --configuration Release --no-build`
   - Result: passed
   - Final total: 8 passed, 0 failed

Smoke coverage includes:

- two unrelated synthetic providers registering and selecting without language coupling;
- single-product internal deployment scope on every provider;
- Unicode Windows paths, point locations, ranged diagnostics, project capture, and ignored progress;
- successful safe build, outside-root rejection, unsupported-target rejection, retained-output bound, and dropped-character count;
- cancellation of only a disposable slow build fixture;
- fragmented and multiple DAP frames;
- invalid and oversized DAP frames;
- fake initialize/capability negotiation, launch, attach, breakpoints, configurationDone, stopped/continued state, threads, stackTrace, scopes, variables, evaluate, continue, step over/in/out, supported cancellation, disconnect, and adapter exit.

The build fixture creates only synthetic `.csproj` files under a unique temp directory and removes that directory. It never reads or builds repository source or secrets. DAP tests use an in-memory fake and never launch, listen, attach, or debug a real process.

## Security and trust boundaries

- Workspace root is the explicit filesystem authority; no target inference or broad directory scan is performed by the build runner.
- Reparse points are rejected instead of attempting to follow and re-authorize them.
- Arguments use `ProcessStartInfo.ArgumentList`; arbitrary CLI fragments, response files, environment overrides, targets, properties, and loggers are not accepted.
- Output and diagnostic retention are bounded. DAP header/payload retention is bounded.
- Cancellation never enumerates or kills by process name. Only the locally created `Process` instance and its owned child tree are terminated.
- The provider contract requires explicit workspace/path mappings and owned lifecycle cancellation for future sidecars.
- DAP listener mode is not implemented. A future NetCoreDbg adapter should use owned stdin/stdout (`--interpreter=vscode`) and must not enable `--server`.
- Raw adapter rejection messages and launch exceptions are not surfaced as trusted UI content.
- No deterministic AI interpretation or decision rule was added. Exact path validation, parser recognition, state transitions, capability matching, and ambiguity reporting are protocol/safety mechanics only.

## Read-only Roslyn architecture audit

The following existing files were inspected read-only:

Project Ali:

- `C:\Users\clsor\Documents\Codex\ProjectAli\src\Modules\Coding\AliCodingProjectResolver.cs`
- `C:\Users\clsor\Documents\Codex\ProjectAli\src\Modules\Coding\AliRoslynWorkspaceLoader.cs`
- `C:\Users\clsor\Documents\Codex\ProjectAli\src\Modules\Coding\RoslynActions\AliRoslynOwnedProviderCatalog.cs`
- `C:\Users\clsor\Documents\Codex\ProjectAli\src\Modules\Coding\Infrastructure\AliBoundedProcessRunner.cs`
- `C:\Users\clsor\Documents\Codex\ProjectAli\src\Modules\Coding\AliMsBuildRuntime.cs`

Project Scarlett:

- `C:\Users\clsor\Documents\Codex\ProjectScarlett\src\Ali.csproj`
- `C:\Users\clsor\Documents\Codex\ProjectScarlett\src\Modules\Coding\AliRoslynWorkspaceLoader.cs`
- `C:\Users\clsor\Documents\Codex\ProjectScarlett\src\Modules\Coding\RoslynActions\AliRoslynOwnedProviderCatalog.cs`
- `C:\Users\clsor\Documents\Codex\ProjectScarlett\src\Modules\Coding\Infrastructure\AliBoundedProcessRunner.cs`

General-purpose patterns adapted:

- validate a precise solution/project boundary and reject reparse escapes before compiler access;
- keep line/column contracts one-based at the external boundary;
- make the workspace or sidecar owner responsible for lifetime and disposal;
- launch fixed executables without a shell, redirect both streams, propagate cancellation, and terminate only the owned child;
- use explicit trusted provider registration rather than assembly scanning, MEF discovery, or implicit construction;
- treat incomplete semantic workspace loads and provider ambiguity as explicit unavailable/error states rather than guessing.

No Ali/Scarlett project-specific behavior, deterministic AI decision, semantic fingerprint rule, secret, binary, executable lease implementation, action publication workflow, or license-incompatible code was copied.

### Serena and Roslyn

Serena remains Hermes's agent semantic-map and tool layer: repository navigation, semantic discovery, and agent-facing tool orchestration. It is not replaced by this work. Roslyn is the future authoritative C# compiler/editor layer for syntax trees, semantic models, compiler diagnostics, completion, references, code actions, and exact C# project/solution understanding. The .NET provider should expose Roslyn results through the same provider-neutral LSP/diagnostic contracts while Serena continues to supply higher-level agent context and tools. Neither layer should reinterpret the other's authoritative outputs.

## NetCoreDbg recommendation and distribution gate

NetCoreDbg is the recommended candidate for a later .NET DAP sidecar because the upstream project implements VS Code DAP for CoreCLR and declares the MIT license. Nothing was downloaded or executed in this lane.

Before distribution, the single Hermes installer must:

1. select and approve one exact upstream release/tag and full commit ID;
2. record a platform-specific SHA-256 for every packaged release asset in a checked-in installer manifest;
3. verify the hash before installation and again before first execution where practical;
4. carry the upstream MIT copyright/license text plus required third-party notices in Hermes NOTICE material;
5. review the exact release's bundled native/transitive components and export/platform obligations;
6. provision it as an internal component of the one complete Hermes Workbench installer, never a separate installer or Docker container;
7. launch it only on demand as an owned local stdio child with fixed `--interpreter=vscode` arguments and explicit workspace/source mapping;
8. present an ordinary provider `Unavailable`/no-debugger state when absent, unapproved, hash-invalid, incompatible, or crashed.

No version or asset hash is pinned in this lane because no distribution artifact was selected or authenticated. Runtime downloading is not recommended. `vsdbg`, Visual Studio, VS Code, C# Dev Kit, and proprietary IDE/debugger binaries remain out of scope; they may only be listed as license-review alternatives and must not become dependencies.

## Exact future Java adapter seam

Java is not implemented or downloaded. A future Java integration should add one internal provider implementation behind the existing contracts:

1. Implement `IToolchainProvider` with `ContractVersion = 1`, `LanguageIds = ["java"]`, declared Maven/Gradle/standalone project kinds, `ToolchainDeploymentScope.HermesWorkbenchInternalModule`, and only approved execution kinds.
2. Report compiler/build, LSP, and DAP capabilities independently. An unavailable JDT LS must not falsely disable an otherwise available build tool, and an unavailable debugger must produce the normal no-debugger state.
3. In `DiscoverExecutablesAsync`, resolve an installer-provisioned JDK, Eclipse JDT LS, and Microsoft `java-debug` assets from approved locations. Pin versions, full source revisions, SHA-256 values, EPL-2.0/MIT texts, and all third-party NOTICE files in the one Hermes installer manifest. Do not download at runtime.
4. In `StartAsync`, validate `ToolchainStartContext.WorkspaceRoot` and every `WorkspacePathMapping`; then start owned JDT LS and Java debug adapter child processes on demand. Use cancellable stdio/named-pipe transport where supported, no public listener, no arbitrary JVM arguments, and no process-name killing.
5. Reuse the provider-neutral `ILspMessageTransport`, `LspFrameDecoder`, and `LspSession` now implemented beside DAP; adapt JDT LS diagnostics/completion/hover/definition/reference/rename results through later language-neutral host messages.
6. Reuse `DapFrameDecoder`, `DapSession`, and the DAP contracts for `java-debug`; add only capability extensions proven necessary by its negotiated response.
7. Register the Java provider in the same `ToolchainProviderRegistry` used by .NET. The host selects it from file/project language metadata; the Workbench UI protocol and product installation model do not change.
8. Include and provision Java support in the one complete Hermes installer when approved. Do not expose separate Java/C#/Python editions or a customer-facing partial-component chooser.

The official Eclipse JDT LS repository describes it as a Java LSP implementation with diagnostics, completion, navigation, and code actions under EPL-2.0. Microsoft's Java debugger repository describes launch/attach, breakpoints, stepping, variables, call stacks, threads, and evaluation under MIT. Exact compatibility, packaging, telemetry defaults, runtime requirements, and licenses still require release-specific review before implementation.

## Integration instructions for Super

No integration edits were made. The exact existing files expected to need integration work are:

- `src/Host/HermesDesktop/HermesDesktop.csproj`
  - add a project reference to `..\HermesDeveloperServices\HermesDeveloperServices.csproj`;
  - keep this in the existing desktop product and installer, not a new executable edition.
- `src/Host/HermesDesktop/MainWindow.xaml.cs`
  - own one developer-services bridge/registry for the window lifetime;
  - advertise one versioned `developerServices` capability in the existing desktop-ready and host-pong payloads;
  - route bounded build, cancellation, provider availability, and DAP messages through the existing trusted WebView message boundary;
  - dispose all owned build/debug/language-service work during the existing window close path.
- `src/app/App.tsx`
  - connect existing Run/activity controls to provider-neutral build/debug actions and maintain Problems/debug state;
  - do not add language-edition or partial-install choices.
- `src/Modules/Workspace/WorkspaceEditor.tsx`
  - pass diagnostics, breakpoint state, and debug navigation into the existing Monaco editor while preserving its public React interface where possible.
- `src/Modules/Workspace/MonacoEditor.tsx`
  - translate `BuildDiagnostic.Range` and severity into `monaco.editor.setModelMarkers`;
  - add breakpoint glyph interaction and stopped-frame decoration from DAP events;
  - clear markers and decorations by versioned owner/session when results become stale.
- `src/Modules/NativeTerminal/TerminalDock.tsx`
  - replace the static `PROBLEMS 0` label with the provider-neutral diagnostic list/count and add Output/Debug Console views without disturbing the native terminal lifecycle.
- `src/app/styles.css`
  - style Problems, build state, debug toolbar, call stack, variables, and unavailable/no-debugger states using existing Workbench tokens.

Super should add a small typed renderer client module adjacent to the relevant Workbench module rather than putting protocol parsing directly in `App.tsx`. On the host side, a bridge class should adapt WebView messages to `ToolchainProviderRegistry`, `DotnetBuildRunner`, and later DAP/LSP transports. Build request IDs and debug session IDs must be correlated; only one owner may cancel its matching operation.

Monaco/Problems mapping is direct: one-based file/range/severity/code/message/project/source contracts become markers and list entries. Selecting a problem opens the existing workspace path and reveals its range. DAP source paths must pass through the explicit `WorkspacePathMapping` before Monaco navigation.

## Integration checkpoint - 2026-08-09

The build portion of this handoff is now integrated into the one Hermes Workbench product:

- `DeveloperServicesBridge` advertises the versioned provider registry and bounded workspace target discovery through the existing trusted WebView2 boundary.
- The typed renderer client and controller correlate describe, build, and cancel messages without placing protocol parsing in `App.tsx`.
- **Run and Debug** exposes discovered `.sln`, `.slnx`, and `.csproj` targets, Debug/Release configuration, build/stop state, provider availability, and honest LSP/debugger **Planned** states.
- Build output reaches the lower **Output** tab. Bounded, deduplicated diagnostics populate **Problems**, status-bar error/warning counts, workspace-relative navigation, and Monaco markers under a versioned owner.
- The developer-services smoke suite passes 13/13, including safe discovery, output bounds, cancellation, a real broken-project diagnostic-deduplication check, DAP fake-adapter coverage, bounded LSP framing/session/cancellation coverage, and a real owned stdio child-process round trip with bounded stderr.
- The desktop smoke suite passes the actual renderer-to-native describe/build contract, alongside Credential Manager, OpenRouter sanitization, ConPTY, and the read-only Codex handshake. The frontend passes 130/130 tests across 30 files and its production build succeeds.

This checkpoint does not change the previously stated security and packaging boundaries. It adds a listener-free LSP protocol nucleus and a reusable shell-free transport for one explicitly authorized stdio child process, but no Roslyn Workspaces/Features, actual Roslyn/JDT server, real debugger process, Java tooling, runtime download, or additional container.

## Remaining limitations and not-live-verified behavior

- No Roslyn Workspaces/Features package, C# LSP process, semantic model, analyzer, completion, reference, rename, or code-action implementation is included yet.
- No real debugger transport or adapter process host is implemented. DAP behavior is verified only against the in-memory fake. The LSP transport is real-process verified, but only against the disposable smoke fixture.
- No real launch, attach, listener, debuggee, breakpoint, or source-map operation was attempted.
- NetCoreDbg was researched but not downloaded, pinned, hash-verified, packaged, licensed in NOTICE material, or live-tested.
- Java/JDT LS/`java-debug`, Python, C, and C++ providers are contracts/report-only future work and were not downloaded or tested.
- The .NET provider discovers `dotnet` from `PATH`; a production installer/host should bind and revalidate its approved SDK executable path rather than trust mutable PATH resolution.
- The build runner has caller cancellation but no independent wall-clock timeout policy. The host should apply a bounded timeout appropriate to the UI operation.
- Retained build output keeps the earliest text up to the configured shared stdout/stderr limit; a future UI may prefer a bounded ring/tail while keeping diagnostics independently bounded.
- The smoke verifies traversal/outside-root rejection but does not create a real Windows junction/symlink; reparse rejection is exercised by code inspection and build-time coverage only.
- DAP covers the required nucleus, not the full 1.71 schema. Reverse adapter requests such as `runInTerminal` are intentionally unsupported; client capabilities declare that fact.
- Guarded build support is live in the desktop host and renderer. LSP protocol handling and exact-child stdio hosting are implemented and fixture-verified, but actual Roslyn/JDT execution, renderer language requests, real DAP execution, breakpoint state, stopped-frame decoration, variables, call stacks, and a Debug Console remain unimplemented.
- Only the current Windows/.NET 10 development environment was verified.

## Official sources consulted

- [Debug Adapter Protocol overview and current specification](https://microsoft.github.io/debug-adapter-protocol/)
- [DAP change log and capability evolution](https://microsoft.github.io/debug-adapter-protocol/changelog.html)
- [`dotnet build` command documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build)
- [NetCoreDbg upstream repository](https://github.com/Samsung/netcoredbg)
- [NetCoreDbg MIT license](https://raw.githubusercontent.com/Samsung/netcoredbg/master/LICENSE)
- [Roslyn upstream repository](https://github.com/dotnet/roslyn)
- [Eclipse JDT Language Server upstream repository](https://github.com/eclipse-jdtls/eclipse.jdt.ls)
- [Microsoft Java debugger upstream repository](https://github.com/microsoft/vscode-java-debug)

Super can find the completed nucleus in `src/Host/HermesDeveloperServices`, its verification executable in `src/Host/HermesDeveloperServices.Smoke`, and this report at `docs/coordination/COMPILER-DEBUGGER-LANE-REPORT.md`.
