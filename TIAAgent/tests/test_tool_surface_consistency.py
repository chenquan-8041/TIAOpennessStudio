"""C# MCP tool surface vs Python catalog drift guard (T15).

Why this exists: the C# server declares the tools the model can call (``tools/list``
in ``TiaContextHttpServer.cs``); the Python catalog classifies each of those names
as read-only or write (``tools/catalog.py``) and attaches curated descriptions. If
the two sides drift, a tool exists but ``invoke_tool`` rejects it, or a tool is
classified with the wrong access level and can bypass the write/approval gate.

The C# declaration is the source of truth here (the list is static and
``tools/list`` serialises it verbatim), so no built exe or running MCP server is
required — the check also runs in CI.

Run (from the TIAAgent directory):
    python -m unittest tests/test_tool_surface_consistency.py
    # or: pytest tests/test_tool_surface_consistency.py
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
)

_REPO_ROOT = os.path.dirname(_ROOT)
SERVER_PATH = os.path.join(
    _REPO_ROOT, "TIAOpennessStudio", "Services", "TiaContextHttpServer.cs")

_DECLARATION = "private static readonly List<object> _mcpToolDefinitions = new List<object>"
_NAME_RE = re.compile(r'new\s*\{\s*name\s*=\s*"([^"]+)"')

with open(SERVER_PATH, encoding="utf-8") as _f:
    _SERVER_SOURCE = _f.read()

_start = _SERVER_SOURCE.index(_DECLARATION)
_end = _SERVER_SOURCE.index("\n        };", _start)
_TOOL_BLOCK = _SERVER_SOURCE[_start:_end]

# Declaration order, exactly what tools/list returns.
C_SHARP_TOOLS = _NAME_RE.findall(_TOOL_BLOCK)

# Python-only tiers (local files + curated static knowledge): not exposed by C#.
PYTHON_ONLY_TIERS = {getattr(t, "name", "") for t in _STATIC_KNOWLEDGE} | {
    "read_file", "write_file", "list_dir", "delete_file", "save_analysis_doc",
}

# Tools whose access level is contract-critical: the read side must never be
# gated as a write, and every mutating tool must be classified as write.
REQUIRED_READONLY = {
    "get_block_content",
    "search_project", "build_context_pack", "describe_knowledge_schema",
    "get_program_tree", "compile_block",
}
REQUIRED_WRITE = {
    "import_block", "delete_block",
    "patch_block_network", "patch_block_interface",
    "build_lad_fc", "build_lad_fb", "build_graph_fb",
    "build_udt", "build_global_db", "build_ob",
}

# The three forwarding aliases of get_block_content were retired in v1.34 (the v1.33
# "keep for one release" window). They must stay out of tools/list, out of the Python
# catalog and out of the curated metadata, or the model picks them at random again.
RETIRED_READ_ALIASES = ("export_block", "get_block_scl_code", "export_block_ir")


class CSharpToolListTest(unittest.TestCase):
    def test_tool_list_is_parsed(self):
        self.assertGreaterEqual(len(C_SHARP_TOOLS), 30, "unexpectedly short MCP tool list")
        self.assertEqual(len(C_SHARP_TOOLS), len(set(C_SHARP_TOOLS)),
                         f"duplicate tool names in C# tools/list: {C_SHARP_TOOLS}")

    def test_retired_aliases_are_gone(self):
        """The alias trio must not be advertised or classified anywhere any more."""
        for alias in RETIRED_READ_ALIASES:
            self.assertNotIn(alias, C_SHARP_TOOLS,
                             f"retired alias '{alias}' is still advertised in C# tools/list")
            self.assertNotIn(alias, _MCP_READONLY_TOOLS,
                             f"retired alias '{alias}' is still classified as read-only")
            self.assertNotIn(alias, _MCP_WRITE_TOOLS,
                             f"retired alias '{alias}' is still classified as write")
            self.assertNotIn(alias, _META,
                             f"retired alias '{alias}' still has curated metadata")


class LegacyAliasBridgeTest(unittest.TestCase):
    """The retired names still resolve (saved plans), but only as a bridge to the unified read."""

    def test_bridge_covers_the_retired_trio(self):
        from tools.meta_tools import _LEGACY_READ_ALIASES
        self.assertEqual(set(_LEGACY_READ_ALIASES), set(RETIRED_READ_ALIASES))
        for name, fmt in _LEGACY_READ_ALIASES.items():
            self.assertIn(fmt, ("ir", "source", "xml", "interface"), f"bad format for {name}")


class CatalogCoverageTest(unittest.TestCase):
    def test_every_csharp_tool_is_classified(self):
        python_mcp = set(_MCP_WRITE_TOOLS) | set(_MCP_READONLY_TOOLS)
        missing = set(C_SHARP_TOOLS) - python_mcp
        self.assertEqual(
            missing, set(),
            f"C# tools/list exposes tools the Python catalog cannot classify "
            f"(invoke_tool would reject them): {sorted(missing)}",
        )

    def test_catalog_has_no_phantom_tools(self):
        python_mcp = set(_MCP_WRITE_TOOLS) | set(_MCP_READONLY_TOOLS)
        phantom = python_mcp - set(C_SHARP_TOOLS)
        self.assertEqual(
            phantom, set(),
            f"Python catalog classifies names that C# tools/list does not expose "
            f"(stale or misspelled): {sorted(phantom)}",
        )

    def test_write_and_readonly_are_disjoint(self):
        overlap = set(_MCP_WRITE_TOOLS) & set(_MCP_READONLY_TOOLS)
        self.assertEqual(overlap, set(), f"tools classified as both read and write: {sorted(overlap)}")

    def test_required_access_levels(self):
        self.assertTrue(REQUIRED_READONLY <= set(_MCP_READONLY_TOOLS),
                        f"missing read-only: {sorted(REQUIRED_READONLY - set(_MCP_READONLY_TOOLS))}")
        self.assertTrue(REQUIRED_WRITE <= set(_MCP_WRITE_TOOLS),
                        f"missing write: {sorted(REQUIRED_WRITE - set(_MCP_WRITE_TOOLS))}")

    def test_every_mcp_tool_has_curated_metadata(self):
        missing = set(C_SHARP_TOOLS) - set(_META)
        self.assertEqual(
            missing, set(),
            f"MCP tools without a curated catalog entry (discover_tools would show the raw "
            f"first description line): {sorted(missing)}",
        )

    def test_meta_has_no_unreachable_entries(self):
        reachable = set(C_SHARP_TOOLS) | PYTHON_ONLY_TIERS
        orphan = set(_META) - reachable
        self.assertEqual(
            orphan, set(),
            f"catalog metadata for names no tier provides (stale entries): {sorted(orphan)}",
        )


if __name__ == "__main__":
    unittest.main()
