"""
MCP Client — dynamically enumerate tools from MCP servers at runtime and adapt
them to langchain BaseTool.

Architecture role (see docs/agent-tool-architecture-plan.md):
  The Python agent no longer hand-writes REST @tools for its TIA expertise;
  instead it dynamically enumerates them over the MCP protocol from the C#-side
  built-in MCP Server (127.0.0.1:8001/mcp, standard JSON-RPC:
  initialize / tools/list / tools/call), sharing one mechanism with third-party
  MCP servers.

Design principles:
  - Dynamic enumeration: whatever tools C#'s /mcp exposes, Python automatically
    gets — no hardcoding.
  - Config-driven: the server list is extensible (in-house + third-party) via the
    TIA_MCP_SERVERS environment variable.
  - Degrade without blocking: any server connection/enumeration failure never
    throws to interrupt the agent; it returns an empty toolset and logs. The core
    agent loop doesn't crash because the extension layer failed.
  - Single init + cache: tools are enumerated once and cached, avoiding a
    reconnect on every agent build.

Dependency: langchain-mcp-adapters (MultiServerMCPClient). When not installed,
this module degrades entirely to "return an empty tool list"; the agent can still
run with local generic/static tools.
"""

import json
import logging
import os
from typing import Any, Dict, List, Optional

from core.request_context import rid_headers

logger = logging.getLogger("backend")

_TIA_SERVER_NAME = "tia"


async def _rid_interceptor(request, handler):
    """Send the run's rid to the built-in TIA server so its write gate can audit by run.

    Only the in-house server gets the header; third-party MCP servers must not receive it.
    """
    if getattr(request, "server_name", "") == _TIA_SERVER_NAME:
        extra = rid_headers()
        if extra:
            request = request.override(headers={**(request.headers or {}), **extra})
    return await handler(request)

# C#-side built-in MCP Server (in-house TIA capabilities). streamable_http
# stateless POST mode, interfacing with the JSON-RPC endpoint of
# TiaContextHttpServer.HandleMcpJsonRpcAsync.
_DEFAULT_TIA_MCP_URL = "http://127.0.0.1:8001/mcp"

# Connect/write/pool timeout and per-read timeout for the built-in TIA MCP server (seconds).
_TIA_MCP_CONNECT_TIMEOUT_S = 10.0
# Gated writes can sit on the approval card until the C# hard deadline (600 s) and the card is
# re-shown up to ApprovalMaxAttempts (3) times before the write is refused. Keep the read timeout
# above attempts × hard deadline (1800 s) so a tool call is not cut off while a person decides.
_TIA_MCP_READ_TIMEOUT_S = 1900.0

# Process-level cache: the enumerated tool list (langchain BaseTool). None means not yet initialized.
_cached_tools: Optional[List[Any]] = None


def _build_server_config() -> Dict[str, Dict[str, Any]]:
    """Build the server config for MultiServerMCPClient.

    Includes the in-house TIA MCP server by default. Third-party servers can be
    injected via the ``TIA_MCP_SERVERS`` environment variable (a JSON object shaped
    like ``{"name": {"url": "...", "transport": "streamable_http"}}``) and merged
    with the in-house config. On parse failure, the third-party config is ignored
    and the in-house server is kept.
    """
    tia_url = os.getenv("TIA_MCP_URL", _DEFAULT_TIA_MCP_URL)
    servers: Dict[str, Dict[str, Any]] = {
        _TIA_SERVER_NAME: {
            "url": tia_url,
            "transport": "streamable_http",
            # Write tools are held on the C# side until the user answers an approval card (up to 3 cards ×
            # 600 s, then refusal). The adapter's default read timeout is 300 s, which is far too close to
            # that. State it explicitly so it cannot drift with a library upgrade.
            "timeout": _TIA_MCP_CONNECT_TIMEOUT_S,
            "sse_read_timeout": _TIA_MCP_READ_TIMEOUT_S,
        }
    }

    extra_raw = os.getenv("TIA_MCP_SERVERS", "").strip()
    if extra_raw:
        try:
            extra = json.loads(extra_raw)
            if isinstance(extra, dict):
                for name, cfg in extra.items():
                    if isinstance(cfg, dict) and "url" in cfg:
                        cfg.setdefault("transport", "streamable_http")
                        servers[name] = cfg
            else:
                logger.warning("[MCP] TIA_MCP_SERVERS is not a JSON object, ignored")
        except Exception as e:
            logger.warning(f"[MCP] failed to parse TIA_MCP_SERVERS ({e}), ignored")

    return servers


async def load_mcp_tools(force_reload: bool = False) -> List[Any]:
    """Connect to all configured MCP servers and enumerate tools, returning a list of
    langchain BaseTool objects.

    Single init + process-level cache. Any failure degrades to returning the
    existing cache (or an empty list) and never throws to interrupt the caller.

    Args:
        force_reload: When True, ignore the cache and re-enumerate (for refreshing
            after config changes).

    Returns:
        A list of langchain BaseTool objects; empty on failure.
    """
    global _cached_tools

    if _cached_tools is not None and not force_reload:
        return _cached_tools

    try:
        # Lazy import: degrade entirely when langchain-mcp-adapters is not installed, without affecting the other tools.
        from langchain_mcp_adapters.client import MultiServerMCPClient
    except Exception as e:
        logger.error(
            f"[MCP] langchain-mcp-adapters not available ({e}); "
            f"MCP tools disabled, agent will run with local tools only"
        )
        _cached_tools = []
        return _cached_tools

    servers = _build_server_config()
    try:
        client = MultiServerMCPClient(servers, tool_interceptors=[_rid_interceptor])
        tools = await client.get_tools()
        _cached_tools = list(tools or [])
        names = ", ".join(getattr(t, "name", "?") for t in _cached_tools)
        logger.info(
            f"[MCP] enumerated {len(_cached_tools)} tool(s) from "
            f"{len(servers)} server(s): {names}"
        )
        return _cached_tools
    except Exception as e:
        logger.error(f"[MCP] tool enumeration failed ({e}); returning empty tool set")
        # Keep the old cache (if any), otherwise an empty list. Degrade without blocking.
        if _cached_tools is None:
            _cached_tools = []
        return _cached_tools


def get_cached_mcp_tools() -> List[Any]:
    """Synchronously get the cached enumerated MCP tools (no network call). Returns an empty list when not initialized."""
    return list(_cached_tools) if _cached_tools else []


def reset_mcp_cache() -> None:
    """Clear the cache (for tests / config refresh)."""
    global _cached_tools
    _cached_tools = None
