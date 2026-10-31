"""
Tool assembly — builds the shared catalog and returns per-role meta-tool sets.

Under the progressive-disclosure design (Design A), agents no longer receive full
tool schemas. Each role is bound to exactly two meta-tools:
  - discover_tools  (query the shared catalog for available tools)
  - invoke_tool     (dispatch to the real tool by name)

The catalog (tools/catalog.py) is the single source of truth: it enumerates MCP +
local + static tools once and caches them at process level. MCP enumeration
failure leaves the local/static tiers available, so this assembly never blocks
the run.
"""

import logging
from typing import Any, Dict, List

from tools.catalog import build_catalog
from tools.meta_tools import build_discover_tool, build_invoke_tool

logger = logging.getLogger("backend")

_ROLES = ("coder", "reviewer", "compiler", "chat", "planner")


async def build_agent_toolsets() -> Dict[str, List[Any]]:
    """Assemble per-role toolsets (each role = the two meta-tools).

    Builds/populates the shared catalog first (idempotent, cached). Returns
    {"coder": [...], "reviewer": [...], "compiler": [...], "chat": [...]}.
    """
    await build_catalog()
    toolsets: Dict[str, List[Any]] = {
        role: [build_discover_tool(role), build_invoke_tool(role)]
        for role in _ROLES
    }
    logger.info("[REGISTRY] toolsets built — each role = [discover_tools, invoke_tool]")
    return toolsets