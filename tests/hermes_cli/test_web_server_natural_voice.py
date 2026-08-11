import json

import pytest
from fastapi import HTTPException

from hermes_cli import web_server
from hermes_cli.web_models import TTSSpeakRequest


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
async def test_local_speech_endpoint_rejects_unbounded_text():
    with pytest.raises(HTTPException) as error:
        await web_server.speak_text_local(TTSSpeakRequest(text="x" * 8_001))
    assert error.value.status_code == 413
