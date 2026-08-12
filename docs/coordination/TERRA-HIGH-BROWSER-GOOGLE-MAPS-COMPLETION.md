# Terra High — Browser, Google Maps, and Help Completion

## Outcome

The existing native WebView2 browser is now a complete Workbench surface for ordinary HTTP/HTTPS browsing, typed Google Maps searches and directions, and the application Help command.

- The browser keeps its tabs, address entry, Back, Forward, Reload/Stop, close, loading state, and native placement behavior.
- A compact expandable Google Maps panel builds exact HTTPS `api=1` search and directions URLs using `URL` and `URLSearchParams`.
- Directions support only `driving`, `walking`, `bicycling`, and `transit`; origin is optional and destination is required.
- **Help → Hermes Help** selects the Browser workspace and opens `https://hermes-agent.nousresearch.com/docs/` using a stable `hermes-help` request key. Repeated activation focuses the retained Help tab instead of adding duplicates.
- Browser addresses with embedded credentials, control characters, unsupported schemes, or oversized values fail closed.
- Native state continues to project only the scheme/authority and a bounded safe title. Google Maps query, origin, and destination values are not returned to the renderer by native navigation state.
- Pending native-surface shows are generation-bound. Hide, reset-style invalidation, and disposal revoke an older show before it can become visible.

Google Maps behavior is ordinary browser navigation. It is **not** a Google Maps JavaScript, Places, Directions, geocoding, billing, or privileged API integration. Hermes does not verify route correctness, live traffic, or Google results.

## File scope and frozen hashes

| File | SHA-256 |
|---|---|
| `src/Host/HermesDesktop/BrowserSurfaceBridge.cs` | `ABE44E909B53B788E12695925AC5AD672CC2EBE06AF97BDDA503C0798EDE16DC` |
| `src/Host/HermesDesktop.Smoke/Program.cs` *(shared browser + developer-tooling intake)* | `499B27EB0539ECC2E74C01E2EBA8CD51393BEAE9743C36B57D3AD818BF23D2BA` |
| `src/Modules/BrowserWorkspace/DesktopBrowserClient.ts` | `F5492DA46D58BD253257ECB36FCEADD5861F70460D56755241075DF520D29079` |
| `src/Modules/BrowserWorkspace/DesktopBrowserClient.test.ts` | `525990A8A009BE8BB0049D860AA8CB4718826CF1EFC19F5F2C53CC5320DDAD0E` |
| `src/Modules/BrowserWorkspace/BrowserWorkspace.tsx` | `20AF5EC28F99CEFCAB9EF6CAD86659BF342DB2F2A8AD75D527ABD362091EB41D` |
| `src/Modules/BrowserWorkspace/BrowserWorkspace.css` | `8F468E01B41FF7D51E3F4CEDBEAC3680D49C315C7B12650703EDA1816FB1F664` |
| `src/Modules/BrowserWorkspace/BrowserWorkspace.test.tsx` | `273F7F57C4F1A45D6283A1B9B374260B97644F939F55D21BFA84CBF070668E3F` |
| `src/app/App.tsx` | `20473A8C9513D829649D9B8BFFA33010CBF38D95D3FE78E121130BF494893799` |

`src/Host/HermesDesktop.Smoke/Program.cs` is a shared mixed-origin file. This lane added only the browser address/Maps projection, repeated placement, and out-of-order show/reset assertions. The developer-tooling lane preserved those assertions and froze the combined file at the hash above; both lanes' isolated combined builds passed.

No MainWindow, project/package, Docker, CAD, DeveloperServices, model-runtime, credential, installer, conversation-bridge, or security-report file was changed by this lane.

## Verification

All commands used the authoritative checkout `C:\Users\clsor\Documents\Codex\HermesAgent`.

| Gate | Result |
|---|---|
| `npx.cmd vitest run Modules/BrowserWorkspace/DesktopBrowserClient.test.ts Modules/BrowserWorkspace/BrowserWorkspace.test.tsx app/WorkbenchMenuCommands.test.ts` | PASS — 3 files, 15 tests |
| `npx.cmd tsc -b --pretty false` | PASS |
| `dotnet build src\Host\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release --no-restore` | PASS — 0 warnings, 0 errors |
| `dotnet run --project src\Host\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release --no-build -- --document-browser-only` | PASS — strict address, Maps redaction, per-tab history, late completion ownership, placement deduplication, single-flight initialization, and deferred show/hide ordering |
| `npx.cmd vitest run` | PASS — 130 files, 801 tests |
| `npx.cmd vite build` | PASS — 3,289 modules transformed |
| scoped `git diff --check` | PASS |

The focused test suite was run outside the restricted sandbox after Vite/esbuild was denied access while resolving the workspace config inside it. The same authoritative files were used.

## Deferred security observations

Recorded only; no security remediation was performed in this functionality-first lane.

1. The existing browser host enables WebView2 DevTools. Product policy should decide whether production builds retain that capability.
2. The existing isolated browser user-data folder retains normal site state between desktop sessions. It does not import the system browser profile, but retention/clear controls should receive a later privacy review.

## Live acceptance

Not run in this lane. No desktop publish, restart, live process, system browser, Google account, location permission, or external API was touched. Root should run the visible Help, Maps Unicode search, directions, panel-switch persistence, resize, duplicate-tab isolation, and hidden-overlay click test after the combined application publish.
