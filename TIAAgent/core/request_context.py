"""Per-request correlation id (rid).

The C# host sends one rid per agent run. It is kept in a ContextVar so that:
  - every line the "backend" logger writes during the run carries it,
  - every call back into the C# :8001 server (REST and MCP) sends it as X-Request-Id,
    which lets the C# write gate and its audit trail name the run that asked.

ContextVar notes: langchain runs sync tools through run_in_executor, which copies the current
context, and asyncio tasks copy it when they are created. So the value must be set BEFORE the
work is started (see with_heartbeat in app/main.py, which sets it first and only then creates tasks).
"""

import logging
import re
from contextvars import ContextVar
from typing import Optional

RID_HEADER = "X-Request-Id"

_MAX_RID_LEN = 32
_SAFE = re.compile(r"[^A-Za-z0-9_\-]")

_current_rid: ContextVar[str] = ContextVar("tia_request_rid", default="")


def sanitize_rid(raw: Optional[str]) -> str:
    """Keep only characters that are safe in a log line and an HTTP header value."""
    return _SAFE.sub("", (raw or "").strip())[:_MAX_RID_LEN]


def set_rid(raw: Optional[str]) -> str:
    """Set the rid for the current context and return the sanitized value."""
    rid = sanitize_rid(raw)
    _current_rid.set(rid)
    return rid


def get_rid() -> str:
    return _current_rid.get()


def rid_headers() -> dict:
    """Headers to add to a call into the C# host. Empty when there is no rid."""
    rid = _current_rid.get()
    return {RID_HEADER: rid} if rid else {}


class RidLogFilter(logging.Filter):
    """Prefix log messages with [rid=...] unless the message already names the rid."""

    def filter(self, record: logging.LogRecord) -> bool:
        rid = _current_rid.get()
        if rid and isinstance(record.msg, str) and "rid=" not in record.msg:
            record.msg = f"[rid={rid}] {record.msg}"
        return True
