"""SCL structural validation module.

Provides lightweight, regex-based structural checks for Siemens SCL source
text. This is *not* a full SCL parser; it targets the kinds of structural
omissions an LLM commonly produces (missing block header, missing closing
keyword, mismatched VAR/END_VAR, etc.).

See design.md §Components.2 SCL_Validator and Requirements 4.7.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import List
import re


@dataclass
class ValidationResult:
    """Result of structural SCL validation."""

    is_valid: bool
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    block_type: str = ""  # detected block type (e.g. "FUNCTION_BLOCK"); "" if not parseable
    block_name: str = ""  # detected block name; "" if not parseable


# ---------------------------------------------------------------------------
# Regex catalog
# ---------------------------------------------------------------------------

# Block header on the first non-empty line. The keyword alternatives are
# ordered longest-first so that "FUNCTION_BLOCK" matches before "FUNCTION".
# block_name appears as either "Quoted Identifier" or a bare identifier.
_BLOCK_HEADER_RE = re.compile(
    r"^\s*(FUNCTION_BLOCK|ORGANIZATION_BLOCK|DATA_BLOCK|FUNCTION)\b"
    r"(?:\s+(?:\"([^\"]*)\"|([A-Za-z_][A-Za-z0-9_]*)))?",
    re.IGNORECASE,
)

# (* ... *) block comments span lines; // line comments stop at newline.
_BLOCK_COMMENT_RE = re.compile(r"\(\*.*?\*\)", re.DOTALL)
_LINE_COMMENT_RE = re.compile(r"//[^\n]*")

_BEGIN_RE = re.compile(r"\bBEGIN\b", re.IGNORECASE)
_GOTO_RE = re.compile(r"\bGOTO\b", re.IGNORECASE)
_END_VAR_RE = re.compile(r"\bEND_VAR\b", re.IGNORECASE)

# VAR-section openers. Order matters inside the alternation: the longer
# variants come first so the regex engine consumes them as a unit before
# trying the bare "VAR". Word boundaries on both sides keep "VAR" from
# matching inside "END_VAR" (no boundary between the underscore and V) and
# keep the bare "VAR" branch from claiming the leading characters of
# "VAR_INPUT" etc. (no boundary between R and _).
_VAR_OPENERS_RE = re.compile(
    r"\b(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_TEMP|VAR_CONSTANT|VAR)\b",
    re.IGNORECASE,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _strip_comments(code: str) -> str:
    """Remove (* ... *) block comments and // line comments.

    Block comments are stripped first so that a // sequence inside a block
    comment is not interpreted as a line comment, and so multi-line block
    comments collapse cleanly.
    """
    if not code:
        return ""
    no_block = _BLOCK_COMMENT_RE.sub("", code)
    no_line = _LINE_COMMENT_RE.sub("", no_block)
    return no_line


def _first_non_empty_line(code: str) -> str:
    for line in code.splitlines():
        if line.strip():
            return line
    return ""


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def validate_scl(code: str) -> ValidationResult:
    """Structurally validate SCL source text.

    Checks performed:

    * V1 - first non-empty line begins with a recognized block keyword
           (FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / ORGANIZATION_BLOCK).
           Failure level: error.
    * V2 - at least one ``BEGIN`` keyword is present. DATA_BLOCK is
           exempt (a DB may consist of only ``STRUCT..END_STRUCT``).
           Failure level: error.
    * V3 - the matching ``END_<keyword>`` for the V1-detected block type
           is present. Failure level: error.
    * V4 - the cumulative count of VAR-section openers (VAR, VAR_INPUT,
           VAR_OUTPUT, VAR_IN_OUT, VAR_TEMP, VAR_CONSTANT) equals the
           count of ``END_VAR``. Failure level: error.
    * V5 - any ``GOTO`` keyword triggers a warning since GOTO is not
           supported on S7-1200/1500. Failure level: warning.

    Comments are stripped (both ``//`` line comments and ``(* ... *)``
    block comments) before any matching, so keywords inside comments are
    not counted.

    Parameters
    ----------
    code:
        Raw SCL source text. May be empty or ``None`` (treated as empty).

    Returns
    -------
    ValidationResult
        ``is_valid`` is True iff ``errors`` is empty. Warnings do not
        affect ``is_valid``. ``block_type`` / ``block_name`` are filled in
        when the V1 header could be parsed; otherwise both are ``""``.
    """
    errors: List[str] = []
    warnings: List[str] = []
    block_type = ""
    block_name = ""

    text = code or ""
    cleaned = _strip_comments(text)

    # V1: block header on first non-empty (post-comment-strip) line.
    first_line = _first_non_empty_line(cleaned)
    header_match = _BLOCK_HEADER_RE.match(first_line)
    if header_match:
        block_type = header_match.group(1).upper()
        # quoted form (group 2) or bare identifier (group 3); "" if neither
        block_name = (header_match.group(2) or header_match.group(3) or "").strip()
    else:
        errors.append(
            "V1: Missing block declaration header "
            "(expected FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / ORGANIZATION_BLOCK "
            "on first non-empty line)"
        )

    # V2: BEGIN keyword presence (DATA_BLOCK exempted - DB may only have STRUCT).
    has_begin = bool(_BEGIN_RE.search(cleaned))
    if not has_begin and block_type != "DATA_BLOCK":
        errors.append("V2: Missing BEGIN keyword")

    # V3: matching END_<block_type> keyword presence.
    if block_type:
        end_keyword = "END_" + block_type
        end_re = re.compile(r"\b" + re.escape(end_keyword) + r"\b", re.IGNORECASE)
        if not end_re.search(cleaned):
            errors.append(f"V3: Missing closing keyword {end_keyword}")

    # V4: VAR-opener count equals END_VAR count.
    var_open_count = len(_VAR_OPENERS_RE.findall(cleaned))
    end_var_count = len(_END_VAR_RE.findall(cleaned))
    if var_open_count != end_var_count:
        errors.append(
            f"V4: VAR_* opener count ({var_open_count}) does not match "
            f"END_VAR count ({end_var_count})"
        )

    # V5: GOTO usage -> warning only.
    if _GOTO_RE.search(cleaned):
        warnings.append("V5: GOTO is not supported on S7-1200/1500")

    return ValidationResult(
        is_valid=len(errors) == 0,
        errors=errors,
        warnings=warnings,
        block_type=block_type,
        block_name=block_name,
    )
