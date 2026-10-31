using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Deep intent understanding result — goes beyond the 6-label classification
    /// to understand complexity, missing info, and clarification needs.
    /// </summary>
    public class StructuredIntent
    {
        /// <summary>Intent classification label.</summary>
        public string IntentType { get; set; }

        /// <summary>Simple | Medium | Complex</summary>
        public TaskComplexity Complexity { get; set; }

        /// <summary>LLM's one-sentence summary of what the user wants.</summary>
        public string Summary { get; set; }

        /// <summary>Information the user didn't provide that would help.</summary>
        public List<string> MissingInfo { get; set; } = new List<string>();

        /// <summary>Questions to ask the user before proceeding.</summary>
        public List<string> ClarificationQuestions { get; set; } = new List<string>();

        /// <summary>Estimated effort (tool calls, tokens).</summary>
        public EstimatedEffort Effort { get; set; } = new EstimatedEffort();
    }

    public enum TaskComplexity
    {
        /// <summary>Single-step: "explain this code", "search for X"</summary>
        Simple,

        /// <summary>Multi-step: "create a simple FB", "modify a block"</summary>
        Medium,

        /// <summary>Multi-subtask: "create 3 inter-dependent FBs + DB + HMI"</summary>
        Complex
    }

    public class EstimatedEffort
    {
        public int EstimatedToolCalls { get; set; }
        public int EstimatedTokens { get; set; }
    }

    /// <summary>
    /// LLM-generated execution plan with steps, dependencies, and risk assessment.
    /// </summary>
    public class ExecutionPlan
    {
        public string PlanId { get; set; }
        public string Summary { get; set; }
        public TaskComplexity Complexity { get; set; }
        public List<ExecutionPlanStep> Steps { get; set; } = new List<ExecutionPlanStep>();
        public RiskAssessment Risk { get; set; } = new RiskAssessment();
        public List<string> MissingInfo { get; set; } = new List<string>();
        public List<string> ClarificationQuestions { get; set; } = new List<string>();
    }

    public class ExecutionPlanStep
    {
        public string Id { get; set; }
        public string Phase { get; set; }   // explore | read | generate | write | verify
        public string Description { get; set; }
        public string ToolName { get; set; }
        public string ExpectedOutput { get; set; }
        public List<string> DependsOn { get; set; } = new List<string>();
    }

    public class RiskAssessment
    {
        public string Level { get; set; }   // low | medium | high
        public List<string> Concerns { get; set; } = new List<string>();
    }

    /// <summary>
    /// Deep intent understanding — calls LLM to produce a StructuredIntent.
    /// </summary>
    public interface IIntentUnderstander
    {
        Task<StructuredIntent> UnderstandAsync(
            string userPrompt,
            string projectContext = "",
            List<Dictionary<string, string>> history = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Generates structured ExecutionPlans from a user prompt and project context.
    /// </summary>
    public interface IAgentPlanner
    {
        Task<ExecutionPlan> CreatePlanAsync(
            string userPrompt,
            string projectContext = "",
            StructuredIntent intent = null,
            CancellationToken ct = default);
    }
}
