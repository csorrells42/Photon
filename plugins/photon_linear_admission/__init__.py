"""Photon's Linear connector with revision-bound admission.

The raw Linear MCP remains the source of issue truth. This plugin adds a
separate execution lane which is deliberately incapable of changing issue
state: it can inspect, request admission, post bounded progress, and record a
review handoff. Chris alone moves an issue to Done.
"""

from __future__ import annotations

import json
import threading
from dataclasses import dataclass
from typing import Any, Mapping

from tools.registry import registry, tool_error, tool_result

from .authority import (
    AdmissionError,
    PhotonLinearAuthority,
    issue_snapshot_from_payload,
    parse_linear_tool_payload,
)


_READ_TOOL = "mcp__linear__get_issue"
_COMMENT_TOOL = "mcp__linear__save_comment"
_RAW_LINEAR_PREFIX = "mcp__linear__"
_RAW_LINEAR_READ_ONLY = {
    "mcp__linear__get_issue",
    "mcp__linear__list_issues",
    "mcp__linear__get_project",
    "mcp__linear__list_projects",
    "mcp__linear__list_issue_statuses",
    "mcp__linear__list_issue_labels",
    "mcp__linear__get_issue_status",
}
_ADMISSION_CONTROL_TOOLS = {
    "tool_describe",
    "tool_search",
    "photon_linear_inspect",
    "photon_linear_begin",
    "photon_linear_direct_override",
    "photon_linear_status",
    "photon_linear_cancel",
}
_READ_CONTEXT_TOOLS = {
    "mem0_search",
}
_MEMORY_MUTATION_TOOLS = {
    "mem0_add",
    "mem0_update",
    "mem0_delete",
}
_WORK_TRACKING_MUTATION_TOOLS = {
    "photon_linear_progress",
    "photon_linear_complete",
}
_authority: PhotonLinearAuthority | None = None
_authority_lock = threading.Lock()


@dataclass(frozen=True)
class CapabilityPolicy:
    """Admission semantics for one model-visible tool action."""

    access_class: str
    mutation: bool
    requires_issue_admission: bool
    result_treatment: str = "ordinary_tool_result"


_READ_CONTEXT_POLICY = CapabilityPolicy(
    access_class="read_context",
    mutation=False,
    requires_issue_admission=False,
    result_treatment="untrusted_context",
)
_READ_WORK_POLICY = CapabilityPolicy(
    access_class="read_work",
    mutation=False,
    requires_issue_admission=False,
    result_treatment="untrusted_context",
)
_ADMISSION_CONTROL_POLICY = CapabilityPolicy(
    access_class="admission_control",
    mutation=False,
    requires_issue_admission=False,
)
_MEMORY_MUTATION_POLICY = CapabilityPolicy(
    access_class="memory_mutation",
    mutation=True,
    # Memory writes remain deliberately closed unless the exact admitted work
    # authorizes them. Keeping this as a distinct class prevents opening writes
    # merely because read-only recall is available.
    requires_issue_admission=True,
)
_WORK_TRACKING_MUTATION_POLICY = CapabilityPolicy(
    access_class="work_tracking_mutation",
    mutation=True,
    requires_issue_admission=True,
)
_ENGINEERING_EXECUTION_POLICY = CapabilityPolicy(
    access_class="engineering_execute",
    mutation=True,
    requires_issue_admission=True,
)


def capability_policy(tool_name: str) -> CapabilityPolicy:
    """Classify a tool by explicit action semantics, defaulting fail-closed."""
    if tool_name in _READ_CONTEXT_TOOLS:
        return _READ_CONTEXT_POLICY
    if tool_name in _RAW_LINEAR_READ_ONLY:
        return _READ_WORK_POLICY
    if tool_name in _ADMISSION_CONTROL_TOOLS:
        return _ADMISSION_CONTROL_POLICY
    if tool_name in _MEMORY_MUTATION_TOOLS:
        return _MEMORY_MUTATION_POLICY
    if tool_name in _WORK_TRACKING_MUTATION_TOOLS or tool_name.startswith(
        _RAW_LINEAR_PREFIX
    ):
        return _WORK_TRACKING_MUTATION_POLICY
    return _ENGINEERING_EXECUTION_POLICY


def _load_issue(issue_id: str):
    raw = registry.dispatch(_READ_TOOL, {"id": issue_id, "includeRelations": True})
    return issue_snapshot_from_payload(parse_linear_tool_payload(raw))


def _write_comment(issue_id: str, body: str) -> None:
    raw = registry.dispatch(_COMMENT_TOOL, {"issueId": issue_id, "body": body})
    payload = parse_linear_tool_payload(raw)
    if payload.get("success") is False or payload.get("error"):
        raise AdmissionError("Linear rejected the bounded progress comment.")


def _get_authority() -> PhotonLinearAuthority:
    global _authority
    with _authority_lock:
        if _authority is None:
            _authority = PhotonLinearAuthority(_load_issue, _write_comment)
        return _authority


def _session(kwargs: Mapping[str, Any]) -> str:
    session_id = str(kwargs.get("session_id") or "").strip()
    if not session_id:
        raise AdmissionError("A live Photon conversation session is required.")
    return session_id


def _safe(handler):
    def wrapped(args: dict, **kwargs):
        try:
            return tool_result(
                handler(args if isinstance(args, dict) else {}, **kwargs)
            )
        except AdmissionError as exc:
            return tool_error(
                str(exc), success=False, code="photon_linear_admission_rejected"
            )
        except Exception:
            return tool_error(
                "Photon Linear admission failed safely before work was authorized.",
                success=False,
                code="photon_linear_admission_unavailable",
            )

    return wrapped


@_safe
def _inspect(args: dict, **kwargs):
    session_id = _session(kwargs)
    return _get_authority().inspect(args.get("issueId"), session_id)


@_safe
def _begin(args: dict, **kwargs):
    session_id = _session(kwargs)
    return _get_authority().admit_dual(
        args.get("issueId"), args.get("issueRevision"), session_id
    )


@_safe
def _direct_override(args: dict, **kwargs):
    session_id = _session(kwargs)
    return _get_authority().admit_direct(
        args.get("issueId"), args.get("issueRevision"), session_id
    )


@_safe
def _status(args: dict, **kwargs):
    session_id = _session(kwargs)
    return _get_authority().status(
        args.get("issueId"), args.get("issueRevision"), session_id
    )


@_safe
def _progress(args: dict, **kwargs):
    return _get_authority().post_progress(
        args.get("admissionId"), args.get("issueId"), args.get("text"), _session(kwargs)
    )


@_safe
def _complete(args: dict, **kwargs):
    record = args.get("maintenanceRecord")
    if not isinstance(record, Mapping):
        raise AdmissionError("A structured maintenance record is required.")
    return _get_authority().complete(
        args.get("admissionId"), args.get("issueId"), record, _session(kwargs)
    )


@_safe
def _cancel(args: dict, **kwargs):
    return _get_authority().cancel(args.get("admissionId"), _session(kwargs))


def _pre_tool_call(
    tool_name: str = "",
    args: Mapping[str, Any] | None = None,
    session_id: str = "",
    **_: Any,
):
    values = args if isinstance(args, Mapping) else {}
    try:
        policy = capability_policy(tool_name)
        if tool_name == "photon_linear_begin":
            issue, challenge = _get_authority().prepare_dual(
                values.get("issueId"), values.get("issueRevision"), session_id
            )
            return {
                "action": "approve",
                "message": (
                    f"Chris approval required: authorize Photon once for {issue.identifier} "
                    f"at exact revision {issue.revision}? This consumes the matching Architect receipt."
                ),
                "rule_key": f"photon-linear:{issue.identifier}:{challenge}",
            }
        if tool_name == "photon_linear_direct_override":
            issue, challenge = _get_authority().prepare_direct(
                values.get("issueId"), values.get("issueRevision"), session_id
            )
            return {
                "action": "approve",
                "message": (
                    f"Direct Chris override: authorize Photon once for {issue.identifier} "
                    f"at exact revision {issue.revision} without Architect approval?"
                ),
                "rule_key": f"photon-linear-direct:{issue.identifier}:{challenge}",
            }
        if tool_name.startswith(_RAW_LINEAR_PREFIX) and policy.mutation:
            return {
                "action": "block",
                "message": (
                    "Raw Linear mutation is disabled for Photon. Use photon_linear_progress or "
                    "photon_linear_complete under an active exact-revision admission. Photon cannot close issues."
                ),
            }
        if policy.requires_issue_admission:
            if not session_id:
                raise AdmissionError(
                    "A live Photon conversation session is required for this capability."
                )
            _get_authority().enforce_session_tool(session_id)
    except AdmissionError as exc:
        return {"action": "block", "message": str(exc)}
    except Exception:
        return {
            "action": "block",
            "message": "Photon Linear admission could not be verified; no tool execution was authorized.",
        }
    return None


_ISSUE_FIELDS = {
    "type": "object",
    "properties": {
        "issueId": {
            "type": "string",
            "description": "Linear issue identifier, for example CLS-6.",
        },
        "issueRevision": {
            "type": "string",
            "description": "Exact revision returned by photon_linear_inspect.",
        },
    },
    "required": ["issueId", "issueRevision"],
    "additionalProperties": False,
}


def _tool_schema(
    name: str, description: str, parameters: Mapping[str, Any]
) -> dict[str, Any]:
    """Return the complete registry schema used by deferred tool discovery."""
    return {
        "name": name,
        "description": description,
        "parameters": dict(parameters),
    }


def register(ctx) -> None:
    global _authority
    # The dashboard and gateway share one runtime boot nonce. A new Photon
    # runtime rotates it, so prior receipts cannot cross the restart boundary.
    _authority = PhotonLinearAuthority(
        _load_issue,
        _write_comment,
        shared_runtime_boot=True,
    )
    ctx.register_hook("pre_tool_call", _pre_tool_call)
    inspect_description = "Inspect a Linear issue as context only and return its exact revision and Photon generation binding."
    ctx.register_tool(
        name="photon_linear_inspect",
        toolset="photon-linear",
        schema=_tool_schema(
            "photon_linear_inspect",
            inspect_description,
            {
                "type": "object",
                "properties": {"issueId": _ISSUE_FIELDS["properties"]["issueId"]},
                "required": ["issueId"],
                "additionalProperties": False,
            },
        ),
        handler=_inspect,
        description=inspect_description,
        emoji="🔎",
    )
    for name, handler, description in (
        (
            "photon_linear_begin",
            _begin,
            "Begin exact-revision work after Architect receipt plus visible Chris approval.",
        ),
        (
            "photon_linear_direct_override",
            _direct_override,
            "Begin exact-revision work after an explicit visible Chris override.",
        ),
        (
            "photon_linear_status",
            _status,
            "Inspect admission state without granting authority.",
        ),
    ):
        ctx.register_tool(
            name=name,
            toolset="photon-linear",
            schema=_tool_schema(name, description, _ISSUE_FIELDS),
            handler=handler,
            description=description,
            emoji="🛂",
        )
    ctx.register_tool(
        name="photon_linear_progress",
        toolset="photon-linear",
        schema=_tool_schema(
            "photon_linear_progress",
            "Post bounded progress to the exact admitted issue. Cannot change issue state.",
            {
                "type": "object",
                "properties": {
                    "admissionId": {"type": "string"},
                    "issueId": {"type": "string"},
                    "text": {"type": "string", "maxLength": 4096},
                },
                "required": ["admissionId", "issueId", "text"],
                "additionalProperties": False,
            },
        ),
        handler=_progress,
        description="Post bounded progress to the exact admitted issue. Cannot change issue state.",
        emoji="📝",
    )
    ctx.register_tool(
        name="photon_linear_complete",
        toolset="photon-linear",
        schema=_tool_schema(
            "photon_linear_complete",
            "Record bounded maintenance evidence and hand the issue to Chris for review; never closes it.",
            {
                "type": "object",
                "properties": {
                    "admissionId": {"type": "string"},
                    "issueId": {"type": "string"},
                    "maintenanceRecord": {
                        "type": "object",
                        "properties": {
                            "purpose": {"type": "string"},
                            "components": {
                                "type": "array",
                                "items": {"type": "string"},
                            },
                            "sourceAnchors": {
                                "type": "array",
                                "items": {"type": "string"},
                            },
                            "contract": {"type": "string"},
                            "tests": {"type": "array", "items": {"type": "string"}},
                            "limitations": {
                                "type": "array",
                                "items": {"type": "string"},
                            },
                        },
                        "required": [
                            "purpose",
                            "components",
                            "sourceAnchors",
                            "contract",
                            "tests",
                            "limitations",
                        ],
                        "additionalProperties": False,
                    },
                },
                "required": ["admissionId", "issueId", "maintenanceRecord"],
                "additionalProperties": False,
            },
        ),
        handler=_complete,
        description="Record bounded maintenance evidence and hand the issue to Chris for review; never closes it.",
        emoji="✅",
    )
    ctx.register_tool(
        name="photon_linear_cancel",
        toolset="photon-linear",
        schema=_tool_schema(
            "photon_linear_cancel",
            "Cancel only the exact active Photon Linear admission.",
            {
                "type": "object",
                "properties": {"admissionId": {"type": "string"}},
                "required": ["admissionId"],
                "additionalProperties": False,
            },
        ),
        handler=_cancel,
        description="Cancel only the exact active Photon Linear admission.",
        emoji="🛑",
    )
