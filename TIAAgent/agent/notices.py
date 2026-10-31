"""User-facing notices: code + English fallback + positional params.

The backend never emits localized UI text. A notice carries:

  code   -- stable translation key resolved by the C# LanguageManager
            (English / German / Chinese).
  text   -- English fallback used when the UI has no entry for the code.
  params -- positional values substituted into the UI template.

Dynamic content (compiler errors, LLM output) never gets a code; it is
passed through as English text.
"""

from typing import Any, Dict, Optional


def _safe_str(value: Any) -> str:
    """Stringify a parameter without ever raising (the notice path must not break a stream)."""
    try:
        return str(value)
    except Exception:
        return ""


def notice(code: str, text: str = "", *params: Any) -> Dict[str, Any]:
    """Build a user-facing notice payload; always JSON-serializable."""
    return {"code": _safe_str(code), "text": _safe_str(text), "params": [_safe_str(p) for p in params]}


# Known write blockers that need a MANUAL user action the agent cannot perform itself.
# Each entry maps an error substring (matched case-insensitively) to a stable code +
# English instruction. The C# UI localizes ``code`` via LanguageManager ("Agent.<code>")
# and falls back to ``text``. This is the deterministic "accumulated experience" table:
# extend it as new manual-fix situations are discovered.
_KNOWN_MANUAL_ACTIONS = (
    {
        "substr": "password was assigned for the safety program",
        "code": "write.safety_password",
        "text": (
            "TIA's safety program (F-CPU) has an access-protection password set, which blocks all "
            "Openness changes to this project. Fix: TIA Portal → Safety Administration → "
            "Access Protection → Revoke the password, then retry."
        ),
    },
)


def match_manual_action(error: str) -> Optional[Dict[str, str]]:
    """Return the known manual-action entry when ``error`` matches a known write blocker, else None."""
    if not error:
        return None
    low = error.lower()
    for entry in _KNOWN_MANUAL_ACTIONS:
        if entry["substr"] in low:
            return entry
    return None
