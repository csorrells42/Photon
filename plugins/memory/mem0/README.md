# Mem0 Memory Provider

Server-side LLM fact extraction with semantic search and hybrid multi-signal retrieval via the Mem0 Platform v3 API.

## Requirements

- `pip install mem0ai`
- Mem0 API key from [app.mem0.ai](https://app.mem0.ai)

## Setup

```bash
hermes memory setup    # select "mem0"
```

Or manually:
```bash
hermes config set memory.provider mem0
echo "MEM0_API_KEY=your-key" >> ~/.hermes/.env
```

## Config

Behavioral settings live in `$HERMES_HOME/mem0.json` (set them via `hermes memory setup`). Only the secret `MEM0_API_KEY` belongs in `~/.hermes/.env`.

| Key | Default | Description |
|-----|---------|-------------|
| `mode` | `platform` | `platform` (Mem0 Cloud) or `oss` (self-managed, in-process) |
| `host` | — | Self-hosted Mem0 server URL (the Docker dashboard). When set, connects over HTTP with `X-API-Key`. Don't combine with `mode: oss` |
| `user_id` | `hermes-user` | User identifier on Mem0 |
| `agent_id` | `hermes` | Agent identifier |
| `rerank` | `false` | Rerank search results for relevance (platform mode only) |

### Authenticated Workbench mode

When `HERMES_WORKBENCH_AUTHENTICATED_MEM0=1`, identity comes only from the
verified dashboard login. `user_id`, `MEM0_USER_ID`, and the legacy
`hermes-user` fallback are ignored and are not offered by setup. With no live
principal, all memory prompt blocks, recall, tools, prefetch, sync, and writes
are absent. Explicit logout revokes live WebSocket/PTY leases and waits for
in-flight memory commits before it returns.

Workbench setup is deterministic and local: Docker Model Runner's
OpenAI-compatible API at the explicitly configured URL (supported container
default `http://host.docker.internal:12434/engines/v1`, with an optional
validated nonsecret `HERMES_MEM0_MODEL_URL` override), request model
`ai/qwen3:4B-UD-Q4_K_XL` plus `ai/nomic-embed-text-v1.5` embedder (768
dimensions), and the private Compose service at `http://memory-vector:6333`
using collection `hermes_workbench_mem0_v3`. The service stores data in the
Docker-managed `memory-vector-data` volume, publishes no host port, and must
pass its `/readyz` probe. Authenticated Workbench never falls back to an
embedded Qdrant directory. A bounded nonsecret `HERMES_MEM0_QDRANT_URL`
override may select another internal HTTP(S) endpoint, but command-line paths
and URLs cannot redirect this mode.
Setup refuses to save until Model Runner's OpenAI inventory and live chat plus
768-dimensional embedding probes pass. It uses Mem0's OpenAI-compatible provider
with the fixed public `not-needed` sentinel (DMR ignores authorization), never a
real API key and never remote OpenAI. The registry-qualified inventory IDs and
approved host-verified digests, embedding provider/model/dimensions, vector
endpoint/provider/distance, and collection are persisted as one identity.
Changing any of them later fails closed and requires an explicit memory rebuild
or migration—existing vectors are never silently deleted.

Implementation note: Mem0 2.0.10's provider named `lmstudio` is used only as
its environment-independent, local OpenAI-compatible transport class. The
product provider and runtime are Docker Model Runner; this does **not** install,
launch, configure, or otherwise depend on LM Studio. This adapter choice also
prevents a process-level `OPENROUTER_API_KEY` from redirecting memory traffic.

Recalled memories are untrusted context. They are never instructions, identity
proof, consent, authorization, policy, permissions, or current user intent.
Raw-ID update/delete stays unavailable unless a backend can enforce the owner
predicate atomically. Portable memory export/import is logical and owner-bound;
it never copies raw Qdrant directories or credentials.

The plugin has three connection modes:

- **Platform** — Mem0's hosted cloud (`api.mem0.ai`). Set `MEM0_API_KEY`. (default)
- **Self-hosted dashboard** — a Mem0 server you run yourself via Docker. Set `host`. See below.
- **OSS** — run Mem0 in-process with your own LLM + vector store. Set `mode: oss`. See below.

## Self-Hosted Dashboard (Server) Mode

Connect the plugin to a standalone Mem0 server you run yourself — the Docker-shipped Mem0 dashboard/server with its own REST API. Unlike OSS mode (which runs `mem0ai` in-process with your own vector store), here the plugin just talks HTTP to your server.

1. Run the Mem0 server (FastAPI + pgvector) from its Docker image and note its URL and `ADMIN_API_KEY`.
2. Point the plugin at it — via the setup wizard:
   ```bash
   hermes memory setup    # select "mem0" → "Self-hosted server"
   # Or non-interactive:
   hermes memory setup mem0 --mode selfhosted --host http://localhost:8888 --api-key your-admin-api-key
   ```
   or via env vars:
   ```bash
   echo "MEM0_HOST=http://localhost:8888" >> ~/.hermes/.env
   echo "MEM0_API_KEY=your-admin-api-key" >> ~/.hermes/.env
   ```
   or in `$HERMES_HOME/mem0.json`:
   ```json
   {
     "host": "http://localhost:8888",
     "api_key": "your-admin-api-key"
   }
   ```
3. Start a fresh Hermes session and call `mem0_search` — it connects to your server.

The plugin authenticates with `X-API-Key` and uses the server's `/search` and `/memories` routes. `api_key` is optional — omit it only for servers running with `AUTH_DISABLED`.

> Setting `host` routes to the self-hosted server automatically. Don't set `mode: oss` — OSS takes precedence and ignores `host`.

## OSS (Self-Hosted) Mode

Run Mem0 locally with your own LLM, embedder, and vector store. This is the in-process SDK mode. To instead connect to a Mem0 server you run via Docker, see [Self-Hosted Dashboard (Server) Mode](#self-hosted-dashboard-server-mode) above.

### Interactive Setup

```bash
hermes memory setup
# Select "mem0" → "Open Source (self-hosted)"
# Follow prompts for LLM, embedder, and vector store
```

### Agent-Driven Setup (Flags)

```bash
hermes memory setup mem0 --mode oss \
  --oss-llm openai --oss-llm-key sk-... \
  --oss-vector qdrant
```

### Supported Providers

| Component | Providers |
|-----------|-----------|
| LLM | openai, ollama |
| Embedder | openai, ollama |
| Vector Store | qdrant (local/server), pgvector |

### Flags Reference

| Flag | Description |
|------|-------------|
| `--mode` | `platform` or `oss` |
| `--oss-llm` | LLM provider (default: openai) |
| `--oss-llm-key` | LLM API key |
| `--oss-embedder` | Embedder provider (default: openai) |
| `--oss-vector` | Vector store (default: qdrant) |
| `--oss-vector-path` | Qdrant local path |
| `--user-id` | User identifier |

## Switching Modes

### Platform to OSS

```bash
hermes memory setup mem0 --mode oss --oss-llm-key sk-...
```

Or edit `$HERMES_HOME/mem0.json` directly:
```json
{
  "mode": "oss",
  "oss": {
    "llm": {"provider": "openai", "config": {"model": "gpt-5-mini"}},
    "embedder": {"provider": "openai", "config": {"model": "text-embedding-3-small"}},
    "vector_store": {"provider": "qdrant", "config": {"path": "~/.hermes/mem0_qdrant"}}
  }
}
```

### OSS to Platform

```bash
hermes memory setup mem0 --mode platform --api-key sk-...
```

### Dry Run (preview without writing)

```bash
hermes memory setup mem0 --mode oss --oss-llm-key sk-... --dry-run
```

## Tools

| Tool | Description |
|------|-------------|
| `mem0_search` | Semantic search by meaning |
| `mem0_add` | Store a fact verbatim (no LLM extraction) |
| `mem0_update` | Update a memory's text by ID |
| `mem0_delete` | Delete a memory by ID |

## Troubleshooting

### "Mem0 temporarily unavailable"

Circuit breaker tripped after 5 consecutive failures. Resets after 2 minutes.

- **Platform mode**: Check API key and internet connectivity.
- **OSS mode**: Check that your vector store (qdrant/pgvector) is running.

### OSS: Qdrant connection refused

```bash
# If using local Qdrant, check the storage path is writable:
ls -la ~/.hermes/mem0_qdrant

# If using Qdrant server, check it's reachable:
curl http://localhost:6333/healthz
```

### OSS: PGVector connection refused

```bash
# Verify PostgreSQL is running and accepting connections:
pg_isready -h localhost -p 5432
```

### Workbench: Docker Model Runner not reachable

```bash
# From the host (Docker Desktop TCP access must be enabled):
curl http://localhost:12434/api/tags
curl http://localhost:12434/engines/v1/models

# Both exact models must appear before authenticated memory setup succeeds:
docker model pull ai/qwen3:4B-UD-Q4_K_XL
docker model pull ai/nomic-embed-text-v1.5
docker model list
```

The pull and request references above are acquisition/request identifiers. DMR
exposes registry-qualified inventory names through `/api/tags` and
`/engines/v1/models`. Workbench persists both identities and the independently
host-verified approved digests. It intentionally does not compare the chat
response's `model` field because Server Engine reports an internal bundle path.

### Memories not appearing

- `mem0_add` stores verbatim (no extraction). Use `sync_turn` for LLM extraction.
- Search uses semantic matching — try broader queries.
- Check `user_id` matches between sessions (`$HERMES_HOME/mem0.json`).
