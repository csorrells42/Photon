from types import SimpleNamespace
from unittest.mock import patch

import tui_gateway.server as server


def _context(model: str, provider: str, base_url: str = "") -> SimpleNamespace:
    return SimpleNamespace(
        current_model=model,
        current_provider=provider,
        current_base_url=base_url,
    )


def _payload(model: str, provider: str, providers=None) -> dict:
    return {"model": model, "provider": provider, "providers": providers or []}


def _control(model: str, provider: str, **expected) -> dict:
    return {
        "model": model,
        "provider": provider,
        "label": expected.pop("label"),
        "options": expected.pop("options", []),
        "effort_aliases": expected.pop("effort_aliases", {}),
        "option_labels": expected.pop("option_labels", {}),
        "default_effort": expected.pop("default_effort", None),
        "default_enabled": expected.pop("default_enabled", None),
        "mandatory": expected.pop("mandatory", False),
        "supports_effort": expected.pop("supports_effort", False),
        "supports_toggle": expected.pop("supports_toggle", False),
        "source": expected.pop("source"),
        **expected,
    }


def test_model_options_projects_exact_lm_studio_hybrid_and_default():
    model = "gpt-oss-20b"
    provider = "custom:local-lm-studio"
    with patch.object(server, "_model_picker_context", return_value=_context(
        model, provider, "http://host.docker.internal:1234/v1",
    )), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.lmstudio_model_reasoning_metadata",
        return_value={
            "supported": True,
            "allowed_options": ["off", "on", "low", "high", "unsupported"],
            "default": "on",
        },
    ):
        response = server._methods["model.options"]("r1", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Thinking",
        options=["none", "enabled", "low", "high"],
        option_labels={"none": "Off", "enabled": "On"},
        default_enabled=True,
        supports_effort=True,
        supports_toggle=True,
        source="server",
    )


def test_model_options_projects_lm_studio_toggle_without_inventing_effort():
    model = "gemma-4-12b-qat"
    provider = "custom:local-lm-studio"
    with patch.object(server, "_model_picker_context", return_value=_context(
        model, provider, "http://host.docker.internal:1234/v1",
    )), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.lmstudio_model_reasoning_metadata",
        return_value={"supported": True, "allowed_options": ["off", "on"], "default": "off"},
    ):
        response = server._methods["model.options"]("r-toggle", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Thinking",
        options=["none", "enabled"],
        option_labels={"none": "Off", "enabled": "On"},
        default_enabled=False,
        supports_toggle=True,
        source="server",
    )


def test_model_options_marks_docker_model_runner_as_runner_managed():
    model = "ai/qwen3"
    provider = "custom:docker-model-runner"
    with patch.object(server, "_model_picker_context", return_value=_context(
        model, provider, "http://model-runner.docker.internal:12434/engines/v1",
    )), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ):
        response = server._methods["model.options"]("r-dmr", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Reasoning",
        source="server-managed",
    )


def test_model_options_uses_exact_openrouter_hybrid_metadata():
    model = "deepseek/deepseek-v4-flash"
    provider = "openrouter"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.openrouter_model_reasoning_metadata",
        return_value={
            "supported": True,
            "metadata_complete": True,
            "supported_efforts": ["low", "medium", "high"],
            "default_effort": "medium",
            "default_enabled": True,
            "mandatory": False,
        },
    ):
        response = server._methods["model.options"]("r-openrouter", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Thinking",
        options=["none", "low", "medium", "high"],
        option_labels={"none": "Off"},
        default_effort="medium",
        default_enabled=True,
        supports_effort=True,
        supports_toggle=True,
        source="server",
    )


def test_model_options_openrouter_mandatory_claude_has_no_off():
    model = "anthropic/claude-opus-4.7"
    provider = "openrouter"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.openrouter_model_reasoning_metadata",
        return_value={
            "supported": True,
            "metadata_complete": True,
            "supported_efforts": ["low", "high", "max"],
            "default_effort": "high",
            "default_enabled": True,
            "mandatory": True,
        },
    ):
        response = server._methods["model.options"]("r-mandatory", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Thinking",
        options=["low", "high", "max"],
        default_effort="high",
        default_enabled=True,
        mandatory=True,
        supports_effort=True,
        source="server",
    )


def test_openrouter_null_effort_list_means_exact_gateway_ladder_without_ultra():
    model = "openai/future-reasoner"
    provider = "openrouter"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.openrouter_model_reasoning_metadata",
        return_value={
            "supported": True,
            "metadata_complete": True,
            "supported_efforts": None,
            "mandatory": False,
        },
    ):
        response = server._methods["model.options"]("r-all-efforts", {})

    assert response["result"]["reasoning_control"]["options"] == [
        "none", "minimal", "low", "medium", "high", "xhigh", "max",
    ]


def test_model_options_does_not_guess_from_coarse_openrouter_boolean():
    model = "openai/future-reasoner"
    provider = "openrouter"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ), patch(
        "hermes_cli.models.openrouter_model_reasoning_metadata",
        return_value={"supported": True, "metadata_complete": False},
    ):
        response = server._methods["model.options"]("r-coarse", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Reasoning",
        source="unverified",
    )


def test_model_options_projects_openai_api_gpt_5_6_off_and_exact_ladder():
    model = "gpt-5.6"
    provider = "openai-api"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ):
        response = server._methods["model.options"]("r-openai", {})

    assert response["result"]["reasoning_control"] == _control(
        model,
        provider,
        label="Reasoning",
        options=["none", "low", "medium", "high", "xhigh", "max"],
        effort_aliases={"minimal": "low", "ultra": "max"},
        default_effort="medium",
        default_enabled=True,
        supports_effort=True,
        supports_toggle=True,
        source="compatibility",
    )


def test_model_options_caches_only_the_current_pair_for_strict_save_validation():
    model = "gpt-5.6"
    provider = "openai-api"
    session = {"agent": None}
    with patch.dict(server._sessions, {"s1": session}, clear=False), patch.object(
        server, "_model_picker_context", return_value=_context(model, provider)
    ), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ):
        server._methods["model.options"]("r-cache", {"session_id": "s1"})

    assert session["reasoning_control"]["model"] == model
    assert session["reasoning_control"]["provider"] == provider

    alternate_session = {"agent": None, "reasoning_control": {"model": "keep-me"}}
    with patch.dict(server._sessions, {"s2": alternate_session}, clear=False), patch.object(
        server, "_model_picker_context", return_value=_context("current", provider)
    ), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload("current", provider, [{"slug": provider, "models": [model]}]),
    ):
        server._methods["model.options"]("r-inspect", {
            "session_id": "s2",
            "reasoning_model": model,
            "reasoning_provider": provider,
        })

    assert alternate_session["reasoning_control"] == {"model": "keep-me"}


def test_model_options_normalizes_deepseek_underscore_family_names():
    model = "deepseek_v4_flash_0731"
    provider = "deepseek"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ):
        response = server._methods["model.options"]("r-deepseek", {})

    control = response["result"]["reasoning_control"]
    assert control["label"] == "Thinking"
    assert control["options"] == ["none", "high", "max"]
    assert control["default_effort"] == "high"
    assert control["default_enabled"] is True
    assert control["effort_aliases"] == {
        "minimal": "high",
        "low": "high",
        "medium": "high",
        "xhigh": "max",
        "ultra": "max",
    }
    assert control["supports_toggle"] is True
    assert control["supports_effort"] is True


def test_gemini_controls_never_label_hidden_thoughts_as_off():
    cases = [
        ("gemini-2.5-flash", ["none", "enabled"], True, False),
        ("gemini-2.5-pro", ["enabled"], False, True),
        ("gemini-3.1-pro-preview", ["low", "high"], False, True),
        ("gemini-3.6-flash", ["low", "medium", "high"], False, True),
    ]
    for model, expected_options, supports_toggle, mandatory in cases:
        provider = "gemini"
        with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
            "hermes_cli.inventory.build_model_options_payload",
            return_value=_payload(model, provider),
        ):
            response = server._methods["model.options"](f"r-{model}", {})

        control = response["result"]["reasoning_control"]
        assert control["options"] == expected_options
        assert control["supports_toggle"] is supports_toggle
        assert control["mandatory"] is mandatory


def test_nous_suppresses_unproven_off_without_losing_real_effort_levels():
    model = "deepseek/deepseek-v4-flash"
    provider = "nous"
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload",
        return_value=_payload(model, provider),
    ):
        response = server._methods["model.options"]("r-nous", {})

    control = response["result"]["reasoning_control"]
    assert control["options"] == ["high", "max"]
    assert control["supports_toggle"] is False
    assert control["supports_effort"] is True


def test_model_options_rejects_unlisted_requested_pair_without_guessing():
    model = "plain-chat"
    provider = "openrouter"
    payload = _payload(model, provider, [{"slug": provider, "models": [model], "capabilities": {}}])
    with patch.object(server, "_model_picker_context", return_value=_context(model, provider)), patch(
        "hermes_cli.inventory.build_model_options_payload", return_value=payload,
    ):
        response = server._methods["model.options"]("r-unlisted", {
            "reasoning_model": "openai/gpt-5.6",
            "reasoning_provider": provider,
        })

    control = response["result"]["reasoning_control"]
    assert control["model"] == "openai/gpt-5.6"
    assert control["provider"] == provider
    assert control["source"] == "unverified"
    assert control["options"] == []
