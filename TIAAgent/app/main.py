"""FastAPI application — TIA AI Code Generator backend server."""

import os
import json
import logging
import time
import asyncio
import threading
from typing import Optional

from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse, JSONResponse
from dotenv import load_dotenv
from logging.handlers import RotatingFileHandler

from core.request_context import set_rid, RidLogFilter

from app.models import (
    GenerateRequest, GenerateResponse,
    ConvertXmlRequest, ConvertXmlResponse,
    ResolveXmlFragmentRequest, ResolveXmlFragmentResponse,
    ToolResultInput,
    ValidateSclRequest, ValidateSclResponse,
    ProbeVisionRequest, ProbeVisionResponse,
    TestProviderRequest, TestProviderResponse,
)
from agent.core import (
    generate_code_stream, generate_code,
    convert_xml_to_code,
    run_planning_agent_stream, start_project_plan, probe_vision_support,
    test_provider_connection,
    LocalModelRejectedError,
)
from converters.xml_ast import extract_xml_payload, extract_xml_fragment
from converters.scl_validator import validate_scl, ValidationResult

load_dotenv()

LOG_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "logs")
os.makedirs(LOG_DIR, exist_ok=True)
logger = logging.getLogger("backend")
logger.setLevel(logging.INFO)
file_handler = RotatingFileHandler(
    os.path.join(LOG_DIR, "backend.log"),
    maxBytes=1_000_000, backupCount=3, encoding="utf-8",
)
file_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
if not any(isinstance(h, RotatingFileHandler) for h in logger.handlers):
    logger.addHandler(file_handler)
# Every line logged while a run is active carries [rid=...] (the id C# sent for that run).
if not any(isinstance(f, RidLogFilter) for f in logger.filters):
    logger.addFilter(RidLogFilter())

# --- Rate limiting & concurrency control ---
_RATE_LIMIT_RPM = int(os.environ.get("TIA_RATE_LIMIT_RPM", "30"))
_RATE_LIMIT_BURST = int(os.environ.get("TIA_RATE_LIMIT_BURST", "5"))
_MAX_CONCURRENT_LLM = int(os.environ.get("TIA_MAX_CONCURRENT_LLM", "3"))
_MAX_PROMPT_LENGTH = int(os.environ.get("TIA_MAX_PROMPT_LENGTH", "16000"))


class TokenBucket:
    """Thread-safe token bucket for rate limiting."""

    def __init__(self, rate_per_minute: int, burst: int):
        self._rate = rate_per_minute / 60.0
        self._burst = burst
        self._tokens = float(burst)
        self._last_refill = time.monotonic()
        self._lock = threading.Lock()

    def consume(self, tokens: int = 1) -> bool:
        with self._lock:
            now = time.monotonic()
            elapsed = now - self._last_refill
            self._tokens = min(self._burst, self._tokens + elapsed * self._rate)
            self._last_refill = now
            if self._tokens >= tokens:
                self._tokens -= tokens
                return True
            return False

    @property
    def available(self) -> float:
        with self._lock:
            now = time.monotonic()
            elapsed = now - self._last_refill
            return min(self._burst, self._tokens + elapsed * self._rate)


_rate_bucket = TokenBucket(_RATE_LIMIT_RPM, _RATE_LIMIT_BURST)
_concurrency_semaphore = asyncio.Semaphore(_MAX_CONCURRENT_LLM)

# --- App ---
app = FastAPI(title="TIA AI Code Generator")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# --- Auth middleware ---
BACKEND_TOKEN = os.environ.get("TIA_BACKEND_TOKEN", "")


@app.middleware("http")
async def auth_middleware(request: Request, call_next):
    if request.url.path in ("/health", "/metrics") or not BACKEND_TOKEN:
        return await call_next(request)
    auth_header = request.headers.get("Authorization", "")
    expected = f"Bearer {BACKEND_TOKEN}"
    if auth_header != expected:
        return JSONResponse(status_code=401, content={"detail": "Unauthorized"})
    return await call_next(request)


# --- Guards ---
async def _check_rate_and_concurrency(request: Request, gen_request = None) -> Optional[JSONResponse]:
    """Token-bucket rate limit. This backend only serves cloud models
    (Ollama runs inside the C# host and never reaches here), so a single
    cloud-tier bucket applies to every request."""
    if not _rate_bucket.consume(1):
        retry_after = max(1, int(60.0 / _RATE_LIMIT_RPM))
        logger.warning(f"[RATE] Rate limit exceeded, retry_after={retry_after}s")
        return JSONResponse(
            status_code=429,
            content={
                "detail": f"Rate limit exceeded. Max {_RATE_LIMIT_RPM} requests/min. Retry after {retry_after}s.",
                "retry_after": retry_after,
            },
            headers={"Retry-After": str(retry_after)},
        )
    return None


def _validate_prompt_length(prompt: str, request_type: str) -> Optional[JSONResponse]:
    if len(prompt) > _MAX_PROMPT_LENGTH:
        return JSONResponse(
            status_code=400,
            content={
                "detail": f"{request_type} prompt too long: {len(prompt)} chars (max {_MAX_PROMPT_LENGTH}). "
                          f"Consider reducing context or splitting your request."
            },
        )
    return None


# HARD ISOLATION: local/Ollama endpoints that must NEVER reach this cloud backend.
_LOCAL_ENDPOINT_MARKERS = ("11434", "ollama", "localhost", "127.0.0.1", "0.0.0.0", "::1")


def _reject_local_model_request(request, request_type: str) -> Optional[JSONResponse]:
    """Fail closed if a local/Ollama model reaches this cloud-only backend.

    Ollama runs entirely inside the C# host's local ReAct loop and must never
    cross into the Python backend — it has no native function calling and would
    hang the multi-agent supervisor. This is the second line of defense behind
    the C# router; here we reject outright instead of silently accommodating.
    """
    provider = str(getattr(request, "provider", "") or "").strip().lower()
    base_url = str(getattr(request, "base_url", "") or "").strip().lower()
    is_local = provider == "ollama" or any(m in base_url for m in _LOCAL_ENDPOINT_MARKERS)
    if is_local:
        logger.warning(
            f"[ISOLATION] Rejected local model on {request_type}: "
            f"provider={provider!r} base_url={base_url!r}"
        )
        return JSONResponse(
            status_code=400,
            content={
                "detail": (
                    "Local/Ollama models are not allowed on the cloud Python backend. "
                    "Local models must run in the C# host's local ReAct loop."
                )
            },
        )
    return None


# --- SSE helpers ---
def _take_full_chunks(text: str, size: int):
    full_len = (len(text) // size) * size
    if full_len <= 0:
        return "", text
    return text[:full_len], text[full_len:]


def _chunk(event: str, text: str, size: int = 16):
    if not text:
        return
    i = 0
    L = len(text)
    while i < L:
        part = text[i:i + size]
        i += size
        yield "event: " + event + "\n" + "data: " + json.dumps(part) + "\n\n"


def _notice_frame(delta) -> str:
    """Serialize a notice payload defensively; a bad payload degrades to its text, never breaks the stream."""
    try:
        return "event: notice\ndata: " + json.dumps(delta, ensure_ascii=False) + "\n\n"
    except Exception:
        try:
            text = str(delta)
        except Exception:
            text = ""
        return "event: notice\ndata: " + json.dumps(
            {"code": "", "text": text, "params": []}, ensure_ascii=False
        ) + "\n\n"


def _is_transport_interruption(err: Exception) -> bool:
    msg = (str(err) if err is not None else "").lower()
    transport_keywords = [
        "errno 22", "invalid argument", "stream closed",
        "connection reset", "broken pipe", "connection aborted",
        "connection closed", "peer disconnected",
        "connection forcibly closed", "recv failure",
        "client closed", "disconnect",
    ]
    for kw in transport_keywords:
        if kw in msg:
            return True
    if isinstance(err, OSError):
        return True
    return False


# ── Endpoints ────────────────────────────────────────────────

_SSE_HEARTBEAT_S = 15.0


async def with_heartbeat(source, interval: float = _SSE_HEARTBEAT_S):
    """Re-yield an SSE generator, adding a ': heartbeat' comment whenever it stays quiet for `interval` s.

    The in-loop heartbeats inside gen() only run when an event arrives. While a tool is blocked on the
    user (a write-approval card), no event arrives, so nothing would keep the connection warm.
    Here the source runs in its own task and the timer is independent of it.

    The task is created while the caller's rid is already set (gen() sets it at its first line, inside
    the pump task, so the ContextVar lives in that task and its children).
    """
    queue: asyncio.Queue = asyncio.Queue()
    done = object()

    async def pump():
        try:
            async for item in source:
                await queue.put(item)
        except asyncio.CancelledError:
            raise
        except BaseException as e:  # hand the failure to the consumer instead of losing it
            await queue.put(e)
        finally:
            await queue.put(done)

    task = asyncio.ensure_future(pump())
    getter = None
    try:
        while True:
            if getter is None:
                getter = asyncio.ensure_future(queue.get())
            finished, _ = await asyncio.wait({getter}, timeout=interval)
            if not finished:
                yield ": heartbeat\n\n"
                continue
            item = getter.result()
            getter = None
            if item is done:
                return
            if isinstance(item, BaseException):
                raise item
            yield item
    finally:
        if getter is not None:
            getter.cancel()
        task.cancel()
        try:
            await task
        except BaseException:
            pass


@app.post("/generate", response_model=GenerateResponse)
async def generate(request: GenerateRequest, req: Request):
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "generate"):
        return local_resp
    if len_resp := _validate_prompt_length(request.prompt, "generate"):
        return len_resp

    if _concurrency_semaphore.locked():
        return JSONResponse(
            status_code=429,
            content={"detail": f"Too many concurrent LLM requests. Max {_MAX_CONCURRENT_LLM}."},
            headers={"Retry-After": "5"},
        )

    try:
        async with _concurrency_semaphore:
            rid = set_rid(request.rid) or set_rid(os.urandom(3).hex())
            logger.info(f"[REQ] /generate rid={rid} model={request.model} prompt_len={len(request.prompt)}")
            model_config = {
                "api_key": request.api_key, "model": request.model,
                "base_url": request.base_url, "provider": request.provider,
                "context_window": request.context_window,
                "reasoning_effort": request.reasoning_effort,
            }
            result = await generate_code(
                request.prompt, request.context or "", model_config,
                request.history or [], request.intent or "",
                request.images,
                request.project_key or "", request.evolution_enabled or False,
            )
            if result.get("error"):
                logger.warning(f"[RES] /generate rid={rid} error={result.get('error')}")
                return GenerateResponse(
                    code=result.get("scl_code", "// No code generated"),
                    error=result.get("error"),
                )
            code = result.get("scl_code", "// No code generated")
            thinking = result.get("thinking", "")
            logger.info(f"[RES] /generate rid={rid} code_len={len(code)} thinking={thinking}")
            return GenerateResponse(code=code, thinking=thinking)
    except LocalModelRejectedError as e:
        # Second line of defense: the endpoint guard should have caught this,
        # but if a local model slipped through, fail closed with a clear 400.
        logger.warning(f"[ISOLATION] /generate rejected local model: {e}")
        raise HTTPException(status_code=400, detail=str(e))
    except Exception as e:
        logger.exception(f"[ERR] /generate failed: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/generate_stream")
async def generate_stream(request: GenerateRequest, req: Request):
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "generate_stream"):
        return local_resp
    if len_resp := _validate_prompt_length(request.prompt or "", "generate_stream"):
        return len_resp

    async def gen():
        rid = set_rid(request.rid) or set_rid(os.urandom(3).hex())
        phase = "init"
        t0 = time.perf_counter()
        try:
            phase = "generating"
            prompt_len = len(request.prompt or "")
            ctx_len = len(request.context or "")

            if (request.prompt or "").strip().startswith("__debug_stream__"):
                payload = ("debug stream\n" + ("0123456789abcdef" * 256)).strip()
                sent = 0
                for frame in _chunk("content", payload, 16):
                    try:
                        yield frame
                    except (OSError, RuntimeError) as emit_err:
                        logger.warning(f"[STREAM] debug emit interrupted rid={rid} sent={sent} err={emit_err}")
                        return
                    sent += 1
                    if sent == 1 or sent % 50 == 0:
                        ms = int((time.perf_counter() - t0) * 1000)
                        logger.info(f"[debug_stream] rid={rid} sent={sent} ms={ms}")
                    await asyncio.sleep(0.02)
                yield "event: done\ndata: ok\n\n"
                return

            yield "event: thinking\ndata: " + json.dumps("generating") + "\n\n"
            model_config = {
                "api_key": request.api_key, "model": request.model,
                "base_url": request.base_url, "provider": request.provider,
                "context_window": request.context_window,
                "reasoning_effort": request.reasoning_effort,
            }
            # Diagnostic: log raw request fields for Ollama debugging
            logger.info(
                f"[DIAG] model={request.model!r} provider={request.provider!r} "
                f"base_url={request.base_url!r} api_key_set={bool(request.api_key)}"
            )
            error = ""
            first_token_ms = None
            out_content = 0
            out_thinking = 0
            content_buf = ""
            event_types = {}
            last_emit_time = time.perf_counter()
            heartbeat_interval = 15.0

            try:
                logger.info(
                    f"[STREAM] rid={rid} starting generate_code_stream model={request.model} "
                    f"prompt_len={prompt_len} ctx_len={ctx_len} intent={request.intent}"
                )
                async for event, delta in generate_code_stream(
                    request.prompt or "", request.context or "",
                    model_config, request.history or [], request.intent or "",
                    request.planning_knowledge or "",
                    request.images,
                    request.project_key or "", request.evolution_enabled or False,
                ):
                    event_types[event] = event_types.get(event, 0) + 1
                    if event == "thinking":
                        out_thinking += 1
                        try:
                            frame = "event: thinking\ndata: " + json.dumps(str(delta)) + "\n\n"
                            yield frame
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError) as emit_err:
                            logger.warning(f"[STREAM] thinking emit interrupted rid={rid} err={emit_err}")
                            return
                    elif event == "notice":
                        try:
                            frame = _notice_frame(delta)
                            yield frame
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError) as emit_err:
                            logger.warning(f"[STREAM] notice emit interrupted rid={rid} err={emit_err}")
                            return
                    elif event in ("content", "delta"):
                        if first_token_ms is None and delta:
                            first_token_ms = int((time.perf_counter() - t0) * 1000)
                            logger.info(
                                f"[cloud] rid={rid} model={request.model} first_token_ms={first_token_ms} "
                                f"prompt_len={prompt_len} ctx_len={ctx_len}"
                            )
                        content_buf += str(delta)
                        head, content_buf = _take_full_chunks(content_buf, 16)
                        for frame in _chunk("content", head, 16):
                            try:
                                yield frame
                                last_emit_time = time.perf_counter()
                            except (OSError, RuntimeError) as emit_err:
                                logger.warning(f"[STREAM] emit interrupted rid={rid} out_content={out_content} err={emit_err}")
                                return
                            out_content += 1
                    elif event == "tool_call":
                        try:
                            yield "event: tool_call\ndata: " + str(delta) + "\n\n"
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError):
                            return
                    elif event == "token_usage":
                        try:
                            yield "event: token_usage\ndata: " + str(delta) + "\n\n"
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError):
                            return
                    elif event == "plan_progress":
                        try:
                            yield "event: plan_progress\ndata: " + str(delta) + "\n\n"
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError):
                            return
                    elif event == "error":
                        error = str(delta)

                    if time.perf_counter() - last_emit_time >= heartbeat_interval:
                        try:
                            yield ": heartbeat\n\n"
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError):
                            return

                logger.info(
                    f"[STREAM] rid={rid} generate_code_stream completed "
                    f"event_types={event_types} out_content={out_content} out_thinking={out_thinking}"
                )
            except Exception as e:
                if _is_transport_interruption(e):
                    logger.warning(f"[STREAM] upstream interrupted rid={rid} type={type(e).__name__} err={e}")
                    return
                error = str(e)
                err_lower = error.lower()
                if "403" in err_lower or "free tier" in err_lower or "quota" in err_lower:
                    logger.warning(f"[STREAM] rid={rid} API quota/error: {error}")
                    error = f"// API quota error: {error}"
                elif "429" in err_lower or "rate limit" in err_lower:
                    logger.warning(f"[STREAM] rid={rid} rate limited: {error}")
                    error = f"// API rate limit: {error}"
                elif "503" in err_lower or "no available" in err_lower:
                    logger.warning(f"[STREAM] rid={rid} service unavailable: {error}")
                    error = f"// API service unavailable: {error}"
                elif "401" in err_lower or "unauthorized" in err_lower or "expired" in err_lower:
                    logger.warning(f"[STREAM] rid={rid} auth error: {error}")
                    error = f"// API authentication failed: {error}"
                elif "missing api key" in err_lower:
                    logger.warning(f"[STREAM] rid={rid} no API key: {error}")
                    error = "// No API key configured: set it in AI settings"
                else:
                    logger.warning(f"[STREAM] rid={rid} agent error: {type(e).__name__}: {e}")

            if content_buf:
                for frame in _chunk("content", content_buf, 16):
                    try:
                        yield frame
                    except (OSError, RuntimeError) as emit_err:
                        logger.warning(f"[STREAM] tail emit interrupted rid={rid} out_content={out_content} err={emit_err}")
                        return
                    out_content += 1

            if error:
                if _is_transport_interruption(Exception(error)):
                    logger.warning(f"[STREAM] suppress transport-like agent error rid={rid} err={error}")
                    return
                yield "event: error\ndata: " + json.dumps(f"// Agent Error: {error}") + "\n\n"

            total_ms = int((time.perf_counter() - t0) * 1000)
            logger.info(f"[cloud] rid={rid} model={request.model} total_ms={total_ms} out_content={out_content}")
            yield "event: done\ndata: ok\n\n"
        except Exception as e:
            if _is_transport_interruption(e):
                logger.warning(f"[STREAM] outer interrupted rid={rid} phase={phase} type={type(e).__name__} err={e}")
                return
            logger.exception(f"[ERR] /generate_stream failed phase={phase} err={str(e)}")
            total_ms = int((time.perf_counter() - t0) * 1000)
            logger.error(f"[backend] rid={rid} failed_ms={total_ms} phase={phase} err={str(e)}")
            try:
                yield "event: error\ndata: " + json.dumps(f"// Backend Error: {str(e)}") + "\n\n"
                yield "event: done\ndata: ok\n\n"
            except (OSError, RuntimeError):
                return

    return StreamingResponse(
        with_heartbeat(gen()),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


@app.post("/chat_plan")
async def chat_plan(request: GenerateRequest, req: Request):
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "chat_plan"):
        return local_resp
    if len_resp := _validate_prompt_length(request.prompt or "", "chat_plan"):
        return len_resp

    async def gen():
        rid = set_rid(request.rid) or set_rid(os.urandom(3).hex())
        t0 = time.perf_counter()
        first_token_ms = None
        out_content = 0
        content_buf = ""
        last_emit_time = time.perf_counter()
        heartbeat_interval = 15.0

        try:
            model_config = {
                "api_key": request.api_key, "model": request.model,
                "base_url": request.base_url, "provider": request.provider,
                "context_window": request.context_window,
                "reasoning_effort": request.reasoning_effort,
            }
            logger.info(f"[PLAN] rid={rid} starting planning_agent model={request.model} prompt_len={len(request.prompt or '')}")

            async for event, delta in run_planning_agent_stream(
                request.prompt or "", request.context or "",
                model_config, request.history or [],
                request.planning_knowledge or "",
                request.intent or "",
                request.images,
                request.project_key or "",
            ):
                if event == "thinking":
                    try:
                        yield "event: thinking\ndata: " + json.dumps(str(delta)) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "notice":
                    try:
                        yield _notice_frame(delta)
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event in ("content", "delta"):
                    if first_token_ms is None and delta:
                        first_token_ms = int((time.perf_counter() - t0) * 1000)
                        logger.info(f"[PLAN] rid={rid} first_token_ms={first_token_ms}")
                    content_buf += str(delta)
                    head, content_buf = _take_full_chunks(content_buf, 16)
                    for frame in _chunk("content", head, 16):
                        try:
                            yield frame
                            last_emit_time = time.perf_counter()
                        except (OSError, RuntimeError):
                            return
                        out_content += 1
                elif event == "tool_call":
                    try:
                        yield "event: tool_call\ndata: " + str(delta) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "error":
                    try:
                        yield "event: error\ndata: " + json.dumps(str(delta)) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "token_usage":
                    try:
                        yield "event: token_usage\ndata: " + str(delta) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "plan_progress":
                    try:
                        yield "event: plan_progress\ndata: " + str(delta) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "milestone":
                    try:
                        yield "event: milestone\ndata: " + str(delta) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "task_failed":
                    try:
                        yield "event: task_failed\ndata: " + str(delta) + "\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return
                elif event == "done":
                    break

                if time.perf_counter() - last_emit_time >= heartbeat_interval:
                    try:
                        yield ": heartbeat\n\n"
                        last_emit_time = time.perf_counter()
                    except (OSError, RuntimeError):
                        return

            if content_buf:
                for frame in _chunk("content", content_buf, 16):
                    try:
                        yield frame
                    except (OSError, RuntimeError):
                        return
                    out_content += 1

            total_ms = int((time.perf_counter() - t0) * 1000)
            logger.info(f"[PLAN] rid={rid} completed total_ms={total_ms} out_content={out_content}")
            yield "event: done\ndata: ok\n\n"
        except Exception as e:
            if _is_transport_interruption(e):
                logger.warning(f"[PLAN] transport interrupted rid={rid} err={e}")
                return
            logger.exception(f"[PLAN] rid={rid} failed: {e}")
            try:
                yield "event: error\ndata: " + json.dumps(str(e)) + "\n\n"
                yield "event: done\ndata: ok\n\n"
            except (OSError, RuntimeError):
                return

    return StreamingResponse(
        with_heartbeat(gen()),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


@app.post("/plan/decide")
async def plan_decide(req: Request):
    """Record a milestone approval decision (approve / adjust / reject). The frontend calls this endpoint after approve,
    then re-sends /chat_plan (with project_key) to resume from the checkpoint. Idempotent, fail-closed."""
    from agent.plan_store import PlanStore
    from agent.plan_models import MilestoneDecision, PlanStatus, TaskStatus

    try:
        body = await req.json()
    except Exception:
        raise HTTPException(status_code=400, detail="invalid JSON body")

    plan_id = (body.get("plan_id") or "").strip()
    phase_id = (body.get("phase_id") or "").strip()
    decision = (body.get("decision") or "").strip()
    note = (body.get("note") or "").strip()
    if not plan_id or not phase_id or decision not in ("approve", "adjust", "reject"):
        raise HTTPException(
            status_code=400,
            detail="plan_id / phase_id / decision required; decision in approve|adjust|reject",
        )

    store = PlanStore()
    plan = store.load(plan_id)
    if plan is None:
        raise HTTPException(status_code=404, detail="plan not found")

    plan.milestone_decisions.append(
        MilestoneDecision(phase_id=phase_id, decision=decision, note=note or None)
    )
    if decision == "reject":
        plan.status = PlanStatus.REJECTED.value
    elif decision == "adjust":
        # Only replan the unfinished part: reset all not-yet-success tasks to pending (including downstream marked stale by
        # interface drift), and keep already-success tasks (idempotent; resume does not redo them).
        # note is an optional adjustment note; the real "re-decompose the DAG" would combine the new requirement (note), but for now
        # we just ensure the unfinished part will re-execute after resume.
        for t in plan.tasks:
            if t.status != TaskStatus.SUCCESS.value:
                t.status = TaskStatus.PENDING.value
                t.error = None
    store.save(plan)
    return JSONResponse({
        "plan_id": plan_id,
        "phase_id": phase_id,
        "decision": decision,
        "plan_status": plan.status,
    })


@app.post("/plan/task_decide")
async def plan_task_decide(req: Request):
    """Task failure decision (continue / skip / terminate). The frontend calls this endpoint after receiving a task_failed event,
    then re-sends /plan/run (with project_key) to resume from the checkpoint. Idempotent, fail-closed."""
    from agent.plan_store import PlanStore
    from agent.executor import apply_task_decision

    try:
        body = await req.json()
    except Exception:
        raise HTTPException(status_code=400, detail="invalid JSON body")

    plan_id = (body.get("plan_id") or "").strip()
    task_id = (body.get("task_id") or "").strip()
    decision = (body.get("decision") or "").strip()
    if not plan_id or not task_id or decision not in ("continue", "skip", "terminate"):
        raise HTTPException(
            status_code=400,
            detail="plan_id/task_id/decision required; decision in continue|skip|terminate",
        )

    store = PlanStore()
    plan = store.load(plan_id)
    if plan is None:
        raise HTTPException(status_code=404, detail="plan not found")

    err = apply_task_decision(plan, task_id, decision)
    if err:
        raise HTTPException(status_code=400, detail=err)
    store.save(plan)
    return JSONResponse({
        "plan_id": plan_id,
        "task_id": task_id,
        "decision": decision,
        "plan_status": plan.status,
    })


@app.post("/plan/run")
async def plan_run(request: GenerateRequest, req: Request):
    """Project-level long-task startup entry: produce the plan and start a background job, immediately returning {started, plan_id, ...}.

    Non-project / planning failure returns {"started": false}; the caller falls back to the single-block /chat_plan flow.
    """
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "plan_run"):
        return local_resp
    if len_resp := _validate_prompt_length(request.prompt or "", "plan_run"):
        return len_resp

    model_config = {
        "api_key": request.api_key, "model": request.model,
        "base_url": request.base_url, "provider": request.provider,
        "context_window": request.context_window,
        "reasoning_effort": request.reasoning_effort,
    }
    result = await start_project_plan(
        request.prompt or "", request.context or "", model_config,
        request.history or [], request.planning_knowledge or "",
        request.intent or "", request.images, request.project_key or "",
        verify_compile_at_end=bool(request.verify_compile_at_end),
    )
    if result is None:
        return JSONResponse({"started": False})
    return JSONResponse({"started": True, **result})


@app.get("/plan/{plan_id}")
async def plan_status(plan_id: str):
    """Progress snapshot: the data source for reconnecting / frontend polling."""
    from agent.plan_job import plan_jobs
    snap = plan_jobs.snapshot(plan_id)
    if snap is None:
        raise HTTPException(status_code=404, detail="plan not found")
    return JSONResponse(snap)


@app.post("/plan/{plan_id}/stream")
async def plan_stream(plan_id: str):
    """Follow a started plan's background job: replay the progress snapshot first, then forward events live until the round ends."""
    from agent.plan_job import plan_jobs, JOB_END_EVENT

    snap = plan_jobs.snapshot(plan_id)
    if snap is None:
        raise HTTPException(status_code=404, detail="plan not found")

    async def gen():
        yield "event: plan_status\ndata: " + json.dumps(snap, ensure_ascii=False) + "\n\n"
        q = plan_jobs.queue(plan_id)
        if q is None:
            yield "event: done\ndata: ok\n\n"
            return
        while True:
            event, delta = await q.get()
            if event == JOB_END_EVENT:
                yield "event: done\ndata: ok\n\n"
                return
            if event == "notice":
                yield _notice_frame(delta)
            elif event in ("plan_progress", "milestone", "done", "task_failed"):
                yield f"event: {event}\ndata: " + str(delta) + "\n\n"
            elif event in ("thinking", "error"):
                yield f"event: {event}\ndata: " + json.dumps(str(delta), ensure_ascii=False) + "\n\n"
            elif event in ("content", "delta"):
                yield f"event: content\ndata: " + json.dumps(str(delta), ensure_ascii=False) + "\n\n"

    return StreamingResponse(
        with_heartbeat(gen()),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


@app.post("/convert_xml", response_model=ConvertXmlResponse)
async def convert_xml(request: ConvertXmlRequest, req: Request):
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "convert_xml"):
        return local_resp
    try:
        rid = (request.rid or "").strip() or os.urandom(3).hex()
        raw_xml = request.xml or ""
        xml_payload = extract_xml_payload(raw_xml)
        if not xml_payload:
            raise HTTPException(status_code=400, detail="No valid XML payload found")

        # The canonical IR comes from the C# snapshot projection supplied by the caller.
        # The backend never re-parses TIA XML into a second IR.
        agent_ir = (request.agent_ir or "").strip()
        agent_ir_summary = (request.agent_ir_summary or "").strip()
        model_config = {
            "api_key": request.api_key, "model": request.model,
            "base_url": request.base_url, "provider": request.provider,
        }
        logger.info(f"[REQ] /convert_xml rid={rid} xml_len={len(xml_payload)} ir_len={len(agent_ir)} model={request.model}")
        result = await convert_xml_to_code(
            xml_payload, request.instruction or "", model_config,
            agent_ir=agent_ir, agent_ir_summary=agent_ir_summary,
        )
        if result.get("error"):
            logger.warning(f"[RES] /convert_xml rid={rid} error={result.get('error')}")
            return ConvertXmlResponse(
                scl_code=result.get("scl_code", ""),
                ast_summary=agent_ir_summary or None, agent_ir=agent_ir or None,
                analysis=result.get("analysis"), error=result.get("error"),
            )
        code = result.get("scl_code", "") or ""
        logger.info(f"[RES] /convert_xml rid={rid} scl_len={len(code)}")
        return ConvertXmlResponse(
            scl_code=code, ast_summary=agent_ir_summary or None,
            agent_ir=agent_ir or None, analysis=result.get("analysis"),
        )
    except HTTPException:
        raise
    except Exception as e:
        logger.exception(f"[ERR] /convert_xml failed: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/resolve_xml_fragment", response_model=ResolveXmlFragmentResponse)
async def resolve_xml_fragment(request: ResolveXmlFragmentRequest):
    try:
        xml_payload = extract_xml_payload(request.xml or "")
        if not xml_payload:
            raise HTTPException(status_code=400, detail="No valid XML payload found")
        fragment = extract_xml_fragment(xml_payload, request.compile_unit_id or "", request.uid or "")
        if not fragment:
            return ResolveXmlFragmentResponse(
                fragment_xml="",
                compile_unit_id=request.compile_unit_id or "",
                uid=request.uid or "",
                error="Fragment not found",
            )
        return ResolveXmlFragmentResponse(
            fragment_xml=fragment,
            compile_unit_id=request.compile_unit_id or "",
            uid=request.uid or "",
        )
    except HTTPException:
        raise
    except Exception as e:
        logger.exception(f"[ERR] /resolve_xml_fragment failed: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/tool_result")
async def tool_result(res: ToolResultInput):
    return {"status": "ok", "name": res.name, "ok": res.ok}


@app.post("/validate", response_model=ValidateSclResponse)
async def validate_code(request: ValidateSclRequest):
    try:
        result: ValidationResult = validate_scl(request.code or "")
        logger.info(f"[VALIDATE] is_valid={result.is_valid} err_count={len(result.errors)}")
        return ValidateSclResponse(
            is_valid=result.is_valid,
            errors=list(result.errors),
            warnings=list(result.warnings),
            block_type=result.block_type,
            block_name=result.block_name,
        )
    except Exception as e:
        logger.exception(f"[ERR] /validate failed: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/probe_vision", response_model=ProbeVisionResponse)
async def probe_vision(request: ProbeVisionRequest, req: Request):
    """Probe whether a model supports image input (vision).

    The C# host maintains a local allow-list of known vision models and only
    calls this for models not on that list, caching the verdict. See
    ``agent.core.probe_vision_support`` for the classification logic.
    """
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "probe_vision"):
        return local_resp
    try:
        rid = (request.rid or "").strip() or os.urandom(3).hex()
        model_config = {
            "api_key": request.api_key, "model": request.model,
            "base_url": request.base_url, "provider": request.provider,
        }
        logger.info(f"[PROBE] /probe_vision rid={rid} model={request.model}")
        result = await probe_vision_support(model_config)
        return ProbeVisionResponse(
            result=result.get("result", "unknown"),
            model=request.model,
            detail=result.get("detail"),
        )
    except Exception as e:
        logger.exception(f"[ERR] /probe_vision failed: {str(e)}")
        # Never 500 for a capability probe — return unknown so the caller can
        # fall back to its allow-list.
        return ProbeVisionResponse(result="unknown", model=request.model, detail=str(e)[:300])


@app.post("/provider/test", response_model=TestProviderResponse)
async def provider_test(request: TestProviderRequest, req: Request):
    """Test a provider/model/key/base_url combination by sending one tiny real
    chat request through the SAME LLM factory the generate paths use.

    The C# "Test connection" button calls this instead of talking to each
    platform directly, so all platform-adapter knowledge stays in one place
    (the backend). The api_key is never echoed back in the response.
    """
    if limit_resp := await _check_rate_and_concurrency(req, request):
        return limit_resp
    if local_resp := _reject_local_model_request(request, "provider_test"):
        return local_resp
    try:
        rid = (request.rid or "").strip() or os.urandom(3).hex()
        model_config = {
            "api_key": request.api_key, "model": request.model,
            "base_url": request.base_url, "provider": request.provider,
        }
        logger.info(f"[TEST] /provider/test rid={rid} model={request.model} base_url={request.base_url}")
        result = await test_provider_connection(model_config)
        return TestProviderResponse(
            ok=bool(result.get("ok", False)),
            category=str(result.get("category", "unknown")),
            latency_ms=result.get("latency_ms"),
            model=result.get("model") or request.model,
            reply=result.get("reply"),
            detail=result.get("detail"),
        )
    except Exception as e:
        logger.exception(f"[ERR] /provider/test failed: {str(e)}")
        # Never 500 for a connectivity test — return a structured failure so the
        # C# UI can show a friendly message.
        return TestProviderResponse(
            ok=False, category="unknown", latency_ms=None,
            model=request.model, reply=None, detail=str(e)[:300],
        )


@app.get("/providers")
async def providers():
    """Return the cloud provider catalog (single source of truth).

    The C# host drives its provider/model dropdowns from this instead of a
    parallel hard-coded table, so "what the UI can select" always equals "what
    the backend can actually serve". Each provider carries an ``available`` flag
    reflecting whether its adapter dependency is installed in the backend.
    """
    try:
        from core.providers import list_providers
        return {"providers": list_providers()}
    except Exception as e:
        logger.exception(f"[ERR] /providers failed: {str(e)}")
        return {"providers": [], "error": str(e)[:300]}


@app.get("/health")
async def health_check():
    return {"status": "ok", "service": "TIA AI Code Generator"}
