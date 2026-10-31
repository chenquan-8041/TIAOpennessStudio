"""Planner node: decomposes "write a whole PLC project" into a ProjectPlan (task 3).

Two-stage planning:
  Stage 1: one LLM call produces the project skeleton (summary + phases + tasks list, with block kinds and dependencies).
  Stage 2: refine detailed interface contracts (inputs/outputs/db_refs/udt_refs) per phase in batches (8 blocks each).
Finally topologically sort (UDT → DB → FC/FB → OB, callee before caller) to produce a READY ProjectPlan.

fail-closed: returns None when the LLM or JSON parsing fails; the caller falls back to single-block generation.
"""

from __future__ import annotations

import json
import logging
import uuid
from collections import deque
from typing import Any, Dict, List, Optional

from langchain_core.messages import HumanMessage, SystemMessage

from agent.plan_models import (
    Phase,
    PlanStatus,
    ProjectPlan,
    Task,
    TaskAction,
    TaskGranularity,
    TaskStatus,
)

logger = logging.getLogger("backend")

# Blocks refined per batch in stage 2 (prevents oversized single-LLM context and quality degradation).
_BATCH_SIZE = 8

# Block kind priority (stable topological-sort fallback: used when dependencies are missing, ensuring UDT/DB come before FC/FB).
_KIND_RANK = {"UDT": 0, "DB": 1, "FB": 2, "LAD_FB": 2, "GRAPH_FB": 2, "FC": 3, "LAD_FC": 3, "OB": 4, "TagTable": 5}


# ── Prompt ─────────────────────────────────────────────────────

_PLANNER_SYSTEM = (
    "You are a TIA Portal (Siemens PLC) project planner. Given a user request to "
    "generate a whole PLC project offline, you decompose it into an execution plan "
    "of blocks organized as a dependency DAG. Output ONLY valid JSON, no markdown "
    "fences, no prose. "
    "The TIA version is part of the environment: V15 and V15.1 are different releases "
    "and the supported baseline starts at V15.1. If a task depends on an Openness API "
    "capability, do not guess it — the version-specific facts come from "
    "query_openness_api(query, version) using the live version from get_environment_profile; "
    "V18–V21 have no bundled docs, so probe_openness_capabilities is the source there."
)

_SKELETON_PROMPT = """## User Request
{user_prompt}

## Project Context
{project_context}

## Existing Project Knowledge (advisory; verify before using)
{planning_knowledge}

## Task
Produce the project skeleton as JSON with this exact schema:

{{
  "summary": "one-line project summary",
  "phases": [
    {{ "id": "p1", "name": "short phase name", "is_milestone": true, "tasks": ["t1", "t2"] }}
  ],
  "tasks": [
    {{
      "id": "t1",
      "kind": "UDT | DB | FC | FB | LAD_FC | LAD_FB | GRAPH_FB | OB | TagTable",
      "block_name": "BlockName",
      "block_number": "FC123 or empty",
      "group_path": "Station/Logic",
      "type_group_path": "Station",
      "action": "create",
      "granularity": "block",
      "depends_on": ["t0"]
    }}
  ]
}}

## Rules
- kind order: UDT → DB → FB/LAD_FB/GRAPH_FB → FC/LAD_FC → OB → TagTable.
- Write paths: UDT tasks set "type_group_path" (e.g. "Station", relative to the Types root) and
  leave "group_path" empty; every other kind sets "group_path" (e.g. "Station/Data",
  "Station/Logic", "Station/Main", relative to the Blocks root) and leaves "type_group_path"
  empty. Never prefix a path with "Blocks/" or "Types/" — those are the roots, not groups.
  Paths are relative, at most 3 segments, no leading slash, no "<>:\"|?*" characters.
  When the request names an area (工位/Station/line), reuse that same area prefix for all
  tasks so the project tree stays coherent; verify existing group paths with
  get_program_tree / list_block_groups when extending a project.
- Use kind "LAD_FC"/"LAD_FB" only when the user explicitly asks for LAD (ladder/梯形图) logic;
  use "GRAPH_FB" only when the user explicitly asks for S7-GRAPH / 顺序控制 / 步序图 logic;
  otherwise use "FC"/"FB" (SCL).
- depends_on MUST reference earlier task ids so the DAG respects: types before
  consumers, shared DB before FC/FB, callee before caller, OB last.
- depends_on also expresses the call graph: if block B calls/uses block A (or A is
  a sub-function B relies on), put A's task id in B.depends_on. Every FC/FB/LAD_FC/
  LAD_FB/GRAPH_FB should be called by at least one other block; the OB that executes
  them must list them in depends_on.
- Global vs local operands is a per-layer choice: encapsulated/reusable blocks keep a local
  interface, while blocks that drive machine I/O directly may bind existing project globals
  (PLC tags or DB members). Name those globals in the refinement step when they apply; never
  invent symbols.
- phases group related tasks and mark milestone boundaries with is_milestone=true.
- Only output the JSON object."""

_REFINE_PROMPT = """## User Request
{user_prompt}

## Existing Project Knowledge (advisory; use it to align names)
{planning_knowledge}

## Blocks to refine (id already assigned)
{blocks_json}

## Task
For each block above, output its detailed interface contract as JSON with schema:

{{
  "interfaces": [
    {{
      "id": "t1",
      "inputs": ["input names/types"],
      "outputs": ["output names/types"],
      "db_refs": ["referenced DB names"],
      "udt_refs": ["referenced UDT names"],
      "tags": ["global operands read/written, e.g. s1, h1, DB_Alarm"],
      "members": ["FIELD_NAME : TYPE", "..."],
    }}
  ]
}}

- For UDT blocks, list their STRUCT fields in "members" (e.g. "Speed : Real", "Enable : Bool").
  "members" MUST be non-empty.
- For global DB blocks, list their stored variables in "members" as typed declarations.
  When the DB instantiates a UDT, declare a member typed by that UDT name (e.g.
  "Station : ZZ_TYPE_StationData", "Motors : Array[0..3] of ZZ_TYPE_MotorParam").
  NEVER flatten a UDT's fields into dotted member names such as
  "Motor.RatedCurrent : Real". "members" MUST be non-empty.
- For FC/FB/LAD_FC/LAD_FB/GRAPH_FB/OB blocks, leave "members" empty and use
  "inputs"/"outputs" instead. Every FC/FB/LAD/GRAPH block MUST have at least one input,
  one output, or a non-empty "tags" list (a block that only drives global operands needs
  no local interface); only OB blocks may leave all of them empty.
- "tags" lists the PROJECT global operands the block reads or writes: PLC tag names
  (from the tag tables) or DB member paths (e.g. "s1", "h1", "DB_StationData.Alarm").
  Name them when the block drives machine I/O directly; leave "tags" empty for
  encapsulated/reusable blocks whose caller supplies the operands, and for pure
  UDT/data DBs. Use ONLY names that exist in the project knowledge above; never
  invent tag or member names.
- For GRAPH_FB blocks, put every operand used by step actions and transition conditions
  in "tags": the sequence needs real conditions and actions, so the coder must know them.
- Align "db_refs" / "udt_refs" with the ACTUAL project names in the project knowledge
  above; do NOT invent UDT/DB names.
- "db_refs" MUST contain only global DB blocks (kind DB). Never put a TagTable or a
  UDT in "db_refs" — code references tag tables by tag name, not as a DB.
- Return exactly one "interfaces" entry per block id listed above; never omit a block.
Only include blocks whose interface is known at this stage; leave lists empty when
nothing applies. Output ONLY valid JSON, no markdown fences, no prose."""


# ── Internal helpers ─────────────────────────────────────────

def _parse_json(text: str) -> Optional[Dict[str, Any]]:
    """Extract a JSON object from LLM output: parse the whole text first, then fall back to the first/last brace substring."""
    if not text:
        return None
    text = text.strip()
    try:
        obj = json.loads(text)
        return obj if isinstance(obj, dict) else None
    except (json.JSONDecodeError, TypeError, ValueError):
        pass
    start = text.find("{")
    end = text.rfind("}")
    if 0 <= start < end:
        try:
            obj = json.loads(text[start:end + 1])
            return obj if isinstance(obj, dict) else None
        except (json.JSONDecodeError, TypeError, ValueError):
            return None
    return None


async def _call_llm(llm, system: str, user: str) -> str:
    """Call the injected LLM and return text content; returns an empty string on error."""
    try:
        resp = await llm.ainvoke(
            [SystemMessage(content=system), HumanMessage(content=user)]
        )
        content = getattr(resp, "content", None)
        if isinstance(content, list):
            content = "".join(str(part) for part in content)
        return (content or "").strip()
    except Exception as exc:  # noqa: BLE001
        logger.warning(f"[planner] LLM call failed: {type(exc).__name__}: {exc}")
        return ""


async def _gather_context_with_tools(llm, user_prompt: str, project_context: str, tools) -> str:
    """Task 3: Planner binds a toolset and retrieves context before planning (project search / standard library / current project).

    Runs one retrieval-style ReAct agent call so the Planner can look up real project facts and coding standards,
    returning a context summary to inject into the planning prompt. fail-closed: returns empty on error, without blocking planning.
    """
    try:
        from langchain.agents import create_agent
        from agent.middleware_config import build_subagent_middleware

        agent = create_agent(
            llm,
            tools=list(tools or []),
            system_prompt=(
                "You are a PLC project context collector. Use the provided tools to "
                "retrieve relevant project facts (existing blocks, variables, types) and "
                "coding-standard conventions (list_standard_topics / read_standard_section). "
                "If the request depends on an Openness API signature, call "
                "query_openness_api(query, version) with the live version from "
                "get_environment_profile (V15 and V15.1 are different; V15 is unsupported, "
                "baseline is V15.1; V18–V21 have no bundled docs — use "
                "probe_openness_capabilities). "
                "Do NOT produce a plan; reply with a concise structured summary of what you found."
            ),
            middleware=build_subagent_middleware(llm),
        )
        user = (
            f"Requirement: {user_prompt}\n\n"
            f"Project context already at hand: {project_context or '(empty)'}\n\n"
            "Gather additional relevant context needed to decompose this into a PLC project plan."
        )
        resp = await agent.ainvoke({"messages": [{"role": "user", "content": user}]})
        msgs = resp.get("messages", []) if isinstance(resp, dict) else []
        last = msgs[-1] if msgs else None
        content = getattr(last, "content", "") if last else ""
        if isinstance(content, list):
            content = "".join(str(part) for part in content)
        return (content or "").strip()
    except Exception as exc:  # noqa: BLE001
        logger.warning(f"[planner] tool-based context gathering failed: {type(exc).__name__}: {exc}")
        return ""


def _skeleton_to_plan(skeleton: Dict[str, Any], project_key: str) -> ProjectPlan:
    """Convert the stage-1 skeleton JSON into a ProjectPlan (structure only; interfaces left empty pending refinement)."""
    plan = ProjectPlan(
        id=uuid.uuid4().hex[:12],
        project_key=project_key,
        summary=str(skeleton.get("summary") or ""),
        status=PlanStatus.PLANNING.value,
    )
    for p in skeleton.get("phases") or []:
        if not isinstance(p, dict):
            continue
        plan.phases.append(Phase.from_dict(p))
    for t in skeleton.get("tasks") or []:
        if not isinstance(t, dict):
            continue
        plan.tasks.append(_task_from_skeleton(t))
    return plan


_PATH_MAX_DEPTH = 3
_PATH_BAD_CHARS = set('<>:"|?*')
_PATH_BAD_SEGMENTS = {".", ".."}


def _normalize_group_path(value: Any, default: Optional[str] = None) -> Optional[str]:
    """Normalise a planner-supplied group path; None when absent or invalid.

    Paths are relative to the tree root the write targets (Blocks for blocks, Types for UDTs), so
    a leading "Blocks"/"Types" segment is stripped: the build tools would otherwise create a literal
    group with that name and nest the real path under it.

    Keeps the path relative and shallow (TIA group trees are navigated segment by segment), and
    rejects characters that would break the Openness group lookup.
    """
    if not isinstance(value, str):
        return default
    text = value.strip().replace("\\", "/").strip("/")
    if not text:
        return default
    segments = [s.strip() for s in text.split("/") if s.strip()]
    if segments and segments[0].lower() in ("blocks", "types"):
        segments = segments[1:]
    if not segments or len(segments) > _PATH_MAX_DEPTH:
        return default
    if any(s in _PATH_BAD_SEGMENTS or any(c in _PATH_BAD_CHARS for c in s) for s in segments):
        return default
    return "/".join(segments)


def _task_from_skeleton(t: Dict[str, Any]) -> Task:
    """Stage-1 task dict → Task object."""
    kind = str(t.get("kind") or "")
    raw_block_path = t.get("group_path")
    raw_type_path = t.get("type_group_path")
    block_path = _normalize_group_path(raw_block_path)
    type_path = _normalize_group_path(raw_type_path)
    # UDT writes into the type tree; everything else into the block tree. Never carry both.
    if kind == "UDT":
        block_path = None
    else:
        type_path = None

    task = Task(
        id=str(t.get("id") or ""),
        kind=kind,
        block_name=str(t.get("block_name") or ""),
        block_number=t.get("block_number"),
        group_path=block_path,
        type_group_path=type_path,
        action=str(t.get("action") or TaskAction.CREATE.value),
        granularity=str(t.get("granularity") or TaskGranularity.BLOCK.value),
        target_block=t.get("target_block"),
        depends_on=[str(x) for x in t.get("depends_on") or []],
    )
    if raw_block_path and not block_path and kind != "UDT":
        task.warnings.append(f"Ignored invalid group_path: {raw_block_path}")
    if raw_type_path and not type_path and kind == "UDT":
        task.warnings.append(f"Ignored invalid type_group_path: {raw_type_path}")
    return task


def _apply_refined_interfaces(plan: ProjectPlan, refined: Dict[str, Any]) -> None:
    """Write the stage-2 refinement results back into each task's interface in the plan."""
    from agent.plan_models import BlockInterface
    items = refined.get("interfaces") or []
    for item in items:
        if not isinstance(item, dict):
            continue
        t = plan.task_by_id(str(item.get("id") or ""))
        if t is None:
            continue
        t.interface = BlockInterface.from_dict(item)


def _toposort(tasks: List[Task]) -> List[Task]:
    """Topologically sort by depends_on (Kahn). Cycles/isolated nodes keep their original relative order and warn."""
    by_id: Dict[str, Task] = {t.id: t for t in tasks if t.id}
    in_degree: Dict[str, int] = {t.id: 0 for t in tasks if t.id}
    dependents: Dict[str, List[str]] = {t.id: [] for t in tasks if t.id}

    for t in tasks:
        if not t.id:
            continue
        for dep in t.depends_on:
            if dep in by_id and dep != t.id:
                in_degree[t.id] += 1
                dependents[dep].append(t.id)

    ready = deque([tid for tid, deg in in_degree.items() if deg == 0])
    ordered: List[str] = []
    while ready:
        cur = ready.popleft()
        ordered.append(cur)
        for nxt in dependents[cur]:
            in_degree[nxt] -= 1
            if in_degree[nxt] == 0:
                ready.append(nxt)

    # On cycles, append remaining nodes by kind priority as a fallback to avoid losing blocks.
    remaining = [t for t in tasks if t.id and t.id not in set(ordered)]
    if remaining:
        logger.warning(
            f"[planner] dependency cycle/invalid refs detected; "
            f"falling back on kind order for {len(remaining)} task(s)"
        )
        remaining.sort(key=lambda t: _KIND_RANK.get(t.kind, 99))
        ordered.extend(t.id for t in remaining)

    # Tasks without an id stay in place, appended at the end.
    result = [by_id[tid] for tid in ordered if tid in by_id]
    result.extend(t for t in tasks if not t.id)
    return result


# ── Top-level entry points ───────────────────────────────────

async def create_project_plan(
    llm,
    user_prompt: str,
    project_context: str = "",
    project_key: str = "",
    planning_knowledge: str = "",
    tools=None,
    environment: Optional[Dict[str, Any]] = None,
) -> Optional[ProjectPlan]:
    """Two-stage planning entry point. Returns the READY plan on success, None on failure (caller falls back).

    tools: task 3 — the toolset bound to the Planner (search_project/build_context_pack + standard library +
    get_current_project meta-tools). When provided, run one round of tool retrieval first and inject the retrieved context into planning.
    """
    # pre-stage-1: when tools are bound, first retrieve real project facts and coding standards as grounded decomposition input.
    gathered = ""
    if tools:
        logger.info(f"[planner] node=context_gather start tools={len(tools)}")
        gathered = await _gather_context_with_tools(llm, user_prompt, project_context or "", tools)
        logger.info(f"[planner] node=context_gather done chars={len(gathered)}")
    knowledge = "\n\n".join(x for x in (planning_knowledge, gathered) if x and x.strip())

    # stage 1: skeleton (retry once: the gateway occasionally returns an empty 200 body)
    skeleton = None
    skeleton_raw = ""
    for attempt in range(1, 3):
        logger.info(f"[planner] node=skeleton attempt {attempt}/2")
        skeleton_raw = await _call_llm(
            llm, _PLANNER_SYSTEM,
            _SKELETON_PROMPT.format(
                user_prompt=user_prompt,
                project_context=project_context or "(empty)",
                planning_knowledge=knowledge or "(none)",
            ),
        )
        skeleton = _parse_json(skeleton_raw)
        if skeleton and skeleton.get("tasks"):
            break
        logger.warning(
            f"[planner] skeleton parse failed (attempt {attempt}/2, len={len(skeleton_raw)}): "
            f"{skeleton_raw[:240]!r}"
        )
        skeleton = None
    if skeleton is None:
        logger.warning("[planner] skeleton parse failed or empty; aborting plan")
        return None
    logger.info(
        f"[planner] node=skeleton done tasks={len(skeleton.get('tasks') or [])} "
        f"phases={len(skeleton.get('phases') or [])}"
    )

    plan = _skeleton_to_plan(skeleton, project_key)
    # Bind the live environment facts the plan is built for. Execution re-reads them and
    # refuses to run when the CPU family / order number / TIA version changed since.
    plan.environment = dict(environment or {})

    # stage 2: refine interface contracts in batches
    tasks = plan.tasks
    batch_total = (len(tasks) + _BATCH_SIZE - 1) // _BATCH_SIZE
    for i in range(0, len(tasks), _BATCH_SIZE):
        batch = tasks[i:i + _BATCH_SIZE]
        batch_no = i // _BATCH_SIZE + 1
        blocks_json = json.dumps([t.to_dict() for t in batch], ensure_ascii=False)
        logger.info(
            f"[planner] node=refine batch {batch_no}/{batch_total} "
            f"ids={batch[0].id}..{batch[-1].id} blocks={len(batch)}"
        )
        refined = None
        for attempt in range(1, 3):
            refine_raw = await _call_llm(
                llm, _PLANNER_SYSTEM,
                _REFINE_PROMPT.format(
                    user_prompt=user_prompt,
                    planning_knowledge=knowledge or "(none)",
                    blocks_json=blocks_json,
                ),
            )
            refined = _parse_json(refine_raw)
            if refined is not None:
                break
            logger.warning(
                f"[planner] refine parse failed for batch {batch[0].id}..{batch[-1].id} "
                f"(attempt {attempt}/2, len={len(refine_raw)})"
            )
        applied = 0
        if refined is not None:
            _apply_refined_interfaces(plan, refined)
            applied = len(refined.get("interfaces") or [])
        logger.info(f"[planner] node=refine batch {batch_no}/{batch_total} done interfaces={applied}")

    plan.tasks = _toposort(plan.tasks)
    # Advisory only: annotate incomplete/unresolved interfaces on the tasks. Never rejects the
    # plan; the warnings travel with the plan into the executor and the coder prompt.
    from agent.plan_checks import validate_plan
    validate_plan(plan)
    plan.status = PlanStatus.READY.value
    plan.touch()
    logger.info(f"[planner] node=toposort done tasks={len(plan.tasks)} status={plan.status}")
    return plan


# ── adjust: replan the unfinished part ─────────────────────────

def _merge_replanned(plan: ProjectPlan, new_plan: ProjectPlan) -> None:
    """Merge the replanned result new_plan into plan: keep success blocks and approved phases, replace unfinished blocks.

    - success tasks are kept as-is (idempotent; resume does not redo them).
    - tasks in new_plan named after a completed block are not regenerated; their ids map to the completed block id.
    - other new tasks are renumbered (to avoid id collisions with kept blocks) and reset to pending.
    - dependency remap: those pointing at completed block names → kept block ids; others keep their new-plan ids.
    - after merging, topologically sort and rebuild phases (kept approved phases + one phase carrying unfinished blocks).
    """
    success = [t for t in plan.tasks if t.status == TaskStatus.SUCCESS.value]
    name_to_success_id: Dict[str, str] = {}
    for t in success:
        if t.block_name:
            name_to_success_id.setdefault(t.block_name.lower(), t.id)

    id_map: Dict[str, str] = {}
    kept_ids = {t.id for t in success}
    merged = list(success)

    for nt in new_plan.tasks:
        orig_id = nt.id
        key = (nt.block_name or "").lower()
        if key and key in name_to_success_id:
            # completed block: map to the kept block and skip generation
            id_map[orig_id] = name_to_success_id[key]
            continue
        # new task: ensure a unique id
        new_id = orig_id
        while new_id in kept_ids:
            new_id = f"{orig_id}-{uuid.uuid4().hex[:4]}"
        nt.id = new_id
        nt.status = TaskStatus.PENDING.value
        nt.error = None
        kept_ids.add(new_id)
        id_map[orig_id] = new_id
        merged.append(nt)

    # dependency remap + prune dangling dependencies
    id_of = {t.id for t in merged}
    for t in merged:
        remapped = [_d for _d in (id_map.get(d, d) for d in t.depends_on)
                    if _d in id_of and _d != t.id]
        t.depends_on = remapped

    plan.tasks = _toposort(merged)

    # rebuild phases
    approved_phase_ids = {m.phase_id for m in plan.milestone_decisions if m.decision == "approve"}
    kept_phases = [p for p in plan.phases if p.id in approved_phase_ids]
    new_task_ids = [t.id for t in plan.tasks if t.status == TaskStatus.PENDING.value]
    if new_task_ids:
        kept_phases.append(Phase(
            id=f"ph-{uuid.uuid4().hex[:6]}",
            name="重规划后续",
            is_milestone=True,
            tasks=new_task_ids,
        ))
    plan.phases = kept_phases
    plan.status = PlanStatus.READY.value
    plan.touch()


async def replan_unfinished(
    llm,
    plan: ProjectPlan,
    user_prompt: str,
    project_context: str = "",
    planning_knowledge: str = "",
    tools=None,
    environment: Optional[Dict[str, Any]] = None,
) -> ProjectPlan:
    """adjust semantics: replan the unfinished part while keeping success blocks (idempotent).

    Re-call the two-stage Planner with "original request + completed block list + adjustment note" to produce a new DAG, then merge it
    back into the plan. Triggered only when the latest milestone decision is adjust; on replan failure (LLM/JSON error) return the plan
    unchanged and let the caller fall back to the normal "reset unfinished to pending and re-run". No-op for approve.
    """
    latest_adjust = None
    for m in plan.milestone_decisions:
        if m.decision == "adjust":
            latest_adjust = m  # iterate to the end to get the "latest adjust"
    if latest_adjust is None:
        return plan

    note = latest_adjust.note or ""
    adjusted_prompt = user_prompt
    if note:
        adjusted_prompt = f"{user_prompt}\n\n## Adjustment Request\n{note}"

    done = [t for t in plan.tasks if t.status == TaskStatus.SUCCESS.value]
    ctx = project_context or ""
    if done:
        done_desc = "; ".join(f"{t.kind}:{t.block_name}" for t in done)
        ctx = f"{ctx}\n## Completed blocks (keep them, do not regenerate; later blocks may reference their interfaces)\n{done_desc}"

    new_plan = await create_project_plan(
        llm,
        user_prompt=adjusted_prompt,
        project_context=ctx,
        project_key=plan.project_key,
        planning_knowledge=planning_knowledge or "",
        tools=tools,
        environment=environment,
    )
    if new_plan is None or not new_plan.tasks:
        logger.warning("[planner] replan failed; keep original plan (fallback to pending re-run)")
        return plan

    # The plan keeps the environment it was created for; only fill it when the original
    # plan has none (e.g. a plan persisted before environment facts existed).
    if not plan.environment:
        plan.environment = dict(new_plan.environment or {})
    _merge_replanned(plan, new_plan)
    # Interfaces may have changed during the merge: re-annotate the advisory warnings.
    from agent.plan_checks import validate_plan
    validate_plan(plan)
    logger.info(f"[planner] replan merged {len(plan.tasks)} tasks (kept {len(done)} success)")
    return plan