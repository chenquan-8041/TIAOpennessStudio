"""
Generic file capabilities (Python local @tools) — the foundation for the agent
acting as a general-purpose programming assistant.

Not limited to TIA XML/SCL: write test scripts, generate arbitrary text files,
read reference files, clean up temporary files.

Security constraints (production-grade, read/write separation):
  - Read / list: confined to the project root (read-only, low risk; reading
    existing scl/config as reference is allowed).
  - Write / delete: strictly confined to GeneratedCode/ (destructive ops tightened).
  - `..` escapes and out-of-bounds absolute paths are rejected (prefix check
    after realpath normalization).
  - delete removes files only — no directories, no recursion.
  - Failures always return a string error (fail-soft) and never throw, so the
    agent loop is not interrupted.

Sandbox roots (see tools/definitions._get_workspace_root):
  Project root  = <this file>/../.. = d:\TIAOpennessStudio
  Generated dir = <project root>/GeneratedCode
"""

import os
from langchain_core.tools import tool


def _project_root() -> str:
    """Project root directory (consistent with tools/definitions._get_workspace_root)."""
    current_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(current_dir))


def _generated_dir() -> str:
    """Sandbox root for write/delete operations: <project root>/GeneratedCode."""
    return os.path.join(_project_root(), "GeneratedCode")


def _resolve_within(path: str, sandbox_root: str):
    """Resolve path to an absolute path within sandbox_root.

    Returns (abs_path, None) when valid; (None, error_msg) when out of bounds/invalid.
    Accepts either a relative path (relative to sandbox_root) or an absolute path
    (which must already be inside sandbox_root).
    """
    if path is None or str(path).strip() == "":
        return None, "path is required"

    root = os.path.realpath(sandbox_root)
    candidate = path if os.path.isabs(path) else os.path.join(root, path)
    abs_path = os.path.realpath(candidate)

    # Prefix check: abs_path must equal root or be within root's subtree.
    if abs_path != root and not abs_path.startswith(root + os.sep):
        return None, f"path escapes sandbox ({sandbox_root}): {path}"
    return abs_path, None


@tool
def read_file(path: str) -> str:
    """Read a text file (for reading existing SCL / config / reference files).

    Args:
        path: Path relative to the project root, or an absolute path within the
            project root.

    Returns:
        The file's text content; an error description when out of bounds /
        missing / read failure.
    """
    abs_path, err = _resolve_within(path, _project_root())
    if err:
        return f"❌ {err}"
    if not os.path.isfile(abs_path):
        return f"❌ file not found: {path}"
    try:
        with open(abs_path, "r", encoding="utf-8") as f:
            return f.read()
    except Exception as e:
        return f"❌ read failed: {e}"


@tool
def write_file(path: str, content: str, description: str = "") -> str:
    """Write/overwrite a text file (parent directories are created automatically).
    Confined to GeneratedCode/.

    Args:
        path: Path relative to GeneratedCode/ (e.g. "MyBlock.scl" or "tests/t1.py"),
            or an absolute path within GeneratedCode/.
        content: The text content to write.
        description: Optional description.

    Returns:
        The operation result; an error description when out of bounds / write failure.
    """
    abs_path, err = _resolve_within(path, _generated_dir())
    if err:
        return f"❌ {err}"
    try:
        parent = os.path.dirname(abs_path)
        if parent and not os.path.exists(parent):
            os.makedirs(parent, exist_ok=True)
        with open(abs_path, "w", encoding="utf-8") as f:
            f.write(content)
        desc = f" ({description})" if description else ""
        return f"✅ written: {abs_path}{desc}\nsize: {len(content)} chars"
    except Exception as e:
        return f"❌ write failed: {e}"


@tool
def list_dir(path: str = "", recursive: bool = False) -> str:
    """List directory contents (for browsing project files). Confined to the project root.

    Args:
        path: Directory path relative to the project root; empty = project root.
        recursive: When True, recursively list files in subdirectories.

    Returns:
        One entry per line (path relative to project root + size); an error
        description when out of bounds / missing.
    """
    root = _project_root()
    target = path or "."
    abs_path, err = _resolve_within(target, root)
    if err:
        return f"❌ {err}"
    if not os.path.isdir(abs_path):
        return f"❌ directory not found: {path}"
    try:
        entries = []
        if recursive:
            for r, _dirs, files in os.walk(abs_path):
                for fn in files:
                    fp = os.path.join(r, fn)
                    rel = os.path.relpath(fp, root)
                    entries.append(f"- {rel} ({os.path.getsize(fp)} bytes)")
        else:
            for name in sorted(os.listdir(abs_path)):
                fp = os.path.join(abs_path, name)
                rel = os.path.relpath(fp, root)
                if os.path.isdir(fp):
                    entries.append(f"- {rel}/ (dir)")
                else:
                    entries.append(f"- {rel} ({os.path.getsize(fp)} bytes)")
        if not entries:
            return f"(empty) {path or '.'}"
        return "\n".join(entries)
    except Exception as e:
        return f"❌ list failed: {e}"


@tool
def delete_file(path: str) -> str:
    """Delete a single file (for cleaning up temporary test scripts). Confined to
    GeneratedCode/; removes files only, never directories.

    Args:
        path: Path relative to GeneratedCode/, or an absolute path within GeneratedCode/.

    Returns:
        The operation result; an error description when out of bounds / is a
        directory / delete failure.
    """
    abs_path, err = _resolve_within(path, _generated_dir())
    if err:
        return f"❌ {err}"
    if os.path.isdir(abs_path):
        return f"❌ refuse to delete a directory: {path}"
    if not os.path.isfile(abs_path):
        return f"❌ file not found: {path}"
    try:
        os.remove(abs_path)
        return f"✅ deleted: {abs_path}"
    except Exception as e:
        return f"❌ delete failed: {e}"


_DOC_DIR_NAME = "analysis"
_DOC_EXTENSIONS = (".md", ".txt")
_DOC_MAX_CHARS = 200_000


@tool
def save_analysis_doc(filename: str, content: str) -> str:
    """Save an analysis report as a Markdown/text document so the user can keep it.

    Use this when the user asks to save/export/archive an analysis. It only writes a document file
    under GeneratedCode/analysis/. It never touches the TIA project or any PLC block.

    Args:
        filename: File name only, e.g. "610425_BH1_analysis.md". Must end with .md or .txt.
            Folders are not allowed.
        content: The full document text.

    Returns:
        The saved path on success; an error description otherwise.
    """
    name = (filename or "").strip()
    if not name:
        return "❌ filename is required"
    if name != os.path.basename(name) or "/" in name or "\\" in name:
        return "❌ filename must be a plain file name without folders"
    if not name.lower().endswith(_DOC_EXTENSIONS):
        return "❌ only .md and .txt documents can be saved here"
    if content is None or not str(content).strip():
        return "❌ content is empty"
    if len(content) > _DOC_MAX_CHARS:
        return f"❌ content too large ({len(content)} chars, max {_DOC_MAX_CHARS})"

    doc_root = os.path.join(_generated_dir(), _DOC_DIR_NAME)
    abs_path, err = _resolve_within(name, doc_root)
    if err:
        return f"❌ {err}"
    try:
        os.makedirs(doc_root, exist_ok=True)
        # Never overwrite an earlier report silently: add a numeric suffix instead.
        base, ext = os.path.splitext(abs_path)
        candidate, n = abs_path, 1
        while os.path.exists(candidate):
            n += 1
            candidate = f"{base}_{n}{ext}"
        with open(candidate, "w", encoding="utf-8") as f:
            f.write(content)
        return f"✅ saved: {candidate}\nsize: {len(content)} chars"
    except Exception as e:
        return f"❌ save failed: {e}"


# Generic local file toolset (does not touch TIA/C#).
FILE_TOOLS = [read_file, write_file, list_dir, delete_file]
