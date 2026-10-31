"""
Reviewer Agent — reviews SCL code quality and provides actionable feedback.

This agent is a LangGraph ReAct agent specialized in code review.
It is bound to the reviewer toolset (read-only access over the shared catalog)
and is invoked by the Supervisor.
"""

from langchain_openai import ChatOpenAI
from langchain.agents import create_agent

from core.prompts import REVIEWER_SYSTEM_PROMPT, REVIEWER_ANALYSIS_ADDENDUM
from agent.middleware_config import build_subagent_middleware, _ANALYZE_TOOL_CALL_LIMIT


def create_reviewer_agent(llm: ChatOpenAI, tools, analysis: bool = False, context_window: int = 0):
    """Create a compiled ReAct agent for SCL code review.

    Args:
        llm: The ChatOpenAI instance configured for the cloud LLM.
        tools: The reviewer toolset from ``build_agent_toolsets()`` (required).
        analysis: True for read-only analysis of existing code (ExplainCode / Analyze):
            the agent explains the attached code instead of issuing a PASS/NEEDS_FIX verdict.

    Returns:
        A compiled LangGraph StateGraph ready for ainvoke/astream.
    """
    if not tools:
        raise ValueError("create_reviewer_agent requires the reviewer toolset (toolsets['reviewer'])")
    system_prompt = REVIEWER_SYSTEM_PROMPT + (REVIEWER_ANALYSIS_ADDENDUM if analysis else "")
    return create_agent(
        llm,
        tools=tools,
        system_prompt=system_prompt,
        middleware=build_subagent_middleware(
            llm,
            context_window=context_window,
            # Read-only analysis has no write to converge on: cap its lookups so a
            # permanent 0-hit lookup cannot ping-pong for hundreds of calls.
            tool_call_limit=_ANALYZE_TOOL_CALL_LIMIT if analysis else None,
        ),
    )
