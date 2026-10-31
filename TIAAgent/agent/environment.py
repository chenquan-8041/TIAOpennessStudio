"""Live environment facts for planning and plan execution.

TIA version and CPU family are read live from the open TIA project through the
C# ``get_environment_profile`` endpoint and never cached: the user may switch the
CPU or the TIA version mid-session. A plan stores the facts it was built for;
execution compares them again and blocks only on CPU/version changes — a
different project is a warning, not a blocker.

This module is also the single place on the Python side that maps a CPU family
to compile-error hints; the prompt rule text itself lives in the C# context
block, so the two runtimes do not duplicate rule wording.
"""

from __future__ import annotations

import json
import logging
from typing import Any, Dict, List, Optional

logger = logging.getLogger("backend")

# Field names as emitted by the C# DTO (PascalCase); lookups are case-insensitive.
_FIELDS = {
    "tia_version": "TiaVersion",
    "project_key": "ProjectKey",
    "project_name": "ProjectName",
    "plc_name": "PlcName",
    "device_name": "DeviceName",
    "cpu_name": "CpuName",
    "order_number": "OrderNumber",
    "type_identifier": "TypeIdentifier",
    "plc_family": "PlcFamily",
    "fingerprint": "Fingerprint",
    "detected_at": "DetectedAt",
    "live": "Live",
}

# Changes that must stop execution: code planned for the old CPU/version can be invalid
# (e.g. DTL is accepted on S7-1500 but rejected on S7-300/400).
_BLOCKING_FIELDS = (
    ("plc_family", "PLC family"),
    ("order_number", "CPU order number"),
    ("tia_version", "TIA version"),
)

# Family-specific compile-error hints are NOT kept here: they live in the single
# matrix (tools/siemens_platform_matrix.md) and are parsed by tools/siemens_platform.py.


def normalize_profile(raw: Any) -> Dict[str, Any]:
    """Normalize a profile payload to lowercase snake_case keys with scalar values."""
    if not isinstance(raw, dict):
        return {}
    lower = {str(k).lower(): v for k, v in raw.items()}
    out: Dict[str, Any] = {}
    for snake, pascal in _FIELDS.items():
        value = lower.get(pascal.lower())
        if value is None:
            continue
        out[snake] = value if isinstance(value, bool) else str(value)
    return out


def fetch_profile() -> Optional[Dict[str, Any]]:
    """Read the live environment profile; returns None when TIA is unreachable.

    Synchronous by design: callers wrap it in ``asyncio.to_thread`` so a slow
    Openness read never blocks the graph's event loop.
    """
    try:
        from tools.tia_client import get_environment_profile  # lazy: avoid import cycles

        text = get_environment_profile()
        if not isinstance(text, str) or not text.strip().startswith("{"):
            logger.warning(f"[environment] profile unavailable: {str(text)[:200]}")
            return None
        return normalize_profile(json.loads(text)) or None
    except Exception as exc:  # noqa: BLE001 - an environment check must never break a run
        logger.warning(f"[environment] fetch failed: {type(exc).__name__}: {exc}")
        return None


def fingerprint_of(profile: Optional[Dict[str, Any]]) -> str:
    return str((profile or {}).get("fingerprint") or "")


def describe_for_prompt(profile: Optional[Dict[str, Any]]) -> str:
    """Facts-only one-liner for a task prompt.

    Deliberately does not repeat the family rules: those are rendered once in the
    host's ``## Environment Profile`` context block.
    """
    if not profile:
        return ""
    parts: List[str] = []
    if profile.get("plc_family"):
        parts.append(f"PLC family: {profile['plc_family']}")
    if profile.get("order_number"):
        parts.append(f"CPU order number: {profile['order_number']}")
    if profile.get("tia_version"):
        parts.append(f"TIA version: {profile['tia_version']}")
    if profile.get("project_name"):
        parts.append(f"Project: {profile['project_name']}")
    fp = fingerprint_of(profile)
    if fp:
        parts.append(f"Environment fingerprint: {fp}")
    return ("Environment: " + "; ".join(parts)) if parts else ""


def detect_change(
    stored: Optional[Dict[str, Any]], live: Optional[Dict[str, Any]]
) -> Dict[str, List[str]]:
    """Compare the plan's environment facts with the live ones.

    ``blocking`` stops execution (CPU family / order number / TIA version changed).
    ``warnings`` continue (different project, or the live facts are unavailable).
    """
    result: Dict[str, List[str]] = {"blocking": [], "warnings": []}
    stored = stored or {}
    live = live or {}
    if not live:
        result["warnings"].append(
            "Live environment profile is unavailable; continuing without the pre-check."
        )
        return result

    for field, label in _BLOCKING_FIELDS:
        old, new = stored.get(field), live.get(field)
        if old and new and str(old) != str(new):
            result["blocking"].append(f"{label} changed: {old} -> {new}")

    old_project, new_project = stored.get("project_key"), live.get("project_key")
    if old_project and new_project and str(old_project) != str(new_project):
        result["warnings"].append(f"Project changed: {old_project} -> {new_project}")

    return result


def annotate_compile_error(message: str, family: Optional[str]) -> str:
    """Append the CPU-family hint for a compile/import message.

    The facts come from the single platform matrix (tools/siemens_platform_matrix.md);
    a family that supports the item returns the message unchanged.
    """
    text = (message or "").strip()
    if not text or not family:
        return text
    try:
        from tools.siemens_platform import annotate  # local import: no cost when unused
        return annotate(text, family)
    except Exception:  # noqa: BLE001 - annotation must never break a run
        return text
