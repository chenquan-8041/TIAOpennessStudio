"""
Siemens CPU incompatibility matrix — one document, one lookup.

The fact source is ``tools/siemens_platform_matrix.md`` (a single human-readable
document: one matrix for S7-300 / S7-400 / S7-1200 / S7-1500 plus the verbatim
compile/import messages). This module only parses and slices it, so there is no
second copy of the rules anywhere: prompts point at this tool, and
``agent/environment.py`` annotates compile errors with the same parser.

Public API:
  query_siemens_platform_matrix(query="", cpu_family="")   — the model-facing tool
  annotate(message, family)                                — compile-error hint
"""

import os
import re
from typing import Dict, List, Optional, Tuple

from langchain_core.tools import tool

_MATRIX_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "siemens_platform_matrix.md")
_MAX_DOC_CHARS = 4500
_FAMILY_KEYS = ("S7-300", "S7-400", "S7-1200", "S7-1500")


def _read(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return f.read()
    except Exception as e:  # noqa: BLE001 - a missing document must not break a run
        return f"(failed to read {os.path.basename(path)}: {e})"


def _section(text: str, title_contains: str) -> List[str]:
    """Lines of the '## ...' section whose title contains the given text."""
    out, inside = [], False
    for line in text.splitlines():
        if line.startswith("## "):
            inside = title_contains in line
            continue
        if inside:
            out.append(line)
    return out


def _table_rows(lines: List[str], header_token: str) -> List[List[str]]:
    """Parse the first markdown table in the section; skip its header + separator."""
    rows, seen_header = [], False
    for line in lines:
        stripped = line.strip()
        if not stripped.startswith("|"):
            continue
        cells = [c.strip() for c in stripped.strip("|").split("|")]
        if not seen_header:
            if header_token.lower() in " ".join(cells).lower():
                seen_header = True
            continue
        if all(set(c) <= set("-: ") for c in cells):
            continue
        rows.append(cells)
    return rows


def _matrix() -> List[dict]:
    rows = _table_rows(_section(_read(_MATRIX_PATH), "兼容性矩阵"), "feature")
    entries = []
    for cells in rows:
        if len(cells) < 7:
            continue
        entries.append({
            "feature": cells[0],
            "families": dict(zip(_FAMILY_KEYS, cells[1:5])),
            "replacement": cells[5],
            "auto_retype": cells[6].strip().startswith("✓"),
        })
    return entries


def _symptoms() -> List[Tuple[str, str, str]]:
    rows = _table_rows(_section(_read(_MATRIX_PATH), "报错原文对照"), "symptom")
    return [(c[0], c[1], c[2]) for c in rows if len(c) >= 3]


def _families_in(family: str) -> List[str]:
    fam = (family or "").upper().replace(" ", "")
    if not fam:
        return list(_FAMILY_KEYS)
    keys = []
    if "S7-300" in fam or fam in ("S7-3XX", "S7-300/400"):
        keys.append("S7-300")
    if "S7-400" in fam or fam in ("S7-4XX", "S7-300/400"):
        keys.append("S7-400")
    if "S7-1200" in fam:
        keys.append("S7-1200")
    if "S7-1500" in fam:
        keys.append("S7-1500")
    return keys or list(_FAMILY_KEYS)


def _unsupported(cell: str) -> bool:
    return cell.strip().startswith("✗")


def _render(entry: dict, families: List[str]) -> str:
    lines = [f"## {entry['feature']}"]
    for fam in families:
        lines.append(f"- {fam}: {entry['families'][fam]}")
    if entry["replacement"] and any(_unsupported(entry["families"][f]) for f in families):
        lines.append(f"- use instead: {entry['replacement']}")
    return "\n".join(lines)


def _find_entries(query: str) -> List[dict]:
    low = query.lower()
    hits = [e for e in _matrix()
            if any(part.strip().lower() in low for part in re.split(r"/", e["feature"]) if part.strip())]
    if hits:
        return hits
    for symptom, feature, _ in _symptoms():
        if symptom.lower() in low:
            hits = [e for e in _matrix() if e["feature"] == feature]
            if hits:
                return hits
    return []


def replacement_for(feature: str, family: str) -> Optional[str]:
    """Replacement datatype/instruction for a feature the family does not support.

    Returns None when the family supports it (or the feature is unknown), so callers
    can decide from the matrix instead of hardcoding per-family rules.
    """
    name = (feature or "").strip().lower()
    entry = next((e for e in _matrix() if e["feature"].lower() == name), None)
    if not entry:
        return None
    families = _families_in(family)
    if not any(_unsupported(entry["families"][f]) for f in families):
        return None
    replacement = (entry["replacement"] or "").strip()
    if not replacement:
        return None
    # The cell is human text ("DATE_AND_TIME (8-byte BCD), or DATE + TIME_OF_DAY");
    # the first token is the drop-in datatype an automated repair can write.
    return re.split(r"[ ,;(]", replacement, maxsplit=1)[0] or None


def replacements_for(family: str) -> Dict[str, str]:
    """Datatype swaps an automated repair may apply on this CPU family.

    Only rows marked "auto retype" in the matrix are returned: those are pure
    datatype substitutions. Instruction/attribute rows are deliberately excluded
    (their replacement changes behaviour and must stay a human/agent decision).
    """
    out: Dict[str, str] = {}
    for entry in _matrix():
        if not entry.get("auto_retype"):
            continue
        replacement = replacement_for(entry["feature"], family)
        if replacement:
            out[entry["feature"]] = replacement
    return out


def annotate(message: str, family: str) -> str:
    """Append the matrix hint for a compile/import message, when the CPU family rejects it."""
    text = (message or "").strip()
    if not text or not family:
        return text
    low = text.lower()
    for symptom, feature, action in _symptoms():
        if symptom.lower() not in low:
            continue
        entry = next((e for e in _matrix() if e["feature"] == feature), None)
        families = _families_in(family)
        rejected = [f for f in families if _unsupported(entry["families"][f])] if entry else []
        gated = [f for f in families
                 if entry and not _unsupported(entry["families"][f]) and "FW" in entry["families"][f]]
        if not rejected and not gated:
            return text
        note = "; ".join(filter(None, [
            ("not supported on " + "/".join(rejected)) if rejected else "",
            ("firmware gate: " + entry["families"][gated[0]]) if gated else "",
            action,
        ]))
        return f"{text} [{note}; details: query_siemens_platform_matrix('{feature}', '{family}')]"
    return text


@tool
def query_siemens_platform_matrix(query: str = "", cpu_family: str = "") -> str:
    """Look up CPU-family incompatibilities for a datatype, instruction, DB member or verbatim error.

    The answer comes from one document (tools/siemens_platform_matrix.md) that lists
    S7-300 / S7-400 / S7-1200 / S7-1500 side by side. Use it before generating code
    that uses timers, edges, date/time, strings, GRAPH or DB attributes, and whenever
    a compile/import error names an unknown datatype, instruction or property.

    Args:
        query: datatype / instruction / member property or the exact error text,
            e.g. "DTL", "Data type DTL is unknown.", "Remanence", "R_TRIG".
        cpu_family: live family from get_environment_profile (e.g. "S7-300/400");
            leave empty to see all four columns.

    Returns:
        The matching matrix row(s) with the replacement to use, the matching error
        wording and what to do; when nothing matches, the matrix itself.
    """
    q = (query or "").strip()
    if not q:
        return _read(_MATRIX_PATH)[:_MAX_DOC_CHARS]

    entries = _find_entries(q)
    families = _families_in(cpu_family)
    if not entries:
        doc = _read(_MATRIX_PATH)[:_MAX_DOC_CHARS]
        return f"No matrix row matched '{q}'. Full matrix:\n{doc}"

    blocks = [_render(e, families) for e in entries]
    hint = ""
    for symptom, feature, action in _symptoms():
        if symptom.lower() in q.lower() and feature in {e["feature"] for e in entries}:
            hint = f"## matched error\n{symptom} -> {action}"
            break
    return "\n\n".join(blocks + ([hint] if hint else []))
