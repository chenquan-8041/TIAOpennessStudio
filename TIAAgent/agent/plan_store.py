"""ProjectPlan persistence and resume (task 2).

Uses the stdlib sqlite3 to persist ProjectPlan to ``TIAAgent/logs/plans.db`` (a single table
``plans`` stores the full JSON snapshot). Resume only does "find the unfinished plan by project_key";
"already-success tasks are not redone" is guaranteed by the Executor's idempotent logic.

Persistence is fail-closed: all reads/writes catch exceptions and warn, never raising to the caller,
so SQLite failures do not interrupt the agent's main flow.
"""

from __future__ import annotations

import json
import logging
import os
import sqlite3
from contextlib import contextmanager
from typing import List, Optional

from agent.plan_models import ProjectPlan

logger = logging.getLogger("backend")


@contextmanager
def _connect(db_path: str):
    """Open a SQLite connection and "commit + close" at the end.

    The ``with`` block of ``sqlite3.Connection`` only handles commit/rollback and does not close the connection;
    using ``with sqlite3.connect(...)`` directly leaks handles in a long-running process (and locks the file on Windows).
    Here we use ``with conn:`` (commit/rollback) + ``finally: conn.close()`` uniformly.
    """
    conn = sqlite3.connect(db_path)
    try:
        with conn:
            yield conn
    finally:
        conn.close()

# database path; overridable via the PLAN_DB_PATH env var, defaulting to TIAAgent/logs/plans.db.
_ENV_DB = os.environ.get("PLAN_DB_PATH")
if _ENV_DB:
    DB_PATH = _ENV_DB
else:
    _BACKEND_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    DB_PATH = os.path.join(_BACKEND_ROOT, "logs", "plans.db")

# plan statuses treated as "unfinished" and eligible for resume.
_UNFINISHED = ("planning", "ready", "running", "paused")

_SCHEMA = """
CREATE TABLE IF NOT EXISTS plans (
    plan_id     TEXT PRIMARY KEY,
    project_key TEXT NOT NULL,
    status      TEXT NOT NULL,
    summary     TEXT NOT NULL DEFAULT '',
    plan_json   TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_plans_project ON plans(project_key, updated_at);
"""


class PlanStore:
    """SQLite persistence for ProjectPlan. Each operation uses an independent short-lived connection, naturally thread-safe."""

    def __init__(self, db_path: str = DB_PATH):
        self._db_path = db_path
        self._ensure_schema()

    def _ensure_schema(self) -> None:
        try:
            os.makedirs(os.path.dirname(self._db_path), exist_ok=True)
            with _connect(self._db_path) as conn:
                conn.executescript(_SCHEMA)
        except Exception as exc:  # noqa: BLE001
            logger.warning(f"[plan_store] schema init failed ({exc}); persistence disabled")

    def save(self, plan: ProjectPlan) -> bool:
        """Save (upsert) a whole plan. Returns True on success; warns and returns False on failure."""
        plan.touch()
        try:
            with _connect(self._db_path) as conn:
                conn.execute(
                    """
                    INSERT INTO plans
                        (plan_id, project_key, status, summary, plan_json, created_at, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(plan_id) DO UPDATE SET
                        status=excluded.status,
                        summary=excluded.summary,
                        plan_json=excluded.plan_json,
                        updated_at=excluded.updated_at
                    """,
                    (
                        plan.id, plan.project_key, plan.status, plan.summary,
                        json.dumps(plan.to_dict(), ensure_ascii=False),
                        plan.created_at, plan.updated_at,
                    ),
                )
            return True
        except Exception as exc:  # noqa: BLE001
            logger.warning(f"[plan_store] save({plan.id}) failed: {exc}")
            return False

    def load(self, plan_id: str) -> Optional[ProjectPlan]:
        """Load by plan id; returns None if missing or corrupt."""
        try:
            with _connect(self._db_path) as conn:
                row = conn.execute(
                    "SELECT plan_json FROM plans WHERE plan_id = ?", (plan_id,)
                ).fetchone()
            if row is None:
                return None
            return ProjectPlan.from_dict(json.loads(row[0]))
        except Exception as exc:  # noqa: BLE001
            logger.warning(f"[plan_store] load({plan_id}) failed: {exc}")
            return None

    def find_unfinished(self, project_key: str) -> Optional[ProjectPlan]:
        """Find the most recently updated unfinished plan for a project, for resume; returns None if none."""
        try:
            placeholders = ",".join("?" for _ in _UNFINISHED)
            with _connect(self._db_path) as conn:
                row = conn.execute(
                    f"""
                    SELECT plan_json FROM plans
                    WHERE project_key = ? AND status IN ({placeholders})
                    ORDER BY updated_at DESC LIMIT 1
                    """,
                    (project_key, *_UNFINISHED),
                ).fetchone()
            if row is None:
                return None
            return ProjectPlan.from_dict(json.loads(row[0]))
        except Exception as exc:  # noqa: BLE001
            logger.warning(f"[plan_store] find_unfinished({project_key}) failed: {exc}")
            return None

    def delete(self, plan_id: str) -> bool:
        """Delete by plan id. Returns True on success; otherwise warns and returns False."""
        try:
            with _connect(self._db_path) as conn:
                conn.execute("DELETE FROM plans WHERE plan_id = ?", (plan_id,))
            return True
        except Exception as exc:  # noqa: BLE001
            logger.warning(f"[plan_store] delete({plan_id}) failed: {exc}")
            return False