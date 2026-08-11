"""Tests for Mem0Backend abstraction — PlatformBackend, OSSBackend, SelfHostedBackend."""

import copy
import json
import pytest

from plugins.memory.mem0._backend import (
    Mem0Backend,
    PlatformBackend,
    OSSBackend,
    SelfHostedBackend,
)


def test_workbench_dmr_model_pin_requires_request_inventory_and_digest(monkeypatch):
    monkeypatch.setenv("OPENROUTER_API_KEY", "must-not-redirect-dmr")
    payload = json.dumps({"data": [
        {
            "id": "docker.io/ai/qwen3:4B-UD-Q4_K_XL",
        },
    ]}).encode()

    class _Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return payload

    monkeypatch.setattr(
        "plugins.memory.mem0._backend.urllib.request.urlopen",
        lambda *_args, **_kwargs: _Response(),
    )
    block = {
        "provider": "lmstudio",
        "config": {
            "model": "ai/qwen3:4B-UD-Q4_K_XL",
            "model_digest": "sha256:d6bb9d7293698b06da1eb40008cdf5944bd6125f437b34ff1a7d535f5e868e80",
            "lmstudio_base_url": "http://host.docker.internal:12434/engines/v1",
            "api_key": "not-needed",
        },
    }
    OSSBackend._validate_dmr_model_pin(block, "LLM")

    block["config"]["model"] = "ai/qwen3:latest"
    with pytest.raises(RuntimeError, match="Docker model pin is incomplete"):
        OSSBackend._validate_dmr_model_pin(block, "LLM")


class FakePlatformClient:
    """Fake MemoryClient for PlatformBackend tests."""

    def __init__(self):
        self.calls = []

    def search(self, query, **kwargs):
        self.calls.append(("search", query, kwargs))
        return {"results": [{"id": "m1", "memory": "fact1", "score": 0.9}]}

    def get_all(self, **kwargs):
        self.calls.append(("get_all", kwargs))
        return {"count": 1, "next": None, "results": [{"id": "m1", "memory": "fact1"}]}

    def add(self, messages, **kwargs):
        self.calls.append(("add", messages, kwargs))
        return {"status": "PENDING", "event_id": "evt-1"}

    def update(self, **kwargs):
        self.calls.append(("update", kwargs))
        return {"id": kwargs["memory_id"], "text": kwargs["text"]}

    def delete(self, **kwargs):
        self.calls.append(("delete", kwargs))


class TestPlatformBackend:

    def _make(self):
        client = FakePlatformClient()
        backend = PlatformBackend.__new__(PlatformBackend)
        backend._client = client
        return backend, client

    def test_search_forwards_params(self):
        backend, client = self._make()
        result = backend.search("test query", filters={"user_id": "u1"}, top_k=5)
        assert client.calls[0][0] == "search"
        assert client.calls[0][1] == "test query"
        assert client.calls[0][2]["filters"] == {"user_id": "u1"}
        assert client.calls[0][2]["top_k"] == 5


    def test_add_forwards_kwargs(self):
        backend, client = self._make()
        msgs = [{"role": "user", "content": "hi"}]
        result = backend.add(msgs, user_id="u1", agent_id="hermes", infer=False)
        call = client.calls[0]
        assert call[2]["user_id"] == "u1"
        assert call[2]["infer"] is False
        # metadata kwarg should be omitted entirely when not provided so we
        # don't surprise older mem0 client versions with an unknown kwarg.
        assert "metadata" not in call[2]


    def test_update_forwards(self):
        backend, client = self._make()
        backend.update("m1", "new text")
        assert client.calls[0][1] == {"memory_id": "m1", "text": "new text"}

    def test_delete_forwards(self):
        backend, client = self._make()
        backend.delete("m1")
        assert client.calls[0][1] == {"memory_id": "m1"}


class FakeOSSMemory:
    """Fake mem0.Memory for OSSBackend tests."""

    def __init__(self):
        self.calls = []

    def search(self, query, **kwargs):
        self.calls.append(("search", query, kwargs))
        return {"results": [{"id": "m1", "memory": "fact1", "score": 0.8}]}

    def get_all(self, **kwargs):
        self.calls.append(("get_all", kwargs))
        return {"results": [{"id": "m1", "memory": "fact1"}]}

    def add(self, messages, **kwargs):
        self.calls.append(("add", messages, kwargs))
        return {"results": [{"id": "m1", "memory": "fact1", "event": "ADD"}]}

    def update(self, memory_id, **kwargs):
        self.calls.append(("update", memory_id, kwargs))
        return {"message": "Memory updated successfully!"}

    def delete(self, memory_id):
        self.calls.append(("delete", memory_id))
        return {"message": "Memory deleted successfully!"}


class TestOSSBackend:

    def _make(self):
        memory = FakeOSSMemory()
        backend = OSSBackend.__new__(OSSBackend)
        backend._memory = memory
        return backend, memory


    def test_legacy_api_base_aliases_are_normalized_before_mem0_init(self, monkeypatch):
        import sys
        import types

        captured = {}

        class Memory:
            @staticmethod
            def from_config(config):
                captured.update(config)
                return FakeOSSMemory()

        # OSSBackend.__init__ does `from mem0 import Memory`. mem0 is a lazy
        # optional dep absent from CI's env, so inject a stub module rather
        # than importing the real package (which would ModuleNotFoundError).
        stub_mem0 = types.ModuleType("mem0")
        stub_mem0.Memory = Memory  # type: ignore[attr-defined]
        monkeypatch.setitem(sys.modules, "mem0", stub_mem0)
        raw = {
            "llm": {
                "provider": "openai",
                "config": {"model": "gpt-5-mini", "api_base": "https://llm.example/v1"},
            },
            "embedder": {
                "provider": "ollama",
                "config": {"model": "nomic-embed-text", "api_base": "http://ollama:11434"},
            },
            "vector_store": {"provider": "qdrant", "config": {}},
        }
        before = copy.deepcopy(raw)

        OSSBackend(raw)

        assert captured["llm"]["config"]["openai_base_url"] == "https://llm.example/v1"
        assert captured["embedder"]["config"]["ollama_base_url"] == "http://ollama:11434"
        assert "api_base" not in captured["llm"]["config"]
        assert "api_base" not in captured["embedder"]["config"]
        assert raw == before

    @staticmethod
    def _strict_workbench_config():
        vector_url = "http://memory-vector:6333"
        collection = "hermes_workbench_mem0_v3"
        return {
            "llm": {
                "provider": "lmstudio",
                "config": {
                    "model": "ai/qwen3:4B-UD-Q4_K_XL",
                    "model_digest": "sha256:d6bb9d7293698b06da1eb40008cdf5944bd6125f437b34ff1a7d535f5e868e80",
                    "lmstudio_base_url": "http://host.docker.internal:12434/engines/v1",
                    "api_key": "not-needed",
                },
            },
            "embedder": {
                "provider": "lmstudio",
                "config": {
                    "model": "ai/nomic-embed-text-v1.5",
                    "model_digest": "sha256:653017dd060f5cd345118ff90382ceb213d383de2887820d2f303893d32ef40d",
                    "lmstudio_base_url": "http://host.docker.internal:12434/engines/v1",
                    "api_key": "not-needed",
                    "embedding_dims": 768,
                },
            },
            "vector_store": {
                "provider": "qdrant",
                "config": {"url": vector_url, "collection_name": collection},
            },
            "embedding_identity": {
                "version": 3,
                "provider": "lmstudio",
                "model": "ai/nomic-embed-text-v1.5",
                "inventory_model": "docker.io/ai/nomic-embed-text-v1.5:latest",
                "model_digest": "sha256:653017dd060f5cd345118ff90382ceb213d383de2887820d2f303893d32ef40d",
                "model_url": "http://host.docker.internal:12434/engines/v1",
                "dimensions": 768,
                "vector_provider": "qdrant",
                "vector_url": vector_url,
                "collection": collection,
                "distance": "cosine",
            },
        }

    def test_two_workbench_providers_use_http_qdrant_without_embedded_locking(
        self, monkeypatch, tmp_path
    ):
        import sys
        import types

        qdrant_constructions = []
        memory_configs = []

        class QdrantClient:
            def __init__(self, **kwargs):
                qdrant_constructions.append(kwargs)

            def collection_exists(self, _collection):
                return False

            def close(self):
                return None

        class _Store:
            collection_name = "hermes_workbench_mem0_v3"

            def __init__(self):
                self.client = object()

        class _MemoryInstance(FakeOSSMemory):
            def __init__(self):
                super().__init__()
                self.vector_store = _Store()

        class Memory:
            @staticmethod
            def from_config(config):
                memory_configs.append(config)
                return _MemoryInstance()

        stub_mem0 = types.ModuleType("mem0")
        stub_mem0.Memory = Memory  # type: ignore[attr-defined]
        stub_qdrant = types.ModuleType("qdrant_client")
        stub_qdrant.QdrantClient = QdrantClient  # type: ignore[attr-defined]
        monkeypatch.setitem(sys.modules, "mem0", stub_mem0)
        monkeypatch.setitem(sys.modules, "qdrant_client", stub_qdrant)
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setenv("HERMES_HOME", str(tmp_path))
        monkeypatch.setattr(OSSBackend, "_validate_dmr_model_pin", lambda *_args: None)

        first = OSSBackend(self._strict_workbench_config())
        second = OSSBackend(self._strict_workbench_config())

        assert first is not second
        assert len(memory_configs) == 2
        assert qdrant_constructions == [
            {"url": "http://memory-vector:6333", "api_key": None},
            {"url": "http://memory-vector:6333", "api_key": None},
        ]
        assert all("path" not in config["vector_store"]["config"] for config in memory_configs)

    def test_workbench_qdrant_endpoint_and_collection_drift_fail_before_mem0_init(
        self, monkeypatch
    ):
        import sys
        import types

        calls = []

        class Memory:
            @staticmethod
            def from_config(config):
                calls.append(config)
                return FakeOSSMemory()

        stub_mem0 = types.ModuleType("mem0")
        stub_mem0.Memory = Memory  # type: ignore[attr-defined]
        monkeypatch.setitem(sys.modules, "mem0", stub_mem0)
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setattr(OSSBackend, "_validate_dmr_model_pin", lambda *_args: None)

        wrong_url = self._strict_workbench_config()
        wrong_url["vector_store"]["config"]["url"] = "http://localhost:6333"
        with pytest.raises(RuntimeError, match="exact internal Qdrant"):
            OSSBackend(wrong_url)

        wrong_collection = self._strict_workbench_config()
        wrong_collection["vector_store"]["config"]["collection_name"] = "mem0"
        with pytest.raises(RuntimeError, match="exact internal Qdrant"):
            OSSBackend(wrong_collection)

        embedded = self._strict_workbench_config()
        embedded["vector_store"]["config"] = {"path": "/tmp/qdrant"}
        with pytest.raises(RuntimeError, match="exact internal Qdrant"):
            OSSBackend(embedded)
        assert calls == []

    @pytest.mark.parametrize(
        ("dimensions", "distance", "message"),
        [
            (384, "Cosine", "dimensions changed"),
            (768, "Dot", "distance changed"),
        ],
    )
    def test_existing_workbench_qdrant_vector_drift_fails_closed(
        self, monkeypatch, tmp_path, dimensions, distance, message
    ):
        import sys
        import types
        from types import SimpleNamespace

        class QdrantClient:
            def __init__(self, **_kwargs):
                pass

            def collection_exists(self, _collection):
                return True

            def get_collection(self, _collection):
                vectors = SimpleNamespace(size=dimensions, distance=distance)
                return SimpleNamespace(
                    config=SimpleNamespace(params=SimpleNamespace(vectors=vectors))
                )

            def close(self):
                return None

        stub_qdrant = types.ModuleType("qdrant_client")
        stub_qdrant.QdrantClient = QdrantClient  # type: ignore[attr-defined]
        monkeypatch.setitem(sys.modules, "qdrant_client", stub_qdrant)
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setenv("HERMES_HOME", str(tmp_path))
        config = self._strict_workbench_config()

        with pytest.raises(RuntimeError, match=message):
            OSSBackend._validate_collection_identity(
                "qdrant",
                config["vector_store"]["config"],
                768,
                config["embedding_identity"],
            )

    def test_unavailable_workbench_qdrant_fails_closed_without_embedded_fallback(
        self, monkeypatch, tmp_path
    ):
        import sys
        import types

        class QdrantClient:
            def __init__(self, **kwargs):
                assert kwargs == {
                    "url": "http://memory-vector:6333",
                    "api_key": None,
                }

            def collection_exists(self, _collection):
                raise OSError("service unavailable")

            def close(self):
                return None

        stub_qdrant = types.ModuleType("qdrant_client")
        stub_qdrant.QdrantClient = QdrantClient  # type: ignore[attr-defined]
        monkeypatch.setitem(sys.modules, "qdrant_client", stub_qdrant)
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setenv("HERMES_HOME", str(tmp_path))
        config = self._strict_workbench_config()

        with pytest.raises(RuntimeError, match="authenticated Workbench Qdrant") as caught:
            OSSBackend._validate_collection_identity(
                "qdrant",
                config["vector_store"]["config"],
                768,
                config["embedding_identity"],
            )
        assert "service unavailable" not in str(caught.value)

    def test_delete_owned_uses_one_atomic_id_and_principal_filter(self):
        pytest.importorskip("qdrant_client")

        class _Client:
            def __init__(self):
                self.calls = []

            def delete(self, **kwargs):
                self.calls.append(kwargs)

        class _Store:
            collection_name = "mem0"

            def __init__(self):
                self.client = _Client()

        memory = FakeOSSMemory()
        memory.vector_store = _Store()
        backend = OSSBackend.__new__(OSSBackend)
        backend._memory = memory
        backend.supports_owner_filtered_delete = True
        memory_id = "72a8548f-4841-41ee-b37a-b8c3d736503c"
        principal = "phk2_owner-bound-principal"

        result = backend.delete_owned(memory_id, principal)

        assert result["memory_id"] == memory_id
        assert len(memory.vector_store.client.calls) == 1
        call = memory.vector_store.client.calls[0]
        assert call["collection_name"] == "mem0"
        assert call["wait"] is True
        must = call["points_selector"].filter.must
        assert any(getattr(condition, "has_id", None) == [memory_id] for condition in must)
        assert any(
            getattr(condition, "key", None) == "user_id"
            and getattr(getattr(condition, "match", None), "value", None) == principal
            for condition in must
        )

    def test_delete_owned_rejects_noncanonical_id_before_storage(self):
        backend, _ = self._make()
        backend.supports_owner_filtered_delete = True
        with pytest.raises(ValueError, match="canonical UUID"):
            backend.delete_owned("not-a-uuid", "phk2_owner-bound-principal")


httpx = pytest.importorskip("httpx")


class _StubServer:
    """Records requests and serves the real self-hosted server's response shapes."""

    def __init__(self, rows=10):
        self.requests = []
        self._rows = [{"id": f"m{i}", "memory": f"f{i}"} for i in range(rows)]

    def handler(self, request):
        self.requests.append(request)
        path, method = request.url.path, request.method
        if path == "/search" and method == "POST":
            return httpx.Response(200, json={"results": [{"id": "m1", "memory": "tea", "score": 0.9}]})
        if path == "/memories" and method == "GET":
            top_k = int(request.url.params.get("top_k", len(self._rows)))
            return httpx.Response(200, json={"results": self._rows[:top_k]})
        if path == "/memories" and method == "POST":
            return httpx.Response(200, json={"results": [{"id": "new", "memory": "stored", "event": "ADD"}]})
        if path.startswith("/memories/") and method in ("PUT", "DELETE"):
            if path.endswith("/missing"):  # server 404s unknown ids
                return httpx.Response(404, json={"detail": "Memory not found"})
            verb = "updated" if method == "PUT" else "Memory deleted successfully"
            return httpx.Response(200, json={"message": verb})
        return httpx.Response(404, json={"detail": "not found"})


def _backend(server, api_key="adminkey", host="http://sh:8888"):
    """Build a SelfHostedBackend routed through the stub transport.

    Uses the real __init__ (via the injectable ``transport`` kwarg) so the
    constructor's header/base_url setup is exercised by every test here.
    """
    return SelfHostedBackend(
        api_key, host, transport=httpx.MockTransport(server.handler)
    )


class TestSelfHostedBackend:
    # --- constructor / auth setup (the crux of the bug) -------------------

    def test_init_uses_x_api_key_not_token_auth(self):
        b = SelfHostedBackend("adminkey", "http://sh:8888")
        assert b._client.headers["x-api-key"] == "adminkey"
        assert "authorization" not in b._client.headers  # NOT the cloud 'Token' scheme


    # --- search ----------------------------------------------------------


    # --- add / update / delete ------------------------------------------


    # --- error propagation (feeds the plugin's circuit breaker) ----------

    def test_http_error_raises(self):
        s = _StubServer()
        with pytest.raises(httpx.HTTPStatusError):
            _backend(s).delete("missing")  # 404 -> raise_for_status; 'not found' won't trip breaker
