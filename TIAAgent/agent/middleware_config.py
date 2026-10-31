"""
Shared middleware configuration for the cloud multi-agent sub-agents
(coder / reviewer / chat).

Why middleware instead of hand-rolled trimming:
    langchain 1.2's ``create_agent`` runs an internal ReAct loop that manages its
    own message list + tool results. The old ``core/context.py`` trimming helpers
    were written for a single hand-assembled message list and never ran inside
    these loops. Rather than re-implement trimming, we attach langchain's built-in
    middleware, which hooks the loop at the right points.

Layered strategy (applied in this order before each LLM call):
    1. ContextEditingMiddleware (ClearToolUsesEdit)
       Cheapest, semantically lossless: when context grows past a threshold,
       replace OLD tool-call outputs with a placeholder, keeping the most recent
       few. SCL blocks / search dumps are large and only needed transiently.
    2. SummarizationMiddleware
       When trimming tool results still isn't enough (long coder<->reviewer
       conversations), summarize older messages with one LLM call. Triggered by
       a FRACTION of the model's input window so we don't hard-code per-model
       context sizes.
    3. ModelCallLimitMiddleware (loose)
       NOT the main loop guard — the graph ``recursion_limit`` and the
       supervisor's round caps are. This only makes a runaway sub-agent END
       gracefully (return what it has) instead of hitting recursion_limit and
       raising, which the outer handler would swallow into an error.

4. ToolCallLimitMiddleware (run-level cap, graceful end)
       Hard-stops tool-calling loops so a runaway sub-agent ends with an explanatory
       message instead of crashing into the LangGraph recursion limit ("Recursion
       limit of 25 reached"). Kept below that limit on purpose so it fires first.
"""

import json
import os
from typing import Any, List, Optional

from langchain_core.messages import AIMessage, ToolMessage
from langchain_openai import ChatOpenAI
from langchain.agents.middleware import (
    SummarizationMiddleware,
    ContextEditingMiddleware,
    ModelCallLimitMiddleware,
    ClearToolUsesEdit,
)
from langchain.agents.middleware.tool_call_limit import ToolCallLimitMiddleware
from langchain.agents.middleware.types import (
    AgentMiddleware,
    AgentState,
    hook_config,
)
from typing_extensions import NotRequired

from core.context import count_total_tokens, get_token_budget

# ── Tunables (env-overridable; initial values, tune after real measurement) ──

# Triggers are FRACTIONS of the model's real context window, not absolute token
# counts: the same numbers must mean the same thing on a 128K and a 1M model.
# The window itself comes from the C# host (request.context_window →
# TokenBudget.ResolveModelWindow); the 128K fallback below is only a last
# resort when the host omits it.
_CLEAR_TRIGGER_FRACTION = 0.55      # clear old tool results at ~55% of window
_SUMMARIZE_TRIGGER_FRACTION = 0.75  # summarize older messages at ~75% of window

# Explicit token overrides (win over the fractions when set).
_TOOL_CLEAR_TRIGGER_ENV = os.getenv("TIA_TOOL_CLEAR_TRIGGER")
_SUMMARIZE_TRIGGER_ENV = os.getenv("TIA_SUMMARIZE_TRIGGER_TOKENS")

# How many of the most recent tool results to keep verbatim.
_TOOL_CLEAR_KEEP = int(os.getenv("TIA_TOOL_CLEAR_KEEP", "3"))
# Keep this many recent messages verbatim after summarization.
_SUMMARIZE_KEEP_MESSAGES = int(os.getenv("TIA_SUMMARIZE_KEEP_MSGS", "6"))
# LLM-call ceiling per sub-agent run. Governance is the token budget + the
# compaction stack above; this is only a runaway guard.
_MODEL_CALL_RUN_LIMIT = int(os.getenv("TIA_MODEL_CALL_RUN_LIMIT", "100"))
# Hard tool-call ceiling per sub-agent run — RUNAWAY GUARD ONLY, not a normal
# workflow boundary. Normal runs are governed by the token budget/compaction;
# crossing the soft limit below notifies the user and execution continues.
_TOOL_CALL_RUN_LIMIT = int(os.getenv("TIA_TOOL_CALL_RUN_LIMIT", "200"))
# Read-only analysis (ExplainCode / TraceVariable) has no write to converge on, so a
# lookup that keeps returning nothing can ping-pong forever (72 calls / 900 s observed).
# A much lower ceiling ends it with the accumulated evidence instead.
_ANALYZE_TOOL_CALL_LIMIT = int(os.getenv("TIA_ANALYZE_TOOL_CALL_LIMIT", "50"))
# Soft threshold: at this many tool calls the backend emits a user-facing notice
# (UI popup) and KEEPS GOING. Budget/compaction decide when the run should stop.
_TOOL_CALL_SOFT_LIMIT = int(os.getenv("TIA_TOOL_CALL_SOFT_LIMIT", "40"))


_UNKNOWN_TOOL_PREFIXES = (
    "error: unknown tool",
    "error: unauthorized tool",
    "error: write tool not allowed",
    # invoke_tool routed a meta-tool through itself (the exact failure that started
    # the 18-step guessing loop in the 2026-10-27 run).
    "error: 'discover_tools' is a meta-tool",
    "error: 'invoke_tool' is a meta-tool",
)


# Tools whose identical repeat is almost always a wasted turn. Writes/compiles are
# excluded: they may legitimately repeat after the code changes, and any write resets
# the counters below.
_REPEAT_GUARDED_TOOLS = frozenset({
    "read_standard_section", "list_standard_topics",
    "get_block_content",
    "search_project", "get_program_tree",
    "list_tags", "get_db_structure", "list_udt_types",
    "query_variable_type", "query_variable_references",
    "query_call_chain", "query_variable_impact",
})
_MUTATING_TOOLS = frozenset({
    "import_block", "delete_block", "compile",
    "compile_snapshot", "compile_diff", "build_lad_fc", "build_lad_fb",
    "build_graph_fb",
    "build_udt", "build_global_db", "build_ob", "configure_device",
    "patch_block_network", "patch_block_interface", "repair_inconsistent_types",
    "set_device_attribute", "add_module", "configure_subnet",
})
_REPEAT_LIMIT = 3


class _UnknownToolGuardState(AgentState[Any]):
    """Per-run counters for the two sub-agent loop guards."""

    unknown_tool_streak: NotRequired[int]
    repeated_calls: NotRequired[dict]
    repeat_seeded: NotRequired[bool]


def _call_parts(call: Any):
    """Return ``(real_tool_name, arguments)`` for a direct or invoke_tool call."""
    if isinstance(call, dict):
        name = str(call.get("name") or "")
        args = call.get("args") or {}
    else:
        name = str(getattr(call, "name", "") or "")
        args = getattr(call, "args", {}) or {}
    if not isinstance(args, dict):
        return name, {}

    inner = name
    inner_args = args
    if name == "invoke_tool":
        inner = str(args.get("name") or "")
        raw = args.get("args_json")
        if isinstance(raw, dict):
            inner_args = raw
        elif isinstance(raw, str) and raw.strip():
            try:
                parsed = json.loads(raw)
                inner_args = parsed if isinstance(parsed, dict) else raw
            except (ValueError, TypeError):
                inner_args = raw
    return inner, inner_args


def _repeat_call_key(call: Any) -> str:
    """Normalize a tool call to ``inner_name|canonical_args`` for repeat detection."""
    inner, inner_args = _call_parts(call)
    if inner not in _REPEAT_GUARDED_TOOLS:
        return ""
    try:
        norm = json.dumps(inner_args, sort_keys=True, ensure_ascii=False)
    except (TypeError, ValueError):
        norm = str(inner_args)
    return inner + "|" + norm


def _seed_repeat_counts(messages, end_index: int) -> dict:
    """Rebuild repeat counts from prior rounds' messages.

    A sub-agent invocation starts with fresh middleware state, so without seeding a
    section read twice before (e.g. coder round 1, reviewer round 1) would look like a
    first read in round 2. Scan the history in order and reset at any write/compile.
    """
    counts: dict = {}
    for msg in messages[:end_index]:
        if not isinstance(msg, AIMessage) or not msg.tool_calls:
            continue
        inner_names = [_call_parts(call)[0] for call in msg.tool_calls]
        if any(name in _MUTATING_TOOLS for name in inner_names):
            counts.clear()
            continue
        keys = {_repeat_call_key(call) for call in msg.tool_calls}
        keys.discard("")
        for key in keys:
            counts[key] = int(counts.get(key, 0)) + 1
    return counts


def _stop_tool_messages(last_ai, text: str) -> List[Any]:
    """Answer pending tool calls so the transcript has no dangling tool ids."""
    return [
        ToolMessage(
            content=text,
            tool_call_id=call["id"],
            name=call.get("name"),
            status="error",
        )
        for call in last_ai.tool_calls
    ]


class UnknownToolGuardMiddleware(AgentMiddleware):
    """End a sub-agent that loops on unknown tools or repeats the same read.

    Two guards share one state schema:

    1. Unknown-tool loop: a batch with three unknown-tool results, or three
       consecutive turns each with at least one, stops the agent.
    2. Repeated identical read: the same guarded tool call (same tool, same arguments)
       is counted once per turn; on the third turn the agent stops and asks the user
       for the missing detail instead of re-reading the same source again.

    The unknown-tool stop uses the existing "Tool call limit reached" marker so the
    coder node treats it as a real abort. The repeat stop uses "USER_INPUT_REQUIRED:"
    so the node surfaces a question to the user instead of routing to the reviewer.
    """

    state_schema = _UnknownToolGuardState

    @hook_config(can_jump_to=["end"])
    def after_model(self, state, runtime) -> Optional[dict]:
        messages = state.get("messages") or []
        last_ai_index = -1
        for i in range(len(messages) - 1, -1, -1):
            if isinstance(messages[i], AIMessage):
                last_ai_index = i
                break
        if last_ai_index < 0:
            return None

        last_ai = messages[last_ai_index]
        if not last_ai.tool_calls:
            # A normal final answer resets the unknown-tool streak.
            return {"unknown_tool_streak": 0}

        # The previous step's results are the contiguous ToolMessage block just before
        # the new AIMessage.
        unknown = 0
        j = last_ai_index - 1
        while j >= 0 and isinstance(messages[j], ToolMessage):
            content = messages[j].content
            text = content if isinstance(content, str) else str(content)
            if text.lstrip().lower().startswith(_UNKNOWN_TOOL_PREFIXES):
                unknown += 1
            j -= 1

        prev = int(state.get("unknown_tool_streak", 0) or 0)
        streak = 0 if unknown == 0 else prev + 1
        if unknown >= 3 or streak >= 3:
            stop_text = (
                "Stopped: repeated calls to tools that do not exist. Call discover_tools "
                "directly (no arguments) to get the exact tool names."
            )
            artificial = _stop_tool_messages(last_ai, stop_text)
            artificial.append(AIMessage(
                content="Tool call limit reached: three consecutive unknown-tool calls. "
                        "Stopped to avoid a guessing loop. Call discover_tools with no arguments."
            ))
            return {
                "messages": artificial,
                "jump_to": "end",
                "unknown_tool_streak": streak,
            }

        counts = dict(state.get("repeated_calls") or {})
        seeded = bool(state.get("repeat_seeded"))
        if not seeded:
            # First model call of this sub-agent invocation: rebuild counts from the
            # rounds already present in the shared history.
            counts = _seed_repeat_counts(messages, last_ai_index)
            seeded = True

        inner_names = []
        guarded_keys = []
        for call in last_ai.tool_calls:
            inner, _args = _call_parts(call)
            inner_names.append(inner)
            key = _repeat_call_key(call)
            if key and key not in guarded_keys:
                guarded_keys.append(key)

        # A write/compile changes the project, so an identical read after it may
        # legitimately return different data: start the counters over.
        if any(name in _MUTATING_TOOLS for name in inner_names):
            counts.clear()

        repeated_key = ""
        for key in guarded_keys:
            n = int(counts.get(key, 0)) + 1
            counts[key] = n
            if n >= _REPEAT_LIMIT and not repeated_key:
                repeated_key = key

        if not repeated_key:
            return {
                "unknown_tool_streak": streak,
                "repeated_calls": counts,
                "repeat_seeded": True,
            }

        label = repeated_key.split("|", 1)[0]
        if label == "read_standard_section":
            question = (
                "I have already read the same standard section 3 times and the content has "
                "not changed. Re-reading it will not add information. Please tell me the exact "
                "rule/topic you need, or confirm that I should continue with the current information."
            )
        else:
            question = (
                "I have already called " + label + " with the same arguments 3 times and the "
                "result has not changed. Please tell me what exactly you need, or confirm that "
                "I should continue with the current information."
            )
        artificial = _stop_tool_messages(
            last_ai,
            "Stopped: this identical call was already made 3 times; asking the user "
            "for clarification instead of repeating it.",
        )
        artificial.append(AIMessage(content="USER_INPUT_REQUIRED: " + question))
        return {
            "messages": artificial,
            "jump_to": "end",
            "unknown_tool_streak": streak,
            "repeated_calls": counts,
            "repeat_seeded": True,
        }

    async def aafter_model(self, state, runtime) -> Optional[dict]:
        return self.after_model(state, runtime)


def _trigger_tokens(env_value: Optional[str], fraction: float, window: int) -> int:
    """Resolve a middleware trigger threshold (explicit env value wins)."""
    if env_value:
        try:
            value = int(env_value)
            if value > 0:
                return value
        except ValueError:
            pass
    return max(1000, int(window * fraction))


def build_subagent_middleware(llm: ChatOpenAI, context_window: int = 0,
                              tool_call_limit: Optional[int] = None) -> List[object]:
    """Build the shared middleware stack for a cloud sub-agent.

    Order matters: cheap tool-result clearing first, then summarization, then the
    loose call-limit safety net.

    Args:
        llm: the same cloud ChatOpenAI used by the sub-agent — reused so the
            summarization step doesn't need a separate model.
        context_window: the model's REAL input window (tokens) as reported by the
            C# host; 0/absent falls back to the conservative 128K default. The
            clear/summarize triggers are derived from it so the same stack works
            for both 128K and 1M-window models.
        tool_call_limit: hard tool-call ceiling for this run. None = the generic
            runaway guard; read-only analysis passes the much lower analyze limit.

    Returns:
        A list of middleware instances to pass to ``create_agent(middleware=...)``.
    """
    window = int(context_window or 0) or get_token_budget()

    context_editing = ContextEditingMiddleware(
        edits=[
            ClearToolUsesEdit(
                trigger=_trigger_tokens(_TOOL_CLEAR_TRIGGER_ENV, _CLEAR_TRIGGER_FRACTION, window),
                keep=_TOOL_CLEAR_KEEP,
                clear_tool_inputs=False,  # keep tool inputs for traceability; only clear outputs
                placeholder="[cleared: older tool result removed to save context]",
            )
        ]
    )

    summarization = SummarizationMiddleware(
        model=llm,
        trigger=("tokens", _trigger_tokens(_SUMMARIZE_TRIGGER_ENV, _SUMMARIZE_TRIGGER_FRACTION, window)),
        keep=("messages", _SUMMARIZE_KEEP_MESSAGES),
        # LangChain calls the counter with the whole message LIST; count_tokens
        # (str-only) silently under-counted, which is why nothing ever compacted.
        token_counter=count_total_tokens,
    )

    model_call_limit = ModelCallLimitMiddleware(
        run_limit=_MODEL_CALL_RUN_LIMIT,
        exit_behavior="end",  # graceful: return current result instead of raising
    )

    tool_call_limit = ToolCallLimitMiddleware(
        run_limit=int(tool_call_limit or _TOOL_CALL_RUN_LIMIT),
        exit_behavior="end",  # graceful: stop the loop + explanatory AI message
    )

    unknown_tool_guard = UnknownToolGuardMiddleware()

    return [context_editing, summarization, model_call_limit, tool_call_limit, unknown_tool_guard]
