"""ProjectPlan long-running plan data model and serialization.

Corresponds to section 4.1 of the "cloud-side Planner long-task orchestration" design doc. Defines the unified
JSON schema shared by Planner output, Executor consumption, and plan_store persistence. Field names align strictly
with the 4.1 ProjectPlan schema, and the serialized form is the intermediate representation for persistence and
LLM structured output.
"""

from __future__ import annotations

import datetime
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, List, Optional


def _now_iso() -> str:
    """ISO string of the current time, used as the default for created_at / updated_at."""
    return datetime.datetime.now().isoformat(timespec="seconds")


# ── Enums (string enums; serialization takes .value directly, avoiding extra conversion) ──


class TaskKind(str, Enum):
    """Block kind."""
    UDT = "UDT"
    DB = "DB"
    FC = "FC"
    FB = "FB"
    OB = "OB"
    TAG_TABLE = "TagTable"


class TaskAction(str, Enum):
    """Task action: create/rebuild or incremental modify."""
    CREATE = "create"
    MODIFY = "modify"


class TaskGranularity(str, Enum):
    """Edit granularity: whole block or in-block network."""
    BLOCK = "block"
    NETWORK = "network"


class TaskStatus(str, Enum):
    """Task execution status."""
    PENDING = "pending"
    IN_PROGRESS = "in_progress"
    SUCCESS = "success"
    FAILED = "failed"
    SKIPPED = "skipped"
    STALE = "stale"


class PlanStatus(str, Enum):
    """Lifecycle status of the whole plan."""
    PLANNING = "planning"
    READY = "ready"
    RUNNING = "running"
    PAUSED = "paused"
    DONE = "done"
    REJECTED = "rejected"


# valid milestone decision values
VALID_MILESTONE_DECISIONS = frozenset({"approve", "adjust", "reject"})


# ── Data structures ─────────────────────────────────────────────


@dataclass
class BlockInterface:
    """Block interface contract: inputs / outputs / DB refs / UDT refs / global tags / members.

    ``tags`` lists the PROJECT global symbols the block reads/writes (PLC tags or DB members).
    A block that only uses local interface variables cannot drive real I/O, so the planner must
    name the global operands it expects and the coder must wire exactly those.
    """
    inputs: List[str] = field(default_factory=list)
    outputs: List[str] = field(default_factory=list)
    db_refs: List[str] = field(default_factory=list)
    udt_refs: List[str] = field(default_factory=list)
    tags: List[str] = field(default_factory=list)   # global operands, e.g. "s1", "h1", "DB.Alarm"
    members: List[str] = field(default_factory=list)  # UDT/DB fields, e.g. "Speed : Real"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "inputs": list(self.inputs),
            "outputs": list(self.outputs),
            "db_refs": list(self.db_refs),
            "udt_refs": list(self.udt_refs),
            "tags": list(self.tags),
            "members": list(self.members),
        }

    @classmethod
    def from_dict(cls, data: Any) -> "BlockInterface":
        if not isinstance(data, dict):
            return cls()
        return cls(
            inputs=[str(x) for x in data.get("inputs") or []],
            outputs=[str(x) for x in data.get("outputs") or []],
            db_refs=[str(x) for x in data.get("db_refs") or []],
            udt_refs=[str(x) for x in data.get("udt_refs") or []],
            tags=[str(x) for x in data.get("tags") or []],
            members=[str(x) for x in data.get("members") or []],
        )


@dataclass
class Task:
    """Execution unit: a generate/modify task for one block (or several networks within a block)."""
    id: str = ""
    kind: str = ""                              # TaskKind value
    block_name: str = ""                        # block name (e.g. "MotorCtrl")
    block_number: Optional[str] = None          # block number (e.g. "FC123")
    # Write target. group_path/type_group_path are RELATIVE to their tree root (block groups under
    # Blocks, type groups under Types); both are at most 3 segments. None/empty = project root.
    group_path: Optional[str] = None
    type_group_path: Optional[str] = None
    action: str = TaskAction.CREATE.value       # create | modify
    granularity: str = TaskGranularity.BLOCK.value  # block | network
    target_block: Optional[str] = None          # network-level: target block id
    network_range: Optional[List[int]] = None   # network-level: [start, count]
    depends_on: List[str] = field(default_factory=list)  # topological dependencies (task ids)
    interface: BlockInterface = field(default_factory=BlockInterface)
    status: str = TaskStatus.PENDING.value
    artifact: Optional[str] = None              # artifact reference (XML/SCL path or content)
    error: Optional[str] = None                 # reason of the most recent failure
    # Advisory contract findings (empty members, unresolved refs, FC/FB without IO). They never
    # block execution; they are shown in plan progress and in the coder's task prompt.
    warnings: List[str] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "kind": self.kind,
            "block_name": self.block_name,
            "block_number": self.block_number,
            "group_path": self.group_path,
            "type_group_path": self.type_group_path,
            "action": self.action,
            "granularity": self.granularity,
            "target_block": self.target_block,
            "network_range": list(self.network_range) if self.network_range else None,
            "depends_on": list(self.depends_on),
            "interface": self.interface.to_dict(),
            "status": self.status,
            "artifact": self.artifact,
            "error": self.error,
            "warnings": list(self.warnings),
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Task":
        if not isinstance(data, dict):
            data = {}
        net = data.get("network_range")
        return cls(
            id=str(data.get("id") or ""),
            kind=str(data.get("kind") or ""),
            block_name=str(data.get("block_name") or ""),
            block_number=data.get("block_number"),
            group_path=data.get("group_path") or None,
            type_group_path=data.get("type_group_path") or None,
            action=str(data.get("action") or TaskAction.CREATE.value),
            granularity=str(data.get("granularity") or TaskGranularity.BLOCK.value),
            target_block=data.get("target_block"),
            network_range=[int(x) for x in net] if isinstance(net, list) else None,
            depends_on=[str(x) for x in data.get("depends_on") or []],
            interface=BlockInterface.from_dict(data.get("interface")),
            status=str(data.get("status") or TaskStatus.PENDING.value),
            artifact=data.get("artifact"),
            error=data.get("error"),
            warnings=[str(x) for x in data.get("warnings") or []],
        )


@dataclass
class Phase:
    """Phase (also a milestone grouping)."""
    id: str = ""
    name: str = ""
    is_milestone: bool = False
    tasks: List[str] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "name": self.name,
            "is_milestone": self.is_milestone,
            "tasks": list(self.tasks),
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Phase":
        if not isinstance(data, dict):
            data = {}
        return cls(
            id=str(data.get("id") or ""),
            name=str(data.get("name") or ""),
            is_milestone=bool(data.get("is_milestone")),
            tasks=[str(x) for x in data.get("tasks") or []],
        )


@dataclass
class MilestoneDecision:
    """A single milestone approval record."""
    phase_id: str = ""
    decision: str = ""          # approve | adjust | reject
    note: Optional[str] = None  # optional adjustment note supplied by the user on adjust
    at: Optional[str] = None    # decision time (ISO)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "phase_id": self.phase_id,
            "decision": self.decision,
            "note": self.note,
            "at": self.at,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "MilestoneDecision":
        if not isinstance(data, dict):
            data = {}
        return cls(
            phase_id=str(data.get("phase_id") or ""),
            decision=str(data.get("decision") or ""),
            note=data.get("note"),
            at=data.get("at"),
        )


# ── ProjectPlan (top level) ───────────────────────────────────


@dataclass
class ProjectPlan:
    """A long-running task plan: produced by Planner, consumed by Executor, persisted by plan_store."""
    id: str = ""                              # plan id (short random string)
    project_key: str = ""                     # associated TIA project identifier
    summary: str = ""                         # one-line project description
    status: str = PlanStatus.PLANNING.value   # PlanStatus value
    created_at: str = field(default_factory=_now_iso)
    updated_at: str = field(default_factory=_now_iso)
    phases: List[Phase] = field(default_factory=list)
    tasks: List[Task] = field(default_factory=list)
    milestone_decisions: List[MilestoneDecision] = field(default_factory=list)
    # Live environment facts the plan was built for (tia_version / plc_family /
    # order_number / project_key / fingerprint). Execution compares these with a fresh
    # read: CPU/version changes block, a different project only warns.
    environment: Dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "project_key": self.project_key,
            "summary": self.summary,
            "status": self.status,
            "created_at": self.created_at,
            "updated_at": self.updated_at,
            "phases": [p.to_dict() for p in self.phases],
            "tasks": [t.to_dict() for t in self.tasks],
            "milestone_decisions": [m.to_dict() for m in self.milestone_decisions],
            "environment": dict(self.environment),
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "ProjectPlan":
        if not isinstance(data, dict):
            data = {}
        env = data.get("environment")
        return cls(
            id=str(data.get("id") or ""),
            project_key=str(data.get("project_key") or ""),
            summary=str(data.get("summary") or ""),
            status=str(data.get("status") or PlanStatus.PLANNING.value),
            created_at=str(data.get("created_at") or _now_iso()),
            updated_at=str(data.get("updated_at") or _now_iso()),
            phases=[Phase.from_dict(p) for p in data.get("phases") or []],
            tasks=[Task.from_dict(t) for t in data.get("tasks") or []],
            milestone_decisions=[
                MilestoneDecision.from_dict(m)
                for m in data.get("milestone_decisions") or []
            ],
            environment=dict(env) if isinstance(env, dict) else {},
        )

    # ── Basic helpers (reused by Executor / plan_store) ──────────

    def touch(self) -> None:
        """Update the updated_at timestamp."""
        self.updated_at = _now_iso()

    def task_by_id(self, task_id: str) -> Optional[Task]:
        """Find a task by id; returns None if not found."""
        for t in self.tasks:
            if t.id == task_id:
                return t
        return None

    def set_task_status(self, task_id: str, status: str, error: Optional[str] = None) -> bool:
        """Update a single task's status; returns True on success, False if the task does not exist."""
        t = self.task_by_id(task_id)
        if t is None:
            return False
        t.status = status
        if error is not None:
            t.error = error
        self.touch()
        return True