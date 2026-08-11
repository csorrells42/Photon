from __future__ import annotations

from agent.secret_sources.base import ErrorKind
from agent.secret_sources.workbench import WorkbenchSecretSource
from hermes_cli import workbench_credentials as wc


CONNECTION = "hcv2_" + "C" * 43


def _config():
    return {
        "enabled": True,
        "profile_id": "profile-1",
        "timeout_seconds": 10,
        "env": {
            "OPENAI_API_KEY": {
                "connection_ref": CONNECTION,
                "purpose": "model:chat",
                "revision": 5,
            }
        },
    }


def test_scope_resolves_exact_reference_without_cache_or_fallback(monkeypatch):
    calls = []

    def resolve(profile_id, reference, timeout):
        calls.append((profile_id, reference, timeout))
        return wc.SecretLease(reference, 2**31, bytearray(b"native-value"))

    monkeypatch.setattr("agent.secret_sources.workbench.resolve_lease_blocking", resolve)
    result = WorkbenchSecretSource().resolve_scope(_config())
    assert result == {"OPENAI_API_KEY": "native-value"}
    assert calls == [("profile-1", wc.LeaseReference(CONNECTION, "model:chat", 5), 10.0)]


def test_scope_is_atomic_and_returns_no_partial_values(monkeypatch):
    cfg = _config()
    cfg["env"]["SECOND_API_KEY"] = {
        "connection_ref": "hcv2_" + "D" * 43,
        "purpose": "model:chat",
        "revision": 1,
    }
    count = 0

    def resolve(_profile_id, reference, _timeout):
        nonlocal count
        count += 1
        if count == 2:
            raise wc.WorkbenchCredentialError("purpose_denied", "The credential purpose is not authorized.")
        return wc.SecretLease(reference, 2**31, bytearray(b"first-value"))

    monkeypatch.setattr("agent.secret_sources.workbench.resolve_lease_blocking", resolve)
    try:
        WorkbenchSecretSource().resolve_scope(cfg)
    except wc.WorkbenchCredentialError as exc:
        assert exc.code == "purpose_denied"
    else:
        raise AssertionError("partial credential resolution must fail closed")


def test_scope_rejects_malformed_mapping_before_resolution(monkeypatch):
    called = False

    def resolve(*_args):
        nonlocal called
        called = True
        raise AssertionError("must not resolve malformed refs")

    monkeypatch.setattr("agent.secret_sources.workbench.resolve_lease_blocking", resolve)
    cfg = _config()
    cfg["env"]["OPENAI_API_KEY"]["value"] = "renderer-secret"
    try:
        WorkbenchSecretSource().resolve_scope(cfg)
    except wc.WorkbenchCredentialError as exc:
        assert exc.code == "invalid_mapping"
    else:
        raise AssertionError("malformed mapping must fail closed")
    assert not called


def test_legacy_fetch_never_places_workbench_values_in_environment(tmp_path, monkeypatch):
    called = False

    def resolve(*_args):
        nonlocal called
        called = True
        raise AssertionError("legacy fetch must not contact the native broker")

    monkeypatch.setattr("agent.secret_sources.workbench.resolve_lease_blocking", resolve)
    result = WorkbenchSecretSource().fetch(_config(), tmp_path)
    assert not result.ok
    assert result.secrets == {}
    assert result.error_kind is ErrorKind.AUTH_FAILED
    assert not called


def test_source_is_disabled_by_default_and_never_exposes_bootstrap_config():
    source = WorkbenchSecretSource()
    assert not source.is_enabled({})
    assert source.is_enabled({"enabled": True})
    schema = source.config_schema()
    assert set(schema) == {"enabled", "profile_id", "env"}
    assert not ({"bootstrap", "token", "secret", "command", "path"} & set(schema))
