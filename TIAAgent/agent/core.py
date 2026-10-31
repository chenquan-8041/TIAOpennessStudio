import os
import json
import asyncio
import logging
from typing import Dict, AsyncIterator, Tuple, List, Optional, Any
from langchain_openai import ChatOpenAI
from langchain_core.messages import SystemMessage, HumanMessage, AIMessage
from core.prompts import (
    S7_CONSTRAINT_PROMPT,
    SYSTEM_PROMPT_BASE,
    CODE_GEN_RULES,
    CHAT_RULES,
    COMMON_RULES,
    get_model_profile,
    detect_provider_from_config,
    get_cache_optimized_params,
)
from core.context import extract_stream_delta, extract_stream_reasoning, build_user_content


# ---------------------------------------------------------------------------
# JSON structured output helpers (Task 2.2 / Requirements 3.2, 3.3, 3.7)
# ---------------------------------------------------------------------------
# Pure helpers used by generate_code / generate_code_stream to parse the model's
# 4-field JSON envelope and assemble it into a complete SCL string. They do
# NOT touch any LLM client, network, or shared state -- this keeps them
# trivially unit/property testable.
#
# Contract (per design.md §Components.3 + §Data Models):
#   _try_parse_structured(text) -> dict | None
#     Returns the parsed dict only if it has all 4 string fields and a
#     whitelisted block_type. Otherwise None (caller falls back to the
#     existing markdown extraction path).
#   _assemble_scl(parsed)  -> str
#     Concatenates a complete SCL block in the canonical layout.

_ALLOWED_BLOCK_TYPES = frozenset({
    "FUNCTION_BLOCK",
    "FUNCTION",
    "DATA_BLOCK",
    "ORGANIZATION_BLOCK",
})

_REQUIRED_JSON_FIELDS = ("block_type", "block_name", "declarations", "body")


def _is_valid_structured(obj) -> bool:
    """True iff `obj` is the 4-field JSON envelope we expect from the LLM.

    All 4 fields must be present AND be strings, and `block_type` must be
    one of the 4 whitelisted block kinds.
    """
    if not isinstance(obj, dict):
        return False
    for k in _REQUIRED_JSON_FIELDS:
        v = obj.get(k)
        if not isinstance(v, str):
            return False
    if obj["block_type"] not in _ALLOWED_BLOCK_TYPES:
        return False
    return True


def _try_parse_structured(text: str) -> Optional[dict]:
    """Extract a 4-field JSON object from raw model output.

    Strategy (matches design.md §Components.3):
      1. ``json.loads`` the whole text.
      2. If that fails, locate the first ``{`` and the last ``}`` and try
         ``json.loads`` on that substring (handles markdown fences or a
         chatty preamble/epilogue around the JSON).
      3. Both failed -> return ``None``.

    Any of the following also yields ``None`` (caller falls back to markdown
    extraction): empty/None text, non-dict result, missing fields, non-string
    fields, or block_type outside the whitelist.
    """
    if not text:
        return None

    # 1) direct json.loads
    try:
        result = json.loads(text)
        if _is_valid_structured(result):
            return result
    except (json.JSONDecodeError, TypeError, ValueError):
        pass

    # 2) substring between first '{' and last '}'
    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        substr = text[start:end + 1]
        try:
            result = json.loads(substr)
            if _is_valid_structured(result):
                return result
        except (json.JSONDecodeError, TypeError, ValueError):
            pass

    return None


def _assemble_scl(parsed: dict) -> str:
    """Assemble a complete SCL string from the 4-field dict.

    Layout (per Requirement 3.3):
        <block_type> "<block_name>"
        { S7_Optimized_Access := 'FALSE' }
        VERSION : 0.1
        <declarations>
        BEGIN
        <body>
        END_<block_type>
    """
    bt = parsed["block_type"].strip()
    bn = parsed["block_name"].strip()
    decls = parsed["declarations"].rstrip()
    body = parsed["body"].strip("\r\n")
    return (
        f'{bt} "{bn}"\n'
        f"{{ S7_Optimized_Access := 'FALSE' }}\n"
        f"VERSION : 0.1\n"
        f"{decls}\n"
        f"BEGIN\n"
        f"{body}\n"
        f"END_{bt}"
    )


def _build_history_messages(history: Optional[List[Dict[str, str]]]):
    messages = []
    for item in history or []:
        role = str(item.get("role", "")).strip().lower()
        content = str(item.get("content", "")).strip()
        if not content:
            continue
        if role == "assistant":
            messages.append(AIMessage(content=content))
        elif role == "user":
            messages.append(HumanMessage(content=content))
        elif role == "system":
            # A `system` history entry is the conversation summary produced by the
            # C# side (AgentOrchestrator.CompressHistoryAsync). Preserve it as a
            # SystemMessage — dropping it here silently discarded the whole
            # compressed history on the Python path.
            messages.append(SystemMessage(content=content))
    return messages


# Openness API docs have been split into a skill directory: docs/deploy/tia-portal-openness/
# Main document: references/api_reference.md
_OPENNESS_DOCS_PATH = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "docs", "deploy", "tia-portal-openness", "references", "api_reference.md"
)

# On-demand loading: instead of pinning the full API reference into every system
# prompt, we keep only a lightweight topic index (the markdown headings) resident.
# The agent pulls the actual method details on demand via the openness-docs tools
# (list_plc_openness_topics / get_plc_openness_api_methods / search_openness_docs).
_openness_topic_index_cache: Optional[str] = None


def _build_openness_topic_index() -> str:
    """Extract only the markdown headings from the API reference as a resident
    topic index. Returns a short outline, not the full document body."""
    global _openness_topic_index_cache
    if _openness_topic_index_cache is not None:
        return _openness_topic_index_cache

    try:
        with open(_OPENNESS_DOCS_PATH, "r", encoding="utf-8") as f:
            lines = f.readlines()
        headings = [ln.rstrip() for ln in lines if ln.lstrip().startswith("#")]
        index = "\n".join(headings).strip()
        if not index:
            index = "(Openness API topic index unavailable; use the openness-docs tools.)"
    except Exception as e:
        logging.getLogger("backend").warning(f"[CTX] Failed to build Openness topic index: {e}")
        index = "(Openness API topic index unavailable; use the openness-docs tools.)"

    _openness_topic_index_cache = index
    return index


def _build_messages(prompt: str, context: str, history: Optional[List[Dict[str, str]]] = None, intent: str = "", planning_knowledge: str = "", images: Optional[List[str]] = None):
    # Resident content is the topic index only; full method details are fetched
    # on demand through the openness-docs tools.
    openness_docs = _build_openness_topic_index()

    # Apply strict constraints only when the intent is explicitly generating or modifying code
    is_generating_code = intent in ["GenerateCode", "ModifyCode", "GenerateAndFix", "Import"]

    constraint_prompt = S7_CONSTRAINT_PROMPT if is_generating_code else ""

    system_prompt = (
        constraint_prompt
        + SYSTEM_PROMPT_BASE.format(openness_docs=openness_docs)
        + (CODE_GEN_RULES if is_generating_code else CHAT_RULES)
        + COMMON_RULES
    )

    # Planning knowledge belongs in the system prompt as advisory guidance, kept
    # explicitly separate from the user's request and from project fact.
    pk = (planning_knowledge or "").strip()
    if pk:
        system_prompt += (
            "\n\n## Planning Knowledge (advisory only)\n"
            "The following are proven tool paths / past plans for similar tasks. "
            "Treat them as guidance, not as facts about the current project. "
            "Verify every observation against the live project before acting.\n"
            + pk
        )

    user_content = f"{prompt}"
    if context:
        user_content += f"\n\nProject Context:\n{context}"

    # Build the HumanMessage: use multimodal content format when images are present
    human_msg = HumanMessage(content=build_user_content(user_content, images))

    return [SystemMessage(content=system_prompt)] + _build_history_messages(history) + [human_msg]


def _clean_base_url(url: str) -> str:
    if not url:
        return "https://api.openai.com/v1"
    url = url.strip()
    if url.endswith("/chat/completions"):
        url = url[:-17]
    return url


class LocalModelRejectedError(Exception):
    """Raised when a local/Ollama model reaches the cloud-only Python backend.

    HARD ISOLATION RULE: Ollama (and any local model) MUST run entirely inside
    the C# host's local ReAct loop and MUST NEVER reach this Python backend.
    This backend is cloud-only. Ollama has no native function calling and is
    incompatible with the langchain-openai multi-agent supervisor here — letting
    it in causes the supervisor loop to spin forever. So we fail closed: any
    request that looks local is rejected outright, never "accommodated".
    """


# Substrings in a base_url that unambiguously indicate a LOCAL model endpoint.
_LOCAL_ENDPOINT_MARKERS = ("11434", "ollama", "localhost", "127.0.0.1", "0.0.0.0", "::1")


def _is_local_model(config: Dict[str, str]) -> bool:
    """Return True iff the config points at a local/Ollama model.

    Detection (any one is sufficient):
      - explicit provider == "ollama"
      - base_url pointing at a local/Ollama endpoint (11434 / localhost / ...)
    Cloud model names may contain colons or "local"-ish words, so we deliberately
    do NOT sniff the model name — only the explicit provider and the base_url.
    """
    cfg = config or {}
    provider = str(cfg.get("provider") or "").strip().lower()
    if provider == "ollama":
        return True
    base_url = str(cfg.get("base_url") or "").strip().lower()
    return any(marker in base_url for marker in _LOCAL_ENDPOINT_MARKERS)


def _reject_local_model(config: Dict[str, str]) -> None:
    """Fail closed if a local/Ollama model somehow reached this cloud-only backend."""
    if _is_local_model(config):
        cfg = config or {}
        raise LocalModelRejectedError(
            "Local/Ollama models are not allowed on the cloud Python backend. "
            "They must run in the C# host's local ReAct loop. "
            f"(provider={cfg.get('provider')!r}, base_url={cfg.get('base_url')!r})"
        )


def _resolve_provider(config: Dict[str, str]) -> str:
    provider = str((config or {}).get("provider") or "").strip().lower()
    if provider in ("openai", "openai_compat"):
        return "openai_compat"
    return "auto"


def _resolve_api_key(config: Dict[str, str], provider: str, base_url: str) -> str:
    # Cloud-only backend: an API key is always required. (Local models are
    # rejected upstream by _reject_local_model, so we never reach here for them.)
    api_key = str((config or {}).get("api_key") or os.getenv("OPENAI_API_KEY") or "").strip()
    if api_key:
        return api_key
    return ""


def _resolve_model_config(config: Dict[str, str]):
    """Resolve all model configuration from config dict + env vars.

    Returns a dict with keys: configured_model, provider, detected_provider,
    base_url, api_key. Raises RuntimeError if api_key is missing for
    OpenAI-compatible providers.
    """
    cfg = config or {}
    # HARD ISOLATION: reject local/Ollama before doing anything else.
    _reject_local_model(cfg)
    configured_model = (cfg.get("model") or os.getenv("MODEL_NAME") or "gpt-3.5-turbo").strip()
    provider = _resolve_provider(cfg)
    detected_provider = detect_provider_from_config(cfg)
    base_url_raw = cfg.get("base_url") or os.getenv("OPENAI_API_BASE") or ""
    base_url = _clean_base_url(base_url_raw)
    api_key = _resolve_api_key(cfg, provider, base_url)
    # Optional real model context window supplied by the C# host (may be absent).
    try:
        context_window = int(cfg.get("context_window") or 0) or None
    except (TypeError, ValueError):
        context_window = None
    # Optional reasoning effort (low/medium/high) forwarded to reasoning models.
    reasoning_effort = (str(cfg.get("reasoning_effort") or "").strip() or None)
    return {
        "configured_model": configured_model,
        "provider": provider,
        "detected_provider": detected_provider,
        "base_url": base_url,
        "api_key": api_key,
        "context_window": context_window,
        "reasoning_effort": reasoning_effort,
    }


class ReasoningChatOpenAI(ChatOpenAI):
    """ChatOpenAI variant that preserves the ``reasoning_content`` field.

    The upstream ``langchain_openai`` only extracts fields defined by the
    official OpenAI spec (content / tool_calls / function_call / audio) and
    explicitly drops non-standard fields such as ``reasoning_content`` that
    reasoning models exposed through OpenAI-compatible endpoints (DeepSeek-R1,
    Qwen/QwQ, Kimi, vLLM, OpenRouter, etc.) stream in the delta.

    We re-attach that field to the message chunk's ``additional_kwargs`` so the
    thinking stream can be surfaced to the UI via ``extract_stream_reasoning``.
    """

    def _convert_chunk_to_generation_chunk(self, chunk, default_chunk_class, base_generation_info):
        gen_chunk = super()._convert_chunk_to_generation_chunk(
            chunk, default_chunk_class, base_generation_info
        )
        if gen_chunk is None:
            return gen_chunk
        try:
            choices = (
                chunk.get("choices", [])
                or chunk.get("chunk", {}).get("choices", [])
            )
            if not choices:
                return gen_chunk
            delta = choices[0].get("delta") or {}
            reasoning = delta.get("reasoning_content") or delta.get("reasoning")
            if reasoning:
                msg = gen_chunk.message
                if getattr(msg, "additional_kwargs", None) is None:
                    msg.additional_kwargs = {}
                # Store only this chunk's delta; downstream consumers stream
                # per-chunk and concatenate on their side.
                msg.additional_kwargs["reasoning_content"] = str(reasoning)
        except Exception:
            # Reasoning extraction is best-effort; never break the content stream.
            pass
        return gen_chunk


class ProviderDependencyError(RuntimeError):
    """Raised when a provider's adapter package is not installed.

    Kept distinct from generic RuntimeError so callers (e.g. /provider/test)
    can surface a clear "install this package" message instead of a raw stack.
    """


def _env_timeout(name: str, default: float) -> Optional[float]:
    """Read a timeout (seconds) from env; return None to disable, default if unset.

    ``0`` / ``none`` / ``off`` disable the timeout (langchain treats None/0 as
    "no stream-chunk timeout"), matching the semantics in the langchain_openai
    stream_chunk_timeout docs. An unparseable value falls back to ``default``.
    """
    raw = os.getenv(name)
    if raw is None:
        return default
    raw = raw.strip().lower()
    if raw in ("", "0", "none", "off", "null", "false", "disabled"):
        return None
    try:
        return float(raw)
    except ValueError:
        return default


def _create_chat_openai(
    *,
    model: str,
    api_key: str,
    base_url: str,
    temperature: float,
    max_tokens: Optional[int] = None,
    streaming: bool = False,
    extra_kwargs: Optional[Dict[str, object]] = None,
    provider: Optional[str] = None,
    request_timeout: Optional[float] = None,
    max_retries: Optional[int] = None,
):
    """Create a LangChain chat model for the given provider.

    This is the SINGLE creation point (adapter dispatch) for all cloud LLMs.
    ``provider`` selects the adapter via the provider registry's api_type:

      - openai_compat (default): ReasoningChatOpenAI — covers OpenAI, DeepSeek,
        Qwen, Zhipu, Moonshot, Doubao, Baidu, StepFun, xAI, custom endpoints.
      - anthropic: langchain_anthropic.ChatAnthropic
      - gemini:    langchain_google_genai.ChatGoogleGenerativeAI

    When ``provider`` is None/unknown we default to the OpenAI-compatible path,
    which preserves the previous behavior exactly.
    """
    from core.providers import get_api_type

    api_type = get_api_type(provider) if provider else "openai_compat"

    if api_type == "anthropic":
        return _create_anthropic(
            model=model, api_key=api_key, base_url=base_url,
            temperature=temperature, max_tokens=max_tokens, streaming=streaming,
        )
    if api_type == "gemini":
        return _create_gemini(
            model=model, api_key=api_key,
            temperature=temperature, max_tokens=max_tokens, streaming=streaming,
        )

    # Default: OpenAI-compatible.
    kwargs = {
        "model": model,
        "api_key": api_key,
        "base_url": base_url,
        "temperature": temperature,
        "streaming": streaming,
        # Reasoning models (deepseek-v4-pro etc.) can think well past langchain's
        # 120s default before emitting the first streaming chunk, which surfaces
        # as "No streaming chunk received for 120.0s". Raise the ceiling (and
        # allow a full disable via env) so long LAD analyses don't get killed.
        "stream_chunk_timeout": _env_timeout("TIA_STREAM_CHUNK_TIMEOUT_S", 900.0),
        "request_timeout": request_timeout if request_timeout is not None else _env_timeout("TIA_REQUEST_TIMEOUT_S", 1200.0),
        "max_retries": max_retries if max_retries is not None else int(os.getenv("TIA_LLM_MAX_RETRIES", "2")),
    }
    if max_tokens is not None:
        kwargs["max_tokens"] = max_tokens
    if extra_kwargs:
        kwargs["model_kwargs"] = extra_kwargs
    return ReasoningChatOpenAI(**kwargs)


def _create_anthropic(*, model, api_key, base_url, temperature, max_tokens, streaming):
    try:
        from langchain_anthropic import ChatAnthropic
    except ImportError as e:
        raise ProviderDependencyError(
            "Anthropic (Claude) support requires the 'langchain-anthropic' package, "
            "which is not installed in the backend."
        ) from e
    kwargs = {
        "model": model,
        "api_key": api_key,
        "temperature": temperature,
        "streaming": streaming,
    }
    # Anthropic requires max_tokens; pick a sane default when caller omits it.
    kwargs["max_tokens"] = max_tokens if max_tokens is not None else 4096
    # Allow a custom base_url (proxy / gateway) when provided.
    if base_url:
        kwargs["base_url"] = base_url
    return ChatAnthropic(**kwargs)


def _create_gemini(*, model, api_key, temperature, max_tokens, streaming):
    try:
        from langchain_google_genai import ChatGoogleGenerativeAI
    except ImportError as e:
        raise ProviderDependencyError(
            "Google Gemini support requires the 'langchain-google-genai' package, "
            "which is not installed in the backend."
        ) from e
    kwargs = {
        "model": model,
        "google_api_key": api_key,
        "temperature": temperature,
    }
    if max_tokens is not None:
        kwargs["max_output_tokens"] = max_tokens
    # ChatGoogleGenerativeAI streams via astream regardless; no explicit flag needed.
    return ChatGoogleGenerativeAI(**kwargs)


async def generate_code(prompt: str, context: str, model_config: Dict[str, str], history: Optional[List[Dict[str, str]]] = None, intent: str = "", images: Optional[List[str]] = None, project_key: str = "", evolution_enabled: bool = False):
    cfg = _resolve_model_config(model_config)
    configured_model = cfg["configured_model"]
    base_url = cfg["base_url"]
    api_key = cfg["api_key"]
    detected_provider = cfg["detected_provider"]
    _log = logging.getLogger("backend")
    if not api_key:
        return {"error": "Missing API key", "scl_code": "// No API key configured"}

    # Use cache-optimized parameters from model profile
    cache_params = get_cache_optimized_params(detected_provider, "code_gen")
    extra_kwargs = {
        k: v for k, v in cache_params.items()
        if k not in ("temperature", "max_tokens", "_default_model", "_base_url", "_provider")
    }
    if cfg.get("reasoning_effort"):
        extra_kwargs["reasoning_effort"] = cfg["reasoning_effort"]
    _log = logging.getLogger("backend")
    _log.info(
        f"[AGENT] generate_code detected_provider={detected_provider} base_url={base_url} "
        f"model={configured_model} api_key_set={bool(api_key)}"
    )

    # ── Cloud LLM path: multi-agent supervisor (function calling). ──
    # This Python backend only ever serves cloud models — Ollama runs entirely
    # inside the C# host and never reaches here.
    llm = _create_chat_openai(
        model=configured_model,
        api_key=api_key,
        base_url=base_url,
        temperature=cache_params.get("temperature", 0.1),
        max_tokens=cache_params.get("max_tokens", 4096),
        extra_kwargs=extra_kwargs,
        provider=detected_provider,
    )
    _log.info(f"[AGENT] Cloud LLM multi-agent supervisor (intent={intent})")
    try:
        from agent.supervisor import run_multi_agent_stream
        accumulated: list[str] = []
        async for event, delta in run_multi_agent_stream(
            llm, prompt, context, intent, history, "", images,
            context_window=cfg.get("context_window"),
            project_key=project_key, evolution_enabled=evolution_enabled,
            model_cfg=cfg,
        ):
            if event == "content":
                accumulated.append(str(delta))
            elif event == "error":
                return {"error": str(delta), "scl_code": "// Multi-agent error: " + str(delta)}
        full_output = "".join(accumulated)
        if full_output:
            parsed = _try_parse_structured(full_output)
            if parsed is not None:
                assembled = _assemble_scl(parsed)
                return {"scl_code": assembled, "thinking": "multi_agent_json_parsed"}
            return {"scl_code": full_output, "thinking": "multi_agent_json_fallback"}
        return {"error": "Multi-agent returned empty response", "scl_code": "// Multi-agent returned empty content"}
    except Exception as e:
        _log.error(f"[AGENT] Multi-agent failed: {e}")
        return {"error": str(e), "scl_code": f"// Multi-agent error: {str(e)}"}


async def generate_code_stream(prompt: str, context: str, model_config: Dict[str, str], history: Optional[List[Dict[str, str]]] = None, intent: str = "", planning_knowledge: str = "", images: Optional[List[str]] = None, project_key: str = "", evolution_enabled: bool = False) -> AsyncIterator[Tuple[str, str]]:
    cfg = _resolve_model_config(model_config)
    configured_model = cfg["configured_model"]
    base_url = cfg["base_url"]
    api_key = cfg["api_key"]
    detected_provider = cfg["detected_provider"]
    _log = logging.getLogger("backend")
    if not api_key:
        raise RuntimeError("Missing API key for OpenAI-compatible provider")

    # Cache-optimized streaming parameters
    stream_params = get_cache_optimized_params(detected_provider, "code_gen")
    stream_extra_kwargs = {
        k: v for k, v in stream_params.items()
        if k not in ("temperature", "max_tokens", "_default_model", "_base_url", "_provider")
    }
    if cfg.get("reasoning_effort"):
        stream_extra_kwargs["reasoning_effort"] = cfg["reasoning_effort"]

    # ── Multi-Agent path for cloud LLMs (streaming) ──
    # This Python backend only ever serves cloud models — Ollama runs entirely
    # inside the C# host and never reaches here.
    llm = _create_chat_openai(
        model=configured_model,
        api_key=api_key,
        base_url=base_url,
        temperature=stream_params.get("temperature", 0.1),
        max_tokens=stream_params.get("max_tokens", 4096),
        streaming=True,
        extra_kwargs=stream_extra_kwargs,
        provider=detected_provider,
    )
    _log.info(f"[AGENT-STREAM] Cloud LLM multi-agent supervisor (intent={intent})")
    try:
        from agent.supervisor import run_multi_agent_stream
        async for event, delta in run_multi_agent_stream(
            llm, prompt, context, intent, history, planning_knowledge, images,
            context_window=cfg.get("context_window"),
            project_key=project_key, evolution_enabled=evolution_enabled,
            model_cfg=cfg,
        ):
            yield event, delta
    except Exception as e:
        _log.error(f"[AGENT-STREAM] Multi-agent failed: {e}")
        yield "error", f"Cloud multi-agent failed: {e}"
    return


async def convert_xml_to_code(
    xml_text: str,
    instruction: str,
    model_config: Dict[str, str],
    agent_ir: Optional[str] = None,
    agent_ir_summary: str = "",
):
    cfg = _resolve_model_config(model_config)  # rejects local/Ollama upstream
    configured_model = cfg["configured_model"]
    base_url = cfg["base_url"]
    api_key = cfg["api_key"]
    if not api_key:
        return {"error": "Missing API key", "scl_code": "// No API key configured"}

    # The canonical IR is produced by the C# snapshot projection and supplied by the caller.
    # Fall back to bounded raw XML only when the caller could not project (no local re-parsing).
    ir_text = (agent_ir or "").strip()
    ir_summary = (agent_ir_summary or "").strip()
    block_name = "AI_Generated_Block"
    block_type = "FB"
    if ir_text:
        try:
            parsed = json.loads(ir_text)
            block_name = parsed.get("blockName") or parsed.get("name") or block_name
            block_type = parsed.get("blockType") or parsed.get("kind") or block_type
        except Exception:
            pass
    else:
        ir_text = (xml_text or "")[:200000]
        ir_summary = "Canonical IR unavailable; bounded raw Openness XML is provided instead."

    system_prompt = (
        "You are an expert Siemens TIA Portal engineer. "
        "Convert TIA LAD/FBD JSON IR to valid Siemens SCL. "
        "Output only SCL code without markdown fences."
    )
    user_content = (
        f"Target block name: {block_name}\n"
        f"Target block type: {block_type}\n"
        f"Additional instruction: {instruction or 'none'}\n\n"
        f"Canonical JSON IR:\n{ir_text}\n\n"
        f"IR summary:\n{ir_summary or 'none'}\n\n"
        "Requirements:\n"
        "1) Generate syntactically valid SCL.\n"
        "2) Preserve interface variables and identifiers from JSON IR.\n"
        "3) Reconstruct logic from network/parts/wires/accesses as best as possible.\n"
        "4) Preserve compileUnitId, uid, and pin relationships when interpreting the ladder graph.\n"
        "5) If some logic is ambiguous, keep deterministic safe logic and annotate minimally in code.\n"
    )
    messages = [SystemMessage(content=system_prompt), HumanMessage(content=user_content)]

    retries = 3
    delay = 1.0
    for attempt in range(retries):
        try:
            llm = _create_chat_openai(
                model=configured_model, api_key=api_key, base_url=base_url, temperature=0.0,
            )
            response = await llm.ainvoke(messages)
            content = response.content if hasattr(response, "content") else str(response)
            return {"scl_code": str(content), "thinking": "xml-ast-llm"}
        except Exception as e:
            msg = str(e).lower()
            if ("429" in msg or "rate limit" in msg or "limit" in msg) and attempt < retries - 1:
                await asyncio.sleep(delay)
                delay *= 2
                continue
            return {"error": str(e), "scl_code": f"// Error converting xml to scl: {str(e)}"}


# ---------------------------------------------------------------------------
# /chat_plan entry — cloud multi-agent supervisor (cloud models only)
# Same supervisor as /generate_stream; differs only in intent defaulting and in
# not carrying project_key / evolution_enabled. The old single-agent
# Plan-and-Execute graph (agent/planning.py) has been removed.
# Existing /generate and /generate_stream are NOT affected.
# ---------------------------------------------------------------------------

def _build_supervisor_llm(model_config: Dict[str, str]):
    """Build the ChatOpenAI shared by cloud supervisor / Planner; returns (None, cfg) when there is no api_key."""
    cfg = _resolve_model_config(model_config)
    if not cfg["api_key"]:
        return None, cfg
    cache_params = get_cache_optimized_params(cfg["detected_provider"], "code_gen")
    extra_kwargs = {
        k: v for k, v in cache_params.items()
        if k not in ("temperature", "max_tokens", "_default_model", "_base_url", "_provider")
    }
    # Honor the caller's reasoning effort (e.g. "medium" from ai_config). Without this
    # the provider default (often "high") makes long planner/supervisor calls exceed the
    # ~120s gateway timeout and fail with HTTP 504.
    if cfg.get("reasoning_effort"):
        extra_kwargs["reasoning_effort"] = cfg["reasoning_effort"]
    llm = _create_chat_openai(
        model=cfg["configured_model"],
        api_key=cfg["api_key"],
        base_url=cfg["base_url"],
        temperature=cache_params.get("temperature", 0.1),
        max_tokens=cache_params.get("max_tokens", 4096),
        streaming=True,
        extra_kwargs=extra_kwargs,
    )
    return llm, cfg


async def run_planning_agent_stream(
    prompt: str,
    context: str,
    model_config: Dict[str, str],
    history: Optional[List[Dict[str, str]]] = None,
    planning_knowledge: str = "",
    intent: str = "",
    images: Optional[List[str]] = None,
    project_key: str = "",
) -> AsyncIterator[Tuple[str, str]]:
    """Run the cloud multi-agent supervisor for the /chat_plan endpoint and yield SSE events.

    Cloud models only. Replaces the old chain:
      C# IntentUnderstander → AgentPlanner → UnifiedAgentEngine → Python ReAct

    Cloud LLMs run the multi-agent supervisor loop routed by the caller's real
    ``intent`` (GenerateCode / ModifyCode / ExplainCode / Compile / Chat / ...),
    NOT a hardcoded intent — so the supervisor can drive the full
    coder→reviewer→compiler cycle rather than always falling back to plain chat.

    This Python backend only ever serves cloud models — Ollama runs entirely
    inside the C# host and never reaches here.

    Events yielded:
      ("thinking", message)  — status, plan display, tool progress
      ("content", delta)     — streaming LLM output
      ("tool_call", json)    — tool call start/complete
      ("error", message)     — error
      ("done", "ok")         — completion
    """
    llm, cfg = _build_supervisor_llm(model_config)
    if llm is None:
        yield "error", "Missing API key"
        yield "done", "ok"
        return

    # ── Multi-Agent path for cloud LLMs ──
    # Route by the caller's real intent so the supervisor runs the full agent
    # loop (coder→reviewer→compiler) instead of being forced into chat-only.
    # Default to GenerateCode when the caller didn't classify an intent, since
    # /chat_plan is the planning/execution entry point, not a pure Q&A channel.
    effective_intent = (intent or "").strip() or "GenerateCode"
    from agent.supervisor import run_multi_agent_stream
    async for event, delta in run_multi_agent_stream(
        llm, prompt, context, effective_intent, history, planning_knowledge, images,
        context_window=cfg.get("context_window"),
        project_key=project_key,
    ):
        yield event, delta


async def start_project_plan(
    prompt: str,
    context: str,
    model_config: Dict[str, str],
    history: Optional[List[Dict[str, str]]] = None,
    planning_knowledge: str = "",
    intent: str = "",
    images: Optional[List[str]] = None,
    project_key: str = "",
    verify_compile_at_end: bool = False,
) -> Optional[Dict[str, Any]]:
    """Project-level long task: produce the plan and start a background job, returning {plan_id, status, summary, total}.

    On a project intent: resume from checkpoint (including adjust replanning) or plan afresh → persist → start the background job,
    immediately returning the plan summary (execution continues in the background without tying up the caller's connection).
    Non-project / planning failure returns None; the caller falls back to the single-block flow.
    """
    effective_intent = (intent or "").strip() or "GenerateCode"
    if effective_intent not in ("GenerateCode", "ModifyCode"):
        return None

    llm, cfg = _build_supervisor_llm(model_config)
    if llm is None:
        return None

    from agent.supervisor import prepare_project_plan
    plan = await prepare_project_plan(
        llm, prompt, context or "", project_key or "", planning_knowledge or ""
    )
    if plan is None or len(plan.tasks) < 2:
        return None

    from agent.plan_job import plan_jobs
    from agent.plan_store import PlanStore

    PlanStore().save(plan)
    plan_jobs.start(plan, llm, context_window=int(cfg.get("context_window") or 0),
                    verify_compile_at_end=verify_compile_at_end)
    return {
        "plan_id": plan.id,
        "status": plan.status,
        "summary": plan.summary,
        "total": len(plan.tasks),
    }


# ---------------------------------------------------------------------------
# Vision capability probe
# ---------------------------------------------------------------------------
# There is no standard "capabilities" endpoint in the OpenAI-compatible API, so
# we determine vision support empirically: send one tiny multimodal request and
# classify the outcome. The C# host keeps a local JSON allow-list of known
# vision models and only calls this probe for models NOT on that list, caching
# the result so each unknown model is probed at most once.
#
# Cost is intentionally minimal: a 1x1 PNG + a 1-token completion cap.

# A valid 1x1 fully-transparent PNG (standard, widely-embedded test image).
_PROBE_PNG_1X1 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk"
    "+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
)

# Error-message fragments that indicate the model/endpoint rejected the IMAGE
# specifically (i.e. it is a text-only model), as opposed to auth/network faults.
_VISION_UNSUPPORTED_MARKERS = (
    "image", "vision", "multimodal", "multi-modal", "image_url",
    "not support", "unsupported", "cannot process image",
    "does not support image", "no image", "modality",
)

# Fragments that indicate an auth/quota/network fault — NOT a vision verdict.
_PROBE_INCONCLUSIVE_MARKERS = (
    "401", "403", "unauthorized", "invalid api key", "incorrect api key",
    "quota", "insufficient", "rate limit", "429", "timeout", "timed out",
    "connection", "network", "getaddrinfo", "ssl",
)


async def probe_vision_support(model_config: Dict[str, str]) -> Dict[str, str]:
    """Empirically probe whether a model accepts image input.

    Returns a dict: ``{"result": "supported"|"unsupported"|"unknown", "detail": str}``.

    - ``supported``: the tiny multimodal request completed without error.
    - ``unsupported``: the request failed with an image/modality-related error.
    - ``unknown``: the request failed for an auth/quota/network reason, so we
      cannot tell whether vision is supported. The caller should fall back to
      its allow-list rather than mark the model either way.
    """
    _log = logging.getLogger("backend")
    cfg = _resolve_model_config(model_config)
    configured_model = cfg["configured_model"]
    base_url = cfg["base_url"]
    api_key = cfg["api_key"]
    if not api_key:
        return {"result": "unknown", "detail": "missing api key"}

    llm = _create_chat_openai(
        model=configured_model,
        api_key=api_key,
        base_url=base_url,
        temperature=0.0,
        max_tokens=1,
    )
    content = build_user_content("What is in this image? Reply with 'ok'.", [_PROBE_PNG_1X1])
    try:
        await llm.ainvoke([HumanMessage(content=content)])
        _log.info(f"[PROBE] vision supported model={configured_model}")
        return {"result": "supported", "detail": ""}
    except Exception as e:
        msg = str(e).lower()
        # Auth/quota/network faults are inconclusive — don't guess a verdict.
        if any(m in msg for m in _PROBE_INCONCLUSIVE_MARKERS):
            _log.info(f"[PROBE] inconclusive model={configured_model} err={e}")
            return {"result": "unknown", "detail": str(e)[:300]}
        # Image/modality-specific rejection => text-only model.
        if any(m in msg for m in _VISION_UNSUPPORTED_MARKERS):
            _log.info(f"[PROBE] vision unsupported model={configured_model} err={e}")
            return {"result": "unsupported", "detail": str(e)[:300]}
        # Any other error is inconclusive.
        _log.info(f"[PROBE] indeterminate error model={configured_model} err={e}")
        return {"result": "unknown", "detail": str(e)[:300]}


# Error-message fragments that indicate the requested MODEL does not exist for
# this key/endpoint (as opposed to an auth or network fault).
_MODEL_NOT_FOUND_MARKERS = (
    "model not found", "does not exist", "no such model", "unknown model",
    "invalid model", "model_not_found", "not available", "unsupported model",
)

# Auth-specific fragments (a subset of the inconclusive markers, but here they
# ARE a definite verdict: the key/endpoint rejected authentication).
_AUTH_FAULT_MARKERS = (
    "401", "403", "unauthorized", "invalid api key", "incorrect api key",
    "authentication", "permission", "forbidden",
)

# Network/connectivity fragments.
_NETWORK_FAULT_MARKERS = (
    "timeout", "timed out", "connection", "network", "getaddrinfo",
    "ssl", "name resolution", "unreachable", "refused",
)


async def test_provider_connection(model_config: Dict[str, str]) -> Dict[str, object]:
    """Probe whether a provider/model/key/base_url combination actually works.

    Sends one tiny real chat request through the SAME LLM factory used by the
    generate/stream paths, so the test exercises the exact adapter that would
    serve real traffic — no separate, drift-prone test path.

    Returns a dict shaped for ``TestProviderResponse``:
        {ok, category, latency_ms, model, reply, detail}

    ``category`` is one of: "ok" | "auth" | "network" | "model_not_found" |
    "unknown". The api_key is NEVER included in ``detail``.
    """
    import time

    _log = logging.getLogger("backend")
    # _resolve_model_config rejects local/Ollama upstream (fail closed).
    cfg = _resolve_model_config(model_config)
    configured_model = cfg["configured_model"]
    base_url = cfg["base_url"]
    api_key = cfg["api_key"]

    if not api_key:
        return {
            "ok": False, "category": "auth", "latency_ms": None,
            "model": configured_model, "reply": None,
            "detail": "missing api key",
        }

    detected_provider = cfg.get("detected_provider")
    # The probe must NOT inherit the generation budget (600s + retries): one tiny
    # request, no retries, hard cap so the UI gets a verdict within seconds.
    try:
        test_timeout_s = float(os.getenv("TIA_PROVIDER_TEST_TIMEOUT_S", "25"))
    except ValueError:
        test_timeout_s = 25.0
    if test_timeout_s <= 0:
        test_timeout_s = 25.0

    try:
        llm = _create_chat_openai(
            model=configured_model,
            api_key=api_key,
            base_url=base_url,
            temperature=0.0,
            max_tokens=8,
            provider=detected_provider,
            request_timeout=test_timeout_s,
            max_retries=0,
        )
    except ProviderDependencyError as e:
        return {
            "ok": False, "category": "unknown", "latency_ms": None,
            "model": configured_model, "reply": None, "detail": str(e),
        }
    start = time.perf_counter()
    try:
        resp = await asyncio.wait_for(
            llm.ainvoke([HumanMessage(content="Reply with exactly 'OK'.")]),
            timeout=test_timeout_s + 5,
        )
        latency_ms = int((time.perf_counter() - start) * 1000)
        reply = resp.content if hasattr(resp, "content") else str(resp)
        reply_str = str(reply).strip()[:100]
        _log.info(f"[TEST] provider ok model={configured_model} latency={latency_ms}ms")
        return {
            "ok": True, "category": "ok", "latency_ms": latency_ms,
            "model": configured_model, "reply": reply_str, "detail": None,
        }
    except asyncio.TimeoutError:
        latency_ms = int((time.perf_counter() - start) * 1000)
        _log.info(f"[TEST] provider timeout model={configured_model} after={latency_ms}ms")
        return {
            "ok": False, "category": "network", "latency_ms": latency_ms,
            "model": configured_model, "reply": None,
            "detail": f"provider test timed out after {test_timeout_s:.0f}s (endpoint not responding)",
        }
    except Exception as e:
        latency_ms = int((time.perf_counter() - start) * 1000)
        msg = str(e).lower()
        detail = str(e)[:300]
        if any(m in msg for m in _AUTH_FAULT_MARKERS):
            category = "auth"
        elif any(m in msg for m in _MODEL_NOT_FOUND_MARKERS):
            category = "model_not_found"
        elif any(m in msg for m in _NETWORK_FAULT_MARKERS):
            category = "network"
        else:
            category = "unknown"
        _log.info(f"[TEST] provider failed model={configured_model} category={category} err={e}")
        return {
            "ok": False, "category": category, "latency_ms": latency_ms,
            "model": configured_model, "reply": None, "detail": detail,
        }
