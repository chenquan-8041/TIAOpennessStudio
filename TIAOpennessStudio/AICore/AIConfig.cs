using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.AICore
{
    public class AIConfig
    {
        public bool UseRealAI { get; set; }
        public bool UseLocalBackend { get; set; } = true; // Use local backend by default
        public string Provider { get; set; } // "OpenAI", "Custom"
        public string Endpoint { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public bool IncludeContext { get; set; }

        /// <summary>
        /// Reasoning effort for reasoning-capable models ("low" / "medium" / "high").
        /// Forwarded to the OpenAI-compatible backend as `reasoning_effort`; ignored
        /// by providers that do not support it. Default "medium".
        /// </summary>
        public string ReasoningEffort { get; set; } = "medium";
        
        // Selected model list
        public string SelectedModel { get; set; }
        
        // Execution mode: "LocalGateway" | "Direct"
        public string ExecutionMode { get; set; }

        // Model provider: "OpenAI" | "Custom" | "Ollama"
        public string ModelProvider { get; set; }

        /// <summary>
        /// Agent loop feature flags (V2 refactor). Persisted as "Agent" section in ai_config.json.
        /// All flags default to false — no behavior change until explicitly enabled.
        /// </summary>
        [JsonPropertyName("agent")]
        public AgentConfig Agent { get; set; }

        /// <summary>
        /// User-selected default Ollama model. When set, startup uses this instead of auto-detection.
        /// </summary>
        [JsonPropertyName("defaultOllamaModel")]
        public string DefaultOllamaModel { get; set; }

        // Local backend URL
        public string LocalBackendUrl { get; set; } = "http://127.0.0.1:8000";

        // Custom model names the user saved in the Model Config dialog (persisted across restarts, removable in the UI)
        public List<string> CustomModels { get; set; } = new List<string>();

        /// <summary>
        /// Vision (multimodal) capability cache. key = model name (lowercase), value = whether image input is supported.
        /// Only "definite" probe results (supported / unsupported) are cached; unknown results are not written,
        /// so the next run can probe again. Persisted in ai_config.json to avoid re-probing the same model on every startup.
        /// The built-in keyword list (IsVisionModel) takes priority over this cache.
        /// </summary>
        [JsonPropertyName("visionSupportCache")]
        public Dictionary<string, bool> VisionSupportCache { get; set; } = new Dictionary<string, bool>();

        /// <summary>
        /// Built-in local LLM model names (Ollama 7B+ models that support the Agent Loop).
        /// Shown read-only in the Model Config dialog.
        /// </summary>
        public static readonly List<string> BuiltInLocalLlmModels = new List<string>
        {
            "qwen2.5:7b",
            "qwen2.5:14b",
            "qwen2.5:32b",
            "qwen2.5:72b",
            "mistral:7b",
            "codellama:7b",
            "codellama:13b",
            "codellama:34b",
        };

        /// <summary>
        /// Determines whether a model name belongs to a local Ollama model.
        /// Ollama model names always include a tag (e.g. qwen2.5:3b), which distinguishes them from cloud models.
        /// Callers should prefer IOllamaHostService.GetInstalledModelsAsync() to get the actually installed list.
        /// </summary>
        public static bool IsLocalLlmModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            return modelName.Contains(":");
        }

        /// <summary>
        /// Adds a custom model name (deduplicated, ignores empty strings, and avoids names already in the local LLM list or the current provider's built-in models).
        /// </summary>
        public bool AddCustomModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            var name = modelName.Trim();

            if (CustomModels == null) CustomModels = new List<string>();
            if (CustomModels.Contains(name)) return false;

            // Deduplicate against local LLMs (names containing ":" are managed by Ollama and are not added to the custom list)
            if (IsLocalLlmModel(name)) return false;

            // Deduplicate against the current provider's built-in models (compared by Id)
            foreach (var m in GetCurrentProviderModels())
            {
                if (string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase)) return false;
            }

            CustomModels.Add(name);
            return true;
        }

        public bool RemoveCustomModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName) || CustomModels == null) return false;
            return CustomModels.RemoveAll(m => string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        private static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TIAOpennessStudio");
        public static string ConfigPath => Path.Combine(ConfigDir, "ai_config.json");
        private static string LegacyConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_config.json");

        /// <summary>
        /// Gets the list of supported models.
        /// </summary>
        public static Dictionary<string, List<ModelInfo>> GetAvailableModels()
        {
            return new Dictionary<string, List<ModelInfo>>
            {
                // ── OpenAI (USA) ──
                ["OpenAI"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "gpt-6-astra", Name = "GPT-6 (Astra)", Description = "旗舰多模态，1.05M上下文", SupportsVision = true },
                    new ModelInfo { Id = "gpt-6-sol",   Name = "GPT-6 (Sol)",   Description = "中端多模态，872K上下文",  SupportsVision = true },
                    new ModelInfo { Id = "gpt-6-luna",  Name = "GPT-6 (Luna)",  Description = "轻量多模态，1.05M上下文，最低价", SupportsVision = true },
                    new ModelInfo { Id = "gpt-4o", Name = "GPT-4o", Description = "旗舰多模态模型", SupportsVision = true },
                    new ModelInfo { Id = "gpt-4o-mini", Name = "GPT-4o-mini", Description = "小型快速多模态", SupportsVision = true },
                    new ModelInfo { Id = "gpt-4.1", Name = "GPT-4.1", Description = "长上下文多模态", SupportsVision = true },
                    new ModelInfo { Id = "gpt-4-turbo", Name = "GPT-4-Turbo", Description = "高性能多模态", SupportsVision = true },
                    new ModelInfo { Id = "o3", Name = "o3", Description = "推理模型（多模态）", SupportsVision = true },
                    new ModelInfo { Id = "o4-mini", Name = "o4-mini", Description = "轻量推理（多模态）", SupportsVision = true },
                    new ModelInfo { Id = "gpt-3.5-turbo", Name = "GPT-3.5-Turbo", Description = "经济实惠（纯文本）" },
                },

                // ── Anthropic Claude (USA) ── all multimodal
                ["Anthropic"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "claude-sonnet-4-20250514", Name = "Claude Sonnet 4", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "claude-opus-4-20250514", Name = "Claude Opus 4", Description = "最强多模态", SupportsVision = true },
                    new ModelInfo { Id = "claude-3-7-sonnet-latest", Name = "Claude 3.7 Sonnet", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "claude-3-5-sonnet-latest", Name = "Claude 3.5 Sonnet", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "claude-3-5-haiku-latest", Name = "Claude 3.5 Haiku", Description = "快速多模态", SupportsVision = true },
                },

                // ── Google Gemini (USA) ── all multimodal
                ["Google"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "gemini-2.5-pro", Name = "Gemini 2.5 Pro", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "gemini-2.5-flash", Name = "Gemini 2.5 Flash", Description = "快速多模态", SupportsVision = true },
                    new ModelInfo { Id = "gemini-2.0-flash", Name = "Gemini 2.0 Flash", Description = "多模态", SupportsVision = true },
                },

                // ── xAI Grok (USA) ──
                ["xAI"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "grok-4", Name = "Grok 4", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "grok-2-vision-1212", Name = "Grok 2 Vision", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "grok-3", Name = "Grok 3", Description = "文本推理" },
                },

                // ── DeepSeek (China mainland) ── chat/reasoner are text-only
                ["DeepSeek"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "deepseek-chat", Name = "DeepSeek-V3", Description = "对话模型，支持上下文缓存（纯文本）" },
                    new ModelInfo { Id = "deepseek-reasoner", Name = "DeepSeek-R1", Description = "推理模型（纯文本）" },
                },

                // ── Alibaba Qwen (China mainland) ──
                ["Qwen"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "qwen-vl-max", Name = "Qwen-VL-Max", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "qwen-vl-plus", Name = "Qwen-VL-Plus", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "qwen2.5-vl-72b-instruct", Name = "Qwen2.5-VL-72B", Description = "开源多模态", SupportsVision = true },
                    new ModelInfo { Id = "qwen-max", Name = "Qwen-Max", Description = "旗舰对话（纯文本）" },
                    new ModelInfo { Id = "qwen-plus", Name = "Qwen-Plus", Description = "均衡对话（纯文本）" },
                },

                // ── Zhipu GLM (China mainland) ──
                ["Zhipu"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "glm-4v-plus", Name = "GLM-4V-Plus", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "glm-4v", Name = "GLM-4V", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "glm-4.5", Name = "GLM-4.5", Description = "旗舰对话（纯文本）" },
                    new ModelInfo { Id = "glm-4-plus", Name = "GLM-4-Plus", Description = "对话（纯文本）" },
                },

                // ── Moonshot Kimi (China mainland) ──
                ["Moonshot"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "moonshot-v1-128k-vision-preview", Name = "Kimi Vision 128K", Description = "长上下文多模态", SupportsVision = true },
                    new ModelInfo { Id = "kimi-latest", Name = "Kimi Latest", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "moonshot-v1-128k", Name = "Moonshot 128K", Description = "长上下文（纯文本）" },
                },

                // ── ByteDance Doubao (China mainland) ──
                ["Doubao"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "doubao-1.5-vision-pro", Name = "豆包 1.5 Vision Pro", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "doubao-1.5-pro-32k", Name = "豆包 1.5 Pro 32K", Description = "对话（纯文本）" },
                },

                // ── Tencent Hunyuan (China mainland) ──
                ["Hunyuan"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "hunyuan-vision", Name = "混元 Vision", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "hunyuan-turbos-latest", Name = "混元 TurboS", Description = "对话（纯文本）" },
                },

                // ── Baidu ERNIE (China mainland) ──
                ["Baidu"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "ernie-4.5-turbo-vl", Name = "文心 4.5 Turbo VL", Description = "多模态", SupportsVision = true },
                    new ModelInfo { Id = "ernie-4.5-turbo-128k", Name = "文心 4.5 Turbo 128K", Description = "对话（纯文本）" },
                },

                // ── StepFun (China mainland) ──
                ["StepFun"] = new List<ModelInfo>
                {
                    new ModelInfo { Id = "step-1o-turbo-vision", Name = "Step-1o Turbo Vision", Description = "旗舰多模态", SupportsVision = true },
                    new ModelInfo { Id = "step-1v-8k", Name = "Step-1V 8K", Description = "多模态", SupportsVision = true },
                },

                ["Custom"] = new List<ModelInfo>
                {
                    // The Custom provider ships no preset models; the user enters real model names
                    // in the Model Config dialog. Saved names go into AIConfig.CustomModels, persist across
                    // sessions, and appear in the input dropdown.
                }
            };
        }

        /// <summary>
        /// Determines whether a model supports image/Vision capability by its name (used for dynamic Ollama model detection).
        /// </summary>
        public static bool IsVisionModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            var name = modelName.ToLowerInvariant();

            // Known vision (multimodal) model keywords. A hit means image input is supported.
            // Covers common China-mainland vendors + major US vendors + local Ollama vision models.
            string[] visionKeywords = {
                // ── Local Ollama vision models ──
                "llava", "bakllava", "moondream", "minicpm-v", "llava-phi",
                "obsidian", "granite3-vision", "llama3.2-vision", "llama-3.2-vision",

                // ── OpenAI (USA) ──
                "gpt-4o", "gpt-4.1", "gpt-4-turbo", "gpt-4-vision", "gpt-4v",
                "o1", "o3", "o4-mini", "chatgpt-4o",

                // ── Anthropic Claude (USA) ── 3/3.5/3.7/4 all multimodal
                "claude-3", "claude-4", "claude-sonnet", "claude-opus", "claude-haiku",
                "claude-3-5", "claude-3.5", "claude-3-7", "claude-3.7",

                // ── Google Gemini (USA) ── all multimodal
                "gemini",

                // ── xAI Grok (USA) ──
                "grok-2-vision", "grok-4", "grok-vision",

                // ── Alibaba Qwen-VL (China mainland) ──
                "qwen-vl", "qwen2-vl", "qwen2.5-vl", "qwen-vl-max", "qwen-vl-plus",
                "qvq",

                // ── Zhipu GLM (China mainland) ──
                "glm-4v", "glm-4.1v", "cogvlm", "cogagent",

                // ── StepFun Step (China mainland, strong in multimodal) ──
                "step-1v", "step-1o", "step-3",

                // ── ByteDance Doubao (China mainland) ──
                "doubao-vision", "doubao-1.5-vision", "doubao-seed",

                // ── Moonshot Kimi (China mainland) ──
                "moonshot-v1-8k-vision", "moonshot-v1-32k-vision",
                "moonshot-v1-128k-vision", "kimi-vl", "kimi-latest",

                // ── Tencent Hunyuan (China mainland) ──
                "hunyuan-vision", "hunyuan-t1-vision", "hunyuan-turbos-vision",

                // ── Baidu ERNIE (China mainland) ──
                "ernie-4.5", "ernie-4.0-8k-latest", "ernie-vl",

                // ── DeepSeek (China mainland) ── VL series is multimodal (deepseek-chat/reasoner are text-only)
                "deepseek-vl",

                // ── Generic vision markers ──
                "-vl", "-vision", "vision-",
            };

            foreach (var kw in visionKeywords)
            {
                if (name.Contains(kw)) return true;
            }

            return false;
        }

        /// <summary>
        /// Uniformly determines whether a model supports image/multimodal input. Three-state return:
        ///   true  → supported (hits the built-in keyword list, or the probe cache is true)
        ///   false → not supported (probe cache is false)
        ///   null  → unknown (neither in the built-in list nor cached) → the caller should trigger a /probe_vision probe
        /// Order: built-in list (IsVisionModel) → probe cache (VisionSupportCache).
        /// </summary>
        public bool? ResolveVisionSupport(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;

            // 1) Built-in keyword list first (gpt-4o / claude-3 / llava, etc.), always true
            if (IsVisionModel(modelName)) return true;

            // 2) Probe cache
            var key = modelName.Trim().ToLowerInvariant();
            if (VisionSupportCache != null && VisionSupportCache.TryGetValue(key, out var cached))
                return cached;

            // 3) Unknown → needs probing
            return null;
        }

        /// <summary>
        /// Writes and persists a model's vision probe result. Call only when probing yields a "definite"
        /// conclusion (supported=true / unsupported=false); do not call for unknown.
        /// </summary>
        public void SetVisionSupport(string modelName, bool supported)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            if (VisionSupportCache == null) VisionSupportCache = new Dictionary<string, bool>();
            var key = modelName.Trim().ToLowerInvariant();
            VisionSupportCache[key] = supported;
            try { Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SetVisionSupport save failed: " + ex.Message); }
        }

        /// <summary>
        /// Gets the model list for the current provider.
        /// </summary>
        public List<ModelInfo> GetCurrentProviderModels()
        {
            var allModels = GetAvailableModels();
            if (allModels.ContainsKey(Provider))
            {
                return allModels[Provider];
            }
            return new List<ModelInfo>();
        }

        public static AIConfig Load()
        {
            AIConfig config = null;

            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<AIConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                    // Ensure UseLocalBackend defaults to true if not in config file
                    if (config != null && !json.Contains("\"UseLocalBackend\""))
                    {
                        config.UseLocalBackend = true;
                        config.Save(); // Update config file with new field
                    }

                    // Ensure LocalBackendUrl exists
                    if (config != null && string.IsNullOrEmpty(config.LocalBackendUrl))
                    {
                        config.LocalBackendUrl = "http://127.0.0.1:8000";
                        config.Save();
                    }

                    // Ensure Agent config section exists (V2 refactor feature flags)
                    if (config != null && config.Agent == null)
                    {
                        config.Agent = AgentConfig.CreateDefault();
                    }

                    // Migration: force cloud routing flags on — these used to default to false
                    // when the unified engine was not yet validated. Cloud models (non-Ollama)
                    // must always use UseUnifiedEngine + UseCloudAgent for the Python LangGraph
                    // path. Without this, GenerateCode falls back to the legacy static 4-step plan.
                    if (config?.Agent != null && !config.Agent.UseUnifiedEngine)
                    {
                        config.Agent.UseUnifiedEngine = true;
                        config.Agent.UseCloudAgent = true;
                        config.Save();
                    }

                    // Backward compatibility: the CustomModels field did not exist before v1.4.x
                    if (config != null && config.CustomModels == null)
                    {
                        config.CustomModels = new List<string>();
                    }

                    // Backward compatibility: the VisionSupportCache field did not exist before vision probing was introduced
                    if (config != null && config.VisionSupportCache == null)
                    {
                        config.VisionSupportCache = new Dictionary<string, bool>();
                    }

                    // Migration: early "Custom" providers shipped a hard-coded placeholder model id="custom";
                    // any request using it is rejected by the upstream gateway with 503. Clear it on next start so the user can re-select.
                    if (config != null &&
                        string.Equals(config.Model, "custom", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Model = "";
                        config.SelectedModel = "";
                        config.Save();
                    }
                }
                else if (File.Exists(LegacyConfigPath))
                {
                    var json = File.ReadAllText(LegacyConfigPath);
                    config = JsonSerializer.Deserialize<AIConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                    if (config != null)
                    {
                        if (string.IsNullOrEmpty(config.LocalBackendUrl))
                        {
                            config.LocalBackendUrl = "http://127.0.0.1:8000";
                        }
                        config.Save();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error loading config: " + ex.Message);
            }

            // Create and save a default config only when no config file exists.
            // If the file exists but deserialization fails, do not overwrite it to avoid data loss.
            if (config == null)
            {
                if (!File.Exists(ConfigPath) && !File.Exists(LegacyConfigPath))
                {
                    config = new AIConfig
                    {
                        UseRealAI = true,
                        Provider = "OpenAI",
                        Endpoint = "https://api.openai.com/v1/chat/completions",
                        ApiKey = "",
                        Model = "gpt-4o-mini",
                        SelectedModel = "gpt-4o-mini",
                        IncludeContext = false,
                        LocalBackendUrl = "http://127.0.0.1:8000",
                        Agent = AgentConfig.CreateDefault()
                    };
                    config.Save();
                }
                else
                {
                    // File exists but loading failed (e.g. corrupted JSON): return a default in-memory config without overwriting the file
                    config = new AIConfig
                    {
                        UseRealAI = true,
                        Provider = "OpenAI",
                        Endpoint = "https://api.openai.com/v1/chat/completions",
                        ApiKey = "",
                        Model = "gpt-4o-mini",
                        SelectedModel = "gpt-4o-mini",
                        IncludeContext = false,
                        LocalBackendUrl = "http://127.0.0.1:8000",
                        Agent = AgentConfig.CreateDefault()
                    };
                }
            }

            return config;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error saving config: " + ex.Message);
                throw; // Re-throw so the caller is aware the save failed
            }
        }
        
        /// <summary>
        /// Gets the effective execution mode (backward compatible with old configs).
        /// </summary>
        public string GetEffectiveExecutionMode()
        {
            if (!string.IsNullOrEmpty(ExecutionMode)) return ExecutionMode;
            return UseLocalBackend ? "LocalGateway" : "Direct";
        }

        /// <summary>
        /// Gets the effective model provider (backward compatible with old configs).
        /// </summary>
        public string GetEffectiveModelProvider()
        {
            if (!string.IsNullOrEmpty(ModelProvider)) return ModelProvider;
            return Provider ?? "OpenAI";
        }

        /// <summary>
        /// Gets the default endpoint for the given provider.
        /// </summary>
        public static string GetDefaultEndpointForProvider(string provider)
        {
            switch (provider)
            {
                // ── USA ──
                case "OpenAI":
                    return "https://api.openai.com/v1/chat/completions";
                case "Anthropic":
                    // Claude exposes an OpenAI-compatible endpoint
                    return "https://api.anthropic.com/v1/chat/completions";
                case "Google":
                    // Gemini OpenAI-compatible endpoint
                    return "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                case "xAI":
                    return "https://api.x.ai/v1/chat/completions";
                // ── China mainland ──
                case "DeepSeek":
                    return "https://api.deepseek.com/v1/chat/completions";
                case "Qwen":
                    return "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
                case "Zhipu":
                    return "https://open.bigmodel.cn/api/paas/v4/chat/completions";
                case "Moonshot":
                    return "https://api.moonshot.cn/v1/chat/completions";
                case "Doubao":
                    return "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
                case "Hunyuan":
                    return "https://api.hunyuan.cloud.tencent.com/v1/chat/completions";
                case "Baidu":
                    return "https://qianfan.baidubce.com/v2/chat/completions";
                case "StepFun":
                    return "https://api.stepfun.com/v1/chat/completions";
                // ── Local ──
                case "Ollama":
                    return "http://localhost:11434/v1/chat/completions";
                case "Custom":
                default:
                    return "";
            }
        }

        /// <summary>
        /// Updates config when the provider is switched.
        /// </summary>
        public void OnProviderChanged(string newProvider)
        {
            Provider = newProvider;
            var models = GetCurrentProviderModels();
            if (models.Count > 0)
            {
                SelectedModel = models[0].Id;
                Model = models[0].Id;
            }

            // Set the default endpoint per provider (covers all built-in providers)
            var ep = GetDefaultEndpointForProvider(newProvider);
            if (!string.IsNullOrEmpty(ep))
            {
                Endpoint = ep;
            }
        }
    }
    
    /// <summary>
    /// Model information.
    /// </summary>
    public class ModelInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Whether the model supports image/multimodal input.</summary>
        public bool SupportsVision { get; set; }
    }
}
