"""OSS provider definitions for LLM, embedder, and vector store."""

from __future__ import annotations

import os
from typing import Any

from hermes_constants import get_hermes_home

WORKBENCH_DMR_DEFAULT_URL = "http://host.docker.internal:12434"
WORKBENCH_QDRANT_DEFAULT_URL = "http://memory-vector:6333"
WORKBENCH_QDRANT_COLLECTION = "hermes_workbench_mem0_v3"
WORKBENCH_DMR_LLM_PULL_ID = "ai/qwen3:4B-UD-Q4_K_XL"
WORKBENCH_DMR_EMBEDDER_PULL_ID = "ai/nomic-embed-text-v1.5"
WORKBENCH_DMR_LLM_INVENTORY_ID = "docker.io/ai/qwen3:4B-UD-Q4_K_XL"
WORKBENCH_DMR_LLM_DIGEST = (
    "sha256:d6bb9d7293698b06da1eb40008cdf5944bd6125f437b34ff1a7d535f5e868e80"
)
WORKBENCH_DMR_EMBEDDER_INVENTORY_ID = "docker.io/ai/nomic-embed-text-v1.5:latest"
WORKBENCH_DMR_EMBEDDER_DIGEST = (
    "sha256:653017dd060f5cd345118ff90382ceb213d383de2887820d2f303893d32ef40d"
)

LLM_PROVIDERS: dict[str, dict[str, Any]] = {
    "openai": {
        "label": "OpenAI",
        "needs_key": True,
        "env_var": "OPENAI_API_KEY",
        "default_model": "gpt-5-mini",
        "base_url_key": "openai_base_url",
    },
    "ollama": {
        "label": "Ollama (local)",
        "needs_key": False,
        "default_model": "llama3.1:8b",
        "default_url": "http://localhost:11434",
        "base_url_key": "ollama_base_url",
        "pip_dep": "ollama",
    },
    # In authenticated Workbench mode this Mem0 adapter is used strictly as a
    # generic local OpenAI-compatible transport to Docker Model Runner. It
    # creates no LM Studio runtime or configuration dependency.
    "lmstudio": {
        "label": "Docker Model Runner (local OpenAI-compatible)",
        "needs_key": False,
        "default_model": WORKBENCH_DMR_LLM_PULL_ID,
        "default_url": f"{WORKBENCH_DMR_DEFAULT_URL}/engines/v1",
        "base_url_key": "lmstudio_base_url",
        "pip_dep": "openai",
    },
}

EMBEDDER_PROVIDERS: dict[str, dict[str, Any]] = {
    "openai": {
        "label": "OpenAI",
        "needs_key": True,
        "env_var": "OPENAI_API_KEY",
        "default_model": "text-embedding-3-small",
        "base_url_key": "openai_base_url",
        "dims": 1536,
    },
    "ollama": {
        "label": "Ollama (local)",
        "needs_key": False,
        "default_model": "nomic-embed-text",
        "default_url": "http://localhost:11434",
        "base_url_key": "ollama_base_url",
        "dims": 768,
        "pip_dep": "ollama",
    },
    "lmstudio": {
        "label": "Docker Model Runner (local OpenAI-compatible)",
        "needs_key": False,
        "default_model": WORKBENCH_DMR_EMBEDDER_PULL_ID,
        "default_url": f"{WORKBENCH_DMR_DEFAULT_URL}/engines/v1",
        "base_url_key": "lmstudio_base_url",
        "dims": 768,
        "pip_dep": "openai",
    },
}

VECTOR_PROVIDERS: dict[str, dict[str, Any]] = {
    "qdrant": {
        "label": "Qdrant",
        # HERMES_HOME is /opt/data in the supported container layout, so this
        # persists across image recreation without a second volume or raw
        # Qdrant-directory portability contract.
        "default_config": {"path": str(get_hermes_home() / "mem0_qdrant")},
        "pip_dep": "qdrant-client",
    },
    "pgvector": {
        "label": "PGVector",
        "default_config": {"host": "localhost", "port": 5432, "user": os.getenv("USER", "postgres"), "dbname": "postgres"},
        "pip_dep": "psycopg2-binary",
    },
}

KNOWN_DIMS: dict[str, int] = {
    "text-embedding-3-small": 1536,
    "text-embedding-3-large": 3072,
    "text-embedding-ada-002": 1536,
    "nomic-embed-text": 768,
    "nomic-embed-text:v1.5": 768,
    WORKBENCH_DMR_EMBEDDER_PULL_ID: 768,
}


def validate_oss_config(oss_config: dict) -> list[str]:
    """Validate an OSS config dict. Returns list of error strings (empty = valid)."""
    errors: list[str] = []

    for section, registry in [("llm", LLM_PROVIDERS), ("embedder", EMBEDDER_PROVIDERS),
                               ("vector_store", VECTOR_PROVIDERS)]:
        block = oss_config.get(section)
        if not block or not isinstance(block, dict):
            errors.append(f"Missing required section: {section}")
            continue
        provider_id = block.get("provider", "")
        if provider_id not in registry:
            valid = ", ".join(registry.keys())
            errors.append(f"Unknown {section} provider '{provider_id}'. Valid: {valid}")

    vs = oss_config.get("vector_store", {})
    if vs.get("provider") == "pgvector":
        cfg = vs.get("config", {})
        if not cfg.get("user"):
            errors.append("PGVector requires 'user' in vector_store.config")

    return errors
