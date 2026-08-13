#!/usr/bin/env python3
"""Run typed Windows developer operations through the Hermes desktop host.

The agent itself may be running inside Linux.  This tool deliberately does not
spawn a shell or guess which SDK is available there.  It asks the authenticated
desktop renderer to invoke the existing native DeveloperServices bridge and
returns the bounded typed result.
"""

import json
from typing import Callable, Optional

from tools.approval import request_tool_approval
from tools.registry import registry, tool_error


_ACTIONS = {"describe", "build", "analyze", "targets", "run", "stop", "administrator"}
_CONFIGURATIONS = {"Debug", "Release"}


def windows_developer_tool(
    action: str,
    project_path: Optional[str] = None,
    program_path: Optional[str] = None,
    arguments: Optional[list[str]] = None,
    configuration: str = "Debug",
    script: Optional[str] = None,
    reason: Optional[str] = None,
    callback: Optional[Callable] = None,
) -> str:
    """Dispatch one native developer-services operation and return JSON."""
    normalized_action = str(action or "").strip().lower()
    if normalized_action not in _ACTIONS:
        return tool_error("action must be one of: describe, build, analyze, targets, run, stop, administrator.")
    if callback is None:
        return tool_error("windows_developer is only available in the Hermes desktop app.")

    normalized_configuration = str(configuration or "Debug").strip().title()
    if normalized_configuration not in _CONFIGURATIONS:
        return tool_error("configuration must be Debug or Release.")

    normalized_path = None
    if normalized_action in {"build", "analyze"}:
        normalized_path = str(project_path or "").strip().replace("\\", "/")
        if (
            not normalized_path
            or len(normalized_path) > 2048
            or normalized_path.startswith("/")
            or ":" in normalized_path
            or any(part in {"", ".", ".."} for part in normalized_path.split("/"))
            or not normalized_path.lower().endswith((".sln", ".slnx", ".csproj"))
        ):
            return tool_error(
                "project_path must be a workspace-relative .sln, .slnx, or .csproj path."
            )

    normalized_program = None
    if normalized_action == "run":
        normalized_program = str(program_path or "").strip().replace("\\", "/")
        if (
            not normalized_program
            or len(normalized_program) > 2048
            or normalized_program.startswith("/")
            or ":" in normalized_program
            or any(part in {"", ".", ".."} for part in normalized_program.split("/"))
            or not normalized_program.lower().endswith((".exe", ".dll"))
        ):
            return tool_error("program_path must be a discovered workspace-relative .exe or .dll path.")

    normalized_arguments = []
    if arguments is not None:
        if not isinstance(arguments, list) or len(arguments) > 32:
            return tool_error("arguments must be an array containing at most 32 strings.")
        normalized_arguments = [str(value) for value in arguments]
        if any(len(value) > 1024 or "\0" in value for value in normalized_arguments):
            return tool_error("Each program argument must be at most 1024 characters and contain no NUL.")

    normalized_script = None
    normalized_reason = None
    if normalized_action == "administrator":
        normalized_script = str(script or "").strip()
        normalized_reason = str(reason or "").strip()
        if not normalized_script or len(normalized_script) > 16 * 1024 or "\0" in normalized_script:
            return tool_error("script is required, must be at most 16384 characters, and contain no NUL.")
        if not normalized_reason or len(normalized_reason) > 512 or "\0" in normalized_reason:
            return tool_error("reason is required, must be at most 512 characters, and contain no NUL.")
        approval = request_tool_approval(
            "windows_administrator",
            f"Photon requests Windows Administrator access: {normalized_reason}\n\nOperation:\n{normalized_script}",
            rule_key="windows-administrator-uac",
        )
        if not approval.get("approved"):
            return tool_error(approval.get("message") or "Windows Administrator access was not approved.")

    try:
        raw = callback(
            action=normalized_action,
            project_path=normalized_path,
            program_path=normalized_program,
            arguments=normalized_arguments,
            configuration=normalized_configuration,
            script=normalized_script,
            reason=normalized_reason,
        )
    except Exception as exc:
        return tool_error(f"Windows developer operation failed: {exc}")

    if not raw:
        return tool_error("The Windows developer host did not return a result before the timeout.")
    try:
        return json.dumps(json.loads(raw), ensure_ascii=False)
    except (TypeError, ValueError):
        return tool_error("The Windows developer host returned an invalid result.")


WINDOWS_DEVELOPER_SCHEMA = {
    "name": "windows_developer",
    "description": (
        "Use the Hermes desktop's native Windows developer host. Use describe to "
        "discover workspace .sln/.slnx/.csproj targets and provider availability; "
        "use build or analyze to run the selected workspace-relative target with "
        "the Windows .NET SDK; use targets, run, and stop to launch a discovered "
        "Windows program through the typed debugger host. Prefer this over installing or invoking .NET inside "
        "the Linux agent container for Windows/WPF projects. Use administrator only "
        "when a specific Windows operation genuinely requires elevation; it presents "
        "both the Hermes approval card and Windows UAC consent and never accepts a password. "
        "Hermes itself runs as root inside its Linux container, so use the normal terminal "
        "for container-internal package, configuration, or workspace repair. Use administrator "
        "for Docker Desktop, Windows service, host filesystem, or host Docker CLI repair that "
        "cannot be performed inside the live container. If the Hermes container is already dead, "
        "report that the desktop launcher must restore it before Photon can resume. "
        "Returns bounded JSON with success, diagnostics, and output."
    ),
    "parameters": {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["describe", "build", "analyze", "targets", "run", "stop", "administrator"],
            },
            "project_path": {
                "type": "string",
                "description": (
                    "Workspace-relative .sln, .slnx, or .csproj path. Required for build/analyze."
                ),
            },
            "configuration": {
                "type": "string",
                "enum": ["Debug", "Release"],
                "default": "Debug",
            },
            "program_path": {
                "type": "string",
                "description": "A workspace-relative .exe or .dll returned by targets. Required for run.",
            },
            "arguments": {
                "type": "array",
                "items": {"type": "string"},
                "maxItems": 32,
                "description": "Optional bounded program arguments for run.",
            },
            "script": {
                "type": "string",
                "maxLength": 16384,
                "description": "PowerShell operation to execute after explicit Hermes approval and Windows UAC consent. Required for administrator.",
            },
            "reason": {
                "type": "string",
                "maxLength": 512,
                "description": "Plain-language explanation shown to the user. Required for administrator.",
            },
        },
        "required": ["action"],
    },
}


registry.register(
    name="windows_developer",
    toolset="desktop_ui",
    schema=WINDOWS_DEVELOPER_SCHEMA,
    handler=lambda args, **kw: windows_developer_tool(
        action=args.get("action", ""),
        project_path=args.get("project_path"),
        program_path=args.get("program_path"),
        arguments=args.get("arguments"),
        configuration=args.get("configuration", "Debug"),
        script=args.get("script"),
        reason=args.get("reason"),
        callback=kw.get("callback"),
    ),
    emoji="🪟",
)
