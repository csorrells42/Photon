# Hermes Parity Audit Handoff

## Files written

- `docs/HERMES-FUNCTION-PARITY.md` — comprehensive source-backed upstream-to-Workbench parity matrix, compatibility risks, prioritized backlog, verification requirements, and update-survival guidance.
- `docs/coordination/HERMES-PARITY-HANDOFF.md` — this handoff.

No source code, tests, packages, Docker files, installers, artifacts, data, logs, environment files, credentials, or other coordination files were modified.

## Upstream version/revision evidence

- Local upstream checkout: `source/`
- Remote: `git@github.com:NousResearch/hermes-agent.git`
- Branch: `main`
- Commit: `8e9ecc1f3e95aaaa3cf2c7582a9da17788a5aa99`
- Commit date/subject: 2026-08-08, `Merge pull request #82014 from NousResearch/bb/hud-band-hit-area`
- `source/package.json` declares package version `1.0.0`, but that is not release-revision proof.
- Important limitation: root `docker-compose.yml` uses `nousresearch/hermes-agent:latest`; this audit does **not** prove that any deployed/running container matches the local source commit.

## Classification counts

| Classification | Count |
|---|---:|
| `native-complete` | 2 |
| `adapter-complete` | 9 |
| `upstream-delegated` | 13 |
| `partial` | 6 |
| `missing` | 1 |
| `not-applicable` | 1 |
| `unverified` | 0 |
| **Total** | **32** |

## Five highest-risk gaps

1. **No deployment identity/compatibility guard.** Workbench adapters can connect to a floating `latest` upstream image without proving the image revision or supported contract.
2. **Core chat recovery is incomplete.** The supported path streams and interrupts, but lacks bounded automatic reconnect/re-ticket, retry/edit/regenerate, queueing, and proven stale-session recovery.
3. **Cookie and WebSocket ticket coupling.** `/api/auth/ws-ticket` plus `/api/ws` and `/api/console` must remain same-origin and shape-compatible; a small upstream auth change can break all interactive paths.
4. **Session/profile dual-ID semantics.** Workbench tracks both runtime and stored session IDs and falls back between unified and legacy list routes; drift can misroute history or mutations.
5. **Security-sensitive surfaces are intentionally delegated.** Provider credentials, MCP OAuth/catalog, raw configuration, backups/imports, and channel integrations remain in upstream dashboard. Native copies would increase secret and update-coupling risk.

## Recommended next implementation slice

Implement a **P0 compatibility-preflight and resilient session-lifecycle slice**:

1. Read-only runtime release/version/digest capability record with an explicit unsupported-contract state.
2. Contract fixtures for ticket/auth, `/api/status`, session create/resume/submit/interrupt, stream events, tools/approvals/prompts, attachments, and console frames.
3. Bounded reconnect/re-ticket plus stale-session recovery, with clear retry/cancel/error states.

This is the smallest slice that protects existing adapter-complete functionality against upstream updates before expanding desktop surface area.

## Audit limitations

- Read-only source audit only; no processes, containers, applications, tests, authenticated requests, or hardware/desktop interactions were run.
- Existing unit and smoke test files are source evidence only; none were executed.
- No data, logs, `.env`, browser profile, credential, or account material was inspected.
- Runtime container revision, live authentication, provider configuration, WebSocket behavior, Serena connectivity, terminal behavior, and actual feature availability remain unverified.
