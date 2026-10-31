"""Side-channel evolution collection: full recording + background extraction worker skeleton.

The scheduling skeleton for the first two stages of the three-stage pipeline
(record → LLM extraction → RAG validation).
"""
from __future__ import annotations

import json
import logging
import os
import queue
import threading
import time
from typing import Any, Optional

logger = logging.getLogger("backend")

# Byte cap for a single record message, to prevent log bloat.
_MAX_ENTRY_BYTES = 64 * 1024

# Recording directory; overridable via env var; defaults to TIAAgent/logs/episodes.
_ENV_DIR = os.environ.get("EVOLUTION_LOG_DIR")
if _ENV_DIR:
    EPISODE_DIR = _ENV_DIR
else:
    _BACKEND_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    EPISODE_DIR = os.path.join(_BACKEND_ROOT, "logs", "episodes")


# Max trace characters read into the LLM context during extraction; when too long,
# keep head and tail (failure cause + fix conclusion).
_MAX_TRACE_CHARS = 60_000
_HEAD_CHARS = 15_000

# Minimum confidence threshold before writing to the store (LLM self-rated 0~1).
# Experiences below this are not produced, avoiding low-quality entries.
_MIN_CONFIDENCE = 0.5


_EXTRACTION_SYSTEM = (
    "You are an SCL repair-experience extractor for TIA Portal. Given the full trace of a code "
    "generation task (tool calls, compile results, code content), extract one reusable repair experience.\n"
    "Requirements:\n"
    "1. Only produce an experience when the trace shows 'compile failed first, compile succeeded last' "
    "(resolved=true); otherwise set resolved=false and leave the remaining fields as empty strings.\n"
    "2. The experience must stay at the type level of error pattern / root cause / fix pattern; do not "
    "include concrete block or variable names (use placeholders, e.g. 'a UDT member was deleted' instead of 'DB_Motor.Speed').\n"
    "3. symptom describes the Siemens compile-error signature (e.g. 'X not declared / unknown identifier').\n"
    "4. compiler_error is the verbatim original compile-error message from the trace; concrete symbols are "
    "allowed (used for cross-project normalized matching). Leave it empty when absent.\n"
    "5. root_cause explains why it happened; fix_pattern explains how to fix it (reusable and transferable).\n"
    "6. confidence is a self-rated 0~1 score; generality is a 0~1 score "
    "(1 = a generic SCL language error that still applies outside this project; 0 = tightly bound to this project's naming/structure).\n"
    "Output strict JSON only, with no extra text:\n"
    '{"resolved": bool, "symptom": str, "compiler_error": str, "root_cause": str, "fix_pattern": str, "confidence": float, "generality": float}'
)


def _read_trace(path: str) -> str:
    """Read the trace file; when too long, keep head and tail so the initial failure and final fix are not lost."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
    except Exception as e:
        logger.warning(f"[evolution] extract failed: read {path}: {e}")
        return ""
    if len(text) <= _MAX_TRACE_CHARS:
        return text
    return (
        text[:_HEAD_CHARS]
        + f"\n...<trace truncated, {len(text)} chars total>...\n"
        + text[-(_MAX_TRACE_CHARS - _HEAD_CHARS):]
    )


def _parse_experience(text: str) -> Optional[dict]:
    """Extract strict JSON from LLM output (tolerating fences and surrounding chatter)."""
    if not text:
        return None
    text = text.strip()
    try:
        obj = json.loads(text)
    except Exception:
        try:
            start, end = text.find("{"), text.rfind("}")
            if start == -1 or end == -1 or end <= start:
                return None
            obj = json.loads(text[start:end + 1])
        except Exception:
            return None
    if not isinstance(obj, dict):
        return None
    # Field fallbacks to avoid missing keys.
    experienced = {
        "resolved": bool(obj.get("resolved", False)),
        "symptom": str(obj.get("symptom") or ""),
        "compiler_error": str(obj.get("compiler_error") or ""),
        "root_cause": str(obj.get("root_cause") or ""),
        "fix_pattern": str(obj.get("fix_pattern") or ""),
        "confidence": 0.0,
        "generality": 0.0,
    }
    try:
        experienced["confidence"] = float(obj.get("confidence", 0.0) or 0.0)
    except (TypeError, ValueError):
        experienced["confidence"] = 0.0
    try:
        experienced["generality"] = float(obj.get("generality", 0.0) or 0.0)
    except (TypeError, ValueError):
        experienced["generality"] = 0.0
    return experienced


def _run_extraction(trace: str, model_cfg: Optional[dict]) -> Optional[dict]:
    """Spin up an LLM from the model config carried by the request and run one extraction; returns None on failure (fail-closed)."""
    cfg = model_cfg or {}
    api_key = cfg.get("api_key")
    if not api_key:
        logger.warning("[evolution] extract failed: no api_key in model_cfg")
        return None
    try:
        from agent.core import _create_chat_openai
        from langchain_core.messages import SystemMessage, HumanMessage
        llm = _create_chat_openai(
            model=cfg.get("configured_model") or "",
            api_key=api_key,
            base_url=cfg.get("base_url") or "",
            temperature=0.1,
            max_tokens=1024,
            provider=cfg.get("detected_provider") or cfg.get("provider"),
        )
        messages = [
            SystemMessage(content=_EXTRACTION_SYSTEM),
            HumanMessage(content=f"Trace:\n{trace}"),
        ]
        result = llm.invoke(messages)
        content = getattr(result, "content", "") or ""
        return _parse_experience(content)
    except Exception as e:
        logger.warning(f"[evolution] extract failed: {e}")
        return None


def _record_repair_experience(project_key: str, experienced: dict) -> None:
    """Write the extracted experience back to the C#-side repair_experience (fail-closed)."""
    try:
        from tools.tia_client import record_repair_experience
        resp = record_repair_experience(project_key, experienced)
        low = (resp or "").lstrip().lower()
        if low.startswith("error:") or low.startswith("tia tool error"):
            logger.warning(f"[evolution] record failed: {resp}")
        else:
            logger.info(f"[evolution] recorded experience project={project_key}")
    except Exception as e:
        logger.warning(f"[evolution] record failed: {e}")


def _safe_filename(project_key: str) -> str:
    """Reduce an absolute path to a safe filename field."""
    if not project_key:
        return "default"
    sanitized = "".join("_" if c in '\\/:*?"<>|' else c for c in project_key)
    return (sanitized.strip("_.") or "default")[:120]


def _truncate(value: Any, cap: int = _MAX_ENTRY_BYTES) -> Any:
    """Recursively truncate over-long strings while keeping the JSON parseable."""
    if isinstance(value, str):
        if len(value.encode("utf-8", "ignore")) > cap:
            return value[:cap] + f"...<truncated>"
        return value
    if isinstance(value, dict):
        return {k: _truncate(v, cap) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_truncate(v, cap) for v in value]
    return value


class EpisodeRecorder:
    """Append-only JSONL recorder; one instance corresponds to one episode."""

    def __init__(self, project_key: str, intent: str, prompt: str, model: str):
        try:
            os.makedirs(EPISODE_DIR, exist_ok=True)
        except Exception as e:  # fail-closed: makedirs failure is not thrown
            logger.warning(f"[evolution] makedirs failed: {e}")

        ts = time.strftime("%Y%m%d_%H%M%S", time.localtime())
        self.episode_id = f"{_safe_filename(project_key)}_{ts}_{os.getpid()}"
        self.path = os.path.join(EPISODE_DIR, f"{self.episode_id}.jsonl")
        self._f: Optional[Any] = None
        self._logged_write_error = False
        try:
            self._f = open(self.path, "a", encoding="utf-8")
            self._write({
                "type": "meta",
                "episode_id": self.episode_id,
                "project_key": project_key,
                "intent": intent,
                "prompt": prompt,
                "model": model,
                "ts": ts,
            })
        except Exception as e:
            logger.warning(f"[evolution] collect skipped: cannot open {self.path}: {e}")
            self._f = None

    @property
    def active(self) -> bool:
        return self._f is not None

    def _write(self, entry: Any) -> None:
        if self._f is None:
            return
        try:
            line = json.dumps(_truncate(entry), ensure_ascii=False, default=str)
            self._f.write(line + "\n")
            self._f.flush()
        except Exception as e:
            # fail-closed: recording failures never throw. Log ONCE so disk-full /
            # serialization errors stay observable without spamming the log on a
            # persistently bad handle.
            if not self._logged_write_error:
                self._logged_write_error = True
                logger.warning(f"[evolution] collect skipped: write failed: {e}")

    def record(self, entry: Any) -> None:
        self._write(entry)

    def close(self, status: str) -> None:
        if self._f is None:
            return
        try:
            self._write({"type": "meta_end", "status": status})
        finally:
            try:
                self._f.close()
            except Exception:
                pass
            self._f = None


class ExtractionWorker:
    """Process-level background worker (daemon thread + queue), independent of the
    HTTP request lifecycle.

    Closing the chat window only disconnects that one HTTP connection from the
    frontend and does not affect this thread; as long as the backend process is
    alive, queued extraction tasks continue to run (P0 skeleton; P1 hooks up LLM
    extraction + RAG validation here).
    """

    def __init__(self):
        self._queue: "queue.Queue[dict]" = queue.Queue()
        self._thread: Optional[threading.Thread] = None
        self._lock = threading.Lock()

    def start(self) -> None:
        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return
            self._thread = threading.Thread(
                target=self._run, name="evolution-extractor", daemon=True
            )
            self._thread.start()

    def enqueue(self, episode_id: str, path: str, model_cfg: Optional[dict] = None, project_key: str = "") -> None:
        # Idempotently ensure the thread is started; enqueue does not block the main flow.
        # model_cfg/project_key are kept in memory only, not persisted.
        self.start()
        try:
            self._queue.put_nowait({
                "episode_id": episode_id, "path": path,
                "model_cfg": model_cfg, "project_key": project_key,
            })
        except Exception as e:
            logger.warning(f"[evolution] enqueue failed: {e}")

    def _run(self) -> None:
        while True:
            try:
                job = self._queue.get()
                self._process(job)
            except Exception as e:  # fail-closed: the worker loop never exits
                logger.warning(f"[evolution] worker error: {e}")

    def _process(self, job: dict) -> None:
        """P1 extraction: read trace → LLM extracts structured experience (RAG validation is wired in P2)."""
        episode_id = job.get("episode_id")
        try:
            trace = _read_trace(job.get("path") or "")
            if not trace:
                return
            experienced = _run_extraction(trace, job.get("model_cfg"))
            if not experienced:
                return
            if not experienced.get("resolved"):
                logger.info(f"[evolution] extract skipped (not resolved) episode={episode_id}")
                return
            if not experienced.get("symptom") or not experienced.get("fix_pattern"):
                logger.info(f"[evolution] extract skipped (empty fields) episode={episode_id}")
                return
            confidence = experienced.get("confidence") or 0.0
            if confidence < _MIN_CONFIDENCE:
                logger.info(f"[evolution] extract skipped (low confidence={confidence:.2f}) episode={episode_id}")
                return
            # Write to the C#-side repair_experience (type-level experience has no symbol to verify; quality gate = resolved + confidence).
            project_key = job.get("project_key") or ""
            if not project_key:
                logger.info(f"[evolution] record skipped (no project_key) episode={episode_id}")
                return
            logger.info(
                f"[evolution] extracted episode={episode_id} "
                f"confidence={experienced.get('confidence'):.2f} "
                f"symptom={experienced.get('symptom')!r}"
            )
            _record_repair_experience(project_key, experienced)
        except Exception as e:
            logger.warning(f"[evolution] extract failed: {e}")


# Module-level singleton, shared by supervisor / core.
extraction_worker = ExtractionWorker()