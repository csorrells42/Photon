"""Operator-only Architect approval writer.

This module is intentionally not registered as a Hermes model tool. The active
Architect runs it out of band after reviewing the exact revision and generation
returned by ``photon_linear_inspect``::

    python -m plugins.photon_linear_admission.approve \
      --issue CLS-6 --revision linear:v1:... \
      --generation photon-generation:v1:...
"""

from __future__ import annotations

import argparse
import json

from hermes_cli.profiles import get_active_profile_name, get_profile_dir

from .authority import PhotonLinearAuthority


def main() -> int:
    parser = argparse.ArgumentParser(description="Write one expiring Architect approval for Photon Linear work.")
    parser.add_argument("--issue", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--generation", required=True)
    parser.add_argument("--ttl-seconds", type=int, default=900)
    parser.add_argument("--profile", default=get_active_profile_name())
    args = parser.parse_args()
    state_root = get_profile_dir(args.profile) / "photon-linear-admission"
    authority = PhotonLinearAuthority(
        lambda _issue: (_ for _ in ()).throw(RuntimeError("inspection unavailable")),
        lambda _issue, _body: None,
        state_root=state_root,
        initialize_boot=False,
    )
    approval = authority.write_architect_approval(
        args.issue,
        args.revision,
        args.generation,
        ttl_seconds=args.ttl_seconds,
    )
    print(json.dumps({
        "accepted": True,
        "issueId": approval.issue_id,
        "issueRevision": approval.issue_revision,
        "generation": approval.generation,
        "expiresAt": approval.expires_at,
    }, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
