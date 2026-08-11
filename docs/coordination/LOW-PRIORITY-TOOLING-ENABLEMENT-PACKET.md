# Low-priority tooling enablement packet

Date: 2026-08-11 (America/Chicago)

## Decision

Enable these providers only after CAD and the main Workbench functionality pass, in this exact order:

1. Arduino
2. Java / Eclipse JDT
3. GNU C/C++

The GNU C/C++ source transaction is already frozen and green. It remains intentionally unmounted until Arduino and Java have been enabled and accepted.

Security findings discovered during this functionality pass belong in the existing deferred-security reports. Do not broaden these packets into security remediation unless a finding directly prevents the feature from operating.

## Arduino: current state

Implemented foundations:

- `Embedded/Arduino/ArduinoHostProvider.cs` provides typed version/core/board/library inventory, compile, upload and package-install operations.
- `ArduinoLanguageToolingOperationHandler` provides bounded project inspection and compile routing.
- The renderer already recognizes `.ino`, accepts an explicit FQBN and exposes `Check with Arduino`.
- The desktop composition already constructs `ArduinoProviderOptions` and registers Arduino evidence and operations.
- Fake authority and concrete handler smokes prove project inspection, exact FQBN binding, host-owned output and permission scope.

Current functional blocker:

- `<install-root>/toolchains/arduino` contains no installer-owned `arduino-cli` payload and `hermes-toolchain-receipt.json`.
- The required `arduino-config.json` and `<install-root>/developer-services/arduino-state` authority are not provisioned.
- Therefore the registered evidence source correctly reports `arduino-runtime-not-provisioned` and the mounted action remains disabled.

### Arduino implementation packet

1. Add a release-owned Arduino CLI lock describing the exact official archive, version, digest, expanded-file inventory and executable digest.
2. Add a provisioner that safely extracts only the locked payload into `toolchains/arduino`, writes the existing trusted receipt shape atomically and preserves the prior install on failure.
3. Provision an app-owned `arduino-config.json` whose data/download/user directories stay under `developer-services/arduino-state`; create that state root during install.
4. Package one explicitly supported board/core profile for the first acceptance target. Start with `arduino:avr:uno`; do not claim arbitrary board support from CLI presence alone.
5. Keep upload disabled for the first functional tranche unless a native host-owned serial-port catalog and explicit reviewed upload action are mounted. Compile must not accept a raw renderer-selected port.
6. Reuse the existing `DeveloperServicesBridge` registration. Do not create a second Arduino provider or renderer protocol.

### Arduino acceptance

- Provisioning fake smoke: clean install, idempotent reinstall, corrupt archive, missing executable, extra payload, link/reparse entry, receipt tamper and failure-preserves-prior.
- Native description reports Arduino project and compiler `2/2 Host verified` only when the exact receipt/config/state are present.
- Open a real disposable `Blink.ino` workspace project, inspect it and compile it for `arduino:avr:uno`.
- Result contains bounded typed diagnostics and fresh host-owned artifacts; no raw process command, environment, private state path or unrestricted output reaches the renderer.
- Cancellation clears busy state and leaves no staged output.
- C#, Python and CAD mounted regressions remain green.

## Java / Eclipse JDT: current state

Implemented foundations:

- `JavaJdtLanguageToolingProvider` owns bounded Java language sessions, workspace/path confinement, initialization, document open and structured stop.
- `JavaJdtLanguageSession` and its fake transport smoke cover typed LSP lifecycle, freshness and bounded diagnostics.
- The renderer catalog already declares `java-jdt.lsp`.

Current functional blocker:

- `JavaJdtProvisioning.CurrentBlocker` intentionally reports `java-jdt-provenance-not-pinned`.
- No release-owned combined lock currently binds an official Eclipse JDT LS distribution and an app-owned Java runtime.
- No complete bounded payload manifest, licenses/notices packet or trusted receipt is installed at `developer-services/java-jdt`.
- Production composition therefore uses `CreateUnprovisioned` and cannot start Java.

### Java implementation packet

1. Freeze one compatible official Eclipse JDT LS distribution and one app-owned Java runtime; record immutable archive and executable digests.
2. Build a complete bounded extracted-file manifest and include the required license/notice material. Legal/compliance observations may be documented and deferred, but the installer still needs an exact payload identity before functional launch.
3. Add a safe atomic provisioner for `developer-services/java-jdt` and its `hermes-toolchain-receipt.json`.
4. Implement the production `IJavaJdtRuntimeAuthority`: verify the receipt and payload on every start, use fixed executable/arguments and app-owned Java only, bind the exact workspace and project state, and own cleanup.
5. Replace only the `CreateUnprovisioned` composition call with the receipt-backed provider. Preserve the existing catalog, request contracts and session lifecycle.
6. Mount typed completion, hover, definition, references, rename and code-action projections before calling Java editor intelligence complete. Reject server commands and confine edits to the current workspace.

### Java acceptance

- Provisioning fake smoke covers corruption, missing Java/JDT files, manifest drift, unexpected payload and failure-preserves-prior.
- Java provider reports `1/1 Host verified` only for the exact provisioned pair.
- A real disposable multi-file Java project passes diagnostics, completion, hover, definition, references, bounded rename and code actions.
- Explicit stop/start recovery works without automatic replay; adapter exit retires the old session.
- No machine `java`, PATH lookup or mutable download is used.
- Arduino, C#, Python, CAD and debugger regressions remain green.

## GNU C/C++: frozen source transaction

Completed source work:

- Fixed container-backed authority bound to the exact Docker executable, immutable `HERMES_IMAGE_REFERENCE`, exact `hermes` container and exact RW `/workspace` mount.
- Fixed `/usr/bin/gcc` and `/usr/bin/g++` selection for C17 and C++20.
- Bounded targets, sources, output, diagnostics, artifacts and timeouts; typed cancellation and cleanup.
- Developer Services and renderer integration are source-green without changing frozen C# or Python behavior.

Required final acceptance after Arduino and Java:

- Coherently publish the frozen seven-file transaction.
- Mounted GNU C/C++ reports `1/1 Host verified`.
- A selected `.c` uses GCC and a selected `.cpp` uses G++; both return bounded diagnostics and a fresh artifact.
- Preserve and disclose two current limitations: local Docker CLI cancellation does not itself prove the remote `docker exec` child died, and the legacy compile artifact contract can name a Linux ELF `artifact.exe`.
- C#, Python, Arduino, Java, CAD and debugger regressions remain green.

