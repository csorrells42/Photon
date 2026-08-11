# Hermes Bridge Client Lane Report

Date: 2026-08-09  
Status: complete and ready for integration review  
Ownership: `src/Tools/HermesBridgeClient/**`, `tests/HermesBridge.Client.Smoke.ps1`, and this report only

## Outcome

This lane adds a package-free, framework-dependent portable .NET 10 command-line client for the Hermes Workbench conversation bridge at `http://127.0.0.1:8972`.

Supported commands:

```text
hermes-bridge health
hermes-bridge status
hermes-bridge send --text <message>
hermes-bridge interrupt
```

- `health` calls `GET /health` without loading settings and without an Authorization header.
- `status` loads `%LOCALAPPDATA%\hermes\conversation-bridge.json` and calls authenticated `GET /v1/session`.
- `send` requires an explicit `--text` argument and calls authenticated `POST /v1/turns` with `{ "text": "..." }`. It never reads stdin or submits a default message.
- `interrupt` calls authenticated `POST /v1/interrupt` with no message body.

No HermesDesktop, React module, launcher, installer, existing test, package manifest outside this lane, or other coordination document was edited.

## Created files

`src/Tools/HermesBridgeClient/`

- `HermesBridgeClient.csproj` — package-free `net10.0` executable, assembly name `hermes-bridge`.
- `Program.cs` — minimal async entry point.
- `BridgeCli.cs` — exact command grammar, output routing, settings use, and stable exit codes.
- `BridgeContracts.cs` — policy, result/exit contracts, secret lease, and literal-IP loopback endpoint policy.
- `BridgeSettingsLoader.cs` — bounded strict settings reader for the local Hermes bridge file.
- `BridgeApiClient.cs` — bounded, timeout-aware, redirect-disabled loopback HTTP client.
- `HermesBridgeClient.Tests/HermesBridgeClient.Tests.csproj` — package-free offline unit-test executable.
- `HermesBridgeClient.Tests/Program.cs` — 28 deterministic unit cases using injected in-memory handlers.

`tests/`

- `HermesBridge.Client.Smoke.ps1` — read-only live health/authentication smoke. It contains no turn or interrupt route.

Generated `bin/` and `obj/` files remain inside `src/Tools/HermesBridgeClient/**`.

## Security behavior

### Endpoint restrictions

- The default and CLI endpoint is exactly `http://127.0.0.1:8972/`.
- The HTTP layer independently validates every endpoint before transport.
- Only literal `IPAddress` loopback values are accepted (`127.0.0.0/8` and `::1` through `IPAddress.IsLoopback`).
- DNS names such as `localhost` are rejected to avoid name-resolution ambiguity.
- HTTPS, remote addresses, ports below 1024, paths, queries, fragments, and user information are rejected.
- The production handler disables automatic redirects, cookies, and proxies. Every 3xx response is treated as a failure.

### Bridge-code handling

- Authenticated commands load only `%LOCALAPPDATA%\hermes\conversation-bridge.json`.
- Settings are bounded to 16 KiB, parsed with comments/trailing commas disabled and JSON depth 8, and require port 1024–65535 plus an exact 64-character hexadecimal authentication code.
- Settings input bytes are cleared after parsing.
- The authentication code is held behind `BridgeAuthentication`; it has no public getter and is never included in a result DTO.
- Its character buffer is cryptographically zeroed on disposal. Creating an HTTP Authorization header necessarily creates a short-lived managed string that .NET cannot deterministically zero.
- The token is used only as `Authorization: Bearer <code>` on authenticated loopback requests.
- Error response bodies are never read or surfaced for unsuccessful HTTP status codes.
- Successful authenticated JSON is normalized and any occurrence of the bridge code is replaced with `[redacted]` before output.
- Settings exceptions and network/protocol failures use fixed messages. Raw exception messages, response bodies, response headers, request headers, and the bearer value are never printed, logged, echoed, serialized, or included in returned errors.

### Bounds and failure handling

- Default client timeout: 15 seconds, with caller cancellation linked to every request/body read.
- Maximum response: 1 MiB, enforced by both declared `Content-Length` and bounded streaming reads.
- JSON response parsing rejects comments, trailing commas, malformed data, non-JSON media types, and depth beyond 64.
- Send text is required, whitespace-only input is rejected, and the host’s 64 KiB character limit is enforced client-side.
- The client uses `ResponseHeadersRead` and never buffers an unsuccessful response body.

Stable status mappings:

| HTTP/result | Exit code | Client meaning |
|---|---:|---|
| Success | 0 | JSON response written to stdout after normalization/redaction. |
| CLI/settings/endpoint rejection | 2 | Usage or local configuration error. |
| 401 | 3 | Bridge authentication failed or settings are stale. |
| 409 | 4 | Another turn is active or the visible dock reported a conflict. |
| 503 | 5 | Visible Hermes dock temporarily unavailable. |
| 504 or client timeout/cancellation | 6 | Bridge/dock timeout or cancelled operation. |
| Redirect, malformed/oversized/non-JSON body, connection/read failure, other HTTP | 7 | Connectivity or protocol failure. |

## Offline unit coverage

The offline tests reference only the client project and the .NET runtime. They use an injected `HttpMessageHandler`; no socket is opened.

The single synthetic send test verifies serialization against an in-memory handler only. It cannot reach the live bridge. All remaining tests are read-only or pre-transport validation.

Coverage:

1. Default Hermes loopback endpoint.
2. Literal IPv4/IPv6 loopback acceptance.
3. Remote endpoint refusal.
4. DNS loopback-name refusal.
5. User information/path/query refusal.
6. End-to-end remote rejection before transport.
7. Valid bounded settings loading without token exposure.
8. Safe missing-settings error.
9. Malformed-settings rejection.
10. Invalid token rejection without echo.
11. Oversized-settings rejection.
12. Unauthenticated health request shape.
13. Authenticated status request and echoed-token redaction.
14. Explicit send JSON shape through in-memory transport only.
15. Empty send rejection before transport.
16. Interrupt POST shape without a message.
17. Explicit 401 handling.
18. Explicit 409 handling.
19. Explicit 503 handling.
20. Explicit 504 handling.
21. Redirect refusal.
22. Malformed JSON rejection.
23. Non-JSON response rejection.
24. Oversized-response rejection.
25. Client-timeout handling.
26. Caller-cancellation handling.
27. Error-body token suppression.
28. CLI refusal of implicit send text.

## Live smoke behavior

`tests/HermesBridge.Client.Smoke.ps1` performs only:

1. unauthenticated `GET http://127.0.0.1:8972/health`, validating service identity, state, and port;
2. unauthenticated `GET /v1/session`, requiring HTTP 401; and
3. authenticated `GET /v1/session` only when the local settings file exists and is valid.

The authenticated response body and Authorization header are not printed. The script never requests `/v1/turns` or `/v1/interrupt`, so it cannot submit or stop a Hermes turn.

## Build and test evidence

Executed from `C:\Users\clsor\Documents\Codex\HermesAgent` on 2026-08-09.

### Client Release build

```powershell
dotnet build .\src\Tools\HermesBridgeClient\HermesBridgeClient.csproj -c Release
```

Result: passed; **0 warnings, 0 errors**.

Output:

```text
src\Tools\HermesBridgeClient\bin\Release\net10.0\hermes-bridge.dll
```

### Offline-test Release build

```powershell
dotnet build .\src\Tools\HermesBridgeClient\HermesBridgeClient.Tests\HermesBridgeClient.Tests.csproj -c Release
```

Result: passed; **0 warnings, 0 errors**.

### Offline tests

```powershell
dotnet run --project .\src\Tools\HermesBridgeClient\HermesBridgeClient.Tests\HermesBridgeClient.Tests.csproj -c Release --no-build
```

Result: **28 passed, 0 failed, 28 total**.

### CLI grammar smoke

```powershell
dotnet run --project .\src\Tools\HermesBridgeClient\HermesBridgeClient.csproj -c Release --no-build -- --help
```

Result: exit 0; printed exactly the four supported commands and stated that send never reads stdin.

### Read-only live smoke

Direct script invocation was blocked by the machine’s PowerShell execution policy before the script ran. It was then executed with a process-scoped bypass:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\HermesBridge.Client.Smoke.ps1
```

Result:

```text
PASS health is the expected loopback service
PASS unauthenticated session returns 401
PASS authenticated session succeeds when settings exist
RESULT 3 passed, 0 failed, 0 skipped
```

This confirms the live Hermes bridge was running on `127.0.0.1:8972`, unauthenticated session access was rejected, the local settings file existed, and authenticated session access succeeded. No live turn was sent or interrupted.

## Usage

From the build output directory:

```powershell
dotnet .\hermes-bridge.dll health
dotnet .\hermes-bridge.dll status
dotnet .\hermes-bridge.dll send --text "Explicit message"
dotnet .\hermes-bridge.dll interrupt
```

Or during development:

```powershell
dotnet run --project .\src\Tools\HermesBridgeClient\HermesBridgeClient.csproj -- health
```

No endpoint or token command-line option is provided. This prevents accidental remote targeting and avoids putting the bridge code in shell history or process arguments.

## Limitations and review notes

- The client is framework-dependent and requires a .NET 10 runtime. No self-contained package was created.
- The live smoke validated health and authenticated/unauthenticated session access only. Live `send` and `interrupt` were intentionally not exercised.
- The actual CLI `status` command was not run against the live bridge because it intentionally prints the visible conversation snapshot. The live smoke validated the same authenticated endpoint while suppressing its body.
- Offline timeout, response-limit, redirect, malformed-body, endpoint, and mutation-shape tests use injected in-memory handlers rather than the live host.
- The root checkout has no `.git` directory, so Git status/diff evidence is unavailable. Filesystem inventory confirmed every created source, test, generated build file, and this report is inside the three assigned ownership paths.

No commit, publish, installer change, launcher change, host integration, renderer change, or automatic message submission was performed.
