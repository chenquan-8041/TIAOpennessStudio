"""
Chat Agent — handles general conversation and project-related questions.

This agent is a LangGraph ReAct agent specialized in conversational responses.
It is bound to the chat toolset (read-only access over the shared catalog) and is
invoked by the Supervisor for Chat intent. Unlike the old approach where the
Supervisor answered directly via llm.invoke(), this agent uses streaming
(astream_events) for natural, incremental output.
"""

import json
import logging
from typing import AsyncIterator, Tuple

from langchain_openai import ChatOpenAI
from langchain.agents import create_agent

from tools.meta_tools import display_tool_name
from core.prompts import CHAT_SYSTEM_PROMPT
from core.context import extract_stream_delta, extract_stream_reasoning, build_user_content
from agent.middleware_config import build_subagent_middleware

logger = logging.getLogger("backend")


def create_chat_agent(llm: ChatOpenAI, tools, context_window: int = 0):
    """Create a compiled ReAct agent for conversational responses.

    Args:
        llm: The ChatOpenAI instance configured for the cloud LLM.
        tools: The chat toolset from ``build_agent_toolsets()`` (required).

    Returns:
        A compiled LangGraph StateGraph ready for ainvoke/astream.
    """
    if not tools:
        raise ValueError("create_chat_agent requires the chat toolset (toolsets['chat'])")
    return create_agent(
        llm,
        tools=tools,
        system_prompt=CHAT_SYSTEM_PROMPT,
        middleware=build_subagent_middleware(llm, context_window=context_window),
    )


async def run_chat_stream(
    llm: ChatOpenAI,
    user_prompt: str,
    tools,
    context: str = "",
    history: list = None,
    images: list = None,
    context_window: int = 0,
) -> AsyncIterator[Tuple[str, str]]:
    """Run the Chat agent and yield SSE events.

    Args:
        llm: Configured ChatOpenAI instance.
        user_prompt: The user's chat message.
        context: Additional project context.
        history: Optional conversation history.
        images: Optional base64-encoded PNG images for vision-capable models.

    Yields:
        ("content", delta)   — streaming chat output
        ("tool_call", json)  — tool call events
        ("thinking", message) — status messages
        ("done", "ok")       — completion
    """
    agent = create_chat_agent(llm, tools=tools, context_window=context_window)

    user_message = user_prompt
    if context:
        user_message += f"\n\nProject Context: {context}"

    # Build messages with history
    messages: list = []
    if history:
        # Preserve the conversation-summary system entry (C# compaction) outside
        # the recent-exchange window so compressed history isn't dropped.
        for h in history:
            if str(h.get("role", "")).strip().lower() == "system":
                content = h.get("content", "")
                if content:
                    messages.append({"role": "system", "content": content})
        for h in history[-5:]:  # Last 5 exchanges
            role = str(h.get("role", "")).strip().lower()
            content = h.get("content", "")
            if role == "user":
                messages.append({"role": "user", "content": content})
            elif role == "assistant":
                messages.append({"role": "assistant", "content": content})
    # The current turn carries images (multimodal) when present.
    messages.append({"role": "user", "content": build_user_content(user_message, images)})

    tool_name_by_run: dict = {}

    try:
        content_yielded = False

        async for event in agent.astream_events(
            {"messages": messages}, version="v2"
        ):
            kind = event["event"]

            if kind == "on_chat_model_stream":
                chunk = (event.get("data") or {}).get("chunk")
                reasoning = extract_stream_reasoning(chunk)
                if reasoning:
                    yield "thinking", reasoning
                delta = extract_stream_delta(chunk)
                if delta:
                    content_yielded = True
                    yield "content", delta

            elif kind == "on_tool_start":
                tool_input = event.get("data", {}).get("input", {})
                tool_name = display_tool_name(event["name"], tool_input)
                tool_name_by_run[event.get("run_id")] = tool_name
                yield "tool_call", json.dumps({
                    "name": tool_name,
                    "input": {k: str(v)[:200] for k, v in (tool_input or {}).items()},
                    "status": "started",
                    "agent": "chat",
                }, ensure_ascii=False)

            elif kind == "on_tool_end":
                tool_name = tool_name_by_run.pop(event.get("run_id"), event["name"])
                tool_output = str(event.get("data", {}).get("output", ""))
                yield "tool_call", json.dumps({
                    "name": tool_name,
                    "output": tool_output[:500],
                    "status": "completed",
                    "agent": "chat",
                }, ensure_ascii=False)

        # Fallback: non-streaming LLM (e.g. /generate endpoint) won't
        # produce on_chat_model_stream events. Use ainvoke instead.
        if not content_yielded:
            logger.info("[CHAT] No stream content, falling back to ainvoke")
            result = await agent.ainvoke({"messages": messages})
            msgs = result.get("messages", [])
            last_msg = msgs[-1] if msgs else None
            full_text = last_msg.content if last_msg and hasattr(last_msg, "content") else ""
            if full_text:
                yield "content", full_text

        yield "done", "ok"

    except Exception as e:
        logger.exception(f"[CHAT] execution failed: {e}")
        yield "error", str(e)
        yield "done", "ok"