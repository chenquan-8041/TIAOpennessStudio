using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Phase 7: Unified agent engine that routes ALL intents through the ReAct loop,
    /// replacing the legacy static-plan switch-case execution.
    ///
    /// This engine replaces AgentOrchestrator.BuildPlanAsync + ExecutePlanAsync when
    /// AgentConfig.UseUnifiedEngine is true.
    /// </summary>
    public interface IUnifiedAgentEngine
    {
        /// <summary>
        /// Execute a user request through the unified ReAct pipeline.
        /// </summary>
        /// <param name="prompt">User's natural language request.</param>
        /// <param name="intent">Pre-classified intent (for system prompt selection).</param>
        /// <param name="context">Project context (block tree, references, etc.).</param>
        /// <param name="history">Conversation history for multi-turn context.</param>
        /// <param name="onAgentEvent">Callback for streaming events (Content, Thinking, ToolStart, etc.).</param>
        /// <param name="confirmCallback">Optional callback to request user confirmation before write operations.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Execution result with status and final answer.</returns>
        Task<UnifiedAgentResult> ExecuteAsync(
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
            string projectKey = "");
    }

    /// <summary>
    /// Result of a unified agent execution.
    /// </summary>
    public class UnifiedAgentResult
    {
        /// <summary>Execution status.</summary>
        public ReActStatus Status { get; set; }

        /// <summary>Final answer text from the agent.</summary>
        public string FinalAnswer { get; set; }

        /// <summary>Error message if Status is Error.</summary>
        public string Message { get; set; }

        /// <summary>Number of ReAct iterations executed.</summary>
        public int Iterations { get; set; }

        /// <summary>Total tokens consumed.</summary>
        public int TotalTokens { get; set; }

        /// <summary>Plan completion report (non-null when PlanTracking is enabled).</summary>
        public PlanCompletionReport PlanCompletion { get; set; }

        public static UnifiedAgentResult Completed(string answer, int iterations, int tokens, PlanCompletionReport planReport = null) =>
            new UnifiedAgentResult { Status = ReActStatus.Completed, FinalAnswer = answer, Iterations = iterations, TotalTokens = tokens, PlanCompletion = planReport };

        public static UnifiedAgentResult Error(string message, PlanCompletionReport planReport = null) =>
            new UnifiedAgentResult { Status = ReActStatus.Error, Message = message, PlanCompletion = planReport };

        public static UnifiedAgentResult Cancelled(PlanCompletionReport planReport = null) =>
            new UnifiedAgentResult { Status = ReActStatus.Cancelled, PlanCompletion = planReport };

        public static UnifiedAgentResult MaxIterations(string answer, int iterations, int tokens, PlanCompletionReport planReport = null) =>
            new UnifiedAgentResult { Status = ReActStatus.MaxIterationsReached, FinalAnswer = answer, Iterations = iterations, TotalTokens = tokens, PlanCompletion = planReport };
    }
}
