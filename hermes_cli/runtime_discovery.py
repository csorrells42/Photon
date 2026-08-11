"""Bounded, truthful projection of model-server discovery metadata.

The OpenAI-compatible model list remains the portability baseline.  When a
server also exposes LM Studio's native API, this module projects only fields
the server actually reported.  Missing fields stay missing; callers must not
invent defaults.
"""

from __future__ import annotations

import math
import urllib.parse
from typing import Any, Dict, List, Optional


_TEXT_LIMIT = 512
_MAX_MODELS = 2_000
_MAX_LOADED_INSTANCES = 16
_LOADED_BOOLEAN_FIELDS = {
    "flash_attention",
    "offload_kv_cache_to_gpu",
    "speculative_draft_mtp",
    "speculative_draft_simple",
}
_LOADED_INTEGER_FIELDS = {
    "context_length",
    "context_checkpoints",
    "eval_batch_size",
    "num_experts",
    "parallel",
    "physical_batch_size",
    "speculative_draft_max_tokens",
    "speculative_draft_min_tokens",
}
_LOADED_NUMBER_FIELDS = {"speculative_draft_min_continue_probability"}
_LOADED_TEXT_FIELDS = {
    "reasoning_budget_message",
    "speculative_draft_model",
}


def _text(value: Any, maximum: int = _TEXT_LIMIT) -> Optional[str]:
    if not isinstance(value, str):
        return None
    result = value.strip()
    return result if result and len(result) <= maximum else None


def _integer(value: Any, minimum: int = 0, maximum: int = 2**53 - 1) -> Optional[int]:
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value if minimum <= value <= maximum else None


def _number(value: Any, minimum: float = -1_000_000.0, maximum: float = 1_000_000.0) -> Optional[float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    result = float(value)
    return result if math.isfinite(result) and minimum <= result <= maximum else None


def native_models_url(openai_base_url: str) -> Optional[str]:
    """Return the same-origin LM Studio native model-list URL."""
    try:
        parsed = urllib.parse.urlsplit(str(openai_base_url or "").strip())
    except Exception:
        return None
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        return None
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        return None
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, "/api/v1/models", "", ""))


def _loaded_config(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict):
        return {}
    result: Dict[str, Any] = {}
    for key in _LOADED_BOOLEAN_FIELDS:
        if isinstance(value.get(key), bool):
            result[key] = value[key]
    for key in _LOADED_INTEGER_FIELDS:
        candidate = _integer(value.get(key), 0, 16_777_216)
        if candidate is not None:
            result[key] = candidate
    for key in _LOADED_NUMBER_FIELDS:
        candidate = _number(value.get(key))
        if candidate is not None:
            result[key] = candidate
    for key in _LOADED_TEXT_FIELDS:
        candidate = _text(value.get(key), 1_024)
        if candidate is not None:
            result[key] = candidate
        elif value.get(key) == "":
            result[key] = ""
    return result


def _reasoning(value: Any) -> Optional[Dict[str, Any]]:
    if not isinstance(value, dict):
        return None
    options = value.get("allowed_options")
    if not isinstance(options, list) or len(options) > 16:
        return None
    allowed = []
    for option in options:
        text = _text(option, 64)
        if text and text not in allowed:
            allowed.append(text)
    default = _text(value.get("default"), 64)
    if not allowed:
        return None
    return {"allowed_options": allowed, **({"default": default} if default in allowed else {})}


def normalize_lm_studio_models(payload: Any) -> List[Dict[str, Any]]:
    if not isinstance(payload, dict):
        return []
    raw_models = payload.get("models")
    if not isinstance(raw_models, list) or len(raw_models) > _MAX_MODELS:
        return []
    result: List[Dict[str, Any]] = []
    seen: set[str] = set()
    for raw in raw_models:
        if not isinstance(raw, dict):
            continue
        key = _text(raw.get("key"))
        model_type = _text(raw.get("type"), 64)
        if not key or not model_type or key in seen:
            continue
        seen.add(key)
        item: Dict[str, Any] = {"id": key, "type": model_type}
        for source, destination, maximum in (
            ("display_name", "display_name", 256),
            ("publisher", "publisher", 128),
            ("architecture", "architecture", 128),
            ("format", "format", 64),
            ("params_string", "parameters", 64),
            ("selected_variant", "selected_variant", 512),
        ):
            candidate = _text(raw.get(source), maximum)
            if candidate is not None:
                item[destination] = candidate
        maximum_context = _integer(raw.get("max_context_length"), 1, 16_777_216)
        if maximum_context is not None:
            item["max_context_length"] = maximum_context
        size_bytes = _integer(raw.get("size_bytes"), 0, 2**63 - 1)
        if size_bytes is not None:
            item["size_bytes"] = size_bytes
        quantization = raw.get("quantization")
        if isinstance(quantization, dict):
            quantization_name = _text(quantization.get("name"), 64)
            bits = _number(quantization.get("bits_per_weight"), 0.0, 64.0)
            projected = {}
            if quantization_name is not None:
                projected["name"] = quantization_name
            if bits is not None:
                projected["bits_per_weight"] = bits
            if projected:
                item["quantization"] = projected
        capabilities = raw.get("capabilities")
        if isinstance(capabilities, dict):
            projected_capabilities: Dict[str, Any] = {}
            for field in ("vision", "trained_for_tool_use"):
                if isinstance(capabilities.get(field), bool):
                    projected_capabilities[field] = capabilities[field]
            reasoning = _reasoning(capabilities.get("reasoning"))
            if reasoning is not None:
                projected_capabilities["reasoning"] = reasoning
            if projected_capabilities:
                item["capabilities"] = projected_capabilities
        instances = raw.get("loaded_instances")
        projected_instances = []
        if isinstance(instances, list) and len(instances) <= _MAX_LOADED_INSTANCES:
            for instance in instances:
                if not isinstance(instance, dict):
                    continue
                instance_id = _text(instance.get("id"))
                if not instance_id:
                    continue
                projected_instances.append({"id": instance_id, "config": _loaded_config(instance.get("config"))})
        item["loaded_instances"] = projected_instances
        item["loaded"] = bool(projected_instances)
        result.append(item)
    return result
