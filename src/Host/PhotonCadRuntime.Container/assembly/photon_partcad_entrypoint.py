"""Photon's fixed offline, method-restricted PartCAD stdio entrypoint."""

from __future__ import annotations

import pathlib

from partcad_service_json_rpc.core.session import Session
from partcad_service_json_rpc.rpc.methods import build_registry
from partcad_service_json_rpc.transport.stdio import serve_stdio


WORKSPACE = pathlib.Path("/workspace")

# No installer, updater, package refresh/load, provider discovery, daemon,
# telemetry, HTTP, arbitrary mutation/conversion, lint/test execution, or
# remote-package methods. Geometry authoring belongs to the separate geometry
# worker; this service reads local projects and emits reviewed interchange.
ALLOWED_METHODS = frozenset(
    {
        "context.create",
        "export.assembly",
        "export.part",
        "healthcheck",
        "info.object",
        "inspect.assembly",
        "inspect.object",
        "inspect.part",
        "list.objects",
        "list.packages",
        "version",
    }
)


def _lock_offline_policy(session: Session) -> None:
    """Load once, then assert the settings PartCAD itself actually reads."""

    partcad = session.ensure_partcad()
    user_config = partcad.user_config
    user_config.offline = True
    user_config.force_update = False
    user_config.python_sandbox = "none"
    if (
        user_config.offline is not True
        or user_config.force_update is not False
        or user_config.python_sandbox != "none"
    ):
        raise RuntimeError("Photon could not lock PartCAD into offline immutable mode")


def main() -> None:
    if not WORKSPACE.is_dir() or WORKSPACE.is_symlink():
        raise SystemExit("required Photon CAD workspace is unavailable")

    complete_registry = build_registry()
    missing = ALLOWED_METHODS.difference(complete_registry)
    if missing:
        raise SystemExit(f"pinned PartCAD registry is missing expected methods: {sorted(missing)}")

    registry = {name: complete_registry[name] for name in sorted(ALLOWED_METHODS)}
    session = Session(
        settings={
            "forceUpdate": "false",
            "pythonSandbox": "none",
            "verbosity": "error",
        }
    )
    session.start_remote_log(log_file=None)
    # Session.load_partcad() currently writes python_runtime while PartCAD reads
    # python_sandbox, and it ignores the offline setting. Apply the authoritative
    # values after the sole load. The reload-capable `activate` RPC is absent.
    _lock_offline_policy(session)
    serve_stdio(session, registry)


if __name__ == "__main__":
    main()
