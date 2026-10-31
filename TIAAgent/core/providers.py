"""
Provider registry — the SINGLE SOURCE OF TRUTH for cloud model platforms.

This module owns all platform knowledge that used to be hard-coded on the C#
side (AIConfig.GetAvailableModels / GetDefaultEndpointForProvider):

  - which platforms exist
  - each platform's default base_url and whether the user may override it
  - each platform's preset model catalog (+ vision flag)
  - which adapter (api_type) serves the platform:
        "openai_compat" → served by the existing ChatOpenAI factory
        "anthropic"     → served by langchain_anthropic.ChatAnthropic
        "gemini"        → served by langchain_google_genai.ChatGoogleGenerativeAI
  - whether the platform's adapter dependency is actually importable right now
    (``available``), so the C# UI can disable platforms the backend cannot serve.

The C# host fetches this via GET /providers and drives its dropdowns from it,
instead of maintaining a parallel hard-coded table. Adding a platform = editing
this one file (+ installing its adapter package for non-OpenAI-compatible ones).
"""

from __future__ import annotations

import importlib.util
from typing import Dict, List, Optional, TypedDict


class ModelEntry(TypedDict, total=False):
    id: str
    name: str
    description: str
    vision: bool


class ProviderEntry(TypedDict, total=False):
    id: str                 # canonical provider key, e.g. "anthropic"
    name: str               # display name, e.g. "Anthropic Claude"
    api_type: str           # "openai_compat" | "anthropic" | "gemini"
    base_url: str           # default endpoint (OpenAI-compatible base or "")
    base_url_editable: bool # whether the user may override base_url
    requires_base_url: bool # whether a base_url is mandatory (e.g. "custom")
    models: List[ModelEntry]


# api_type → the Python module that must be importable to serve it.
# OpenAI-compatible platforms all use langchain_openai, which is always present.
_API_TYPE_DEPENDENCY: Dict[str, Optional[str]] = {
    "openai_compat": "langchain_openai",
    "anthropic": "langchain_anthropic",
    "gemini": "langchain_google_genai",
}


def _dep_available(module_name: Optional[str]) -> bool:
    if not module_name:
        return True
    try:
        return importlib.util.find_spec(module_name) is not None
    except (ImportError, ValueError, ModuleNotFoundError):
        return False


# ---------------------------------------------------------------------------
# The registry. Model catalogs mirror the former C# AIConfig.GetAvailableModels.
# ---------------------------------------------------------------------------

_PROVIDERS: List[ProviderEntry] = [
    {
        "id": "openai", "name": "OpenAI", "api_type": "openai_compat",
        "base_url": "https://api.openai.com/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "gpt-6-astra", "name": "GPT-6 Astra", "description": "旗舰多模态，1.05M上下文", "vision": True},
            {"id": "gpt-6-sol",   "name": "GPT-6 Sol",   "description": "中端多模态，872K上下文",  "vision": True},
            {"id": "gpt-6-luna",  "name": "GPT-6 Luna",  "description": "轻量多模态，1.05M上下文，最低价", "vision": True},
            {"id": "gpt-4o",      "name": "GPT-4o",      "description": "上代旗舰多模态", "vision": True},
            {"id": "gpt-4o-mini", "name": "GPT-4o-mini", "description": "上代小型快速多模态", "vision": True},
            {"id": "gpt-4.1",     "name": "GPT-4.1",     "description": "长上下文多模态", "vision": True},
            {"id": "o3",          "name": "o3",           "description": "推理模型（多模态）", "vision": True},
            {"id": "o4-mini",     "name": "o4-mini",      "description": "轻量推理（多模态）", "vision": True},
        ],
    },
    {
        "id": "anthropic", "name": "Anthropic Claude", "api_type": "anthropic",
        "base_url": "https://api.anthropic.com", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "claude-sonnet-4-20250514", "name": "Claude Sonnet 4", "description": "旗舰多模态", "vision": True},
            {"id": "claude-opus-4-20250514", "name": "Claude Opus 4", "description": "最强多模态", "vision": True},
            {"id": "claude-3-7-sonnet-latest", "name": "Claude 3.7 Sonnet", "description": "多模态", "vision": True},
            {"id": "claude-3-5-sonnet-latest", "name": "Claude 3.5 Sonnet", "description": "多模态", "vision": True},
            {"id": "claude-3-5-haiku-latest", "name": "Claude 3.5 Haiku", "description": "快速多模态", "vision": True},
        ],
    },
    {
        "id": "gemini", "name": "Google Gemini", "api_type": "gemini",
        "base_url": "", "base_url_editable": False, "requires_base_url": False,
        "models": [
            {"id": "gemini-2.5-pro", "name": "Gemini 2.5 Pro", "description": "旗舰多模态", "vision": True},
            {"id": "gemini-2.5-flash", "name": "Gemini 2.5 Flash", "description": "快速多模态", "vision": True},
            {"id": "gemini-2.0-flash", "name": "Gemini 2.0 Flash", "description": "多模态", "vision": True},
        ],
    },
    {
        "id": "xai", "name": "xAI Grok", "api_type": "openai_compat",
        "base_url": "https://api.x.ai/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "grok-4", "name": "Grok 4", "description": "旗舰多模态", "vision": True},
            {"id": "grok-2-vision-1212", "name": "Grok 2 Vision", "description": "多模态", "vision": True},
            {"id": "grok-3", "name": "Grok 3", "description": "文本推理"},
        ],
    },
    {
        "id": "deepseek", "name": "DeepSeek", "api_type": "openai_compat",
        "base_url": "https://api.deepseek.com/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "deepseek-flash",    "name": "DeepSeek V4.1 Flash", "description": "V4旗舰，1M上下文，推荐（纯文本）"},
            {"id": "deepseek-v4-pro",   "name": "DeepSeek V4 Pro",     "description": "V4最强，1.6T参数，1M上下文（纯文本）"},
            {"id": "deepseek-chat",     "name": "DeepSeek-V3 (旧)",     "description": "V3对话，支持上下文缓存（纯文本）"},
            {"id": "deepseek-reasoner", "name": "DeepSeek-R1 (旧)",     "description": "V3推理模型（纯文本）"},
        ],
    },
    {
        "id": "qwen", "name": "阿里通义千问", "api_type": "openai_compat",
        "base_url": "https://dashscope.aliyuncs.com/compatible-mode/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "qwen-vl-max", "name": "Qwen-VL-Max", "description": "旗舰多模态", "vision": True},
            {"id": "qwen-vl-plus", "name": "Qwen-VL-Plus", "description": "多模态", "vision": True},
            {"id": "qwen2.5-vl-72b-instruct", "name": "Qwen2.5-VL-72B", "description": "开源多模态", "vision": True},
            {"id": "qwen-max", "name": "Qwen-Max", "description": "旗舰对话（纯文本）"},
            {"id": "qwen-plus", "name": "Qwen-Plus", "description": "均衡对话（纯文本）"},
        ],
    },
    {
        "id": "zhipu", "name": "智谱 GLM", "api_type": "openai_compat",
        "base_url": "https://open.bigmodel.cn/api/paas/v4", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "glm-4v-plus", "name": "GLM-4V-Plus", "description": "旗舰多模态", "vision": True},
            {"id": "glm-4v", "name": "GLM-4V", "description": "多模态", "vision": True},
            {"id": "glm-4.5", "name": "GLM-4.5", "description": "旗舰对话（纯文本）"},
            {"id": "glm-4-plus", "name": "GLM-4-Plus", "description": "对话（纯文本）"},
        ],
    },
    {
        "id": "moonshot", "name": "月之暗面 Kimi", "api_type": "openai_compat",
        "base_url": "https://api.moonshot.cn/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "moonshot-v1-128k-vision-preview", "name": "Kimi Vision 128K", "description": "长上下文多模态", "vision": True},
            {"id": "kimi-latest", "name": "Kimi Latest", "description": "多模态", "vision": True},
            {"id": "moonshot-v1-128k", "name": "Moonshot 128K", "description": "长上下文（纯文本）"},
        ],
    },
    {
        "id": "doubao", "name": "字节豆包", "api_type": "openai_compat",
        "base_url": "https://ark.cn-beijing.volces.com/api/v3", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "doubao-1.5-vision-pro", "name": "豆包 1.5 Vision Pro", "description": "旗舰多模态", "vision": True},
            {"id": "doubao-1.5-pro-32k", "name": "豆包 1.5 Pro 32K", "description": "对话（纯文本）"},
        ],
    },
    {
        "id": "hunyuan", "name": "腾讯混元", "api_type": "openai_compat",
        "base_url": "https://api.hunyuan.cloud.tencent.com/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "hunyuan-vision", "name": "混元 Vision", "description": "多模态", "vision": True},
            {"id": "hunyuan-turbos-latest", "name": "混元 TurboS", "description": "对话（纯文本）"},
        ],
    },
    {
        "id": "baidu", "name": "百度文心", "api_type": "openai_compat",
        "base_url": "https://qianfan.baidubce.com/v2", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "ernie-4.5-turbo-vl", "name": "文心 4.5 Turbo VL", "description": "多模态", "vision": True},
            {"id": "ernie-4.5-turbo-128k", "name": "文心 4.5 Turbo 128K", "description": "对话（纯文本）"},
        ],
    },
    {
        "id": "stepfun", "name": "阶跃星辰", "api_type": "openai_compat",
        "base_url": "https://api.stepfun.com/v1", "base_url_editable": True,
        "requires_base_url": False,
        "models": [
            {"id": "step-1o-turbo-vision", "name": "Step-1o Turbo Vision", "description": "旗舰多模态", "vision": True},
            {"id": "step-1v-8k", "name": "Step-1V 8K", "description": "多模态", "vision": True},
        ],
    },
    {
        "id": "custom", "name": "自定义 (OpenAI 兼容)", "api_type": "openai_compat",
        "base_url": "", "base_url_editable": True, "requires_base_url": True,
        "models": [],  # user types the model name manually
    },
]

# Fast lookup by id.
_BY_ID: Dict[str, ProviderEntry] = {p["id"]: p for p in _PROVIDERS}


def get_provider(provider_id: str) -> Optional[ProviderEntry]:
    """Return the registry entry for a provider id (case-insensitive), or None."""
    if not provider_id:
        return None
    return _BY_ID.get(provider_id.strip().lower())


def get_api_type(provider_id: str) -> str:
    """Return the api_type for a provider id, defaulting to 'openai_compat'."""
    entry = get_provider(provider_id)
    return entry["api_type"] if entry else "openai_compat"


def list_providers() -> List[dict]:
    """Return all providers with a computed ``available`` flag for each.

    ``available`` reflects whether the adapter dependency for that platform is
    importable right now. OpenAI-compatible platforms are always available; the
    Anthropic/Gemini platforms are available only when their langchain package
    is installed. The C# UI uses this to disable platforms it cannot serve.
    """
    result: List[dict] = []
    for p in _PROVIDERS:
        dep = _API_TYPE_DEPENDENCY.get(p["api_type"])
        available = _dep_available(dep)
        entry = dict(p)
        entry["available"] = available
        if not available and dep:
            entry["unavailable_reason"] = f"backend missing dependency: {dep}"
        result.append(entry)
    return result
