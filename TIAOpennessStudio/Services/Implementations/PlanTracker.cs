using System;
using System.Collections.Generic;
using System.Linq;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Tracks ReAct loop progress against an ExecutionPlan using heuristic matching.
    ///
    /// Matching strategy (best-effort, no LLM):
    ///   1. Walk plan.Steps in order, find first uncompleted step whose Phase
    ///      and ToolName match the current tool call
    ///   2. Phase→tool mapping:
    ///      explore → search_project, get_project_tree
    ///      read    → export_block
    ///      write   → import_block_code, plc_build_and_import, delete_block
    ///      verify  → compile
    ///      generate → matches any tool (LLM thinking step, no specific tool required)
    ///   3. If no exact match, try loose match (Phase only, ignore ToolName)
    ///   4. If still no match, record deviation
    ///   5. Mark matched step as completed
    ///
    /// Pure observer — never intercepts or blocks tool calls.
    /// </summary>
    public class PlanTracker : IPlanTracker
    {
        private ExecutionPlan _plan;
        private readonly Dictionary<string, PlanStepState> _stepStates = new Dictionary<string, PlanStepState>();
        private readonly List<PlanDeviation> _deviations = new List<PlanDeviation>();
        private int _toolCallCount;

        // ── Phase → canonical tool names ─────────────────────────────

        private static readonly HashSet<string> ExploreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "search_project", "query_variable_references", "get_project_tree", "get_index_status", "refresh_project_index"
        };

        private static readonly HashSet<string> ReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "export_block"
        };

        private static readonly HashSet<string> WriteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "import_block_code", "plc_build_and_import", "delete_block"
        };

        private static readonly HashSet<string> VerifyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "compile"
        };

        // ── Public API ────────────────────────────────────────────────

        public void Initialize(ExecutionPlan plan)
        {
            _plan = plan;
            _stepStates.Clear();
            _deviations.Clear();
            _toolCallCount = 0;

            if (plan?.Steps != null)
            {
                foreach (var step in plan.Steps)
                {
                    _stepStates[step.Id] = new PlanStepState
                    {
                        Step = step,
                        Completed = false,
                        MatchCount = 0
                    };
                }
            }
        }

        public PlanStepMatch OnToolCalled(string toolName, string arguments, bool success)
        {
            _toolCallCount++;

            if (_plan == null || _plan.Steps.Count == 0)
                return null;

            // Step 1: Try exact match (Phase + ToolName)
            var match = FindExactMatch(toolName);
            if (match != null)
                return RecordMatch(match, toolName, success);

            // Step 2: Try loose match (Phase only, for steps without a specific ToolName)
            match = FindLooseMatch(toolName);
            if (match != null)
                return RecordMatch(match, toolName, success);

            // Step 3: No match — record deviation
            RecordDeviation(DeviationType.UnexpectedTool,
                $"Unexpected tool '{toolName}' — not in any plan step");

            return null;
        }

        public PlanCompletionReport GetCompletionReport()
        {
            if (_plan == null || _plan.Steps.Count == 0)
                return null;

            var report = new PlanCompletionReport
            {
                TotalSteps = _plan.Steps.Count
            };

            foreach (var kvp in _stepStates)
            {
                if (kvp.Value.Completed)
                    report.CompletedSteps++;
                else if (kvp.Value.MatchCount > 0 && !kvp.Value.Completed)
                    report.FailedSteps++;
            }

            // Detect skipped steps (a later step completed but this one wasn't)
            var stepIds = _plan.Steps.Select(s => s.Id).ToList();
            for (int i = 0; i < stepIds.Count; i++)
            {
                var state = _stepStates[stepIds[i]];
                if (!state.Completed)
                {
                    // Check if any step AFTER this one was completed
                    bool laterStepCompleted = false;
                    for (int j = i + 1; j < stepIds.Count; j++)
                    {
                        if (_stepStates[stepIds[j]].Completed)
                        {
                            laterStepCompleted = true;
                            break;
                        }
                    }
                    if (laterStepCompleted)
                    {
                        report.SkippedSteps++;
                        RecordDeviation(DeviationType.StepSkipped,
                            $"Step '{state.Step.Description}' was skipped");
                    }
                }
            }

            // Copy deviations to report
            foreach (var d in _deviations)
                report.Deviations.Add(d.Message);

            return report;
        }

        // ── Matching logic ────────────────────────────────────────────

        private PlanStepState FindExactMatch(string toolName)
        {
            foreach (var step in _plan.Steps)
            {
                var state = _stepStates[step.Id];

                // Already completed? Check if duplicate
                if (state.Completed)
                {
                    if (ToolMatchesStep(toolName, step))
                    {
                        RecordDeviation(DeviationType.DuplicateStep,
                            $"Tool '{toolName}' matched already-completed step '{step.Description}'");
                    }
                    continue;
                }

                if (ToolMatchesStep(toolName, step))
                    return state;
            }

            return null;
        }

        private PlanStepState FindLooseMatch(string toolName)
        {
            foreach (var step in _plan.Steps)
            {
                var state = _stepStates[step.Id];
                if (state.Completed)
                    continue;

                // Loose match: step has no specific ToolName but the tool
                // fits the step's phase category.
                if (string.IsNullOrWhiteSpace(step.ToolName)
                    && PhaseMatchesTool(step.Phase, toolName))
                {
                    return state;
                }
            }

            return null;
        }

        private PlanStepMatch RecordMatch(PlanStepState state, string toolName, bool success)
        {
            bool isNew = !state.Completed;
            state.MatchCount++;
            state.MatchedByTool = toolName;

            // Only mark as completed if the tool succeeded
            if (success)
                state.Completed = true;

            int stepIndex = _plan.Steps.IndexOf(state.Step) + 1;
            return new PlanStepMatch
            {
                StepIndex = stepIndex,
                TotalSteps = _plan.Steps.Count,
                Description = state.Step.Description,
                ToolName = toolName,
                IsNewMatch = isNew
            };
        }

        private void RecordDeviation(DeviationType type, string message)
        {
            _deviations.Add(new PlanDeviation
            {
                Type = type,
                Message = message,
                ToolCallIndex = _toolCallCount
            });
        }

        // ── Tool↔Phase matching helpers ───────────────────────────────

        private static bool ToolMatchesStep(string toolName, ExecutionPlanStep step)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            // If step specifies a tool name, check exact match
            if (!string.IsNullOrWhiteSpace(step.ToolName))
            {
                return string.Equals(toolName, step.ToolName, StringComparison.OrdinalIgnoreCase);
            }

            // Steps without a ToolName (typically "generate" phase) are NOT
            // matched by exact tool name — only by loose match in FindLooseMatch.
            // This prevents "generate" from swallowing unexpected tools.
            return false;
        }

        private static bool PhaseMatchesTool(string phase, string toolName)
        {
            if (string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(toolName))
                return false;

            return phase.ToLowerInvariant() switch
            {
                "explore" => ExploreTools.Contains(toolName),
                "read" => ReadTools.Contains(toolName),
                "write" => WriteTools.Contains(toolName),
                "verify" => VerifyTools.Contains(toolName),
                "generate" => ExploreTools.Contains(toolName)
                            || ReadTools.Contains(toolName),
                _ => false
            };
        }

        // ── Internal state types ──────────────────────────────────────

        private class PlanStepState
        {
            public ExecutionPlanStep Step { get; set; }
            public bool Completed { get; set; }
            public int MatchCount { get; set; }
            public string MatchedByTool { get; set; }
        }

        private class PlanDeviation
        {
            public DeviationType Type { get; set; }
            public string Message { get; set; }
            public int ToolCallIndex { get; set; }
        }
    }
}
