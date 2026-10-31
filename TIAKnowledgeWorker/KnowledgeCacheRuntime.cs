using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;

namespace TIAKnowledgeWorker
{
    /// <summary>
    /// Composition root for the Worker process.
    /// Owns all TIA + indexing + cache services and exposes them
    /// through a unified business API consumed by WorkerHost.
    /// </summary>
    public sealed class KnowledgeCacheRuntime : IDisposable
    {
        private readonly TiaStaExecutor _staExecutor;
        private readonly OpennessService _opennessService;
        private readonly BlockVersionService _blockVersionService;
        private readonly ProjectIndexService _projectIndexService;
        private readonly PlcKnowledgeStore _knowledgeStore;
        private readonly PlcKnowledgeStoreWriter _knowledgeWriter;
        private readonly PlcKnowledgeIndexService _knowledgeIndexService;
        private readonly PlcNetworkFactExtractor _networkExtractor;
        private readonly PlcKnowledgeIndexer _knowledgeIndexer;
        private readonly PlcProjectIndexingService _projectIndexingService;
        private readonly ProjectKnowledgeCacheService _knowledgeCacheService;

        private readonly string _cacheBasePath;

        // ---- Rebuild progress snapshot (read-mostly, volatile suffices; polled cross-process by /index/status) ----
        private volatile bool _isRebuilding;
        private volatile string _progressMessage = string.Empty;
        private int _progressCurrent = -1;
        private int _progressTotal = -1;

        // ---- Attached project snapshot (string copies only; no COM references) ----
        // Written on the STA thread, read lock-free by HTTP handler threads.
        private volatile string _attachedProjectKey;
        private volatile string _attachedProjectName;

        // ---- External change scanner state (passive detection of manual TIA edits) ----
        private const int ScanSliceSize = 100;
        private const int ScanIntervalMinMs = 30000;
        private const int ScanIntervalMaxMs = 120000;
        private const int ScanSliceGapMs = 250;
        private readonly object _scanSync = new object();
        private Dictionary<string, long> _scanBaseline = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private string _scanBaselineProjectKey;
        private bool _scanSeeded;
        private int _scanInProgress;
        private int _scanIntervalMs = ScanIntervalMinMs;
        private CancellationTokenSource _scannerCts;

        public KnowledgeCacheRuntime(TiaStaExecutor staExecutor)
        {
            _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));

            // Cache path: %LocalAppData%\TIAOpennessStudio\KnowledgeCache\
            _cacheBasePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TIAOpennessStudio",
                "KnowledgeCache");

            System.IO.Directory.CreateDirectory(_cacheBasePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_cacheBasePath, "logs"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_cacheBasePath, "snapshots"));

            // Wire up services (v3 layered knowledge store, same Index\project_index.db).
            _opennessService = new OpennessService(new WorkerUIService());
            _blockVersionService = new BlockVersionService();
            _projectIndexService = new ProjectIndexService(_cacheBasePath); // planning/cache-state/dirty + call-chain
            _knowledgeStore = new PlcKnowledgeStore(_cacheBasePath);
            _knowledgeWriter = new PlcKnowledgeStoreWriter(_knowledgeStore);
            _knowledgeIndexService = new PlcKnowledgeIndexService(_knowledgeStore, new TokenBudget());

            _networkExtractor = new PlcNetworkFactExtractor();
            _knowledgeIndexer = new PlcKnowledgeIndexer(
                _opennessService,
                _blockVersionService,
                _knowledgeWriter,
                _networkExtractor);
            _projectIndexingService = new PlcProjectIndexingService(_knowledgeIndexer);
            _knowledgeCacheService = new ProjectKnowledgeCacheService(
                _opennessService,
                _blockVersionService,
                _projectIndexService,
                _knowledgeIndexService,
                _projectIndexingService);
        }

        // ---- TIA connection ----

        public Task<bool> AttachAsync()
        {
            // Must run on TiaStaHost (the thread that OWNS the Openness COM objects),
            // NOT on _staExecutor. OpennessService.AttachToRunningTiaPortalAsync creates
            // TiaPortal/Project/PlcSoftware on TiaStaHost, so the continuation
            // (RefreshProjectSnapshotCore -> GetCurrentProjectInfo) MUST also run there;
            // otherwise it touches COM from the wrong STA thread and Openness throws
            // "Cross-thread operation is not valid in Openness within STA".
            return TiaStaHost.Instance.RunAsync(async () =>
            {
                var result = await _opennessService.AttachToRunningTiaPortalAsync();
                RefreshProjectSnapshotCore();
                return result;
            });
        }

        public Task<bool> ConnectAsync()
        {
            return TiaStaHost.Instance.RunAsync(async () =>
            {
                var result = await _opennessService.ConnectAsync();
                RefreshProjectSnapshotCore();
                return result;
            });
        }

        public bool IsConnected => _opennessService.IsConnected;

        /// <summary>
        /// Lock-free snapshot of the currently attached project (string copies only).
        /// Safe to call from any thread; returns (null, null) when nothing is attached.
        /// </summary>
        public (string Key, string Name) GetAttachedProjectSnapshot()
            => (_attachedProjectKey, _attachedProjectName);

        /// <summary>
        /// Refreshes the attached-project snapshot. MUST run on the STA thread
        /// (touches _tiaService.Project). Never throws. When the project key changes,
        /// the external-change scan baseline is reset so the next cycle reseeds.
        /// </summary>
        private void RefreshProjectSnapshotCore()
        {
            try
            {
                var info = _knowledgeCacheService.GetCurrentProjectInfo();
                _attachedProjectKey = info.Key;
                _attachedProjectName = info.Name;

                bool reset;
                lock (_scanSync)
                {
                    reset = !string.Equals(_scanBaselineProjectKey, info.Key, StringComparison.OrdinalIgnoreCase);
                }
                if (reset)
                    ResetScanBaseline(info.Key);
            }
            catch
            {
                // Keep the previous snapshot on any TIA access failure.
            }
        }

        /// <summary>
        /// Ensure the Worker's Openness session is fully ready for a rebuild:
        /// TiaPortal attached, a Project selected, and PlcSoftware resolved.
        /// Must run on the STA thread. Returns true when PlcSoftware is available.
        ///
        /// Without this, /index/rebuild fails immediately: attach only sets TiaPortal,
        /// but rebuild requires PlcSoftware (Project + CPU software container).
        /// </summary>
        private bool EnsureTiaReadyCore()
        {
            if (_opennessService.PlcSoftware != null)
                return true;

            // 1) Attach to a running TIA Portal instance if not connected.
            if (!_opennessService.IsConnected)
            {
                var attached = _opennessService.AttachToRunningTiaPortal();
                if (!attached || !_opennessService.IsConnected)
                {
                    AppLogger.Log("[KnowledgeCacheRuntime] EnsureTiaReady: attach failed, no running TIA Portal.");
                    return false;
                }
            }

            // 2) Select a project if none is set yet.
            if (_opennessService.Project == null && _opennessService.TiaPortal != null)
            {
                try
                {
                    if (_opennessService.TiaPortal.Projects.Count > 0)
                        _opennessService.SetProject(_opennessService.TiaPortal.Projects[0]);
                }
                catch (Exception ex)
                {
                    AppLogger.Log("[KnowledgeCacheRuntime] EnsureTiaReady: project select failed: " + ex.Message);
                }
            }

            // 3) Resolve PlcSoftware from the project.
            if (_opennessService.Project != null)
            {
                try
                {
                    var sw = _opennessService.FindPlcSoftware();
                    if (sw == null)
                        AppLogger.Log("[KnowledgeCacheRuntime] EnsureTiaReady: no PlcSoftware (CPU) found in project.");
                }
                catch (Exception ex)
                {
                    // Commonly seen when the cached Project reference is stale (Access to a disposed object).
                    AppLogger.Log("[KnowledgeCacheRuntime] EnsureTiaReady: FindPlcSoftware failed: " + ex.Message);
                }
            }

            RefreshProjectSnapshotCore();
            return _opennessService.PlcSoftware != null;
        }

        // ---- Cache status ----

        public async Task<ProjectKnowledgeCacheStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var status = await _knowledgeCacheService.GetStatusAsync(ct).ConfigureAwait(false);

            // Merge the live rebuild progress snapshot so the main process can poll and show status-bar progress.
            if (_isRebuilding)
            {
                status.IsIndexing = true;
                status.ProgressMessage = _progressMessage;
                status.ProgressCurrent = System.Threading.Volatile.Read(ref _progressCurrent);
                status.ProgressTotal = System.Threading.Volatile.Read(ref _progressTotal);
            }

            return status;
        }

        public Task<ProjectKnowledgeCacheStatus> EnsureReadyAsync(
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            // EnsureReadyAsync may trigger a rebuild / freshness check that touches
            // Openness COM, so it must run on the COM-owning thread (TiaStaHost), not
            // on _staExecutor.
            return TiaStaHost.Instance.RunAsync(() =>
                _knowledgeCacheService.EnsureReadyAsync(freshness, onProgress, ct));
        }

        // ---- Index operations ----

        public Task<PlcProjectIndexBuildResult> RebuildAsync(
            bool force = false,
            Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            _isRebuilding = true;
            _progressMessage = "Starting index rebuild...";
            System.Threading.Volatile.Write(ref _progressCurrent, 0);
            System.Threading.Volatile.Write(ref _progressTotal, -1);

            // Wrap onProgress: forward to the original callback and update the progress snapshot for /index/status.
            // Messages like "Indexing block/type ..." mean one object was processed; increment the count.
            Action<string> wrapped = msg =>
            {
                try { onProgress?.Invoke(msg); } catch { /* progress forwarding failure does not affect the build */ }

                if (!string.IsNullOrEmpty(msg))
                {
                    _progressMessage = msg;
                    if (msg.StartsWith("Indexing ", StringComparison.OrdinalIgnoreCase))
                        System.Threading.Interlocked.Increment(ref _progressCurrent);
                }
            };

            // Run on TiaStaHost (the thread that owns the Openness COM objects), NOT on
            // _staExecutor: EnsureTiaReadyCore touches TiaPortal.Projects directly, and
            // Openness rejects calls from any other STA thread ("Cross-thread operation
            // is not valid in Openness within STA").
            return TiaStaHost.Instance.RunAsync(async () =>
            {
                AppLogger.Log($"[KnowledgeCacheRuntime] Rebuild requested. force={force}");
                try
                {
                    // Ensure TIA is attached + Project + PlcSoftware resolved before rebuild.
                    // Runs on this STA thread (same as the rebuild), so Openness calls are safe.
                    if (!EnsureTiaReadyCore())
                    {
                        AppLogger.Log("[KnowledgeCacheRuntime] Rebuild aborted: TIA not ready (not attached or no PLC software).");
                        wrapped("Index rebuild aborted: TIA Portal not attached or no PLC software available.");
                        return new PlcProjectIndexBuildResult { Failed = -1 };
                    }

                    var result = await _knowledgeCacheService.RebuildAsync(force, wrapped, ct).ConfigureAwait(false);
                    AppLogger.Log($"[KnowledgeCacheRuntime] Rebuild done. upserted={result.Upserted}, skipped={result.SkippedUnchanged}, failed={result.Failed}");
                    return result;
                }
                catch (Exception ex)
                {
                    // Previously this exception bubbled to WorkerHost's top-level catch, leaving only a single
                    // "Unhandled request error" line; the main process received HTTP 500 and returned
                    // Failed=-1, so the UI showed "-1 failed" with no way to diagnose it.
                    AppLogger.LogError("[KnowledgeCacheRuntime] Rebuild threw", ex);
                    wrapped("Index rebuild failed: " + ex.Message);
                    return new PlcProjectIndexBuildResult { Failed = -1 };
                }
                finally
                {
                    _isRebuilding = false;
                    _progressMessage = string.Empty;
                }
            });
        }

        public Task MarkDirtyAsync(
            string blockKey = null,
            string reason = null,
            CancellationToken ct = default)
        {
            return _knowledgeCacheService.MarkDirtyAsync(blockKey, reason, ct);
        }

        // ---- Query ----

        /// <summary>
        /// Attach to the running TIA Portal, select its project and resolve the PLC software
        /// (only when not already connected). Safe to call from a query handler: it marshals
        /// to the COM-owning STA thread and never throws.
        /// </summary>
        public Task<bool> EnsureAttachedAsync()
            => TiaStaHost.Instance.RunAsync(() => Task.FromResult(EnsureTiaReadyCore()));

        /// <summary>Index status for an explicit project key (no attachment refresh).</summary>
        public Task<ProjectKnowledgeCacheStatus> GetStatusForProjectAsync(string projectKey, CancellationToken ct = default)
            => _knowledgeCacheService.GetStatusAsync(projectKey, ct);

        public Task<System.Collections.Generic.List<PlcKnowledgeSearchHit>> SearchKnowledgeAsync(
            string query,
            int limit = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default,
            string projectKey = null)
        {
            return _knowledgeCacheService.SearchKnowledgeAsync(query, limit, freshness, ct, projectKey);
        }

        public Task<PlcVariableReferenceResult> QueryVariableReferencesAsync(
            string variablePath,
            string direction = "both",
            int topK = 20,
            bool includeDefinitions = true,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            return _knowledgeCacheService.QueryVariableReferencesAsync(
                variablePath, direction, topK, includeDefinitions, freshness, ct);
        }

        public Task<ContextPack> BuildContextPackAsync(
            string query,
            string rid = null,
            int topK = 5,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred,
            CancellationToken ct = default)
        {
            return _knowledgeCacheService.BuildContextPackAsync(query, rid, topK, freshness, ct);
        }

        // Dependency graph traversal: pure SQLite reads (plc_call_relation / plc_symbol_doc),
        // no TIA STA access, so no _staExecutor marshaling required.
        public Task<DependencyChainResult> QueryCallChainAsync(
            string blockName, string direction = "both", int maxDepth = 5, int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred, CancellationToken ct = default)
        {
            return _knowledgeCacheService.QueryCallChainAsync(blockName, direction, maxDepth, maxNodeCount, freshness, ct);
        }

        public Task<DependencyChainResult> QueryVariableImpactAsync(
            string canonicalPath, string direction = "both", int maxDepth = 5, int maxNodeCount = 200,
            string freshness = ProjectKnowledgeFreshnessModes.CachePreferred, CancellationToken ct = default)
        {
            return _knowledgeCacheService.QueryVariableImpactAsync(canonicalPath, direction, maxDepth, maxNodeCount, freshness, ct);
        }

        /// <summary>
        /// Query variable cross-references (whole-project scan) through TIA Openness.
        /// The TIA API is STA-thread affine, so this runs on the Worker's dedicated STA thread;
        /// the main process calls it over HTTP, so the UI thread is not blocked.
        /// </summary>
        public Task<CrossReferenceResult> GetCrossReferencesAsync(
            string variableName,
            string blockName = null,
            CancellationToken ct = default)
        {
            // GetCrossReferencesAsync does NOT marshal internally — it reads PlcSoftware
            // and walks the project via reflection directly. It must therefore run on
            // the COM-owning thread (TiaStaHost). Running it on _staExecutor was the
            // source of the "Cross-thread operation is not valid in Openness within STA"
            // errors seen during rebuild (agent cross-reference queries ran concurrently).
            return TiaStaHost.Instance.RunAsync(() =>
                _opennessService.GetCrossReferencesAsync(variableName, blockName));
        }

        // ---- Cache info ----

        public string CacheBasePath => _cacheBasePath;

        // ---- External change scanner (passive detection of manual TIA edits) ----

        /// <summary>
        /// Starts the background scan loop that passively watches for blocks the user
        /// edited manually in TIA Portal and marks them dirty. The loop yields to all
        /// other work: it never queues a slice while the STA executor is busy or a
        /// rebuild is running, and each 100-block slice is a separate STA dispatch so
        /// real tasks always interleave between slices.
        /// </summary>
        public void StartExternalChangeScanner()
        {
            if (_scannerCts != null) return;
            _scannerCts = new CancellationTokenSource();
            Task.Run(() => ScannerLoopAsync(_scannerCts.Token));
        }

        private async Task ScannerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_scanIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                // Reentrancy guard: only one scan cycle at a time.
                if (Interlocked.CompareExchange(ref _scanInProgress, 1, 0) != 0)
                    continue;

                bool foundDirty = false;
                try
                {
                    // Priority constraint: skip the whole cycle instead of queueing behind real work.
                    // All real COM work now runs on TiaStaHost (the single COM-owning thread),
                    // so the throttle must observe TiaStaHost's queue, not _staExecutor's.
                    if (TiaStaHost.Instance.PendingTaskCount == 0 && !_isRebuilding)
                        foundDirty = await RunScanCycleAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.Log("[KnowledgeCacheRuntime] scan cycle failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _scanInProgress, 0);
                }

                // After marking external changes dirty, re-index them so the RAG index
                // stays fresh without a manual rebuild. Incremental rebuild (force=false)
                // skips unchanged blocks via version-hash comparison, so only the changed
                // blocks are re-indexed.
                if (foundDirty)
                {
                    try
                    {
                        await RebuildAsync(force: false, ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log("[KnowledgeCacheRuntime] auto re-index after external change failed: " + ex.Message);
                    }
                }

                // Adaptive interval: back off while clean, snap back on any change.
                _scanIntervalMs = foundDirty
                    ? ScanIntervalMinMs
                    : Math.Min(_scanIntervalMs * 2, ScanIntervalMaxMs);
            }
        }

        private sealed class ScanSlice
        {
            public bool NotReady;
            public bool HasMore;
            public bool EnumerateFailed;
            public string ProjectKey;
            public List<KeyValuePair<string, long>> Items = new List<KeyValuePair<string, long>>();
        }

        private async Task<bool> RunScanCycleAsync(CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool foundDirty = false;
            int cursor = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Priority constraint: re-check before every slice; abort the cycle (not
                // queue) when real work arrived on the COM-owning thread (TiaStaHost).
                if (TiaStaHost.Instance.PendingTaskCount > 0 || _isRebuilding)
                    break;

                int skip = cursor;
                // ScanSliceCore enumerates Openness block COM objects, so it must run on
                // TiaStaHost (the COM-owning thread), not on _staExecutor.
                var slice = await TiaStaHost.Instance.RunAsync(() => ScanSliceCore(skip, ScanSliceSize)).ConfigureAwait(false);

                if (slice.NotReady)
                    return false;

                string projectKey = slice.ProjectKey;
                if (string.IsNullOrWhiteSpace(projectKey))
                    return false; // Cannot attribute changes to a project; fail-closed.

                if (slice.EnumerateFailed)
                {
                    // Conservative: stale COM object mid-scan → mark the whole project dirty.
                    _projectIndexService.MarkProjectDirty(projectKey, null, "tia_scan_exception");
                    ResetScanBaseline(projectKey);
                    return true;
                }

                // Project switched mid-cycle → drop state; next cycle reseeds.
                string baselineKey;
                lock (_scanSync) { baselineKey = _scanBaselineProjectKey; }
                if (!string.Equals(baselineKey, projectKey, StringComparison.OrdinalIgnoreCase))
                {
                    ResetScanBaseline(projectKey);
                    return false;
                }

                List<string> changed = null;
                lock (_scanSync)
                {
                    foreach (var kv in slice.Items)
                    {
                        seen.Add(kv.Key);
                        if (!_scanSeeded)
                        {
                            _scanBaseline[kv.Key] = kv.Value;
                            continue;
                        }
                        if (!_scanBaseline.TryGetValue(kv.Key, out long old) || old != kv.Value)
                        {
                            _scanBaseline[kv.Key] = kv.Value;
                            (changed ??= new List<string>()).Add(kv.Key);
                        }
                    }
                }

                if (changed != null)
                {
                    foreach (var k in changed)
                        _projectIndexService.MarkProjectDirty(projectKey, k, "external_tia_change");
                    foundDirty = true;
                    AppLogger.Log($"[KnowledgeCacheRuntime] external change scan: {changed.Count} block(s) marked dirty.");
                }

                cursor += slice.Items.Count;
                if (!slice.HasMore)
                {
                    // Cycle complete: detect deletions and flip the seed flag.
                    List<string> deleted = null;
                    lock (_scanSync)
                    {
                        if (_scanSeeded)
                        {
                            foreach (var k in _scanBaseline.Keys)
                            {
                                if (!seen.Contains(k))
                                    (deleted ??= new List<string>()).Add(k);
                            }
                            if (deleted != null)
                                foreach (var k in deleted)
                                    _scanBaseline.Remove(k);
                        }
                        _scanSeeded = true;
                    }
                    if (deleted != null)
                    {
                        foreach (var k in deleted)
                            _projectIndexService.MarkProjectDirty(projectKey, k, "external_tia_delete");
                        foundDirty = true;
                        AppLogger.Log($"[KnowledgeCacheRuntime] external change scan: {deleted.Count} deleted block(s) marked dirty.");
                    }
                    break;
                }

                // Leave a gap so other STA work can interleave between slices.
                try { await Task.Delay(ScanSliceGapMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            return foundDirty;
        }

        /// <summary>
        /// Reads one slice of blocks with their modification stamps. Runs on the STA thread.
        /// Uses the same enumeration and key format as the indexer so dirty marks match
        /// the keys written into the index.
        /// </summary>
        private ScanSlice ScanSliceCore(int skip, int take)
        {
            var slice = new ScanSlice();
            try
            {
                if (!_opennessService.IsConnected || _opennessService.PlcSoftware == null)
                {
                    slice.NotReady = true;
                    return slice;
                }

                RefreshProjectSnapshotCore();
                slice.ProjectKey = _attachedProjectKey;

                int index = 0;
                foreach (var block in PlcKnowledgeIndexer.EnumerateBlocks(_opennessService.PlcSoftware.BlockGroup))
                {
                    if (index++ < skip) continue;
                    if (slice.Items.Count >= take)
                    {
                        slice.HasMore = true;
                        break;
                    }

                    try
                    {
                        var blockType = block.GetType().Name;
                        var key = PlcKnowledgeIndexer.BuildBlockCacheKey(block, blockType);
                        long ticks = Math.Max(
                            SafeDateTicks(() => block.ModifiedDate),
                            Math.Max(SafeDateTicks(() => block.CodeModifiedDate),
                                     SafeDateTicks(() => block.InterfaceModifiedDate)));
                        if (!string.IsNullOrWhiteSpace(key))
                            slice.Items.Add(new KeyValuePair<string, long>(key, ticks));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log("[KnowledgeCacheRuntime] scan slice: block metadata read failed: " + ex.Message);
                        slice.EnumerateFailed = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("[KnowledgeCacheRuntime] scan slice failed: " + ex.Message);
                slice.EnumerateFailed = true;
            }
            return slice;
        }

        private static long SafeDateTicks(Func<DateTime> read)
        {
            try { return read().Ticks; } catch { return 0; }
        }

        private void ResetScanBaseline(string projectKey)
        {
            lock (_scanSync)
            {
                _scanBaselineProjectKey = projectKey;
                _scanBaseline = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                _scanSeeded = false;
            }
        }

        public void Dispose()
        {
            try { _scannerCts?.Cancel(); } catch { }
            _staExecutor?.Dispose();
        }
    }
}
