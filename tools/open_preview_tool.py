#!/usr/bin/env python3
"""Open a URL, dev server, or file in the Hermes desktop GUI's preview pane.

Lives in the ``desktop_ui`` toolset, which the GUI gateway enables only for a
session whose source is the desktop app — so the schema never reaches a CLI,
messaging, or cron agent, and it DOES reach a desktop client on a remote/cloud
backend. Emits ``preview.open`` through the shared ``desktop_ui`` bridge; the
renderer opens the pane beside the chat for the window that asked and never
steals focus for a background session.
"""

import json
import os
import re

from tools import desktop_ui
from tools.registry import registry, tool_error


def _normalize_target(raw: str) -> str:
    """Coax a bare host/domain into a fetchable URL; leave paths + schemes alone.

    ``www.cnn.com`` → ``https://www.cnn.com``; ``localhost:3000`` →
    ``http://localhost:3000``. File paths and explicit schemes pass through for
    the renderer's preview normalizer to classify.
    """
    v = raw.strip().strip("`").strip()
    if not v or "://" in v or v.startswith(("/", "./", "../", "~", "file:")):
        return v
    if re.match(r"^(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(:\d+)?(/|$)", v, re.I):
        return "http://" + v
    if re.match(r"^[\w.-]+\.[a-z]{2,}(:\d+)?(/.*)?$", v, re.I):
        return "https://" + v
    return v


def open_preview_tool(url: str, label: str = "", bookmark: bool = False) -> str:
    """Ask the desktop GUI to show ``url`` in the preview pane beside the chat."""
    target = _normalize_target(url or "")
    if not target:
        return tool_error(
            "url is required — a web URL (https://…), a localhost dev server, or a "
            "file path to show in the preview pane."
        )

    if os.environ.get("HERMES_WORKBENCH") == "1" and not re.match(r"^https?://", target, re.I):
        workbench_path = target[7:] if target.startswith("file://") else target
        if not workbench_path.startswith("/workspace/"):
            return tool_error(
                "This Workbench can preview web URLs and files inside /workspace only. "
                "Use the Files pane for other local content."
            )

    label = (label or "").strip()
    try:
        ok = desktop_ui.emit(
            "preview.open",
            {"url": target, "label": label, "bookmark": bookmark is True},
        )
    except Exception as exc:
        return tool_error(f"Failed to open the preview pane: {exc}")
    if not ok:
        return tool_error("The preview pane is only available in the Hermes desktop app.")

    return json.dumps(
        {"success": True, "url": target, "label": label, "bookmarked": bookmark is True},
        ensure_ascii=False,
    )


OPEN_PREVIEW_SCHEMA = {
    "name": "open_preview",
    "description": (
        "Open something in the preview pane beside the chat in the Hermes desktop "
        "app. Use this when the user asks to see a page, dev server, or file in the "
        "preview pane — e.g. \"open cnn.com in the preview pane\" or \"preview "
        "localhost:3000\". Also use it to visibly open a map or directions URL when "
        "the user asks to pull up a map, route, location, or directions. Accepts a "
        "web URL (a bare domain like www.cnn.com is fine), "
        "a localhost dev-server URL, or a file path. In authenticated Workbench "
        "mode, local paths must be inside /workspace and open in the source editor; "
        "web URLs open in the isolated browser. The pane opens for the current window only."
    ),
    "parameters": {
        "type": "object",
        "properties": {
            "url": {
                "type": "string",
                "description": (
                    "What to preview: a web URL (https://… or a bare domain), a "
                    "localhost URL (localhost:3000), or a file path."
                ),
            },
            "label": {
                "type": "string",
                "description": "Optional tab label; defaults to the target's name.",
            },
            "bookmark": {
                "type": "boolean",
                "description": "Also save this web page to the persistent Photon browser bookmarks list.",
                "default": False,
            },
        },
        "required": ["url"],
    },
}


registry.register(
    name="open_preview",
    toolset="desktop_ui",
    schema=OPEN_PREVIEW_SCHEMA,
    handler=lambda args, **kw: open_preview_tool(
        url=args.get("url", ""),
        label=args.get("label", ""),
        bookmark=args.get("bookmark") is True,
    ),
    emoji="🖼️",
)
