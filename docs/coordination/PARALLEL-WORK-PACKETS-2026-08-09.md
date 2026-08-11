# Hermes Workbench Parallel Work Packets

Copy one complete packet into each independent Codex task or into Scarlett. These are research and specification lanes only so they cannot collide with the active implementation lane.

## Shared rules for every packet

- Work in `C:\Users\clsor\Documents\Codex\HermesAgent`.
- Treat the current files on disk as authoritative.
- Do not edit application source, tests, scripts, configuration, generated reports, ZIP files, artifacts, dependencies, or upstream `source/`.
- You may read any project file needed for evidence.
- Create or edit only the single Markdown deliverable named in your packet.
- Do not commit, push, install software, restart services, change credentials, or send external messages.
- Never read or expose API keys, passwords, cookies, tokens, `.env` contents, session data, or files under `data/`.
- Use exact file paths, symbols, endpoints, contract fields, and evidence. Separate confirmed facts from recommendations.
- Do not claim runtime verification unless you actually performed a safe read-only check allowed by the packet.
- When finished, reply with exactly: `COMPLETE — <absolute output path>` followed by a maximum five-bullet summary and any blockers.

---

## Packet 1 — Codex: OpenAI Usage Collector v1 Specification

```text
You are the OpenAI Usage Collector specification lane for Hermes Workbench.

Workspace:
C:\Users\clsor\Documents\Codex\HermesAgent

Read and obey:
docs\coordination\PARALLEL-WORK-PACKETS-2026-08-09.md

Your only writable deliverable:
docs\coordination\OPENAI-USAGE-COLLECTOR-V1-SPEC.md

Objective:
Produce an implementation-ready, security-reviewed specification for a native C# OpenAI API organization usage/cost collector that fits the existing Usage Intelligence protocol. This is for OpenAI API organization/project telemetry, not ChatGPT consumer subscription usage.

Required research:
1. Inspect the current local contracts and OpenRouter precedent:
   - src\Host\HermesDesktop\OpenRouterUsageCollector.cs
   - src\Host\HermesDesktop\MainWindow.xaml.cs
   - src\Modules\UsageIntelligence\DesktopUsageBridge.ts
   - src\Modules\UsageIntelligence\DesktopUsageAdapter.ts
   - src\Modules\UsageIntelligence\types.ts
   - docs\USAGE-INTELLIGENCE-PROVIDERS.md
2. Browse only current official OpenAI documentation for organization usage and costs. Prefer the API reference. Do not use blogs, forum posts, search-result snippets, or third-party SDK documentation as authority.
3. Explicitly verify the required credential class/role. Do not assume an ordinary project API key can read organization usage or costs.

Required specification sections:
- Purpose and explicitly excluded scope.
- Official endpoints, HTTP methods, API version, and direct official links.
- Authentication and minimum required organization role/credential.
- Exact query parameters, time boundaries, bucket widths, grouping options, page size, pagination cursor behavior, and date/time normalization.
- Exact response fields used for requests, input/output/cached tokens, cost, currency, project/model attribution, and freshness.
- A proposed native protocol version and request/response envelope that preserves the current opaque `credentialId` renderer boundary.
- Mapping into the existing `UsageProviderSnapshot`, including which values are reported, delayed, estimated, unavailable, or not comparable.
- Multi-page aggregation rules, USD/currency handling, missing buckets, duplicate protection, and overflow/invalid-number handling.
- Fixed-host HTTPS and redirect policy, timeouts, response-size limits, cancellation, retry policy, and rate-limit behavior.
- Sanitized error-code mapping for unauthorized, forbidden, insufficient role, rate limited, upstream failure, malformed response, timeout, and cancellation.
- Data-minimization table listing fields that may cross into React and fields that must be discarded, including organization/project/user identifiers and raw response bodies.
- Fake-handler test matrix with exact success, pagination, empty, delayed, malformed, authorization, rate-limit, redirect, timeout, cancellation, and sanitization cases.
- Integration checklist naming the exact existing files that a later implementation would need to change. Do not change those files yourself.
- Open questions that genuinely require an account owner or live credential.

Hard requirements:
- State clearly that ChatGPT consumer subscription allowance is not represented by these API organization endpoints.
- Do not propose scraping chatgpt.com or any private dashboard.
- Do not place any real or example-shaped secret in the document.
- Do not implement code.

Completion:
Reply with `COMPLETE — C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\OPENAI-USAGE-COLLECTOR-V1-SPEC.md`, then at most five bullets and any blockers.
```

---

## Packet 2 — Codex: Hermes Rich Output Parity Inventory

```text
You are the rich-output parity research lane for Hermes Workbench.

Workspace:
C:\Users\clsor\Documents\Codex\HermesAgent

Read and obey:
docs\coordination\PARALLEL-WORK-PACKETS-2026-08-09.md

Your only writable deliverable:
docs\coordination\HERMES-RICH-OUTPUT-PARITY.md

Objective:
Build a source-mapped inventory of every upstream Hermes rich-input and rich-output behavior that affects a polished agent conversation, compare it with Workbench, and recommend one smallest update-safe implementation slice.

Evidence scope:
- Read upstream Hermes under `source\` without modifying it.
- Read Workbench under `src\` without modifying it.
- Read docs\HERMES-FUNCTION-PARITY.md for context, but independently verify current source because the matrix contains an older audit snapshot.
- Internet research is not required. If used, official Hermes repository/documentation only.

Required domains:
- Citations and linked sources.
- Markdown links and safe external navigation.
- Inline and attached images, image metadata, upload references, and image-result rendering.
- Audio transcription, generated speech, streaming speech, and voice conversation.
- URL references or webpage attachments.
- Downloadable/generated files and agent artifacts.
- Tool results, structured result sections, command output, errors, diffs, and patches.
- Code blocks, syntax/language metadata, copy behavior, and any file/line navigation affordances.
- Streaming lifecycle for partial content, final content, failures, cancellations, and retries.

For every domain provide:
- User outcome.
- Exact upstream file paths and symbols/routes/events.
- Exact current Workbench file paths and symbols.
- Wire shape or REST/WebSocket method when present.
- Classification: implemented, partial, missing, or intentionally delegated.
- Security implications: untrusted URLs, remote media, HTML, file paths, downloads, MIME types, and renderer/native trust boundaries.
- Concrete verification needed.

Required final recommendation:
- Select exactly one next implementation slice that creates visible user value while remaining versioned and update-safe.
- Define its smallest complete contract, source files likely involved, fixtures/tests required, security acceptance criteria, and live acceptance check.
- Explain why it should precede the other missing rich-output slices.
- Do not implement it.

Hard requirements:
- Do not infer behavior from filenames alone; inspect the relevant implementation.
- Do not copy upstream UI internals as a proposed architecture. Prefer public HTTP/WebSocket contracts and narrow Workbench adapters.
- Do not claim vision/model capability when the evidence only proves attachment transport.

Completion:
Reply with `COMPLETE — C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\HERMES-RICH-OUTPUT-PARITY.md`, then at most five bullets and any blockers.
```

---

## Packet 3 — Codex: Portable Windows Acceptance Plan

```text
You are the clean-computer acceptance-planning lane for Hermes Workbench.

Workspace:
C:\Users\clsor\Documents\Codex\HermesAgent

Read and obey:
docs\coordination\PARALLEL-WORK-PACKETS-2026-08-09.md

Your only writable deliverable:
docs\coordination\PORTABLE-INSTALL-ACCEPTANCE-PLAN.md

Objective:
Produce an exact, beginner-friendly but engineering-grade acceptance plan for proving the portable ZIP on a clean Windows 11 machine or disposable VM. This is a plan only: do not install, launch, stop, update, or delete anything during this task.

Required inspection:
- Root install/launch/check/shutdown/update scripts and `.cmd` wrappers.
- `remote-install\` scripts, README, smoke tests, and ZIP builder.
- `launcher.settings.json`, `docker-compose.yml`, `.vscode\tasks.json`, and the generated bundle manifest contract.
- Current desktop host and Usage Intelligence documentation.

Required plan sections:
- Test-machine prerequisites and isolation assumptions.
- How to transfer the ZIP plus `.sha256` file and independently verify the transfer checksum before extraction.
- Clean extraction and `Install-Hermes.ps1 -VerifyBundleOnly` evidence.
- Default install and custom-path install cases.
- Expected prerequisite prompts for Docker Desktop, Node.js, WebView2, Git, Python/uv/Serena, including restart boundaries.
- Desktop shortcut creation and exact expected shortcuts.
- First launch, Docker/container readiness, Serena readiness, Vite readiness, desktop-host path, and `Check Hermes` 9-check evidence.
- Dashboard account setup and in-Workbench sign-in.
- Real Hermes prompt/stream/Stop test, session persistence/resume, attachment, expandable reasoning, and full-text session search.
- Real Hermes-to-Serena tool discovery and one harmless read-only code-navigation tool call.
- Usage Intelligence OpenRouter credential save, live refresh, masked metadata, Windows Credential Manager target presence, and deletion. Never instruct the tester to record or expose the secret.
- Browser fallback boundaries: no native terminal, credentials, usage collection, or Codex bridge.
- Optional Codex panel detection and a no-model-turn startup check.
- Controlled no-change update check, rollback evidence requirements, and host-mounted data preservation.
- Scoped shutdown proving only Hermes-owned processes/container stop while Docker Desktop and unrelated containers remain untouched.
- Relaunch after shutdown and persistence checks.
- Failure-evidence capture that avoids secrets: exact safe logs/commands/screenshots and what must be redacted.
- Pass/fail table with an authoritative evidence artifact for every step.
- Cleanup/uninstall gap assessment. Clearly identify whether an uninstall workflow exists; do not invent one.
- Automation opportunities divided into safe-to-automate, requires interactive user, and should remain manual.

Hard requirements:
- Write for a first-time Docker user without weakening technical precision.
- Every destructive or externally visible operation in the future test must have an explicit target and expected effect.
- Do not tell the tester to use `docker system prune`, recursive broad deletion, or any command that could affect unrelated applications.
- Do not execute the plan in this task.

Completion:
Reply with `COMPLETE — C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\PORTABLE-INSTALL-ACCEPTANCE-PLAN.md`, then at most five bullets and any blockers.
```

---

## Packet 4 — Scarlett: Google, Gemini, and Anthropic Usage Research

```text
You are Scarlett working as the provider-research lane for Hermes Workbench.

Workspace:
C:\Users\clsor\Documents\Codex\HermesAgent

Read and obey:
docs\coordination\PARALLEL-WORK-PACKETS-2026-08-09.md

Your only writable deliverable:
docs\coordination\SCARLETT-USAGE-PROVIDER-RESEARCH.md

Objective:
Research the safest supported sources for Google Cloud/API, Google AI Studio/Gemini API, Anthropic API, and Claude consumer usage intelligence. Produce a precise evidence pack for later native collectors. Do not implement code.

Research rules:
- Use current official Google Cloud, Google AI for Developers, and Anthropic/Claude documentation only.
- Do not use blogs, Stack Overflow, Reddit, vendor comparisons, unofficial SDK docs, or search-result snippets as authority.
- Open the actual official pages and cite direct links next to each claim.
- If no supported programmatic endpoint is documented, say unavailable or manual-only. Do not invent or propose scraping.

For each provider/surface report:
- Classification: ready, partial, manual-only, or unsupported.
- Exact official endpoint/API/export when one exists.
- Required authentication type, IAM role, organization role, admin key, service account, OAuth scope, or billing permission.
- Whether the credential is appropriate for a local Windows native host, a backend only, or neither.
- Metrics available: spend, credits, budgets, requests, tokens, quotas, rate limits, billing period, model/project/workspace attribution, and reset data.
- Pagination, aggregation period, currency, time zone, reporting delay, freshness signal, and documented exclusions.
- Account-type restrictions: individual, organization, enterprise, cloud-hosted, API versus consumer subscription.
- Whether a standard API key is sufficient or a privileged reporting credential is required.
- Fields safe to normalize into React and identifiers/raw responses that must be discarded.
- Minimum fake-handler and malformed-response test cases for a future collector.

Provider-specific questions that must be answered:
1. Google Cloud: what Budget API reads actually report versus what requires Cloud Billing export to BigQuery; minimum IAM roles for both; export delay and query-cost implications.
2. Gemini/AI Studio: whether an API key exposes dashboard usage, prepay balance, spend-cap consumption, or quotas programmatically; what can be obtained only through AI Studio UI or linked Cloud Billing.
3. Anthropic: exact Usage and Cost API routes, Admin API key requirements, organization/account eligibility, bucket options, token fields, cost currency, pagination, and documented excluded charges.
4. Claude consumer subscriptions: whether any supported public allowance/usage API exists. Private-page scraping is prohibited.

Finish with:
- A comparison table.
- Recommended implementation order with rationale based on official support, credential risk, user value, and engineering complexity.
- Open questions requiring the account owner or a live least-privilege test credential.

Hard requirements:
- Never request, inspect, or store a real credential.
- Never edit any file except the named Markdown deliverable.
- Keep conclusions conservative and source-backed.

Completion:
Reply with `COMPLETE — C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\SCARLETT-USAGE-PROVIDER-RESEARCH.md`, then at most five bullets and any blockers.
```
