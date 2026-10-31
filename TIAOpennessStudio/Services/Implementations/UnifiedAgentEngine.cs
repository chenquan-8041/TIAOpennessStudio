using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Phase 7: Unified agent engine that routes ALL intents through the ReAct loop.
    ///
    /// Key design decisions:
    ///   - Intent-based system prompt prefix: guides the LLM toward the right behavior
    ///     without hard-coded switch-case steps
    ///   - Phase 4 Planning integration: when an ExecutionPlan is provided, its steps
    ///     are injected into the system prompt as a structured execution guide,
    ///     steering the ReAct loop while preserving its dynamic decision-making
    ///   - Delegates execution to ReActLoopEngine (which already has all Phase 1-6 features)
    ///   - All write-protection (QualityGate/Snapshot/CompileFix) is handled by ReActLoopEngine
    ///     interceptors (Phase 2)
    ///   - Reflection (Phase 5) and compression (Phase 6) are transparently handled by ReActLoopEngine
    ///
    /// The old AgentOrchestrator.BuildPlanAsync + ExecutePlanAsync (switch-case ~400 lines)
    /// are preserved but no longer called when UseUnifiedEngine=true.
    /// </summary>
    public class UnifiedAgentEngine : IUnifiedAgentEngine
    {
        private readonly IReActLoopEngine _reactEngine;
        private readonly ICloudAgentEngine _cloudAgentEngine;
        private readonly AgentConfig _agentConfig;

        public UnifiedAgentEngine(IReActLoopEngine reactEngine, ICloudAgentEngine cloudAgentEngine, AgentConfig agentConfig)
        {
            _reactEngine = reactEngine ?? throw new ArgumentNullException(nameof(reactEngine));
            _cloudAgentEngine = cloudAgentEngine ?? throw new ArgumentNullException(nameof(cloudAgentEngine));
            _agentConfig = agentConfig ?? AgentConfig.CreateDefault();
        }

        /// <inheritdoc />
        public async Task<UnifiedAgentResult> ExecuteAsync(
            string prompt,
            AgentIntent intent,
            string context,
            List<Dictionary<string, string>> history,
            Action<AgentStreamEvent> onAgentEvent,
            Func<string, string, Task<bool>> confirmCallback,
            CancellationToken ct,
            ExecutionPlan plan = null,
            StructuredIntent structuredIntent = null,
            string planningKnowledge = "",
            string projectKey = "")
        {
            if (_reactEngine == null)
                return UnifiedAgentResult.Error("ReAct loop engine not available.");

            // ── Build unified prompt with intent guidance + plan injection ──
            var unifiedPrompt = BuildUnifiedPrompt(prompt, intent, plan, structuredIntent);

            // ── Emit a status event so the user knows which mode is active ──
            var modeDescription = plan != null && plan.Steps.Count > 0
                ? $"\n🧠 Unified Engine — {intent} mode ({plan.Complexity}, {plan.Steps.Count} steps)\n"
                : $"\n🧠 Unified Engine — {intent} mode\n";
            onAgentEvent?.Invoke(AgentStreamEvent.Thinking(modeDescription));

            // ── Routing ──
            // HARD RULE: Ollama (local models) MUST ALWAYS run on the C# ReActLoopEngine
            // local loop, regardless of UseChatPlanEndpoint / UseCloudAgent.
            //   - Ollama has no native function calling and is incompatible with the
            //     langchain-openai streaming parser used by the Python graphs.
            //   - Without tool_calls a Python agent loop can spin forever (this was seen
            //     with the since-removed planning graph), and the Python backend now rejects
            //     local endpoints fail-closed. So Ollama never goes to Python.
            //   - Tools still work: the C# local loop executes them in-process via
            //     ToolRegistry (direct TIA Openness), no 8001 round-trip needed.
            // The 8001 reverse endpoint stays available for CLOUD models on the Python path.
            // Forward the recognised intent to the Python supervisor. Without it the backend
            // received "" and routed every cloud task (including ModifyCode) to the chat agent.
            // Unknown has no Python route, so it keeps the previous behaviour ("" -> Chat).
            string pythonIntent = intent == AgentIntent.Unknown ? "" : intent.ToString();

            ReActResult result;
            if (IsOllamaModel())
            {
                onAgentEvent?.Invoke(AgentStreamEvent.Thinking("🦙 Ollama model detected, using C# ReActLoopEngine local loop (Python path bypassed)..."));
                result = await _reactEngine.ExecuteAsync(
                    unifiedPrompt, context, history, onAgentEvent, confirmCallback, ct, plan: plan);
            }
            // ── Chat Plan path: Python multi-agent supervisor via /chat_plan. Cloud only. ──
            else if (_agentConfig.UseChatPlanEndpoint)
            {
                onAgentEvent?.Invoke(AgentStreamEvent.Thinking("📋 Chat Plan mode (Python multi-agent supervisor)..."));
                result = await _cloudAgentEngine.ExecuteChatPlanAsync(
                    unifiedPrompt, context, history, onAgentEvent, ct, planningKnowledge, intent: pythonIntent, projectKey: projectKey);
            }
            // ── Cloud path: Python LangGraph Agent (function calling), C# forwards SSE ──
            else if (_agentConfig.UseCloudAgent)
            {
                onAgentEvent?.Invoke(AgentStreamEvent.Thinking("☁️ Cloud Agent mode (Python LangGraph)..."));
                result = await _cloudAgentEngine.ExecuteCloudAsync(
                    unifiedPrompt, context, history, onAgentEvent, ct, planningKnowledge, projectKey, pythonIntent);
            }
            else
            {
                result = await _reactEngine.ExecuteAsync(
                    unifiedPrompt, context, history, onAgentEvent, confirmCallback, ct, plan: plan);
            }

            // ── Map ReActResult → UnifiedAgentResult ──
            // PlanCompletion is only populated by the local C# ReAct loop; cloud/chat-plan
            // paths run tools inside Python and leave it null.
            var planReport = result.PlanCompletion;
            return result.Status switch
            {
                ReActStatus.Completed => UnifiedAgentResult.Completed(
                    result.FinalAnswer ?? "", result.TotalIterations, 0, planReport),
                ReActStatus.Cancelled => UnifiedAgentResult.Cancelled(planReport),
                ReActStatus.Error => UnifiedAgentResult.Error(result.Message ?? "Unknown error", planReport),
                ReActStatus.MaxIterationsReached => UnifiedAgentResult.MaxIterations(
                    result.FinalAnswer ?? "", result.TotalIterations, 0, planReport),
                _ => UnifiedAgentResult.Error($"Unexpected ReAct status: {result.Status}")
            };
        }

        /// <summary>
        /// Build the full prompt including intent prefix and optional LLM execution plan.
        ///
        /// When a plan is available, its steps are injected as numbered execution guidance.
        /// This steers the ReAct loop without hard-coding it — the LLM can still adapt
        /// if observations contradict the plan.
        /// </summary>
        private static string BuildUnifiedPrompt(
            string prompt, AgentIntent intent, ExecutionPlan plan, StructuredIntent structuredIntent)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"You are a TIA Portal engineering assistant. Intent: {intent}.");
            if (plan != null && plan.Steps.Count > 0)
            {
                sb.AppendLine("Execution Plan:");
                foreach (var step in plan.Steps)
                    sb.AppendLine($"  - [{step.Phase}] {step.Description}");
            }
            sb.AppendLine();
            sb.AppendLine($"User Request: {prompt}");
            return sb.ToString();
        }

        /// <summary>
        /// Check if the current model is Ollama (local model, no function calling).
        /// Detected by IReActLoopEngine.ModelProvider == "ollama" (not by sniffing the model name).
        /// </summary>
        private bool IsOllamaModel()
        {
            try
            {
                return _reactEngine?.ModelProvider == "ollama";
            }
            catch { return false; }
        }

        /// <summary>
        /// Fallback guidance when no LLM plan is available.
        /// </summary>
        private static string GetIntentGuidance(AgentIntent intent)
        {
            return intent switch
            {
                AgentIntent.ExplainCode =>
                    "## Task\n" +
                    "Read the relevant block(s) using export_block, " +
                    "then provide a detailed explanation of the logic, potential issues, and optimization tips.\n\n",

                AgentIntent.ModifyCode =>
                    "## Task\n" +
                    "First export the existing block using export_block, " +
                    "then modify the SCL code based on the user's requirements. " +
                    "Maintain consistency with existing naming conventions. " +
                    "Write the modified code back using import_block_code.\n\n",

                AgentIntent.GenerateCode =>
                    "## Task\n" +
                    "First search the project to understand existing blocks " +
                    "and naming conventions using search_project. Then generate efficient and robust SCL code. " +
                    "After generating, write the code to the project using import_block_code, " +
                    "then compile to verify using compile.\n\n",

                AgentIntent.GenerateAndFix =>
                    "## Task\n" +
                    "Generate SCL code, write it to the project, " +
                    "compile, and if compilation fails, analyze the errors and fix the code automatically. " +
                    "Keep iterating until compilation succeeds or you're confident the code is correct.\n\n",

                AgentIntent.Compile =>
                    "## Task\n" +
                    "Compile the project using the compile tool, then analyze the results. " +
                    "If there are errors, explain them and suggest fixes.\n\n",

                AgentIntent.Diagnose =>
                    "## Task\n" +
                    "Export the relevant block(s), " +
                    "perform a comprehensive diagnostic check on the SCL code. " +
                    "Check for: type mismatches, unused variables, potential infinite loops, " +
                    "missing error handling, and Siemens best-practice violations.\n\n",

                AgentIntent.Chat =>
                    "## Task\n" +
                    "Assist the user with their request regarding TIA Portal and SCL programming. " +
                    "Use available tools to explore the project and provide accurate, context-aware answers.\n\n",

                _ => ""
            };
        }
    }
}
