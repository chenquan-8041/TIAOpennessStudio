"""Background job management for project-plan execution (the stateful job model of design 4.12).

Decouples the Executor from the SSE connection: after /chat_plan (or /plan/run) produces/recovers a plan,
PlanJobManager.start runs run_project_plan_stream in a background asyncio.Task, pushing events into that plan's
asyncio.Queue as they are produced (execution state itself is still persisted in real time by plan_store).

Thus:
  - disconnecting / closing the session does not interrupt execution (the job is an independent Task, not tied to the HTTP response);
  - the frontend can follow live events via subscribe(plan_id), or poll progress via snapshot(plan_id);
  - after a process restart in-memory jobs are lost, but plan state is already in SQLite, so resume + status query still work.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any, Dict, Optional

from agent.plan_models import TaskStatus
from agent.plan_store import PlanStore

logger = logging.getLogger("backend")

# Event queue capacity cap (overflow is dropped to avoid a single stuck job exhausting memory; persistence is unaffected).
_QUEUE_MAX = 2000

# Sentinel event in the follow stream meaning "this plan's job has ended" (not an SSE event name, internal use only).
JOB_END_EVENT = "__job_end__"


class PlanJobManager:
    """One background Task + one event queue per plan_id; process-wide singleton."""

    def __init__(self, store: Optional[PlanStore] = None) -> None:
        self._store = store or PlanStore()
        self._tasks: Dict[str, asyncio.Task] = {}
        self._queues: Dict[str, asyncio.Queue] = {}

    def start(self, plan, llm, context_window: int = 0, verify_compile_at_end: bool = False) -> None:
        """Start (or reuse) a background execution job. Returns idempotently if already running."""
        task = self._tasks.get(plan.id)
        if task is not None and not task.done():
            return
        q: asyncio.Queue = asyncio.Queue(maxsize=_QUEUE_MAX)
        self._queues[plan.id] = q
        self._tasks[plan.id] = asyncio.create_task(
            self._run(plan, llm, q, context_window, verify_compile_at_end)
        )

    def running(self, plan_id: str) -> bool:
        task = self._tasks.get(plan_id)
        return task is not None and not task.done()

    def queue(self, plan_id: str) -> Optional[asyncio.Queue]:
        return self._queues.get(plan_id)

    def snapshot(self, plan_id: str) -> Optional[Dict[str, Any]]:
        """Build a progress snapshot from the persisted plan for status queries / reconnecting progress display."""
        plan = self._store.load(plan_id)
        if plan is None:
            return None
        total = len(plan.tasks)
        done = sum(1 for t in plan.tasks if t.status == TaskStatus.SUCCESS.value)
        failed = sum(1 for t in plan.tasks if t.status == TaskStatus.FAILED.value)
        current = None
        for t in plan.tasks:
            if t.status == TaskStatus.IN_PROGRESS.value:
                current = t.block_name or t.id
                break
        return {
            "plan_id": plan.id,
            "status": plan.status,
            "summary": plan.summary,
            "total": total,
            "done": done,
            "failed": failed,
            "current": current,
            "milestones": [m.to_dict() for m in plan.milestone_decisions],
        }

    async def _run(self, plan, llm, q: asyncio.Queue, context_window: int, verify_compile_at_end: bool = False) -> None:
        """Run run_project_plan_stream in the background, forwarding events into the queue; deliver the sentinel at the end."""
        from agent.executor import run_project_plan_stream

        try:
            async for event, delta in run_project_plan_stream(
                llm, plan, context_window=context_window,
                verify_compile_at_end=verify_compile_at_end,
            ):
                try:
                    q.put_nowait((event, delta))
                except asyncio.QueueFull:
                    logger.warning(f"[plan_job] event queue full for {plan.id}; dropping {event}")
        except Exception as exc:  # noqa: BLE001
            logger.exception(f"[plan_job] run failed for {plan.id}: {exc}")
            try:
                q.put_nowait(("error", str(exc)))
            except asyncio.QueueFull:
                pass
        finally:
            try:
                q.put_nowait((JOB_END_EVENT, ""))
            except asyncio.QueueFull:
                pass


# process-wide singleton (shared by endpoints).
plan_jobs = PlanJobManager()