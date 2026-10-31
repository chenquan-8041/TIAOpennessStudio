"""Internal client for the C# :8001 tool endpoints (no LangChain @tool wrappers).

The C# side exposes the same capabilities twice: MCP at ``/mcp`` (model-facing) and
plain REST at ``/api/tool/*``. Model-facing tools come exclusively from the MCP
catalog (tools/catalog.py); this module is the only Python caller of the REST
endpoints, and only for deterministic internal control flow (environment check,
post-write block compile, repair experience recall/feedback).

Keeping these as plain functions means they are never bound into an agent and
never duplicated into the tool catalog — no second definition to keep in sync.

Return contract matches the C# envelope: the ``result`` string on success,
``"Error: <reason>"`` or ``"TIA tool error (<path>): <reason>"`` on failure. Callers
that must not break a run treat those prefixes as "unavailable".
"""

import json

import httpx

from core.request_context import rid_headers

_TIA_CTX_BASE = "http://127.0.0.1:8001"
# Read tools: connect 5 s / read 10 s keeps a stage from blocking the agent loop.
_TIA_TIMEOUT = httpx.Timeout(connect=5.0, read=10.0, write=5.0, pool=5.0)
# Compiles can take tens of seconds.
_TIA_WRITE_TIMEOUT = httpx.Timeout(connect=5.0, read=90.0, write=30.0, pool=5.0)
# Asking the user: sized above the C# interaction hard timeout (600 s) so the card, not the
# socket, decides how long the wait lasts.
_TIA_ASK_TIMEOUT = httpx.Timeout(connect=5.0, read=700.0, write=10.0, pool=5.0)


def _post(path: str, body: dict, timeout: "httpx.Timeout | None" = None) -> str:
    """POST to the C# :8001 tool endpoint and return the result string."""
    try:
        with httpx.Client(timeout=timeout or _TIA_WRITE_TIMEOUT) as client:
            # X-Request-Id lets the C# write gate and its audit trail name the run that asked.
            r = client.post(f"{_TIA_CTX_BASE}{path}", json=body, headers=rid_headers())
            r.raise_for_status()
            data = r.json()
            if data.get("ok"):
                return str(data.get("result", "Success"))
            # A few endpoints (get_environment_profile, get_current_project) answer with the
            # raw payload instead of the {ok, result} envelope; return that text unchanged.
            if "ok" not in data:
                return r.text
            return f"Error: {data.get('error', 'Unknown error')}"
    except Exception as e:  # noqa: BLE001 - callers decide whether to fail closed
        return f"TIA tool error ({path}): {e}"


def get_environment_profile() -> str:
    """Live environment facts (TIA version, CPU family, order number, fingerprint)."""
    return _post("/api/tool/get_environment_profile", {}, timeout=_TIA_TIMEOUT)


def compile_block(block_name: str) -> str:
    """Compile one block and return the structured JSON result."""
    return _post("/api/tool/compile_block", {"block_name": block_name})


def ask_user(question: str, detail: str = "", hint: str = "") -> str:
    """Ask the user a free-text question through the C# interaction drawer (Input card).

    Only for the case where the system cannot name the owner of a compile error: the answer
    names the block/type, it is never used to approve a write. Returns the raw JSON
    ``{"status": "answered|denied|timed_out|cancelled|unavailable", "answer": "..."}``.
    """
    payload = {"question": question}
    if detail:
        payload["detail"] = detail
    if hint:
        payload["hint"] = hint
    return _post("/api/tool/ask_user", payload, timeout=_TIA_ASK_TIMEOUT)


def query_repair_experience(query: str, limit: int = 3) -> str:
    """Recall repair experience (compile error → root cause → fix) as a JSON array."""
    return _post("/api/tool/query_repair_experience", {"query": query, "limit": limit}, timeout=_TIA_TIMEOUT)


def feedback_repair_experience(signatures: list, success: bool) -> str:
    """Feed a fix-round outcome back to the repair-experience store."""
    return _post(
        "/api/tool/feedback_repair_experience",
        {"signatures": signatures, "success": success},
        timeout=_TIA_TIMEOUT,
    )


def record_repair_experience(project_key: str, experienced: dict) -> str:
    """Store one extracted experience (the C# side scopes it to ``project_key``)."""
    causal_text = json.dumps(experienced, ensure_ascii=False, default=str)
    return _post(
        "/api/tool/record_repair_experience",
        {
            "project_key": project_key,
            "symptom": experienced.get("symptom") or "",
            "compiler_error": experienced.get("compiler_error") or "",
            "causal_text": causal_text,
            "confidence": experienced.get("confidence") or 0.0,
            "generality": experienced.get("generality") or 0.0,
        },
    )
