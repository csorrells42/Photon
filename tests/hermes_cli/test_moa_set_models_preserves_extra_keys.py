"""Complete, lossless Photon/dashboard round trips for Hermes MoA config."""

from __future__ import annotations

from unittest.mock import patch

from hermes_cli.web_server import MoaConfigPayload, MoaModelSlot, MoaPresetPayload, set_moa_models


def _base_payload(**overrides) -> MoaConfigPayload:
    """Return a minimal valid MoaConfigPayload."""
    defaults = dict(
        default_preset="default",
        active_preset="",
        presets={
            "default": MoaPresetPayload(
                reference_models=[
                    MoaModelSlot(provider="openai-codex", model="gpt-5.5"),
                ],
                aggregator=MoaModelSlot(provider="openrouter", model="anthropic/claude-opus-4.8"),
                max_tokens=4096,
                enabled=True,
            ),
        },
    )
    defaults.update(overrides)
    return MoaConfigPayload(**defaults)


class TestSetMoaModelsPreservesUndeclaredKeys:
    """save_traces / trace_dir must survive a GUI save."""

    def test_save_traces_preserved(self, tmp_path):
        """Hand-edited ``moa.save_traces: true`` must not be dropped."""
        existing_cfg = {
            "moa": {
                "save_traces": True,
                "trace_dir": "/custom/traces",
                "privacy_filter": "full",
                "default_preset": "default",
                "presets": {
                    "default": {
                        "reference_models": [
                            {"provider": "openai-codex", "model": "gpt-5.5"},
                        ],
                        "aggregator": {"provider": "openrouter", "model": "anthropic/claude-opus-4.8"},
                        "max_tokens": 4096,
                        "enabled": True,
                    },
                },
            },
        }

        saved_cfg = {}

        def fake_load_config():
            return dict(existing_cfg)  # shallow copy

        def fake_save_config(cfg):
            saved_cfg.update(cfg)

        payload = _base_payload()

        with (
            patch("hermes_cli.web_server.load_config", side_effect=fake_load_config),
            patch("hermes_cli.web_server.save_config", side_effect=fake_save_config),
            patch("hermes_cli.web_server._profile_scope"),
        ):
            set_moa_models(payload)

        moa = saved_cfg["moa"]
        assert moa.get("save_traces") is True, (
            "save_traces was dropped by set_moa_models"
        )
        assert moa.get("trace_dir") == "/custom/traces", (
            "trace_dir was dropped by set_moa_models"
        )
        assert moa.get("privacy_filter") == "full", (
            "privacy_filter was reset by an older client that omitted it"
        )

    def test_supported_top_level_and_per_advisor_controls_are_editable(self):
        existing_cfg = {"moa": {}}
        saved_cfg = {}

        def fake_load_config():
            return existing_cfg

        def fake_save_config(cfg):
            saved_cfg.update(cfg)

        payload = _base_payload(
            privacy_filter="display",
            save_traces=True,
            trace_dir="/opt/data/moa-traces",
            presets={
                "default": MoaPresetPayload(
                    reference_models=[MoaModelSlot(
                        provider="openai-codex",
                        model="gpt-5.5",
                        reasoning_effort="high",
                        max_tokens=640,
                    )],
                    aggregator=MoaModelSlot(
                        provider="openrouter",
                        model="anthropic/claude-opus-4.8",
                    ),
                    reference_max_tokens=900,
                    max_tokens=4096,
                ),
            },
        )

        with (
            patch("hermes_cli.web_server.load_config", side_effect=fake_load_config),
            patch("hermes_cli.web_server.save_config", side_effect=fake_save_config),
            patch("hermes_cli.web_server._profile_scope"),
        ):
            response = set_moa_models(payload)

        moa = saved_cfg["moa"]
        advisor = moa["presets"]["default"]["reference_models"][0]
        assert advisor["reasoning_effort"] == "high"
        assert advisor["max_tokens"] == 640
        assert moa["privacy_filter"] == "display"
        assert moa["save_traces"] is True
        assert moa["trace_dir"] == "/opt/data/moa-traces"
        assert response["privacy_filter"] == "display"
        assert response["save_traces"] is True

