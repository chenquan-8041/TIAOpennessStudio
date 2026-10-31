"""Deterministic compile loop + type-level repair (no LLM involved).

Only newly introduced errors count: the executor captures a compile_snapshot baseline
before the write, the compiler node compiles through compile_diff, and a block-less
(type-level) new error triggers the C# ``repair_inconsistent_types`` tool, which
patches the **last indexed snapshot** (interface-only, everything else byte-identical)
with the replacements taken from the single platform matrix. Unsafe cases (no
snapshot, nested struct, start value, non-requested datatype) are refused by that
tool and reported instead of guessed.

``call`` is injectable so the flow is testable without TIA.
"""

import json
import logging
from typing import Any, Callable, Dict, Optional

from core.request_context import rid_headers

logger = logging.getLogger("backend")

_TIA_MCP_URL = "http://127.0.0.1:8001/mcp"
_READ_TIMEOUT_S = 700.0


def _http_call(name: str, arguments: Dict[str, Any]) -> str:
    """One MCP tools/call over the built-in TIA server; returns the text payload."""
    import httpx

    body = {"jsonrpc": "2.0", "id": "1", "method": "tools/call",
            "params": {"name": name, "arguments": arguments}}
    with httpx.Client(timeout=httpx.Timeout(connect=10.0, read=_READ_TIMEOUT_S, write=10.0, pool=10.0)) as client:
        response = client.post(_TIA_MCP_URL, json=body, headers=rid_headers())
        response.raise_for_status()
        data = response.json()
    result = data.get("result") or {}
    text = "\n".join(c.get("text", "") for c in (result.get("content") or []) if isinstance(c, dict))
    return text if text else json.dumps(result, ensure_ascii=False)


def _json(text: str) -> Optional[dict]:
    try:
        data = json.loads(text)
        return data if isinstance(data, dict) else None
    except Exception:  # noqa: BLE001 - non-JSON means a plain-text tool answer
        return None


def take_baseline(call: Optional[Callable[[str, Dict[str, Any]], str]] = None) -> None:
    """Capture the pre-write error set so post-write errors can be split into new vs pre-existing."""
    try:
        (call or _http_call)("compile_snapshot", {})
    except Exception as exc:  # noqa: BLE001 - the baseline is advisory, never fatal
        logger.warning(f"[REPAIR] baseline snapshot unavailable: {type(exc).__name__}: {exc}")


def _compile(call: Callable[[str, Dict[str, Any]], str]) -> Dict[str, Any]:
    """Prefer compile_diff (new errors only); fall back to a full structured compile."""
    diff = _json(call("compile_diff", {}))
    if diff is not None and not diff.get("baseline_missing"):
        new_errors = diff.get("new_errors") or []
        return {"success": int(diff.get("new_count") or 0) == 0, "errors": new_errors, "mode": "diff"}

    data = _json(call("compile", {}))
    if data is None:
        return {"unavailable": "compile returned no structured result"}
    return {
        "success": bool(data.get("success")),
        "errors": data.get("errors") or [],
        "mode": "structured",
        # C# probes the index for objects that could own a block-less error, so the caller can
        # offer real candidates instead of guessing one.
        "suspects": data.get("unknown_block_suspects") or [],
    }


def _blockless(error: Dict[str, Any]) -> bool:
    block = str((error or {}).get("block") or "").strip()
    return block == "" or block.lower() == "unknown"


def _unattributed(errors) -> list:
    """Errors TIA reported without a block owner — the only ones a user may need to attribute."""
    return [e for e in (errors or []) if isinstance(e, dict) and _blockless(e)]


def _family(call: Callable[[str, Dict[str, Any]], str]) -> str:
    return str((_json(call("get_environment_profile", {})) or {}).get("PlcFamily") or "")


def repair_types(call: Callable[[str, Dict[str, Any]], str], family: str) -> Dict[str, Any]:
    """Ask the C# side to rebuild inconsistent types from their indexed snapshots."""
    from tools.siemens_platform import replacements_for

    replacements = replacements_for(family)
    if not replacements:
        return {"repaired": [], "skipped": [], "reason": f"no matrix replacement for '{family}'"}

    data = _json(call("repair_inconsistent_types", {"replacements": replacements})) or {}
    return {
        "repaired": data.get("repaired") or [],
        "skipped": data.get("skipped") or [],
        "compile": data.get("compile"),
    }


def auto_compile_and_repair(
    call: Optional[Callable[[str, Dict[str, Any]], str]] = None,
    family: Optional[str] = None,
) -> Dict[str, Any]:
    """Compile; repair block-less (type-level) new errors deterministically; compile again."""
    call = call or _http_call
    try:
        first = _compile(call)
        if first.get("unavailable"):
            return {"unavailable": first["unavailable"]}
        if first["success"]:
            return {"success": True, "errors": [], "repairs": [], "skipped": [], "mode": first["mode"],
                    "unattributed": []}
        if not any(_blockless(e) for e in first["errors"]):
            return {"success": False, "errors": first["errors"], "repairs": [], "skipped": [],
                    "mode": first["mode"], "unattributed": []}

        report = repair_types(call, family or _family(call))
        if not report.get("repaired"):
            return {"success": False, "errors": first["errors"], "repairs": [],
                    "skipped": report.get("skipped") or [], "mode": first["mode"],
                    "repair_note": report.get("reason"),
                    "unattributed": _unattributed(first["errors"]),
                    "suspects": first.get("suspects") or []}

        second = _compile(call)
        if second.get("unavailable"):
            return {"success": False, "errors": first["errors"], "repairs": report["repaired"],
                    "skipped": report.get("skipped") or [], "mode": second.get("mode"),
                    "unattributed": _unattributed(first["errors"]),
                    "suspects": first.get("suspects") or []}
        logger.info(f"[REPAIR] type repair applied: {report['repaired']}")
        remaining = second.get("errors") or []
        return {"success": bool(second["success"]), "errors": remaining,
                "repairs": report["repaired"], "skipped": report.get("skipped") or [],
                "mode": second.get("mode"),
                "unattributed": [] if second["success"] else _unattributed(remaining),
                "suspects": (second.get("suspects") or first.get("suspects") or [])}
    except Exception as exc:  # noqa: BLE001 - the loop must never break on a repair error
        logger.warning(f"[REPAIR] automatic repair unavailable: {type(exc).__name__}: {exc}")
        return {"unavailable": f"{type(exc).__name__}: {exc}"}
