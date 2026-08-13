import json

import pytest
from fastapi import HTTPException

from hermes_cli import web_server
from hermes_cli.web_models import TTSSpeakRequest
from hermes_cli.web_models import LocalVoiceSettingsUpdate


@pytest.mark.asyncio
async def test_local_speech_endpoint_forces_kokoro(tmp_path, monkeypatch):
    output = tmp_path / "local.wav"
    calls = []

    def synthesize(text, **kwargs):
        calls.append((text, kwargs))
        output.write_bytes(b"RIFF-local-voice")
        return json.dumps({
            "success": True,
            "file_path": str(output),
            "provider": "kokoro",
        })

    import tools.tts_tool as tts_tool
    monkeypatch.setattr(tts_tool, "text_to_speech_tool", synthesize)

    result = await web_server.speak_text_local(TTSSpeakRequest(text=" Hello Chris. "))

    assert calls == [("Hello Chris.", {"provider": "kokoro"})]
    assert result["provider"] == "kokoro"
    assert result["mime_type"] == "audio/wav"
    assert result["data_url"].startswith("data:audio/wav;base64,")
    assert not output.exists()


@pytest.mark.asyncio
async def test_local_speech_preview_passes_only_explicit_kokoro_controls(tmp_path, monkeypatch):
    output = tmp_path / "preview.wav"
    calls = []

    def synthesize(text, **kwargs):
        calls.append((text, kwargs))
        output.write_bytes(b"RIFF-local-preview")
        return {"success": True, "file_path": str(output), "provider": "kokoro"}

    import tools.tts_tool as tts_tool
    monkeypatch.setattr(tts_tool, "text_to_speech_tool", synthesize)

    result = await web_server.speak_text_local(
        TTSSpeakRequest(text="Preview.", voice="am_michael", speed=1.04)
    )

    assert calls == [("Preview.", {"provider": "kokoro", "speed": 1.04, "voice": "am_michael"})]
    assert result["provider"] == "kokoro"


@pytest.mark.asyncio
async def test_local_voice_settings_read_and_exact_revision_save(monkeypatch):
    voice_ids = ("af_heart", "am_michael", "jf_gongitsune")
    saved = {
        "tts": {"provider": "edge", "kokoro": {"voice": "af_heart", "speed": 0.98}},
        "unrelated": {"keep": True},
    }
    monkeypatch.setattr(web_server, "load_config", lambda: saved.copy())
    monkeypatch.setattr(web_server, "read_raw_config", lambda: {
        "tts": {"provider": "edge", "kokoro": {"voice": "af_heart", "speed": 0.98}},
        "unrelated": {"keep": True},
    })
    monkeypatch.setattr(web_server, "_local_voice_ids", lambda: voice_ids)

    def save_config(value, **_kwargs):
        saved.clear()
        saved.update(value)

    monkeypatch.setattr(web_server, "save_config", save_config)

    before = await web_server.get_local_voice_settings()
    assert before["settings"]["voiceId"] == "af_heart"
    assert before["voices"] == list(voice_ids)
    after = await web_server.put_local_voice_settings(LocalVoiceSettingsUpdate(
        contract_version=1,
        expected_revision=before["revision"],
        profile_id="default",
        request_id="save:1",
        voice_id="am_michael",
        speed=1.03,
    ))
    assert after["settings"]["voiceId"] == "am_michael"
    assert after["settings"]["speed"] == 1.03
    assert saved["tts"]["provider"] == "edge"
    assert saved["unrelated"] == {"keep": True}

    with pytest.raises(HTTPException) as error:
        await web_server.put_local_voice_settings(LocalVoiceSettingsUpdate(
            contract_version=1,
            expected_revision=before["revision"],
            profile_id="default",
            request_id="save:2",
            voice_id="af_heart",
            speed=0.98,
        ))
    assert error.value.status_code == 409


@pytest.mark.asyncio
async def test_local_voice_settings_never_advertise_a_fallback_when_the_pinned_bank_is_unavailable(monkeypatch):
    def unavailable():
        raise RuntimeError("pinned voice bank missing")

    monkeypatch.setattr(web_server, "_local_voice_ids", unavailable)

    with pytest.raises(HTTPException) as error:
        await web_server.get_local_voice_settings()

    assert error.value.status_code == 503
    assert error.value.detail == "local_voice_unavailable"


@pytest.mark.asyncio
async def test_local_speech_endpoint_rejects_unbounded_text():
    with pytest.raises(HTTPException) as error:
        await web_server.speak_text_local(TTSSpeakRequest(text="x" * 8_001))
    assert error.value.status_code == 413
