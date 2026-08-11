# GitExtensions Phase 1A lane handoff

## Result

The isolated Phase 1A source-control core is complete inside the assigned ownership paths. It provides a `net10.0` host library, deterministic smoke executable, bounded protocol-v1 renderer client, and an unintegrated React source-control workspace. The third activity icon is intentionally not wired.

The owned targets did not exist when the lane began, so the collision audit was clean.

## Authored files

Host library:

- `src/Host/HermesGitServices/HermesGitServices.csproj`
- `src/Host/HermesGitServices/AssemblyInfo.cs`
- `src/Host/HermesGitServices/Contracts.cs`
- `src/Host/HermesGitServices/GitErrorRedactor.cs`
- `src/Host/HermesGitServices/GitExecutableLocator.cs`
- `src/Host/HermesGitServices/RepositoryResolver.cs`
- `src/Host/HermesGitServices/GitCommandRunner.cs`
- `src/Host/HermesGitServices/GitStatusParser.cs`
- `src/Host/HermesGitServices/GitExtensionsLocator.cs`
- `src/Host/HermesGitServices/GitExtensionsLauncher.cs`
- `src/Host/HermesGitServices/HermesGitService.cs`

Smoke project:

- `src/Host/HermesGitServices.Smoke/HermesGitServices.Smoke.csproj`
- `src/Host/HermesGitServices.Smoke/Program.cs`

Renderer module:

- `src/Modules/SourceControl/contracts.ts`
- `src/Modules/SourceControl/DesktopSourceControlClient.ts`
- `src/Modules/SourceControl/DesktopSourceControlClient.test.ts`
- `src/Modules/SourceControl/ChangesView.tsx`
- `src/Modules/SourceControl/SourceControlWorkspace.tsx`
- `src/Modules/SourceControl/SourceControlWorkspace.css`
- `src/Modules/SourceControl/SourceControlWorkspace.test.tsx`

Handoff:

- `docs/coordination/GITEXTENSIONS-PHASE1A-LANE.md`

`bin/`, `obj/`, TypeScript incremental metadata, and `dist/` are normal ignored build outputs produced by the required verification commands; they are not authored lane sources.

## Dependencies

No dependencies were added. No package manifest or lockfile changed. The host library uses only the .NET 10 base class library, and the renderer uses the repository's existing React/Vitest/TypeScript toolchain.

## Implemented behavior

- Discovers only absolute existing `git.exe` and `GitExtensions.exe` candidates through bounded candidate lists; discovery never executes a candidate.
- Resolves renderer-relative paths under one native workspace authority, walks to a repository root, understands `.git` directories and bounded `gitdir:` files, rejects path traversal/rooted input/reparse points, and revalidates before every native action.
- Mints 48-character random opaque repository IDs; absolute repository paths remain native-only.
- Exposes no generic Git or Git Extensions command surface.
- Runs one fixed, read-only status operation through `UseShellExecute=false` and `ProcessStartInfo.ArgumentList`.
- Removes Git repository/index/object/executable/config, pager, editor, askpass, SSH, and credential-interaction redirectors; forces noninteractive/no-pager/no-color/no-optional-lock behavior.
- Drains stdout and stderr concurrently into bounded buffers, applies timeout and caller cancellation, and kills only the exact owned child process tree.
- Classifies unsafe ownership without changing `safe.directory`; raw stderr and native paths are not returned in renderer DTOs.
- Parses NUL-framed porcelain v2 branch, stash, ordinary, rename, unmerged, untracked, ignored, and submodule records into bounded typed groups.
- Maps only Git Extensions surface `browse` to two separate arguments: `browse`, then the revalidated repository root.
- Renderer drops malformed, cyclic, wrong-version, oversized, bad-request-ID, and unknown frames; normalizes bounded values; requires request ID plus expected response kind for completion; rejects pending work and removes its listener on close.
- Renderer provides a cancel message seam, nonfatal browser/Git/GitExtensions unavailable states, branch/upstream/divergence/stash summary, all requested change groups, text-only filename rendering, responsive layout, focus-visible controls, large-text wrapping, and reduced-motion handling.

## Exact protocol-v1 contract

`SOURCE_CONTROL_PROTOCOL_VERSION` / `SourceControlProtocol.Version` is `1`.

Every message has `version: 1` and a host/client request ID limited to 128 characters matching `[A-Za-z0-9_:-]+`. Renderer frames are limited to 2 MiB before normalization. Renderer-selected repository paths are relative, at most 1,024 characters, and may not contain NUL. Status entries are limited to 10,000; parsed paths are limited to 32 Ki characters.

Renderer-to-host messages exposed by `DesktopSourceControlClient`:

- `sourceControl.describe`: `{ version, requestId }`
- `sourceControl.repository.resolve`: `{ version, requestId, workspaceRelativePath }`
- `sourceControl.status`: `{ version, requestId, repositoryId }`
- `sourceControl.cancel`: `{ version, requestId, targetRequestId }`
- `sourceControl.gitExtensions.open`: `{ version, requestId, repositoryId, surface: "browse" }`

Normalized host-to-renderer frames:

- `sourceControl.describe.result`
- `sourceControl.repository.resolve.result`
- `sourceControl.status.result`
- `sourceControl.gitExtensions.open.result`
- `sourceControl.error`

The normalizer accepts architecture-form availability (`available: boolean`) and the typed library form (`state: "available" | "unavailable" | "error"`). Repository resolution accepts the architecture's nested `repository` object. Git Extensions completion accepts architecture field `started` and maps it to the module's typed `succeeded` result.

The native `HermesGitService` facade exposes `Describe`, `ResolveRepository`, `GetStatusAsync`, and `OpenGitExtensionsAsync`. The future desktop bridge owns envelope validation/serialization and the exact request-to-cancellation-token registry.

## Exact Git and Git Extensions contracts

The only production Git command assembled by this lane is:

```text
<absolute-trusted-git.exe> --no-pager --no-optional-locks -c core.fsmonitor=false -C <validated-repository-root> status --porcelain=v2 --branch --show-stash -z --untracked-files=all
```

`<validated-repository-root>` is passed as one `ArgumentList` entry. No shell is used. There is no generic argument endpoint.

The only Git Extensions mapping is:

```text
surface: browse
executable: <discovered-absolute-GitExtensions.exe>
ArgumentList[0]: browse
ArgumentList[1]: <revalidated-repository-root>
```

Unknown surfaces are rejected before the launcher boundary.

## Verification

All final verification passed.

1. Host library Release build:

   ```powershell
   dotnet build .\src\Host\HermesGitServices\HermesGitServices.csproj --configuration Release --nologo --tl:off --verbosity:minimal
   ```

   Result: passed, 0 warnings, 0 errors.

2. Deterministic Release smoke:

   ```powershell
   dotnet run --project .\src\Host\HermesGitServices.Smoke\HermesGitServices.Smoke.csproj --configuration Release --no-restore
   ```

   Result: passed, 10/10 checks. Windows did not grant symbolic-link creation, so that fixture printed one explicit skip; outside-authority `.git` metadata escape rejection and the production reparse-point rejection code path remain covered structurally.

3. Focused SourceControl Vitest:

   ```powershell
   & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\vitest\vitest.mjs run Modules\SourceControl
   ```

   Result: passed, 2 files and 9 tests.

4. Focused frontend TypeScript compilation:

   ```powershell
   & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\typescript\bin\tsc -p .\tsconfig.app.json --noEmit --incremental false
   ```

   Result: passed with no diagnostics.

5. Full frontend test suite:

   ```powershell
   & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\vitest\vitest.mjs run
   ```

   Result: passed, 53 files and 233 tests.

6. Production TypeScript build phase:

   ```powershell
   & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\typescript\bin\tsc -b
   ```

   Result: passed with no diagnostics.

7. Vite production build:

   ```powershell
   & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\vite\bin\vite.js build
   ```

   Result: passed; 3,143 modules transformed. Vite retained the repository's existing warning that some minified chunks exceed 500 kB.

An initial direct `npm` attempt could not start because `npm` was absent from that shell's PATH. The coordinator-specified bundled Node commands above are the canonical final results.

## Smoke coverage and limitations

The smoke project creates only random temporary directories, synthetic `.git` markers, synthetic executable-name files that are never executed, and fake process/launcher abstractions. It covers:

- normal/unborn/detached branch metadata and all requested porcelain-v2 record/group types;
- distinct spaces, leading dash, tab, newline, quote, backslash, Unicode, emoji, and long filenames;
- NUL framing, entry limit, relative-path containment, rooted/traversal/missing/NUL inputs, `.git` metadata escape, and revalidation;
- exact Git argument list, absolute executable selection, poisoned environment removal, timeout/output/cancellation mapping, unsafe-ownership classification, opaque IDs, redaction, and unchanged sentinel files;
- fixed Git Extensions browse arguments, unknown-surface rejection, stale-repository rejection, and unavailable discovery;
- malformed/wrong-version/cyclic/oversized renderer frames, group rendering, HTML-as-text, correlation, cancellation messages, listener cleanup, and unavailable states.

Because the lane explicitly prohibited automated tests from launching real Git or GitExtensions, smoke verifies the production command/environment construction at the injected process boundary and feeds synthetic porcelain output to the real parser. It does not claim live-Git proof of hook/filter/diff/textconv/fsmonitor behavior. A later clean-machine integration pass may run the fixed status operation against a generated repository if product policy authorizes launching the user's Git. The symbolic-link runtime fixture also needs a Windows environment with link-creation privilege; the junction/symlink rejection implementation is present, while this run verified the separate metadata escape path.

No desktop host publish, portable ZIP rebuild, real external UI launch, or shared-file integration was performed.

## Shared-file integration checklist

### `src/app/App.tsx`

- Import `SourceControlWorkspace` from `src/Modules/SourceControl/SourceControlWorkspace`.
- Add `'sourceControl'` to the existing `LeftPanel` union.
- Wire the existing third `GitBranch` activity button's active state and click behavior; preserve its keyboard/ARIA label.
- Render `SourceControlWorkspace` for that panel and pass the Workspace Explorer-selected path as a workspace-relative path (use `.` for the authority root).
- Do not expose absolute renderer paths or add repository mutation controls.

### `src/app/styles.css`

- Place the source-control workspace in the existing left/editor grid region without changing AgentDock or docking contracts.
- Do not duplicate module styling; `SourceControlWorkspace.tsx` already imports its owned local CSS.

### `src/Host/HermesDesktop/MainWindow.xaml.cs`

- Construct one `HermesGitService` using the trusted native workspace authority and dispose/cancel bridge-owned requests with the window.
- Validate trusted origin, 2 MiB frame limit, protocol version, request-ID regex/length, repository/path bounds, and closed message types before dispatch.
- Dispatch exactly `sourceControl.describe`, `sourceControl.repository.resolve`, `sourceControl.status`, `sourceControl.cancel`, and `sourceControl.gitExtensions.open` (`browse` only).
- Maintain `requestId -> CancellationTokenSource` only for exact owned in-flight operations; remove/dispose it on completion and let cancel target only that entry.
- Serialize explicit protocol envelopes in camelCase. Map enum availability to architecture `available`/safe version fields or configure string-enum serialization consistently; never serialize native root/executable/command/environment/raw-stderr fields.
- Advertise `sourceControl: true` and `sourceControlVersion: 1` in readiness/pong capabilities.

### `src/Host/HermesDesktop/HermesDesktop.csproj`

- Add a `ProjectReference` to `..\HermesGitServices\HermesGitServices.csproj`.
- Include the coordinator-owned bridge source if a separate `SourceControlBridge.cs` is chosen.
- Do not add Git/GitExtensions packages, binaries, content items, or installer payloads.

## Explicit confirmations

- No Git or GitExtensions binary was downloaded, installed, launched, bundled, modified, or copied by this lane.
- No real Git or GitExtensions process was started by implementation or verification.
- No repository remotes, hooks, credential helpers, credentials, secrets, user Git configuration, or global/system Git configuration were inspected.
- No `safe.directory` setting was read or changed, and unsafe ownership is never bypassed.
- No desktop host, installer, portable archive, launcher, package manifest, lockfile, shared app file, or third activity icon was modified.
