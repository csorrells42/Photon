"""Model-bound reasoning controls for the desktop/TUI model picker.

The runtime accepts a broad Hermes vocabulary, but each provider adapter maps
only a subset of it onto a model's real wire contract.  This module publishes
the distinct, safe choices for one exact provider/model pair.  Unknown pairs
fail closed: callers get no invented effort ladder and leave the model at its
own default.
"""

from __future__ import annotations

import re
from typing import Any


_EFFORTS = ("minimal", "low", "medium", "high", "xhigh", "max", "ultra")
_OPENROUTER_EFFORTS = ("minimal", "low", "medium", "high", "xhigh", "max")
_ALLOWED = ("none", "enabled", *_EFFORTS)


def _control(
    provider: str,
    model: str,
    *,
    label: str,
    options: tuple[str, ...] = (),
    aliases: dict[str, str] | None = None,
    default_effort: str | None = None,
    default_enabled: bool | None = None,
    mandatory: bool = False,
    option_labels: dict[str, str] | None = None,
    source: str = "compatibility",
    supports_effort: bool | None = None,
    supports_toggle: bool | None = None,
) -> dict[str, Any]:
    distinct = [option for option in dict.fromkeys(options) if option in _ALLOWED]
    safe_aliases = {
        source_effort: target_effort
        for source_effort, target_effort in (aliases or {}).items()
        if source_effort in _ALLOWED and target_effort in distinct
    }
    safe_labels = {
        effort: str(text)[:32]
        for effort, text in (option_labels or {}).items()
        if effort in distinct and str(text).strip()
    }
    enabled_options = [option for option in distinct if option not in {"none", "enabled"}]
    inferred_toggle = "none" in distinct or "enabled" in distinct
    inferred_effort = len(enabled_options) > 1 or (
        len(enabled_options) == 1 and "enabled" not in distinct
    )
    safe_default_effort = (
        default_effort if default_effort in distinct and default_effort in _EFFORTS else None
    )
    return {
        "model": model,
        "provider": provider,
        "label": label,
        "options": distinct,
        "effort_aliases": safe_aliases,
        "option_labels": safe_labels,
        "default_effort": safe_default_effort,
        "default_enabled": default_enabled if isinstance(default_enabled, bool) else None,
        "mandatory": bool(mandatory),
        "supports_effort": inferred_effort if supports_effort is None else bool(supports_effort),
        "supports_toggle": inferred_toggle if supports_toggle is None else bool(supports_toggle),
        "source": source if distinct or source in {"unsupported", "server-managed"} else "unverified",
    }


def _label(provider: str, model: str) -> str:
    identity = f"{provider} {model}".lower()
    if any(token in identity for token in (
        "lmstudio", "lm-studio", "ollama", "local", "deepseek", "qwen",
        "glm", "gpt-oss", "anthropic", "claude", "gemini", "gemma", "kimi",
    )):
        return "Thinking"
    if any(token in identity for token in ("openai", "codex", "gpt-5", "o1", "o3", "o4")):
        return "Effort"
    return "Reasoning"


def _bare_model(model: str) -> str:
    return re.sub(r"[_.]+", "-", model.strip().lower().rsplit("/", 1)[-1])


def _lm_studio_control(
    provider: str,
    model: str,
    base_url: str,
    api_key: str | None,
) -> dict[str, Any] | None:
    identity = f"{provider} {model}".lower()
    if not base_url or not (
        "lmstudio" in identity or "lm-studio" in identity or ":1234" in base_url.lower()
    ):
        return None

    from hermes_cli.models import lmstudio_model_reasoning_metadata

    metadata = lmstudio_model_reasoning_metadata(model, base_url, api_key=api_key)
    if metadata is None:
        return None
    if metadata.get("supported") is False:
        return _control(provider, model, label="Thinking", source="unsupported")
    published = metadata.get("allowed_options") or []
    normalized: list[str] = []
    labels: dict[str, str] = {}
    raw_normalized: dict[str, str] = {}
    for raw_option in published:
        option = str(raw_option).strip().lower()
        candidate = "none" if option in {"off", "disabled"} else "enabled" if option in {"on", "enabled"} else option
        if candidate not in _ALLOWED or candidate in normalized:
            continue
        normalized.append(candidate)
        raw_normalized[option] = candidate
        if option in {"off", "disabled"}:
            labels[candidate] = "Off"
        elif option in {"on", "enabled"}:
            labels[candidate] = "On"
    if not normalized:
        return None
    if normalized == ["none"]:
        return _control(provider, model, label="Thinking", source="unsupported")
    raw_default = str(metadata.get("default") or "").strip().lower()
    normalized_default = raw_normalized.get(raw_default, raw_default)
    default_enabled = None if not raw_default else normalized_default != "none"
    default_effort = normalized_default if normalized_default in _EFFORTS else None
    toggle_options = {"none", "enabled"}
    supports_effort = any(option not in toggle_options for option in normalized)
    return _control(
        provider,
        model,
        label="Thinking",
        options=tuple(normalized),
        default_effort=default_effort,
        default_enabled=default_enabled,
        option_labels=labels,
        source="server",
        supports_effort=supports_effort,
        supports_toggle=bool(toggle_options.intersection(normalized)),
    )


def _openrouter_control(provider: str, model: str) -> dict[str, Any] | None:
    if provider.strip().lower() != "openrouter":
        return None

    from hermes_cli.models import openrouter_model_reasoning_metadata

    metadata = openrouter_model_reasoning_metadata(model)
    if metadata is None:
        return None
    label = _label(provider, model)
    if metadata.get("supported") is False:
        return _control(provider, model, label=label, source="unsupported")
    # ``supported_parameters: ["reasoning"]`` is only a coarse signal. It
    # cannot prove the exact effort ladder, whether Off is legal, or whether
    # reasoning is mandatory. Only the catalog's reasoning object is safe.
    if metadata.get("metadata_complete") is not True:
        return None

    mandatory = metadata.get("mandatory") is True
    raw_efforts_present = "supported_efforts" in metadata
    raw_efforts = metadata.get("supported_efforts")
    efforts = (
        list(_OPENROUTER_EFFORTS)
        if raw_efforts_present and raw_efforts is None
        else [str(value).strip().lower() for value in raw_efforts]
        if isinstance(raw_efforts, list)
        else []
    )
    efforts = [value for value in efforts if value in _OPENROUTER_EFFORTS]
    options: list[str] = []
    labels: dict[str, str] = {}
    if not mandatory:
        options.append("none")
        labels["none"] = "Off"
    options.extend(efforts)
    if not efforts and not mandatory:
        options.append("enabled")
        labels["enabled"] = "On"

    default_effort = str(metadata.get("default_effort") or "").strip().lower()
    return _control(
        provider,
        model,
        label=("Reasoning" if label == "Effort" and not mandatory and efforts else label),
        options=tuple(options),
        default_effort=default_effort,
        default_enabled=metadata.get("default_enabled"),
        mandatory=mandatory,
        option_labels=labels,
        source="server",
        supports_effort=bool(efforts),
        supports_toggle=not mandatory,
    )


def _docker_model_runner_control(provider: str, model: str, base_url: str) -> dict[str, Any] | None:
    identity = f"{provider} {model} {base_url}".lower()
    if ":12434" not in identity and "model-runner" not in identity and "model runner" not in identity:
        return None
    return _control(
        provider,
        model,
        label="Reasoning",
        source="server-managed",
    )


def _openai_control(provider: str, model: str) -> dict[str, Any] | None:
    bare = _bare_model(model)
    if not (bare.startswith("gpt-5") or bare.startswith(("o1", "o3", "o4"))):
        return None
    if bare.startswith("gpt-5-6"):
        return _control(
            provider,
            model,
            label="Reasoning",
            options=("none", "low", "medium", "high", "xhigh", "max"),
            default_effort="medium",
            default_enabled=True,
            aliases={"minimal": "low", "ultra": "max"},
            supports_effort=True,
            supports_toggle=True,
        )
    if bare.startswith("gpt-5"):
        return _control(
            provider,
            model,
            label="Effort",
            options=("minimal", "low", "medium", "high"),
            aliases={"xhigh": "high", "max": "high", "ultra": "high"},
        )
    return _control(
        provider,
        model,
        label="Effort",
        options=("low", "medium", "high"),
        aliases={"minimal": "low", "xhigh": "high", "max": "high", "ultra": "high"},
    )


def _claude_control(provider: str, model: str) -> dict[str, Any] | None:
    bare = _bare_model(model)
    if "claude" not in bare:
        return None
    if "haiku" in bare and not any(token in bare for token in ("4.5", "4-5")):
        return _control(provider, model, label="Thinking", source="unsupported")
    if any(token in bare for token in ("4.7", "4-7")):
        return _control(
            provider,
            model,
            label="Thinking",
            options=("low", "medium", "high", "xhigh", "max"),
            default_enabled=True,
            aliases={"minimal": "low", "ultra": "max"},
            mandatory=True,
            supports_effort=True,
            supports_toggle=False,
        )
    if any(token in bare for token in ("4.6", "4-6")):
        return _control(
            provider,
            model,
            label="Thinking",
            options=("low", "medium", "high", "max"),
            default_enabled=True,
            aliases={"minimal": "low", "xhigh": "max", "ultra": "max"},
            mandatory=True,
            supports_effort=True,
            supports_toggle=False,
        )
    if any(token in bare for token in ("claude-3", "4.0", "4-0", "4.1", "4-1", "4.5", "4-5")):
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "low", "medium", "high", "xhigh"),
            aliases={"minimal": "low", "max": "xhigh", "ultra": "xhigh"},
            supports_effort=True,
            supports_toggle=True,
        )
    # The native adapter deliberately defaults unknown future Claude releases
    # to the modern adaptive contract. Keep the picker aligned with that wire
    # behavior instead of routing a new model through legacy manual thinking.
    return _control(
        provider,
        model,
        label="Thinking",
        options=("low", "medium", "high", "xhigh", "max"),
        default_enabled=True,
        aliases={"minimal": "low", "ultra": "max"},
        mandatory=True,
        supports_effort=True,
        supports_toggle=False,
    )


def _family_control(provider: str, model: str) -> dict[str, Any] | None:
    bare = _bare_model(model)
    provider_key = provider.strip().lower()

    if "deepseek-v" in bare and "deepseek-v3" not in bare:
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "high", "max"),
            aliases={
                "minimal": "high",
                "low": "high",
                "medium": "high",
                "xhigh": "max",
                "ultra": "max",
            },
            default_effort="high",
            default_enabled=True,
            option_labels={"none": "Off"},
            supports_effort=True,
            supports_toggle=True,
        )
    if any(token in bare for token in ("glm-5.2", "glm-5-2", "glm-5p2")):
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "high", "max"),
            aliases={"minimal": "high", "low": "high", "medium": "high", "xhigh": "max", "ultra": "max"},
            option_labels={"none": "Off"},
        )
    glm_version = re.match(r"^glm-(\d+)(?:[.-](\d+))?", bare)
    if glm_version and (int(glm_version.group(1)), int(glm_version.group(2) or 0)) >= (4, 5):
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "enabled"),
            aliases={"minimal": "enabled", "low": "enabled", "medium": "enabled", "high": "enabled", "xhigh": "enabled", "max": "enabled", "ultra": "enabled"},
            option_labels={"none": "Off", "enabled": "On"},
            supports_effort=False,
            supports_toggle=True,
        )
    if "kimi" in bare or "moonshot" in bare:
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "low", "medium", "high"),
            aliases={"minimal": "low", "xhigh": "high", "max": "high", "ultra": "high"},
        )
    if bare.startswith("gemini-3"):
        if "pro" in bare:
            return _control(
                provider,
                model,
                label="Thinking",
                options=("low", "high"),
                aliases={"minimal": "low", "medium": "low", "xhigh": "high", "max": "high", "ultra": "high"},
                default_enabled=True,
                mandatory=True,
                supports_effort=True,
                supports_toggle=False,
            )
        return _control(
            provider,
            model,
            label="Thinking",
            options=("low", "medium", "high"),
            aliases={"minimal": "low", "xhigh": "high", "max": "high", "ultra": "high"},
            default_enabled=True,
            mandatory=True,
            supports_effort=True,
            supports_toggle=False,
        )
    if bare.startswith(("gemini-2.5", "gemini-2-5")):
        if "flash" not in bare:
            return _control(
                provider,
                model,
                label="Thinking",
                options=("enabled",),
                default_enabled=True,
                mandatory=True,
                option_labels={"enabled": "On"},
                supports_effort=False,
                supports_toggle=False,
            )
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "enabled"),
            aliases={"minimal": "enabled", "low": "enabled", "medium": "enabled", "high": "enabled", "xhigh": "enabled", "max": "enabled", "ultra": "enabled"},
            option_labels={"none": "Off", "enabled": "On"},
            supports_effort=False,
            supports_toggle=True,
        )
    if provider_key in {"upstage", "solar"} and "solar-mini" not in bare and "syn-pro" not in bare:
        return _control(
            provider,
            model,
            label="Reasoning",
            options=("none", "low", "medium", "high"),
            aliases={"minimal": "none", "xhigh": "high", "max": "high", "ultra": "high"},
        )
    return None


def reasoning_control_for_model(
    provider: str,
    model: str,
    *,
    base_url: str = "",
    api_key: str | None = None,
    supports_reasoning: bool | None = None,
    selection_verified: bool = True,
) -> dict[str, Any]:
    """Return the exact bounded control for one selected provider/model."""
    provider = str(provider or "").strip()
    model = str(model or "").strip()
    label = _label(provider, model)
    if not provider or not model or not selection_verified:
        return _control(provider, model, label=label, source="unverified")

    docker_model_runner = _docker_model_runner_control(provider, model, base_url)
    if docker_model_runner is not None:
        return docker_model_runner

    lm_studio = _lm_studio_control(provider, model, base_url, api_key)
    if lm_studio is not None:
        return lm_studio

    provider_key = provider.lower()
    openrouter = _openrouter_control(provider, model)
    if openrouter is not None:
        return openrouter
    if provider_key == "openrouter":
        # Never fall through to a model-name guess for an aggregator. The same
        # slug can change upstream behavior without a Hermes release.
        return _control(
            provider,
            model,
            label="Reasoning",
            source="unsupported" if supports_reasoning is False else "unverified",
        )
    if provider_key in {"copilot", "github", "github-copilot"}:
        from hermes_cli.models import github_model_reasoning_efforts

        efforts = tuple(effort for effort in github_model_reasoning_efforts(model, api_key=api_key) if effort in _ALLOWED)
        if efforts:
            return _control(
                provider,
                model,
                label="Effort",
                options=efforts,
                source="server" if api_key else "compatibility",
            )

    aggregator = provider_key in {"openrouter", "nous", "ai-gateway", "opencode-go", "opencode-zen"}
    if aggregator and supports_reasoning is False:
        return _control(provider, model, label=label, source="unsupported")

    openai_routes = {"openai", "openai-api", "openai-codex", "openrouter", "nous", "ai-gateway"}
    claude_routes = {"anthropic", "openrouter", "nous", "ai-gateway"}
    family_routes = {
        "deepseek", "zai", "kimi", "kimi-coding", "kimi-coding-cn", "moonshot",
        "gemini", "google", "openrouter", "nous", "ai-gateway", "opencode-go",
        "opencode-zen", "opencode",
    }
    known = (
        _claude_control(provider, model) if provider_key in claude_routes else None
    ) or (
        _openai_control(provider, model) if provider_key in openai_routes else None
    ) or (
        _family_control(provider, model) if provider_key in family_routes else None
    )
    if known is not None:
        if provider_key == "nous" and "none" in known.get("options", []):
            # Nous' generic profile rejects reasoning.enabled=false and has no
            # proven alternate Off wire shape. Keep its real effort/on choices
            # while suppressing a control the transport cannot honor.
            known = dict(known)
            known["options"] = [option for option in known["options"] if option != "none"]
            known["effort_aliases"] = {
                source: target
                for source, target in known.get("effort_aliases", {}).items()
                if target != "none"
            }
            known["option_labels"] = {
                option: text
                for option, text in known.get("option_labels", {}).items()
                if option != "none"
            }
            known["supports_toggle"] = False
        return known

    if provider_key in {"xai", "xai-oauth"}:
        from agent.model_metadata import grok_supports_reasoning_effort

        if grok_supports_reasoning_effort(model):
            return _control(
                provider,
                model,
                label="Effort",
                options=("low", "medium", "high"),
                aliases={"minimal": "low", "xhigh": "high", "max": "high", "ultra": "high"},
            )

    if provider_key == "ollama-cloud" and supports_reasoning is True:
        return _control(
            provider,
            model,
            label="Thinking",
            options=("none", "low", "medium", "high", "max"),
            aliases={"minimal": "low", "xhigh": "max", "ultra": "max"},
        )
    if provider_key == "actual" and supports_reasoning is not False:
        return _control(
            provider,
            model,
            label="Reasoning",
            options=("none", "low", "medium", "high", "max"),
            aliases={"minimal": "low", "xhigh": "high", "ultra": "max"},
        )

    if supports_reasoning is False:
        return _control(provider, model, label=label, source="unsupported")
    return _control(provider, model, label=label, source="unverified")
