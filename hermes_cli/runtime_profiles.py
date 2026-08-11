"""Truthful named runtime profiles for custom model endpoints.

Profiles are omission based: only fields the operator explicitly stores are
returned as request overrides.  An absent profile or field leaves the model
server authoritative.
"""

from __future__ import annotations

import math
import re
from typing import Any, Dict


_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")
_DIRECT_NUMERIC = {
    "temperature": (-100.0, 100.0),
    "top_p": (0.0, 1.0),
    "frequency_penalty": (-100.0, 100.0),
    "presence_penalty": (-100.0, 100.0),
}
_EXTRA_NUMERIC = {
    "top_k": (0.0, 1_000_000.0),
    "min_p": (0.0, 1.0),
    "repeat_penalty": (0.0, 100.0),
}
_INTEGER_FIELDS = {"max_tokens": (1, 16_777_216), "seed": (-2_147_483_648, 2_147_483_647)}


class RuntimeProfileError(ValueError):
    pass


def require_identifier(value: Any, label: str) -> str:
    result = str(value or "").strip()
    if not _IDENTIFIER.fullmatch(result):
        raise RuntimeProfileError(f"{label} is invalid")
    return result


def normalize_overrides(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict) or len(value) > 16:
        raise RuntimeProfileError("runtime profile overrides are invalid")
    result: Dict[str, Any] = {}
    for key, raw in value.items():
        if key in _DIRECT_NUMERIC or key in _EXTRA_NUMERIC:
            if isinstance(raw, bool) or not isinstance(raw, (int, float)) or not math.isfinite(float(raw)):
                raise RuntimeProfileError(f"runtime profile field {key} is invalid")
            minimum, maximum = (_DIRECT_NUMERIC | _EXTRA_NUMERIC)[key]
            number = float(raw)
            if number < minimum or number > maximum:
                raise RuntimeProfileError(f"runtime profile field {key} is invalid")
            result[key] = number
        elif key in _INTEGER_FIELDS:
            if isinstance(raw, bool) or not isinstance(raw, int):
                raise RuntimeProfileError(f"runtime profile field {key} is invalid")
            minimum, maximum = _INTEGER_FIELDS[key]
            if raw < minimum or raw > maximum:
                raise RuntimeProfileError(f"runtime profile field {key} is invalid")
            result[key] = raw
        elif key == "stop":
            if not isinstance(raw, list) or not 1 <= len(raw) <= 8:
                raise RuntimeProfileError("runtime profile stop sequences are invalid")
            stops = []
            for item in raw:
                if not isinstance(item, str) or not item or len(item) > 256 or "\0" in item:
                    raise RuntimeProfileError("runtime profile stop sequences are invalid")
                stops.append(item)
            result[key] = stops
        else:
            raise RuntimeProfileError(f"runtime profile field {key} is unsupported")
    return result


def upsert_profile(entry: Dict[str, Any], body: Any) -> str:
    profile_id = require_identifier(getattr(body, "id", ""), "runtime profile id")
    name = str(getattr(body, "name", "") or "").strip()
    model = str(getattr(body, "model", "") or "").strip()
    if not name or len(name) > 120 or not model or len(model) > 512:
        raise RuntimeProfileError("runtime profile name or model is invalid")
    overrides = normalize_overrides(getattr(body, "overrides", None))
    profiles = entry.get("runtime_profiles")
    profiles = dict(profiles) if isinstance(profiles, dict) else {}
    profiles[profile_id] = {"name": name, "model": model, "overrides": overrides}
    entry["runtime_profiles"] = profiles
    if bool(getattr(body, "make_active", False)):
        active = entry.get("active_runtime_profiles")
        active = dict(active) if isinstance(active, dict) else {}
        active[model] = profile_id
        entry["active_runtime_profiles"] = active
    return profile_id


def delete_profile(entry: Dict[str, Any], profile_id: str) -> None:
    profile_id = require_identifier(profile_id, "runtime profile id")
    profiles = entry.get("runtime_profiles")
    if not isinstance(profiles, dict) or profile_id not in profiles:
        raise KeyError(profile_id)
    profiles = dict(profiles)
    profiles.pop(profile_id, None)
    entry["runtime_profiles"] = profiles
    active = entry.get("active_runtime_profiles")
    if isinstance(active, dict):
        entry["active_runtime_profiles"] = {
            str(model): str(active_id)
            for model, active_id in active.items()
            if str(active_id) != profile_id
        }


def activate_profile(entry: Dict[str, Any], model: str, profile_id: str | None) -> None:
    model = str(model or "").strip()
    if not model or len(model) > 512:
        raise RuntimeProfileError("runtime profile model is invalid")
    active = entry.get("active_runtime_profiles")
    active = dict(active) if isinstance(active, dict) else {}
    if profile_id is None:
        active.pop(model, None)
    else:
        profile_id = require_identifier(profile_id, "runtime profile id")
        profiles = entry.get("runtime_profiles")
        profile = profiles.get(profile_id) if isinstance(profiles, dict) else None
        if not isinstance(profile, dict) or str(profile.get("model") or "") != model:
            raise RuntimeProfileError("runtime profile does not belong to this model")
        active[model] = profile_id
    entry["active_runtime_profiles"] = active


def profile_response(entry: Dict[str, Any]) -> list[Dict[str, Any]]:
    profiles = entry.get("runtime_profiles")
    active = entry.get("active_runtime_profiles")
    active = active if isinstance(active, dict) else {}
    result = []
    if not isinstance(profiles, dict) or len(profiles) > 256:
        return result
    for profile_id, raw in profiles.items():
        if not _IDENTIFIER.fullmatch(str(profile_id)) or not isinstance(raw, dict):
            continue
        try:
            overrides = normalize_overrides(raw.get("overrides", {}))
        except RuntimeProfileError:
            continue
        model = str(raw.get("model") or "").strip()
        name = str(raw.get("name") or "").strip()
        if not model or not name:
            continue
        result.append({
            "id": str(profile_id),
            "name": name[:120],
            "model": model[:512],
            "overrides": overrides,
            "is_active": str(active.get(model) or "") == str(profile_id),
        })
    return sorted(result, key=lambda item: (item["model"].lower(), item["name"].lower(), item["id"]))


def selected_profile_request_overrides(entry: Dict[str, Any]) -> Dict[str, Any]:
    model = str(entry.get("model") or entry.get("default_model") or "").strip()
    active = entry.get("active_runtime_profiles")
    profiles = entry.get("runtime_profiles")
    profile_id = active.get(model) if isinstance(active, dict) else None
    profile = profiles.get(profile_id) if isinstance(profiles, dict) and profile_id else None
    if not isinstance(profile, dict) or str(profile.get("model") or "") != model:
        return {}
    values = normalize_overrides(profile.get("overrides", {}))
    result = {key: values[key] for key in (*_DIRECT_NUMERIC, *_INTEGER_FIELDS, "stop") if key in values}
    extra = {key: values[key] for key in _EXTRA_NUMERIC if key in values}
    if extra:
        result["extra_body"] = extra
    return result
