# Developer Functionality — Deferred Security Notes

Date: 2026-08-11  
Scope: Roslyn, compiler, .NET debugger, Serena semantic Workspace Search  
Disposition: recorded for a future security pass; not remediated during the functionality pass

## Policy for this pass

The current objective is complete feature behavior. Findings below did not block the verified functional workflows and were intentionally not expanded into security implementation work. No credential, host-hardening, installer, ACL, or portable-secret behavior is changed by this note.

## DS-01 — Live Serena workspace identity can drift from the operator's intended workspace

Observed current state:

- The held live Serena listener on `127.0.0.1:9121` was launched for `C:\Users\clsor\OneDrive\Desktop\Hermes`.
- `launcher.settings.json` currently carries that same `WorkspacePath`.
- The current development checkout is `C:\Users\clsor\Documents\Codex\HermesAgent`.
- A separate disposable Serena `1.28.1` process launched for the current checkout returned the expected `DeveloperServicesBridge` symbols and was stopped after validation.

Why this is security-relevant later: a UI can honestly reach Serena while Serena is indexing a different directory than the operator believes, creating a workspace-confusion/data-scope risk.

Future-pass acceptance direction: bind every semantic request to a host-owned workspace identity and require trusted server evidence of the same canonical root before attributing results to Serena. A mismatch should disable semantic search until an explicit rebind. Do not infer workspace identity from renderer data.

## DS-02 — The standalone Serena loopback service exposes a broader MCP surface than Workspace Search uses

Observed current state:

- Serena `1.28.1` advertised 22 MCP tools on the isolated loopback validation server.
- The native Workspace Search adapter validates the exact read-only/non-destructive `find_symbol` annotations and exact argument schema before calling it.
- No per-client authentication mechanism was observed in the direct streamable-HTTP handshake used for validation.

Why this is security-relevant later: the Workbench adapter is narrow, but another local process can potentially address the broader Serena service directly rather than passing through the Workbench allowlist.

Future-pass acceptance direction: evaluate Windows-user-bound service authentication, process/ACL isolation, or a host-owned narrow proxy that does not expose unused tools. Preserve the current provider-neutral renderer contract.

## DS-03 — Debug attach authorization is single-use but is presently satisfied by the same renderer-originated operation

Observed current state:

- Launch and attach use typed operations and one-use exact request authorization.
- The desktop bridge authorizes the exact request immediately before calling the provider.
- Attach accepts a caller-selected positive PID; there is no separate native user-confirmation step in this functionality pass.

Why this is security-relevant later: if a trusted renderer session is compromised, the existing seam may not provide an independent user-intent boundary for attaching to another process.

Future-pass acceptance direction: keep the current typed/single-use seam, but consider a host-owned confirmation handle bound to operation, target identity, workspace/session, short expiry, consume/discard, and no replay. Do not add a generic command or process surface.

## DS-04 — DAP evaluation can have target-process side effects

Observed current state:

- The debugger supports bounded `watch`, `hover`, and `repl` evaluation strings while a debug session is active.
- The renderer has no shell or generic DAP request surface, but expression evaluation is still executed by the debug target/runtime.

Why this is security-relevant later: some expressions can mutate program state or invoke code even when presented as inspection.

Future-pass acceptance direction: define explicit product policy for read-like versus side-effecting evaluation contexts, and consider a host-owned confirmation boundary for `repl` or other side-effect-capable evaluations.

## DS-05 — Validation payloads are user-writable test installations

Observed current state:

- Real validation used pinned Roslyn `5.0.0-1.25277.114` and NetCoreDbg `3.1.3-1062` payloads under `%LOCALAPPDATA%\hermes\spat-live-toolchains`.
- Executable SHA-256 values matched their receipts during this pass.
- These are disposable validation payloads, not proof of production installer ACLs or publisher provenance.

Future-pass acceptance direction: production installation must preserve the existing fixed-path/hash/version checks and additionally prove installer ownership, immutable release provenance, and appropriate Windows ACLs. Do not treat this validation directory as a production trust boundary.

## DS-06 — A live-shaped OpenRouter credential is stored in plaintext runtime state under the workspace

Observed current state:

- `data/.env` line 1 contains a non-placeholder OpenRouter credential-shaped value. The value is intentionally omitted from this report and must never be copied into logs, browser output, tests, commits, reports, or handoffs.
- The root `.gitignore` excludes `.env` files and `data/`, and no duplicate credential was established in reviewed tracked source, Usage Intelligence code, tests, documentation, renderer assets, or generated reports.
- `docker-compose.yml` mounts the workspace-local `data` directory into the gateway container, so this is active plaintext runtime configuration rather than inert example text.
- Static inspection did not contact OpenRouter and therefore did not independently prove whether the credential remains valid.

Why this is security-relevant later: any local process or unfiltered workspace copy, archive, backup, or migration payload that can read the ignored `data` directory can acquire the credential. Git ignore rules reduce accidental staging but do not protect plaintext at rest or ad hoc bundles.

Future-pass acceptance direction: revoke and rotate the credential, remove provider secrets from workspace-local plaintext state, bind them to the approved Windows-user/machine credential store, and add redacting preflight scans for both Git content and ignored runtime/package inputs. Portable bundles, exports, backups, and migrations must omit machine-scoped opaque references and all secret values; moving machines intentionally requires re-entry or re-login.

## DS-07 — The .NET compiler/test authority is host-discovered from PATH rather than receipt-bound

Observed current state:

- The C# compiler and test capabilities use the existing host-discovered `dotnet` SDK and expose only fixed `build` and `test` verbs through typed, workspace-relative requests.
- Discovery requires an absolute existing executable before advertising the capabilities, but the current `DotnetBuildRunner` launches the `dotnet` name and inherits the host process environment.
- The description and execution lookups are separate, so a local PATH or executable change between those steps is not prevented by an immutable receipt.

Why this is security-relevant later: availability is honest at inspection time, but the current seam does not prove publisher identity, immutable SDK bytes, or byte-for-byte continuity between inspection and execution.

Future-pass acceptance direction: bind compiler/test execution to one host-owned absolute executable identity with version, hash or trusted publisher evidence, empty-by-default environment, and a receipt that is revalidated immediately before each operation. Preserve the current fixed verbs and typed target/filter contract.

## DS-08 — Running project tests intentionally executes workspace code

Observed current state:

- The new test operation is explicit, user-invoked, cancellable, bounded, and restricted to a selected `.sln`, `.slnx`, or `.csproj` under the active workspace.
- No tests run automatically on reconnect, refresh, project discovery, build, or language-service startup.
- `dotnet test` necessarily builds and executes test assemblies and related project build targets from the workspace.

Why this is security-relevant later: a malicious or unexpectedly modified project can execute code under the desktop user's account when the user chooses Run tests.

Future-pass acceptance direction: add a host-owned confirmation/trust policy bound to canonical workspace, project identity, operation, and short-lived single-use authorization. Keep reconnect and discovery non-executing, and retain explicit cancellation/process-tree ownership.

## DS-09 — Python unittest executes workspace code inside a read-write mounted container

Observed current state:

- Python syntax checking uses `compile()` against immutable source snapshots and does not execute those sources.
- The explicit Python test operation uses the fixed standard-library `unittest` runner inside the launcher-owned Hermes container.
- The active workspace is mounted read-write at `/workspace`; tests therefore run with container-user access to the same workspace files the Workbench exposes.
- No Python test runs automatically during provider inspection, refresh, reconnect, language-service verification, or syntax checking.

Why this is security-relevant later: a malicious or unexpectedly modified Python test can execute arbitrary Python inside the Hermes container and may modify any workspace content writable through the mount. Container execution reduces direct host-process exposure but is not a read-only analysis boundary.

Future-pass acceptance direction: add a host-owned, short-lived, single-use confirmation/trust decision for Python test execution; consider a disposable read-only source snapshot plus a separately bounded writable output directory. Preserve explicit invocation, exact container/image/workspace binding, cancellation, and no automatic replay.

## DS-10 — Python tooling depends on local Docker daemon authority

Observed current state:

- The provider launches only the fixed Docker CLI path and fixed `inspect`/`exec` operations; renderer input cannot select an executable, container, image, argument vector, environment, URL, or working directory.
- Every syntax/test operation re-verifies the exact running container ID, immutable image ID, running state, and dedicated workspace mount before execution.
- The Docker daemon remains a privileged local control plane outside the renderer contract.

Why this is security-relevant later: compromise or misconfiguration of the local Docker daemon/desktop boundary can invalidate assumptions made by a client-side image and mount check even though the renderer request surface remains narrow.

Future-pass acceptance direction: document and verify the required Windows Docker Desktop ACL/ownership boundary, launcher-to-desktop identity handoff, and daemon provenance. Keep the current exact-operation adapter and do not expose a generic Docker or shell surface.

## Functionality evidence that remains valid

These deferred items do not invalidate the current functionality evidence:

- real Roslyn initialization, solution load, hover, definition, references, rename, code actions, completion, diagnostics, stale-version rejection, and shutdown;
- compiler build/analyze and cancellation workflows;
- real NetCoreDbg launch, breakpoint, stopped/continued events, threads, stack, scopes, variables, evaluate, continue, step-in/out/over, disconnect, cancellation recovery, and explicit restart;
- real Serena MCP handshake, exact `find_symbol` validation, native path-policy projection, bounded previews, and typed activation coordinates.
- real Python 3.13.5 container identity, bounded project inspection, syntax compilation, explicit unittest execution, and Serena-backed language-session start/stop.
