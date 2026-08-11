from types import SimpleNamespace

import pytest

from hermes_cli.runtime_discovery import native_models_url, normalize_lm_studio_models
from hermes_cli.runtime_profiles import (
    RuntimeProfileError,
    activate_profile,
    delete_profile,
    profile_response,
    selected_profile_request_overrides,
    upsert_profile,
)


def _body(**values):
    defaults = {
        "id": "balanced",
        "name": "Balanced",
        "model": "openai/gpt-oss-20b",
        "overrides": {},
        "make_active": False,
    }
    return SimpleNamespace(**(defaults | values))


def test_absent_or_inactive_profile_sends_no_overrides():
    entry = {"model": "openai/gpt-oss-20b"}
    upsert_profile(entry, _body(overrides={"temperature": 0.4}))
    assert selected_profile_request_overrides(entry) == {}


def test_active_profile_emits_only_explicit_fields_and_can_restore_server_defaults():
    entry = {"model": "openai/gpt-oss-20b"}
    upsert_profile(entry, _body(overrides={"temperature": 0.4, "top_k": 40}, make_active=True))
    assert selected_profile_request_overrides(entry) == {
        "temperature": 0.4,
        "extra_body": {"top_k": 40.0},
    }
    activate_profile(entry, "openai/gpt-oss-20b", None)
    assert selected_profile_request_overrides(entry) == {}


def test_runtime_request_merges_active_profile_without_hidden_defaults():
    from hermes_cli.runtime_provider import _custom_provider_request_overrides

    entry = {"model": "m", "extra_body": {"vendor_flag": True}}
    upsert_profile(entry, _body(model="m", overrides={"temperature": 0.25, "top_k": 32}, make_active=True))
    assert _custom_provider_request_overrides(entry) == {
        "temperature": 0.25,
        "extra_body": {"vendor_flag": True, "top_k": 32.0},
    }


def test_profile_is_bound_to_exact_model_and_delete_clears_activation():
    entry = {"model": "model-a"}
    upsert_profile(entry, _body(model="model-a", make_active=True))
    with pytest.raises(RuntimeProfileError):
        activate_profile(entry, "model-b", "balanced")
    delete_profile(entry, "balanced")
    assert profile_response(entry) == []
    assert entry["active_runtime_profiles"] == {}


@pytest.mark.parametrize("overrides", [
    {"context_length": 65_536},
    {"top_p": 2},
    {"temperature": float("nan")},
    {"stop": [""]},
])
def test_unsupported_or_invalid_values_fail_closed(overrides):
    with pytest.raises(RuntimeProfileError):
        upsert_profile({"model": "m"}, _body(model="m", overrides=overrides))


def test_lm_studio_projection_reports_only_server_values():
    payload = {"models": [{
        "type": "llm",
        "publisher": "openai",
        "key": "openai/gpt-oss-20b",
        "display_name": "GPT-OSS 20B",
        "architecture": "gpt-oss",
        "quantization": {"name": "MXFP4", "bits_per_weight": 4},
        "size_bytes": 12_109_664_217,
        "loaded_instances": [{"id": "openai/gpt-oss-20b", "config": {
            "context_length": 65_536,
            "eval_batch_size": 2_048,
            "flash_attention": True,
            "unknown_setting": "must-not-project",
        }}],
        "max_context_length": 131_072,
        "format": "gguf",
        "capabilities": {"vision": False, "trained_for_tool_use": True, "reasoning": {
            "allowed_options": ["low", "medium", "high"], "default": "low",
        }},
        "path": "C:/private/model.gguf",
    }]}
    projected = normalize_lm_studio_models(payload)
    assert projected == [{
        "id": "openai/gpt-oss-20b",
        "type": "llm",
        "display_name": "GPT-OSS 20B",
        "publisher": "openai",
        "architecture": "gpt-oss",
        "format": "gguf",
        "max_context_length": 131_072,
        "size_bytes": 12_109_664_217,
        "quantization": {"name": "MXFP4", "bits_per_weight": 4.0},
        "capabilities": {"vision": False, "trained_for_tool_use": True, "reasoning": {
            "allowed_options": ["low", "medium", "high"], "default": "low",
        }},
        "loaded_instances": [{"id": "openai/gpt-oss-20b", "config": {
            "context_length": 65_536,
            "eval_batch_size": 2_048,
            "flash_attention": True,
        }}],
        "loaded": True,
    }]


def test_native_url_is_same_origin_and_rejects_credentials():
    assert native_models_url("http://host.docker.internal:1234/v1") == "http://host.docker.internal:1234/api/v1/models"
    assert native_models_url("http://user:secret@host:1234/v1") is None


def test_profile_routes_persist_activation_and_restore_server_defaults(monkeypatch):
    from fastapi.testclient import TestClient
    import hermes_cli.web_server as server

    cfg = {"model": {}, "providers": {"lm-studio": {
        "name": "LM Studio", "base_url": "http://host.docker.internal:1234/v1",
        "model": "openai/gpt-oss-20b", "models": {"openai/gpt-oss-20b": {}},
    }}}
    monkeypatch.setattr(server, "load_config", lambda: cfg)
    monkeypatch.setattr(server, "save_config", lambda value: None)
    client = TestClient(server.app)
    client.headers[server._SESSION_HEADER_NAME] = server._SESSION_TOKEN

    saved = client.post("/api/providers/custom-endpoints/lm-studio/profiles", json={
        "id": "balanced", "name": "Balanced", "model": "openai/gpt-oss-20b",
        "overrides": {"temperature": 0.4}, "make_active": True,
    })
    assert saved.status_code == 200
    endpoint = next(item for item in saved.json()["endpoints"] if item["id"] == "lm-studio")
    assert endpoint["profiles"][0]["is_active"] is True

    restored = client.post("/api/providers/custom-endpoints/lm-studio/profiles/activate", json={
        "model": "openai/gpt-oss-20b", "profile_id": None,
    })
    assert restored.status_code == 200
    endpoint = next(item for item in restored.json()["endpoints"] if item["id"] == "lm-studio")
    assert endpoint["profiles"][0]["is_active"] is False
