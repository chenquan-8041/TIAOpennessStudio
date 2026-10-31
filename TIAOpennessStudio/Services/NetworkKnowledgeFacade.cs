using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Unified facade for the Network-centric knowledge model.
    /// Exposes build status, retrieval, and agent integration points.
    /// Does NOT modify cloud :8001 endpoints (Requirement 17 compliance).
    /// </summary>
    public sealed class NetworkKnowledgeFacade
    {
        private readonly ProjectSnapshotManager _snapshotManager;
        private readonly NetworkRetrievalPipeline _retrievalPipeline;
        private readonly GlobalRelationResolver _relationResolver;
        private readonly NetworkAnnotationService _annotationService;
        private readonly CrossReferenceEnricher _crossRefEnricher;
        private readonly INetworkDocumentStore _networkStore;
        private readonly PlcNetworkFactExtractor _extractor;

        public NetworkKnowledgeFacade(
            ProjectSnapshotManager snapshotManager,
            INetworkDocumentStore networkStore,
            PlcNetworkFactExtractor extractor = null,
            NetworkAnnotationService annotationService = null,
            ICrossReferenceProvider crossRefProvider = null)
        {
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _networkStore = networkStore ?? throw new ArgumentNullException(nameof(networkStore));
            _extractor = extractor ?? new PlcNetworkFactExtractor();
            _retrievalPipeline = new NetworkRetrievalPipeline();
            _relationResolver = new GlobalRelationResolver();
            _annotationService = annotationService;
            _crossRefEnricher = crossRefProvider != null ? new CrossReferenceEnricher(crossRefProvider) : null;
            _crossRefProvider = crossRefProvider;
        }

        // ---- Build Status (Task 20: UI Integration) ----

        /// <summary>Get current build job status for UI display.</summary>
        public BuildJobStatus GetBuildStatus(string projectKey)
        {
            var job = _snapshotManager.GetActiveBuildJob(projectKey);
            var published = _snapshotManager.GetPublishedSnapshot(projectKey);

            return new BuildJobStatus
            {
                HasActiveJob = job != null,
                JobId = job?.JobId,
                State = job?.State.ToString() ?? "idle",
                CurrentPhase = job?.CurrentPhase ?? "none",
                CurrentObject = job?.CurrentObject,
                ExpectedObjects = job?.ExpectedObjects ?? 0,
                CompletedObjects = job?.CompletedObjects ?? 0,
                WarningCount = job?.WarningCount ?? 0,
                FailureCount = job?.FailureCount ?? 0,
                LastUpdatedAtUtc = job?.LastUpdatedAtUtc,
                HasPublishedSnapshot = published != null,
                PublishedSnapshotId = published?.SnapshotId,
                PublishedAtUtc = published?.PublishedAtUtc,
                PublishedCompleteness = published?.Completeness.ToString() ?? "none",
                PublishedNetworkCount = published?.Networks.Count ?? 0,
                PublishedRelationCount = published?.Relations.Count ?? 0,
                CrossRefAvailable = _crossRefProvider?.IsAvailable ?? false,
                CrossRefVerified = published?.Coverage.CrossRefEnrichment.Completed ?? 0,
                CrossRefCorrected = 0, // Detailed stats require storing EnrichmentResult in snapshot
                CrossRefNewDiscoveries = 0
            };
        }

        // ---- Retrieval (Task 21: Agent Integration) ----

        /// <summary>
        /// Retrieve Network-centric evidence for a query.
        /// Uses only the Published Snapshot (never candidate).
        /// Returns unavailable status if no snapshot exists.
        /// </summary>
        public EvidencePack RetrieveEvidence(string projectKey, string query)
        {
            var snapshot = _snapshotManager.GetPublishedSnapshot(projectKey);
            return _retrievalPipeline.Retrieve(snapshot, query);
        }

        /// <summary>
        /// Build a context string suitable for injection into Agent prompt.
        /// Returns null if no published snapshot exists.
        /// </summary>
        public string BuildAgentContext(string projectKey, string query, int maxTokens = 8192)
        {
            var pack = RetrieveEvidence(projectKey, query);
            if (!pack.IsAvailable) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## PLC Project Knowledge (Network-centric)");
            sb.AppendLine($"Snapshot: {pack.SnapshotId} | Networks: {pack.Networks.Count} | Relations: {pack.Relations.Count}");
            sb.AppendLine($"CrossRef: {(_crossRefProvider?.IsAvailable == true ? "verified" : "xml-inferred")}");

            if (!string.IsNullOrWhiteSpace(pack.LocationWarning))
                sb.AppendLine($"⚠️ {pack.LocationWarning}");

            sb.AppendLine();

            foreach (var net in pack.Networks)
            {
                sb.AppendLine($"### Network [{net.BlockId}] #{net.NetworkOrder}: {net.Title}");
                sb.AppendLine($"  Accesses: {net.AccessCount} | Calls: {net.CallSiteCount} | Parts: {net.PartCount} | Wires: {net.WireCount}");
            }

            if (pack.Relations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Relations");
                foreach (var rel in pack.Relations.Take(30))
                {
                    sb.AppendLine($"  {rel.SourceBlockId} --[{rel.Type}:{rel.Resolution}]--> {rel.RawTarget}");
                }
            }

            if (pack.OmittedNetworkCount > 0 || pack.OmittedRelationCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"(Omitted: {pack.OmittedNetworkCount} networks, {pack.OmittedRelationCount} relations due to token budget)");
            }

            var text = sb.ToString();
            if (text.Length > maxTokens * 4)
                text = text.Substring(0, maxTokens * 4) + "\n... (truncated)";

            return text;
        }

        // ---- On-demand Cross-Reference Query ----

        /// <summary>
        /// Query authoritative cross-references for a block directly from TIA Portal.
        /// Available when TIA is connected; returns empty list otherwise.
        /// </summary>
        public async Task<IReadOnlyList<BlockCrossReference>> QueryCrossReferencesAsync(
            string blockName, CancellationToken ct = default)
        {
            if (_crossRefEnricher == null)
                return Array.Empty<BlockCrossReference>();

            // Delegate to the underlying provider (injected via constructor)
            var provider = GetCrossRefProvider();
            if (provider == null || !provider.IsAvailable)
                return Array.Empty<BlockCrossReference>();

            return await provider.GetBlockCrossReferencesAsync(blockName, ct).ConfigureAwait(false);
        }

        private ICrossReferenceProvider _crossRefProvider;
        private ICrossReferenceProvider GetCrossRefProvider()
        {
            // Extract from enricher's dependency - store reference at construction
            return _crossRefProvider;
        }

        // ---- Full Build Orchestration ----

        /// <summary>
        /// Execute a complete build cycle: discover → extract → resolve → annotate → validate → publish.
        /// Designed to be called from Worker background thread.
        /// </summary>
        public async Task<bool> ExecuteFullBuildAsync(
            string projectKey,
            IReadOnlyList<(string blockId, string blockName, string blockType, string language, string xmlContent)> blocks,
            Dictionary<string, string> objectInventory,
            Dictionary<string, string> objectTypes,
            bool force = false,
            Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            // 1. Request build
            var job = _snapshotManager.RequestBuild(projectKey, force);
            onProgress?.Invoke($"Build job {job.JobId} started");

            // 2. Complete discovery
            var versions = blocks.ToDictionary(b => b.blockId, b => b.blockId); // simplified version map
            _snapshotManager.CompleteDiscovery(projectKey, versions, blocks.Count);

            // 3. Extract Networks for each LAD block
            foreach (var (blockId, blockName, blockType, language, xmlContent) in blocks)
            {
                ct.ThrowIfCancellationRequested();

                if (!IsLad(language))
                {
                    _snapshotManager.RecordBlockExtracted(projectKey, blockId, new List<PlcNetworkDocument>(), true);
                    continue;
                }

                try
                {
                    var networks = _extractor.ExtractNetworks(projectKey, blockId, blockName, xmlContent);
                    _networkStore.UpsertNetworks(projectKey, blockId, networks);
                    _snapshotManager.RecordBlockExtracted(projectKey, blockId, networks,
                        networks.All(n => n.Status == ParseStatus.Succeeded));

                    onProgress?.Invoke($"Extracted {networks.Count} networks from {blockName}");
                }
                catch (Exception ex)
                {
                    _snapshotManager.RecordBlockExtracted(projectKey, blockId, new List<PlcNetworkDocument>(), false);
                    _snapshotManager.RecordWarning(projectKey, "extraction", "extracting", blockId, ex.Message);
                }
            }

            // 4. Global relation resolution
            _snapshotManager.BeginGlobalResolution(projectKey);
            var candidate = _snapshotManager.GetCandidateSnapshot(projectKey);
            if (candidate != null)
            {
                var relations = _relationResolver.ResolveAll(candidate.Networks, objectInventory, objectTypes);
                _snapshotManager.RecordRelationsResolved(projectKey, relations);
                onProgress?.Invoke($"Resolved {relations.Count} global relations");
            }

            // 4.5. Cross-reference enrichment (authoritative API validation)
            if (_crossRefEnricher != null && candidate != null)
            {
                var blockNameMap = blocks.ToDictionary(b => b.blockId, b => b.blockName, StringComparer.OrdinalIgnoreCase);
                var enrichResult = await _crossRefEnricher.EnrichAsync(
                    candidate.Networks, blockNameMap, onProgress, ct).ConfigureAwait(false);

                // Record coverage
                candidate.Coverage.CrossRefEnrichment.Expected = enrichResult.TotalProcessed;
                candidate.Coverage.CrossRefEnrichment.Completed = enrichResult.Verified + enrichResult.Corrected + enrichResult.NewDiscoveries;
                candidate.Coverage.CrossRefEnrichment.Warnings = enrichResult.NotFound;

                if (!enrichResult.Skipped)
                {
                    onProgress?.Invoke(
                        $"CrossRef enrichment: {enrichResult.Verified} verified, " +
                        $"{enrichResult.Corrected} corrected, {enrichResult.NewDiscoveries} new discoveries");
                }
                else
                {
                    onProgress?.Invoke($"CrossRef enrichment skipped: {enrichResult.SkipReason}");
                }
            }

            // 5. Semantic annotation (optional, if annotation service available)
            if (_annotationService != null && candidate != null)
            {
                var succeeded = candidate.Networks.Where(n => n.Status == ParseStatus.Succeeded).ToList();
                int annotated = 0, annotFailed = 0;

                foreach (var network in succeeded)
                {
                    ct.ThrowIfCancellationRequested();

                    // Get 2-hop context networks
                    var context = succeeded
                        .Where(n => n.BlockId == network.BlockId && n.NetworkId != network.NetworkId)
                        .OrderBy(n => Math.Abs(n.NetworkOrder - network.NetworkOrder))
                        .Take(4)
                        .ToList();

                    var annotation = await _annotationService.AnnotateAsync(network, context, ct).ConfigureAwait(false);
                    if (annotation.Status == AnnotationStatus.Succeeded)
                        annotated++;
                    else
                        annotFailed++;
                }

                if (candidate.Coverage.SemanticAnnotation == null)
                    candidate.Coverage.SemanticAnnotation = new CoveragePhase();
                candidate.Coverage.SemanticAnnotation.Expected = succeeded.Count;
                candidate.Coverage.SemanticAnnotation.Completed = annotated;
                candidate.Coverage.SemanticAnnotation.Failed = annotFailed;

                onProgress?.Invoke($"Annotated {annotated}/{succeeded.Count} networks ({annotFailed} failed)");
            }

            // 6. Validate and publish
            var published = _snapshotManager.TryPublish(projectKey);
            onProgress?.Invoke(published ? "Snapshot published successfully" : "Snapshot publish blocked (check errors)");

            return published;
        }

        private static bool IsLad(string language)
        {
            return string.Equals(language, "LAD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(language, "Ladder", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- UI Status Model ----

    public sealed class BuildJobStatus
    {
        public bool HasActiveJob { get; set; }
        public string JobId { get; set; }
        public string State { get; set; }
        public string CurrentPhase { get; set; }
        public string CurrentObject { get; set; }
        public int ExpectedObjects { get; set; }
        public int CompletedObjects { get; set; }
        public int WarningCount { get; set; }
        public int FailureCount { get; set; }
        public DateTime? LastUpdatedAtUtc { get; set; }

        public bool HasPublishedSnapshot { get; set; }
        public string PublishedSnapshotId { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public string PublishedCompleteness { get; set; }
        public int PublishedNetworkCount { get; set; }
        public int PublishedRelationCount { get; set; }

        // Cross-reference enrichment stats (from last build)
        public bool CrossRefAvailable { get; set; }
        public int CrossRefVerified { get; set; }
        public int CrossRefCorrected { get; set; }
        public int CrossRefNewDiscoveries { get; set; }
    }
}
