# Photon Docker MCP backlog

Last reconciled: 2026-08-14

Photon uses one Docker MCP gateway with task-scoped profiles. Servers remain
isolated behind the gateway; do not use `--enable-all-servers`.

## Implemented profiles

- `photon-engineering-discovery`: Microsoft Learn, Docker Docs, OpenAPI
  Toolkit, .NET Types Explorer, Javadocs, and GitHub discovery-only tools.
- `photon-relentless-repair`: bounded Desktop Commander operations and
  Globalping. Dormant until an untrusted-server approval activates it.
- `photon-web-research`: Fetch, DuckDuckGo search-only, and MarkItDown.
  Dormant until profile activation. MarkItDown receives only the exact
  configured Photon workspace at launch.

Linear tracking: parent PHO-1; Fetch PHO-2; DuckDuckGo PHO-3; MarkItDown PHO-4.

## Deferred candidates

- Google Maps Comprehensive: add only with an approved Google Maps API key and
  keep it separate from the visual Maps workspace.
- WolframAlpha: evaluate credentials, cost, engineering-unit behavior, and
  result provenance first.
- Playwright: activate only for rendered browser validation that existing
  browser automation cannot cover.
- ast-grep, ArXiv, YouTube Transcripts, Maven Tools, Arm, and ROS 2: add as
  narrow job profiles when a demonstrated Photon workflow needs them.
- Prometheus, ThingsBoard, OpenWeather, Paper Search, and Scorecard: revisit
  with live telemetry, monitoring, or formal research workflows.

## Explicitly skipped

- Python Refactoring Assistant: Serena plus the real language tooling provides
  stronger source intelligence.
- Generic filesystem servers, competing memory systems, Sequential Thinking,
  Task Orchestrator, and extra Python/Node sandboxes: duplicate existing
  capabilities or add unnecessary tool/context overhead.
