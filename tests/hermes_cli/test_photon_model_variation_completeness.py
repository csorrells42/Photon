"""Exhaustive contract for every Hermes model variation exposed to Photon."""

import pytest
from pydantic import ValidationError

from hermes_cli.provider_catalog import provider_catalog
from hermes_cli.web_models import CustomEndpointUpdate
from hermes_cli.web_server import (
    _AUX_TASK_SLOTS,
    _all_aux_task_slots,
    _build_oauth_catalog,
    _catalog_provider_env_metadata,
    app,
)


EXPECTED_PROVIDER_SLUGS = (
    "nous", "fireworks", "openrouter", "moa", "novita", "lmstudio",
    "anthropic", "openai-codex", "openai-api", "alibaba", "xai-oauth",
    "xiaomi", "tencent-tokenhub", "nvidia", "copilot", "copilot-acp",
    "huggingface", "gemini", "vertex", "deepseek", "xai", "zai",
    "kimi-coding", "kimi-coding-cn", "stepfun", "minimax",
    "minimax-oauth", "minimax-cn", "ollama-cloud", "arcee", "gmi",
    "kilocode", "opencode-zen", "opencode-go", "bedrock", "azure-foundry",
    "ai-gateway", "qwen-oauth", "actual", "alibaba-coding-plan", "custom",
    "deepinfra", "upstage",
)

EXPECTED_ACCOUNT_ROWS = {
    "nous", "openai-codex", "qwen-oauth", "minimax-oauth", "xai-oauth",
    "copilot-acp", "anthropic", "claude-code",
}

EXPECTED_AUXILIARY_SLOTS = (
    "vision", "web_extract", "compression", "skills_hub", "approval", "mcp",
    "title_generation", "memory_query_rewrite", "tts_audio_tags",
    "triage_specifier", "kanban_decomposer",
    "profile_describer", "curator",
)

EXPECTED_CUSTOM_API_MODES = (
    "", "chat_completions", "codex_responses", "anthropic_messages",
)


def test_every_current_provider_has_a_photon_configuration_surface():
    rows = provider_catalog()
    slugs = tuple(row.slug for row in rows)
    assert slugs == EXPECTED_PROVIDER_SLUGS

    account_providers = {row["id"] for row in _build_oauth_catalog()}
    credential_providers = {
        row["provider"] for row in _catalog_provider_env_metadata().values()
    }
    assert len(_catalog_provider_env_metadata()) == 73

    # API-key/SDK providers are configured in Photon Keys, subscription and
    # external-process providers in Accounts, while the two virtual/synthetic
    # families own dedicated Local & Custom and Mixture-of-Agents surfaces.
    surfaced = credential_providers | account_providers | {"custom", "moa"}
    assert set(slugs) <= surfaced
    assert account_providers == EXPECTED_ACCOUNT_ROWS


def test_every_model_variation_route_and_auxiliary_slot_is_packaged():
    routes = {
        (route.path, method)
        for route in app.routes
        for method in (getattr(route, "methods", None) or set())
    }
    expected_routes = {
        ("/api/model/options", "GET"),
        ("/api/model/set", "POST"),
        ("/api/model/defaults", "GET"),
        ("/api/model/defaults", "PUT"),
        ("/api/model/auxiliary", "GET"),
        ("/api/model/fallback", "GET"),
        ("/api/model/fallback", "PUT"),
        ("/api/model/moa", "GET"),
        ("/api/model/moa", "PUT"),
        ("/api/providers/oauth", "GET"),
        ("/api/credentials/pool", "GET"),
        ("/api/credentials/pool", "POST"),
        ("/api/providers/custom-endpoints", "GET"),
        ("/api/providers/custom-endpoints", "POST"),
    }
    assert expected_routes <= routes
    assert _AUX_TASK_SLOTS == EXPECTED_AUXILIARY_SLOTS


def test_plugin_registered_auxiliary_slots_are_added_without_duplicates(monkeypatch):
    monkeypatch.setattr(
        "hermes_cli.plugins.get_plugin_auxiliary_tasks",
        lambda: [
            {"key": "curator"},
            {"key": "photon_review"},
            {"key": ""},
        ],
    )

    assert _all_aux_task_slots() == (*EXPECTED_AUXILIARY_SLOTS, "photon_review")


@pytest.mark.parametrize("api_mode", EXPECTED_CUSTOM_API_MODES)
def test_every_native_custom_transport_is_accepted(api_mode):
    payload = CustomEndpointUpdate(
        name="Custom",
        base_url="http://host.docker.internal:1234/v1",
        model="qwen/local",
        api_mode=api_mode,
    )
    assert payload.api_mode == api_mode


def test_unknown_custom_transport_is_rejected():
    with pytest.raises(ValidationError):
        CustomEndpointUpdate(
            name="Custom",
            base_url="http://host.docker.internal:1234/v1",
            model="qwen/local",
            api_mode="almost-openai",
        )
