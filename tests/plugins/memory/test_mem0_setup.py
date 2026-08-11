"""Tests for Mem0 setup wizard — flag parsing, config building, validation."""

import json
import sys
import types
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

from plugins.memory.mem0._setup import (
    parse_flags,
    build_oss_config,
    _write_env,
    _prompt_api_key,
    post_setup,
    _check_qdrant_path,
    _check_ollama,
    _check_pgvector,
    _ollama_model_manifest,
    _validate_workbench_local_runtime,
    _workbench_model_url,
    _workbench_qdrant_url,
    _probe_workbench_qdrant_ready,
)


def _inject_fake_hermes_cli(monkeypatch):
    """Inject fake hermes_cli modules so yaml/curses aren't required."""
    fake_config_mod = types.ModuleType("hermes_cli.config")
    fake_config_mod.save_config = lambda c: None

    fake_setup_mod = types.ModuleType("hermes_cli.memory_setup")
    fake_setup_mod._curses_select = lambda *a, **kw: 0
    fake_setup_mod._prompt = lambda label, default=None, secret=False: default or ""

    fake_hermes_cli = types.ModuleType("hermes_cli")
    fake_hermes_cli.config = fake_config_mod
    fake_hermes_cli.memory_setup = fake_setup_mod

    monkeypatch.setitem(sys.modules, "hermes_cli", fake_hermes_cli)
    monkeypatch.setitem(sys.modules, "hermes_cli.config", fake_config_mod)
    monkeypatch.setitem(sys.modules, "hermes_cli.memory_setup", fake_setup_mod)

    monkeypatch.setattr("plugins.memory.mem0._setup._curses_select", lambda *a, **kw: 0)
    monkeypatch.setattr("plugins.memory.mem0._setup._prompt", lambda label, default=None, secret=False: default or "")
    return fake_config_mod


class TestParseFlags:

    def test_mode_platform(self):
        flags = parse_flags(["--mode", "platform", "--api-key", "sk-test"])
        assert flags["mode"] == "platform"
        assert flags["api_key"] == "sk-test"


    def test_no_flags_returns_empty_mode(self):
        flags = parse_flags([])
        assert flags["mode"] == ""

    def test_oss_vector_path_flag(self):
        flags = parse_flags(["--mode", "oss", "--oss-vector-path", "/data/qdrant"])
        assert flags["oss_vector_path"] == "/data/qdrant"


class TestBuildOSSConfig:

    def test_openai_defaults(self):
        flags = parse_flags(["--mode", "oss", "--oss-llm-key", "sk-oai"])
        oss, env_writes = build_oss_config(flags)
        assert oss["llm"]["provider"] == "openai"
        assert oss["llm"]["config"]["model"] == "gpt-5-mini"
        assert oss["embedder"]["provider"] == "openai"
        assert oss["embedder"]["config"]["model"] == "text-embedding-3-small"
        assert oss["vector_store"]["provider"] == "qdrant"
        assert env_writes["OPENAI_API_KEY"] == "sk-oai"


    def test_ollama_no_key_needed(self):
        flags = parse_flags(["--mode", "oss", "--oss-llm", "ollama", "--oss-embedder", "ollama"])
        oss, env_writes = build_oss_config(flags)
        assert oss["llm"]["provider"] == "ollama"
        assert "model" in oss["llm"]["config"]
        assert oss["llm"]["config"]["ollama_base_url"] == "http://localhost:11434"
        assert oss["embedder"]["config"]["ollama_base_url"] == "http://localhost:11434"
        assert env_writes == {}

    def test_workbench_dmr_validation_binds_inventory_and_ignores_response_model_path(self, monkeypatch):
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        flags = parse_flags(["--mode", "oss"])
        oss, _ = build_oss_config(flags)

        payloads = {
            "/readyz": b"healthz check passed",
            "/models": json.dumps({"data": [
                {"id": "docker.io/ai/qwen3:4B-UD-Q4_K_XL"},
                {"id": "docker.io/ai/nomic-embed-text-v1.5:latest"},
            ]}).encode(),
            "/chat/completions": json.dumps({
                "model": "/models/bundles/sha256/d6bb9d/model/model.gguf",
                "choices": [{"message": {"content": "OK"}}],
            }).encode(),
            "/embeddings": json.dumps({
                "data": [{"embedding": [0.0] * 768, "index": 0}],
            }).encode(),
        }

        class _Response:
            def __init__(self, payload):
                self.payload = payload

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                return self.payload

        def _urlopen(request, **_kwargs):
            for suffix, payload in payloads.items():
                if request.full_url.endswith(suffix):
                    if suffix == "/chat/completions":
                        body = json.loads(request.data)
                        assert body["model"] == "ai/qwen3:4B-UD-Q4_K_XL"
                        assert body["response_format"] == {"type": "json_object"}
                    elif suffix == "/embeddings":
                        body = json.loads(request.data)
                        assert body["model"] == "ai/nomic-embed-text-v1.5"
                        assert body["dimensions"] == 768
                    return _Response(payload)
            raise AssertionError(request.full_url)

        monkeypatch.setattr(
            "plugins.memory.mem0._setup.urllib.request.urlopen",
            _urlopen,
        )
        assert _validate_workbench_local_runtime(oss) == []
        assert oss["llm"]["config"]["model_digest"] == "sha256:d6bb9d7293698b06da1eb40008cdf5944bd6125f437b34ff1a7d535f5e868e80"
        assert oss["embedder"]["config"]["model_digest"] == "sha256:653017dd060f5cd345118ff90382ceb213d383de2887820d2f303893d32ef40d"
        assert oss["embedding_identity"]["model_digest"] == "sha256:653017dd060f5cd345118ff90382ceb213d383de2887820d2f303893d32ef40d"

    def test_embedder_reuses_llm_key(self):
        """When LLM and embedder share same provider, key written once."""
        flags = parse_flags(["--mode", "oss", "--oss-llm-key", "sk-oai"])
        _, env_writes = build_oss_config(flags)
        assert env_writes == {"OPENAI_API_KEY": "sk-oai"}

    def test_different_embedder_needs_separate_key(self):
        flags = parse_flags([
            "--mode", "oss",
            "--oss-llm", "ollama",
            "--oss-embedder", "openai", "--oss-embedder-key", "sk-oai",
        ])
        _, env_writes = build_oss_config(flags)
        assert env_writes == {"OPENAI_API_KEY": "sk-oai"}

    def test_pgvector_config(self):
        flags = parse_flags([
            "--mode", "oss", "--oss-llm-key", "sk-oai",
            "--oss-vector", "pgvector",
            "--oss-vector-host", "db.local", "--oss-vector-port", "5433",
            "--oss-vector-user", "pg", "--oss-vector-dbname", "memdb",
        ])
        oss, _ = build_oss_config(flags)
        vs = oss["vector_store"]
        assert vs["provider"] == "pgvector"
        assert vs["config"]["host"] == "db.local"
        assert vs["config"]["port"] == 5433
        assert vs["config"]["user"] == "pg"

    def test_known_dims_auto_set(self):
        flags = parse_flags(["--mode", "oss", "--oss-llm-key", "sk-oai"])
        oss, _ = build_oss_config(flags)
        dims = oss["embedder"]["config"].get("embedding_dims")
        assert dims == 1536

    def test_custom_qdrant_path(self):
        flags = parse_flags([
            "--mode", "oss", "--oss-llm-key", "sk-oai",
            "--oss-vector-path", "/data/qdrant",
        ])
        oss, _ = build_oss_config(flags)
        assert oss["vector_store"]["config"]["path"] == "/data/qdrant"

    def test_authenticated_workbench_forces_pinned_local_stack(self, monkeypatch):
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        flags = parse_flags([
            "--mode", "platform",
            "--oss-llm", "openai",
            "--oss-embedder", "openai",
            "--oss-vector", "pgvector",
            "--oss-llm-key", "must-not-persist",
            "--oss-embedder-key", "must-not-persist-either",
        ])
        oss, env_writes = build_oss_config(flags)

        assert oss["llm"]["provider"] == "lmstudio"
        assert oss["llm"]["config"]["model"] == "ai/qwen3:4B-UD-Q4_K_XL"
        assert oss["llm"]["config"]["lmstudio_base_url"] == "http://host.docker.internal:12434/engines/v1"
        assert oss["llm"]["config"]["api_key"] == "not-needed"
        assert oss["embedder"]["provider"] == "lmstudio"
        assert oss["embedder"]["config"]["model"] == "ai/nomic-embed-text-v1.5"
        assert oss["embedder"]["config"]["lmstudio_base_url"] == "http://host.docker.internal:12434/engines/v1"
        assert oss["embedder"]["config"]["api_key"] == "not-needed"
        assert oss["embedder"]["config"]["embedding_dims"] == 768
        assert oss["vector_store"]["provider"] == "qdrant"
        assert oss["vector_store"]["config"] == {
            "url": "http://memory-vector:6333",
            "collection_name": "hermes_workbench_mem0_v3",
        }
        assert oss["embedding_identity"] == {
            "version": 3,
            "provider": "lmstudio",
            "model": "ai/nomic-embed-text-v1.5",
            "inventory_model": "docker.io/ai/nomic-embed-text-v1.5:latest",
            "model_url": "http://host.docker.internal:12434/engines/v1",
            "dimensions": 768,
            "vector_provider": "qdrant",
            "vector_url": "http://memory-vector:6333",
            "collection": "hermes_workbench_mem0_v3",
            "distance": "cosine",
        }
        assert env_writes == {}

    def test_workbench_model_url_override_is_normalized_and_cli_urls_are_ignored(self, monkeypatch):
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setenv("HERMES_MEM0_MODEL_URL", "HTTPS://Model-Runner.Docker.Internal/engines/v1/")
        assert _workbench_model_url() == "https://model-runner.docker.internal/engines/v1"

        flags = parse_flags([
            "--mode", "oss",
            "--oss-llm-url", "http://attacker.invalid:9999",
            "--oss-embedder-url", "http://attacker.invalid:9999",
            "--oss-llm-model", "attacker/model:latest",
            "--oss-embedder-model", "attacker/embedder:latest",
        ])
        oss, _ = build_oss_config(flags)
        assert oss["llm"]["config"]["lmstudio_base_url"] == "https://model-runner.docker.internal/engines/v1"
        assert oss["llm"]["config"]["model"] == "ai/qwen3:4B-UD-Q4_K_XL"
        assert oss["embedder"]["config"]["lmstudio_base_url"] == "https://model-runner.docker.internal/engines/v1"
        assert oss["embedder"]["config"]["model"] == "ai/nomic-embed-text-v1.5"
        assert oss["embedding_identity"]["model_url"] == "https://model-runner.docker.internal/engines/v1"

    def test_workbench_model_url_preserves_an_explicit_bounded_port(self, monkeypatch):
        monkeypatch.setenv("HERMES_MEM0_MODEL_URL", "HTTP://host.docker.internal:12434/")
        assert _workbench_model_url() == "http://host.docker.internal:12434/engines/v1"

    def test_workbench_qdrant_url_has_internal_default_and_bounded_override(self, monkeypatch):
        monkeypatch.delenv("HERMES_MEM0_QDRANT_URL", raising=False)
        assert _workbench_qdrant_url() == "http://memory-vector:6333"
        monkeypatch.setenv(
            "HERMES_MEM0_QDRANT_URL", "HTTPS://Memory-Vector.Internal:7443/"
        )
        assert _workbench_qdrant_url() == "https://memory-vector.internal:7443"

    @pytest.mark.parametrize("bad_url", [
        "http://user:password@memory-vector:6333",
        "http://memory-vector:6333/collections",
        "http://memory-vector:6333?api_key=secret",
        "file:///qdrant",
        "http://memory-vector:0",
    ])
    def test_workbench_qdrant_url_rejects_unsafe_shapes(self, monkeypatch, bad_url):
        monkeypatch.setenv("HERMES_MEM0_QDRANT_URL", bad_url)
        with pytest.raises(ValueError):
            _workbench_qdrant_url()

    def test_workbench_cli_vector_path_and_url_cannot_restore_embedded_qdrant(self, monkeypatch):
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        oss, _ = build_oss_config(parse_flags([
            "--mode", "oss",
            "--oss-vector-path", "/tmp/embedded-qdrant",
            "--oss-vector-url", "http://host.docker.internal:6333",
        ]))
        assert "path" not in oss["vector_store"]["config"]
        assert oss["vector_store"]["config"]["url"] == "http://memory-vector:6333"

    def test_workbench_mem0_adapter_ignores_openrouter_environment_override(self, monkeypatch):
        monkeypatch.setenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", "1")
        monkeypatch.setenv("OPENROUTER_API_KEY", "must-not-leave-localhost")
        oss, _ = build_oss_config(parse_flags(["--mode", "oss"]))
        assert oss["llm"]["provider"] == "lmstudio"
        assert oss["llm"]["config"]["lmstudio_base_url"] == "http://host.docker.internal:12434/engines/v1"
        assert "openrouter_base_url" not in oss["llm"]["config"]

    @pytest.mark.parametrize("bad_url", [
        "ftp://memory-model:11434",
        "http://user:password@memory-model:11434",
        "http://memory-model:11434/api/tags",
        "http://memory-model:11434/engines/v2",
        "http://memory-model:11434?token=secret",
        "http://memory-model:11434#fragment",
        "http://memory-model:0",
        "http://memory-model:70000",
        "http://-memory-model:11434",
    ])
    def test_workbench_model_url_rejects_unsafe_shapes(self, monkeypatch, bad_url):
        monkeypatch.setenv("HERMES_MEM0_MODEL_URL", bad_url)
        with pytest.raises(ValueError):
            _workbench_model_url()


class TestWriteEnv:

    def test_write_new_vars(self, tmp_path):
        env_path = tmp_path / ".env"
        _write_env(env_path, {"OPENAI_API_KEY": "sk-test"})
        content = env_path.read_text()
        assert "OPENAI_API_KEY=sk-test" in content

    def test_update_existing_var(self, tmp_path):
        env_path = tmp_path / ".env"
        env_path.write_text("OPENAI_API_KEY=old\nOTHER=keep\n")
        _write_env(env_path, {"OPENAI_API_KEY": "new"})
        content = env_path.read_text()
        assert "OPENAI_API_KEY=new" in content
        assert "OTHER=keep" in content
        assert "old" not in content

    def test_preserves_non_ascii_existing_lines(self, tmp_path):
        """Existing non-ASCII .env content must survive the read-modify-write
        as UTF-8 (the locale codec would crash/mangle it on Windows)."""
        env_path = tmp_path / ".env"
        env_path.write_bytes("PROXY_NOTE=café-zürich-完了\n".encode("utf-8"))
        _write_env(env_path, {"OPENAI_API_KEY": "sk-test"})
        content = env_path.read_text(encoding="utf-8")
        assert "PROXY_NOTE=café-zürich-完了" in content
        assert "OPENAI_API_KEY=sk-test" in content

    def test_updates_first_key_with_bom(self, tmp_path):
        """A Notepad-edited .env carries a BOM; the first key must still be
        matched/updated in place, not duplicated."""
        env_path = tmp_path / ".env"
        env_path.write_bytes("﻿OPENAI_API_KEY=old\n".encode("utf-8"))
        _write_env(env_path, {"OPENAI_API_KEY": "new"})
        content = env_path.read_text(encoding="utf-8")
        assert content.count("OPENAI_API_KEY=") == 1
        assert "OPENAI_API_KEY=new" in content


class TestPromptApiKey:

    def test_existing_key_found_behind_bom(self, tmp_path, monkeypatch):
        """The masked-current-value lookup must see a key on the BOM'd first
        line of a Notepad-edited .env instead of prompting from scratch."""
        env_path = tmp_path / ".env"
        env_path.write_bytes("﻿OPENAI_API_KEY=sk-existing\n".encode("utf-8"))
        monkeypatch.delenv("OPENAI_API_KEY", raising=False)

        prompts: list[str] = []

        def _fake_getpass(prompt):
            prompts.append(prompt)
            return ""

        monkeypatch.setattr("plugins.memory.mem0._setup.getpass.getpass", _fake_getpass)
        _prompt_api_key("OpenAI", "OPENAI_API_KEY", str(tmp_path))

        assert len(prompts) == 1
        assert "current: ...ting" in prompts[0]


class TestPostSetup:

    def test_platform_flag_mode(self, tmp_path, monkeypatch):
        monkeypatch.setattr("sys.argv", ["hermes", "--mode", "platform", "--api-key", "sk-test"])
        monkeypatch.setattr("plugins.memory.mem0._setup.get_hermes_home", lambda: tmp_path)
        _inject_fake_hermes_cli(monkeypatch)
        config = {"memory": {}}
        post_setup(str(tmp_path), config)
        assert config["memory"]["provider"] == "mem0"
        env_content = (tmp_path / ".env").read_text()
        assert "MEM0_API_KEY=sk-test" in env_content
        mem0_json = json.loads((tmp_path / "mem0.json").read_text())
        assert mem0_json["mode"] == "platform"


    def test_selfhosted_flag_mode(self, tmp_path, monkeypatch):
        monkeypatch.setattr("sys.argv", [
            "hermes", "--mode", "selfhosted",
            "--host", "http://localhost:8888/", "--api-key", "admin-key",
        ])
        monkeypatch.setattr("plugins.memory.mem0._setup.get_hermes_home", lambda: tmp_path)
        _inject_fake_hermes_cli(monkeypatch)
        monkeypatch.setattr("plugins.memory.mem0._setup._check_selfhosted_server", lambda h: None)
        config = {"memory": {}}
        post_setup(str(tmp_path), config)
        assert config["memory"]["provider"] == "mem0"
        env_content = (tmp_path / ".env").read_text()
        assert "MEM0_API_KEY=admin-key" in env_content
        mem0_json = json.loads((tmp_path / "mem0.json").read_text())
        assert mem0_json["host"] == "http://localhost:8888"  # trailing slash stripped
        assert mem0_json["user_id"] == "hermes-user"


class TestDryRun:

    def test_dry_run_flag_parsed(self):
        flags = parse_flags(["--mode", "oss", "--oss-llm-key", "sk-oai", "--dry-run"])
        assert flags["dry_run"] is True


class TestConnectivityChecks:

    def test_qdrant_path_writable(self, tmp_path):
        ok, msg = _check_qdrant_path(str(tmp_path / "qdrant"))
        assert ok is True

    def test_workbench_qdrant_readiness_is_an_operational_probe(self, monkeypatch):
        seen = {}

        class _Response:
            status = 200

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

        def _urlopen(request, timeout):
            seen["url"] = request.full_url
            seen["timeout"] = timeout
            return _Response()

        monkeypatch.setattr(
            "plugins.memory.mem0._setup.urllib.request.urlopen", _urlopen
        )
        assert _probe_workbench_qdrant_ready("http://memory-vector:6333") is None
        assert seen == {
            "url": "http://memory-vector:6333/readyz",
            "timeout": 5,
        }

    def test_workbench_qdrant_unavailable_fails_runtime_validation_without_detail_leak(self, monkeypatch):
        monkeypatch.setattr(
            "plugins.memory.mem0._setup.urllib.request.urlopen",
            lambda *_args, **_kwargs: (_ for _ in ()).throw(
                OSError("proxy contains top-secret-token")
            ),
        )
        error = _probe_workbench_qdrant_ready("http://memory-vector:6333")
        assert error == "Authenticated Workbench Qdrant service is unavailable"
        assert "secret" not in error

    def test_standalone_qdrant_path_default_and_url_override_remain_supported(self, monkeypatch):
        monkeypatch.delenv("HERMES_WORKBENCH_AUTHENTICATED_MEM0", raising=False)
        default, _ = build_oss_config(parse_flags(["--mode", "oss"]))
        assert "path" in default["vector_store"]["config"]
        remote, _ = build_oss_config(parse_flags([
            "--mode", "oss", "--oss-vector-url", "http://localhost:6333"
        ]))
        assert remote["vector_store"]["config"] == {"url": "http://localhost:6333"}
