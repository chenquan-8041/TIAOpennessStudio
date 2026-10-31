using System;
using System.Collections.Generic;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    public sealed class PlanningKnowledgeEntry
    {
        public string ProjectKey { get; set; }
        public string KnowledgeKey { get; set; }
        public string TaskIntent { get; set; }
        public string PromptPattern { get; set; }
        public string TargetKinds { get; set; }
        public string UsedTools { get; set; }
        public string PlanSummary { get; set; }
        public bool Success { get; set; }
        public int CompletedSteps { get; set; }
        public int TotalSteps { get; set; }
        public int DeviationCount { get; set; }
        public string ExecutionStatus { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Stores and recalls planning knowledge (successful/failed plans) to guide
    /// future planning.
    /// </summary>
    public interface IPlanningKnowledgeService
    {
        List<PlanningKnowledgeEntry> Search(string projectKey, string query, string taskIntent = null, int limit = 3);

        void Record(
            string projectKey,
            string taskIntent,
            string promptPattern,
            string targetKinds,
            string usedTools,
            string planSummary,
            bool success,
            int completedSteps,
            int totalSteps,
            int deviationCount,
            string executionStatus);
    }
}
