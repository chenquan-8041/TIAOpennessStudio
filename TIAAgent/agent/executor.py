"""Executor: consumes the ProjectPlan DAG in the background, reusing the supervisor single-block flow for each task (task 5).

Job model: this function runs as a background async job, persisting each task's status in real time, pushing progress via
the `plan_progress` event; resume relies on plan_store (skipping already-success tasks).

Reuse strategy: each task calls the supervisor's `_build_supervisor_graph` once to run the coder→reviewer→compiler
loop with a single-block GenerateCode intent, inheriting tool assembly (state.toolsets), compile repair experience
(query_repair_experience), and WriteGate write approval.

Event protocol (reused):
  ("thinking", msg) / ("notice", payload) / ("plan_progress", json) / ("done", "ok") / ("error", msg)
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
from typing import Any, AsyncIterator, Dict, List, Optional, Tuple

from langchain_core.messages import HumanMessage

from agent.notices import notice, match_manual_action
from agent.plan_models import (
    BlockInterface,
    Phase,
    PlanStatus,
    ProjectPlan,
    Task,
    TaskGranularity,
    TaskStatus,
)

logger = logging.getLogger("backend")

# Task-level token budget (in+out accumulated); exceeding it downgrades to failure (4.12 "over task budget → task failure downgrade").
_TASK_TOKEN_BUDGET = int(os.environ.get("TIA_TASK_TOKEN_BUDGET", "200000"))

# LAD (ladder) block kinds: composed natively via build_lad_fc/build_lad_fb by the coder (LLM),
# not SCL — so they skip the SCL reviewer/compiler and are judged by the write confirmation.
_LAD_KINDS = frozenset({"LAD_FC", "LAD_FB"})

# S7-GRAPH block kind: composed natively via build_graph_fb (step sequence), not SCL.
# Like LAD it skips the SCL reviewer/compiler and is judged by the write + compile confirmation.
_GRAPH_KINDS = frozenset({"GRAPH_FB"})

# SCL block kinds: authored as SCL source. The coder generates only (returns the raw SCL block);
# the executor writes it deterministically via import_block — the same generate→review→precheck→write
# shape as the UDT/DB direct-write path, so the SCL reviewer/compiler no longer see a narrative.
_SCL_KINDS = frozenset({"FC", "FB", "OB"})


# ── Pure functions (independently testable) ────────────────────────────────────────

def _task_prompt(task: Task) -> str:
    """Convert a Task into a requirement description for the coder (block-level generation / network-level modify)."""
    # network-level: modify the target block's specified network; read IR, edit, write the whole block back (plan A)
    if task.granularity == TaskGranularity.NETWORK.value:
        start = 1
        count = 1
        if task.network_range and len(task.network_range) >= 2:
            start, count = int(task.network_range[0]), int(task.network_range[1])
        target = task.target_block or task.block_name
        if task.kind in _GRAPH_KINDS:
            return (
                f"Modify network {start}~{start + count - 1} of GRAPH block `{target}`: "
                f"first call get_block_content(block_name=\"{target}\", format=\"ir\") to read the sequence, "
                f"apply the requested change, then write the whole block back with build_graph_fb."
            )
        return (
            f"Modify network {start}~{start + count - 1} of {task.kind} block `{target}`: "
            f"first call get_block_content(block_name=\"{target}\", format=\"ir\", network_start={start}, network_count={count}) "
            f"to read that IR segment and keep its revision, apply the requested change, then call "
            f"patch_block_network(block_name=\"{target}\", base_revision=<revision>, compile_unit_id=<networks[].compileUnitId>, body=<edited body>). "
            f"Only that network changes; never rebuild the whole block unless the user asks for it."
        )
    iface = task.interface
    if task.kind in _LAD_KINDS:
        # LAD blocks are composed natively (ladder logic), not SCL — direct the coder to
        # build_lad_fc / build_lad_fb (see CODER_SYSTEM_PROMPT's LAD section for the JSON spec).
        kind_label = "function block (FB)" if task.kind == "LAD_FB" else "function (FC)"
        lad_parts = [f"Generate a native LAD (ladder) {kind_label} block `{task.block_name}`"]
        if task.block_number:
            lad_parts[0] += f" (block number {task.block_number})"
        if iface.inputs:
            lad_parts.append(f"Inputs: {', '.join(iface.inputs)}")
        if iface.outputs:
            lad_parts.append(f"Outputs: {', '.join(iface.outputs)}")
        if iface.db_refs:
            lad_parts.append(f"Referenced DBs: {', '.join(iface.db_refs)}")
        if getattr(iface, "tags", None):
            lad_parts.append(f"Global tags to drive: {', '.join(iface.tags)}")
        lad_parts.append(
            "Each ladder network must be one FC/FB call wired to tags/constants. When the task lists "
            "`Global tags to drive:`, use exactly those project operands (Scope=GlobalVariable); "
            "local interface variables remain valid for reusable blocks. Write it via "
            f"build_lad_{'fb' if task.kind == 'LAD_FB' else 'fc'}. Do NOT emit SCL."
        )
        if task.group_path:
            lad_parts.append(
                f'Target block group: "{task.group_path}" — pass group_path="{task.group_path}" '
                "in the build call so the block lands in that group."
            )
        return "\n".join(lad_parts)
    if task.kind in _GRAPH_KINDS:
        graph_parts = [f"Generate a native S7-GRAPH FB block `{task.block_name}`"]
        if task.block_number:
            graph_parts[0] += f" (block number {task.block_number})"
        if iface.inputs:
            graph_parts.append(f"Inputs: {', '.join(iface.inputs)}")
        if iface.outputs:
            graph_parts.append(f"Outputs: {', '.join(iface.outputs)}")
        if iface.db_refs:
            graph_parts.append(f"Referenced DBs: {', '.join(iface.db_refs)}")
        if getattr(iface, "tags", None):
            graph_parts.append(f"Global tags to drive: {', '.join(iface.tags)}")
        graph_parts.append(
            "Build it with build_graph_fb: sequence.steps (exactly one init=true), "
            "sequence.transitions, optional branches, and connections between "
            "Step/Transition/Branch/End. Every transition MUST declare a real condition "
            "(Contact/compare wired to real operands) — a missing condition is rejected; the "
            "sequence must contain at least one action (qualifier + real operand) that runs while "
            "its step is active, and every step that drives an output should carry one. A pure "
            "wait step may stay empty. Do NOT emit SCL."
        )
        if task.group_path:
            graph_parts.append(
                f'Target block group: "{task.group_path}" — pass group_path="{task.group_path}" '
                "in the build_graph_fb call so the block lands in that group."
            )
        return "\n".join(graph_parts)
    parts = [f"Generate a {task.kind} block `{task.block_name}`"]
    if task.block_number:
        parts[0] += f" (block number {task.block_number})"
    if iface.inputs:
        parts.append(f"Inputs: {', '.join(iface.inputs)}")
    if iface.outputs:
        parts.append(f"Outputs: {', '.join(iface.outputs)}")
    if iface.db_refs:
        parts.append(f"Referenced DBs: {', '.join(iface.db_refs)}")
    if iface.udt_refs:
        parts.append(f"Referenced UDTs: {', '.join(iface.udt_refs)}")
    if getattr(iface, "tags", None):
        parts.append(f"Global tags to drive: {', '.join(iface.tags)}")
    if task.kind == "OB":
        # Reviewer-enforced rules that the OB envelope cannot fix for the model: an OB has no instance
        # data and its TEMP is undefined at cycle start, so the generated body must set its own values.
        parts.append(
            "OB rules: an OB has NO instance data — use only VAR_TEMP (and VAR CONSTANT), never VAR/STATIC. "
            "TEMP is undefined at cycle start, so assign every temp with a constant before use. Call the FB "
            "through its instance DB (\"<instance_db>\"(...)). When a data DB is referenced, write at least "
            "one meaningful value into it so the block has an observable effect. Put ONLY "
            "ORGANIZATION_BLOCK ... END_ORGANIZATION_BLOCK inside the ```scl fence — no design prose inside it."
        )
    if getattr(iface, "members", None):
        parts.append(f"Members: {', '.join(iface.members)}")
    elif task.kind in ("UDT", "DB"):
        # DB/UDT fields were not planned out. Tell the coder to derive sensible
        # fields from the block name instead of repeatedly probing the live project
        # (list_udt_types / get_db_structure), which burns model calls and trips the
        # call-limit middleware before any SCL is produced.
        parts.append(
            "Members: (none provided — derive sensible fields from the block name; "
            "do NOT probe the live project to invent them)"
        )
    return "\n".join(parts)


def _interface_summary(plan: ProjectPlan, dep_ids: List[str]) -> str:
    """Assemble the specified dependency blocks' interface contracts into an interface-only summary (no implementation body)."""
    by_id = {t.id: t for t in plan.tasks}
    lines: List[str] = []
    for dep in dep_ids:
        t = by_id.get(dep)
        if t is None or t.status != TaskStatus.SUCCESS.value:
            continue
        iface = t.interface
        seg = f"- {t.kind} `{t.block_name}`"
        if iface.inputs:
            seg += f" IN[{','.join(iface.inputs)}]"
        if iface.outputs:
            seg += f" OUT[{','.join(iface.outputs)}]"
        if iface.db_refs:
            seg += f" DB[{','.join(iface.db_refs)}]"
        if iface.udt_refs:
            seg += f" UDT[{','.join(iface.udt_refs)}]"
        if getattr(iface, "tags", None):
            seg += f" TAGS[{','.join(iface.tags)}]"
        if getattr(iface, "members", None):
            seg += f" MEMBERS[{','.join(iface.members)}]"
        lines.append(seg)
    return "\n".join(lines)


def _deps_ready(task: Task, by_id: Dict[str, Task]) -> bool:
    """Ready only when all depends_on tasks are success."""
    for dep in task.depends_on:
        d = by_id.get(dep)
        if d is None or d.status != TaskStatus.SUCCESS.value:
            return False
    return True


def _progress(plan: ProjectPlan, current_id: Optional[str]) -> str:
    """Progress snapshot JSON (for frontend display)."""
    total = len(plan.tasks)
    done = sum(1 for t in plan.tasks if t.status == TaskStatus.SUCCESS.value)
    failed = sum(1 for t in plan.tasks if t.status == TaskStatus.FAILED.value)
    return json.dumps(
        {
            "plan_id": plan.id,
            "status": plan.status,
            "total": total,
            "done": done,
            "failed": failed,
            "current": current_id,
        },
        ensure_ascii=False,
    )


def _referenced_member_schemas(plan: ProjectPlan, task: Task) -> List[str]:
    """Member contracts of the UDTs/DBs this task references, taken from the plan itself.

    The coder can only see live project data through tools, and ``get_db_structure`` does not expand a
    UDT-typed member, so a task that references a UDT has no way to learn its field names and tends to
    invent them (which then fails TIA code generation). The plan already carries those fields on the
    UDT/DB task that created them, so surface them deterministically instead of hoping the model guesses
    or probes correctly.
    """
    wanted = list(task.interface.udt_refs or []) + list(task.interface.db_refs or [])
    if not wanted:
        return []
    by_name = {
        t.block_name: t
        for t in plan.tasks
        if t is not task
        and t.status == TaskStatus.SUCCESS.value
        and getattr(t.interface, "members", None)
    }
    lines: List[str] = []
    udt_types: List[str] = []
    for name in wanted:
        src = by_name.get(name)
        if src is not None:
            lines.append(f"- {src.kind} `{name}` fields: {', '.join(src.interface.members)}")
        # A referenced DB often only exposes its struct member ("Motor : V2_MotorParam").
        # Follow that type to the UDT task that defined it, otherwise the caller sees a
        # struct name it cannot address into and has to invent the leaf fields.
        for member in (src.interface.members if src is not None else []):
            if ":" in member:
                type_name = member.split(":", 1)[1].strip()
                if type_name and type_name not in udt_types:
                    udt_types.append(type_name)
    for type_name in udt_types:
        src = by_name.get(type_name)
        if src is not None and src.kind == "UDT":
            line = f"- UDT `{type_name}` fields: {', '.join(src.interface.members)}"
            if line not in lines:
                lines.append(line)
    return lines


async def _build_task_state(
    task: Task,
    plan: ProjectPlan,
    toolsets: Dict[str, List[Any]],
    context_window: int,
) -> Dict[str, Any]:
    """Build a fresh supervisor initial state for a single task (context isolation).

    Task 7: sidecar becomes task-level instead of run-level — each block prefetches its
    repair hint + relevant RAG + project status and folds them into context; fail-closed (any slot failure returns empty, without affecting the main flow).
    """
    prompt = _task_prompt(task)
    if task.kind in _SCL_KINDS:
        prompt += (
            "\nThe host writes this block itself after review: ONLY the content inside the single"
            " ```scl fenced block is imported — design prose outside the fence is never part of the"
            " imported source, so review the SCL block itself."
        )
    schema_lines = _referenced_member_schemas(plan, task)
    if schema_lines:
        prompt += (
            "\nReferenced type/DB fields (exact member names — use these, do not invent members):\n"
            + "\n".join(schema_lines)
        )
    if task.warnings:
        prompt += (
            "\nAdvisory contract warnings for this task (verify against the real project before"
            " writing; they do not by themselves mean the plan is wrong):\n- "
            + "\n- ".join(task.warnings)
        )
    summary = _interface_summary(plan, task.depends_on)
    context = ("Prior block interface contracts:\n" + summary) if summary else ""

    from agent.sidecar import run_sidecar, sidecar_context_block  # lazy import to avoid module loading cost

    side_q = f"{task.kind or ''} {task.block_name or ''} {prompt[:200]}".strip()
    sidecar_block = ""
    try:
        sidecar_results = await run_sidecar(
            prompt=side_q, context=context, project_key=plan.project_key or ""
        )
        sidecar_block = sidecar_context_block(sidecar_results)
    except Exception as exc:  # noqa: BLE001
        logger.warning(f"[executor] task-sidecar failed for {task.block_name}: {exc}")

    if sidecar_block:
        context = (context + "\n\n" + sidecar_block) if context else sidecar_block

    # Restate the plan's environment facts per task (CPU family / order number / version), so
    # the coder never works from a compressed-away context. Rules stay in the host block.
    from agent.environment import describe_for_prompt

    env_line = describe_for_prompt(plan.environment)
    if env_line:
        context = (context + "\n\n" + env_line) if context else env_line

    return {
        "messages": [HumanMessage(content=prompt)],
        "user_prompt": prompt,
        "context": context,
        "intent": "GenerateCode",
        "task_kind": task.kind,
        "images": [],
        "current_agent": "",
        "coder_output": "",
        "review_result": "",
        "review_passed": False,
        "coder_reviewer_rounds": 0,
        "compiler_attempts": 0,
        "compile_failed": False,
        "needs_compile": False,
        "context_window": int(context_window or 0),
        "final_answer": "",
        "status": "running",
        "toolsets": toolsets,
        "is_automated": True,
        "write_mode": "deferred" if task.kind in _SCL_KINDS else "",
        "environment": dict(plan.environment or {}),
    }


# Block kinds a block-level compile can target (UDT is a PlcType; TagTable is not a block).
_COMPILE_CHECK_KINDS = frozenset({"OB", "FC", "FB", "LAD_FC", "LAD_FB", "GRAPH_FB", "DB"})


def _compile_written_block(task: Task) -> Optional[Dict[str, Any]]:
    """Compile only the block this task just wrote; None for kinds that cannot be compiled.

    This is the gentle T5 gate: a block-level compile touches one block instead of the whole PLC,
    so it neither compiles Safety programs nor competes with blocks the user is editing.
    """
    if (task.kind or "").strip().upper() not in _COMPILE_CHECK_KINDS:
        return None
    if not task.block_name:
        return None
    try:
        from tools.tia_client import compile_block

        text = compile_block(task.block_name)
        if not isinstance(text, str) or not text.strip().startswith("{"):
            return {"unavailable": str(text)[:200]}
        data = json.loads(text)
        return data if isinstance(data, dict) else {"unavailable": "unexpected payload"}
    except Exception as exc:  # noqa: BLE001 - a broken check must not fail the task
        return {"unavailable": f"{type(exc).__name__}: {exc}"}


def _apply_compile_gate(task: Task, gate: Optional[Dict[str, Any]]) -> Optional[str]:
    """Apply a block-compile payload to a task.

    Returns None when the task stays successful; otherwise the failure reason. A skipped or
    unavailable check only annotates the task — it must never turn a written block into a failure.
    """
    if gate is None:
        return None
    skipped = str(gate.get("skipped") or "")
    unavailable = str(gate.get("unavailable") or "")
    error_count = int(gate.get("error_count") or 0)

    if skipped:
        task.warnings.append(f"Compile check skipped: {skipped}")
        return None
    if unavailable:
        task.warnings.append(f"Compile check unavailable: {unavailable}")
        return None
    if error_count > 0:
        messages = [
            str(e.get("message") or "")
            for e in (gate.get("errors") or [])
            if isinstance(e, dict)
        ]
        return ("Block compile failed: " + "; ".join(messages[:3]))[:400]
    return None


def _run_compile_tool(name: str) -> Optional[Dict[str, Any]]:
    """Invoke a whole-PLC compile tool (compile_snapshot / compile_diff) and parse its JSON."""
    from tools import definitions

    tool = getattr(definitions, name, None)
    if tool is None:
        return None
    try:
        text = tool.invoke({})
        if not isinstance(text, str) or not text.strip().startswith("{"):
            return None
        data = json.loads(text)
        return data if isinstance(data, dict) else None
    except Exception:  # noqa: BLE001 - the advisory check must never break a run
        return None


def _judge_success(result: Dict[str, Any], task_kind: str = "") -> bool:
    """Determine whether generation succeeded from the single-block graph's final state.

    Only used for non-SCL, non-UDT/DB block kinds (LAD + TagTable). SCL blocks are judged by
    `_finish_task` (review PASS + deterministic import_block write); UDT/DB by `_execute_udt_db_direct`.
    """
    coder_output = result.get("coder_output") or ""
    if task_kind in _LAD_KINDS or task_kind in _GRAPH_KINDS:
        # LAD/GRAPH have no SCL to review; success is the write confirmation echoed by the coder.
        # build_graph_fb additionally performs a block compile and reports failure as an error.
        return "built and imported" in coder_output
    # Any remaining kind (e.g. TagTable): require output + a passing review.
    return bool(coder_output) and bool(result.get("review_passed", False))


# ── UDT/DB deterministic direct write ─────────────────────────────────────
# UDT/DB are data definitions, not SCL logic. The executor writes them straight
# from the planned members via build_udt / build_global_db (no LLM, no
# reviewer/compiler SCL loop), because the coder→reviewer→compiler flow would
# misjudge a successful data-type/DB write as a compile failure.

def _parse_members(members) -> Optional[List[Dict[str, Any]]]:
    """Parse planned members ("Field : Type" strings) into build_udt/build_global_db member dicts.

    Returns None (not deterministically writable) when any member lacks a name or datatype.
    """
    if not members:
        return None
    parsed: List[Dict[str, Any]] = []
    for m in members:
        m = str(m).strip()
        if not m:
            return None
        if ":" in m:
            name, _, dtype = m.partition(":")
        else:
            name, dtype = m, ""
        name = name.strip()
        dtype = dtype.strip()
        if not name or not dtype:
            return None
        parsed.append({"name": name, "datatype": dtype})
    return parsed or None


def _db_number(task: Task) -> int:
    """Resolve a positive DB number from the task's block_number digits; default 1 when absent."""
    raw = str(task.block_number or "") if task.block_number is not None else ""
    digits = "".join(ch for ch in raw if ch.isdigit())
    if digits and int(digits) > 0:
        return int(digits)
    return 1


async def _execute_udt_db_direct(task: Task) -> Tuple[bool, Optional[str]]:
    """Deterministically write a UDT/DB block via build_udt / build_global_db.

    Returns ``(success, error)``. On success the write has been performed (subject to WriteGate
    approval); on failure returns a reason WITHOUT retrying (a refused write must not be retried).
    """
    from tools.catalog import get_catalog
    from tools.meta_tools import _tool_output_to_text

    members = _parse_members(task.interface.members)
    if members is None:
        return False, (
            "Planner did not provide parseable members for this UDT/DB block; "
            "cannot build it deterministically."
        )

    tool_name = "build_udt" if task.kind == "UDT" else "build_global_db"
    if task.kind == "UDT":
        spec: Dict[str, Any] = {"name": task.block_name, "members": members}
    else:
        spec = {"dbName": task.block_name, "dbNumber": _db_number(task), "staticMembers": members}

    entry = get_catalog().get(tool_name)
    if entry is None:
        return False, f"Tool '{tool_name}' is not in the catalog (is the C# MCP server running?)"

    args: Dict[str, Any] = {"json": spec}
    path = task.type_group_path if task.kind == "UDT" else task.group_path
    if path:
        args["group_path"] = path

    try:
        output = await entry.tool.ainvoke(args)
        text = _tool_output_to_text(output)
    except Exception as exc:  # noqa: BLE001
        return False, f"{tool_name} failed: {type(exc).__name__}: {exc}"

    stripped = (text or "").strip()
    if "built and imported" in stripped and not stripped.lower().startswith("error:"):
        logger.info(f"[executor] {tool_name} wrote '{task.block_name}': {stripped[:200]}")
        return True, None
    err = stripped
    if err.lower().startswith("error:"):
        err = err[len("Error:"):].strip()
    return False, (err or f"{tool_name} did not confirm the write")


# ── SCL deterministic deferred write ───────────────────────────────────────
# FC/FB/OB are authored as SCL. The coder generates only (returns the raw SCL
# block); after the reviewer/precheck pass, the executor imports it via import_block
# — same deterministic-write shape as UDT/DB, so success = review PASS + write success.

_SCL_FENCE_RE = re.compile(r"```[ \t]*(?:scl|SCL)?[ \t]*\r?\n(.*?)```", re.S)
_SCL_DECL_RE = re.compile(r"(?m)^\s*(?:FUNCTION_BLOCK|ORGANIZATION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\b")


def _extract_scl_block(text: str) -> Optional[str]:
    """Extract the raw SCL block from the coder's answer.

    Robust against the two shapes models actually emit: Design prose inside the same fence (trim to
    the block declaration) and a nested second fence (pick the first candidate that declares a block).
    """
    if not text:
        return None
    candidates = [m.group(1).strip() for m in _SCL_FENCE_RE.finditer(text)]
    # Last resort: the whole answer. A nested fence pair ("```scl ... ```scl ... ```") makes the
    # regex consume the inner opening as the outer closing, so the block only remains in the raw text.
    candidates.append(text.strip())
    for body in candidates:
        decl = _SCL_DECL_RE.search(body)
        if decl:
            return body[decl.start():].strip() or None
    return candidates[0] or None


# ── OB body splitting (organization blocks need their own write path) ──────
# An OB cannot come from an SCL external source (TIA rejects the number / the 20-byte interface), so
# build_ob composes the envelope itself. Only the body statements and the temp declarations travel.
_OB_HEADER_RE = re.compile(r'^\s*ORGANIZATION_BLOCK\s+"?([^"\n]+)"?', re.I | re.M)
_OB_VAR_TEMP_RE = re.compile(r"\bVAR_TEMP\b(.*?)\bEND_VAR\b", re.S | re.I)
_OB_VAR_CONST_RE = re.compile(r"\bVAR\s+CONSTANT\b(.*?)\bEND_VAR\b", re.S | re.I)
_OB_CONST_DECL_RE = re.compile(
    r"^\s*([A-Za-z_]\w*)\s*:\s*[^:=;\n]+?\s*:=\s*([^;\n]+?)\s*;", re.M
)
_OB_BEGIN_RE = re.compile(r"\bBEGIN\b", re.I)
_OB_END_RE = re.compile(r"\bEND_ORGANIZATION_BLOCK\b", re.I)
_OB_DECL_RE = re.compile(r"^\s*([A-Za-z_]\w*)\s*:\s*([^;\n]+)\s*;", re.M)


def _split_ob_scl(text: str) -> Optional[Dict[str, Any]]:
    """Split a coder's OB SCL into ``{name, temps, body}``; None when it is not an OB block."""
    src = text or ""
    header = _OB_HEADER_RE.search(src)
    if header is None:
        return None
    begin = _OB_BEGIN_RE.search(src, header.end())
    if begin is None:
        return None
    end = _OB_END_RE.search(src, begin.end())
    body = src[begin.end():(end.start() if end else len(src))].strip()
    if not body:
        return None
    temps: List[Dict[str, str]] = []
    temp_block = _OB_VAR_TEMP_RE.search(src, header.end(), begin.start())
    if temp_block:
        for decl in _OB_DECL_RE.finditer(temp_block.group(1)):
            temps.append({"name": decl.group(1), "datatype": decl.group(2).strip()})
    # build_ob only composes the 20-byte OB envelope + TEMP; it cannot carry the coder's
    # VAR CONSTANT section. Inline each constant's literal into the body, otherwise the
    # temp-FC tokenizer (and the imported OB) fail on undefined #<constant> operands.
    const_block = _OB_VAR_CONST_RE.search(src, header.end(), begin.start())
    if const_block:
        for decl in _OB_CONST_DECL_RE.finditer(const_block.group(1)):
            cname, value = decl.group(1), decl.group(2).strip()
            body = re.sub(r"#\s*%s\b" % re.escape(cname), value, body)
    return {"name": header.group(1).strip(), "temps": temps, "body": body}


def _is_write_refusal(error: Optional[str]) -> bool:
    """Whether a write error is a refusal/timeout (must not be retried)."""
    if not error:
        return False
    low = error.lower()
    return any(
        low.startswith(m)
        for m in ("user_denied", "approval_timeout", "approval_unavailable", "run_cancelled")
    )


async def _import_scl_direct(
    name: str, block_type: str, code: str, group_path: Optional[str] = None
) -> Tuple[bool, Optional[str]]:
    """Deterministically import an SCL block via the MCP ``import_block`` tool.

    Returns ``(success, error)``. Mirrors `_execute_udt_db_direct`: a refused write is not retried.
    """
    from tools.catalog import get_catalog
    from tools.meta_tools import _tool_output_to_text

    entry = get_catalog().get("import_block")
    if entry is None:
        return False, "Write tool 'import_block' is not in the catalog (is the C# MCP server running?)"

    args: Dict[str, Any] = {"block_name": name, "block_type": block_type, "code": code}
    if group_path:
        args["group_path"] = group_path
    try:
        output = await entry.tool.ainvoke(args)
        text = _tool_output_to_text(output)
    except Exception as exc:  # noqa: BLE001
        return False, f"import_block failed: {type(exc).__name__}: {exc}"

    stripped = (text or "").strip()
    low = stripped.lower()
    if _is_write_refusal(stripped):
        return False, stripped
    if "generated from scl source" in low:
        logger.info(f"[executor] import_block wrote '{name}': {stripped[:200]}")
        return True, None
    err = stripped
    if err.lower().startswith("error:"):
        err = err[len("Error:"):].strip()
    return False, (err or f"import_block did not confirm the write for '{name}'")


def _ob_number_of(value: Any) -> Optional[int]:
    """Parse an OB number out of task.block_number ("OB33", "33", "OB 33")."""
    if value is None:
        return None
    m = re.search(r"\d+", str(value))
    return int(m.group(0)) if m else None


def _resolve_ob_number(
    task: Task, tree: Optional[Dict[str, Any]]
) -> Tuple[Optional[int], bool, Optional[str]]:
    """Resolve ``(number, overwrite, error)`` for an OB task from the live program directory.

    - the planned number is reused when free; a same-name block means "overwrite it"
    - otherwise the first free cyclic-interrupt number (30-38) is allocated
    """
    ob_blocks = {
        b.get("number"): b.get("name")
        for b in ((tree or {}).get("blocks") or [])
        if str(b.get("type") or "").upper() == "OB"
    }
    planned = _ob_number_of(task.block_number)
    if planned is not None:
        legal = planned == 1 or 30 <= planned <= 38 or 100 <= planned <= 102
        if not legal:
            return None, False, f"OB number {planned} is not legal (use 1, 30-38 or 100-102)."
        owner = ob_blocks.get(planned)
        if owner and owner != task.block_name:
            return None, False, f"OB number {planned} is already used by '{owner}'."
        return planned, bool(owner == task.block_name), None
    for candidate in range(30, 39):
        if candidate not in ob_blocks:
            return candidate, False, None
    return None, False, "No free cyclic-interrupt OB number (30-38); set block_number explicitly."


async def _fetch_program_tree() -> Optional[Dict[str, Any]]:
    """Read the lightweight program directory (read-only); None when unavailable."""
    from tools.catalog import get_catalog
    from tools.meta_tools import _tool_output_to_text

    entry = get_catalog().get("get_program_tree")
    if entry is None:
        return None
    try:
        text = _tool_output_to_text(await entry.tool.ainvoke({}))
        return json.loads(text) if text and text.lstrip().startswith("{") else None
    except Exception as exc:  # noqa: BLE001
        logger.warning(f"[executor] program tree unavailable: {exc}")
        return None


async def _execute_ob_direct(task: Task, scl: str) -> Tuple[bool, Optional[str]]:
    """Deterministically build an OB via build_ob.

    build_ob composes the legal OB envelope (number + SecondaryType + 20-byte start-information
    interface) and tokenizes the SCL body itself, so the SCL external-source path is not used for OBs.
    A refused write is not retried.
    """
    from tools.catalog import get_catalog
    from tools.meta_tools import _tool_output_to_text

    parsed = _split_ob_scl(scl)
    if parsed is None:
        return False, "Coder produced no ORGANIZATION_BLOCK body to build."
    number, overwrite, err = _resolve_ob_number(task, await _fetch_program_tree())
    if err:
        return False, err
    entry = get_catalog().get("build_ob")
    if entry is None:
        return False, "Tool 'build_ob' is not in the catalog (is the app running the new build?)"
    spec = {
        "blockName": task.block_name or parsed["name"],
        "obNumber": number,
        "language": "SCL",
        "body": parsed["body"],
        "temps": parsed["temps"],
        "allow_overwrite": overwrite,
    }
    args: Dict[str, Any] = {"json": spec}
    if task.group_path:
        args["group_path"] = task.group_path
    try:
        output = await entry.tool.ainvoke(args)
        text = _tool_output_to_text(output)
    except Exception as exc:  # noqa: BLE001
        return False, f"build_ob failed: {type(exc).__name__}: {exc}"

    stripped = (text or "").strip()
    if _is_write_refusal(stripped):
        return False, stripped
    if "built and imported" in stripped.lower():
        logger.info(f"[executor] build_ob wrote '{spec['blockName']}' (OB{number}): {stripped[:200]}")
        return True, None
    err_text = stripped[len("Error:"):].strip() if stripped.lower().startswith("error:") else stripped
    return False, (err_text or "build_ob did not confirm the write")


async def _judge_scl_deferred(result: Dict[str, Any], task: Task) -> Tuple[bool, Optional[str]]:
    """Judge a deferred SCL task: require a passing review of the raw SCL, then write it."""
    if not bool(result.get("review_passed", False)):
        return False, None
    scl = _extract_scl_block(result.get("coder_output") or "")
    if not scl:
        return False, "Coder produced no SCL block to import"
    if task.kind == "OB":
        return await _execute_ob_direct(task, scl)
    name = task.target_block or task.block_name
    return await _import_scl_direct(name, task.kind, scl, task.group_path)


async def _finish_task(result: Dict[str, Any], task: Task) -> Tuple[bool, Optional[str]]:
    """Judge a single-block graph result and, for SCL blocks, perform the deterministic write."""
    if task.kind in _SCL_KINDS:
        return await _judge_scl_deferred(result, task)
    return _judge_success(result, task.kind), None


# ── Interface drift detection (task 6) ────────────────────────────────────

_VAR_INPUT_BLOCK = re.compile(r"\bVAR_INPUT\b(.*?)\bEND_VAR\b", re.S)
_VAR_OUTPUT_BLOCK = re.compile(r"\bVAR_OUTPUT\b(.*?)\bEND_VAR\b", re.S)
_VAR_INOUT_BLOCK = re.compile(r"\bVAR_IN_OUT\b(.*?)\bEND_VAR\b", re.S)
_VAR_LINE = re.compile(r"^\s*([A-Za-z_]\w*)\s*:", re.M)
_DB_REF = re.compile(r'"([A-Za-z_]\w*)"\s*\.')

# UDT reference extraction: find `name : type ;` declarations in all VAR sections; non-builtin/non-keyword identifiers in the type are treated as UDT/DB type references.
_VAR_SECTION = re.compile(r"\bVAR(?:_INPUT|_OUTPUT|_IN_OUT|_TEMP|_STAT|_CONSTANT)?\b(.*?)\bEND_VAR\b", re.S)
_DECL = re.compile(r"^\s*([A-Za-z_]\w*)\s*:\s*([^;\n]+)\s*;", re.M)
_UDT_TOKEN = re.compile(r'"([A-Za-z_]\w*)"|([A-Za-z_][A-Za-z0-9_]*)')

# SCL builtin primitive types / keywords (matches are not UDTs and are excluded from udt_refs).
_SCL_BUILTIN_TYPES = {
    "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT",
    "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "CHAR", "WCHAR",
    "STRING", "WSTRING", "TIME", "LTIME", "DATE", "TIME_OF_DAY", "TOD",
    "DATE_AND_TIME", "DTL", "VOID", "ANY", "VARIANT", "POINTER", "COUNTER", "TIMER",
}
_SCL_KEYWORDS = {"ARRAY", "OF", "STRUCT", "REF_TO", "CONSTANT", "AT", "RETAIN"}


def _collect_vars(text: str, block_re) -> List[str]:
    """Extract variable names from the specified VAR_xxx sections."""
    names: List[str] = []
    for m in block_re.finditer(text):
        for ln in _VAR_LINE.finditer(m.group(1)):
            names.append(ln.group(1))
    return names


def _extract_scl_udt_refs(text: str) -> List[str]:
    """Extract UDT/DB type references from variable declarations in SCL VAR sections (excluding builtin types)."""
    refs: set = set()
    for vm in _VAR_SECTION.finditer(text):
        for dm in _DECL.finditer(vm.group(1)):
            texpr = dm.group(2).strip()
            for tok in _UDT_TOKEN.finditer(texpr):
                raw = tok.group(1) or tok.group(2) or ""
                if raw and raw.upper() not in _SCL_BUILTIN_TYPES and raw.upper() not in _SCL_KEYWORDS:
                    refs.add(raw)
    return sorted(refs)


def _extract_scl_interface(scl_text: str) -> BlockInterface:
    """Extract the interface contract from SCL text (VAR_INPUT/OUTPUT/IN_OUT variable names + DB refs + UDT refs)."""
    text = scl_text or ""
    inputs = _collect_vars(text, _VAR_INPUT_BLOCK) + _collect_vars(text, _VAR_INOUT_BLOCK)
    outputs = _collect_vars(text, _VAR_OUTPUT_BLOCK)
    db_refs = sorted(set(_DB_REF.findall(text)))
    udt_refs = _extract_scl_udt_refs(text)
    return BlockInterface(inputs=inputs, outputs=outputs, db_refs=db_refs, udt_refs=udt_refs)


# XML interface extraction (graphic blocks like LAD/FBD are represented as XML in Openness): Section(Input/Output/InOut) + Member(Name, Datatype).
_XML_SECTION = re.compile(r'<Section\s+Name="(Input|Output|InOut|In_Out)"[^>]*>(.*?)</Section>', re.S | re.I)
_XML_MEMBER = re.compile(r'<Member\b[^>]*Name="([^"]+)"[^>]*?Datatype="([^"]+)"[^>]*>', re.S | re.I)


def _extract_xml_interface(xml_text: str) -> BlockInterface:
    """Extract the interface contract from the XML block representation (Input/Output/InOut members + UDT type references)."""
    text = xml_text or ""
    inputs: List[str] = []
    outputs: List[str] = []
    udt_refs: List[str] = []
    for sm in _XML_SECTION.finditer(text):
        sname = (sm.group(1) or "").lower()
        for mm in _XML_MEMBER.finditer(sm.group(2) or ""):
            name = mm.group(1)
            dtype = (mm.group(2) or "").strip()
            if sname in ("input", "inout", "in_out"):
                inputs.append(name)
            if sname == "output":
                outputs.append(name)
            dup = dtype.upper()
            if dup and dup not in _SCL_BUILTIN_TYPES and dup not in _SCL_KEYWORDS:
                udt_refs.append(dtype)
    return BlockInterface(inputs=inputs, outputs=outputs, db_refs=[], udt_refs=sorted(set(udt_refs)))


def _extract_interface(artifact_text: str) -> BlockInterface:
    """Dispatch interface extraction by artifact type: XML (graphic blocks) goes through XML parsing, otherwise SCL regex."""
    t = (artifact_text or "").lstrip()
    if t.startswith("<"):
        return _extract_xml_interface(t)
    return _extract_scl_interface(t)


def _interface_changed(planned: BlockInterface, actual: BlockInterface) -> bool:
    """Whether the planned contract differs from the actual artifact interface (order ignored)."""
    return (
        sorted(planned.inputs) != sorted(actual.inputs)
        or sorted(planned.outputs) != sorted(actual.outputs)
        or sorted(planned.db_refs) != sorted(actual.db_refs)
        or sorted(planned.udt_refs) != sorted(actual.udt_refs)
    )


def _reconcile_interface(plan: ProjectPlan, task: Task, actual: BlockInterface) -> bool:
    """If the actual interface diverges from the plan, write back task.interface and mark downstream dependents stale."""
    # An all-empty extraction is not treated as drift, to avoid mis-marking downstream as stale.
    if not (actual.inputs or actual.outputs or actual.db_refs or actual.udt_refs):
        return False
    if not _interface_changed(task.interface, actual):
        return False
    task.interface = actual
    for d in plan.tasks:
        if task.id in d.depends_on and d.status == TaskStatus.PENDING.value:
            d.status = TaskStatus.STALE.value
    logger.info(f"[executor] interface drift on {task.id} ({task.block_name}); downstream marked stale")
    return True


def _pending_milestone(plan: ProjectPlan) -> Optional[Phase]:
    """Return a milestone phase whose tasks are all success but not yet approved; None if none."""
    approved = {m.phase_id for m in plan.milestone_decisions if m.decision == "approve"}
    for p in plan.phases:
        if not p.is_milestone or not p.tasks or p.id in approved:
            continue
        if all(
            (t := plan.task_by_id(tid)) is not None and t.status == TaskStatus.SUCCESS.value
            for tid in p.tasks
        ):
            return p
    return None


# ── Top-level entry points ───────────────────────────────────

def apply_task_decision(plan: ProjectPlan, task_id: str, decision: str) -> Optional[str]:
    """Apply a task failure decision (continue / skip / terminate), mutating plan in place; returns an error message or None.

    - continue: reset the task to pending so resume re-executes it.
    - skip: mark the task skipped and its pending downstream stale (cascading skip).
    - terminate: mark the whole plan rejected (terminated but kept).
    """
    t = plan.task_by_id(task_id)
    if t is None:
        return "task not found"
    if decision == "continue":
        t.status = TaskStatus.PENDING.value
        t.error = None
        plan.status = PlanStatus.READY.value
        return None
    if decision == "skip":
        t.status = TaskStatus.SKIPPED.value
        for d in plan.tasks:
            if task_id in d.depends_on and d.status == TaskStatus.PENDING.value:
                d.status = TaskStatus.STALE.value
        plan.status = PlanStatus.READY.value
        return None
    if decision == "terminate":
        plan.status = PlanStatus.REJECTED.value
        return None
    return "invalid decision"


def next_resumable_tasks(plan: ProjectPlan, resume: bool) -> List[Task]:
    """When resuming, return pending tasks in plan order: skip success (done) and skipped (manually skipped)."""
    if not resume:
        return list(plan.tasks)
    return [
        t for t in plan.tasks
        if t.status not in (TaskStatus.SUCCESS.value, TaskStatus.SKIPPED.value)
    ]


def _extract_task_tokens(result: Dict[str, Any]) -> int:
    """Sum the in+out token count of one single-block graph result (task-level: additive).

    Extract usage_metadata from each AIMessage in the final state's messages (reusing the supervisor's
    `_extract_usage_metadata`), adding input+output. Returns 0 when no usage is available (does not block the main flow).
    """
    from agent.supervisor import _extract_usage_metadata

    messages = (result or {}).get("messages") or []
    if not isinstance(messages, (list, tuple)):
        return 0
    total = 0
    for m in messages:
        u = _extract_usage_metadata(m)
        if u:
            total += int(u.get("input_tokens", 0) or 0) + int(u.get("output_tokens", 0) or 0)
    return total


async def run_project_plan_stream(
    llm,
    plan: ProjectPlan,
    store=None,
    context_window: int = 0,
    resume: bool = True,
    verify_compile_at_end: bool = False,
) -> AsyncIterator[Tuple[str, str]]:
    """Consume the DAG in topological order, running the supervisor graph once per task, persisting and pushing progress as it goes."""
    from agent.plan_store import PlanStore
    from agent.supervisor import _build_supervisor_graph
    from tools.registry import build_agent_toolsets

    store = store or PlanStore()

    # Environment pre-check: the user may have switched the CPU or TIA version since the plan
    # was built. CPU family / order number / TIA version changes block execution (code planned
    # for the old CPU can be invalid); a different project only warns.
    from agent import environment as environment_mod

    live_environment = await asyncio.to_thread(environment_mod.fetch_profile)
    change = environment_mod.detect_change(plan.environment, live_environment)
    if change["blocking"]:
        plan.status = PlanStatus.PAUSED.value
        store.save(plan)
        yield "error", (
            "Environment changed since this plan was built: "
            + "; ".join(change["blocking"])
            + ". Re-plan for the current CPU / TIA version before continuing."
        )
        return
    for warning in change["warnings"]:
        yield "notice", notice("plan.environment.warning", warning)

    toolsets = await build_agent_toolsets()
    graph = _build_supervisor_graph(llm)

    plan.status = PlanStatus.RUNNING.value
    store.save(plan)
    yield "notice", notice(
        "plan.started", f"Plan started: {len(plan.tasks)} blocks", len(plan.tasks)
    )
    warning_count = sum(len(t.warnings or []) for t in plan.tasks)
    if warning_count:
        # Advisory only: the plan still runs; each affected task carries its own warnings.
        yield "notice", notice(
            "plan.contract.warnings",
            f"Plan has {warning_count} advisory contract warning(s); they are attached to the affected tasks.",
            warning_count,
        )

    by_id = {t.id: t for t in plan.tasks}
    pending = next_resumable_tasks(plan, resume)

    for task in pending:
        # dependencies not ready (upstream failed/incomplete) → skip
        if not _deps_ready(task, by_id):
            task.status = TaskStatus.SKIPPED.value
            task.error = "Upstream dependency not ready or failed"
            store.save(plan)
            yield "plan_progress", _progress(plan, task.id)
            continue

        task.status = TaskStatus.IN_PROGRESS.value
        store.save(plan)
        yield "notice", notice(
            "plan.task.generating",
            f"Generating {task.kind} block `{task.block_name}`...",
            task.kind,
            task.block_name,
        )
        yield "plan_progress", _progress(plan, task.id)

        result: Dict[str, Any] = {}
        task_tokens = 0
        direct_error: Optional[str] = None

        if task.kind in ("UDT", "DB"):
            # UDT/DB are data definitions: write them deterministically from the planned
            # members via build_udt / build_global_db, bypassing the SCL coder/reviewer/compiler loop.
            success, direct_error = await _execute_udt_db_direct(task)
        else:
            initial_state = await _build_task_state(task, plan, toolsets, context_window)
            result = await _run_once(graph, initial_state)
            task_tokens += _extract_task_tokens(result)
            success, direct_error = await _finish_task(result, task)

            if not success and not _is_write_refusal(direct_error):
                # retry once (bounded retry)
                result = await _run_once(graph, initial_state)
                task_tokens += _extract_task_tokens(result)
                success, direct_error = await _finish_task(result, task)

        degrade_reason = None
        if success and task_tokens > _TASK_TOKEN_BUDGET:
            # generated successfully but over token budget → downgrade to failure (4.12 task-level downgrade)
            success = False
            degrade_reason = f"Task token usage {task_tokens} exceeds budget {_TASK_TOKEN_BUDGET}"

        # Gentle post-write gate: compile only the block this task wrote. A whole-PLC compile is
        # opt-in at the end of the plan, so a user editing other blocks is not disturbed here.
        if success and task.block_name:
            gate = await asyncio.to_thread(_compile_written_block, task)
            failure = _apply_compile_gate(task, gate)
            if failure:
                success = False
                direct_error = failure

        if success:
            task.status = TaskStatus.SUCCESS.value
            task.error = None
            if result.get("coder_output"):
                _reconcile_interface(plan, task, _extract_interface(result.get("coder_output", "")))
            yield "plan_progress", _progress(plan, task.id)
        else:
            # pause on failure for manual resolution: failure/timeout/over-budget no longer silently continue; set paused + emit task_failed,
            # and let the user decide continue / skip / terminate via /plan/task_decide.
            task.status = TaskStatus.FAILED.value
            task.error = direct_error or degrade_reason or result.get("compile_error_summary") or result.get("final_answer") or "Generation/compilation failed"
            plan.status = PlanStatus.PAUSED.value
            store.save(plan)
            yield "notice", notice(
                "plan.task.failed",
                f"{task.kind} block `{task.block_name}` failed: {task.error[:200]}",
                task.kind,
                task.block_name,
                task.error[:200],
            )
            manual_action = match_manual_action(task.error)
            yield "task_failed", json.dumps(
                {
                    "plan_id": plan.id,
                    "task_id": task.id,
                    "kind": task.kind,
                    "block_name": task.block_name,
                    "error": (task.error or "")[:400],
                    "manual_action": (
                        {"code": manual_action["code"], "text": manual_action["text"]}
                        if manual_action else None
                    ),
                },
                ensure_ascii=False,
            )
            yield "plan_progress", _progress(plan, task.id)
            return

        store.save(plan)

        # milestone pause: if a milestone phase exists whose tasks are all success and not yet approved → pause for approval
        mile = _pending_milestone(plan)
        if mile is not None:
            plan.status = PlanStatus.PAUSED.value
            store.save(plan)
            yield "milestone", json.dumps(
                {"plan_id": plan.id, "phase_id": mile.id, "name": mile.name, "tasks": mile.tasks},
                ensure_ascii=False,
            )
            yield "notice", notice(
                "plan.milestone.waiting",
                f"Milestone '{mile.name}' completed; awaiting approval (approve, then resume to continue)",
                mile.name,
            )
            return

    if verify_compile_at_end:
        # Opt-in, once per plan. Advisory only: it may surface errors the user introduced manually,
        # so it reports counts and never rewrites task statuses.
        yield "notice", notice("plan.compile.verify", "Running one full PLC compile to verify the plan (advisory)...")
        await asyncio.to_thread(_run_compile_tool, "compile_snapshot")
        diff = await asyncio.to_thread(_run_compile_tool, "compile_diff")
        if diff is not None:
            yield "notice", notice(
                "plan.compile.result",
                "Full compile after plan: new={0}, pre_existing={1}, resolved={2}",
                diff.get("new_count"),
                diff.get("pre_existing_count"),
                diff.get("resolved_count"),
            )

    plan.status = PlanStatus.DONE.value
    store.save(plan)
    yield "notice", notice("plan.completed", "Plan completed")
    yield "plan_progress", _progress(plan, None)
    yield "done", "ok"


async def _run_once(graph, initial_state: Dict[str, Any]) -> Dict[str, Any]:
    """Run the single-block graph once; on exception return a failure state (fail-closed)."""
    try:
        # Pre-write compile baseline: without it the compiler node cannot tell the errors
        # this write introduced from errors the project already carried. Opt out with
        # TIA_AUTO_COMPILE=0 (every compile is a whole-PLC compile, so large projects pay for it).
        if (os.getenv("TIA_AUTO_COMPILE", "1") != "0"
                and "generate" in str(initial_state.get("intent") or "").lower()):
            from agent.type_repair import take_baseline
            await asyncio.to_thread(take_baseline)
        return await graph.ainvoke(initial_state) or {}
    except Exception as exc:  # noqa: BLE001
        logger.exception(f"[executor] single-task graph run failed: {exc}")
        return {"final_answer": f"Execution error: {type(exc).__name__}: {exc}"}