"""Advisory plan contract checks (T4-light).

The planner can emit interfaces that are incomplete (empty members), internally
inconsistent (FC/FB without IO) or that reference names nothing in the plan defines.
Users bring every kind of project shape, so these findings must NOT reject a plan or
trigger replanning. They annotate each task with warnings that are:

  * persisted with the plan (Task.warnings),
  * summarised in the plan start notice,
  * appended to the coder's task prompt so it verifies the real project before writing.

Coupling with the live project is deliberately out of scope here: a name missing from
the plan becomes "verify it exists in the project", not an error.
"""

from __future__ import annotations

import logging
from typing import List, Set

from agent.plan_models import ProjectPlan, Task, TaskKind

logger = logging.getLogger("backend")

# Block kinds that must expose at least one input or output.
_IO_KINDS = frozenset({"FC", "FB", "LAD_FC", "LAD_FB", "GRAPH_FB"})


def _warn(task: Task, message: str) -> None:
    task.warnings.append(message)


def _flattened_members(task: Task) -> List[str]:
    """Members like ``Motor.Speed : Real`` flatten a UDT into dotted leaves (forbidden shape)."""
    flattened: List[str] = []
    for member in task.interface.members or []:
        text = (member or "").strip()
        if ":" not in text:
            continue
        name = text.split(":", 1)[0].strip()
        if "." in name:
            flattened.append(member)
    return flattened


def validate_plan(plan: ProjectPlan) -> List[str]:
    """Annotate every task with advisory warnings and return them as ``block: message``.

    Idempotent: existing warnings are cleared first, so a re-run after an interface edit
    replaces the previous findings instead of stacking duplicates.
    """
    if plan is None:
        return []

    known_types: Set[str] = {
        t.block_name for t in plan.tasks
        if (t.kind or "").strip().upper() == TaskKind.UDT.value and t.block_name
    }
    known_dbs: Set[str] = {
        t.block_name for t in plan.tasks
        if (t.kind or "").strip().upper() == TaskKind.DB.value and t.block_name
    }
    known_ids: Set[str] = {t.id for t in plan.tasks if t.id}

    findings: List[str] = []
    for task in plan.tasks:
        task.warnings = []
        kind = (task.kind or "").strip().upper()
        iface = task.interface

        if kind == TaskKind.UDT.value and not iface.members:
            _warn(task, "UDT has no members; list its STRUCT fields in interface.members.")
        if kind == TaskKind.DB.value and not iface.members:
            _warn(task, "DB has no members; declare its stored variables in interface.members.")
        if kind in _IO_KINDS and not iface.inputs and not iface.outputs:
            _warn(task, f"{kind} has no inputs or outputs; give it at least one.")

        for name in iface.udt_refs or []:
            if name and name not in known_types:
                _warn(task, f"udt_refs '{name}' is not defined by this plan; verify it exists in the project.")
        for name in iface.db_refs or []:
            if name and name not in known_dbs:
                _warn(task, f"db_refs '{name}' is not defined by this plan; verify it exists in the project.")

        flattened = _flattened_members(task)
        if flattened:
            _warn(task, "members flatten a UDT into dotted fields ("
                       + ", ".join(flattened[:3])
                       + "); declare a member typed by the UDT instead.")

        for dep in task.depends_on or []:
            if dep and dep not in known_ids:
                _warn(task, f"depends_on '{dep}' is not a task in this plan.")

        label = task.block_name or task.id or "<unnamed>"
        findings.extend(f"{label}: {w}" for w in task.warnings)

    if findings:
        logger.info(f"[plan_checks] {len(findings)} advisory warning(s) across {len(plan.tasks)} task(s)")
    return findings
