"""Deterministic CPU-family guard for UDT/DB writes.

The platform matrix is the single source of the rules; this guard applies them
*before* a spec reaches TIA, because a type created with an unsupported datatype
imports "successfully" and then can never be exported again (TIA refuses
inconsistent types), which leaves no source to repair it from. Prevention is the
only reliable path for that case.

Only datatypes marked "auto retype" in the matrix are rejected here — the same
set an automatic repair may rewrite, so the two can never disagree.
"""

import json
from typing import Any, Dict, Optional

_GUARDED_TOOLS = frozenset({"build_udt", "build_global_db", "patch_block_interface"})


def _live_family() -> str:
    try:
        from tools.tia_client import get_environment_profile

        return str((json.loads(get_environment_profile()) or {}).get("PlcFamily") or "")
    except Exception:  # noqa: BLE001 - a guard must never block on a profile hiccup
        return ""


def _iter_member_pairs(value):
    """Yield (name, datatype) pairs from either the builder spec or the canonical IR."""
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except Exception:  # noqa: BLE001 - unparseable spec is the tool's problem, not ours
            return
    if isinstance(value, dict):
        attrs = value.get("attrs")
        if isinstance(attrs, dict) and attrs.get("Datatype"):
            yield str(attrs.get("Name") or "?"), str(attrs["Datatype"])
        datatype = value.get("datatype") or value.get("dataType")
        if datatype and not isinstance(attrs, dict):
            yield str(value.get("name") or "?"), str(datatype)
        for child in value.values():
            yield from _iter_member_pairs(child)
    elif isinstance(value, list):
        for child in value:
            yield from _iter_member_pairs(child)


def validate_write_spec(tool_name: str, args: Any, family: Optional[str] = None) -> Optional[str]:
    """Return a refusal message when the spec uses a datatype this CPU rejects, else None."""
    if tool_name not in _GUARDED_TOOLS or not isinstance(args, dict):
        return None

    resolved_family = family if family is not None else _live_family()
    if not resolved_family or resolved_family.strip().lower() == "unknown":
        return None

    from tools.siemens_platform import replacements_for

    replacements = replacements_for(resolved_family)
    if not replacements:
        return None
    by_lower: Dict[str, tuple] = {key.lower(): (key, value) for key, value in replacements.items()}

    # Builders take a spec object under "json"; the interface patch takes the canonical IR
    # under "interface". Both are scanned the same way, so no write path can create an
    # inconsistent type/block behind this guard.
    spec = args.get("json") if tool_name != "patch_block_interface" else args.get("interface")
    if spec is None:
        return None

    for name, datatype in _iter_member_pairs(spec):
        hit = by_lower.get(datatype.strip().lower())
        if not hit:
            continue
        key, replacement = hit
        return (
            f"Error: unsupported_datatype_on_cpu: member '{name}' uses {datatype}, which "
            f"{resolved_family} does not support; use {replacement} instead (platform matrix). "
            "The write was refused before import, so nothing was created."
        )
    return None
