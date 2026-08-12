import hashlib
import json
import sys
from types import SimpleNamespace

import pytest

from tools import lazy_deps, tts_tool


def test_kokoro_is_pinned_local_provider():
    assert lazy_deps.LAZY_DEPS["tts.kokoro"] == (
        "kokoro-onnx==0.5.0",
        "soundfile==0.13.1",
    )
    assert "kokoro" in tts_tool.BUILTIN_TTS_PROVIDERS
    assert tts_tool._resolve_max_text_length("kokoro") == 2000
    assert tts_tool.DEFAULT_KOKORO_VOICE == "af_heart"


def test_existing_kokoro_assets_must_match_exact_digest(tmp_path, monkeypatch):
    model = b"model"
    voices = b"voices"
    assets = (
        ("model.onnx", len(model), hashlib.sha256(model).hexdigest(), "https://invalid/model"),
        ("voices.bin", len(voices), hashlib.sha256(voices).hexdigest(), "https://invalid/voices"),
    )
    (tmp_path / "natural-voice").mkdir()
    (tmp_path / "natural-voice" / "model.onnx").write_bytes(model)
    (tmp_path / "natural-voice" / "voices.bin").write_bytes(voices)
    monkeypatch.setattr(tts_tool, "KOKORO_MODEL_ASSETS", assets)
    monkeypatch.setattr(tts_tool, "_kokoro_verified_assets", None)
    monkeypatch.setattr(tts_tool, "get_hermes_home", lambda: tmp_path)
    monkeypatch.setattr(tts_tool, "urlopen", lambda *_a, **_k: pytest.fail("network must not be used"))

    assert tts_tool._ensure_kokoro_assets() == (
        str(tmp_path / "natural-voice" / "model.onnx"),
        str(tmp_path / "natural-voice" / "voices.bin"),
    )

    monkeypatch.setattr(tts_tool, "_kokoro_verified_assets", None)
    (tmp_path / "natural-voice" / "model.onnx").write_bytes(b"wrong")
    with pytest.raises(RuntimeError, match="digest mismatch"):
        tts_tool._ensure_kokoro_assets()


def test_kokoro_generation_uses_selected_local_voice(tmp_path, monkeypatch):
    calls = []

    class FakeKokoro:
        def __init__(self, model_path, voices_path):
            calls.append(("load", model_path, voices_path))

        def get_voices(self):
            return ["af_heart", "af_bella"]

        def create(self, text, *, voice, speed, lang):
            calls.append(("create", text, voice, speed, lang))
            return [0.0, 0.1], 24_000

    def write_audio(path, samples, sample_rate):
        calls.append(("write", path, samples, sample_rate))
        with open(path, "wb") as output:
            output.write(b"RIFF-local-kokoro")

    monkeypatch.setattr(tts_tool, "_import_kokoro", lambda: FakeKokoro)
    monkeypatch.setattr(tts_tool, "_ensure_kokoro_assets", lambda: ("model.onnx", "voices.bin"))
    monkeypatch.setattr(tts_tool, "_kokoro_model_cache", {})
    monkeypatch.setitem(sys.modules, "soundfile", SimpleNamespace(write=write_audio))
    output = tmp_path / "voice.wav"

    assert tts_tool._generate_kokoro_tts(
        "Hello Chris.",
        str(output),
        {"kokoro": {"voice": "af_bella", "speed": 1.02}},
    ) == str(output)
    assert output.read_bytes() == b"RIFF-local-kokoro"
    assert ("create", "Hello Chris.", "af_bella", 1.02, "en-us") in calls


def test_kokoro_generation_rejects_unlisted_voice(tmp_path, monkeypatch):
    class FakeKokoro:
        def __init__(self, *_args):
            pass

        def get_voices(self):
            return ["af_heart"]

    monkeypatch.setattr(tts_tool, "_import_kokoro", lambda: FakeKokoro)
    monkeypatch.setattr(tts_tool, "_ensure_kokoro_assets", lambda: ("model.onnx", "voices.bin"))
    monkeypatch.setattr(tts_tool, "_kokoro_model_cache", {})

    with pytest.raises(ValueError, match="Unknown Kokoro voice"):
        tts_tool._generate_kokoro_tts(
            "Hello.",
            str(tmp_path / "voice.wav"),
            {"kokoro": {"voice": "not_a_voice"}},
        )


def test_kokoro_public_tool_applies_bounded_per_call_voice_without_mutating_profile(tmp_path, monkeypatch):
    captured = []

    monkeypatch.setattr(tts_tool, "_load_tts_config", lambda: {
        "provider": "edge",
        "kokoro": {"voice": "af_heart", "speed": 0.98},
    })
    monkeypatch.setattr(tts_tool, "_split_text_for_tts", lambda text, _cap: [text])

    def synthesize(**kwargs):
        captured.append(kwargs["tts_config_override"])
        path = kwargs["output_path"]
        with open(path, "wb") as output:
            output.write(b"RIFF-preview")
        return json.dumps({
            "success": True,
            "file_path": path,
            "provider": "kokoro",
            "voice_compatible": False,
        })

    monkeypatch.setattr(tts_tool, "_text_to_speech_single", synthesize)
    monkeypatch.setattr(
        tts_tool,
        "_build_audio_delivery_files",
        lambda paths, _base, _profile, **_kwargs: (paths, False),
    )

    result = json.loads(tts_tool.text_to_speech_tool(
        "Hello.",
        provider="kokoro",
        voice="am_michael",
        speed=1.03,
        output_path=str(tmp_path / "preview.wav"),
    ))
    assert result["success"] is True
    assert captured[0]["provider"] == "edge"
    assert captured[0]["kokoro"] == {"voice": "am_michael", "speed": 1.03}
