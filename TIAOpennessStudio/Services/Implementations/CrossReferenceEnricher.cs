using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.Models.NetworkKnowledge;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// Enriches Network documents with authoritative cross-reference data
    /// from the TIA Openness CrossReferenceProvider API.
    /// 
    /// This runs as an optional phase in the build pipeline (after GlobalResolution).
    /// When the TIA Portal is connected, it:
    /// 1. Queries real cross-references per block
    /// 2. Validates/corrects Access directions inferred from Wire/Pin XML
    /// 3. Discovers additional references not visible in XML (e.g. indirect addressing)
    /// 4. Marks verified accesses with CrossRefVerified = true
    /// 
    /// When TIA Portal is not connected, this phase is skipped gracefully.
    /// </summary>
    public sealed class CrossReferenceEnricher
    {
        private readonly ICrossReferenceProvider _provider;

        public CrossReferenceEnricher(ICrossReferenceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>
        /// Enrich a set of network documents with authoritative cross-reference data.
        /// Returns an enrichment result with statistics.
        /// </summary>
        /// <param name="networks">Networks to enrich (modified in-place).</param>
        /// <param name="blockNameMap">Map of blockId → blockName for API queries.</param>
        /// <param name="onProgress">Progress callback.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<EnrichmentResult> EnrichAsync(
            IReadOnlyList<PlcNetworkDocument> networks,
            Dictionary<string, string> blockNameMap,
            Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            var result = new EnrichmentResult();

            if (!_provider.IsAvailable)
            {
                result.Skipped = true;
                result.SkipReason = "TIA Portal not connected or PlcSoftware unavailable";
                onProgress?.Invoke("CrossRef enrichment skipped: no TIA connection");
                return result;
            }

            // Group networks by block
            var networksByBlock = networks
                .Where(n => n.Status == ParseStatus.Succeeded)
                .GroupBy(n => n.BlockId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Get unique block names to query
            var blockNamesToQuery = networksByBlock.Keys
                .Where(bid => blockNameMap.ContainsKey(bid))
                .Select(bid => blockNameMap[bid])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (blockNamesToQuery.Count == 0)
            {
                result.Skipped = true;
                result.SkipReason = "No blocks to query";
                return result;
            }

            onProgress?.Invoke($"CrossRef enrichment: querying {blockNamesToQuery.Count} blocks...");

            // Batch query cross-references
            Dictionary<string, List<BlockCrossReference>> crossRefs;
            try
            {
                crossRefs = await _provider.GetBatchCrossReferencesAsync(blockNamesToQuery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Skipped = true;
                result.SkipReason = $"Batch query failed: {ex.Message}";
                onProgress?.Invoke($"CrossRef enrichment failed: {ex.Message}");
                return result;
            }

            // Build a lookup: blockName (lowercase) → cross-ref entries
            var crossRefsByBlock = new Dictionary<string, List<BlockCrossReference>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in crossRefs)
            {
                crossRefsByBlock[kvp.Key] = kvp.Value;
            }

            // Enrich each block's networks
            foreach (var kvpBlock in networksByBlock)
            {
                var blockId = kvpBlock.Key;
                var blockNetworks = kvpBlock.Value;
                ct.ThrowIfCancellationRequested();

                if (!blockNameMap.TryGetValue(blockId, out var blockName))
                    continue;

                if (!crossRefsByBlock.TryGetValue(blockName, out var blockRefs) || blockRefs.Count == 0)
                {
                    result.BlocksWithNoCrossRefs++;
                    continue;
                }

                result.BlocksEnriched++;

                // Build a symbol → access direction map from cross-references
                var symbolDirections = BuildSymbolDirectionMap(blockRefs, blockName);

                // Enrich each network's Access nodes
                foreach (var network in blockNetworks)
                {
                    foreach (var access in network.Accesses)
                    {
                        EnrichAccess(access, symbolDirections, ref result);
                    }
                }
            }

            onProgress?.Invoke($"CrossRef enrichment done: {result.Verified} verified, {result.Corrected} corrected, {result.NewDiscoveries} new");
            return result;
        }

        private void EnrichAccess(AccessNode access, Dictionary<string, AccessDirectionInfo> symbolDirections, ref EnrichmentResult result)
        {
            if (string.IsNullOrWhiteSpace(access.CanonicalPath))
                return;

            // Try exact match first, then prefix match
            if (!symbolDirections.TryGetValue(access.CanonicalPath, out var dirInfo))
            {
                // Try without block prefix for local variables
                var dotIdx = access.CanonicalPath.IndexOf('.');
                if (dotIdx > 0)
                {
                    var withoutPrefix = access.CanonicalPath.Substring(dotIdx + 1);
                    symbolDirections.TryGetValue(withoutPrefix, out dirInfo);
                }
            }

            if (dirInfo == null)
            {
                // No cross-reference found for this access - leave as-is
                result.NotFound++;
                return;
            }

            var apiDirection = dirInfo.Direction;

            if (access.Direction == AccessDirection.Unresolved)
            {
                // Was unresolved by Wire/Pin analysis → cross-ref provides the answer
                access.Direction = apiDirection;
                access.DirectionEvidence = (access.DirectionEvidence ?? "") +
                    " | xref:" + dirInfo.Evidence;
                access.CrossRefVerified = true;
                result.NewDiscoveries++;
            }
            else if (access.Direction == apiDirection)
            {
                // Wire/Pin agrees with CrossRef → mark as verified
                access.CrossRefVerified = true;
                result.Verified++;
            }
            else if (apiDirection == AccessDirection.ReadWrite &&
                     (access.Direction == AccessDirection.Read || access.Direction == AccessDirection.Write))
            {
                // CrossRef says ReadWrite, Wire/Pin saw only one direction
                // Upgrade to ReadWrite (cross-ref sees full project scope)
                access.OriginalDirection = access.Direction;
                access.Direction = AccessDirection.ReadWrite;
                access.DirectionEvidence = (access.DirectionEvidence ?? "") +
                    " | xref-upgrade:" + dirInfo.Evidence;
                access.CrossRefVerified = true;
                result.Corrected++;
            }
            else
            {
                // Genuine conflict: cross-ref says different direction than Wire/Pin
                // Trust the cross-reference (authoritative) but record the conflict
                access.OriginalDirection = access.Direction;
                access.Direction = apiDirection;
                access.DirectionEvidence = (access.DirectionEvidence ?? "") +
                    " | xref-override:" + dirInfo.Evidence + " (was:" + access.OriginalDirection + ")";
                access.CrossRefVerified = true;
                result.Corrected++;
            }
        }

        /// <summary>
        /// Build a map of symbol path → direction from cross-reference entries.
        /// Merges multiple references to same symbol (Read + Write = ReadWrite).
        /// </summary>
        private static Dictionary<string, AccessDirectionInfo> BuildSymbolDirectionMap(
            List<BlockCrossReference> blockRefs, string blockName)
        {
            var map = new Dictionary<string, AccessDirectionInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var xref in blockRefs)
            {
                var symbol = xref.ReferencedSymbol;
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                // Only consider references FROM the block we're enriching
                // (the block itself as referencing entity)
                if (!string.Equals(xref.ReferencingBlockName, blockName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dir = ParseAccessDirection(xref.AccessType);
                if (dir == AccessDirection.Unresolved)
                    continue;

                if (map.TryGetValue(symbol, out var existing))
                {
                    // Merge: any combination → ReadWrite
                    if (existing.Direction != dir)
                    {
                        existing.Direction = AccessDirection.ReadWrite;
                        existing.Evidence += "+" + xref.AccessType;
                    }
                }
                else
                {
                    map[symbol] = new AccessDirectionInfo
                    {
                        Direction = dir,
                        Evidence = xref.AccessType + "@" + (xref.Location ?? "?")
                    };
                }
            }

            return map;
        }

        private static AccessDirection ParseAccessDirection(string accessType)
        {
            if (string.IsNullOrWhiteSpace(accessType))
                return AccessDirection.Unresolved;

            var lower = accessType.ToLowerInvariant();
            if (lower.Contains("readwrite") || lower.Contains("inout"))
                return AccessDirection.ReadWrite;
            if (lower.Contains("write"))
                return AccessDirection.Write;
            if (lower.Contains("read"))
                return AccessDirection.Read;

            return AccessDirection.Unresolved;
        }

        private sealed class AccessDirectionInfo
        {
            public AccessDirection Direction { get; set; }
            public string Evidence { get; set; }
        }
    }

    /// <summary>
    /// Statistics from the cross-reference enrichment phase.
    /// </summary>
    public sealed class EnrichmentResult
    {
        /// <summary>Whether the phase was skipped entirely.</summary>
        public bool Skipped { get; set; }

        /// <summary>Reason for skipping (e.g. "TIA Portal not connected").</summary>
        public string SkipReason { get; set; }

        /// <summary>Number of blocks successfully enriched.</summary>
        public int BlocksEnriched { get; set; }

        /// <summary>Number of blocks where no cross-references were available.</summary>
        public int BlocksWithNoCrossRefs { get; set; }

        /// <summary>Accesses where Wire/Pin direction was confirmed by CrossRef.</summary>
        public int Verified { get; set; }

        /// <summary>Accesses where CrossRef corrected or upgraded the direction.</summary>
        public int Corrected { get; set; }

        /// <summary>Accesses that were Unresolved and got direction from CrossRef.</summary>
        public int NewDiscoveries { get; set; }

        /// <summary>Accesses with no matching cross-reference entry.</summary>
        public int NotFound { get; set; }

        /// <summary>Total accesses processed.</summary>
        public int TotalProcessed => Verified + Corrected + NewDiscoveries + NotFound;
    }
}
