using System.Collections.Generic;
using System;

namespace TIAOpennessStudio.Services.Abstractions
{
    // ── Deviation types ──────────────────────────────────────────────

    public enum DeviationType
    {
        /// <summary>LLM called a tool not listed in any plan step.</summary>
        UnexpectedTool,

        /// <summary>A plan step was skipped (step 3 completed but step 2 wasn't).</summary>
        StepSkipped,

        /// <summary>A step was executed more than once.</summary>
        DuplicateStep
    }

    // ── Match result ─────────────────────────────────────────────────

    /// <summary>
    /// Result of matching a tool call to a plan step. Returned by OnToolCalled
    /// so the ReAct loop can emit progress events to the UI.
    /// </summary>
    public class PlanStepMatch
    {
        /// <summary>1-based index of the matched step within the plan.</summary>
        public int StepIndex { get; set; }

        /// <summary>Total steps in the plan.</summary>
        public int TotalSteps { get; set; }

        /// <summary>Description of the matched step.</summary>
        public string Description { get; set; }

        /// <summary>The tool name that triggered this match.</summary>
        public string ToolName { get; set; }

        /// <summary>Whether this is the first time this step was matched.</summary>
        public bool IsNewMatch { get; set; }
    }

    // ── Completion report ────────────────────────────────────────────

    /// <summary>
    /// Summary of plan execution after the ReAct loop completes.
    /// Contains counts of completed/skipped/failed steps and any deviations.
    /// </summary>
    public class PlanCompletionReport
    {
        public int TotalSteps { get; set; }
        public int CompletedSteps { get; set; }
        public int SkippedSteps { get; set; }
        public int FailedSteps { get; set; }
        public List<string> Deviations { get; set; } = new List<string>();

        /// <summary>Human-readable one-line summary. e.g. "✅ 4/5 steps completed, 1 skipped"</summary>
        public string Summary
        {
            get
            {
                if (TotalSteps == 0) return "";

                var parts = new List<string>();
                parts.Add($"{CompletedSteps}/{TotalSteps} steps completed");

                if (SkippedSteps > 0)
                    parts.Add($"{SkippedSteps} skipped");
                if (FailedSteps > 0)
                    parts.Add($"{FailedSteps} failed");
                if (Deviations.Count > 0)
                    parts.Add($"{Deviations.Count} deviation(s)");

                var prefix = CompletedSteps == TotalSteps ? "✅" : "⚠️";
                return $"{prefix} {string.Join(", ", parts)}";
            }
        }
    }

    // ── PlanTracker interface ────────────────────────────────────────

    /// <summary>
    /// Tracks ReAct loop progress against an ExecutionPlan.
    ///
    /// PlanTracker is a pure observer — it matches tool calls to plan steps
    /// using heuristic rules (tool name + phase + order), detects deviations,
    /// and produces a completion report. It does NOT intercept, block, or
    /// modify any tool calls. The LLM retains full autonomy.
    ///
    /// Controlled by AgentConfig.EnablePlanTracking (default false).
    /// </summary>
    public interface IPlanTracker
    {
        /// <summary>
        /// Called once at the start of a ReAct loop. Pass null or empty plan
        /// to make all subsequent calls no-ops.
        /// </summary>
        void Initialize(ExecutionPlan plan);

        /// <summary>
        /// Called after each tool call in the ReAct loop. Matches the tool
        /// to the best-fitting plan step using heuristic rules.
        /// Returns null if no plan is active or no match was found.
        /// </summary>
        PlanStepMatch OnToolCalled(string toolName, string arguments, bool success);

        /// <summary>
        /// Called when the ReAct loop ends (Completed / Error / Cancelled / MaxIterations).
        /// Returns a completion report or null if no plan was active.
        /// </summary>
        PlanCompletionReport GetCompletionReport();
    }
}
