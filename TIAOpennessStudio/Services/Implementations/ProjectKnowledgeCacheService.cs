using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Builds and serves the project knowledge cache (blocks, types, cross-references) used by the worker and agent.</summary>
    public sealed class ProjectKnowledgeCacheService : IProjectKnowledgeCacheService
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IBlockVersionService _blockVersionService;
        private readonly ProjectIndexService _projectIndexService;
        private readonly IPlcKnowledgeIndexService _knowledgeIndexService;
        private readonly IPlcProjectIndexingService _projectIndexingService;
        private readonly IGlobalProgressService _progressService;

        // Plain-string snapshot of the attached project's path/name, refreshed only when
        // we are on the COM-owning STA thread. Read-mostly; volatile is sufficient.
        // Lets hot, high-frequency paths (status polling) resolve the project key WITHOUT
        // touching the Project COM object from an arbitrary HTTP/thread-pool thread, which
        // throws "Cross-thread operation is not valid in Openness within STA".
        private volatile string _cachedProjectPath;
        private volatile string _cachedProjectName;

        public ProjectKnowledgeCacheService(
            ITiaOpennessService tiaService,
            IBlockVersionService blockVersionService,
            ProjectIndexService projectIndexService,
            IPlcKnowledgeIndexService knowledgeIndexService,
            IPlcProjectIndexingService projectIndexingService,
            IGlobalProgressService progressService = null)
        {
            _tiaService = tiaService ?? throw new ArgumentNullException(nameof(tiaService));
            _blockVersionService = blockVersionService ?? throw new ArgumentNullException(nameof(blockVersionService));
            _projectIndexService = projectIndexService ?? throw new ArgumentNullException(nameof(projectIndexService));
            _knowledgeIndexService = knowledgeIndexService ?? throw new ArgumentNullException(nameof(knowledgeIndexService));
            _projectIndexingService = projectIndexingService ?? throw new ArgumentNullException(nameof(projectIndexingService));
            _progressService = progressService;
        }

        public Task<ProjectKnowledgeCacheStatus> GetStatusAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BuildStatus(ResolveProjectKey()));
        }

        /// <summary>Index status for an explicit project key (used when the caller knows the project).</summary>
        public Task<ProjectKnowledgeCacheStatus> GetStatusAsync(string projectKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(BuildStatus(projectKey));
        }

        private ProjectKnowledgeCacheStatus BuildStatus(string projectKey)
        {
            var status = _projectIndexService.GetProjectCacheStatus(projectKey);
            status.IsConnected = _tiaService.IsConnected;
            if (!status.HasIndex && string.IsNullOrWhiteSpace(status.Note))
            {
                status.Note = _tiaService.IsConnected
                    ? "Knowledge cache has not been built for the current project."
                    : "TIA Portal is not connected.";
            }
            return status;
        }

        public async Task<ProjectKnowledgeCacheStatus> EnsureReadyAsync(
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            if (!ShouldRefresh(status, freshness))
            {
                return status;
            }

            if (!_tiaService.IsConnected || _tiaService.PlcSoftware == null)
            {
                status.Note = "Cannot refresh project knowledge cache because TIA Portal is not connected.";
                return status;
            }

            await RebuildAsync(
                force: string.Equals(freshness, ProjectKnowledgeFreshnessModes.ForceRefresh, StringComparison.OrdinalIgnoreCase),
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);

            return await GetStatusAsync(ct).ConfigureAwait(false);
        }

        public async Task<PlcProjectIndexBuildResult> RebuildAsync(bool force = false, Action<string> onProgress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var projectKey = ResolveProjectKey();

            string gitBranch;
            string gitCommit;
            var hasGit = _blockVersionService.TryGetGitSnapshot(out gitBranch, out gitCommit);
            _projectIndexService.UpdateProjectCacheState(projectKey, "indexing", null, hasGit ? gitBranch : "", hasGit ? gitCommit : "", force ? "forced rebuild" : "rebuild");

            // Wrap onProgress to also report to global progress UI
            Action<string> wrappedProgress = msg =>
            {
                onProgress?.Invoke(msg);
                _progressService?.Show(msg, -1, "indexing");
            };

            _progressService?.Show("Building project knowledge index...");

            try
            {
                var result = await _projectIndexingService.RebuildAsync(force, wrappedProgress, ct).ConfigureAwait(false);
                _projectIndexService.ClearProjectDirty(projectKey);
                _projectIndexService.UpdateProjectCacheState(projectKey, "ready", DateTime.UtcNow.ToString("o"), hasGit ? gitBranch : "", hasGit ? gitCommit : "", "rebuild completed");
                return result;
            }
            catch (Exception ex)
            {
                _projectIndexService.UpdateProjectCacheState(projectKey, "error", null, hasGit ? gitBranch : "", hasGit ? gitCommit : "", ex.Message);
                throw;
            }
            finally
            {
                _progressService?.Hide();
            }
        }

        public Task MarkDirtyAsync(string blockKey = null, string reason = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _projectIndexService.MarkProjectDirty(ResolveProjectKey(), blockKey, reason ?? "dirty");
            return Task.CompletedTask;
        }

        public async Task<List<PlcKnowledgeSearchHit>> SearchKnowledgeAsync(
            string query,
            int limit = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default,
            string projectKey = null)
        {
            // An explicit project key (from the caller's own TIA session) makes the search
            // independent of whether this process is attached; only the attached-project
            // path needs the freshness check / rebuild.
            if (string.IsNullOrWhiteSpace(projectKey))
                await EnsureReadyAsync(freshness, null, ct).ConfigureAwait(false);
            var key = string.IsNullOrWhiteSpace(projectKey) ? ResolveProjectKey() : projectKey;
            return _knowledgeIndexService.Search(key, query, limit);
        }

        public async Task<PlcVariableReferenceResult> QueryVariableReferencesAsync(
            string variablePath,
            string direction = "both",
            int topK = 20,
            bool includeDefinitions = true,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            await EnsureReadyAsync(freshness, null, ct).ConfigureAwait(false);
            return _knowledgeIndexService.QueryVariableReferences(ResolveProjectKey(), variablePath, direction, topK, includeDefinitions);
        }

        public async Task<ContextPack> BuildContextPackAsync(
            string query,
            string rid = null,
            int topK = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            await EnsureReadyAsync(freshness, null, ct).ConfigureAwait(false);
            return _knowledgeIndexService.BuildContextPack(ResolveProjectKey(), query, rid, topK);
        }

        public async Task<DependencyChainResult> QueryCallChainAsync(
            string blockName,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            await EnsureReadyAsync(freshness, null, ct).ConfigureAwait(false);
            return _projectIndexService.QueryCallChain(ResolveProjectKey(), blockName, direction, maxDepth, maxNodeCount);
        }

        public async Task<DependencyChainResult> QueryVariableImpactAsync(
            string canonicalPath,
            string direction = "both",
            int maxDepth = 5,
            int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            await EnsureReadyAsync(freshness, null, ct).ConfigureAwait(false);
            return _projectIndexService.QueryVariableImpact(ResolveProjectKey(), canonicalPath, direction, maxDepth, maxNodeCount);
        }

        private string ResolveProjectKey()
        {
            // On the COM-owning STA thread we may read Project.Path/Name directly and
            // refresh the plain-string cache. On any other thread (e.g. the worker's
            // HTTP listener handling a status poll) we MUST NOT touch the Project COM
            // object — use the cached strings instead, otherwise Openness throws
            // "Cross-thread operation is not valid in Openness within STA".
            if (TiaStaHost.Instance.CheckAccess())
            {
                try
                {
                    var path = _tiaService.Project?.Path?.FullName;
                    if (!string.IsNullOrWhiteSpace(path))
                        _cachedProjectPath = path;
                }
                catch { }

                try
                {
                    var name = _tiaService.Project?.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                        _cachedProjectName = name;
                }
                catch { }
            }

            var cachedPath = _cachedProjectPath;
            if (!string.IsNullOrWhiteSpace(cachedPath))
                return _blockVersionService.GetProjectCacheKey(cachedPath);

            return _blockVersionService.GetProjectCacheKey(_cachedProjectName);
        }

        /// <summary>
        /// Returns the current attached project key and display name for project-awareness
        /// responses. Must be called from a thread that is allowed to touch TIA COM objects
        /// (STA / UI thread). Never throws; returns (null, null) when no project is attached
        /// or when TIA access fails.
        /// </summary>
        public (string Key, string Name) GetCurrentProjectInfo()
        {
            try
            {
                // ResolveProjectKey refreshes the cached path/name when called on the STA
                // thread, and reads the cache otherwise. Either way, never touch COM here
                // off the STA thread.
                string key = ResolveProjectKey();
                if (string.IsNullOrWhiteSpace(key) || key == "default-project")
                    key = null;

                var name = _cachedProjectName;
                if (key == null && string.IsNullOrWhiteSpace(name))
                    return (null, null);

                return (key, name);
            }
            catch
            {
                return (null, null);
            }
        }

        private static bool ShouldRefresh(ProjectKnowledgeCacheStatus status, string freshness)
        {
            if (status == null)
            {
                return true;
            }

            if (!status.HasIndex)
            {
                return true;
            }

            if (string.Equals(freshness, ProjectKnowledgeFreshnessModes.ForceRefresh, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(freshness, ProjectKnowledgeFreshnessModes.PreferFresh, StringComparison.OrdinalIgnoreCase) && status.IsStale)
            {
                return true;
            }

            return false;
        }
    }
}
