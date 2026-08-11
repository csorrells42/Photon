"""Fixed Photon entrypoint for the pinned build123d-mcp stdio worker."""

from __future__ import annotations

import os
import pathlib


WORKSPACE = pathlib.Path("/workspace")
LIBRARY = WORKSPACE / "library"
EXECUTABLE = "/opt/photon/venv/bin/build123d-mcp"


def _require_directory(path: pathlib.Path) -> None:
    if not path.is_dir() or path.is_symlink():
        raise SystemExit(f"required Photon CAD directory is unavailable: {path}")


def main() -> None:
    _require_directory(WORKSPACE)
    if LIBRARY.exists():
        _require_directory(LIBRARY)
    else:
        LIBRARY.mkdir(mode=0o700)

    # Ignore caller-supplied argv. The desktop broker selects operations through
    # the typed Photon contract; it cannot weaken these process options.
    argv = [
        EXECUTABLE,
        "--transport",
        "stdio",
        "--library",
        str(LIBRARY),
        "--exec-timeout",
        "120",
        # Memory stays comprehensively bounded by the broker's Docker cgroup.
        # build123d-mcp's optional RLIMIT_DATA cap excludes mmap-backed OCC
        # allocations and makes the pinned OCC import spin until timeout.
        "--cpu-limit-s",
        "300",
    ]
    os.execv(EXECUTABLE, argv)


if __name__ == "__main__":
    main()
