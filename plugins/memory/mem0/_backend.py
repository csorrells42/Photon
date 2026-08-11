"""Backend abstraction for Mem0 Platform and OSS modes."""

from __future__ import annotations

import hashlib
import json
import os
import urllib.request
import uuid
from abc import ABC, abstractmethod
from pathlib import Path
from typing import Any

from ._oss_providers import (
    WORKBENCH_DMR_EMBEDDER_DIGEST,
    WORKBENCH_DMR_EMBEDDER_INVENTORY_ID,
    WORKBENCH_DMR_EMBEDDER_PULL_ID,
    WORKBENCH_DMR_LLM_DIGEST,
    WORKBENCH_DMR_LLM_INVENTORY_ID,
    WORKBENCH_DMR_LLM_PULL_ID,
    WORKBENCH_QDRANT_COLLECTION,
)


class OwnerFilteredMutationUnsupported(RuntimeError):
    """Backend cannot atomically bind a raw memory id to its owner."""


class Mem0Backend(ABC):
    """Unified interface over Platform (MemoryClient) and OSS (Memory) backends."""

    supports_owner_filtered_update = False
    supports_owner_filtered_delete = False

    @abstractmethod
    def search(self, query: str, *, filters: dict, top_k: int = 10, rerank: bool = False) -> list[dict]:
        ...

    @abstractmethod
    def add(
        self,
        messages: list,
        *,
        user_id: str,
        agent_id: str,
        infer: bool = False,
        metadata: dict | None = None,
    ) -> dict:
        ...

    @abstractmethod
    def update(self, memory_id: str, text: str) -> dict:
        ...

    @abstractmethod
    def delete(self, memory_id: str) -> dict:
        ...

    def update_owned(self, memory_id: str, text: str, principal_id: str) -> dict:
        raise OwnerFilteredMutationUnsupported(
            "This backend cannot atomically verify memory ownership during update"
        )

    def delete_owned(self, memory_id: str, principal_id: str) -> dict:
        raise OwnerFilteredMutationUnsupported(
            "This backend cannot atomically verify memory ownership during delete"
        )

    supports_logical_records = False

    def list_owned(self, principal_id: str) -> list[dict]:
        raise OwnerFilteredMutationUnsupported(
            "This backend cannot enumerate logical records by owner"
        )

    def delete_all_owned(self, principal_id: str) -> None:
        raise OwnerFilteredMutationUnsupported(
            "This backend cannot atomically delete all records for one owner"
        )

    def close(self) -> None:
        pass


def _unwrap_results(response: Any) -> list:
    """Normalize API response — extract results list from dict or pass through."""
    if isinstance(response, dict):
        return response.get("results", [])
    if isinstance(response, list):
        return response
    return []


class PlatformBackend(Mem0Backend):
    """Wraps mem0.MemoryClient for Mem0 Platform (cloud API)."""

    def __init__(self, api_key: str):
        from mem0 import MemoryClient
        self._client = MemoryClient(api_key=api_key)

    def search(self, query: str, *, filters: dict, top_k: int = 10, rerank: bool = False) -> list[dict]:
        response = self._client.search(query, filters=filters, top_k=top_k, rerank=rerank)
        return _unwrap_results(response)

    def add(
        self,
        messages: list,
        *,
        user_id: str,
        agent_id: str,
        infer: bool = False,
        metadata: dict | None = None,
    ) -> dict:
        kwargs: dict[str, Any] = {"user_id": user_id, "agent_id": agent_id, "infer": infer}
        if metadata:
            kwargs["metadata"] = metadata
        return self._client.add(messages, **kwargs)

    def update(self, memory_id: str, text: str) -> dict:
        self._client.update(memory_id=memory_id, text=text)
        return {"result": "Memory updated.", "memory_id": memory_id}

    def delete(self, memory_id: str) -> dict:
        self._client.delete(memory_id=memory_id)
        return {"result": "Memory deleted.", "memory_id": memory_id}


class SelfHostedBackend(Mem0Backend):
    """Direct HTTP backend for a self-hosted Mem0 server (the FastAPI ``server/``).

    mem0.MemoryClient can't be reused for self-hosted: it is hardwired to the
    cloud API — ``Authorization: Token`` auth and a ``GET /v1/ping/`` validation
    call in ``__init__`` that the self-hosted server does not expose (it would
    404 before any real request). This client talks to that server directly,
    using its actual contract: ``X-API-Key`` auth and the ``/memories`` /
    ``/search`` routes.
    """

    def __init__(self, api_key: str, host: str, transport=None):
        import httpx

        headers = {"Content-Type": "application/json"}
        if api_key:
            headers["X-API-Key"] = api_key  # omitted only for AUTH_DISABLED servers
        # Connect-level retries smooth over transient blips so a single
        # dropped SYN doesn't count toward the provider failure breaker.
        # ``transport`` is injectable for tests (httpx.MockTransport).
        if transport is None:
            transport = httpx.HTTPTransport(retries=2)
        self._client = httpx.Client(
            base_url=host.rstrip("/"), headers=headers, timeout=30.0,
            transport=transport,
        )

    def _json(self, method: str, path: str, **kwargs) -> Any:
        resp = self._client.request(method, path, **kwargs)
        resp.raise_for_status()
        return resp.json() if resp.content else {}

    def search(self, query: str, *, filters: dict, top_k: int = 10, rerank: bool = False) -> list[dict]:
        # rerank is a platform-only feature; the self-hosted /search ignores it.
        body: dict[str, Any] = {"query": query, "top_k": top_k}
        if filters:
            body["filters"] = filters  # user_id belongs in filters (top-level is deprecated)
        return _unwrap_results(self._json("POST", "/search", json=body))

    def add(
        self,
        messages: list,
        *,
        user_id: str,
        agent_id: str,
        infer: bool = False,
        metadata: dict | None = None,
    ) -> dict:
        body: dict[str, Any] = {
            "messages": messages,
            "user_id": user_id,
            "agent_id": agent_id,
            "infer": infer,
        }
        if metadata:
            body["metadata"] = metadata
        return self._json("POST", "/memories", json=body)

    def update(self, memory_id: str, text: str) -> dict:
        self._json("PUT", f"/memories/{memory_id}", json={"text": text})
        return {"result": "Memory updated.", "memory_id": memory_id}

    def delete(self, memory_id: str) -> dict:
        self._json("DELETE", f"/memories/{memory_id}")
        return {"result": "Memory deleted.", "memory_id": memory_id}

    def close(self) -> None:
        try:
            self._client.close()
        except Exception:
            pass


class OSSBackend(Mem0Backend):
    """Wraps mem0.Memory for self-hosted (OSS) mode."""

    def __init__(self, oss_config: dict):
        from mem0 import Memory

        def _provider_block(name: str) -> dict:
            block = dict(oss_config[name])
            provider = str(block.get("provider") or "").strip().lower()
            provider_config = dict(block.get("config", {}))
            # Workbench provenance fields are validated by this wrapper and
            # are not part of Mem0's Ollama provider schema.
            provider_config.pop("model_digest", None)
            legacy_base = provider_config.pop("api_base", None)
            if legacy_base:
                from ._oss_providers import EMBEDDER_PROVIDERS, LLM_PROVIDERS

                provider_def = (
                    LLM_PROVIDERS if name == "llm" else EMBEDDER_PROVIDERS
                ).get(provider, {})
                canonical_key = provider_def.get("base_url_key")
                if canonical_key:
                    provider_config.setdefault(canonical_key, legacy_base)
            block["config"] = provider_config
            return block

        vector_store = dict(oss_config["vector_store"])
        vs_config = dict(vector_store.get("config", {}))

        if "path" in vs_config:
            vs_config["path"] = os.path.expanduser(vs_config["path"])

        embedder_config = oss_config.get("embedder", {}).get("config", {})
        if os.environ.get("HERMES_WORKBENCH_AUTHENTICATED_MEM0") == "1":
            from ._setup import _workbench_model_url, _workbench_qdrant_url

            embedding_identity = oss_config.get("embedding_identity", {})
            vector_url = str(vs_config.get("url") or "").rstrip("/")
            expected_vector_url = _workbench_qdrant_url()
            if (
                embedding_identity.get("version") != 3
                or embedding_identity.get("provider") != "lmstudio"
                or embedding_identity.get("model") != WORKBENCH_DMR_EMBEDDER_PULL_ID
                or embedding_identity.get("inventory_model")
                != WORKBENCH_DMR_EMBEDDER_INVENTORY_ID
                or embedding_identity.get("model_digest")
                != WORKBENCH_DMR_EMBEDDER_DIGEST
                or embedding_identity.get("vector_provider") != "qdrant"
                or embedding_identity.get("vector_url") != expected_vector_url
                or embedding_identity.get("collection") != WORKBENCH_QDRANT_COLLECTION
                or embedding_identity.get("dimensions")
                != embedder_config.get("embedding_dims")
                or embedding_identity.get("distance") != "cosine"
            ):
                raise RuntimeError(
                    "Authenticated Workbench Mem0 embedding identity does not match the approved DMR artifact"
                )
            if (
                str(vector_store.get("provider") or "").lower() != "qdrant"
                or "path" in vs_config
                or vector_url != expected_vector_url
                or vs_config.get("collection_name") != WORKBENCH_QDRANT_COLLECTION
                or vs_config.get("api_key")
            ):
                raise RuntimeError(
                    "Authenticated Workbench Mem0 requires its exact internal Qdrant service identity"
                )
            model_url = str(embedding_identity.get("model_url") or "").rstrip("/")
            llm_url = str(
                oss_config.get("llm", {}).get("config", {}).get("lmstudio_base_url") or ""
            ).rstrip("/")
            embedder_url = str(embedder_config.get("lmstudio_base_url") or "").rstrip("/")
            if (
                not model_url
                or model_url != _workbench_model_url()
                or llm_url != model_url
                or embedder_url != model_url
            ):
                raise RuntimeError(
                    "Authenticated Workbench Mem0 model endpoint does not match its persisted identity"
                )
            self._validate_dmr_model_pin(oss_config.get("llm", {}), "LLM")
            self._validate_dmr_model_pin(oss_config.get("embedder", {}), "embedder")
        dims = embedder_config.get("embedding_dims")
        if not dims:
            from ._oss_providers import KNOWN_DIMS
            model = embedder_config.get("model", "")
            dims = KNOWN_DIMS.get(model)
        if dims:
            vs_config["embedding_model_dims"] = dims
            embedding_identity = oss_config.get("embedding_identity", {})
            if not embedding_identity and os.environ.get("HERMES_WORKBENCH_AUTHENTICATED_MEM0") != "1":
                embedding_identity = {
                    "provider": str(oss_config.get("embedder", {}).get("provider") or "legacy"),
                    "model": str(embedder_config.get("model") or "legacy"),
                }
            self._validate_collection_identity(
                vector_store.get("provider", "qdrant"),
                vs_config,
                dims,
                embedding_identity,
            )

        vector_store["config"] = vs_config

        config = {
            "vector_store": vector_store,
            "llm": _provider_block("llm"),
            "embedder": _provider_block("embedder"),
            "version": "v1.1",
        }
        self._memory = Memory.from_config(config)
        store = getattr(self._memory, "vector_store", None)
        self.supports_owner_filtered_delete = bool(
            str(vector_store.get("provider") or "").lower() == "qdrant"
            and store is not None
            and getattr(store, "client", None) is not None
            and getattr(store, "collection_name", None)
        )

    @staticmethod
    def _validate_dmr_model_pin(block: dict, label: str) -> None:
        if str(block.get("provider") or "") != "lmstudio":
            raise RuntimeError(
                f"Authenticated Workbench Mem0 {label} must use its local DMR OpenAI-compatible adapter"
            )
        config = block.get("config", {})
        model = str(config.get("model") or "")
        digest = str(config.get("model_digest") or "")
        url = str(config.get("lmstudio_base_url") or "").rstrip("/")
        api_key = str(config.get("api_key") or "")
        expected_model = (
            WORKBENCH_DMR_LLM_PULL_ID
            if label == "LLM"
            else WORKBENCH_DMR_EMBEDDER_PULL_ID
        )
        inventory_model = (
            WORKBENCH_DMR_LLM_INVENTORY_ID
            if label == "LLM"
            else WORKBENCH_DMR_EMBEDDER_INVENTORY_ID
        )
        expected_digest = (
            WORKBENCH_DMR_LLM_DIGEST
            if label == "LLM"
            else WORKBENCH_DMR_EMBEDDER_DIGEST
        )
        if (
            model != expected_model
            or digest != expected_digest
            or not url
            or api_key != "not-needed"
        ):
            raise RuntimeError(
                f"Authenticated Workbench Mem0 {label} Docker model pin is incomplete"
            )
        try:
            request = urllib.request.Request(f"{url}/models", method="GET")
            with urllib.request.urlopen(request, timeout=5) as response:
                payload = json.loads(response.read())
        except Exception as exc:
            raise RuntimeError(f"Could not verify Docker Model Runner {label} model: {exc}") from exc
        for entry in payload.get("data", []):
            if (
                isinstance(entry, dict)
                and str(entry.get("id") or "") == inventory_model
            ):
                return
        raise RuntimeError(
            f"Docker Model Runner {label} inventory changed; rerun setup and explicitly rebuild or migrate memory"
        )

    @staticmethod
    def _validate_collection_identity(
        provider: str,
        vs_config: dict,
        expected_dims: int,
        declared_identity: dict,
    ) -> None:
        """Fail closed on embedding/collection drift; never delete user data."""
        collection_name = vs_config.get("collection_name", "mem0")
        strict_workbench = (
            os.environ.get("HERMES_WORKBENCH_AUTHENTICATED_MEM0") == "1"
        )
        expected = {
            "version": int(declared_identity.get("version") or 1),
            "provider": str(declared_identity.get("provider") or ""),
            "model": str(declared_identity.get("model") or ""),
            "dimensions": int(expected_dims),
            "vector_provider": str(provider),
            "collection": str(collection_name),
        }
        if strict_workbench:
            expected.update({
                "inventory_model": str(declared_identity.get("inventory_model") or ""),
                "model_url": str(declared_identity.get("model_url") or ""),
                "model_digest": str(declared_identity.get("model_digest") or ""),
                "vector_url": str(declared_identity.get("vector_url") or ""),
                "distance": str(declared_identity.get("distance") or ""),
            })
        if (
            not expected["provider"]
            or not expected["model"]
            or (
                strict_workbench
                and not expected.get("model_digest")
            )
            or (
                strict_workbench
                and not expected.get("model_url")
            )
            or (
                strict_workbench
                and not expected.get("vector_url")
            )
            or (
                strict_workbench
                and expected.get("distance") != "cosine"
            )
            or (
                strict_workbench
                and (
                    expected["version"] != 3
                    or not expected.get("inventory_model")
                )
            )
        ):
            raise RuntimeError(
                "Mem0 embedding identity is missing; rerun authenticated memory setup"
            )

        # Bind model as well as dimensions.  Two models can share a vector
        # width while producing incompatible spaces, so a dimension-only check
        # would silently poison recall after a future config edit.
        target = str(vs_config.get("path") or vs_config.get("url") or provider)
        if strict_workbench:
            target = f"{target}#{collection_name}"
        target_hash = hashlib.sha256(target.encode("utf-8")).hexdigest()
        from hermes_constants import get_hermes_home

        identity_path = Path(get_hermes_home()) / "mem0_vector_identities" / f"{target_hash}.json"
        if identity_path.exists():
            stored = json.loads(identity_path.read_text(encoding="utf-8"))
            if stored != expected:
                raise RuntimeError(
                    "Mem0 embedding identity changed; explicit memory rebuild or migration is required"
                )

        if provider == "qdrant":
            try:
                path = vs_config.get("path")
                url = vs_config.get("url")
                if not path and not url:
                    return
                from qdrant_client import QdrantClient
                if path:
                    client = QdrantClient(path=path)
                elif url:
                    client = QdrantClient(url=url, api_key=vs_config.get("api_key"))
                try:
                    if client.collection_exists(collection_name):
                        info = client.get_collection(collection_name)
                        vectors = info.config.params.vectors
                        # Named-vector collections expose a dict; unnamed expose an object with .size.
                        if isinstance(vectors, dict):
                            if len(vectors) != 1:
                                raise RuntimeError(
                                    "Mem0 Qdrant vector layout changed; explicit memory rebuild or migration is required"
                                )
                            first = next(iter(vectors.values()), None)
                            current_dims = first.size if first else None
                            current_distance = getattr(first, "distance", None) if first else None
                        else:
                            current_dims = getattr(vectors, "size", None)
                            current_distance = getattr(vectors, "distance", None)
                        if current_dims is None or current_dims != expected_dims:
                            raise RuntimeError(
                                "Mem0 Qdrant dimensions changed; explicit memory rebuild or migration is required"
                            )
                        normalized_distance = str(
                            getattr(current_distance, "value", current_distance) or ""
                        ).lower()
                        if normalized_distance != "cosine":
                            raise RuntimeError(
                                "Mem0 Qdrant distance changed; explicit memory rebuild or migration is required"
                            )
                finally:
                    client.close()
            except RuntimeError:
                raise
            except Exception as exc:
                if strict_workbench:
                    raise RuntimeError(
                        "Could not validate authenticated Workbench Qdrant service"
                    ) from exc
                raise RuntimeError(f"Could not validate Mem0 Qdrant collection identity: {exc}") from exc
        elif provider == "pgvector":
            try:
                import psycopg2
                from psycopg2 import sql as pgsql
                conn_params = {}
                for k in ("host", "port", "user", "password", "dbname"):
                    if vs_config.get(k):
                        conn_params[k] = vs_config[k]
                if vs_config.get("sslmode"):
                    conn_params["sslmode"] = vs_config["sslmode"]
                conn = psycopg2.connect(**conn_params)
                conn.autocommit = True
                try:
                    cur = conn.cursor()
                    try:
                        cur.execute(
                            "SELECT atttypmod FROM pg_attribute "
                            "WHERE attrelid = %s::regclass AND attname = 'vector'",
                            (collection_name,),
                        )
                        row = cur.fetchone()
                        if row and row[0] > 0 and row[0] != expected_dims:
                            raise RuntimeError(
                                "Mem0 pgvector dimensions changed; explicit memory rebuild or migration is required"
                            )
                    finally:
                        cur.close()
                finally:
                    conn.close()
            except RuntimeError:
                raise
            except Exception as exc:
                raise RuntimeError(f"Could not validate Mem0 pgvector identity: {exc}") from exc

        if not identity_path.exists():
            from utils import atomic_json_write

            identity_path.parent.mkdir(parents=True, exist_ok=True)
            atomic_json_write(identity_path, expected, mode=0o600)

    def search(self, query: str, *, filters: dict, top_k: int = 10, rerank: bool = False) -> list[dict]:
        response = self._memory.search(query, filters=filters, top_k=top_k)
        return _unwrap_results(response)

    def add(
        self,
        messages: list,
        *,
        user_id: str,
        agent_id: str,
        infer: bool = False,
        metadata: dict | None = None,
    ) -> dict:
        kwargs: dict[str, Any] = {"user_id": user_id, "agent_id": agent_id, "infer": infer}
        if metadata:
            kwargs["metadata"] = metadata
        return self._memory.add(messages, **kwargs)

    def update(self, memory_id: str, text: str) -> dict:
        self._memory.update(memory_id, data=text)
        return {"result": "Memory updated.", "memory_id": memory_id}

    def delete(self, memory_id: str) -> dict:
        self._memory.delete(memory_id)
        return {"result": "Memory deleted.", "memory_id": memory_id}

    def delete_owned(self, memory_id: str, principal_id: str) -> dict:
        """Atomically delete one exact Qdrant point only when its owner matches."""
        try:
            canonical_id = str(uuid.UUID(str(memory_id)))
        except (TypeError, ValueError, AttributeError) as exc:
            raise ValueError("memory_id must be a canonical UUID") from exc
        if str(memory_id) != canonical_id:
            raise ValueError("memory_id must be a canonical UUID")
        principal = str(principal_id or "")
        if not principal or len(principal) > 256 or "\0" in principal:
            raise ValueError("principal_id is invalid")
        if not self.supports_owner_filtered_delete:
            raise OwnerFilteredMutationUnsupported(
                "This backend cannot atomically verify memory ownership during delete"
            )

        from qdrant_client import models

        store = self._memory.vector_store
        selector = models.FilterSelector(filter=models.Filter(must=[
            models.HasIdCondition(has_id=[canonical_id]),
            models.FieldCondition(
                key="user_id",
                match=models.MatchValue(value=principal),
            ),
        ]))
        store.client.delete(
            collection_name=store.collection_name,
            points_selector=selector,
            wait=True,
        )
        return {
            "result": "Owned memory deletion applied.",
            "memory_id": canonical_id,
        }

    supports_logical_records = True

    def list_owned(self, principal_id: str) -> list[dict]:
        return _unwrap_results(self._memory.get_all(user_id=principal_id))

    def delete_all_owned(self, principal_id: str) -> None:
        # Mem0 OSS applies the user predicate in the storage operation itself;
        # no raw record id is treated as authority.
        self._memory.delete_all(user_id=principal_id)

    def close(self):
        try:
            telemetry = getattr(self._memory, "telemetry", None)
            if telemetry and hasattr(telemetry, "posthog"):
                try:
                    telemetry.posthog.shutdown()
                except Exception:
                    pass
            if hasattr(self._memory, "close"):
                self._memory.close()
            vs = getattr(self._memory, "vector_store", None)
            if vs and hasattr(vs, "close"):
                vs.close()
            client = getattr(vs, "client", None)
            if client and hasattr(client, "close"):
                client.close()
        except Exception:
            pass
    supports_owner_filtered_update = False
