# GitExtensions-grade source control integration plan

Audit date: 2026-08-09  
Canonical upstream: [`gitextensions/gitextensions`](https://github.com/gitextensions/gitextensions) under the [`gitextensions` organization](https://github.com/gitextensions)  
Local target: Hermes Workbench, `C:\Users\clsor\Documents\Codex\HermesAgent`

## Decision

Make the third activity-rail icon open a Hermes-owned, React source-control workspace that talks to a narrow, versioned C# host service running the installed Git CLI. Add **Open in Git Extensions** as an optional external-process handoff for the full upstream UI.

Do **not** embed GitExtensions assemblies, forms, plugins, or a reparented GitExtensions window in Hermes. Do not copy its UI, icons, source, or assets.

This delivers the requested GitExtensions-grade workflow while preserving Hermes' React/WebView2 architecture, allowing Git and GitExtensions to update independently, and keeping GPL-covered code outside the Hermes process. The first slice is useful on its own: the icon becomes functional, status and staged/unstaged/untracked/conflict groups are real, and a user-installed GitExtensions can open the same repository.

This is an engineering and license-compliance recommendation, not legal advice. Any plan to ship, link, modify, or embed GPL-covered binaries should receive counsel review before distribution.

## Source-confirmed starting point

The local Workbench currently has the right extension seams:

- `src/app/App.tsx` defines `LeftPanel`, renders the activity rail, and already renders a third `GitBranch` button labeled `Source control`. The button has no active state or click handler. The left panel union currently contains only `explorer | sessions | run | usage | system`.
- `src/Host/HermesDesktop/MainWindow.xaml` contains one WPF WebView2 surface. There is no WinForms host or native docking surface.
- `src/Host/HermesDesktop/MainWindow.xaml.cs` exposes a trusted-loopback WebView message bridge. It validates message origin, enforces a 2 MiB input ceiling, advertises versioned capabilities, and dispatches feature-specific bridges.
- `src/Modules/DeveloperServices/DesktopDeveloperServicesClient.ts` is the best renderer-side pattern: a protocol constant, bounded normalization, request IDs, pending-request correlation, typed frames, and bridge lifecycle cleanup.
- `src/Host/HermesDesktop/DeveloperServicesBridge.cs` is the best host-side pattern: explicit protocol validation, bounded request IDs, cancellation, typed results, and sanitized error codes.
- `DesktopOptions.WorkspacePath` is the native authority root passed to terminal, Codex, and developer services. Source control must use the same root rather than accept arbitrary absolute paths from React.
- `remote-install/Build-Install-Zip.ps1` packages source plus the self-contained desktop host, generates a SHA-256/length manifest, and excludes runtime data, secrets, build output, and environment files. `remote-install/THIRD-PARTY-NOTICES.md` is the existing notice surface.

The existing `WorkbenchDocking/DockGroup` is for the Hermes/Codex agent area. Source control should initially be a left workspace panel selected by the activity rail, following Explorer, Run, Usage, and System. Its internal views can use tabs and splitters, but it should not be registered as an agent dock panel.

## Why actual UI embedding is the wrong seam

GitExtensions identifies itself as a **standalone Windows UI** and currently targets Windows 10+ with the .NET Desktop runtime; the upstream README also documents its own portable-update rules and user settings files ([official README](https://github.com/gitextensions/gitextensions#readme)). The v7.2 source enters through Windows Forms `Application.Run`, initializes global settings, theming, telemetry, plugin registration, and process-wide services ([Program.cs](https://github.com/gitextensions/gitextensions/blob/v7.2.0/src/app/GitExtensions/Program.cs)). Its main UI APIs construct top-level/modeless WinForms forms, not embeddable React or WPF controls ([GitUICommands.cs](https://github.com/gitextensions/gitextensions/blob/v7.2.0/src/app/GitUI/GitUICommands.cs)).

| Option | Technical result | Update risk | License/distribution result | Recommendation |
| --- | --- | --- | --- | --- |
| Reference GitExtensions assemblies and host its forms | Mixes WPF/WebView2, WinForms message loops, global static settings, plugins, modals, DPI/focus/accessibility, and process-wide lifecycle | Very high; internal API and assembly changes couple releases | Likely one combined/derived program; conservative reading requires the whole combined work to be GPLv3-compatible | Reject |
| Reparent the GitExtensions top-level HWND into Hermes | Focus, keyboard accelerators, owned dialogs, menus, drag/drop, taskbar, accessibility, DPI, crash recovery, and updates remain unreliable; unsupported HWND manipulation is not an API | Extreme | Still distributes/integrates the GPL app and creates ambiguous combination questions | Reject |
| Write a GitExtensions plugin that calls Hermes | Plugins load into GitExtensions, which is the reverse ownership direction; it does not provide the third-icon React surface | High; v7 release notes explicitly say external plugins must be rebuilt after extensibility API changes | Plugin is tightly linked to GPLv3-covered interfaces and needs separate compliance review | Reject for the core integration |
| Launch installed GitExtensions as a separate process | Uses its supported command entry points and native dialogs; failures are isolated | Low; probe capability by installed version and keep launch mapping small | Separate process using a public command surface; no GitExtensions code is copied into Hermes | **Use as optional escape hatch** |
| Hermes React UI over installed `git.exe` | Native Workbench UX, typed contracts, bounded output, complete control of confirmation and security policy | Low when restricted to Git's documented porcelain/plumbing output | Executing a user-installed GPLv2 Git process does not copy/link Git into Hermes | **Primary architecture** |

Trying to visually embed the upstream program would also defeat portable independence: GitExtensions v7.2 currently requires a .NET 10 Desktop runtime and recommends Git 2.53+, while HermesDesktop is self-contained. The upstream release can move independently ([v7.2.0 release](https://github.com/gitextensions/gitextensions/releases/tag/v7.2.0)).

## Supported upstream seams

### GitExtensions command surface

The official manual says most features can be started from the command line and recommends `gitex.cmd` ([command-line manual](https://git-extensions-documentation.readthedocs.io/en/main/command_line.html)). For Hermes, locate and launch `GitExtensions.exe` directly; do not invoke a `.cmd` through a shell.

The v7.2 command dispatcher confirms these UI entry points: `add`/`addfiles`, `applypatch`, `blame`, `branch`, `browse`, `checkoutbranch`, `checkoutrevision`, `cherry`, `cleanup`, `clone`, `commit`, `difftool`, `filehistory`, `formatpatch`, `gitignore`, `init`, `merge`, `mergeconflicts`/`mergetool`, `pull`, `push`, `rebase`, `remotes`, `reset`, `settings`, `stash`, `synchronize`, `tag`, `viewdiff`, and `viewpatch` ([v7.2 command dispatcher](https://github.com/gitextensions/gitextensions/blob/v7.2.0/src/app/GitUI/GitUICommands.cs#L1399-L1564)). The same source shows `browse` accepts revision and path filters and accepts a repository path/working directory at process startup.

Only expose a deliberately small launch allowlist in protocol v1:

| Hermes action | Executable arguments | Notes |
| --- | --- | --- |
| Open repository | `browse <absolute-repository-root>` | Default external handoff |
| Open commit dialog | `commit <absolute-repository-root>` | GitExtensions owns its own confirmation, hooks, and credentials |
| Open branches | `branch <absolute-repository-root>` | Opens its branch UI |
| Open conflicts | `mergeconflicts <absolute-repository-root>` | Only enable when status reports unmerged entries |
| Open remotes | `remotes <absolute-repository-root>` | GitExtensions owns remote settings and credential flow |
| Open stash | `stash <absolute-repository-root>` | GitExtensions owns stash mutations |
| File history | `filehistory <absolute-file-path>` | File must be inside the validated worktree |
| Blame | `blame <absolute-file-path> [line]` | Line is a positive bounded integer |

Never accept an arbitrary GitExtensions command string from React. The host maps a fixed enum to fixed arguments and supplies each argument through `ProcessStartInfo.ArgumentList`, with `UseShellExecute = false` and the validated repository root as `WorkingDirectory`.

### Plugins

The official manual confirms that GitExtensions loads external plugins and exposes plugin forms/settings ([plugin manual](https://git-extensions-documentation.readthedocs.io/en/main/plugins.html)). That is an extension seam *inside GitExtensions*, not a supported way to embed its UI in another application. It is also intentionally version-sensitive: v7 release notes state that the extensibility API changed and external plugins must be rebuilt ([official releases](https://github.com/gitextensions/gitextensions/releases)). Do not make a GitExtensions plugin a dependency of the Hermes panel.

A future optional plugin could add “Send selected repository/commit to Hermes,” but it must be a separately built, separately licensed add-on with a version matrix. It is not required for any phase in this plan.

### IPC

This audit found no public, stable GitExtensions IPC/RPC automation contract in the official manual or v7.2 command path. Treat process start plus documented arguments as the only supported inter-process seam. Do not use window messages, UI Automation, registry scraping of runtime state, named-pipe guessing, remote-control plugins, or private `GitUICommands` calls.

### Git CLI

GitExtensions itself uses command-line Git to access repositories ([official settings manual](https://git-extensions-documentation.readthedocs.io/en/main/settings.html)). Hermes should do the same through its own restricted service. Use documented machine formats rather than parsing localized human output:

- Status: `git --no-pager --no-optional-locks -c core.fsmonitor=false -C <root> status --porcelain=v2 --branch --show-stash -z --untracked-files=all`. Porcelain v2 supplies branch/upstream/ahead-behind/stash and typed ordinary/rename/unmerged records; `-z` makes paths unquoted and NUL-delimited ([git-status](https://git-scm.com/docs/git-status)).
- Refs: `git --no-pager --no-optional-locks -C <root> for-each-ref --format=<NUL-delimited fields> refs/heads refs/remotes refs/tags`. The format language supports `%00` NUL separators ([git-for-each-ref](https://git-scm.com/docs/git-for-each-ref)).
- History/graph: `git --no-pager --no-optional-locks -C <root> log --no-show-signature --date=iso-strict --format=<bounded machine record> --max-count=<n> [-- <path>]`; return full object ID and parent IDs so React draws lanes instead of parsing `--graph` ASCII. Git documents custom formats and strict ISO dates ([git-log](https://git-scm.com/docs/git-log)).
- Diffs: `git --no-pager --no-optional-locks -C <root> diff --no-ext-diff --no-textconv --no-color [--cached] -- <path>`. Git documents that `--no-ext-diff` disallows external drivers and `--no-textconv` disallows external converters ([git-diff](https://git-scm.com/docs/git-diff)).
- Blame: `git --no-pager --no-optional-locks -C <root> blame --line-porcelain <revision> -- <path>`; line porcelain is explicitly designed for machine consumption ([git-blame](https://git-scm.com/docs/git-blame)).
- Stashes: use `git log` with a fixed NUL-delimited format over `refs/stash`; do not parse localized `git stash list` decoration.
- Conflicts: derive from porcelain-v2 unmerged `u` records; load stage 1/2/3 blobs only when the user opens a conflict.
- Submodules: inventory gitlinks and `.gitmodules` through fixed read-only commands. Do not run `submodule foreach` or recursively execute repository-provided commands.
- Staging: pass selected workspace-relative paths on stdin to `git add --pathspec-from-file=- --pathspec-file-nul`; Git documents that this keeps newlines and quotes literal ([git-add](https://git-scm.com/docs/git-add)). Use an equivalent NUL pathspec flow for unstage where the installed Git version supports it.
- Always insert `--` before path arguments; Git explicitly documents revision/path disambiguation ([gitcli](https://git-scm.com/docs/gitcli)).

## Recommended architecture

```text
React SourceControlWorkspace
  -> DesktopSourceControlClient (protocol v1, bounded typed frames)
    -> trusted WebView2 postMessage
      -> MainWindow dispatcher
        -> SourceControlBridge (session/repository handles, policy, cancellation)
          -> HermesGitServices
             |- RepositoryResolver
             |- GitCommandRunner -> installed absolute git.exe
             |- porcelain parsers / DTOs
             |- mutation policy / confirmation tokens
             `- GitExtensionsLauncher -> installed absolute GitExtensions.exe
```

Key properties:

1. Hermes owns every renderer contract and all UI state.
2. React never receives a process path, credential, environment block, `.git` absolute path, or executable command string.
3. The host resolves an opaque `repositoryId` to a canonical root under `DesktopOptions.WorkspacePath`.
4. Every Git operation is a named allowlisted method. There is no generic `run git` endpoint.
5. Reads may run concurrently within conservative limits; one writer is allowed per repository. A writer invalidates all cached reads after exit.
6. Status refresh is latest-value/no-backlog: one in flight, coalesce filesystem hints, cancel/discard stale results, then refresh once. File watchers are hints, never authority.
7. GitExtensions is an optional separate process. Its version and location are discovered natively and returned only as sanitized availability/version metadata.

## Security boundaries

### Repository authority and paths

- Start with `workspaceRelativePath: "."`; do not recursively scan a user's drive.
- Canonicalize the workspace root and candidate path with platform APIs before Git execution. The candidate must exist and remain under the workspace root with a separator-boundary, case-insensitive comparison.
- Reject reparse-point escapes. Walk each path segment from the workspace root and reject a junction/symlink that resolves outside the root.
- Resolve repository metadata using fixed `git rev-parse` calls, then validate the reported top-level. Record the absolute git dir and common dir internally. A `.git` file is normal for worktrees/submodules, but a git/common dir outside the authority root requires an explicit native trust decision before any mutation.
- Mint a random opaque `repositoryId` per host session. All later renderer calls use it; never trust a repeated absolute path.
- Revalidate repository root, HEAD, index identity, and target relative paths immediately before a mutation to reduce time-of-check/time-of-use races.
- Validate object IDs as the repository's reported hash algorithm and full hex length. Validate new refs with `git check-ref-format`; do not implement ref grammar in React.
- Honor Git's ownership defense. Never set `safe.directory=*`, silently add a safe-directory exception, or bypass “dubious ownership.” Git says unsafe ownership is refused before repository config or hooks are parsed and exceptions must come from protected configuration ([safe.directory](https://git-scm.com/docs/git-config#Documentation/git-config.txt-safedirectory)). Surface a safe error and let the user make an explicit trust decision outside this bridge.

### Process and shell boundary

- Resolve one absolute `git.exe` at startup from a bounded native discovery policy. Do not execute `git.cmd`, a repository-local `git.exe`, an alias, a shell function, or a path supplied by React.
- `UseShellExecute = false`; use `ArgumentList`; never concatenate a command line, call `cmd /c`, PowerShell, Bash, `sh -c`, or `Process.Start` on a URL/command from repository data.
- Use a fixed working directory and a per-operation argument builder. Reject NUL in every input. Put filenames in NUL-delimited stdin pathspecs or after `--`.
- Remove environment variables that can redirect Git's repository/index/object/config/executable context (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_COMMON_DIR`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_ALTERNATE_OBJECT_DIRECTORIES`, `GIT_EXEC_PATH`, `GIT_CONFIG_COUNT`, and indexed `GIT_CONFIG_KEY_*`/`GIT_CONFIG_VALUE_*`). Set `GIT_PAGER=cat`, `PAGER=cat`, `GIT_TERMINAL_PROMPT=0` for noninteractive reads, `GIT_OPTIONAL_LOCKS=0`, and `NO_COLOR=1`.
- Apply hard ceilings: 10 seconds/status, 20 seconds/diff or blame, 30 seconds/history, configurable longer interactive mutation/remote timeouts; 2 MiB per message, 8 MiB captured stdout, 1 MiB stderr, 10,000 status entries, 2,000 commits, 20,000 blame lines. Return truncation metadata.
- Cancellation kills only the exact owned process tree. Never kill by process name. Dispose handles and delete only validated bridge-owned temporary files.

### Hooks, filters, and repository-configured executables

Repository content can cause code execution even when the UI itself does not expose a shell:

- `commit`, `merge`, `rebase`, `checkout`, `push`, and related operations can invoke Git hooks. Git documents that it changes into the worktree or Git directory before invoking hooks ([githooks](https://git-scm.com/docs/githooks)).
- Diff attributes can invoke external diff/textconv programs; therefore every Hermes-rendered diff must use `--no-ext-diff --no-textconv`.
- `core.fsmonitor` can name a hook/command; therefore local status uses `-c core.fsmonitor=false`.
- Clean/smudge/process filters may execute during add/checkout. Before the first stage or checkout in a repository, inspect only the *names* of active filter drivers through safe Git config output and warn that staging/checking out may run configured software. Do not return the command text to React or logs.
- Never silently pass `--no-verify`. That changes repository policy and still does not eliminate every hook. Instead, the prepare result reports `hooksPresent: true` and the confirmation UI explains that repository code may run.

The first slice is read-only and disables fsmonitor/external diff/textconv, so it does not need to run repository hooks or filters.

### Credentials and remotes

- Hermes must not read Git credential files, Windows Credential Manager entries, SSH private keys, askpass responses, or authentication tokens. This audit did not inspect any credential or secret-bearing repository configuration.
- Do not send credentials through the renderer. Git's documented credential subsystem delegates to configured helpers, and Git Credential Manager is included with Git for Windows ([gitcredentials](https://git-scm.com/docs/gitcredentials), [credential helpers](https://git-scm.com/doc/credential-helpers)).
- Protocol v1 remote operations should be unavailable. The initial UI can list sanitized remote names and URLs only; strip URI userinfo and redact suspicious query values before returning or logging.
- In a later remote phase, execute Git with the user's installed credential helper and an owned native interaction path. Do not capture prompts. If a helper needs a browser/native dialog, let it own that UI. A noninteractive operation that cannot authenticate fails with `authentication_required` and offers **Open remotes in Git Extensions**.
- Never place a remote URL, username, token, commit message, or file content in process arguments when stdin or a fixed config-safe mechanism is available.

### Mutations and destructive operations

All mutations use a two-step host policy. `sourceControl.mutation.prepare` validates current state and returns an effect summary plus either `ready` or a short-lived, single-use confirmation token. The token binds repository ID, operation, normalized arguments, HEAD, index checksum/stat identity, worktree status digest, and expiry. `sourceControl.mutation.confirm` revalidates that binding before execution.

| Class | Examples | Policy |
| --- | --- | --- |
| Read | status, diff, history, blame, refs, remotes, stash list, submodule inventory | No confirmation; bounded/cancellable |
| Reversible index | stage, unstage | Explicit click is sufficient; still goes through prepare, writer lock, path revalidation |
| Creates history | commit, tag, branch creation, stash create | Review summary; warn if hooks/filters/signing can run; no command-text preview |
| Worktree/ref-changing | checkout, merge, rebase, stash apply/pop, pull, conflict resolution, submodule update | Confirmation required with dirty/conflict/hook warning |
| Network | fetch, pull, push, remote add/edit/delete | Confirmation for destination and refs; sanitize URL; no credentials in renderer |
| Destructive | discard, clean, reset, branch/tag deletion, stash drop, force-with-lease | Typed high-friction confirmation; exact affected paths/refs; no plain `--force`; no `reset --hard` or `clean -fdx` in the first three phases |

Never expose generic refspecs until there is a validator. When force push is eventually added, permit only `--force-with-lease=<ref>:<expected-oid>` created by the host from a fresh read; never expose unconditional `--force`.

## Protocol v1

Use `SOURCE_CONTROL_PROTOCOL_VERSION = 1` in both C# and TypeScript. Every renderer request is:

```json
{
  "type": "sourceControl.status",
  "version": 1,
  "requestId": "status:m8xy:1",
  "repositoryId": "repo:8d5bb55b"
}
```

Request IDs use the existing 128-character `[A-Za-z0-9_:-]` limit. Repository IDs and confirmation tokens are host-minted opaque strings with the same character set. Unknown type/version/data is rejected without native action.

### Renderer-to-host messages

| Type | Required payload | Limit/meaning |
| --- | --- | --- |
| `sourceControl.describe` | envelope only | Git/GitExtensions availability and protocol capabilities |
| `sourceControl.repository.resolve` | `workspaceRelativePath` | v1 permits `.` and explicit paths already selected inside Workspace Explorer; max 1,024 chars |
| `sourceControl.status` | `repositoryId` | Full porcelain-v2 snapshot |
| `sourceControl.diff` | `repositoryId`, `path`, `comparison` | `comparison`: `worktree-to-index`, `index-to-head`, or two host-known object IDs |
| `sourceControl.refs` | `repositoryId` | Heads/remotes/tags; max 5,000 |
| `sourceControl.history` | `repositoryId`, optional `path`, `cursor`, `limit` | `limit` 1–200; opaque cursor |
| `sourceControl.blame` | `repositoryId`, `path`, optional `revision`, `startLine`, `lineCount` | max 2,000 requested lines |
| `sourceControl.stashes` | `repositoryId` | Read-only list |
| `sourceControl.remotes` | `repositoryId` | Sanitized metadata only |
| `sourceControl.submodules` | `repositoryId` | Read-only inventory |
| `sourceControl.mutation.prepare` | `repositoryId`, `operation`, typed `arguments` | Fixed operation union below |
| `sourceControl.mutation.confirm` | `confirmationToken` | Executes exactly one prepared high-risk operation |
| `sourceControl.cancel` | `targetRequestId` | Cancels only an owned in-flight operation |
| `sourceControl.gitExtensions.open` | `repositoryId`, `surface`, optional `path`/`line` | Fixed launch allowlist only |

The mutation operation union is versioned and closed:

```ts
type SourceControlMutation =
  | { operation: 'stage'; paths: string[] }
  | { operation: 'unstage'; paths: string[] }
  | { operation: 'commit'; message: string; amend: boolean; signoff: boolean }
  | { operation: 'branch.create'; name: string; startPointId: string; checkout: boolean }
  | { operation: 'branch.checkout'; refId: string }
  | { operation: 'stash.create'; message: string; includeUntracked: boolean }
  | { operation: 'stash.apply'; stashId: string; pop: boolean }
  | { operation: 'conflict.stageResolution'; path: string }
  | { operation: 'remote.fetch'; remoteId: string; prune: boolean }
  | { operation: 'remote.pull'; remoteId: string; branchId: string; mode: 'ff-only' | 'merge' | 'rebase' }
  | { operation: 'remote.push'; remoteId: string; sourceRefId: string; destinationRefId: string; leaseObjectId?: string }
  | { operation: 'submodule.update'; path: string; initialize: boolean; recursive: boolean }
```

Delete/reset/clean/discard/rebase-edit/remote-edit operations are intentionally absent through phase 3. Adding one requires a protocol version/capability addition, policy review, and dedicated tests.

### Host-to-renderer frames

| Type | Payload |
| --- | --- |
| `sourceControl.describe.result` | `git { available, version }`, `gitExtensions { available, version }`, `capabilities[]`, limits |
| `sourceControl.repository.resolve.result` | `repository { repositoryId, displayName, workspaceRelativeRoot, isBare, hashAlgorithm, head }`, trust flags |
| `sourceControl.status.result` | branch/upstream/ahead/behind/stash count, typed changes, snapshot token, truncation |
| `sourceControl.diff.result` | path/comparison, bounded unified hunks or binary summary, truncation |
| `sourceControl.refs.result` | typed refs with opaque `refId`, display name, object ID, upstream/ahead/behind |
| `sourceControl.history.result` | commits with object/parent IDs, author display, strict ISO time, subject, decorations, `nextCursor` |
| `sourceControl.blame.result` | bounded line records with object ID, author display, time, source path/line |
| `sourceControl.stashes.result` / `.remotes.result` / `.submodules.result` | typed bounded lists |
| `sourceControl.mutation.prepared` | `state`, exact human-safe effect model, state digest, warnings, optional confirmation token/expiry |
| `sourceControl.operation.started` / `.progress` / `.result` | operation ID, safe phase, final exit category and refreshed snapshot token; never raw command |
| `sourceControl.confirmation.required` | confirmation token, expiry, effect model, warnings |
| `sourceControl.gitExtensions.open.result` | `started`, sanitized version, safe failure code |
| `sourceControl.repository.changed` | repository ID and monotonically increasing generation; renderer refreshes |
| `sourceControl.error` | request ID, stable code, sanitized message, retryable |

Stable v1 error codes: `unsupported_version`, `invalid_request_id`, `invalid_repository`, `outside_workspace`, `reparse_point`, `unsafe_ownership`, `git_unavailable`, `git_too_old`, `git_extensions_unavailable`, `invalid_path`, `invalid_ref`, `state_changed`, `confirmation_expired`, `operation_busy`, `authentication_required`, `hook_confirmation_required`, `timeout`, `cancelled`, `output_limit`, and `git_failed`. Raw stderr is not a protocol error message; classify it natively and retain only bounded diagnostics with credential/URL/path redaction.

## Exact implementation file map

No files below are changed by this planning lane. They are the proposed implementation set.

### New host library

- `src/Host/HermesGitServices/HermesGitServices.csproj`
- `src/Host/HermesGitServices/SourceControlContracts.cs`
- `src/Host/HermesGitServices/GitExecutableLocator.cs`
- `src/Host/HermesGitServices/RepositoryResolver.cs`
- `src/Host/HermesGitServices/GitCommandRunner.cs`
- `src/Host/HermesGitServices/GitStatusParser.cs`
- `src/Host/HermesGitServices/GitRefParser.cs`
- `src/Host/HermesGitServices/GitHistoryParser.cs`
- `src/Host/HermesGitServices/GitDiffService.cs`
- `src/Host/HermesGitServices/GitMutationPolicy.cs`
- `src/Host/HermesGitServices/GitExtensionsLocator.cs`
- `src/Host/HermesGitServices/GitExtensionsLauncher.cs`
- `src/Host/HermesGitServices/Redaction.cs`

Keep Git execution and parsers in this library so they are independently testable and do not bloat `MainWindow.xaml.cs`.

### New desktop bridge

- `src/Host/HermesDesktop/SourceControlBridge.cs`

### Existing host files to update

- `src/Host/HermesDesktop/MainWindow.xaml.cs`: construct/dispose bridge; advertise `sourceControl: true` and `sourceControlVersion: 1`; dispatch only the message types above.
- `src/Host/HermesDesktop/HermesDesktop.csproj`: reference `HermesGitServices`.
- `src/Host/README.md`: document the boundary and smoke command.

### New React module

- `src/Modules/SourceControl/contracts.ts`
- `src/Modules/SourceControl/DesktopSourceControlClient.ts`
- `src/Modules/SourceControl/DesktopSourceControlClient.test.ts`
- `src/Modules/SourceControl/SourceControlWorkspace.tsx`
- `src/Modules/SourceControl/SourceControlWorkspace.css`
- `src/Modules/SourceControl/ChangesView.tsx`
- `src/Modules/SourceControl/DiffView.tsx`
- `src/Modules/SourceControl/CommitComposer.tsx`
- `src/Modules/SourceControl/BranchesView.tsx`
- `src/Modules/SourceControl/HistoryView.tsx`
- `src/Modules/SourceControl/HistoryGraph.tsx`
- `src/Modules/SourceControl/BlameView.tsx`
- `src/Modules/SourceControl/StashView.tsx`
- `src/Modules/SourceControl/RemotesView.tsx`
- `src/Modules/SourceControl/ConflictsView.tsx`
- `src/Modules/SourceControl/SubmodulesView.tsx`
- `src/Modules/SourceControl/SourceControlWorkspace.test.tsx`

Build these incrementally; the first slice needs only contracts, client, workspace, changes view, CSS, and tests.

### Existing renderer files to update

- `src/app/App.tsx`: add `'sourceControl'` to `LeftPanel`, make the third icon active/clickable, and render `SourceControlWorkspace` with the selected workspace path.
- `src/app/styles.css`: assign the source-control workspace to the existing left/editor grid without changing the agent dock contract.
- `src/Modules/Workspace/WorkspaceEditor.tsx`: later, accept diff/blame open requests through typed props; do not couple Monaco directly to the native bridge.

### Tests and packaging

- `src/Host/HermesGitServices.Smoke/HermesGitServices.Smoke.csproj`
- `src/Host/HermesGitServices.Smoke/Program.cs`
- `src/Host/HermesDesktop.Smoke/Program.cs` (extend existing renderer round-trip)
- `remote-install/Test-Hermes.ps1` (report Git and optional GitExtensions availability without installing or reading config/secrets)
- `remote-install/THIRD-PARTY-NOTICES.md` (only if a GPL binary is actually shipped; otherwise a non-license optional-integration note is sufficient)
- `remote-install/Build-Install-Zip.ps1` and `tests/Install-Hermes.Smoke.ps1` (only if packaging rules change)
- `README.md` (user-facing source-control and optional GitExtensions behavior)

## Phased UX scope

### Phase 1 — real status and external GitExtensions handoff

- Third icon opens Source Control and participates in active-panel state.
- Resolve the workspace root repository; show clear non-repository/Git-unavailable/browser-mode states.
- Branch/upstream/ahead/behind/stash badge.
- Group staged, unstaged, untracked, renamed, deleted, conflicted, and submodule changes from porcelain v2.
- Refresh button plus coalesced filesystem refresh.
- Read-only working/index diff with binary and truncation states.
- **Open repository in Git Extensions** and context actions for Commit, File history, Blame, Conflicts.
- No staging or mutation.

### Phase 2 — staging and commit

- Per-file and group stage/unstage with NUL pathspecs.
- Commit composer with staged summary, message validation, amend/sign-off toggles, and hook/filter disclosure.
- Post-operation refresh; state-changed recovery; busy/cancel UI.
- No credentials, remotes, reset, clean, or discard.

### Phase 3 — branches, history/graph, diffs, blame, stash

- Branch/tag/ref lists, checkout and branch creation.
- Paginated history with graph lanes from parent IDs, decorations, commit detail, file list, and compare.
- Unified and side-by-side diff presentation backed by bounded native output.
- Blame with file navigation.
- Stash list/create/apply/pop; drop remains external/high-risk until separately approved.

### Phase 4 — remotes and conflicts

- Sanitized remotes; fetch/pull/push with explicit target/ref summaries.
- Let installed credential helpers own authentication; never expose credentials to React.
- Three-way conflict view from stage blobs, choose ours/theirs/manual file edit, then explicit stage-resolution.
- Abort/continue merge/rebase remains “Open in Git Extensions” until dedicated hook/state-machine tests exist.

### Phase 5 — submodules and advanced parity

- Submodule inventory, dirty/uninitialized state, open submodule as a separate validated repository handle.
- Confirmation-gated init/update/sync with recursion and URL disclosure.
- Tags, cherry-pick, revert, interactive rebase, worktrees, reflog/recovery, patches, bisect, maintenance, and destructive reset/clean only as separate reviewed capabilities. GitExtensions remains the immediate escape hatch.

## Portable installer and compliance impact

### Recommended package policy

Do not bundle GitExtensions or Git in `Hermes-Remote-Install.zip` for protocol v1.

- Discover a user-installed `git.exe`; if absent, the third icon shows an unavailable state and an installation-help link. The installer may offer a separate, explicit prerequisite step only after package ID/version/hash policy is verified.
- Discover a user-installed `GitExtensions.exe` from a user-configured absolute path, a documented installer registration/App Path if present, PATH, and a short allowlist of standard installation directories. Do not recursively scan disks.
- GitExtensions remains optional. Missing GitExtensions disables only the external-launch actions; the native panel still works with Git.
- Do not persist machine-specific executable paths into the portable source bundle or checked-in `launcher.settings.json`. If an override is needed, store it in local runtime settings excluded from packaging.
- Add no GitExtensions files to the SHA-256 manifest, no updater responsibility, and no uninstall responsibility.

This keeps the existing portable bundle size, self-contained Hermes host, manifest semantics, and update flow unchanged apart from new Hermes source/host binaries naturally included by the existing build.

### GPL and notices

GitExtensions v7.2 is GPLv3 ([official license](https://github.com/gitextensions/gitextensions/blob/v7.2.0/LICENSE.md)). The license distinguishes running an unmodified program from conveying it; it also says that aggregating separate independent works on one distribution medium does not automatically apply GPL to the other works, while distributing object code still requires corresponding-source compliance and notices (GPLv3 sections 2, 5, and 6). Git itself is GPLv2-only unless a file says otherwise ([Git COPYING](https://github.com/git/git/blob/master/COPYING)).

Consequences:

1. **User-installed external processes:** Hermes may execute installed Git and GitExtensions without copying or linking their code. No GPL source offer is created by that execution alone.
2. **Optional integration name:** An upstream link and “Git Extensions is not bundled; install separately” statement are prudent. Do not imply endorsement and do not copy the logo/icons; the GitExtensions README separately credits icon licensing.
3. **If binaries are ever bundled unchanged:** treat them as separate aggregate components; ship the exact upstream licenses/copyright notices, exact version and hashes, and GPL-compliant corresponding source or durable source offer/equivalent access. A one-line `THIRD-PARTY-NOTICES.md` entry is not sufficient by itself. Preserve all upstream notices and no-warranty text. Add archive-manifest entries and source availability verification.
4. **If GitExtensions is modified:** publish the modified corresponding source, build/install scripts, prominent modification/date notices, and GPLv3 terms for the covered work. Do not ship until reproducibility and source access are tested.
5. **If assemblies/forms are linked or embedded:** assume the combined program must be GPLv3-compatible as a whole unless counsel concludes otherwise. Git is GPLv2-only, so combining GPLv2-only and GPLv3-covered code in one derived executable raises an additional compatibility problem. This plan avoids both.
6. **NOTICE ownership:** update `remote-install/THIRD-PARTY-NOTICES.md` only for content actually conveyed. If neither Git nor GitExtensions is shipped, document optional interoperability in README/help rather than falsely saying Hermes “includes” them.

## Test plan

### Pure parser/unit tests

- Porcelain-v2 ordinary, rename/copy, unmerged, untracked, ignored, submodule, branch, upstream, ahead/behind, stash, unborn, detached, and initial-repository records.
- Filenames containing spaces, leading dashes, tabs, newlines, quotes, backslashes, non-ASCII, emoji, reserved Windows-looking names, and very long relative paths. NUL is rejected before process start.
- NUL-delimited ref/history/config output; malicious subjects/authors/ref names cannot break framing or inject HTML.
- Diff binary/truncated/oversized/malformed cases; external diff/textconv flags always present.
- Redaction of HTTPS userinfo, token-like query values, UNC authority, home path, and stderr credential patterns.
- Protocol wrong version/type/request ID, oversized arrays/strings, unknown enum, duplicate request, stale cursor, and output ceiling.

### Repository resolver/security tests

Use only generated temporary repositories with synthetic identities and no remote credentials:

- Normal root, nested candidate, worktree `.git` file, submodule, bare repo rejection/readonly mode, SHA-1/SHA-256 when supported.
- Candidate outside workspace, `..`, rooted path, case variants, symlink/junction escape, missing path, changed path after prepare.
- Git/common dir outside authority root is readonly/blocked for mutation until native trust.
- Dubious ownership maps to `unsafe_ownership`; no safe-directory config is changed.
- Poisoned `GIT_DIR`, `GIT_INDEX_FILE`, `GIT_CONFIG_COUNT`, `GIT_EXEC_PATH`, pager/editor/askpass environment is removed.
- Repository aliases, hooks, fsmonitor, filters, diff drivers, and textconv never execute during first-slice reads. Use sentinel files to prove absence.

### Command runner tests

- Assert executable path and every argument separately; prove no shell is created.
- Status/diff/history timeouts and cancellation kill only the child tree.
- Concurrent reads bounded; one writer per repository; stale status discarded; writer triggers exactly one refresh generation.
- stdout/stderr/count ceilings set `truncated` and never allocate unbounded memory.
- Stage/unstage literal pathspec stdin round-trip for pathological filenames.
- Prepare token expires, is single-use, is request/repository/operation/state bound, and fails after HEAD/index/worktree changes.

### GitExtensions launcher tests

- Use a fake signed-path fixture executable in tests; never install or launch real GitExtensions in automated tests.
- Discovery precedence is deterministic and bounded; a repository-local executable is rejected.
- Each surface maps to the exact fixed command; repository/file/line are separate arguments.
- Unknown surface/path outside repository/GitExtensions missing/version probe failure returns a safe code.
- No command output, environment secret, user path, or arbitrary argument crosses to React/logs.

### Renderer tests

- Third icon active state, keyboard/ARIA label, browser-mode unavailable state, non-repo state, loading/error/retry.
- All change groups, badges, selection, diff loading, truncation, empty state, and external-action availability.
- Normalize/drop malformed host frames; cap lists/text; correlate and cancel requests; close listener cleanly.
- Mutation confirmation summaries show host-supplied structured effects, never raw shell commands.
- History virtualization and pagination; graph lane stability for merges/octopus commits; diff/blame navigation.

### Integration and portable acceptance

- Extend `HermesDesktop.Smoke` with a synthetic WebView message round-trip for describe/resolve/status and malformed-message rejection.
- Run smoke tests against a temporary synthetic repo only. Do not inspect this repository's credentials, remotes, hooks, or secret-bearing config.
- Frontend: focused Vitest, then full `npm test`, then `npm run build`.
- Host: new Git services smoke, existing desktop smoke, then Release publish.
- Rebuild the portable ZIP; verify manifest/hash, excluded-data rules, source inclusion, desktop byte identity, and existing install/update/shutdown smoke suites.
- Clean-VM acceptance with Git installed and GitExtensions absent; then GitExtensions installed separately. Verify missing optional app does not break source control, external launch opens the exact synthetic repo, and Hermes remains usable after GitExtensions exits/crashes.
- License acceptance: archive contains no Git/GitExtensions binary or asset in the recommended configuration. If that policy changes, fail the build unless full licenses, version/hash inventory, corresponding-source path/offer, and notice checks pass.

## First product slice

Deliver **Phase 1A: status + GitExtensions handoff** with no repository mutation.

### Scope

1. Add the minimal `HermesGitServices` files: contracts, executable locator, resolver, runner, status parser, GitExtensions locator/launcher, redaction.
2. Add `SourceControlBridge` methods for `describe`, `repository.resolve`, `status`, `cancel`, and `gitExtensions.open` with only the `browse` surface.
3. Advertise source-control protocol v1 in the host readiness/pong capability objects and dispatch those five message types.
4. Add `contracts.ts`, `DesktopSourceControlClient.ts`, `SourceControlWorkspace.tsx`, `ChangesView.tsx`, CSS, and focused tests.
5. Add `'sourceControl'` to `LeftPanel`; wire the third icon active/click behavior; render the module.
6. Show branch/upstream/ahead/behind/stash count and staged/unstaged/untracked/conflict/submodule groups from one porcelain-v2 snapshot.
7. Provide Refresh and **Open in Git Extensions**. In browser mode or without installed Git/GitExtensions, show precise nonfatal availability states.
8. Add generated-repository smoke tests and full frontend/host/build verification.

### Explicit non-scope

No stage, unstage, commit, discard, branch, stash mutation, remote access, credentials, hooks, filter execution, submodule update, Git/GitExtensions installation, binary bundling, plugin, UI embedding, generic Git command endpoint, recursive drive scan, or user/repository config inspection.

### Acceptance criteria

- Clicking the third activity icon always opens a real source-control panel and gives it active styling.
- A synthetic repo with staged, unstaged, untracked, renamed, deleted, conflicted, and submodule fixtures is rendered from exact Git state; pathological filenames remain distinct and safe.
- Status command is fixed, NUL-framed, bounded, cancellable, uses `--no-optional-locks` and disables fsmonitor, and invokes no hook/filter/diff helper sentinel.
- Candidate repository and all returned paths remain inside the explicit workspace authority; unsafe ownership is not bypassed.
- The renderer receives no absolute git-dir path, executable path, command line, environment value, credential, or unredacted error output.
- If `GitExtensions.exe` is unavailable, the panel remains fully usable and shows an optional-app message. If a fake launcher fixture is available, `browse` receives the exact validated repo root as a separate argument.
- Focused and full tests pass; frontend production build and desktop Release publish pass; portable manifest checks still pass.
- Only after this slice is green should Phase 1B add native read-only diff/history, followed by the separately reviewed staging/commit phase.

## First independently implementable lane

To avoid colliding with concurrent `App.tsx`, `MainWindow.xaml.cs`, docking, installer, or desktop-host work, the first worker should build the source-control core and renderer module behind a fake/test boundary **without wiring the third icon yet**. A short integration pass can then adopt the completed contracts by changing the shared files listed earlier.

### Exclusive ownership

The worker may create and edit only these new paths:

- `src/Host/HermesGitServices/**`
- `src/Host/HermesGitServices.Smoke/**`
- `src/Modules/SourceControl/**`
- `docs/coordination/GITEXTENSIONS-PHASE1A-LANE.md`

Nothing in those paths exists at audit time. The worker must stop and report a collision if any target appears with unrelated content before its first write.

### Forbidden collision boundaries

The worker must not edit, build into, regenerate, copy over, or reformat:

- `src/app/App.tsx`, `src/app/styles.css`, or `src/app/main.tsx`
- `src/Modules/WorkbenchDocking/**`, `src/Modules/Workspace/**`, or any existing module
- `src/Host/HermesDesktop/**`, including its project and smoke project
- `src/Host/HermesDeveloperServices/**`, `src/Host/HermesUsageCollectors/**`, or their smoke projects
- `remote-install/**`, `tests/**`, `README.md`, `.vscode/**`, launcher scripts, or existing docs
- `artifacts/**`, `data/**`, `logs/**`, `source/**`, `workspace/**`, generated `bin/**`, `obj/**`, `dist/**`, or package lockfiles

The lane may run the new smoke project and focused frontend tests. It may run full frontend test/build verification because those read the existing tree and emit only normal ignored build output, but it must not publish the desktop host or rebuild the portable ZIP.

### Required deliverable

The lane ends with `docs/coordination/GITEXTENSIONS-PHASE1A-LANE.md` containing:

- every created file;
- exact supported protocol/data contracts and Git commands;
- all test/build commands and results;
- any deviation from this plan;
- a shared-file integration checklist for `App.tsx`, `styles.css`, `MainWindow.xaml.cs`, and `HermesDesktop.csproj`;
- explicit confirmation that no GitExtensions/Git binary was downloaded, installed, launched, bundled, or modified and no credentials/user Git config were inspected.

### Copy-paste worker task

```text
Work in C:\Users\clsor\Documents\Codex\HermesAgent. Implement the isolated Phase 1A GitExtensions-grade source-control core described in docs\coordination\GITEXTENSIONS-INTEGRATION-PLAN.md.

OWNERSHIP — create/edit only:
  src\Host\HermesGitServices\**
  src\Host\HermesGitServices.Smoke\**
  src\Modules\SourceControl\**
  docs\coordination\GITEXTENSIONS-PHASE1A-LANE.md

Stop and report a collision if any owned target already contains unrelated work. Do not edit any existing file outside those paths. In particular do not touch App.tsx, styles.css, WorkbenchDocking, Workspace, HermesDesktop, any existing host project, remote-install, tests, README, launchers, package manifests/locks, artifacts, data, logs, source, or workspace.

Build:
1. A net10.0 HermesGitServices library with protocol-v1 DTOs, bounded GitExecutableLocator, RepositoryResolver, GitCommandRunner, GitStatusParser, GitExtensionsLocator, GitExtensionsLauncher, and redaction. The only production Git operation exposed by this lane is read-only status:
   git --no-pager --no-optional-locks -c core.fsmonitor=false -C <validated-root> status --porcelain=v2 --branch --show-stash -z --untracked-files=all
2. The runner must use an absolute git.exe, UseShellExecute=false, ProcessStartInfo.ArgumentList, no shell, sanitized Git redirection/config environment, bounded stdout/stderr/time/count, cancellation of only the owned process tree, opaque repository IDs, and no safe.directory bypass.
3. GitExtensions support is discovery plus a fixed browse launch mapping only. Production code maps the enum to GitExtensions.exe arguments ["browse", validatedRoot]; it never accepts a raw command. Automated tests must use a fake launcher executable/abstraction and must not launch, download, install, or modify real GitExtensions or Git.
4. A React SourceControl module with contracts.ts, DesktopSourceControlClient.ts, SourceControlWorkspace.tsx, ChangesView.tsx, CSS, and focused tests. It must render fake/normalized snapshots for branch/upstream/ahead/behind/stash plus staged, unstaged, untracked, conflicted, renamed, deleted, and submodule groups. It is intentionally not imported by App.tsx in this lane.
5. A HermesGitServices.Smoke console that uses only generated temporary synthetic repositories and identities. Fixture setup may invoke fixed Git init/config/add/commit/branch commands only inside its validated temporary directory; none of those mutations may be reachable through the production service. Do not inspect this repo's remotes, hooks, credential helpers, secret-bearing config, or user data.

Acceptance tests:
- Unit fixtures cover porcelain-v2 ordinary/rename/unmerged/untracked/submodule/unborn/detached records and filenames with spaces, leading dash, tab, newline, quotes, backslashes, Unicode, emoji, and long paths.
- Resolver rejects outside-root, .., rooted renderer input, missing paths, and symlink/junction escape; unsafe ownership is surfaced and never auto-trusted.
- Poisoned GIT_DIR, GIT_WORK_TREE, GIT_INDEX_FILE, GIT_CONFIG_COUNT/KEY/VALUE, GIT_EXEC_PATH, pager/editor/askpass inputs do not redirect the child.
- Hook, fsmonitor, filter, diff-driver, and textconv sentinels are not executed by status.
- Output/time/count ceilings and cancellation are verified; cancellation kills only the owned fixture process.
- GitExtensions mapping rejects unknown surfaces and paths outside the validated repo; tests use only the fake launcher boundary.
- React drops malformed/wrong-version/oversized frames, correlates request IDs, removes listeners, renders all groups, and shows nonfatal browser/Git/GitExtensions-unavailable states.
- Run the new C# smoke in Release, focused SourceControl Vitest tests, full npm test, and npm run build. Do not publish HermesDesktop or build the portable ZIP.

Write docs\coordination\GITEXTENSIONS-PHASE1A-LANE.md with created files, exact results, limitations, and the four-shared-file integration checklist. End after that handoff; do not wire the third activity icon.
```

## Bottom line

“GitExtensions-grade” should describe the workflow quality and coverage, not an in-process dependency. A Hermes-native panel over Git's stable machine interfaces gives the third icon a first-class IDE experience; the official GitExtensions command surface supplies immediate access to its mature dialogs when installed. Keeping both upstream tools out of the Hermes process and portable archive is the most update-safe, secure, and license-conservative design.
