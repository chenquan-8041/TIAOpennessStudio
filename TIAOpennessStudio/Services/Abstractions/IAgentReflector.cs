using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Minimal tool call history entry for reflection analysis.
    /// Lives in Abstractions to avoid circular dependency with Implementations.
    /// </summary>
    public class ToolCallHistoryEntry
    {
        public string ToolName { get; set; }
        public string Result { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Result of a reflection analysis after a tool failure.
    /// </summary>
    public class ReflectionResult
    {
        /// <summary>Human-readable analysis of what went wrong.</summary>
        public string Analysis { get; set; }

        /// <summary>LLM-suggested fix or adjustment.</summary>
        public string SuggestedFix { get; set; }

        /// <summary>Whether the agent should retry the tool with adjusted parameters.</summary>
        public bool ShouldRetry { get; set; }

        /// <summary>
        /// Whether the agent should stop the entire task.
        /// True when the error is unrecoverable (permissions, missing tools, etc.).
        /// </summary>
        public bool ShouldStop { get; set; }

        /// <summary>
        /// Adjusted approach description — injected into conversation history
        /// to guide the LLM on the next iteration.
        /// </summary>
        public string AdjustedApproach { get; set; }

        /// <summary>Error category for duplicate detection.</summary>
        public string ErrorCategory { get; set; }

        /// <summary>
        /// Create a simple stop result for unrecoverable errors.
        /// </summary>
        public static ReflectionResult Stop(string reason)
        {
            return new ReflectionResult
            {
                Analysis = reason,
                ShouldStop = true,
                ShouldRetry = false,
                ErrorCategory = "unrecoverable"
            };
        }

        /// <summary>
        /// Create a retry result with adjusted approach.
        /// </summary>
        public static ReflectionResult Retry(string analysis, string adjustedApproach, string errorCategory = "transient")
        {
            return new ReflectionResult
            {
                Analysis = analysis,
                ShouldRetry = true,
                ShouldStop = false,
                AdjustedApproach = adjustedApproach,
                ErrorCategory = errorCategory
            };
        }

        /// <summary>
        /// Create a result that tells the agent to continue but not retry the same tool.
        /// </summary>
        public static ReflectionResult Continue(string analysis, string adjustedApproach, string errorCategory = "transient")
        {
            return new ReflectionResult
            {
                Analysis = analysis,
                ShouldRetry = false,
                ShouldStop = false,
                AdjustedApproach = adjustedApproach,
                ErrorCategory = errorCategory
            };
        }
    }

    /// <summary>
    /// Phase 5: Analyzes tool failures and suggests strategy adjustments.
    /// Instead of blindly retrying failed tools (or giving up immediately),
    /// the reflector uses LLM analysis to determine whether to retry with
    /// different parameters, try a different approach, or stop entirely.
    ///
    /// Integrated into ReActEngine.ExecuteAsync — when a tool fails and
    /// EnableReflection is true, the reflector analyzes the failure and
    /// injects guidance into the conversation history for the next iteration.
    /// </summary>
    public interface IAgentReflector
    {
        /// <summary>
        /// Analyze a tool failure and produce a reflection result.
        /// </summary>
        /// <param name="toolName">The tool that failed.</param>
        /// <param name="error">Error message from the tool.</param>
        /// <param name="userPrompt">The original user task.</param>
        /// <param name="history">Recent tool call history (last 5 calls).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<ReflectionResult> ReflectAsync(
            string toolName,
            string error,
            string userPrompt,
            List<ToolCallHistoryEntry> history = null,
            CancellationToken ct = default);
    }
}
