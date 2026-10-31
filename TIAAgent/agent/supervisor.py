"""
Supervisor Agent — orchestrates multi-agent collaboration for cloud LLMs.

Architecture:
  supervisor_node → coder_agent → supervisor_node
                  → reviewer_agent → supervisor_node
                  → compiler_agent → supervisor_node
                  → chat_agent → supervisor_node
                  → FINAL (end)

The supervisor analyzes the user request, routes to specialist agents,
collects their outputs, and decides next steps. Supports:
  - GenerateCode: coder → reviewer → (coder if fixes needed) → compiler
  - ExplainCode: reviewer only
  - Compile: compiler only
  - Chat: chat agent (streaming conversational responses)
"""

import asyncio
import json
import logging
import os
import re
import time
from typing import Dict, List, Optional, Any, AsyncIterator, Tuple, Annotated, Literal

from langchain_openai import ChatOpenAI
from langchain_core.messages import (
    SystemMessage, HumanMessage, AIMessage, BaseMessage, ToolMessage,
)
from langgraph.graph import StateGraph, END
from langgraph.graph.message import add_messages
from typing_extensions import TypedDict

from agent.coder import create_coder_agent
from agent.reviewer import create_reviewer_agent
from agent.chat import create_chat_agent, run_chat_stream
from agent.middleware_config import _TOOL_CALL_SOFT_LIMIT
from agent.notices import notice
from tools.meta_tools import display_tool_name
from core.context import (
    extract_stream_delta, extract_stream_reasoning, build_user_content,
    trim_messages, truncate_tool_result, get_token_budget,
)

logger = logging.getLogger("backend")

# ── Constants ────────────────────────────────────────────────

_MAX_CODER_REVIEWER_ROUNDS = 2  # Max coder→reviewer→coder cycles
_MAX_COMPILER_ATTEMPTS = 3      # Max compile→fix→compile cycles
_MAX_ANALYZE_ROUNDS = 1         # Max reviewer runs for read-only analysis
_MAX_APPROVAL_REFLECT_ROUNDS = 1  # After an approval timeout: one reflect+regenerate round

# Refusal tokens a gated write returns instead of a result. The write was NOT executed,
# so reviewing it as "code" is meaningless: the run must end (or reflect once) instead of
# burning reviewer/coder rounds on a message that contains no compilable payload.
_WRITE_REFUSAL_TOKENS = (
    "approval_timeout", "user_denied", "approval_unavailable", "run_cancelled",
)

# Markers of a run aborted by a server-side tool-call cap (the relay kills the
# run and its jargon becomes the "answer"). Detected so the user gets a
# friendly, actionable note instead of raw technical text.
_TOOL_LIMIT_ABORT_MARKERS = (
    "tool call limit reached",
    "run limit exceeded",
    "model call limits exceeded",
)

# ── Transient-error retry ────────────────────────────────────
# Intermittent network resets (EOF during TLS handshake, connect timeouts, etc.)
# hit the LLM client and the compile tool transport now and then. Browsers and other
# clients silently retry these; here we retry a bounded number of times so a single
# blip does not fail an entire coder/reviewer/compiler round. Permanent errors (auth,
# bad request, parse) are NOT retried and propagate immediately.

_MAX_RETRY_ATTEMPTS = 3                 # 1 initial attempt + 2 retries
_RETRY_BACKOFF_SECONDS = (1.0, 2.0)     # per-retry delay (index = retry number)

# Matched by class NAME (not by import) so this works across openai/httpx/requests
# versions and covers both the LLM client and the compile tool transport.
_TRANSIENT_ERROR_MARKERS = (
    "APIConnectionError", "APITimeoutError", "RateLimitError",
    "InternalServerError", "ConnectError", "ConnectTimeout", "ReadTimeout",
    "ConnectionError", "RemoteProtocolError", "StreamChunkTimeoutError",
)

# Provider hiccups that surface as generic exceptions (LangChain raises a bare
# ValueError when the stream carries no generation, and a StreamChunkTimeoutError
# when the connection stalls). Message-matched, because the class name says nothing.
_TRANSIENT_ERROR_MESSAGES = (
    "No generations found in stream",
    "No streaming chunk received",
)

# Provider-side throttling surfaces as a plain exception carrying the operator's text
# ("站点触发限流，请稍后重试") rather than a RateLimitError, so it is matched by text.
# It needs a much longer backoff than a dropped connection: retrying after 1s against
# a per-minute quota just burns another call.
_RATE_LIMIT_MARKERS = (
    "站点触发限流", "触发限流", "rate limit", "rate_limit",
    "too many requests", "quota exceeded", "overloaded", "429",
)
_RATE_LIMIT_BACKOFF_SECONDS = (15.0, 30.0)

_RATE_LIMIT_MESSAGE = (
    "模型服务正在限流（站点触发限流），本轮评审没能执行，也没有写入任何内容。"
    "请稍后重试，或临时切换到其他模型。"
)


def _is_rate_limit_error(exc: BaseException) -> bool:
    text = str(exc).lower()
    return any(marker in text for marker in _RATE_LIMIT_MARKERS)


def _is_transient_error(exc: BaseException) -> bool:
    name = type(exc).__name__
    if any(marker in name for marker in _TRANSIENT_ERROR_MARKERS):
        return True
    if _is_rate_limit_error(exc):
        return True
    text = str(exc)
    return any(marker in text for marker in _TRANSIENT_ERROR_MESSAGES)


async def _ainvoke_with_retry(fn, what: str = "ainvoke"):
    """Invoke ``fn`` (a coroutine-returning callable), retrying transient errors.

    Retries transient network errors up to 2 extra times; raises immediately on
    non-transient errors (and after the final attempt), so callers' existing
    fail-closed handling keeps working exactly as before.

    The sub-agents and the meta-tools (``invoke_tool``) are async-only, so the
    graph nodes must run them via ``ainvoke`` on the async path.
    """
    last_exc: Optional[BaseException] = None
    for attempt in range(_MAX_RETRY_ATTEMPTS):
        try:
            return await fn()
        except Exception as exc:  # noqa: BLE001 - re-raise non-transient errors
            last_exc = exc
            if not _is_transient_error(exc) or attempt >= _MAX_RETRY_ATTEMPTS - 1:
                raise
            if _is_rate_limit_error(exc):
                delay = _RATE_LIMIT_BACKOFF_SECONDS[min(attempt, len(_RATE_LIMIT_BACKOFF_SECONDS) - 1)]
            else:
                delay = _RETRY_BACKOFF_SECONDS[attempt] if attempt < len(_RETRY_BACKOFF_SECONDS) else _RETRY_BACKOFF_SECONDS[-1]
            logger.warning(
                f"[{what}] transient error, retry {attempt + 1}/{_MAX_RETRY_ATTEMPTS - 1} "
                f"after {delay}s: {type(exc).__name__}: {exc}"
            )
            await asyncio.sleep(delay)
    raise last_exc  # pragma: no cover - loop always returns or raises


# ── State ────────────────────────────────────────────────────

class MultiAgentState(TypedDict, total=False):
    messages: Annotated[List[BaseMessage], add_messages]
    user_prompt: str
    context: str
    intent: str
    task_kind: str  # executor-set block kind (e.g. LAD_FC/LAD_FB) used to skip the SCL review/compile loop
    images: List[str]  # base64-encoded PNG images for vision-capable models
    current_agent: str
    coder_output: str
    coder_code: str  # exact SCL payload captured from the coder's import_block call
    coder_failed: bool  # coder aborted by a call-limit middleware before producing SCL
    needs_user_input: bool  # a guard stopped the agent and surfaced a clarification question
    approval_reflect_rounds: int  # after a refused write: one reflect+regenerate round
    review_result: str
    review_passed: bool
    coder_reviewer_rounds: int
    compiler_attempts: int
    compile_failed: bool
    needs_compile: bool  # True when fresh code exists that hasn't been compiled yet
    compile_error_summary: str  # recent compile failure summary (drives the compile-fix branch)
    compile_user_asked: bool  # the user was already asked to attribute a block-less compile error
    repair_hints: str  # advisory repair experiences recalled for the failure ("" when none)
    recalled_signatures: List[str]  # normalized keys recalled last fix round (for eval feedback)
    context_window: int  # model's real max input tokens (0/absent → conservative default)
    final_answer: str
    status: str  # "running" | "completed" | "error"
    toolsets: Dict[str, List[Any]]  # per-agent runtime toolsets (MCP + local), from tools.registry
    write_mode: str  # executor-set "deferred" for planner SCL blocks (coder generate-only; executor writes)
    is_automated: bool  # executor-set automated batch run (no interactive user to ask)
    environment: Dict[str, Any]  # live environment facts for this run (plc_family / order_number / fingerprint)


def _extract_usage_metadata(output) -> Optional[dict]:
    """Pull token usage from an ``on_chat_model_end`` output.

    LangChain surfaces usage in a few shapes depending on version/provider:
      - an AIMessage(.usage_metadata = {input_tokens, output_tokens, total_tokens})
      - an LLMResult whose .generations[0][0].message carries the same
      - .llm_output["token_usage"] / ["usage"] (OpenAI-style)
    Returns a normalized dict or None.
    """
    if output is None:
        return None

    def _norm(um) -> Optional[dict]:
        if not isinstance(um, dict):
            return None
        # OpenAI-style keys → normalized keys
        return {
            "input_tokens": um.get("input_tokens", um.get("prompt_tokens", 0)) or 0,
            "output_tokens": um.get("output_tokens", um.get("completion_tokens", 0)) or 0,
            "total_tokens": um.get("total_tokens", 0) or 0,
        }

    # 1) Direct AIMessage-like object
    um = getattr(output, "usage_metadata", None)
    n = _norm(um)
    if n:
        return n

    # 2) LLMResult-like: dig into generations
    generations = getattr(output, "generations", None)
    if generations:
        try:
            for gen_list in generations:
                for gen in gen_list:
                    msg = getattr(gen, "message", None)
                    n = _norm(getattr(msg, "usage_metadata", None))
                    if n:
                        return n
        except (TypeError, AttributeError):
            pass

    # 3) llm_output token_usage
    llm_output = getattr(output, "llm_output", None)
    if isinstance(llm_output, dict):
        n = _norm(llm_output.get("token_usage") or llm_output.get("usage"))
        if n:
            return n

    # 4) output itself is a dict carrying usage
    if isinstance(output, dict):
        n = _norm(output.get("usage_metadata") or output.get("token_usage") or output.get("usage"))
        if n:
            return n

    return None


# ── Node functions ───────────────────────────────────────────

# Intents that require generating/modifying code (coder → reviewer → compiler).
_CODE_INTENTS = frozenset({"GenerateCode", "ModifyCode", "GenerateAndFix", "Import"})
# Intents that are read-only analysis (reviewer only). Mirrors C# AgentIntent.
_ANALYZE_INTENTS = frozenset({"ExplainCode", "Analyze", "TraceVariable"})
# Intents that only compile/diagnose (compiler only).
_COMPILE_INTENTS = frozenset({"Compile", "Diagnose"})
# Whether a code intent should run the compiler after review passes.
_INTENTS_REQUIRING_COMPILE = frozenset({"GenerateCode", "ModifyCode", "GenerateAndFix", "Import"})


# ── Project-level long-task detection ─────────────────────────
# Keyword fast path: only go to the Planner on a hit (the Planner doubles as the "project-level detector"; if it cannot
# decompose a multi-block DAG it falls back to the single-block flow), avoiding an extra Planner LLM call per GenerateCode.
_PROJECT_GEN_KEYWORDS = (
    "整个项目", "整套", "生成一个plc项目", "生成一个 plc 项目", "离线项目",
    "完整项目", "生成项目", "生成整个", "多个块", "plc项目", "全部块",
    "whole project", "entire project", "generate a plc project", "full project",
)


def _looks_like_project_generation(prompt: str) -> bool:
    """Roughly judge whether a user request is a "project-level long task" (keyword fast path, not exact)."""
    p = (prompt or "").lower()
    return any(k.lower() in p for k in _PROJECT_GEN_KEYWORDS)


async def _try_plan_project(llm, prompt: str, context: str, project_key: str, planning_knowledge: str, tools=None, environment=None):
    """Call the Planner to produce a ProjectPlan; returns None on failure / cannot decompose (caller falls back to single block)."""
    from agent.planner import create_project_plan
    try:
        return await create_project_plan(
            llm=llm,
            user_prompt=prompt,
            project_context=context or "",
            project_key=project_key or "",
            planning_knowledge=planning_knowledge or "",
            tools=tools,
            environment=environment,
        )
    except Exception:  # noqa: BLE001
        logger.warning("[supervisor] project planning failed; falling back to single-block flow")
        return None


async def prepare_project_plan(
    llm, prompt: str, context: str, project_key: str, planning_knowledge: str
):
    """Prepare a project-level plan (shared by background-job startup and run_multi_agent_stream).

    On a project intent, returns the plan to execute: resume from checkpoint (including adjust replanning) → fresh planning;
    otherwise returns None (caller falls back to the single-block flow). Does no streaming output.
    """
    from agent.plan_models import PlanStatus
    from agent.plan_store import PlanStore
    from tools.registry import build_agent_toolsets

    if not _looks_like_project_generation(prompt):
        return None

    # Live environment facts (TIA version + CPU family). Read once per planning call and
    # bound to the plan; the executor re-reads them before running and blocks on CPU/version
    # changes. Fetch failure must not block planning (returns None → facts left empty).
    live_environment: Dict[str, Any] = {}
    try:
        from agent import environment as environment_mod
        live_environment = await asyncio.to_thread(environment_mod.fetch_profile) or {}
    except Exception:  # noqa: BLE001
        logger.warning("[supervisor] environment profile unavailable; planning without it")

    # task 3: Planner-bound toolset (assembled before each planning, includes search_project / standard library / get_current_project meta-tools).
    planner_tools = None
    try:
        planner_tools = (await build_agent_toolsets()).get("planner")
    except Exception:  # noqa: BLE001
        logger.warning("[supervisor] planner toolset build failed; planning without tools")

    if project_key:
        resume_plan = PlanStore().find_unfinished(project_key)
        if resume_plan is not None and resume_plan.status == PlanStatus.PAUSED.value:
            from agent.planner import replan_unfinished
            return await replan_unfinished(
                llm, resume_plan, prompt, context or "", planning_knowledge or "", tools=planner_tools,
                environment=live_environment,
            )
    return await _try_plan_project(llm, prompt, context, project_key, planning_knowledge, tools=planner_tools,
                                   environment=live_environment)


def _decide_next(state: MultiAgentState) -> str:
    """Deterministic router: decide the next node purely from state.

    Returns one of: "coder", "reviewer", "compiler", "chat", "__end__".

    This replaces the old LLM-decides-routing scheme, which was fragile (a
    mis-formatted "NEXT:" line silently ended the whole task) and let the round
    caps live only in the prompt text. Here the caps are enforced in code.

    The LLM is NOT consulted for routing — it only runs inside the specialist
    nodes to generate/review/chat content.
    """
    intent = state.get("intent", "Chat") or "Chat"
    coder_output = state.get("coder_output", "") or ""
    review_done = bool(state.get("review_result", ""))
    review_passed = bool(state.get("review_passed", False))
    rounds = int(state.get("coder_reviewer_rounds", 0) or 0)
    compiler_attempts = int(state.get("compiler_attempts", 0) or 0)
    compile_failed = bool(state.get("compile_failed", False))
    needs_compile = bool(state.get("needs_compile", False))

    # Coder aborted by a call-limit middleware before producing SCL: end immediately
    # instead of routing empty output back into the coder loop.
    if state.get("coder_failed"):
        return "__end__"

    # A loop guard stopped the agent and produced a question for the user: end the run
    # and surface that question instead of sending it to the reviewer as "code".
    if state.get("needs_user_input"):
        return "__end__"

    # ── Chat ──
    if intent == "Chat":
        return "chat"

    # ── Read-only analysis: reviewer once, then end ──
    # The round cap is a hard backstop: if the reviewer returns EMPTY content,
    # review_done stays False and without the cap this would loop forever.
    # The reviewer node always increments coder_reviewer_rounds (success or
    # error), so after _MAX_ANALYZE_ROUNDS we always end.
    if intent in _ANALYZE_INTENTS:
        if review_done or rounds >= _MAX_ANALYZE_ROUNDS:
            return "__end__"
        return "reviewer"

    # ── Compile/Diagnose only ──
    if intent in _COMPILE_INTENTS:
        return "compiler" if compiler_attempts == 0 else "__end__"

    # ── Code intents: coder → reviewer → (coder on fix) → compiler ──
    if intent in _CODE_INTENTS:
        # 1) No code yet → coder
        if not coder_output and not state.get("coder_code"):
            return "coder"

        # LAD blocks have no SCL to review or compile: end right after the coder writes them.
        if state.get("task_kind") in ("LAD_FC", "LAD_FB", "GRAPH_FB"):
            return "__end__"

        # 2) Code exists but not reviewed this round → reviewer
        if not review_done:
            return "reviewer"

        # 3) Reviewed and needs fix → coder again, if rounds remain
        if not review_passed:
            if rounds < _MAX_CODER_REVIEWER_ROUNDS:
                return "coder"
            # Out of fix rounds → proceed to compile with what we have
            # (or end if this intent doesn't compile).
            # fall through to the compile decision below.

        # 4) Review passed (or out of fix rounds) → compile if required
        if intent in _INTENTS_REQUIRING_COMPILE:
            # There is freshly reviewed code that hasn't been compiled yet.
            if needs_compile and compiler_attempts < _MAX_COMPILER_ATTEMPTS:
                return "compiler"
            # Last compile failed and we still have attempts → coder fixes it,
            # which will set needs_compile=True again for the next round.
            if compile_failed and compiler_attempts < _MAX_COMPILER_ATTEMPTS:
                return "coder"
            return "__end__"
        return "__end__"

    # ── Unknown intent → end safely, but loudly: a new C# intent that slips
    # through here is a routing bug that should be visible, not silently swallowed.
    logger.warning(f"[SUPERVISOR] unknown intent={intent!r}; no route, ending safely")
    return "__end__"


def _recent_context_messages(
    existing_messages: list,
    context_window: int,
    hard_cap: int = 8,
) -> list:
    """Pick the recent non-system messages to carry into a sub-agent call,
    bounded by BOTH a message-count cap and the model's token budget.

    Replaces the old bare ``existing_messages[-8:]`` slices: those bounded only
    by count, so a few huge messages (long SCL blocks / tool dumps) could still
    blow the window. We first take the last ``hard_cap`` non-system messages,
    then run them through ``trim_messages`` against the real context window.
    """
    recent = [m for m in existing_messages[-hard_cap:]
              if not (hasattr(m, "type") and m.type == "system")]
    if not recent:
        return recent
    # trim_messages keeps system msgs + most-recent within budget; there are no
    # system msgs here, so it purely trims to the token budget.
    return trim_messages(recent, context_window=(context_window or None))


# Human-friendly routing banners.
_ROUTE_BANNERS = {
    "coder": "🔄 Routing to Coder Agent...",
    "reviewer": "🔍 Routing to Reviewer Agent...",
    "compiler": "🔨 Routing to Compiler Agent...",
    "chat": "💬 Routing to Chat Agent...",
}


def _supervisor_node(state: MultiAgentState, llm: ChatOpenAI) -> dict:
    """Supervisor routes to the next node using a DETERMINISTIC state machine.

    ``llm`` is intentionally unused here — routing no longer depends on the LLM.
    It's kept in the signature so the graph wiring (which binds the same llm to
    every node) stays uniform.
    """
    next_node = _decide_next(state)
    rounds = int(state.get("coder_reviewer_rounds", 0) or 0)
    compiler_attempts = int(state.get("compiler_attempts", 0) or 0)
    logger.info(
        f"[SUPERVISOR] intent={state.get('intent')} rounds={rounds} "
        f"compiler_attempts={compiler_attempts} review_passed={state.get('review_passed')} "
        f"→ next={next_node}"
    )

    if next_node in _ROUTE_BANNERS:
        return {
            "current_agent": next_node,
            "messages": [AIMessage(content=_ROUTE_BANNERS[next_node])],
        }

    # __end__ : synthesize a short completion summary from state (no LLM call).
    intent = state.get("intent", "")
    if intent in _ANALYZE_INTENTS:
        # Read-only analysis has no coder<->reviewer fix loop: the reviewer runs once and its
        # answer IS the analysis itself, stored in review_result (not in "REVIEW: PASS" format).
        # Surface that as the final answer — otherwise only stray on_chat_model_stream fragments
        # leak through and the user sees a truncated reply.
        answer = state.get("review_result") or state.get("final_answer") or ""
        if not answer.strip():
            # Never end silently: on_chain_end only yields a "content" event when
            # final_answer is non-empty, so an empty answer would leave the user
            # staring at a blank reply. Emit an honest, actionable fallback.
            answer = (
                "No valid analysis result was produced; retries stopped. Possible causes: incomplete "
                "block data, oversized context, or the model returned no body. Narrow the scope "
                "(specify a block/variable name) and retry."
            )
        elif any(m in answer.lower() for m in _TOOL_LIMIT_ABORT_MARKERS):
            # The relay aborted the run at its tool-call cap and handed back its
            # own jargon as the "answer". Translate it for the user.
            answer = (
                "Analysis stopped because the tool-call limit was reached; no complete conclusion "
                "was produced. Narrow the scope (specify a block/variable name, or ask about a single "
                "network) and retry."
            )
        return {
            "current_agent": "__end__",
            "final_answer": answer,
            "status": "completed",
            "messages": [],
        }

    if state.get("review_result") and not state.get("review_passed", False):
        summary = "Completed with remaining review notes (fix rounds exhausted)."
    elif state.get("coder_output"):
        summary = "Task completed."
    else:
        summary = "Done."
    return {
        "current_agent": "__end__",
        "final_answer": state.get("final_answer") or summary,
        "status": "completed",
        "messages": [AIMessage(content=state.get("final_answer") or summary)],
    }


_IMPORT_TOOL_NAMES = frozenset({"import_block"})
_USER_INPUT_MARKER = "USER_INPUT_REQUIRED:"
_SCL_DECL_RE = re.compile(
    r"(?m)^\s*(?:FUNCTION_BLOCK|ORGANIZATION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\b"
)


def _user_input_question(text: str) -> str:
    """Return the clarification text after the guard marker, or "" when absent."""
    stripped = (text or "").lstrip()
    if not stripped.startswith(_USER_INPUT_MARKER):
        return ""
    return stripped[len(_USER_INPUT_MARKER):].strip()


def _meta_call_payload(call: Any) -> Tuple[str, dict]:
    """Unwrap one LangChain tool call into ``(real_tool_name, arguments)``.

    The coder is bound to the ``invoke_tool`` meta-tool only, so the real tool name and
    its arguments live inside ``args_json``. Direct tool calls are returned unchanged.
    """
    if isinstance(call, dict):
        name = str(call.get("name") or "")
        args = call.get("args") or {}
    else:
        name = str(getattr(call, "name", "") or "")
        args = getattr(call, "args", {}) or {}
    if not isinstance(args, dict):
        return name, {}
    if name != "invoke_tool":
        return name, args

    inner = str(args.get("name") or "")
    raw = args.get("args_json")
    if isinstance(raw, dict):
        return inner, raw
    if isinstance(raw, str) and raw.strip():
        try:
            parsed = json.loads(raw)
            if isinstance(parsed, dict):
                return inner, parsed
        except (ValueError, TypeError):
            pass
    return inner, {}


def _extract_written_scl(messages: List[BaseMessage]) -> str:
    """Return the SCL payload the coder actually sent to an import tool.

    In write mode the coder usually passes the complete SCL to ``import_block`` inside
    the tool call instead of emitting a fenced block in its final chat message. Its last
    message is then only a narrative summary ("done, compile clean"), which is useless
    to the reviewer. Capture the payload from the tool call so the reviewer reviews the
    artifact that was written, not the summary.
    """
    for msg in reversed(messages or []):
        for call in reversed(getattr(msg, "tool_calls", None) or []):
            name, args = _meta_call_payload(call)
            if name in _IMPORT_TOOL_NAMES:
                code = args.get("code")
                if isinstance(code, str) and code.strip():
                    return code

        # Only model-authored text can carry the artifact; tool results often contain
        # fenced SCL from the project and must not be mistaken for this round's code.
        # Require a real fence AND a block declaration: _extract_scl_block falls back
        # to the whole message, so a plain summary would otherwise be returned.
        content = getattr(msg, "content", "")
        if isinstance(msg, AIMessage) and isinstance(content, str) and "```" in content:
            try:
                from agent.executor import _extract_scl_block
                fenced = _extract_scl_block(content)
            except Exception:  # noqa: BLE001 - absence of a fence is normal
                fenced = ""
            if fenced and _SCL_DECL_RE.search(fenced):
                return fenced
    return ""


def _write_refusal(messages: List[BaseMessage]) -> str:
    """Return the refusal token when a gated write in ``messages`` was not executed.

    A refused write returns ``approval_timeout`` / ``user_denied`` / ... instead of a
    result, so there is no code to review. Feeding that back into the reviewer loop only
    burns rounds (and reviews the wrong artifact).
    """
    for msg in messages or []:
        if not isinstance(msg, ToolMessage):
            continue
        text = (msg.content if isinstance(msg.content, str) else str(msg.content or "")).lstrip().lower()
        for token in _WRITE_REFUSAL_TOKENS:
            if text.startswith(token):
                return token
    return ""


def _refusal_answer(token: str) -> str:
    """User-facing answer when a gated write was refused (nothing was written)."""
    if token == "approval_timeout":
        return (
            "The write was NOT executed: the approval card timed out (after the retry "
            "limit) and nothing was changed. The prepared change is shown above — send the "
            "request again when you can approve it, or tell me what to adjust."
        )
    if token == "user_denied":
        return (
            "The write was NOT executed: you declined the approval card, so nothing was "
            "changed. The prepared change is shown above — tell me how you want to proceed."
        )
    if token == "approval_unavailable":
        return (
            "The write was NOT executed: the approval UI was unavailable, so nothing was "
            "changed. The prepared change is shown above — retry when the card can be shown."
        )
    return "The write was NOT executed: the run was cancelled before approval, so nothing was changed."


async def _coder_node(state: MultiAgentState, llm: ChatOpenAI) -> dict:
    """Run the Coder agent as a subgraph, reusing previous context.

    On first call: starts fresh with the user's task.
    On subsequent calls (review feedback): sees previous coder output
    and reviewer feedback from shared state messages.
    """
    user_prompt = state.get("user_prompt", "")
    context = state.get("context", "")
    review_result = state.get("review_result", "")
    images = state.get("images", []) or []
    existing_messages = list(state.get("messages", []))
    compile_error_summary = state.get("compile_error_summary", "") or ""

    # Is this a fix round (coder called again after a review), or the first pass?
    is_fix_round = bool(review_result) and not state.get("review_passed", True)

    task = user_prompt
    if is_fix_round:
        task = f"Fix the following issues in the previously generated code:\n\n{review_result}\n\nOriginal request: {user_prompt}"
    elif compile_error_summary:
        # Compile-fix round: pass the actual compile errors (previously dropped on
        # this path) plus any deterministically recalled repair experiences.
        task = f"The previous code failed to compile. Fix the compilation errors:\n\n{compile_error_summary}"
        repair_hints = state.get("repair_hints", "") or ""
        if repair_hints:
            task += (
                f"\n\nRepair hints from past similar errors (advisory — verify against the live project):\n"
                f"{repair_hints}"
            )
        task += f"\n\nOriginal request: {user_prompt}"

    # Approval-timeout reflect round: the gated write was refused, so re-think the change
    # instead of repeating it verbatim (a too-large / unclear write is what timed out).
    reflect_rounds = int(state.get("approval_reflect_rounds", 0) or 0)
    if reflect_rounds and not is_fix_round and not compile_error_summary:
        task = (
            "The previous write was NOT executed: the approval card timed out, so nothing "
            "was written. Reflect on why it was not approved (the change was too large, "
            "unclear, or hard to judge) and regenerate a smaller, reviewable version: same "
            "target block and interface, minimal diff, and state what changes in the design "
            "section before the write call. Do not repeat the previous payload unchanged.\n\n"
            f"Original request: {user_prompt}"
        )

    user_message = ""
    if state.get("is_automated"):
        user_message = (
            "Automated batch generation (no interactive user available): do NOT stop to ask for "
            "clarification — pick reasonable defaults, note assumptions in `## Design`, and "
            "generate the block directly.\n\n"
        )
    user_message += f"Task: {task}"
    if context:
        user_message += f"\n\nProject Context: {context}"

    # Reuse recent conversation context so the coder remembers its previous
    # output and reviewer feedback when called for fixes. Bounded by both a
    # message-count cap and the model's real token budget.
    input_messages: list = _recent_context_messages(
        existing_messages, state.get("context_window", 0)
    )
    # Attach images ONLY on the first coding pass. On fix rounds the images are
    # already present earlier in the shared conversation (existing_messages), so
    # re-attaching them every round would duplicate potentially large image
    # payloads and waste context/tokens.
    turn_images = [] if (is_fix_round or compile_error_summary) else images
    input_messages.append(HumanMessage(content=build_user_content(user_message, turn_images)))

    agent = create_coder_agent(
        llm,
        tools=(state.get("toolsets") or {}).get("coder"),
        generate_only=(state.get("write_mode") == "deferred"),
        context_window=state.get("context_window", 0),
    )
    try:
        result = await _ainvoke_with_retry(lambda: agent.ainvoke({"messages": input_messages}), what="CODER")
        messages_out = result.get("messages", [])
        last_msg = messages_out[-1] if messages_out else None
        coder_output = last_msg.content if last_msg and hasattr(last_msg, "content") else ""

        # The final message is usually a narrative summary; the SCL actually written lives
        # in the import_block tool call. Only scan messages after this round's task message
        # so a previous round's payload can never be mistaken for the current one.
        round_start = 0
        for _i in range(len(messages_out) - 1, -1, -1):
            if isinstance(messages_out[_i], HumanMessage):
                round_start = _i + 1
                break
        coder_code = _extract_written_scl(messages_out[round_start:])
        if coder_code:
            logger.info(f"[CODER] captured {len(coder_code)} chars of SCL from the import call")
        else:
            logger.warning("[CODER] no SCL payload captured from the write call; reviewer will fall back to the final message")

        # The gated write was refused (approval timeout / denied / cancelled): nothing was
        # written, so there is no artifact to review. End the run (or reflect once).
        refusal = _write_refusal(messages_out[round_start:])
        if refusal:
            logger.warning(f"[CODER] gated write refused: {refusal}")
            done_reflect_rounds = int(state.get("approval_reflect_rounds", 0) or 0)
            if refusal == "approval_timeout" and done_reflect_rounds < _MAX_APPROVAL_REFLECT_ROUNDS:
                return {
                    "coder_output": "",
                    "coder_code": "",
                    "coder_failed": False,
                    "approval_reflect_rounds": done_reflect_rounds + 1,
                    "review_result": "",
                    "review_passed": False,
                    "compile_failed": False,
                    "needs_compile": False,
                    "compile_error_summary": "",
                    "repair_hints": "",
                    "messages": messages_out,
                }
            return {
                "coder_output": coder_output,
                "coder_code": coder_code,
                "coder_failed": False,
                "needs_user_input": True,
                "final_answer": _refusal_answer(refusal),
                "status": "completed",
                "messages": messages_out,
                "review_result": "",
                "review_passed": False,
                "compile_failed": False,
                "needs_compile": False,
                "compile_error_summary": "",
                "repair_hints": "",
            }

        logger.info(f"[CODER] Generated output: {len(coder_output)} chars (context msgs: {len(input_messages)})\n===== CODER OUTPUT BEGIN =====\n{coder_output[:2000]}\n===== CODER OUTPUT END =====")

        # The repeat guard stopped the agent and asked the user for the missing detail.
        _question = _user_input_question(coder_output)
        if _question:
            logger.info(f"[CODER] stopped for user input: {_question[:300]}")
            return {
                "coder_output": coder_output,
                "coder_code": coder_code,
                "coder_failed": False,
                "needs_user_input": True,
                "final_answer": _question,
                "status": "completed",
                "messages": messages_out,
                "review_result": "",
                "review_passed": False,
                "compile_failed": False,
                "needs_compile": False,
                "compile_error_summary": "",
                "repair_hints": "",
            }

        # A call-limit middleware (Model/ToolCallLimit) can abort the ReAct loop and
        # hand back its own jargon as the "output" (e.g. "Model call limits exceeded").
        # Treat that as a real generation failure instead of forwarding the placeholder
        # into the reviewer/compiler chain.
        if any(m in (coder_output or "").lower() for m in _TOOL_LIMIT_ABORT_MARKERS):
            logger.warning("[CODER] aborted by call-limit middleware; marking task failed")
            return {
                "coder_output": "",
                "coder_code": "",
                "coder_failed": True,
                "review_result": "",
                "review_passed": False,
                "compile_failed": False,
                "needs_compile": False,
                "compile_error_summary": "",
                "repair_hints": "",
                "final_answer": (
                    "Code generation was cut off by the model/tool call limit before any SCL "
                    "was produced. The block requirements were likely incomplete; re-plan with "
                    "explicit members/fields, then retry."
                ),
                "status": "error",
                "messages": [AIMessage(content=coder_output)],
            }

        # Clear the previous round's review verdict so the deterministic router
        # sends the fresh code back to the reviewer instead of treating the old
        # review as still valid. Also clear the compile-failure flag and the
        # compile-fix artifacts: the code changed, so they no longer apply until
        # the next compile failure.
        return {
            "coder_output": coder_output,
            "coder_code": coder_code,
            "review_result": "",
            "review_passed": False,
            "compile_failed": False,
            "needs_compile": True,  # fresh code awaits review + compile
            "compile_error_summary": "",
            "repair_hints": "",
            "messages": messages_out,
        }
    except Exception as e:
        logger.exception(f"[CODER] failed: {e}")
        return {
            "coder_output": f"// Coder error: {e}",
            "coder_code": "",
            "review_result": "",
            "review_passed": False,
            "compile_failed": False,
            "needs_compile": False,
            "compile_error_summary": "",
            "repair_hints": "",
            "messages": [AIMessage(content=f"Coder error: {e}")],
        }


async def _reviewer_node(state: MultiAgentState, llm: ChatOpenAI) -> dict:
    """Run the Reviewer agent as a subgraph, reusing previous context.

    Sees the coder's output (passed in state.coder_output as text) plus
    any previous review rounds from shared state messages.
    """
    coder_output = state.get("coder_output", "") or ""
    coder_code = state.get("coder_code", "") or ""
    user_prompt = state.get("user_prompt", "")
    context = state.get("context", "") or ""
    existing_messages = list(state.get("messages", []))

    # Read-only analysis (ExplainCode / Analyze / TraceVariable) never runs the coder, so there
    # is no generated SCL. Previously the reviewer was handed an EMPTY code fence and never saw
    # `state["context"]`, which is where the user's attached blocks/references live. It then
    # answered "no SCL provided" and had to re-fetch the block through tools on its own.
    analysis_mode = not coder_output.strip() and not coder_code.strip()
    if analysis_mode:
        review_task = (
            f"Analysis task (read-only): {user_prompt}\n\n"
            "The user attached the code to analyze in the Project Context below. It may be SCL "
            "source or a LAD/FBD structured JSON IR; analyze it directly."
        )
        if context:
            review_task += f"\n\nProject Context:\n{context}"
    else:
        # Review the artifact that was written, not the coder's closing narrative. In write
        # mode the SCL lives in the import_block tool call (captured as coder_code); passing
        # the summary instead forced the reviewer to re-export the whole block every round.
        if coder_code.strip():
            review_source = coder_code
            source_note = (
                "This is the exact SCL the coder passed to import_block. Review it directly; "
                "do NOT re-export or re-read the block unless the import result shows it failed."
            )
        else:
            review_source = coder_output
            source_note = (
                "The exact written payload was not captured; if the write result indicates a "
                "failed import, inspect the live block before judging the code."
            )
        # Deferred mode (planner SCL): the host imports only the fenced SCL block, so review exactly
        # that block. Handing the whole answer to the reviewer makes Design prose look like payload
        # contamination and burns review rounds on a defect the importer never sees.
        if state.get("write_mode") == "deferred":
            from agent.executor import _extract_scl_block
            review_source = _extract_scl_block(review_source) or review_source
        review_code = truncate_tool_result(review_source, max_chars=60000)
        review_task = (
            f"Review the following SCL code generated for: {user_prompt}\n\n"
            f"{source_note}\n\nCode:\n```scl\n{review_code}\n```"
        )

    # Reuse recent context so the reviewer sees previous review rounds, bounded
    # by message count + the model's token budget.
    input_messages: list = _recent_context_messages(
        existing_messages, state.get("context_window", 0), hard_cap=6
    )
    input_messages.append(HumanMessage(content=review_task))

    agent = create_reviewer_agent(
        llm,
        tools=(state.get("toolsets") or {}).get("reviewer"),
        analysis=analysis_mode,
        context_window=state.get("context_window", 0),
    )
    try:
        result = await _ainvoke_with_retry(lambda: agent.ainvoke({"messages": input_messages}), what="REVIEWER")
        messages_out = result.get("messages", [])
        last_msg = messages_out[-1] if messages_out else None
        review_text = last_msg.content if last_msg and hasattr(last_msg, "content") else ""

        # The repeat guard stopped the reviewer and asked the user for the missing detail.
        _question = _user_input_question(review_text)
        if _question:
            logger.info(f"[REVIEWER] stopped for user input: {_question[:300]}")
            return {
                "review_result": _question,
                "review_passed": False,
                "needs_user_input": True,
                "final_answer": _question,
                "coder_reviewer_rounds": state.get("coder_reviewer_rounds", 0) + 1,
                "messages": messages_out,
            }

        # Parse review verdict
        review_passed = "REVIEW: PASS" in review_text.upper() or "REVIEW:PASS" in review_text.upper().replace(" ", "")

        logger.info(f"[REVIEWER] Review: {'PASS' if review_passed else 'NEEDS_FIX'} ({len(review_text)} chars): {review_text[:800]!r}")
        return {
            "review_result": review_text,
            "review_passed": review_passed,
            "coder_reviewer_rounds": state.get("coder_reviewer_rounds", 0) + 1,
            "messages": messages_out,
        }
    except Exception as e:
        logger.exception(f"[REVIEWER] failed: {e}")
        if _is_rate_limit_error(e):
            # Retries are already exhausted and the model service is throttling us: there is no
            # verdict to route on. End the round with an actionable message instead of handing
            # the coder a fake "Review error" and asking it to fix nothing.
            return {
                "review_result": _RATE_LIMIT_MESSAGE,
                "review_passed": False,
                "needs_user_input": True,
                "final_answer": _RATE_LIMIT_MESSAGE,
                "coder_reviewer_rounds": state.get("coder_reviewer_rounds", 0) + 1,
                "messages": [AIMessage(content=_RATE_LIMIT_MESSAGE)],
            }
        # Do NOT fake a PASS on error — that would silently release un-reviewed
        # code. Record it as a failed review; the deterministic router bounds the
        # coder<->reviewer loop via _MAX_CODER_REVIEWER_ROUNDS, so a persistently
        # failing reviewer can't spin forever. review_passed stays False.
        return {
            "review_result": f"Review error (treated as not passed): {e}",
            "review_passed": False,
            "coder_reviewer_rounds": state.get("coder_reviewer_rounds", 0) + 1,
            "messages": [AIMessage(content=f"Reviewer error: {e}")],
        }


def _compile_structured_failed(result_text: str) -> Optional[bool]:
    """Try to decide failure from a **structured** compile result.

    Returns True/False when ``result_text`` parses as the structured compile
    envelope (``{"success":bool,"error_count":int,...}``) produced by the
    ``compile`` tool (structured fields). Returns ``None`` when the text is not
    structured JSON, so the caller can fall back to the text heuristic.

    Transport-level errors (tools return "Error:" / "TIA tool error") are not
    JSON and yield ``None`` here → handled by the text path.
    """
    if not result_text:
        return None
    t = result_text.strip()
    if not t.startswith("{"):
        return None
    try:
        data = json.loads(t)
    except (ValueError, TypeError):
        return None
    if not isinstance(data, dict):
        return None
    # Prefer explicit success flag; fall back to error_count when absent.
    if "success" in data:
        return not bool(data.get("success"))
    if "error_count" in data:
        try:
            return int(data.get("error_count") or 0) > 0
        except (ValueError, TypeError):
            return None
    return None


def _compile_result_failed(result_text: str) -> bool:
    """Decide whether a compile result indicates failure.

    Prefers the structured verdict (``_compile_structured_failed``) when the
    result is the structured JSON envelope; otherwise falls back to the text
    heuristic below.

    TIA tools return a string starting with "Error:" / "TIA tool error"
    on transport/tool failure, otherwise the compiler's own report text. We treat
    it as failed if it's a tool-level error OR the report shows a non-zero error
    count / explicit failure marker.
    """
    structured = _compile_structured_failed(result_text)
    if structured is not None:
        return structured
    if not result_text:
        # No result at all — treat as failed so we don't claim false success.
        return True
    t = result_text.strip().lower()
    if t.startswith("error:") or t.startswith("tia tool error"):
        return True
    # Explicit failure markers commonly present in TIA compile reports.
    failure_markers = (
        "compilation failed", "compile failed", "编译失败",
        "error count: ", "errors: ",
    )
    for marker in failure_markers:
        idx = t.find(marker)
        if idx >= 0:
            # If the marker is followed by a number, only fail on a non-zero count.
            tail = t[idx + len(marker):idx + len(marker) + 8].strip()
            num = ""
            for ch in tail:
                if ch.isdigit():
                    num += ch
                else:
                    break
            if num == "":
                return True  # marker with no parseable count → treat as failure
            if int(num) > 0:
                return True
    return False


# Noise to strip from compile error signatures: line/column/position numbers, file paths, timestamps, etc.
_SIG_NOISE = re.compile(
    r"(\b(?:line|row|col(?:umn)?|pos(?:ition)?)\s*[:=]?\s*\d+)|(\d+\s*[:.,]\s*\d+)|([A-Za-z]:[\\/][^\s'\"]+)",
    re.I,
)
_SIG_WS = re.compile(r"\s+")


def _norm_sig_text(text: str) -> str:
    """Strip line/column/path noise from an error message and collapse whitespace, returning a compact, reproducible signature fragment."""
    t = _SIG_NOISE.sub(" ", text or "")
    return _SIG_WS.sub(" ", t).strip()[:400]


def _ask_user_attribution(errors: List[Dict[str, Any]], suspects: List[str]) -> Optional[Dict[str, Any]]:
    """Ask the user which object owns compile errors TIA reported without a block name.

    Blocking HTTP call into the C# :8001 server (Input interaction card), so it is always
    invoked from a worker thread. Returns ``{"answer": ...}`` or None when the user did not
    answer / no UI is available; the caller then keeps the ordinary failure path.
    """
    try:
        from tools.tia_client import ask_user

        messages = [str((e or {}).get("message") or "").strip() for e in (errors or [])][:5]
        messages = [m for m in messages if m]
        detail = "TIA compile errors that name no block:\n" + "\n".join("- " + m for m in messages)
        if suspects:
            detail += "\n\nCandidates found in the project: " + ", ".join(str(s) for s in suspects[:8])
        raw = ask_user(
            "有编译错误无法定位到具体对象，请指明它属于哪个块或类型（留空可跳过）",
            detail,
            "例如：AlarmRecord（UDT）或 FB_Motor",
        )
        data = json.loads(raw) if raw and raw.strip().startswith("{") else None
        if not data or data.get("status") != "answered":
            logger.info(
                "[COMPILER] user attribution not supplied: "
                f"{(data or {}).get('status') or (raw or '')[:80]}"
            )
            return None
        answer = str(data.get("answer") or "").strip()
        return {"answer": answer} if answer else None
    except Exception as exc:  # noqa: BLE001 - asking is advisory, never fatal
        logger.warning(f"[COMPILER] ask_user unavailable: {type(exc).__name__}: {exc}")
        return None


def normalize_compile_error_signature(result_text: str) -> str:
    """Normalize compile error signatures (corresponding to design section 4.8 NormalizeCompileErrorSignature).

    Structured results carry an ``errors[]`` list of {block, message, code?}; we
    fold each entry's code+message into a noise-stripped signature (Tier A exact
    match). free-text results are normalized the same way and truncated (Tier B
    keyword match). Returns "" when nothing useful is found.
    """
    if not result_text:
        return ""
    t = result_text.strip()
    if t.startswith("{"):
        try:
            data = json.loads(t)
        except (ValueError, TypeError):
            data = None
        if isinstance(data, dict):
            errors = data.get("errors") or []
            sigs = []
            for e in errors[:5]:
                if not isinstance(e, dict):
                    continue
                code = str(e.get("code") or "").strip()
                msg = str(e.get("message") or "").strip()
                seg = _norm_sig_text(f"{code} {msg}" if code else msg)
                if seg:
                    sigs.append(seg)
            if sigs:
                return "; ".join(sigs)[:800]
            # when there is a structured envelope but no errors[], fall back to normalizing the whole text.
            return _norm_sig_text(json.dumps(data, ensure_ascii=False))
    return _norm_sig_text(t)


# structural precheck (4.8): only a cheap block-level integrity check.
_SCL_BLOCK_RE = re.compile(
    r"\b(FUNCTION_BLOCK|ORGANIZATION_BLOCK|DATA_BLOCK|FUNCTION|TYPE)\b", re.I
)


def _scl_structure_precheck(scl_text: str) -> str:
    """Fast structural precheck before compiling (equivalent to the C# SclStructureParser integrity check).

    Only checks that the block type is recognizable and BEGIN / END_xxx are closed; on hit returns an error description (skipping the real compile),
    while an empty string means the structure is basically complete. DB/UDT have no mandatory BEGIN, only END closure is required. Does not replace the real compile.
    """
    s = (scl_text or "").strip()
    if not s:
        return ""
    m = _SCL_BLOCK_RE.search(s)
    if not m:
        return "Cannot determine block type (missing FUNCTION_BLOCK / DATA_BLOCK / FUNCTION / TYPE declaration)"
    btype = m.group(1).upper()
    kind = {
        "FUNCTION_BLOCK": "FB",
        "ORGANIZATION_BLOCK": "OB",
        "DATA_BLOCK": "DB",
        "FUNCTION": "FC",
        "TYPE": "UDT",
    }[btype]

    if kind in ("DB", "UDT"):
        if not re.search(rf"\bEND_{btype}\b", s, re.I):
            return f"{btype} is missing the END_{btype} terminator"
        return ""

    if not re.search(r"\bBEGIN\b", s, re.I):
        return f"{btype} is missing the BEGIN statement"
    if not re.search(rf"\bEND_{btype}\b", s, re.I):
        return f"{btype} is missing the END_{btype} terminator"
    return ""


def _recall_repair_hints(symptom: str, limit: int = 3) -> Tuple[str, List[str]]:
    """Deterministically recall past repair experiences for a compile error.

    Returns ``(hints_text, signatures)`` where ``signatures`` are the normalized
    keys of the experiences actually recalled — used later to feed the evaluation
    loop (success/failure counting) once the fix round resolves.

    Fail-closed: any error (unreachable service, bad response) returns ("", []) so
    the recall never blocks the fix loop. Blank hints are simply omitted by the coder.
    """
    if not symptom:
        return "", []
    try:
        from tools.tia_client import query_repair_experience
        resp = query_repair_experience(symptom, limit)
        low = (resp or "").lstrip().lower()
        if low.startswith("error:") or low.startswith("tia tool error"):
            return "", []
        data = json.loads(resp) if resp else []
        if not isinstance(data, list) or not data:
            return "", []
        lines = []
        signatures = []
        for e in data[:limit]:
            if not isinstance(e, dict):
                continue
            sig = str(e.get("signature") or "").strip()
            causal = str(e.get("causal_text") or "")
            if causal:
                # causal_text is a JSON string of {symptom, root_cause, fix_pattern, ...}.
                try:
                    obj = json.loads(causal)
                except (ValueError, TypeError):
                    obj = None
                if isinstance(obj, dict):
                    seg = []
                    if obj.get("symptom"):
                        seg.append(f"symptom: {obj.get('symptom')}")
                    if obj.get("root_cause"):
                        seg.append(f"root cause: {obj.get('root_cause')}")
                    if obj.get("fix_pattern"):
                        seg.append(f"fix: {obj.get('fix_pattern')}")
                    if seg:
                        lines.append("\n".join(seg))
                        if sig:
                            signatures.append(sig)
                        continue
                lines.append(causal[:600])
            else:
                sym = str(e.get("symptom") or "")
                if sym:
                    lines.append(f"symptom: {sym}")
                else:
                    continue
            if sig:
                signatures.append(sig)
        return "\n---\n".join(lines)[:1600], signatures
    except Exception as e:
        logger.warning(f"[SUPERVISOR] repair recall failed: {e}")
        return "", []


def _feedback_repair_experience(signatures: List[str], success: bool) -> None:
    """Feed a fix-round outcome back to the repair-experience store.

    Fail-closed and fire-and-forget: any error (unreachable service) is logged and
    dropped — the feedback loop must never block or alarm the main generation path.
    """
    if not signatures:
        return
    try:
        from tools.tia_client import feedback_repair_experience
        feedback_repair_experience(signatures, success)
    except Exception as e:
        logger.warning(f"[SUPERVISOR] repair feedback failed: {e}")


async def _compiler_node(state: MultiAgentState, llm: ChatOpenAI) -> dict:
    """Compile the project and record success/failure into state.

    This node does NOT decide what happens next — the deterministic router
    (`_decide_next`) reads ``compile_failed`` + ``compiler_attempts`` and sends
    the run back to the coder for another fix round when appropriate, up to
    ``_MAX_COMPILER_ATTEMPTS``. Compilation itself is deterministic, so no LLM
    reasoning is needed here.

    The real project compile now runs here (after a structural precheck): it is the only
    signal for type / CPU-family errors such as "Data type DTL is unknown." that never
    name a block. Block-less failures are repaired deterministically (platform matrix +
    the last indexed snapshot, agent/type_repair.py) and compiled again; named errors are
    reported to the coder through ``compile_error_summary`` for a normal fix round.
    """
    attempts = state.get("compiler_attempts", 0) + 1

    try:
        # Structural precheck (4.8): a cheap block-level integrity check. In deferred mode
        # (planner SCL) ``coder_output`` is the raw SCL, so we always run the precheck on it —
        # including after a PASS — because there is no real wrote-then-re-read block to rely on.
        # In the normal (single-block) flow the coder already wrote the block and its final
        # message is only a narrative, so a PASS skips the precheck (it would false-fail on
        # "missing BEGIN").
        review_passed = bool(state.get("review_passed", False))
        deferred = state.get("write_mode") == "deferred"
        precheck_err = None
        if deferred:
            # In deferred mode only the fenced SCL block is imported later, so the precheck must
            # validate exactly that block — scanning the whole answer lets a "TYPE ..." mention in
            # the Design prose false-fail with "TYPE is missing the END_TYPE terminator".
            source_for_precheck = state.get("coder_output") or ""
            from agent.executor import _extract_scl_block
            source_for_precheck = _extract_scl_block(source_for_precheck) or source_for_precheck
            precheck_err = _scl_structure_precheck(source_for_precheck)
        elif not review_passed:
            precheck_err = _scl_structure_precheck(state.get("coder_output") or "")

        # Block-less new errors (no subject) plus the index-probed candidates, when the real
        # compile reported any. Only this list can justify asking the user anything.
        unattributed: List[Dict[str, Any]] = []
        suspects: List[str] = []

        if precheck_err:
            logger.info(f"[COMPILER] structure precheck failed: {precheck_err}")
            result_text = json.dumps(
                {"success": False, "errors": [{"block": "Unknown", "message": precheck_err}]},
                ensure_ascii=False,
            )
            failed = True
        elif deferred:
            # Nothing is written yet in deferred mode, so there is nothing to compile.
            result_text = json.dumps(
                {"success": True, "errors": [], "skipped": "deferred write; compile runs after import"},
                ensure_ascii=False,
            )
            failed = False
        else:
            # Real project compile: the only signal for type / CPU-family errors that the
            # structural precheck cannot see (e.g. "Data type DTL is unknown."). A block-less
            # failure is repaired deterministically from the platform matrix + the last
            # indexed snapshot and compiled again (agent/type_repair.py).
            from agent.type_repair import auto_compile_and_repair

            outcome = await asyncio.to_thread(
                auto_compile_and_repair,
                None,
                (state.get("environment") or {}).get("plc_family"),
            )
            if outcome.get("unavailable"):
                result_text = json.dumps(
                    {"success": True, "errors": [],
                     "skipped": "real compile unavailable: " + str(outcome["unavailable"])[:200]},
                    ensure_ascii=False,
                )
                failed = False
            else:
                payload = {"success": bool(outcome["success"]), "errors": outcome.get("errors") or []}
                if outcome.get("repairs"):
                    payload["repaired_types"] = outcome["repairs"]
                if outcome.get("skipped"):
                    payload["unrepaired"] = outcome["skipped"]
                result_text = json.dumps(payload, ensure_ascii=False)
                failed = not bool(outcome["success"])
                unattributed = outcome.get("unattributed") or []
                suspects = outcome.get("suspects") or []

        logger.info(
            f"[COMPILER] attempt {attempts}/{_MAX_COMPILER_ATTEMPTS} "
            f"failed={failed}: {result_text[:200]}"
        )

        # Whether the run ends now depends on the router. We only mark the run as
        # completed when it either passed, or we've exhausted compile attempts.
        exhausted = attempts >= _MAX_COMPILER_ATTEMPTS

        # No subject → the ONLY case a human is asked for help. Everything the compile named
        # stays on the automatic path. An automated batch run has no interactive user, and one
        # ask per run is enough (the answer then feeds every later fix round).
        user_note = ""
        asked = False
        if (failed and unattributed and not exhausted
                and not state.get("is_automated") and not state.get("compile_user_asked")):
            asked = True
            attribution = await asyncio.to_thread(_ask_user_attribution, unattributed, suspects)
            if attribution:
                user_note = "User says these errors belong to: " + attribution["answer"]
                logger.info(f"[COMPILER] user attribution: {attribution['answer'][:200]}")

        # Recall + evaluation feedback: on failure with a fix round ahead, capture the
        # error signature and deterministically pull past repair experiences for the
        # coder's next pass. When the round then resolves (pass) or exhausts (give up),
        # feed that outcome back to the store so recall quality is gated by real
        # success/failure counts. All feedback is fail-closed / fire-and-forget.
        recalled = list(state.get("recalled_signatures") or [])
        compile_error_summary = ""
        repair_hints = ""
        new_signatures: List[str] = []
        if failed:
            # Sync TIA client helpers run in a worker thread so the async node never
            # blocks the graph's event loop.
            if not exhausted:
                compile_error_summary = normalize_compile_error_signature(result_text)
                if compile_error_summary:
                    # Tag family-specific failures (e.g. DTL on S7-300/400) so the repair hint
                    # and the recalled experience match the actual CPU.
                    from agent.environment import annotate_compile_error
                    compile_error_summary = annotate_compile_error(
                        compile_error_summary,
                        (state.get("environment") or {}).get("plc_family"),
                    )
                if user_note:
                    # The coder must see the user's attribution in the same fix round.
                    compile_error_summary = (
                        f"{compile_error_summary}\n\n{user_note}" if compile_error_summary else user_note
                    )
                if compile_error_summary:
                    repair_hints, new_signatures = await asyncio.to_thread(
                        _recall_repair_hints, compile_error_summary
                    )
            else:
                await asyncio.to_thread(_feedback_repair_experience, recalled, success=False)
        else:
            if recalled:
                await asyncio.to_thread(_feedback_repair_experience, recalled, success=True)

        completed = (not failed) or exhausted

        banner = (
            f"🔨 Compilation attempt {attempts}/{_MAX_COMPILER_ATTEMPTS}: "
            + ("✅ passed" if not failed else ("❌ failed (giving up)" if exhausted else "❌ failed, retrying"))
        )
        out = {
            "compiler_attempts": attempts,
            "compile_failed": failed,
            "needs_compile": False,  # this code has now been compiled
            "compile_error_summary": compile_error_summary,
            "repair_hints": repair_hints,
            "recalled_signatures": new_signatures,
            "messages": [AIMessage(content=f"{banner}\n{result_text[:500]}")],
        }
        if asked:
            out["compile_user_asked"] = True
        if completed:
            out["status"] = "completed"
            out["final_answer"] = f"Compilation result (attempt {attempts}):\n{result_text}"
        return out
    except Exception as e:
        logger.exception(f"[COMPILER] failed: {e}")
        # Infrastructure error (not a code error) — end rather than loop.
        return {
            "compiler_attempts": attempts,
            "compile_failed": True,
            "final_answer": f"Compilation error: {e}",
            "status": "completed",
            "messages": [AIMessage(content=f"Compilation error: {e}")],
        }


async def _chat_node(state: MultiAgentState, llm: ChatOpenAI) -> dict:
    """Run the Chat agent as a subgraph for conversational responses.

    Unlike the old approach where the supervisor answered directly via
    llm.invoke(), the chat agent uses streaming for natural output.
    """
    user_prompt = state.get("user_prompt", "")
    context = state.get("context", "")
    images = state.get("images", []) or []
    existing_messages = list(state.get("messages", []))

    # Build input messages with conversation history, bounded by message count +
    # the model's token budget.
    input_messages: list = _recent_context_messages(
        existing_messages, state.get("context_window", 0)
    )

    user_message = user_prompt
    if context:
        user_message += f"\n\nProject Context: {context}"
    input_messages.append(HumanMessage(content=build_user_content(user_message, images)))

    agent = create_chat_agent(
        llm,
        tools=(state.get("toolsets") or {}).get("chat"),
        context_window=state.get("context_window", 0),
    )
    try:
        result = await agent.ainvoke({"messages": input_messages})
        messages_out = result.get("messages", [])
        last_msg = messages_out[-1] if messages_out else None
        chat_output = last_msg.content if last_msg and hasattr(last_msg, "content") else ""
        _question = _user_input_question(chat_output)
        if _question:
            chat_output = _question

        logger.info(f"[CHAT] Generated output: {len(chat_output)} chars (context msgs: {len(input_messages)})")
        return {
            "final_answer": chat_output,
            "status": "completed",
            "messages": messages_out,
        }
    except Exception as e:
        logger.exception(f"[CHAT] failed: {e}")
        return {
            "final_answer": f"Chat error: {e}",
            "status": "completed",
            "messages": [AIMessage(content=f"Chat error: {e}")],
        }


def _route_after_supervisor(state: MultiAgentState) -> Literal["coder", "reviewer", "compiler", "chat", "__end__"]:
    """Route to the selected agent or end."""
    agent = state.get("current_agent", "__end__")
    if agent in ("coder", "reviewer", "compiler", "chat"):
        return agent
    return "__end__"


def _route_after_agent(state: MultiAgentState) -> Literal["supervisor", "__end__"]:
    """After an agent runs, go back to supervisor for next decision."""
    status = state.get("status", "")
    if status == "completed":
        return "__end__"
    return "supervisor"


# ── Graph builder ────────────────────────────────────────────

def _bind_async_node(fn, llm: ChatOpenAI):
    """Bind ``llm`` into an async node while keeping coroutine detection intact.

    LangGraph picks sync-vs-async node execution via iscoroutinefunction. A plain
    ``lambda s: fn(s, llm)`` around an async fn looks sync and would hand the graph
    a raw coroutine as the node output, so async nodes get a real async wrapper.
    """
    async def _node(state: MultiAgentState) -> dict:
        return await fn(state, llm)
    return _node


def _build_supervisor_graph(llm: ChatOpenAI):
    """Build the multi-agent StateGraph."""
    workflow = StateGraph(MultiAgentState)

    workflow.add_node("supervisor", lambda s: _supervisor_node(s, llm))
    workflow.add_node("coder", _bind_async_node(_coder_node, llm))
    workflow.add_node("reviewer", _bind_async_node(_reviewer_node, llm))
    workflow.add_node("compiler", _bind_async_node(_compiler_node, llm))
    workflow.add_node("chat", _bind_async_node(_chat_node, llm))

    workflow.set_entry_point("supervisor")

    workflow.add_conditional_edges("supervisor", _route_after_supervisor, {
        "coder": "coder",
        "reviewer": "reviewer",
        "compiler": "compiler",
        "chat": "chat",
        "__end__": END,
    })

    workflow.add_edge("coder", "supervisor")
    workflow.add_edge("reviewer", "supervisor")
    workflow.add_edge("compiler", "supervisor")
    # Chat agent: when completed, end; otherwise go back to supervisor
    workflow.add_conditional_edges("chat", _route_after_agent, {
        "supervisor": "supervisor",
        "__end__": END,
    })

    return workflow.compile()


# ── Public API ───────────────────────────────────────────────

def _answer_already_streamed(text: str, streamed: List[str]) -> bool:
    """True when ``text`` was already delivered token-by-token on this run.

    The analysis/review agent answers through the streaming model callback
    (``on_chat_model_stream``), and the supervisor later synthesizes the SAME text
    as ``final_answer`` at ``on_chain_end``. Emitting it again there made the
    final answer appear twice in the UI (the exact text was stored twice in the
    chat history). Compare against everything already streamed and skip the
    re-emit; only genuinely new text (synthesized summaries, compile reports,
    fallbacks) is still delivered.
    """
    candidate = (text or "").strip()
    if not candidate or not streamed:
        return False
    return candidate in "".join(streamed)


async def run_multi_agent_stream(
    llm: ChatOpenAI,
    prompt: str,
    context: str = "",
    intent: str = "GenerateCode",
    history: Optional[List[Dict[str, str]]] = None,
    planning_knowledge: str = "",
    images: Optional[List[str]] = None,
    context_window: Optional[int] = None,
    project_key: str = "",
    evolution_enabled: bool = False,
    model_cfg: Optional[dict] = None,
) -> AsyncIterator[Tuple[str, str]]:
    """Run the multi-agent supervisor workflow and yield SSE events.

    This is the main entry point for cloud LLM code generation.
    Local Ollama models never reach this function: they run on the C# ReActEngine,
    and the API layer rejects local endpoints (fail-closed).

    Args:
        llm: Configured ChatOpenAI instance for the cloud LLM.
        prompt: User's request.
        context: Project context string.
        intent: Intent classification (GenerateCode, ModifyCode, ExplainCode, etc.).
        history: Optional conversation history.
        planning_knowledge: Advisory prior-plan guidance (never treated as fact).
        images: Optional base64-encoded PNG images. Passed to the coder and chat
            agents so vision-capable models can reason over screenshots (e.g. a
            LAD diagram). The caller (C#) is responsible for only sending images
            to models it has determined support vision.

    Yields:
        ("thinking", message) — status updates
        ("content", delta)    — streaming output from agents
        ("tool_call", json)   — tool call events
        ("error", message)    — errors
        ("done", "ok")        — completion
    """
    # Assemble per-agent runtime toolsets once (MCP dynamic enumeration + local
    # tools). Failure degrades gracefully to local-only toolsets inside the
    # registry, so this never blocks the run.
    from tools.registry import build_agent_toolsets
    toolsets = await build_agent_toolsets()

    # Normalize unclassified intent to the full-capability flow (coder→reviewer→
    # compiler) instead of silently ending or defaulting to read-only Chat. The C#
    # write gate still governs whether writes actually happen.
    if not intent or intent == "Unknown":
        intent = "GenerateCode"

    # ── Chat intent: bypass the supervisor graph entirely ──
    # Chat is a direct conversational task that doesn't need routing.
    # The chat agent uses streaming internally, which would be lost if we
    # ran it through the graph (graph nodes use agent.ainvoke(), not astream).
    if intent == "Chat":
        yield "thinking", f"💬 Chat Agent starting..."
        async for event, delta in run_chat_stream(
            llm, prompt, toolsets.get("chat"),
            context=context, history=history, images=images,
            context_window=int(context_window or 0),
        ):
            yield event, delta
        yield "done", "ok"
        return

    # ── project-level long-task routing: Planner produces a DAG → Executor; if it can't decompose, fall back to single block ──
    if intent in ("GenerateCode", "ModifyCode") and _looks_like_project_generation(prompt):
        # resume: project_key hits a "paused awaiting approval" plan → continue from checkpoint, no re-planning
        if project_key:
            from agent.plan_store import PlanStore
            from agent.plan_models import PlanStatus
            resume_plan = PlanStore().find_unfinished(project_key)
            if resume_plan is not None and resume_plan.status == PlanStatus.PAUSED.value:
                yield "notice", notice(
                    "plan.resume",
                    f"Found unfinished plan {resume_plan.id}; resuming from the checkpoint",
                    resume_plan.id,
                )
                # adjust semantics: latest milestone decision is adjust → replan the unfinished part (keep success blocks).
                from agent.planner import replan_unfinished
                resume_plan = await replan_unfinished(
                    llm, resume_plan, prompt, context or "", planning_knowledge or ""
                )
                from agent.executor import run_project_plan_stream
                async for event, delta in run_project_plan_stream(
                    llm, resume_plan, context_window=context_window
                ):
                    yield event, delta
                return

        plan = await _try_plan_project(llm, prompt, context, project_key, planning_knowledge)
        if plan is not None and len(plan.tasks) >= 2:
            yield "notice", notice(
                "plan.detected",
                f"Project-level long task detected; planned {len(plan.tasks)} blocks",
                len(plan.tasks),
            )
            from agent.executor import run_project_plan_stream
            async for event, delta in run_project_plan_stream(
                llm, plan, context_window=context_window
            ):
                yield event, delta
            return
        logger.info("[supervisor] project planning declined; falling back to single-block flow")

    t0 = time.perf_counter()
    graph = _build_supervisor_graph(llm)

    # Sidecar: pre-fetch RAG/standards/repair/project in parallel with the graph
    # setup, so context is ready before the first agent turn (fail-closed).
    from agent.sidecar import run_sidecar, sidecar_context_block
    sidecar_task = asyncio.create_task(run_sidecar(prompt, context or "", project_key))

    # Build initial messages
    initial_messages: List[BaseMessage] = []
    if history:
        # Preserve the conversation-summary system entry (from the C# compaction)
        # regardless of the recent-exchange window, so long-history context isn't
        # lost after compression.
        for h in history:
            if str(h.get("role", "")).strip().lower() == "system":
                content = h.get("content", "")
                if content:
                    initial_messages.append(SystemMessage(content=content))
        for h in history[-5:]:  # Last 5 exchanges
            role = str(h.get("role", "")).strip().lower()
            content = h.get("content", "")
            if role == "user":
                initial_messages.append(HumanMessage(content=content))
            elif role == "assistant":
                initial_messages.append(AIMessage(content=content))
    # The current turn's user message carries images (multimodal) when present,
    # so the coder agent's first LLM call can see them.
    initial_messages.append(HumanMessage(content=build_user_content(prompt, images)))

    # Fold advisory planning knowledge into the context the agents see, clearly
    # labelled so it is never mistaken for verified project fact.
    effective_context = context or ""
    _pk = (planning_knowledge or "").strip()
    if _pk:
        effective_context = (
            "## Planning Knowledge (advisory only — verify against the live project)\n"
            + _pk
            + ("\n\n" + effective_context if effective_context else "")
        )

    # Await the sidecar (already running in parallel) and fold its pre-fetched
    # context into the agents' view. Fail-closed: any error leaves it empty.
    try:
        sidecar_block = sidecar_context_block(await sidecar_task)
    except Exception:
        sidecar_block = ""
    if sidecar_block:
        effective_context = (
            effective_context + "\n\n" + sidecar_block if effective_context else sidecar_block
        )

    initial_state: MultiAgentState = {
        "messages": initial_messages,
        "user_prompt": prompt,
        "context": effective_context,
        "intent": intent,
        "images": list(images or []),
        "current_agent": "",
        "coder_output": "",
        "coder_code": "",
        "needs_user_input": False,
        "approval_reflect_rounds": 0,
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
    }

    yield "thinking", f"🧠 Multi-Agent Supervisor starting (intent: {intent})..."

    # The supervisor runs a coder/reviewer/compiler workflow rather than a
    # step-based ExecutionPlan, so we synthesize a completion report from the
    # observed tool-call trace. This lets the C# side feed real success/failure
    # signals into planning knowledge for the cloud path too.
    tool_calls_total = 0
    tool_calls_failed = 0
    deviations: List[str] = []
    stream_failed = False

    # ── Delivery report (fail-closed) ──
    # Collects written blocks + write records + compile outcome for the final `delivery` SSE event.
    _WRITE_TOOLS = frozenset({"import_block", "build_lad_fc", "build_lad_fb", "build_graph_fb", "build_udt", "build_global_db", "patch_block_network", "patch_block_interface"})
    _COMPILE_TOOLS = frozenset({"compile"})
    delivery_blocks: Dict[str, dict] = {}
    delivery_writes: List[dict] = []
    _write_pending: Dict[Any, dict] = {}  # run_id -> parsed write info
    compile_pass: Optional[bool] = None


    def _parse_write(tool_name: str, tool_input: Any) -> Optional[dict]:
        """Best-effort parse of a write tool call → {tool,name,type,lang,number}. Fail-closed."""
        try:
            if not isinstance(tool_input, dict):
                return None
            # invoke_tool wraps the real name + a JSON string of args. Prefer the wrapper's
            # "name" if present (the outer tool_name may already be unwrapped), and always
            # re-parse args from "args_json" when it exists.
            real = tool_input.get("name") or tool_name
            raw_args = tool_input.get("args_json", "")
            if isinstance(raw_args, str) and raw_args.strip():
                args = json.loads(raw_args) if raw_args else {}
                if not isinstance(args, dict):
                    args = {}
            else:
                args = tool_input
            if real not in _WRITE_TOOLS:
                return None
            if real in ("patch_block_network", "patch_block_interface"):
                name = args.get("block_name")
                if not name:
                    return None
                lang = "lad-network-patch" if real == "patch_block_network" else "interface-patch"
                return {"tool": real, "name": name, "type": "", "lang": lang, "number": None}
            if real in ("build_lad_fc", "build_lad_fb", "build_graph_fb"):
                spec = args.get("json")
                if isinstance(spec, str):
                    spec = json.loads(spec)
                if not isinstance(spec, dict):
                    return None
                name = spec.get("blockName")
                if not name:
                    return None
                block_type = "FC" if real == "build_lad_fc" else "FB"
                lang = "graph" if real == "build_graph_fb" else "lad"
                return {"tool": real, "name": name,
                        "type": block_type,
                        "lang": lang, "number": spec.get("blockNumber")}
            name = args.get("block_name")
            if not name:
                return None
            return {"tool": real, "name": name, "type": args.get("block_type") or "",
                    "lang": "scl", "number": None}
        except Exception:
            return None


    def _write_result(tool_output: str, transport_failed: bool) -> str:
        """Map a write tool's output + transport flag to a stable result token."""
        out = (tool_output or "").lstrip()
        low = out.lower()
        for refused in ("user_denied", "approval_timeout", "approval_unavailable", "run_cancelled"):
            if low.startswith(refused):
                return refused
        return "error" if transport_failed else "ok"


    def _block_status(result: str) -> str:
        """Map a write result token to a block status."""
        if result == "ok":
            return "written"
        if result == "error":
            return "failed"
        return "prepared_not_written"

    # Aggregate real token usage across every sub-agent LLM call so the cloud
    # path reports usage back to C# (previously the multi-agent path emitted no
    # token_usage at all).
    usage_input = 0
    usage_output = 0
    usage_total = 0

    # Every content delta already forwarded to the client in this run. The
    # supervisor's synthesized final_answer is compared against this so an
    # answer the agent already streamed is never emitted a second time.
    streamed_content: List[str] = []

    # run_id -> start time, so each tool call can be logged with its duration.
    _tool_t0: Dict[Any, float] = {}
    # run_id -> real tool name (invoke_tool hides it), for failure signals / logging.
    _tool_name_by_run: Dict[Any, str] = {}
    # run_ids of invoke_tool runs currently open. A real tool invoked inside
    # invoke_tool (entry.tool.ainvoke) inherits the ambient callback context and
    # emits its OWN on_tool_start/end with the real name while the outer run is
    # still open; those nested events are skipped to avoid double reporting.
    _open_meta_runs = set()
    _skipped_tool_runs = set()

    # Graph-level recursion cap: prevents supervisor<->agent ping-pong from
    # hitting LangGraph's default (25) and raising. This + the supervisor round
    # caps are the MAIN loop guards; sub-agent middleware is only a supplement.
    # Kept well ABOVE the sub-agent runaway guards (200 tool / 100 model calls)
    # so the graceful middleware path always fires first.
    _GRAPH_RECURSION_LIMIT = int(os.getenv("TIA_GRAPH_RECURSION_LIMIT", "300"))

    # ── Evolution recording (P0, fail-closed side-channel) ──
    # Activated only when D9 is enabled and project_key exists; otherwise fully silent with zero collection.
    recorder = None
    if evolution_enabled and project_key:
        try:
            from agent.evolution import EpisodeRecorder
            recorder = EpisodeRecorder(
                project_key, intent, prompt,
                getattr(llm, "model_name", "") or "",
            )
        except Exception as rec_ex:
            logger.warning(f"[evolution] collect skipped: {rec_ex}")

    try:
        async for event in graph.astream_events(
            initial_state, version="v2",
            config={"recursion_limit": _GRAPH_RECURSION_LIMIT},
        ):
            kind = event["event"]

            if kind == "on_chat_model_stream":
                chunk = (event.get("data") or {}).get("chunk")
                reasoning = extract_stream_reasoning(chunk)
                if reasoning:
                    yield "thinking", reasoning
                    if recorder:
                        recorder.record({"type": "thinking", "text": reasoning})
                delta = extract_stream_delta(chunk)
                if delta:
                    streamed_content.append(delta)
                    yield "content", delta
                    if recorder:
                        recorder.record({"type": "content", "text": delta})

            elif kind == "on_chat_model_end":
                # Per-call token usage. The ReAct loop re-sends the full context on
                # every model call, so SUMMING prompt tokens across calls reports
                # nonsense (e.g. 880k "used" against a 128k window). Track the PEAK
                # single-call footprint (the real window pressure) plus the SUMMED
                # generated tokens instead.
                out = (event.get("data") or {}).get("output")
                usage = _extract_usage_metadata(out)
                if usage:
                    call_in = int(usage.get("input_tokens", 0) or 0)
                    call_out = int(usage.get("output_tokens", 0) or 0)
                    call_total = int(usage.get("total_tokens", 0) or call_in + call_out)
                    usage_input = max(usage_input, call_in)
                    usage_output += call_out
                    usage_total = max(usage_total, call_total)

            elif kind == "on_tool_start":
                tool_input = event.get("data", {}).get("input", {})
                tool_name = display_tool_name(event["name"], tool_input)
                _run_id = event.get("run_id")
                # Dedupe nested tool events (see _open_meta_runs above): while an
                # invoke_tool run is open, real-name tool events are the inner
                # tool's own callbacks — skip them. Direct tool runs outside any
                # meta run (e.g. the compiler node) and discover_tools (never
                # nested) still report normally.
                if event["name"] == "invoke_tool":
                    _open_meta_runs.add(_run_id)
                elif _open_meta_runs and event["name"] != "discover_tools":
                    _skipped_tool_runs.add(_run_id)
                    continue
                tool_calls_total += 1
                # Soft budget signal: tell the user we are still running (no abort)
                # once the run crosses the notice threshold. Token budget +
                # compaction, not this counter, decide when the run ends.
                if tool_calls_total == _TOOL_CALL_SOFT_LIMIT:
                    yield "notice", notice(
                        "tool.soft_limit",
                        f"This run has made {_TOOL_CALL_SOFT_LIMIT} tool calls and keeps going; "
                        "execution is now governed by the token budget and auto-compaction.",
                        _TOOL_CALL_SOFT_LIMIT,
                    )
                _tool_t0[_run_id] = time.perf_counter()
                _tool_name_by_run[_run_id] = tool_name
                # Delivery tracking: remember the write call so its result can be recorded at end.
                _write_info = _parse_write(tool_name, tool_input)
                if _write_info is not None:
                    _write_pending[_run_id] = _write_info
                try:
                    _args_preview = json.dumps(
                        {k: str(v)[:80] for k, v in (tool_input or {}).items()},
                        ensure_ascii=False,
                    )[:300]
                except Exception:
                    _args_preview = "<unprintable>"
                logger.info(f"[TOOL] start name={tool_name} args={_args_preview}")
                if recorder:
                    recorder.record({"type": "tool_start", "name": tool_name, "input": tool_input})
                yield "tool_call", json.dumps({
                    "name": tool_name,
                    "input": {k: str(v)[:200] for k, v in (tool_input or {}).items()},
                    "status": "started",
                }, ensure_ascii=False)

            elif kind == "on_tool_end":
                _run_id = event.get("run_id")
                if _run_id in _skipped_tool_runs:
                    _skipped_tool_runs.discard(_run_id)
                    continue
                _open_meta_runs.discard(_run_id)
                tool_name = _tool_name_by_run.pop(_run_id, event["name"])
                tool_output = str(event.get("data", {}).get("output", ""))
                # Use the tool-return CONTRACT, not a substring scan. TIA tools
                # return a string starting with "Error:" or
                # "TIA tool error" on failure. A plain ``"error" in output`` scan
                # mis-flagged normal outputs that merely mention the word "error"
                # (e.g. an error-handling block), polluting the success signal
                # fed back to planning knowledge.
                stripped = tool_output.lstrip()
                low = stripped.lower()
                _failed = low.startswith("error:") or low.startswith("tia tool error")
                if _failed:
                    tool_calls_failed += 1
                    deviations.append(f"Tool '{tool_name}' reported a failure")
                # Delivery tracking: finalize a pending write record and update its block.
                _write_info = _write_pending.pop(_run_id, None)
                if _write_info is not None:
                    result = _write_result(tool_output, _failed)
                    block = delivery_blocks.setdefault(_write_info["name"], {
                        "name": _write_info["name"],
                        "type": _write_info["type"] or None,
                        "lang": _write_info["lang"],
                        "number": _write_info["number"],
                        "compiled": None,
                        "status": "prepared_not_written",
                    })
                    # A later write (fixing/re-writing the same block) wins as the final record.
                    block["status"] = _block_status(result)
                    delivery_writes.append({
                        "tool": _write_info["tool"],
                        "block": _write_info["name"],
                        "approved": result == "ok",
                        "result": result,
                    })
                # Delivery tracking: remember whether the last compile pass succeeded.
                if tool_name in _COMPILE_TOOLS:
                    compile_pass = not _compile_result_failed(tool_output)
                _started = _tool_t0.pop(_run_id, None)
                _dur_ms = int((time.perf_counter() - _started) * 1000) if _started else -1
                logger.info(
                    f"[TOOL] end name={tool_name} ok={not _failed} ms={_dur_ms} out_len={len(tool_output)}"
                )
                if recorder:
                    recorder.record({"type": "tool_end", "name": tool_name, "output": tool_output})
                yield "tool_call", json.dumps({
                    "name": tool_name,
                    "output": tool_output[:500],
                    "status": "completed",
                }, ensure_ascii=False)

            elif kind == "on_chain_end":
                # Check if this is the end of a supervisor/agent node
                node_name = event.get("name", "")
                output = event.get("data", {}).get("output", {})
                if isinstance(output, dict):
                    agent = output.get("current_agent", "")
                    if agent and agent != "__end__":
                        yield "thinking", f"➡️ Supervisor routing to: {agent}"
                    # When a node finishes with a final_answer (e.g. Chat agent or
                    # supervisor FINAL), the answer was generated via agent.invoke()
                    # (non-streaming within the graph), so on_chat_model_stream never
                    # fires for it. We must yield the content here.
                    # Only the supervisor NODE synthesizes final answers: the
                    # graph-level on_chain_end (name "LangGraph") carries the same
                    # final state and would double-emit the answer to the frontend.
                    if node_name == "supervisor":
                        final_answer = output.get("final_answer", "")
                        status = output.get("status", "")
                        if final_answer and status == "completed":
                            # Read-only analysis: the reviewer's answer was already
                            # streamed token-by-token, and the supervisor reuses it as
                            # final_answer — emitting it again renders the answer twice.
                            if _answer_already_streamed(final_answer, streamed_content):
                                logger.info(
                                    "[SUPERVISOR] final answer already streamed; "
                                    "skipping duplicate emit"
                                )
                            else:
                                yield "content", final_answer
                            if recorder:
                                recorder.record({"type": "final_answer", "text": final_answer})

        total_ms = int((time.perf_counter() - t0) * 1000)
        logger.info(
            f"[SUPERVISOR] completed total_ms={total_ms} "
            f"tokens(peak_in/sum_out/peak_total)={usage_input}/{usage_output}/{usage_total}"
        )

        # Report real token usage back to C# (multi-agent path previously emitted
        # none). Semantics: peak single-call footprint, NOT a sum across the ReAct
        # loop's repeated full-context calls (see on_chat_model_end).
        if usage_total > 0 or usage_input > 0 or usage_output > 0:
            try:
                # Emit the budget-feedback schema the C# UI (TokenUsageData) reads:
                # total_used / total_budget / usage_percent / status. Previously we
                # sent input_tokens/output_tokens/total_tokens, which the C# parser
                # could not map and rendered as "Tokens: 0 / 0 (0%)".
                # total_used = the largest single model call's footprint, which is
                # what actually competes with the context-window budget.
                used = int(usage_total or (usage_input + usage_output) or 0)
                budget = get_token_budget(context_window=context_window)
                pct = round(used * 100.0 / budget, 1) if budget > 0 else 0.0
                if pct >= 95:
                    status = "Exceeded"
                elif pct >= 80:
                    status = "Warning"
                else:
                    status = "Ok"
                yield "token_usage", json.dumps({
                    "prompt_tokens": int(usage_input or 0),
                    "context_tokens": 0,
                    "history_tokens": 0,
                    "tool_output_tokens": int(usage_output or 0),
                    "total_used": used,
                    "total_budget": budget,
                    "remaining": max(0, budget - used),
                    "usage_percent": pct,
                    "status": status,
                }, ensure_ascii=False)
            except Exception as usage_ex:
                logger.warning(f"[SUPERVISOR] token_usage emit failed: {usage_ex}")

        # Emit a synthesized plan_progress so cloud runs also contribute real
        # completion/deviation data to planning knowledge. Treat each tool call
        # as a step; failed tool calls count as failed steps + deviations.
        try:
            completed_steps = max(0, tool_calls_total - tool_calls_failed)
            summary = (
                f"✅ {completed_steps}/{tool_calls_total} tool step(s) completed"
                if tool_calls_failed == 0
                else f"⚠️ {completed_steps}/{tool_calls_total} tool step(s) completed, {tool_calls_failed} failed"
            )
            yield "plan_progress", json.dumps({
                "total_steps": tool_calls_total,
                "completed_steps": completed_steps,
                "skipped_steps": 0,
                "failed_steps": tool_calls_failed,
                "deviations": deviations,
                "summary": summary,
            }, ensure_ascii=False)
        except Exception as report_ex:
            logger.warning(f"[SUPERVISOR] plan_progress emit failed: {report_ex}")

        # ── Delivery report ──
        # Emit the structured block/write summary so the C# side can render a delivery
        # card and feed block-level provenance into planning knowledge. Fail-closed:
        # the emit is optional and must never block or crash the main stream.
        try:
            if delivery_blocks or delivery_writes:
                blocks = [dict(b) for b in delivery_blocks.values()]
                if compile_pass is not None:
                    for b in blocks:
                        if b.get("status") == "written":
                            b["compiled"] = compile_pass
                yield "delivery", json.dumps({
                    "event": "delivery",
                    "blocks": blocks,
                    "write_records": delivery_writes,
                }, ensure_ascii=False)
        except Exception as delivery_ex:
            logger.warning(f"[SUPERVISOR] delivery emit failed: {delivery_ex}")

        # ── Evolution wrap-up: persist recording result + enqueue background extraction (fail-closed) ──
        if recorder and recorder.active:
            try:
                recorder.record({
                    "type": "plan_progress",
                    "total_steps": tool_calls_total,
                    "failed_steps": tool_calls_failed,
                    "summary": summary,
                })
                recorder.close("ended")
                from agent.evolution import extraction_worker
                extraction_worker.enqueue(recorder.episode_id, recorder.path, model_cfg, project_key)
            except Exception as evo_ex:
                logger.warning(f"[evolution] close failed: {evo_ex}")

        yield "done", "ok"

    except Exception as e:
        logger.exception(f"[SUPERVISOR] execution failed: {e}")
        if recorder and recorder.active:
            try:
                recorder.close("error")
            except Exception:
                pass
        yield "error", str(e)
        yield "done", "ok"
