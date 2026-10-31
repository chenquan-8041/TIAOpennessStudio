"""
Meta-tools — ``discover_tools`` and ``invoke_tool``, the ONLY tools bound to each
agent role under the progressive-disclosure design (Design A).

Instead of binding every business tool schema into the LLM context, each role is
bound to just these two tiny meta-tools:

  - discover_tools(query?, category?) → query the shared catalog and return the
    accessible tools' one-line description + category + parameter signature.
  - invoke_tool(name, args_json)      → route ``name`` to the real tool object and
    run it, enforcing the role's access mask (fail-closed).

Access enforcement lives here: previously "not bound = can't call" was the guard;
now the LLM can attempt any ``name``, so ``invoke_tool`` must reject unknown or
role-inaccessible tools. Write tools are only allowed for ``coder`` / ``compiler``.
"""

import asyncio
import difflib
import json
import logging
import re
from typing import Any, Dict

from langchain_core.tools import tool

from core.request_context import get_rid
from tools.catalog import get_catalog

logger = logging.getLogger("backend")

_WRITE_ROLES = frozenset({"coder", "compiler"})

# Read-only tools whose results are immutable for the life of a run: coding-standard
# libraries, Openness docs, library specs and samples. Reusing them across turns and
# across agent roles (coder → reviewer) stops the same section being read again and
# again. Project reads are deliberately NOT cached — the project changes under us.
_IMMUTABLE_READ_TOOLS = frozenset({
    "list_standard_topics",
    "read_standard_section",
    "search_openness_docs",
    "list_plc_openness_topics",
    "get_plc_openness_api_methods",
    "get_plc_openness_csharp_template",
    "get_plc_runtime_diagnostic_api",
    "get_plc_runtime_analysis_recipe",
    "list_library_types",
    "get_library_type_details",
    "list_openness_samples",
    "get_openness_sample",
    "describe_knowledge_schema",
})

# Extra search terms per tool. The catalog descriptions are English, but users and the
# model often query in Chinese; without these aliases a query like "标准 章节" matches
# nothing even though the tool is right there. Keyed by canonical tool name.
_ALIASES: Dict[str, str] = {
    "list_standard_topics": "标准 规范 知识库 目录 书签 标题 standard library topics outline bookmark",
    "read_standard_section": "标准 规范 章节 内容 读取 书签 section standard read",
    "get_block_content": "块 程序块 内容 读取 源码 代码 xml json 接口 block content read source interface",
    "patch_block_network": "修改 网络 补丁 就地 patch network block",
    "patch_block_interface": "修改 接口 成员 补丁 interface patch block",
    "import_block": "导入 写入 程序块 块 代码 新建 scl block import write",
    "delete_block": "删除 程序块 块 block delete",
    "search_project": "搜索 查找 项目 代码 变量 引用 search project",
    "get_program_tree": "目录 树 程序块 结构 program tree",
    "list_tags": "变量 标签 全局 表 tag variable",
    "get_db_structure": "数据块 db 结构 成员 data block structure",
    "list_udt_types": "udt 类型 数据类型 type",
    "query_variable_type": "变量 类型 查询 type variable",
    "query_variable_references": "变量 引用 交叉引用 reference",
    "query_call_chain": "调用 链 调用关系 call chain",
    "query_variable_impact": "变量 影响 传播 impact",
    "compile": "编译 检查 compile",
    "compile_block": "编译 单个块 检查 compile block",
    "compile_snapshot": "编译 基线 快照 baseline snapshot",
    "compile_diff": "编译 差异 新增 错误 diff",
    "read_file": "文件 读取 file read",
    "write_file": "文件 写入 file write",
    "list_dir": "目录 文件 列表 dir list",
    "query_siemens_platform_matrix": "西门子 平台 差异 兼容 型号 cpu 家族 数据类型 指令 报错 查询 dtl wstring r_trig siemens platform matrix query",
}


# Retired in v1.34: forwarding aliases of get_block_content. Kept only so a saved plan /
# old client still resolves; the names are not advertised anywhere the model can see.
_LEGACY_READ_ALIASES: Dict[str, str] = {
    "export_block": "xml",
    "get_block_scl_code": "source",
    "export_block_ir": "ir",
}


def _alias_text(name: str) -> str:
    """Alias keywords for a catalog entry."""
    return _ALIASES.get(name, "")


# Identical read calls already in flight are awaited instead of launched again (the
# model routinely emits the same call three times in one batch).
_INFLIGHT: Dict[str, asyncio.Task] = {}
# Cache for _IMMUTABLE_READ_TOOLS, keyed per run.
_CACHE: Dict[str, str] = {}
_MAX_CACHE_ENTRIES = 300

# Hard backstop against a single tool dumping a whole block into the context. Code
# reads use the tool's own offset/length paging (default 20k); this only catches the
# rest and still allows an explicit larger paging request.
_MAX_OUTPUT_CHARS = 60000


def _cache_key(name: str, args: dict) -> str:
    return "{}|{}|{}".format(
        get_rid() or "-",
        name,
        json.dumps(args, sort_keys=True, ensure_ascii=False),
    )


def _cache_put(key: str, value: str) -> None:
    if len(_CACHE) >= _MAX_CACHE_ENTRIES:
        _CACHE.clear()
    _CACHE[key] = value


async def _run_shared(key: str, tool_obj, args: dict) -> str:
    """Run ``tool_obj(args)`` once per in-flight key, sharing the result with duplicates."""
    task = _INFLIGHT.get(key)
    created = task is None
    if task is None:
        task = asyncio.ensure_future(tool_obj.ainvoke(args))
        _INFLIGHT[key] = task
    try:
        output = await asyncio.shield(task)
    finally:
        if created:
            _INFLIGHT.pop(key, None)
    return _tool_output_to_text(output)


def _cap_output(name: str, text: str) -> str:
    """Truncate an oversized read result with an explicit, actionable marker."""
    if not isinstance(text, str) or len(text) <= _MAX_OUTPUT_CHARS:
        return text
    head = _MAX_OUTPUT_CHARS - 4000
    tail = 3000
    return (
        text[:head]
        + "\n\n[... {} output truncated: {} chars total; showing the first {} and the last {}. "
          "Narrow the request, or page it (get_block_content / read_standard_section accept "
          "offset+length).]\n\n".format(name, len(text), head, tail)
        + text[-tail:]
    )


def build_discover_tool(role: str):
    """Build a per-role ``discover_tools`` meta-tool bound to the shared catalog."""

    @tool
    def discover_tools(query: str = "", category: str = "") -> str:
        """Query the tool catalog and return the accessible tools' names, one-line
        purpose, category, and parameters.

        Call this first when you need to know which tools exist or are unsure about a
        tool's parameters; once you have a name, use invoke_tool(name, args_json) to
        actually invoke it. Pass keywords in ``query`` to filter by name/purpose/
        category; leave it empty to return all accessible tools at once.
        """
        catalog = get_catalog()
        entries = [e for e in catalog.values() if role in e.roles]
        if category:
            entries = [e for e in entries if e.category == category]
        if query:
            # Match per keyword, not the whole query string: multi-word queries such as
            # "block source compile" can never be a substring of one tool entry, which
            # made discovery look empty and drove the model to guess tool names.
            tokens = [t for t in re.split(r"[\s,;./\\]+", query.strip().lower()) if t]
            # Chinese queries are often written without spaces ("标准章节"). Add the
            # 2-char CJK bigrams so they can still hit the per-word aliases.
            expanded = []
            for t in tokens:
                expanded.append(t)
                if len(t) > 2 and any('\u4e00' <= c <= '\u9fff' for c in t):
                    expanded.extend(t[i:i + 2] for i in range(len(t) - 1))
            tokens = expanded

            def _score(entry) -> int:
                hay = (
                    entry.name + " " + entry.short_desc + " " + entry.category +
                    " " + _alias_text(entry.name)
                ).lower()
                return sum(1 for t in tokens if t in hay)

            scored = [(_score(e), e) for e in entries]
            scored = [(s, e) for s, e in scored if s > 0]
            if scored:
                scored.sort(key=lambda se: (-se[0], se[1].category, se[1].name))
                entries = [e for _, e in scored]
            else:
                names = ", ".join(sorted(e.name for e in entries)[:25])
                return (
                    "No tools matched '" + query + "'. Available tool names: " + names +
                    ". Call discover_tools with no arguments for the full list."
                )
        if not entries:
            return "No matching tools found. Call discover_tools with no arguments to see all accessible tools."
        entries.sort(key=lambda e: (e.category, e.name))
        lines = []
        for e in entries:
            w = " [write · requires approval]" if e.access == "write" else ""
            p = f" params({e.params})" if e.params else ""
            lines.append(f"- {e.name} [{e.category}]{w}\n    {e.short_desc}{p}")
        return "Accessible tools (invoke via invoke_tool(name, args_json)):\n" + "\n".join(lines)

    return discover_tools


def _tool_output_to_text(output) -> str:
    """Normalize a tool's raw return value into a single string for the agent.

    local/static tools return a str. MCP tools are created with
    ``response_format="content_and_artifact"``, so ``ainvoke`` returns a list of
    LangChain message content blocks (dicts like ``{"type": "text", "text": ...}``);
    flatten those into plain text. A ToolMessage/AIMessage exposes the text under
    ``.content``.
    """
    if isinstance(output, str):
        return output
    content = getattr(output, "content", output)
    if isinstance(content, list):
        parts = []
        for block in content:
            if isinstance(block, str):
                parts.append(block)
            elif isinstance(block, dict) and block.get("type") == "text":
                parts.append(str(block.get("text", "")))
            else:
                parts.append(str(block))
        return "\n".join(p for p in parts if p)
    return str(content)


def build_invoke_tool(role: str):
    """Build a per-role ``invoke_tool`` meta-tool that dispatches by name."""

    @tool
    async def invoke_tool(name: str, args_json: str = "") -> str:
        """Generic tool executor: invoke a tool in the catalog accessible to this role by name.

        Args:
            name: Tool name (as returned by discover_tools).
            args_json: JSON string of arguments, e.g. {"query":"Motor","limit":5}; leave empty for tools without parameters.

        Returns:
            The tool's actual returned text; failures start with "Error:".
        """
        catalog = get_catalog()

        if name in ("discover_tools", "invoke_tool"):
            return (
                f"Error: '{name}' is a meta-tool bound directly to you, not a catalog tool. "
                "Call it directly; do not route it through invoke_tool. Use discover_tools "
                "with no arguments to list every accessible tool."
            )

        # v1.34 retired the three get_block_content aliases from the tool surface, but a saved
        # plan or an old client may still carry the old name. Map it onto the unified read
        # instead of failing the whole step (never advertised, so the model cannot pick it).
        legacy_format = _LEGACY_READ_ALIASES.get(name)
        if legacy_format:
            name = "get_block_content"

        entry = catalog.get(name)
        if entry is None:
            close = difflib.get_close_matches(name, sorted(catalog.keys()), n=5, cutoff=0.45)
            hint = (" Closest names: " + ", ".join(close) + ".") if close else ""
            return (
                f"Error: unknown tool: {name}.{hint} Call discover_tools directly with no "
                "arguments to see every accessible tool name; do not guess."
            )
        if role not in entry.roles:
            return f"Error: unauthorized tool for role '{role}': {name} (the tool exists but this role cannot use it)."
        if entry.access == "write" and role not in _WRITE_ROLES:
            return f"Error: write tool not allowed for role '{role}': {name}"
        try:
            raw = (args_json or "").strip()
            args = json.loads(raw) if raw else {}
            if not isinstance(args, dict):
                return f"Error: args_json must be a JSON object (got {type(args).__name__})"
            if legacy_format:
                args.setdefault("format", legacy_format)

            # Writes are never shared/cached: two identical writes must each go through
            # approval and actually execute.
            if entry.access == "write":
                # Deterministic CPU-family guard: an unsupported datatype in a UDT/DB spec
                # imports "successfully" and then can never be exported again, so refuse it
                # before it reaches TIA (rules come from the single platform matrix).
                from tools.platform_guard import validate_write_spec

                refusal = await asyncio.to_thread(validate_write_spec, name, args)
                if refusal:
                    return refusal
                output = await entry.tool.ainvoke(args)
                return _cap_output(name, _tool_output_to_text(output))

            # ainvoke works for BOTH sync (local/static) and async (MCP) tools.
            # MCP tools are async-only and raise "StructuredTool does not support
            # sync invocation" when called via .invoke().
            key = _cache_key(name, args)
            if name in _IMMUTABLE_READ_TOOLS and key in _CACHE:
                return _CACHE[key]
            text = await _run_shared(key, entry.tool, args)
            text = _cap_output(name, text)
            if name in _IMMUTABLE_READ_TOOLS:
                _cache_put(key, text)
            return text
        except json.JSONDecodeError as e:
            return f"Error: invalid args_json: {e}"
        except Exception as e:
            return f"Error: tool invocation failed ({name}): {e}"

    return invoke_tool


def display_tool_name(name: str, tool_input: Any) -> str:
    """Recover the real tool name from a meta-tool call for observability.

    When ``invoke_tool`` is used, the LLM-facing tool name is ``invoke_tool`` but the
    actual tool is ``tool_input["name"]``. Failure signals / logging / evolution
    recording must key off the real name, not ``invoke_tool``.
    """
    if name == "invoke_tool" and isinstance(tool_input, dict):
        inner = tool_input.get("name")
        if isinstance(inner, str) and inner:
            return inner
    return name