using System;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>Daemon that periodically refreshes/rebuilds the project knowledge cache on a DispatcherTimer.</summary>
    public sealed class ProjectKnowledgeCacheDaemonService : IProjectKnowledgeCacheDaemonService
    {
        private readonly ITiaOpennessService _tiaService;
        private readonly IProjectKnowledgeCacheService _knowledgeCacheService;
        private readonly PlcKnowledgeIndexer _knowledgeIndexer;
        private int _started;
        private int _warmupRunning;

        public ProjectKnowledgeCacheDaemonService(
            ITiaOpennessService tiaService,
            IProjectKnowledgeCacheService knowledgeCacheService,
            PlcKnowledgeIndexer knowledgeIndexer = null)
        {
            _tiaService = tiaService ?? throw new ArgumentNullException(nameof(tiaService));
            _knowledgeCacheService = knowledgeCacheService ?? throw new ArgumentNullException(nameof(knowledgeCacheService));
            _knowledgeIndexer = knowledgeIndexer;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            _tiaService.ConnectionStatusChanged += OnConnectionStatusChanged;
            // Auto-refresh the index when any block is imported/modified via Openness
            // (UI import, agent import, patch service — all funnel through OpennessService).
            _tiaService.BlockChanged += OnBlockChanged;
            _ = TriggerWarmupAsync("startup");
        }

        private void OnBlockChanged(object sender, string blockName)
        {
            if (_knowledgeIndexer == null || string.IsNullOrWhiteSpace(blockName)) return;
            // Single-block incremental update: delete old derived rows + write new.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _knowledgeIndexer.UpdateBlockAsync(blockName).ConfigureAwait(false);
                    AppLogger.Log($"[KnowledgeCacheDaemon] auto-updated index for changed block: {blockName}");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[KnowledgeCacheDaemon] block-change update failed for '{blockName}': {ex.Message}");
                }
            });
        }

        public Task TriggerWarmupAsync(string reason = null)
        {
            if (Interlocked.Exchange(ref _warmupRunning, 1) == 1)
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                try
                {
                    if (!_tiaService.IsConnected)
                    {
                        // AttachToRunningTiaPortalAsync re-marshals to TiaStaHost internally,
                        // so it is safe to await directly from this background thread. (Do NOT
                        // route through the WPF UI dispatcher: the UI thread is NOT the COM-owning
                        // thread — TiaStaHost is — so UI-dispatch is both unnecessary and misleading.)
                        await _tiaService.AttachToRunningTiaPortalAsync().ConfigureAwait(false);

                        if (!_tiaService.IsConnected)
                        {
                            AppLogger.Log("[KnowledgeCacheDaemon] warmup skipped. No running TIA instance is attached.");
                            return;
                        }
                    }

                    AppLogger.Log("[KnowledgeCacheDaemon] warmup queued. reason=" + (reason ?? "unspecified"));
                    await Task.Delay(1200).ConfigureAwait(false);
                    // Run the warmup on the COM-owning STA thread (TiaStaHost). EnsureReadyAsync
                    // may trigger a rebuild that touches Openness COM; marshaling here keeps the
                    // whole chain on the single COM thread instead of relying on each downstream
                    // call to re-marshal.
                    await TiaStaHost.Instance.RunAsync(() =>
                        _knowledgeCacheService.EnsureReadyAsync(
                            ProjectKnowledgeFreshnessModes.CachePreferred,
                            message => AppLogger.Log("[KnowledgeCacheDaemon] " + message)))
                        .ConfigureAwait(false);
                    AppLogger.Log("[KnowledgeCacheDaemon] warmup finished.");
                }
                catch (Exception ex)
                {
                    AppLogger.Log("[KnowledgeCacheDaemon] warmup skipped/failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _warmupRunning, 0);
                }
            });
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (!isConnected)
            {
                return;
            }

            _ = TriggerWarmupAsync("tia_connected");
        }
    }
}
