using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;
namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Local chat service. Routes chat requests to the local Ollama or cloud
    /// pipeline and streams the response as agent events.
    /// </summary>
    public class LocalChatService : ILocalChatService
    {
        private readonly ILocalLlmService _localLlmService;
        private readonly IAIEngineService _aiService;

        public LocalChatService(ILocalLlmService localLlmService, IAIEngineService aiService)
        {
            _localLlmService = localLlmService;
            _aiService = aiService;
        }

        public async Task ChatAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default)
        {
            await ChatAsync(prompt, references, history, null, onEvent, ct);
        }

        public async Task ChatAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            List<string> images,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default)
        {
            onEvent?.Invoke(AgentStreamEvent.Thinking("本地对话模式"));

            var fullPrompt = BuildChatPrompt(prompt, references, history);
            bool cloudUsed = false;

            // Resolve the local model name to use and sync it to the local service. Fixes
            // a broken-chain bug: routing reads the model name from config, but execution
            // used _localLlmService.CurrentModel; if SetModel was never called,
            // CurrentModel is empty -> Ollama receives an empty model -> 404. Here the
            // config model name wins (falls back to CurrentModel) and is written back
            // via SetModel for consistency.
            string localModel = ResolveLocalModelName();

            // Local Ollama and cloud are two independent pipelines with no mutual fallback:
            //   - Local Ollama model selected -> local direct connection only; a failure fails loudly, never routes through Python.
            //   - Cloud model selected -> Python cloud SSE only.
            bool isLocalModel = IsLocalModelSelected();

            try
            {
                if (isLocalModel)
                {
                    // ── Local Ollama pipeline (direct /api/chat, bypassing the Python backend) ──
                    if (_localLlmService == null || !await _localLlmService.IsAvailableAsync())
                    {
                        onEvent?.Invoke(AgentStreamEvent.Error(
                            "本地 Ollama 服务不可用。请确认 Ollama 已启动（http://localhost:11434）且已加载所选模型。"));
                        return;
                    }

                    onEvent?.Invoke(AgentStreamEvent.Thinking("🦙 Ollama 本地模型"));

                    // Conversation / reasoning tasks -> InvokeAsync(Chat): /api/chat with think=true.
                    // The thought stream is shown live as Thinking events, the body streams as content.
                    string result;
                    if (images != null && images.Count > 0)
                    {
                        // The vision path has no Chat equivalent yet, so keep GenerateStreamWithImagesAsync.
                        result = await _localLlmService.GenerateStreamWithImagesAsync(
                            fullPrompt,
                            images,
                            chunk => onEvent?.Invoke(AgentStreamEvent.Content(chunk)),
                            localModel,
                            maxTokens: 4096,
                            ct: ct);
                    }
                    else
                    {
                        result = await _localLlmService.InvokeAsync(
                            fullPrompt,
                            kind: OllamaTaskKind.Chat,
                            onChunk: c => onEvent?.Invoke(AgentStreamEvent.Content(c)),
                            onThinking: t => { if (!string.IsNullOrEmpty(t)) onEvent?.Invoke(AgentStreamEvent.Thinking(t)); },
                            maxTokens: 4096,
                            ct: ct);
                    }

                    if (string.IsNullOrWhiteSpace(result))
                        onEvent?.Invoke(AgentStreamEvent.Error("本地模型返回空结果。"));
                    return;
                }

                // ── Cloud pipeline (Python SSE, never touching local Ollama) ──
                if (_aiService != null)
                {
                    cloudUsed = true;
                    onEvent?.Invoke(AgentStreamEvent.Thinking("☁️ 云端对话模式"));

                    await _aiService.GenerateSclStreamingExAsync(
                        fullPrompt,
                        sseEvent => OnSseEvent(sseEvent, onEvent),
                        intent: "Chat",
                        ct: ct);
                    return;
                }

                // Service unavailable
                onEvent?.Invoke(AgentStreamEvent.Error("本地聊天服务返回空结果。"));
            }
            catch (OperationCanceledException)
            {
                if (cloudUsed)
                    onEvent?.Invoke(AgentStreamEvent.Thinking("对话已取消"));
            }
            catch (Exception ex)
            {
                onEvent?.Invoke(AgentStreamEvent.Error("本地聊天失败: " + ex.Message));
            }
        }

        private static void OnSseEvent(SseEvent sseEvent, Action<AgentStreamEvent> onEvent)
        {
            if (sseEvent == null || onEvent == null) return;

            switch (sseEvent.EventType)
            {
                case "content":
                case "delta":
                    onEvent(AgentStreamEvent.Content(sseEvent.TextContent ?? ""));
                    break;

                case "thinking":
                    var thinkingText = (sseEvent.TextContent ?? sseEvent.Thought?.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(thinkingText)) break;
                    // Filter internal backend markers (JSON parse results, diagnostic tags, etc.) — they are not real thought content.
                    if (thinkingText == "done" || thinkingText == "generating" ||
                        thinkingText == "json_fallback" || thinkingText == "json_parsed" ||
                        thinkingText == "langchain-agent" || thinkingText == "xml-ast-llm" ||
                        thinkingText == "xml-analysis-llm" || thinkingText == "multi_agent_json_parsed" ||
                        thinkingText == "multi_agent_json_fallback")
                        break;
                    onEvent(AgentStreamEvent.Thinking(thinkingText));
                    break;

                case "error":
                    onEvent(AgentStreamEvent.Error(sseEvent.ErrorMessage ?? sseEvent.TextContent ?? ""));
                    break;

                case "tool_call":
                    HandleToolCallEvent(sseEvent.TextContent, onEvent);
                    break;

                case "status":
                    onEvent(AgentStreamEvent.Status(sseEvent.Status?.Message ?? sseEvent.TextContent ?? ""));
                    break;

                // done / plan_progress / context_ref — not forwarded to the Chat UI
            }
        }

        private static void HandleToolCallEvent(string jsonText, Action<AgentStreamEvent> onEvent)
        {
            if (string.IsNullOrWhiteSpace(jsonText)) return;
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

                if (status == "started")
                {
                    var input = root.TryGetProperty("input", out var inp) ? inp.GetRawText() : null;
                    onEvent(AgentStreamEvent.ToolStart(name, input));
                }
                else if (status == "completed")
                {
                    var output = root.TryGetProperty("output", out var outp) ? outp.GetString() : null;
                    onEvent(AgentStreamEvent.ToolEnd(name, output));
                }
            }
            catch
            {
                // Silently ignore parse failures
            }
        }

        /// <summary>
        /// Whether the currently selected model is a local Ollama model (model
        /// name carries a tag such as qwen3:9b). Returns false conservatively
        /// (treated as cloud) when reading the config fails.
        /// </summary>
        private bool IsLocalModelSelected()
        {
            try
            {
                var cfg = AIConfig.Load();
                bool isLocal = ChatModelRouting.IsLocalModelSelected(cfg, _localLlmService?.CurrentModel);
                AppLogger.Log($"[LocalChat] model routing: Model='{cfg?.Model}' SelectedModel='{cfg?.SelectedModel}' " +
                              $"Provider='{cfg?.ModelProvider}' localCurrent='{_localLlmService?.CurrentModel}' -> isLocal={isLocal}");
                return isLocal;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the Ollama model name for this local chat. Preference order:
        ///   config.Model / config.SelectedModel (same source routing uses)
        ///   &gt; local service CurrentModel (fallback).
        /// The result is written back to _localLlmService to keep CurrentModel
        /// consistent with what is actually used, avoiding the broken chain where
        /// "routing recognizes the model but execution sends an empty model name -> 404".
        /// </summary>
        private string ResolveLocalModelName()
        {
            string fromConfig = null;
            try
            {
                var cfg = AIConfig.Load();
                // Take the first non-empty: Model > SelectedModel (same rule as ChatModelRouting).
                fromConfig = new[] { cfg?.Model, cfg?.SelectedModel }
                    .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            }
            catch
            {
                // Fall back to CurrentModel when reading the config fails.
            }

            var current = _localLlmService?.CurrentModel;
            var resolved = !string.IsNullOrWhiteSpace(fromConfig) ? fromConfig : current;

            // Sync back to the local service: keep CurrentModel in step with what is actually
            // sent, so downstream callers that depend on CurrentModel (intent
            // classification, compression, etc.) also use the correct model.
            if (!string.IsNullOrWhiteSpace(resolved) &&
                !string.Equals(resolved, current, StringComparison.Ordinal))
            {
                _localLlmService?.SetModel(resolved);
                AppLogger.Log($"[LocalChat] resolved local model '{resolved}' (was CurrentModel='{current}'), synced via SetModel");
            }

            return resolved;
        }

        private static string BuildChatPrompt(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a concise TIA Portal assistant.");
            sb.AppendLine("Answer as a normal chat assistant. Do not call tools or propose file writes unless the user explicitly asks.");

            var recentHistory = history?
                .Where(m => m != null)
                .Reverse()
                .Take(6)
                .Reverse()
                .ToList();
            if (recentHistory != null && recentHistory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Conversation history:");
                foreach (var item in recentHistory)
                {
                    var role = item.TryGetValue("role", out var roleVal) ? roleVal : "user";
                    var content = item.TryGetValue("content", out var contentVal) ? contentVal : string.Empty;
                    sb.AppendLine($"[{role}] {content}");
                }
            }

            if (references != null && references.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Referenced context:");
                foreach (var reference in references.Take(3))
                {
                    sb.AppendLine($"- {reference.DisplayName ?? reference.Source ?? "reference"}");
                    if (!string.IsNullOrWhiteSpace(reference.Packed))
                    {
                        var packed = reference.Packed.Length > 1000 ? reference.Packed.Substring(0, 1000) + "..." : reference.Packed;
                        sb.AppendLine(packed);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("User request:");
            sb.AppendLine(prompt);
            return sb.ToString();
        }
    }
}
