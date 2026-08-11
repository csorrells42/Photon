"""Apply Photon's fail-closed offline runtime adaptation to pinned PartCAD.

PartCAD's ``none`` Python runtime still provisions its CAD dependencies with
pip into a mutable per-user sandbox.  Photon already supplies that exact CAD
stack inside an immutable, network-disabled container, so runtime provisioning
is both impossible and undesirable.  This build-only patch makes ``none`` mean
what the container boundary needs: use the preverified interpreter and never
install or download packages at runtime.
"""

from __future__ import annotations

import hashlib
import pathlib
import sys


EXPECTED_SOURCE_SHA256 = "2e749ec64be1583f8cbda9d543dc5ab2810d279a578f134ad1b1d3d87ae75996"

OLD_IMPORTS = b"""import os
import shutil
"""

NEW_IMPORTS = b"""import importlib.metadata
import os
import sys

from packaging.requirements import InvalidRequirement, Requirement
from packaging.utils import canonicalize_name
"""

OLD_CLASS = b"""    def __init__(self, ctx, version=None):
        super().__init__(ctx, "none", version)

        which = shutil.which("python")
        if which is not None:
            self.exec_path = which
        else:
            which3 = shutil.which("python3")
            if which3 is not None:
                self.exec_path = which3
            else:
                self.exec_path = which

    def once(self):
        os.makedirs(self.path, exist_ok=True)
        super().once()

    async def once_async(self):
        os.makedirs(self.path, exist_ok=True)
        await super().once_async()
"""

NEW_CLASS = b"""    _PHOTON_DISTRIBUTIONS = {
        "backports-zstd": "1.6.0",
        "build123d": "0.11.1",
        "cadquery-ocp": "7.9.3.1.1",
        "numpy": "2.5.1",
        "ocpsvg": "0.6.0",
        "typing-extensions": "4.16.0",
    }

    def __init__(self, ctx, version=None):
        current_version = "%d.%d" % (sys.version_info.major, sys.version_info.minor)
        if version is not None and str(version) != current_version:
            raise RuntimeError(
                "Photon's immutable PartCAD runtime is Python %s, not %s"
                % (current_version, version)
            )
        super().__init__(ctx, "none", current_version)
        self.exec_path = sys.executable

    def _verify_offline_requirement(self, python_package, session=None, path=None, force=False):
        if not isinstance(python_package, str) or not python_package.strip():
            raise RuntimeError("Photon rejected an invalid PartCAD Python requirement")
        if path is not None:
            raise RuntimeError("Photon does not permit alternate PartCAD Python environments")
        if force:
            raise RuntimeError("Photon does not permit PartCAD dependency reinstallation")

        try:
            requirement = Requirement(python_package)
        except InvalidRequirement as exc:
            raise RuntimeError("Photon rejected an invalid PartCAD Python requirement") from exc
        if requirement.url is not None or requirement.extras:
            raise RuntimeError("Photon rejected a non-package PartCAD Python requirement")
        if requirement.marker is not None and not requirement.marker.evaluate():
            return

        distribution = canonicalize_name(requirement.name)
        expected_version = self._PHOTON_DISTRIBUTIONS.get(distribution)
        if expected_version is None:
            raise RuntimeError(
                "Photon rejected non-allowlisted PartCAD Python requirement: %s" % requirement.name
            )
        try:
            installed_version = importlib.metadata.version(requirement.name)
        except importlib.metadata.PackageNotFoundError as exc:
            raise RuntimeError(
                "Photon's immutable PartCAD dependency is missing: %s" % requirement.name
            ) from exc
        if installed_version != expected_version:
            raise RuntimeError(
                "Photon's immutable PartCAD dependency version is invalid: %s"
                % requirement.name
            )
        if requirement.specifier and not requirement.specifier.contains(
            installed_version, prereleases=True
        ):
            raise RuntimeError(
                "Photon's immutable PartCAD dependency does not satisfy the request: %s"
                % requirement.name
            )

        # Session-scoped requirements are verified against the same immutable
        # interpreter.  Never append dependencies or mark a session dirty,
        # because either action makes upstream create a mutable venv and pip.
        del session

    def once(self):
        os.makedirs(self.path, exist_ok=True)
        self.initialized = True

    async def once_async(self):
        os.makedirs(self.path, exist_ok=True)
        self.initialized = True

    def ensure(self, python_package, session=None, path=None, force=False):
        self.once()
        self._verify_offline_requirement(python_package, session, path, force)

    def ensure_onced(self, python_package, session=None, path=None, force=False):
        self._verify_offline_requirement(python_package, session, path, force)

    def ensure_onced_locked(self, python_package, session=None, path=None, force=False):
        self._verify_offline_requirement(python_package, session, path, force)

    async def ensure_async(self, python_package, session=None, path=None, force=False):
        await self.once_async()
        self._verify_offline_requirement(python_package, session, path, force)

    async def ensure_async_onced(self, python_package, session=None, path=None, force=False):
        self._verify_offline_requirement(python_package, session, path, force)

    async def ensure_async_onced_locked(
        self, python_package, session=None, path=None, force=False
    ):
        self._verify_offline_requirement(python_package, session, path, force)
"""


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: photon_partcad_offline_patch.py <runtime_python_none.py>")

    target = pathlib.Path(sys.argv[1])
    source = target.read_bytes()
    source_hash = hashlib.sha256(source).hexdigest()
    if source_hash != EXPECTED_SOURCE_SHA256:
        raise SystemExit(f"unexpected pinned PartCAD source hash: {source_hash}")
    if source.count(OLD_IMPORTS) != 1 or source.count(OLD_CLASS) != 1:
        raise SystemExit("pinned PartCAD offline-runtime patch anchors are not unique")

    patched = source.replace(OLD_IMPORTS, NEW_IMPORTS).replace(OLD_CLASS, NEW_CLASS)
    target.write_bytes(patched)
    print(f"photon PartCAD offline patch sha256={hashlib.sha256(patched).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
