using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Hosts the worker process that runs STA-affine TIA API calls off the UI thread
    /// and proxies its HTTP API.
    /// </summary>
    public interface IProjectKnowledgeWorkerHostService
    {
        /// <summary>
        /// Query variable cross-references via the Worker (STA-affine TIA API runs
        /// in the worker process, so the UI thread is never blocked).
        /// </summary>
        Task<CrossReferenceResult> GetCrossReferencesAsync(
            string variableName,
            string blockName = null,
            CancellationToken ct = default);

        string BaseUrl { get; }
        string AuthToken { get; }
        string LogFilePath { get; }
        Task<bool> IsHealthyAsync();
        Task<ProjectKnowledgeWorkerStatus> GetStatusAsync();
        Task<bool> EnsureStartedAsync();
        Task<bool> EnsureStartedAsync(IProgress<string> progress);
        void StopOwnedProcess();

        /// <summary>Read the last N lines from the worker log file.</summary>
        Task<string> GetLogTailAsync(int lines = 50);

        // ---- Proxy methods to Worker HTTP API ----

        /// <summary>Get the full cache status from the Worker.</summary>
        Task<ProjectKnowledgeCacheStatus> GetCacheStatusAsync(CancellationToken ct = default);

        /// <summary>Ensure the cache is ready (freshness check + optional rebuild).</summary>
        Task<ProjectKnowledgeCacheStatus> EnsureCacheReadyAsync(
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default);

        /// <summary>Search the project knowledge index via the Worker.</summary>
        Task<List<PlcKnowledgeSearchHit>> SearchKnowledgeAsync(
            string query,
            int limit = 5,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default,
            string projectKey = null);

        /// <summary>Build a Network-centric evidence context pack via the Worker.</summary>
        Task<ContextPack> BuildContextPackAsync(
            string query,
            string rid = null,
            int topK = 5,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default,
            string projectKey = null);

        /// <summary>Query variable references via the Worker.</summary>
        Task<PlcVariableReferenceResult> QueryVariableReferencesAsync(
            string variablePath,
            string direction = "both",
            int topK = 20,
            bool includeDefinitions = true,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default,
            string projectKey = null);

        /// <summary>Traverse the block call chain (upstream/downstream) via the Worker.</summary>
        Task<DependencyChainResult> QueryCallChainAsync(
            string blockName,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default,
            string projectKey = null);

        /// <summary>Trace variable impact through the call graph via the Worker.</summary>
        Task<DependencyChainResult> QueryVariableImpactAsync(
            string canonicalPath,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.PreferFresh,
            CancellationToken ct = default,
            string projectKey = null);

        /// <summary>Rebuild the project index via the Worker.</summary>
        Task<PlcProjectIndexBuildResult> RebuildAsync(
            bool force = false,
            CancellationToken ct = default);

        /// <summary>Mark a block as dirty in the Worker cache.</summary>
        Task MarkDirtyAsync(
            string blockKey = null,
            string reason = null,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the project the Worker is currently attached to (plus index state).
        /// Never throws; returns null when the worker is unreachable.
        /// </summary>
        Task<WorkerCurrentProjectInfo> GetCurrentProjectInfoAsync(CancellationToken ct = default);
    }
}
