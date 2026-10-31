using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;
using TIAOpennessStudio.Services.Abstractions;
namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Phase 5: Analyzes tool failures using LLM + rule-based fallback.
    ///
    /// When a tool fails in the ReAct loop, the reflector:
    ///   1. Asks the LLM to diagnose the error and suggest an adjusted approach
    ///   2. Falls back to rule-based classification if LLM is unavailable
    ///   3. Categorizes errors for duplicate detection (same error twice → stop)
    ///
    /// The reflection result is injected into conversation history to guide
    /// the LLM on the next iteration.
    /// </summary>
    public class AgentReflector : IAgentReflector
    {
        private readonly IAIEngineService _aiService;
        private readonly ILocalLlmService _localLlmService;

        public AgentReflector(IAIEngineService aiService, ILocalLlmService localLlmService = null)
        {
            _aiService = aiService;
            _localLlmService = localLlmService;
        }

        public async Task<ReflectionResult> ReflectAsync(
            string toolName,
            string error,
            string userPrompt,
            List<ToolCallHistoryEntry> history = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return ReflectionResult.Retry(
                    "No error message to analyze — retrying with same approach.",
                    "Retry the same tool call with the same parameters.",
                    "no_error");
            }

            // Try LLM-based analysis first
            try
            {
                var response = await CallLlmForReflectionAsync(toolName, error, userPrompt, history, ct);
                var result = ParseReflectionResponse(response, error);
                if (result != null)
                    return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentReflector] LLM reflection failed: {ex.Message}, using rule-based fallback");
            }

            // Rule-based fallback
            return RuleBasedReflect(toolName, error, history);
        }

        /// <summary>
        /// Call LLM to analyze the tool failure and suggest adjustments.
        /// </summary>
        private async Task<string> CallLlmForReflectionAsync(
            string toolName, string error, string userPrompt,
            List<ToolCallHistoryEntry> history, CancellationToken ct)
        {
            var prompt = BuildReflectionPrompt(toolName, error, userPrompt, history);
            var isLocal = ChatModelRouting.IsLocalModelSelected(AIConfig.Load(), _localLlmService?.CurrentModel);
            if (isLocal && _localLlmService != null)
                return await _localLlmService.InvokeAsync(prompt, OllamaTaskKind.Completion, ct: ct);
            return await _aiService.GenerateSclAsync(prompt);
        }

        /// <summary>
        /// Build the reflection prompt for the LLM.
        /// </summary>
        private string BuildReflectionPrompt(
            string toolName, string error, string userPrompt,
            List<ToolCallHistoryEntry> history)
        {
            var lines = new List<string>
            {
                "You are a TIA Portal automation expert debugging a tool execution failure.",
                "Analyze the error and determine the best next step.",
                "",
                "## User's Original Task",
                userPrompt,
                "",
                "## Failed Tool",
                $"Tool: {toolName}",
                $"Error: {error}",
                ""
            };

            if (history != null && history.Count > 0)
            {
                lines.Add("## Recent Tool History");
                foreach (var call in history.Skip(Math.Max(0, history.Count - 5)))
                {
                    var summary = (call.Result ?? "");
                    if (summary.Length > 100) summary = summary.Substring(0, 100);
                    lines.Add($"- [{call.ToolName}] {(call.Success ? "SUCCESS" : "FAILED")}: {summary}");
                }
                lines.Add("");
            }

            lines.Add("## Your Task");
            lines.Add("Output ONLY valid JSON (no markdown fences, no explanation):");
            lines.Add("{");
            lines.Add("  \"analysis\": \"Root cause of the failure\",");
            lines.Add("  \"shouldRetry\": true,");
            lines.Add("  \"shouldStop\": false,");
            lines.Add("  \"suggestedFix\": \"What parameter or approach to change\",");
            lines.Add("  \"adjustedApproach\": \"Guidance for the next LLM iteration\",");
            lines.Add("  \"errorCategory\": \"unrecoverable|transient|blocked|timeout|compile_error|permission\"");
            lines.Add("}");
            lines.Add("");
            lines.Add("## Rules");
            lines.Add("- shouldStop=true only for truly unrecoverable errors (permissions, missing blocks that don't exist)");
            lines.Add("- shouldRetry=true if the error is fixable (wrong parameters, code syntax)");
            lines.Add("- shouldRetry=false and shouldStop=false means try a DIFFERENT approach/tool instead");
            lines.Add("- errorCategory helps detect repeated identical failures");
            lines.Add("- Be concise but specific in adjustedApproach — this goes into the LLM's context");
            lines.Add("");
            lines.Add("JSON response:");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Parse the LLM's reflection JSON response.
        /// Returns null if parsing fails (caller falls back to rule-based).
        /// </summary>
        private ReflectionResult ParseReflectionResponse(string response, string originalError)
        {
            if (string.IsNullOrWhiteSpace(response))
                return null;

            try
            {
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

                var result = new ReflectionResult
                {
                    Analysis = originalError
                };

                if (root.TryGetProperty("analysis", out var a))
                    result.Analysis = a.GetString() ?? originalError;

                if (root.TryGetProperty("shouldRetry", out var sr) && sr.ValueKind == JsonValueKind.True)
                    result.ShouldRetry = true;

                if (root.TryGetProperty("shouldStop", out var ss) && ss.ValueKind == JsonValueKind.True)
                    result.ShouldStop = true;

                if (root.TryGetProperty("suggestedFix", out var sf))
                    result.SuggestedFix = sf.GetString() ?? "";

                if (root.TryGetProperty("adjustedApproach", out var aa))
                    result.AdjustedApproach = aa.GetString() ?? "";

                if (root.TryGetProperty("errorCategory", out var ec))
                    result.ErrorCategory = ec.GetString() ?? "transient";

                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentReflector] Failed to parse LLM response: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Rule-based fallback: categorize errors without LLM.
        /// Used when LLM is unavailable or response parsing fails.
        /// </summary>
        private ReflectionResult RuleBasedReflect(
            string toolName, string error,
            List<ToolCallHistoryEntry> history)
        {
            var errorLower = (error ?? "").ToLowerInvariant();
            var toolLower = (toolName ?? "").ToLowerInvariant();

            // ── Unrecoverable errors → Stop ──
            if (errorLower.Contains("unknown tool") ||
                errorLower.Contains("tool not found") ||
                errorLower.Contains("not registered"))
            {
                return ReflectionResult.Stop($"Unknown tool '{toolName}' — cannot execute. Check available tools.");
            }

            if (errorLower.Contains("cancelled") ||
                errorLower.Contains("user cancelled") ||
                errorLower.Contains("user canceled"))
            {
                return ReflectionResult.Stop("Execution cancelled by user.");
            }

            if (errorLower.Contains("permission") ||
                errorLower.Contains("unauthorized") ||
                errorLower.Contains("access denied"))
            {
                return ReflectionResult.Stop($"Permission denied for '{toolName}'.");
            }

            // ── Quality Gate blocked → Retry with adjusted approach ──
            if (errorLower.Contains("quality gate") || errorLower.Contains("blocked"))
            {
                return ReflectionResult.Retry(
                    $"Quality Gate blocked the code. Fix the violations and retry.",
                    $"The previous code was blocked by Quality Gate. Review the violations listed in the error and generate corrected code that passes all checks.",
                    "blocked");
            }

            // ── Compile errors → Try different approach ──
            if (errorLower.Contains("compile") && (errorLower.Contains("error") || errorLower.Contains("failed")))
            {
                return ReflectionResult.Continue(
                    $"Compile failed. Try a different approach.",
                    $"The compile step failed. Do NOT retry the same approach. Try: (1) export the block to see current state, (2) fix the syntax errors, (3) re-import and compile again.",
                    "compile_error");
            }

            // ── Timeout → Retry once with longer timeout suggestion ──
            if (errorLower.Contains("timeout") || errorLower.Contains("timed out"))
            {
                return ReflectionResult.Retry(
                    $"Tool '{toolName}' timed out. Retry with simpler/smaller parameters.",
                    $"The previous tool call timed out. Reduce the scope of your request — try smaller chunks or a simpler operation.",
                    "timeout");
            }

            // ── Export/read errors → Continue with different approach ──
            if (errorLower.Contains("export") && errorLower.Contains("fail"))
            {
                return ReflectionResult.Continue(
                    $"Export failed. The block may not exist or may be locked.",
                    $"The export/read failed. The block may not exist yet (try creating it) or may be locked. Try search_project to find existing blocks first.",
                    "export_failed");
            }

            // ── Import/write errors → Retry with adjusted code ──
            if (errorLower.Contains("import") && errorLower.Contains("fail"))
            {
                return ReflectionResult.Retry(
                    $"Import failed. Check block name and code format.",
                    $"The import failed. Check: (1) block name matches TIA Portal conventions, (2) code syntax is valid SCL, (3) no duplicate block names exist. Export the existing block first if modifying.",
                    "import_failed");
            }

            // ── Default: transient error → Retry once ──
            return ReflectionResult.Retry(
                $"Tool '{toolName}' failed with: {error}",
                $"The previous call to '{toolName}' failed. Check the error message and adjust your approach. Consider using a different tool if appropriate.",
                "transient");
        }
    }
}
