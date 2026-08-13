from __future__ import annotations

import json
from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from hermes_cli import profiles
from hermes_cli.web_routers import profile_runtime


@pytest.fixture()
def client(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> TestClient:
    root = tmp_path / ".hermes"
    root.mkdir()
    (root / "SOUL.md").write_text("Be helpful.", encoding="utf-8")
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    # Native Windows defaults live under LOCALAPPDATA rather than Path.home().
    # Pin the platform default as well so profile root discovery cannot escape
    # this test's isolated HOME.
    import hermes_constants

    monkeypatch.setattr(hermes_constants, "_get_platform_default_hermes_home", lambda: root)
    monkeypatch.setenv("HERMES_HOME", str(root))
    profile_runtime._correlations.clear()
    profile_runtime._previews.clear()
    app = FastAPI()
    app.include_router(profile_runtime.router)
    return TestClient(app)


def _snapshot(client: TestClient, profile: str = "default", correlation: str = "load:1") -> dict:
    response = client.get(
        "/api/workbench/profile-runtime/v1/snapshot",
        params={"contract": profile_runtime.CONTRACT, "profileId": profile, "correlationId": correlation},
    )
    assert response.status_code == 200, response.text
    return response.json()


def _body(profile: str, correlation: str, revision: int, **extra):
    return {
        "contract": profile_runtime.CONTRACT,
        "profileId": profile,
        "correlationId": correlation,
        "expectedRevision": revision,
        **extra,
    }


def test_snapshot_is_renderer_safe_and_revision_stable(client: TestClient, tmp_path: Path):
    first = _snapshot(client)
    second = _snapshot(client, correlation="load:2")
    assert first["revision"] == second["revision"]
    assert first["value"]["documents"] == [{"kind": "soul", "text": "Be helpful.", "maximumCharacters": 65536}]
    assert first["value"]["configuration"] == {}
    assert first["value"]["providers"] == []
    serialized = json.dumps(first).lower()
    for forbidden in (str(tmp_path).lower(), "\\users\\", '"path"', "has_env", "token", "api_key"):
        assert forbidden not in serialized
    profiles.get_profile_dir("default").joinpath("SOUL.md").write_text("Changed outside the router.", encoding="utf-8")
    assert _snapshot(client, correlation="load:restart")["revision"] != first["revision"]


def test_soul_write_is_revision_bound_and_post_read_back(client: TestClient):
    revision = _snapshot(client)["revision"]
    response = client.post(
        "/api/workbench/profile-runtime/v1/documents/soul",
        json=_body("default", "soul:1", revision, document="soul", text="Be exact."),
    )
    assert response.status_code == 200, response.text
    result = response.json()
    assert result["revision"] != revision
    assert result["value"]["documents"][0]["text"] == "Be exact."
    stale = client.post(
        "/api/workbench/profile-runtime/v1/documents/soul",
        json=_body("default", "soul:2", revision, document="soul", text="Stale."),
    )
    assert stale.status_code == 409
    assert stale.json()["code"] == "stale-revision"
    assert profiles.get_profile_dir("default").joinpath("SOUL.md").read_text(encoding="utf-8") == "Be exact."


def test_preview_commit_is_single_use_and_clone_excludes_credentials(client: TestClient):
    source = profiles.create_profile("source", no_alias=True)
    (source / "SOUL.md").write_text("Source soul.", encoding="utf-8")
    (source / ".env").write_text("API_KEY=must-not-copy\n", encoding="utf-8")
    (source / "config.yaml").write_text("model:\n  provider: openrouter\n  default: safe/model\nsecret: must-not-copy\n", encoding="utf-8")
    revision = _snapshot(client, "source", "load:source")["revision"]
    mutation = {"kind": "clone", "sourceProfileId": "source", "name": "copy"}
    preview = client.post(
        "/api/workbench/profile-runtime/v1/profile-mutation/preview",
        json=_body("source", "preview:1", revision, mutation=mutation),
    )
    assert preview.status_code == 200, preview.text
    preview_id = preview.json()["value"]["previewId"]
    commit_body = _body("source", "commit:1", revision, mutation=mutation, previewId=preview_id)
    commit = client.post("/api/workbench/profile-runtime/v1/profile-mutation/commit", json=commit_body)
    assert commit.status_code == 200, commit.text
    copied = profiles.get_profile_dir("copy")
    assert (copied / "SOUL.md").read_text(encoding="utf-8") == "Source soul."
    # Fresh profiles may seed an empty credential file. The source profile's
    # credential value must never be copied into it.
    assert "must-not-copy" not in ((copied / ".env").read_text(encoding="utf-8") if (copied / ".env").exists() else "")
    config_text = (copied / "config.yaml").read_text(encoding="utf-8")
    assert "safe/model" in config_text
    assert "must-not-copy" not in config_text
    replay = client.post(
        "/api/workbench/profile-runtime/v1/profile-mutation/commit",
        json={
            **commit_body,
            "correlationId": "commit:2",
            "expectedRevision": _snapshot(client, "source", "load:source-after")["revision"],
        },
    )
    assert replay.status_code == 409
    assert replay.json()["code"] == "preview-replay"


def test_delete_requires_exact_confirmation_and_preserves_default(client: TestClient):
    profiles.create_profile("doomed", no_alias=True)
    revision = _snapshot(client, "doomed", "load:doomed")["revision"]
    mutation = {"kind": "delete"}
    preview = client.post(
        "/api/workbench/profile-runtime/v1/profile-mutation/preview",
        json=_body("doomed", "preview:delete", revision, mutation=mutation),
    ).json()["value"]
    wrong = client.post(
        "/api/workbench/profile-runtime/v1/profile-mutation/commit",
        json=_body("doomed", "commit:wrong", revision, mutation=mutation, previewId=preview["previewId"], destructiveConfirmation={"phrase": "delete other"}),
    )
    assert wrong.status_code == 409
    assert profiles.get_profile_dir("doomed").exists()
    blocked = client.post(
        "/api/workbench/profile-runtime/v1/profile-mutation/preview",
        json=_body("default", "preview:default", _snapshot(client, correlation="load:default2")["revision"], mutation=mutation),
    )
    assert blocked.status_code == 409


def test_model_terminal_and_active_selection_are_exact_and_safe(client: TestClient):
    profiles.create_profile("worker", no_alias=True)
    revision = _snapshot(client, "worker", "load:worker")["revision"]
    model = client.post(
        "/api/workbench/profile-runtime/v1/intent/model",
        json=_body("worker", "model:1", revision, provider="openrouter", model="deepseek/model"),
    )
    assert model.status_code == 200, model.text
    revision = model.json()["revision"]
    terminal = client.post(
        "/api/workbench/profile-runtime/v1/terminal",
        json=_body("worker", "terminal:1", revision, backendId="docker"),
    )
    assert terminal.status_code == 200, terminal.text
    revision = terminal.json()["revision"]
    active = client.post(
        "/api/workbench/profile-runtime/v1/active-profile",
        json=_body("worker", "active:1", revision, targetProfileId="worker"),
    )
    assert active.status_code == 200, active.text
    assert active.json()["value"] == {"activeProfileId": "worker"}
    assert profiles.get_active_profile() == "worker"
    serialized = model.text + terminal.text + active.text
    assert str(profiles.get_profile_dir("worker")) not in serialized


def test_unknown_fields_oversize_and_correlation_replay_fail_closed(client: TestClient):
    revision = _snapshot(client)["revision"]
    base = _body("default", "terminal:replay", revision, backendId="local")
    first = client.post("/api/workbench/profile-runtime/v1/terminal", json=base)
    assert first.status_code == 200
    replay = client.post("/api/workbench/profile-runtime/v1/terminal", json=base)
    assert replay.status_code == 409
    assert replay.json()["code"] == "correlation-replay"
    unknown = client.post(
        "/api/workbench/profile-runtime/v1/terminal",
        json={**_body("default", "terminal:unknown", first.json()["revision"], backendId="local"), "path": "hidden"},
    )
    assert unknown.status_code == 400
    oversized = client.post(
        "/api/workbench/profile-runtime/v1/documents/soul",
        json=_body("default", "soul:large", first.json()["revision"], document="soul", text="x" * 70_000),
    )
    assert oversized.status_code in {400, 413}
