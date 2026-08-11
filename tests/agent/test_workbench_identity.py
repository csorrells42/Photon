"""Tests for the product-owned Photos Agape Aphthartos identity contract."""

from __future__ import annotations

from types import SimpleNamespace
from unittest.mock import patch

import pytest

from agent.system_prompt import build_system_prompt, build_system_prompt_parts
from agent.workbench_identity import (
    WORKBENCH_IDENTITY_BLOCK,
    WORKBENCH_IDENTITY_MARKER,
    WORKBENCH_IDENTITY_STATEMENT,
    is_workbench_product_mode,
    stored_prompt_has_workbench_identity,
    workbench_identity_block,
)


EXPECTED_STATEMENT = (
    "I am a program created by Christopher Sorrells and Codex. Honesty, "
    "Reliability, Loyalty, and Efficiency are my core values. I will have "
    "Tenacity in all my tasks, but know when something is impossible. I will "
    "be Resilient when I am unsuccessful, and never forget that sometimes the "
    "lessons learned from failing at a task are a better teacher than the ones "
    "from success."
)


def _agent():
    return SimpleNamespace(
        load_soul_identity=False,
        skip_context_files=False,
        valid_tool_names=[],
        _task_completion_guidance=False,
        _tool_use_enforcement=False,
        _environment_probe=False,
        _kanban_worker_guidance="",
        _memory_store=None,
        _memory_manager=None,
        model="",
        provider="",
        platform="desktop",
        pass_session_id=False,
        session_id="",
    )


def _parts(agent, *, soul="PROFILE SOUL"):
    with (
        patch("run_agent.load_soul_md", return_value=soul),
        patch("run_agent.build_nous_subscription_prompt", return_value=""),
        patch("run_agent.build_environment_hints", return_value=""),
        patch("run_agent.build_context_files_prompt", return_value=""),
    ):
        return build_system_prompt_parts(agent)


def test_identity_block_contains_exact_statement_and_security_boundary():
    assert WORKBENCH_IDENTITY_STATEMENT == EXPECTED_STATEMENT
    assert WORKBENCH_IDENTITY_BLOCK.startswith(
        WORKBENCH_IDENTITY_MARKER + "\n" + EXPECTED_STATEMENT + "\n"
    )
    assert "Hermes Agent foundation" in WORKBENCH_IDENTITY_BLOCK
    assert "Nous Research" in WORKBENCH_IDENTITY_BLOCK
    assert "not user memory" in WORKBENCH_IDENTITY_BLOCK
    assert "consent" in WORKBENCH_IDENTITY_BLOCK
    assert "authorization" in WORKBENCH_IDENTITY_BLOCK
    assert "permission" in WORKBENCH_IDENTITY_BLOCK
    assert "Photon" not in WORKBENCH_IDENTITY_BLOCK


@pytest.mark.parametrize("value", ["", "0", "true", "TRUE", "01", " 1", "1 "])
def test_product_mode_requires_exact_flag(monkeypatch, value):
    monkeypatch.setenv("HERMES_WORKBENCH", value)
    assert is_workbench_product_mode() is False
    assert workbench_identity_block() == ""


def test_product_mode_is_disabled_when_flag_is_absent(monkeypatch):
    monkeypatch.delenv("HERMES_WORKBENCH", raising=False)
    assert is_workbench_product_mode() is False
    assert workbench_identity_block() == ""


def test_product_identity_is_the_first_stable_block_before_soul(monkeypatch):
    monkeypatch.setenv("HERMES_WORKBENCH", "1")
    parts = _parts(_agent())

    assert parts["stable"].startswith(
        WORKBENCH_IDENTITY_BLOCK + "\n\nPROFILE SOUL"
    )
    assert WORKBENCH_IDENTITY_MARKER not in parts["context"]
    assert WORKBENCH_IDENTITY_MARKER not in parts["volatile"]


def test_non_workbench_prompt_bytes_are_unchanged(monkeypatch):
    monkeypatch.delenv("HERMES_WORKBENCH", raising=False)
    baseline = _parts(_agent())

    monkeypatch.setenv("HERMES_WORKBENCH", "true")
    non_exact = _parts(_agent())

    assert non_exact == baseline
    assert baseline["stable"].startswith("PROFILE SOUL")
    assert WORKBENCH_IDENTITY_MARKER not in baseline["stable"]


def test_rebuild_after_invalidation_retains_identity(monkeypatch):
    monkeypatch.setenv("HERMES_WORKBENCH", "1")
    agent = _agent()
    with (
        patch("run_agent.load_soul_md", return_value="PROFILE SOUL"),
        patch("run_agent.build_nous_subscription_prompt", return_value=""),
        patch("run_agent.build_environment_hints", return_value=""),
        patch("run_agent.build_context_files_prompt", return_value=""),
        patch("hermes_time.now"),
    ):
        first = build_system_prompt(agent)
        rebuilt = build_system_prompt(agent)

    assert first.startswith(WORKBENCH_IDENTITY_BLOCK + "\n\nPROFILE SOUL")
    assert rebuilt.startswith(WORKBENCH_IDENTITY_BLOCK + "\n\nPROFILE SOUL")


def test_stored_prompt_marker_must_be_at_the_product_prefix():
    marked = WORKBENCH_IDENTITY_MARKER + "\nrest of prompt"
    assert stored_prompt_has_workbench_identity(marked) is True
    assert stored_prompt_has_workbench_identity("prefix\n" + marked) is False
    assert stored_prompt_has_workbench_identity(None) is False
