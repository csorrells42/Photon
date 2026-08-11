"""Write-only Workbench credential scope backed by the native reverse channel.

This source deliberately has no cache and no local fallback.  Configuration
contains only opaque references, revisions, and exact purposes.  The source is
never registered with the legacy startup source orchestrator because that path
writes resolved values into ``os.environ``.  Authenticated Workbench mode calls
``resolve_scope`` only after the desktop reverse channel is live and installs
the returned mapping in the per-profile secret ContextVar.  Standalone Hermes
keeps its existing secret sources unchanged.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Dict, Tuple

from agent.secret_sources.base import ErrorKind, FetchResult, SecretSource, is_valid_env_name
from hermes_cli.workbench_credentials import (
    LeaseReference,
    WorkbenchCredentialError,
    resolve_lease_blocking,
)


class WorkbenchSecretSource(SecretSource):
    name = "workbench"
    label = "Photon native Connections"
    shape = "mapped"
    scheme = "hcv2"

    def fetch(self, cfg: dict, home_path: Path) -> FetchResult:
        """Reject the legacy registry path, which would populate os.environ."""
        result = FetchResult()
        result.error = "Workbench credentials require an authenticated profile scope."
        result.error_kind = ErrorKind.AUTH_FAILED
        return result

    def resolve_scope(self, cfg: dict) -> Dict[str, str]:
        """Resolve one atomic, context-local profile mapping after channel auth."""
        profile_id, references, timeout = _validate_config(cfg)
        if not references:
            raise WorkbenchCredentialError("not_configured", "No native credential references are configured.")

        leases = []
        try:
            for env_name, reference in references.items():
                leases.append((env_name, resolve_lease_blocking(profile_id, reference, timeout)))
            return {env_name: lease.text() for env_name, lease in leases}
        finally:
            for _env_name, lease in leases:
                lease.close()

    def is_enabled(self, cfg: dict) -> bool:
        return bool(isinstance(cfg, dict) and cfg.get("enabled") is True)

    def override_existing(self, cfg: dict) -> bool:
        return False

    def fetch_timeout_seconds(self, cfg: dict) -> float:
        try:
            value = float((cfg or {}).get("timeout_seconds", 10.0))
        except (TypeError, ValueError):
            return 10.0
        return value if 0 < value <= 120 else 10.0

    def config_schema(self) -> dict:
        return {
            "enabled": {"description": "Use the native Workbench credential broker.", "default": False},
            "profile_id": {"description": "Exact authenticated profile identifier.", "default": "default"},
            "env": {"description": "Environment names mapped to opaque native references.", "default": {}},
        }

    def remediation(self, kind: ErrorKind | None, cfg: dict) -> str:
        if kind in {ErrorKind.AUTH_FAILED, ErrorKind.AUTH_EXPIRED, ErrorKind.NETWORK, ErrorKind.TIMEOUT}:
            return "Reconnect the native Workbench session and retry."
        if kind in {ErrorKind.NOT_CONFIGURED, ErrorKind.REF_INVALID}:
            return "Open Connections & Credentials in Photon and reconnect this provider."
        return ""


def _validate_config(cfg: Any) -> Tuple[str, Dict[str, LeaseReference], float]:
    if not isinstance(cfg, dict):
        raise WorkbenchCredentialError("invalid_config", "The native credential configuration is invalid.")
    profile_id = cfg.get("profile_id", "default")
    if not isinstance(profile_id, str) or not profile_id:
        raise WorkbenchCredentialError("invalid_profile", "The native credential profile is invalid.")
    timeout = WorkbenchSecretSource().fetch_timeout_seconds(cfg)
    raw_env = cfg.get("env")
    if raw_env is None:
        raw_env = {}
    if not isinstance(raw_env, dict) or len(raw_env) > 128:
        raise WorkbenchCredentialError("invalid_mapping", "The native credential mapping is invalid.")
    references: Dict[str, LeaseReference] = {}
    for env_name, raw in raw_env.items():
        if not isinstance(env_name, str) or not is_valid_env_name(env_name) or not isinstance(raw, dict):
            raise WorkbenchCredentialError("invalid_mapping", "The native credential mapping is invalid.")
        if set(raw) != {"connection_ref", "purpose", "revision"}:
            raise WorkbenchCredentialError("invalid_mapping", "The native credential mapping is invalid.")
        reference = LeaseReference(
            connection_ref=raw.get("connection_ref"),
            purpose=raw.get("purpose"),
            revision=raw.get("revision"),
        ).validate()
        references[env_name] = reference
    return profile_id, references, timeout
