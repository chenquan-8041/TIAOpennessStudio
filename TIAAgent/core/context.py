"""
Context Manager — token counting, message window management, history compression.

Responsibilities:
  1. Token counting (tiktoken with fallback to char/4 estimate)
  2. Message window management (sliding window, keep system prompt + recent)
  3. Tool result truncation (prevent massive ToolMessage from blowing context)
  4. History compression (summarize old messages when window overflows)
  5. Context smart truncation (keep beginning + end, not just first N chars)

All functions are stateless — callers pass messages in, get trimmed messages out.
"""

import logging
import os
from typing import Dict, List, Optional, Tuple

from langchain_core.messages import BaseMessage, SystemMessage, HumanMessage, AIMessage, ToolMessage

logger = logging.getLogger("backend")


# ── Multimodal (vision) content ──────────────────────────────

def build_user_content(text: str, images: Optional[List[str]] = None):
    """Build a HumanMessage ``content`` payload, adding images when present.

    Returns a plain string when there are no images (keeps text-only requests
    unchanged), or the OpenAI multimodal ``content`` block list when one or more
    base64-encoded PNG images are supplied.

    Args:
        text: The user's text.
        images: Optional list of base64-encoded PNG data (no data-URI prefix).

    Returns:
        ``str`` (no images) or ``list[dict]`` (multimodal content blocks).
    """
    if not images:
        return text
    parts: List[dict] = [{"type": "text", "text": text}]
    for img_b64 in images:
        if not img_b64:
            continue
        parts.append({
            "type": "image_url",
            "image_url": {"url": f"data:image/png;base64,{img_b64}"},
        })
    # If every image entry was empty, fall back to plain text.
    if len(parts) == 1:
        return text
    return parts

# ── Token counting ───────────────────────────────────────────

# Try tiktoken, fall back to char/4 heuristic
try:
    import tiktoken
    _TIKTOKEN_AVAILABLE = True
    _ENCODING = tiktoken.get_encoding("cl100k_base")  # GPT-4 / GPT-3.5 encoding
except (ImportError, Exception):
    _TIKTOKEN_AVAILABLE = False
    _ENCODING = None


def count_tokens(text: str) -> int:
    """Count tokens in a string. Uses tiktoken if available, else char/4 heuristic."""
    if not text:
        return 0
    if _TIKTOKEN_AVAILABLE and _ENCODING:
        try:
            return len(_ENCODING.encode(text))
        except Exception:
            pass
    return max(1, len(text) // 4)


# Approximate token cost of one image in a multimodal message.
# OpenAI bills images by tiles; a typical high-detail image lands around
# ~1000+ tokens. Without the source resolution we can't compute exactly, so we
# use a single conservative constant. The point is that images STOP counting as
# 0 tokens (which made every budget/trigger check under-count with images).
# Override via TIA_IMAGE_TOKEN_COST.
IMAGE_TOKEN_COST = int(os.getenv("TIA_IMAGE_TOKEN_COST", "1100"))


def count_message_tokens(msg: BaseMessage) -> int:
    """Count tokens in a single LangChain message (including role overhead).

    Multimodal messages: text blocks are counted normally; each image block is
    charged a flat ``IMAGE_TOKEN_COST`` instead of 0 so token budgeting and the
    summarization/clearing triggers don't systematically under-count with images.
    """
    # Role overhead: ~4 tokens per message (OpenAI convention)
    overhead = 4
    content = ""
    image_tokens = 0
    if hasattr(msg, "content"):
        c = msg.content
        if isinstance(c, str):
            content = c
        elif isinstance(c, list):
            # Multi-modal content blocks: text is counted; images charged a flat cost.
            text_parts = []
            for block in c:
                if isinstance(block, dict):
                    btype = block.get("type")
                    if btype == "image_url" or "image_url" in block:
                        image_tokens += IMAGE_TOKEN_COST
                    else:
                        text_parts.append(str(block.get("text", "")))
                else:
                    text_parts.append(str(block))
            content = " ".join(text_parts)
        else:
            content = str(c)
    return overhead + count_tokens(content) + image_tokens


def count_total_tokens(messages) -> int:
    """Count total tokens across a message list.

    This is the adapter LangChain middleware expects: its ``token_counter`` hook
    is called with the whole message LIST, not a single string. Passing the
    string-based ``count_tokens`` there made the counter return
    ``len(list)//4`` (a couple of "tokens"), so middleware triggers never fired.
    """
    total = 0
    for m in messages or []:
        if isinstance(m, BaseMessage):
            total += count_message_tokens(m)
        elif isinstance(m, dict):
            total += 4 + count_tokens(str(m.get("content", "") or ""))
        else:
            total += count_tokens(str(m))
    return total


# ── Tool result truncation ───────────────────────────────────

# Max characters for a ToolMessage content.
# SCL code can be hundreds of lines — we keep the first + last portion.
MAX_TOOL_RESULT_CHARS = 2000
MAX_TOOL_RESULT_TOKENS = 800


def truncate_tool_result(content: str, max_chars: int = MAX_TOOL_RESULT_CHARS) -> str:
    """Truncate a tool result, keeping beginning + end for context.

    For SCL code results, we keep the header (block type, name, declarations)
    and the tail (body logic), trimming the middle if needed.
    """
    if not content or len(content) <= max_chars:
        return content

    # Keep 60% from start, 40% from end
    head_size = int(max_chars * 0.6)
    tail_size = max_chars - head_size - 50  # 50 chars for the truncation marker

    head = content[:head_size]
    tail = content[-tail_size:] if tail_size > 0 else ""

    # Try to break at a newline for readability
    last_nl = head.rfind("\n")
    if last_nl > head_size // 2:
        head = head[:last_nl]

    first_nl = tail.find("\n")
    if first_nl > 0 and first_nl < 50:
        tail = tail[first_nl:]

    return f"{head}\n\n... [truncated {len(content) - head_size - (len(content) - len(content[-tail_size:]) if tail_size > 0 else 0)} chars] ...\n\n{tail}"


# ── Context smart truncation ─────────────────────────────────

def truncate_context(context: str, max_chars: int = 3000) -> str:
    """Smart truncation: keep first 70% + last 30% of context.

    For TIA project context, the beginning typically has project structure
    and the end has the most recent/relevant blocks.
    """
    if not context or len(context) <= max_chars:
        return context

    head_size = int(max_chars * 0.7)
    tail_size = max_chars - head_size - 80

    head = context[:head_size]
    tail = context[-tail_size:] if tail_size > 0 else ""

    # Break at section boundaries
    last_section = head.rfind("\n## ")
    if last_section > head_size // 2:
        head = head[:last_section]

    return f"{head}\n\n... [context truncated, {len(context) - head_size - tail_size} chars omitted] ...\n\n{tail}"


# ── Message window management ─────────────────────────────────

# Fallback budget when the model is unknown — override via TIA_TOKEN_BUDGET_DEFAULT.
# 6000 is a conservative floor for tiny models (leaves ~2000 for the response).
_DEFAULT_BUDGET = int(os.getenv("TIA_TOKEN_BUDGET_DEFAULT", "6000"))

# ── Context-window budget policy ──────────────────────────────
# This backend only serves cloud models (Ollama runs inside the C# host and
# never reaches here). Modern cloud models ship huge windows (1M+ is common),
# so we default to 1M unless an operator forces a smaller ceiling via
# TIA_TOKEN_BUDGET_DEFAULT.

# Conservative default window when the caller doesn't tell us the model's real
# one. 128K covers most current cloud models; the old 1M default effectively
# disabled trimming and let requests overflow smaller (128K/200K) models.
_CLOUD_DEFAULT_WINDOW = int(os.getenv("TIA_CLOUD_DEFAULT_WINDOW", "128000"))

# Reserve tokens for the response
RESPONSE_RESERVE = 2000
# Minimum messages to keep (system + last N exchanges)
MIN_RECENT_EXCHANGES = 3


def get_token_budget(model: str = None, context_window: Optional[int] = None) -> int:
    """Token budget (context window) for a model.

    Priority:
      1. ``context_window`` argument — the model's REAL window, supplied by the
         C# host. Most accurate; use it when available.
      2. TIA_TOKEN_BUDGET_DEFAULT env var, if explicitly set (operator ceiling).
      3. Conservative default (128K, override via TIA_CLOUD_DEFAULT_WINDOW).
    """
    if context_window and context_window > 0:
        return int(context_window)
    if os.getenv("TIA_TOKEN_BUDGET_DEFAULT"):
        return _DEFAULT_BUDGET
    return _CLOUD_DEFAULT_WINDOW


def trim_messages(
    messages: List[BaseMessage],
    model: str = "default",
    max_tokens: Optional[int] = None,
    min_recent: int = MIN_RECENT_EXCHANGES,
    context_window: Optional[int] = None,
) -> List[BaseMessage]:
    """Trim messages to fit within token budget.

    Strategy:
      1. Always keep SystemMessage(s) at the front
      2. Keep the most recent N exchanges (user + assistant pairs)
      3. If still over budget, summarize older messages
      4. Never drop the last user message

    Returns trimmed list (new list, original unchanged).
    """
    if not messages:
        return []

    budget = max_tokens or get_token_budget(model, context_window)
    budget = max(budget - RESPONSE_RESERVE, 1000)  # Reserve for response

    # Separate system messages
    system_msgs = [m for m in messages if isinstance(m, SystemMessage)]
    other_msgs = [m for m in messages if not isinstance(m, SystemMessage)]

    # Always keep system messages
    system_tokens = count_total_tokens(system_msgs)
    available = budget - system_tokens

    if available <= 0:
        # System prompt alone exceeds budget — truncate system messages
        logger.warning(f"[CTX] System prompt ({system_tokens} tokens) exceeds budget ({budget})")
        truncated_system = []
        remaining = budget
        for sm in system_msgs:
            content = sm.content if hasattr(sm, "content") else ""
            if isinstance(content, str) and len(content) > remaining * 4:
                content = content[:remaining * 4]
            truncated_system.append(SystemMessage(content=str(content)))
            break  # Only keep first system message
        return truncated_system + other_msgs[-min_recent * 2:]

    # If total fits, return as-is
    total = system_tokens + count_total_tokens(other_msgs)
    if total <= budget:
        return list(messages)

    # Keep last N exchanges
    # Each exchange = HumanMessage + AIMessage (possibly with tool calls)
    kept = []
    kept_tokens = 0
    exchange_count = 0

    # Always keep the last message (user's current request)
    for msg in reversed(other_msgs):
        msg_tokens = count_message_tokens(msg)

        if kept_tokens + msg_tokens > available:
            # If we haven't kept enough exchanges yet, force-keep this one
            if exchange_count < min_recent:
                kept.insert(0, msg)
                kept_tokens += msg_tokens
                if isinstance(msg, HumanMessage):
                    exchange_count += 1
            else:
                # Truncate older messages
                break
        else:
            kept.insert(0, msg)
            kept_tokens += msg_tokens
            if isinstance(msg, HumanMessage):
                exchange_count += 1

    result = system_msgs + kept
    logger.debug(
        f"[CTX] Trimmed messages: {len(messages)} → {len(result)}, "
        f"tokens: {total} → {count_total_tokens(result)}/{budget}"
    )
    return result


# ── History compression ──────────────────────────────────────

HISTORY_COMPRESSION_PROMPT = """Summarize the following conversation concisely. Preserve:
1. What the user asked for (original task)
2. What was done (key actions, tools called, code generated)
3. Any errors encountered and how they were resolved
4. Current state of the task (what's done, what remains)

Previous conversation:
{history}

Concise summary (2-4 sentences):"""


async def compress_history(
    messages: List[BaseMessage],
    llm=None,
    max_summary_tokens: int = 500,
) -> Optional[str]:
    """Compress old messages into a summary using LLM.

    Args:
        messages: Messages to compress (should be older messages, not recent ones)
        llm: LangChain ChatOpenAI instance (optional — if None, returns None)
        max_summary_tokens: Target max tokens for the summary

    Returns:
        Summary string, or None if compression not possible.
    """
    if not messages or not llm:
        return None

    # Build history text from messages
    history_parts = []
    for msg in messages:
        role = "User" if isinstance(msg, HumanMessage) else "Assistant" if isinstance(msg, AIMessage) else "System"
        content = msg.content if hasattr(msg, "content") else str(msg)
        if isinstance(content, list):
            content = " ".join(str(b) for b in content)
        content = str(content)[:500]  # Truncate each message for the compression prompt
        history_parts.append(f"[{role}]: {content}")

    history_text = "\n".join(history_parts[-20:])  # At most 20 messages for compression input

    prompt = HISTORY_COMPRESSION_PROMPT.format(history=history_text)

    try:
        response = await llm.ainvoke([HumanMessage(content=prompt)])
        summary = response.content if hasattr(response, "content") else str(response)
        # Truncate summary to target tokens
        if count_tokens(summary) > max_summary_tokens:
            summary = summary[:max_summary_tokens * 4]
        return summary.strip()
    except Exception as e:
        logger.warning(f"[CTX] History compression failed: {e}")
        return None


def inject_summary(
    messages: List[BaseMessage],
    summary: str,
    after_system: bool = True,
) -> List[BaseMessage]:
    """Inject a conversation summary into the message list.

    Args:
        messages: Current message list
        summary: Summary text to inject
        after_system: If True, insert after system messages; otherwise append at start

    Returns:
        New message list with summary injected.
    """
    result = list(messages)
    summary_msg = HumanMessage(content=f"[Previous conversation summary]\n{summary}")

    if after_system:
        # Insert after last system message
        insert_idx = 0
        for i, msg in enumerate(result):
            if isinstance(msg, SystemMessage):
                insert_idx = i + 1
        result.insert(insert_idx, summary_msg)
    else:
        result.insert(0, summary_msg)

    return result


# ── Message construction helpers ─────────────────────────────

def build_initial_messages(
    system_prompt: str,
    prompt: str,
    context: str = "",
    history: Optional[List[Dict[str, str]]] = None,
    max_history_exchanges: int = 5,
) -> List[BaseMessage]:
    """Build the initial message list for an agent run (currently unreferenced).

    Applies:
      - Smart context truncation
      - History truncation (last N exchanges)
      - Token budget awareness
    """
    messages: List[BaseMessage] = []

    # System prompt
    if context:
        truncated_ctx = truncate_context(context)
        system_prompt += f"\n\n## Project Context\n{truncated_ctx}"
    messages.append(SystemMessage(content=system_prompt))

    # History — keep last N exchanges
    if history:
        # A leading `system` entry is the conversation summary (produced by the
        # C# side when compacting long history). It must survive independently of
        # the "last N exchanges" window, otherwise the compressed history is lost.
        summary_msgs: List[BaseMessage] = []
        for item in history:
            if str(item.get("role", "")).strip().lower() == "system":
                content = str(item.get("content", "")).strip()
                if content:
                    summary_msgs.append(SystemMessage(content=content))
        messages.extend(summary_msgs)

        # Each exchange = user + assistant pair
        recent = history[-(max_history_exchanges * 2):]
        for item in recent:
            role = str(item.get("role", "")).strip().lower()
            content = str(item.get("content", "")).strip()
            if not content:
                continue
            # Truncate long history messages
            if len(content) > 3000:
                content = content[:1500] + "\n... [truncated] ...\n" + content[-1500:]
            if role == "assistant":
                messages.append(AIMessage(content=content))
            elif role == "user":
                messages.append(HumanMessage(content=content))
            # `system` entries are handled above as summary_msgs (skip here to
            # avoid duplicating the summary when it falls inside the recent window)

    # Current user prompt
    messages.append(HumanMessage(content=prompt))

    return messages


def ensure_token_budget(
    messages: List[BaseMessage],
    model: str = "default",
    max_tokens: Optional[int] = None,
    context_window: Optional[int] = None,
) -> List[BaseMessage]:
    """Ensure messages fit within token budget. Trims if needed."""
    budget = max_tokens or get_token_budget(model, context_window)
    budget = max(budget - RESPONSE_RESERVE, 1000)

    total = count_total_tokens(messages)
    if total <= budget:
        return messages

    logger.info(f"[CTX] Token budget exceeded: {total}/{budget}, trimming...")
    return trim_messages(messages, model=model, max_tokens=max_tokens, context_window=context_window)


# ── Streaming chunk extraction ───────────────────────────────

def extract_stream_delta(chunk) -> str:
    """Extract text delta from a streaming LLM chunk.

    Handles dict chunks (langgraph astream_events) and object chunks
    (langchain streaming) with common attribute names.

    Args:
        chunk: A chunk from LLM streaming (dict or object with .content/.text).

    Returns:
        Extracted text string, or empty string if no text found.
    """
    if chunk is None:
        return ""
    if isinstance(chunk, dict):
        for k in ("content", "text", "delta", "message"):
            if k in chunk and chunk.get(k) is not None:
                v = chunk.get(k)
                if isinstance(v, list):
                    return "".join(str(x) for x in v)
                return str(v)
        return ""
    content = (
        getattr(chunk, "content", None)
        or getattr(chunk, "text", None)
        or getattr(chunk, "message", None)
    )
    if content is None:
        return ""
    if isinstance(content, list):
        return "".join(str(x) for x in content)
    return str(content)


def extract_stream_reasoning(chunk) -> str:
    """Extract reasoning/thinking delta from a streaming LLM chunk.

    Reasoning models exposed through the OpenAI-compatible API (DeepSeek-R1,
    Qwen/QwQ, Kimi, o1-style, etc.) stream their chain-of-thought in a
    separate ``reasoning_content`` (sometimes ``reasoning``) field rather than
    in ``content``. LangChain surfaces it under ``additional_kwargs`` on the
    message chunk, or directly on dict chunks from ``astream_events``.

    Args:
        chunk: A chunk from LLM streaming (dict or object).

    Returns:
        Extracted reasoning text, or empty string if none present.
    """
    if chunk is None:
        return ""

    def _from_mapping(m) -> str:
        if not isinstance(m, dict):
            return ""
        for k in ("reasoning_content", "reasoning"):
            v = m.get(k)
            if v:
                if isinstance(v, list):
                    return "".join(str(x) for x in v)
                return str(v)
        return ""

    if isinstance(chunk, dict):
        # Direct fields on the dict chunk.
        direct = _from_mapping(chunk)
        if direct:
            return direct
        # Nested under additional_kwargs.
        return _from_mapping(chunk.get("additional_kwargs"))

    # Object chunk (AIMessageChunk etc.)
    ak = getattr(chunk, "additional_kwargs", None)
    nested = _from_mapping(ak)
    if nested:
        return nested
    # Some providers expose it as a top-level attribute.
    for attr in ("reasoning_content", "reasoning"):
        v = getattr(chunk, attr, None)
        if v:
            if isinstance(v, list):
                return "".join(str(x) for x in v)
            return str(v)
    return ""
