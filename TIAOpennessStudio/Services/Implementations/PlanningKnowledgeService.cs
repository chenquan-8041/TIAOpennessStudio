using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Stores and recalls historical planning guidance (tool paths, success patterns) keyed by project.</summary>
    public sealed class PlanningKnowledgeService : IPlanningKnowledgeService
    {
        private readonly ProjectIndexService _indexService;

        public PlanningKnowledgeService(ProjectIndexService indexService)
        {
            _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        }

        public List<PlanningKnowledgeEntry> Search(string projectKey, string query, string taskIntent = null, int limit = 3)
        {
            return _indexService.SearchPlanningKnowledge(projectKey, query, taskIntent, limit);
        }

        public void Record(
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
            string executionStatus)
        {
            _indexService.RecordPlanningKnowledge(
                projectKey, taskIntent, promptPattern, targetKinds, usedTools, planSummary,
                success, completedSteps, totalSteps, deviationCount, executionStatus);
        }
    }
}
