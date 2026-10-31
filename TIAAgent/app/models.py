"""Pydantic request/response models for the TIA AI Code Generator API."""

from typing import Optional, List
from pydantic import BaseModel


class GenerateRequest(BaseModel):
    api_key: Optional[str] = ""
    model: Optional[str] = ""
    provider: Optional[str] = "auto"
    base_url: Optional[str] = ""
    rid: Optional[str] = ""
    prompt: str
    context: Optional[str] = ""
    stream: Optional[bool] = False
    history: Optional[List[dict]] = None
    intent: Optional[str] = ""
    # Base64-encoded images for vision-capable models
    images: Optional[List[str]] = None
    # Optional historical planning guidance (proven tool paths / past plans).
    # Kept separate from `context` so the planner can distinguish project *fact*
    # from prior *experience* and never treat guidance as ground truth.
    planning_knowledge: Optional[str] = ""
    # Optional real model context window (max input tokens), supplied by the C#
    # host. When present, token budgeting uses it instead of a generic default,
    # so we don't over-fill models with smaller windows. Falls back to a
    # conservative default when omitted.
    context_window: Optional[int] = None
    # Evolution (self-improving repair experience) —— project identifier,
    # supplied by the C# host. project_key is the TIA project file absolute path.
    project_key: Optional[str] = ""
    # Opt-in gate (D9). When false (default) the backend records nothing and the
    # evolution system is fully silent.
    evolution_enabled: Optional[bool] = False
    # Optional reasoning effort for reasoning-capable models ("low" / "medium" /
    # "high"). Forwarded as-is to the OpenAI-compatible endpoint; ignored by
    # providers that do not support it. None = provider default.
    reasoning_effort: Optional[str] = None
    # Opt-in whole-PLC verify at the end of a plan (snapshot + diff). Off by default: a full
    # compile is heavy and competes with manual editing; per-block checks after writes still run.
    verify_compile_at_end: Optional[bool] = False


class GenerateResponse(BaseModel):
    code: str
    thinking: Optional[str] = None
    error: Optional[str] = None


class ConvertXmlRequest(BaseModel):
    api_key: Optional[str] = ""
    model: Optional[str] = ""
    provider: Optional[str] = "auto"
    base_url: Optional[str] = ""
    rid: Optional[str] = ""
    xml: str
    instruction: Optional[str] = ""
    mode: Optional[str] = "analysis"
    # Canonical IR produced by the C# snapshot projection. The caller (C# AICodeGenerator)
    # supplies it so the backend never re-parses TIA XML into a second, lossy IR.
    agent_ir: Optional[str] = None
    agent_ir_summary: Optional[str] = None


class ConvertXmlResponse(BaseModel):
    scl_code: str
    ast_summary: Optional[str] = None
    agent_ir: Optional[str] = None
    analysis: Optional[str] = None
    error: Optional[str] = None


class ResolveXmlFragmentRequest(BaseModel):
    xml: str
    compile_unit_id: Optional[str] = ""
    uid: Optional[str] = ""


class ResolveXmlFragmentResponse(BaseModel):
    fragment_xml: str
    compile_unit_id: Optional[str] = None
    uid: Optional[str] = None
    error: Optional[str] = None


class ToolResultInput(BaseModel):
    serverId: Optional[str] = "builtin"
    name: str
    ok: bool
    output: Optional[str] = ""
    error: Optional[str] = ""


class ProbeVisionRequest(BaseModel):
    api_key: Optional[str] = ""
    model: Optional[str] = ""
    provider: Optional[str] = "auto"
    base_url: Optional[str] = ""
    rid: Optional[str] = ""


class ProbeVisionResponse(BaseModel):
    # "supported" | "unsupported" | "unknown"
    # unknown = the probe couldn't decide (network/auth error); the C# caller
    # should fall back to its local allow-list or leave the model unmarked.
    result: str
    model: Optional[str] = ""
    detail: Optional[str] = None


class TestProviderRequest(BaseModel):
    api_key: Optional[str] = ""
    model: Optional[str] = ""
    provider: Optional[str] = "auto"
    base_url: Optional[str] = ""
    rid: Optional[str] = ""


class TestProviderResponse(BaseModel):
    ok: bool
    # "ok" | "auth" | "network" | "model_not_found" | "unknown"
    category: str = "unknown"
    latency_ms: Optional[int] = None
    model: Optional[str] = ""
    reply: Optional[str] = None   # short snippet of the model's reply on success
    detail: Optional[str] = None  # failure reason (never contains the api key)


class ValidateSclRequest(BaseModel):
    code: str


class ValidateSclResponse(BaseModel):
    is_valid: bool
    errors: List[str]
    warnings: List[str]
    block_type: str
    block_name: str
