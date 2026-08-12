"""Regression: text_to_speech_tool output_path must reject '..' traversal.

The TTS surface accepts agent/user-supplied absolute paths (writing to a
chosen file is the whole point). What it must reject is paths that use
``..`` components to escape their declared base — those are almost
always either a bug or prompt-injection-controlled
(e.g. ``output_path="audio/../../etc/cron.d/x"``).
"""

import json
from pathlib import Path

from tools import tts_tool
from tools.tts_tool import text_to_speech_tool


def test_output_path_rejects_traversal_escape():
    """A path with '..' components must be rejected before any provider work."""
    result = json.loads(text_to_speech_tool(
        text="hello",
        output_path="audio/../../etc/cron.d/malicious",
    ))
    assert result["success"] is False
    assert "traversal" in result["error"].lower()


def test_output_path_rejects_bare_dotdot():
    """Bare '..' prefix must be rejected."""
    result = json.loads(text_to_speech_tool(
        text="hello",
        output_path="../escape.mp3",
    ))
    assert result["success"] is False
    assert "traversal" in result["error"].lower()


def test_output_path_rejects_hermes_oauth_store(tmp_path, monkeypatch):
    """TTS output_path must not bypass the shared protected-file write guard."""
    import agent.file_safety as file_safety

    hermes_home = tmp_path / "hermes-home"
    hermes_home.mkdir()
    monkeypatch.setattr(file_safety, "_hermes_home_path", lambda: hermes_home)
    monkeypatch.setattr(file_safety, "_hermes_root_path", lambda: hermes_home)

    target = hermes_home / ".anthropic_oauth.json"
    result = json.loads(text_to_speech_tool(
        text="hello",
        output_path=str(target),
    ))

    assert result["success"] is False
    assert "protected credential" in result["error"]
    assert not target.exists()


def test_output_path_rejects_mcp_token_directory(tmp_path, monkeypatch):
    """TTS output_path must not write synthesized audio over MCP token files."""
    import agent.file_safety as file_safety

    hermes_home = tmp_path / "hermes-home"
    token_dir = hermes_home / "mcp-tokens"
    token_dir.mkdir(parents=True)
    monkeypatch.setattr(file_safety, "_hermes_home_path", lambda: hermes_home)
    monkeypatch.setattr(file_safety, "_hermes_root_path", lambda: hermes_home)

    target = token_dir / "server.mp3"
    result = json.loads(text_to_speech_tool(
        text="hello",
        output_path=str(target),
    ))

    assert result["success"] is False
    assert "protected credential" in result["error"]
    assert not target.exists()


def test_fixed_audio_cache_remains_writable_under_workspace_safe_root(
    tmp_path, monkeypatch,
):
    """The host-owned TTS cache is not a model-selected workspace write."""
    workspace = tmp_path / "workspace"
    cache = tmp_path / "hermes-home" / "cache" / "audio"
    workspace.mkdir()
    cache.mkdir(parents=True)
    target = cache / "preview.wav"

    monkeypatch.setenv("HERMES_WRITE_SAFE_ROOT", str(workspace))
    monkeypatch.setattr(tts_tool, "DEFAULT_OUTPUT_DIR", str(cache))
    monkeypatch.setattr(tts_tool, "_import_kokoro", lambda: object)

    def synthesize(_text, output_path, _config):
        Path(output_path).write_bytes(b"RIFF-local-preview")
        return output_path

    monkeypatch.setattr(tts_tool, "_generate_kokoro_tts", synthesize)

    result = json.loads(tts_tool._text_to_speech_single(
        "hello",
        output_path=str(target),
        provider="kokoro",
        tts_config_override={"kokoro": {"voice": "af_heart"}},
    ))

    assert result["success"] is True
    assert target.read_bytes() == b"RIFF-local-preview"


def test_audio_cache_exception_resolves_symlink_before_allowing_write(
    tmp_path, monkeypatch,
):
    """A cache symlink must not bypass the normal protected-path guard."""
    cache = tmp_path / "hermes-home" / "cache" / "audio"
    outside = tmp_path / "outside"
    cache.mkdir(parents=True)
    outside.mkdir()
    link = cache / "escape"
    try:
        link.symlink_to(outside, target_is_directory=True)
    except (OSError, NotImplementedError):
        import pytest
        pytest.skip("directory symlinks are unavailable for this test identity")

    monkeypatch.setattr(tts_tool, "DEFAULT_OUTPUT_DIR", str(cache))
    assert tts_tool._is_application_audio_cache_path(link / "voice.wav") is False


def test_audio_cache_exception_never_overrides_credential_denial(
    tmp_path, monkeypatch,
):
    """Only the safe-root category may be bypassed for the fixed cache."""
    import agent.file_safety as file_safety

    cache = tmp_path / "hermes-home" / "cache" / "audio"
    cache.mkdir(parents=True)
    target = cache / "voice.wav"
    monkeypatch.setattr(tts_tool, "DEFAULT_OUTPUT_DIR", str(cache))
    monkeypatch.setattr(file_safety, "_classify_write_denial", lambda _path: "credential")

    result = json.loads(tts_tool._text_to_speech_single(
        "hello",
        output_path=str(target),
        provider="kokoro",
        tts_config_override={"kokoro": {"voice": "af_heart"}},
    ))

    assert result["success"] is False
    assert "protected credential" in result["error"]
    assert not target.exists()
