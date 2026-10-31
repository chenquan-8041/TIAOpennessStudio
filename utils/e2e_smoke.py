"""End-to-end smoke for the running TIA Openness Studio MCP server (:8001).

Checks the pieces that regressed in the past and are easy to break again:
  1. get_current_project      -- the knowledge worker is attached to the open project and has an index
  2. search_project           -- RAG returns real hits (not a fake empty success)
  3. get_environment_profile  -- live TIA version + CPU family + fingerprint
  4. compile_snapshot / compile_diff -- baseline diff is taken and reports zero new errors

Read-only by default. ``--delete-probes`` additionally removes the leftover probe blocks
listed in ``PROBE_BLOCKS``; each delete raises an approval card in the app, so run this
only when a person is in front of the UI.

Usage (from the repository root):
    python utils/e2e_smoke.py
    python utils/e2e_smoke.py --url http://127.0.0.1:8001/mcp --token <token>
    python utils/e2e_smoke.py --delete-probes

Exit code 0 = every check passed, 1 = at least one failed.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.request

# Local calls must bypass any system proxy (a configured proxy breaks 127.0.0.1).
urllib.request.getproxies = lambda: {}

PROBE_BLOCKS = ("ZZ_OB_Probe", "ZZ_OB_Probe2", "ZZ_OB_Body_Tmp", "ZZ_LAD_Probe")


_result_count = 0
_failure_count = 0


def call_mcp(url: str, token: str | None, tool: str, arguments: dict, timeout: int = 120) -> str:
    """Invoke one MCP tool and return the text payload (or an ``Error:`` string)."""
    body = json.dumps({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "tools/call",
        "params": {"name": tool, "arguments": arguments},
    }).encode("utf-8")
    request = urllib.request.Request(url, data=body, method="POST")
    request.add_header("Content-Type", "application/json; charset=utf-8")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read().decode("utf-8"))
    return payload["result"]["content"][0]["text"]


def check(name: str, ok: bool, detail: str) -> None:
    global _result_count, _failure_count
    _result_count += 1
    if not ok:
        _failure_count += 1
    print(("PASS " if ok else "FAIL ") + name + " :: " + detail)


def parse_json(text: str) -> dict | None:
    try:
        data = json.loads(text)
        return data if isinstance(data, dict) else None
    except (ValueError, TypeError):
        return None


def run_checks(url: str, token: str | None) -> None:
    # 1) Worker attached + index available
    text = call_mcp(url, token, "get_current_project", {})
    info = parse_json(text) or {}
    project_key = info.get("project_key") or ""
    check("worker/attached", bool(project_key) and info.get("is_connected") is True,
          f"project_key={project_key or '<empty>'} connected={info.get('is_connected')}")
    check("worker/has-index", info.get("has_index") is True,
          f"has_index={info.get('has_index')} stale={info.get('is_stale')}")

    # 2) RAG returns real hits
    text = call_mcp(url, token, "search_project", {"query": "Motor", "limit": 3})
    hits = (parse_json(text) or {}).get("result") or []
    check("rag/search-hits", len(hits) > 0,
          "hits=" + str(len(hits)) + (" first=" + str(hits[0].get("BlockName")) if hits else ""))

    # 3) Live environment facts
    text = call_mcp(url, token, "get_environment_profile", {})
    env = parse_json(text) or {}
    family = env.get("PlcFamily") or ""
    fingerprint = env.get("Fingerprint") or ""
    check("env/live", env.get("Live") is True, f"Live={env.get('Live')} version={env.get('TiaVersion')}")
    check("env/family", family in ("S7-300/400", "S7-1200", "S7-1500"),
          f"family={family or '<empty>'} order={env.get('OrderNumber') or '<empty>'}")
    check("env/fingerprint", len(fingerprint) == 12,
          f"fingerprint={fingerprint or '<empty>'}")

    # 4) Compile baseline diff (slow: compiles the PLC twice)
    text = call_mcp(url, token, "compile_snapshot", {}, timeout=300)
    snap = parse_json(text) or {}
    check("compile/snapshot", "error_count" in snap,
          f"baseline={snap.get('baseline_id')} error_count={snap.get('error_count')}")
    text = call_mcp(url, token, "compile_diff", {}, timeout=300)
    diff = parse_json(text) or {}
    check("compile/diff-no-new", diff.get("new_count") == 0,
          f"new={diff.get('new_count')} pre_existing={diff.get('pre_existing_count')} "
          f"resolved={diff.get('resolved_count')}")


def delete_probes(url: str, token: str | None) -> None:
    """Remove the leftover probe blocks. Every delete waits for the approval card."""
    print("\n-- deleting probe blocks (approve each card in the app) --")
    for name in PROBE_BLOCKS:
        text = call_mcp(url, token, "delete_block", {"block_name": name}, timeout=700)
        ok = "Error:" not in text and "user_denied" not in text and "approval_timeout" not in text
        check("cleanup/" + name, ok, text[:160].replace("\n", " "))


def main() -> int:
    parser = argparse.ArgumentParser(description="TIA Openness Studio end-to-end smoke")
    parser.add_argument("--url", default="http://127.0.0.1:8001/mcp")
    parser.add_argument("--token", default="", help="Bearer token when the server enforces auth")
    parser.add_argument("--delete-probes", action="store_true",
                        help="also delete the leftover probe blocks (requires UI approval)")
    args = parser.parse_args()

    try:
        run_checks(args.url, args.token or None)
        if args.delete_probes:
            delete_probes(args.url, args.token or None)
    except Exception as exc:  # noqa: BLE001 - report transport failures as a failed check
        check("transport", False, f"{type(exc).__name__}: {exc}")

    print(f"\n{_result_count - _failure_count}/{_result_count} checks passed")
    return 0 if _failure_count == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
