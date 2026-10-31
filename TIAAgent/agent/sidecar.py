"""Sidecar parallel context engine — prefetch/refine context alongside the main loop.

While the main model reasons, the sidecar prefetches a batch of context in
parallel background tasks:
  1. RAG project-knowledge evidence (build_context_pack / search_project)
  2. repair-experience hints (query_repair_experience)
  3. current project state (get_current_project)

The results are ready before the main model needs them, so the user perceives no
extra latency (and, more importantly, the main model no longer has to do RAG
lookups inside its ReAct loop, saving an LLM + tool round-trip per lookup).

Design principles:
  - fail-closed: a per-task failure/timeout yields an empty string; never raise,
    never block the main flow.
  - the sidecar invokes the real tool objects directly through the catalog
    (get_catalog()[name].tool.ainvoke), not the discover/invoke meta-tools, so no
    extra LLM decision happens on the side path.
  - each logical slot tries its candidate catalog tool names in order; when MCP
    is down the catalog has no TIA tools and the slot simply yields nothing.
"""

import asyncio
import logging
import os
from typing import Any, Dict, List, Tuple

from tools.catalog import get_catalog
from tools.meta_tools import _tool_output_to_text

logger = logging.getLogger("backend")

# Total prefetch budget (seconds); unfinished tasks are discarded past this.
_SIDECAR_BUDGET_S = float(os.getenv("TIA_SIDECAR_BUDGET_S", "6.0"))

# Output markers treated as "no useful result / not ready" (prefix match, case-insensitive).
# The Chinese markers are kept because the C# side emits Chinese error/notice strings.
_SKIP_PREFIXES = (
    "error:", "tia tool error", "tia context service unavailable",
    "未连接", "未找到", "项目不匹配", "no global tags", "no udt",
    "project mismatch",
)


def _usable(text: str) -> str:
    """Return injectable text, or '' for error/empty/not-ready output."""
    if not text or not text.strip():
        return ""
    stripped = text.strip()
    low = stripped.lstrip().lower()
    for p in _SKIP_PREFIXES:
        if low.startswith(p):
            return ""
    return stripped


async def _try_fetch(candidates: List[Tuple[str, dict]]) -> str:
    """Try candidate tools in order; return the first injectable non-empty result.

    Uses ainvoke: MCP tools are async-only StructuredTools whose sync .invoke()
    raises "StructuredTool does not support sync invocation". MCP outputs arrive
    as content blocks, so they are flattened to plain text.
    """
    catalog = get_catalog()
    for name, args in candidates:
        entry = catalog.get(name)
        if entry is None:
            continue
        try:
            out = await entry.tool.ainvoke(args)
        except Exception as e:
            logger.warning(f"[sidecar] {name} invoke failed: {e}")
            continue
        text = _usable(_tool_output_to_text(out))
        if text:
            return text
    return ""


def sidecar_context_block(results: Dict[str, str]) -> str:
    """Assemble sidecar results into an injectable Markdown context block ('' when empty)."""
    labels = {
        "rag": "Project knowledge evidence (pre-fetched)",
        "repair": "Repair-experience hints (pre-fetched)",
        "project": "Current project state (pre-fetched)",
    }
    parts = []
    for key, label in labels.items():
        text = (results.get(key) or "").strip()
        if text:
            parts.append(f"## {label}\n{text}")
    return "\n\n".join(parts) if parts else ""


async def run_sidecar(
    prompt: str,
    context: str = "",
    project_key: str = "",
    budget: float = _SIDECAR_BUDGET_S,
) -> Dict[str, str]:
    """Prefetch context in parallel; returns {rag, repair, project} blocks.

    fail-closed: a whole-run failure returns an all-empty dict; a per-task
    failure/timeout yields '' for that slot.
    """
    q = (prompt or "").strip() or (context or "").strip() or "project"

    slots: Dict[str, List[Tuple[str, dict]]] = {
        "rag": [
            ("build_context_pack", {"query": q, "top_k": 5}),
            ("search_project", {"query": q, "limit": 10}),
        ],
        "repair": [
            ("query_repair_experience", {"query": q, "limit": 3}),
        ],
        "project": [
            ("get_current_project", {}),
        ],
    }

    tasks: Dict[str, asyncio.Task] = {
        key: asyncio.create_task(_try_fetch(candidates))
        for key, candidates in slots.items()
    }

    results: Dict[str, str] = {key: "" for key in slots}
    try:
        done, pending = await asyncio.wait(list(tasks.values()), timeout=budget)
        for key, task in tasks.items():
            if task in done:
                try:
                    results[key] = task.result()
                except Exception:
                    results[key] = ""
        # Cancel timed-out tasks and await them to avoid "Task was destroyed" warnings.
        for task in pending:
            task.cancel()
        if pending:
            await asyncio.gather(*pending, return_exceptions=True)
    except Exception as e:
        logger.warning(f"[sidecar] run_sidecar failed: {e}")

    return results