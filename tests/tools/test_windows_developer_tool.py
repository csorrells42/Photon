"""Tests for the GUI-surface ``windows_developer`` tool."""

import json

from tools import windows_developer_tool as wd
from tools.registry import registry


def test_lives_in_desktop_ui_toolset():
    entry = registry.get_entry("windows_developer")
    assert entry is not None
    assert entry.toolset == "desktop_ui"
    assert entry.check_fn is None


def test_requires_desktop_callback():
    result = json.loads(wd.windows_developer_tool("describe", callback=None))
    assert "desktop" in result["error"]


def test_forwards_typed_workspace_relative_build():
    seen = {}

    def callback(**kwargs):
        seen.update(kwargs)
        return json.dumps({"ok": True, "operation": "build"})

    result = json.loads(wd.windows_developer_tool(
        "build",
        project_path="Chess/Chess.csproj",
        configuration="Release",
        callback=callback,
    ))
    assert result == {"ok": True, "operation": "build"}
    assert seen == {
        "action": "build",
        "project_path": "Chess/Chess.csproj",
        "program_path": None,
        "arguments": [],
        "configuration": "Release",
        "script": None,
        "reason": None,
    }


def test_rejects_absolute_or_traversal_targets():
    callback = lambda **_: json.dumps({"ok": True})
    for path in ("C:/escape.csproj", "/escape.csproj", "../escape.csproj", "folder/file.txt"):
        result = json.loads(wd.windows_developer_tool("build", project_path=path, callback=callback))
        assert "error" in result


def test_rejects_invalid_result_and_configuration():
    assert "error" in json.loads(wd.windows_developer_tool("build", "a.csproj", configuration="fast", callback=lambda **_: "{}"))
    assert "error" in json.loads(wd.windows_developer_tool("describe", callback=lambda **_: "not json"))


def test_run_requires_discovered_relative_program():
    seen = {}
    result = json.loads(wd.windows_developer_tool(
        "run",
        program_path="Chess/bin/Debug/net10.0-windows/Chess.exe",
        arguments=["--demo"],
        callback=lambda **kwargs: seen.update(kwargs) or json.dumps({"ok": True}),
    ))
    assert result == {"ok": True}
    assert seen["program_path"].endswith("Chess.exe")
    assert seen["arguments"] == ["--demo"]


def test_administrator_requires_approval_and_forwards_exact_operation(monkeypatch):
    seen = {}
    approvals = []

    monkeypatch.setattr(
        wd,
        "request_tool_approval",
        lambda action, description, **kwargs: approvals.append((action, description, kwargs)) or {"approved": True},
    )
    result = json.loads(wd.windows_developer_tool(
        "administrator",
        script="docker version",
        reason="Repair Docker Desktop connectivity.",
        callback=lambda **kwargs: seen.update(kwargs) or json.dumps({"ok": True}),
    ))

    assert result == {"ok": True}
    assert approvals[0][0] == "windows_administrator"
    assert "Repair Docker Desktop connectivity." in approvals[0][1]
    assert seen["script"] == "docker version"
    assert seen["reason"] == "Repair Docker Desktop connectivity."


def test_administrator_decline_never_reaches_native_host(monkeypatch):
    called = False

    monkeypatch.setattr(
        wd,
        "request_tool_approval",
        lambda *_args, **_kwargs: {"approved": False, "message": "Not now."},
    )

    def callback(**_kwargs):
        nonlocal called
        called = True
        return json.dumps({"ok": True})

    result = json.loads(wd.windows_developer_tool(
        "administrator",
        script="docker version",
        reason="Repair Docker Desktop connectivity.",
        callback=callback,
    ))
    assert result == {"error": "Not now."}
    assert called is False
