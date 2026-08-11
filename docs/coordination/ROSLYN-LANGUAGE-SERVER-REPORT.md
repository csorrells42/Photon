# Roslyn Language Server isolated lane report

## Completion status

The isolated Roslyn LSP provider is complete in the two assigned new host projects. It compiles against the existing provider-neutral `HermesDeveloperServices` contracts, owns one explicitly installer-authorized stdio sidecar, negotiates the required language capabilities, tracks document versions, rejects stale work, bounds retained data, and shuts down only its owned transport.

No existing developer-services source, desktop host, Workbench renderer, installer, package manifest, Docker file, `HermesSessionAdmin` file, `HermesSystemWorkspace.tsx`, or other module/report was modified. No real Roslyn server was downloaded, installed, launched, copied, or inspected.

## Files created

Provider library:

- `src/Host/HermesRoslynLanguageServer/HermesRoslynLanguageServer.csproj`
- `src/Host/HermesRoslynLanguageServer/AssemblyInfo.cs`
- `src/Host/HermesRoslynLanguageServer/RoslynProviderContracts.cs`
- `src/Host/HermesRoslynLanguageServer/RoslynLanguageSession.cs`
- `src/Host/HermesRoslynLanguageServer/RoslynLanguageServerProvider.cs`

Smoke executable:

- `src/Host/HermesRoslynLanguageServer.Smoke/HermesRoslynLanguageServer.Smoke.csproj`
- `src/Host/HermesRoslynLanguageServer.Smoke/Program.cs`

Handoff:

- `docs/coordination/ROSLYN-LANGUAGE-SERVER-REPORT.md`

Release `bin`/`obj` files were generated only under the two owned project directories by the required builds.

## Dependencies

No NuGet or other dependency was added. `HermesRoslynLanguageServer` has one project reference to the existing read-only `HermesDeveloperServices` project. The smoke executable references only the new provider project. Both target `net10.0` and use the shared framework.

The Microsoft Roslyn server package is a future installer payload, not a compile-time package reference and not a runtime download.

## Provider contract

`RoslynLanguageServerProvider` implements the existing `IToolchainProvider` contract:

- provider ID: `roslyn-lsp`
- contract version: `DeveloperServicesProtocol.ToolchainProviderVersion` (`1`)
- language IDs: `csharp`
- project kinds: `sln`, `slnx`, `csproj`
- build: unsupported by this adapter; the existing .NET build provider remains authoritative
- LSP: diagnostics, completion, hover, definition, references, and rename declared through the existing provider descriptor
- DAP: unsupported
- execution kind: `LocalSidecarProcess` only
- deployment scope: inherited default `HermesWorkbenchInternalModule`

Code actions are negotiated and exposed by `RoslynLanguageSession`, but the current read-only `ToolchainLspCapabilities` record has no `SupportsCodeActions` field. Super may advertise the session's `RoslynNegotiatedCapabilities.CodeActions` through a bounded host description now, or add a separately versioned provider-contract field in a future shared-contract lane.

The host must reference both `HermesDeveloperServices` and this provider project. `HermesDeveloperServices` must not reference this project, because that would create a dependency cycle. The one developer-services host constructs this adapter explicitly and registers it in the existing `ToolchainProviderRegistry`.

## Executable ownership and launch contract

Construction requires `RoslynLanguageServerConfiguration` with all of:

- one absolute installer authority root;
- one absolute `Microsoft.CodeAnalysis.LanguageServer.exe` path contained under that authority;
- one exact 64-hex-character SHA-256;
- one bounded package version string;
- one absolute installer-provisioned `DOTNET_ROOT` contained under the same authority.

Discovery never scans `PATH`, probes a drive, launches a process, downloads an asset, or accepts renderer input. It checks only the configured locations, rejects reparse points, hashes the configured executable, and returns a safe unavailable state for missing or mismatched provisioning.

Start revalidates the executable and hash, accepts only `ToolchainExecutionKind.LocalSidecarProcess`, requires an existing absolute workspace root, rejects reparse roots, and requires an exact identity host-to-adapter workspace mapping. It then creates this fixed launch specification:

```text
Executable: <installer-root>/.../Microsoft.CodeAnalysis.LanguageServer.exe
ArgumentList[0]: --stdio
WorkingDirectory: <validated workspace root>
UseShellExecute: false (enforced by existing LspProcessTransport)
InheritEnvironment: false
```

The cleared environment receives exactly these provider-owned values:

```text
DOTNET_ROOT=<installer-provisioned absolute runtime root>
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
DOTNET_MULTILEVEL_LOOKUP=0
```

There is no named pipe/server/listener mode, daemon mode, extension assembly argument, arbitrary environment, raw argument, generic LSP method, generic command, process-name lookup, or runtime acquisition endpoint. `LspProcessTransport` redirects stdin/stdout/stderr, never invokes a shell, and terminates only its exact owned process tree after the graceful-exit window.

## Bounded LSP surface

`RoslynLanguageSession` is a provider-specific bounded client over the existing `ILspMessageTransport` and existing provider-neutral LSP message records. It does not expose Roslyn workspaces, compilations, projects, solutions, syntax trees, semantic models, MEF exports, assembly scanning, extension catalogs, or compiler object graphs.

Initialization advertises no dynamic registration and requires the server to negotiate:

- document synchronization;
- completion;
- hover;
- definition;
- references;
- rename;
- code actions.

Diagnostics use bounded `textDocument/publishDiagnostics` notifications. Required operations are closed public methods, not caller-provided LSP method strings:

- `OpenDocumentAsync`
- `ChangeDocumentAsync`
- `CloseDocumentAsync`
- `CompletionAsync`
- `HoverAsync`
- `DefinitionAsync`
- `ReferencesAsync`
- `RenameAsync`
- `CodeActionsAsync`
- `ShutdownAsync`

Limits:

- 4 Mi characters per synchronized document;
- 256 open documents;
- 128 in-flight requests;
- 2,000 retained diagnostics;
- 16,384 characters per diagnostic message;
- 128 characters per diagnostic source;
- 1 Mi character per outward language result;
- 32 Ki characters retained stderr, with a dropped-character count;
- 512 characters for a rename target.

Transport framing remains bounded by the existing 4 MiB `LspFrameDecoder` payload ceiling. A server request is answered with JSON-RPC method-not-found rather than dispatched. Server errors are converted to safe method/code failures instead of returning raw stderr.

Every open document has one current integer version. `didChange` must strictly increase it. Completion, hover, definition, references, rename, and code-action calls must name the exact current version; otherwise `RoslynStaleDocumentException` is raised before a server request. Versioned diagnostics not matching the current document are dropped. Cancellation removes only the correlated pending request and sends `$/cancelRequest` with that exact LSP request ID.

Shutdown sends `shutdown`, then `exit`, then disposes the injected transport. Failed initialization disposes the transport. Provider stop/disposal owns no unrelated process or global service.

Rename and code-action responses are bounded LSP JSON values only. This lane does not apply returned edits or execute returned commands. A future host integration must validate workspace edits against the same workspace authority, correlate them with the current document versions, preview user-visible effects where appropriate, and never execute a server-supplied command through a generic endpoint.

## Serena and Roslyn responsibilities

The existing responsibility map remains unchanged:

- Serena is the shared agent-facing semantic map and task-oriented navigation/editing tool layer.
- Roslyn is authoritative for C# compiler/editor diagnostics, completion, hover, navigation, references, rename, and code actions.
- The Workbench UI consumes provider-neutral bounded results; agents do not receive Roslyn object graphs.
- Neither layer reinterprets the other's authoritative results.

This adapter adds no agent-facing Roslyn tool, AI interpretation, deterministic semantic decision rule, assembly scan, or alternate semantic map.

## Microsoft/Roslyn distribution research

Research was limited to official Microsoft/.NET Foundation sources. Nothing was downloaded.

### Public RID package candidate

The current public NuGet Gallery page exposes `Microsoft.CodeAnalysis.LanguageServer.win-x64` version `5.0.0-1.25277.114`. The page marks it prerelease, owned by Microsoft/dotnetframework, prefix-reserved, targeting .NET 9, licensed MIT, and built from `dotnet/dotnet` commit `ddf39a1b4690fbe23aea79c78da67004a5c31094`. The page lists one published version, last updated 2025-06-06, plus a substantial transitive dependency set. Source: [official NuGet package page](https://www.nuget.org/packages/Microsoft.CodeAnalysis.LanguageServer.win-x64/5.0.0-1.25277.114).

The current Roslyn `Program.cs` supports `--stdio`, explicitly rejects combining stdio with pipe/daemon operation, and binds standard input/output directly in stdio mode. This is the transport required by Hermes. Source: [Roslyn language-server Program.cs](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Program.cs).

Roslyn source is MIT licensed. Its repository also carries a large third-party notice inventory that must travel through a distribution review. Sources: [Roslyn MIT license](https://raw.githubusercontent.com/dotnet/roslyn/main/License.txt) and [Roslyn third-party notices](https://raw.githubusercontent.com/dotnet/roslyn/main/THIRD-PARTY-NOTICES.txt).

Microsoft's official C# VS Code extension uses a Roslyn-backed LSP server and provides completion, navigation, references, and refactoring/code-fix features. The extension source is MIT, but the shipped extension is also subject to Microsoft extension license terms. Therefore Hermes should not extract or redistribute a VSIX/C# Dev Kit payload as its server acquisition mechanism. Source: [official dotnet/vscode-csharp repository](https://github.com/dotnet/vscode-csharp).

### Recommended pin and distribution gate

Recommended integration baseline for Windows x64 evaluation:

```text
Package ID: Microsoft.CodeAnalysis.LanguageServer.win-x64
Version: 5.0.0-1.25277.114
Source commit recorded by NuGet: ddf39a1b4690fbe23aea79c78da67004a5c31094
Top-level license: MIT
Runtime target reported by NuGet: net9.0
```

This is a baseline candidate, not approval to ship blindly: it is prerelease and older than the current Roslyn/C# extension code. Before Super or the installer lane adopts it, CI must verify that this exact package's executable accepts `--stdio`, negotiates every required capability, loads supported SDK/project formats, honors shutdown, and operates with the cleared Hermes environment. If Microsoft publishes a newer official RID package, the version change must be an explicit reviewed manifest change, never a floating `latest` selection.

Required installer strategy:

1. Acquire the exact package at installer-build time from the official NuGet v3 source; never at Hermes runtime.
2. Pin package ID, exact version, source URL, NuGet repository-signature result, reported source commit, RID, and target framework in the one Hermes installer manifest.
3. Record SHA-256 for the original `.nupkg`, the extracted `Microsoft.CodeAnalysis.LanguageServer.exe`, every runtime/dependency file actually shipped, the license, and the exact notice inventory.
4. Fail packaging on any missing file, unexpected file, signature failure, hash mismatch, source/version drift, or unreviewed license/notice change.
5. Provision a compatible pinned .NET runtime/SDK under the same installer authority and pass its absolute location as `DOTNET_ROOT`; do not resolve it from `PATH`.
6. Recompute and verify the executable hash at install/update time. The provider rechecks it before availability and every start.
7. Include the MIT license and the release-specific third-party notices in Hermes NOTICE material. Review every extracted dependency's license; do not assume the Roslyn top-level MIT license replaces transitive obligations.
8. Keep Roslyn as an internal component of the single complete Hermes Workbench installer. Do not create a Roslyn edition, separate customer installer, plugin marketplace item, or Docker container.
9. Retain provenance/source-offer information required by any transitive license and repeat legal/security review for every package update.

No package hash is claimed in this report because downloading the real package was explicitly forbidden. The installer lane must derive the first authenticated hashes from the exact approved artifact and check them into its owned manifest.

## Smoke coverage

The smoke executable creates only a random temporary workspace, a synthetic installer tree, a text fixture named `Microsoft.CodeAnalysis.LanguageServer.exe` that is hashed but never executed, and an in-memory `IRoslynServerTransport` fake. It removes the fixture on completion.

Seven passing smoke scenarios prove:

1. absolute installer containment, exact executable name, SHA-256 success, tamper failure, and relative/outside path rejection;
2. the exact `--stdio` argument list, exact working directory, fixed four-entry environment, provider descriptor, and required capability negotiation;
3. diagnostics plus completion, hover, definition, references, rename, code actions, document open/close, and diagnostic source/message bounds;
4. one cancelled request emits one `$/cancelRequest` for the exact held request ID;
5. duplicate/older document changes, stale feature requests, and stale versioned diagnostics are rejected;
6. stderr retention/drop accounting and outward result limits;
7. missing capability failure disposes the fake, while normal stop sends `shutdown`, then `exit`, disposes transport, and reaches `Stopped`.

No network, listener, shell, PATH scan, external process, real Roslyn server, repository source, credential, user configuration, or installer operation is used by smoke.

## Commands and results

Provider Release build:

```powershell
dotnet build .\src\Host\HermesRoslynLanguageServer\HermesRoslynLanguageServer.csproj --configuration Release --nologo --tl:off --verbosity:minimal
```

Result: passed, 0 warnings, 0 errors.

Smoke Release build:

```powershell
dotnet build .\src\Host\HermesRoslynLanguageServer.Smoke\HermesRoslynLanguageServer.Smoke.csproj --configuration Release --nologo --tl:off --verbosity:minimal
```

Result: passed, 0 warnings, 0 errors.

Smoke execution:

```powershell
dotnet run --project .\src\Host\HermesRoslynLanguageServer.Smoke\HermesRoslynLanguageServer.Smoke.csproj --configuration Release --no-build
```

Result: passed, 7/7 scenarios.

The first smoke build surfaced one nullable-analysis error in the smoke harness. The owned test dereference was corrected; the final builds above are the acceptance results.

## Integration instructions for Super

No integration file was edited.

1. In the one local developer-services host project, add a project reference to `..\HermesRoslynLanguageServer\HermesRoslynLanguageServer.csproj` alongside its existing reference to `HermesDeveloperServices`.
2. Have the one complete installer provision the approved Roslyn package/runtime/NOTICE set and pass the manifest's absolute installer root, executable path, executable SHA-256, package version, and `DOTNET_ROOT` into `RoslynLanguageServerConfiguration`.
3. Construct `RoslynLanguageServerProvider` explicitly and register it in the existing `ToolchainProviderRegistry`. Do not use reflection or assembly scanning.
4. Select it for `LanguageId = "csharp"` and `RequiresLsp = true`; continue selecting the existing .NET provider for builds. Provider-neutral renderer messages do not change by language.
5. Build `ToolchainStartContext` from the already-authorized workspace root with one exact local host/adapter path mapping. Do not accept arbitrary renderer paths or environment values.
6. Route only typed diagnostics/completion/hover/definition/references/rename/code-action/document/cancel operations to the closed session methods. Normalize bounded LSP JSON into Workbench DTOs; do not forward raw server commands or expose compiler object graphs.
7. Own one provider/session per authorized workspace lifecycle, correlate cancellation to the matching request, and call `StopAsync` during the existing developer-services/window shutdown path.
8. Keep Serena as the agent semantic-map layer. Do not add a broad agent Roslyn endpoint.
9. Do not touch `HermesSessionAdmin` or `HermesSystemWorkspace.tsx` for this integration.

## Remaining limitations

- The real Microsoft server was not downloaded or launched. Exact package layout, executable hash, `--stdio` compatibility of the 2025 package, capability shape, SDK discovery, project loading, analyzer behavior, performance, telemetry behavior, and shutdown are not live-verified.
- The recommended NuGet package is prerelease and currently has only one public version on its official page. A later official release may be a better ship candidate after the same pin/hash/license gate.
- The current Roslyn main source proves stdio support, but current-main behavior is not proof that every older packaged binary has the same command-line contract. Installer acceptance must test the exact pinned artifact.
- The cleared four-variable environment is intentionally strict. Real project loading may require additional narrowly approved installer-owned SDK/MSBuild/cache/temp configuration; add only fixed typed configuration after real-package testing, never arbitrary inherited environment.
- The provider does not download SDKs, restore packages, install workloads, or authenticate feeds. Those behaviors need separate explicit product policy.
- Document changes use bounded full-document synchronization. Incremental sync may be added later behind the same version rules if negotiated and tested.
- Diagnostics without a server-supplied version cannot be proven fresh; versioned mismatches are dropped. Host integration should clear markers on document change and correlate diagnostic ownership/session generation.
- LSP results remain bounded JSON inside the provider lane. Renderer DTO normalization and safe application of rename/code-action workspace edits remain Super-owned integration work.
- The existing provider descriptor has no code-action bit; session negotiation is authoritative until a versioned shared-contract addition is approved.
- Only Windows x64 packaging was researched because the public candidate inspected is `win-x64`. Other RIDs require separate official packages, hashes, runtime tests, and notice review.
- No desktop/UI/Monaco integration, installer packaging, Docker work, full-product build, or live C# workspace test was performed in this isolated lane.

## Explicit confirmations

- No real Roslyn/C# extension/C# Dev Kit package, binary, VSIX, runtime, or dependency was downloaded, installed, launched, copied, bundled, or modified.
- No `PATH` scan, shell, listener, daemon, named-pipe server, arbitrary argument, arbitrary environment, runtime downloader, generic command endpoint, assembly scan, or Roslyn object-graph endpoint was added.
- No existing `HermesDeveloperServices` file was edited.
- `HermesSessionAdmin` and `HermesSystemWorkspace.tsx` were not read or modified by this lane.
- Work stopped after this report; Super owns integration.
