using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Read-only analysis service. Routes analysis requests to the local Ollama
    /// or cloud pipeline and streams the result as agent events.
    /// </summary>
    public class SimpleAnalyzeService : ISimpleAnalyzeService
    {
        private readonly ILocalLlmService _localLlmService;
        private readonly IAIEngineService _aiService;

        public SimpleAnalyzeService(ILocalLlmService localLlmService, IAIEngineService aiService)
        {
            _localLlmService = localLlmService;
            _aiService = aiService;
        }

        public async Task AnalyzeAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default)
        {
            await AnalyzeAsync(prompt, references, history, null, onEvent, ct);
        }

        public async Task AnalyzeAsync(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            List<string> images,
            Action<AgentStreamEvent> onEvent,
            CancellationToken ct = default)
        {
            onEvent?.Invoke(AgentStreamEvent.Thinking("只读分析模式"));

            // Local Ollama keeps the references inline in the prompt (small context window, so the
            // 4000-char per-reference cap in BuildAnalyzePrompt is intentional there).
            var fullPrompt = BuildAnalyzePrompt(prompt, references, history);
            bool cloudUsed = false;

            // Local Ollama and cloud are two independent pipelines; neither falls back to the other.
            bool isLocalModel = IsLocalModelSelected();

            // Same as LocalChatService: trust the config model name (fall back to CurrentModel) to avoid an empty model -> 404.
            string localModel = ResolveLocalModelName();

            try
            {
                if (isLocalModel)
                {
                    // ── Local Ollama pipeline (direct, bypassing Python) ──
                    if (_localLlmService == null || !await _localLlmService.IsAvailableAsync())
                    {
                        onEvent?.Invoke(AgentStreamEvent.Error(
                            "本地 Ollama 服务不可用。请确认 Ollama 已启动（http://localhost:11434）且已加载所选模型。"));
                        return;
                    }

                    onEvent?.Invoke(AgentStreamEvent.Thinking("🦙 Ollama 本地分析"));

                    // Conversation / reasoning tasks -> InvokeAsync(Chat): /api/chat with think=true.
                    string result;
                    if (images != null && images.Count > 0)
                    {
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

                // ── Cloud pipeline (Python SSE) ──
                if (_aiService != null)
                {
                    cloudUsed = true;
                    onEvent?.Invoke(AgentStreamEvent.Thinking("☁️ 云端分析模式"));

                    // Cloud: send the references as the structured `context` field, complete. Putting
                    // them in the prompt cut each one to its first 4000 chars, which for a LAD/FBD block
                    // is only the interface section; the networks never reached the model.
                    var cloudPrompt = BuildAnalyzePrompt(prompt, null, history, cloud: true);
                    var cloudContext = BuildCloudReferenceContext(references);
                    AppLogger.Log($"[Analyze] cloud: prompt={cloudPrompt.Length} chars, reference context={cloudContext.Length} chars");

                    await _aiService.GenerateSclStreamingExAsync(
                        cloudPrompt,
                        sseEvent => OnSseEvent(sseEvent, onEvent),
                        context: cloudContext,
                        intent: AgentIntent.ExplainCode.ToString(),
                        ct: ct);
                    return;
                }

                onEvent?.Invoke(AgentStreamEvent.Error("云端分析服务不可用。"));
            }
            catch (OperationCanceledException)
            {
                if (cloudUsed)
                    onEvent?.Invoke(AgentStreamEvent.Thinking("分析已取消"));
            }
            catch (Exception ex)
            {
                onEvent?.Invoke(AgentStreamEvent.Error("只读分析失败: " + ex.Message));
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
            catch { }
        }

        /// <summary>
        /// Whether the currently selected model is a local Ollama model. Returns
        /// false conservatively when reading the config fails.
        /// </summary>
        private bool IsLocalModelSelected()
        {
            try
            {
                var cfg = AIConfig.Load();
                bool isLocal = ChatModelRouting.IsLocalModelSelected(cfg, _localLlmService?.CurrentModel);
                AppLogger.Log($"[Analyze] model routing: Model='{cfg?.Model}' SelectedModel='{cfg?.SelectedModel}' " +
                              $"Provider='{cfg?.ModelProvider}' localCurrent='{_localLlmService?.CurrentModel}' -> isLocal={isLocal}");
                return isLocal;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the Ollama model name for this local analysis. Prefers
        /// config.Model / config.SelectedModel, falls back to CurrentModel, and
        /// writes the result back via SetModel. Fixes the broken chain where
        /// "routing recognizes the model but execution sends an empty model
        /// name -> 404".
        /// </summary>
        private string ResolveLocalModelName()
        {
            string fromConfig = null;
            try
            {
                var cfg = AIConfig.Load();
                fromConfig = new[] { cfg?.Model, cfg?.SelectedModel }
                    .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            }
            catch
            {
                // Fall back to CurrentModel when reading the config fails.
            }

            var current = _localLlmService?.CurrentModel;
            var resolved = !string.IsNullOrWhiteSpace(fromConfig) ? fromConfig : current;

            if (!string.IsNullOrWhiteSpace(resolved) &&
                !string.Equals(resolved, current, StringComparison.Ordinal))
            {
                _localLlmService?.SetModel(resolved);
                AppLogger.Log($"[Analyze] resolved local model '{resolved}' (was CurrentModel='{current}'), synced via SetModel");
            }

            return resolved;
        }

        /// <summary>
        /// Full text of the user-attached references for the cloud backend. Each reference is capped
        /// at <see cref="DefaultValues.MaxReferenceLength"/> (the same limit the IR builder already
        /// compacts to), not the 4000 chars used for local models.
        /// </summary>
        private static string BuildCloudReferenceContext(IReadOnlyList<AiReferenceItem> references)
        {
            if (references == null || references.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## User-Attached References");
            sb.AppendLine("The user attached the following blocks/files to analyze. LAD/FBD/GRAPH blocks are given as structured JSON IR.");
            sb.AppendLine();

            int index = 0;
            int totalChars = 0;
            foreach (var reference in references.Where(r => r != null))
            {
                var packed = reference.Packed ?? string.Empty;
                // Always include the first reference; afterwards stop once the combined
                // size would exceed the total budget, so a multi-block selection is no
                // longer silently cut to 5 blocks.
                if (index > 0 && totalChars + packed.Length > DefaultValues.MaxTotalReferenceLength)
                {
                    sb.AppendLine("... (remaining references omitted: total context budget reached)");
                    break;
                }

                index++;
                sb.AppendLine($"### Reference {index}: {reference.DisplayName ?? reference.Source ?? "(unnamed)"}");
                if (!string.IsNullOrWhiteSpace(packed))
                {
                    // The IR builder already guarantees a structurally complete result
                    // (it drops whole networks + sets fidelity.truncated when over
                    // budget), so this must not blind-cut the JSON mid-structure.
                    sb.AppendLine(packed);
                }
                sb.AppendLine();
                totalChars += packed.Length;
            }
            return sb.ToString();
        }

        private static string BuildAnalyzePrompt(
            string prompt,
            IReadOnlyList<AiReferenceItem> references,
            IReadOnlyList<Dictionary<string, string>> history,
            bool cloud = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a TIA Portal read-only analysis assistant.");
            sb.AppendLine("Explain code, logic, or compile diagnostics clearly.");
            if (cloud)
            {
                // The cloud agent has read-only project tools and save_analysis_doc (documents only).
                sb.AppendLine("Never modify the TIA project or propose write-back steps unless the user explicitly asks.");
                sb.AppendLine("If the user asks to save or export the analysis, save it as a document with the save_analysis_doc tool.");
            }
            else
            {
                // Local Ollama path has no tools at all.
                sb.AppendLine("Do not call tools. Do not propose write-back steps unless the user explicitly asks.");
            }

            var recentHistory = history?
                .Where(m => m != null)
                .Reverse()
                .Take(4)
                .Reverse()
                .ToList();
            if (recentHistory != null && recentHistory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent conversation:");
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
                sb.AppendLine("Analysis context:");
                foreach (var reference in references.Take(5))
                {
                    sb.AppendLine($"## {reference.DisplayName ?? reference.Source ?? "reference"}");
                    if (!string.IsNullOrWhiteSpace(reference.Packed))
                    {
                        var packed = reference.Packed.Length > 4000 ? reference.Packed.Substring(0, 4000) + "..." : reference.Packed;
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
