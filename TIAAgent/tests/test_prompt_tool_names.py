"""Prompt/tool drift guard: every tool name the prompts mention must be reachable via the catalog.

Why this exists: the system prompts are prose, but the tools they name are code. A tool can be
renamed or retired (``import_block_code`` → ``import_block``, ``compile_project`` → ``compile``)
without any test noticing, and the model then calls a name that ``invoke_tool`` rejects with
``unknown tool``. These tests fail closed on that class of drift.

No LLM and no TIA Portal required: the catalog's static name sets are the source of truth here,
so this also runs in CI where the :8001 MCP server is absent.

Run (from the TIAAgent directory):
    python -m unittest tests/test_prompt_tool_names.py
    # or: pytest tests/test_prompt_tool_names.py
"""

import os
import re
import sys
import unittest

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from tools.catalog import (  # noqa: E402
    _MCP_READONLY_TOOLS,
    _MCP_WRITE_TOOLS,
    _META,
    _STATIC_KNOWLEDGE,
    ToolEntry,
)

PROMPTS_PATH = os.path.join(_ROOT, "core", "prompts.py")
with open(PROMPTS_PATH, encoding="utf-8") as _f:
    PROMPT_SOURCE = _f.read()

# Names the model can actually invoke through discover_tools / invoke_tool, plus the two meta-tools.
# Derived from catalog.py's own declarations so a tool being (re)classified cannot silently pass.
KNOWN_TOOLS = (
    set(_MCP_WRITE_TOOLS)
    | set(_MCP_READONLY_TOOLS)
    | set(_META)
    | {getattr(t, "name", "") for t in _STATIC_KNOWLEDGE}
    | {"discover_tools", "invoke_tool"}
)

# REACT_SYSTEM_PROMPT targets the legacy C# ReActEngine, which has its own tool registry; its names
# are not in the Python catalog and must not be reported as drift.
LEGACY_REACT_TOOLS = {
    "search_openness_docs",
    "list_project_blocks",
    "list_tag_tables",
}

# Names that used to exist and must never come back into the prompts.
RETIRED_TOOL_NAMES = {
    "import_block_code",       # removed REST wrapper; the tool is `import_block`
    "import_block_code_rest",  # the removed wrapper's renamed form
    "compile_project",         # the real tool is `compile` (structured result)
}

# Backticked snake_case tokens in the prompts that are NOT tool names.
NON_TOOL_TOKENS = {
    "approval_timeout",
    "approval_unavailable",
    "run_cancelled",
    "state_changed",
    "user_denied",
    "group_path",
    "type_group_path",   # planner task field, not a tool argument name
    "tia_version",
    "memory_layout",     # build_global_db argument name, not a tool
    # patch tool parameters (argument names, not tools)
    "base_revision",
    "compile_unit_id",
    # compile_diff result fields (JSON keys, not tools)
    "new_errors",
    "pre_existing_errors",
    "resolved_errors",
}


def _token_re(name):
    return re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])")


class PromptToolNameTest(unittest.TestCase):
    def test_retired_tool_names_are_absent(self):
        """Renamed/retired tools must not be named anywhere in the prompts."""
        for name in sorted(RETIRED_TOOL_NAMES):
            self.assertIsNone(
                _token_re(name).search(PROMPT_SOURCE),
                f"prompts.py still references retired tool '{name}'; "
                f"use a name from the live catalog instead",
            )

    def test_known_tool_names_are_not_stale(self):
        """Any catalog-derived name mentioned must still be a current, reachable name."""
        allowed = KNOWN_TOOLS | LEGACY_REACT_TOOLS
        mentioned = {name for name in KNOWN_TOOLS if _token_re(name).search(PROMPT_SOURCE)}
        unknown = mentioned - allowed
        self.assertEqual(unknown, set(), f"prompts.py references tools missing from the catalog: {unknown}")

    def test_backticked_tool_tokens_exist(self):
        """Every backticked snake_case token is either a real tool name or a known non-tool token."""
        tokens = set(re.findall(r"`([a-z][a-z0-9_]*(?:_[a-z0-9]+)+)`", PROMPT_SOURCE))
        allowed = KNOWN_TOOLS | LEGACY_REACT_TOOLS | NON_TOOL_TOKENS
        unknown = {t for t in tokens if t not in allowed}
        self.assertEqual(
            unknown, set(),
            f"prompts.py mentions backticked names that are neither catalog tools nor known "
            f"non-tool tokens: {sorted(unknown)}",
        )


class CatalogClassificationTest(unittest.TestCase):
    """Read-only directory tools must never be classified as write tools (that would gate them)."""

    def test_get_program_tree_is_read_only(self):
        self.assertIn("get_program_tree", _MCP_READONLY_TOOLS)
        self.assertNotIn("get_program_tree", _MCP_WRITE_TOOLS)
        self.assertIn("get_program_tree", _META)

    def test_compile_diff_tools_are_registered(self):
        """compile_snapshot / compile_diff must be in the catalog (same tier group as compile)."""
        for name in ("compile_snapshot", "compile_diff"):
            self.assertIn(name, _MCP_WRITE_TOOLS)
            self.assertIn(name, _META)


class CoderPromptRenderTest(unittest.TestCase):
    def test_placeholder_is_always_replaced(self):
        from agent.coder import build_coder_system_prompt
        from core.prompts import CODER_WRITE_TOOLS_PLACEHOLDER

        for generate_only in (False, True):
            text = build_coder_system_prompt(generate_only=generate_only)
            self.assertNotIn(CODER_WRITE_TOOLS_PLACEHOLDER, text)

    def test_write_tool_list_comes_from_the_catalog(self):
        """With a populated catalog the rendered list carries the tool's own parameter signature."""
        from unittest.mock import patch

        import agent.coder as coder

        fake = {
            "import_block": ToolEntry(
                name="import_block",
                tool=object(),
                short_desc="Import SCL/XML into a TIA block",
                category="Write",
                params="block_name:str, block_type:str, code:str, group_path?:str",
                roles=frozenset({"coder"}),
                access="write",
                tier="mcp",
            ),
            "search_project": ToolEntry(
                name="search_project",
                tool=object(),
                short_desc="Search the project",
                category="Knowledge retrieval",
                params="query:str",
                roles=frozenset({"coder"}),
                access="read",
                tier="mcp",
            ),
        }
        with patch.object(coder, "get_catalog", return_value=fake):
            text = coder.build_coder_system_prompt()
        self.assertIn("- import_block params(block_name:str, block_type:str, code:str, group_path?:str)", text)
        self.assertNotIn("search_project params", text)  # read-only tools are not in the write list


if __name__ == "__main__":
    unittest.main()
