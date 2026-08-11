# Serena and Roslyn responsibility map

Hermes Workbench uses Serena and Roslyn together. They solve different problems and neither replaces the other.

## Serena: shared agent map

Serena is the canonical agent-facing semantic navigation and editing layer. Hermes, Codex, Ali, Scarlett, and future agents should use the same Serena project map for symbol discovery, focused reads, references, and bounded semantic edits. One map gives every agent the same code identities and avoids duplicating repository indexing work.

Agent tools should prefer Serena because its vocabulary is compact and task-oriented. The model asks for a symbol or edit, not for compiler-workspace plumbing. Results must remain bounded, stable, and easy to cite between agents.

## Roslyn: authoritative C# editor and compiler layer

Roslyn is the authoritative C# implementation behind editor diagnostics, syntax highlighting support, completion, hover, go-to-definition, references, rename, formatting, code actions, exact project/solution loading, and compilation. Roslyn results feed Monaco, Problems, build/debug views, and deliberate editor actions.

Roslyn is not exposed as a broad mirror of its compiler object model. Low-level syntax trees, semantic models, workspaces, and provider APIs are implementation details unless a narrowly designed agent operation supplies proven value that Serena cannot.

## Provider-neutral editor seam

Workbench language features flow through the provider-neutral developer-services/LSP boundary. The same renderer contracts can later host Eclipse JDT for Java and other language servers without changing the agent tool vocabulary or adding another container. Each provider owns only an explicitly authorized child process and workspace mapping.

## Invariants

1. Serena remains the shared agent-facing map across all assistants.
2. Roslyn remains authoritative for C# compiler/editor facts; Serena does not reinterpret compiler diagnostics.
3. Agent-facing Roslyn additions require narrow task-oriented contracts, bounded results, explicit targets, and evidence that Serena cannot already perform the operation well.
4. No agent receives an unbounded compiler object graph or assembly-scanned tool catalog.
5. Roslyn and other language servers never inherit parent-process secrets by default.
6. One Workbench installation contains all language modules; modular providers do not become separate product editions or Docker containers.
