# Handoff: Compiler and Debugger Services Lane

You are the compiler/debugger infrastructure lane for Hermes Workbench. Work autonomously and keep going through implementation and verification. The shared project root is:

`C:\Users\clsor\Documents\Codex\HermesAgent`

## Outcome

Build an isolated, production-quality nucleus that lets Hermes Workbench eventually offer Visual Studio-style C# build diagnostics and a graphical debugger without embedding or copying Visual Studio or VS Code.

Use the open/tool-neutral path:

- `dotnet build` / MSBuild for authoritative project builds.
- Roslyn concepts and diagnostic shapes for C# source intelligence.
- Microsoft Debug Adapter Protocol (DAP) for the debugger boundary.
- Evaluate the MIT-licensed Samsung NetCoreDbg adapter for later .NET debugging.

Do not bundle, download, automate, or depend on `vsdbg`, Visual Studio, VS Code, C# Dev Kit, or proprietary IDE binaries. Record them only as out-of-scope/license-review alternatives.

## Exclusive ownership

You may create and edit only:

- `src\Host\HermesDeveloperServices\**`
- `src\Host\HermesDeveloperServices.Smoke\**`
- `docs\coordination\COMPILER-DEBUGGER-LANE-REPORT.md`

You may inspect the rest of the repository read-only. Do not edit the current desktop host, React app, Monaco module, package files, installer scripts, Docker files, data, artifacts, or any other coordination document. If integration needs an existing file changed, describe the exact change in your report for Super to apply later.

## Required implementation

1. Create a dependency-light `net10.0` C# library named `HermesDeveloperServices`.
2. Define immutable, JSON-friendly contracts for:
   - build request/result;
   - diagnostic file, start/end line and column, severity, code, message, project, and source;
   - bounded build output and cancellation state;
   - DAP request, response, event, source, breakpoint, stack frame, scope, and variable essentials.
3. Implement a safe `DotnetBuildRunner`:
   - accept only an existing `.sln`, `.slnx`, or `.csproj` under an explicitly supplied workspace root;
   - resolve paths and reject traversal, reparse-point escape, missing targets, and unrelated targets;
   - launch `dotnet` directly with `UseShellExecute=false`, never through PowerShell/cmd;
   - use fixed safe arguments such as `build`, `--nologo`, `--tl:off`, and bounded verbosity; do not accept arbitrary command-line fragments;
   - capture stdout/stderr concurrently, support cancellation, stop only the owned process, bound retained output, and return sanitized failures.
4. Implement an MSBuild diagnostic parser for canonical diagnostics such as `file(line,column[,endLine,endColumn]): error|warning|info CODE: message [project]`. Preserve Windows drive letters and Unicode paths. Ignore unrelated progress output.
5. Implement a standalone DAP framing codec for `Content-Length` messages and a small session state machine that can be tested against a fake in-memory adapter. Cover initialize, launch/attach capability negotiation, setBreakpoints, configurationDone, stopped/continued, threads, stackTrace, scopes, variables, evaluate, continue/step, disconnect, cancellation, malformed frames, and adapter exit.
6. Do not launch a real debugger or download NetCoreDbg in this lane. Produce an integration-ready recommendation for locating or installing a pinned, hash-verified NetCoreDbg build later, including license/NOTICE handling and a no-debugger-available state.
7. Create `HermesDeveloperServices.Smoke`, with no test framework dependency required, that exits nonzero on failure and exercises:
   - safe/unsafe target resolution;
   - representative MSBuild diagnostic parsing including Windows paths and ranges;
   - bounded output and cancellation behavior using only a disposable fixture owned by the smoke test;
   - fragmented and multiple DAP frames;
   - the fake DAP lifecycle without a real target process.

## Safety and quality gates

- Never read or emit secrets, environment files, Credential Manager values, or user source contents beyond deliberately created temporary fixtures.
- Never kill a process that was not started and tracked by the lane.
- Do not perform a real attach, launch an arbitrary workspace program, or open a network listener.
- Do not add deterministic "AI interpretation" rules; parsing compiler/DAP protocols is allowed and must be explicitly documented as protocol handling.
- Keep public contracts versioned and ready for a later native-host bridge and Monaco marker/Problems-panel adapter.
- Add XML documentation where the trust boundary is not obvious.

## Verification and report

Run Release builds and the smoke executable. In `COMPILER-DEBUGGER-LANE-REPORT.md`, record:

- exact files created;
- architecture and protocol version;
- commands run and pass/fail totals;
- security/path/process boundaries;
- official sources used;
- NetCoreDbg licensing/distribution conclusion;
- exact existing files Super must later touch to connect this to Monaco, Problems, Run/Debug, and the desktop host;
- all limitations and anything not live-verified.

Do not commit, merge, publish, package, or modify another lane. Finish by telling the user that Super can find the work in the two owned source folders and the report path.
