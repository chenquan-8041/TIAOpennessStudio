"""
Coder Agent — generates and writes SCL code to TIA Portal.

This agent is a LangGraph ReAct agent specialized in SCL code generation.
It is bound to the coder toolset (discover_tools + invoke_tool over the shared
catalog) and is invoked by the Supervisor.
"""

from langchain_openai import ChatOpenAI
from langchain.agents import create_agent

from tools.catalog import get_catalog
from core.prompts import (
    CODER_SYSTEM_PROMPT,
    CODER_GENERATE_ONLY_ADDENDUM,
    CODER_WRITE_TOOLS_PLACEHOLDER,
    CODER_WRITE_TOOLS_FALLBACK,
)
from agent.middleware_config import build_subagent_middleware


def _render_write_tools(role: str = "coder") -> str:
    """Render the role's write-capable tools from the live catalog.

    One line per tool with the tool's own parameter signature (``catalog`` derives it from the
    tool's arg schema), so the system prompt cannot drift from the real tool names/signatures.
    Falls back to a name-free instruction when the catalog has not been built yet.
    """
    try:
        entries = [
            e for e in get_catalog().values()
            if role in e.roles and e.access == "write" and e.tier == "mcp"
        ]
    except Exception:  # noqa: BLE001 - prompt building must never break the agent
        entries = []
    if not entries:
        return CODER_WRITE_TOOLS_FALLBACK
    entries.sort(key=lambda e: (e.category, e.name))
    lines = []
    for e in entries:
        params = f" params({e.params})" if e.params else ""
        lines.append(f"    - {e.name}{params}  [write · requires approval]")
    return "\n".join(lines)


def build_coder_system_prompt(generate_only: bool = False) -> str:
    """CODER_SYSTEM_PROMPT with the write-tool list rendered from the catalog."""
    prompt = CODER_SYSTEM_PROMPT.replace(CODER_WRITE_TOOLS_PLACEHOLDER, _render_write_tools("coder"))
    if generate_only:
        prompt += CODER_GENERATE_ONLY_ADDENDUM
    return prompt


def create_coder_agent(
    llm: ChatOpenAI,
    tools,
    generate_only: bool = False,
    context_window: int = 0,
):
    """Create a compiled ReAct agent for SCL code generation.

    Args:
        llm: The ChatOpenAI instance configured for the cloud LLM.
        tools: The coder toolset from ``build_agent_toolsets()`` (required).
        generate_only: When True, the coder generates the complete SCL block and
            returns it WITHOUT writing to TIA (the executor writes it afterwards).

    Returns:
        A compiled LangGraph StateGraph ready for ainvoke/astream.
    """
    if not tools:
        raise ValueError("create_coder_agent requires the coder toolset (toolsets['coder'])")
    system_prompt = build_coder_system_prompt(generate_only=generate_only)
    return create_agent(
        llm,
        tools=tools,
        system_prompt=system_prompt,
        middleware=build_subagent_middleware(llm, context_window=context_window),
    )
