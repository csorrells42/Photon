# Scarlett Assignment: Hermes Functionality Inventory

## Objective

Create an evidence-based inventory of every user-facing capability in the current Hermes application so a new React/TypeScript interface can reproduce the behavior without modifying the upstream Hermes implementation.

## Workspace and output

- Read-only workspace: `C:\Users\clsor\Documents\Codex\HermesAgent`
- Upstream Hermes checkout: `C:\Users\clsor\Documents\Codex\HermesAgent\source`
- Existing desktop frontend begins under: `source\apps\desktop`
- Gateway/server implementation includes: `source\hermes_cli\web_server.py`
- Your only permitted write target:
  `C:\Users\clsor\Documents\Codex\HermesAgent\docs\research\scarlett-hermes-functionality-inventory.md`

Create or replace only that single report file. Do not modify any source, configuration, Docker, installer, dependency, lock, or generated file.

## Safety rules

1. This is a read-only investigation except for the one report file above.
2. Do not install packages, run formatters, start or stop containers, change settings, edit `.env`, or execute scripts that mutate the workspace.
3. Do not refactor or fix anything you discover.
4. Never include secret values, API keys, tokens, cookies, or environment-file contents in the report.
5. If evidence is incomplete, label the item `Uncertain`; do not guess.
6. Stop after writing the report.

## What to inventory

Inspect the existing frontend and its calls into Hermes. Cover at least:

1. Chat lifecycle: new conversation, submit, streamed output, cancel/stop, retry, edit, regenerate, queueing, errors, and reconnect behavior.
2. Sessions/history: list, load, search, rename, delete, archive/pin if present, persistence, and resume behavior.
3. Models/providers: selection, model metadata, authentication surfaces, runtime options, and connection status.
4. Tools and MCP: tool-call presentation, arguments/results, approvals, errors, tool-server management, and Serena-relevant behavior.
5. Files/workspaces: workspace selection, context/mentions, attachments, drag/drop, file inspection, diffs, and generated artifacts.
6. Terminal and commands: terminal/PTY features, slash commands, command palette actions, and keyboard shortcuts.
7. Rich content: Markdown, code blocks, copy buttons, syntax highlighting, images/media, citations, thinking/reasoning, and status/progress cards.
8. Settings and system status: appearance, preferences, logs, health, updates, notifications, and destructive actions.
9. Any capability implemented in the gateway but not visibly exposed in the current frontend.

## Required evidence for each feature

For every feature, record:

- Feature name and user outcome.
- Status: `Visible UI`, `Hidden/conditional UI`, `Backend only`, or `Uncertain`.
- Frontend evidence: exact relative file path plus component/function/event name.
- Backend contract: endpoint or WebSocket/RPC method/event, when identifiable.
- Request fields and response/event fields, summarized without secret values.
- Important states: loading, streaming, success, empty, failure, cancelled, disconnected.
- Dependencies or coupling that our compatibility adapter must isolate.
- Parity priority: `P0 core`, `P1 important`, or `P2 later`.

## Report structure

Write the report with these sections:

1. Executive summary
2. Architecture and entry points
3. Feature matrix
4. HTTP endpoint inventory
5. WebSocket/RPC method and event inventory
6. UI state and error-state inventory
7. Keyboard shortcuts and commands
8. Compatibility risks across Hermes updates
9. Recommended vertical-slice implementation order
10. Unknowns requiring runtime verification

Use Markdown tables where they improve scanning. Cite exact repo-relative paths and symbols. Separate confirmed source evidence from inference.

## Completion check

Before stopping, confirm the report exists at the exact output path, contains all ten sections, and that no other project file was changed.
