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
    /// Phase 4: LLM-generated execution plans.
    /// Takes a user prompt + project context + optional StructuredIntent and produces
    /// an ExecutionPlan with steps, dependencies, and risk assessment.
    ///
    /// When Planning is disabled or LLM fails, falls back to the existing
    /// AgentOrchestrator.BuildPlanAsync (fixed 3-4 step template).
    /// </summary>
    public class AgentPlanner : IAgentPlanner
    {
        private readonly IAIEngineService _aiService;
        private readonly ILocalLlmService _localLlmService;

        public AgentPlanner(IAIEngineService aiService, ILocalLlmService localLlmService = null)
        {
            _aiService = aiService;
            _localLlmService = localLlmService;
        }

        public async Task<ExecutionPlan> CreatePlanAsync(
            string userPrompt,
            string projectContext = "",
            StructuredIntent intent = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                return new ExecutionPlan
                {
                    PlanId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Summary = "Empty prompt",
                    Complexity = TaskComplexity.Simple
                };
            }

            try
            {
                var response = await CallLlmForPlanningAsync(userPrompt, projectContext, intent, ct);
                var plan = ParseExecutionPlan(response, userPrompt);

                // If LLM gave too few steps, generate sensible defaults
                if (plan.Steps.Count == 0)
                {
                    plan.Steps = GenerateDefaultSteps(intent, userPrompt);
                }

                return plan;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPlanner] LLM call failed: {ex.Message}, using default steps");
                return CreateFallbackPlan(intent, userPrompt);
            }
        }

        private async Task<string> CallLlmForPlanningAsync(
            string userPrompt, string projectContext, StructuredIntent intent, CancellationToken ct)
        {
            var prompt = BuildPlanningPrompt(userPrompt, projectContext, intent);
            var isLocal = ChatModelRouting.IsLocalModelSelected(AIConfig.Load(), _localLlmService?.CurrentModel);
            if (isLocal && _localLlmService != null)
                return await _localLlmService.InvokeAsync(prompt, OllamaTaskKind.Completion, ct: ct);
            return await _aiService.GenerateSclAsync(prompt);
        }

        private string BuildPlanningPrompt(string userPrompt, string projectContext, StructuredIntent intent)
        {
            var lines = new List<string>
            {
                "You are a TIA Portal task planner. Generate a structured execution plan.",
                "",
                "## User Request",
                userPrompt,
                ""
            };

            if (intent != null)
            {
                lines.Add("## Intent Analysis");
                lines.Add($"Type: {intent.IntentType}");
                lines.Add($"Complexity: {intent.Complexity}");
                if (!string.IsNullOrWhiteSpace(intent.Summary))
                    lines.Add($"Summary: {intent.Summary}");
                lines.Add("");
            }

            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                // Only include first 2000 chars of context to keep prompt small
                var ctx = projectContext.Length > 2000 ? projectContext.Substring(0, 2000) + "..." : projectContext;
                lines.Add("## Project Context");
                lines.Add(ctx);
                lines.Add("");
            }

            lines.Add("## Your Task");
            lines.Add("Output ONLY valid JSON (no markdown fences, no explanation):");
            lines.Add("{");
            lines.Add("  \"summary\": \"One-line plan summary\",");
            lines.Add("  \"complexity\": \"Simple|Medium|Complex\",");
            lines.Add("  \"steps\": [");
            lines.Add("    {");
            lines.Add("      \"id\": \"step-1\",");
            lines.Add("      \"phase\": \"explore|read|generate|write|verify\",");
            lines.Add("      \"description\": \"Human-readable description\",");
            lines.Add("      \"toolName\": \"tool_name or null if no tool needed\",");
            lines.Add("      \"expectedOutput\": \"What this step should produce\",");
            lines.Add("      \"dependsOn\": []");
            lines.Add("    }");
            lines.Add("  ],");
            lines.Add("  \"risk\": {");
            lines.Add("    \"level\": \"low|medium|high\",");
            lines.Add("    \"concerns\": [\"potential issues\"]");
            lines.Add("  }");
            lines.Add("}");
            lines.Add("");
            lines.Add("## Rules");
            lines.Add("- Simple task: 1-2 steps (explain → answer, or search → report)");
            lines.Add("- Medium task: 3-5 steps (explore → generate → write → verify)");
            lines.Add("- Complex task: 5-10 steps with dependencies");
            lines.Add("- Always include a verify/compile step after any write step");
            lines.Add("- Phase values: explore, read, generate, write, verify");
            lines.Add("- Available tools: get_index_status, refresh_project_index, search_project, query_variable_references, export_block, import_block_code, compile,");
            lines.Add("  plc_build_and_import, plc_build_fb, plc_build_fc, plc_build_udt,");
            lines.Add("  plc_build_global_db, plc_build_tag_table, resolve_xml_fragment");
            lines.Add("- If the cache may be stale or missing, first use get_index_status and optionally refresh_project_index");
            lines.Add("- For variable usage, where-used, reference chain, or impact analysis, prefer query_variable_references over search_project");
            lines.Add("- For steps that just need AI thinking (no TIA tool), use toolName: null");
            lines.Add("");
            lines.Add("JSON response:");

            return string.Join("\n", lines);
        }

        private ExecutionPlan ParseExecutionPlan(string response, string userPrompt)
        {
            if (string.IsNullOrWhiteSpace(response))
                return CreateFallbackPlan(null, userPrompt);

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

                var plan = new ExecutionPlan
                {
                    PlanId = Guid.NewGuid().ToString("N").Substring(0, 8)
                };

                if (root.TryGetProperty("summary", out var sm))
                    plan.Summary = sm.GetString() ?? userPrompt;

                if (root.TryGetProperty("complexity", out var cx))
                {
                    plan.Complexity = (cx.GetString() ?? "").ToLowerInvariant() switch
                    {
                        "simple" => TaskComplexity.Simple,
                        "complex" => TaskComplexity.Complex,
                        _ => TaskComplexity.Medium
                    };
                }

                if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    int idx = 1;
                    foreach (var stepEl in steps.EnumerateArray())
                    {
                        var step = new ExecutionPlanStep
                        {
                            Id = $"step-{idx}"
                        };

                        if (stepEl.TryGetProperty("id", out var idProp))
                            step.Id = idProp.GetString() ?? step.Id;
                        if (stepEl.TryGetProperty("phase", out var phProp))
                            step.Phase = phProp.GetString() ?? "";
                        if (stepEl.TryGetProperty("description", out var descProp))
                            step.Description = descProp.GetString() ?? "";
                        if (stepEl.TryGetProperty("toolName", out var tnProp))
                            step.ToolName = tnProp.ValueKind == JsonValueKind.Null ? null : tnProp.GetString();
                        if (stepEl.TryGetProperty("expectedOutput", out var eoProp))
                            step.ExpectedOutput = eoProp.GetString() ?? "";
                        if (stepEl.TryGetProperty("dependsOn", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var dep in depsProp.EnumerateArray())
                                step.DependsOn.Add(dep.GetString());
                        }

                        plan.Steps.Add(step);
                        idx++;
                    }
                }

                if (root.TryGetProperty("risk", out var riskEl))
                {
                    if (riskEl.TryGetProperty("level", out var rl))
                        plan.Risk.Level = rl.GetString() ?? "low";
                    if (riskEl.TryGetProperty("concerns", out var rc) && rc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var concern in rc.EnumerateArray())
                            plan.Risk.Concerns.Add(concern.GetString());
                    }
                }

                return plan;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AgentPlanner] Failed to parse response: {ex.Message}");
                return CreateFallbackPlan(null, userPrompt);
            }
        }

        /// <summary>
        /// Generate sensible default steps based on intent type.
        /// Used as fallback when LLM planning fails.
        /// </summary>
        private List<ExecutionPlanStep> GenerateDefaultSteps(StructuredIntent intent, string userPrompt)
        {
            var steps = new List<ExecutionPlanStep>();
            var intentType = intent?.IntentType ?? "Chat";

            switch (intentType)
            {
                case "GenerateCode":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "explore", Description = "Search project for similar blocks", ToolName = "search_project" });
                    steps.Add(new ExecutionPlanStep { Id = "step-2", Phase = "generate", Description = "Generate code based on requirements", ToolName = null });
                    steps.Add(new ExecutionPlanStep { Id = "step-3", Phase = "write", Description = "Write generated code to TIA Portal", ToolName = "import_block_code" });
                    steps.Add(new ExecutionPlanStep { Id = "step-4", Phase = "verify", Description = "Compile and verify", ToolName = "compile", DependsOn = new List<string> { "step-3" } });
                    break;

                case "ModifyCode":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "read", Description = "Export existing block code", ToolName = "export_block" });
                    steps.Add(new ExecutionPlanStep { Id = "step-2", Phase = "generate", Description = "Modify code based on requirements", ToolName = null });
                    steps.Add(new ExecutionPlanStep { Id = "step-3", Phase = "write", Description = "Write modified code to TIA Portal", ToolName = "import_block_code", DependsOn = new List<string> { "step-1" } });
                    steps.Add(new ExecutionPlanStep { Id = "step-4", Phase = "verify", Description = "Compile and verify", ToolName = "compile", DependsOn = new List<string> { "step-3" } });
                    break;

                case "Compile":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "verify", Description = "Compile the project", ToolName = "compile" });
                    break;

                case "ExplainCode":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "read", Description = "Export block code for analysis", ToolName = "export_block" });
                    steps.Add(new ExecutionPlanStep { Id = "step-2", Phase = "generate", Description = "Analyze and explain the code", ToolName = null });
                    break;

                case "Diagnose":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "explore", Description = "Search project for issues", ToolName = "search_project" });
                    steps.Add(new ExecutionPlanStep { Id = "step-2", Phase = "read", Description = "Export suspicious blocks", ToolName = "export_block" });
                    steps.Add(new ExecutionPlanStep { Id = "step-3", Phase = "generate", Description = "Analyze and diagnose issues", ToolName = null });
                    break;

                case "TraceVariable":
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "explore", Description = "Query structured variable references from the PLC knowledge index", ToolName = "query_variable_references" });
                    steps.Add(new ExecutionPlanStep { Id = "step-2", Phase = "generate", Description = "Summarize the variable reference chain and likely impact scope", ToolName = null, DependsOn = new List<string> { "step-1" } });
                    break;

                default: // Chat / TraceVariable
                    steps.Add(new ExecutionPlanStep { Id = "step-1", Phase = "generate", Description = "Process user request", ToolName = null });
                    break;
            }

            return steps;
        }

        private ExecutionPlan CreateFallbackPlan(StructuredIntent intent, string userPrompt)
        {
            return new ExecutionPlan
            {
                PlanId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Summary = intent?.Summary ?? userPrompt,
                Complexity = intent?.Complexity ?? TaskComplexity.Medium,
                Steps = GenerateDefaultSteps(intent, userPrompt),
                Risk = new RiskAssessment { Level = "low" }
            };
        }
    }
}
