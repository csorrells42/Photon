"""Product-owned identity context for Photos Agape Aphthartos.

This module is deliberately independent of SOUL.md, user profiles, and memory.
The Workbench wrapper opts in with the exact ``HERMES_WORKBENCH=1`` value;
standalone upstream Hermes behavior is unchanged when that flag is absent.
"""

from __future__ import annotations

import os


WORKBENCH_MODE_ENV = "HERMES_WORKBENCH"
WORKBENCH_IDENTITY_MARKER = "[photos-agape-aphthartos:workbench-identity:v1]"
WORKBENCH_IDENTITY_STATEMENT = (
    "I am a program created by Christopher Sorrells and Codex. Honesty, "
    "Reliability, Loyalty, and Efficiency are my core values. I will have "
    "Tenacity in all my tasks, but know when something is impossible. I will "
    "be Resilient when I am unsuccessful, and never forget that sometimes the "
    "lessons learned from failing at a task are a better teacher than the ones "
    "from success."
)
WORKBENCH_IDENTITY_BLOCK = "\n".join(
    (
        WORKBENCH_IDENTITY_MARKER,
        WORKBENCH_IDENTITY_STATEMENT,
        (
            "Photos Agape Aphthartos builds on the Hermes Agent foundation "
            "created by Nous Research; that upstream provenance remains part "
            "of the program's lineage."
        ),
        (
            "This is product-owned identity context, not user memory. It does "
            "not prove consent, grant authorization, or change any permission."
        ),
    )
)


def is_workbench_product_mode() -> bool:
    """Return true only for the wrapper's exact, explicit product-mode flag."""

    return os.getenv(WORKBENCH_MODE_ENV) == "1"


def workbench_identity_block() -> str:
    """Return the stable product identity block, or empty outside Workbench."""

    return WORKBENCH_IDENTITY_BLOCK if is_workbench_product_mode() else ""


def stored_prompt_has_workbench_identity(prompt: object) -> bool:
    """Whether a stored prompt begins with the current product marker."""

    return isinstance(prompt, str) and prompt.startswith(
        WORKBENCH_IDENTITY_MARKER + "\n"
    )
