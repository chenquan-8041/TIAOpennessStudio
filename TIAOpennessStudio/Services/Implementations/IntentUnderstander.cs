using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Phase 4: Deep intent understanding via LLM.
    /// Goes beyond the 6-label keyword classifier to understand task complexity,
    /// identify missing information, and generate clarification questions.
    ///
    /// When Planning is disabled or LLM fails, falls back to the simple
    /// IntentClassifier behavior (single label, no deep understanding).
    /// </summary>
    public class IntentUnderstander : IIntentUnderstander
    {
        private readonly IAIEngineService _aiService;
        private readonly ILocalLlmService _localLlmService;

        public IntentUnderstander(IAIEngineService aiService, ILocalLlmService localLlmService = null)
        {
            _aiService = aiService;
            _localLlmService = localLlmService;
        }

        public async Task<StructuredIntent> UnderstandAsync(
            string userPrompt,
            string projectContext = "",
            List<Dictionary<string, string>> history = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                return new StructuredIntent
                {
                    IntentType = "Chat",
                    Complexity = TaskComplexity.Simple,
                    Summary = "Empty prompt"
                };
            }

            try
            {
                var response = await CallLlmForUnderstandingAsync(userPrompt, projectContext, ct);
                return ParseStructuredIntent(response, userPrompt);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[IntentUnderstander] LLM call failed: {ex.Message}, falling back to simple classification");
                return CreateFallbackIntent(userPrompt);
            }
        }

        /// <summary>
        /// Call LLM with a structured prompt to understand the user's intent.
        /// </summary>
        private async Task<string> CallLlmForUnderstandingAsync(string userPrompt, string projectContext, CancellationToken ct)
        {
            var prompt = BuildUnderstandingPrompt(userPrompt, projectContext);
            var isLocal = ChatModelRouting.IsLocalModelSelected(AIConfig.Load(), _localLlmService?.CurrentModel);
            if (isLocal && _localLlmService != null)
                return await _localLlmService.InvokeAsync(prompt, OllamaTaskKind.Completion, ct: ct);
            return await _aiService.GenerateSclAsync(prompt);
        }

        /// <summary>
        /// Build the prompt for LLM-based intent understanding.
        /// </summary>
        private string BuildUnderstandingPrompt(string userPrompt, string projectContext)
        {
            var lines = new List<string>
            {
                "You are a TIA Portal task analyzer. Analyze the user's request and output a JSON response.",
                "",
                "## User Request",
                userPrompt,
                ""
            };

            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                lines.Add("## Project Context");
                lines.Add(projectContext);
                lines.Add("");
            }

            lines.Add("## Your Task");
            lines.Add("Output ONLY valid JSON (no markdown fences, no explanation):");
            lines.Add("{");
            lines.Add("  \"intentType\": \"Chat|GenerateCode|ModifyCode|Compile|Diagnose|ExplainCode|TraceVariable\",");
            lines.Add("  \"complexity\": \"Simple|Medium|Complex\",");
            lines.Add("  \"summary\": \"One-sentence summary of what the user wants\",");
            lines.Add("  \"missingInfo\": [\"info not provided that would help\"],");
            lines.Add("  \"clarificationQuestions\": [\"questions to ask before proceeding\"],");
            lines.Add("  \"estimatedToolCalls\": 3,");
            lines.Add("  \"estimatedTokens\": 5000");
            lines.Add("}");
            lines.Add("");
            lines.Add("## Rules");
            lines.Add("- Simple = single step (explain, search, read). 0-1 tool calls.");
            lines.Add("- Medium = multi-step (create/modify one block). 2-6 tool calls.");
            lines.Add("- Complex = multi-subtask (multiple blocks, dependencies). 7+ tool calls.");
            lines.Add("- If the request is ambiguous, add clarification questions.");
            lines.Add("- If critical info is missing (block name, type, specs), list it in missingInfo.");
            lines.Add("- Default to Medium if unsure.");
            lines.Add("");
            lines.Add("JSON response:");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Parse the LLM's JSON response into a StructuredIntent.
        /// Falls back gracefully on any parse failure.
        /// </summary>
        private StructuredIntent ParseStructuredIntent(string response, string userPrompt)
        {
            if (string.IsNullOrWhiteSpace(response))
                return CreateFallbackIntent(userPrompt);

            try
            {
                // Strip markdown fences if present
                var json = response.Trim();
                if (json.StartsWith("```"))
                {
                    var endIdx = json.IndexOf('\n');
                    if (endIdx > 0)
                        json = json.Substring(endIdx + 1);
                    if (json.EndsWith("```"))
                        json = json.Substring(0, json.Length - 3);
                    json = json.Trim();
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var intent = new StructuredIntent();

                if (root.TryGetProperty("intentType", out var it))
                    intent.IntentType = it.GetString() ?? "Chat";
                else
                    intent.IntentType = "Chat";

                if (root.TryGetProperty("complexity", out var cx))
                {
                    var cxStr = cx.GetString();
                    intent.Complexity = cxStr switch
                    {
                        "Simple" => TaskComplexity.Simple,
                        "Complex" => TaskComplexity.Complex,
                        _ => TaskComplexity.Medium
                    };
                }

                if (root.TryGetProperty("summary", out var sm))
                    intent.Summary = sm.GetString() ?? userPrompt;

                if (root.TryGetProperty("missingInfo", out var mi) && mi.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in mi.EnumerateArray())
                        intent.MissingInfo.Add(item.GetString());
                }

                if (root.TryGetProperty("clarificationQuestions", out var cq) && cq.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cq.EnumerateArray())
                        intent.ClarificationQuestions.Add(item.GetString());
                }

                if (root.TryGetProperty("estimatedToolCalls", out var etc) && etc.TryGetInt32(out var tc))
                    intent.Effort.EstimatedToolCalls = tc;

                if (root.TryGetProperty("estimatedTokens", out var etk) && etk.TryGetInt32(out var tk))
                    intent.Effort.EstimatedTokens = tk;

                return intent;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[IntentUnderstander] Failed to parse response: {ex.Message}");
                return CreateFallbackIntent(userPrompt);
            }
        }

        /// <summary>
        /// Fallback: simple keyword-based classification when LLM is unavailable.
        /// </summary>
        private StructuredIntent CreateFallbackIntent(string userPrompt)
        {
            var prompt = (userPrompt ?? "").ToLowerInvariant();
            var intentType = "Chat";
            var complexity = TaskComplexity.Medium;

            // Simple keyword matching (matching IntentClassifier behavior)
            if (prompt.Contains("compile") || prompt.Contains("build") || prompt.Contains("编译"))
                intentType = "Compile";
            else if (prompt.Contains("generate") || prompt.Contains("create") || prompt.Contains("生成") || prompt.Contains("创建"))
                intentType = "GenerateCode";
            else if (prompt.Contains("modify") || prompt.Contains("fix") || prompt.Contains("修改") || prompt.Contains("修复"))
                intentType = "ModifyCode";
            else if (prompt.Contains("explain") || prompt.Contains("解释") || prompt.Contains("说明"))
                intentType = "ExplainCode";
            else if (prompt.Contains("diagnose") || prompt.Contains("诊断") || prompt.Contains("health"))
                intentType = "Diagnose";
            else if (prompt.Contains("trace") || prompt.Contains("追踪") || prompt.Contains("where"))
                intentType = "TraceVariable";

            // Complexity heuristic
            if (prompt.Contains(" and ") || prompt.Contains("then") || prompt.Contains("然后") || prompt.Contains("之后"))
                complexity = TaskComplexity.Complex;
            else if (prompt.Split(' ').Length < 8)
                complexity = TaskComplexity.Simple;

            return new StructuredIntent
            {
                IntentType = intentType,
                Complexity = complexity,
                Summary = userPrompt,
                Effort = new EstimatedEffort { EstimatedToolCalls = 3, EstimatedTokens = 3000 }
            };
        }
    }
}
