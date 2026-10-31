using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// In-process interface for the project knowledge cache: search, dependency
    /// traversal, context packs, and rebuilds.
    /// </summary>
    public interface IProjectKnowledgeCacheService
    {
        Task<ProjectKnowledgeCacheStatus> GetStatusAsync(CancellationToken ct = default);
        /// <summary>Index status for a specific project key (falls back to the attached project when null).</summary>
        Task<ProjectKnowledgeCacheStatus> GetStatusAsync(string projectKey, CancellationToken ct = default);
        Task<ProjectKnowledgeCacheStatus> EnsureReadyAsync(
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            Action<string> onProgress = null,
            CancellationToken ct = default);
        Task<PlcProjectIndexBuildResult> RebuildAsync(bool force = false, Action<string> onProgress = null, CancellationToken ct = default);
        Task MarkDirtyAsync(string blockKey = null, string reason = null, CancellationToken ct = default);
        Task<List<PlcKnowledgeSearchHit>> SearchKnowledgeAsync(
            string query,
            int limit = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default,
            string projectKey = null);
        Task<PlcVariableReferenceResult> QueryVariableReferencesAsync(
            string variablePath,
            string direction = "both",
            int topK = 20,
            bool includeDefinitions = true,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default);
        Task<ContextPack> BuildContextPackAsync(
            string query,
            string rid = null,
            int topK = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default);
        Task<DependencyChainResult> QueryCallChainAsync(
            string blockName,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default);
        Task<DependencyChainResult> QueryVariableImpactAsync(
            string canonicalPath,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default);
    }
}
