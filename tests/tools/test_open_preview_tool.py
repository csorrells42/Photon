"""Tests for the GUI-surface ``open_preview`` tool."""

import json

import pytest

from tools import desktop_ui, open_preview_tool as op
from tools.registry import registry


@pytest.fixture(autouse=True)
def _reset_emitter():
    """Each test controls the emitter; never leak one across tests."""
    desktop_ui.set_emitter(None)
    yield
    desktop_ui.set_emitter(None)


def test_lives_in_the_gui_surface_toolset(monkeypatch):
    """Reaches a desktop client on ANY backend, including one with no
    HERMES_DESKTOP in its environment (URL / cloud gateways)."""
    monkeypatch.delenv("HERMES_DESKTOP", raising=False)
    entry = registry.get_entry("open_preview")

    assert entry is not None
    assert entry.toolset == "desktop_ui"
    assert entry.check_fn is None


def test_emitter_failure_is_reported():
    def _boom(*_a):
        raise RuntimeError("no window")

    desktop_ui.set_emitter(_boom)
    assert "no window" in json.loads(op.open_preview_tool("https://x.example"))["error"]


def test_workbench_local_preview_is_confined_and_honest(monkeypatch):
    emitted = []
    desktop_ui.set_emitter(lambda _sid, event, payload: emitted.append((event, payload)) or True)
    monkeypatch.setenv("HERMES_WORKBENCH", "1")

    assert json.loads(op.open_preview_tool("/workspace/docs/readme.md"))["success"] is True
    assert emitted == [("preview.open", {"url": "/workspace/docs/readme.md", "label": "", "bookmark": False})]
    assert "error" in json.loads(op.open_preview_tool("/workspace"))
    assert "error" in json.loads(op.open_preview_tool("/etc/passwd"))
    assert len(emitted) == 1


def test_photon_can_save_a_web_page_to_the_visible_bookmarks_bar():
    emitted = []
    desktop_ui.set_emitter(lambda _sid, event, payload: emitted.append((event, payload)) or True)

    result = json.loads(op.open_preview_tool("developer.mozilla.org", "MDN", bookmark=True))

    assert result == {
        "success": True,
        "url": "https://developer.mozilla.org",
        "label": "MDN",
        "bookmarked": True,
    }
    assert emitted == [
        (
            "preview.open",
            {"url": "https://developer.mozilla.org", "label": "MDN", "bookmark": True},
        )
    ]
    schema = op.OPEN_PREVIEW_SCHEMA
    assert "visible persistent Photon browser bookmarks bar" in schema["parameters"]["properties"]["bookmark"]["description"]
