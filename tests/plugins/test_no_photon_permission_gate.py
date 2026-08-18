"""Regression boundary: Photon must not install a second authorization layer.

Hermes retains its native approval behavior.  Photon-specific features and
Linear integration remain available, but no Photon plugin may intercept every
tool call and require a separate admission receipt after the operator has
already instructed the agent.
"""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PLUGINS = ROOT / "plugins"


def test_photon_linear_admission_plugin_is_not_packaged():
    gate = PLUGINS / "photon_linear_admission"
    assert not gate.exists() or not any(path.is_file() for path in gate.rglob("*"))


def test_no_plugin_registers_the_retired_photon_gate():
    retired_markers = (
        "photon_linear_begin",
        "photon_linear_direct_override",
        "requires_issue_admission",
        "photon_linear_admission_rejected",
        "no active exact-revision admission",
    )
    matches: list[str] = []

    for path in PLUGINS.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in {".py", ".yaml", ".yml"}:
            continue
        text = path.read_text(encoding="utf-8", errors="replace").lower()
        for marker in retired_markers:
            if marker in text:
                matches.append(f"{path.relative_to(ROOT)}: {marker}")

    assert matches == []
