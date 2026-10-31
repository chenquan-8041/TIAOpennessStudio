"""
Shared cached tool catalog — the single source of truth for every tool the cloud
agents can use.

Every tool (MCP-enumerated + local file + static knowledge) is registered ONCE
with a compact metadata entry: a one-line description, a category, and a compact
parameter signature. Agents never bind the full tool schemas; instead they query
this catalog via the ``discover_tools`` meta-tool and invoke tools via the
``invoke_tool`` meta-tool (see tools/meta_tools.py).

Design notes:
  - TIA capabilities come from the C# MCP server only. Its REST copy is not a
    model-facing fallback; deterministic Python call sites use tools/tia_client.py.
  - Classification of MCP tools (read-only / write) lives here so the catalog is
    the single source.
  - Catalog is built once and cached at process level (mirrors
    mcp_client._cached_tools); ``reset_catalog`` clears it for tests/config refresh.
"""

import logging
from dataclasses import dataclass
from typing import Any, Dict, Optional

from tools.mcp_client import load_mcp_tools
from tools.file_tools import (
    read_file, write_file, list_dir, delete_file, save_analysis_doc,
)
from tools.openness_samples_tool import list_openness_samples, get_openness_sample
from tools.siemens_platform import query_siemens_platform_matrix
from tools.definitions import (
    search_openness_docs,
    list_plc_openness_topics,
    get_plc_openness_api_methods,
    get_plc_openness_csharp_template,
    get_plc_runtime_diagnostic_api,
    get_plc_runtime_analysis_recipe,
    list_library_types,
    get_library_type_details,
    query_openness_api,
)

logger = logging.getLogger("backend")

# ── Classification (moved from tools/registry.py) ──────────────────────────

_MCP_WRITE_TOOLS = frozenset({
    "import_block", "delete_block", "compile",
    "compile_snapshot", "compile_diff",
    "build_lad_fc", "build_lad_fb", "build_graph_fb", "build_udt", "build_global_db",
    "build_ob",
    "patch_block_network", "patch_block_interface",
    "repair_inconsistent_types",
    "configure_device", "set_device_attribute", "add_module", "configure_subnet",
})
_MCP_READONLY_TOOLS = frozenset({
    "search_project", "build_context_pack", "describe_knowledge_schema",
    "query_variable_references", "query_call_chain", "query_variable_impact",
    "list_tags", "get_db_structure",
    "get_program_tree",
    "get_block_content",
    "list_udt_types", "probe_openness_capabilities",
    "list_standard_topics", "read_standard_section", "get_current_project",
    "get_environment_profile",
    "query_variable_type",
    "query_repair_experience",
    "list_block_groups", "list_type_groups",
    "build_hmi_screen", "build_hmi_tag_table",
    "compile_block",
})


def _split_mcp_tools(mcp_tools):
    readonly, write, unknown = [], [], []
    for t in mcp_tools:
        name = getattr(t, "name", "")
        if name in _MCP_WRITE_TOOLS:
            write.append(t)
        elif name in _MCP_READONLY_TOOLS:
            readonly.append(t)
        else:
            unknown.append(t)
    return readonly, write, unknown


_STATIC_KNOWLEDGE = [
    search_openness_docs,
    list_plc_openness_topics,
    get_plc_openness_api_methods,
    get_plc_openness_csharp_template,
    get_plc_runtime_diagnostic_api,
    get_plc_runtime_analysis_recipe,
    list_library_types,
    get_library_type_details,
    list_openness_samples,
    get_openness_sample,
    query_siemens_platform_matrix,
    query_openness_api,
]

_FOUR_ROLES = ("coder", "reviewer", "compiler", "chat")


# ── Curated metadata (one-line description + category) ─────────────────────
# Keyed by the tool name agents call. Params are derived from each tool's arg
# schema automatically.

_META: Dict[str, tuple] = {
    # Knowledge/standard/experience retrieval
    "search_project": ("Search the project knowledge base: blocks/variables/networks/calls/cross-references (including code and LAD IR)", "Knowledge retrieval"),
    "build_context_pack": ("Build a RAG context pack for a task (top-K code/network evidence + dependency context)", "Knowledge retrieval"),
    "describe_knowledge_schema": ("Show the knowledge index data dictionary (tables/fields/enums/relations)", "Knowledge retrieval"),
    "query_variable_references": ("Find all read/write locations of a variable (cross-reference)", "Knowledge retrieval"),
    "query_call_chain": ("Query a block's call chain (upstream callers/downstream callees, multi-hop)", "Knowledge retrieval"),
    "query_variable_impact": ("Query a variable's impact chain (multi-hop propagation)", "Knowledge retrieval"),
    "get_current_project": ("Show the project currently attached to the knowledge index (key/name/status)", "Knowledge retrieval"),
    "get_environment_profile": ("Live environment precheck: TIA version, CPU family, order number and environment fingerprint", "Capability probe"),
    "list_standard_topics": ("List libraries as a bookmark outline (level, rule_id, page, doc_offset, content_length, next_doc_offset)", "Standard lookup"),
    "read_standard_section": ("Read a standard section from doc_offset or rule_id+offset; stops at the next heading by default", "Standard lookup"),
    "query_repair_experience": ("Retrieve repair experience (compile error → root cause → fix, type-level)", "Repair experience"),
    # Block read/export
    "get_block_content": ("Unified block read: ir (canonical JSON) | source (SCL/STL text) | xml (raw XML) | interface (canonical tree); one contract shared with the write tools", "Block read"),
    "list_block_groups": ("List PLC program block group paths (target for group_path)", "Block read"),
    "list_type_groups": ("List PLC data type (UDT) group paths (target for group_path)", "Block read"),
    "get_program_tree": (
        "Lightweight PLC program directory: blocks with number/type/language/group path (+ optional types)",
        "Block read",
    ),
    "list_tags": ("List global tag table variables", "Variable read"),
    "get_db_structure": ("Query DB member structure (fields + types)", "Variable read"),
    "list_udt_types": ("List UDT user-defined types", "Variable read"),
    "query_variable_type": ("Query a variable's data type", "Variable read"),
    "probe_openness_capabilities": ("Probe available Openness capabilities", "Capability probe"),
    # Write/compile
    "import_block": ("Import SCL/XML into a TIA block (requires approval)", "Write"),
    "delete_block": ("Delete a block (requires approval)", "Write"),
    "build_lad_fc": ("Build a native LAD FC block and import it (requires approval)", "Write"),
    "build_lad_fb": ("Build a native LAD FB block and import it (requires approval)", "Write"),
    "build_graph_fb": ("Build a native S7-GRAPH FB (step sequence; rejects transitions without conditions or action-less sequences) and import it, then compile the block (requires approval)", "Write"),
    "build_ob": ("Build an organization block (OB) with a legal number + 20-byte start-info interface (requires approval)", "Write"),
    "build_udt": ("Build a native PLC UDT from members[] or canonical interface JSON and import it (requires approval)", "Write"),
    "build_global_db": ("Build a native global DB from staticMembers[] or canonical interface JSON (TIA keeps its own block layout; requires approval)", "Write"),
    "patch_block_network": ("Patch one network body of an existing block in place (revision-guarded, requires approval)", "Write"),
    "patch_block_interface": ("Patch only the interface of an existing DB/UDT/FB/FC in place (revision-guarded, requires approval)", "Write"),
    "build_hmi_screen": ("Generate an HMI screen offline (Classic→XML / Unified→design JSON, does not write TIA)", "HMI generation"),
    "build_hmi_tag_table": ("Generate an HMI tag table offline (Classic→XML / Unified→tag planning JSON, does not write TIA)", "HMI generation"),
    "configure_device": ("Create a TIA device/station by hardware catalog TypeIdentifier (requires approval)", "Configuration"),
    "set_device_attribute": ("Set a device item attribute (requires approval)", "Configuration"),
    "add_module": ("Insert a module under a rack/station (requires approval)", "Configuration"),
    "configure_subnet": ("Create a subnet and connect device items (requires approval)", "Configuration"),
    "compile": ("Compile the whole PLC and return a structured verdict (success/error_count/errors[]/unknown_block_suspects[])", "Compile"),
    "compile_snapshot": ("Compile and store the current error set as the baseline for compile_diff", "Compile"),
    "compile_diff": ("Compile and diff against the last snapshot (new vs pre-existing errors)", "Compile"),
    "compile_block": ("Compile one block only (gentle post-write check; never the whole PLC)", "Compile"),
    # Openness docs
    "search_openness_docs": ("Search curated Openness concept topics (version-independent summary; use query_openness_api for version-specific signatures)", "Openness docs"),
    "list_plc_openness_topics": ("List curated Openness concept topics (version-independent)", "Openness docs"),
    "get_plc_openness_api_methods": ("Get curated methods/object paths/examples for a topic (version-independent)", "Openness docs"),
    "get_plc_openness_csharp_template": ("Get a ready-to-reuse C# method template", "Openness docs"),
    "get_plc_runtime_diagnostic_api": ("Get runtime/diagnostic API methods", "Openness docs"),
    "get_plc_runtime_analysis_recipe": ("Get the combined variable+DB+logic diagnostic workflow and templates", "Openness docs"),
    "query_openness_api": ("Version-specific Openness API reference from the official per-version XML docs (V15.1/V16/V17 bundled; V18+ via probe_openness_capabilities)", "Openness docs"),
    # Library standards
    "list_library_types": ("List the FB/FC spec files under Library Types", "Library specs"),
    "get_library_type_details": ("Read the spec content of a block (text/Word/Excel/PDF)", "Library specs"),
    # Samples
    "list_openness_samples": ("List verified Openness implementation sample topics", "Samples"),
    "get_openness_sample": ("Read the implementation code of a specified sample", "Samples"),
    "query_siemens_platform_matrix": ("Query the single Siemens CPU incompatibility matrix (S7-300/400/1200/1500) by datatype, instruction, DB member property or verbatim compile text", "Platform knowledge"),
    "repair_inconsistent_types": ("Repair blocks/PLC types TIA marks inconsistent: last indexed snapshot patched losslessly, datatype swapped from the matrix, then project compiled (write, approval)", "Block write"),
    # Local files
    "read_file": ("Read a text file inside the project", "Local files"),
    "write_file": ("Write a text file under GeneratedCode/", "Local files"),
    "list_dir": ("List directory contents", "Local files"),
    "delete_file": ("Delete a single file under GeneratedCode/", "Local files"),
    "save_analysis_doc": ("Save an analysis report as md/txt (GeneratedCode/analysis/)", "Local files"),
}


# ── The catalog ────────────────────────────────────────────────────────────

@dataclass
class ToolEntry:
    name: str
    tool: Any
    short_desc: str
    category: str
    params: str
    roles: frozenset
    access: str  # "read" | "write"
    tier: str    # "mcp" | "local" | "static"


_catalog: Optional[Dict[str, ToolEntry]] = None


def _describe_params(tool) -> str:
    """Compact parameter signature derived from the tool's arg schema."""
    try:
        args_schema = getattr(tool, "args_schema", None)
        if args_schema is None:
            return ""
        if isinstance(args_schema, dict):
            schema = args_schema
        else:
            fn = getattr(args_schema, "model_json_schema", None)
            schema = fn() if callable(fn) else None
        if not isinstance(schema, dict):
            return ""
        props = schema.get("properties") or {}
        required = set(schema.get("required") or [])
        parts = []
        for k, v in props.items():
            if not isinstance(v, dict):
                continue
            t = v.get("type", "")
            if isinstance(t, list):
                t = "|".join(x.get("type", "?") if isinstance(x, dict) else "?" for x in t)
            elif not t:
                t = "obj"
            mark = "" if k in required else "?"
            parts.append(f"{k}{mark}:{t}")
        return ", ".join(parts)
    except Exception:
        return ""


def _meta_for(name: str) -> tuple:
    return _META.get(name, (name, "Other"))


def _short_desc(name: str, tool) -> str:
    desc, _ = _meta_for(name)
    # Curated entry wins; otherwise fall back to first line of the tool's own description.
    if name in _META:
        return desc
    raw = (getattr(tool, "description", "") or "").strip()
    first = raw.splitlines()[0].strip() if raw else name
    return first[:200] or name


def _category(name: str) -> str:
    return _meta_for(name)[1]


def _make_entry(name: str, tool, tier: str, roles: Dict[str, set],
                access: Optional[str] = None) -> ToolEntry:
    if access is None:
        access = "read"
    return ToolEntry(
        name=name,
        tool=tool,
        short_desc=_short_desc(name, tool),
        category=_category(name),
        params=_describe_params(tool),
        roles=frozenset(roles.get(name, set())),
        access=access,
        tier=tier,
    )


async def build_catalog(force: bool = False) -> Dict[str, ToolEntry]:
    """Enumerate every tool source and assemble the shared catalog once.

    MCP enumeration failure yields an empty MCP tier: local + static tools stay,
    and callers see that TIA capabilities are missing instead of silently
    substituting a differently-named REST tool.
    """
    global _catalog
    if _catalog is not None and not force:
        return _catalog

    mcp_tools = await load_mcp_tools(force_reload=force)
    readonly, write, unknown = _split_mcp_tools(mcp_tools)
    local = [read_file, write_file, list_dir, delete_file, save_analysis_doc]

    # Per-role tool lists — used only to derive each tool's roles set.
    role_lists = {
        "coder": (readonly + write + unknown
                  + [read_file, write_file, list_dir, delete_file]
                  + _STATIC_KNOWLEDGE),
        "reviewer": (readonly
                     + [read_file, list_dir, save_analysis_doc] + _STATIC_KNOWLEDGE),
        "compiler": (readonly + write
                     + [read_file, write_file, list_dir]
                     # The compiler prompt sends block-less / unknown-datatype errors to the
                     # platform matrix, so this role must be able to invoke it.
                     + [query_siemens_platform_matrix]),
        "chat": (readonly + unknown
                 + [read_file, list_dir] + _STATIC_KNOWLEDGE),
        # Planner (task 3): read-only retrieval — search/build_context_pack + standard library + get_current_project.
        "planner": (readonly + [read_file, list_dir] + _STATIC_KNOWLEDGE),
    }

    roles: Dict[str, set] = {}
    for role, tools in role_lists.items():
        for t in tools:
            roles.setdefault(getattr(t, "name", ""), set()).add(role)

    # Assemble entries in source order of precedence.
    catalog: Dict[str, ToolEntry] = {}

    def _add(name, tool, tier, access):
        catalog[name] = _make_entry(name, tool, tier, roles, access)

    for t in readonly:
        _add(getattr(t, "name", ""), t, "mcp", "read")
    for t in write:
        _add(getattr(t, "name", ""), t, "mcp", "write")
    for t in unknown:
        _add(getattr(t, "name", ""), t, "mcp", "read")
    for t in local:
        name = getattr(t, "name", "")
        access = "write" if name in {"write_file", "delete_file"} else "read"
        _add(name, t, "local", access)
    for t in _STATIC_KNOWLEDGE:
        _add(getattr(t, "name", ""), t, "static", "read")

    _catalog = catalog
    logger.info(
        f"[CATALOG] built {len(catalog)} tool(s) "
        f"mcp(ro={len(readonly)},wr={len(write)},unk={len(unknown)})"
    )
    return _catalog


def get_catalog() -> Dict[str, ToolEntry]:
    """Synchronous accessor for the already-built catalog (no network)."""
    return dict(_catalog or {})


def reset_catalog() -> None:
    global _catalog
    _catalog = None