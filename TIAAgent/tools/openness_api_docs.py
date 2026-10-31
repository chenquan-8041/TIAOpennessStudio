"""Version-specific Openness API reference backed by the bundled official XML docs.

`docs/PublicAPI/{V15.1,V16,V17}/Siemens.Engineering.xml` are the per-version
Siemens `Siemens.Engineering.dll` XML documentation files. Querying them makes
API answers version-sensitive instead of relying on one hand-written doc
snapshot (the old 11/2023 summary).

Versions without a bundled file (V18–V21) are not answered from a guess: the
caller is told to use `probe_openness_capabilities` / `get_environment_profile`
against the installed TIA.
"""

from __future__ import annotations

import logging
import os
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

logger = logging.getLogger("backend")

# repo root: tools/ -> TIAAgent/ -> <repo>
_DOC_ROOT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "docs", "PublicAPI",
)
_DOC_FILE = "Siemens.Engineering.xml"

_VERSION_DIR_RE = re.compile(r"^V(\d+(?:\.\d+)?)$")
_KIND_LABEL = {
    "T": "type",
    "M": "method",
    "P": "property",
    "F": "field",
    "E": "event",
}


@dataclass(frozen=True)
class ApiMember:
    member_id: str
    kind: str
    full_name: str
    name: str
    summary: str
    detail: str


_cache: Dict[str, Tuple[List[ApiMember], int]] = {}


def available_versions() -> List[str]:
    """Bundled doc versions, e.g. ["15.1", "16", "17"]."""
    if not os.path.isdir(_DOC_ROOT):
        return []
    versions = []
    for entry in os.listdir(_DOC_ROOT):
        match = _VERSION_DIR_RE.match(entry)
        if match and os.path.isfile(os.path.join(_DOC_ROOT, entry, _DOC_FILE)):
            versions.append(match.group(1))
    return sorted(versions, key=_version_key)


def normalize_version(value: str) -> str:
    """Accept "15.1", "V15.1", "TIA V16" → "15.1" / "16"; "" stays ""."""
    text = (value or "").strip().upper()
    match = re.search(r"(?:V|TIA\s*V?\s*)?(\d+(?:\.\d+)?)", text)
    return match.group(1) if match else ""


def _version_key(value: str) -> Tuple[int, int]:
    parts = value.split(".")
    major = int(parts[0]) if parts else 0
    minor = int(parts[1]) if len(parts) > 1 else 0
    return (major, minor)


def _plain(node: Optional[ET.Element]) -> str:
    if node is None:
        return ""
    text = "".join(node.itertext())
    return re.sub(r"\s+", " ", text).strip()


def _load(version: str) -> Tuple[List[ApiMember], int]:
    """Parse and cache one version's XML doc (members + total parsed count)."""
    if version in _cache:
        return _cache[version]
    path = os.path.join(_DOC_ROOT, f"V{version}", _DOC_FILE)
    if not os.path.isfile(path):
        return [], 0
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as exc:
        logger.warning(f"[openness-api] parse failed V{version}: {exc}")
        return [], 0

    members: List[ApiMember] = []
    for node in root.findall("./members/member"):
        member_id = node.get("name") or ""
        kind = member_id[:1]
        if kind not in _KIND_LABEL:
            continue
        full = member_id[2:]
        # Method ids carry a parameter list: "M:...GetService``1(...)".
        full = full.split("(", 1)[0]
        type_name, _, name = full.rpartition(".")
        summary = _plain(node.find("summary"))
        detail_parts = []
        for tag in ("remarks", "returns"):
            text = _plain(node.find(tag))
            if text:
                detail_parts.append(f"{tag}: {text}")
        for param in node.findall("param"):
            pname = param.get("name") or ""
            ptext = _plain(param)
            if ptext:
                detail_parts.append(f"param {pname}: {ptext}")
        members.append(ApiMember(
            member_id=member_id,
            kind=kind,
            full_name=full,
            name=name or full,
            summary=summary,
            detail="\n    ".join(detail_parts),
        ))
    _cache[version] = (members, len(root.findall("./members/member")))
    return _cache[version]


def _score(member: ApiMember, query: str, tokens: List[str]) -> int:
    full = member.full_name.lower()
    name = re.sub(r"`+", "", member.name.lower())
    summary = member.summary.lower()
    if query == full or query == name or query == member.member_id.lower():
        return 1000
    score = 0
    if name.startswith(query):
        score = 500
    elif query and query in full:
        score = 300
    else:
        token_hits = sum(1 for t in tokens if t in full)
        if token_hits == len(tokens) and tokens:
            score = 150
        elif token_hits:
            score = 100
        else:
            summary_hits = sum(1 for t in tokens if t in summary)
            score = 10 * summary_hits
    # Explicit-interface members ("global::Siemens#...") and unresolved doc
    # placeholders ("%[X]%") are real but noisy; keep them last, not first.
    if score and "global::" in full and "global" not in query:
        score -= 40
    if score and "%[" in summary:
        score -= 20
    return score


def query(version: str, text: str, limit: int = 10) -> str:
    """Search one version's API docs; returns a ready-to-return tool string."""
    query_text = (text or "").strip()
    versions = available_versions()
    # V15 and V15.1 are distinct TIA versions; the supported baseline starts at
    # V15.1. Never alias V15 onto the V15.1 docs.
    if version == "15":
        return (
            "TIA V15 is not supported: the supported baseline starts at V15.1.\n"
            "Pass version=\"15.1\" for V15.1 (V15 is a different release). "
            f"Bundled docs: {', '.join(f'V{v}' for v in versions) or '(none)'}."
        )
    if version not in versions:
        bundled = ", ".join(f"V{v}" for v in versions) or "(none)"
        return (
            f"No bundled Openness API documentation for TIA V{version}.\n"
            f"Bundled versions: {bundled}.\n"
            "For a version without bundled docs, use `probe_openness_capabilities` "
            "(reflection of the installed Siemens.Engineering.dll) and "
            "`get_environment_profile` (live TIA version) instead of guessing."
        )

    members, total = _load(version)
    if not members:
        return (
            f"Openness API documentation for TIA V{version} could not be read "
            f"(expected {os.path.join(_DOC_ROOT, 'V' + version, _DOC_FILE)})."
        )
    if not query_text:
        by_kind: Dict[str, int] = {}
        for member in members:
            by_kind[member.kind] = by_kind.get(member.kind, 0) + 1
        counts = ", ".join(
            f"{_KIND_LABEL[k]}={by_kind.get(k, 0)}" for k in ("T", "M", "P", "F", "E")
        )
        return (
            f"Openness API docs — TIA V{version} (official {_DOC_FILE})\n"
            f"{total} documented members ({counts}).\n"
            "Pass a query, e.g. `query_openness_api(query=\"GetService\", version=\"V"
            + version
            + "\")`."
        )

    tokens = [t for t in re.split(r"[\s.():`]+", query_text.lower()) if t]
    scored = []
    for member in members:
        score = _score(member, query_text.lower(), tokens)
        if score:
            scored.append((score, member))
    scored.sort(key=lambda item: (
        -item[0], item[1].kind != "T", len(item[1].full_name), item[1].full_name,
    ))
    limit = max(1, min(int(limit or 10), 30))
    if not scored:
        return (
            f"Openness API docs — TIA V{version}: no member matched \"{query_text}\".\n"
            "Try a type or method name (e.g. `ICompilable`, `GetService`, `Export`), "
            "or call with an empty query to see what the version's doc contains."
        )

    lines = [
        f"Openness API docs — TIA V{version} (official {_DOC_FILE})",
        f"Query: \"{query_text}\" — {len(scored)} match(es), showing {min(limit, len(scored))}",
    ]
    for score, member in scored[:limit]:
        kind = _KIND_LABEL.get(member.kind, member.kind)
        lines.append(f"[{kind}] {member.full_name}")
        if member.member_id != member.full_name:
            lines.append(f"    id: {member.member_id}")
        if member.summary:
            lines.append(f"    Summary: {member.summary}")
        if member.detail:
            lines.append(f"    {member.detail}")
        if score >= 1000:
            break
    return "\n".join(lines)
